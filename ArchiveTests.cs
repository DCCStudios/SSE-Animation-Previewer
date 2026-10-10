using System.Text;
using FO4_Base_Library;
using BSA_BA2_Library_DLL.BethesdaArchive.Core;

namespace SSEPreview;
public static class ArchiveTests
{
    public static async Task Run()
    {
        var log=new List<string>();
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"archive-tests.txt"),log);if(!ok)throw new InvalidDataException(message);}
        string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,@"..\..\..\..\verification\archive-fixtures"));
        string Folder(string name){string p=Path.Combine(root,name);Directory.CreateDirectory(p);return p;}
        const string shared=@"meshes\archive-test\shared.nif",loose=@"meshes\archive-test\loose.nif",old=@"meshes\archive-test\old.nif";
        void Archive(string folder,string name,params (string Path,byte[] Bytes)[] files){
            string destination=Path.Combine(folder,name),temporary=destination+".new";
            using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.ReadWrite))
            BsaWriter.Write(stream,files.Select(f=>new VirtualEntry{Directory=Path.GetDirectoryName(f.Path)!,FileName=Path.GetFileName(f.Path),Data=f.Bytes,PreferCompress=true}),new BsaWriter.Options{GlobalCompressed=true});
            File.Move(temporary,destination,true);
        }
        byte[] Bytes(string text)=>Encoding.UTF8.GetBytes(text);
        string baseRoot=Folder("base"),first=Folder("first"),second=Folder("second"),third=Folder("third"),fourth=Folder("fourth"),bad=Folder("bad");
        Archive(baseRoot,"Fallback.bsa",(shared,Bytes("base")));
        string loosePath=Path.Combine(baseRoot,loose);Directory.CreateDirectory(Path.GetDirectoryName(loosePath)!);File.WriteAllText(loosePath,"loose");
        Archive(first,"Effects.bsa",(shared,Bytes("first")),(old,Bytes("old")),(loose,Bytes("archive")));
        Archive(second,"Effects.bsa",(shared,Bytes("second")),(@"meshes\archive-test\compressed.nif",Bytes(new string('X',16384))));
        Archive(third,"Other.bsa",(shared,Bytes("third")));
        Archive(fourth,"Other.bsa",(@"meshes\archive-test\unrelated.nif",Bytes("unrelated")));
        File.WriteAllText(Path.Combine(bad,"Effects.bsa"),"invalid archive");
        var assets=new Assets();var progress=new Progress<string>();
        await assets.Mount(baseRoot,false,progress);
        await assets.Mount(first,true,progress);
        Check(assets.Text(shared)=="first","Mod archive overrides a base archive asset");
        Check(assets.Text(loose)=="loose","Base loose file wins over an added mod archive");
        Check(Encoding.UTF8.GetString(FilesDictionary_class.GetBytes(shared))=="first","Renderer asset bridge reads the external archive using its full physical path");
        await assets.Mount(second,true,progress);
        Check(assets.Text(shared)=="second"&&assets.Source(shared)==Path.Combine(second,"Effects.bsa"),"Same-named archive in a later mod resolves to the correct physical source");
        Check(!assets.Exists(old),"Replacing a virtual archive removes entries absent from its replacement");
        Check(assets.Read(@"meshes\archive-test\compressed.nif").Length==16384,"Compressed SSE BSA payload extracts through the bundled reader");
        await assets.Mount(third,true,progress);Check(assets.Text(shared)=="third","Later distinct mod archive wins an asset conflict");
        await assets.Mount(fourth,true,progress);Check(assets.Text(shared)=="second","Replacing a later archive restores the earlier winning asset");
        await assets.Mount(bad,true,progress);
        Check(assets.Text(shared)=="second"&&assets.Diagnostics.Any(d=>d.Contains("Could not mount archive")),"Invalid replacement archive reports failure and preserves the previous valid data");
        var staleLocation=FilesDictionary_class.Dictionary[shared];
        FilesDictionary_class.ReleaseExternalArchiveReaders(Path.Combine(second,"Effects.bsa"));
        Archive(second,"Effects.bsa",(shared,Bytes("updated")));
        await assets.Mount(second,true,progress);
        Check(assets.Text(shared)=="updated"&&Encoding.UTF8.GetString(FilesDictionary_class.GetBytes(shared))=="updated"&&Encoding.UTF8.GetString(staleLocation.GetBytes())=="updated",
            "Reindexing after releasing archive handles refreshes reads and rejects stale entry indices");
        string empty=Folder("empty");await assets.Mount(empty,false,progress);
        Check(!assets.Exists(shared),"Opening a new base clears previous archive overlays");
        await assets.Mount(first,true,progress);Check(assets.Text(shared)=="first","A previously indexed archive can mount after a full asset reset");

        string ordered=Folder("ordered"),profile=Folder("profile");
        const string archiveClip=@"meshes\actors\character\animations\ordered.hkx";
        Archive(ordered,"Foo.bsa",(shared,Bytes("foo")),(archiveClip,Bytes("foo-animation")));
        Archive(ordered,"Bar.bsa",(shared,Bytes("bar")),(archiveClip,Bytes("bar-animation")));
        Archive(ordered,"Unlisted.bsa",(@"meshes\archive-test\unlisted.nif",Bytes("unlisted")));
        foreach(string name in new[]{"Foo.esp","Bar.esp"}){var header=new byte[24];Bytes("TES4").CopyTo(header,0);File.WriteAllBytes(Path.Combine(ordered,name),header);}
        await assets.Mount(ordered,true,progress);
        Check(assets.Text(shared)=="foo","Standalone archive inspection retains folder and filename precedence before selecting a profile");
        string plugins=Path.Combine(profile,"plugins.txt"),ini=Path.Combine(profile,"Skyrim.ini");
        File.WriteAllText(ini,"[Archive]\nsResourceArchiveList=Unlisted.bsa\nsResourceArchiveList2=\n");
        File.WriteAllText(plugins,"*Foo.esp\n*Bar.esp\n");var records=new Records();records.Load(assets,plugins);
        Check(assets.Text(shared)=="bar"&&assets.Text(archiveClip)=="bar-animation","Active plugin order overrides archive filename/add order for models and animations");
        Check(assets.Exists(@"meshes\archive-test\unlisted.nif"),"Explicit profile INI archives remain active without matching plugins");
        File.WriteAllText(plugins,"*Bar.esp\n*Foo.esp\n");records.Load(assets,plugins);
        Check(assets.Text(shared)=="foo"&&Encoding.UTF8.GetString(FilesDictionary_class.GetBytes(shared))=="foo","Reversing active plugin order updates both public reads and the renderer bridge");
        File.WriteAllText(plugins,"*Bar.esp\nFoo.esp\n");records.Load(assets,plugins);
        Check(assets.Text(shared)=="bar","An inactive plugin archive cannot override an active plugin archive");
        File.WriteAllText(plugins,"Foo.esp\nBar.esp\n");records.Load(assets,plugins);
        Check(!assets.Exists(shared)&&!assets.Clips.Any(c=>c.Path==archiveClip),"Inactive archive assets disappear from the mounted paths and animation catalog");
        File.WriteAllText(plugins,"*Foo.esp\n");records.Load(assets,plugins);
        Check(assets.Text(shared)=="foo"&&assets.Clips.Any(c=>c.Path==archiveClip),"Reactivating an indexed archive restores its assets and animation catalog without stale winners");
        string overrideRoot=Folder("ordered-loose");string overrideFile=Path.Combine(overrideRoot,shared);Directory.CreateDirectory(Path.GetDirectoryName(overrideFile)!);File.WriteAllText(overrideFile,"loose-order");await assets.Mount(overrideRoot,true,progress);records.Load(assets,plugins);
        Check(assets.Text(shared)=="loose-order","Loose assets still override archives after applying an explicit profile");
        var priority=ArchiveLoadOrder.Build(new[]{"Foo.esp","Bar.esp"},plugins,out bool usedIni);
        Check(usedIni&&priority["Unlisted.bsa"]<priority["Foo.bsa"]&&priority["Foo - Textures.bsa"]<priority["Bar.bsa"],"INI archives precede standard plugin and texture archives in the active order");
        await assets.Mount(empty,false,progress);
        File.WriteAllText(ini,"[Archive]\nsResourceArchiveList2=Custom.bsa\n");
        var partial=ArchiveLoadOrder.Build(new[]{"Foo.esp"},plugins,out bool complete);
        Check(!complete&&partial.ContainsKey("Skyrim - Meshes0.bsa")&&partial.ContainsKey("Custom.bsa")&&!partial.ContainsKey("Skyrim - Textures0.bsa"),"Partial profile INI inherits only the absent archive-list key, preserving explicit overrides");
        string ccGame=Folder("cc-game"),ccData=Folder("cc-game/Data");
        File.WriteAllText(Path.Combine(ccGame,"Skyrim.ccc"),"# comment\n; comment\n Foo.esp \nfoo.ESP\nMissing.esl\n../Other.esp\n");
        Check(ImplicitPlugins.Read(new[]{ccData,ordered}).SequenceEqual(new[]{"Foo.esp","Missing.esl"}),"Creation Club list ignores comments, duplicate names and paths while retaining missing plugin diagnostics");
        Check(!ImplicitPlugins.Read(new[]{empty,ccData}).Any(),"An overlay Data directory cannot select a different game's Creation Club list");
        foreach(string name in new[]{"Foo.esp","Bar.esp"})File.Copy(Path.Combine(ordered,name),Path.Combine(ccData,name),true);
        var artRecord=new byte[24];Bytes("ARTO").CopyTo(artRecord,0);BitConverter.GetBytes((uint)0x800).CopyTo(artRecord,12);
        using(var plugin=new FileStream(Path.Combine(ccData,"Foo.esp"),FileMode.Append))plugin.Write(artRecord);
        Archive(ccData,"Foo.bsa",(shared,Bytes("cc")),(archiveClip,Bytes("cc-animation")));
        Archive(ccData,"Bar.bsa",(shared,Bytes("regular")));
        await assets.Mount(ccData,false,progress);
        // Explicitly index fixtures independently of the library's global game settings.
        await assets.Mount(ccData,true,progress);
        File.WriteAllText(plugins,"Foo.esp\nBar.esp\n");records.Load(assets,plugins);
        Check(assets.Text(shared)=="cc"&&assets.Clips.Any(c=>c.Path==archiveClip),"Unstarred Creation Club plugin keeps its model and animation archive active");
        Check(records.Resolve("Foo.esp",0x800)?.Kind=="ARTO","Unstarred Creation Club visual-effect records load alongside their archives");
        Check(records.Diagnostics.Contains("Active plugin is missing: Missing.esl"),"Missing forced Creation Club plugin is reported");
        File.WriteAllText(plugins,"*Bar.esp\n*Foo.esp\n");records.Load(assets,plugins);
        Check(assets.Text(shared)=="regular","Creation Club archives precede regular plugins even when duplicated later in plugins.txt");
        File.WriteAllText(Path.Combine(ccGame,"Skyrim.ccc"),"# empty\n");records.Load(assets,plugins);
        Check(assets.Text(shared)=="cc","Reloading a changed Creation Club list restores ordinary explicit plugin ordering");
        await assets.Mount(empty,false,progress);
        string archiveRender=Folder("render-mod");
        string fixtures=Path.GetFullPath(Path.Combine(root,@"..\..\particle-fixtures"));
        var fixture=ParticleFixture.Create(fixtures);
        Archive(archiveRender,"PreviewerEffects.bsa",(@"meshes\previewer\fountain.nif",File.ReadAllBytes(fixture)),
            (@"textures\previewer\particle.dds",File.ReadAllBytes(Path.Combine(fixtures,@"textures\previewer\particle.dds"))),
            (@"meshes\actors\character\animations\archive-test.hkx",new byte[]{0}));
        await assets.Mount(archiveRender,true,progress);
        Check(assets.Clips.Any(c=>c.Name=="archive-test.hkx"),"Archived animations appear in the animation library");
        Check(assets.Source(@"textures\previewer\particle.dds").EndsWith("PreviewerEffects.bsa")&&assets.Read(@"meshes\previewer\fountain.nif").Length>100,
            "Render fixture mesh and texture are both supplied exclusively by the mod BSA");
        string orderRender=Folder("archive-order-render");string cards=BillboardTests.Fixture(fixtures);
        var white=File.ReadAllBytes(Path.Combine(fixtures,@"textures\previewer\particle.dds"));var black=(byte[])white.Clone();
        for(int i=128;i<black.Length;i+=4){black[i]=0;black[i+1]=0;black[i+2]=0;}
        Archive(orderRender,"White.bsa",(@"meshes\previewer\billboards.nif",File.ReadAllBytes(cards)),(@"textures\previewer\particle.dds",white));
        Archive(orderRender,"Black.bsa",(@"meshes\previewer\billboards.nif",File.ReadAllBytes(cards)),(@"textures\previewer\particle.dds",black));
        foreach(string name in new[]{"White.esp","Black.esp"}){var header=new byte[24];Bytes("TES4").CopyTo(header,0);File.WriteAllBytes(Path.Combine(orderRender,name),header);}
        File.WriteAllText(Path.Combine(orderRender,"Skyrim.ini"),"[Archive]\nsResourceArchiveList=\nsResourceArchiveList2=\n");
        File.WriteAllText(Path.Combine(orderRender,"white.txt"),"*Black.esp\n*White.esp\n");File.WriteAllText(Path.Combine(orderRender,"black.txt"),"*White.esp\n*Black.esp\n");
        log.Add("Render base: "+empty);log.Add("Render overlay: "+archiveRender);File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"archive-tests.txt"),log);
    }
}