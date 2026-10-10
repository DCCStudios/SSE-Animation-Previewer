using System.Text;
using System.Text.Json;
using FO4_Base_Library;

namespace SSEPreview;

public sealed record ClipEntry(string Path, string Framework, string Mod, string Group, string ConfigPath, string Config)
{
    public string SourceName {get;init;}="";
    public string VariantGroup {get;init;}="";
    public string VariantDetails {get;init;}="";
    public bool Disabled {get;init;}
    public int? Priority {get;init;}
    // The animation file this clip stands in for, from "meshes" down: an OAR or DAR clip's path with the replacer
    // folders taken out, a plain clip's own path. Clips with the same key compete for one animation.
    public string ReplacementKey {get;init;}="";
    public string Name => System.IO.Path.GetFileName(Path);
    public string DisplayName => Name+(Disabled?"  [disabled in OAR]":"")+(VariantDetails.Length>0?"  · "+VariantDetails:"");
    public string LibraryMod => Framework=="OAR"&&SourceName.Length>0&&SourceName!=Mod?Mod+" · "+SourceName:Mod;
    public override string ToString() => Name;
}

public sealed class Assets
{
    readonly ArchiveOverlayIndex archiveOverlays=new();
    public List<string> Roots { get; } = [];
    public long Revision {get;private set;}
    public List<ClipEntry> Clips { get; } = [];
    readonly HashSet<string> droppedPaths=new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> DroppedPaths=>droppedPaths;
    public List<string> Diagnostics { get; } = [];
    public Dictionary<string,string> External { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<string> Paths => FilesDictionary_class.Dictionary.Keys.Concat(External.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
    public static bool Supported(string path)=>Path.GetExtension(path).ToLowerInvariant() is
        ".hkx" or ".nif" or ".dds" or ".bgsm" or ".bgem" or ".tri" or ".xml" or ".json" or ".txt" or ".ini" or ".toml" or ".yaml" or ".pex" or ".esp" or ".esm" or ".esl" or ".wav" or ".xwm" or ".fuz";
    // Sound files for annotation sounds; the engine's dictionary only indexes the extensions it is told about.
    static Assets()=>FilesDictionary_class.RegisterExtensions(".wav",".xwm",".fuz");
    public static string Normalize(string path) => path.Replace('/', '\\').TrimStart('\\');
    public static string GamePath(string path)
    {
        path=Normalize(path);
        foreach(string root in new[]{"textures","meshes"}){
            int at=path.IndexOf("\\"+root+"\\",StringComparison.OrdinalIgnoreCase);
            if(at>=0)return path[(at+1)..];
        }
        return path;
    }
    public bool Exists(string path) => File.Exists(path) || External.ContainsKey(Normalize(path)) || FilesDictionary_class.Dictionary.ContainsKey(Normalize(path));
    public byte[] Read(string path)
    {
        if (System.IO.Path.IsPathRooted(path)) return File.ReadAllBytes(path);
        path = Normalize(path);
        if (External.TryGetValue(path, out var actual)) return File.ReadAllBytes(actual);
        if (FilesDictionary_class.Dictionary.TryGetValue(path, out var loc)) return loc.GetBytes();
        throw new FileNotFoundException("Asset is not present in the mounted files", path);
    }
    public string Text(string path) => Encoding.UTF8.GetString(Read(path)).TrimStart('\uFEFF');
    public string Source(string path)
    {
        if (External.TryGetValue(Normalize(path), out var ext)) return ext;
        if (FilesDictionary_class.Dictionary.TryGetValue(Normalize(path), out var loc)) return string.IsNullOrEmpty(loc.BA2File) ? loc.FullPath : loc.BA2File;
        return path;
    }
    public void Reset()
    {
        Roots.Clear();External.Clear();Diagnostics.Clear();Clips.Clear();droppedPaths.Clear();archiveOverlays.Clear();FilesDictionary_class.Dictionary.Clear();Revision++;
    }
    public async Task Mount(string folder, bool overlay, IProgress<string> progress,bool rebuildClips=true)
    {
        folder = System.IO.Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        if (!overlay)
        {
            Roots.Clear(); External.Clear(); Diagnostics.Clear();archiveOverlays.Clear();
            progress.Report("Indexing loose assets and active archives…");
            await FilesDictionary_class.Fill_DictionaryAsync(folder, null, false);
        }
        if(overlay){
            await Task.Run(()=>{
                foreach(var archive in Directory.EnumerateFiles(folder,"*",SearchOption.TopDirectoryOnly)
                    .Where(p=>Path.GetExtension(p).Equals(".bsa",StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p=>p,StringComparer.OrdinalIgnoreCase)){
                    try{progress.Report("Indexing "+Path.GetFileName(archive));archiveOverlays.Mount(archive,this);}
                    catch(Exception ex){Diagnostics.Add("Could not mount archive "+archive+": "+ex.Message);}
                }
            });
        }
        // The engine's extension filter omits replacer/effect configs. Index these too.
        await Task.Run(() => {
            var opts = new EnumerationOptions { RecurseSubdirectories=true, IgnoreInaccessible=true, AttributesToSkip=FileAttributes.ReparsePoint };
            foreach (var path in Directory.EnumerateFiles(folder, "*", opts))
            {
                var rel = Normalize(System.IO.Path.GetRelativePath(folder, path));
                var suffix = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (Supported(path))
                {
                    External[rel]=path;
                    FilesDictionary_class.Dictionary[rel]=new() { FullPath=path, BA2File="", Index=-1 };
                }
            }
        });
        Roots.Add(folder);Revision++;
        if(rebuildClips)await Task.Run(RebuildClips);
    }
    public void ApplyArchiveLoadOrder(IEnumerable<string> plugins,string profileFile)
    {
        archiveOverlays.ApplyLoadOrder(this,plugins,profileFile);Revision++;RebuildClips();
    }
    public void RebuildClips()
    {
        Clips.Clear();
        var catalog=new ClipCatalog(this);
        Clips.AddRange(catalog.Build());
        Diagnostics.RemoveAll(d=>d.StartsWith("Dropped animation unavailable: ",StringComparison.Ordinal));
        foreach(var file in droppedPaths) {
            if(!File.Exists(file)){Diagnostics.Add("Dropped animation unavailable: "+file);continue;}
            if(!Clips.Any(c=>c.Path.Equals(file,StringComparison.OrdinalIgnoreCase)))Clips.Add(DescribeDropped(file));
        }
        Clips.Sort((a,b)=>StringComparer.OrdinalIgnoreCase.Compare(a.Path,b.Path));
    }
    public ClipEntry Describe(string path)=>new ClipCatalog(this).Describe(path);
    public ClipEntry Drop(string file)
    {
        file=System.IO.Path.GetFullPath(file);
        if(!file.EndsWith(".hkx",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Drop HKX animation files or a folder containing them.");
        if(!File.Exists(file))throw new FileNotFoundException("Dropped animation does not exist.",file);
        var clip=DescribeDropped(file);
        droppedPaths.Add(file);
        if(!Clips.Any(c=>c.Path.Equals(file,StringComparison.OrdinalIgnoreCase))) Clips.Add(clip);
        return clip;
    }
    public void RestoreDroppedPaths(IEnumerable<string> paths)
    {
        foreach(var path in paths) {
            try {
                var file=Path.GetFullPath(path);
                if(file.EndsWith(".hkx",StringComparison.OrdinalIgnoreCase))droppedPaths.Add(file);
            }catch(ArgumentException){ }catch(NotSupportedException){ }
        }
        RebuildClips();
    }
    ClipEntry DescribeDropped(string file)
    {
        var recognized=Describe(file);
        var clip=recognized.Framework is "DAR" or "OAR"?recognized:new ClipEntry(file,"Dropped","External animations",System.IO.Path.GetDirectoryName(file) ?? "","", "External animation; original file is read without moving or converting it.");

        return clip;
    }
}
