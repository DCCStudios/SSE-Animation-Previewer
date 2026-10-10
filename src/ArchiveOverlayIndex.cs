using FO4_Base_Library;
using BSA_BA2_Library_DLL.BethesdaArchive.Core;

namespace SSEPreview;

// Archive names are virtual Data files: a later same-named archive replaces the
// entire earlier archive. Asset conflicts still give every loose file priority.
public sealed class ArchiveOverlayIndex
{
    readonly Dictionary<string,List<FilesDictionary_class.File_Location>> candidates=new(StringComparer.OrdinalIgnoreCase);
    public void Clear()=>candidates.Clear();
    static bool FromArchive(FilesDictionary_class.File_Location entry,string name)=>
        entry.BA2File.Length>0&&Path.GetFileName(entry.BA2File).Equals(name,StringComparison.OrdinalIgnoreCase);
    List<FilesDictionary_class.File_Location> Candidates(string key)
    {
        if(candidates.TryGetValue(key,out var list))return list;
        list=FilesDictionary_class.GetOverriddenEntries(key).ToList();
        if(FilesDictionary_class.Dictionary.TryGetValue(key,out var current))list.Add(current);
        return candidates[key]=list;
    }
    public void ApplyLoadOrder(Assets assets,IEnumerable<string> plugins,string profileFile)
    {
        var priority=ArchiveLoadOrder.Build(plugins,profileFile,out bool usedIni);
        assets.Diagnostics.RemoveAll(d=>d.StartsWith("Archive load order:",StringComparison.Ordinal));
        if(!usedIni)assets.Diagnostics.Add("Archive load order: one or both archive lists are absent from the adjacent Skyrim.ini; missing keys use shipped SE defaults, followed by active plugin archives.");
        foreach(string key in FilesDictionary_class.Dictionary.Keys.Concat(candidates.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()){
            var list=Candidates(key);
            if(assets.External.TryGetValue(key,out var loose)){FilesDictionary_class.Dictionary[key]=new(){FullPath=loose,BA2File="",Index=-1};continue;}
            var winner=list.Where(e=>e.IsLosseFile||priority.ContainsKey(Path.GetFileName(e.BA2File)))
                .OrderBy(e=>e.IsLosseFile?int.MaxValue:priority[Path.GetFileName(e.BA2File)]).LastOrDefault();
            if(winner==null)FilesDictionary_class.Dictionary.TryRemove(key,out _);else FilesDictionary_class.Dictionary[key]=winner;
        }
    }
    public int Mount(string archive,Assets assets)
    {
        // Validate and fully stage the index before changing the previous mount.
        List<KeyValuePair<string,int>> index=[];
        using(var stream=new FileStream(archive,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var reader=new BethesdaReader(stream))
            foreach(var entry in reader.EntriesFiles){
                string path=Assets.Normalize(entry.FullPath);
                if(Path.IsPathRooted(path)||path.Split('\\').Any(p=>p=="..")||!Assets.Supported(path))continue;
                index.Add(new(path,entry.Index));
            }
        string name=Path.GetFileName(archive);
        var affected=FilesDictionary_class.Dictionary.Where(p=>FromArchive(p.Value,name)).Select(p=>p.Key)
            .Concat(candidates.Where(p=>p.Value.Any(e=>FromArchive(e,name))).Select(p=>p.Key))
            .Concat(index.Select(p=>p.Key)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach(var key in affected)Candidates(key);
        var locations=FilesDictionary_class.BuildExternalArchiveLocations(archive,index,int.MaxValue-1);
        foreach(var key in affected){
            var list=candidates[key];list.RemoveAll(e=>FromArchive(e,name));
            if(locations.TryGetValue(key,out var entry))list.Add(entry);
            if(assets.External.TryGetValue(key,out var loose)){
                FilesDictionary_class.Dictionary[key]=new(){FullPath=loose,BA2File="",Index=-1};continue;
            }
            var winner=list.OrderBy(e=>e.IsLosseFile?int.MaxValue:e.SourceOrder).LastOrDefault();
            if(winner==null)FilesDictionary_class.Dictionary.TryRemove(key,out _);
            else FilesDictionary_class.Dictionary[key]=winner;
        }
        return locations.Count;
    }
}