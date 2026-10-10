namespace SSEPreview;
internal static class ImplicitPlugins
{
    internal static IEnumerable<string> Read(IEnumerable<string> roots)
    {
        // Only the selected base Data directory identifies a game installation.
        // Overlay mod folders must not introduce another installation's forced plugins.
        string? root=roots.FirstOrDefault();
        if(root==null)return [];
        var data=new DirectoryInfo(root);
        if(!data.Name.Equals("Data",StringComparison.OrdinalIgnoreCase)||data.Parent==null)return [];
        string path=Path.Combine(data.Parent.FullName,"Skyrim.ccc");
        if(!File.Exists(path))return [];
        return File.ReadLines(path).Select(line=>line.Trim()).Where(line=>
            line.Length>0&&!line.StartsWith('#')&&!line.StartsWith(';')&&
            !line.Contains('/')&&!line.Contains('\\')&&!line.Contains(':')&&
            Path.GetExtension(line).ToLowerInvariant() is ".esm" or ".esp" or ".esl")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
