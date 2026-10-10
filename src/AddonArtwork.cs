namespace SSEPreview;
internal static class AddonArtwork
{
    internal static GameRecord? Resolve(Records? records,uint index,out string? issue)
    {
        issue=null;
        var matches=records?.Of("ADDN").Where(r=>r.First("DATA") is {Bytes.Length:>=4} data&&data.UInt()==index).ToArray()??[];
        if(matches.Length==1)return matches[0];
        issue=matches.Length==0?$"No loaded ADDN record for node index {index}.":$"Ambiguous ADDN node index {index}: "+string.Join(", ",matches.Select(r=>r.Key));
        return null;
    }
    internal static string Model(GameRecord record)
    {
        string path=Assets.GamePath(record.First("MODL")?.Text??"");
        return path.Length==0||path.StartsWith(@"meshes\",StringComparison.OrdinalIgnoreCase)?path:@"meshes\"+path;
    }
}
