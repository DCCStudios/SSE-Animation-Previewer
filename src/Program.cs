using System.Text.Json;
using FO4_Base_Library;

namespace SSEPreview;
internal static class Program
{
    // Every switch the program answers to. ParticleTests checks this against the source.
    internal static readonly HashSet<string> Switches=new(StringComparer.Ordinal){"--no-activate","--show","--annotation-smoke","--archive-order-smoke","--attachment-smoke","--attack-effect-inspect","--background","--bfco-smoke","--billboard-test","--bladebeam-smoke","--camera-smoke","--catalog-test","--character-smoke","--ci-test","--clip-profile","--playback-probe","--cloud-effect-alpha-smoke","--cloud-equipment-smoke","--color-fixture","--data","--docs-capture","--dump-annotations","--effect-capture","--effect-graph-audit","--effect-sequence-test","--emitter-inspect","--emitter-source-smoke","--equipment-smoke","--fade-fixture","--ff7vfx-smoke","--flipbook-smoke","--floor-smoke","--frame-smoke","--frames-capture","--hair-smoke","--holster-smoke","--inspect","--library-smoke","--mod-sweep","--override-smoke","--particle-smoke","--particle-test","--precision-smoke","--preferences-test","--prepare-fixture","--queue-smoke","--record-inspect","--reference-effect-smoke","--refraction-smoke","--remote-cast-smoke","--scene-dialog-smoke","--scene-save-smoke","--self-test","--skeleton-smoke","--smoke","--smp-determinism","--smp-smoke","--smp-soak","--smp-test","--sound-smoke","--sources-recent-smoke","--speed-smoke","--tagfile-smoke","--texture-dump","--timing-smoke","--track-bone","--weaponart-profile-smoke","--window-pick-smoke"};
    [STAThread]
    static int Main(string[] args)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        Config_App.Current=new Config_App {Game=Config_App.Game_Enum.Skyrim,Setting_BackColorName="LightCyan",Setting_BackFade=50,Setting_BackFadeDirectional=false};
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        // A run started with a tool or test switch works off-screen without being asked, so a script never takes the
        // keyboard from whoever is at the machine. --show keeps such a run on screen. --no-activate (or
        // SSE_PREVIEW_NO_ACTIVATE=1) opens the window on screen as usual but leaves the focus where it was.
        MainWindow.FocusBefore=MainWindow.ForegroundNow();
        bool tool=args.Any(a=>a.StartsWith("--")&&a is not ("--data" or "--background" or "--no-activate" or "--show"));
        MainWindow.Background=!args.Contains("--show")&&(tool||args.Contains("--background")||Environment.GetEnvironmentVariable("SSE_PREVIEW_BACKGROUND")=="1");
        MainWindow.NoActivate=MainWindow.Background||args.Contains("--no-activate")||Environment.GetEnvironmentVariable("SSE_PREVIEW_NO_ACTIVATE")=="1";
        // A switch the program does not know is an error, not a silent normal start: a caller waiting for output
        // files would otherwise wait on a run that did nothing.
        if(args.Where(a=>a.StartsWith("--")).FirstOrDefault(a=>!Switches.Contains(a)) is {} unknown){
            string message=$"Unknown switch {unknown}. See docs/command-line.md for the switches this build has.";
            Console.Error.WriteLine(message);try{File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"error.log"),DateTime.Now+" "+message+Environment.NewLine);}catch(IOException){}catch(UnauthorizedAccessException){}
            return 2;
        }
        try {
            if(args.Contains("--preferences-test")){ViewerPreferences.Verify();return 0;}
            if(args.Contains("--smoke")){Logger.AllowInReleaseBuilds=true;Logger.Enabled=true;Logger.Initialize(Path.Combine(AppContext.BaseDirectory,"engine.log"));}
            if(args.Contains("--record-inspect")){
                string file=args[Array.IndexOf(args,"--record-inspect")+1];
                var rows=Records.Parse(File.ReadAllBytes(file),Path.GetFileName(file)).Select(r=>new{r.Kind,r.EditorId,r.Key,r.Masters,
                    fields=r.Fields.Select(f=>new{f.Tag,text=f.Bytes.All(b=>b==0||b>=32&&b<127)?f.Text:null,hex=Convert.ToHexString(f.Bytes)})});
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"record-inspection.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));return 0;
            }
            if(args.Contains("--attack-effect-inspect")){int i=Array.IndexOf(args,"--attack-effect-inspect");AttackEffectInspection.Run(args[i+1],args[i+2]).GetAwaiter().GetResult();return 0;}
            if(args.Contains("--emitter-inspect")){EmitterInspection.Run(args[Array.IndexOf(args,"--emitter-inspect")+1]);return 0;}
            if(args.Contains("--inspect")) {Inspect(args[Array.IndexOf(args,"--inspect")+1]);return 0;}
            if(args.Contains("--prepare-fixture")) {SelfTest.Fixture();return 0;}
            if(args.Contains("--texture-dump")){int i=Array.IndexOf(args,"--texture-dump");TextureInspection.Save(args[i+1],args[i+2]);return 0;}
            if(args.Contains("--catalog-test")) {ClipCatalogTests.Run();return 0;}
            if(args.Contains("--effect-sequence-test")) {EffectSequenceTests.Run();return 0;}
            if(args.Contains("--effect-graph-audit")) {EffectGraphInspection.Run();return 0;}
            if(args.Contains("--fade-fixture")){ParticleFixture.CreateDistanceFade(args[Array.IndexOf(args,"--fade-fixture")+1]);return 0;}
            if(args.Contains("--color-fixture")){ParticleFixture.CreateColorPacking(args[Array.IndexOf(args,"--color-fixture")+1]);return 0;}
            if(args.Contains("--billboard-test")) {BillboardTests.Run();return 0;}
            if(args.Contains("--particle-test")) {ParticleTests.Run();return 0;}
            if(args.Contains("--smp-test")) {SmpTests.Run();return 0;}
            if(args.Contains("--self-test")) {SelfTest.Run();return 0;}
            if(args.Contains("--ci-test")) return CiTest();
            Application.ThreadException+=(_,e)=>File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"error.log"),e.Exception.ToString()+Environment.NewLine);
            Application.Run(new MainWindow(args));return Environment.ExitCode;
        }catch(Exception ex) {File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"error.log"),ex.ToString());return 1;}
    }
    // The suites that run on bundled data alone. Checks that want test-data or the mod folders log a SKIP line.
    // Every suite runs even after another fails; the exit code is 0 only when all of them pass.
    static int CiTest()
    {
        var started=DateTime.UtcNow.AddSeconds(-2);var summary=new List<string>();bool passed=true;
        foreach(var (name,log,run) in new (string,string,Action)[]{("particle","particle-tests.txt",ParticleTests.Run),("smp","smp-tests.txt",()=>SmpTests.Run()),("catalog","catalog-tests.txt",ClipCatalogTests.Run)}){
            string? error=null;try{run();}catch(Exception ex){error=ex.ToString();}
            string file=Path.Combine(AppContext.BaseDirectory,log);var lines=File.Exists(file)&&File.GetLastWriteTimeUtc(file)>=started?File.ReadAllLines(file):[];
            int Count(string prefix)=>lines.Count(l=>l.StartsWith(prefix,StringComparison.Ordinal));
            bool ok=error==null&&Count("FAIL ")==0&&Count("PASS ")>0;passed&=ok;
            summary.Add($"{(ok?"PASS":"FAIL")} {name}: {Count("PASS ")} passed, {Count("FAIL ")} failed, {Count("SKIP ")} skipped, log {log}");
            summary.AddRange(lines.Where(l=>l.StartsWith("FAIL ",StringComparison.Ordinal)||l.StartsWith("SKIP ",StringComparison.Ordinal)).Select(l=>"  "+l));
            if(error!=null)summary.Add("  "+error.ReplaceLineEndings(Environment.NewLine+"  "));
        }
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"ci-test.txt"),summary);
        foreach(string line in summary)Console.WriteLine(line);
        return passed?0:1;
    }
    static string? Describe(object? v,int depth=0)
    {
        if(v==null)return null;
        if(v is NiflySharp.INiRef nr)return "#"+nr.Index;
        if(v is NiflySharp.NiString ns)return ns.Content;
        if(v is NiflySharp.Bitfields.AlphaFlags af)return af.Value.ToString();
        if(v is string||v.GetType().IsPrimitive||v is decimal)return v.ToString();
        if(v is System.Collections.IEnumerable e&&v is not string){
            if(depth>2)return v.GetType().Name;
            var items=e.Cast<object>().Take(64).Select(x=>Describe(x,depth+1)).ToList();return "["+string.Join("; ",items)+"]";
        }
        var type=v.GetType();
        if(type.IsValueType&&!type.IsEnum||type.Namespace?.StartsWith("NiflySharp.Structs")==true){
            if(depth>2)return type.Name;
            var parts=type.GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public).Select(f=>f.Name+"="+Describe(f.GetValue(v),depth+1))
                .Concat(type.GetProperties(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public).Where(p=>p.GetIndexParameters().Length==0).Select(p=>{object? x=null;try{x=p.GetValue(v);}catch{}return p.Name+"="+Describe(x,depth+1);}));
            return type.Name+"{"+string.Join(", ",parts)+"}";
        }
        return v.ToString();
    }
    static void Inspect(string root)
    {
        List<object> output=[];
        foreach(var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)) {
            try {

                if(file.EndsWith(".hkx",StringComparison.OrdinalIgnoreCase)) {var a=AnimationClip.Load(File.ReadAllBytes(file));output.Add(new {file,a.Duration,a.Frames,a.Tracks,a.Events});}
                if(file.EndsWith(".esp",StringComparison.OrdinalIgnoreCase))foreach(var r in Records.Parse(File.ReadAllBytes(file),Path.GetFileName(file)))output.Add(new {file,r.Kind,r.EditorId,r.Key,fields=r.Fields.Select(f=>new{f.Tag,text=f.Bytes.All(b=>b==0||b>=32&&b<127)?f.Text:null,hex=Convert.ToHexString(f.Bytes)})});
                if(file.EndsWith(".nif",StringComparison.OrdinalIgnoreCase)) {var n=new Nifcontent_Class_Manolo();n.Load_Manolo(file);output.Add(new{file,blocks=n.Blocks.Select(b=>new{index=n.Blocks.IndexOf(b),controller=b is NiflySharp.Blocks.NiTimeController tc?new{flags=Convert.ToUInt16(NifAnimation.Get(tc.Flags,"Value")),interpolator=n.GetBlock(NifAnimation.Get(tc,"Interpolator") as NiflySharp.INiRef)?.GetType().Name}:null,strings=b is NiflySharp.Blocks.NiStringsExtraData strings?strings.Data.Select(s=>s.Content).ToArray():null,children=b is NiflySharp.Blocks.NiNode node?node.Children.Indices.ToArray():null,type=b.GetType().Name,maxVertices=b is NiflySharp.Blocks.NiGeometryData gd?typeof(NiflySharp.Blocks.NiGeometryData).GetField("_bSMaxVertices",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)?.GetValue(gd)?.ToString():null,properties=b.GetType().GetProperties().Where(p=>p.GetIndexParameters().Length==0 && p.Name is not "References" and not "Pointers" and not "ReferenceArrays" and not "StringRefs").Select(p=> {object? v=null;try{v=p.GetValue(b);}catch{}return new{name=p.Name,value=Describe(v)};})})});}
            }catch(Exception ex) {output.Add(new{file,error=ex.ToString()});}
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"inspection.json"),JsonSerializer.Serialize(output,new JsonSerializerOptions {WriteIndented=true}));
    }
}
