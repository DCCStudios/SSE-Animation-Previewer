using System.Text;
using System.Text.Json;

namespace SSEPreview;

// Metadata inspection only: actor conditions and OAR's runtime random/sequential selection are not evaluated.
public sealed class ClipCatalog(Assets assets)
{
    readonly Dictionary<string,(JsonElement? Json,string Body)> configs=new(StringComparer.OrdinalIgnoreCase);
    static string Join(IEnumerable<string> parts)=>string.Join("\\",parts);
    static string Text(JsonElement? json,string key)=>json is {} j&&j.ValueKind==JsonValueKind.Object&&j.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
    static JsonElement? Property(JsonElement? json,string key)=>json is {} j&&j.ValueKind==JsonValueKind.Object&&j.TryGetProperty(key,out var value)?value:null;
    static bool Flag(JsonElement? json,string key)=>Property(json,key)?.ValueKind==JsonValueKind.True;
    static int? Integer(JsonElement? json,string key)=>Property(json,key) is {ValueKind:JsonValueKind.Number} p&&p.TryGetInt32(out int value)?value:null;
    (JsonElement? Json,string Body) Read(string path)
    {
        if(configs.TryGetValue(path,out var found))return found;
        if(!assets.Exists(path))return configs[path]=(null,"");
        string body=assets.Text(path);
        try{
            using var doc=JsonDocument.Parse(body);
            return configs[path]=(doc.RootElement.Clone(),JsonSerializer.Serialize(doc.RootElement,new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
        }catch(JsonException ex){
            string warning=$"Config {path}: {ex.Message}";if(!assets.Diagnostics.Contains(warning))assets.Diagnostics.Add(warning);
            return configs[path]=(null,body+"\n\nPARSE ERROR: "+ex.Message);
        }
    }
    string SourceLabel(string path)
    {
        string source=assets.Source(path);
        foreach(string root in assets.Roots.OrderByDescending(r=>r.Length))
            if(source.StartsWith(root.TrimEnd('\\','/')+"\\",StringComparison.OrdinalIgnoreCase))
                return System.IO.Path.GetFileName(root.TrimEnd('\\','/'));
        if(System.IO.Path.IsPathRooted(source)&&source.EndsWith(".bsa",StringComparison.OrdinalIgnoreCase))
            return System.IO.Path.GetFileName(source);
        if(System.IO.Path.IsPathRooted(source)){
            int meshes=source.IndexOf("\\meshes\\",StringComparison.OrdinalIgnoreCase);
            if(meshes>0)return System.IO.Path.GetFileName(source[..meshes]);
        }
        return "Mounted Data";
    }
    static string? SubRoot(string path)
    {
        var parts=Assets.Normalize(path).Split('\\');
        int index=Array.FindIndex(parts,p=>p.Equals("OpenAnimationReplacer",StringComparison.OrdinalIgnoreCase)||
            p.Equals("DynamicAnimationReplacer",StringComparison.OrdinalIgnoreCase));
        return index>=0&&parts.Length>index+3?Join(parts.Take(index+3)):null;
    }
    string? ResolveFolder(string owner,string value)
    {
        if(System.IO.Path.IsPathRooted(value)){
            assets.Diagnostics.Add(owner+": absolute OAR animation folder is outside virtual Data routing: "+value);return null;
        }
        var parts=new List<string>(owner.Split('\\').SkipLast(1));
        foreach(string part in value.Replace('/','\\').Split('\\',StringSplitOptions.RemoveEmptyEntries)){
            if(part==".")continue;
            if(part==".."){if(parts.Count==0){assets.Diagnostics.Add(owner+": animation folder escapes mounted Data");return null;}parts.RemoveAt(parts.Count-1);}
            else parts.Add(part);
        }
        return Join(parts).TrimEnd('\\');
    }
    public IEnumerable<ClipEntry> Build()
    {
        var paths=assets.Paths.ToArray();
        var animations=paths.Where(p=>p.EndsWith(".hkx",StringComparison.OrdinalIgnoreCase)&&p.Contains("animations\\",StringComparison.OrdinalIgnoreCase)).ToArray();
        var roots=paths.Where(p=>p.EndsWith("\\config.json",StringComparison.OrdinalIgnoreCase)||p.EndsWith("\\user.json",StringComparison.OrdinalIgnoreCase))
            .Select(SubRoot).Where(r=>r!=null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase);
        var routes=new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase);
        foreach(string root in roots){
            string author=root+"\\config.json",user=root+"\\user.json";
            bool legacy=root.Contains("\\DynamicAnimationReplacer\\",StringComparison.OrdinalIgnoreCase);
            if(!assets.Exists(legacy?root+"\\_conditions.txt":author))continue;
            var data=Read(assets.Exists(user)?user:author).Json;
            string folder=Text(data,"overrideAnimationsFolder");
            if(folder.Length>0)routes[root]=ResolveFolder(root,folder);
        }
        foreach(string path in animations){
            var root=SubRoot(path);
            if(root!=null&&routes.ContainsKey(root))continue; // OAR replaces this submod's animation directory.
            if(root!=null&&!assets.Exists(root+"\\config.json")&&routes.Values.Any(r=>r!=null&&path.StartsWith(r+"\\",StringComparison.OrdinalIgnoreCase)))continue;
            yield return Describe(path);
        }
        foreach(var route in routes){
            if(route.Value==null)continue;
            string prefix=route.Value+"\\";
            var borrowed=animations.Where(p=>p.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(borrowed.Length==0){assets.Diagnostics.Add(route.Key+": shared animation folder has no mounted HKX files: "+route.Value);continue;}
            foreach(string path in borrowed)
                yield return Describe(path,route.Key+"\\"+path[prefix.Length..],route.Value);
        }
    }
    public ClipEntry Describe(string path,string? configurationContext=null,string? sharedFolder=null)
    {
        var parts=Assets.Normalize(configurationContext??path).Split('\\');
        int oar=Array.FindIndex(parts,p=>p.Equals("OpenAnimationReplacer",StringComparison.OrdinalIgnoreCase));
        int dar=Array.FindIndex(parts,p=>p.Equals("DynamicAnimationReplacer",StringComparison.OrdinalIgnoreCase));
        int frameworkIndex=oar>=0?oar:dar;
        string source=SourceLabel(path),framework="Vanilla / loose",mod=source,group=System.IO.Path.GetDirectoryName(path)??"",configPath="",variantGroup="",variantDetails="";
        int? priority=null;bool disabled=false;string replacementKey=ClipOverrides.FromMeshes(parts);
        var body=new StringBuilder();
        JsonElement? data=null;
        void Section(string heading,string config){
            if(!assets.Exists(config))return;
            body.AppendLine(heading).AppendLine(config).AppendLine("Source: "+assets.Source(config));
            body.AppendLine(config.EndsWith(".json",StringComparison.OrdinalIgnoreCase)?Read(config).Body:assets.Text(config)).AppendLine();
        }
        if(frameworkIndex>=0&&parts.Length>frameworkIndex+3){
            string subRoot=Join(parts.Take(frameworkIndex+3));
            if(oar>=0){
                framework="OAR";
                string modRoot=Join(parts.Take(oar+2)),author=modRoot+"\\config.json",user=modRoot+"\\user.json";
                var info=Read(author).Json;
                mod=Text(info,"name");if(mod.Length==0)mod=parts[oar+1];
                Section("MOD \u00b7 AUTHOR CONFIGURATION",author);Section("MOD \u00b7 USER DATA (when present, replaces author data)",user);
                author=subRoot+"\\config.json";user=subRoot+"\\user.json";
                var subInfo=Read(author).Json;bool hasUser=assets.Exists(user);
                data=hasUser?Read(user).Json:subInfo;
                configPath=hasUser?user:author;
                group=Text(subInfo,"name");if(group.Length==0)group=parts[oar+2];
                priority=Integer(data,"priority");disabled=Flag(data,"disabled");
                Section("SUBMOD \u00b7 AUTHOR CONFIGURATION",author);
                Section("SUBMOD \u00b7 USER DATA (active data source; author name/description retained)",user);
                if(subInfo==null)body.AppendLine("Author submod configuration is missing or invalid; OAR eligibility is unverified.");
                if(priority==null)body.AppendLine("Active submod data has no valid priority; OAR cannot load it as a normal submod.");
                string overrideFolder=Text(data,"overrideAnimationsFolder");
                if(overrideFolder.Length>0)body.AppendLine("Animation folder override: "+overrideFolder);
                if(sharedFolder!=null)body.AppendLine("Resolved shared folder: "+sharedFolder).AppendLine("Animation asset: "+path).AppendLine("Asset source: "+assets.Source(path));
            }else{
                framework="DAR";group=parts[dar+1].Equals("_CustomConditions",StringComparison.OrdinalIgnoreCase)?"Priority "+parts[dar+2]:parts[dar+1]+" \u00b7 actor "+parts[dar+2];
                // OAR gives a legacy plugin-and-actor folder priority 0 (Parsing.cpp, kLegacyActorBase).
                if(!parts[dar+1].Equals("_CustomConditions",StringComparison.OrdinalIgnoreCase))priority=0;
                else if(int.TryParse(parts[dar+2],out int value))priority=value;
                configPath=subRoot+"\\_conditions.txt";
                Section("DAR CONDITIONS",configPath);
                string user=subRoot+"\\user.json";
                if(assets.Exists(user)){
                    data=Read(user).Json;configPath=user;priority=Integer(data,"priority");disabled=Flag(data,"disabled");
                    group=priority.HasValue?"Priority "+priority+" \u00b7 OAR user override":group+" \u00b7 invalid OAR user data";
                    Section("OAR LEGACY USER DATA (overrides DAR condition data)",user);
                }
                if(!assets.Exists(configPath)&&parts[dar+1].Equals("_CustomConditions",StringComparison.OrdinalIgnoreCase))body.AppendLine("Missing DAR condition file.");
            }
            int variant=Array.FindIndex(parts,frameworkIndex+3,p=>p.StartsWith("_variants_",StringComparison.OrdinalIgnoreCase));
            string replacementPath;
            if(variant>=0){
                variantGroup=parts[variant]["_variants_".Length..]+".hkx";
                replacementPath=Join(parts.Skip(frameworkIndex+3).Take(variant-frameworkIndex-3).Append(variantGroup));
                variantDetails="variant";
                body.AppendLine("Variant of: "+replacementPath);
            }else replacementPath=Join(parts.Skip(frameworkIndex+3));
            replacementKey=ClipOverrides.FromMeshes(parts.Take(frameworkIndex).Append(replacementPath));
            var actualParts=Assets.Normalize(path).Split('\\');
            int actualVariant=Array.FindIndex(actualParts,p=>p.StartsWith("_variants_",StringComparison.OrdinalIgnoreCase));
            string actual=Assets.GamePath(actualVariant>=0?Join(actualParts.Take(actualVariant+1)):path);
            bool Matches(JsonElement setting){
                string configured=Assets.GamePath(Text(setting,"path"));
                return configured.Length>0&&(configured.Equals(replacementPath,StringComparison.OrdinalIgnoreCase)||
                    actual.Equals(configured,StringComparison.OrdinalIgnoreCase)||actual.EndsWith("\\"+configured,StringComparison.OrdinalIgnoreCase));
            }
            if(Property(data,"disabledAnimations") is {ValueKind:JsonValueKind.Array} oldDisabled&&oldDisabled.EnumerateArray().Any(Matches)){
                disabled=true;body.AppendLine("Disabled by legacy disabledAnimations configuration.");
            }
            if(Property(data,"replacementAnimDatas") is {ValueKind:JsonValueKind.Array} replacements)
                foreach(var replacement in replacements.EnumerateArray()){
                    if(!Matches(replacement))continue;
                    disabled|=Flag(replacement,"disabled");
                    body.AppendLine("MATCHING ANIMATION SETTINGS").AppendLine(JsonSerializer.Serialize(replacement,new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
                    if(variant>=0&&Property(replacement,"variants") is {ValueKind:JsonValueKind.Array} variants)
                        foreach(var v in variants.EnumerateArray())
                            if(Text(v,"filename").Equals(parts[^1],StringComparison.OrdinalIgnoreCase)){
                                disabled|=Flag(v,"disabled");
                                string weight=Property(v,"weight")?.ToString()??"1";
                                variantDetails="weight "+weight+(Flag(v,"playOnce")?" \u00b7 play once":"");
                            }
                }
            if(oar>=0&&priority.HasValue)group+=" \u00b7 priority "+priority;
            if(disabled)body.AppendLine("Disabled in OAR configuration. Manual preview remains available.");
            body.AppendLine("Conditions and runtime variant choices are shown for inspection, not evaluated as an in-game actor.");
        }
        string summary=$"{framework} / {mod} / {group}\nSource mod: {source}\n";
        if(priority.HasValue)summary+="Active priority: "+priority+"\n";
        if(variantGroup.Length>0)summary+=$"Replaces: {variantGroup} \u00b7 {variantDetails}\n";
        if(disabled)summary+="Disabled in OAR; manual preview is available.\n";
        return new(path,framework,mod,group,configPath,summary+"\n"+ConditionOutline.Render(data)+"\n"+body){
            SourceName=source,VariantGroup=variantGroup,VariantDetails=variantDetails,Priority=priority,Disabled=disabled,ReplacementKey=replacementKey
        };
    }
}

// Which other clips would replace a clip in game. OAR picks, for one animation file, the enabled submod with the
// highest priority whose conditions pass; conditions are not evaluated here, so these are the submods that can win.
public static class ClipOverrides
{
    public const string Plain="Vanilla / loose";
    public static string FromMeshes(IEnumerable<string> parts)
    {
        var list=parts.SelectMany(p=>p.Split('\\',StringSplitOptions.RemoveEmptyEntries)).ToList();
        int meshes=list.FindLastIndex(p=>p.Equals("meshes",StringComparison.OrdinalIgnoreCase));
        return string.Join("\\",meshes>=0?list.Skip(meshes):list);
    }
    public static ILookup<string,ClipEntry> Index(IEnumerable<ClipEntry> clips)=>clips.Where(c=>c.ReplacementKey.Length>0).ToLookup(c=>c.ReplacementKey,StringComparer.OrdinalIgnoreCase);
    // Enabled OAR and DAR submods, other than the clip's own, that hold the same animation with a higher priority (any
    // priority when the clip is a plain file). One clip per submod, highest priority first.
    public static List<ClipEntry> For(ClipEntry entry,ILookup<string,ClipEntry> index)
    {
        if(entry.ReplacementKey.Length==0)return [];
        long own=entry.Framework==Plain?long.MinValue:entry.Priority??long.MinValue;
        // A clip dropped in from outside the mounted folders (a rooted path) is not part of the load, so it overrides nothing.
        return index[entry.ReplacementKey].Where(o=>o.Framework!=Plain&&!o.Disabled&&!System.IO.Path.IsPathRooted(o.Path)&&o.Priority is int p&&p>own
                &&!(o.ConfigPath.Length>0&&o.ConfigPath.Equals(entry.ConfigPath,StringComparison.OrdinalIgnoreCase)))
            .GroupBy(o=>o.ConfigPath.Length>0?o.ConfigPath:o.Path,StringComparer.OrdinalIgnoreCase).Select(g=>g.First())
            .OrderByDescending(o=>o.Priority).ThenBy(o=>o.Path,StringComparer.OrdinalIgnoreCase).ToList();
    }
    public static string Describe(ClipEntry entry,List<ClipEntry> overriders,int limit=int.MaxValue)
    {
        if(overriders.Count==0)return "";
        var text=new StringBuilder($"Overridden by {overriders.Count} higher-priority {(overriders.Count==1?"folder":"folders")} holding {System.IO.Path.GetFileName(entry.ReplacementKey)} (conditions are not evaluated):");
        foreach(var o in overriders.Take(limit))text.Append($"\n  {o.Framework} \u00b7 {o.LibraryMod} / {o.Group}\n      {System.IO.Path.GetDirectoryName(o.Path)}");
        if(overriders.Count>limit)text.Append($"\n  and {overriders.Count-limit} more (see Configuration & source)");
        return text.ToString();
    }
}
