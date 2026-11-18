using System.Drawing;
using FO4_Base_Library;

namespace SSEPreview;

// Paired animations (killmoves, hugs, mounting): the clip holds two actors, and the second one is drawn as a plain
// body of a chosen race beside the scene's own actor. The rules are in ScenePaired.cs.
public sealed partial class MainWindow
{
    const string AutoPartner="(Partner race: chosen from the clip)";
    readonly CheckBox partnerShow=new(){Text="Show the second actor of a paired clip",AutoSize=true,Checked=true};
    readonly CheckBox partnerSwap=new(){Text="Swap roles (play the other actor's tracks)",AutoSize=true};
    readonly ComboBox partnerRace=Combo(),partnerSex=Combo();
    readonly Label partnerNote=new(){AutoSize=true,MaximumSize=new Size(330,0)};
    readonly Dictionary<string,HashSet<string>?> skeletonBones=new(StringComparer.OrdinalIgnoreCase);long skeletonBonesRevision=-1;

    void AddPartnerControls(FlowLayoutPanel stack)
    {
        partnerSex.Items.AddRange(["Partner: male","Partner: female"]);partnerSex.SelectedIndex=0;
        partnerRace.Items.Add(AutoPartner);partnerRace.SelectedIndex=0;
        stack.Controls.Add(partnerRace);stack.Controls.Add(partnerSex);stack.Controls.Add(partnerSwap);stack.Controls.Add(partnerNote);
        partnerNote.Text="The selected clip is not a paired clip.";
        partnerShow.CheckedChanged+=(_,_)=>{if(!selecting)RebuildScene();};partnerSwap.CheckedChanged+=(_,_)=>{if(!selecting)RebuildScene();};
        partnerRace.SelectedIndexChanged+=(_,_)=>{if(!selecting)RebuildScene();};partnerSex.SelectedIndexChanged+=(_,_)=>{if(!selecting)RebuildScene();};
    }
    void PopulatePartnerRaces()
    {
        bool was=selecting;selecting=true;
        try{
            string? kept=(partnerRace.SelectedItem as GameRecord)?.Key;
            partnerRace.Items.Clear();partnerRace.Items.Add(AutoPartner);partnerRace.Items.AddRange(records.Of("RACE").Cast<object>().ToArray());
            var race=kept==null?null:records.All.GetValueOrDefault(kept);
            if(race?.Kind=="RACE")partnerRace.SelectedItem=race;else partnerRace.SelectedIndex=0;
        }finally{selecting=was;}
    }
    // Bone names of the skeleton.hkx beside a skeleton NIF; null when there is none or it cannot be read.
    HashSet<string>? SkeletonBones(string skeletonNif)
    {
        if(skeletonBonesRevision!=assets.Revision){skeletonBones.Clear();skeletonBonesRevision=assets.Revision;}
        if(skeletonBones.TryGetValue(skeletonNif,out var known))return known;
        HashSet<string>? bones=null;string hkx=Path.ChangeExtension(skeletonNif,"hkx");
        try{
            if(assets.Exists(hkx)){
                var skeleton=HkxObjectGraphParser_Class.BuildGraph(HkxPackfileParser_Class.Parse(HkxTagfile.EnsurePackfile(assets.Read(hkx)))).EsqueletoDeAnimacion();
                if(skeleton?.Bones!=null)bones=skeleton.Bones.Select(b=>b.Name??"").Where(n=>n.Length>0).ToHashSet(StringComparer.Ordinal);
            }
        }catch(Exception){bones=null;}
        return skeletonBones[skeletonNif]=bones;
    }
    // The race whose skeleton has the most of the bones the clip animates for the partner's role. The actor's own
    // race is kept when it fits (two humans); among races sharing the best skeleton, one named in the clip's folder
    // (human&bear) comes first, then the shortest editor ID.
    GameRecord? AutoPartnerRace(PairedClip paired,string prefix,GameRecord? actorRace,string clipPath)
    {
        var wanted=paired.Bones(prefix).ToHashSet(StringComparer.Ordinal);
        if(wanted.Count==0)return actorRace;
        double Score(string nif)=>SkeletonBones(nif) is {} bones?wanted.Count(bones.Contains)/(double)wanted.Count:0;
        if(actorRace!=null&&Score(RaceSkeleton(actorRace,false))>=.9)return actorRace;
        string folder=Path.GetFileName(Path.GetDirectoryName(Assets.Normalize(clipPath))??"");
        string creature=folder.Contains('&')?folder[(folder.IndexOf('&')+1)..]:"";
        var best=races.Items.Cast<GameRecord>().Select(r=>(Race:r,Nif:DeclaredSkeleton(r,false))).Where(x=>x.Nif!=null&&assets.Exists(x.Nif))
            .GroupBy(x=>x.Nif!,StringComparer.OrdinalIgnoreCase).Select(g=>(Score:Score(g.Key),Races:g.Select(x=>x.Race).ToList()))
            .OrderByDescending(g=>g.Score).FirstOrDefault();
        if(best.Races==null||best.Score<.5)return null;
        return best.Races.OrderByDescending(r=>creature.Length>0&&r.EditorId.Contains(creature,StringComparison.OrdinalIgnoreCase)).ThenBy(r=>r.EditorId.Length).First();
    }
    // Adds the partner's body to the scene being rebuilt: the race's skin and its default head parts, on a skeleton of
    // its own. Runs after the scene's own skeleton is loaded, because the roles are told from it.
    void AddPartner(GameRecord? actorRace)
    {
        if(clip?.Paired is not {} paired){scene.PairedSwap=false;return;}
        scene.PairedSwap=partnerSwap.Checked;scene.ChoosePairedPrefix(clip);
        if(!partnerShow.Checked)return;
        bool female=partnerSex.SelectedIndex==1;
        var race=partnerRace.SelectedItem as GameRecord??AutoPartnerRace(paired,scene.PartnerPrefix,actorRace,selected?.Path??"");
        if(race==null){scene.Diagnostics.Add("No loaded race has a skeleton that fits the paired clip's second actor; choose the partner race by hand.");return;}
        string nif=RaceSkeleton(race,female),project=RaceProject(race,female);
        string hkx=new[]{project+(female?@"character assets female\skeleton_female.hkx":@"character assets\skeleton.hkx"),project+@"character assets\skeleton.hkx",Path.ChangeExtension(nif,"hkx")}.FirstOrDefault(assets.Exists)??Path.ChangeExtension(nif,"hkx");
        if(!scene.BeginPartner(nif,hkx))return;
        try{
            var skinTone=records.RaceSkinColor(race,female);
            if(records.Resolve(race,race.First("WNAM")?.UInt()??0) is {Kind:"ARMO"} skin)
                foreach(var addon in records.ArmorAddons(skin,race)){
                    var path=addon.First(female?"MOD3":"MOD2")?.Text??addon.First("MOD2")?.Text;if(string.IsNullOrWhiteSpace(path))continue;
                    scene.AddModel(ProjectileVisuals.ModelPath(path),skinTextureOverride:records.ArmorSkinTextures(addon,female),skinColor:skinTone,hairColor:records.RaceHairColor(race,female));
                }
            var faceSet=records.TexturePaths(records.Resolve(race,race.First(female?"DFTF":"DFTM")?.UInt()??0));
            foreach(var head in records.HeadPartRecords(NpcLooks.HeadParts(records,[],race,female))){
                var model=head.First("MODL")?.Text;if(string.IsNullOrWhiteSpace(model))continue;
                bool face=head.First("PNAM")?.UInt()==1;
                scene.AddModel(model,textureOverride:records.TexturePaths(records.Resolve(head,head.First("TNAM")?.UInt()??0))??(face?faceSet:null),hairColor:records.HeadHairColor(head,race,female),skinColor:skinTone,
                    faceTextures:face?slots=>{if(faceSet!=null)foreach(int i in new[]{1,2,3})if(faceSet[i].Length>0)slots[i]=faceSet[i];slots[6]="";return slots;}:null);
            }
        }finally{scene.EndPartner();}
        scene.Diagnostics.Add($"Paired partner: a plain {race.EditorId} {(female?"female":"male")} ({scene.PartnerShapeCount} shapes), without equipment.");
    }
    void UpdatePartnerNote()=>partnerNote.Text=clip?.Paired==null?"The selected clip is not a paired clip.":scene.PairedStatus;

    // Plays paired clips on two actors without a window and measures how close named bones of the two come, which
    // only works out when both roles are bound by name and both marks are applied. Arguments: an output folder, Data
    // or mod folders, then checks "clip path|own bone|partner bone|time|expected distance".
    async Task PairedSmoke(string[] arguments)
    {
        var log=new List<string>();string output=arguments.Length>0?Path.GetFullPath(arguments[0]):Path.Combine(AppContext.BaseDirectory,"paired-smoke");Directory.CreateDirectory(output);
        var invariant=System.Globalization.CultureInfo.InvariantCulture;
        try{
            var folders=arguments.Skip(1).TakeWhile(Directory.Exists).ToArray();var checks=arguments.Skip(1+folders.Length).ToArray();
            if(folders.Length==0||checks.Length==0)throw new ArgumentException("--paired-smoke needs an output folder, at least one Data folder, then checks: clip|own bone|partner bone|time|distance.");
            for(int i=0;i<folders.Length;i++)await Mount(folders[i],i>0);
            followCamera.Checked=false;scene.FollowScene=false;smpPhysics.Checked=false;cameraSubject.SelectedIndex=0;
            int failed=0;
            foreach(string check in checks){
                var part=check.Split('|');string path=Assets.Normalize(part[0]);
                var entry=assets.Clips.FirstOrDefault(c=>Assets.Normalize(c.Path).EndsWith(path,StringComparison.OrdinalIgnoreCase));
                if(entry==null){log.Add("FAIL clip not found: "+path);failed++;continue;}
                await SelectClip(entry);clock.Pause();
                string name=Path.GetFileNameWithoutExtension(path);
                log.Add($"== {entry.Path}");log.Add("  "+scene.PairedStatus);
                log.Add($"  actor race {(races.SelectedItem as GameRecord)?.EditorId}; partner shapes {scene.PartnerShapeCount}; partner posed {scene.PartnerPosed}");
                foreach(string d in scene.Diagnostics.Where(d=>d.Contains("artner")||d.Contains("binding")).Distinct())log.Add("  scene: "+d);
                if(clip?.Paired==null){log.Add("FAIL the clip was not read as a paired clip");failed++;continue;}
                if(part.Length>=5){
                    double time=double.Parse(part[3],invariant),expected=double.Parse(part[4],invariant);
                    clock.Seek(time);scene.SetTime(time);
                    var own=scene.PairedBoneWorld(false,part[1]);var other=scene.PairedBoneWorld(true,part[2]);
                    if(own==null||other==null){log.Add($"FAIL bone not found: {(own==null?part[1]:part[2])}");failed++;}
                    else{
                        double distance=(own.Value-other.Value).Length();bool ok=Math.Abs(distance-expected)<=1.5;
                        log.Add(FormattableString.Invariant($"  {(ok?"PASS":"FAIL")} {part[1]} to partner {part[2]} at {time:0.##} s: {distance:0.00} units, expected {expected:0.00}"));
                        if(!ok)failed++;
                    }
                }
                foreach(double fraction in new[]{.1,.5,.9}){
                    double time=clip.Duration*fraction;clock.Seek(time);scene.SetTime(time);scene.Frame();
                    await WaitRenderReady(120,name);
                    using var image=viewport.CaptureBitmap();image?.Save(Path.Combine(output,FormattableString.Invariant($"{name}-{fraction*100:0}.png")));
                }
            }
            log.Add(failed==0?"PASS":"FAIL "+failed+" check(s)");if(failed>0)Environment.ExitCode=1;
        }catch(Exception ex){log.Add("FAIL "+ex);Environment.ExitCode=1;}
        finally{File.WriteAllLines(Path.Combine(output,"paired-smoke.txt"),log);Close();}
    }
}
