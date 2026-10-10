using System.Text.Json;
using System.Numerics;
using System.Diagnostics;
using FO4_Base_Library;
using BSA_BA2_Library_DLL.BethesdaArchive.Core;
namespace SSEPreview;
internal static class SelfTest
{
    // Local data the tests read. SSE_TEST_MODS overrides the mod folder root; the vanilla assets sit in test-data beside the source.
    // The repository root: the nearest folder above the exe that holds the solution, looking no further than six levels,
    // or four levels up (the build output layout) when there is none.
    public static readonly string RepoRoot=FindRepoRoot();
    static string FindRepoRoot()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        for(int level=0;level<6&&directory!=null;level++,directory=directory.Parent)if(File.Exists(Path.Combine(directory.FullName,"SSEAnimationPreviewer.sln")))return directory.FullName;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,@"..\..\..\.."));
    }
    // The folder of mods the wider checks read: SSE_TEST_MODS, or the first line of test-mods.txt at the repository
    // root (a local file git ignores). Empty when neither is set, and those checks then report that they were skipped.
    public static readonly string Mods=FindMods();
    static string FindMods()
    {
        if(Environment.GetEnvironmentVariable("SSE_TEST_MODS") is {Length:>0} mods)return mods;
        try{string file=Path.Combine(RepoRoot,"test-mods.txt");if(File.Exists(file)&&File.ReadLines(file).Select(l=>l.Trim()).FirstOrDefault(l=>l.Length>0) is {} line)return line;}catch(IOException){}catch(UnauthorizedAccessException){}
        return "";
    }
    public static readonly string TestData=Path.Combine(RepoRoot,"test-data");
    public static readonly string GameData=Path.GetFullPath(Path.Combine(Mods,@"..\Game Root\Data"));
    public static bool HasTestData=>File.Exists(Path.Combine(TestData,"Skyrim.esm"));
    public static bool HasMods=>Directory.Exists(Mods)&&Directory.EnumerateDirectories(Mods).Any();
    public static bool HasMod(string name)=>Directory.Exists(Path.Combine(Mods,name));
    // The running suite sets this so shared sub-tests can log a "SKIP " line when their data is missing.
    public static Action<string> Skip=_=>{};
    public static string Missing(string file)=>Path.GetFileName(file)+" is missing under "+Mods;
    public static readonly string[] TestMods=["Cloud FF7 Blade Beam MCO","DAR Moveset and Buster Sword - Cloud Strife FF7 Remake","Anchor MCO Animations v2 - Cloud Fenrir","FF7 Cloud Animations","Simple MCO Weaponart Casting - Vanilla Spells"];
    public static void Run()
    {
        List<string> log=[];void Check(bool ok,string msg){log.Add((ok?"PASS ":"FAIL ")+msg);if(!ok)throw new InvalidDataException(msg);}
        Check(Math.Abs(PlaybackClock.Normalize(2.6,2,true)-.6)<1e-6,"Loop overshoot preserves time");
        var clock=new PlaybackClock{Duration=10};clock.Seek(1.25);clock.SourceFps=60;Check(clock.Time==1.25,"FPS change does not alter playback time");
        clock.Step(1);Check(Math.Abs(clock.Time-1.25-1d/60)<1e-6,"Frame step uses selected sample rate");
        var amr=new AnimationClip{Translation=[(1,new Vector3(10,20,30)),(2,new Vector3(20,40,60))],Rotation=[(2,MathF.PI/2)]};
        Check(Vector3.Distance(amr.Motion(.5).Position,new(5,10,15))<1e-5,"AMR cumulative annotation interpolation");
        foreach(var mod in TestMods) {
            int good=0,failed=0,nifs=0;
            foreach(var file in Directory.EnumerateFiles(Path.Combine(Mods,mod),"*",SearchOption.AllDirectories)) {
                try {
                    if(file.EndsWith(".hkx",StringComparison.OrdinalIgnoreCase)&&file.Contains(@"\animations\",StringComparison.OrdinalIgnoreCase)){var a=AnimationClip.Load(File.ReadAllBytes(file));Check(a.Duration>0 && a.Tracks>0,Path.GetRelativePath(Mods,file));good++;}
                    if(file.EndsWith(".nif",StringComparison.OrdinalIgnoreCase)){var n=new Nifcontent_Class_Manolo();n.Load_Manolo(file);nifs++;}
                }catch(Exception ex){failed++;log.Add("UNSUPPORTED "+file+" "+ex.Message);}
            }
            log.Add($"SUMMARY {mod}: {good} HKX decoded, {nifs} NIF loaded, {failed} failures");
        }
        var dds=File.ReadAllBytes(Path.Combine(Mods,@"Cloud FF7 Blade Beam MCO\textures\CLoud\BeamAura.dds"));
        var tex=DirectXTexWrapperCLI.Loader.LoadTextures([dds],true,true);
        Check(tex.Count==1&&tex[0].Loaded,"Real Cloud DDS decoding");
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"self-test.txt"),log);
    }
    public static void Fixture()
    {
        var root=TestData;
        Directory.CreateDirectory(root);
        var game=GameData;
        File.Copy(Path.Combine(game,"Skyrim.esm"),Path.Combine(root,"Skyrim.esm"),true);
        string[] meshPrefixes=[@"meshes\actors\character\character assets\",@"meshes\actors\character\character assets female\",@"meshes\armor\iron\",@"meshes\weapons\iron\",@"meshes\magic\"];
        string[] texturePrefixes=[@"textures\actors\character\male\",@"textures\actors\character\female\",@"textures\actors\character\hair\",@"textures\actors\character\eyes\",@"textures\armor\iron\",@"textures\weapons\iron\",@"textures\effects\",@"textures\magic\",@"textures\cubemaps\"];
        foreach(var archive in Directory.EnumerateFiles(game,"Skyrim - *.bsa")) {
            if(!Path.GetFileName(archive).Contains("Meshes")&&!Path.GetFileName(archive).Contains("Textures"))continue;
            using var stream=File.OpenRead(archive);using var reader=new BethesdaReader(stream);
            foreach(var entry in reader.EntriesFiles) {
                var path=Assets.Normalize(entry.FullPath);
                if(!meshPrefixes.Concat(texturePrefixes).Any(prefix=>path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)))continue;
                var dest=Path.Combine(root,path);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.WriteAllBytes(dest,reader.ExtractToMemory(entry.Index));
            }
        }
        var skel=Path.Combine(Mods,@"XP32 Maximum Skeleton Special Extended - XPMSSE\meshes\actors\character\character assets");
        foreach(var file in Directory.EnumerateFiles(skel,"skeleton.*")) {var dest=Path.Combine(root,@"meshes\actors\character\character assets",Path.GetFileName(file));Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Copy(file,dest,true);}
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"fixture.txt"),root);
    }
}
