namespace SSEPreview;
internal static class ArchiveLoadOrder
{
    internal static Dictionary<string,int> Build(IEnumerable<string> plugins,string profileFile,out bool usedIni)
    {
        var order=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);int next=0;
        void Add(string name){name=Path.GetFileName(name.Trim());if(name.EndsWith(".bsa",StringComparison.OrdinalIgnoreCase)&&!order.ContainsKey(name))order[name]=next++;}
        string ini=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(profileFile))!,"Skyrim.ini");
        var lists=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);string section="";
        if(File.Exists(ini))foreach(string raw in File.ReadLines(ini)){
            string line=raw.Split(';',2)[0].Trim();if(line.StartsWith('[')){section=line.Trim('[',']').Trim();continue;}
            int equal=line.IndexOf('=');if(!section.Equals("Archive",StringComparison.OrdinalIgnoreCase)||equal<0)continue;
            string key=line[..equal].Trim();if(key.Equals("sResourceArchiveList",StringComparison.OrdinalIgnoreCase)||key.Equals("sResourceArchiveList2",StringComparison.OrdinalIgnoreCase))lists[key]=line[(equal+1)..].Trim();
        }
        usedIni=lists.Count==2;
        // Missing keys inherit the shipped SE defaults; explicitly empty keys stay empty.
        var defaults=new[]{
            "Skyrim - Misc.bsa, Skyrim - Shaders.bsa, Skyrim - Interface.bsa, Skyrim - Animations.bsa, Skyrim - Meshes0.bsa, Skyrim - Meshes1.bsa, Skyrim - Sounds.bsa",
            "Skyrim - Voices_en0.bsa, Skyrim - Textures0.bsa, Skyrim - Textures1.bsa, Skyrim - Textures2.bsa, Skyrim - Textures3.bsa, Skyrim - Textures4.bsa, Skyrim - Textures5.bsa, Skyrim - Textures6.bsa, Skyrim - Textures7.bsa, Skyrim - Textures8.bsa, Skyrim - Patch.bsa"};
        string[] keys=["sResourceArchiveList","sResourceArchiveList2"];
        for(int i=0;i<keys.Length;i++)foreach(string name in (lists.TryGetValue(keys[i],out var value)?value:defaults[i]).Split(',',StringSplitOptions.RemoveEmptyEntries))Add(name);
        foreach(string plugin in plugins){string name=Path.GetFileNameWithoutExtension(plugin);Add(name+".bsa");Add(name+" - Textures.bsa");}
        return order;
    }
}
