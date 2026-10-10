using System.Diagnostics;
using System.Text.Json;
using FO4_Base_Library;

using V3d=OpenTK.Mathematics.Vector3d;

namespace SSEPreview;
public sealed class MainWindow : Form
{
    readonly Assets assets=new();readonly Records records=new();readonly PlaybackClock clock=new();
    readonly Viewport viewport=new(){Dock=DockStyle.Fill};
    readonly TreeView tree=new ResponsiveTreeView(){Dock=DockStyle.Fill,HideSelection=false,BorderStyle=BorderStyle.None,FullRowSelect=true,ItemHeight=26,ShowLines=false};
    readonly TextBox search=new(){PlaceholderText="Search animations, mods, folders…",Dock=DockStyle.Top};
    readonly RichTextBox detail=TextPanel(),events=TextPanel(),diagnostics=TextPanel(),warnings=TextPanel();
    // Annotation sounds: SoundPlay.<EditorID> resolved through the loaded plugins and played as playback crosses them.
    readonly CheckBox playSounds=new(){Text="Sounds",Checked=false,AutoSize=true};
    readonly SoundPlayer soundPlayer=new();SoundLibrary? soundLibrary;
    double lastSoundAbsolute=double.NaN;CancellationTokenSource? soundSearch;
    // Results of the search through mod folders that are not loaded, by editor ID.
    Dictionary<string,List<string>> soundsElsewhere=new(StringComparer.OrdinalIgnoreCase);
    readonly ComboBox races=Combo(),sex=Combo(),weapons=Combo(),slot=Combo(),armor=Combo(),triggers=Combo(),effectSequence=Combo(),cameraSubject=Combo();
    readonly CheckedListBox heads=new WrappedCheckedListBox(){Dock=DockStyle.Fill,CheckOnClick=true,BorderStyle=BorderStyle.None,IntegralHeight=false};
    readonly ListBox equipped=new WrappedListBox(){Height=120,Width=310,BorderStyle=BorderStyle.None};
    readonly Label status=new(){Dock=DockStyle.Bottom,Height=30,Padding=new Padding(12,5,0,0),Text="Mount a Data folder or a small mod folder to begin."};
    readonly Label clipTitle=new(){AutoSize=true,Text="No animation selected",Padding=new Padding(8)};
    readonly Label timeLabel=new(){AutoSize=true,Padding=new Padding(8),Text="0.000 s"};
    readonly CheckBox snapFrames=new(){Text="Snap to frame",AutoSize=true,Padding=new Padding(0,6,0,0)};
    // Animation queue: clips played one after another with their root motion carried through.
    readonly List<ClipEntry> queue=[];
    // Each queue slot is its own ClipEntry instance (a copy), so the same clip queued twice keeps two choices.
    // Per slot and window event: which occurrence of that annotation hands over to the next clip (0 = first).
    readonly Dictionary<ClipEntry,Dictionary<string,int>> queueWindowPicks=new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<string,(DateTime Written,List<Annotation> Events)> queueClipEvents=new(StringComparer.OrdinalIgnoreCase);long queueClipEventsRevision=-1;
    const int QueueChipWidth=128;
    // Owner drawn so a drag can outline the clip being moved and mark where it will land.
    readonly ListBox queueList=new(){Dock=DockStyle.Fill,IntegralHeight=false,BorderStyle=BorderStyle.None,AllowDrop=true,HorizontalScrollbar=true,DrawMode=DrawMode.OwnerDrawFixed};
    readonly Panel queueBody=new(){Dock=DockStyle.Fill};
    Form? queueWindow;
    int queueDragFrom=-1;Point queueDragStart;
    // During a drag: the queued clip being moved (-1 for clips dragged in from the library) and the index the drop
    // inserts before (-1 when the pointer is outside the list).
    int queueDragging=-1,queueDropLine=-1;
    readonly NumericUpDown queueBlend=new(){Minimum=0,Maximum=2,Value=.3m,Increment=.05m,DecimalPlaces=2,Width=62};
    readonly Label queueNote=new(){AutoSize=true,Padding=new Padding(4,8,0,0)};
    readonly CheckBox queueAtWindow=new(){Text="Next clip starts at",Checked=true,AutoSize=true,Padding=new Padding(0,6,0,0)};
    readonly ComboBox queueWindowEvent=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=330,FormattingEnabled=true};
    TabControl? inspectorTabs;TabPage? queueTab,diagnosticsTab,warningsTab;
    bool QueuePlaying=>clip is {Segments:not null,Queued:true};
    string? QueueWindowEvent=>queueAtWindow.Checked?queueWindowEvent.SelectedItem as string:null;
    readonly Label speedNote=new(){AutoSize=true,Padding=new Padding(2,7,6,0),ForeColor=Color.FromArgb(255,214,120)};
    readonly TimelineBar timeline=new(){Dock=DockStyle.Fill,Minimum=0,Maximum=10000,Margin=new Padding(4,2,4,2)};
    readonly NumericUpDown projectileElevation=new(){Minimum=-89,Maximum=89,DecimalPlaces=1,Increment=5,Value=0,Width=100};
    readonly NumericUpDown particleLod=new(){Minimum=0,Maximum=10,DecimalPlaces=3,Increment=.01m,Value=0,Width=100};
    readonly NumericUpDown particleBudget=new(){Minimum=0,Maximum=65535,Increment=500,Value=2000,Width=100};
    readonly NumericUpDown fps=new(){Minimum=1,Maximum=60,Value=30,Width=55};
    readonly NumericUpDown speed=new(){Minimum=.05m,Maximum=4,Value=1,Increment=.05m,DecimalPlaces=2,Width=62};
    readonly CheckBox loop=new(){Text="Loop",Checked=true,AutoSize=true},rootMotion=new(){Text="Root motion",Checked=true,AutoSize=true},effectToggle=new(){Text="Effects",Checked=true,AutoSize=true},conditionalDac=new(){Text="Preview conditional DAC triggers",AutoSize=true};
    readonly CheckBox followCamera=new(){Text="Follow scene",Checked=true,AutoSize=true};
    readonly CheckBox requireAlt=new(){Text="Require Alt for camera controls",Checked=true,AutoSize=true};
    readonly Label cameraHint=new(){Dock=DockStyle.Bottom,Height=26,TextAlign=ContentAlignment.MiddleCenter};
    readonly ViewerPreferences preferences;
    readonly bool verificationMode;
    readonly CheckBox gridFloor=new(){Text="Grid floor at origin",Checked=true,AutoSize=true};
    readonly CheckBox showWeapon=new(){Text="Show weapon",Checked=true,AutoSize=true};
    readonly CheckBox sheathed=new(){Text="Sheathed on holster node",AutoSize=true};
    readonly CheckBox smpPhysics=new(){Text="Simulate HDT-SMP physics",AutoSize=true};
    // Actor hair color: null follows the race, otherwise a CLFM record key or #RRGGBB for a custom color.
    sealed record HairChoice(string Label,string? Key,Color? Color){public override string ToString()=>Label;}
    readonly ComboBox hairColor=Combo();
    readonly Label hairNote=new(){AutoSize=true,MaximumSize=new Size(330,0)};
    string? hairChoice;
    const string CustomHairKey="#custom";
    readonly CheckBox smpColliders=new(){Text="Show collision shapes",AutoSize=true};
    readonly CheckBox smpBones=new(){Text="Show bones and constraints",AutoSize=true};
    readonly Label smpLabel=new(){AutoSize=true,MaximumSize=new Size(330,0)};
    readonly CheckBox precisionShow=new(){Text="Show Precision attack collisions",AutoSize=true};
    // Skeleton overlay: the tick box above the character drop-down, and a Bones window for names and the bone filter.
    readonly CheckBox showSkeleton=new(){Text="Show skeleton",AutoSize=true,Padding=new Padding(0,6,0,0)};
    readonly CheckBox showBoneNames=new(){Text="Show bone names",AutoSize=true};
    readonly TextBox boneSearch=new(){Width=300,PlaceholderText="Filter the list (all words must match)"};
    readonly CheckedListBox boneList=new(){Width=300,Height=360,CheckOnClick=true,IntegralHeight=false};
    readonly Label boneFilterNote=new(){AutoSize=true,MaximumSize=new Size(300,0)};
    Form? bonesWindow;bool fillingBones;Button? bonesButton;
    // Highlights the annotations that are active at the playback time in the Annotations tab.
    AnnotationHighlighter? annotationHighlight;AnnotationHighlighter Highlight=>annotationHighlight??=new(events);
    readonly ComboBox precisionActor=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=170};
    readonly Label precisionLabel=new(){AutoSize=true,MaximumSize=new Size(330,0)};
    double lastPhysicsLabel;
    readonly TrackBar weight=new(){Minimum=0,Maximum=100,Value=100,TickFrequency=10,Width=310,AutoSize=false,Height=32};
    readonly Label weightLabel=new(){AutoSize=true};
    readonly ListBox sources=new WrappedListBox(){Dock=DockStyle.Bottom,Height=118,BorderStyle=BorderStyle.None,IntegralHeight=false};
    ContextMenuStrip? sceneMenu;ToolStripMenuItem? recentMenu;readonly RecentScenes recentScenes;
    readonly ToolTip sourceTip=new();string sourceTipText="";
    readonly System.Windows.Forms.Timer timer=new(){Interval=8};
    readonly Stopwatch frameClock=Stopwatch.StartNew();
    double lastSample=-100;bool busy,selecting,scrubbing,previewingEffect,resumeAfterScrub;
    readonly Scene scene;readonly Effects effects;
    readonly bool emitterSourceSmoke;
    ClipEntry? selected;AnimationClip? clip;string? pluginsFile;string? sceneFile;
    readonly List<GameRecord> worn=[];List<EffectCue> cues=[];readonly List<TimedTrigger> manualTriggers=[];readonly List<TimedReferenceEffect> referenceActions=[];int referenceTarget=1;readonly Dictionary<int,(System.Numerics.Vector3 Position,float Heading,bool Follow)> referenceTargets=[];readonly List<string> customModels=[];readonly List<string> effectOverlays=[];string? customSkeleton;
    readonly string settingsPath=Path.Combine(AppContext.BaseDirectory,"session.json");
    static readonly string Fixture=Path.Combine(SelfTest.Mods,"Cloud FF7 Blade Beam MCO");
    // Smoke and tool switches in dispatch order: the local data each needs, how many arguments must follow it, and its log beside the exe.
    // T vanilla test-data, M the mod folders, G the mod list's game Data folder, S a saved tool session, A the archive fixtures, C the fixture --catalog-test writes.
    static (string Switch,string Needs,int Arguments,string? Log)[] SmokeModes()=>[
        ("--weaponart-profile-smoke","S",0,null),("--smp-determinism","T",1,null),("--equipment-smoke",Environment.GetEnvironmentVariable("SSE_EQUIP_SESSION")=="1"?"S":"T",0,@"equipment-smoke\equipment-smoke.txt"),
        ("--queue-smoke","T",2,@"queue-smoke\queue-smoke.txt"),("--smp-soak","S",1,null),("--dump-annotations","",0,null),("--track-bone","S",2,"track-bone.txt"),("--override-smoke","C",0,@"override-smoke\override-smoke.txt"),
        ("--docs-capture","",3,null),("--bfco-smoke","T",3,@"bfco-smoke\bfco-smoke.txt"),("--frames-capture","",3,null),("--flipbook-smoke","T",1,@"flipbook-smoke\flipbook-smoke.txt"),("--ff7vfx-smoke","S",0,@"ff7vfx-smoke\ff7vfx-smoke.txt"),("--annotation-smoke","T",1,@"annotation-smoke\annotation-smoke.txt"),
        ("--skeleton-smoke","T",1,@"skeleton-smoke\skeleton-smoke.txt"),("--window-pick-smoke","T",2,@"window-pick-smoke\window-pick-smoke.txt"),("--sound-smoke","GM",0,@"sound-smoke\sound-smoke.txt"),
        ("--precision-smoke","",2,@"precision-smoke\precision-smoke.txt"),("--frame-smoke","T",1,@"frame-smoke\frame-smoke.txt"),("--hair-smoke","T",0,@"hair-smoke\hair-smoke.txt"),("--smp-smoke","T",0,@"smp-smoke\smp-smoke.txt"),
        ("--holster-smoke","T",3,"holster-tests.txt"),("--clip-profile","S",2,null),("--mod-sweep","S",1,null),("--sources-recent-smoke","",0,null),("--scene-save-smoke","",0,null),("--scene-dialog-smoke","",0,null),
        ("--cloud-equipment-smoke","GM",0,"cloud-equipment-tests.txt"),("--bladebeam-smoke","GM",0,"bladebeam-smoke.txt"),("--timing-smoke","TM",0,"timing-smoke.txt"),("--emitter-source-smoke","",0,null),("--refraction-smoke","",0,null),
        ("--archive-order-smoke",Environment.GetEnvironmentVariable("SSE_ARCHIVE_TEST_ROOT")==null?"A":"",0,"archive-order-render.txt"),("--reference-effect-smoke","GM",0,"reference-effects.txt"),("--remote-cast-smoke","",0,null),
        ("--camera-smoke","",0,null),("--attachment-smoke","TM",0,"attachment-tests.txt"),("--floor-smoke","",0,null),("--tagfile-smoke","",1,"tagfile-tests.txt"),("--speed-smoke","",2,"speed-tests.txt"),
        ("--character-smoke","T",0,"character-mount-tests.txt"),("--library-smoke",Environment.GetEnvironmentVariable("SSE_LIBRARY_TEST_ROOT")==null?"M":"",0,null),("--particle-smoke","",0,null),
        ("--smoke",Environment.GetEnvironmentVariable("SSE_PREVIEW_TEST_DATA")==null?"TM":"M",0,"smoke.txt")];
    // True when the requested mode cannot start. Missing data is a skip (exit code 0), too few arguments an error (exit code 2).
    // The reason goes to the mode's log, or to skipped.txt beside the exe when the mode has no fixed log.
    static bool SmokeBlocked(string[] args)
    {
        var mode=SmokeModes().FirstOrDefault(m=>args.Contains(m.Switch));if(mode.Switch==null)return false;
        int given=args.Skip(Array.IndexOf(args,mode.Switch)+1).Count(a=>!a.StartsWith("--"));
        string verification=Path.Combine(SelfTest.RepoRoot,"verification");
        var missing=mode.Needs.Select(need=>need switch{
            'T' when !SelfTest.HasTestData=>"the vanilla test data (no Skyrim.esm in "+SelfTest.TestData+")",
            'M' when !SelfTest.HasMods=>"the mod folders (none in "+SelfTest.Mods+", set SSE_TEST_MODS to their root)",
            'G' when !Directory.Exists(SelfTest.GameData)=>"the game Data folder "+SelfTest.GameData,
            'S' when !File.Exists(ToolSessionPath())=>"a saved session at "+ToolSessionPath()+" (SSE_SESSION_FILE names another)",
            'A' when !Directory.Exists(Path.Combine(verification,"archive-fixtures"))=>"the archive fixtures in "+Path.Combine(verification,"archive-fixtures"),
            'C' when !Directory.Exists(Path.Combine(verification,"catalog-fixture"))=>"the catalog fixture in "+Path.Combine(verification,"catalog-fixture")+" (run --catalog-test first)",
            _=>null}).Where(reason=>reason!=null).ToArray();
        string? message=given<mode.Arguments?$"FAILED: {mode.Switch} needs {mode.Arguments} argument{(mode.Arguments==1?"":"s")} after it and got {given}":missing.Length>0?$"SKIPPED: {mode.Switch} needs "+string.Join(" and ",missing):null;
        if(message==null)return false;
        string file=Path.Combine(AppContext.BaseDirectory,mode.Log??"skipped.txt");Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllText(file,message+Environment.NewLine);
        Console.Error.WriteLine(message);Environment.ExitCode=message.StartsWith("SKIPPED")?0:2;return true;
    }
    static RichTextBox TextPanel()=>new(){Dock=DockStyle.Fill,ReadOnly=true,BorderStyle=BorderStyle.None,WordWrap=true,Font=new Font("Consolas",10)};
    // --background or SSE_PREVIEW_BACKGROUND=1. Set by Program before the window exists.
    internal static bool Background;
    [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
    protected override bool ShowWithoutActivation=>Background;
    protected override CreateParams CreateParams{get{var p=base.CreateParams;if(Background)p.ExStyle|=0x08000000|0x00000080;return p;}} // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
    static ComboBox Combo()=>new SearchableComboBox{Width=310,DropDownHeight=330,IntegralHeight=false};
    public MainWindow(string[] args)
    {
        verificationMode=args.Any(a=>a.EndsWith("-smoke",StringComparison.Ordinal));
        try{preferences=verificationMode?new():ViewerPreferences.Load(ViewerPreferences.FilePath);}
        catch(Exception ex){preferences=new();if(!Background)MessageBox.Show("Could not restore viewer settings: "+ex.Message,"Viewer settings");}
        requireAlt.Checked=preferences.RequireAlt;viewport.RequireAlt=preferences.RequireAlt;
        emitterSourceSmoke=args.Contains("--emitter-source-smoke");
        recentScenes=new(Path.Combine(AppContext.BaseDirectory,args.Any(a=>a.EndsWith("-smoke",StringComparison.Ordinal))?"verification-recent-scenes.json":"recent-scenes.json"));
        if(args.Contains("--smp-determinism")||args.Contains("--equipment-smoke")||args.Contains("--queue-smoke")||args.Contains("--frame-smoke")||args.Contains("--precision-smoke")||args.Contains("--window-pick-smoke")||args.Contains("--skeleton-smoke")||args.Contains("--annotation-smoke")||args.Contains("--ff7vfx-smoke")||args.Contains("--flipbook-smoke")||args.Contains("--override-smoke")||args.Contains("--docs-capture")||args.Contains("--frames-capture")||args.Contains("--effect-capture")||args.Contains("--cloud-effect-alpha-smoke")||args.Contains("--bfco-smoke")||args.Contains("--track-bone")||args.Contains("--sound-smoke")||args.Contains("--smp-soak")||args.Contains("--hair-smoke")||args.Contains("--smp-smoke")||args.Contains("--holster-smoke")||args.Contains("--clip-profile")||args.Contains("--mod-sweep")||args.Contains("--weaponart-profile-smoke")||args.Contains("--sources-recent-smoke")||args.Contains("--scene-save-smoke")||args.Contains("--cloud-equipment-smoke")||args.Contains("--bladebeam-smoke")||args.Contains("--timing-smoke")||args.Contains("--emitter-source-smoke")||args.Contains("--refraction-smoke")||args.Contains("--archive-order-smoke")||args.Contains("--reference-effect-smoke")||args.Contains("--remote-cast-smoke")||args.Contains("--camera-smoke")||args.Contains("--smoke")||args.Contains("--particle-smoke")||args.Contains("--library-smoke")||args.Contains("--character-smoke")||args.Contains("--attachment-smoke")||args.Contains("--floor-smoke")||args.Contains("--tagfile-smoke")||args.Contains("--speed-smoke"))settingsPath=Path.Combine(AppContext.BaseDirectory,"verification-session.json");
        // Existing regression renders measure effect pixels against the bare background; only the floor smoke wants the floor.
        if(settingsPath.EndsWith("verification-session.json")&&!args.Contains("--floor-smoke"))gridFloor.Checked=false;
        if(Background){
            // Background mode: a real window with a live GL surface, parked off-screen, absent from the taskbar and
            // never activated, so test and tool runs do not cover or steal focus from whatever the user is doing.
            ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;Location=new(-32000,-32000);
        }
        Text="SSE Animation Previewer "+Application.ProductVersion;Size=new(1580,1000);MinimumSize=new(1150,750);StartPosition=Background?FormStartPosition.Manual:FormStartPosition.CenterScreen;KeyPreview=true;
        scene=new(assets,viewport,records);viewport.FrameRequested=scene.Frame;effects=new(assets,records);BuildUi();Theme(this);viewport.MaximumPresentationFps=60;
        scene.FollowScene=followCamera.Checked;scene.FramingSubject=(Scene.FrameSubject)cameraSubject.SelectedIndex;scene.ShowFloor=gridFloor.Checked;
        UpdateCameraHint();
        requireAlt.CheckedChanged+=(_,_)=>{viewport.RequireAlt=requireAlt.Checked;preferences.RequireAlt=requireAlt.Checked;UpdateCameraHint();SavePreferences();};
        FormClosing+=(_,_)=>SavePreferences();
        gridFloor.CheckedChanged+=(_,_)=>{scene.ShowFloor=gridFloor.Checked;if(!busy&&!selecting)SaveSession();};
        showWeapon.CheckedChanged+=(_,_)=>{scene.ShowWeapon=showWeapon.Checked;if(!busy&&!selecting)SaveSession();};
        sheathed.CheckedChanged+=(_,_)=>{scene.Sheathed=sheathed.Checked;UpdateHolsterLabel();scene.SetTime(clock.Time);if(!busy&&!selecting)SaveSession();};
        smpPhysics.CheckedChanged+=(_,_)=>{scene.PhysicsEnabled=smpPhysics.Checked;UpdatePhysicsLabel();UpdateDiagnostics();if(!busy&&!selecting)SaveSession();};
        smpColliders.CheckedChanged+=(_,_)=>{scene.PhysicsColliders=smpColliders.Checked;viewport.UpdateRequired=true;viewport.Invalidate();if(!busy&&!selecting)SaveSession();};
        smpBones.CheckedChanged+=(_,_)=>{scene.PhysicsBones=smpBones.Checked;viewport.UpdateRequired=true;viewport.Invalidate();if(!busy&&!selecting)SaveSession();};
        showSkeleton.CheckedChanged+=(_,_)=>{scene.ShowSkeleton=showSkeleton.Checked;if(!busy&&!selecting)SaveSession();};
        showBoneNames.CheckedChanged+=(_,_)=>{scene.ShowBoneNames=showBoneNames.Checked;if(!busy&&!selecting)SaveSession();};
        boneSearch.TextChanged+=(_,_)=>FillBoneList();
        boneList.ItemCheck+=(_,e)=>{
            if(fillingBones||e.Index<0)return;string name=(string)boneList.Items[e.Index];
            if(e.NewValue==CheckState.Checked)scene.BoneFilter.Add(name);else scene.BoneFilter.Remove(name);
            BeginInvoke(()=>{UpdateBoneNote();scene.BoneFilterChanged();if(!busy&&!selecting)SaveSession();});
        };
        precisionShow.CheckedChanged+=(_,_)=>{scene.ShowPrecision=precisionShow.Checked;UpdatePrecisionLabel();if(!busy&&!selecting)SaveSession();};
        precisionActor.Items.AddRange(["Sized as the player","Sized as an NPC"]);precisionActor.SelectedIndex=0;
        precisionActor.SelectedIndexChanged+=(_,_)=>{scene.PrecisionAsPlayer=precisionActor.SelectedIndex==0;UpdatePrecisionLabel();if(!busy&&!selecting)SaveSession();};
        sources.DoubleClick+=(_,_)=>OpenSource();sources.ContextMenuStrip=SourcesMenu();
        sources.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Right){int i=sources.IndexFromPoint(e.Location);if(i>=0)sources.SelectedIndex=i;}};
        sources.MouseMove+=(_,e)=>{int i=sources.IndexFromPoint(e.Location);string text=i>=0&&sources.Items[i] is SourceEntry entry?entry.Path:"";if(text!=sourceTipText){sourceTipText=text;sourceTip.SetToolTip(sources,text);}};
        cameraSubject.SelectedIndexChanged+=(_,_)=>{scene.FramingSubject=(Scene.FrameSubject)Math.Max(0,cameraSubject.SelectedIndex);scene.Frame();if(!busy&&!selecting)SaveSession();};
        followCamera.CheckedChanged+=(_,_)=>{scene.FollowScene=followCamera.Checked;if(followCamera.Checked)scene.Frame();if(!busy&&!selecting)SaveSession();};
        // Orbiting, panning or zooming by hand leaves Follow camera ticked. With a clip the camera keeps travelling
        // with the actor root from the new view; an effect preview stops re-fitting until the scene is framed again.
        viewport.ManualCameraRequested=()=>scene.HoldFraming=true;
        effectSequence.SelectedIndexChanged+=(_,_)=>{if(!selecting)scene.SelectSequence(effectSequence.SelectedIndex<=0?null:effectSequence.SelectedItem?.ToString());};
        search.TextChanged+=(_,_)=>BuildTree();
        var treeMenu=new ContextMenuStrip();TreeNode? menuNode=null;
        treeMenu.Items.Add("Add to queue",null,(_,_)=>{if(menuNode!=null)AddToQueue(LibraryClips(menuNode));});
        tree.NodeMouseClick+=(_,e)=>{if(e.Button==MouseButtons.Right&&LibraryClips(e.Node).Length>0){menuNode=e.Node;var count=LibraryClips(e.Node).Length;treeMenu.Items[0].Text=count==1?"Add to queue":$"Add {count} clips to queue";treeMenu.Show(tree,e.Location);}};
        queueWindowEvent.Items.AddRange(["MCO_WinOpen","MCO_PowerWinOpen"]);queueWindowEvent.SelectedIndex=0;
        // The stored choice keeps MCO's name; a BFCO clip in the queue hands over at BFCO's window of the same kind.
        queueWindowEvent.Format+=(_,e)=>e.Value=(e.ListItem as string)=="MCO_PowerWinOpen"?"MCO_PowerWinOpen / BFCO_NextPowerWinStart":"MCO_WinOpen / BFCO_NextWinStart";
        queueAtWindow.CheckedChanged+=(_,_)=>{queueWindowEvent.Enabled=queueAtWindow.Checked;QueueChanged();};
        queueWindowEvent.SelectedIndexChanged+=(_,_)=>QueueChanged();
        queueBlend.ValueChanged+=(_,_)=>{if(QueuePlaying){clip.BlendTime=(double)queueBlend.Value;scene.SetTime(scene.Time);viewport.InvalidateRender();}if(!busy&&!selecting)SaveSession();};
        tree.AfterSelect+=async(_,e)=>{if(e.Node?.Tag is string info)detail.Text=info;else if(!busy && e.Node?.Tag is ClipEntry entry)await SelectClip(entry);};
        tree.AllowDrop=true;tree.DragEnter+=(_,e)=>e.Effect=e.Data?.GetDataPresent(DataFormats.FileDrop)==true?DragDropEffects.Copy:DragDropEffects.None;
        tree.DragDrop+=(_,e)=>{if(!busy && e.Data?.GetData(DataFormats.FileDrop) is string[] files)DropFiles(files);};
        races.SelectedIndexChanged+=(_,_)=>{if(!selecting){PopulateHeads();RebuildScene();}};
        sex.SelectedIndexChanged+=(_,_)=>{if(!selecting){PopulateHeads();RebuildScene();}};
        hairColor.SelectedIndexChanged+=(_,_)=>{
            if(selecting||hairColor.SelectedItem is not HairChoice choice)return;
            if(choice.Key==CustomHairKey){
                using var dialog=new ColorDialog{FullOpen=true,Color=ParseHair(hairChoice)??Color.FromArgb(150,130,85)};
                if(dialog.ShowDialog(this)!=DialogResult.OK){PopulateHairColors();return;}
                SetHairColor($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");return;
            }
            SetHairColor(choice.Key);
        };
        weapons.SelectedIndexChanged+=(_,_)=>{if(!selecting)RebuildScene();};
        heads.ItemCheck+=(_,e)=>{
            if(selecting)return;
            if(e.NewValue==CheckState.Checked&&heads.Items[e.Index] is GameRecord chosen){
                uint type=chosen.First("PNAM")?.UInt()??0;
                if(type!=0){selecting=true;try{for(int i=0;i<heads.Items.Count;i++)
                    if(i!=e.Index&&heads.GetItemChecked(i)&&heads.Items[i] is GameRecord other&&(other.First("PNAM")?.UInt()??0)==type)heads.SetItemChecked(i,false);
                }finally{selecting=false;}}
            }
            BeginInvoke(()=>RebuildScene());
        };
        slot.SelectedIndexChanged+=(_,_)=>PopulateArmor();
        loop.CheckedChanged+=(_,_)=>clock.Loop=loop.Checked;
        playSounds.CheckedChanged+=(_,_)=>{if(!playSounds.Checked)soundPlayer.StopAll();lastSoundAbsolute=double.NaN;if(!busy&&!selecting)SaveSession();};
        rootMotion.CheckedChanged+=(_,_)=>{scene.RootMotion=rootMotion.Checked;scene.ResetParticles();scene.SetTime(clock.Time);};
        effectToggle.CheckedChanged+=(_,_)=>{scene.ShowEffects=effectToggle.Checked;scene.SetTime(clock.Time);};
        conditionalDac.CheckedChanged+=(_,_)=>{if(clip!=null){cues=effects.Build(clip,conditionalDac.Checked,manualTriggers,referenceActions);RebuildScene();}};
        speed.ValueChanged+=(_,_)=>clock.Speed=(double)speed.Value;
        timeline.MouseDown+=(_,_)=>BeginScrub();timeline.MouseUp+=(_,_)=>EndScrub();
        timeline.MouseCaptureChanged+=(_,_)=>{if(!timeline.Capture)EndScrub();};
        timeline.Scroll+=(_,_)=>ScrubTo(timeline.Value);
        fps.ValueChanged+=(_,_)=>{clock.SourceFps=(double)fps.Value;if(snapFrames.Checked&&!clock.Playing)SeekFrame(0);UpdateTimeLabel(scene.Time);};
        snapFrames.CheckedChanged+=(_,_)=>{if(snapFrames.Checked&&!clock.Playing&&(clip!=null||previewingEffect))SeekFrame(0);UpdateTimeLabel(scene.Time);if(!busy&&!selecting)SaveSession();};
        timer.Tick+=(_,_)=>TickPlayback();timer.Start();
        KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Space && ActiveControl is not TextBox and not RichTextBox and not ComboBox){TogglePlay();e.Handled=true;}};
        FormClosed+=(_,_)=>{timer.Stop();timer.Dispose();sceneMenu?.Dispose();scene.Dispose();viewport.Clean();};
        Shown+=async(_,_)=>{if(args.FirstOrDefault(a=>a is "--effect-capture" or "--cloud-effect-alpha-smoke") is {} capture){int j=Array.IndexOf(args,capture);if(args.Length<j+5){Console.Error.WriteLine(capture+" needs an output folder, two NIFs and a Data folder after it.");Environment.ExitCode=2;Close();return;}await EffectCapture(args[j+1],args[j+2],args[j+3],args[j+4]);return;}bool blocked;try{blocked=SmokeBlocked(args);}catch(Exception ex){ShowError(ex);Environment.ExitCode=2;blocked=true;}if(blocked){Close();return;}if(args.Contains("--weaponart-profile-smoke")){await WeaponArtProfile();return;}if(args.Contains("--smp-determinism")){await SmpDeterminism(args.Skip(Array.IndexOf(args,"--smp-determinism")+1).Where(a=>!a.StartsWith("--")).ToArray());return;}if(args.Contains("--equipment-smoke")){await EquipmentSmoke(args.Skip(Array.IndexOf(args,"--equipment-smoke")+1).Where(a=>!a.StartsWith("--")).ToArray());return;}if(args.Contains("--queue-smoke")){await QueueSmoke(args.Skip(Array.IndexOf(args,"--queue-smoke")+1).Where(a=>!a.StartsWith("--")).ToArray());return;}if(args.Contains("--smp-soak")){await SmpSoak(args.Skip(Array.IndexOf(args,"--smp-soak")+1).Where(a=>!a.StartsWith("--")).ToArray());return;}if(args.Contains("--dump-annotations")){DumpAnnotations(args.Skip(Array.IndexOf(args,"--dump-annotations")+1).Where(x=>!x.StartsWith("--")).ToArray());Close();return;}if(args.Contains("--track-bone")){await TrackBone(args.Skip(Array.IndexOf(args,"--track-bone")+1).Where(a=>!a.StartsWith("--")).ToArray());return;}if(args.Contains("--override-smoke")){await OverrideSmoke();return;}if(args.Contains("--bfco-smoke")){await BfcoSmoke(args.Skip(Array.IndexOf(args,"--bfco-smoke")+1).Where(a=>!a.StartsWith("--")).ToArray());return;}if(args.Contains("--frames-capture")){await FramesCapture(args.Skip(Array.IndexOf(args,"--frames-capture")+1).Where(a=>!a.StartsWith("--")).ToArray());return;}if(args.Contains("--docs-capture")){await DocsCapture(args.Skip(Array.IndexOf(args,"--docs-capture")+1).Where(a=>!a.StartsWith("--")).ToArray());return;}if(args.Contains("--flipbook-smoke")){await FlipbookSmoke(args.Skip(Array.IndexOf(args,"--flipbook-smoke")+1).Where(a=>!a.StartsWith("--")).ToArray());return;}if(args.Contains("--ff7vfx-smoke")){await Ff7VfxSmoke();return;}if(args.Contains("--annotation-smoke")){await AnnotationSmoke(args[Array.IndexOf(args,"--annotation-smoke")+1]);return;}if(args.Contains("--skeleton-smoke")){await SkeletonSmoke(args[Array.IndexOf(args,"--skeleton-smoke")+1]);return;}if(args.Contains("--window-pick-smoke")){int j=Array.IndexOf(args,"--window-pick-smoke");await WindowPickSmoke(args[j+1],args[j+2]);return;}if(args.Contains("--sound-smoke")){await SoundSmoke();return;}if(args.Contains("--precision-smoke")){await PrecisionSmoke(args.Skip(Array.IndexOf(args,"--precision-smoke")+1).Where(a=>!a.StartsWith("--")).ToArray());return;}if(args.Contains("--frame-smoke")){await FrameSmoke(args[Array.IndexOf(args,"--frame-smoke")+1]);return;}if(args.Contains("--hair-smoke")){await HairSmoke(args.Skip(Array.IndexOf(args,"--hair-smoke")+1).Where(a=>!a.StartsWith("--")).ToArray());return;}if(args.Contains("--smp-smoke")){await SmpSmoke(args.Skip(Array.IndexOf(args,"--smp-smoke")+1).Where(a=>!a.StartsWith("--")).ToArray());return;}if(args.Contains("--holster-smoke")){await HolsterSmoke(args[Array.IndexOf(args,"--holster-smoke")+1],args[Array.IndexOf(args,"--holster-smoke")+2],args[Array.IndexOf(args,"--holster-smoke")+3]);return;}if(args.Contains("--clip-profile")){await ClipProfile(args[Array.IndexOf(args,"--clip-profile")+1],args[Array.IndexOf(args,"--clip-profile")+2]);return;}if(args.Contains("--mod-sweep")){await ModSweep(args[Array.IndexOf(args,"--mod-sweep")+1]);return;}if(args.Contains("--sources-recent-smoke")){await SourcesRecentSmoke();return;}if(args.Contains("--scene-save-smoke")){await SceneSaveSmoke();return;}if(args.Contains("--scene-dialog-smoke")){await SceneDialogSmoke();return;}if(args.Contains("--cloud-equipment-smoke")){await CloudEquipmentSmoke();return;}if(args.Contains("--bladebeam-smoke")){await BladeBeamSmoke();return;}if(args.Contains("--timing-smoke")){await TimingSmoke();return;}if(args.Contains("--emitter-source-smoke")){await EmitterSourceSmoke();return;}if(args.Contains("--refraction-smoke")){await RefractionSmoke();return;}if(args.Contains("--archive-order-smoke")){await ArchiveOrderSmoke();return;}if(args.Contains("--reference-effect-smoke")){await ReferenceEffectSmoke();return;}if(args.Contains("--remote-cast-smoke")){await RemoteCastSmoke();return;}if(args.Contains("--camera-smoke")){await CameraSmoke();return;}if(args.Contains("--attachment-smoke")){await AttachmentSmoke();return;}if(args.Contains("--floor-smoke")){await FloorSmoke();return;}if(args.Contains("--tagfile-smoke")){await TagfileSmoke(args[Array.IndexOf(args,"--tagfile-smoke")+1]);return;}if(args.Contains("--speed-smoke")){await SpeedSmoke(args[Array.IndexOf(args,"--speed-smoke")+1],args[Array.IndexOf(args,"--speed-smoke")+2]);return;}if(args.Contains("--character-smoke")){await CharacterSmoke();return;}if(args.Contains("--library-smoke")){await LibrarySmoke();return;}if(args.Contains("--particle-smoke")){await ParticleSmoke();return;}if(args.Contains("--smoke")){await Smoke();return;}int i=Array.IndexOf(args,"--data");if(i>=0 && args.Length>i+1)await Mount(args[i+1],false);};
    }
    void BuildUi()
    {
        var toolbar=new FlowLayoutPanel(){Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(8),WrapContents=true};
        toolbar.Controls.AddRange([Button("Open Data…",async()=>await ChooseMount(false)),Button("Add mod folder…",async()=>await ChooseMount(true)),Button("Load order…",ChoosePlugins),Button("Restore session",async()=>await Restore()),Button("Scenes...",PresetMenu),Button("Reset",()=>ResetPreview()),Button("Add HKX…",ChooseAnimation),Button("Lighting…",Lighting),Button("Frame  F",scene.Frame)]);
        var outer=new SplitContainer(){Dock=DockStyle.Fill,FixedPanel=FixedPanel.Panel1,SplitterWidth=6};
        outer.Panel1.Padding=new Padding(10);
        var library=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,FixedPanel=FixedPanel.Panel2,SplitterWidth=7};outer.Panel1.Controls.Add(library);
        library.Panel1.Controls.Add(tree);library.Panel1.Controls.Add(search);
        library.Panel1.Controls.Add(new Label {Text="ANIMATION LIBRARY · drop HKX files here",Dock=DockStyle.Top,Height=34});
        sources.Dock=DockStyle.Fill;library.Panel2.Controls.Add(sources);
        library.Panel2.Controls.Add(new Label{Text="LOADED SOURCES · later folders win",Dock=DockStyle.Top,Height=30,TextAlign=ContentAlignment.BottomLeft});
        var workspace=new SplitContainer(){Dock=DockStyle.Fill,FixedPanel=FixedPanel.Panel2,SplitterWidth=6};outer.Panel2.Controls.Add(workspace);
        var center=new SplitContainer(){Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,FixedPanel=FixedPanel.Panel2,SplitterWidth=6};
        workspace.Panel1.Controls.Add(center);center.Panel1.Controls.Add(viewport);
        center.Panel1.Controls.Add(cameraHint);
        var tabs=new ThemedTabControl(){Dock=DockStyle.Fill};
        Tab(tabs,"Configuration & source",detail).Tag=Color.FromArgb(64,156,255);
        Tab(tabs,"Annotations & effects",events).Tag=Color.FromArgb(72,201,140);
        diagnosticsTab=Tab(tabs,"Diagnostics",diagnostics);diagnosticsTab.Tag=Color.FromArgb(245,176,65);
        warningsTab=Tab(tabs,"Warnings",warnings);warningsTab.Tag=Color.FromArgb(235,96,96);warnings.Text="No warnings.";
        center.Panel2.Controls.Add(tabs);
        Control QueueSetting(string label,Control input){var group=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=false,Margin=new Padding(2)};group.Controls.Add(new Label{Text=label,AutoSize=true,Padding=new Padding(2,7,0,0)});group.Controls.Add(input);return group;}
        var queueButtons=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true};
        queueButtons.Controls.AddRange([Button("Add selected clips",()=>{var picked=tree.SelectedNode!=null?LibraryClips(tree.SelectedNode):[];if(picked.Length>0)AddToQueue(picked);else if(selected!=null)AddToQueue([selected]);}),Button("Remove",()=>EditQueue(i=>queue.RemoveAt(i),0)),Button("Up",()=>EditQueue(i=>{if(i>0)(queue[i-1],queue[i])=(queue[i],queue[i-1]);},-1)),Button("Down",()=>EditQueue(i=>{if(i<queue.Count-1)(queue[i+1],queue[i])=(queue[i],queue[i+1]);},1)),Button("Clear",()=>{queue.Clear();RefreshQueue();QueueChanged();}),Button("▶ Play queue",async()=>await PlayQueue()),QueueSetting("Blend (s)",queueBlend),queueAtWindow,queueWindowEvent,(queueWindowButton=Button("Open in window",ToggleQueueWindow)),queueNote]);
        queueBody.Controls.Add(queueList);queueBody.Controls.Add(queueButtons);
        queueTab=new TabPage("Queue"){Padding=new Padding(8),Tag=Color.FromArgb(178,132,255)};queueTab.Controls.Add(queueBody);tabs.TabPages.Add(queueTab);inspectorTabs=tabs;RefreshQueue();
        // Drag a queued clip to reorder it, or drag a clip in from the library to insert it at that place.
        tree.ItemDrag+=(_,e)=>{if(e.Item is TreeNode node&&LibraryClips(node) is {Length:>0} picked)tree.DoDragDrop(picked,DragDropEffects.Copy);};
        queueList.MouseDown+=(_,e)=>{
            int hit=queueList.IndexFromPoint(e.Location);
            if(e.Button==MouseButtons.Left&&hit>=0&&QueueChip(hit) is {} chip&&chip.Contains(e.Location)){queueDragFrom=-1;ShowWindowMenu(hit,chip);return;}
            queueDragFrom=e.Button==MouseButtons.Left?hit:-1;queueDragStart=e.Location;
        };
        queueList.MouseMove+=(_,e)=>{
            if(queueDragFrom<0||e.Button!=MouseButtons.Left)return;
            var size=SystemInformation.DragSize;
            if(Math.Abs(e.X-queueDragStart.X)<size.Width&&Math.Abs(e.Y-queueDragStart.Y)<size.Height)return;
            int from=queueDragFrom;queueDragFrom=-1;
            SetQueueDragState(from,-1);
            try{queueList.DoDragDrop(new QueueDrag(from),DragDropEffects.Move);}finally{SetQueueDragState(-1,-1);}
        };
        queueList.DragOver+=(_,e)=>{
            e.Effect=e.Data?.GetDataPresent(typeof(QueueDrag))==true?DragDropEffects.Move:e.Data?.GetDataPresent(typeof(ClipEntry[]))==true?DragDropEffects.Copy:DragDropEffects.None;
            SetQueueDragState(queueDragging,e.Effect==DragDropEffects.None?-1:QueueDropIndex(e.X,e.Y));
        };
        queueList.DragLeave+=(_,_)=>SetQueueDragState(queueDragging,-1);
        queueList.DrawItem+=(_,e)=>{if(e.Index>=0)DrawQueueItem(e.Graphics,e.Bounds,e.Index,(e.State&DrawItemState.Selected)!=0);};
        queueList.DragDrop+=(_,e)=>{
            SetQueueDragState(queueDragging,-1);
            int target=QueueDropIndex(e.X,e.Y);
            if(e.Data?.GetData(typeof(QueueDrag)) is QueueDrag drag)MoveQueueItem(drag.From,target);
            else if(e.Data?.GetData(typeof(ClipEntry[])) is ClipEntry[] picked)InsertIntoQueue(picked,target);
        };
        queueList.DoubleClick+=(_,_)=>{if(QueuePlaying&&queueList.SelectedIndex>=0&&queueList.SelectedIndex<clip!.Segments!.Count){double at=clip.Segments[queueList.SelectedIndex].Start;clock.Seek(at);scene.SetTime(at);viewport.InvalidateRender();}};
        var side=new Panel(){Dock=DockStyle.Fill,AutoScroll=true,Padding=new Padding(12)};workspace.Panel2.Controls.Add(side);
        var stack=new FlowLayoutPanel(){Dock=DockStyle.Top,AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};side.Controls.Add(stack);
        side.SizeChanged+=(_,_)=>ResponsiveLayout.Fit(stack,Math.Max(100,side.ClientSize.Width-side.Padding.Horizontal-SystemInformation.VerticalScrollBarWidth));
        void Labelled(string label,Control control) {stack.Controls.Add(new Label{Text=label,AutoSize=true,Margin=new Padding(0,13,0,6)});stack.Controls.Add(control);}
        Labelled("CAMERA",followCamera);stack.Controls.Add(requireAlt);stack.Controls.Add(gridFloor);cameraSubject.Items.AddRange(["Frame whole scene","Frame character","Frame effects"]);cameraSubject.SelectedIndex=1;stack.Controls.Add(cameraSubject);
        var skeletonRow=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=false,Margin=new Padding(0)};
        skeletonRow.Controls.Add(showSkeleton);skeletonRow.Controls.Add(bonesButton=Button("Bones…",OpenBonesWindow));
        Labelled("CHARACTER",skeletonRow);stack.Controls.Add(races);sex.Items.AddRange(["Male","Female"]);sex.SelectedIndex=0;stack.Controls.Add(sex);
        Labelled("HAIR COLOR · hair, brows and wigs",hairColor);stack.Controls.Add(hairNote);
        Labelled("HEAD PARTS",new Panel{Width=310,Height=180,Controls={heads}});
        Labelled("EQUIPMENT · all biped slots",slot);slot.Items.Add(new SlotChoice(null,"All slots"));for(int i=30;i<=61;i++)slot.Items.Add(new SlotChoice(i-30,$"{i} · {SlotName(i)}"));slot.SelectedIndex=3;((SearchableComboBox)armor).KeepFilter=true;
        stack.Controls.Add(armor);stack.Controls.Add(Button("Equip armor",Equip));
        Labelled("WEIGHT · body and armor _0/_1 blend",weight);stack.Controls.Add(weightLabel);
        weight.ValueChanged+=(_,_)=>{scene.Weight=weight.Value;UpdateWeightLabel();if(!busy&&!selecting)SaveSession();};
        Labelled("PHYSICS · HDT-SMP cloth, hair and weapon straps",smpPhysics);stack.Controls.Add(smpColliders);stack.Controls.Add(smpBones);stack.Controls.Add(Button("Reload meshes and physics XML",RebuildScene));stack.Controls.Add(smpLabel);UpdatePhysicsLabel();
        Labelled("PRECISION · Collision_* annotation capsules",precisionShow);stack.Controls.Add(precisionActor);stack.Controls.Add(precisionLabel);UpdatePrecisionLabel();
        Labelled("WORN ITEMS",equipped);stack.Controls.Add(Button("Remove selected",()=>{if(equipped.SelectedItem is GameRecord r){worn.Remove(r);RebuildScene();}}));
        Labelled("WEAPON",weapons);stack.Controls.Add(showWeapon);stack.Controls.Add(sheathed);stack.Controls.Add(Button("Add mesh…",ChooseMesh));stack.Controls.Add(Button("Add effect overlay…",ChooseEffectOverlay));stack.Controls.Add(Button("Clear effect overlays",()=>{effectOverlays.Clear();RebuildScene();}));stack.Controls.Add(Button("Preview effect NIF…",async()=>await ChooseParticle()));stack.Controls.Add(Button("Skeleton NIF…",ChooseSkeleton));
        Labelled("EFFECT SEQUENCE",effectSequence);effectSequence.Items.Add("Automatic");effectSequence.SelectedIndex=0;
        stack.Controls.Add(Button("Emitter sources…",ChooseEmitterSources));
        Labelled("PARTICLE BUDGET · live particles per system",particleBudget);
        particleBudget.ValueChanged+=(_,_)=>{ParticleNif.PreviewBudget=(int)particleBudget.Value;scene.ResetParticles();scene.SetTime(clock.Time);if(!busy&&!selecting)SaveSession();};
        stack.Controls.Add(new Label{Text="0 = authored NiPSysData limit only. Some mod effects author 9,999 births per second; this keeps playback interactive. Dropped births are listed in Diagnostics.",AutoSize=true,MaximumSize=new Size(310,0)});
        Labelled("PARTICLE LOD · normalized test distance",particleLod);
        particleLod.ValueChanged+=(_,_)=>scene.SetParticleLodDistance((float)particleLod.Value);
        stack.Controls.Add(new Label{Text="0 = full detail. Fixed test value; camera distance is not mapped automatically.",AutoSize=true,MaximumSize=new Size(310,0)});
        Labelled("PROJECTILE ELEVATION · degrees",projectileElevation);
        projectileElevation.ValueChanged+=(_,_)=>{
            effects.ProjectileElevation=(float)projectileElevation.Value*MathF.PI/180;
            if(!selecting&&!busy&&(clip!=null||manualTriggers.Count>0||referenceActions.Count>0)){cues=effects.Build(clip??new(),conditionalDac.Checked,manualTriggers,referenceActions);RebuildScene();}
            if(!selecting&&!busy)try{SaveSession();}catch(Exception ex){ShowError(ex);}
        };
        stack.Controls.Add(new Label{Text="Positive angles fire upward. Applies to resolved projectile effects; gravity stays vertical.",AutoSize=true,MaximumSize=new Size(310,0)});
        stack.Controls.Add(Button("Plugin visual effects…",ChooseReferenceEffect));stack.Controls.Add(Button("Impacts / explosions...",ChooseImpactEffect));Labelled("EFFECT TRIGGERS",triggers);stack.Controls.Add(Button("Preview selected trigger",TriggerEffect));stack.Controls.Add(Button("Clear manual triggers",ClearManualTriggers));stack.Controls.Add(conditionalDac);
        stack.Controls.Add(new Label{Text="Conditional effects assume their conditions pass.\nRead the original conditions in the inspector.",AutoSize=true,MaximumSize=new Size(310,0),Margin=new Padding(0,6,0,8)});
        triggers.SelectedIndexChanged+=(_,_)=>{if(triggers.SelectedItem is Trigger t)detail.Text=$"{t.Name}\n{t.Source}\n\n{string.Join("\n",t.Instructions)}\n\n{t.Conditions}";};
        var transport=new TableLayoutPanel(){Dock=DockStyle.Bottom,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,RowCount=2,ColumnCount=1};
        transport.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));transport.RowStyles.Add(new RowStyle(SizeType.Absolute,64));transport.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var buttons=new FlowLayoutPanel(){Dock=DockStyle.Fill,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(4),WrapContents=true};
        Control Setting(string label,Control input){var group=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=false,Margin=new Padding(2)};group.Controls.Add(new Label{Text=label,AutoSize=true,Padding=new Padding(2,7,0,0)});group.Controls.Add(input);return group;}
        buttons.Controls.AddRange([Button("▶ / Ⅱ",TogglePlay),Button("■",()=>{clock.Pause();clock.Seek(0);scene.SetTime(0);}),Button("‹",()=>SeekFrame(-1)),Button("›",()=>SeekFrame(1)),loop,rootMotion,effectToggle,playSounds,snapFrames,Setting("Step FPS",fps),Setting("Speed",speed),speedNote,timeLabel,clipTitle]);
        transport.Controls.Add(timeline,0,0);transport.Controls.Add(buttons,0,1);
        center.Panel1.Controls.Add(transport);
        Controls.Add(outer);Controls.Add(status);Controls.Add(toolbar);
        Shown+=(_,_)=>{library.Panel1MinSize=120;library.Panel2MinSize=110;library.SplitterDistance=Math.Max(120,library.Height-210);outer.SplitterDistance=380;workspace.SplitterDistance=Math.Max(450,workspace.Width-368);center.SplitterDistance=Math.Max(300,center.Height-235);};
    }
    static Button Button(string label,Action click){var b=new Button{Text=label,AutoSize=true,Height=32,FlatStyle=FlatStyle.Flat,Margin=new Padding(3),Padding=new Padding(8,2,8,2)};b.FlatAppearance.BorderSize=0;b.Click+=(_,_)=>click();return b;}
    static TabPage Tab(TabControl tabs,string label,Control body){
        var p=new TabPage(label){Padding=new Padding(8)};p.Controls.Add(body);
        var expand=Button("Expand / search…",()=>{var reader=CreateInspector(label,body.Text);reader.Show(tabs.FindForm());});
        expand.Dock=DockStyle.Top;expand.Height=30;p.Controls.Add(expand);tabs.TabPages.Add(p);return p;
    }
    static Form CreateInspector(string title,string text)
    {
        var reader=new Form{Text=title,Size=new(1100,780),MinimumSize=new(650,400),StartPosition=FormStartPosition.CenterParent};
        var content=TextPanel();content.Text=text;content.HideSelection=false;
        var bar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=46,Padding=new Padding(6),WrapContents=false};
        var query=new TextBox{Width=320,PlaceholderText="Find in this configuration…"};
        var result=new Label{AutoSize=true,Padding=new Padding(6)};
        void FindNext(){
            if(query.Text.Length==0)return;
            int start=content.SelectionStart+content.SelectionLength;
            int found=content.Find(query.Text,Math.Min(start,content.TextLength),RichTextBoxFinds.None);
            if(found<0)found=content.Find(query.Text,0,RichTextBoxFinds.None);
            result.Text=found<0?"No match":"";
            if(found>=0){content.Select(found,query.Text.Length);content.ScrollToCaret();}
        }
        query.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Enter){FindNext();e.SuppressKeyPress=true;}};
        var wrap=new CheckBox{Text="Wrap lines",AutoSize=true,Checked=true};wrap.CheckedChanged+=(_,_)=>content.WordWrap=wrap.Checked;
        bar.Controls.AddRange([query,Button("Find next",FindNext),wrap,result]);reader.Controls.Add(content);reader.Controls.Add(bar);
        Theme(reader);return reader;
    }
    static void Theme(Control c){c.BackColor=Color.FromArgb(28,32,40);c.ForeColor=Color.FromArgb(218,224,235);if(c is Button)c.BackColor=Color.FromArgb(48,59,73);if(c is TextBox or RichTextBox or ComboBox or ListBox or TreeView)c.BackColor=Color.FromArgb(20,24,31);if(c is not RichTextBox)c.Font=new Font("Segoe UI",9);foreach(Control child in c.Controls)Theme(child);}
    // Vanilla names for 30-43, 50, 51 and 61; the unnamed slots carry the usage the Nexus wiki body-part list suggests.
    static string SlotName(int s)=>s switch{30=>"Head",31=>"Hair",32=>"Body",33=>"Hands",34=>"Forearms",35=>"Amulet",36=>"Ring",37=>"Feet",38=>"Calves",39=>"Shield",40=>"Tail",41=>"Long hair",42=>"Circlet",43=>"Ears",
        44=>"(face/mouth)",45=>"(neck: cape, scarf, shawl, neck-tie)",46=>"(chest primary or outergarment)",47=>"(back: backpack, wings)",48=>"(misc/FX)",49=>"(pelvis primary or outergarment)",
        50=>"Decapitate head",51=>"Decapitate",52=>"(pelvis secondary or undergarment)",53=>"(leg primary or outergarment or right leg)",54=>"(leg secondary or undergarment or left leg)",
        55=>"(face alternate or jewelry)",56=>"(chest secondary or undergarment)",57=>"(shoulder)",58=>"(arm secondary or undergarment or left arm)",59=>"(arm primary or outergarment or right arm)",60=>"(misc/FX)",61=>"FX",_=>"Unnamed"};
    // One entry of the equipment slot list; Bit is null for the All entry.
    sealed record SlotChoice(int? Bit,string Label){public override string ToString()=>Label;}
    async Task ChooseMount(bool overlay){using var d=new FolderBrowserDialog{Description=overlay?"Choose a loose mod folder; later sources win":"Choose Skyrim Data, MO2 virtual Data, or a small mod folder"};if(d.ShowDialog()==DialogResult.OK)await Mount(d.SelectedPath,overlay);}
    async Task Mount(string folder,bool overlay)
    {
        if(busy)return;busy=true;clock.Pause();UseWaitCursor=true;
        try {
            status.Text="Loading "+folder;await assets.Mount(folder,overlay,new Progress<string>(s=>status.Text=s));
            await Task.Run(()=>{records.Load(assets,pluginsFile);effects.ReadConfigs();});
            // Adding a folder refills the record lists; keep the current race, weapon, head parts and trigger
            // by record key so the character survives. Defaults apply only when nothing was chosen before.
            string? raceKey=(races.SelectedItem as GameRecord)?.Key,weaponKey=(weapons.SelectedItem as GameRecord)?.Key;
            var headKeys=heads.CheckedItems.Cast<GameRecord>().Select(h=>h.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);bool hadHeads=heads.Items.Count>0;
            string? triggerName=(triggers.SelectedItem as Trigger)?.Name;
            selecting=true;races.Items.Clear();races.Items.AddRange(records.Of("RACE").Cast<object>().ToArray());
            var keptRace=raceKey==null?null:records.Of("RACE").FirstOrDefault(r=>r.Key.Equals(raceKey,StringComparison.OrdinalIgnoreCase));
            if(keptRace!=null)races.SelectedItem=keptRace;
            else{var nord=records.Of("RACE").FirstOrDefault(r=>r.EditorId=="NordRace");if(nord!=null)races.SelectedItem=nord;else if(races.Items.Count>0)races.SelectedIndex=0;if(raceKey!=null)assets.Diagnostics.Add("Selected race "+raceKey+" is no longer loaded; default race selected.");}
            weapons.Items.Clear();weapons.Items.Add("(None)");weapons.Items.AddRange(records.Of("WEAP").Cast<object>().ToArray());weapons.SelectedIndex=0;
            if(weaponKey!=null){var keptWeapon=records.Of("WEAP").FirstOrDefault(w=>w.Key.Equals(weaponKey,StringComparison.OrdinalIgnoreCase));if(keptWeapon!=null)weapons.SelectedItem=keptWeapon;else assets.Diagnostics.Add("Selected weapon "+weaponKey+" is no longer loaded; weapon cleared.");}
            triggers.DisplayMember=nameof(Trigger.Name);triggers.Items.Clear();triggers.Items.AddRange(effects.Triggers.Cast<object>().ToArray());
            int keptTrigger=triggerName==null?-1:triggers.Items.Cast<Trigger>().ToList().FindIndex(t=>t.Name==triggerName);
            if(triggers.Items.Count>0)triggers.SelectedIndex=Math.Max(0,keptTrigger);
            selecting=false;PopulateHeads();
            if(hadHeads&&keptRace!=null){selecting=true;try{for(int i=0;i<heads.Items.Count;i++)heads.SetItemChecked(i,headKeys.Contains(((GameRecord)heads.Items[i]).Key));}finally{selecting=false;}}
            PopulateArmor();BuildTree();RebuildScene();
            status.Text=$"{assets.Clips.Count:N0} animations · {records.All.Count:N0} records · {assets.Roots.Count} source(s) · render cap 60 FPS";
            SaveSession();
        }catch(Exception ex){ShowError(ex);}finally{busy=false;UseWaitCursor=false;RefreshSources();}
    }
    void ChoosePlugins(){using var d=new OpenFileDialog{Filter="MO2 / game load order (plugins.txt)|plugins.txt|Text files|*.txt"};if(d.ShowDialog()==DialogResult.OK){pluginsFile=d.FileName;Config_App.Current.Setting_PluginsTxtPath_SSE=pluginsFile;_=ReloadMounted();}}
    async Task ReloadMounted(){var roots=assets.Roots.ToArray();for(int i=0;i<roots.Length;i++)await Mount(roots[i],i>0);}
    SessionSettings CaptureSettings()=>new(assets.Roots.ToArray(),pluginsFile,assets.DroppedPaths.ToArray(),(float)projectileElevation.Value,cameraSubject.SelectedIndex,followCamera.Checked,gridFloor.Checked,
        (races.SelectedItem as GameRecord)?.Key,sex.SelectedIndex,heads.CheckedItems.Cast<GameRecord>().Select(h=>h.Key).ToArray(),worn.Select(w=>w.Key).ToArray(),(weapons.SelectedItem as GameRecord)?.Key,selected?.Path,clock.Time,(double)fps.Value,(double)speed.Value,loop.Checked,rootMotion.Checked,effectToggle.Checked,conditionalDac.Checked,(float)particleLod.Value,customModels.ToArray(),customSkeleton,ShowWeapon:showWeapon.Checked,Sheathed:sheathed.Checked,Weight:weight.Value,ParticleBudget:(int)particleBudget.Value,EffectOverlays:effectOverlays.ToArray(),SmpPhysics:smpPhysics.Checked,SmpColliders:smpColliders.Checked,SmpBones:smpBones.Checked,HairColor:hairChoice,SnapFrames:snapFrames.Checked,Queue:queue.Select(q=>q.Path).ToArray(),QueuePlaying:QueuePlaying,QueueBlend:(double)queueBlend.Value,QueueAtWindow:queueAtWindow.Checked,QueueWindow:queueWindowEvent.SelectedItem as string,PrecisionCollisions:precisionShow.Checked,PrecisionNpc:precisionActor.SelectedIndex==1,PlaySounds:playSounds.Checked,QueueWindowPicks:queue.Select(q=>queueWindowPicks.GetValueOrDefault(q)).ToArray(),ShowSkeleton:showSkeleton.Checked,ShowBoneNames:showBoneNames.Checked,BoneFilter:scene.BoneFilter.OrderBy(b=>b,StringComparer.OrdinalIgnoreCase).ToArray());
    void SaveSession()=>File.WriteAllText(settingsPath,JsonSerializer.Serialize(CaptureSettings(),new JsonSerializerOptions{WriteIndented=true}));
    void PresetMenu(){
        if(busy)return;
        if(sceneMenu==null){
            sceneMenu=new ContextMenuStrip();
            sceneMenu.Items.Add("Save scene...",null,(_,_)=>SavePreset());
            sceneMenu.Items.Add("Load scene...",null,async(_,_)=>await LoadPreset());
            recentMenu=new ToolStripMenuItem("Recent");sceneMenu.Items.Add(recentMenu);
        }
        RefreshRecentMenu();
        // Closed runs before the item's Click. Keep handlers alive until the window closes.
        sceneMenu.Show(Cursor.Position);
    }
    void RefreshRecentMenu(){
        if(recentMenu==null)return;
        foreach(ToolStripItem old in recentMenu.DropDownItems.Cast<ToolStripItem>().ToArray())old.Dispose();recentMenu.DropDownItems.Clear();
        if(recentScenes.Entries.Count==0){recentMenu.DropDownItems.Add(new ToolStripMenuItem("No recent scenes"){Enabled=false});return;}
        foreach(string path in recentScenes.Entries){
            string label=Path.GetFileName(path)+" — "+Path.GetDirectoryName(path)+(File.Exists(path)?"":" (missing)");
            var item=new ToolStripMenuItem(label){ToolTipText=path,Tag=path};
            item.Click+=async(_,_)=>{string? file=File.Exists(path)?path:QueryMissingScenePath(path,false);if(!string.IsNullOrEmpty(file))await RestoreFile(file);};
            recentMenu.DropDownItems.Add(item);
        }
    }
    void RememberScene(string path){try{recentScenes.Remember(path);}catch(Exception ex){assets.Diagnostics.Add("Could not save recent scenes: "+ex.Message);UpdateDiagnostics();}}
    async Task SourcesRecentSmoke(){
        var log=new List<string>();string folder=Path.Combine(AppContext.BaseDirectory,"recent-source-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        void Check(bool ok,string text){log.Add((ok?"PASS ":"FAIL ")+text);if(!ok)throw new InvalidDataException(text);}
        try{
            var history=new RecentScenes(Path.Combine(folder,"history.json"));
            for(int i=0;i<15;i++)history.Remember(Path.Combine(folder,$"Scene {i}.ssescene.json"));
            Check(history.Entries.Count==12&&history.Entries[0].EndsWith("Scene 14.ssescene.json"),"Recent scenes are newest-first and capped at twelve");
            history.Remember(history.Entries[4].ToUpperInvariant());
            var restoredHistory=new RecentScenes(Path.Combine(folder,"history.json"));
            Check(restoredHistory.Entries.Count==12&&restoredHistory.Entries[0].EndsWith("SCENE 10.SSESCENE.JSON"),"Reopening moves an existing scene to the top without case-sensitive duplicates; history survives restart");
            string first=Path.Combine(folder,"First.ssescene.json"),second=Path.Combine(folder,"Second.ssescene.json");
            speed.Value=.65m;WriteScene(first);speed.Value=1.25m;WriteScene(second);
            PresetMenu();Check(recentMenu!.DropDownItems.Count>=2,"Scenes menu contains the Recent submenu");
            var entry=recentMenu.DropDownItems.Cast<ToolStripMenuItem>().Single(item=>(string?)item.Tag==first);
            entry.PerformClick();for(int i=0;i<100&&(busy||sceneFile!=first);i++)await Task.Delay(50);
            Check(sceneFile==first&&speed.Value==.65m&&!busy,"Clicking a recent scene loads it through the normal scene loader");
            Check(recentScenes.Entries[0]==first,"A successfully loaded recent scene moves to the top");
            sources.Items.Clear();
            for(int i=0;i<24;i++)sources.Items.Add(new SourceEntry($"{i+1} · Mod folder · Long mod name with armor, animation and particle assets number {i+1} — 1,234 loose files · 2 BSA",Path.Combine(folder,$"Mod {i}"),true));
            var outer=(SplitContainer)sources.Parent!.Parent!.Parent!.Parent!;
            foreach(int width in new[]{330,560,330}){
                outer.SplitterDistance=width;await Task.Delay(100);((WrappedListBox)sources).Reflow();
                Check(Enumerable.Range(0,sources.Items.Count).All(i=>sources.GetItemHeight(i)>=((WrappedListBox)sources).RequiredHeight(i)),"All source rows fit wrapped text at panel width "+width);
                Check(sources.GetItemHeight(0)>sources.Font.Height+8,"Long source rows receive multiple lines at panel width "+width);
            }
            sources.TopIndex=sources.Items.Count-1;await Task.Delay(100);
            Check(sources.GetItemRectangle(sources.Items.Count-1).IntersectsWith(sources.ClientRectangle),"Scrolling can reach the final source without compressing earlier rows");
            tree.Nodes.Clear();var dar=tree.Nodes.Add("DAR · 75");dar.Nodes.Add("FF7 Cloud Animations");dar.Expand();var vanilla=tree.Nodes.Add("Vanilla / loose · 5,938");vanilla.Nodes.Add("Mounted Data");vanilla.Expand();tree.Nodes.Add("A deliberately long folder name that should remain on a single compact row and end with an ellipsis");
            foreach(int width in new[]{330,560,330}){outer.SplitterDistance=width;await Task.Delay(80);Check(tree.ItemHeight<=tree.Font.Height+6,"Animation folders stay single-line at panel width "+width);}
            using(var libraryImage=new Bitmap(tree.Width,tree.Height)){tree.DrawToBitmap(libraryImage,tree.ClientRectangle);libraryImage.Save(Path.Combine(AppContext.BaseDirectory,"compact-animation-library.png"));}
            sources.TopIndex=0;
            using(var image=new Bitmap(sources.Width,sources.Height)){sources.DrawToBitmap(image,sources.ClientRectangle);image.Save(Path.Combine(AppContext.BaseDirectory,"loaded-sources-wrapped.png"));}
            var splitter=(SplitContainer)sources.Parent!.Parent!;int before=sources.Height;splitter.SplitterDistance-=90;await Task.Delay(100);
            Check(sources.Height>before,"Dragging the library/source divider makes the source area taller");
            using(var image=new Bitmap(sources.Width,sources.Height)){sources.DrawToBitmap(image,sources.ClientRectangle);image.Save(Path.Combine(AppContext.BaseDirectory,"loaded-sources-expanded.png"));}
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"sources-recent-tests.txt"),log);}finally{Close();}}
    }
    async Task SceneSaveSmoke(){
        var log=new List<string>();string folder=Path.Combine(AppContext.BaseDirectory,"scene-save-verification",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        void Check(bool passed,string message){log.Add((passed?"PASS ":"FAIL ")+message);if(!passed)throw new InvalidDataException(message);}
        try{
            string file=Path.Combine(folder,"Cloud scene — test.ssescene.json");
            speed.Value=.75m;fps.Value=24;WriteScene(file);
            var first=JsonSerializer.Deserialize<SessionSettings>(File.ReadAllText(file))!;
            Check(first.Speed==.75&&first.Fps==24,"New scene saves current settings to a path containing spaces and Unicode");
            string original=File.ReadAllText(file);
            using(var dialog=CreateSceneSaveDialog())Check(dialog.FileName==file&&dialog.OverwritePrompt&&dialog.AddExtension&&dialog.DefaultExt=="ssescene.json","Save browser remembers current scene, adds the scene extension and requires overwrite confirmation");
            PresetMenu();await Task.Delay(100);
            Check(SceneDialogProbe.ClickAndCancel(this,sceneMenu!.Items[0],"Save preview scene")&&File.ReadAllText(file)==original,"Cancelling the real Save browser leaves the existing scene byte-for-byte unchanged");
            speed.Value=1.5m;fps.Value=60;loop.Checked=false;
            PresetMenu();await Task.Delay(100);
            Check(SceneDialogProbe.SaveAndAnswerOverwrite(this,sceneMenu!.Items[0],false)&&File.ReadAllText(file)==original,"Declining the actual overwrite confirmation preserves the existing scene");
            PresetMenu();await Task.Delay(100);
            Check(SceneDialogProbe.SaveAndAnswerOverwrite(this,sceneMenu!.Items[0],true),"Accepting the actual overwrite confirmation completes Save");
            var second=JsonSerializer.Deserialize<SessionSettings>(File.ReadAllText(file))!;
            Check(second.Speed==1.5&&second.Fps==60&&!second.Loop&&File.ReadAllText(file)!=original,"Overwrite replaces the complete scene with edited settings");
            string overwritten=File.ReadAllText(file);
            bool rejected=false;using(var locked=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read)){
                try{SceneFile.Save(file,"replacement that must not be committed");}catch(IOException){rejected=true;}
            }
            Check(rejected&&File.ReadAllText(file)==overwritten,"Failed overwrite of a locked file preserves the previous scene");
            Check(!Directory.EnumerateFiles(folder,"*.tmp").Any(),"Failed and successful saves leave no temporary files");
            ResetPreview(false);await RestoreFile(file);
            Check(speed.Value==1.5m&&fps.Value==60&&!loop.Checked&&sceneFile==file,"Overwritten scene reloads edited settings and keeps its save destination");
            string secondFile=Path.Combine(folder,"copy.ssescene.json");sceneFile=secondFile;
            PresetMenu();await Task.Delay(100);
            Check(!SceneDialogProbe.SaveAndAnswerOverwrite(this,sceneMenu!.Items[0],true),"New-file Save completes through the actual browser without an overwrite prompt");
            Check(File.Exists(secondFile)&&File.ReadAllText(file)==overwritten&&sceneFile==secondFile,"Saving to a new filename preserves the original scene");
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"scene-save-tests.txt"),log);}finally{Close();}}
    }
    async Task SceneDialogSmoke(){
        var results=new List<string>();
        try{
            for(int i=0;i<4;i++){
                PresetMenu();await Task.Delay(100);
                bool closed=false;ToolStripDropDownClosedEventHandler handler=(_,_)=>closed=true;sceneMenu!.Closed+=handler;
                bool opened=SceneDialogProbe.ClickAndCancel(this,sceneMenu.Items[i%2],i%2==0?"Save preview scene":"Load preview scene");
                sceneMenu.Closed-=handler;
                if(!opened||!closed||sceneMenu.IsDisposed)throw new InvalidOperationException($"Dialog {i}: opened={opened}, menu closed={closed}, disposed={sceneMenu.IsDisposed}");
                results.Add("PASS "+(i%2==0?"Save":"Load")+" scene opens its owned file browser and cancels; menu remains reusable");
            }
        }catch(Exception ex){results.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"scene-dialog-tests.txt"),results);}finally{Close();}}
    }
    SaveFileDialog CreateSceneSaveDialog(string? suggestion=null)=>new(){Title="Save preview scene",Filter="Preview scene (*.ssescene.json)|*.ssescene.json",FileName=suggestion??sceneFile??"Scene.ssescene.json",DefaultExt="ssescene.json",AddExtension=true,OverwritePrompt=true,CheckPathExists=true,RestoreDirectory=true};
    void SavePreset(){
        if(busy)return;
        using var dialog=CreateSceneSaveDialog();
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        try{WriteScene(dialog.FileName);status.Text="Scene saved: "+Path.GetFileName(dialog.FileName);}catch(Exception ex){ShowError(ex);}
    }
    void WriteScene(string path){
        SceneFile.Save(path,JsonSerializer.Serialize(CaptureSettings(),new JsonSerializerOptions{WriteIndented=true}));
        sceneFile=Path.GetFullPath(path);RememberScene(sceneFile);
    }
    async Task LoadPreset(){
        if(busy)return;using var dialog=new OpenFileDialog{Title="Load preview scene",InitialDirectory=Path.Combine(AppContext.BaseDirectory,"Scenes"),Filter="Preview scenes and legacy presets (*.json)|*.json"};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;await RestoreFile(dialog.FileName);
    }
    async Task Restore()=>await RestoreFile(settingsPath);
    async Task RestoreFile(string path){
        if(busy||!File.Exists(path))return;
        try{var settings=JsonSerializer.Deserialize<SessionSettings>(File.ReadAllText(path));if(settings?.Roots==null)throw new InvalidDataException("Scene has no source folder list.");var repaired=RepairScenePaths(settings,QueryMissingScenePath);if(repaired!=null){await ApplySettings(repaired);sceneFile=Path.GetFullPath(path).Equals(Path.GetFullPath(settingsPath),StringComparison.OrdinalIgnoreCase)?null:Path.GetFullPath(path);if(sceneFile!=null)RememberScene(sceneFile);}}
        catch(Exception ex){ShowError(ex);}
    }
    // null means cancel the whole load; an empty replacement explicitly skips this reference.
    string? QueryMissingScenePath(string path,bool folder){
        var choice=MessageBox.Show(this,$"This scene references a missing {(folder?"folder":"file")}:\n\n{path}\n\nYes: locate a replacement\nNo: skip this reference\nCancel: keep the current scene", "Missing scene path",MessageBoxButtons.YesNoCancel,MessageBoxIcon.Question);
        if(choice==DialogResult.Cancel)return null;if(choice==DialogResult.No)return "";
        if(folder){using var picker=new FolderBrowserDialog{Description="Locate replacement for "+path};return picker.ShowDialog(this)==DialogResult.OK?picker.SelectedPath:null;}
        using var file=new OpenFileDialog{Title="Locate replacement for "+Path.GetFileName(path),Filter="All files|*.*"};return file.ShowDialog(this)==DialogResult.OK?file.FileName:null;
    }
    static SessionSettings? RepairScenePaths(SessionSettings settings,Func<string,bool,string?> query){
        var replacements=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);bool cancelled=false;
        string? Resolve(string? path,bool folder=false){
            if(string.IsNullOrWhiteSpace(path))return path;
            if(replacements.TryGetValue(path,out var cached))return cached.Length==0?null:cached;
            string candidate=path;
            foreach(var pair in replacements.OrderByDescending(p=>p.Key.Length)){
                string prefix=pair.Key.TrimEnd('\\','/')+Path.DirectorySeparatorChar;
                if(path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)&&pair.Value.Length>0){candidate=Path.Combine(pair.Value,path[prefix.Length..]);break;}
            }
            if(folder?Directory.Exists(candidate):File.Exists(candidate)){replacements[path]=candidate;return candidate;}
            string? answer=query(candidate,folder);if(answer==null){cancelled=true;return null;}
            replacements[path]=answer;return answer.Length==0?null:answer;
        }
        var roots=new List<string>();foreach(string root in settings.Roots){var value=Resolve(root,true);if(cancelled)return null;if(value!=null&&!roots.Contains(value,StringComparer.OrdinalIgnoreCase))roots.Add(value);}
        string? plugins=Resolve(settings.Plugins);if(cancelled)return null;
        var dropped=new List<string>();foreach(string path in settings.Dropped??[]){var value=Resolve(path);if(cancelled)return null;if(value!=null)dropped.Add(value);}
        var models=new List<string>();foreach(string path in settings.Models??[]){var value=Path.IsPathRooted(path)?Resolve(path):path;if(cancelled)return null;if(value!=null)models.Add(value);}
        var overlays=new List<string>();foreach(string path in settings.EffectOverlays??[]){var value=Path.IsPathRooted(path)?Resolve(path):path;if(cancelled)return null;if(value!=null)overlays.Add(value);}
        string? skeleton=settings.Skeleton;if(skeleton!=null&&Path.IsPathRooted(skeleton))skeleton=Resolve(skeleton);if(cancelled)return null;
        string? animation=settings.Clip;if(animation!=null&&Path.IsPathRooted(animation))animation=Resolve(animation);if(cancelled)return null;
        if(animation!=null&&Path.IsPathRooted(animation)&&!dropped.Contains(animation,StringComparer.OrdinalIgnoreCase))dropped.Add(animation);
        var queued=new List<string>();foreach(string path in settings.Queue??[]){var value=Path.IsPathRooted(path)?Resolve(path):path;if(cancelled)return null;if(value==null)continue;queued.Add(value);if(Path.IsPathRooted(value)&&!dropped.Contains(value,StringComparer.OrdinalIgnoreCase))dropped.Add(value);}
        return settings with {Roots=roots.ToArray(),Plugins=plugins,Dropped=dropped.ToArray(),Models=models.ToArray(),EffectOverlays=overlays.ToArray(),Skeleton=skeleton,Clip=animation,Queue=queued.ToArray()};
    }
    async Task ApplySettings(SessionSettings settings){
        ResetPreview(false);busy=true;UseWaitCursor=true;
        foreach(Control control in Controls)if(control!=status)control.Enabled=false;
        try{
        pluginsFile=settings.Plugins;Config_App.Current.Setting_PluginsTxtPath_SSE=pluginsFile??"";
        var progress=new Progress<string>(message=>status.Text=message);
        await Task.Run(async()=>{
            foreach(string root in settings.Roots){
                if(Directory.Exists(root)){((IProgress<string>)progress).Report("Loading scene source: "+root);await assets.Mount(root,assets.Roots.Count>0,progress,false);}
                else assets.Diagnostics.Add("Scene source unavailable: "+root);
            }
            ((IProgress<string>)progress).Report("Loading plugin records and animation library...");
            records.Load(assets,pluginsFile);effects.ReadConfigs();assets.RestoreDroppedPaths(settings.Dropped??[]);
        });
        selecting=true;
        races.Items.Clear();races.Items.AddRange(records.Of("RACE").Cast<object>().ToArray());
        if(races.Items.Count>0)races.SelectedIndex=0;
        weapons.Items.Clear();weapons.Items.Add("(None)");weapons.Items.AddRange(records.Of("WEAP").Cast<object>().ToArray());weapons.SelectedIndex=0;
        triggers.DisplayMember=nameof(Trigger.Name);triggers.Items.Clear();triggers.Items.AddRange(effects.Triggers.Cast<object>().ToArray());
        selecting=false;
        customModels.AddRange(settings.Models??[]);effectOverlays.AddRange(settings.EffectOverlays??[]);customSkeleton=settings.Skeleton;
        scene.SkeletonHkx=customSkeleton!=null&&File.Exists(Path.ChangeExtension(customSkeleton,"hkx"))?Path.ChangeExtension(customSkeleton,"hkx"):@"meshes\actors\character\character assets\skeleton.hkx";
        selecting=true;
        hairChoice=settings.HairColor;
        try{sex.SelectedIndex=Math.Clamp(settings.Sex,0,1);
            if(settings.Race!=null){var race=records.All.GetValueOrDefault(settings.Race);if(race?.Kind=="RACE")races.SelectedItem=race;else assets.Diagnostics.Add("Scene race unavailable: "+settings.Race);}
        }finally{selecting=false;}
        PopulateHeads();selecting=true;
        try{
            if(settings.Heads!=null){var wanted=settings.Heads.ToHashSet(StringComparer.OrdinalIgnoreCase);for(int i=0;i<heads.Items.Count;i++)heads.SetItemChecked(i,wanted.Contains(((GameRecord)heads.Items[i]).Key));}
            worn.Clear();foreach(string key in settings.Armor??[]){if(records.All.GetValueOrDefault(key) is {Kind:"ARMO"} item)worn.Add(item);else assets.Diagnostics.Add("Scene armor unavailable: "+key);}
            if(settings.Weapon!=null){if(records.All.GetValueOrDefault(settings.Weapon) is {Kind:"WEAP"} weapon)weapons.SelectedItem=weapon;else assets.Diagnostics.Add("Scene weapon unavailable: "+settings.Weapon);}
            projectileElevation.Value=(decimal)Math.Clamp(settings.ProjectileElevation,-89,89);cameraSubject.SelectedIndex=Math.Clamp(settings.CameraSubject,0,2);followCamera.Checked=settings.FollowCamera;gridFloor.Checked=settings.GridFloor;showWeapon.Checked=settings.ShowWeapon;sheathed.Checked=settings.Sheathed;smpPhysics.Checked=settings.SmpPhysics;smpColliders.Checked=settings.SmpColliders;smpBones.Checked=settings.SmpBones;precisionShow.Checked=settings.PrecisionCollisions;playSounds.Checked=settings.PlaySounds;showSkeleton.Checked=settings.ShowSkeleton;showBoneNames.Checked=settings.ShowBoneNames;scene.BoneFilter.Clear();foreach(string bone in settings.BoneFilter??[])scene.BoneFilter.Add(bone);precisionActor.SelectedIndex=settings.PrecisionNpc?1:0;snapFrames.Checked=settings.SnapFrames;weight.Value=Math.Clamp(settings.Weight,0,100);particleBudget.Value=Math.Clamp(settings.ParticleBudget,0,65535);ParticleNif.PreviewBudget=(int)particleBudget.Value;
        }finally{selecting=false;}
        loop.Checked=settings.Loop;rootMotion.Checked=settings.RootMotion;effectToggle.Checked=settings.Effects;conditionalDac.Checked=settings.ConditionalDac;
        fps.Value=(decimal)Math.Clamp(double.IsFinite(settings.Fps)?settings.Fps:30,1,60);speed.Value=(decimal)Math.Clamp(double.IsFinite(settings.Speed)?settings.Speed:1,.05,4);
        particleLod.Value=(decimal)Math.Clamp(float.IsFinite(settings.ParticleLod)?settings.ParticleLod:0,0,10);
        BuildTree();PopulateArmor();
        status.Text="Preparing character meshes...";
        var chosenRace=races.SelectedItem as GameRecord;bool chosenFemale=sex.SelectedIndex==1;
        var body=chosenRace==null?null:records.Resolve(chosenRace,chosenRace.First("WNAM")?.UInt()??0);
        var meshes=worn.Concat(body==null?Array.Empty<GameRecord>():[body]).SelectMany(item=>records.ArmorModels(item,chosenRace,chosenFemale))
            .Concat(records.HeadPartModels(heads.CheckedItems.Cast<GameRecord>())).Concat(customModels).ToList();
        if(weapons.SelectedItem is GameRecord selectedWeapon&&selectedWeapon.First("MODL") is {} model)meshes.Add(model.Text);
        await scene.PrepareModelsAsync(meshes);
        if(settings.Clip!=null){var entry=assets.Clips.FirstOrDefault(c=>c.Path.Equals(settings.Clip,StringComparison.OrdinalIgnoreCase));if(entry!=null){await SelectClip(entry,resetSpeed:false);clock.Pause();}else assets.Diagnostics.Add("Scene animation unavailable: "+settings.Clip);}
        selecting=true;queueBlend.Value=(decimal)Math.Clamp(double.IsFinite(settings.QueueBlend)?settings.QueueBlend:.3,0,2);queueAtWindow.Checked=settings.QueueAtWindow;queueWindowEvent.SelectedItem=queueWindowEvent.Items.Contains(settings.QueueWindow??"")?settings.QueueWindow:"MCO_WinOpen";selecting=false;
        queue.Clear();queueWindowPicks.Clear();
        for(int k=0;k<(settings.Queue?.Length??0);k++){
            string path=settings.Queue![k];var entry=assets.Clips.FirstOrDefault(c=>c.Path.Equals(path,StringComparison.OrdinalIgnoreCase));
            if(entry==null){assets.Diagnostics.Add("Queued animation unavailable: "+path);continue;}
            var slot=entry with{};queue.Add(slot);
            if(settings.QueueWindowPicks is {} picks&&k<picks.Length&&picks[k] is {Count:>0} chosen)queueWindowPicks[slot]=new(chosen,StringComparer.OrdinalIgnoreCase);
        }
        RefreshQueue();
        if(settings.QueuePlaying&&queue.Count>0){busy=false;await PlayQueue();busy=true;clock.Pause();}
        if(clip==null)RebuildScene();
        clock.Pause();clock.Seek(double.IsFinite(settings.Time)?settings.Time:0);scene.SetTime(clock.Time);
        UpdateDiagnostics();RefreshSources();SaveSession();status.Text="Scene loaded; playback paused.";
        }finally{busy=false;selecting=false;UseWaitCursor=false;foreach(Control control in Controls)control.Enabled=true;}
    }
    void ResetPreview(bool save=true){
        if(busy)return;sceneFile=null;clock.Pause();selecting=true;
        try{
            clip=null;selected=null;scene.Clip=null;previewingEffect=false;clock.Duration=1;clock.Seek(0);worn.Clear();hairChoice=null;queue.Clear();RefreshQueue();cues.Clear();customModels.Clear();effectOverlays.Clear();customSkeleton=null;scene.SkeletonHkx=@"meshes\actors\character\character assets\skeleton.hkx";
            gridFloor.Checked=true;loop.Checked=true;rootMotion.Checked=true;effectToggle.Checked=true;conditionalDac.Checked=false;fps.Value=30;speed.Value=1;particleLod.Value=0;search.Clear();
            manualTriggers.Clear();referenceActions.Clear();referenceTargets.Clear();
            assets.Reset();records.All.Clear();records.Diagnostics.Clear();effects.ReadConfigs();
            races.Items.Clear();heads.Items.Clear();weapons.Items.Clear();weapons.Items.Add("(None)");weapons.SelectedIndex=0;armor.Items.Clear();equipped.Items.Clear();triggers.Items.Clear();tree.Nodes.Clear();effectSequence.Items.Clear();sex.SelectedIndex=0;
            pluginsFile=null;Config_App.Current.Setting_PluginsTxtPath_SSE="";
            scene.Clear();scene.Commit();scene.SetTime(0);detail.Clear();events.Clear();Highlight.Clear();diagnostics.Clear();clipTitle.Text="No animation selected";timeLabel.Text="0.000 s · frame 0";timeline.Value=0;
        }finally{selecting=false;}
        RefreshSources();if(save)SaveSession();status.Text="Preview reset. Load a scene or add a Data folder.";
    }
    void PopulateHeads()
    {
        selecting=true;heads.Items.Clear();
        var picked=new HashSet<uint>();
        var race=races.SelectedItem as GameRecord;bool female=sex.SelectedIndex==1;
        var defaults=records.DefaultHeadParts(race,female).ToArray();
        var defaultKeys=defaults.Select(h=>h.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var h in defaults.Concat(records.HeadParts(race,female).Where(h=>!defaultKeys.Contains(h.Key)))) {
            uint type=h.First("PNAM")?.UInt()??0;
            bool basic=type is 1 or 2 or 3 or 6;
            heads.Items.Add(h,basic && picked.Add(type));
        }
        selecting=false;
        PopulateHairColors();
    }
    static Color? ParseHair(string? value)=>value is {Length:7}&&value[0]=='#'&&int.TryParse(value.AsSpan(1),System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out int rgb)?Color.FromArgb(255,rgb>>16&255,rgb>>8&255,rgb&255):null;
    // The hair color the actor carries, and whether the user chose it. The game hands this one color to every
    // hair-tint shape on the actor: head parts and worn pieces such as wigs alike.
    (Color? Color,bool Chosen) ActorHair(GameRecord? race,bool female)
    {
        if(hairChoice==null)return (records.RaceHairColor(race,female),false);
        var color=ParseHair(hairChoice)??Records.ColorValue(records.All.GetValueOrDefault(hairChoice));
        return color==null?(records.RaceHairColor(race,female),false):(color,true);
    }
    void PopulateHairColors()
    {
        bool was=selecting;selecting=true;
        var race=races.SelectedItem as GameRecord;bool female=sex.SelectedIndex==1;
        HairChoice Named(GameRecord c,string note){var v=Records.ColorValue(c)!.Value;return new($"{c.EditorId} · {v.R}, {v.G}, {v.B}{note}",c.Key,v);}
        hairColor.Items.Clear();
        var standard=records.RaceHairColorRecord(race,female);
        hairColor.Items.Add(new HairChoice(standard==null?"Race default · none set":$"Race default · {standard.EditorId}",null,Records.ColorValue(standard)));
        var allowed=records.RaceHairColors(race,female);
        foreach(var c in allowed)if(Records.ColorValue(c)!=null)hairColor.Items.Add(Named(c," · offered by this race"));
        foreach(var c in records.Of("CLFM").Where(c=>!allowed.Contains(c)&&Records.ColorValue(c)!=null).OrderBy(c=>c.EditorId,StringComparer.OrdinalIgnoreCase))hairColor.Items.Add(Named(c,""));
        var custom=ParseHair(hairChoice);
        hairColor.Items.Add(new HairChoice(custom is {} k?$"Custom · {k.R}, {k.G}, {k.B} · change…":"Custom color…",CustomHairKey,custom));
        int index=custom!=null?hairColor.Items.Count-1:hairChoice==null?0:hairColor.Items.Cast<HairChoice>().ToList().FindIndex(c=>string.Equals(c.Key,hairChoice,StringComparison.OrdinalIgnoreCase));
        hairColor.SelectedIndex=Math.Max(0,index);
        selecting=was;
        var (active,_)=ActorHair(race,female);
        hairNote.Text=active is {} a
            ?$"Tint {a.R}, {a.G}, {a.B}. The game multiplies hair textures by this value over 128, so 128 keeps the texture as painted and 255 doubles it."
            :"No hair color record for this race. Hair meshes keep the tint stored in their NIF.";
    }
    void SetHairColor(string? choice){hairChoice=choice;PopulateHairColors();RebuildScene();}
    // Armor for the chosen slot, or every armor for All. A filter typed in the armor list stays across slot changes,
    // and the first armor it matches is picked.
    void PopulateArmor()
    {
        if(slot.SelectedItem is not SlotChoice choice)return;
        armor.Items.Clear();
        armor.Items.AddRange(records.Of("ARMO").Where(r=>choice.Bit is not int bit||(r.Slots&(1u<<bit))!=0).OrderBy(r=>r.ToString(),StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray());
        if(armor.Items.Count==0)return;
        var list=(SearchableComboBox)armor;int first=armor.Items.Cast<object>().ToList().FindIndex(list.Matches);
        armor.SelectedIndex=Math.Max(0,first);
    }
    void Equip(){if(armor.SelectedItem is GameRecord r){worn.RemoveAll(x=>(x.Slots&r.Slots)!=0);worn.Add(r);RebuildScene();}}
    // Library highlight: the playing clip's row is green, or orange when an enabled OAR or DAR folder with a higher
    // priority holds the same animation file and so can replace it in game.
    public static readonly Color PlayingRow=Color.FromArgb(46,125,50),OverriddenRow=Color.FromArgb(204,102,0);
    ILookup<string,ClipEntry>? overrideIndex;readonly Dictionary<ClipEntry,List<ClipEntry>> overrideCache=new(ReferenceEqualityComparer.Instance);
    List<ClipEntry> Overriders(ClipEntry entry)
    {
        overrideIndex??=ClipOverrides.Index(assets.Clips);
        return overrideCache.TryGetValue(entry,out var found)?found:overrideCache[entry]=ClipOverrides.For(entry,overrideIndex);
    }
    bool IsPlaying(ClipEntry entry)=>selected!=null&&entry.Path.Equals(selected.Path,StringComparison.OrdinalIgnoreCase)&&entry.ConfigPath.Equals(selected.ConfigPath,StringComparison.OrdinalIgnoreCase);
    Color? LibraryRowColor(TreeNode node)=>node.Tag is ClipEntry entry&&IsPlaying(entry)?(Overriders(entry).Count>0?OverriddenRow:PlayingRow):null;
    string ClipToolTip(ClipEntry entry)
    {
        string text=entry.Path+"\nSource: "+assets.Source(entry.Path);string overridden=ClipOverrides.Describe(entry,Overriders(entry),8);
        return overridden.Length>0?text+"\n\n"+overridden:text;
    }
    void BuildTree()
    {
        overrideIndex=null;overrideCache.Clear();((ResponsiveTreeView)tree).RowColor=LibraryRowColor;
        tree.BeginUpdate();tree.Nodes.Clear();var terms=search.Text.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        foreach(var framework in assets.Clips.Where(c=>terms.All(t=>(c.Path+" "+c.LibraryMod+" "+c.Group+" "+c.VariantGroup+" "+c.Config).Contains(t,StringComparison.OrdinalIgnoreCase))).GroupBy(c=>c.Framework)) {
            var root=tree.Nodes.Add($"{framework.Key} · {framework.Count():N0}");
            foreach(var mod in framework.GroupBy(c=>c.LibraryMod)) {
                var mn=root.Nodes.Add(mod.Key);
                mn.Tag=string.Join("\n\n",mod.Select(c=>c.Config).Distinct());
                foreach(var group in mod.GroupBy(c=>(c.Group,c.ConfigPath)).OrderByDescending(g=>g.First().Priority??int.MinValue)){
                    var gn=mn.Nodes.Add(group.Key.Group);gn.Tag=group.First().Config;gn.ToolTipText=group.Key.ConfigPath;
                    foreach(var variants in group.GroupBy(c=>c.VariantGroup)){
                        var parent=gn;
                        if(variants.Key.Length>0){parent=gn.Nodes.Add(variants.Key+" · variants");parent.Tag=variants.First().Config;}
                        foreach(var entry in variants.OrderBy(c=>c.Name,StringComparer.OrdinalIgnoreCase))
                            parent.Nodes.Add(new TreeNode(entry.DisplayName){Tag=entry,ToolTipText=ClipToolTip(entry),ForeColor=entry.Disabled?Color.Silver:tree.ForeColor});
                    }
                }
            }
            root.Expand();
        }
        void Tooltips(TreeNodeCollection nodes){foreach(TreeNode node in nodes){if(node.Tag is not ClipEntry)node.ToolTipText=node.Text+(node.ToolTipText.Length>0?"\n"+node.ToolTipText:"");Tooltips(node.Nodes);}}
        Tooltips(tree.Nodes);tree.ShowNodeToolTips=true;if(assets.Clips.Count<60)tree.ExpandAll();tree.EndUpdate();
    }
    // The Speed box is the viewer's own playback rate and starts at 1.00x. MCO_AttackSpeed annotations change the clip's
    // speed along its timeline (see SpeedMap); the note beside the box lists them. Sessions saved before this build put
    // the first annotation's value into the Speed box, so a restored speed equal to it is taken back to 1.00x.
    void ApplyClipSpeed(string name,AnimationClip decoded,bool reset)
    {
        bool bfco=Bfco.IsClip(name,decoded.Events);var changes=AnimationClip.EffectiveSpeedChanges(name,decoded);
        if(reset||changes.Count>0&&Math.Abs((double)speed.Value-changes[0].Value)<1e-4&&Math.Abs(changes[0].Value-1)>1e-4)speed.Value=1;
        speedNote.Text=SpeedNote(changes,bfco?"BFCO speed":"MCO_AttackSpeed")+(!bfco&&decoded.BfcoSpeedChanges.Count>0?" · BFCO_AttackSpeed ignored (not a BFCO clip)":"");
    }
    static string SpeedNote(List<(double Time,double Value)> changes,string variable="MCO_AttackSpeed")=>changes.Count==0?"":variable+" "+string.Join(", ",changes.Take(4).Select(c=>FormattableString.Invariant($"{c.Value:0.00}x at {c.Time:0.00} s")))+(changes.Count>4?$" +{changes.Count-4} more":"");
    async Task SelectClip(ClipEntry entry,bool resetSpeed=true)
    {
        soundPlayer.StopAll();lastSoundAbsolute=double.NaN;
        busy=true;clock.Pause();status.Text="Decoding "+entry.Name;
        try {var decoded=await Task.Run(()=>AnimationClip.Load(assets.Read(entry.Path)));selected=entry;
            // A clip that changes speed plays as a one-clip timeline in playback time; otherwise it is the clip itself.
            var timeline=AnimationClip.Timeline(entry.Name,decoded);clip=timeline;clock.Duration=Math.Max(.001,timeline.Duration);clock.Seek(0);ApplyClipSpeed(entry.Name,decoded,resetSpeed);
            manualTriggers.Clear();referenceActions.Clear();cues=effects.Build(timeline,conditionalDac.Checked);RebuildScene();clipTitle.Text=entry.Name;RefreshQueue();
            string overridden=ClipOverrides.Describe(entry,Overriders(entry));tree.Invalidate();
            detail.Text=$"{entry.Framework} / {entry.Mod} / {entry.Group}\n\n{entry.Path}\nSource: {assets.Source(entry.Path)}\n\n"+(overridden.Length>0?overridden+"\n\n":"")+$"{entry.ConfigPath}\n{entry.Config}";
            var map=timeline.Segments?[0].Speed;
            string When(double clipTime)=>map==null?$"{clipTime,8:F4}":$"{map.ToPlay(clipTime),8:F4} (clip {clipTime:F4})";
            events.Text=$"{decoded.Duration:F4} seconds · {decoded.Frames} source frames · {decoded.Tracks} tracks"+(map==null?"":$"\nPlays in {timeline.Duration:F4} s: {timeline.Segments![0].SpeedVariable} {map.Describe()}. Times below are playback times, clip times in brackets.")+"\n\n"+string.Join("\n",decoded.Events.Select(e=>$"{When(e.Time)} {e.Text}"))+(decoded.WeaponEvents.Count==0?"":"\n\nWEAPON ATTACHMENT\n"+$"{0d,8:F4} {(decoded.WeaponSheathedAt(0)==true?"on the holster node":"in the hand")} (state before the first annotation)\n"+string.Join("\n",decoded.WeaponEvents.Select(e=>$"{When(e.Time)} {(e.Sheathed?"weaponSheathe: moves to the holster node":"weaponDraw: moves to the hand")}")))+(decoded.BfcoSpeedChanges.Count==0||Bfco.IsClip(entry.Name,decoded.Events)?"":"\n\nBFCO_AttackSpeed annotations are listed above but not applied: this is not a BFCO clip, and MCO's behaviors read MCO_AttackSpeed only.")+Bfco.Describe(entry.Name,decoded)+PrecisionText()+SoundText()+"\n\nRESOLVED VISUAL EFFECTS\n"+string.Join("\n",cues.Select(c=>$"{c.Time:F4} {c.Model}\n  {c.Origin}"));
            Highlight.Index(decoded.Events.Select(e=>new AnnotationRow(map?.ToPlay(e.Time)??e.Time,e.Text,true,$"{When(e.Time)} {e.Text}")));
            status.Text=$"{entry.Name} · {decoded.Duration:F3} s"+(map==null?"":$" (plays in {timeline.Duration:F3} s)")+$" · {cues.Count} visual cue(s) · {decoded.Translation.Count} AMR keys";clock.Play();
        }catch(Exception ex){ShowError(ex);}finally{busy=false;}
    }
    void RefreshQueue()
    {
        int index=queueList.SelectedIndex;queueList.Items.Clear();
        var segments=clip?.Segments;bool live=segments!=null&&segments.Count==queue.Count;
        string Line(int i){
            if(!live)return $"{i+1} · {queue[i].Name}";
            var seg=segments![i];var travel=seg.Clip.Motion(seg.HandOver);
            string motion=seg.Clip.Translation.Count+seg.Clip.Rotation.Count==0?"no root motion":$"moves {travel.Position.Length():0} units"+(Math.Abs(travel.Yaw)>.001?$", turns {travel.Yaw*180/Math.PI:0}°":"");
            string handOver=i==queue.Count-1?"":seg.Window!=null?$" · next at {seg.Window} {seg.HandOver:0.00} s":QueueWindowEvent!=null?$" · no {QueueWindowName(queue[i],QueueWindowEvent)}, plays to end":"";
            return $"{i+1} · {queue[i].Name} · {seg.Start:0.000} to {seg.Start+seg.Length:0.000} s · {motion}"+(seg.Speed.Uniform?"":(seg.SpeedVariable.StartsWith("BFCO")?" · BFCO ":" · MCO ")+string.Join("→",seg.Speed.Points.Select(p=>p.Speed.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture)+"x")))+handOver;
        }
        for(int i=0;i<queue.Count;i++)queueList.Items.Add(Line(i));
        // An owner-drawn list does not measure its items, so the horizontal scroll range is set here.
        queueList.ItemHeight=queueList.Font.Height+8;
        queueList.HorizontalExtent=queueList.Items.Cast<string>().Select((t,i)=>TextRenderer.MeasureText(t,queueList.Font).Width+16+(QueueChip(i)!=null?QueueChipWidth+16:0)).DefaultIfEmpty(0).Max();
        if(queue.Count>0)queueList.SelectedIndex=Math.Clamp(index,0,queue.Count-1);
        if(queueTab!=null)queueTab.Text=queue.Count==0?"Queue":$"Queue · {queue.Count}";
        queueNote.Text=queue.Count==0?"Drag clips here from the library (Ctrl or Shift+click to pick several, or drag a folder), right-click them, or select and press Add.":(QueuePlaying?$"Playing {queue.Count} clip(s) in this order. Double-click one to jump to it.":$"{queue.Count} clip(s) queued, top plays first.")+" Drag to reorder.";
    }
    sealed record QueueDrag(int From);
    // Distinct times of a window annotation in a queued clip. The HKX is read again when its file changes on disk
    // (a re-export while the previewer is open) or the mounted folders change.
    List<Annotation> QueueClipEvents(ClipEntry entry)
    {
        if(queueClipEventsRevision!=assets.Revision){queueClipEvents.Clear();queueClipEventsRevision=assets.Revision;}
        DateTime written=default;try{string source=assets.Source(entry.Path);if(File.Exists(source))written=File.GetLastWriteTimeUtc(source);}catch(Exception){}
        if(!queueClipEvents.TryGetValue(entry.Path,out var cached)||cached.Written!=written){
            List<Annotation> events;try{events=AnimationClip.Load(assets.Read(entry.Path)).Events;}catch(Exception){events=[];}
            queueClipEvents[entry.Path]=cached=(written,events);
        }
        return cached.Events;
    }
    // The times of the hand-over annotation a queued clip uses for the chosen window, and that annotation's name: a
    // BFCO clip hands over at BFCO_NextWinStart or BFCO_NextPowerWinStart, any other at MCO's.
    List<double> QueueWindowTimes(ClipEntry entry,string window)=>Bfco.Window(entry.Name,QueueClipEvents(entry),window).Times;
    string QueueWindowName(ClipEntry entry,string window)=>Bfco.Window(entry.Name,QueueClipEvents(entry),window).Annotation??window;
    // A pick is kept under the annotation it indexes, so one made for MCO_WinOpen is not applied to BFCO_NextWinStart.
    int WindowPick(ClipEntry slot,string window)=>queueWindowPicks.TryGetValue(slot,out var picks)&&picks.TryGetValue(QueueWindowName(slot,window),out int pick)?pick:0;
    // The little drop-down on a queued clip that has the selected window annotation at more than one time.
    Rectangle? QueueChip(int index)
    {
        if(QueueWindowEvent is not {} window||index<0||index>=queue.Count||QueueWindowTimes(queue[index],window).Count<2)return null;
        var row=queueList.GetItemRectangle(index);
        // A list that has not been laid out yet (its tab hidden) reports empty rows.
        if(row.Height<=0)row=new Rectangle(0,index*queueList.ItemHeight,queueList.ClientSize.Width,queueList.ItemHeight);
        return new Rectangle(QueueChipLeft(index,row.Left),row.Top+3,QueueChipWidth,row.Height-6);
    }
    // The drop-down sits just after the clip's number and file name; the rest of the row's text moves right past it.
    const TextFormatFlags QueueTextFlags=TextFormatFlags.VerticalCenter|TextFormatFlags.Left|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding;
    string QueueRowName(int index)=>$"{index+1} · {queue[index].Name}";
    int QueueChipLeft(int index,int rowLeft)=>rowLeft+6+TextRenderer.MeasureText(QueueRowName(index),queueList.Font,Size.Empty,QueueTextFlags).Width+8;
    void ShowWindowMenu(int index,Rectangle chip)
    {
        if(QueueWindowEvent is not {} window)return;
        var slot=queue[index];var times=QueueWindowTimes(slot,window);int pick=Math.Clamp(WindowPick(slot,window),0,times.Count-1);
        var menu=new ContextMenuStrip{ShowCheckMargin=true};
        for(int k=0;k<times.Count;k++){
            int choice=k;
            var item=new ToolStripMenuItem($"{QueueWindowName(slot,window)} at {times[k]:0.000} s"+(k==0?" (first)":""),null,(_,_)=>ChooseWindow(slot,window,choice)){Checked=k==pick};
            menu.Items.Add(item);
        }
        menu.Closed+=(_,_)=>BeginInvoke(menu.Dispose);
        menu.Show(queueList,new Point(chip.Left,chip.Bottom));
    }
    void ChooseWindow(ClipEntry slot,string window,int choice)
    {
        if(!queueWindowPicks.TryGetValue(slot,out var picks))queueWindowPicks[slot]=picks=new(StringComparer.OrdinalIgnoreCase);
        picks[QueueWindowName(slot,window)]=choice;RefreshQueue();QueueChanged();
    }
    // Coming back to the window (after a re-export) redraws the queue, so changed window annotations show at once.
    protected override void OnActivated(EventArgs e){base.OnActivated(e);queueList.Invalidate();}
    void SetQueueDragState(int dragging,int dropLine)
    {
        if(dragging==queueDragging&&dropLine==queueDropLine)return;
        queueDragging=dragging;queueDropLine=dropLine;queueList.Invalidate();
    }
    // One queue row: the normal text and selection, a white outline on the clip being dragged, and a white insertion
    // line on the boundary the drop will insert at (drawn half in the row above and half in the row below).
    void DrawQueueItem(Graphics g,Rectangle bounds,int index,bool selected)
    {
        var back=selected?SystemColors.Highlight:queueList.BackColor;
        using(var fill=new SolidBrush(back))g.FillRectangle(fill,bounds);
        var text=new Rectangle(bounds.Left+6,bounds.Top,Math.Max(bounds.Width,queueList.HorizontalExtent)-6,bounds.Height);
        var color=selected?SystemColors.HighlightText:queueList.ForeColor;string rowText=queueList.Items[index] as string??"";
        if(QueueChip(index) is not null&&index<queue.Count&&rowText.StartsWith(QueueRowName(index),StringComparison.Ordinal)){
            // Name, then the drop-down (drawn below), then the rest of the line after it.
            string name=QueueRowName(index);int after=QueueChipLeft(index,bounds.Left)+QueueChipWidth+8;
            TextRenderer.DrawText(g,name,queueList.Font,text,color,QueueTextFlags);
            TextRenderer.DrawText(g,rowText[name.Length..],queueList.Font,new Rectangle(after,bounds.Top,Math.Max(10,text.Right-after),bounds.Height),color,QueueTextFlags);
        }else TextRenderer.DrawText(g,rowText,queueList.Font,text,color,QueueTextFlags);
        int right=Math.Max(bounds.Right,queueList.ClientSize.Width);
        if(QueueChip(index) is {} chip&&QueueWindowEvent is {} window){
            var times=QueueWindowTimes(queue[index],window);int pick=Math.Clamp(WindowPick(queue[index],window),0,times.Count-1);
            var box=new Rectangle(QueueChipLeft(index,bounds.Left),bounds.Top+3,chip.Width,bounds.Height-6);
            using(var fill=new SolidBrush(Color.FromArgb(58,62,74)))g.FillRectangle(fill,box);
            using(var edge=new Pen(Color.FromArgb(178,132,255)))g.DrawRectangle(edge,box);
            TextRenderer.DrawText(g,$"▾ {times[pick]:0.000} s  ({pick+1}/{times.Count})",queueList.Font,box,Color.White,TextFormatFlags.VerticalCenter|TextFormatFlags.HorizontalCenter|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine);
        }
        if(index==queueDragging)using(var outline=new Pen(Color.White,1)){g.DrawRectangle(outline,bounds.Left+1,bounds.Top+1,right-bounds.Left-3,bounds.Height-3);}
        if(queueDropLine<0)return;
        int count=queueList.Items.Count;
        bool above=index==queueDropLine,below=index==queueDropLine-1;
        if(!above&&!below)return;
        using var line=new SolidBrush(Color.White);
        // The line is 2 px either side of the boundary, with a short tick at the left end so it reads as a marker.
        if(above){g.FillRectangle(line,bounds.Left,bounds.Top,right-bounds.Left,2);g.FillRectangle(line,bounds.Left,bounds.Top,3,6);}
        if(below){int y=bounds.Bottom-(index==count-1?3:2);g.FillRectangle(line,bounds.Left,y,right-bounds.Left,index==count-1?3:2);g.FillRectangle(line,bounds.Left,bounds.Bottom-6,3,6);}
    }
    // Index a drop at this screen point inserts before; below the last item appends.
    int QueueDropIndex(int screenX,int screenY)
    {
        var point=queueList.PointToClient(new Point(screenX,screenY));int index=queueList.IndexFromPoint(point);
        if(index<0)return queue.Count;
        var bounds=queueList.GetItemRectangle(index);return point.Y>bounds.Top+bounds.Height/2?index+1:index;
    }
    // Moves a queued clip so it plays before whatever is at `before` now (queue.Count moves it to the end).
    void MoveQueueItem(int from,int before)
    {
        if(from<0||from>=queue.Count)return;
        before=Math.Clamp(before,0,queue.Count);var item=queue[from];queue.RemoveAt(from);if(before>from)before--;
        queue.Insert(before,item);RefreshQueue();queueList.SelectedIndex=before;QueueChanged();
    }
    void InsertIntoQueue(IReadOnlyList<ClipEntry> entries,int before)
    {
        if(entries.Count==0)return;
        before=Math.Clamp(before,0,queue.Count);queue.InsertRange(before,entries.Select(e=>e with{}));RefreshQueue();queueList.SelectedIndex=before+entries.Count-1;QueueChanged();
    }
    // The clips a library action covers, in library order. A node that is part of a multiple selection stands for
    // every marked node; a folder or group node stands for every clip beneath it.
    ClipEntry[] LibraryClips(TreeNode node)
    {
        var library=(ResponsiveTreeView)tree;
        var nodes=library.Marked.Count>1&&library.IsMarked(node)?library.Marked:[node];
        var order=new List<TreeNode>();void Walk(TreeNodeCollection level){foreach(TreeNode n in level){order.Add(n);Walk(n.Nodes);}}Walk(tree.Nodes);
        var result=new List<ClipEntry>();var seen=new HashSet<TreeNode>();
        void Collect(TreeNode n){if(!seen.Add(n))return;if(n.Tag is ClipEntry entry)result.Add(entry);foreach(TreeNode child in n.Nodes)Collect(child);}
        foreach(var n in nodes.OrderBy(order.IndexOf))Collect(n);
        return result.ToArray();
    }
    // The queue in its own window, beside the viewport; closing it returns the list to the Queue tab.
    void ToggleQueueWindow()
    {
        if(queueWindow!=null){queueWindow.Close();return;}
        queueWindow=new Form{Text="Animation queue",Size=new Size(760,520),MinimumSize=new Size(460,300),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false,FormBorderStyle=FormBorderStyle.SizableToolWindow,BackColor=BackColor,ForeColor=ForeColor,Padding=new Padding(8)};
        var screen=Screen.FromControl(this).WorkingArea;
        queueWindow.Location=new Point(Math.Clamp(Right-queueWindow.Width-40,screen.Left,screen.Right-queueWindow.Width),Math.Clamp(Top+120,screen.Top,screen.Bottom-queueWindow.Height));
        queueTab!.Controls.Remove(queueBody);queueWindow.Controls.Add(queueBody);
        queueTab.Controls.Add(queueBackLabel??=new Label{Text="The queue is open in its own window. Close that window to bring it back here.",AutoSize=true,Padding=new Padding(6)});
        queueWindowButton!.Text="Dock back in tab";
        queueWindow.FormClosed+=(_,_)=>{queueWindow=null;queueWindowButton.Text="Open in window";queueTab.Controls.Remove(queueBackLabel);queueTab.Controls.Add(queueBody);};
        queueWindow.Show(this);
    }
    Label? queueBackLabel;Button? queueWindowButton;
    void AddToQueue(ClipEntry entry)=>AddToQueue([entry]);
    void AddToQueue(IReadOnlyList<ClipEntry> entries)
    {
        if(entries.Count==0)return;
        queue.AddRange(entries.Select(e=>e with{}));RefreshQueue();queueList.SelectedIndex=queue.Count-1;
        if(queueWindow==null&&inspectorTabs!=null&&queueTab!=null)inspectorTabs.SelectedTab=queueTab;
        QueueChanged();
    }
    void EditQueue(Action<int> edit,int move)
    {
        int index=queueList.SelectedIndex;if(index<0||index>=queue.Count)return;
        edit(index);RefreshQueue();if(queue.Count>0)queueList.SelectedIndex=Math.Clamp(index+move,0,queue.Count-1);
        QueueChanged();
    }
    // A change to a queue that is playing rebuilds it and keeps the playhead where it was.
    // Set when the queue changes during a rebuild; the rebuild then runs once more with the latest list.
    bool queuePending;
    async void QueueChanged()
    {
        queueList.Invalidate();
        if(selecting)return;
        if(busy){queuePending=true;return;}
        if(QueuePlaying&&queue.Count>0){double at=clock.Time;bool playing=clock.Playing;await PlayQueue();clock.Seek(Math.Min(at,clock.Duration));scene.SetTime(clock.Time);if(!playing)clock.Pause();}
        SaveSession();
    }
    // Decodes every queued clip and plays them as one timeline. Root motion carries from clip to clip, each clip's
    // MCO_AttackSpeed applies to that clip alone, and annotations and effects keep their place in their clip.
    async Task PlayQueue()
    {
        if(busy||queue.Count==0)return;
        soundPlayer.StopAll();lastSoundAbsolute=double.NaN;
        busy=true;clock.Pause();status.Text=$"Decoding {queue.Count} queued clip(s)";
        try{
            var entries=queue.ToArray();
            var decoded=await Task.Run(()=>entries.Select(e=>(e.Name,Clip:AnimationClip.Load(assets.Read(e.Path)))).ToList());
            var combined=AnimationClip.Queue(decoded,(double)queueBlend.Value,QueueWindowEvent,windowPicks:entries.Select(e=>QueueWindowEvent is {} w?WindowPick(e,w):0).ToList());
            selected=null;clip=combined;clock.Duration=Math.Max(.001,combined.Duration);clock.Seek(0);
            speed.Value=1;int scaled=combined.Segments!.Count(s=>!s.Speed.Uniform);
            speedNote.Text=scaled==0?"":$"Attack speed annotations change speed inside {scaled} queued clip(s)";
            manualTriggers.Clear();referenceActions.Clear();cues=effects.Build(combined,conditionalDac.Checked);RebuildScene();
            clipTitle.Text=$"Queue · {entries.Length} clips";
            var end=combined.Motion(combined.Duration);
            detail.Text=$"Animation queue · {entries.Length} clips · {combined.Duration:F3} s · blend {combined.BlendTime:0.00} s\n\n"+string.Join("\n\n",combined.Segments.Select((s,i)=>{
                var travel=s.Clip.Motion(s.HandOver);
                return $"{i+1}. {s.Name}\n   {entries[i].Path}\n   starts {s.Start:F4} s · plays {s.Length:F4} s"+(s.Window!=null?$" · hands over at {s.Window} ({s.HandOver:F3} s of {s.Clip.Duration:F3} s); later annotations are skipped":"")+(s.Speed.Uniform?"":$" · {s.SpeedVariable} {s.Speed.Describe()}"+(s.Speed.Points[0].Speed!=1&&!s.Changes.Any(c=>c.Time<1e-9)?" (the first value carries over from the clip before)":""))+$"\n   root motion in this clip: {travel.Position.Length():F1} units, {travel.Yaw*180/Math.PI:F1}° turn · starts at ({s.BasePosition.X:F1}, {s.BasePosition.Y:F1}, {s.BasePosition.Z:F1}) facing {s.BaseYaw*180/Math.PI:F1}°";}))
                +$"\n\nEnd of queue: ({end.Position.X:F1}, {end.Position.Y:F1}, {end.Position.Z:F1}) facing {end.Yaw*180/Math.PI:F1}°";
            events.Text=$"{combined.Duration:F4} seconds · {entries.Length} queued clips · times are queue times\n\n"+string.Join("\n",combined.Segments.Select((s,i)=>$"{s.Start,8:F4} ── clip {i+1}: {s.Name}\n"+string.Join("\n",s.Clip.Events.Select(e=>$"{s.Global(e.Time),8:F4} {e.Text}"))))
                +(combined.WeaponEvents.Count==0?"":"\n\nWEAPON ATTACHMENT\n"+string.Join("\n",combined.WeaponEvents.Select(e=>$"{e.Time,8:F4} {(e.Sheathed?"weaponSheathe: moves to the holster node":"weaponDraw: moves to the hand")}")))
                +PrecisionText()+SoundText()
                +"\n\nRESOLVED VISUAL EFFECTS\n"+string.Join("\n",cues.Select(c=>$"{c.Time:F4} {c.Model}\n  {c.Origin}"));
            // A queued clip's annotations after its hand-over never fire, so they are listed but never highlighted.
            Highlight.Index(combined.Segments.SelectMany((s,k)=>s.Clip.Events.Select(e=>new AnnotationRow(s.Global(e.Time),e.Text,s.Window==null||e.Time<=s.HandOver+1e-9,$"{s.Global(e.Time),8:F4} {e.Text}",k==combined.Segments.Count-1?double.PositiveInfinity:s.Start+s.Length))));
            status.Text=$"Queue · {entries.Length} clips · {combined.Duration:F3} s · {cues.Count} visual cue(s) · {decoded.Sum(d=>d.Clip.Translation.Count)} AMR keys";
            RefreshQueue();clock.Play();
        }catch(Exception ex){ShowError(ex);}
        finally{busy=false;if(queuePending){queuePending=false;BeginInvoke(QueueChanged);}}
    }
    void RebuildScene()
    {
        if(selecting)return;bool playing=clock.Playing;clock.Pause();scene.Clear();
        var race=races.SelectedItem as GameRecord;bool female=sex.SelectedIndex==1;scene.LoadSkeleton(customSkeleton??RaceSkeleton(race,female));
        var models=new Dictionary<string,(string Path,uint Slots,string[]? Textures)>(StringComparer.OrdinalIgnoreCase);var skin=race==null?null:records.Resolve(race,race.First("WNAM")?.UInt()??0);
        string ModelPath(GameRecord addon)=>ProjectileVisuals.ModelPath(addon.First(female?"MOD3":"MOD2")?.Text);
        uint coveredSlots=worn.SelectMany(item=>records.ArmorAddons(item,race)).Where(addon=>ModelPath(addon).Length>0&&assets.Exists(ModelPath(addon))).Aggregate(0u,(mask,addon)=>mask|addon.Slots);
        void Collect(GameRecord armor,uint hidden=0){
            foreach(var addon in records.ArmorAddons(armor,race)){
                if((addon.Slots&hidden)!=0)continue;
                var path=addon.First(female?"MOD3":"MOD2")?.Text;if(string.IsNullOrWhiteSpace(path))continue;
                path=ProjectileVisuals.ModelPath(path);models[addon.Key]=(path,addon.Slots,records.ArmorSkinTextures(addon,female));
            }
        }
        if(skin!=null)Collect(skin,coveredSlots);foreach(var item in worn)Collect(item);
        // One hair color per actor. The game writes it into every hair-tint material under the actor's 3D
        // (Actor::UpdateHairColor), worn armor included, which is how a wig takes the character's hair color.
        var (hair,hairChosen)=ActorHair(race,female);
        foreach(var m in models.Values)scene.AddModel(m.Path,bipedSlots:m.Slots,skinTextureOverride:m.Textures,skinColor:records.RaceSkinColor(race,female),hairColor:hair);
        // Equipment on slot 31 (Hair) replaces the hair head part and its extra parts, as a helmet or wig does in game.
        var hiddenHair=(coveredSlots&2)!=0?records.HeadPartRecords(heads.CheckedItems.Cast<GameRecord>().Where(h=>h.First("PNAM")?.UInt()==3)).Select(h=>h.Key).ToHashSet(StringComparer.OrdinalIgnoreCase):[];
        if(hiddenHair.Count>0)scene.Diagnostics.Add("Hair head parts are not drawn: worn equipment covers slot 31 (Hair), as in game.");
        foreach(var head in records.HeadPartRecords(heads.CheckedItems.Cast<GameRecord>())){
            if(hiddenHair.Contains(head.Key))continue;
            var model=head.First("MODL")?.Text;if(string.IsNullOrWhiteSpace(model))continue;
            scene.AddModel(model,textureOverride:records.TexturePaths(records.Resolve(head,head.First("TNAM")?.UInt()??0)??(head.First("PNAM")?.UInt()==1&&race!=null?records.Resolve(race,race.First(female?"DFTF":"DFTM")?.UInt()??0):null)),hairColor:hairChosen?hair:records.HeadHairColor(head,race,female),skinColor:records.RaceSkinColor(race,female),bipedSlots:head.First("PNAM")?.UInt()==3?(1u<<1)|(1u<<11):1u);
        }
        scene.HolsterNode=weapons.SelectedItem is GameRecord holstered?Records.WeaponHolsterNode(holstered):null;scene.Sheathed=sheathed.Checked;
        if(weapons.SelectedItem is GameRecord w && w.First("MODL") is {} mf)scene.AddModel(mf.Text,true,weaponIndex:Records.WeaponBipedIndex(w));
        foreach(var m in customModels)scene.AddModel(m);scene.AddEffects(cues);
        foreach(var path in effectOverlays)scene.AddModel(path,false,new EffectCue(0,path,1,0,Math.Max(10,clip?.Duration??0),1,System.Numerics.Vector3.Zero,"Saved effect overlay",WorldSpace:true));
        scene.Weight=weight.Value;scene.Commit();UpdateHolsterLabel();UpdateWeightLabel();UpdatePhysicsLabel();RefreshEffectSequences();if(clip!=null)scene.BindClip(clip);scene.PrecisionPowerAttack=PowerAttackAt;UpdatePrecisionLabel();UpdateSoundWarnings();if(bonesWindow!=null)FillBoneList();UpdateHolsterLabel();equipped.Items.Clear();equipped.Items.AddRange(worn.Cast<object>().ToArray());((WrappedListBox)equipped).Reflow();scene.SetTime(clock.Time);scene.FrameAfterRebuild();UpdateDiagnostics();if(playing)clock.Play();if(!busy&&!selecting)SaveSession();
    }
    static string RaceSkeleton(GameRecord? race,bool female)
    {
        if(race!=null) {int gender=-1;bool skeleton=false;foreach(var f in race.Fields) {if(f.Tag=="NAM0"){skeleton=true;gender=-1;}if(f.Tag=="NAM1")skeleton=false;if(f.Tag=="MNAM")gender=0;if(f.Tag=="FNAM")gender=1;if(skeleton&&gender==(female?1:0)&&f.Tag=="MODL"&&f.Text.EndsWith(".nif",StringComparison.OrdinalIgnoreCase))return @"meshes\"+f.Text;}}
        return female?@"meshes\actors\character\character assets female\skeleton_female.nif":@"meshes\actors\character\character assets\skeleton.nif";
    }
    void ChooseAnimation(){using var d=new OpenFileDialog{Filter="Havok animations|*.hkx",Multiselect=true};if(d.ShowDialog()==DialogResult.OK)DropFiles(d.FileNames);}
    void DropFiles(string[] files){ClipEntry? last=null;foreach(var file in files)try{if(Directory.Exists(file))foreach(var f in Directory.EnumerateFiles(file,"*",SearchOption.AllDirectories).Where(f=>f.EndsWith(".hkx",StringComparison.OrdinalIgnoreCase)))last=assets.Drop(f);else last=assets.Drop(file);}catch(Exception ex){ShowError(ex);}BuildTree();try{SaveSession();}catch(Exception ex){ShowError(ex);}if(last!=null)_=SelectClip(last);}
    void ChooseMesh(){using var d=new OpenFileDialog{Filter="Skyrim meshes|*.nif",Multiselect=true};if(d.ShowDialog()==DialogResult.OK){customModels.AddRange(d.FileNames);RebuildScene();}}
    void ChooseEffectOverlay(){using var d=new OpenFileDialog{Filter="Skyrim effect meshes|*.nif",Multiselect=true};if(d.ShowDialog()==DialogResult.OK){foreach(var path in d.FileNames)if(!effectOverlays.Contains(path,StringComparer.OrdinalIgnoreCase))effectOverlays.Add(path);RebuildScene();}}
    static string? EffectDataRoot(string path)
    {
        for(var directory=Directory.GetParent(Path.GetFullPath(path));directory!=null;directory=directory.Parent)
            if(Directory.Exists(Path.Combine(directory.FullName,"meshes"))&&Directory.Exists(Path.Combine(directory.FullName,"textures")))return directory.FullName;
        return null;
    }
    async Task EnsureEffectDataRoot(string path)
    {
        var root=EffectDataRoot(path);
        if(root!=null&&!assets.Roots.Any(x=>Path.GetFullPath(x).Equals(root,StringComparison.OrdinalIgnoreCase)))await Mount(root,assets.Roots.Count>0);
    }
    async Task ChooseParticle()
    {
        using var d=new OpenFileDialog{Filter="Effect NIF|*.nif"};
        if(d.ShowDialog()!=DialogResult.OK)return;
        await EnsureEffectDataRoot(d.FileName);
        PreviewParticle(d.FileName);
    }
    void ChooseImpactEffect()
    {
        var entries=ImpactEffects.Catalog(records,assets);
        if(entries.Length==0){status.Text="No impact or explosion records loaded.";return;}
        bool playing=clock.Playing;clock.Pause();ImpactEffectEntry? chosen=null;
        using var dialog=new Form{Text="Impacts and explosions",Size=new(900,680),MinimumSize=new(650,480),StartPosition=FormStartPosition.CenterParent};
        var search=new TextBox{Dock=DockStyle.Top,PlaceholderText="Search effect, plugin, material or projectile..."};
        var list=new ListBox{Dock=DockStyle.Top,Height=220};
        var info=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both};
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=48};
        var preview=Button("Preview mesh",()=>{chosen=list.SelectedItem as ImpactEffectEntry;dialog.Close();});
        void Select(){var entry=list.SelectedItem as ImpactEffectEntry;info.Text=entry?.Details??"No matches.";preview.Enabled=entry!=null&&entry.Model.Length>0&&assets.Exists(entry.Model);}
        void Filter(){list.Items.Clear();list.Items.AddRange(entries.Where(e=>e.Details.Contains(search.Text,StringComparison.OrdinalIgnoreCase)).Cast<object>().ToArray());if(list.Items.Count>0)list.SelectedIndex=0;Select();}
        list.SelectedIndexChanged+=(_,_)=>Select();search.TextChanged+=(_,_)=>Filter();
        buttons.Controls.Add(preview);buttons.Controls.Add(Button("Close",()=>dialog.Close()));
        dialog.Controls.Add(info);dialog.Controls.Add(list);dialog.Controls.Add(search);dialog.Controls.Add(buttons);Filter();Theme(dialog);dialog.ShowDialog(this);
        if(chosen!=null){PreviewParticle(chosen.Model,chosen.Duration);detail.Text=chosen.Details;status.Text="Impact/explosion mesh: "+chosen.Record.EditorId;}
        else if(playing)clock.Play();
    }
    void ChooseReferenceEffect()
    {
        var entries=ReferenceEffects.Catalog(records,assets);
        if(entries.Length==0){status.Text="No reference-effect records are loaded. Load a plugin containing visual effects.";return;}
        bool wasPlaying=clock.Playing;clock.Pause();ReferenceEffectEntry? chosen=null;int action=0;
        using var dialog=new Form{Text="Plugin visual effects",Size=new(900,720),MinimumSize=new(820,650),StartPosition=FormStartPosition.CenterParent};
        var search=new TextBox{Dock=DockStyle.Top,PlaceholderText="Search effect, plugin, form ID or mesh…"};
        var list=new ListBox{Dock=DockStyle.Fill};
        var info=new TextBox{Dock=DockStyle.Bottom,Height=220,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
        var actions=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=48};
        var targetPanel=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=92,Padding=new(6),AutoScroll=true};
        var target=new NumericUpDown{Minimum=1,Maximum=8,Value=referenceTarget,Width=48};
        NumericUpDown Coordinate()=>new(){Minimum=-100000,Maximum=100000,DecimalPlaces=1,Increment=10,Width=86};
        var px=Coordinate();var py=Coordinate();var pz=Coordinate();
        var heading=new NumericUpDown{Minimum=-360,Maximum=360,DecimalPlaces=1,Increment=15,Width=70};
        var follow=new CheckBox{Text="Follow character root motion",AutoSize=true};
        void LoadTarget(){var value=referenceTargets.GetValueOrDefault((int)target.Value);follow.Checked=value.Follow;px.Value=(decimal)value.Position.X;py.Value=(decimal)value.Position.Y;pz.Value=(decimal)value.Position.Z;heading.Value=Math.Clamp((decimal)(value.Heading*180/MathF.PI),heading.Minimum,heading.Maximum);}
        void SaveTarget(){referenceTargets[referenceTarget]=(new((float)px.Value,(float)py.Value,(float)pz.Value),(float)heading.Value*MathF.PI/180,follow.Checked);}
        target.ValueChanged+=(_,_)=>{SaveTarget();referenceTarget=(int)target.Value;LoadTarget();};LoadTarget();
        foreach(var pair in new (string Name,Control Control)[]{("Target",target),("X",px),("Y",py),("Z (up)",pz),("Yaw °",heading)}){
            targetPanel.Controls.Add(new Label{Text=pair.Name,AutoSize=true,Margin=new(6,6,3,0)});targetPanel.Controls.Add(pair.Control);
        }
        targetPanel.Controls.Add(follow);
        targetPanel.Controls.Add(new Label{Text="Game units; Z is up. Following uses character-relative offsets. Edits affect new plays.",AutoSize=true,Margin=new(6,6,3,0)});
        var preview=Button("Preview art",()=>{chosen=list.SelectedItem as ReferenceEffectEntry;dialog.Close();});
        var play=Button("Play here",()=>{chosen=list.SelectedItem as ReferenceEffectEntry;action=1;dialog.Close();});
        var stop=Button("Stop here",()=>{chosen=list.SelectedItem as ReferenceEffectEntry;action=2;dialog.Close();});
        void Select(){var entry=list.SelectedItem as ReferenceEffectEntry;info.Text=entry?.Details??"No matching effects.";preview.Enabled=entry!=null&&entry.Model.Length>0&&assets.Exists(entry.Model);play.Enabled=preview.Enabled;stop.Enabled=entry!=null;}
        void Filter(){list.BeginUpdate();list.Items.Clear();foreach(var entry in entries.Where(e=>(e.Name+" "+e.Record.Key+" "+e.Model).Contains(search.Text.Trim(),StringComparison.OrdinalIgnoreCase)))list.Items.Add(entry);if(list.Items.Count>0)list.SelectedIndex=0;list.EndUpdate();Select();}
        search.TextChanged+=(_,_)=>Filter();list.SelectedIndexChanged+=(_,_)=>Select();
        list.DoubleClick+=(_,_)=>{if(preview.Enabled)preview.PerformClick();};
        actions.Controls.Add(preview);actions.Controls.Add(play);actions.Controls.Add(stop);actions.Controls.Add(Button("Close",()=>dialog.Close()));
        dialog.Controls.Add(list);dialog.Controls.Add(info);dialog.Controls.Add(targetPanel);dialog.Controls.Add(actions);dialog.Controls.Add(search);Theme(dialog);Filter();dialog.ShowDialog(this);SaveTarget();
        if(chosen!=null){if(action==0)PreviewReferenceEffect(chosen);else {var placement=referenceTargets[referenceTarget];QueueReferenceEffect(chosen,action==2,referenceTarget,placement.Position,placement.Heading,placement.Follow);}}else if(wasPlaying)clock.Play();
    }
    void QueueReferenceEffect(ReferenceEffectEntry entry,bool stop,int target=1,System.Numerics.Vector3 position=default,float heading=0,bool followCharacter=false)
    {
        referenceActions.Add(new(entry.Record.Key,clock.Time,stop,target,position,heading,followCharacter));
        cues=effects.Build(clip??new AnimationClip(),conditionalDac.Checked,manualTriggers,referenceActions);
        if(clip==null){previewingEffect=true;clock.Duration=Math.Max(clock.Duration,clock.Time+10);}
        RebuildScene();
        detail.Text=entry.Details+"\n\nMANUAL REFERENCE-EFFECT TIMELINE\n"+string.Join("\n",referenceActions.OrderBy(a=>a.Time).Select(a=>$"{a.Time:F3}s · {(a.Stop?"Stop":"Play")} · Target {a.Target} · {a.Key} · {a.Position}"));
        status.Text=$"{(stop?"Stop":"Play")} queued at {clock.Time:F3}s · {entry.Record.EditorId}";
        clock.Play();
    }
    void PreviewReferenceEffect(ReferenceEffectEntry entry)
    {
        PreviewParticle(entry.Model);detail.Text=entry.Details;
        if(entry.Issues.Length>0)diagnostics.AppendText(Environment.NewLine+string.Join(Environment.NewLine,entry.Issues));
        status.Text="Reference-effect art: "+entry.Record.EditorId;
    }
    void ChooseEmitterSources()
    {
        var emitters=scene.MeshEmitters;
        if(emitters.Length==0){status.Text="The current scene has no mesh particle emitters.";return;}
        bool playing=clock.Playing;clock.Pause();
        using var dialog=new Form{Text="Particle emitter sources",Size=new(640,540),MinimumSize=new(480,400),StartPosition=FormStartPosition.CenterParent};
        var systems=new SearchableComboBox{Name="EmitterSourceList",Dock=DockStyle.Top};
        var meshes=new CheckedListBox{Name="EmitterSourceMeshes",Dock=DockStyle.Fill,CheckOnClick=true};
        var explanation=new Label{Dock=DockStyle.Top,AutoSize=true,MaximumSize=new(dialog.ClientSize.Width,0),Padding=new(8),Text="Choose source meshes for this emitter. Changes are temporary: they do not modify the NIF or establish Skyrim's runtime bindings."};
        dialog.SizeChanged+=(_,_)=>explanation.MaximumSize=new(dialog.ClientSize.Width,0);
        var feedback=new TextBox{Dock=DockStyle.Bottom,Height=100,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=48};
        NiflySharp.Blocks.NiAVObject[] options=[];
        void RefreshMeshes(){
            var system=emitters[systems.SelectedIndex];options=system.AvailableEmitterMeshes;meshes.Items.Clear();
            foreach(var node in options)meshes.Items.Add($"{system.Nif.Blocks.IndexOf(node)} · {node.Name}",system.BoundEmitterMeshes.Contains(node));
            feedback.Text=system.EmitterSourceDetails;
        }
        void Apply(bool restore){
            var system=emitters[systems.SelectedIndex];
            system.BindEmitterMeshes(restore?null:meshes.CheckedIndices.Cast<int>().Select(i=>options[i]));
            scene.RefreshEmitterDiagnostics();
            scene.ResetParticles();scene.SetTime(clock.Time);RefreshMeshes();UpdateDiagnostics();
            status.Text=restore?"Restored authored emitter sources.":"Emitter source override applied for preview only.";
        }
        for(int i=0;i<emitters.Length;i++)systems.Items.Add($"{i+1} · {emitters[i].Name}");
        systems.SelectedIndexChanged+=(_,_)=>RefreshMeshes();systems.SelectedIndex=0;
        buttons.Controls.AddRange([Button("Apply preview binding",()=>Apply(false)),Button("Restore authored",()=>Apply(true)),Button("Close",()=>dialog.Close())]);
        dialog.Controls.Add(meshes);dialog.Controls.Add(feedback);dialog.Controls.Add(buttons);dialog.Controls.Add(systems);dialog.Controls.Add(explanation);
        if(emitterSourceSmoke){dialog.Opacity=0;dialog.ShowInTaskbar=false;}
        Theme(dialog);dialog.ShowDialog(this);if(playing)clock.Play();
    }
    void RefreshEffectSequences()
    {
        bool previous=selecting;selecting=true;
        effectSequence.Items.Clear();effectSequence.Items.Add("Automatic");effectSequence.Items.AddRange(scene.SequenceNames);
        effectSequence.SelectedIndex=0;effectSequence.Enabled=scene.SequenceNames.Length>0;selecting=previous;
    }
    void PreviewParticle(string path,double emissionDuration=10)
    {
        clock.Pause();clip=null;manualTriggers.Clear();referenceActions.Clear();cues.Clear();previewingEffect=true;scene.Clip=null;scene.Clear();scene.AddModel(path,cue:new EffectCue(0,path,0,0,emissionDuration,1,System.Numerics.Vector3.Zero,"Direct NIF preview"));scene.Commit();
        RefreshEffectSequences();
        clock.Duration=scene.EffectDuration>.1?scene.EffectDuration:Math.Max(1,emissionDuration);clock.Seek(0);clipTitle.Text=Path.GetFileName(path);speedNote.Text="";
        viewport.camera=new OrbitCamera();viewport.camera.MaxDistance=100000;viewport.camera.Rotate(45,65);scene.SetTime(.2);scene.Frame();
        scene.SetTime(0);UpdateDiagnostics();clock.Play();
    }
    void ChooseSkeleton(){using var d=new OpenFileDialog{Filter="Skeleton NIF|*.nif"};if(d.ShowDialog()==DialogResult.OK){customSkeleton=d.FileName;var hkx=Path.ChangeExtension(d.FileName,"hkx");if(File.Exists(hkx))scene.SkeletonHkx=hkx;RebuildScene();}}
    void TriggerEffect()
    {
        if(triggers.SelectedItem is not Trigger trigger)return;
        manualTriggers.Add(new(trigger,clock.Time));
        cues=effects.Build(clip??new AnimationClip(),conditionalDac.Checked,manualTriggers,referenceActions);
        if(clip==null){
            previewingEffect=true;
            double end=cues.Select(c=>double.IsFinite(c.RemovedAt)?c.RemovedAt+3:
                double.IsFinite(c.Lifetime)?c.Time+c.Lifetime:c.Time+10).DefaultIfEmpty(clock.Time+3).Max();
            clock.Duration=Math.Max(clock.Duration,end);
        }
        RebuildScene();clock.Play();
    }
    void ClearManualTriggers()
    {
        manualTriggers.Clear();referenceActions.Clear();cues=effects.Build(clip??new AnimationClip(),conditionalDac.Checked);
        RebuildScene();
    }
    void UpdateCameraHint(){string prefix=requireAlt.Checked?"Alt + ":"";cameraHint.Text=$"{prefix}left: orbit     {prefix}middle: pan     {prefix}right / wheel: dolly     F: frame";}
    void SavePreferences(){if(verificationMode)return;try{preferences.Save(ViewerPreferences.FilePath);}catch(Exception ex){status.Text="Could not save viewer settings: "+ex.Message;}}
    void Lighting(){using var d=new LightRigForm();d.LightsChanged+=()=>{viewport.ApplyRenderSettingsFromConfig();viewport.InvalidateRender();};d.RenderSettingsChanged+=()=>viewport.InvalidateRender();d.ShowDialog(this);SavePreferences();}
    void TogglePlay(){if(clip==null&&!previewingEffect)return;if(clock.Playing)clock.Pause();else clock.Play();}
    void BeginScrub(){if(busy)return;resumeAfterScrub=clock.Playing;scrubbing=true;clock.Pause();viewport.PlayingAnimation=false;}
    void EndScrub(){if(!scrubbing)return;scrubbing=false;if(resumeAfterScrub)clock.Play();resumeAfterScrub=false;}
    void ScrubTo(int value){
        if(busy||(clip==null&&!previewingEffect))return;
        double target=clock.Duration*Math.Clamp(value,0,10000)/10000d;
        if(snapFrames.Checked)target=FrameTime(Math.Round(target*clock.SourceFps));
        clock.Seek(target);scene.SetTime(target);viewport.InvalidateRender();
        UpdateTimeLabel(target);
    }
    // Frames count at the Step FPS rate from the start of the clip; frame 0 is the first pose.
    double FrameTime(double frame)=>Math.Clamp(frame/clock.SourceFps,0,clock.Duration);
    int FrameAt(double time)=>(int)Math.Floor(time*clock.SourceFps+1e-6);
    void UpdateTimeLabel(double time)
    {
        string text=$"{time:F3} / {clock.Duration:F3} s · frame {FrameAt(time)} / {FrameAt(clock.Duration)}";
        if(clip?.Segments is {Count:>0} segments){
            int index=clip.SegmentIndex(time);var segment=segments[index];
            text+=clip.Queued?$" · clip {index+1}/{segments.Count} {segment.Name}":$" · clip time {segment.Local(time):F3} s at {segment.Speed.SpeedAt(segment.Local(time)):0.00}x";
        }
        timeLabel.Text=text;
        Highlight.Update(time,clip==null?null:scene.Precision,follow:clock.Playing);
    }
    // Steps whole frames from the current one; 0 moves to the nearest frame. Pauses playback like the old step buttons.
    void SeekFrame(int frames)
    {
        if(busy||(clip==null&&!previewingEffect))return;
        clock.Pause();
        double target=FrameTime(Math.Round(clock.Time*clock.SourceFps)+frames);
        clock.Seek(target);scene.SetTime(target);viewport.InvalidateRender();
        timeline.Value=(int)Math.Clamp(target/clock.Duration*10000,0,10000);UpdateTimeLabel(target);
    }
    void TickPlayback()
    {
        if(busy)return;
        double now=frameClock.Elapsed.TotalSeconds;if(now-lastSample<1/60d)return;lastSample=now;
        // Physics runs on wall-clock time: with no clip, while scrubbing and while paused, as in game.
        if((clip==null&&!previewingEffect)||scrubbing){StepPhysics(now);return;}
        if(!clock.Loop&&clock.Absolute>=clock.Duration)clock.Pause();
        double time=clock.Time;
        // Snapped playback holds each frame's pose until the clock reaches the next frame.
        if(snapFrames.Checked)time=FrameTime(FrameAt(time));
        if(clock.Playing||time!=scene.Time)scene.SetTime(time);
        StepPhysics(now);
        FireSounds();
        viewport.PlayingAnimation=clock.Playing;
        // PlayingAnimation stops the shared viewport's idle pipeline timer.
        // The playback host must consume dirty poses and upload skinning here.
        if(clock.Playing)viewport.InvalidateRender();
        timeline.Value=(int)Math.Clamp(time/clock.Duration*10000,0,10000);
        UpdateTimeLabel(time);
        if(scene.ParticleSystems>0)status.Text=$"{scene.ParticleSystems} particle systems · {scene.ParticleCount} live particles · {scene.DrawnParticles} drawn"+(scene.BudgetDrops>0?$" · {scene.BudgetDrops:N0} births over budget":"");
    }

    SoundLibrary Sounds()
    {
        if(soundLibrary==null||soundLibrary.RecordsRevision!=records.Revision||soundLibrary.AssetsRevision!=assets.Revision){soundLibrary=new SoundLibrary(records,assets);soundsElsewhere.Clear();}
        return soundLibrary;
    }
    // Plays every SoundPlay annotation the playback clock passed since the last tick. The clock's absolute time keeps
    // growing across loop wraps, so each pass through the clip triggers its sounds once. Paused, scrubbing or seeking
    // only moves the cursor: the next tick of playback starts from where the clock stands, so a sound placed exactly
    // at the starting point still plays. Sounds that are already playing finish, as they would in game.
    void FireSounds()
    {
        double now=clock.Absolute;
        if(!playSounds.Checked||clip==null||!clock.Playing||scrubbing){lastSoundAbsolute=now-1e-6;return;}
        double from=double.IsNaN(lastSoundAbsolute)?now-1e-6:lastSoundAbsolute;lastSoundAbsolute=now;
        if(!(now>from)||now-from>1)return;
        var library=Sounds();double duration=Math.Max(1e-6,clock.Duration);
        foreach(var e in SoundLibrary.Events(clip.Events)){
            if(clock.Loop){
                for(double k=Math.Floor(from/duration);k<=Math.Floor(now/duration);k++){double at=k*duration+e.Time;if(at>from&&at<=now)PlaySound(library,e);}
            }else if(e.Time>from&&e.Time<=now)PlaySound(library,e);
        }
    }
    void PlaySound(SoundLibrary library,SoundEvent e)
    {
        var info=library.Resolve(e.EditorId);if(!info.Playable)return;
        int before=soundPlayer.Problems.Count;soundPlayer.Play(info,assets,e.Time);
        if(soundPlayer.Problems.Count!=before)UpdateSoundWarnings();
    }
    string SoundText()
    {
        if(clip==null)return "";
        var list=SoundLibrary.Events(clip.Events);if(list.Count==0)return "";
        var library=Sounds();
        var lines=new List<string>{"","","SOUNDS"+(playSounds.Checked?"":" · tick Sounds under the timeline to hear them")};
        foreach(var e in list)lines.Add($"{e.Time,8:F4} {e.EditorId}: {library.Resolve(e.EditorId).Summary()}");
        return string.Join("\n",lines);
    }
    // The Warnings tab: sounds the annotations name that the loaded plugins cannot play. Editor IDs that no loaded
    // plugin defines are looked up in the other mod folders next to the mounted ones, in the background.
    void UpdateSoundWarnings()
    {
        var lines=new List<string>();
        if(clip!=null){
            var library=Sounds();var missing=new List<string>();
            foreach(var group in SoundLibrary.Events(clip.Events).GroupBy(e=>e.EditorId,StringComparer.OrdinalIgnoreCase)){
                var info=library.Resolve(group.Key);if(info.Problems.Count==0)continue;
                string times=string.Join(", ",group.Select(e=>e.Time.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+" s"));
                lines.Add($"SoundPlay.{group.Key} at {times} cannot play.");
                foreach(string problem in info.Problems)lines.Add("   "+problem);
                if(info.Descriptor==null&&info.Marker==null){
                    if(soundsElsewhere.TryGetValue(group.Key,out var places))
                        lines.Add(places.Count>0?"   It is defined in "+string.Join("; ",places)+", which is not loaded. Add that mod folder to hear it.":"   No plugin in the mod folders next to the loaded ones defines it either.");
                    else{lines.Add("   Searching the other mod folders for the plugin that defines it…");missing.Add(group.Key);}
                }
            }
            foreach(string problem in soundPlayer.Problems)lines.Add(problem);
            if(missing.Count>0)SearchUnloadedPlugins(library,missing);
        }
        warnings.Text=lines.Count==0?"No warnings.":string.Join("\n",lines);
        if(warningsTab!=null)warningsTab.Text=lines.Count==0?"Warnings":$"Warnings · {lines.Count(l=>!l.StartsWith(' '))}";
    }
    void SearchUnloadedPlugins(SoundLibrary library,List<string> ids)
    {
        soundSearch?.Cancel();var cancel=soundSearch=new CancellationTokenSource();
        _=Task.Run(()=>library.FindInUnloadedPlugins(ids,cancel.Token),cancel.Token).ContinueWith(task=>{
            if(cancel.IsCancellationRequested||task.Status!=TaskStatus.RanToCompletion||library!=soundLibrary)return;
            foreach(var (id,places) in task.Result)soundsElsewhere[id]=places;
            UpdateSoundWarnings();
        },TaskScheduler.FromCurrentSynchronizationContext());
    }
    void UpdateDiagnostics(){var lines=assets.Diagnostics.Concat(records.Diagnostics).Concat(effects.Diagnostics).Concat(scene.Diagnostics).Concat(scene.BudgetDiagnostics).Distinct().ToList();diagnostics.Text=string.Join("\n",lines);if(diagnosticsTab!=null)diagnosticsTab.Text=lines.Count==0?"Diagnostics":$"Diagnostics · {lines.Count}";if(diagnostics.Text.Length==0)diagnostics.Text="No loader errors. Skyrim engine parity and MO2 virtual-file testing are separate validation steps.";}
    void ShowError(Exception ex){status.Text=ex.Message;diagnostics.AppendText("\n"+ex);File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"previewer.log"),DateTime.Now+" "+ex+"\n");}
    sealed record SessionSettings(string[] Roots,string? Plugins,string[]? Dropped=null,float ProjectileElevation=0,int CameraSubject=1,bool FollowCamera=true,bool GridFloor=true,string? Race=null,int Sex=0,string[]? Heads=null,string[]? Armor=null,string? Weapon=null,string? Clip=null,double Time=0,double Fps=30,double Speed=1,bool Loop=true,bool RootMotion=true,bool Effects=true,bool ConditionalDac=false,float ParticleLod=0,string[]? Models=null,string? Skeleton=null,int Version=2,bool ShowWeapon=true,bool Sheathed=false,int Weight=100,int ParticleBudget=2000,string[]? EffectOverlays=null,bool SmpPhysics=false,bool SmpColliders=false,bool SmpBones=false,string? HairColor=null,bool SnapFrames=false,string[]? Queue=null,bool QueuePlaying=false,double QueueBlend=.3,bool QueueAtWindow=true,string? QueueWindow="MCO_WinOpen",bool PrecisionCollisions=false,bool PrecisionNpc=false,bool PlaySounds=false,Dictionary<string,int>?[]? QueueWindowPicks=null,bool ShowSkeleton=false,bool ShowBoneNames=false,string[]? BoneFilter=null);
    // Label shows the node in use so a missing holster on a custom skeleton is visible at a glance.
    void StepPhysics(double now)
    {
        if(!scene.PhysicsActive)return;
        scene.StepPhysics();
        if(now-lastPhysicsLabel>.5){lastPhysicsLabel=now;UpdatePhysicsLabel();}
    }
    void UpdatePhysicsLabel()=>smpLabel.Text=scene.PhysicsStatus;
    // The Bones window: names on or off, and which bones to show. With none ticked every bone is shown; ticking any
    // limits the overlay to those and adds their local axes. The filter box only narrows the list.
    void OpenBonesWindow()
    {
        if(bonesWindow!=null){bonesWindow.Activate();return;}
        if(!showSkeleton.Checked)showSkeleton.Checked=true;
        // Rows: names, heading, search, the list (takes the spare height), buttons (wrap when narrow), note.
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Padding=new Padding(10)};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        foreach(var style in new[]{SizeType.AutoSize,SizeType.AutoSize,SizeType.AutoSize,SizeType.Percent,SizeType.AutoSize,SizeType.AutoSize})layout.RowStyles.Add(new RowStyle(style,100));
        var buttons=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true,Dock=DockStyle.Fill,Margin=new Padding(0,4,0,0)};
        buttons.Controls.Add(Button("Tick listed",()=>SetListedBones(true)));buttons.Controls.Add(Button("Untick listed",()=>SetListedBones(false)));buttons.Controls.Add(Button("Show all",()=>{scene.BoneFilter.Clear();FillBoneList();scene.BoneFilterChanged();SaveSession();}));
        boneSearch.Dock=DockStyle.Fill;boneList.Dock=DockStyle.Fill;boneFilterNote.MaximumSize=new Size(0,0);boneFilterNote.Dock=DockStyle.Fill;
        layout.Controls.Add(showBoneNames,0,0);layout.Controls.Add(new Label{Text="BONES · none ticked shows them all",AutoSize=true,Margin=new Padding(0,10,0,4)},0,1);
        layout.Controls.Add(boneSearch,0,2);layout.Controls.Add(boneList,0,3);layout.Controls.Add(buttons,0,4);layout.Controls.Add(boneFilterNote,0,5);
        // A pop-up tool window that opens beside the Bones… button, kept on screen.
        var size=new Size(380,640);var anchor=bonesButton!=null?bonesButton.PointToScreen(new Point(0,bonesButton.Height+2)):new Point(Right-size.Width-20,Top+120);
        var area=Screen.FromPoint(anchor).WorkingArea;var location=new Point(Math.Clamp(anchor.X-size.Width+(bonesButton?.Width??0),area.Left,area.Right-size.Width),Math.Clamp(anchor.Y,area.Top,area.Bottom-size.Height));
        bonesWindow=new Form{Text="Skeleton bones",Size=size,MinimumSize=new Size(340,420),StartPosition=FormStartPosition.Manual,Location=location,FormBorderStyle=FormBorderStyle.SizableToolWindow,ShowInTaskbar=false,Owner=this,BackColor=BackColor,ForeColor=ForeColor,Font=Font,KeyPreview=true};
        bonesWindow.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Escape)bonesWindow?.Close();};
        bonesWindow.Controls.Add(layout);
        bonesWindow.FormClosed+=(_,_)=>{layout.Controls.Clear();bonesWindow=null;};
        FillBoneList();bonesWindow.Show(this);
    }
    void FillBoneList()
    {
        string[] words=boneSearch.Text.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        fillingBones=true;boneList.BeginUpdate();boneList.Items.Clear();
        foreach(string name in scene.BoneNames.Where(n=>words.All(w=>n.Contains(w,StringComparison.OrdinalIgnoreCase))))boneList.Items.Add(name,scene.BoneFilter.Contains(name));
        boneList.EndUpdate();fillingBones=false;UpdateBoneNote();
    }
    void SetListedBones(bool on)
    {
        foreach(string name in boneList.Items.Cast<string>()){if(on)scene.BoneFilter.Add(name);else scene.BoneFilter.Remove(name);}
        FillBoneList();scene.BoneFilterChanged();SaveSession();
    }
    void UpdateBoneNote()
    {
        int total=scene.BoneNames.Count();int chosen=scene.BoneFilter.Count(b=>scene.Skeleton.SkeletonDictionary.ContainsKey(b));
        boneFilterNote.Text=(chosen==0?$"Showing all {total} bones.":$"Showing {chosen} of {total} bones, with their local axes (X red, Y green, Z blue).")+(scene.BoneFilter.Count>chosen?$" {scene.BoneFilter.Count-chosen} ticked bone(s) are not in this skeleton.":"");
    }
    // Precision lengthens Duration(0) for power attacks. The preview has no attack data, so the clip's file name
    // (BFCO_PowerAttack, mco_powerattack and the like) stands in for it.
    bool PowerAttackAt(double time)=>(clip?.Segments is {Count:>0} segments?segments[clip.SegmentIndex(time)].Name:selected?.Path??"").Contains("power",StringComparison.OrdinalIgnoreCase);
    static string Seconds(double t)=>double.IsPositiveInfinity(t)?"the end":t.ToString("F4",System.Globalization.CultureInfo.InvariantCulture)+" s";
    // Capsule sizes depend on the player/NPC choice and the mounted Precision settings, so they live beside the controls.
    void UpdatePrecisionLabel()
    {
        var timeline=clip==null?null:scene.Precision;bool player=precisionActor.SelectedIndex==0;
        if(timeline==null||!timeline.HasAnnotations){precisionLabel.Text=clip==null?"Load a clip with Precision annotations.":"This clip has no Collision_* annotations. Precision's default swing collisions are not drawn.";return;}
        var lines=new List<string>{$"{timeline.Collisions.Count} collision(s) in this clip"+(timeline.Notes.Count>0?$", {timeline.Notes.Count} note(s) in the Annotations tab":"")+".",
            "Settings: "+scene.PrecisionConfig.Describe(player)+"."};
        foreach(var c in timeline.Collisions)lines.Add($"{Seconds(c.Start)} to {Seconds(c.End)} · {c.Definition.Node}{(c.Definition.Id is {} id?$" ID {id}":"")} · {scene.PrecisionShapeDetail(c)}"+(c.Recoil?$" · recoil until {Seconds(c.RecoilEnd)}":""));
        precisionLabel.Text=string.Join("\n",lines);
    }
    string PrecisionText()
    {
        var timeline=scene.Precision;
        if(timeline==null||!timeline.HasAnnotations)return "";
        var lines=new List<string>{"","","PRECISION ATTACK COLLISIONS"+(precisionShow.Checked?"":" · tick Show Precision attack collisions to draw them")};
        if(timeline.Collisions.Count==0)lines.Add("  no collision is created");
        foreach(var c in timeline.Collisions)
            lines.Add($"{c.Start,8:F4} {c.Definition.Summary()}\n         until {Seconds(c.End)}: {c.EndReason}"+(c.Recoil?$"\n         recoil capsule until {Seconds(c.RecoilEnd)}":""));
        if(timeline.Notes.Count>0){lines.Add("");lines.Add("PRECISION NOTES");lines.AddRange(timeline.Notes.Select(n=>"  "+n));}
        return string.Join("\n",lines);
    }
    void UpdateWeightLabel()=>weightLabel.Text=scene.WeightedShapes==0?$"{weight.Value} · no _0/_1 mesh pairs in this scene":$"{weight.Value} · {scene.WeightedShapes} shape(s) blended";
    void UpdateHolsterLabel(){
        string? node=scene.HolsterNode;
        sheathed.Text=node==null?"Sheathed on holster node":scene.HolsterNodeMissing?$"Sheathed on holster node · {node} missing, hand used":$"Sheathed on holster node · {node}";
        // A clip with weaponDraw / weaponSheathe annotations moves the weapon itself; the manual toggle waits for a clip without them.
        bool driven=scene.ClipDrivesWeapon&&node!=null;
        if(driven)sheathed.Text=(scene.HolsterNodeMissing?$"{node} missing, hand used":$"Holster {node}")+" · set by this clip: "+string.Join(", ",clip!.WeaponEvents.Select(e=>$"{(e.Sheathed?"weaponSheathe":"weaponDraw")} {e.Time:0.00} s"));
        sheathed.Enabled=node!=null&&!driven;
    }
    sealed record SourceEntry(string Label,string Path,bool Folder){public override string ToString()=>Label;}
    // Mounted roots in precedence order, with the archives and loose files each one contributed.
    void RefreshSources()
    {
        sources.BeginUpdate();sources.Items.Clear();
        var archives=FilesDictionary_class.Dictionary.Values.Select(v=>v.BA2File).Where(a=>a.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        for(int i=0;i<assets.Roots.Count;i++){
            string root=assets.Roots[i];string prefix=root.TrimEnd('\\')+"\\";
            int loose=assets.External.Values.Count(p=>p.StartsWith(prefix,StringComparison.OrdinalIgnoreCase));
            // Base-folder archives are stored Data-relative; overlay archives keep their absolute path.
            int packed=archives.Count(a=>{string dir=Path.GetDirectoryName(a)??"";return dir.Length==0?i==0:dir.TrimEnd('\\').Equals(root.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);});
            sources.Items.Add(new SourceEntry($"{i+1} · {(i==0?"Base":"Mod folder")} · {Path.GetFileName(root.TrimEnd('\\'))}   {loose:N0} loose · {packed} BSA",root,true));
        }
        sources.Items.Add(new SourceEntry(pluginsFile==null?"Load order: none selected · standalone archives use folder order":"Load order: "+Path.GetFileName(pluginsFile)+" · "+Path.GetDirectoryName(pluginsFile),pluginsFile??"",false));
        if(assets.DroppedPaths.Count>0)sources.Items.Add(new SourceEntry($"Dropped animations: {assets.DroppedPaths.Count:N0} file(s) kept in the library","",false));
        if(assets.Roots.Count==0)sources.Items.Add(new SourceEntry("Nothing mounted yet. Use Open Data, then Add mod folder.","",false));
        sources.EndUpdate();((WrappedListBox)sources).Reflow();
    }
    ContextMenuStrip SourcesMenu()
    {
        var menu=new ContextMenuStrip();
        var open=menu.Items.Add("Open in Explorer",null,(_,_)=>OpenSource());
        var copy=menu.Items.Add("Copy path",null,(_,_)=>{if(sources.SelectedItem is SourceEntry e&&e.Path.Length>0)Clipboard.SetText(e.Path);});
        var remove=menu.Items.Add("Remove this folder and reload the rest",null,(_,_)=>_=RemoveSource());
        menu.Opening+=(_,_)=>{var e=sources.SelectedItem as SourceEntry;open.Enabled=copy.Enabled=e?.Path.Length>0;remove.Enabled=e?.Folder==true&&!busy;};
        return menu;
    }
    void OpenSource()
    {
        if(sources.SelectedItem is not SourceEntry e||e.Path.Length==0)return;
        try{Process.Start(new ProcessStartInfo("explorer.exe",e.Folder?"\""+e.Path+"\"":"/select,\""+e.Path+"\""){UseShellExecute=false});}catch(Exception ex){ShowError(ex);}
    }
    async Task RemoveSource()
    {
        if(busy||sources.SelectedItem is not SourceEntry{Folder:true} e)return;
        var roots=assets.Roots.Where(r=>!r.Equals(e.Path,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(roots.Length==0){status.Text="The only mounted folder cannot be removed; open a different Data folder instead.";return;}
        for(int i=0;i<roots.Length;i++)await Mount(roots[i],i>0);
        status.Text="Removed "+e.Path+" and reloaded "+roots.Length+" source(s).";
    }
    // Loads a Havok binary tagfile clip on a real skeleton and body: the converted packfile must decode,
    // bind to the skeleton and move the pose over time.
    async Task TagfileSmoke(string file)
    {
        var log=new List<string>();
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        try{
            var bytes=File.ReadAllBytes(file);
            Check(HkxTagfile.IsTagfile(bytes),"Input is a Havok binary tagfile: "+file);
            var packfile=HkxTagfile.ToPackfile(bytes);
            Check(FO4_Base_Library.HkxPackfileParser_Class.EsPackfileHavok(packfile),"Converted bytes carry the SSE packfile magic");
            var decoded=AnimationClip.Load(bytes);
            log.Add($"decoded: {decoded.Duration:F4} s, {decoded.Frames} frames, {decoded.Tracks} tracks, {decoded.Events.Count} annotations; packfile {packfile.Length:N0} bytes from tagfile {bytes.Length:N0}");
            Check(decoded.Tracks>0&&decoded.Frames>1&&decoded.Duration>0&&(Math.Abs(decoded.Duration-(decoded.Frames-1)/30.0)<.02||Math.Abs(decoded.Duration-decoded.Frames/30.0)<.02),"Converted clip decodes with a 30 fps frame count matching its duration");
            string testData=SelfTest.TestData;
            if(!Directory.Exists(Path.Combine(testData,"meshes"))){log.Add("SKIP posed render: test-data fixture is not present");return;}
            await Mount(testData,false);DropFiles([file]);await Task.Delay(300);
            Check(clip!=null&&selected!=null&&selected.Path.Equals(Path.GetFullPath(file),StringComparison.OrdinalIgnoreCase),"Dropped tagfile is selected and decoded in the library");
            Check(scene.Session!=null,"Converted clip binds to the mounted race skeleton");
            clock.Pause();followCamera.Checked=false;scene.FollowScene=false;cameraSubject.SelectedIndex=1;scene.SetTime(0);scene.Frame();viewport.camera.Rotate(30,20);await Task.Delay(200);
            var first=scene.Session!.BuildPoseAtTime(0,"tagfile",false).Pose;
            using(var start=viewport.CaptureBitmap())start.Save(Path.Combine(AppContext.BaseDirectory,"tagfile-0.png"));
            double mid=Math.Min(clip!.Duration*.6,clip.Duration);clock.Seek(mid);scene.SetTime(mid);await Task.Delay(200);
            var later=scene.Session.BuildPoseAtTime((float)mid,"tagfile",false).Pose;
            using(var middle=viewport.CaptureBitmap())middle.Save(Path.Combine(AppContext.BaseDirectory,"tagfile-mid.png"));
            bool moved=!File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"tagfile-0.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"tagfile-mid.png")));
            Check(moved,"The posed body changes between the first frame and mid-clip");
            log.Add("diagnostics: "+string.Join(" | ",scene.Diagnostics.Concat(assets.Diagnostics).Where(d=>d.Contains("nimation",StringComparison.Ordinal)).Take(5)));
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"tagfile-tests.txt"),log);}finally{Close();}}
    }
    // Playback speed follows the HKX: an MCO_AttackSpeed clip sets the control and shows the note, a plain clip resets to 1.00x.
    async Task SpeedSmoke(string mcoClip,string plainClip)
    {
        var log=new List<string>();
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        try{
            await Mount(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,@"..\..\..\..\particle-fixtures")),false);
            speed.Value=2;DropFiles([mcoClip]);await Task.Delay(300);clock.Pause();
            log.Add($"mco clip: speed {speed.Value} note '{speedNote.Text}' clock {clock.Speed}; timeline {clip?.Duration:F4} s");
            var source=AnimationClip.Load(File.ReadAllBytes(mcoClip));
            // Expected playback length, summed piece by piece: each annotation's speed runs until the next one.
            double expected=0,at=0,rate=1;foreach(var (time,value) in source.SpeedChanges){expected+=(time-at)/rate;at=time;rate=value;}expected+=(source.Duration-at)/rate;
            log.Add("speed changes: "+string.Join(", ",source.SpeedChanges.Select(c=>$"{c.Value:0.00}x at {c.Time:F3}")));
            Check(source.SpeedChanges.Select(c=>c.Value).Distinct().Count()>=2,"The test clip changes speed more than once");
            Check(speed.Value==1&&clip is {Segments:{Count:1},Queued:false}&&Math.Abs(clip.Duration-expected)<1e-6,$"An MCO_AttackSpeed clip keeps the Speed box at 1.00x and plays through every speed change ({source.Duration:F3} s of clip in {clip!.Duration:F3} s, expected {expected:F3})");
            var last=source.Events.Last(e=>e.Time>source.SpeedChanges[^1].Time+1e-6);double lastAt=0;at=0;rate=1;foreach(var (time,value) in source.SpeedChanges){lastAt+=(time-at)/rate;at=time;rate=value;}lastAt+=(last.Time-at)/rate;
            Check(clip.Events.Any(e=>e.Text==last.Text&&Math.Abs(e.Time-lastAt)<1e-6),$"An annotation after the last speed change ({last.Text} at clip {last.Time:F3} s) fires at playback {lastAt:F3} s");
            Check(Math.Abs(clip.Segments![0].Local(lastAt)-last.Time)<1e-6,"The pose at that playback time is the clip frame the annotation sits on");
            Check(speedNote.Text.StartsWith("MCO_AttackSpeed")&&speedNote.Text.Contains("x at")&&speedNote.Visible,"The note beside the speed control lists the annotation");
            using(var window=new Bitmap(Width,Height)){DrawToBitmap(window,new Rectangle(Point.Empty,Size));window.Save(Path.Combine(AppContext.BaseDirectory,"speed-note.png"));}
            DropFiles([plainClip]);await Task.Delay(300);clock.Pause();
            log.Add($"plain clip: speed {speed.Value} note '{speedNote.Text}'");
            Check(clip?.Segments==null&&speed.Value==1&&speedNote.Text.Length==0,"A clip without the annotation plays as itself at 1.00x and clears the note");
            speed.Value=1.5m;PreviewParticle(Path.Combine(AppContext.BaseDirectory,@"..\..\..\..\particle-fixtures\meshes\previewer\fountain.nif"));
            Check(speedNote.Text.Length==0,"Effect previews carry no speed note");
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"speed-tests.txt"),log);}finally{Close();}}
    }
    async Task FloorSmoke()
    {
        var log=new List<string>();
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        try{
            string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,@"..\..\..\..\particle-fixtures"));
            await Mount(root,false);
            Check(sources.Items.OfType<SourceEntry>().Any(e=>e.Folder&&e.Path.Equals(root,StringComparison.OrdinalIgnoreCase)),"Loaded sources list shows the mounted fixture folder");
            PreviewParticle(Path.Combine(root,@"meshes\previewer\fountain.nif"));clock.Pause();clock.Seek(.5);scene.SetTime(.5);
            followCamera.Checked=false;scene.FollowScene=false;
            viewport.camera=new OrbitCamera{MaxDistance=100000,distance=700};viewport.camera.Rotate(40,55);await Task.Delay(250);
            static int Bright(Bitmap b){int n=0;for(int y=0;y<b.Height;y+=2)for(int x=0;x<b.Width;x+=2){var c=b.GetPixel(x,y);if(c.R>225&&c.G>225&&c.B>225)n++;}return n;}
            using var withFloor=viewport.CaptureBitmap();withFloor.Save(Path.Combine(AppContext.BaseDirectory,"floor-on.png"));int on=Bright(withFloor);int drawn=scene.FloorDrawn;
            gridFloor.Checked=false;await Task.Delay(100);
            using var without=viewport.CaptureBitmap();without.Save(Path.Combine(AppContext.BaseDirectory,"floor-off.png"));int off=Bright(without);
            log.Add($"bright pixels with floor {on}, without {off}; floor draws {drawn} then {scene.FloorDrawn}");
            Check(!scene.Diagnostics.Any(d=>d.Contains("Grid floor texture unavailable")),"Floor diffuse, normal and specular DDS maps load from Resources");
            Check(drawn==1&&scene.FloorDrawn==0,"Floor draws once per frame while enabled and not at all when disabled");
            int changed=0;for(int y=0;y<withFloor.Height;y+=2)for(int x=0;x<withFloor.Width;x+=2)if(withFloor.GetPixel(x,y)!=without.GetPixel(x,y))changed++;
            Check(changed>2000,"Textured floor changes rendered pixels and disappears when disabled");
            gridFloor.Checked=true;await Task.Delay(100);
            using var again=viewport.CaptureBitmap();
            Check(Math.Abs(Bright(again)-on)<50,"Re-enabling the floor restores the same render");
            var combo=(SearchableComboBox)cameraSubject;combo.OpenPopup();combo.SetFilter("effects");
            Check(combo.PopupOpen&&combo.VisibleCount==1,"Filter text narrows the drop-down to the matching entry");
            using(var popupImage=combo.CapturePopup())popupImage?.Save(Path.Combine(AppContext.BaseDirectory,"floor-dropdown-filtered.png"));
            combo.CommitSelection();
            Check(!combo.PopupOpen&&cameraSubject.SelectedIndex==2&&scene.FramingSubject==Scene.FrameSubject.Effects,"Committing the filtered entry selects it through the normal ComboBox events");
            combo.OpenPopup();combo.SetFilter("");Check(combo.VisibleCount==3,"Clearing the filter lists every entry");combo.SetFilter("zzz");Check(combo.VisibleCount==0,"No match leaves the list empty instead of failing");
            combo.CommitSelection();Check(cameraSubject.SelectedIndex==2,"Committing with no visible entry keeps the selection");
            var raceCombo=(SearchableComboBox)races;raceCombo.OpenPopup();raceCombo.SetFilter("");using(var open=raceCombo.CapturePopup())open?.Save(Path.Combine(AppContext.BaseDirectory,"floor-dropdown-open.png"));raceCombo.CommitSelection();
            using(var window=new Bitmap(Width,Height)){DrawToBitmap(window,new Rectangle(Point.Empty,Size));window.Save(Path.Combine(AppContext.BaseDirectory,"floor-window.png"));}
            string testData=SelfTest.TestData;
            if(Directory.Exists(Path.Combine(testData,"meshes"))){
                await Mount(testData,false);await Mount(root,true);clock.Pause();
                Check(sources.Items.OfType<SourceEntry>().Count(e=>e.Folder)==2&&((SourceEntry)sources.Items[0]).Path.Equals(testData,StringComparison.OrdinalIgnoreCase),"Sources list orders the base folder before the mod folder");
                cameraSubject.SelectedIndex=1;followCamera.Checked=false;scene.FollowScene=false;scene.SetTime(0);scene.Frame();
                viewport.camera.Rotate(30,25);viewport.camera.distance*=1.15f;await Task.Delay(300);
                using var character=viewport.CaptureBitmap();character.Save(Path.Combine(AppContext.BaseDirectory,"floor-character.png"));
                int floorBelow=0,figure=0;for(int y=0;y<character.Height;y+=2)for(int x=0;x<character.Width;x+=2){var c=character.GetPixel(x,y);bool white=c.R>225&&c.G>225&&c.B>225;if(white&&y>character.Height*3/4)floorBelow++;if(!white&&Math.Abs(x-character.Width/2)<character.Width/8&&Math.Abs(y-character.Height/2)<character.Height/4)figure++;}
                log.Add($"character render: floor pixels in lower quarter {floorBelow}; figure pixels near centre {figure}; meshes {viewport.Model.meshes.Count}");
                Check(viewport.Model.meshes.Count>0&&scene.FloorDrawn==1&&figure>500,"A mounted race body stands on the grid floor at the origin");
                var sword=records.Of("WEAP").FirstOrDefault(w=>w.First("MODL")?.Text.Contains("iron",StringComparison.OrdinalIgnoreCase)==true&&assets.Exists(@"meshes\"+w.First("MODL")!.Text));
                if(sword!=null){
                    weapons.SelectedItem=sword;await Task.Delay(300);int shown=scene.VisibleWeaponMeshes;
                    using(var armed=viewport.CaptureBitmap())armed.Save(Path.Combine(AppContext.BaseDirectory,"floor-weapon-on.png"));
                    showWeapon.Checked=false;await Task.Delay(100);using(var bare=viewport.CaptureBitmap())bare.Save(Path.Combine(AppContext.BaseDirectory,"floor-weapon-off.png"));int hidden=scene.VisibleWeaponMeshes;
                    bool differs=!File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"floor-weapon-on.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"floor-weapon-off.png")));
                    showWeapon.Checked=true;await Task.Delay(100);using(var restored=viewport.CaptureBitmap()){}
                    log.Add($"weapon {sword.EditorId}: visible meshes {shown} shown, {hidden} hidden, {scene.VisibleWeaponMeshes} restored");
                    Check(shown>0&&hidden==0&&scene.VisibleWeaponMeshes==shown&&differs,"Show weapon hides and restores the equipped weapon meshes without rebuilding the scene");
                    var handPlacement=scene.WeaponTransforms;string? holster=Records.WeaponHolsterNode(sword);
                    sheathed.Checked=true;await Task.Delay(150);using(var hung=viewport.CaptureBitmap())hung.Save(Path.Combine(AppContext.BaseDirectory,"floor-weapon-sheathed.png"));
                    var holsterPlacement=scene.WeaponTransforms;
                    log.Add($"holster: type node {holster}; scene bone {scene.WeaponBoneName}; hand {handPlacement[0].Row3}; holster {holsterPlacement[0].Row3}");
                    Check(holster!=null&&scene.WeaponBoneName==holster&&!scene.HolsterNodeMissing,"Sheathed mode selects the WEAP animation type's holster node on the mounted skeleton");
                    Check((holsterPlacement[0].Row3-handPlacement[0].Row3).Length>5&&scene.VisibleWeaponMeshes==shown,"Sheathing moves the weapon to a different bone position while keeping it visible");
                    sheathed.Checked=false;await Task.Delay(100);using(var unsheathed=viewport.CaptureBitmap()){}
                    Check(scene.WeaponBoneName=="Weapon"&&scene.WeaponTransforms[0]==handPlacement[0],"Unsheathing returns the weapon to the hand transform exactly");
                }else log.Add("SKIP weapon toggle: no iron weapon with a mounted mesh");
                {
                    static double Span(IEnumerable<FO4_Base_Library.PreviewModel.RenderableMesh> meshes)=>meshes.Sum(m=>(m.BoundsMax-m.BoundsMin).Length);
                    var bodyMeshes=viewport.Model.meshes.Where(m=>!scene.MeshIsWeapon(m.MeshData.Shape)).ToList();
                    using(var heavy=viewport.CaptureBitmap())heavy.Save(Path.Combine(AppContext.BaseDirectory,"floor-weight-100.png"));double heavySpan=Span(bodyMeshes);
                    weight.Value=0;await Task.Delay(300);using(var light=viewport.CaptureBitmap())light.Save(Path.Combine(AppContext.BaseDirectory,"floor-weight-0.png"));double lightSpan=Span(bodyMeshes);
                    bool differs=!File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"floor-weight-100.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"floor-weight-0.png")));
                    log.Add($"weight: {scene.WeightedShapes} blended shapes; bounds span {heavySpan:F2} at 100, {lightSpan:F2} at 0");
                    Check(scene.WeightedShapes>0&&differs&&lightSpan<heavySpan,"Weight 0 blends body and armor toward their _0 meshes and shrinks the figure");
                    weight.Value=100;await Task.Delay(300);using(var back=viewport.CaptureBitmap())back.Save(Path.Combine(AppContext.BaseDirectory,"floor-weight-restored.png"));
                    Check(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"floor-weight-100.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"floor-weight-restored.png"))),"Returning to weight 100 reproduces the original render exactly");
                }
            }else log.Add("SKIP character-on-floor render: test-data fixture is not present");
            foreach(Form f in Application.OpenForms.Cast<Form>().ToArray())if(f!=this)f.Close();
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"floor-tests.txt"),log);}finally{Close();}}
    }
    async Task EmitterSourceSmoke()
    {
        var log=new List<string>();
        try{
            string root=Path.Combine(AppContext.BaseDirectory,"emitter-binding-fixture");MultiEmitterTests.Fixture(root);await Mount(root,false);
            PreviewParticle(@"meshes\previewer\multiple-emitters.nif");clock.Pause();clock.Seek(.5);scene.SetTime(.5);scene.FollowScene=false;followCamera.Checked=false;await Task.Delay(250);
            var selected=scene.MeshEmitters.Single();var links=selected.Source.EmitterMeshes.Indices.ToArray();
            using(var before=viewport.CaptureBitmap())before.Save(Path.Combine(AppContext.BaseDirectory,"emitter-source-before.png"));
            Exception? failure=null;using var timer=new System.Windows.Forms.Timer{Interval=100};
            timer.Tick+=(_,_)=>{
                timer.Stop();Form? dialog=null;
                try{
                    dialog=Application.OpenForms.Cast<Form>().Single(f=>f.Text=="Particle emitter sources");
                    var picker=(ComboBox)dialog.Controls.Find("EmitterSourceList",true).Single();var meshes=(CheckedListBox)dialog.Controls.Find("EmitterSourceMeshes",true).Single();
                    if(picker.Items.Count!=1||!picker.SelectedItem!.ToString()!.Contains("Green mesh")||meshes.CheckedItems.Count!=1)throw new InvalidDataException("Source picker did not select the secondary mesh emitter");
                    using(var image=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(AppContext.BaseDirectory,"emitter-source-dialog.png"));}
                    log.Add("PASS Dialog identifies the secondary mesh emitter and its authored source geometry.");
                    for(int i=0;i<meshes.Items.Count;i++)meshes.SetItemChecked(i,false);
                    Button Action(string text)=>dialog.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(b=>b.Text==text);
                    Action("Apply preview binding").PerformClick();using(var empty=viewport.CaptureBitmap())empty.Save(Path.Combine(AppContext.BaseDirectory,"emitter-source-empty.png"));
                    if(!selected.HasEmitterOverride||selected.Owner.Simulation.Particles.Count!=60||!selected.Source.EmitterMeshes.Indices.SequenceEqual(links))throw new InvalidDataException("Applying a secondary override changed other channels or saved links");
                    log.Add("PASS Applying an empty source override removes only the selected green channel and preserves saved NIF links.");
                    Action("Restore authored").PerformClick();using(var restored=viewport.CaptureBitmap())restored.Save(Path.Combine(AppContext.BaseDirectory,"emitter-source-restored.png"));
                    if(selected.HasEmitterOverride||selected.Owner.Simulation.Particles.Count!=75||!File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"emitter-source-before.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"emitter-source-restored.png"))))throw new InvalidDataException("Restore did not recover the original rendered system");
                    log.Add("PASS Restore authored recovers all three channels and the exact original rendered image.");
                }catch(Exception ex){failure=ex;}finally{dialog?.Close();}
            };
            timer.Start();ChooseEmitterSources();if(failure!=null)throw failure;
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"emitter-source-tests.txt"),log);}finally{Close();}}
    }
    async Task RefractionSmoke()
    {
        var log=new List<string>();
        try{
            string root=Path.Combine(AppContext.BaseDirectory,"refraction-fixture");RefractionFixture.Create(root);await Mount(root,false);
            PreviewParticle(@"meshes\previewer\refraction.nif");clock.Pause();clock.Seek(0);scene.SetTime(0);scene.FollowScene=false;
            viewport.camera=new OrbitCamera{distance=250,MaxDistance=1000};await Task.Delay(300);
            var shader=viewport.Model.meshes.Select(m=>m.MeshData.Shape.NifShader).OfType<NiflySharp.Blocks.BSLightingShaderProperty>().Single();
            Bitmap Capture(string name){var b=viewport.CaptureBitmap();b.Save(Path.Combine(AppContext.BaseDirectory,"refraction-"+name+".png"));return b;}
            scene.RefractionEnabled=false;using var baseline=Capture("baseline");
            scene.RefractionEnabled=true;shader.RefractionStrength=0;using var zero=Capture("zero");
            shader.RefractionStrength=1;using var displaced=Capture("displaced");
            int changed=0,neutralChanged=0;for(int y=0;y<baseline.Height;y++)for(int x=0;x<baseline.Width;x++){
                var a=baseline.GetPixel(x,y);var b=zero.GetPixel(x,y);var c=displaced.GetPixel(x,y);
                if(Math.Abs(a.R-b.R)>1||Math.Abs(a.G-b.G)>1||Math.Abs(a.B-b.B)>1)neutralChanged++;
                if(Math.Abs(a.R-c.R)>8||Math.Abs(a.G-c.G)>8||Math.Abs(a.B-c.B)>8)changed++;
            }
            var glError=OpenTK.Graphics.OpenGL4.GL.GetError();
            log.Add($"Neutral changed pixels {neutralChanged}; displaced changed pixels {changed}; drawn layers {scene.DrawnRefractionLayers}; GL {glError}");
            if(glError!=OpenTK.Graphics.OpenGL4.ErrorCode.NoError)throw new InvalidDataException("Distortion produced a graphics error: "+glError);
            if(neutralChanged!=0||changed<100||scene.DrawnRefractionLayers!=1)throw new InvalidDataException("Distortion failed neutral/displacement conformance");
            log.Add("PASS Zero strength preserves the original scene; authored diffuse-slot vectors displace the checker backdrop.");
            using var replay=Capture("replay");if(!File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"refraction-displaced.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"refraction-replay.png"))))throw new InvalidDataException("Refraction replay changed");
            log.Add("PASS Identical inputs reproduce the same rendered image.");
            int occludedChanges=0;for(int y=baseline.Height/2-10;y<baseline.Height/2+10;y++)for(int x=baseline.Width/2-10;x<baseline.Width/2+10;x++)if(baseline.GetPixel(x,y)!=displaced.GetPixel(x,y))occludedChanges++;
            if(occludedChanges!=0)throw new InvalidDataException("Distortion changed pixels behind the foreground occluder");
            log.Add("PASS Opaque foreground depth prevents distortion across the occluder.");
            shader.RefractionStrength=-1;using var negative=Capture("negative");
            if(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"refraction-negative.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"refraction-displaced.png"))))throw new InvalidDataException("Signed strength failed to reverse displacement");
            log.Add("PASS Positive and negative authored strength produce distinct rendered displacement.");
            RefractionFade.Tests((ok,message)=>{log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);});
            shader.ShaderFlags_SSPF2=(NiflySharp.Enums.SkyrimShaderPropertyFlags2)((uint)shader.ShaderFlags_SSPF2&~8u);shader.RefractionStrength=1;
            viewport.camera.distance=1200;scene.RefractionEnabled=false;using var farPlain=Capture("far-baseline");scene.RefractionEnabled=true;using var far=Capture("far-faded");
            if(scene.DrawnRefractionLayers!=0||!File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"refraction-far-baseline.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"refraction-far-faded.png"))))throw new InvalidDataException("Distant refraction did not fade out");
            log.Add("PASS Default native fade category removes distant distortion and preserves the scene exactly.");
            shader.ShaderFlags_SSPF2=(NiflySharp.Enums.SkyrimShaderPropertyFlags2)((uint)shader.ShaderFlags_SSPF2|8u);using var farNoFade=Capture("far-no-fade");
            if(scene.DrawnRefractionLayers!=1||File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"refraction-far-no-fade.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"refraction-far-baseline.png"))))throw new InvalidDataException("NoFade did not bypass distance attenuation");
            log.Add("PASS Authored NoFade preserves distant distortion.");
            viewport.camera.distance=1000;using var middleFull=Capture("middle-full");shader.ShaderFlags_SSPF2=(NiflySharp.Enums.SkyrimShaderPropertyFlags2)((uint)shader.ShaderFlags_SSPF2&~8u);using var middleFade=Capture("middle-faded");
            if(scene.DrawnRefractionLayers!=1||File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"refraction-middle-full.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"refraction-middle-faded.png"))))throw new InvalidDataException("Intermediate distance did not attenuate refraction");
            log.Add("PASS Intermediate camera distance changes distortion strength while retaining the layer.");
            viewport.camera.distance=250;using var near=Capture("near-restored");if(scene.DrawnRefractionLayers!=1||!File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"refraction-near-restored.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"refraction-displaced.png"))))throw new InvalidDataException("Returning camera did not restore the same refraction image");
            log.Add("PASS Returning the camera restores refraction deterministically.");
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"refraction-tests.txt"),log);}finally{Close();}}
    }
    async Task ArchiveOrderSmoke()
    {
        var log=new List<string>();
        try{
            string root=Environment.GetEnvironmentVariable("SSE_ARCHIVE_TEST_ROOT")??Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,@"..\..\..\..\verification\archive-fixtures"));
            await Mount(Path.Combine(root,"empty"),false);await Mount(Path.Combine(root,"archive-order-render"),true);
            foreach(string stage in new[]{"black","white","black-replay"}){
                string profile=Path.Combine(root,"archive-order-render",stage.StartsWith("black")?"black.txt":"white.txt");
                records.Load(assets,profile);PreviewParticle(@"meshes\previewer\billboards.nif");clock.Pause();clock.Seek(.5);scene.SetTime(.5);scene.Frame();await Task.Delay(500);
                using var bitmap=viewport.CaptureBitmap();bitmap.Save(Path.Combine(AppContext.BaseDirectory,"archive-order-"+stage+".png"));
                log.Add(stage+": "+assets.Source(@"textures\previewer\particle.dds")+"; revision "+assets.Revision);
            }
            byte[] Read(string name)=>File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"archive-order-"+name+".png"));
            if(Read("black").SequenceEqual(Read("white")))throw new InvalidDataException("Archive texture change was not reflected in the viewport");
            if(!Read("black").SequenceEqual(Read("black-replay")))throw new InvalidDataException("Restoring the prior archive did not reproduce its render");
            log.Add("PASS Changed archive texture renders differently; restoring the original order reproduces the original image exactly.");
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"archive-order-render.txt"),log);}finally{Close();}}
    }
    async Task ReferenceEffectSmoke()
    {
        var log=new List<string>();
        try{
            await Mount(SelfTest.GameData,false);
            await Mount(Path.Combine(SelfTest.Mods,"Anchor MCO Animations v2 - Cloud Fenrir"),true);
            var impactCatalog=ImpactEffects.Catalog(records,assets);File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"impact-catalog.txt"),string.Join("\n\n",impactCatalog.Select(e=>e.Details)));
            var impactEntry=impactCatalog.Single(e=>e.Record.EditorId=="MAGFireSpellmpactROCKS01");
            PreviewParticle(impactEntry.Model,impactEntry.Duration);clock.Pause();clock.Seek(.1);scene.SetTime(.1);scene.Frame();await Task.Delay(250);
            using(var impactImage=viewport.CaptureBitmap())impactImage.Save(Path.Combine(AppContext.BaseDirectory,"record-impact.png"));
            log.Add($"Impact mesh {impactEntry.Record.EditorId}: authored duration={impactEntry.Duration}, systems={scene.ParticleSystems}, drawn={scene.DrawnParticles}");
            log.AddRange(scene.Diagnostics);File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory,"inspected-impact.nif"),assets.Read(impactEntry.Model));
            if(scene.ParticleSystems==0||scene.DrawnParticles==0)throw new InvalidDataException("ADDN-linked fire impact did not render particles");
            log.Add("PASS ADDN-linked fire impact resolves and renders its artwork.");
            var bloodImpact=impactCatalog.Single(e=>e.Record.EditorId=="BloodSprayBleedImpactRed");
            PreviewParticle(bloodImpact.Model,bloodImpact.Duration);clock.Pause();followCamera.Checked=false;scene.FollowScene=false;clock.Seek(.1);scene.SetTime(.1);scene.Frame();await Task.Delay(250);
            using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"record-blood-impact.png"));
            log.Add($"Blood impact {bloodImpact.Record.EditorId}: duration={bloodImpact.Duration}, systems={scene.ParticleSystems}, drawn={scene.DrawnParticles}");log.AddRange(scene.Diagnostics);
            if(scene.ParticleSystems==0||scene.DrawnParticles==0)throw new InvalidDataException("ADDN-linked blood impact did not render particles");
            var bloodSystems=scene.ParticleInstances.ToArray();
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"blood-materials.txt"),bloodSystems.Select(p=>p.Name+" | "+p.Texture+" | "+p.Shader?.GetType().Name+" | "+string.Join("; ",new[]{"BaseColor","BaseColorScale","ShaderFlags_SSPF1","ShaderFlags_SSPF2","HasGreyscaleToPaletteColor","HasGreyscaleToPaletteAlpha","FalloffStartAngle","FalloffStopAngle","FalloffStartOpacity","FalloffStopOpacity"}.Select(k=>k+"="+NifAnimation.Get(p.Shader,k)))+" | Alpha="+NifAnimation.Get(NifAnimation.Get(p.Alpha,"Flags"),"Value")+" | particle="+(p.Simulation.Particles.Count>0?p.Simulation.Particles[0].Color.ToString():"none")));

            if(bloodSystems.Any(p=>p.Simulation.EmissionEnabled(bloodImpact.Duration+.01)))throw new InvalidDataException("Blood impact emission outlived its authored duration");
            clock.Seek(.4);scene.SetTime(.4);clock.Seek(.1);scene.SetTime(.1);await Task.Delay(250);
            using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"record-blood-impact-replay.png"));
            if(!File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"record-blood-impact.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"record-blood-impact-replay.png"))))throw new InvalidDataException("Blood impact rewind changed its rendered image");
            log.Add("PASS Real blood impact resolves ADDN artwork, renders particles, respects authored emission duration, and rewinds byte-identically.");
            var particleImpact=new ImpactEffectEntry(new GameRecord{Kind="IPCT",Plugin="verification",Key="verification|000001"},@"meshes\previewer\impact-duration-fixture.nif",.25f,[]);
            assets.External[particleImpact.Model]=ParticleFixture.Create(Path.Combine(AppContext.BaseDirectory,"impact-duration-fixture"));
            await assets.Mount(Path.Combine(AppContext.BaseDirectory,"impact-duration-fixture"),true,new Progress<string>());
            PreviewParticle(particleImpact.Model,particleImpact.Duration);clock.Pause();clock.Seek(.1);scene.SetTime(.1);scene.Frame();await Task.Delay(250);
            using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"record-particle-impact.png"));
            log.AddRange(scene.Diagnostics);log.Add(scene.ParticleDebug);
            if(scene.ParticleSystems==0||scene.DrawnParticles==0)throw new InvalidDataException("Original duration fixture did not render");
            log.Add($"PASS Impact mesh {particleImpact.Record.EditorId}: authored duration={particleImpact.Duration}, systems={scene.ParticleSystems}, drawn={scene.DrawnParticles}");
            var falloffShaders=scene.ParticleInstances.Select(p=>p.Shader).OfType<NiflySharp.Blocks.BSEffectShaderProperty>().Distinct().ToArray();
            foreach(var shader in falloffShaders){shader.ShaderFlags_SSPF1|=(NiflySharp.Enums.SkyrimShaderPropertyFlags1)64u;shader.FalloffStartAngle=.8f;shader.FalloffStopAngle=.2f;shader.FalloffStartOpacity=0;shader.FalloffStopOpacity=0;}
            await Task.Delay(250);using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"falloff-zero.png"));
            foreach(var shader in falloffShaders){shader.FalloffStartOpacity=1;shader.FalloffStopOpacity=1;}
            await Task.Delay(250);using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"falloff-one.png"));
            if(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"falloff-zero.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"falloff-one.png"))))throw new InvalidDataException("Authored particle falloff did not change the rendered opacity");
            if(!File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"record-particle-impact.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"falloff-one.png"))))throw new InvalidDataException("Unit particle falloff altered the original render");
            log.Add("PASS Authored zero falloff changes the particle render; unit falloff reproduces the original image exactly.");
            var savedParticleRig=Config_App.Current.ActiveLights();
            try{
                foreach(var shader in falloffShaders){shader.ShaderFlags_SSPF2|=(NiflySharp.Enums.SkyrimShaderPropertyFlags2)0x40000000u;shader.LightingInfluence=255;}
                Config_App.Current.SetActiveLights(new PreviewLightRig());
                await Task.Delay(250);using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"particle-lighting-dark.png"));
                Config_App.Current.SetActiveLights(new PreviewLightRig{AmbientIntensity=1,AmbientSkyColor=RigColor.White,AmbientGroundColor=RigColor.White,AmbientGroundLevel=1});
                await Task.Delay(250);using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"particle-lighting-neutral.png"));
                if(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"particle-lighting-dark.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"particle-lighting-neutral.png"))))throw new InvalidDataException("Effect particles ignored studio lighting");
                if(!File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"falloff-one.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"particle-lighting-neutral.png"))))throw new InvalidDataException("Unit studio radiance altered the original particle render");
                Config_App.Current.SetActiveLights(new PreviewLightRig());foreach(var shader in falloffShaders)shader.LightingInfluence=0;
                await Task.Delay(250);using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"particle-lighting-zero-influence.png"));
                if(!File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"falloff-one.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"particle-lighting-zero-influence.png"))))throw new InvalidDataException("Zero lighting influence did not preserve the emissive particle render");
                log.Add("PASS Effect particles respond to studio lighting; unit radiance and zero influence reproduce the original render exactly.");
            }finally{Config_App.Current.SetActiveLights(savedParticleRig);}
            string addonRoot=Path.Combine(AppContext.BaseDirectory,"addon-fixture");string addonFile=AddonArtworkTests.Fixture(addonRoot);
            MultiTargetEmitterTests.Fixture(addonRoot);
            var addonRecord=AddonArtworkTests.Record();addonRecord.Fields.RemoveAll(f=>f.Tag=="MODL");addonRecord.Fields.Add(new("MODL",System.Text.Encoding.UTF8.GetBytes(@"previewer\multi-target-emitter.nif")));records.All[addonRecord.Key]=addonRecord;await assets.Mount(addonRoot,true,new Progress<string>());
            PreviewParticle(addonFile,1);clock.Pause();followCamera.Checked=false;scene.FollowScene=false;clock.Seek(.5);scene.SetTime(.5);scene.Frame();await Task.Delay(250);
            var addonParticle=scene.ParticleInstances.Single();
            if(System.Numerics.Vector3.Distance(addonParticle.Placement(.5).Translation,new(70,20,30))>.001)throw new InvalidDataException("Animated add-on hierarchy was not applied exactly once");
            if(addonParticle.Simulation.Particles.Any(p=>p.Velocity.Length()>100))throw new InvalidDataException("Scaled ADDN launch multiplied particle speed");
            if(Math.Abs(addonParticle.Simulation.PrimaryEmitter.SettingsAt!(.5).Radius-3.3f)>.0001)throw new InvalidDataException("ADDN radius did not use local scale separately from scaled parent placement");
            using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"addon-original.png"));
            if(scene.DrawnParticles==0||addonParticle.Simulation.TotalBorn!=12)throw new InvalidDataException("Original bound multi-target add-on did not render its expected births");
            clock.Seek(.8);scene.SetTime(.8);await Task.Delay(100);clock.Seek(.5);scene.SetTime(.5);await Task.Delay(250);
            using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"addon-original-replay.png"));
            if(!File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"addon-original.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"addon-original-replay.png"))))throw new InvalidDataException("Add-on rewind render differs");
            if(addonParticle.Simulation.EmissionEnabled(1.2))throw new InvalidDataException("ADDN emission exceeded parent cue lifetime");
            log.Add("PASS Original ADDN follows animated hierarchical transform, inherits emission lifetime, renders and replays exactly.");
            records.All.Remove(addonRecord.Key);
            var catalog=ReferenceEffects.Catalog(records,assets);log.Add($"Selectable reference effects: {catalog.Length}");
            foreach(int index in new[]{1,2,3}){
                var entry=catalog.Single(e=>e.Record.EditorId==$"CrossSlash_HitFX0{index}");
                PreviewReferenceEffect(entry);clock.Pause();clock.Seek(.5);scene.SetTime(.5);scene.Frame();await Task.Delay(250);
                using var bitmap=viewport.CaptureBitmap();bitmap.Save(Path.Combine(AppContext.BaseDirectory,$"reference-effect-{index}.png"));
                if(scene.ParticleSystems==0||scene.DrawnParticles==0)throw new InvalidDataException(entry.Name+" did not draw its particle systems");
                log.Add(entry.Details);log.Add($"systems={scene.ParticleSystems}, live={scene.ParticleCount}, drawn={scene.DrawnParticles}");
                clock.Seek(0);QueueReferenceEffect(entry,false);clock.Pause();
                clock.Seek(.8);QueueReferenceEffect(entry,true);clock.Pause();
                clock.Seek(.5);scene.SetTime(.5);scene.Frame();await Task.Delay(200);
                using(var active=viewport.CaptureBitmap())active.Save(Path.Combine(AppContext.BaseDirectory,$"reference-timeline-cross-{index}.png"));
                if(scene.DrawnParticles==0)throw new InvalidDataException(entry.Name+" timeline play did not draw");
                clock.Seek(3.9);scene.SetTime(3.9);await Task.Delay(200);using(var finished=viewport.CaptureBitmap()){}
                if(scene.DrawnParticles!=0)throw new InvalidDataException(entry.Name+" timeline stop did not finish");
                clock.Seek(.5);scene.SetTime(.5);await Task.Delay(200);
                using(var replay=viewport.CaptureBitmap())replay.Save(Path.Combine(AppContext.BaseDirectory,$"reference-timeline-cross-{index}-replay.png"));
                var first=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,$"reference-timeline-cross-{index}.png"));
                var second=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,$"reference-timeline-cross-{index}-replay.png"));
                if(!first.SequenceEqual(second))throw new InvalidDataException(entry.Name+" timeline rewind render differs");
                log.Add("PASS "+entry.Record.EditorId+": timeline play draws; stop finishes; rewind reproduces the original render exactly.");
            }
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"reference-effects.txt"),log);}finally{Close();}}
    }
    async Task RemoteCastSmoke()
    {
        var log=new List<string>();
        try{
            string root=Environment.GetEnvironmentVariable("SSE_REMOTE_TEST_ROOT")??Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,@"..\..\..\..\particle-fixtures"));
            await Mount(root,false);clock.Pause();clip=null;scene.Clear();scene.Clip=null;previewingEffect=true;clock.Duration=3;
            var frames=ScriptedCasts.FenrirFrames();
            foreach(var frame in frames){
                var cue=new EffectCue(0,@"meshes\previewer\fountain.nif",100,0,3,1,frame.Start,"Remote-cast placement fixture",ProjectileType:1,Elevation:frame.Elevation,Heading:frame.Heading);
                scene.AddModel(cue.Model,cue:cue);
                log.Add($"Start {frame.Start}; target {frame.Target}; heading {frame.Heading}; elevation {frame.Elevation}");
            }
            scene.Commit();scene.SetTime(1);viewport.camera=new OrbitCamera{MaxDistance=100000};viewport.camera.Rotate(20,65);scene.Frame();
            foreach(double time in new[]{.5,1d}){
                clock.Seek(time);scene.SetTime(time);await Task.Delay(250);
                using var bitmap=viewport.CaptureBitmap();bitmap.Save(Path.Combine(AppContext.BaseDirectory,$"remote-casts-{time:0.0}.png"));
                if(scene.ParticleCount==0||scene.DrawnParticles==0)throw new InvalidDataException("Remote-cast fixture particles were not rendered");
                log.Add($"{time:F2}s: live={scene.ParticleCount}, drawn={scene.DrawnParticles}");
            }
            scene.SetTime(.2);scene.SetTime(1);await Task.Delay(250);
            using(var replay=viewport.CaptureBitmap())replay.Save(Path.Combine(AppContext.BaseDirectory,"remote-casts-replay.png"));
            log.Add("Original fountain fixtures visualize trajectories; these are not Blade Beam art or native collision validation.");
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"remote-casts.txt"),log);}finally{Close();}}
    }
    async Task AttachmentSmoke()
    {
        var log=new List<string>();
        void Check(bool condition,string text){log.Add((condition?"PASS ":"FAIL ")+text);if(!condition)throw new InvalidDataException(text);}
        try{
            await Mount(SelfTest.TestData,false);
            await Mount(Fixture,true);
            string fixtures=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,@"..\..\..\..\particle-fixtures"));
            await assets.Mount(fixtures,true,new Progress<string>());
            weapons.SelectedItem=records.Of("WEAP").First(w=>w.EditorId=="IronGreatsword");
            await SelectClip(assets.Clips.First(c=>c.Name.Equals("mco_attack1.hkx",StringComparison.OrdinalIgnoreCase)));
            clock.Pause();
            var a=new EffectCue(.2,@"meshes\previewer\fountain.nif",0,0,1,1,System.Numerics.Vector3.Zero,"Attachment fixture A",37);
            var b=a with {Time=.7,Origin="Attachment fixture B"};
            cues=[a,b];RebuildScene();clock.Seek(.9);scene.SetTime(.9);
            var pose=scene.Skeleton.Pose;
            var early=scene.AttachmentFrame(a);var late=scene.AttachmentFrame(b);
            Check(early!=null&&late!=null,"Equipped two-handed weapon resolves biped index 37");
            Check(System.Numerics.Vector3.Distance(early!.Value.Translation,late!.Value.Translation)>1,"Different annotation times use different animated weapon bounds");
            Check(ReferenceEquals(pose,scene.Skeleton.Pose),"Historical attachment sampling restores the displayed actor pose");
            Check(scene.AttachmentFrame(a)==early,"Initial attachment reference remains fixed when queried after the event");
            var followed=scene.FollowingAttachmentFrame(a,.7);
            Check(followed!=null&&System.Numerics.Vector3.Distance(followed.Value.Translation,early.Value.Translation)>1,"Temporary particle attachment follows the animated weapon after spawn");
            Check(scene.FollowingAttachmentFrame(a,0)==early,"Following starts at the authored initial placement");
            Check(scene.FollowingAttachmentFrame(a with {SpawnFlags=0},.7)==scene.FollowingAttachmentFrame(a with {SpawnFlags=3},.7),"Target following is independent of spawn flags, matching AE update");
            Check(ReferenceEquals(pose,scene.Skeleton.Pose),"Following attachment sampling preserves the displayed actor pose");
            Check(scene.AttachmentFrame(a with {Biped=33})==null,"Unequipped one-handed biped is suppressed");
            scene.ResetParticles();clock.Seek(.9);scene.SetTime(.9);
            Check(scene.AttachmentFrame(a)==early,"Attachment placement is deterministic after reset and replay");
            viewport.camera=new OrbitCamera{MaxDistance=100000};viewport.camera.Rotate(45,65);
            scene.Frame();await Task.Delay(300);
            using var image=viewport.CaptureBitmap();image.Save(Path.Combine(AppContext.BaseDirectory,"attachment.png"));
            Check(scene.ParticleCount>0&&scene.DrawnParticles>0,"Event-positioned particle systems are present in the captured frame");
            cues=[a with {Lifetime=.3,TemporaryParticle=true}];RebuildScene();clock.Seek(.4);scene.SetTime(.4);await Task.Delay(200);
            using(var alive=viewport.CaptureBitmap())alive.Save(Path.Combine(AppContext.BaseDirectory,"attachment-alive.png"));
            Check(scene.ParticleCount>0,"Temporary attachment emits particles before expiry");
            clock.Seek(.6);scene.SetTime(.6);await Task.Delay(200);
            using(var expired=viewport.CaptureBitmap())expired.Save(Path.Combine(AppContext.BaseDirectory,"attachment-expired.png"));
            Check(scene.ParticleCount==0&&scene.DrawnParticles==0,"Expired native temporary attachment removes all remaining live particles");
            clock.Seek(.4);scene.SetTime(.4);await Task.Delay(200);
            using(var replay=viewport.CaptureBitmap()){}
            Check(scene.ParticleCount>0,"Rewinding before expiry reconstructs the temporary particle effect");
            var muzzleRecord=new GameRecord{Kind="PROJ",Plugin="fixture.esp",Key="fixture.esp|000001"};var muzzleData=new byte[84];muzzleData[0]=8;BitConverter.GetBytes(.3f).CopyTo(muzzleData,44);
            muzzleRecord.Fields.Add(new("DATA",muzzleData));muzzleRecord.Fields.Add(new("NAM1",System.Text.Encoding.UTF8.GetBytes(@"previewer\fountain.nif")));
            cues=[ProjectileVisuals.Muzzle(muzzleRecord,.2,new(0,0,100),0,0)!];RebuildScene();followCamera.Checked=false;scene.FollowScene=false;
            clock.Seek(.4);scene.SetTime(.4);await Task.Delay(200);
            scene.FramingSubject=Scene.FrameSubject.Effects;scene.Frame();await Task.Delay(200);
            using(var muzzle=viewport.CaptureBitmap())muzzle.Save(Path.Combine(AppContext.BaseDirectory,"muzzle-alive.png"));
            Check(scene.ParticleCount>0&&scene.DrawnParticles>0,"ESP muzzle cue renders its authored launch artwork before expiry");
            clock.Seek(.6);scene.SetTime(.6);await Task.Delay(200);
            using(var expiredMuzzle=viewport.CaptureBitmap())expiredMuzzle.Save(Path.Combine(AppContext.BaseDirectory,"muzzle-expired.png"));
            Check(scene.ParticleCount==0&&scene.DrawnParticles==0,"ESP muzzle duration removes every emitted particle after expiry");
            clock.Seek(.4);scene.SetTime(.4);await Task.Delay(200);
            using(var replayMuzzle=viewport.CaptureBitmap())replayMuzzle.Save(Path.Combine(AppContext.BaseDirectory,"muzzle-replay.png"));
            Check(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"muzzle-alive.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"muzzle-replay.png"))),"ESP muzzle render rewinds byte for byte");
            var explosionRecords=new Records();var explosionRecord=new GameRecord{Kind="EXPL",Plugin="fixture.esp",Key="fixture.esp|000002",Id=2};
            explosionRecord.Fields.Add(new("MODL",System.Text.Encoding.UTF8.GetBytes(@"previewer\fountain.nif")));explosionRecords.All[explosionRecord.Key]=explosionRecord;
            muzzleData[0]=6;muzzleData[2]=1;BitConverter.GetBytes(.3f).CopyTo(muzzleData,32);BitConverter.GetBytes(2u).CopyTo(muzzleData,36);
            var timedLaunch=new EffectCue(.2,@"meshes\previewer\fountain.nif",100,0,2,1,new(0,0,100),"Timed flight",ProjectileType:1);
            var timedBlast=ProjectileVisuals.Explosion(muzzleRecord,explosionRecords,timedLaunch,.3)!;
            Check(System.Numerics.Vector3.Distance(scene.EffectPlacement(timedLaunch,.3).Translation,scene.EffectPlacement(timedBlast,0).Translation)<1e-4,"Delayed explosion retains the launch actor frame despite animation root motion");
            cues=[timedBlast];RebuildScene();clock.Seek(.4);scene.SetTime(.4);await Task.Delay(200);
            using(var beforeBlast=viewport.CaptureBitmap()){}
            Check(scene.ParticleCount==0&&scene.DrawnParticles==0,"Timed explosion remains absent before its scheduled activation");
            clock.Seek(.7);scene.SetTime(.7);scene.FramingSubject=Scene.FrameSubject.Effects;scene.Frame();await Task.Delay(200);
            using(var blastImage=viewport.CaptureBitmap())blastImage.Save(Path.Combine(AppContext.BaseDirectory,"timed-explosion.png"));
            Check(scene.ParticleCount>0&&scene.DrawnParticles>0,"Timed explosion artwork emits after its scheduled activation");
            clock.Seek(.4);scene.SetTime(.4);await Task.Delay(100);clock.Seek(.7);scene.SetTime(.7);await Task.Delay(200);
            using(var blastReplay=viewport.CaptureBitmap())blastReplay.Save(Path.Combine(AppContext.BaseDirectory,"timed-explosion-replay.png"));
            Check(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"timed-explosion.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"timed-explosion-replay.png"))),"Timed explosion render rewinds byte for byte");
            double loopWall=0;var loopClock=new PlaybackClock(()=>loopWall){Duration=1,Loop=true};loopClock.Play();
            async Task RenderLoop(double wall,string? capture=null){
                loopWall=wall;clock.Seek(loopClock.Time);scene.SetTime(loopClock.Time);await Task.Delay(200);
                using var loopImage=viewport.CaptureBitmap();if(capture!=null)loopImage.Save(Path.Combine(AppContext.BaseDirectory,capture));
            }
            await RenderLoop(.7,"effect-loop-first.png");
            Check(scene.ParticleCount>0&&scene.DrawnParticles>0,"Loop fixture starts with an active delayed explosion");
            await RenderLoop(1.2,"effect-loop-before-trigger.png");
            Check(scene.ParticleCount==0&&scene.DrawnParticles==0,"Crossing the animation loop clears delayed effect particles before the next trigger");
            await RenderLoop(1.7,"effect-loop-second.png");
            Check(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"effect-loop-first.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"effect-loop-second.png"))),"Delayed effect renders identically at the same phase of the next loop");
            await RenderLoop(5.2);
            Check(scene.ParticleCount==0&&scene.DrawnParticles==0,"A stalled presentation crossing several loops does not leak old effect particles");
            await RenderLoop(5.7,"effect-loop-after-stall.png");
            Check(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"effect-loop-first.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"effect-loop-after-stall.png"))),"Delayed effect phase remains deterministic after several skipped loops");
            cues=[a with {AppliedSpell="fixture|000001",RemovedAt=.5}];RebuildScene();clock.Seek(.4);scene.SetTime(.4);await Task.Delay(200);
            using(var applied=viewport.CaptureBitmap())applied.Save(Path.Combine(AppContext.BaseDirectory,"ability-applied.png"));
            Check(scene.ParticleCount>0&&scene.DrawnParticles>0,"Applied ability particles render before removal");
            clock.Seek(.6);scene.SetTime(.6);await Task.Delay(200);
            using(var removed=viewport.CaptureBitmap())removed.Save(Path.Combine(AppContext.BaseDirectory,"ability-removed.png"));
            Check(scene.ParticleCount==0&&scene.DrawnParticles==0,"Ability removal clears simulated and rendered particles");
            clock.Seek(.4);scene.SetTime(.4);await Task.Delay(200);
            using(var replayAbility=viewport.CaptureBitmap()){}
            Check(scene.ParticleCount>0&&scene.DrawnParticles>0,"Seeking before ability removal reconstructs its visuals");
            string graphAssets=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,@"..\..\..\..\verification\effect-graph\assets"));
            if(Directory.Exists(graphAssets)){
                await assets.Mount(graphAssets,true,new Progress<string>());
                cues=[a with{Model=@"meshes\previewer\sequence-end.nif",FollowActor=true,AppliedSpell="fixture|000002",RemovedAt=1.2,Lifetime=4}];
                RebuildScene();clock.Seek(.9);scene.SetTime(.9);scene.Frame();await Task.Delay(200);
                using(var loopFrame=viewport.CaptureBitmap())loopFrame.Save(Path.Combine(AppContext.BaseDirectory,"sequence-loop.png"));
                Check(scene.BehaviorGraphCount>0&&scene.ParticleCount>0&&scene.DrawnParticles>0,"Mounted project, character, and behavior graph drive the rendered looping effect");
                clock.Seek(1.4);scene.SetTime(1.4);await Task.Delay(200);
                using(var endFrame=viewport.CaptureBitmap())endFrame.Save(Path.Combine(AppContext.BaseDirectory,"sequence-ending.png"));
                Check(scene.ParticleCount>0&&scene.DrawnParticles>0,"Authored ending remains visible after ability removal");
                clock.Seek(1.7);scene.SetTime(1.7);await Task.Delay(200);
                using(var doneFrame=viewport.CaptureBitmap())doneFrame.Save(Path.Combine(AppContext.BaseDirectory,"sequence-finished.png"));
                Check(scene.ParticleCount==0&&scene.DrawnParticles==0,"Authored EndAnim event removes the rendered effect");
                clock.Seek(.9);scene.SetTime(.9);await Task.Delay(200);using(var rewindFrame=viewport.CaptureBitmap()){}
                Check(scene.ParticleCount>0&&scene.DrawnParticles>0,"Rewinding a completed behavior effect restores its loop");
                const string plugin="previewer-trigger-test.esp";
                var spell=new GameRecord{Kind="SPEL",Plugin=plugin,Id=1,Key=plugin+"|000001"};
                var spit=new byte[36];BitConverter.GetBytes(4u).CopyTo(spit,8);
                spell.Fields.Add(new("SPIT",spit));spell.Fields.Add(new("EFID",BitConverter.GetBytes(2u)));spell.Fields.Add(new("EFIT",new byte[12]));
                var magic=new GameRecord{Kind="MGEF",Plugin=plugin,Id=2,Key=plugin+"|000002"};
                var magicData=new byte[152];BitConverter.GetBytes(3u).CopyTo(magicData,96);magic.Fields.Add(new("DATA",magicData));
                var art=new GameRecord{Kind="ARTO",Plugin=plugin,Id=3,Key=plugin+"|000003"};
                art.Fields.Add(new("MODL",System.Text.Encoding.UTF8.GetBytes(@"previewer\sequence-end.nif")));
                foreach(var record in new[]{spell,magic,art})records.All[record.Key]=record;
                clip=null;manualTriggers.Clear();referenceActions.Clear();clock.Seek(.2);
                var press=new Trigger("Fixture press","@APPLYSPELL|1|"+plugin,"in-memory fixture");
                var release=new Trigger("Fixture release","@UNAPPLYSPELL|1|"+plugin,"in-memory fixture");
                triggers.Items.Add(press);triggers.Items.Add(release);triggers.SelectedItem=press;TriggerEffect();clock.Pause();
                clock.Seek(.4);TriggerEffect();clock.Pause();
                Check(cues.Count==1&&double.IsPositiveInfinity(cues[0].Lifetime),"UI repeated presses retain one constant ability effect");
                clock.Seek(1.2);triggers.SelectedItem=release;TriggerEffect();clock.Pause();
                Check(cues.Count==1&&Math.Abs(cues[0].RemovedAt-1.2)<.001,"UI release edits the previously applied effect timeline");
                clock.Seek(1.4);scene.SetTime(1.4);scene.Frame();await Task.Delay(200);
                using(var manualFade=viewport.CaptureBitmap())manualFade.Save(Path.Combine(AppContext.BaseDirectory,"manual-release-ending.png"));
                Check(scene.DrawnParticles>0,"UI key release plays the authored ending");
                clock.Seek(1.7);scene.SetTime(1.7);await Task.Delay(200);
                using(var manualDone=viewport.CaptureBitmap())manualDone.Save(Path.Combine(AppContext.BaseDirectory,"manual-release-finished.png"));
                Check(scene.DrawnParticles==0,"UI key release completes its effect");
                ClearManualTriggers();Check(cues.Count==0&&manualTriggers.Count==0,"Clear manual triggers removes the queued preview actions");
                var reference=new GameRecord{Kind="RFCT",Plugin=plugin,Id=4,Key=plugin+"|000004"};
                var refData=new byte[12];BitConverter.GetBytes(3u).CopyTo(refData,0);reference.Fields.Add(new("DATA",refData));records.All[reference.Key]=reference;
                var refEntry=ReferenceEffects.Resolve(reference,records,assets);
                clip=new AnimationClip{Duration=3};var retainedClip=clip;
                clock.Seek(.2);QueueReferenceEffect(refEntry,false);clock.Pause();
                clock.Seek(1.2);QueueReferenceEffect(refEntry,true);clock.Pause();
                Check(ReferenceEquals(clip,retainedClip)&&cues.Count==1&&cues[0].ArtLifecycle&&Math.Abs(cues[0].RemovedAt-1.2)<1e-9,"Reference-effect play/stop preserves the selected animation and builds its separate art timeline");
                clock.Seek(1.4);scene.SetTime(1.4);scene.Frame();await Task.Delay(200);
                using(var refEnding=viewport.CaptureBitmap())refEnding.Save(Path.Combine(AppContext.BaseDirectory,"reference-timeline-ending.png"));
                Check(scene.DrawnParticles>0,"Reference-effect Stop plays the authored ending even at a fixed world origin");
                clock.Seek(1.7);scene.SetTime(1.7);await Task.Delay(200);
                using(var refFinished=viewport.CaptureBitmap())refFinished.Save(Path.Combine(AppContext.BaseDirectory,"reference-timeline-finished.png"));
                Check(scene.DrawnParticles==0,"Reference-effect authored EndAnim removes the stopped art");
                clock.Seek(.9);scene.SetTime(.9);await Task.Delay(200);
                using(var refRewind=viewport.CaptureBitmap())refRewind.Save(Path.Combine(AppContext.BaseDirectory,"reference-timeline-rewind.png"));
                Check(scene.DrawnParticles>0,"Rewinding before a reference-effect Stop reconstructs its active particles");
                ClearManualTriggers();Check(referenceActions.Count==0&&cues.Count==0,"Clear manual triggers also clears reference-effect timeline actions");
                clip!.Translation=[(0,System.Numerics.Vector3.Zero),(1,new(100,50,0)),(3,new(300,150,0))];
                clip.Rotation=[(0,0),(1,MathF.PI/2),(3,MathF.PI/2)];
                bool previousRootMotion=scene.RootMotion;scene.RootMotion=true;
                clock.Seek(.2);QueueReferenceEffect(refEntry,false,1,new(20,0,0),followCharacter:true);clock.Pause();
                var referenceFollowed=scene.EffectPlacement(cues.Single(),.8).Translation;
                Check(System.Numerics.Vector3.Distance(referenceFollowed,new(100,70,0))<.001,"Following reference target samples absolute animation time and rotates its local offset with AMR yaw");
                scene.RootMotion=false;
                Check(System.Numerics.Vector3.Distance(scene.EffectPlacement(cues.Single(),.8).Translation,new(20,0,0))<.001,"Disabling root motion also disables reference-target motion without removing its local offset");
                bool previousFollowingCamera=followCamera.Checked;followCamera.Checked=false;scene.FollowScene=false;
                scene.RootMotion=true;scene.ResetParticles();clock.Seek(1);scene.SetTime(1);scene.Frame();await Task.Delay(200);
                using(var moving=viewport.CaptureBitmap())moving.Save(Path.Combine(AppContext.BaseDirectory,"reference-following.png"));
                Check(scene.DrawnParticles>0,"Character-following reference effect renders after translated and rotated root motion");
                clock.Seek(1.5);scene.SetTime(1.5);await Task.Delay(200);using(var advanced=viewport.CaptureBitmap()){}
                clock.Seek(1);scene.SetTime(1);await Task.Delay(200);
                using(var replay=viewport.CaptureBitmap())replay.Save(Path.Combine(AppContext.BaseDirectory,"reference-following-replay.png"));
                Check(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"reference-following.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"reference-following-replay.png"))),"Following target rewind reproduces the complete particle-history render exactly");
                ClearManualTriggers();clip.Translation.Clear();clip.Rotation.Clear();scene.RootMotion=previousRootMotion;followCamera.Checked=previousFollowingCamera;
                clock.Seek(.2);QueueReferenceEffect(refEntry,false,1,new(-180,0,0));clock.Pause();
                clock.Seek(.2);QueueReferenceEffect(refEntry,false,2,new(180,0,0),MathF.PI/2);clock.Pause();
                Check(cues.Count==2&&scene.EffectPlacement(cues[0],.5).Translation.X==-180&&scene.EffectPlacement(cues[1],.5).Translation.X==180,"Scene places two reference effects at independent explicit targets");
                clock.Seek(.9);scene.SetTime(.9);scene.Frame();await Task.Delay(200);
                using(var both=viewport.CaptureBitmap())both.Save(Path.Combine(AppContext.BaseDirectory,"reference-targets-both.png"));
                clock.Seek(1.2);QueueReferenceEffect(refEntry,true,1);clock.Pause();
                clock.Seek(1.7);scene.SetTime(1.7);scene.Frame();await Task.Delay(200);
                using(var remaining=viewport.CaptureBitmap())remaining.Save(Path.Combine(AppContext.BaseDirectory,"reference-targets-one.png"));
                Check(scene.DrawnParticles>0&&cues.Count==2&&Math.Abs(cues[0].RemovedAt-1.2)<1e-9&&double.IsPositiveInfinity(cues[1].RemovedAt),"Stopping Target 1 leaves Target 2's authored looping effect visible");
                clock.Seek(1.8);QueueReferenceEffect(refEntry,true,2);clock.Pause();
                clock.Seek(2.3);scene.SetTime(2.3);await Task.Delay(200);using(var ended=viewport.CaptureBitmap()){}
                Check(scene.DrawnParticles==0,"Both explicitly targeted effects finish after their respective stops");
                ClearManualTriggers();
                clip=null;
            }
            clip=null;
            var ballistic=new EffectCue(0,@"meshes\previewer\fountain.nif",240,.35f,1.5,1,new(0,0,350),"Projectile gravity render fixture",ProjectileType:64);
            cues=[ballistic];RebuildScene();clock.Seek(.5);scene.SetTime(.5);scene.Frame();await Task.Delay(200);
            using(var earlyProjectile=viewport.CaptureBitmap())earlyProjectile.Save(Path.Combine(AppContext.BaseDirectory,"projectile-early.png"));
            var projectileBefore=scene.EffectPlacement(ballistic,.5).Translation;
            clock.Seek(1.2);scene.SetTime(1.2);await Task.Delay(200);
            using(var lateProjectile=viewport.CaptureBitmap())lateProjectile.Save(Path.Combine(AppContext.BaseDirectory,"projectile-late.png"));
            var projectileAfter=scene.EffectPlacement(ballistic,1.2).Translation;
            Check(projectileAfter.Y>projectileBefore.Y&&projectileAfter.Z<projectileBefore.Z&&scene.DrawnParticles>0,"Rendered projectile trail follows forward travel and AE-scaled gravity");
            Check(Math.Abs(projectileAfter.Z-(350-.5*.35*ProjectileMotion.GravityUnits*1.2*1.2))<.001,"Scene projectile placement uses verified AE gravity units");
            Check(scene.EffectPlacement(ballistic,2)==scene.EffectPlacement(ballistic,1.5),"Projectile effect origin stops advancing after resolved lifetime");
            var elevated=ballistic with{Elevation=MathF.PI/4};
            cues=[elevated];RebuildScene();clock.Seek(1.2);scene.SetTime(1.2);scene.Frame();await Task.Delay(200);
            using(var elevatedFrame=viewport.CaptureBitmap())elevatedFrame.Save(Path.Combine(AppContext.BaseDirectory,"projectile-elevated.png"));
            var elevatedPlacement=scene.EffectPlacement(elevated,1.2);
            Check(elevatedPlacement.Translation.Z>projectileAfter.Z+100&&elevatedPlacement.Translation.Y<projectileAfter.Y,"Elevated projectile scene placement changes forward and vertical travel");
            var launchDirection=System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitY,scene.EffectPlacement(elevated,0));
            Check(Math.Abs(launchDirection.Y-.7071068)<.001&&Math.Abs(launchDirection.Z-.7071068)<.001,"Projectile NIF and local emitter axes follow the chosen launch angle");
            var flightDirection=System.Numerics.Vector3.Normalize(System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitY,elevatedPlacement));
            Check(flightDirection.Z<0&&flightDirection.Y>0,"Rendered elevated projectile turns downward after passing its apex");
            manualTriggers.Clear();referenceActions.Clear();projectileElevation.Value=35;
            var saved=JsonSerializer.Deserialize<SessionSettings>(File.ReadAllText(settingsPath));
            Check(saved?.ProjectileElevation==35&&Math.Abs(effects.ProjectileElevation-35*MathF.PI/180)<.0001,"Elevation control updates the effect resolver and saved session");
            projectileElevation.Value=0;
            log.Add("Early center: "+early.Value.Translation);log.Add("Late center: "+late.Value.Translation);
            UpdateDiagnostics();log.Add(diagnostics.Text);
        }catch(Exception ex){ShowError(ex);log.Add(ex.ToString());Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"attachment-tests.txt"),log);}finally{Close();}}
    }
    async Task CameraSmoke()
    {
        var log=new List<string>();
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"camera-tests.txt"),log);if(!ok)throw new InvalidDataException(message);}
        async Task Capture(double time,string name){
            clock.Seek(time);scene.SetTime(time);await Task.Delay(200);
            // The background may be a user-configured gradient, so the effect footprint is the difference
            // against the same frame rendered with effects hidden rather than against one corner pixel.
            scene.ShowEffects=false;scene.SetTime(time);using var plain=viewport.CaptureBitmap();scene.ShowEffects=true;scene.SetTime(time);
            using var bitmap=viewport.CaptureBitmap();
            bitmap.Save(Path.Combine(AppContext.BaseDirectory,"camera-"+name+".png"));
            int minX=bitmap.Width,minY=bitmap.Height,maxX=-1,maxY=-1,count=0;
            for(int y=0;y<bitmap.Height;y+=2)for(int x=0;x<bitmap.Width;x+=2){
                var c=bitmap.GetPixel(x,y);var background=plain.GetPixel(x,y);
                if(Math.Abs(c.R-background.R)+Math.Abs(c.G-background.G)+Math.Abs(c.B-background.B)<25)continue;
                minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);count++;
            }
            Check(count>10&&minX>2&&minY>2&&maxX<bitmap.Width-3&&maxY<bitmap.Height-3,
                $"{name}: rendered effect is visible and contained inside the viewport");
            log.Add($"{name}: focus={viewport.camera.FocusPosition}; distance={viewport.camera.distance}; pixels={count}; bounds={minX},{minY}..{maxX},{maxY}");
        }
        try{
            string root=Environment.GetEnvironmentVariable("SSE_CAMERA_TEST_ROOT")??Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,@"..\..\..\..\particle-fixtures"));
            await Mount(root,false);PreviewParticle(Path.Combine(root,@"meshes\previewer\inherit-motion.nif"));clock.Pause();
            await Capture(.2,"early");var early=viewport.camera.FocusPosition;
            await Capture(.5,"middle");await Capture(1,"late");
            Check((viewport.camera.FocusPosition-early).Length>10,"Camera follows the moving effect rather than keeping the startup focus");
            float heldDistance=viewport.camera.distance;await Capture(.2,"rewind");
            Check(viewport.camera.distance>=heldDistance,"Following does not repeatedly zoom inward when an effect shrinks or rewinds");
            viewport.ManualCameraRequested?.Invoke();var heldFocus=viewport.camera.FocusPosition;
            clock.Seek(1);scene.SetTime(1);await Task.Delay(200);using(var ignored=viewport.CaptureBitmap()){}
            Check(followCamera.Checked&&scene.FollowScene&&viewport.camera.FocusPosition==heldFocus,"Manual camera input leaves Follow camera ticked and preserves the chosen focus");
            viewport.Zoom(1000);Check(viewport.camera.distance<=Viewport.ClosestZoom+.001f,$"The camera zooms in to {Viewport.ClosestZoom} units from its focus (it stopped at {viewport.camera.distance:F2})");
            Check(Math.Abs(scene.FloorSize-6144)<.01f,"The grid floor is 6144 units across");
            followCamera.Checked=true;
            Size=new(1150,1000);await Task.Delay(200);scene.Frame();
            await Capture(1,"narrow");
            Check(scene.ParticleCount==90,"Camera following and framing leave particle playback time and count unchanged");
            cameraSubject.SelectedIndex=0;scene.Frame();float allDistance=viewport.camera.distance;
            cameraSubject.SelectedIndex=1;
            Check(Math.Abs(viewport.camera.distance-allDistance)<.01,"Character framing falls back to the complete scene when no character is loaded");
            cameraSubject.SelectedIndex=2;
            Check(scene.FramingSubject==Scene.FrameSubject.Effects&&scene.ParticleCount==90,"Effect framing selects the effect subject without changing playback");
        }catch(Exception ex){ShowError(ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"camera-tests.txt"),log);}finally{Close();}}
    }
    async Task CharacterSmoke()
    {
        try{
            await Mount(SelfTest.TestData,false);
            clock.Pause();clip=null;scene.Clip=null;
            if(Environment.GetEnvironmentVariable("SSE_CHARACTER_TEST_SEX")=="female")sex.SelectedIndex=1;
            var cuirass=records.Of("ARMO").Single(r=>r.EditorId=="ArmorIronCuirass");
            foreach(bool equippedArmor in new[]{false,true}){
                worn.Clear();if(equippedArmor)worn.Add(cuirass);
                RebuildScene();viewport.camera=new OrbitCamera{MaxDistance=100000};viewport.camera.Rotate(45,65);
                scene.SetTime(0);scene.Frame();await Task.Delay(350);
                string state=equippedArmor?"iron":"skin";
                using var image=viewport.CaptureBitmap();image.Save(Path.Combine(AppContext.BaseDirectory,"character-"+state+".png"));
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"character-"+state+".txt"),
                    string.Join("\n",viewport.Model.meshes.Select(m=>m.MeshData.Shape.ShapeName))+"\n\n"+diagnostics.Text);
            }
            // Adding a mod folder must not reset the chosen character: pick a non-default race, weapon and
            // head part, mount the fixture folder as an overlay, and expect the same keys afterwards.
            var keep=new List<string>();void Check(bool ok,string message){keep.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
            var otherRace=records.Of("RACE").First(r=>r.EditorId=="ImperialRace");races.SelectedItem=otherRace;
            var sword=records.Of("WEAP").First(w=>w.EditorId=="IronSword");weapons.SelectedItem=sword;
            int hairIndex=-1;for(int i=0;i<heads.Items.Count;i++)if(!heads.GetItemChecked(i)&&((GameRecord)heads.Items[i]).First("PNAM")?.UInt()==3){hairIndex=i;break;}
            if(hairIndex>=0)heads.SetItemChecked(hairIndex,true);await Task.Delay(200);
            var headKeys=heads.CheckedItems.Cast<GameRecord>().Select(h=>h.Key).OrderBy(k=>k).ToArray();int wornCount=worn.Count;
            await Mount(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,@"..\..\..\..\particle-fixtures")),true);
            keep.Add($"after overlay mount: race {(races.SelectedItem as GameRecord)?.EditorId}, weapon {(weapons.SelectedItem as GameRecord)?.EditorId}, heads {headKeys.Length}, worn {worn.Count}, sources {assets.Roots.Count}");
            Check(assets.Roots.Count==2,"Overlay folder mounted as a second source");
            Check((races.SelectedItem as GameRecord)?.Key==otherRace.Key,"Adding a mod folder keeps the selected race");
            Check((weapons.SelectedItem as GameRecord)?.Key==sword.Key,"Adding a mod folder keeps the selected weapon");
            Check(heads.CheckedItems.Cast<GameRecord>().Select(h=>h.Key).OrderBy(k=>k).SequenceEqual(headKeys),"Adding a mod folder keeps the checked head parts");
            Check(worn.Count==wornCount,"Adding a mod folder keeps worn armor");
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"character-mount-tests.txt"),keep);
        }catch(Exception ex){ShowError(ex);Environment.ExitCode=1;}
        finally{Close();}
    }
    // Renders two effect NIFs for other projects' preview sheets. Arguments: an output folder, the two NIFs (the same
    // file twice is fine) and a Data folder holding their textures. It writes original_sample.png, original_wrapped.png,
    // corrected_sample.png, corrected_wrapped.png and validation.txt, and exits 1 when an effect does not show or its
    // clock does not loop. Environment: SSE_EFFECT_CAPTURE_TIME (seconds, default 0.934), SSE_EFFECT_CAPTURE_DISTANCE
    // (camera distance, default 350), SSE_EFFECT_CAPTURE_FOCUS_ORIGIN=1 (look at 0, 0, 100) and
    // SSE_EFFECT_CAPTURE_AUTOMOUNT=1 (mount the folder above the NIF's meshes folder, not the Data argument). The
    // older SSE_CLOUD_SMOKE_* names and the switch --cloud-effect-alpha-smoke still work.
    static string? CaptureSetting(string name)=>Environment.GetEnvironmentVariable("SSE_EFFECT_CAPTURE_"+name) is {Length:>0} value?value:Environment.GetEnvironmentVariable("SSE_CLOUD_SMOKE_"+name);
    async Task EffectCapture(string folder,string original,string corrected,string dataRoot)
    {
        Directory.CreateDirectory(folder);var log=new List<string>();
        double sampleTime=double.TryParse(CaptureSetting("TIME"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var requestedTime)?requestedTime:.934;
        float cameraDistance=float.TryParse(CaptureSetting("DISTANCE"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var requestedDistance)?requestedDistance:350;
        try{
            Size=new Size(1500,950);
            if(CaptureSetting("AUTOMOUNT")=="1"){
                await EnsureEffectDataRoot(original);
                log.Add("Direct NIF auto-mounted "+string.Join(";",assets.Roots));
            }else await Mount(dataRoot,false);
            foreach(var (label,path) in new[]{("original",original),("corrected",corrected)}){
                PreviewParticle(path);clock.Pause();followCamera.Checked=false;scene.FollowScene=false;
                log.Add(label+": duration "+clock.Duration.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+"; loop "+clock.Loop);
                if(clock.Duration<1||clock.Duration>4)throw new InvalidDataException("Effect clock did not match the authored duration.");
                clock.Seek(sampleTime);scene.SetTime(sampleTime);scene.Frame();log.Add(label+": camera "+viewport.camera.FocusPosition+" distance "+viewport.camera.distance);if(CaptureSetting("FOCUS_ORIGIN")=="1")viewport.camera.FocusPosition=new OpenTK.Mathematics.Vector3(0,0,100);viewport.camera.distance=cameraDistance;viewport.UpdateProjection(true);await Task.Delay(250);
                using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(folder,label+"_sample.png"));
                log.AddRange(scene.Diagnostics.Take(10));
                log.Add(label+": visible meshes "+scene.VisibleEffectMeshes);
                log.AddRange(viewport.Model.meshes.Where(m=>!m.MeshData.Shape.RenderHide).Take(30).Select(m=>label+": visible "+m.MeshData.Shape.ShapeName+" textures "+string.Join(";",m.MeshData.Material.Textures_Path_List)));
                if(scene.VisibleEffectMeshes==0)throw new InvalidDataException(label+": no visible effect shapes.");
                double wrapped=PlaybackClock.Normalize(clock.Duration+sampleTime,clock.Duration,true);
                if(Math.Abs(wrapped-sampleTime)>1e-6)throw new InvalidDataException("Clock failed to wrap at effect duration.");
                clock.Seek(wrapped);scene.SetTime(wrapped);scene.Frame();if(CaptureSetting("FOCUS_ORIGIN")=="1")viewport.camera.FocusPosition=new OpenTK.Mathematics.Vector3(0,0,100);viewport.camera.distance=cameraDistance;viewport.UpdateProjection(true);await Task.Delay(150);
                using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(folder,label+"_wrapped.png"));
                clock.Seek(clock.Duration-.04);clock.Play();await Task.Delay(110);TickPlayback();clock.Pause();
                log.Add(label+": live rollover time "+clock.Time.ToString("F3",System.Globalization.CultureInfo.InvariantCulture));
                if(clock.Time>.3)throw new InvalidDataException("Live effect preview did not loop.");
            }
        }catch(Exception ex){log.Add("FAIL "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(folder,"validation.txt"),log);}finally{Close();}}
    }
    async Task CloudEquipmentSmoke()
    {
        var log=new List<string>();void Check(bool value,string message){log.Add((value?"PASS ":"FAIL ")+message);if(!value)throw new InvalidDataException(message);}
        try{
            pluginsFile=null;
            await Mount(SelfTest.GameData,false);
            await Mount(Path.Combine(SelfTest.Mods,"Just4u.FF7RE_Cloud_Strife-SE (1)"),true);
            await Mount(Path.Combine(SelfTest.Mods,"FF7 Cloud Animations"),true);
            Check(!records.Of("RACE").Any(r=>r.Plugin.StartsWith("Just4u.",StringComparison.OrdinalIgnoreCase)),"Cloud equipment plugin has no RACE records to populate");
            await SelectClip(assets.Clips.First(c=>c.Path.EndsWith(@"9901101\mco_attack2.hkx",StringComparison.OrdinalIgnoreCase)));
            clock.Pause();clock.Seek(.5);scene.SetTime(.5);followCamera.Checked=false;scene.FollowScene=false;
            weapons.SelectedItem=records.Of("WEAP").Single(w=>w.EditorId=="FF7REBusterSword2H");
            await Task.Delay(250);using(var first=viewport.CaptureBitmap()){}
            var weaponBefore=scene.WeaponTransforms;
            Check(weaponBefore.Length>0,"Selecting the Buster Sword alone creates weapon render meshes");
            RebuildScene();clock.Seek(.5);scene.SetTime(.5);await Task.Delay(250);using(var rebuilt=viewport.CaptureBitmap()){}
            Check(weaponBefore.SequenceEqual(scene.WeaponTransforms),"Weapon attachment is identical before and after an unrelated scene rebuild");
            scene.FramingSubject=Scene.FrameSubject.Character;scene.Frame();await Task.Delay(150);
            using(var weaponImage=viewport.CaptureBitmap())weaponImage.Save(Path.Combine(AppContext.BaseDirectory,"cloud-weapon-only.png"));
            var cloud=records.Of("ARMO").Single(r=>r.EditorId=="CloudArmorAlternative");var race=(GameRecord)races.SelectedItem!;
            var paths=records.ArmorModels(cloud,race,false).ToArray();
            Check(paths.Length>0&&paths.All(assets.Exists)&&paths.All(p=>!p.Contains(@"meshes\meshes\",StringComparison.OrdinalIgnoreCase)),"Actual Cloud armor paths resolve without a duplicate meshes prefix");
            worn.Add(cloud);RebuildScene();clock.Seek(.5);scene.SetTime(.5);await Task.Delay(250);
            Check(!scene.Diagnostics.Any(d=>d.StartsWith("Missing model:")),"Equipped Cloud outfit loads without missing-model diagnostics");
            Check(viewport.Model.meshes.Count(m=>m.MeshData.Shape.IsSkinned&&!m.MeshData.Shape.RenderHide)>4,"Cloud armor contributes visible skinned geometry");
            Check(weaponBefore.SequenceEqual(scene.WeaponTransforms),"Equipping Cloud armor does not change the weapon attachment at the same pose");
            scene.Frame();await Task.Delay(150);using(var armorImage=viewport.CaptureBitmap())armorImage.Save(Path.Combine(AppContext.BaseDirectory,"cloud-equipped.png"));
            await Mount(Path.Combine(SelfTest.Mods,"SC_FF7R_Cloud"),true);
            var cloudRace=records.Of("RACE").Single(r=>r.EditorId=="SC_FF7R_Cloud_Race");
            Check(races.Items.Cast<GameRecord>().Any(r=>r.Key==cloudRace.Key),"SC_FF7R_Cloud race appears after mounting its mod folder");
            var cloudHair=records.DefaultHeadParts(cloudRace,false).Single(h=>h.First("PNAM")?.UInt()==3);
            var cloudColor=records.HeadHairColor(cloudHair,cloudRace,false);
            var allowedColor=records.Resolve(cloudRace,cloudRace.First("AHCM")!.UInt())!;
            var rgb=allowedColor.First("CNAM")!.Bytes;
            Check(cloudColor.HasValue&&cloudColor.Value.R==rgb[0]&&cloudColor.Value.G==rgb[1]&&cloudColor.Value.B==rgb[2],"Cloud uses its sole allowed plugin hair color when the default link is empty");
            log.Add($"Cloud hair: {allowedColor.EditorId}, RGB {rgb[0]}, {rgb[1]}, {rgb[2]}; skin: {records.RaceSkinColor(cloudRace,false)}");
            Check(records.RaceSkinColor(cloudRace,false)!=null,"Cloud race skin-tone preset resolves from its male tint section");
            races.SelectedItem=cloudRace;clock.Pause();clock.Seek(.5);scene.SetTime(.5);scene.Frame();await Task.Delay(200);
            using(var raceImage=viewport.CaptureBitmap())raceImage.Save(Path.Combine(AppContext.BaseDirectory,"cloud-custom-race.png"));
            weapons.SelectedItem=records.Of("WEAP").Single(w=>w.EditorId=="FF7REBusterSword2H");
            assets.Drop(assets.Source(selected!.Path));
            clock.Pause();clock.Seek(.65);fps.Value=24;speed.Value=.75m;loop.Checked=false;rootMotion.Checked=false;
            var saved=CaptureSettings();string presetPath=Path.Combine(AppContext.BaseDirectory,"cloud-verification.ssepreview.json");
            File.WriteAllText(presetPath,JsonSerializer.Serialize(saved));
            ResetPreview(false);
            Check(assets.Roots.Count==0&&records.All.Count==0&&races.Items.Count==0&&worn.Count==0&&scene.WeaponTransforms.Length==0,"Reset clears sources, records, character and equipment");
            int responsiveTicks=0;using var responseTimer=new System.Windows.Forms.Timer{Interval=20};responseTimer.Tick+=(_,_)=>responsiveTicks++;responseTimer.Start();
            var loadWatch=Stopwatch.StartNew();await ApplySettings(JsonSerializer.Deserialize<SessionSettings>(File.ReadAllText(presetPath))!);responseTimer.Stop();
            Check(responsiveTicks>5,"Scene source loading and mesh preparation keep the UI message loop responsive");log.Add($"Scene load: {loadWatch.Elapsed.TotalSeconds:F2}s; UI ticks: {responsiveTicks}");
            var restored=CaptureSettings();
            Check(saved.Dropped!.SequenceEqual(restored.Dropped!)&&restored.Dropped!.All(p=>assets.Clips.Any(c=>c.Path.Equals(p,StringComparison.OrdinalIgnoreCase))),"Scene restores externally loaded animations in the library");
            Check(Math.Abs(restored.Time-saved.Time)<.0001&&restored.Fps==24&&restored.Speed==.75&&!restored.Loop&&!restored.RootMotion,"Scene restores animation time and playback controls");
            string missing=Path.Combine(AppContext.BaseDirectory,"missing-scene-root");int prompts=0;
            var repair=RepairScenePaths(saved with {Roots=[missing],Dropped=[Path.Combine(missing,"cloud-verification.ssepreview.json")],Clip=Path.Combine(missing,"cloud-verification.ssepreview.json")},(path,folder)=>{prompts++;return folder?AppContext.BaseDirectory:null;});
            Check(repair!=null&&prompts==1&&repair.Dropped![0]==presetPath&&repair.Clip==presetPath,"Replacing a missing root remaps dependent files without repeated prompts");
            Check(RepairScenePaths(saved with {Roots=[missing]},(_,_)=>null)==null&&CaptureSettings().Race==restored.Race,"Cancelling missing-path repair preserves the active scene");
            var skipped=RepairScenePaths(saved with {Roots=[missing],Dropped=[],Clip=null},(_,_)=>"");
            Check(skipped!=null&&skipped.Roots.Length==0,"Missing source can be explicitly skipped");
            Check(saved.Roots.SequenceEqual(restored.Roots)&&saved.Plugins==restored.Plugins,"Preset restores mounted folders in their original override order and load order");
            Check(saved.Race==restored.Race&&saved.Sex==restored.Sex&&saved.Heads!.SequenceEqual(restored.Heads!),"Preset restores custom race, sex and selected head parts");
            Check(saved.Armor!.SequenceEqual(restored.Armor!)&&saved.Weapon==restored.Weapon&&saved.Clip==restored.Clip&&!clock.Playing,"Preset restores armor, weapon and animation selection with playback paused");
            clock.Seek(.5);scene.SetTime(.5);scene.Frame();await Task.Delay(250);
            using(var presetImage=viewport.CaptureBitmap())presetImage.Save(Path.Combine(AppContext.BaseDirectory,"cloud-preset-restored.png"));

            clock.Play();BeginScrub();ScrubTo(2500);
            Check(!clock.Playing&&Math.Abs(scene.Time-clock.Duration*.25)<.0001,"Scrubbing updates the selected animation pose immediately while playback is paused");
            EndScrub();Check(clock.Playing,"Releasing a playing animation resumes playback");clock.Pause();BeginScrub();ScrubTo(10000);EndScrub();TickPlayback();
            Check(!clock.Playing&&Math.Abs(scene.Time-clock.Duration)<.0001,"Paused scrubbing reaches and holds the final frame with looping enabled or disabled");
            ScrubTo(2500);scene.Frame();await Task.Delay(200);
            foreach(var size in new[]{new Size(1580,1000),new Size(1200,800)}){
                Size=size;await Task.Delay(250);
                Check(timeline.Parent==viewport.Parent!.Controls.OfType<TableLayoutPanel>().Single()&&timeline.PointToScreen(Point.Empty).Y>=viewport.PointToScreen(Point.Empty).Y+viewport.Height,"Timeline is directly beneath the viewport at width "+size.Width);
                using var ui=new Bitmap(Width,Height);DrawToBitmap(ui,new Rectangle(Point.Empty,Size));using(var draw=Graphics.FromImage(ui))using(var rendered=viewport.CaptureBitmap()){draw.DrawImage(rendered,new Rectangle(new Point(viewport.PointToScreen(Point.Empty).X-Left,viewport.PointToScreen(Point.Empty).Y-Top),viewport.Size));}ui.Save(Path.Combine(AppContext.BaseDirectory,$"responsive-ui-{size.Width}.png"));
            }
            var tintedHair=viewport.Model.meshes.Where(m=>m.MeshData.Shape.NifShader?.IsTypeHairTint==true).ToArray();
            Check(tintedHair.Length>0&&tintedHair.All(m=>m.MeshData.Material.MaterialBase.TintColorScale==2&&m.MeshData.Material.MaterialBase.HairTintColor.ToArgb()==cloudColor!.Value.ToArgb()),"Cloud hair shader receives plugin platinum-blonde color with Skyrim double tint scale");
            using(var colorImage=viewport.CaptureBitmap())colorImage.Save(Path.Combine(AppContext.BaseDirectory,"cloud-colors.png"));
            Size=new Size(1580,1000);gridFloor.Checked=true;followCamera.Checked=false;scene.Frame();viewport.camera.Rotate(25,25);
            int floorChanges=0;
            foreach(double phase in new[]{.25,.75}){
                clock.Seek(clock.Duration*phase);scene.SetTime(clock.Time);
                var lighting=Config_App.Current.ActiveLights();lighting.ShadowOnGround=false;Config_App.Current.SetActiveLights(lighting);viewport.InvalidateRender();await Task.Delay(150);
                using var unshadowed=viewport.CaptureBitmap();
                lighting.ShadowOnGround=true;Config_App.Current.SetActiveLights(lighting);viewport.InvalidateRender();await Task.Delay(150);
                using var shadowed=viewport.CaptureBitmap();int changed=0;
                for(int y=0;y<unshadowed.Height;y+=2)for(int x=0;x<unshadowed.Width;x+=2){var a=unshadowed.GetPixel(x,y);var b=shadowed.GetPixel(x,y);if(a.R>25&&a.G>25&&a.B>25&&(a.R+a.G+a.B)-(b.R+b.G+b.B)>12)changed++;}
                Check(changed>20,"Grid receives the character/equipment shadow at animation phase "+phase+" (darkened floor samples: "+changed+")");floorChanges+=changed;
                unshadowed.Save(Path.Combine(AppContext.BaseDirectory,$"grid-unshadowed-{phase}.png"));shadowed.Save(Path.Combine(AppContext.BaseDirectory,$"grid-shadowed-{phase}.png"));
            }
            gridFloor.Checked=false;await Task.Delay(100);using(var hidden=viewport.CaptureBitmap()){}
            Check(!Config_App.Current.ActiveLights().ShadowOnGround&&scene.FloorDrawn==0,"Hiding the grid also disables its shadow receiver");

            log.AddRange(scene.Diagnostics);
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"cloud-equipment-tests.txt"),log);}finally{Close();}}
    }
    async Task LibrarySmoke()
    {
        try{
            string root=Environment.GetEnvironmentVariable("SSE_LIBRARY_TEST_ROOT")??Path.Combine(SelfTest.Mods,"Simple MCO Weaponart Casting - Vanilla Spells");
            await assets.Mount(root,false,new Progress<string>());
            BuildTree();search.Text=Environment.GetEnvironmentVariable("SSE_LIBRARY_TEST_SEARCH")??"Fireball";
            tree.ExpandAll();
            IEnumerable<TreeNode> Nodes(TreeNodeCollection nodes){foreach(TreeNode n in nodes){yield return n;foreach(var child in Nodes(n.Nodes))yield return child;}}
            var group=Nodes(tree.Nodes).LastOrDefault(n=>n.Tag is string);
            if(group!=null){tree.SelectedNode=group;group.EnsureVisible();}
            await Task.Delay(200);
            using(var bitmap=new Bitmap(tree.Width,tree.Height)){tree.DrawToBitmap(bitmap,tree.ClientRectangle);bitmap.Save(Path.Combine(AppContext.BaseDirectory,"library-tree.png"));}
            using(var bitmap=new Bitmap(detail.Width,detail.Height)){detail.DrawToBitmap(bitmap,detail.ClientRectangle);bitmap.Save(Path.Combine(AppContext.BaseDirectory,"library-config.png"));}
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"library-selection.txt"),detail.Text);
            using var inspector=CreateInspector("Configuration & source",detail.Text);inspector.Show(this);await Task.Delay(100);
            using(var bitmap=new Bitmap(inspector.Width,inspector.Height)){inspector.DrawToBitmap(bitmap,new Rectangle(Point.Empty,inspector.Size));bitmap.Save(Path.Combine(AppContext.BaseDirectory,"library-reader.png"));}
            inspector.Close();
        }catch(Exception ex){ShowError(ex);Environment.ExitCode=1;}
        finally{Close();}
    }
    async Task BladeBeamSmoke()
    {
        var log=new List<string>();
        try{
            await Mount(SelfTest.GameData,false);await Mount(Path.Combine(SelfTest.Mods,"Anchor MCO Animations v2 - Cloud Fenrir"),true);
            var entry=assets.Clips.Single(c=>c.Path.EndsWith(@"9901106\mco_powerattack1.hkx",StringComparison.OrdinalIgnoreCase));await SelectClip(entry);clock.Pause();
            log.Add(entry.Path);log.AddRange(cues.Select(c=>$"{c.Time:F6}: {c.Model} | {c.Origin}"));log.AddRange(effects.Diagnostics);
            foreach(double time in new[]{.2,.65,1d,1.6}){
                clock.Seek(time);scene.SetTime(time);await Task.Delay(250);scene.Frame();await Task.Delay(250);
                using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,$"bladebeam-{time:0.00}.png"));
                log.Add($"{time:F2}s: systems={scene.ParticleSystems}, live={scene.ParticleCount}, drawn={scene.DrawnParticles}, camera={viewport.camera.distance}, subject={scene.FramingSubject}");
            }
            log.AddRange(scene.Diagnostics);
            if(!cues.Any(c=>c.Model.EndsWith(@"FF7Fenrir\Fenrir_BladeBeam.nif",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Real Blade Beam annotation did not resolve its separate hit art");
            var attached=scene.ParticleInstances.Select(p=>(System:p,Target:NamedNodeAttachment.Read(p.Nif,out _))).Where(p=>p.Target!=null).GroupBy(p=>p.Target).Select(g=>g.First()).ToArray();
            if(attached.Length!=2)throw new InvalidDataException("Blade Beam did not expose both authored attachments");
            var displayedPose=scene.Skeleton.Pose;double attachmentAge=.2-cues.First(c=>c.Model.EndsWith("BladeBeam_StartFX.nif",StringComparison.OrdinalIgnoreCase)).Time;
            var locations=attached.Select(p=>p.System.Placement(attachmentAge).Translation).ToArray();
            if(System.Numerics.Vector3.Distance(locations[0],locations[1])<1)throw new InvalidDataException("Left/right startup effects collapsed to one root placement");
            if(System.Numerics.Vector3.Distance(attached[0].System.Placement(.4).Translation,locations[0])<1)throw new InvalidDataException("Named attachment did not follow the animated pose");
            if(!ReferenceEquals(displayedPose,scene.Skeleton.Pose))throw new InvalidDataException("Historical named attachment sampling replaced the displayed pose");
            followCamera.Checked=false;scene.FollowScene=false;clock.Seek(.2);scene.SetTime(.2);scene.Frame();await Task.Delay(250);
            using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"bladebeam-attachment-a.png"));
            clock.Seek(.8);scene.SetTime(.8);await Task.Delay(100);clock.Seek(.2);scene.SetTime(.2);await Task.Delay(250);
            using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"bladebeam-attachment-b.png"));
            if(!File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"bladebeam-attachment-a.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"bladebeam-attachment-b.png"))))throw new InvalidDataException("Named attachment rewind did not reproduce the rendered image");
            log.Add("PASS Distinct named hand attachments follow historical animation poses, preserve the displayed pose, and rewind byte-identically.");
            cues=cues.Where(c=>c.Model.Contains("BladeBeam_StartFX",StringComparison.OrdinalIgnoreCase)).Select(c=>c with{RemovedAt=.5}).ToList();RebuildScene();clock.Duration=4;clock.Loop=false;
            clock.Seek(.3);scene.SetTime(.3);await Task.Delay(250);
            using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"bladebeam-before-removal.png"));
            if(scene.ParticleCount==0||scene.DrawnParticles==0)throw new InvalidDataException("Attached startup particles were absent before removal");
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"bladebeam-size-audit.txt"),
                viewport.Model.meshes.Select(m=>$"Mesh {m.MeshData.Shape.ShapeName}: hidden={m.MeshData.Shape.RenderHide}, skinned={m.MeshData.Shape.IsSkinned}, bones={m.MeshData.Shape.ShapeBones.Count}, local min={m.MeshData.Meshgeometry.Minv}, max={m.MeshData.Meshgeometry.Maxv}, transform={m.MeshData.Transform}")
                .Concat(scene.ParticleInstances.Select(p=>$"Particles {p.Name}: worldSpace={p.WorldSpace}, count={p.Simulation.Particles.Count}, maxRadius={p.Simulation.Particles.Select(x=>x.Radius).DefaultIfEmpty().Max()}, boundsMin={p.Simulation.Particles.Select(x=>x.Position).Aggregate(new System.Numerics.Vector3(float.MaxValue),System.Numerics.Vector3.Min)}, boundsMax={p.Simulation.Particles.Select(x=>x.Position).Aggregate(new System.Numerics.Vector3(float.MinValue),System.Numerics.Vector3.Max)}")));

            int trailBones=0;float maxTrailError=0;
            foreach(var mesh in viewport.Model.meshes.Where(m=>m.MeshData.Shape.ShapeName.StartsWith("Trail")&&m.MeshData.Shape.IsSkinned)){
                var shape=mesh.MeshData.Shape;
                for(int i=0;i<shape.ShapeBones.Count;i++){
                    var expected=Transform_Class.GetGlobalTransform(shape.ShapeBones[i],shape.NifContent).ComposeTransforms(shape.ShapeBoneTransforms[i]).ToMatrix4();
                    var actual=mesh.MeshData.Meshgeometry.GPUBoneMatrices[i];
                    maxTrailError=Math.Max(maxTrailError,(expected.Row0-actual.Row0).Length+(expected.Row1-actual.Row1).Length+(expected.Row2-actual.Row2).Length+(expected.Row3-actual.Row3).Length);trailBones++;
                }
            }
            float liveTrailError=0;int liveFrames=0;
            void AuditLiveTrail(){
                liveFrames++;
                foreach(var mesh in viewport.Model.meshes.Where(m=>m.MeshData.Shape.ShapeName.StartsWith("Trail")&&m.MeshData.Shape.IsSkinned&&!m.MeshData.Shape.RenderHide)){
                    var shape=mesh.MeshData.Shape;
                    for(int i=0;i<shape.ShapeBones.Count;i++){
                        var expected=Transform_Class.GetGlobalTransform(shape.ShapeBones[i],shape.NifContent).ComposeTransforms(shape.ShapeBoneTransforms[i]).ToMatrix4();var actual=mesh.MeshData.Meshgeometry.GPUBoneMatrices[i];
                        liveTrailError=Math.Max(liveTrailError,(expected.Row0-actual.Row0).Length+(expected.Row1-actual.Row1).Length+(expected.Row2-actual.Row2).Length+(expected.Row3-actual.Row3).Length);
                    }
                }
            }
            viewport.BeforeSceneFrame+=AuditLiveTrail;
            try{clock.Play();await Task.Delay(450);clock.Pause();}finally{viewport.BeforeSceneFrame-=AuditLiveTrail;clock.Pause();}
            log.Add($"Live trail skinning: frames={liveFrames}, maximum matrix error={liveTrailError}");
            if(liveFrames<3||liveTrailError>.001f)throw new InvalidDataException("Live effect rendering uses stale trail bone matrices");
            log.Add($"Trail skinning audit: bones={trailBones}, maximum matrix error={maxTrailError}");
            if(trailBones==0||maxTrailError>.001f)throw new InvalidDataException("Rendered trail bones do not match the current authored NIF hierarchy");
            clock.Seek(.6);scene.SetTime(.6);await Task.Delay(250);
            using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"bladebeam-removal-ending.png"));
            if(!scene.ParticleInstances.All(p=>p.Animation.Phase(.6-cues[0].Time) is {Sequence:"mIdle",Finished:false}))throw new InvalidDataException("Named effect removal did not enter its authored mIdle ending state");
            log.Add("PASS Stop at .5 seconds enters the real startup art's authored mIdle state at .6 seconds.");
            clock.Seek(3.6);scene.SetTime(3.6);await Task.Delay(250);
            using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"bladebeam-after-removal.png"));
            if(scene.ParticleCount!=0||scene.DrawnParticles!=0)throw new InvalidDataException("Removed named attachments survived the three-second art teardown timeout");
            clock.Seek(.3);scene.SetTime(.3);await Task.Delay(250);
            using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"bladebeam-removal-rewind.png"));
            if(!File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"bladebeam-before-removal.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"bladebeam-removal-rewind.png"))))throw new InvalidDataException("Rewinding removed named attachments did not restore their exact render");
            log.Add("PASS Real named startup attachments clear all simulated/rendered particles by the three-second teardown timeout and restore byte-identically on rewind.");
            SaveSession();var cameraSaved=JsonSerializer.Deserialize<SessionSettings>(File.ReadAllText(settingsPath));
            if(cameraSaved?.CameraSubject!=1||scene.FramingSubject!=Scene.FrameSubject.Character)throw new InvalidDataException("Character framing default was not applied and saved");
            log.Add("PASS Character framing is the default and is retained in session settings.");
            log.Add("PASS Real annotation/record chain resolves Blade Beam hit art separately from its empty cone-projectile model. Renders require visual review; this does not prove native projectile or script behavior.");
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"bladebeam-smoke.txt"),log);}finally{Close();}}
    }
    // Profiles any library clip under the live Portable session: per-frame timings, particle counts,
    // per-system statistics and renders at a set of times. Used to chase runaway effects.
    // Plays every library clip from one mounted source with the real playback timer and records what a user
    // would see: presented frames, distinct scene times, worst frame interval, particle peaks and diagnostics.
    // Tool modes read the live session: an explicit SSE_SESSION_FILE, the session beside this exe (Portable), or the
    // source tree's Portable folder when run from bin.
    // weaponDraw / weaponSheathe annotations move the weapon between its holster node and the hand at their times.
    // Also records how the window behaves in background mode, since this is the first suite written to run there.
    async Task HolsterSmoke(string equipClip,string unequipClip,string plainClip)
    {
        var log=new List<string>();
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        try{
            await Mount(SelfTest.TestData,false);
            var blade=records.Of("WEAP").First(w=>w.EditorId=="IronGreatsword");weapons.SelectedItem=blade;string holster=Records.WeaponHolsterNode(blade)!;
            followCamera.Checked=false;scene.FollowScene=false;
            async Task<(string Bone,OpenTK.Mathematics.Matrix4 Transform)> At(double time,string? capture=null){
                clock.Pause();clock.Seek(time);scene.SetTime(time);viewport.InvalidateRender();await Task.Delay(120);
                using var image=viewport.CaptureBitmap();if(capture!=null)image.Save(Path.Combine(AppContext.BaseDirectory,"holster-"+capture+".png"));
                return (scene.WeaponBoneName,scene.WeaponTransforms[0]);
            }
            sheathed.Checked=false;DropFiles([equipClip]);await Task.Delay(400);
            var draw=clip!.WeaponEvents.Single();
            Check(!draw.Sheathed&&scene.ClipDrivesWeapon,$"Equip clip carries weaponDraw at {draw.Time:F3} s");
            var before=await At(draw.Time-.1,"equip-before-draw");var after=await At(draw.Time+.2,"equip-after-draw");
            Check(before.Bone==holster&&after.Bone=="Weapon","Before weaponDraw the weapon hangs on "+holster+"; from the annotation on it is in the hand");
            Check((before.Transform.Row3-after.Transform.Row3).Length>5,"The weapon mesh moves between the holster and hand transforms across the annotation");
            var exact=await At(draw.Time);Check(exact.Bone=="Weapon","The switch happens on the annotation's own frame");
            Check(!sheathed.Enabled&&sheathed.Text.Contains("weaponDraw")&&sheathed.Text.Contains(holster),"The holster toggle is locked and names the annotation while a clip drives the weapon: "+sheathed.Text);
            sheathed.Checked=true;var forced=await At(draw.Time+.2);sheathed.Checked=false;
            Check(forced.Bone=="Weapon","The manual toggle cannot override a clip's own draw state");
            var rewound=await At(draw.Time-.1);Check(rewound.Bone==holster&&rewound.Transform==before.Transform,"Seeking back before the annotation returns the weapon to the holster exactly");
            Check(events.Text.Contains("WEAPON ATTACHMENT")&&events.Text.Contains("weaponDraw: moves to the hand"),"Annotations panel lists the weapon attachment schedule");
            DropFiles([unequipClip]);await Task.Delay(400);
            var sheathe=clip!.WeaponEvents.Single();
            Check(sheathe.Sheathed,$"Unequip clip carries weaponSheathe at {sheathe.Time:F3} s");
            var held=await At(sheathe.Time-.2,"unequip-before-sheathe");var stowed=await At(sheathe.Time+.1,"unequip-after-sheathe");
            Check(held.Bone=="Weapon"&&stowed.Bone==holster,"Before weaponSheathe the weapon is in the hand; afterwards it hangs on "+holster);
            DropFiles([plainClip]);await Task.Delay(400);
            Check(!scene.ClipDrivesWeapon&&sheathed.Enabled&&!sheathed.Text.Contains("set by this clip"),"A clip without either annotation returns control to the manual toggle");
            var manualHand=await At(.2);sheathed.Checked=true;var manualHolster=await At(.2);sheathed.Checked=false;
            Check(manualHand.Bone=="Weapon"&&manualHolster.Bone==holster,"The manual toggle still works for clips without the annotations");
            log.Add($"window: background={Background}; visible={Visible}; location={Location}; in taskbar={ShowInTaskbar}; active={ReferenceEquals(Form.ActiveForm,this)}; foreground={GetForegroundWindow()==Handle}");
            if(Background){
                using var shot=new Bitmap(Path.Combine(AppContext.BaseDirectory,"holster-equip-after-draw.png"));
                int distinct=Enumerable.Range(0,400).Select(i=>shot.GetPixel(i*shot.Width/400,(i*37)%shot.Height).ToArgb()).Distinct().Count();
                Check(Visible&&!ShowInTaskbar&&Location.X<-10000&&!ReferenceEquals(Form.ActiveForm,this)&&GetForegroundWindow()!=Handle,"Background window is off-screen, absent from the taskbar and never becomes the foreground window");
                Check(distinct>40,$"Background renders are real frames ({distinct} distinct colours across 400 samples)");
            }
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"holster-tests.txt"),log);}finally{Close();}}
    }
    // Equipment panel: weight survives rebuilds and mounts, the All slot, the kept armor filter, slot names.
    async Task EquipmentSmoke(string[] folders)
    {
        var log=new List<string>();string output=Path.Combine(AppContext.BaseDirectory,"equipment-smoke");Directory.CreateDirectory(output);
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        try{
            // SSE_EQUIP_SESSION=1 starts from the live session (its mods, race and armor) instead of the vanilla test data.
            bool live=Environment.GetEnvironmentVariable("SSE_EQUIP_SESSION")=="1";
            if(live)await ApplySettings(JsonSerializer.Deserialize<SessionSettings>(File.ReadAllText(ToolSessionPath()))! with {Queue=[],QueuePlaying=false,SmpPhysics=Environment.GetEnvironmentVariable("SSE_EQUIP_PHYSICS")=="1"});
            else await Mount(SelfTest.TestData,false);
            followCamera.Checked=false;scene.FollowScene=false;
            log.Add($"start: weight box {weight.Value}, scene {scene.Weight}, {scene.WeightedShapes} blended shapes, worn {string.Join(", ",worn.Select(w=>w.EditorId))}");
            foreach(string d in scene.Diagnostics.Where(d=>d.Contains("weight",StringComparison.OrdinalIgnoreCase)))log.Add("diagnostic: "+d);
            foreach(var m in viewport.Model.meshes.Where(m=>!scene.MeshIsWeapon(m.MeshData.Shape)&&m.MeshData.Shape.IsSkinned&&!m.MeshData.Shape.RenderHide))log.Add($"mesh {m.MeshData.Shape.ShapeName} ({m.MeshData.Shape.Geometry.VertexCount} verts) blended={scene.IsWeighted(m.MeshData.Shape)}");
            if(!live){var cuirass=records.Of("ARMO").First(r=>r.EditorId=="ArmorIronCuirass");worn.Clear();worn.Add(cuirass);RebuildScene();await Task.Delay(300);}
            // Vertex extent of each weighted shape, by name, as the renderer has it.
            Dictionary<string,double> Extents(){var result=new Dictionary<string,double>();foreach(var m in viewport.Model.meshes.Where(m=>!scene.MeshIsWeapon(m.MeshData.Shape))){var v=m.MeshData.Meshgeometry.NifLocalVertices;if(v==null||v.Length==0)continue;double lo=v.Min(p=>p.X),hi=v.Max(p=>p.X);result[m.MeshData.Shape.ShapeName+"#"+v.Length]=hi-lo;}return result;}
            int shot=0;
            async Task<bool> AtLight(string step){
                if(clip!=null){clock.Play();await Task.Delay(600);}
                clock.Pause();scene.SetTime(clock.Time);viewport.InvalidateRender();await Task.Delay(300);
                var now=Extents();using var before=viewport.CaptureBitmap();before.Save(Path.Combine(output,$"{++shot}-{step}-as-shown.png"));
                weight.Value=1;await Task.Delay(250);weight.Value=0;await Task.Delay(300);viewport.InvalidateRender();await Task.Delay(200);
                var again=Extents();using var after=viewport.CaptureBitmap();after.Save(Path.Combine(output,$"{shot}-{step}-reapplied.png"));
                var off=now.Keys.Where(k=>again.TryGetValue(k,out var x)&&Math.Abs(x-now[k])>.01).ToList();
                int differ=0,total=0;for(int y=0;y<before.Height;y+=3)for(int x=0;x<before.Width;x+=3){total++;if(before.GetPixel(x,y)!=after.GetPixel(x,y))differ++;}
                log.Add($"{step}: weight box {weight.Value}, scene {scene.Weight}, {scene.WeightedShapes} blended shapes, shapes at the wrong weight {off.Count} ({string.Join(", ",off.Select(k=>$"{k} {now[k]:F2} should be {again[k]:F2}"))}); pixels changed by re-applying {differ} of {total}");
                // Cloth moves between captures under HDT-SMP, so with physics on only the mesh data is compared.
                // SSE_EQUIP_PLAY=1 keeps the clip playing between checks, so rebuilds land between playback ticks.
                if(Environment.GetEnvironmentVariable("SSE_EQUIP_PLAY")=="1"&&clip!=null){clock.Play();await Task.Delay(400);}
                return off.Count==0&&(scene.PhysicsActive||differ<=total/500);
            }
            if(live)Check(await AtLight("session load"),"The scene loaded from the session is at the saved weight");
            int Changed(Bitmap a,Bitmap b){int n=0;for(int y=0;y<a.Height;y+=3)for(int x=0;x<a.Width;x+=3)if(a.GetPixel(x,y)!=b.GetPixel(x,y))n++;return n;}
            weight.Value=100;await Task.Delay(300);viewport.InvalidateRender();await Task.Delay(200);var heavy=Extents();using var heavyShot=viewport.CaptureBitmap();
            weight.Value=0;await Task.Delay(300);viewport.InvalidateRender();await Task.Delay(200);var light=Extents();using var lightShot=viewport.CaptureBitmap();
            int control=Changed(heavyShot,lightShot);log.Add($"control: weight 100 against weight 0 changes {control} sampled pixels");
            Check(control>40,"The capture can see a weight change, so an unchanged capture means the weight was right");
            var weighted=heavy.Keys.Where(k=>light.ContainsKey(k)&&Math.Abs(heavy[k]-light[k])>.01).ToList();
            log.Add($"{weighted.Count} weighted shape(s): {string.Join(", ",weighted.Select(k=>$"{k} {heavy[k]:F2}->{light[k]:F2}"))}");
            Check(weighted.Count>0,"Weight 0 changes the weighted meshes");
            // A mesh shown at the right weight does not move when the weight is applied again from scratch.
            RebuildScene();await Task.Delay(300);Check(await AtLight("rebuild"),"A scene rebuild keeps weight 0");
            // SSE_EQUIP_PLUGIN equips every armor from that plugin, slot by slot, as the Equip button does.
            if(Environment.GetEnvironmentVariable("SSE_EQUIP_PLUGIN") is {Length:>0} plugin){
                foreach(var item in records.Of("ARMO").Where(r=>r.Plugin.Equals(plugin,StringComparison.OrdinalIgnoreCase)).ToList()){
                    worn.RemoveAll(x=>(x.Slots&item.Slots)!=0);worn.Add(item);RebuildScene();await Task.Delay(250);
                    Check(await AtLight("equip "+item.EditorId),$"Equipping {item.EditorId} keeps weight 0");
                }
            }
            var boots=records.Of("ARMO").FirstOrDefault(r=>r.EditorId=="ArmorIronBoots");if(boots!=null){worn.RemoveAll(x=>(x.Slots&boots.Slots)!=0);worn.Add(boots);RebuildScene();await Task.Delay(300);Check(await AtLight("equip boots"),"Equipping another armor keeps weight 0");}
            foreach(string folder in folders){await Mount(folder,true);await Task.Delay(400);Check(await AtLight("mount "+Path.GetFileName(folder)),"Adding a mod folder keeps weight 0");}
            // HDT-SMP collision meshes follow the weight.
            if(scene.PhysicsActive){
                var world=scene.PhysicsWorld!;var bodies=world.Systems.SelectMany(sy=>sy.Bodies).ToList();
                double Spread(SmpBody b)=>b.SkinPos.Length==0?0:b.SkinPos.Max(p=>p.X)-b.SkinPos.Min(p=>p.X);
                var atZero=bodies.ToDictionary(b=>b.Name,Spread);
                weight.Value=100;await Task.Delay(900);var heavyBodies=scene.PhysicsWorld!.Systems.SelectMany(sy=>sy.Bodies).ToDictionary(b=>b.Name,Spread);
                log.Add("collision mesh widths at 0 / 100: "+string.Join(", ",atZero.Keys.Where(heavyBodies.ContainsKey).Select(k=>$"{k} {atZero[k]:F2} / {heavyBodies[k]:F2}")));
                Check(scene.PhysicsWeight==100&&atZero.Keys.Any(k=>heavyBodies.TryGetValue(k,out var h)&&Math.Abs(h-atZero[k])>.05),"HDT-SMP collision meshes are rebuilt at the new weight");
                weight.Value=0;await Task.Delay(900);
            }
            // Slot list: All, names, and the kept armor filter.
            var slotNames=slot.Items.Cast<object>().Select(o=>o.ToString()!).ToList();
            Check(slotNames[0]=="All slots"&&slotNames.Contains("44 · (face/mouth)")&&slotNames.Contains("57 · (shoulder)")&&!slotNames.Any(n=>n.Contains("Accessory")),"Slot names use the body-part list instead of Accessory");
            slot.SelectedIndex=0;await Task.Delay(100);
            Check(armor.Items.Count==records.Of("ARMO").Count(),$"All slots lists every armor ({armor.Items.Count})");
            var armorList=(SearchableComboBox)armor;armorList.SetFilter("iron");
            slot.SelectedItem=slot.Items.Cast<SlotChoice>().First(c=>c.Bit==2);await Task.Delay(100);
            Check(armorList.FilterText=="iron"&&armor.SelectedItem?.ToString()?.Contains("iron",StringComparison.OrdinalIgnoreCase)==true,$"Switching slot keeps the typed filter and picks the first match ({armor.SelectedItem})");
            armorList.OpenPopup();await Task.Delay(200);
            Check(armorList.FilterText=="iron"&&armorList.VisibleCount>0&&armorList.VisibleCount<armor.Items.Count,$"Reopening the armor list shows the kept filter ({armorList.VisibleCount} of {armor.Items.Count} shown)");
            using(var popupImage=armorList.CapturePopup())popupImage?.Save(Path.Combine(output,"armor-filter-kept.png"));
            armorList.CommitSelection();armorList.SetFilter("");
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"equipment-smoke.txt"),log);}finally{Close();}}
    }
    // Animation queue: several clips on one timeline with root motion carried from clip to clip and blended joins.
    async Task QueueSmoke(string[] clips)
    {
        var log=new List<string>();string output=Path.Combine(AppContext.BaseDirectory,"queue-smoke");Directory.CreateDirectory(output);
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        try{
            await Mount(SelfTest.TestData,false);
            followCamera.Checked=false;scene.FollowScene=false;gridFloor.Checked=true;cameraSubject.SelectedIndex=1;
            Check(queueBlend.Value==.3m&&queueAtWindow.Checked&&queueWindowEvent.SelectedItem as string=="MCO_WinOpen","Blend defaults to 0.30 s and hand-over to MCO_WinOpen");
            queueAtWindow.Checked=false;
            var singles=new List<AnimationClip>();
            foreach(string file in clips){var entry=assets.Drop(file);AddToQueue(entry);singles.Add(AnimationClip.Load(File.ReadAllBytes(file)));}
            Check(queue.Count==clips.Length&&queueList.Items.Count==clips.Length&&!QueuePlaying,"Clips are queued without changing what is playing");
            {
                // Drag feedback: clip 1 dragged, drop line before clip 3 (between rows 2 and 3), then before the end.
                int rowHeight=queueList.ItemHeight,width=420;
                Bitmap Paint(int dragging,int dropLine){
                    SetQueueDragState(dragging,dropLine);var bitmap=new Bitmap(width,rowHeight*queue.Count);
                    using(var g=Graphics.FromImage(bitmap))for(int i=0;i<queue.Count;i++)DrawQueueItem(g,new Rectangle(0,i*rowHeight,width,rowHeight),i,false);
                    return bitmap;
                }
                bool White(Bitmap b,int x,int y)=>b.GetPixel(x,y).ToArgb()==Color.White.ToArgb();
                using(var plain=Paint(-1,-1))Check(!White(plain,200,1)&&!White(plain,200,rowHeight*2),"Without a drag no row has an outline or a line");
                using(var drag=Paint(0,2)){
                    drag.Save(Path.Combine(output,"queue-drag.png"));
                    Check(White(drag,200,1)&&White(drag,1,rowHeight/2)&&White(drag,200,rowHeight-2)&&!White(drag,3,rowHeight/2),"The dragged clip has a white outline");
                    Check(White(drag,200,rowHeight*2)&&White(drag,200,rowHeight*2-1)&&!White(drag,3,rowHeight*2+4)&&!White(drag,200,rowHeight+1),"A white line marks the boundary the drop inserts at");
                }
                using(var end=Paint(-1,queue.Count)){
                    end.Save(Path.Combine(output,"queue-drag-end.png"));
                    Check(White(end,200,rowHeight*queue.Count-1)&&!White(end,200,1),"A drop below the last clip draws the line under it; a library drag outlines nothing");
                }
                SetQueueDragState(-1,-1);
            }
            await PlayQueue();clock.Pause();await Task.Delay(200);
            var q=clip!;var segments=q.Segments!;
            {
                // Root motion arrow: at the actor root from the AMR annotations, pointing along the actor's facing.
                double at=segments[^1].Start+segments[^1].Length*.5;clock.Seek(at);scene.SetTime(at);scene.Frame();viewport.camera.distance*=1.3f;viewport.InvalidateRender();await Task.Delay(150);
                var motion=q.Motion(at);var arrow=scene.RootArrowAt(at);
                Check(arrow is {} a0&&(a0.Origin-new OpenTK.Mathematics.Vector3d(motion.Position.X,motion.Position.Y,motion.Position.Z)).Length<1e-3,$"The root motion arrow sits at the actor root ({motion.Position.X:F1}, {motion.Position.Y:F1}, {motion.Position.Z:F1})");
                var facing=new OpenTK.Mathematics.Vector3d(-Math.Sin(motion.Yaw),Math.Cos(motion.Yaw),0);
                Check(arrow is {} a1&&OpenTK.Mathematics.Vector3d.Dot(a1.Forward,facing)>.9999,$"It points along the accumulated facing ({motion.Yaw*180/Math.PI:F1}°)");
                Check(scene.RootArrowTriangles==3,"The arrow is drawn as a filled flat shape");
                using(var shot=viewport.CaptureBitmap()){
                    shot.Save(Path.Combine(output,"root-arrow.png"));
                    int amber=0;for(int y=0;y<shot.Height;y+=2)for(int x=0;x<shot.Width;x+=2){var c=shot.GetPixel(x,y);if(c.R>110&&c.R>c.G+15&&c.G>c.B+40&&c.R-c.B>90)amber++;}
                    Check(amber>20,$"The arrow is visible in the viewport ({amber} sampled amber pixels)");
                }
                // Follow camera: the focus keeps its offset from the actor root as root motion moves it.
                followCamera.Checked=true;clock.Seek(0);scene.SetTime(0);scene.Frame();viewport.InvalidateRender();await Task.Delay(100);
                var startFocus=viewport.camera.FocusPosition;var startRoot=scene.RootPosition;float startDistance=viewport.camera.distance;
                clock.Seek(at);scene.SetTime(at);viewport.InvalidateRender();await Task.Delay(150);
                var moved=viewport.camera.FocusPosition-startFocus;var travelled=scene.RootPosition-startRoot;
                Check(travelled.Length>50&&(moved-travelled).Length<.01f&&viewport.camera.distance==startDistance,$"The follow camera moves with the actor root ({travelled.Length:F1} units) without re-framing or zooming");
                using(var follow=viewport.CaptureBitmap())follow.Save(Path.Combine(output,"follow-root.png"));
                // Moving the camera by hand keeps following: the root is still tracked from the new view.
                viewport.ManualCameraRequested?.Invoke();viewport.camera.Rotate(35,-8);viewport.Zoom(4);viewport.camera.Pan(40,-25);
                var handFocus=viewport.camera.FocusPosition;var handRoot=scene.RootPosition;float handDistance=viewport.camera.distance;
                clock.Seek(at*.5);scene.SetTime(at*.5);viewport.InvalidateRender();await Task.Delay(150);
                var handMoved=viewport.camera.FocusPosition-handFocus;var handTravelled=scene.RootPosition-handRoot;
                Check(followCamera.Checked&&scene.FollowScene&&handDistance<startDistance&&handTravelled.Length>10&&(handMoved-handTravelled).Length<.01f&&viewport.camera.distance==handDistance,
                    $"After orbiting, zooming and panning by hand Follow camera stays ticked and still tracks the root ({handTravelled.Length:F1} units) from the new view");
                clock.Seek(at);scene.SetTime(at);
                followCamera.Checked=false;scene.FollowScene=false;
                viewport.camera.Rotate(90,10);viewport.UpdateProjection(true);viewport.InvalidateRender();await Task.Delay(120);
                using(var side=viewport.CaptureBitmap())side.Save(Path.Combine(output,"root-arrow-side.png"));
                rootMotion.Checked=false;viewport.InvalidateRender();await Task.Delay(150);
                Check(scene.RootArrowAt(at)==null&&scene.RootArrowTriangles==0,"With Root motion clear there is no arrow");
                rootMotion.Checked=true;viewport.InvalidateRender();await Task.Delay(100);
                clock.Seek(0);scene.SetTime(0);
            }
            foreach(var seg in segments){var m=seg.Clip.Motion(seg.Clip.Duration);log.Add($"{seg.Name}: {seg.Clip.Duration:F3} s, plays {seg.Length:F3} s, {seg.Clip.Translation.Count} translation / {seg.Clip.Rotation.Count} rotation keys, travel ({m.Position.X:F1}, {m.Position.Y:F1}, {m.Position.Z:F1}), turn {m.Yaw*180/Math.PI:F1}°, base ({seg.BasePosition.X:F1}, {seg.BasePosition.Y:F1}) yaw {seg.BaseYaw*180/Math.PI:F1}°");}
            Check(QueuePlaying&&segments.Count==clips.Length&&Math.Abs(q.Duration-segments.Sum(s=>s.Length))<1e-9&&Math.Abs(clock.Duration-q.Duration)<1e-9,$"The queue plays as one timeline of {q.Duration:F3} s");
            Check(q.Events.Count==singles.Sum(c=>c.Events.Count)&&q.Events.Zip(q.Events.Skip(1)).All(p=>p.First.Time<=p.Second.Time),"Every clip's annotations are kept, in queue time");
            // Root motion: composing each clip's end transform by hand must give the queue's end transform.
            var position=System.Numerics.Vector3.Zero;float yaw=0;
            for(int i=0;i<singles.Count;i++){
                Check((segments[i].BasePosition-position).Length()<1e-3&&Math.Abs(segments[i].BaseYaw-yaw)<1e-5,$"Clip {i+1} starts where clip {i} left the actor");
                var end=singles[i].Motion(singles[i].Duration);position+=System.Numerics.Vector3.Transform(end.Position,System.Numerics.Matrix4x4.CreateRotationZ(yaw));yaw+=end.Yaw;
            }
            var total=q.Motion(q.Duration);
            Check((total.Position-position).Length()<1e-3&&Math.Abs(total.Yaw-yaw)<1e-5,$"The queue ends at the sum of every clip's root motion: ({total.Position.X:F1}, {total.Position.Y:F1}, {total.Position.Z:F1}) facing {total.Yaw*180/Math.PI:F1}°");
            Check(singles.Any(c=>c.Motion(c.Duration).Position.Length()>1),"At least one queued clip carries root motion, so the test means something");
            // A turn in one clip redirects the travel of every clip after it.
            var turn=new AnimationClip{Duration=1,Translation=[(1,new(0,100,0))],Rotation=[(1,MathF.PI/2)]};var walk=new AnimationClip{Duration=1,Translation=[(1,new(0,100,0))]};
            var turned=AnimationClip.Queue([("turn",turn),("walk",walk)],0);var turnedEnd=turned.Motion(2);var midway=turned.Motion(1.5);
            log.Add($"synthetic turn: end ({turnedEnd.Position.X:F1}, {turnedEnd.Position.Y:F1}) facing {turnedEnd.Yaw*180/Math.PI:F1}°, half-way through the walk ({midway.Position.X:F1}, {midway.Position.Y:F1})");
            var expected=System.Numerics.Vector3.Transform(new System.Numerics.Vector3(0,100,0),System.Numerics.Matrix4x4.CreateRotationZ(MathF.PI/2))+new System.Numerics.Vector3(0,100,0);
            Check((turnedEnd.Position-expected).Length()<1e-3&&Math.Abs(turnedEnd.Yaw-MathF.PI/2)<1e-5&&(midway.Position-(new System.Numerics.Vector3(0,100,0)+(expected-new System.Numerics.Vector3(0,100,0))*.5f)).Length()<1e-3,"After a 90° turn the next clip's forward travel follows the new facing, in the same rotation convention as the actor placement");
            for(int i=1;i<segments.Count;i++){
                var before=q.Motion(segments[i].Start-1e-4);var after=q.Motion(segments[i].Start+1e-4);
                Check((before.Position-after.Position).Length()<1&&Math.Abs(before.Yaw-after.Yaw)<.02,$"Root motion is continuous across the join into clip {i+1}");
            }
            // Blending: a join starts on the outgoing clip's last pose and reaches the incoming clip's own pose after the blend time.
            static double Difference(Poses_class a,Poses_class b)=>a.Transforms.Keys.Intersect(b.Transforms.Keys,StringComparer.OrdinalIgnoreCase).Max(k=>{var x=a.Transforms[k];var y=b.Transforms[k];return Math.Abs(x.Yaw-y.Yaw)+Math.Abs(x.Pitch-y.Pitch)+Math.Abs(x.Roll-y.Roll)+(Math.Abs(x.X-y.X)+Math.Abs(x.Y-y.Y)+Math.Abs(x.Z-y.Z))*.01;});
            double join=segments[1].Start,blend=q.BlendTime;
            var lastOfFirst=scene.PoseAt(join-1e-5,"t")!;var atJoin=scene.PoseAt(join,"t")!;var half=scene.PoseAt(join+blend/2,"t")!;var done=scene.PoseAt(join+blend,"t")!;
            q.BlendTime=0;var cut=scene.PoseAt(join,"t")!;var pureHalf=scene.PoseAt(join+blend/2,"t")!;var pureDone=scene.PoseAt(join+blend,"t")!;q.BlendTime=blend;
            log.Add($"join at {join:F3} s: outgoing vs incoming {Difference(lastOfFirst,cut):F4}, blended start vs outgoing {Difference(atJoin,lastOfFirst):F4}, half-way vs incoming {Difference(half,pureHalf):F4}, end vs incoming {Difference(done,pureDone):F6}");
            Check(Difference(lastOfFirst,cut)>.01,"The two clips meet in different poses, so a hard cut would pop");
            Check(Difference(atJoin,lastOfFirst)<.01,"With blending the join starts on the outgoing clip's last pose");
            Check(Difference(half,pureHalf)>.001&&Difference(half,lastOfFirst)>.001,"Half-way through the blend the pose lies between the two clips");
            Check(Difference(done,pureDone)<1e-4,"After the blend time the incoming clip plays unmodified");
            Check(Difference(Scene.BlendPoses(lastOfFirst,cut,0),lastOfFirst)<1e-3&&Difference(Scene.BlendPoses(lastOfFirst,cut,1),cut)<1e-3,"Blend weights 0 and 1 reproduce the two source poses");
            queueBlend.Value=0;await Task.Delay(50);
            Check(q.BlendTime==0&&Difference(scene.PoseAt(join,"t")!,cut)<1e-6,"Setting Blend to 0 gives a hard cut");
            queueBlend.Value=.3m;
            // Hand-over at the MCO window: each attack but the last stops at its MCO_WinOpen, the next starts there.
            queueAtWindow.Checked=true;await Task.Delay(700);clock.Pause();
            var win=clip!;var winSegs=win.Segments!;
            for(int i=0;i<winSegs.Count;i++)log.Add($"window {i+1}: {winSegs[i].Name} hands over at {winSegs[i].Window??"end"} {winSegs[i].HandOver:F3} s of {winSegs[i].Clip.Duration:F3} s, plays {winSegs[i].Length:F3} s");
            for(int i=0;i<winSegs.Count-1;i++){
                var open=singles[i].AnnotationTime("MCO_WinOpen");
                Check(open!=null&&winSegs[i].Window=="MCO_WinOpen"&&Math.Abs(winSegs[i].HandOver-open.Value)<1e-9&&Math.Abs(winSegs[i+1].Start-winSegs[i].Global(open.Value))<1e-9,$"Clip {i+2} starts at clip {i+1}'s MCO_WinOpen ({open:F3} s into it)");
            }
            Check(winSegs[^1].Window==null&&Math.Abs(winSegs[^1].HandOver-singles[^1].Duration)<1e-9,"The last clip plays to its end");
            Check(win.Duration<q.Duration-.1,$"Handing over at the window shortens the queue ({q.Duration:F3} -> {win.Duration:F3} s)");
            var handPos=System.Numerics.Vector3.Zero;float handYaw=0;
            for(int i=0;i<winSegs.Count;i++){
                Check((winSegs[i].BasePosition-handPos).Length()<1e-3,$"Clip {i+1} starts where the previous clip had moved the actor by its hand-over");
                var m=singles[i].Motion(winSegs[i].HandOver);handPos+=System.Numerics.Vector3.Transform(m.Position,System.Numerics.Matrix4x4.CreateRotationZ(handYaw));handYaw+=m.Yaw;
            }
            int closesBeforeLast=win.Events.Count(e=>e.Text.Trim().Equals("MCO_WinClose",StringComparison.OrdinalIgnoreCase)&&e.Time<winSegs[^1].Start-1e-9);
            Check(win.Events.Count<q.Events.Count&&closesBeforeLast==0,$"Annotations after a hand-over are skipped: no MCO_WinClose fires before the last clip ({q.Events.Count} -> {win.Events.Count} annotations)");
            double hand=winSegs[1].Start;
            var outgoingMid=scene.PoseAt(hand+blend/2,"t")!;win.BlendTime=0;var incomingMid=scene.PoseAt(hand+blend/2,"t")!;win.BlendTime=blend;
            Check(Difference(outgoingMid,incomingMid)>.001,"During the blend the outgoing attack still contributes");
            queueWindowEvent.SelectedItem="MCO_PowerWinOpen";await Task.Delay(700);clock.Pause();
            var power=clip!.Segments!;
            Check(Enumerable.Range(0,power.Count-1).All(i=>power[i].Window=="MCO_PowerWinOpen"&&Math.Abs(power[i].HandOver-singles[i].AnnotationTime("MCO_PowerWinOpen")!.Value)<1e-9),"Choosing MCO_PowerWinOpen hands over at that annotation instead");
            Check(queueList.Items.Cast<string>().First().Contains("next at MCO_PowerWinOpen"),"The queue list says where each clip hands over: "+queueList.Items[0]);
            queueWindowEvent.SelectedItem="MCO_WinOpen";await Task.Delay(700);clock.Pause();
            // Captures across the timeline, with the actor's position logged.
            async Task Shot(double time,string name){
                clock.Pause();clock.Seek(time);scene.SetTime(time);viewport.InvalidateRender();await Task.Delay(150);
                using var image=viewport.CaptureBitmap();image.Save(Path.Combine(output,name+".png"));
            }
            scene.SetTime(0);scene.Frame();viewport.camera.distance*=2.2f;viewport.camera.Rotate(0,35);viewport.UpdateProjection(true);
            await Shot(0,"queue-start");
            for(int i=1;i<segments.Count;i++){await Shot(segments[i].Start+blend/2,$"queue-join-{i}-mid-blend");await Shot(segments[i].Start+segments[i].Length*.6,$"queue-clip-{i+1}");}
            await Shot(q.Duration,"queue-end");
            Check(timeLabel.Text.Contains($"clip {segments.Count}/{segments.Count}"),"The time readout names the clip being played: "+timeLabel.Text);
            // Playback runs through every clip.
            clock.Seek(0);clock.Play();var seen=new HashSet<int>();var watch=Stopwatch.StartNew();
            while(watch.Elapsed.TotalSeconds<Math.Min(12,q.Duration+.5)){await Task.Delay(30);seen.Add(q.SegmentIndex(scene.Time));}
            clock.Pause();
            Check(seen.Count==segments.Count,$"Playback passes through all {segments.Count} clips");
            // Queue edits, session round trip, and leaving queue mode.
            var saved=CaptureSettings();double savedDuration=clip!.Duration;
            Check(saved.QueuePlaying&&saved.Queue!.Length==clips.Length&&saved.QueueBlend==.3&&saved.QueueAtWindow&&saved.QueueWindow=="MCO_WinOpen","The queue, its blend time, its hand-over setting and its playing state are saved with the session");
            var order=queue.ToArray();
            MoveQueueItem(0,queue.Count);await Task.Delay(600);
            Check(queue[^1]==order[0]&&queue[0]==order[1]&&clip!.Segments![^1].Name==order[0].Name,"Dragging the first clip below the last moves it to the end and rebuilds the playing queue");
            MoveQueueItem(queue.Count-1,0);await Task.Delay(600);
            Check(queue.SequenceEqual(order),"Dragging it back to the top restores the order");
            InsertIntoQueue([order[0]],1);await Task.Delay(600);
            Check(queue.Count==order.Length+1&&queue[1]==order[0]&&clip!.Segments!.Count==order.Length+1,"Dropping a library clip between two queued clips inserts it there");
            // Multiple selection in the library: marked clips come out in library order, a folder stands for its clips.
            var library=(ResponsiveTreeView)tree;search.Text="";BuildTree();tree.ExpandAll();
            var clipNodes=new List<TreeNode>();void Find(TreeNodeCollection level){foreach(TreeNode n in level){if(n.Tag is ClipEntry e&&clips.Any(c=>Path.GetFullPath(c).Equals(e.Path,StringComparison.OrdinalIgnoreCase)))clipNodes.Add(n);Find(n.Nodes);}}Find(tree.Nodes);
            Check(clipNodes.Count==clips.Length,$"The {clips.Length} test clips are in the library ({clipNodes.Count} found)");
            library.Mark(clipNodes[^1],false);library.Mark(clipNodes[0],true);
            var picked=LibraryClips(clipNodes[^1]);
            Check(picked.Length==2&&picked[0].Path==((ClipEntry)clipNodes[0].Tag).Path&&picked[1].Path==((ClipEntry)clipNodes[^1].Tag).Path,"Two Ctrl-marked clips drag together, in library order");
            library.Mark(clipNodes[0],false);library.MarkRange(clipNodes[^1]);
            Check(LibraryClips(clipNodes[0]).Length==clips.Length,"Shift-marking a range covers every clip between the two");
            var folder=clipNodes[0].Parent;
            if(folder!=null){library.ClearMarks();Check(LibraryClips(folder).Length==folder.Nodes.Cast<TreeNode>().Count(n=>n.Tag is ClipEntry),"Dragging a folder stands for every clip in it");}
            library.Mark(clipNodes[0],false);library.MarkRange(clipNodes[^1]);int queuedBefore=queue.Count;
            InsertIntoQueue(LibraryClips(clipNodes[1]),queue.Count);await Task.Delay(700);
            Check(queue.Count==queuedBefore+clips.Length&&queue.Skip(queuedBefore).Select(q=>q.Path).SequenceEqual(clipNodes.Select(n=>((ClipEntry)n.Tag).Path))&&clip!.Segments!.Count==queue.Count,"Dropping a multiple selection appends every clip in order and rebuilds the playing queue");
            using(var lib=new Bitmap(tree.Width,tree.Height)){tree.DrawToBitmap(lib,tree.ClientRectangle);lib.Save(Path.Combine(output,"library-multiselect.png"));}
            library.Mark(clipNodes[0],false);
            for(int i=0;i<clips.Length;i++){queueList.SelectedIndex=queue.Count-1;EditQueue(x=>queue.RemoveAt(x),0);}
            await Task.Delay(700);
            ToggleQueueWindow();await Task.Delay(300);
            Check(queueWindow!=null&&queueBody.Parent!=queueTab&&queueList.Items.Count==queue.Count,"The queue opens in its own window with the same list");
            using(var popup=new Bitmap(queueWindow!.Width,queueWindow.Height)){queueWindow.DrawToBitmap(popup,new Rectangle(Point.Empty,queueWindow.Size));popup.Save(Path.Combine(output,"queue-window.png"));}
            ToggleQueueWindow();await Task.Delay(200);
            Check(queueWindow==null&&queueBody.Parent==queueTab,"Closing the window returns the list to the Queue tab");
            queueList.SelectedIndex=1;EditQueue(i=>queue.RemoveAt(i),0);await Task.Delay(600);
            Check(queue.SequenceEqual(order),"Remove takes out the selected clip");
            queueList.SelectedIndex=clips.Length-1;EditQueue(i=>queue.RemoveAt(i),0);await Task.Delay(600);
            Check(queue.Count==clips.Length-1&&clip!.Segments!.Count==clips.Length-1,"Removing a clip rebuilds a playing queue");
            await SelectClip(assets.Clips.First(c=>c.Path.Equals(Path.GetFullPath(clips[0]),StringComparison.OrdinalIgnoreCase)));clock.Pause();
            Check(!QueuePlaying&&queue.Count==clips.Length-1,"Selecting a library clip plays it alone and keeps the queue list");
            await ApplySettings(saved);await Task.Delay(300);
            Check(QueuePlaying&&queue.Count==clips.Length&&clip!.Segments!.Count==clips.Length&&Math.Abs(clip.Duration-savedDuration)<1e-9&&queueAtWindow.Checked&&clip.Segments[0].Window=="MCO_WinOpen","A saved scene restores the queue with its hand-over setting and plays it");
            if(inspectorTabs!=null&&queueTab!=null)inspectorTabs.SelectedTab=queueTab;await Task.Delay(150);
            using(var window=new Bitmap(Width,Height)){DrawToBitmap(window,new Rectangle(Point.Empty,Size));window.Save(Path.Combine(output,"window-queue.png"));}
            // Tab strip: themed, bold, each tab in its own colour, counts on Queue and Diagnostics.
            var strip=(ThemedTabControl)inspectorTabs!;
            for(int i=0;i<strip.TabCount;i++)log.Add($"tab {i}: rect {strip.GetTabRect(i).Width} px, bold text {TextRenderer.MeasureText(strip.TabPages[i].Text,strip.Font).Width} px, font {strip.Font.Name} {strip.Font.Size} {strip.Font.Style}");
            Check(strip.Font.Bold&&queueTab!.Text==$"Queue · {queue.Count}"&&strip.TabPages.Cast<TabPage>().Select(ThemedTabControl.Accent).Distinct().Count()==strip.TabCount,$"Tabs are bold, each has its own colour, and the queue tab shows its count ({string.Join(" | ",strip.TabPages.Cast<TabPage>().Select(p=>p.Text))})");
            for(int i=0;i<strip.TabCount;i++){
                strip.SelectedIndex=i;await Task.Delay(80);
                int height=Enumerable.Range(0,strip.TabCount).Max(t=>strip.GetTabRect(t).Bottom)+8;
                using var image=new Bitmap(strip.Width,strip.Height);strip.DrawToBitmap(image,new Rectangle(Point.Empty,strip.Size));
                using var crop=image.Clone(new Rectangle(0,0,Math.Min(image.Width,strip.GetTabRect(strip.TabCount-1).Right+60),height),image.PixelFormat);crop.Save(Path.Combine(output,$"tabs-{i}.png"));
                var r=strip.GetTabRect(i);var bar=image.GetPixel(r.X+r.Width/2,r.Y+1);var accent=ThemedTabControl.Accent(strip.TabPages[i]);
                // Empty strip to the right of the last tab on this tab's row.
                int rowEnd=Enumerable.Range(0,strip.TabCount).Select(strip.GetTabRect).Where(t=>t.Y==r.Y).Max(t=>t.Right);
                var empty=image.GetPixel(Math.Min(image.Width-2,rowEnd+30),r.Y+r.Height/2);
                Check(Math.Abs(bar.R-accent.R)+Math.Abs(bar.G-accent.G)+Math.Abs(bar.B-accent.B)<12&&empty.ToArgb()==ThemedTabControl.Strip.ToArgb(),$"Selecting {strip.TabPages[i].Text} draws its accent bar, and the empty strip is the dark theme colour");
            }
            var wide=Size;Size=new Size(1100,800);await Task.Delay(300);
            using(var narrow=new Bitmap(strip.Width,strip.Height)){strip.DrawToBitmap(narrow,new Rectangle(Point.Empty,strip.Size));int rows=Enumerable.Range(0,strip.TabCount).Select(t=>strip.GetTabRect(t).Y).Distinct().Count();
                using var crop=narrow.Clone(new Rectangle(0,0,narrow.Width,Enumerable.Range(0,strip.TabCount).Max(t=>strip.GetTabRect(t).Bottom)+8),narrow.PixelFormat);crop.Save(Path.Combine(output,"tabs-narrow.png"));
                Check(Enumerable.Range(0,strip.TabCount).All(t=>strip.GetTabRect(t).Width>=TextRenderer.MeasureText(strip.TabPages[t].Text,strip.Font).Width+8),$"In a narrow window every tab still fits its bold text ({rows} row(s))");}
            Size=wide;await Task.Delay(200);
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"queue-smoke.txt"),log);}finally{Close();}}
    }
    // Snap to frame and the frame readout beside the time slider.
    // Precision attack collisions. First the event replay on synthetic annotations, then a scene: arguments are the
    // game Data folder, the clip file name to pick, then mod folders (the clip's mod and Precision's settings mods).
    async Task PrecisionSmoke(string[] arguments)
    {
        var log=new List<string>();string output=Path.Combine(AppContext.BaseDirectory,"precision-smoke");Directory.CreateDirectory(output);
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        static List<Annotation> Events(params (double Time,string Text)[] items)=>items.Select(i=>new Annotation(i.Time,i.Text,0)).ToList();
        try{
            var defaults=new PrecisionSettings();
            PrecisionTimeline Run(List<Annotation> events,bool player=true,bool power=false,PrecisionSettings? settings=null)=>PrecisionTimeline.Build(events,settings??defaults,player,_=>power,n=>new[]{"WEAPON","SHIELD","NPC R Hand [RHnd]"}.Contains(n,StringComparer.OrdinalIgnoreCase));
            var t=Run(Events((0,"Collision_Add.Node(WEAPON)"),(.1,"Collision_AttackStart"),(.2,"Collision_Add.Node(WEAPON)|Scale(1.5)"),(.5,"Collision_Remove.node(WEAPON)"),(.9,"Collision_AttackEnd")));
            Check(t.Collisions.Count==1&&Math.Abs(t.Collisions[0].Start-.2)<1e-9&&Math.Abs(t.Collisions[0].End-.5)<1e-9,"Collision_Add before Collision_AttackStart is ignored; Add at 0.2 lives until Remove.node(WEAPON) at 0.5");
            Check(t.Notes.Any(n=>n.Contains("no Collision_AttackStart")),"The ignored Add is reported");
            var closed=Run(Events((0,"Collision_AttackStart"),(.3,"Collision_AttackEnd"),(.6,"Collision_Add.Node(WEAPON)")));
            Check(closed.Collisions.Count==0&&closed.Notes.Single().Contains("Collision_AttackEnd at 0.3000 s closed the attack"),"An Add after an early Collision_AttackEnd is ignored, and the note names the event that closed the attack: "+closed.Notes.Single());
            Check(t.Collisions[0].Definition.Scale==1.5f&&t.Collisions[0].Recoil,"Payload parameters are case-insensitive and a weapon collision gets a recoil capsule");
            t=Run(Events((0,"Collision_AttackStart"),(.1,"Collision_Add.Node(WEAPON)|Duration(0)"),(.2,"Collision_Add.Node(SHIELD)|Duration(0.25)")));
            Check(Math.Abs(t.Collisions[0].End-.4)<1e-6&&Math.Abs(t.Collisions[1].End-.45)<1e-6,"Duration(0) uses fDefaultCollisionLifetime 0.3 s and Duration(0.25) lasts 0.25 s");
            t=Run(Events((0,"Collision_AttackStart"),(.1,"Collision_Add.Node(WEAPON)|Duration(0)")),power:true);
            Check(Math.Abs(t.Collisions[0].End-(.1+.3*1.8))<1e-6,"A power attack stretches Duration(0) by fDefaultCollisionLifetimePowerAttackMult");
            t=Run(Events((0,"Collision_AttackStart"),(.1,"Collision_Add.Node(WEAPON)|ID(1)"),(.15,"Collision_Add.Node(NPC R Hand [RHnd])|ID(1)"),(.4,"Collision_Remove.ID(1)"),(.8,"Collision_AttackEnd")));
            log.Add("ID removal, two ID 1 entries last in the list: "+string.Join("; ",t.Collisions.Select(c=>$"{c.Definition.Node} ends {c.End:F2} ({c.EndReason})")));
            Check(t.Collisions[0].End==.4&&t.Collisions[1].End==.8,"Collision_Remove.ID(1) on [W1, H1] erases W1 only (remove_if then a single-iterator erase); H1 lasts until AttackEnd");
            Check(t.Notes.Any(n=>n.Contains("erases a single entry")),"The single-entry erase is reported");
            t=Run(Events((0,"Collision_AttackStart"),(.1,"Collision_Add.Node(WEAPON)|ID(1)"),(.15,"Collision_Add.Node(NPC R Hand [RHnd])|ID(1)"),(.2,"Collision_Add.Node(SHIELD)|ID(2)"),(.4,"Collision_Remove.ID(1)")));
            log.Add("ID removal, [W1, H1, S2]: "+string.Join("; ",t.Collisions.Select(c=>$"{c.Definition.Node} ends {c.End:F2}")));
            Check(t.Collisions[0].End==.4&&t.Collisions[1].End==.4&&double.IsPositiveInfinity(t.Collisions[2].End)&&!t.Notes.Any(n=>n.Contains("single entry")),"On [W1, H1, S2] remove_if moves S2 over W1 and the erase takes H1, so both ID 1 entries go");
            t=Run(Events((0,"Collision_AttackStart"),(.1,"Collision_Add.Node(WEAPON)"),(.3,"Collision_Remove.Node(weapon)"),(.5,"Collision_Cancel"),(.6,"Collision_Add.Node(WEAPON)")));
            Check(t.Collisions.Count==1&&t.Collisions[0].End==.5&&t.Notes.Any(n=>n.Contains("case sensitive")),"Remove matches node names case-sensitively; Collision_Cancel removes all and needs a new AttackStart");
            t=Run(Events((0,"Collision_AttackStart"),(.1,"Collision_Add.Node(weapon)")));
            Check(t.Collisions.Count==1&&!t.Collisions[0].Definition.IsWeapon,"Node(weapon) in lower case is not treated as the weapon node");
            t=Run(Events((0,"Collision_AttackStart"),(.1,"Collision_Add.Node(WEAPON)"),(.2,"Collision_Add.Node(WEAPON)|NoRecoil"),(.3,"hitFrame")));
            Check(t.Collisions[0].RecoilEnd==.3&&!t.Collisions[1].Recoil,"hitFrame removes the first recoil capsule; NoRecoil adds none");
            t=Run(Events((0,"Collision_AttackStart"),(.1,"Collision_Add.Node(WEAPON)|Scale(x)"),(.2,"Collision_Add.ID(3)"),(.3,"Collision_Add.Node(Missing Bone)")));
            Check(t.Collisions.Count==0&&t.Notes.Count(n=>n.Contains("rejected")||n.Contains("no node"))==3,"Bad parameters, a missing Node() and an unknown bone add nothing and are reported");
            var ini=new PrecisionSettings();ini.Apply("[AttackCollisions]\r\nfWeaponLengthMult = 1.000000\r\nfPlayerAttackLengthMult = 1.25\r\n[Recoil]\r\nbRecoilPlayer = 0\r\n");
            Check(ini.WeaponLengthMult==1&&ini.PlayerAttackLengthMult==1.25f&&!ini.RecoilPlayer,"MCM ini values override the defaults");
            var blade=PrecisionGeometry.Apply(PrecisionGeometry.WeaponRotation,new OpenTK.Mathematics.Vector3d(100,0,0));
            Check((blade-new OpenTK.Mathematics.Vector3d(0,100,0)).Length<1e-9,"The weapon rotation turns the capsule's X axis onto the weapon node's Y axis");
            var weaponDefinition=PrecisionDefinition.Parse("Node(WEAPON)|WeaponTip",out _)!;
            var tip=PrecisionGeometry.Weapon(defaults,weaponDefinition,100,true);
            Check(Math.Abs(tip.A.X-120)<1e-3&&Math.Abs(tip.Radius-18)<1e-3&&Math.Abs(tip.Translate.Y-102)<1e-3,"Weapon capsule: 100 mesh x 1.2 = 120 long, 12 x 1.5 = 18 radius for the player, WeaponTip shifts it 102 along Y");
            var npc=PrecisionGeometry.Weapon(defaults,PrecisionDefinition.Parse("Node(WEAPON)",out _)!,0,false);
            Check(Math.Abs(npc.A.X-48)<1e-3&&Math.Abs(npc.Radius-12)<1e-3,"Without a weapon mesh an NPC capsule takes fMinWeaponLength 40 x 1.2 and radius 12");

            // Scene.
            pluginsFile=null;
            await Mount(arguments[0],false);
            foreach(string folder in arguments.Skip(2))await Mount(folder,true);
            followCamera.Checked=false;scene.FollowScene=false;
            var entry=assets.Clips.First(c=>Path.GetFileName(c.Path).Equals(arguments[1],StringComparison.OrdinalIgnoreCase));
            var axe=records.Of("WEAP").Where(w=>w.EditorId.Contains("Battleaxe",StringComparison.OrdinalIgnoreCase)&&assets.Exists(ProjectileVisuals.ModelPath(w.First("MODL")?.Text))).OrderBy(w=>w.EditorId.Length).First();
            selecting=true;weapons.SelectedItem=axe;selecting=false;
            await SelectClip(entry);clock.Pause();await Task.Delay(300);
            log.Add($"clip {entry.Path}, weapon {axe.EditorId}, {scene.BoneCapsules} skeleton bones with capsule collision");
            log.Add("annotations: "+string.Join(" | ",clip!.Events.Select(e=>$"{e.Time:F3} {e.Text}")));
            var timeline=scene.Precision!;
            Check(timeline.HasAnnotations&&timeline.Collisions.Count>0&&timeline.Collisions.All(c=>c.Definition.Node=="WEAPON"),$"The clip's annotations give {timeline.Collisions.Count} WEAPON collision(s)");
            var settings=scene.PrecisionConfig;log.Add("settings: "+settings.Describe(true));
            Check(settings.Sources.Count>0,"Precision's MCM settings are read from the mounted mods");
            Check(events.Text.Contains("PRECISION ATTACK COLLISIONS"),"The Annotations panel lists the Precision collisions");
            double mesh=scene.WeaponMeshLength();log.Add($"weapon mesh length {mesh:F2}");
            Check(mesh>40&&mesh<200,"The battleaxe's mesh bounds give a plausible reach");
            var first=timeline.Collisions[0];double during=first.Start+Math.Min(.05,(Math.Min(first.End,clip.Duration)-first.Start)/2);
            precisionShow.Checked=false;clock.Seek(during);scene.SetTime(during);viewport.InvalidateRender();await Task.Delay(150);
            Check(scene.PrecisionLines==0,"With the box clear nothing is drawn (default off)");
            precisionShow.Checked=true;viewport.InvalidateRender();await Task.Delay(150);
            var capsules=scene.PrecisionCapsulesAt(during);
            Check(capsules.Count>=1&&scene.PrecisionLines>0,$"Ticking the box draws {capsules.Count} capsule(s) at {during:F3} s ({scene.PrecisionLines} lines)");
            using(var shot=viewport.CaptureBitmap())shot.Save(Path.Combine(output,"capsule-during.png"));
            var (grip,bladeTip)=scene.WeaponBladeSegment();
            var attack=capsules.First(c=>!c.Recoil);
            var bladeDirection=new OpenTK.Mathematics.Vector3d(bladeTip.X-grip.X,bladeTip.Y-grip.Y,bladeTip.Z-grip.Z);
            double angle=OpenTK.Mathematics.Vector3d.CalculateAngle(attack.A-attack.B,bladeDirection)*180/Math.PI;
            double offset=(attack.B-new OpenTK.Mathematics.Vector3d(grip.X,grip.Y,grip.Z)).Length;
            log.Add($"capsule {attack.Detail}; from {attack.B} to {attack.A}; blade grip {grip} tip {bladeTip}; angle {angle:F1} deg, base offset {offset:F2}");
            // The farthest vertex sits on the blade's edge (an axe head corner), so the haft axis is a few degrees off it.
            Check(angle<20&&offset<1,"The weapon capsule starts at the weapon node and runs along the blade");
            double after=Math.Min(first.End+.05,clip.Duration);
            if(first.End<clip.Duration){
                clock.Seek(after);scene.SetTime(after);viewport.InvalidateRender();await Task.Delay(150);
                Check(scene.PrecisionCapsulesAt(after).All(c=>c.Collision!=first),$"After {first.EndReason} at {first.End:F3} s the capsule is gone");
                using(var shot=viewport.CaptureBitmap())shot.Save(Path.Combine(output,"capsule-after.png"));
            }
            // A hand collision from the bone's own capsule shape, added to the loaded clip's annotations.
            clip.Events.Add(new Annotation(during-.01,"Collision_Add.Node(NPC R Hand [RHnd])|ID(7)",0));clip.Events=clip.Events.OrderBy(e=>e.Time).ToList();
            var hand=scene.PrecisionCapsulesAt(during).FirstOrDefault(c=>c.Collision.Definition.Id==7);
            Check(hand.Collision!=null,"A Collision_Add on a hand bone yields a capsule: "+hand.Detail);
            log.Add($"hand capsule from {hand.B} to {hand.A}, radius {hand.Radius:F2}");
            Check(hand.Radius>1&&hand.Radius<30&&(hand.A-hand.B).Length<60,"The hand capsule has a hand-sized radius and length");
            viewport.InvalidateRender();await Task.Delay(150);
            using(var shot=viewport.CaptureBitmap())shot.Save(Path.Combine(output,"capsule-hand.png"));
            precisionActor.SelectedIndex=1;double npcRadius=scene.PrecisionCapsulesAt(during).First(c=>!c.Recoil&&c.Collision.Definition.Node=="WEAPON").Radius;
            precisionActor.SelectedIndex=0;double playerRadius=scene.PrecisionCapsulesAt(during).First(c=>!c.Recoil&&c.Collision.Definition.Node=="WEAPON").Radius;
            Check(Math.Abs(playerRadius-npcRadius*settings.PlayerAttackRadiusMult)<1e-6,$"Player capsules take fPlayerAttackRadiusMult ({npcRadius:F1} NPC, {playerRadius:F1} player)");
            Check(precisionLabel.Text.Contains("collision(s) in this clip"),"The label under the box lists the collisions: "+precisionLabel.Text.Replace("\n"," / "));
            Check(scene.Precision!.Notes.Count==0,"No Precision notes for this clip: "+string.Join(" | ",scene.Precision!.Notes));
            Check(CaptureSettings().PrecisionCollisions&&!CaptureSettings().PrecisionNpc,"The setting is saved with the session");
            using(var window=new Bitmap(Width,Height)){DrawToBitmap(window,new Rectangle(Point.Empty,Size));window.Save(Path.Combine(output,"window-precision.png"));}
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"precision-smoke.txt"),log);}finally{Close();}}
    }
    // Annotation sounds on the NordicSoulsPBR mod list: a vanilla SoundPlay on an MCO battleaxe clip, then an FF7 clip
    // whose sounds come from a mod folder that is not loaded (Warnings names it), then with that folder mounted.
    async Task SoundSmoke()
    {
        var log=new List<string>();string output=Path.Combine(AppContext.BaseDirectory,"sound-smoke");Directory.CreateDirectory(output);
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        string Mod(string name)=>Path.Combine(SelfTest.Mods,name);
        try{
            soundPlayer.Offline=true;pluginsFile=null;
            await Mount(SelfTest.GameData,false);
            await Mount(Mod("ADXP I MCO I BFCO NPC BattleAxe (2HW) Moveset - SCAR 2.0"),true);
            await Mount(Mod("FF7 Cloud Animations"),true);
            followCamera.Checked=false;scene.FollowScene=false;
            Check(!playSounds.Checked,"Sounds is off by default");
            await SelectClip(assets.Clips.First(c=>c.Path.EndsWith(@"variantAttack5\mco_powerattack1.hkx",StringComparison.OrdinalIgnoreCase)));clock.Pause();
            var events=SoundLibrary.Events(clip!.Events);
            log.Add("sound annotations: "+string.Join(" | ",events.Select(e=>$"{e.Time:F3} {e.EditorId}")));
            Check(events.Count>0,$"The battleaxe clip has {events.Count} SoundPlay annotation(s)");
            var info=Sounds().Resolve(events[0].EditorId);
            log.Add($"{info.EditorId}: {info.Summary()}; files {string.Join(", ",info.Files)}; missing {string.Join(", ",info.MissingFiles)}; sound paths indexed {assets.Paths.Count(p=>p.StartsWith(@"sound\",StringComparison.OrdinalIgnoreCase))}, e.g. {string.Join(" | ",assets.Paths.Where(p=>p.Contains("swing",StringComparison.OrdinalIgnoreCase)&&p.StartsWith(@"sound\",StringComparison.OrdinalIgnoreCase)).Take(3))}");
            Check(info.Descriptor!=null&&info.Playable,$"{info.EditorId} resolves to a sound descriptor with playable files");
            Check(info.StaticAttenuationDb>=0&&Math.Abs(info.Gain-Math.Pow(10,-info.StaticAttenuationDb/20)*info.CategoryVolume)<1e-9,$"Gain is static attenuation {info.StaticAttenuationDb:0.##} dB times category volume {info.CategoryVolume:0.###} = {info.Gain:0.###}");
            foreach(string file in info.Files.Take(3)){
                var decoded=soundPlayer.Decode(assets,file);double peak=decoded.Samples.Select(Math.Abs).DefaultIfEmpty().Max();
                log.Add($"  {file}: {decoded.Format.SampleRate} Hz, {decoded.Format.Channels} ch, {decoded.Samples.Length/decoded.Format.Channels/(double)decoded.Format.SampleRate:F3} s, peak {peak:F3}");
                Check(decoded.Samples.Length>0&&peak>.01,$"{Path.GetExtension(file)} file decodes to audible samples");
            }
            Check(warnings.Text=="No warnings.","No warnings for a clip whose sounds all resolve");
            {
                // An .xwm sound from the vanilla archives goes through Windows' WMA decoder.
                var all=records.Of("SNDR").Select(r=>Sounds().Resolve(r.EditorId)).ToList();
                // No vanilla effect is an .xwm, so a mod's loose one stands in for the decoder check.
                log.Add("SNDR file extensions: "+string.Join(", ",all.SelectMany(i=>i.Files.Concat(i.MissingFiles)).GroupBy(f=>Path.GetExtension(f).ToLowerInvariant()).Select(g=>$"{g.Key} {g.Count()}"))+$"; indexed sound files: "+string.Join(", ",assets.Paths.Where(p=>p.StartsWith(@"sound\",StringComparison.OrdinalIgnoreCase)).GroupBy(f=>Path.GetExtension(f).ToLowerInvariant()).Select(g=>$"{g.Key} {g.Count()}")));
                string file=Mod(Path.Combine("Alternate Start Reborn","sound","fx","tempestian","reborngoddess","reborn_entercellvoice.xwm"));var xwm=new{EditorId="(loose file)"};
                var watch=System.Diagnostics.Stopwatch.StartNew();var decoded=soundPlayer.Decode(assets,file);double peak=decoded.Samples.Select(Math.Abs).DefaultIfEmpty().Max();
                log.Add($"  {xwm.EditorId}: {file}: {decoded.Format.SampleRate} Hz, {decoded.Format.Channels} ch, {decoded.Samples.Length/decoded.Format.Channels/(double)decoded.Format.SampleRate:F3} s, peak {peak:F3}, {watch.ElapsedMilliseconds} ms");
                Check(decoded.Samples.Length==991232/2&&peak>.01,".xwm decodes to the same 247,808 sample frames ffmpeg produces, audible");
            }
            Check(events.Count==0||events.All(e=>events.Count>0)&&SoundText().Contains("SOUNDS")&&this.events.Text.Contains("SOUNDS"),"The Annotations tab lists the sounds");
            // Playback: off -> nothing; on, no loop -> each annotation once at its time; loop -> once per pass.
            loop.Checked=false;clock.Loop=false;clock.Seek(0);clock.Play();await Task.Delay((int)(clip.Duration*1000)+300);clock.Pause();
            Check(soundPlayer.Played.Count==0,"With Sounds clear nothing plays");
            playSounds.Checked=true;soundPlayer.Played.Clear();clock.Seek(0);scene.SetTime(0);await Task.Delay(80);clock.Play();await Task.Delay((int)(clip.Duration*1000)+300);clock.Pause();
            log.Add("played: "+string.Join(" | ",soundPlayer.Played.Select(p=>$"{p.Time:F3} {p.EditorId} gain {p.Gain:F3} pitch {p.Pitch:F3}")));
            Check(soundPlayer.Played.Count==events.Count&&soundPlayer.Played.Select(p=>p.Time).SequenceEqual(events.Select(e=>e.Time)),$"Playing once through plays each of the {events.Count} annotation(s) once, at its time");
            Check(soundPlayer.Played.All(p=>{var i=Sounds().Resolve(p.EditorId);return p.Gain<=i.Gain+1e-9&&p.Gain>=i.Gain*Math.Pow(10,-i.DbVariance/20)-1e-9;}),"Each play uses the static attenuation, less at most the random dB variance");
            var mix=new float[44100*2];int rendered=soundPlayer.Render(mix);double rms=Math.Sqrt(mix.Take(rendered).Sum(v=>(double)v*v)/Math.Max(1,rendered));
            Check(rms>1e-4,$"The mix carries the sound (RMS {rms:F4})");
            soundPlayer.StopAll();soundPlayer.Played.Clear();
            int passes=3;loop.Checked=true;clock.Loop=true;clock.Seek(0);scene.SetTime(0);await Task.Delay(80);clock.Play();await Task.Delay((int)(clip.Duration*1000*passes)-150);clock.Pause();
            Check(soundPlayer.Played.Count==events.Count*passes,$"Looping plays them once per pass ({soundPlayer.Played.Count} plays over {passes} passes)");
            soundPlayer.StopAll();soundPlayer.Played.Clear();
            clock.Seek(clip.Duration*.5);scene.SetTime(clock.Time);BeginScrub();ScrubTo(100);ScrubTo(9000);ScrubTo(200);EndScrub();clock.Pause();await Task.Delay(200);
            Check(soundPlayer.Played.Count==0,"Scrubbing across the annotations plays nothing");
            // A clip whose sounds live in a mod folder that is not mounted.
            await SelectClip(assets.Clips.First(c=>c.Path.EndsWith(@"Buster_Fusion Sword Moveset\mco_sprintattack.hkx",StringComparison.OrdinalIgnoreCase)));clock.Pause();
            var ff7=SoundLibrary.Events(clip!.Events);log.Add("FF7 sound annotations: "+string.Join(" | ",ff7.Select(e=>$"{e.Time:F3} {e.EditorId}")));
            for(int i=0;i<100&&warnings.Text.Contains("Searching");i++)await Task.Delay(100);
            log.Add("warnings:\n"+warnings.Text);
            Check(warningsTab!.Text.StartsWith("Warnings · ")&&warnings.Text.Contains("SSBU_")&&warnings.Text.Contains("FF7CloudAudioSSBU.esp in the mod folder FF7 Cloud Audio - Smash Ultimate"),"Warnings names the plugin and mod folder the missing sounds come from: "+warningsTab.Text);
            using(var window=new Bitmap(Width,Height)){inspectorTabs!.SelectedTab=warningsTab;await Task.Delay(100);DrawToBitmap(window,new Rectangle(Point.Empty,Size));window.Save(Path.Combine(output,"window-warnings.png"));}
            await Mount(Mod("FF7 Cloud Audio - Smash Ultimate"),true);
            await SelectClip(assets.Clips.First(c=>c.Path.EndsWith(@"Buster_Fusion Sword Moveset\mco_sprintattack.hkx",StringComparison.OrdinalIgnoreCase)));clock.Pause();
            foreach(var e in ff7.Where(e=>e.EditorId.StartsWith("SSBU_",StringComparison.OrdinalIgnoreCase)).DistinctBy(e=>e.EditorId)){
                var resolved=Sounds().Resolve(e.EditorId);log.Add($"{e.EditorId}: {resolved.Summary()}");
                Check(resolved.Playable,$"With the audio mod mounted {e.EditorId} resolves ({resolved.Files.Count} file(s))");
                var decoded=soundPlayer.Decode(assets,resolved.Files[0]);Check(decoded.Samples.Length>0,$"{Path.GetExtension(resolved.Files[0])} decodes ({decoded.Samples.Length/decoded.Format.Channels/(double)decoded.Format.SampleRate:F2} s)");
            }
            Check(!warnings.Text.Contains("SSBU_"),"The warnings for those sounds are gone: "+warnings.Text.Replace("\n"," / "));
            Check(CaptureSettings().PlaySounds,"The setting is saved with the session");
            // The real output path, fed silence so the test makes no noise.
            try{using var probe=new NAudio.Wave.WaveOutEvent{DesiredLatency=90};probe.Init(new NAudio.Wave.SilenceProvider(SoundPlayer.MixFormat));probe.Play();await Task.Delay(300);Check(probe.PlaybackState==NAudio.Wave.PlaybackState.Playing,"The audio device opens and plays the 44.1 kHz stereo mix format");probe.Stop();}
            catch(NAudio.MmException ex){log.Add("NOTE no audio device in this session: "+ex.Message);}
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"sound-smoke.txt"),log);}finally{Close();}}
    }
    // A queued clip with the window annotation at several times gets a drop-down to choose the hand-over.
    // Arguments: a clip with two or more MCO_WinOpen times, and a clip to follow it.
    // Follows one bone through clips on the live session: its transform relative to a reference bone at every frame,
    // whether the animation's pose has an entry for it, and how much that relative transform changes.
    // Arguments: bone, reference bone, then HKX paths (or library path suffixes). Writes track-bone.txt.
    async Task TrackBone(string[] arguments)
    {
        var log=new List<string>();
        try{
            string boneName=arguments[0],referenceName=arguments[1];
            var state=JsonSerializer.Deserialize<SessionSettings>(File.ReadAllText(ToolSessionPath()))!;
            await ApplySettings(state with {FollowCamera=false,QueuePlaying=false,SmpPhysics=false,Clip=null});
            timer.Stop();clock.Pause();
            log.Add($"skeleton: {scene.Skeleton.SkeletonDictionary.Count} bones, skeleton HKX {scene.SkeletonHkx}");
            if(scene.Skeleton.SkeletonDictionary.TryGetValue(boneName,out var b0))log.Add($"{boneName} parent chain: "+string.Join(" <- ",Enumerable.Range(0,6).Select(i=>{var x=b0;for(int k=0;k<i&&x!=null;k++)x=x.Parent;return x?.BoneName;}).TakeWhile(n=>n!=null)));
            foreach(string target in arguments.Skip(2)){
                var entry=assets.Clips.FirstOrDefault(c=>c.Path.EndsWith(Path.GetFileName(target),StringComparison.OrdinalIgnoreCase)&&(File.Exists(target)?assets.Source(c.Path).Equals(Path.GetFullPath(target),StringComparison.OrdinalIgnoreCase):true))??assets.Clips.FirstOrDefault(c=>c.Path.EndsWith(Path.GetFileName(target),StringComparison.OrdinalIgnoreCase));
                if(entry==null&&File.Exists(target))entry=assets.Drop(target);
                if(entry==null){log.Add("== not in the library: "+target);continue;}
                await SelectClip(entry);clock.Pause();timer.Stop();
                log.Add($"== {entry.Path} ({clip!.Duration:F3} s, {clip.Frames} frames, {clip.Tracks} tracks)");
                var pose=scene.PoseAt(0,"track");
                try{
                    var anim=HkxObjectGraphParser_Class.BuildGraph(HkxPackfileParser_Class.Parse(clip.Segments?[0].Clip.Bytes??clip.Bytes)).Animaciones().First();
                    var skeletonGraph=HkxObjectGraphParser_Class.BuildGraph(HkxPackfileParser_Class.Parse(HkxTagfile.EnsurePackfile(assets.Read(scene.SkeletonHkx))));
                    var embedded=HkxObjectGraphParser_Class.BuildGraph(HkxPackfileParser_Class.Parse(clip.Segments?[0].Clip.Bytes??clip.Bytes)).EsqueletoDeAnimacion();if(embedded!=null)log.Add($"   the clip embeds its own skeleton: {embedded.Name}, {embedded.Bones.Count} bones");var skeletonBones=(embedded??skeletonGraph.EsqueletoDeAnimacion())?.Bones.Select(x=>x.Name).ToList()??[];
                    var bindingList=anim.Binding?.TransformTrackToBoneIndices??[];var names=anim.TrackNames;int tracks=anim.Animacion.NumberOfTransformTracks;
                    log.Add($"   raw: {tracks} tracks, binding {(bindingList.Count==0?"none (track i = bone i)":bindingList.Count+" entries")}, {names.Count} annotation-track names, skeleton.hkx {skeletonBones.Count} bones");
                    int mismatches=0;for(int i=0;i<Math.Min(names.Count,tracks);i++){int boneIndex=bindingList.Count>i?bindingList[i]:i;string bn=boneIndex>=0&&boneIndex<skeletonBones.Count?skeletonBones[boneIndex]:"?";if(names[i].Length>0&&!names[i].Equals(bn,StringComparison.OrdinalIgnoreCase)){mismatches++;if(mismatches<=6)log.Add($"   track {i}: named {names[i]} but bound to skeleton bone {bn}");}}
                    log.Add($"   track-name / bone mismatches: {mismatches}");
                    for(int i=0;i<tracks;i++){
                        int boneIndex=bindingList.Count>i?bindingList[i]:i;string bn=boneIndex>=0&&boneIndex<skeletonBones.Count?skeletonBones[boneIndex]:"?";string tn=i<names.Count?names[i]:"";
                        if(!bn.Contains("weapon",StringComparison.OrdinalIgnoreCase)&&!tn.Contains("weapon",StringComparison.OrdinalIgnoreCase)&&!bn.Contains("R Hand",StringComparison.OrdinalIgnoreCase))continue;
                        var frames0=Enumerable.Range(0,anim.NumFrames).Select(f=>anim.GetTransform(f,i)).Where(v=>v!=null).ToList();
                        double R(int k)=>frames0.Max(v=>v![k])-frames0.Min(v=>v![k]);
                        log.Add(FormattableString.Invariant($"   track {i} -> bone {bn} (named {tn}): T range {R(0):F4}/{R(1):F4}/{R(2):F4}, quat range {R(4):F4}/{R(5):F4}/{R(6):F4}/{R(7):F4}; frame0 T ({frames0[0]![0]:F3},{frames0[0]![1]:F3},{frames0[0]![2]:F3}) q ({frames0[0]![4]:F3},{frames0[0]![5]:F3},{frames0[0]![6]:F3},{frames0[0]![7]:F3}); mask {anim.GetMask(0,i)}"));
                    }
                }catch(Exception ex){log.Add("   raw read failed: "+ex.Message);}
                log.Add($"   pose entries: {pose?.Transforms.Count}; has {boneName}: {pose?.Transforms.ContainsKey(boneName)}; has {referenceName}: {pose?.Transforms.ContainsKey(referenceName)}");
                if(!scene.Skeleton.SkeletonDictionary.TryGetValue(boneName,out var bone)||!scene.Skeleton.SkeletonDictionary.TryGetValue(referenceName,out var reference)){log.Add("   bone missing from the skeleton");continue;}
                var rows=new List<(double T,OpenTK.Mathematics.Vector3d Offset,OpenTK.Mathematics.Vector3d Euler,OpenTK.Mathematics.Vector3d LocalT,OpenTK.Mathematics.Vector3d LocalE)>();
                int frames=Math.Max(2,(int)Math.Round(clip.Duration*30));
                for(int f=0;f<=frames;f++){
                    double t=clip.Duration*f/frames;scene.SetTime(t);
                    var relative=bone.GetGlobalTransform.ToMatrix4d()*reference.GetGlobalTransform.ToMatrix4d().Inverted();
                    var local=bone.LocaLTransform.ToMatrix4d();
                    static OpenTK.Mathematics.Vector3d Euler(OpenTK.Mathematics.Matrix4d m){var q=m.ExtractRotation();var e=q.ToEulerAngles();return e*(180/Math.PI);}
                    rows.Add((t,relative.ExtractTranslation(),Euler(relative),local.ExtractTranslation(),Euler(local)));
                }
                double Range(Func<OpenTK.Mathematics.Vector3d,double> pick,IEnumerable<OpenTK.Mathematics.Vector3d> values){var v=values.Select(pick).ToList();return v.Max()-v.Min();}
                var offsets=rows.Select(r=>r.Offset).ToList();var eulers=rows.Select(r=>r.Euler).ToList();
                log.Add($"   {boneName} relative to {referenceName} over {rows.Count} frames: offset range X {Range(v=>v.X,offsets):F3} Y {Range(v=>v.Y,offsets):F3} Z {Range(v=>v.Z,offsets):F3} units; rotation range {Range(v=>v.X,eulers):F2} / {Range(v=>v.Y,eulers):F2} / {Range(v=>v.Z,eulers):F2} degrees");
                foreach(var r in rows.Where((_,i)=>i%Math.Max(1,rows.Count/8)==0))log.Add(FormattableString.Invariant($"   t {r.T:F3}: offset ({r.Offset.X:F2}, {r.Offset.Y:F2}, {r.Offset.Z:F2}) rot ({r.Euler.X:F1}, {r.Euler.Y:F1}, {r.Euler.Z:F1}) | local T ({r.LocalT.X:F2}, {r.LocalT.Y:F2}, {r.LocalT.Z:F2}) rot ({r.LocalE.X:F1}, {r.LocalE.Y:F1}, {r.LocalE.Z:F1})"));
                if(pose!=null&&pose.Transforms.TryGetValue(boneName,out var raw0)){
                    var samples=Enumerable.Range(0,frames+1).Select(f=>scene.PoseAt(clip.Duration*f/frames,"track")!.Transforms[boneName]).ToList();
                    log.Add(FormattableString.Invariant($"   raw pose {boneName}: X {samples.Min(p=>p.X):F3}..{samples.Max(p=>p.X):F3} Y {samples.Min(p=>p.Y):F3}..{samples.Max(p=>p.Y):F3} Z {samples.Min(p=>p.Z):F3}..{samples.Max(p=>p.Z):F3} yaw {samples.Min(p=>p.Yaw):F2}..{samples.Max(p=>p.Yaw):F2} pitch {samples.Min(p=>p.Pitch):F2}..{samples.Max(p=>p.Pitch):F2} roll {samples.Min(p=>p.Roll):F2}..{samples.Max(p=>p.Roll):F2}"));
                }
            }
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"track-bone.txt"),log);}finally{Close();}}
    }
    // BFCO clips in the window: the speed note, the BFCO notes, the lasting annotations and a queue that hands over at
    // BFCO's windows. Arguments: three BFCO attack clips of one moveset, in combo order.
    async Task BfcoSmoke(string[] clips)
    {
        var log=new List<string>();string output=Path.Combine(AppContext.BaseDirectory,"bfco-smoke");Directory.CreateDirectory(output);
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        try{
            await Mount(SelfTest.TestData,false);
            followCamera.Checked=false;scene.FollowScene=false;playSounds.Checked=false;
            clips=clips.Select(Path.GetFullPath).ToArray();
            DropFiles(clips);await Task.Delay(400);
            ClipEntry Entry(string file)=>assets.Clips.First(c=>c.Path.Equals(file,StringComparison.OrdinalIgnoreCase));
            await SelectClip(Entry(clips[0]));clock.Pause();inspectorTabs!.SelectedIndex=1;await Task.Delay(150);
            var source=AnimationClip.Load(File.ReadAllBytes(clips[0]));string name=Path.GetFileName(clips[0]);
            log.Add($"{name}: {source.Duration:F3} s, BFCO speed annotations {source.BfcoSpeedChanges.Count}, MCO speed annotations {source.SpeedChanges.Count}");
            foreach(var e in source.Events.Where(e=>e.Text.Contains("BFCO",StringComparison.OrdinalIgnoreCase)||e.Text.StartsWith("MCO_",StringComparison.OrdinalIgnoreCase)))log.Add($"   {e.Time:F3} {e.Text}");
            Check(Bfco.IsClip(name,source.Events)&&events.Text.Contains("\nBFCO\n")&&events.Text.Contains("Read as a BFCO clip"),"A BFCO clip gets the BFCO notes in the Annotations tab");
            var nextLight=Bfco.Next(source.Events,false);var lightWindow=Bfco.Window(name,source.Events,"MCO_WinOpen");
            Check(nextLight!=null&&events.Text.Contains($"Next light attack: BFCO_Attack{nextLight.Value.Number}.hkx"),$"The notes name the next light clip (BFCO_Attack{nextLight?.Number})");
            Check(lightWindow.Annotation=="BFCO_NextWinStart"&&lightWindow.Times.Count>0,"The clip's light window is BFCO_NextWinStart");
            var effective=AnimationClip.EffectiveSpeedChanges(name,source);
            Check(!speedNote.Text.Contains("ignored")&&(effective.Count==0||speedNote.Text.StartsWith("BFCO speed")),"BFCO_AttackSpeed is applied to a BFCO clip, not reported as ignored: '"+speedNote.Text+"'");
            if(effective.Any(c=>Math.Abs(c.Value-1)>1e-9)){
                double expected=0,at=0,rate=1;foreach(var (time,value) in effective){expected+=(time-at)/rate;at=time;rate=value;}expected+=(source.Duration-at)/rate;
                Check(Math.Abs(clock.Duration-expected)<1e-6,$"The clip plays in {clock.Duration:F3} s at its BFCO speed, the value summed piece by piece ({expected:F3} s)");
            }
            var rows=Highlight.Rows;void Go(double time){clock.Seek(time);scene.SetTime(clock.Time);UpdateTimeLabel(clock.Time);}
            int selector=Enumerable.Range(0,rows.Count).First(i=>rows[i].Text.Trim().StartsWith("BFCO_NextIsAttack",StringComparison.OrdinalIgnoreCase));
            int window=Enumerable.Range(0,rows.Count).First(i=>rows[i].Text.Trim().Equals("BFCO_NextWinStart",StringComparison.OrdinalIgnoreCase));
            int close=Enumerable.Range(0,rows.Count).Where(i=>rows[i].Text.Trim().Equals("BFCO_DIY_EndLoop",StringComparison.OrdinalIgnoreCase)&&rows[i].Time>rows[window].Time).DefaultIfEmpty(-1).First();
            Go(rows[window].Time+.01);
            Check(Highlight.Active.Contains(selector)&&Highlight.Active.Contains(window),"The next-attack annotation and BFCO_NextWinStart are highlighted once the window has opened");
            if(close>=0){Go(rows[close].Time+.01);Check(!Highlight.Active.Contains(selector)&&!Highlight.Active.Contains(window),"BFCO_DIY_EndLoop ends both highlights");}
            else{Go(clock.Duration);Check(Highlight.Active.Contains(selector)&&Highlight.Active.Contains(window),"With no BFCO_DIY_EndLoop both stay highlighted to the end of the clip");}
            using(var shot=new Bitmap(Width,Height)){DrawToBitmap(shot,new Rectangle(Point.Empty,Size));shot.Save(Path.Combine(output,"bfco-annotations.png"));}
            foreach(string file in clips)AddToQueue(Entry(file));
            queueAtWindow.Checked=true;queueWindowEvent.SelectedItem="MCO_WinOpen";inspectorTabs.SelectedTab=queueTab;await PlayQueue();clock.Pause();await Task.Delay(200);
            var segments=clip!.Segments!;var singles=clips.Select(f=>AnimationClip.Load(File.ReadAllBytes(f))).ToList();
            for(int i=0;i<segments.Count-1;i++){
                var expected=Bfco.Window(Path.GetFileName(clips[i]),singles[i].Events,"MCO_WinOpen");
                Check(expected.Annotation=="BFCO_NextWinStart"&&segments[i].Window=="BFCO_NextWinStart"&&Math.Abs(segments[i].HandOver-expected.Times[0])<1e-9,$"Clip {i+1} hands over to clip {i+2} at its BFCO_NextWinStart ({segments[i].HandOver:F3} s)");
            }
            Check(segments[^1].Window==null&&queueList.Items.Cast<string>().First().Contains("next at BFCO_NextWinStart"),"The queue list names BFCO's window: "+queueList.Items[0]);
            Check(queueWindowEvent.GetItemText(queueWindowEvent.SelectedItem)=="MCO_WinOpen / BFCO_NextWinStart"&&QueueWindowEvent=="MCO_WinOpen","The hand-over drop-down shows both names and keeps its saved value");
            queueWindowEvent.SelectedItem="MCO_PowerWinOpen";await Task.Delay(700);clock.Pause();
            var powered=clip!.Segments!;var powerWindow=Bfco.Window(Path.GetFileName(clips[0]),singles[0].Events,"MCO_PowerWinOpen");
            Check(powerWindow.Annotation==null?powered[0].Window==null:powered[0].Window==powerWindow.Annotation&&Math.Abs(powered[0].HandOver-powerWindow.Times[0])<1e-9,$"The power choice hands over at {powerWindow.Annotation??"the end of the clip"}");
            cameraSubject.SelectedIndex=1;Go(clock.Duration*.4);scene.Frame();viewport.InvalidateRender();await Task.Delay(200);
            using(var shot=new Bitmap(Width,Height)){DrawToBitmap(shot,new Rectangle(Point.Empty,Size));shot.Save(Path.Combine(output,"bfco-queue.png"));}
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"bfco-smoke.txt"),log);}finally{Close();}}
    }
    // Frames for an animated picture, played in real time so HDT-SMP physics and effects run as they do on screen.
    // Arguments: a scene file, an output folder, seconds to record, then options: fps=15, warm=1.5 (seconds played
    // before recording), clip=<part of a clip path>, queue=<part>|<part>|..., precision, skeleton, smp, colliders, bones, window (the
    // whole window, not the viewport alone), tab=<index>, follow, subject=<0..2>, rotate=<dx>,<dy>, zoom=<steps>,
    // speed=<rate>, size=<width>x<height>. Frames are frame_0000.png and up; frames.txt holds the real frame rate.
    async Task FramesCapture(string[] arguments)
    {
        string output=Path.GetFullPath(arguments[1]);Directory.CreateDirectory(output);var log=new List<string>();
        var invariant=System.Globalization.CultureInfo.InvariantCulture;
        string? Option(string name)=>arguments.Skip(3).FirstOrDefault(a=>a.StartsWith(name+"=",StringComparison.OrdinalIgnoreCase))?[(name.Length+1)..];
        bool Flag(string name)=>arguments.Skip(3).Any(a=>a.Equals(name,StringComparison.OrdinalIgnoreCase));
        double Number(string name,double fallback)=>double.TryParse(Option(name),System.Globalization.NumberStyles.Float,invariant,out double value)?value:fallback;
        try{
            foreach(string old in Directory.GetFiles(output,"frame_*.png"))File.Delete(old);
            var size=(Option("size")??"1600x1000").Split('x');Size=new Size(int.Parse(size[0],invariant),int.Parse(size[1],invariant));
            await RestoreFile(Path.GetFullPath(arguments[0]));
            ClipEntry Find(string part)=>assets.Clips.First(c=>c.Path.Contains(part,StringComparison.OrdinalIgnoreCase));
            if(Option("clip") is {} wanted)await SelectClip(Find(wanted));
            if(Option("queue") is {} parts){foreach(string part in parts.Split('|'))AddToQueue(Find(part));inspectorTabs!.SelectedTab=queueTab;await PlayQueue();}
            if(Option("tab") is {} tab)inspectorTabs!.SelectedIndex=int.Parse(tab,invariant);
            precisionShow.Checked=Flag("precision");showSkeleton.Checked=Flag("skeleton");if(Flag("smp"))smpPhysics.Checked=true;
            // A recording never plays the clip's sounds out loud.
            playSounds.Checked=false;soundPlayer.StopAll();smpColliders.Checked=Flag("colliders");smpBones.Checked=Flag("bones");
            if(Option("speed")!=null)speed.Value=(decimal)Number("speed",1);
            if(Option("subject")!=null)cameraSubject.SelectedIndex=(int)Number("subject",1);
            followCamera.Checked=Flag("follow");scene.FollowScene=Flag("follow");
            clock.Seek(0);scene.SetTime(0);scene.Frame();
            if(Option("rotate") is {} turn){var t=turn.Split(',');viewport.camera.Rotate(float.Parse(t[0],invariant),float.Parse(t[1],invariant));}
            if(Option("zoom")!=null)viewport.Zoom((float)Number("zoom",0));
            double fps=Number("fps",15),seconds=double.Parse(arguments[2],invariant);bool window=Flag("window");
            clock.Seek(0);clock.Play();await Task.Delay((int)(Number("warm",1.5)*1000));
            if(Flag("restart")){clock.Seek(0);clock.Play();}
            var watch=System.Diagnostics.Stopwatch.StartNew();int frames=(int)Math.Round(seconds*fps);
            for(int i=0;i<frames;i++){
                double due=i/fps*1000;if(watch.Elapsed.TotalMilliseconds<due)await Task.Delay(Math.Max(1,(int)(due-watch.Elapsed.TotalMilliseconds)));else await Task.Yield();
                Bitmap frame;
                if(window){
                    frame=new Bitmap(Width,Height);DrawToBitmap(frame,new Rectangle(Point.Empty,Size));
                    using var view=viewport.CaptureBitmap();var origin=viewport.PointToScreen(Point.Empty);
                    using var g=Graphics.FromImage(frame);g.DrawImage(view,origin.X-Left,origin.Y-Top,viewport.Width,viewport.Height);
                }else frame=viewport.CaptureBitmap();
                using(frame)frame.Save(Path.Combine(output,$"frame_{i:0000}.png"));
            }
            double real=frames/watch.Elapsed.TotalSeconds;
            log.Add(real.ToString("F3",invariant));log.Add($"{frames} frames in {watch.Elapsed.TotalSeconds:F2} s, asked {fps} fps; clip {clipTitle.Text}, duration {clock.Duration:F2} s, {scene.ParticleSystems} particle systems, physics {smpPhysics.Checked}");
            foreach(string d in scene.Diagnostics.Where(d=>d.StartsWith("Missing")).Take(8))log.Add(d);
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"frames.txt"),log);}finally{Close();}}
    }
    // Screenshots for the documentation. Arguments: an output folder, a Skyrim Data folder (or the test data), then
    // attack clips. The character is a Nord in iron armour with an iron battleaxe, so the pictures show game assets only.
    async Task DocsCapture(string[] arguments)
    {
        string output=Path.GetFullPath(arguments[0]);Directory.CreateDirectory(output);var log=new List<string>();
        Bitmap Window(){
            var window=new Bitmap(Width,Height);DrawToBitmap(window,new Rectangle(Point.Empty,Size));
            viewport.InvalidateRender();using var view=viewport.CaptureBitmap();var origin=viewport.PointToScreen(Point.Empty);
            using var g=Graphics.FromImage(window);g.DrawImage(view,origin.X-Left,origin.Y-Top,viewport.Width,viewport.Height);return window;
        }
        async Task SaveWindow(string name){await Task.Delay(350);using var b=Window();b.Save(Path.Combine(output,name+".png"));log.Add(name);}
        async Task SaveView(string name){viewport.InvalidateRender();await Task.Delay(300);viewport.InvalidateRender();using var b=viewport.CaptureBitmap();b.Save(Path.Combine(output,name+".png"));log.Add(name);}
        void Go(double time){clock.Seek(time);scene.SetTime(clock.Time);UpdateTimeLabel(clock.Time);}
        try{
            Size=new Size(2200,1400);
            await Mount(arguments[1],false);
            followCamera.Checked=false;scene.FollowScene=false;gridFloor.Checked=true;
            foreach(string id in new[]{"ArmorIronCuirass","ArmorIronBoots","ArmorIronGauntlets"})if(records.Of("ARMO").FirstOrDefault(r=>r.EditorId==id) is {} piece)worn.Add(piece);
            if(records.Of("WEAP").FirstOrDefault(w=>w.EditorId=="IronBattleaxe") is {} axe)weapons.SelectedItem=axe;
            RebuildScene();
            string[] clips=arguments.Skip(2).Select(Path.GetFullPath).ToArray();
            DropFiles(clips);await Task.Delay(500);
            ClipEntry Entry(string file)=>assets.Clips.First(c=>c.Path.Equals(file,StringComparison.OrdinalIgnoreCase));
            await SelectClip(Entry(clips[0]));clock.Pause();timer.Stop();
            cameraSubject.SelectedIndex=1;Go(clock.Duration*.3);scene.Frame();viewport.camera.Rotate(-35,8);
            inspectorTabs!.SelectedIndex=1;
            await SaveWindow("overview");await SaveView("character");
            var hit=scene.Precision?.Collisions.FirstOrDefault();
            if(hit!=null){precisionShow.Checked=true;Go((hit.Start+hit.End)/2);await SaveView("precision");await SaveWindow("precision-window");precisionShow.Checked=false;}
            Go(clock.Duration*.3);
            showSkeleton.Checked=true;await SaveView("skeleton");
            showBoneNames.Checked=true;scene.BoneFilter.Clear();foreach(string bone in scene.Skeleton.SkeletonDictionary.Keys.Where(k=>new[]{"NPC R Hand [RHnd]","NPC L Hand [LHnd]","NPC Head [Head]","NPC Pelvis [Pelv]","NPC L Foot [Lft ]","NPC R Foot [Rft ]","WEAPON"}.Contains(k,StringComparer.OrdinalIgnoreCase)))scene.BoneFilter.Add(bone);
            await SaveView("skeleton-names");
            scene.BoneFilter.Clear();showBoneNames.Checked=false;showSkeleton.Checked=false;
            Go(clock.Duration*.85);cameraSubject.SelectedIndex=0;scene.Frame();viewport.camera.Rotate(0,25);await SaveView("root-motion");
            foreach(string file in clips)AddToQueue(Entry(file));
            inspectorTabs.SelectedTab=queueTab;await PlayQueue();clock.Pause();timer.Stop();
            cameraSubject.SelectedIndex=1;Go(clock.Duration*.45);scene.Frame();viewport.camera.Rotate(-35,8);await SaveWindow("queue");
            string? effect=new[]{@"meshes\magic\fireballexp01.nif",@"meshes\magic\explosionfrost01.nif",@"meshes\magic\firestormexplosion.nif",@"meshes\magic\fxfireexplosion01.nif",@"meshes\magic\fireboltimpact01.nif"}.FirstOrDefault(assets.Exists)
                ??assets.Paths.Where(p=>p.StartsWith(@"meshes\magic\",StringComparison.OrdinalIgnoreCase)&&p.Contains("explosion",StringComparison.OrdinalIgnoreCase)).OrderBy(p=>p).FirstOrDefault();
            if(effect!=null){
                PreviewParticle(effect);clock.Pause();timer.Stop();
                double best=.4;int most=-1;for(double t=.3;t<Math.Min(clock.Duration,2.5);t+=.1){clock.Seek(t);scene.SetTime(t);if(scene.ParticleCount>most){most=scene.ParticleCount;best=t;}}
                Go(best);scene.Frame();viewport.Zoom(24);inspectorTabs.SelectedIndex=2;await SaveView("effect");await SaveWindow("effect-window");log.Add($"effect {effect} at {best:F1} s, {most} particles");
            }
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"docs-capture.txt"),log);}finally{Close();}}
    }
    // Library highlight and hover text on the catalog fixture (--catalog-test writes it): the playing clip's row is green,
    // orange when a higher-priority folder holds the same file, and the tooltip names those folders.
    async Task OverrideSmoke()
    {
        var log=new List<string>();string output=Path.Combine(AppContext.BaseDirectory,"override-smoke");Directory.CreateDirectory(output);
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        try{
            string fixture=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,@"..\..\..\..\verification\catalog-fixture"));
            await assets.Mount(fixture,false,new Progress<string>());
            search.Text="ovr_";BuildTree();tree.ExpandAll();await Task.Delay(200);
            IEnumerable<TreeNode> Nodes(TreeNodeCollection nodes){foreach(TreeNode n in nodes){yield return n;foreach(var child in Nodes(n.Nodes))yield return child;}}
            TreeNode Node(string part)=>Nodes(tree.Nodes).Single(n=>n.Tag is ClipEntry c&&c.Path.Contains(part,StringComparison.OrdinalIgnoreCase));
            Color Row(TreeNode node){node.EnsureVisible();tree.Refresh();using var bitmap=new Bitmap(tree.Width,tree.Height);tree.DrawToBitmap(bitmap,tree.ClientRectangle);return bitmap.GetPixel(tree.ClientSize.Width-12,node.Bounds.Top+3);}
            bool Same(Color a,Color b)=>Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B)<12;
            TreeNode low=Node(@"Override low\ovr_attack"),top=Node(@"_variants_ovr_attack\a.hkx"),plain=Nodes(tree.Nodes).Single(n=>n.Tag is ClipEntry c&&c.Framework==ClipOverrides.Plain&&c.Name=="ovr_attack.hkx"),alone=Node("ovr_only_low");
            Check(!Same(Row(low),PlayingRow)&&!Same(Row(low),OverriddenRow),"A clip that is not playing has no highlight");
            selected=(ClipEntry)low.Tag;tree.SelectedNode=alone;
            Check(Same(Row(low),OverriddenRow),$"The playing clip is orange when higher-priority folders hold the same file ({Row(low)})");
            Check(!Same(Row(top),OverriddenRow)&&!Same(Row(top),PlayingRow)&&!Same(Row(plain),OverriddenRow),"Only the playing clip is highlighted");
            using(var bitmap=new Bitmap(tree.Width,tree.Height)){tree.DrawToBitmap(bitmap,tree.ClientRectangle);bitmap.Save(Path.Combine(output,"library-overridden.png"));}
            selected=(ClipEntry)top.Tag;
            Check(Same(Row(top),PlayingRow)&&!Same(Row(low),OverriddenRow),$"The playing clip is green when nothing overrides it, and the highlight moves with the clip ({Row(top)})");
            tree.SelectedNode=top;Check(Same(Row(top),PlayingRow),"The playing colour replaces the selection colour on the selected row");
            using(var bitmap=new Bitmap(tree.Width,tree.Height)){tree.DrawToBitmap(bitmap,tree.ClientRectangle);bitmap.Save(Path.Combine(output,"library-playing.png"));}
            selected=(ClipEntry)plain.Tag;Check(Same(Row(plain),OverriddenRow),"A plain file that OAR folders replace is orange");
            log.Add("--- tooltip of Override low\\ovr_attack.hkx");log.Add(low.ToolTipText);
            Check(low.ToolTipText.Contains("Overridden by 3 higher-priority folders")&&low.ToolTipText.Contains(@"Override top")&&low.ToolTipText.Contains(@"Override high")&&low.ToolTipText.Contains(@"DynamicAnimationReplacer\_CustomConditions\650")&&!low.ToolTipText.Contains(@"Override off"),"Hovering a clip lists the submods and folders that override it");
            Check(plain.ToolTipText.Contains("Overridden by 4")&&!top.ToolTipText.Contains("Overridden")&&!alone.ToolTipText.Contains("Overridden"),"Clips nothing overrides keep the plain path tooltip");
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"override-smoke.txt"),log);}finally{Close();}}
    }
    // Sub-texture sheets: every live particle of each NIF must sample one cell of its sheet, (u, width, v, height) as the
    // game stores it, so a cell is never wider than the sheet divided by its columns. Arguments: a mod folder, then NIF
    // paths under it. Writes a capture of each.
    async Task FlipbookSmoke(string[] arguments)
    {
        var log=new List<string>();string output=Path.Combine(AppContext.BaseDirectory,"flipbook-smoke");Directory.CreateDirectory(output);
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        try{
            // SSE_FLIPBOOK_MOD_ONLY=1 leaves the test data unmounted, to see a NIF whose textures are missing.
            if(Environment.GetEnvironmentVariable("SSE_FLIPBOOK_MOD_ONLY")!="1")await Mount(SelfTest.TestData,false);
            await Mount(arguments[0],true);
            followCamera.Checked=false;scene.FollowScene=false;
            foreach(string nif in arguments.Skip(1)){
                PreviewParticle(nif,10);clock.Pause();clock.Seek(.6);scene.SetTime(.6);await Task.Delay(300);
                int systems=0,sheets=0,particlesChecked=0;float widest=0,narrowest=1;var seen=new HashSet<(float,float)>();
                foreach(var system in scene.ParticleInstances){
                    systems++;
                    var offsets=system.SheetCells;if(offsets.Length==0)continue;sheets++;
                    foreach(var particle in system.Simulation.Particles){
                        var uv=system.UV(particle);float w=uv.Z-uv.X,h=uv.W-uv.Y;particlesChecked++;
                        widest=Math.Max(widest,Math.Max(w,h));narrowest=Math.Min(narrowest,Math.Min(w,h));seen.Add((MathF.Round(uv.X,3),MathF.Round(uv.Y,3)));
                        Check(w>0&&h>0&&uv.X>=0&&uv.Y>=0&&uv.Z<=1.0001f&&uv.W<=1.0001f,$"{system.Name}: a cell is a proper rectangle inside the sheet ({uv})");
                    }
                    var first=offsets[0];
                    log.Add($"   {system.Name}: {offsets.Length} cells, first stored as ({first.X}, {first.Y}, {first.Z}, {first.W}), {system.Simulation.Particles.Count} live particles");
                }
                log.Add($"== {nif}: {systems} systems, {sheets} with sheets, {particlesChecked} particles checked, cell size {narrowest:F4}..{widest:F4}, {seen.Count} distinct cell origins");
                Check(sheets>0&&particlesChecked>0,"The NIF has sub-texture sheets with live particles at 0.6 s");
                Check(widest<=.5f+1e-4f&&seen.Count>1,"Cells are sheet cells, not the whole sheet, and particles sit on different cells");
                scene.Frame();viewport.InvalidateRender();await Task.Delay(200);using var image=viewport.CaptureBitmap();image.Save(Path.Combine(output,Path.GetFileNameWithoutExtension(nif)+".png"));
            }
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"flipbook-smoke.txt"),log);}finally{Close();}}
    }
    // PIE.@FF7VFX on the three Limit Break clips of the FF7 Limit Gauge mod, in the live session (Cloud and his sword).
    async Task Ff7VfxSmoke()
    {
        var log=new List<string>();string output=Path.Combine(AppContext.BaseDirectory,"ff7vfx-smoke");Directory.CreateDirectory(output);
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        static bool Near(System.Numerics.Vector3 a,System.Numerics.Vector3 b,float tolerance=.01f)=>(a-b).Length()<=tolerance;
        try{
            var state=JsonSerializer.Deserialize<SessionSettings>(File.ReadAllText(ToolSessionPath()))!;
            await ApplySettings(state with {FollowCamera=false,QueuePlaying=false,SmpPhysics=false,Clip=null,Effects=true,PrecisionCollisions=false,ShowSkeleton=false,GridFloor=true});
            timer.Stop();clock.Pause();
            foreach(string level in new[]{"Limit Break","Limit Break 2","Limit Break 3"}){
                var entry=assets.Clips.First(c=>c.Path.EndsWith(@"FF7 Limit Gauge\"+level+@"\mco_powerattack1.hkx",StringComparison.OrdinalIgnoreCase));
                await SelectClip(entry);clock.Pause();timer.Stop();
                // SSE_FF7VFX_ONLY=<part of a NIF name>: load only those effects (and no others) and capture two views, for a close look.
                if(Environment.GetEnvironmentVariable("SSE_FF7VFX_ONLY") is {Length:>0} only){
                    cues=cues.Where(c=>c.Origin.StartsWith("@FF7VFX|")&&c.Model.Contains(only,StringComparison.OrdinalIgnoreCase)).ToList();RebuildScene();clock.Pause();timer.Stop();
                    foreach(var c in cues.Take(2)){
                        double t=c.Time+Math.Min(.3,c.Lifetime*.4);clock.Seek(t);scene.SetTime(t);scene.Frame();float d=viewport.camera.distance;
                        foreach(var (name,yaw,pitch) in new[]{("side",-90f,5f),("front",0f,5f),("three-quarter",-55f,20f)}){
                            viewport.camera=new OrbitCamera{MaxDistance=100000};viewport.camera.Rotate(yaw,pitch);var r0=scene.RootPosition;viewport.camera.FocusPosition=new OpenTK.Mathematics.Vector3(r0.X,r0.Y+40,70);viewport.camera.distance=d*1.8f;viewport.UpdateProjection(true);
                            viewport.InvalidateRender();await Task.Delay(250);viewport.InvalidateRender();await Task.Delay(100);
                            using var shot=viewport.CaptureBitmap();shot.Save(Path.Combine(output,$"only-{only}-{level.Replace(' ','-')}-{c.Time:0.00}-{name}.png"));
                        }
                        log.Add($"only {only}: {c.Origin} at {c.Time:F3}, captured at {t:F3}; drawn particles {scene.DrawnParticles}, visible effect meshes {scene.VisibleEffectMeshes}");
                    }
                    continue;
                }
                var annotations=clip!.Events.Where(e=>e.Text.StartsWith("PIE.@FF7VFX|")).ToList();
                var vfx=cues.Where(c=>c.Origin.StartsWith("@FF7VFX|")).ToList();var roots=vfx.Where(c=>!c.BladeSpace).ToList();var held=vfx.Where(c=>c.BladeSpace).ToList();
                log.Add($"== {level}: {annotations.Count} @FF7VFX annotations -> {roots.Count} root cue(s), {held.Count} weapon cue(s); blade axis {"+X +Y +Z -X -Y -Z".Split(' ')[scene.WeaponBladeAxis()]}");
                Check(annotations.Count>0&&roots.Count==annotations.Count(a=>a.Text.Split('|')[2]=="root"),"Every root annotation gives one effect");
                foreach(var group in held.GroupBy(c=>c.Origin)){
                    var first=group.OrderBy(c=>c.Time).First();int expected=(int)Math.Ceiling((first.RemovedAt-first.Time)/first.Lifetime-1e-6);
                    log.Add($"   weapon {Path.GetFileName(first.Model)}: from {first.Time:F3} s, {first.Lifetime} s each, until {first.RemovedAt:F3} s -> {group.Count()} spawn(s)");
                    Check(group.Count()==expected&&group.All(c=>c.RemovedAt==first.RemovedAt)&&first.RemovedAt<first.Time+Effects.Ff7VfxMaximum,"A weapon effect is spawned again every <seconds> until its @FF7VFXEND");
                }
                var missing=vfx.Where(c=>!assets.Exists(c.Model)).Select(c=>c.Model).Distinct().ToList();
                Check(missing.Count==0,"Every effect NIF is found in the mounted folders"+(missing.Count>0?": missing "+string.Join(", ",missing):""));
                foreach(var cue in roots){
                    var placement=scene.CuePlacement(cue,.1);if(placement==null){log.Add("   not loaded: "+cue.Model);continue;}
                    var fields=cue.Origin.Split('|');var motion=clip.Motion(cue.Time);
                    var actor=System.Numerics.Matrix4x4.CreateRotationZ(motion.Yaw)*System.Numerics.Matrix4x4.CreateTranslation(motion.Position);
                    var offset=new System.Numerics.Vector3(float.Parse(fields[4],System.Globalization.CultureInfo.InvariantCulture),float.Parse(fields[5],System.Globalization.CultureInfo.InvariantCulture),float.Parse(fields[6],System.Globalization.CultureInfo.InvariantCulture));
                    var origin=System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero,placement.Value);
                    bool placed=Near(origin,System.Numerics.Vector3.Transform(offset,actor));
                    // R*p for p = +X is R's first column: fields 7, 10, 13.
                    var column=fields.Length>=16?new System.Numerics.Vector3(float.Parse(fields[7],System.Globalization.CultureInfo.InvariantCulture),float.Parse(fields[10],System.Globalization.CultureInfo.InvariantCulture),float.Parse(fields[13],System.Globalization.CultureInfo.InvariantCulture)):System.Numerics.Vector3.UnitX;
                    var xAxis=System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitX,placement.Value);
                    bool turned=Near(xAxis,System.Numerics.Vector3.TransformNormal(column,actor),.002f);
                    bool stays=Near(System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero,scene.CuePlacement(cue,cue.Lifetime*.9)!.Value),origin);
                    if(!(placed&&turned&&stays))log.Add($"   {cue.Origin}: origin {origin}, x axis {xAxis}");
                    Check(placed&&turned&&stays,$"{Path.GetFileName(cue.Model)} at {cue.Time:F3} s sits at root + offset ({offset.X}, {offset.Y}, {offset.Z}), turned by its matrix, and stays there");
                }
                foreach(var cue in held){
                    double age=cue.Lifetime*.5;if(cue.Time+age>=clip.Duration)continue;
                    var placement=scene.CuePlacement(cue,age);if(placement==null){log.Add("   not loaded: "+cue.Model);continue;}
                    scene.SetTime(cue.Time+age);
                    // The weapon node's blade axis in the world. (The sword is a skinned HDT mesh, so its vertices cannot be used.)
                    var motion=clip.Motion(cue.Time+age);var actor=OpenTK.Mathematics.Matrix4d.CreateRotationZ(motion.Yaw)*OpenTK.Mathematics.Matrix4d.CreateTranslation(motion.Position.X,motion.Position.Y,motion.Position.Z);
                    var node=scene.Skeleton.SkeletonDictionary.First(k=>k.Key.Equals("WEAPON",StringComparison.OrdinalIgnoreCase)).Value.GetGlobalTransform.ToMatrix4d()*actor;
                    int axis=scene.WeaponBladeAxis();var row=axis%3==0?node.Row0:axis%3==1?node.Row1:node.Row2;
                    var blade=System.Numerics.Vector3.Normalize(new System.Numerics.Vector3((float)row.X,(float)row.Y,(float)row.Z))*(axis<3?1:-1);var grip=node.ExtractTranslation();
                    var origin=System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero,placement.Value);var z=System.Numerics.Vector3.Normalize(System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitZ,placement.Value));
                    double angle=Math.Acos(Math.Clamp(System.Numerics.Vector3.Dot(z,blade),-1,1))*180/Math.PI;float distance=(origin-new System.Numerics.Vector3((float)grip.X,(float)grip.Y,(float)grip.Z)).Length();
                    log.Add($"   weapon {Path.GetFileName(cue.Model)} at {cue.Time+age:F3} s: origin to WEAPON node {distance:F2} units, NIF +Z to the node's blade axis {angle:F1} deg; weapon bound {scene.WeaponBoundText}");
                    Check(distance<.5f&&angle<1,$"{Path.GetFileName(cue.Model)} follows the WEAPON node with +Z along its blade axis");
                }
                // A fixed three-quarter view from in front and above, wide enough for effects 100 units ahead of the actor.
                scene.SetTime(0);cameraSubject.SelectedIndex=1;scene.Frame();float baseDistance=viewport.camera.distance;
                foreach(double fraction in new[]{.2,.35,.45,.55,.7,.85}){
                    double t=clip.Duration*fraction;clock.Seek(t);scene.SetTime(t);scene.Frame();
                    viewport.camera=new OrbitCamera{MaxDistance=100000};viewport.camera.Rotate(-55,20);var root=scene.RootPosition;viewport.camera.FocusPosition=new OpenTK.Mathematics.Vector3(root.X,root.Y+40,70);viewport.camera.distance=baseDistance*2.2f;viewport.UpdateProjection(true);
                    viewport.InvalidateRender();await Task.Delay(250);viewport.InvalidateRender();await Task.Delay(100);
                    using var image=viewport.CaptureBitmap();image.Save(Path.Combine(output,$"{level.Replace(' ','-')}-{t:0.00}s.png"));
                }
                var problems=scene.Diagnostics.Concat(effects.Diagnostics).Where(d=>d.Contains("RebirthVFX",StringComparison.OrdinalIgnoreCase)||d.Contains("FF7VFX")).Distinct().ToList();
                log.Add($"   diagnostics mentioning these effects: {problems.Count}");foreach(string p in problems.Take(12))log.Add("     "+p);
            }
            {
                // follow: like root, but the effect moves and turns with the actor. No clip uses it yet, so it is checked on
                // a synthetic instruction against a clip with root motion.
                var moving=assets.Clips.Where(c=>c.Path.Contains("FF7 Limit Gauge",StringComparison.OrdinalIgnoreCase)||c.Path.Contains("FF7 Cloud Animations",StringComparison.OrdinalIgnoreCase)).Select(c=>(Entry:c,Clip:AnimationClip.Load(assets.Read(c.Path)))).First(x=>x.Clip.Translation.Count>4&&x.Clip.Duration>1&&(x.Clip.Motion(x.Clip.Duration*.8).Position-x.Clip.Motion(x.Clip.Duration*.2).Position).Length()>20);
                await SelectClip(moving.Entry);clock.Pause();timer.Stop();
                double at=clip!.Duration*.2,age=clip.Duration*.6;
                System.Numerics.Matrix4x4 Actor(double t){var m=clip.Motion(t);return System.Numerics.Matrix4x4.CreateRotationZ(m.Yaw)*System.Numerics.Matrix4x4.CreateTranslation(m.Position);}
                var offset=new System.Numerics.Vector3(10,84,5);
                EffectCue One(string attach)=>effects.Preview(new Trigger("test",$"@FF7VFX|RebirthVFX/Cmn_Smoke_01.nif|{attach}|9|10|84|5","smoke"),at).Single();
                var follow=One("follow");var root=One("root");
                var followed=System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero,scene.EffectPlacement(follow,age));var stayed=System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero,scene.EffectPlacement(root,age));
                float travel=(clip.Motion(at+age).Position-clip.Motion(at).Position).Length();
                log.Add($"== follow, on {moving.Entry.Name}: the actor travels {travel:F1} units between {at:F2} s and {at+age:F2} s");
                Check(travel>10&&Near(followed,System.Numerics.Vector3.Transform(offset,Actor(at+age)))&&Near(stayed,System.Numerics.Vector3.Transform(offset,Actor(at))),"A follow effect keeps its offset from the moving actor; a root effect stays where it was spawned");
                Check(follow is {FollowActor:true,BladeSpace:false}&&follow.Lifetime==9&&effects.Preview(new Trigger("test","@FF7VFX|RebirthVFX/Cmn_Smoke_01.nif|follow|1.5|0|0|0","smoke"),0).Count==1,"A follow effect plays once for its seconds, with no respawn");
            }
            Check(warnings.Text!=null,"done");
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"ff7vfx-smoke.txt"),log);}finally{Close();}}
    }
    // Annotations tab highlight on a real clip with several windows, speed changes and Precision collisions.
    async Task AnnotationSmoke(string clipPath)
    {
        var log=new List<string>();string output=Path.Combine(AppContext.BaseDirectory,"annotation-smoke");Directory.CreateDirectory(output);
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        try{
            await Mount(SelfTest.TestData,false);
            followCamera.Checked=false;scene.FollowScene=false;
            DropFiles([clipPath]);await Task.Delay(400);clock.Pause();
            inspectorTabs!.SelectedIndex=1;await Task.Delay(150);
            var rows=Highlight.Rows;
            Check(rows.Count==clip!.Events.Count&&rows.Count>10,$"Every annotation line of the tab is indexed ({rows.Count} lines)");
            int Row(string text,int occurrence=0)=>Enumerable.Range(0,rows.Count).Where(i=>rows[i].Text.Trim().Equals(text,StringComparison.OrdinalIgnoreCase)).ElementAt(occurrence);
            int RowStarting(string prefix,int occurrence=0)=>Enumerable.Range(0,rows.Count).Where(i=>rows[i].Text.Trim().StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ElementAt(occurrence);
            void Go(double time){clock.Seek(time);scene.SetTime(clock.Time);UpdateTimeLabel(clock.Time);}
            bool Painted(int row){var span=Highlight.Span(row);int s0=events.SelectionStart,l0=events.SelectionLength;events.Select(span.Start,span.Length);bool on=events.SelectionBackColor.ToArgb()==AnnotationHighlighter.ActiveBack.ToArgb();events.Select(s0,l0);return on;}
            string Names()=>string.Join(" | ",Highlight.Active.OrderBy(i=>i).Select(i=>rows[i].Text.Trim()).Where(t=>!t.StartsWith("anim")));
            int open1=Row("MCO_WinOpen"),close1=Row("MCO_WinClose"),open2=Row("MCO_WinOpen",1),powerOpen1=Row("MCO_PowerWinOpen"),add1=RowStarting("Collision_Add"),remove1=RowStarting("Collision_Remove"),hit1=Row("HitFrame");
            int speed1=RowStarting("PIE.@SGVF|MCO_AttackSpeed"),speed2=RowStarting("PIE.@SGVF|MCO_AttackSpeed",1),next1=RowStarting("PIE.@SGVI|MCO_nextattack");
            events.Select(5,3);
            Go((rows[add1].Time+rows[remove1].Time)/2);log.Add($"during the first collision ({clock.Time:F3} s): {Names()}");
            Check(Highlight.Active.Contains(add1)&&Painted(add1)&&!Highlight.Active.Contains(remove1)&&!Painted(remove1),"A Collision_Add line is highlighted while its collision is alive");
            Check(events.SelectionStart==5&&events.SelectionLength==3,"Highlighting leaves the text selection where it was");
            Go(rows[open1].Time+.01);log.Add($"after the first MCO_WinOpen ({clock.Time:F3} s): {Names()}");
            Check(Highlight.Active.Contains(open1)&&Highlight.Active.Contains(powerOpen1)&&Painted(open1)&&!Highlight.Active.Contains(add1)&&!Painted(add1),"MCO_WinOpen and MCO_PowerWinOpen are highlighted once they fire; the removed collision no longer is");
            Check(!Highlight.Active.Contains(hit1)&&Highlight.Active.Contains(next1),"HitFrame ended when the next annotation fired; MCO_nextattack is still active");
            Go(rows[close1].Time+.01);log.Add($"after the first MCO_WinClose ({clock.Time:F3} s): {Names()}");
            Check(!Highlight.Active.Contains(open1)&&!Painted(open1)&&Highlight.Active.Contains(close1)&&Painted(close1),"MCO_WinClose ends the MCO_WinOpen highlight");
            Check(Highlight.Active.Count(i=>rows[i].Text.Contains("MCO_AttackSpeed"))==1&&!Highlight.Active.Contains(speed1)&&!Highlight.Active.Contains(speed2),"Only the latest MCO_AttackSpeed is active");
            Go(rows[open2].Time+.01);log.Add($"after the second MCO_WinOpen ({clock.Time:F3} s): {Names()}");
            Check(Highlight.Active.Contains(open2)&&!Highlight.Active.Contains(close1)&&!Highlight.Active.Contains(open1),"The second MCO_WinOpen takes over from the WinClose before it");
            Check(Highlight.Active.Count(i=>rows[i].Text.Contains("MCO_AttackSpeed"))==1&&Highlight.Active.Count(i=>rows[i].Text.Contains("MCO_nextattack"))==1,"One MCO_AttackSpeed and one MCO_nextattack are active");
            if(inspectorTabs.Parent?.Parent is SplitContainer split)split.SplitterDistance=Math.Max(120,split.Height-520);events.Select(Highlight.Span(open2).Start,0);events.ScrollToCaret();await Task.Delay(200);
            using(var window=new Bitmap(Width,Height)){DrawToBitmap(window,new Rectangle(Point.Empty,Size));window.Save(Path.Combine(output,"window-annotations.png"));}
            clock.Seek(0);clock.Play();var seen=new HashSet<int>();for(int i=0;i<40;i++){await Task.Delay(40);foreach(int a in Highlight.Active)seen.Add(a);}clock.Pause();
            Check(seen.Count>Highlight.Active.Count,$"The highlight follows playback ({seen.Count} different lines were active over 1.6 s)");
            {int latest=Highlight.Active.Where(i=>!rows[i].Text.StartsWith("anim")).OrderByDescending(i=>rows[i].Time).First();int y=events.GetPositionFromCharIndex(Highlight.Span(latest).Start).Y;
             Check(y>=0&&y<events.ClientSize.Height,$"During playback the list scrolls to keep the latest annotation in view ({rows[latest].Text.Trim()} at y {y} of {events.ClientSize.Height})");
             using(var playing=new Bitmap(Width,Height)){DrawToBitmap(playing,new Rectangle(Point.Empty,Size));playing.Save(Path.Combine(output,"window-annotations-playing.png"));}}
            Go(0);Check(Highlight.Active.All(i=>rows[i].Time<=1e-6),"Back at the start only the annotations at time 0 are active");
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"annotation-smoke.txt"),log);}finally{Close();}}
    }
    // Skeleton overlay: off by default, every bone at its posed position, the Bones window's search and tick filter,
    // local axes on filtered bones, name labels, the controls' place above the character list, and the session.
    async Task SkeletonSmoke(string clipPath)
    {
        var log=new List<string>();string output=Path.Combine(AppContext.BaseDirectory,"skeleton-smoke");Directory.CreateDirectory(output);
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        void Capture(string name){viewport.InvalidateRender();using var image=viewport.CaptureBitmap();image.Save(Path.Combine(output,name+".png"));}
        try{
            await Mount(SelfTest.TestData,false);
            followCamera.Checked=false;scene.FollowScene=false;showSkeleton.Checked=false;showBoneNames.Checked=false;scene.BoneFilter.Clear();
            DropFiles([clipPath]);await Task.Delay(400);clock.Pause();double at=clock.Duration*.4;clock.Seek(at);scene.SetTime(at);cameraSubject.SelectedIndex=1;scene.Frame();
            Capture("off");
            Check(!showSkeleton.Checked&&scene.SkeletonLines==0,"The skeleton is hidden by default");
            var row=showSkeleton.Parent!;var stack=row.Parent!;
            Check(stack.Controls.GetChildIndex(row)<stack.Controls.GetChildIndex(races)&&row.Controls.OfType<Button>().Any(b=>b.Text=="Bones…"),"The tick box and the Bones… button sit above the character drop-down");
            showSkeleton.Checked=true;Capture("all-bones");
            int bones=scene.Skeleton.SkeletonDictionary.Count;var visible=scene.VisibleBones(at);
            log.Add($"{bones} bones; {scene.SkeletonLines} overlay lines");
            Check(visible.Count==bones&&scene.SkeletonLines>=bones*3,$"Every bone is drawn: {bones} joints and their parent lines ({scene.SkeletonLines} lines)");
            var head=visible.First(b=>b.Name.Equals("NPC Head [Head]",StringComparison.OrdinalIgnoreCase));
            var motion=clip!.Motion(at);var expected=(head.Bone.GetGlobalTransform.ToMatrix4d()*(OpenTK.Mathematics.Matrix4d.CreateRotationZ(motion.Yaw)*OpenTK.Mathematics.Matrix4d.CreateTranslation(motion.Position.X,motion.Position.Y,motion.Position.Z))).ExtractTranslation();
            Check((head.Position-expected).Length<1e-6&&head.Position.Z>80,$"Joints sit at the posed bone positions with root motion (head at {head.Position.Z:F1} units up)");
            OpenBonesWindow();await Task.Delay(150);
            Check(bonesWindow!=null&&boneList.Items.Count==bones&&boneFilterNote.Text.StartsWith("Showing all"),$"The Bones window lists all {bones} bones");
            boneSearch.Text="head";await Task.Delay(50);
            Check(boneList.Items.Count>0&&boneList.Items.Cast<string>().All(n=>n.Contains("head",StringComparison.OrdinalIgnoreCase))&&boneList.Items.Count<bones,$"The search box narrows the list ({boneList.Items.Count} match \"head\")");
            boneList.SetItemChecked(boneList.Items.IndexOf(head.Name),true);await Task.Delay(100);viewport.InvalidateRender();
            Check(scene.BoneFilter.Count==1&&scene.VisibleBones(at).Count==1&&scene.SkeletonLines==1+3+3,$"Ticking one bone shows just that bone with its parent line and local axes ({scene.SkeletonLines} lines)");
            Check(boneFilterNote.Text.StartsWith("Showing 1 of"),"The note counts the shown bones: "+boneFilterNote.Text);
            showBoneNames.Checked=true;Capture("head-with-name");
            Check(scene.BoneLabelsDrawn==1,"Names: one label for the one shown bone");
            boneSearch.Text="";await Task.Delay(50);SetListedBones(false);Capture("all-names");
            Check(scene.BoneFilter.Count==0&&scene.BoneLabelsDrawn>bones/2,$"Unticking every bone shows them all again, named ({scene.BoneLabelsDrawn} labels on screen)");
            boneSearch.Text="Spine";await Task.Delay(50);SetListedBones(true);boneSearch.Text="";await Task.Delay(50);Capture("spine-names");
            Check(scene.BoneFilter.Count>=3&&scene.VisibleBones(at).All(b=>b.Name.Contains("Spine",StringComparison.OrdinalIgnoreCase)),$"Tick listed after a search shows that selection ({scene.BoneFilter.Count} spine bones)");
            var saved=CaptureSettings();
            Check(saved.ShowSkeleton&&saved.ShowBoneNames&&saved.BoneFilter!.Length==scene.BoneFilter.Count,"The tick box, names and bone selection are saved with the session");
            using(var window=new Bitmap(bonesWindow!.Width,bonesWindow.Height)){bonesWindow.DrawToBitmap(window,new Rectangle(Point.Empty,bonesWindow.Size));window.Save(Path.Combine(output,"bones-window.png"));}
            using(var main=new Bitmap(Width,Height)){DrawToBitmap(main,new Rectangle(Point.Empty,Size));main.Save(Path.Combine(output,"window.png"));}
            bonesWindow.Close();
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"skeleton-smoke.txt"),log);}finally{Close();}}
    }
    // Writes every annotation of each HKX (time, track, text) to annotations.txt beside the exe.
    void DumpAnnotations(string[] files)
    {
        var lines=new List<string>();
        foreach(string file in files){
            try{var c=AnimationClip.Load(File.ReadAllBytes(file));lines.Add($"== {file} ({c.Duration:F4} s, {c.Events.Count} annotations)");lines.AddRange(c.Events.Select(e=>$"{e.Time,9:F4}  track {e.Track}  {e.Text}"));}
            catch(Exception ex){lines.Add($"== {file}: {ex.Message}");}
        }
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"annotations.txt"),lines);
    }
    async Task WindowPickSmoke(string multiple,string next)
    {
        var log=new List<string>();string output=Path.Combine(AppContext.BaseDirectory,"window-pick-smoke");Directory.CreateDirectory(output);
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        try{
            await Mount(SelfTest.TestData,false);
            followCamera.Checked=false;scene.FollowScene=false;queueAtWindow.Checked=true;queueWindowEvent.SelectedItem="MCO_WinOpen";
            // No installed clip has two distinct MCO_WinOpen times, so the fixture is a copy of an MCO clip whose
            // MCO_WinClose annotation is renamed MCO_WinOpen: a second window, later in the clip.
            var bytes=File.ReadAllBytes(multiple);int at=bytes.AsSpan().IndexOf("MCO_WinClose\0"u8);
            if(at>=0&&AnimationClip.WindowTimes(AnimationClip.Load(bytes).Events,"MCO_WinOpen").Count<2){"MCO_WinOpen\0\0"u8.CopyTo(bytes.AsSpan(at));log.Add($"fixture: MCO_WinClose renamed MCO_WinOpen in a copy of {Path.GetFileName(multiple)}");}
            string fixture=Path.Combine(output,"two-windows_"+Path.GetFileName(multiple));File.WriteAllBytes(fixture,bytes);
            var a=assets.Drop(fixture);var b=assets.Drop(next);
            AddToQueue([a,b]);await Task.Delay(100);
            var times=QueueWindowTimes(queue[0],"MCO_WinOpen");log.Add($"{a.Name}: MCO_WinOpen at {string.Join(", ",times.Select(t=>t.ToString("F3")))}; {b.Name}: {QueueWindowTimes(queue[1],"MCO_WinOpen").Count} time(s)");
            Check(times.Count>=2,"The first clip has MCO_WinOpen at more than one time");
            Check(QueueChip(0)!=null&&(QueueWindowTimes(queue[1],"MCO_WinOpen").Count>=2)==(QueueChip(1)!=null),"Only a clip with several window times gets the drop-down");
            int rowHeight=queueList.ItemHeight,width=queueList.ClientSize.Width;
            using(var image=new Bitmap(width,rowHeight*queue.Count)){
                using(var g=Graphics.FromImage(image))for(int i=0;i<queue.Count;i++)DrawQueueItem(g,new Rectangle(0,i*rowHeight,width,rowHeight),i,false);
                image.Save(Path.Combine(output,"queue-window-chip.png"));
                var chip=QueueChip(0)!.Value;var edge=image.GetPixel(chip.Left,3+(rowHeight-6)/2);int nameEnd=6+TextRenderer.MeasureText(QueueRowName(0),queueList.Font,Size.Empty,QueueTextFlags).Width;
                log.Add($"chip {chip}, name ends at {nameEnd}, edge pixel {edge}");
                Check(edge.ToArgb()==Color.FromArgb(178,132,255).ToArgb()&&chip.Left>nameEnd&&chip.Left-nameEnd<=12,"The drop-down is drawn just after the file name");
            }
            await PlayQueue();clock.Pause();
            Check(Math.Abs(clip!.Segments![0].HandOver-times[0])<1e-6,$"By default the first occurrence hands over ({times[0]:F3} s)");
            ChooseWindow(queue[0],"MCO_WinOpen",1);for(int i=0;i<40&&Math.Abs(clip!.Segments![0].HandOver-times[1])>1e-6;i++)await Task.Delay(50);
            Check(Math.Abs(clip!.Segments![0].HandOver-times[1])<1e-6&&Math.Abs(clip.Segments[1].Start-clip.Segments[0].Global(times[1]))<1e-6,$"Choosing the second rebuilds the playing queue to hand over there ({times[1]:F3} s)");
            Check(queueList.Items[0] is string line&&line.Contains($"next at MCO_WinOpen {times[1]:0.00} s"),"The queue list shows the chosen hand-over: "+queueList.Items[0]);
            using(var playing=new Bitmap(queueList.ClientSize.Width,queueList.ItemHeight*queue.Count)){using(var g=Graphics.FromImage(playing))for(int i=0;i<queue.Count;i++)DrawQueueItem(g,new Rectangle(0,i*queueList.ItemHeight,playing.Width,queueList.ItemHeight),i,i==0);playing.Save(Path.Combine(output,"queue-window-chip-playing.png"));}
            AddToQueue([a]);await Task.Delay(300);
            Check(queue.Count==3&&!ReferenceEquals(queue[0],queue[2])&&WindowPick(queue[2],"MCO_WinOpen")==0&&WindowPick(queue[0],"MCO_WinOpen")==1,"The same clip queued again keeps its own choice");
            queueWindowEvent.SelectedItem="MCO_PowerWinOpen";await Task.Delay(100);
            Check(WindowPick(queue[0],"MCO_WinOpen")==1&&WindowPick(queue[0],"MCO_PowerWinOpen")==0,"Each window event keeps its own choice");
            queueWindowEvent.SelectedItem="MCO_WinOpen";await Task.Delay(300);
            var saved=JsonSerializer.Deserialize<SessionSettings>(JsonSerializer.Serialize(CaptureSettings()))!;
            Check(saved.QueueWindowPicks is {Length:3} p&&p[0]!["MCO_WinOpen"]==1&&p[2]==null,"The choices are saved with the session, one per queue slot");
            using(var window=new Bitmap(Width,Height)){if(inspectorTabs!=null&&queueTab!=null)inspectorTabs.SelectedTab=queueTab;await Task.Delay(150);DrawToBitmap(window,new Rectangle(Point.Empty,Size));window.Save(Path.Combine(output,"window-queue.png"));}
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"window-pick-smoke.txt"),log);}finally{Close();}}
    }
    async Task FrameSmoke(string clipPath)
    {
        var log=new List<string>();string output=Path.Combine(AppContext.BaseDirectory,"frame-smoke");Directory.CreateDirectory(output);
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        bool OnFrame(double t)=>Math.Abs(t*clock.SourceFps-Math.Round(t*clock.SourceFps))<1e-6;
        try{
            await Mount(SelfTest.TestData,false);
            DropFiles([clipPath]);await Task.Delay(400);
            Check(clip!=null,"Clip loaded: "+clipPath);
            clock.Pause();fps.Value=30;snapFrames.Checked=false;
            int total=FrameAt(clock.Duration);log.Add($"duration {clock.Duration:F4} s, {total} frames at 30 fps, HKX frames {clip!.Frames}");
            BeginScrub();ScrubTo(3333);EndScrub();
            Check(timeLabel.Text.Contains($"frame {FrameAt(scene.Time)} / {total}"),"The frame number shows with snapping off: "+timeLabel.Text);
            double free=scene.Time;
            snapFrames.Checked=true;await Task.Delay(50);
            Check(OnFrame(scene.Time)&&Math.Abs(scene.Time-free)<=.5/30+1e-9,$"Ticking the box moves a paused pose to the nearest frame ({free:F4} -> {scene.Time:F4} s)");
            BeginScrub();ScrubTo(5123);EndScrub();
            Check(OnFrame(scene.Time)&&OnFrame(clock.Time),$"Scrubbing lands on a frame ({scene.Time:F4} s, {timeLabel.Text})");
            int before=FrameAt(scene.Time);SeekFrame(1);
            Check(FrameAt(scene.Time)==before+1&&OnFrame(scene.Time),"› steps one frame and stays on the grid");
            SeekFrame(-2);Check(FrameAt(scene.Time)==before-1,"‹ steps back one frame");
            clock.Seek(0);clock.Play();var seen=new List<double>();
            for(int i=0;i<25;i++){await Task.Delay(20);seen.Add(scene.Time);}
            clock.Pause();
            Check(seen.All(OnFrame)&&seen.Distinct().Count()>1,$"Snapped playback shows only frame poses ({seen.Distinct().Count()} distinct frames)");
            fps.Value=24;await Task.Delay(50);
            Check(OnFrame(scene.Time)&&timeLabel.Text.Contains($"/ {FrameAt(clock.Duration)}"),"Changing Step FPS re-snaps and recounts frames: "+timeLabel.Text);
            fps.Value=30;
            Check(CaptureSettings().SnapFrames,"The setting is saved with the session");
            snapFrames.Checked=false;BeginScrub();ScrubTo(5123);EndScrub();
            Check(Math.Abs(scene.Time-clock.Duration*.5123)<1e-9,"With the box clear, scrubbing is continuous again");
            // The timeline: a press anywhere jumps there (no need to grab the scrubber), a held drag keeps scrubbing.
            Check(timeline.Height>=56,$"The timeline is {timeline.Height} px tall with a {TimelineBar.ThumbWidth} x {TimelineBar.ThumbHeight} px scrubber");
            clock.Pause();
            int Px(double fraction)=>timeline.XOf((int)Math.Round(fraction*timeline.Maximum));
            double pixel=clock.Duration*timeline.ValuePerPixel/timeline.Maximum;
            timeline.Press(Px(.8));
            Check(Math.Abs(scene.Time-clock.Duration*.8)<=pixel&&!clock.Playing,$"Pressing the bar at 80% moves the scrubber and pose there ({scene.Time:F4} of {clock.Duration:F4} s)");
            timeline.DragTo(Px(.25));
            Check(Math.Abs(scene.Time-clock.Duration*.25)<=pixel,"Dragging with the button held keeps scrubbing");
            timeline.Release(Px(.25));
            Check(!clock.Playing&&Math.Abs(timeline.Value-2500)<=timeline.ValuePerPixel,"Releasing leaves a paused clip paused at the new point");
            clock.Play();timeline.Press(Px(.6));
            Check(!clock.Playing&&Math.Abs(scene.Time-clock.Duration*.6)<=pixel,"Pressing during playback pauses and jumps");
            timeline.Release(Px(.6));
            Check(clock.Playing,"Releasing resumes playback");
            clock.Pause();
            snapFrames.Checked=true;timeline.Press(Px(.333));timeline.Release(Px(.333));
            Check(OnFrame(scene.Time),"A click with Snap to frame on lands on a frame");
            snapFrames.Checked=false;
            using(var bar=new Bitmap(timeline.Width,timeline.Height)){timeline.DrawToBitmap(bar,new Rectangle(Point.Empty,timeline.Size));bar.Save(Path.Combine(output,"timeline.png"));}
            using(var window=new Bitmap(Width,Height)){DrawToBitmap(window,new Rectangle(Point.Empty,Size));window.Save(Path.Combine(output,"window-frames.png"));}
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"frame-smoke.txt"),log);}finally{Close();}}
    }
    // Actor hair color reaches head-part hair and worn hair-tint pieces (wigs). Arguments: mod folders mounted over
    // test-data; the first race and the first hair-tint armor found in them are used.
    async Task HairSmoke(string[] folders)
    {
        var log=new List<string>();string output=Path.Combine(AppContext.BaseDirectory,"hair-smoke");Directory.CreateDirectory(output);
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        try{
            await Mount(SelfTest.TestData,false);
            foreach(string folder in folders)await Mount(folder,true);
            followCamera.Checked=false;scene.FollowScene=false;smpPhysics.Checked=false;cameraSubject.SelectedIndex=1;
            bool Modded(GameRecord r)=>!r.Plugin.Equals("Skyrim.esm",StringComparison.OrdinalIgnoreCase);
            var race=records.Of("RACE").FirstOrDefault(r=>Modded(r)&&records.DefaultHeadParts(r,false).Any(h=>h.First("PNAM")?.UInt()==3))??records.Of("RACE").First(r=>r.EditorId=="NordRace");
            races.SelectedItem=race;await Task.Delay(300);
            PreviewModel.RenderableMesh[] Tinted()=>viewport.Model.meshes.Where(m=>m.MeshData.Shape.NifShader?.IsTypeHairTint==true).ToArray();
            bool Carries(PreviewModel.RenderableMesh m,Color c)=>m.MeshData.Material.MaterialBase is {} material&&material.TintColorScale==2&&material.HairTintColor.ToArgb()==c.ToArgb();
            async Task Shot(string name){
                clock.Pause();scene.SetTime(clock.Time);scene.Frame();
                // Close on the head so the hair fills the capture.
                if(scene.Skeleton.SkeletonDictionary.TryGetValue("NPC Head [Head]",out var headBone)){var at=headBone.GetGlobalTransform.Translation;viewport.camera.FocusPosition=new OpenTK.Mathematics.Vector3(at.X,at.Y,at.Z+4);viewport.camera.distance=75;viewport.UpdateProjection(true);}
                viewport.InvalidateRender();await Task.Delay(250);
                using var image=viewport.CaptureBitmap();image.Save(Path.Combine(output,name+".png"));
            }
            var standard=records.RaceHairColor(race,false);
            log.Add($"race {race.EditorId}: default hair color {records.RaceHairColorRecord(race,false)?.EditorId} {standard}; offered {string.Join(", ",records.RaceHairColors(race,false).Select(c=>c.EditorId))}");
            Check(standard!=null,"The race resolves a default hair color");
            var headHair=Tinted();
            Check(headHair.Length>0&&headHair.All(m=>Carries(m,standard!.Value)),$"Head-part hair carries the race color at the game's double scale ({headHair.Length} shape(s))");
            Check(hairNote.Text.Contains($"{standard!.Value.R}, {standard.Value.G}, {standard.Value.B}")&&hairColor.SelectedIndex==0,"The hair color control shows the race default and its value: "+hairNote.Text);
            await Shot("head-hair-race-default");
            // A worn hair-tint piece takes the same color and replaces the hair head part when it covers slot 31.
            GameRecord? wig=null;
            foreach(var a in records.Of("ARMO").Where(Modded)){
                foreach(var addon in records.ArmorAddons(a,race)){
                    string path=ProjectileVisuals.ModelPath(addon.First("MOD2")?.Text);if(path.Length==0||!assets.Exists(path))continue;
                    try{var nif=new Nifcontent_Class_Manolo();nif.Load_Manolo(assets.Read(path));if(nif.Blocks.OfType<NiflySharp.Blocks.BSLightingShaderProperty>().Any(p=>p.IsTypeHairTint)){wig=a;break;}}catch(Exception){ }
                }
                if(wig!=null)break;
            }
            if(wig!=null){
                var headNames=headHair.Select(m=>m.MeshData.Shape.NifShape.Name.ToString()).ToHashSet();
                worn.Add(wig);RebuildScene();await Task.Delay(300);
                var wigHair=Tinted();bool coversHair=(wig.Slots&2)!=0||records.ArmorAddons(wig,race).Any(a=>(a.Slots&2)!=0);
                log.Add($"wig {wig.EditorId}: {wigHair.Length} hair-tint shape(s), covers slot 31: {coversHair}");
                Check(wigHair.Length>0&&wigHair.All(m=>Carries(m,standard.Value)),"A worn hair-tint piece carries the actor's hair color instead of the placeholder tint in its NIF");
                if(coversHair)Check(scene.Diagnostics.Any(d=>d.StartsWith("Hair head parts are not drawn"))&&wigHair.Length!=headHair.Length+wigHair.Count(m=>!headNames.Contains(m.MeshData.Shape.NifShape.Name.ToString())),"Equipment on slot 31 replaces the hair head part");
                await Shot("wig-race-default");
            }
            // Picking another record, then a custom color, recolors every hair-tint shape.
            var other=records.Of("CLFM").FirstOrDefault(c=>c.EditorId=="HairColor03BrightBlond")??records.Of("CLFM").First(c=>Records.ColorValue(c)!=standard);
            hairColor.SelectedIndex=hairColor.Items.Cast<HairChoice>().ToList().FindIndex(c=>c.Key==other.Key);await Task.Delay(300);
            var picked=Records.ColorValue(other)!.Value;
            Check(Tinted().Length>0&&Tinted().All(m=>Carries(m,picked))&&CaptureSettings().HairColor==other.Key,$"Choosing {other.EditorId} recolors hair and is saved with the session");
            await Shot("chosen-record");
            var custom=Color.FromArgb(255,235,200,120);SetHairColor("#EBC878");await Task.Delay(300);
            Check(Tinted().All(m=>Carries(m,custom))&&hairColor.SelectedItem is HairChoice{Key:CustomHairKey}&&hairNote.Text.Contains("235, 200, 120"),"A custom color recolors hair and shows in the control");
            await Shot("custom-blonde");
            var saved=CaptureSettings();SetHairColor(null);await Task.Delay(200);
            Check(Tinted().All(m=>Carries(m,standard.Value)),"Race default returns the race color");
            await ApplySettings(saved);await Task.Delay(400);
            Check(hairChoice=="#EBC878"&&Tinted().Length>0&&Tinted().All(m=>Carries(m,custom)),"A saved scene restores the chosen hair color");
            for(Control? parent=hairNote.Parent;parent!=null;parent=parent.Parent)if(parent is ScrollableControl {AutoScroll:true} scroller){scroller.AutoScrollPosition=new Point(0,Math.Max(0,races.Top-60));break;}
            await Task.Delay(150);
            using(var window=new Bitmap(Width,Height)){DrawToBitmap(window,new Rectangle(Point.Empty,Size));window.Save(Path.Combine(output,"window-hair-color.png"));}
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"hair-smoke.txt"),log);}finally{Close();}}
    }
    // HDT-SMP on real assets. Arguments: a clip, then mod folders mounted over test-data. The weapon whose mesh has
    // bones outside the skeleton and every armor whose mesh links a physics XML are equipped automatically.
    // Mounts test-data and the given mod folders, equips the weapon and every armor in them that carries HDT-SMP
    // physics, and loads the clip. Arguments: a clip, then mod folders. SSE_SMP_WEAPON, SSE_SMP_NO_ARMOR and
    // SSE_SMP_FEMALE adjust the choice.
    async Task EquipPhysicsScene(string[] arguments,List<string> log)
    {
        await Mount(SelfTest.TestData,false);
        foreach(string folder in arguments.Skip(1))await Mount(folder,true);
        followCamera.Checked=false;scene.FollowScene=false;smpPhysics.Checked=false;
        bool female=Environment.GetEnvironmentVariable("SSE_SMP_FEMALE")=="1";if(female){selecting=true;sex.SelectedIndex=1;selecting=false;}
        // Equipment: everything in the mounted mods that carries physics.
        GameRecord? weapon=null;
        foreach(var w in records.Of("WEAP")){
            string model=w.First("MODL")?.Text??"";if(model.Length==0)continue;
            string path=ProjectileVisuals.ModelPath(model);if(!assets.Exists(path)||assets.Source(path).Contains("test-data",StringComparison.OrdinalIgnoreCase))continue;
            if(Environment.GetEnvironmentVariable("SSE_SMP_WEAPON") is {Length:>0} wanted&&!model.Contains(wanted,StringComparison.OrdinalIgnoreCase))continue;
            try{var nif=new Nifcontent_Class_Manolo();nif.Load_Manolo(assets.Read(path));if(nif.Blocks.OfType<NiflySharp.Blocks.NiSkinInstance>().Any()){weapon=w;log.Add("weapon: "+w.EditorId+" "+path);break;}}catch(Exception){ }
        }
        var race=races.SelectedItem as GameRecord;int equippedCount=0;
        // SSE_SMP_NO_ARMOR=1 leaves the carrier armor off, so the weapon's physics has to be found beside its mesh.
        foreach(var a in Environment.GetEnvironmentVariable("SSE_SMP_NO_ARMOR")=="1"?[]:records.Of("ARMO")){
            bool physics=false;
            foreach(var addon in records.ArmorAddons(a,race)){
                string path=ProjectileVisuals.ModelPath(addon.First(female?"MOD3":"MOD2")?.Text);
                if(path.Length==0||!assets.Exists(path)||assets.Source(path).Contains("test-data",StringComparison.OrdinalIgnoreCase))continue;
                try{if(assets.Read(path).AsSpan().IndexOf("HDT Skinned Mesh Physics Object"u8)>=0){physics=true;log.Add("armor: "+a.EditorId+" "+path);}}catch(Exception){ }
            }
            if(physics&&!worn.Contains(a)){worn.Add(a);equippedCount++;}
        }
        selecting=true;if(weapon!=null)weapons.SelectedItem=weapon;selecting=false;
        RebuildScene();await Task.Delay(300);
        if(arguments.Length>0&&File.Exists(arguments[0])){DropFiles([arguments[0]]);await Task.Delay(500);}
    }
    // Fixed-timestep HDT-SMP run for comparing builds: the same steps every time, every simulated bone's transform
    // written at full precision, viewport captures, and the time spent in the physics step and the render pass.
    // Arguments: an output folder, a clip, then mod folders.
    async Task SmpDeterminism(string[] arguments)
    {
        string output=Path.GetFullPath(arguments[0]);Directory.CreateDirectory(output);
        var log=new List<string>();var state=new List<string>();var timing=new List<string>{"phase,step,physics_ms,render_ms,contacts,read_ms,predict_ms,skin_ms,pairs_ms,solve_ms,write_ms"};
        try{
            await EquipPhysicsScene(arguments[1..],log);
            timer.Stop();clock.Pause();cameraSubject.SelectedIndex=1;scene.FixedPhysicsInterval=1/60d;
            // A fixed camera: automatic framing depends on mesh bounds at the moment it runs and can move by a pixel.
            scene.SetTime(.2);viewport.camera=new OrbitCamera{MaxDistance=100000};viewport.camera.Rotate(25,15);viewport.camera.FocusPosition=new OpenTK.Mathematics.Vector3(0,0,75);viewport.camera.distance=230;viewport.UpdateProjection(true);
            smpPhysics.Checked=true;viewport.InvalidateRender();
            var world=scene.PhysicsWorld??throw new InvalidDataException("No physics system was built.");
            log.Add($"{scene.PhysicsSystems} systems, {scene.PhysicsDynamicBones} simulated bones, {scene.PhysicsBodies} collision shapes, {scene.PhysicsMovedShapes} of {viewport.Model.meshes.Count} meshes re-skinned per step");
            string R(double v)=>v.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
            void Record(string label){
                state.Add("== "+label);
                foreach(var system in world.Systems)foreach(var bone in system.Bones){
                    if(bone.Kinematic)continue;var m=bone.Rig.WorldTransform;
                    state.Add($"{system.Name}/{bone.Name} {R(m.M11)} {R(m.M12)} {R(m.M13)} {R(m.M21)} {R(m.M22)} {R(m.M23)} {R(m.M31)} {R(m.M32)} {R(m.M33)} {R(m.M41)} {R(m.M42)} {R(m.M43)}");
                }
                foreach(var c in world.Contacts)state.Add($"contact {c.A.Name} {c.B.Name} {R(c.Depth)} {R(c.WorldA.X)} {R(c.WorldA.Y)} {R(c.WorldA.Z)} {R(c.Normal.X)} {R(c.Normal.Y)} {R(c.Normal.Z)}");
            }
            void Step(string phase,int i){
                var watch=Stopwatch.StartNew();scene.StepPhysics();double physics=watch.Elapsed.TotalMilliseconds;
                watch.Restart();viewport.InvalidateRender();double render=watch.Elapsed.TotalMilliseconds;
                timing.Add(FormattableString.Invariant($"{phase},{i},{physics:F4},{render:F4},{world.LastContacts},{world.LastReadMs:F4},{world.LastPredictMs:F4},{world.LastSkinMs:F4},{world.LastPairsMs:F4},{world.LastSolveMs:F4},{world.LastWriteMs:F4}"));
            }
            void Capture(string name){using var image=viewport.CaptureBitmap();image.Save(Path.Combine(output,name+".png"));}
            // Paused: the pose holds and only physics moves.
            for(int i=1;i<=120;i++){Step("paused",i);if(i%30==0){Record($"paused {i}");Capture($"paused-{i:000}");}}
            // Playing at 60 steps per second through the clip, wrapping at its end.
            double duration=Math.Max(.05,clock.Duration);
            for(int i=1;i<=180;i++){double t=(.2+i/60d)%duration;scene.SetTime(t);Step("playing",i);if(i%30==0){Record($"playing {i}");Capture($"playing-{i:000}");}}
            // Overlay on.
            smpColliders.Checked=true;smpBones.Checked=true;
            for(int i=1;i<=30;i++){double t=(.2+(180+i)/60d)%duration;scene.SetTime(t);Step("overlay",i);if(i%15==0){Record($"overlay {i}");Capture($"overlay-{i:000}");}}
            // Overlay with the simulation unchanged but the camera turned: camera-facing rings must follow.
            int builds=scene.PhysicsOverlayBuilds;
            for(int i=0;i<3;i++){viewport.UpdateRequired=true;viewport.InvalidateRender();}
            log.Add($"overlay redraws without a physics step: {scene.PhysicsOverlayBuilds-builds} rebuild(s)");
            Capture("overlay-redrawn");
            viewport.camera.Rotate(30,0);viewport.UpdateProjection(true);viewport.InvalidateRender();Capture("overlay-camera-turned");
            log.Add($"overlay rebuilds after turning the camera: {scene.PhysicsOverlayBuilds-builds}");
            smpColliders.Checked=false;smpBones.Checked=false;
            string Stat(string phase,int column){var v=timing.Skip(1).Select(r=>r.Split(',')).Where(r=>r[0]==phase).Select(r=>double.Parse(r[column],System.Globalization.CultureInfo.InvariantCulture)).OrderBy(x=>x).ToArray();return v.Length==0?"-":$"mean {v.Average():F2} ms, median {v[v.Length/2]:F2} ms, max {v[^1]:F2} ms";}
            foreach(string phase in new[]{"paused","playing","overlay"}){
                log.Add($"{phase}: physics {Stat(phase,2)}; render {Stat(phase,3)}");
                string Mean(int column)=>timing.Skip(1).Select(r=>r.Split(',')).Where(r=>r[0]==phase).Average(r=>double.Parse(r[column],System.Globalization.CultureInfo.InvariantCulture)).ToString("F2",System.Globalization.CultureInfo.InvariantCulture);
                log.Add($"   mean ms: read {Mean(5)}, predict {Mean(6)}, skin {Mean(7)}, pairs {Mean(8)}, solve {Mean(9)}, write {Mean(10)}");
            }
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{File.WriteAllLines(Path.Combine(output,"state.txt"),state);File.WriteAllLines(Path.Combine(output,"timing.csv"),timing);File.WriteAllLines(Path.Combine(output,"summary.txt"),log);Close();}
    }
    // Long HDT-SMP run on the live session (SSE_SESSION_FILE or the Portable session): the session's clip loops at a
    // fixed 1/60 s step for the given number of simulated minutes, optionally with SSE_SOAK_JITTER=1 frame times.
    // Every second it logs how far constraint links have stretched from their starting length, how far simulated
    // bones are from the actor and their fastest speed, and stops early once a link has grown 100 units or a bone's scale leaves 0.5 to 2.
    // Arguments: output folder, minutes.
    async Task SmpSoak(string[] arguments)
    {
        string output=Path.GetFullPath(arguments[0]);Directory.CreateDirectory(output);double minutes=arguments.Length>1?double.Parse(arguments[1],System.Globalization.CultureInfo.InvariantCulture):5;
        var log=new List<string>();var rows=new List<string>{"second,clip_time,max_stretch,stretch_link,max_extra,max_distance,distance_bone,max_speed,speed_bone,min_scale,max_scale,contacts,resets,substeps"};
        try{
            var state=JsonSerializer.Deserialize<SessionSettings>(File.ReadAllText(ToolSessionPath()))!;
            await ApplySettings(state with {FollowCamera=false,QueuePlaying=false});
            timer.Stop();clock.Pause();viewport.PlayingAnimation=false;
            // SSE_SOAK_CLIP=<path suffix> plays that library clip instead of the session's.
            if(Environment.GetEnvironmentVariable("SSE_SOAK_CLIP") is {Length:>0} wanted){wanted=wanted.Replace('/','\\');var pick=assets.Clips.FirstOrDefault(c=>c.Path.EndsWith(wanted,StringComparison.OrdinalIgnoreCase))??throw new InvalidDataException($"No clip ends with {wanted}; {assets.Clips.Count} clips, e.g. {string.Join(" | ",assets.Clips.Where(c=>c.Path.Contains("Dodge",StringComparison.OrdinalIgnoreCase)).Take(3).Select(c=>c.Path))}");await SelectClip(pick);timer.Stop();clock.Pause();viewport.PlayingAnimation=false;}
            if(Environment.GetEnvironmentVariable("SSE_SOAK_QUEUE")=="1"&&queue.Count>0){await PlayQueue();timer.Stop();clock.Pause();viewport.PlayingAnimation=false;}
            if(Environment.GetEnvironmentVariable("SSE_SOAK_NOROOT")=="1"){rootMotion.Checked=false;scene.RootMotion=false;}
            smpPhysics.Checked=true;scene.SetTime(0);
            bool jitter=Environment.GetEnvironmentVariable("SSE_SOAK_JITTER")=="1";bool scrub=Environment.GetEnvironmentVariable("SSE_SOAK_SCRUB")=="1";string? trace=Environment.GetEnvironmentVariable("SSE_SOAK_TRACE");var traceLines=new List<string>();var random=new Random(1);
            scene.FixedPhysicsInterval=1/60d;
            var world=scene.PhysicsWorld??throw new InvalidDataException("No physics system was built.");
            log.Add($"session {ToolSessionPath()}; clip {clip?.Duration:F3} s; {scene.PhysicsSystems} systems, {scene.PhysicsDynamicBones} simulated bones; jitter {jitter}");
            foreach(var system in world.Systems)log.Add($"  system {system.Name}: {system.Bones.Count(b=>!b.Kinematic)} simulated / {system.Bones.Count} bones, {system.Links.Count} links, {system.StiffSprings.Count} stiff springs");
            static OpenTK.Mathematics.Vector3d Origin(SmpBone b){var m=b.Rig.WorldTransform;return new(m.M41,m.M42,m.M43);}
            // Link lengths after the first second, once the reset pose has settled.
            Dictionary<(SmpBone,SmpBone),double>? rest=null;
            double duration=Math.Max(.05,clock.Duration),time=0;int seconds=(int)Math.Ceiling(minutes*60);
            scene.Frame();viewport.camera.distance*=1.4f;
            void Capture(string name){viewport.InvalidateRender();using var image=viewport.CaptureBitmap();image.Save(Path.Combine(output,name+".png"));}
            Capture("start");
            for(int second=1;second<=seconds;second++){
                for(int i=0;i<60;i++){
                    double dt=jitter?1/60d*(0.5+random.NextDouble()*1.5):1/60d;
                    // SSE_SOAK_SCRUB=1: every 5 s, half a second of dragging the scrubber to random points.
                    if(scrub&&second%5==0&&i<30)time=random.NextDouble()*duration;else time=(time+dt*(double)speed.Value)%duration;
                    scene.FixedPhysicsInterval=dt;scene.SetTime(time);scene.StepPhysics();
                    // SSE_SOAK_TRACE=<system name>: every step, each bone of that system (origin, speed) and the resets so far.
                    if(trace!=null)foreach(var system in world.Systems.Where(x=>x.Name.Equals(trace,StringComparison.OrdinalIgnoreCase))){
                        var line=new System.Text.StringBuilder(FormattableString.Invariant($"{second}.{i:00} t={time:F4} resets={world.Resets} gap={world.LastKinematicGap:F2} sub={world.LastSubSteps}"));
                        foreach(var bone in system.Bones){var m=bone.Rig.WorldTransform;var v=bone.Rig.LinearVelocity;var w=bone.Rig.AngularVelocity;line.Append(FormattableString.Invariant($" | {bone.Name}{(bone.Kinematic?"*":"")} ({m.M41:F2},{m.M42:F2},{m.M43:F2}) v={Math.Sqrt(v.X*v.X+v.Y*v.Y+v.Z*v.Z):F1} w={Math.Sqrt(w.X*w.X+w.Y*w.Y+w.Z*w.Z):F1} s={bone.Current.Scale:R} im={bone.Rig.InvMass:R}"));}
                        traceLines.Add(line.ToString());
                    }
                }
                rest??=world.Systems.SelectMany(s=>s.Links).Distinct().ToDictionary(l=>l,l=>(Origin(l.A)-Origin(l.B)).Length);
                var actor=scene.ActorOrigin;
                double maxStretch=0,maxDistance=0,maxSpeed=0,maxExtra=0,minScale=double.MaxValue,maxScale=0;string stretchLink="",distanceBone="",speedBone="";
                foreach(var system in world.Systems){
                    foreach(var link in system.Links){
                        double length=(Origin(link.A)-Origin(link.B)).Length,start=rest.GetValueOrDefault(link,length);
                        double ratio=start>1e-3?length/start:1;
                        maxExtra=Math.Max(maxExtra,length-start);
                        if(ratio>maxStretch){maxStretch=ratio;stretchLink=$"{system.Name}:{link.A.Name}-{link.B.Name} ({start:F1}->{length:F1})";}
                    }
                    foreach(var bone in system.Bones){
                        if(bone.Kinematic)continue;
                        double d=(Origin(bone)-actor).Length;if(d>maxDistance){maxDistance=d;distanceBone=system.Name+":"+bone.Name;}
                        var v=bone.Rig.LinearVelocity;double speedNow=Math.Sqrt(v.X*v.X+v.Y*v.Y+v.Z*v.Z);if(speedNow>maxSpeed){maxSpeed=speedNow;speedBone=system.Name+":"+bone.Name;}
                        minScale=Math.Min(minScale,bone.Current.Scale);maxScale=Math.Max(maxScale,bone.Current.Scale);
                    }
                }
                rows.Add(FormattableString.Invariant($"{second},{time:F3},{maxStretch:F3},\"{stretchLink}\",{maxExtra:F2},{maxDistance:F1},{distanceBone},{maxSpeed:F1},{speedBone},{minScale:F3},{maxScale:F3},{world.LastContacts},{world.Resets},{world.LastSubSteps}"));
                if(second%60==0)Capture($"minute-{second/60:00}");
                if(maxExtra>100||maxScale>2||minScale<.5){log.Add(FormattableString.Invariant($"BLOW-UP at {second} s: stretch {maxStretch:F2} (+{maxExtra:F1}) {stretchLink}; scale {minScale:F3}..{maxScale:F3}; distance {maxDistance:F0} {distanceBone}; speed {maxSpeed:F0} {speedBone}"));Capture("blowup");break;}
            }
            if(traceLines.Count>0)File.WriteAllLines(Path.Combine(output,"trace.txt"),traceLines);
            log.Add($"ran {rows.Count-1} s; resets {world.Resets}; diagnostics: {string.Join(" | ",scene.Diagnostics.Where(d=>d.Contains("HDT")).Distinct().Take(5))}");
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{File.WriteAllLines(Path.Combine(output,"soak.csv"),rows);File.WriteAllLines(Path.Combine(output,"summary.txt"),log);Close();}
    }
    async Task SmpSmoke(string[] arguments)
    {
        var log=new List<string>();string output=Path.Combine(AppContext.BaseDirectory,"smp-smoke");Directory.CreateDirectory(output);
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);if(!ok)throw new InvalidDataException(message);}
        try{
            await EquipPhysicsScene(arguments,log);
            cameraSubject.SelectedIndex=1;clock.Pause();clock.Seek(0);scene.SetTime(0);scene.Frame();viewport.InvalidateRender();await Task.Delay(200);
            using(var off=viewport.CaptureBitmap())off.Save(Path.Combine(output,"physics-off.png"));
            var restWeapon=scene.WeaponTransforms;
            // Switch on and let gravity act with the animation paused.
            smpPhysics.Checked=true;await Task.Delay(100);
            log.AddRange(scene.Diagnostics.Where(d=>d.StartsWith("HDT-SMP")));
            Check(scene.PhysicsActive&&scene.PhysicsSystems>=1,$"Physics systems were built from the equipped items ({scene.PhysicsSystems} system(s), {scene.PhysicsDynamicBones} simulated bones, {scene.PhysicsConstraints} constraints, {scene.PhysicsBodies} collision shapes)");
            Check(scene.PhysicsDynamicBones>0,"At least one bone is simulated");
            var world=scene.PhysicsWorld!;
            V3d[] Origins()=>world.Systems.SelectMany(s=>s.Bones).Where(b=>!b.Kinematic).Select(b=>SmpTransform.From(b.Rig.WorldTransform).Origin).ToArray();
            await Task.Delay(250);var start=Origins();
            await Task.Delay(2500);var settled=Origins();
            using(var on=viewport.CaptureBitmap())on.Save(Path.Combine(output,"physics-on-paused.png"));
            double moved=Enumerable.Range(0,start.Length).Max(i=>(settled[i]-start[i]).Length);
            foreach(var system in world.Systems){
                var simulated=system.Bones.Where(b=>!b.Kinematic).ToArray();var fixedBones=system.Bones.Where(b=>b.Kinematic).ToArray();
                // Distance from each simulated bone to the nearest kinematic bone of its system tells where the system sits.
                double reach=simulated.Length==0||fixedBones.Length==0?0:simulated.Max(b=>fixedBones.Min(k=>(k.Current.Origin-b.Current.Origin).Length));
                log.Add($"system {system.Name}: {simulated.Length} simulated, lowest z {simulated.Select(b=>b.Current.Origin.Z).DefaultIfEmpty(0).Min():F1}, farthest simulated bone from a kinematic bone {reach:F1}");
                Check(reach<150,$"{system.Name}: simulated bones stay near the bones they hang from");
            }
            if(scene.WeaponTransforms.Length>0&&world.Systems.SelectMany(s=>s.Bones).Any(b=>b.Depth>=64)){
                // Weapon strap: its bones live in the weapon NIF, so they must sit at the weapon, not at the actor root.
                var weaponAt=scene.WeaponTransforms[0].Row3.Xyz;var strap=world.Systems.SelectMany(s=>s.Bones).Where(b=>b.Depth>=64).ToArray();
                double far=strap.Max(b=>(b.Current.Origin-new V3d(weaponAt.X,weaponAt.Y,weaponAt.Z)).Length);
                log.Add($"weapon bones: {strap.Length} ({strap.Count(b=>!b.Kinematic)} simulated), farthest from the weapon origin {far:F1}");
                Check(far<200,"Bones that live in the weapon NIF are simulated at the weapon");
                var saved=(viewport.camera.FocusPosition,viewport.camera.distance);
                var middle=strap.Aggregate(V3d.Zero,(sum,b)=>sum+b.Current.Origin)/strap.Length;
                smpColliders.Checked=true;smpBones.Checked=true;
                viewport.camera.FocusPosition=new OpenTK.Mathematics.Vector3((float)middle.X,(float)middle.Y,(float)middle.Z);viewport.camera.distance=70;viewport.UpdateProjection(true);viewport.InvalidateRender();await Task.Delay(250);
                using(var close=viewport.CaptureBitmap())close.Save(Path.Combine(output,"weapon-strap-overlay.png"));
                smpColliders.Checked=false;smpBones.Checked=false;viewport.InvalidateRender();await Task.Delay(150);
                using(var close=viewport.CaptureBitmap())close.Save(Path.Combine(output,"weapon-strap.png"));
                (viewport.camera.FocusPosition,viewport.camera.distance)=saved;viewport.UpdateProjection(true);viewport.InvalidateRender();await Task.Delay(100);
            }
            log.Add($"paused: steps {scene.PhysicsSteps}, resets {scene.PhysicsResets}, largest bone travel {moved:F2}, contacts now {scene.PhysicsContacts}, peak {scene.PhysicsPeakContacts}, step {scene.PhysicsStepMs:F2} ms");
            Check(scene.PhysicsSteps>60,"The simulation keeps stepping while playback is paused");
            Check(settled.All(p=>double.IsFinite(p.X+p.Y+p.Z)&&p.Length<5000),"Every simulated bone stays finite and near the actor");
            // Constraints hold: a simulated bone stays within reach of the bone it is tied to.
            double worst=0;foreach(var system in world.Systems)foreach(var (a,b) in system.Links)worst=Math.Max(worst,(a.Current.Origin-b.Current.Origin).Length);
            log.Add($"longest constraint span {worst:F2}");Check(worst<120,"Constrained bones stay together");
            // Play the clip: kinematic bones follow the animation and drag the simulated ones.
            smpColliders.Checked=true;smpBones.Checked=true;
            if(clip!=null){clock.Seek(0);clock.Play();}
            double peakMs=0,sumMs=0,sumCollision=0;int samples=0,substeps=0;
            for(int i=0;i<40;i++){await Task.Delay(100);peakMs=Math.Max(peakMs,scene.PhysicsStepMs);sumMs+=scene.PhysicsStepMs;sumCollision+=scene.PhysicsCollisionMs;substeps+=scene.PhysicsSubSteps;samples++;if(i==20)using(var mid=viewport.CaptureBitmap())mid.Save(Path.Combine(output,"physics-playing-overlay.png"));}
            clock.Pause();
            var played=Origins();
            log.Add($"playing: steps {scene.PhysicsSteps}, resets {scene.PhysicsResets}, contacts peak {scene.PhysicsPeakContacts}, step mean {sumMs/samples:F2} ms (collision {sumCollision/samples:F2} ms, {substeps/(double)samples:F1} substeps), peak {peakMs:F2} ms, overlay {scene.PhysicsOverlayLines} lines ({scene.PhysicsOverlaySpheres} spheres, {scene.PhysicsOverlayPlates} plates), last pose jump at a wrap {scene.LastPoseJump:F2}, clip {clip?.Duration:F3} s");
            Check(played.All(p=>double.IsFinite(p.X+p.Y+p.Z)&&p.Length<5000),"The simulation stays finite through playback");
            if(scene.PhysicsBodies>0)Check(scene.PhysicsOverlaySpheres+scene.PhysicsOverlayPlates>0,"The overlay draws the collision shapes");
            Check(scene.PhysicsOverlayLines>0,"The overlay draws bones and constraints");
            UpdatePhysicsLabel();for(Control? parent=smpLabel.Parent;parent!=null;parent=parent.Parent)if(parent is ScrollableControl {AutoScroll:true} scroller){scroller.AutoScrollPosition=new Point(0,Math.Max(0,smpPhysics.Top-120));break;}
            await Task.Delay(150);
            using(var window=new Bitmap(Width,Height)){DrawToBitmap(window,new Rectangle(Point.Empty,Size));window.Save(Path.Combine(output,"window-physics.png"));}
            Check(smpLabel.Text.StartsWith("HDT-SMP ·")&&smpLabel.Text.Contains("simulated"),"The panel reports the live simulation: "+smpLabel.Text);
            smpColliders.Checked=false;smpBones.Checked=false;await Task.Delay(150);
            using(var plain=viewport.CaptureBitmap())plain.Save(Path.Combine(output,"physics-playing.png"));
            if(scene.WeaponTransforms.Length>0&&sheathed.Enabled&&world.Systems.SelectMany(s=>s.Bones).Any(b=>b.Depth>=64)){
                // Sheathing moves the weapon to its holster node in one frame; the strap is reset there, not dragged across.
                int resets=scene.PhysicsResets;sheathed.Checked=true;await Task.Delay(700);
                var holstered=scene.WeaponTransforms[0].Row3.Xyz;var strap=world.Systems.SelectMany(s=>s.Bones).Where(b=>b.Depth>=64).ToArray();
                double far=strap.Max(b=>(SmpTransform.From(b.Rig.WorldTransform).Origin-new V3d(holstered.X,holstered.Y,holstered.Z)).Length);
                log.Add($"sheathed on {scene.WeaponBoneName}: resets {scene.PhysicsResets-resets}, strap bones within {far:F1} of the weapon");
                Check(scene.PhysicsResets>resets&&far<200,"Moving the weapon to its holster node resets the strap at the new place");
                using(var back=viewport.CaptureBitmap())back.Save(Path.Combine(output,"weapon-sheathed.png"));
                sheathed.Checked=false;await Task.Delay(300);
            }
            if(clip!=null){
                // Scrub across the clip while paused: a seek carries or resets the simulation, it never explodes.
                foreach(double fraction in new[]{.8,.1,.5}){clock.Seek(clip.Duration*fraction);scene.SetTime(clock.Time);await Task.Delay(200);}
                Check(Origins().All(p=>double.IsFinite(p.X+p.Y+p.Z)&&p.Length<5000),"Seeking keeps the simulation finite");
                double worstAfter=0;foreach(var system in world.Systems)foreach(var (a,b) in system.Links)worstAfter=Math.Max(worstAfter,(a.Current.Origin-b.Current.Origin).Length);
                log.Add($"after seeks: longest constraint span {worstAfter:F2}, resets {scene.PhysicsResets}, last pose jump {scene.LastPoseJump:F2}");
                Check(worstAfter<120,"Constraints hold after seeking");
            }
            // Switch off: every bone returns to the animation alone.
            smpPhysics.Checked=false;clock.Seek(0);scene.SetTime(0);viewport.InvalidateRender();await Task.Delay(200);
            Check(!scene.PhysicsActive&&scene.Skeleton.SkeletonDictionary.Values.All(b=>b.PhysicsDeltaTransform==null),"Switching physics off clears the physics layer on every bone");
            var restored=scene.WeaponTransforms;
            Check(restored.Length==restWeapon.Length&&Enumerable.Range(0,restored.Length).All(i=>restored[i]==restWeapon[i]),"The weapon returns to its unsimulated placement");
            using(var again=viewport.CaptureBitmap())again.Save(Path.Combine(output,"physics-off-again.png"));
            using(var first=new Bitmap(Path.Combine(output,"physics-off.png")))using(var last=new Bitmap(Path.Combine(output,"physics-off-again.png"))){
                int different=0,total=0;
                for(int y=0;y<first.Height;y+=4)for(int x=0;x<first.Width;x+=4){total++;if(first.GetPixel(x,y)!=last.GetPixel(x,y))different++;}
                log.Add($"off/on/off pixel difference: {different} of {total} samples");
                Check(different<=total/200,"The render with physics off is the same before and after a simulation run");
            }
        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"smp-smoke.txt"),log);}finally{Close();}}
    }
    static string ToolSessionPath()
    {
        string? chosen=Environment.GetEnvironmentVariable("SSE_SESSION_FILE");if(!string.IsNullOrWhiteSpace(chosen))return chosen;
        // A build inside the source tree (bin or Portable) reads the tree's Portable session; a copied Portable folder reads its own.
        try{return Path.Combine(ParticleTests.VerificationProjectRoot(),"Portable","session.json");}
        catch(InvalidOperationException){return Path.Combine(AppContext.BaseDirectory,"session.json");}
    }
    async Task ModSweep(string sourceSubstring){
        string output=Path.Combine(AppContext.BaseDirectory,"mod-sweep");Directory.CreateDirectory(output);
        var rows=new List<string>{"clip,duration,presented,distinct_times,worst_gap_ms,max_particles,max_drawn,max_radius,max_seek_ms,effects,texture_failures,note"};
        try{
            var state=JsonSerializer.Deserialize<SessionSettings>(File.ReadAllText(ToolSessionPath()))!;
            await ApplySettings(state with {Clip=null,Effects=true,Speed=1,FollowCamera=false,GridFloor=true});
            var entries=assets.Clips.Where(c=>assets.Source(c.Path).Contains(sourceSubstring,StringComparison.OrdinalIgnoreCase)).OrderBy(c=>c.Path,StringComparer.OrdinalIgnoreCase).ToArray();
            int limit=int.TryParse(Environment.GetEnvironmentVariable("SSE_SWEEP_LIMIT"),out int l)?l:int.MaxValue;
            double seconds=double.TryParse(Environment.GetEnvironmentVariable("SSE_SWEEP_SECONDS"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double s)?s:1.5;
            foreach(var entry in entries.Take(limit)){
                string note="";int presented=0;var times=new HashSet<double>();double worstGap=0,lastPresent=-1;int maxParticles=0,maxDrawn=0;double maxRadius=0,maxSeek=0;
                var watch=Stopwatch.StartNew();
                void Observe(){presented++;times.Add(Math.Round(scene.Time,4));double now=watch.Elapsed.TotalMilliseconds;if(lastPresent>=0)worstGap=Math.Max(worstGap,now-lastPresent);lastPresent=now;
                    maxParticles=Math.Max(maxParticles,scene.ParticleCount);maxDrawn=Math.Max(maxDrawn,scene.DrawnParticles);
                    foreach(var system in scene.ParticleInstances){maxSeek=Math.Max(maxSeek,system.LastSeekMs);foreach(var p in system.Simulation.Particles)maxRadius=Math.Max(maxRadius,p.Radius);}}
                try{
                    // SSE_SWEEP_NOFRAME=1 keeps whatever camera a plain library selection produces, to check the automatic framing.
                    int subject=int.TryParse(Environment.GetEnvironmentVariable("SSE_SWEEP_SUBJECT"),out int chosen)?Math.Clamp(chosen,0,2):1;
                    followCamera.Checked=false;scene.FollowScene=false;if(cameraSubject.SelectedIndex!=subject)cameraSubject.SelectedIndex=subject;
                    await SelectClip(entry);clock.Pause();clock.Seek(0);scene.SetTime(0);if(Environment.GetEnvironmentVariable("SSE_SWEEP_NOFRAME")!="1")scene.Frame();
                    note=FormattableString.Invariant($"camera distance {viewport.camera.distance:F0}; meshes {viewport.Model.meshes.Count}; pooled {scene.PooledEffectCues} cues in {scene.PooledEffectInstances} instances; oversized {scene.OversizedEffectMeshes}");
                    viewport.BeforeSceneFrame+=Observe;watch.Restart();clock.Play();timer.Start();
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds,clip!.Duration+.2)));
                    clock.Pause();viewport.BeforeSceneFrame-=Observe;
                    double mid=Math.Min(clip.Duration*.55,clip.Duration);scene.SetTime(mid);if(subject!=1){scene.Frame();note+=FormattableString.Invariant($"; mid-clip framing distance {viewport.camera.distance:F0}, oversized excluded {scene.OversizedEffectMeshes}");}
                    using(var shot=viewport.CaptureBitmap())shot.Save(Path.Combine(output,Path.GetFileNameWithoutExtension(entry.Path)+"-"+Path.GetFileName(Path.GetDirectoryName(entry.Path))+".png"));
                    if(presented<5||times.Count<3)note="PLAYBACK STALLED "+note;
                }catch(Exception ex){note="ERROR "+ex.Message.Replace(',',';').Replace('\n',' ');}
                int textureFailures=scene.Diagnostics.Count(d=>d.StartsWith("Particle texture "));
                rows.Add(FormattableString.Invariant($"{entry.Path.Replace(',',';')},{clip?.Duration??0:F3},{presented},{times.Count},{worstGap:F1},{maxParticles},{maxDrawn},{maxRadius:F1},{maxSeek:F2},{cues.Count},{textureFailures},{note}"));
                File.WriteAllLines(Path.Combine(output,"sweep.csv"),rows);
                File.AppendAllText(Path.Combine(output,"diagnostics.txt"),"=== "+entry.Path+"\n"+string.Join("\n",scene.Diagnostics.Where(d=>d.StartsWith("Particle texture ")||d.Contains("no alpha property")||d.Contains("Missing")))+"\n");
            }
        }catch(Exception ex){File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"sweep.csv"),rows);}finally{Close();}}
    }
    async Task ClipProfile(string clipSuffix,string outputName){
        var rows=new List<string>{"time,pose_ms,frame_ms,update_ms,simulation_ms,effect_draw_ms,particles,drawn"};
        string output=Path.Combine(AppContext.BaseDirectory,"clip-profile",outputName);Directory.CreateDirectory(output);
        try{
            var state=JsonSerializer.Deserialize<SessionSettings>(File.ReadAllText(ToolSessionPath()))!;
            await ApplySettings(state with {Clip=null,Effects=true,Speed=1,FollowCamera=false,GridFloor=true});
            var entry=assets.Clips.First(c=>c.Path.EndsWith(clipSuffix,StringComparison.OrdinalIgnoreCase));
            await SelectClip(entry);clock.Pause();viewport.PlayingAnimation=false;timer.Stop();followCamera.Checked=false;scene.FollowScene=false;
            scene.SetTime(0);cameraSubject.SelectedIndex=1;scene.Frame();viewport.camera.distance*=1.6f;
            File.WriteAllText(Path.Combine(output,"effects.txt"),detail.Text+"\n\n"+events.Text+"\n\n"+diagnostics.Text);
            var systems=new List<string>();
            foreach(string name in (Environment.GetEnvironmentVariable("SSE_PROFILE_HIDE")??"").Split(';',StringSplitOptions.RemoveEmptyEntries))scene.HiddenSystems.Add(name.Trim());
            if(Environment.GetEnvironmentVariable("SSE_PROFILE_NOEFFECTS")=="1"){effectToggle.Checked=false;scene.ShowEffects=false;}
            var extraTimes=(Environment.GetEnvironmentVariable("SSE_PROFILE_TIMES")??"").Split(';',StringSplitOptions.RemoveEmptyEntries).Select(v=>double.Parse(v,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            foreach(double at in extraTimes){scene.SetTime(at);using var shot=viewport.CaptureBitmap();shot.Save(Path.Combine(output,FormattableString.Invariant($"at-{at:0.000}.png")));systems.Add($"--- at {at:F3}");systems.AddRange(scene.FramingDiagnostics());}
            int frames=(int)Math.Ceiling(clip!.Duration*30);
            for(int i=0;i<=frames;i++){
                // InvalidateRender runs the engine pose pipeline as live playback does, so captured poses are current.
                double t=i/30d;var watch=Stopwatch.StartNew();scene.SetTime(t);viewport.InvalidateRender();double pose=watch.Elapsed.TotalMilliseconds;watch.Restart();
                using(var rendered=viewport.CaptureBitmap()){if(i%10==0)rendered.Save(Path.Combine(output,$"frame-{i:000}.png"));}
                rows.Add(FormattableString.Invariant($"{t:F6},{pose:F4},{watch.Elapsed.TotalMilliseconds:F4},{scene.LastUpdateMs:F4},{scene.LastSimulationMs:F4},{scene.LastEffectDrawMs:F4},{scene.ParticleCount},{scene.DrawnParticles}"));
                if(i%10==0){systems.Add($"--- t={t:F3}");systems.AddRange(scene.FramingDiagnostics());File.WriteAllLines(Path.Combine(output,"frames.csv"),rows);await Task.Delay(1);}
            }
            File.WriteAllLines(Path.Combine(output,"systems.txt"),systems);
            File.WriteAllLines(Path.Combine(output,"profile-buckets.txt"),ParticleSimulation.Profile.OrderByDescending(p=>p.Value).Select(p=>$"{p.Value*1000.0/Stopwatch.Frequency,10:F1} ms  {p.Key}"));
        }catch(Exception ex){File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"frames.csv"),rows);}finally{Close();}}
    }
    async Task WeaponArtProfile(){
        var rows=new List<string>{"time,pose_ms,frame_ms,update_ms,simulation_ms,effect_draw_ms,particles,drawn"};
        string output=Environment.GetEnvironmentVariable("SSE_PROFILE_OUTPUT")??Path.Combine(AppContext.BaseDirectory,"weaponart-profile");Directory.CreateDirectory(output);
        try{
            var state=JsonSerializer.Deserialize<SessionSettings>(File.ReadAllText(ToolSessionPath()))!;
            await ApplySettings(state with {Clip=@"meshes\actors\character\animations\DynamicAnimationReplacer\_CustomConditions\2050000001\mco_weaponart.HKX",Effects=true,Speed=1,FollowCamera=false,GridFloor=true});
            clock.Pause();viewport.PlayingAnimation=false;timer.Stop();scene.Frame();
            File.WriteAllText(Path.Combine(output,"effects.txt"),events.Text+"\n"+diagnostics.Text);
            for(int i=0;i<=80;i++){
                double t=i/30d;var watch=Stopwatch.StartNew();scene.SetTime(t);double pose=watch.Elapsed.TotalMilliseconds;watch.Restart();
                using(var rendered=viewport.CaptureBitmap()){if(i==44)rendered.Save(Path.Combine(output,"attack.png"));}
                rows.Add(FormattableString.Invariant($"{t:F6},{pose:F4},{watch.Elapsed.TotalMilliseconds:F4},{scene.LastUpdateMs:F4},{scene.LastSimulationMs:F4},{scene.LastEffectDrawMs:F4},{scene.ParticleCount},{scene.DrawnParticles}"));
                if(i%10==0){File.WriteAllLines(Path.Combine(output,"frames.csv"),rows);await Task.Delay(1);}
            }
            scene.SetTime(44/30d);scene.FramingSubject=Scene.FrameSubject.Effects;scene.Frame();
            using(var rendered=viewport.CaptureBitmap())rendered.Save(Path.Combine(output,"effect-closeup.png"));
        }catch(Exception ex){File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());Environment.ExitCode=1;}
        finally{try{File.WriteAllLines(Path.Combine(output,"frames.csv"),rows);}finally{Close();}}
    }
    async Task TimingSmoke()
    {
        var log=new List<string>();var frames=new List<(double Wall,double Time)>();var watch=Stopwatch.StartNew();
        void Observe()=>frames.Add((watch.Elapsed.TotalSeconds,scene.Time));
        try{
            string root=Path.Combine(AppContext.BaseDirectory,"timing-fixture");string model=ParticleFixture.Create(root);await Mount(root,false);
            PreviewParticle(model);clock.Pause();clock.Seek(0);scene.SetTime(0);followCamera.Checked=false;scene.FollowScene=false;
            fps.Value=1;clock.Loop=false;clock.Duration=10;clock.Speed=1;await Task.Delay(200);
            viewport.BeforeSceneFrame+=Observe;watch.Restart();clock.Play();await Task.Delay(1600);clock.Pause();viewport.BeforeSceneFrame-=Observe;
            double elapsed=watch.Elapsed.TotalSeconds;
            if(Math.Abs(clock.Absolute-elapsed)>.1)throw new InvalidDataException("Playback time drifted from elapsed wall time");
            if(frames.Select(f=>Math.Round(f.Time,3)).Distinct().Count()<8)throw new InvalidDataException("Step FPS still throttles presented scene times");
            var gaps=frames.Zip(frames.Skip(1),(a,b)=>b.Wall-a.Wall).ToArray();
            if(gaps.Length==0||gaps.Min()<1/60d-.001)throw new InvalidDataException("Presented frames exceeded the 60 FPS cap");
            using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"timing-render.png"));
            log.Add($"PASS elapsed={elapsed:F6}s playback={clock.Absolute:F6}s presented={frames.Count}; minimum presentation interval={gaps.Min():F6}s; step FPS=1.");
            log.Add("PASS Distinct scene times continue between stepping frames; wall-clock speed and the 60 FPS presentation cap are independent.");
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"timing-frames.csv"),new[]{"wall_seconds,scene_seconds"}.Concat(frames.Select(f=>FormattableString.Invariant($"{f.Wall:F9},{f.Time:F9}"))));
            await Mount(SelfTest.TestData,false);await Mount(Fixture,true);
            await SelectClip(assets.Clips.First(c=>c.Name.Equals("mco_attack1.hkx",StringComparison.OrdinalIgnoreCase)));clock.Pause();viewport.PlayingAnimation=false;scene.RootMotion=false;scene.ShowEffects=false;followCamera.Checked=false;scene.FollowScene=false;
            clock.Seek(.1);scene.SetTime(.1);scene.Frame();await Task.Delay(300);
            var beforeBones=viewport.Model.meshes.SelectMany(m=>m.MeshData.Meshgeometry.GPUBoneMatrices??[]).ToArray();
            using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"live-pose-before.png"));
            clock.Play();await Task.Delay(450);
            var afterBones=viewport.Model.meshes.SelectMany(m=>m.MeshData.Meshgeometry.GPUBoneMatrices??[]).ToArray();
            clock.Pause();using(var image=viewport.CaptureBitmap())image.Save(Path.Combine(AppContext.BaseDirectory,"live-pose-after.png"));
            if(beforeBones.Length==0||beforeBones.SequenceEqual(afterBones))throw new InvalidDataException("Live HKX playback did not update GPU skinning matrices");
            log.Add($"PASS Live HKX playback updated {afterBones.Length} GPU skinning matrices while the viewport's idle timer was stopped.");

        }catch(Exception ex){log.Add("FAILED: "+ex);Environment.ExitCode=1;}
        finally{viewport.BeforeSceneFrame-=Observe;File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"timing-smoke.txt"),log);Close();}
    }
    async Task ParticleSmoke()
    {
        try{
            string root=Environment.GetEnvironmentVariable("SSE_PARTICLE_TEST_ROOT")??Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,@"..\..\..\..\particle-fixtures"));
            string model=Environment.GetEnvironmentVariable("SSE_PARTICLE_TEST_NIF")??Path.Combine(root,@"meshes\previewer\fountain.nif");
            await Mount(root,false);
            string? overlay=Environment.GetEnvironmentVariable("SSE_PARTICLE_TEST_OVERLAY");if(overlay!=null)await Mount(overlay,true);
            PreviewParticle(model);clock.Pause();
            if(Environment.GetEnvironmentVariable("SSE_PARTICLE_TEST_BIND_SOURCES")=="1"){
                foreach(var item in scene.MeshEmitters.Where(p=>p.BoundEmitterMeshes.Length==0)){
                    var source=scene.MeshEmitters.Where(p=>ReferenceEquals(p.Nif,item.Nif)).SelectMany(p=>p.BoundEmitterMeshes).FirstOrDefault();
                    if(source!=null)item.BindEmitterMeshes([source]);
                }
                scene.RefreshEmitterDiagnostics();scene.ResetParticles();
            }
            if(Environment.GetEnvironmentVariable("SSE_PARTICLE_TEST_SIDE_VIEW")=="1"){
                clock.Seek(.5);scene.SetTime(.5);await Task.Delay(250);
                viewport.camera=new OrbitCamera{MaxDistance=100000};viewport.camera.Rotate(45,15);scene.Frame();followCamera.Checked=false;scene.FollowScene=false;
            }
            if(float.TryParse(Environment.GetEnvironmentVariable("SSE_PARTICLE_TEST_LOD"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var lodDistance))scene.SetParticleLodDistance(lodDistance);
            if(float.TryParse(Environment.GetEnvironmentVariable("SSE_PARTICLE_TEST_CAMERA_DISTANCE"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var cameraDistance)&&float.IsFinite(cameraDistance)&&cameraDistance>0){followCamera.Checked=false;scene.FollowScene=false;viewport.camera.distance=cameraDistance;}
            var log=new List<string>{$"camera focus={viewport.camera.FocusPosition} distance={viewport.camera.distance}",
                "Explicit test binding: "+(Environment.GetEnvironmentVariable("SSE_PARTICLE_TEST_BIND_SOURCES")=="1")};
            var smokeTimes=(Environment.GetEnvironmentVariable("SSE_PARTICLE_TEST_TIMES")??"").Split(';',StringSplitOptions.RemoveEmptyEntries).Select(v=>double.Parse(v,System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(double.NaN).ToArray();
            foreach(double time in double.IsNaN(smokeTimes[0])?new[]{.2,.5,1d,2d,4d}:smokeTimes){
                clock.Seek(time);scene.SetTime(time);await Task.Delay(250);
                using var bitmap=viewport.CaptureBitmap();
                bitmap.Save(Path.Combine(AppContext.BaseDirectory,$"particles-{time:0.0}.png"));
                if(time==.2)File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"particle-meshes.txt"),viewport.Model.meshes.Select(m=>$"{m.MeshData.Shape.NifShape.Name}: hidden={m.MeshData.Shape.RenderHide}; flags={m.MeshData.Shape.NifShape.Flags_ui}; alpha={m.MeshData.Material.MaterialBase.Alpha}; threshold={m.MeshData.Material.MaterialBase.AlphaTestRef}; min={m.BoundsMin}; max={m.BoundsMax}; transform={m.MeshData.Transform}; textures={string.Join(";",m.MeshData.Material.Textures_Path_List)}"));
                log.Add($"{time:F2}s systems={scene.ParticleSystems} live={scene.ParticleCount} drawn={scene.DrawnParticles}");
                if(Environment.GetEnvironmentVariable("SSE_PARTICLE_TEST_TIMES")!=null)log.AddRange(scene.FramingDiagnostics().Where(l=>l.StartsWith("Particles")).Select(l=>"    "+l));
                if(Environment.GetEnvironmentVariable("SSE_PARTICLE_TEST_EXPECT_LIGHTING_UV")=="1"){
                    var litMeshes=viewport.Model.meshes.Where(m=>m.MeshData.Shape.NifShader is NiflySharp.Blocks.BSLightingShaderProperty).ToArray();
                    if(litMeshes.Length==0)throw new InvalidDataException("Expected lighting-shader meshes");
                    int validatedLighting=0;
                    foreach(var mesh in litMeshes.Where(scene.SubmittedForDrawing)){
                        var shader=(NiflySharp.Blocks.BSLightingShaderProperty)mesh.MeshData.Shape.NifShader;var material=mesh.MeshData.Material.MaterialBase;
                        if(Math.Abs(material.VOffset-shader.UVOffset.V)>1e-6||Math.Abs(material.UOffset-shader.UVOffset.U)>1e-6||Math.Abs(material.Alpha-shader.Alpha)>1e-6)
                            throw new InvalidDataException($"Lighting material at {time}: {mesh.MeshData.Shape.NifShape.Name}: UV {material.UOffset},{material.VOffset} vs {shader.UVOffset.U},{shader.UVOffset.V}; alpha {material.Alpha} vs {shader.Alpha}; hidden={mesh.MeshData.Shape.RenderHide}");
                        validatedLighting++;
                        log.Add($"PASS Lighting material upload: {mesh.MeshData.Shape.NifShape.Name}; VOffset={material.VOffset}; alpha={material.Alpha}");
                    }
                    if(time==.2&&validatedLighting==0)throw new InvalidDataException("No visible or refractive lighting material was validated");
                }
                if(Environment.GetEnvironmentVariable("SSE_PARTICLE_TEST_EXPECT_MESH_VISIBILITY")=="1"){
                    if(scene.ParticleCount==0||viewport.Model.meshes.Count!=0||
                        (time==.2?scene.DrawnParticles!=0:scene.DrawnParticles<=0||scene.DrawnParticles>=scene.ParticleCount))
                        throw new InvalidDataException("Visibility fixture must hide young instances and draw only the authored age window");
                    log.Add("PASS Mesh visibility draws only the authored particle-age window without standalone template geometry.");
                }
                if(Environment.GetEnvironmentVariable("SSE_PARTICLE_TEST_EXPECT_MESH")=="1"){
                    if(scene.ParticleCount==0||scene.DrawnParticles!=scene.ParticleCount||viewport.Model.meshes.Count!=0)throw new InvalidDataException("Mesh fixture must draw one geometry instance per live particle and no standalone template");
                    log.Add("PASS Mesh instance count equals live particle count; no standalone template mesh is drawn.");
                }

            }
            if(Environment.GetEnvironmentVariable("SSE_PARTICLE_TEST_REPLAY")=="1"){
                followCamera.Checked=false;scene.FollowScene=false;
                clock.Seek(.5);scene.SetTime(.5);await Task.Delay(250);using(var first=viewport.CaptureBitmap())first.Save(Path.Combine(AppContext.BaseDirectory,"particles-replay-a.png"));
                clock.Seek(.1);scene.SetTime(.1);await Task.Delay(50);clock.Seek(.5);scene.SetTime(.5);await Task.Delay(250);using(var replay=viewport.CaptureBitmap())replay.Save(Path.Combine(AppContext.BaseDirectory,"particles-replay-b.png"));
                if(!File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"particles-replay-a.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"particles-replay-b.png"))))throw new InvalidDataException("Fixed-camera particle rewind did not reproduce the rendered image");
                log.Add("PASS Fixed-camera backward seek reproduces the particle render byte for byte.");
            }
            UpdateDiagnostics();log.Add(diagnostics.Text);log.Add(scene.ParticleDebug);
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"particle-render.txt"),log);
        }catch(Exception ex){ShowError(ex);File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"particle-render.txt"),"FAILED: "+ex);Environment.ExitCode=1;}
        finally{Close();}
    }
    async Task Smoke()
    {
        try {await Mount(Environment.GetEnvironmentVariable("SSE_PREVIEW_TEST_DATA")??SelfTest.TestData,false);await Mount(Fixture,true);var first=assets.Clips.First(c=>c.Name.Equals("mco_attack1.hkx",StringComparison.OrdinalIgnoreCase));await SelectClip(first);clock.Pause();clock.Seek(.72);scene.SetTime(.72);viewport.camera.FocusPosition=new OpenTK.Mathematics.Vector3(0,290,90);viewport.camera.distance=500;viewport.camera.Rotate(75,15);viewport.UpdateProjection(true);
            var capture=new System.Windows.Forms.Timer{Interval=1800};capture.Tick+=(_,_)=>{capture.Stop();File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"scene-debug.txt"),string.Join("\n",viewport.Model.meshes.Select(m=>$"{m.MeshData.Shape.ShapeName} hidden={m.MeshData.Shape.RenderHide} alpha={m.MeshData.Material.MaterialBase.Alpha} transform={m.MeshData.Transform} diffuse={m.MeshData.Material.DiffuseTexture_ID} paths={string.Join(",",m.MeshData.Material.Textures_Path_List)}"))+" textures="+string.Join(";",viewport.Model.Textures_Dictionary.Select(t=>$"{t.Key}:{t.Value.Loaded}")));File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"loaded-modules.txt"),Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Select(m=>m.FileName));using var bmp=viewport.CaptureBitmap();bmp.Save(Path.Combine(AppContext.BaseDirectory,"smoke.png"));File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"smoke.txt"),$"clips={assets.Clips.Count}\nrecords={records.All.Count}\ncues={cues.Count}\nmeshes={viewport.Model.meshes.Count}\n{diagnostics.Text}");capture.Dispose();Close();};capture.Start();
        }catch(Exception ex){ShowError(ex);Environment.ExitCode=1;Close();}
    }
}





