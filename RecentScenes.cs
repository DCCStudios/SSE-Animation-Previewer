using System.Text.Json;
namespace SSEPreview;
internal sealed class RecentScenes
{
    readonly string path;readonly List<string> entries=[];
    public IReadOnlyList<string> Entries=>entries;
    public RecentScenes(string path){
        this.path=path;
        try{if(File.Exists(path))foreach(string value in JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))??[]){if(!string.IsNullOrWhiteSpace(value)){var full=Path.GetFullPath(value);if(!entries.Contains(full,StringComparer.OrdinalIgnoreCase))entries.Add(full);}}}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException){ }
        if(entries.Count>12)entries.RemoveRange(12,entries.Count-12);
    }
    public void Remember(string scene){
        scene=Path.GetFullPath(scene);entries.RemoveAll(p=>p.Equals(scene,StringComparison.OrdinalIgnoreCase));entries.Insert(0,scene);
        if(entries.Count>12)entries.RemoveRange(12,entries.Count-12);
        SceneFile.Save(path,JsonSerializer.Serialize(entries,new JsonSerializerOptions{WriteIndented=true}));
    }
}
