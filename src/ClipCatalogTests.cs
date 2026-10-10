using System.Text.Json;

namespace SSEPreview;
public static class ClipCatalogTests
{
    public static void Run()
    {
        List<string> log=[];
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"catalog-tests.txt"),log);if(!ok)throw new InvalidDataException(message);}
        void Skip(string message){log.Add("SKIP "+message);File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"catalog-tests.txt"),log);}
        using(var conditionDoc=JsonDocument.Parse("""{"conditions":[{"condition":"OR","Conditions":[{"condition":"CustomCheck","negated":true,"disabled":true,"requiredPlugin":"Custom.dll","Form":{"pluginName":"Example.esp","formID":"ABC"},"Comparison":">="},{"condition":"AND","Conditions":[]}]}]}""")){
            var outline=ConditionOutline.Render(conditionDoc.RootElement);
            Check(outline.Contains("ANY (OR)")&&outline.Contains("ALL (AND)")&&outline.Contains("[disabled] NOT CustomCheck"),"Readable condition outline preserves nested groups, negation and disabled state");
            Check(outline.Contains("Example.esp")&&outline.Contains("ABC")&&outline.Contains("Custom.dll")&&outline.Contains("Comparison = \">=\""),"Condition arguments and unknown plugin metadata retain their authored values");
            Check(outline.Contains("not evaluated here"),"Readable conditions never imply live actor eligibility");
        }
        var assets=new Assets();
        // The real mod samples need the mod folders; the fixture section below writes its own data and always runs.
        if(SelfTest.TestMods.FirstOrDefault(m=>!SelfTest.HasMod(m)) is {} missingMod)Skip("Real OAR and DAR mod catalog checks and the dropped clip checks that use them: "+SelfTest.Missing(missingMod));else{
            var coverage=new List<object>();
            foreach(string mod in SelfTest.TestMods){
                string root=Path.Combine(SelfTest.Mods,mod);var mounted=new Assets();mounted.Roots.Add(root);
                var files=Directory.GetFiles(root,"*",SearchOption.AllDirectories);
                foreach(string file in files)mounted.External[Assets.Normalize(Path.GetRelativePath(root,file))]=file;
                mounted.RebuildClips();
                var hkx=files.Where(f=>f.EndsWith(".hkx",StringComparison.OrdinalIgnoreCase)).Select(f=>Assets.Normalize(Path.GetRelativePath(root,f))).Where(f=>f.Contains("animations\\",StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Check(hkx.Count>0&&hkx.SetEquals(mounted.Clips.Select(c=>c.Path)),mod+": every animation-folder HKX is represented in the library");
                Check(mounted.Clips.All(c=>c.LibraryMod.Contains(mod)),mod+": every clip retains its physical mod identity");
                Check(mounted.Clips.All(c=>c.Framework==(c.Path.Contains("OpenAnimationReplacer\\",StringComparison.OrdinalIgnoreCase)?"OAR":c.Path.Contains("DynamicAnimationReplacer\\",StringComparison.OrdinalIgnoreCase)?"DAR":"Vanilla / loose")),mod+": replacer and ordinary loose files are identified separately");
                Check(mounted.Clips.Where(c=>c.Framework is "DAR" or "OAR").All(c=>c.ConfigPath.Length>0&&c.Config.Length>0),mod+": every replacer clip exposes its configuration context");
                coverage.Add(new{mod,physicalHkx=hkx.Count,libraryEntries=mounted.Clips.Count,clips=mounted.Clips.Select(c=>new{c.Path,c.Framework,c.LibraryMod,c.Group,c.Priority,c.ConfigPath}),diagnostics=mounted.Diagnostics.ToArray()});
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"supplied-mod-catalog.json"),JsonSerializer.Serialize(coverage,new JsonSerializerOptions{WriteIndented=true}));
            assets=new Assets();
            string realRoot=Path.Combine(SelfTest.Mods,"Simple MCO Weaponart Casting - Vanilla Spells");
            assets.Roots.Add(realRoot);
            foreach(string file in Directory.EnumerateFiles(realRoot,"*",SearchOption.AllDirectories))
                assets.External[Assets.Normalize(Path.GetRelativePath(realRoot,file))]=file;
            assets.RebuildClips();
            var fire=assets.Clips.First(c=>c.Group.StartsWith("SWA Fireball"));
            Check(fire.Framework=="OAR"&&fire.Mod=="Simple Weapon Art"&&fire.Priority==404,"Real OAR parent name and submod priority are read from their own configs");
            Check(fire.Config.Contains("rob18")&&fire.Config.Contains("Sigil")&&fire.Config.Contains("CompareValues"),"Real OAR parent metadata and complete nested conditions remain readable");
            Check(fire.Config.Contains("ACTIVE CONDITION OUTLINE")&&fire.Config.IndexOf("ACTIVE CONDITION OUTLINE")<fire.Config.IndexOf("MOD \u00b7 AUTHOR CONFIGURATION"),"Real OAR conditions appear before the complete source configuration");
            Check(fire.LibraryMod.Contains("Simple MCO Weaponart Casting"),"Library names identify the physical source mod");
            // The Blade Beam mod was converted to OAR, so the DAR sample is the Buster Sword moveset.
            string darRoot=Path.Combine(SelfTest.Mods,"DAR Moveset and Buster Sword - Cloud Strife FF7 Remake");
            assets=new Assets();assets.Roots.Add(darRoot);
            foreach(string file in Directory.EnumerateFiles(darRoot,"*",SearchOption.AllDirectories))
                assets.External[Assets.Normalize(Path.GetRelativePath(darRoot,file))]=file;
            assets.RebuildClips();
            var dar=assets.Clips.First(c=>c.Path.Contains("DynamicAnimationReplacer",StringComparison.OrdinalIgnoreCase));
            Check(dar.Framework=="DAR"&&dar.Mod=="DAR Moveset and Buster Sword - Cloud Strife FF7 Remake"&&dar.Priority==205000,"DAR entries retain source mod identity and numeric priority");
            Check(dar.Config.Contains("DAR CONDITIONS")&&dar.Config.Contains("IsActorBase"),"DAR condition text is shown without JSON conversion");
            var dropped=assets.Drop(Path.Combine(darRoot,dar.Path));
            Check(dropped.Framework=="DAR"&&dropped.Config.Contains("IsActorBase"),"Dragging an HKX from a replacer folder preserves its configuration context");

            assets.RebuildClips();
            Check(assets.Clips.Count(c=>c.Path.Equals(dropped.Path,StringComparison.OrdinalIgnoreCase))==1,"Dropped DAR clip survives library rebuild without duplicate entries");
            assets.Drop(dropped.Path.ToUpperInvariant());
            Check(assets.DroppedPaths.Count==1,"Repeated drops are deduplicated without regard to path casing");
            var restored=new Assets();
            restored.RestoreDroppedPaths(assets.DroppedPaths);
            Check(restored.Clips.Single().Config.Contains("IsActorBase"),"Restored dropped animation retains adjacent DAR configuration");
            string absent=Path.Combine(AppContext.BaseDirectory,"missing-dropped-test.hkx");
            restored.RestoreDroppedPaths(new[]{absent});
            restored.RebuildClips();
            Check(restored.DroppedPaths.Count==2&&restored.Diagnostics.Count(d=>d.StartsWith("Dropped animation unavailable: "))==1,"Missing dropped files retain their saved path and produce one diagnostic across refreshes");
            bool missingRejected=false;
            try{restored.Drop(absent);}catch(FileNotFoundException){missingRejected=true;}
            Check(missingRejected,"New drops reject missing files");
        }
        string fixture=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,@"..\..\..\..\verification\catalog-fixture"));
        assets=new Assets();assets.Roots.Add(fixture);
        void Write(string path,string body){string full=Path.Combine(fixture,path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);File.WriteAllText(full,body);assets.External[path]=full;}
        const string parent=@"meshes\actors\character\animations\OpenAnimationReplacer\Folder Name";
        const string sub=parent+@"\Subfolder";
        const string variant=sub+@"\_variants_idle";
        Write(parent+@"\config.json","""{"name":"Author mod","author":"Fixture author","description":"Parent metadata"}""");
        Write(sub+@"\config.json","""{"name":"Author submod","priority":10,"conditions":[{"condition":"AuthorCondition"}]}""");
        Write(sub+@"\user.json",JsonSerializer.Serialize(new{
            name="Must not replace author name",priority=99,conditions=new[]{new{condition="UserCondition"}},
            replacementAnimDatas=new[]{new{projectName="DefaultMale",path=@"Data\"+variant,variantMode=1,
                variants=new[]{new{filename="one.hkx",weight=3,disabled=false,playOnce=true},new{filename="two.hkx",weight=1,disabled=true,playOnce=false}}}}
        }));
        Write(variant+@"\one.hkx","");Write(variant+@"\two.hkx","");
        assets.RebuildClips();
        var one=assets.Clips.Single(c=>c.Name=="one.hkx");var two=assets.Clips.Single(c=>c.Name=="two.hkx");
        Check(one.Config.Replace("\r","").Contains("ACTIVE CONDITION OUTLINE (inspection only)\nALL of the following:\n  UserCondition"),"Condition outline uses active user overrides rather than inactive author conditions");
        Check(one.Group.StartsWith("Author submod")&&one.Priority==99,"User data overrides priority while retaining author name");
        Check(one.ConfigPath.EndsWith("user.json")&&one.Config.Contains("AuthorCondition")&&one.Config.Contains("UserCondition"),"Author and active user data are separately readable");
        Check(one.VariantGroup=="idle.hkx"&&one.VariantDetails=="weight 3 \u00b7 play once","OAR variant directory and full Data path settings identify weights and play-once flags: "+one.VariantGroup+" / "+one.VariantDetails);
        Check(!one.Disabled&&two.Disabled&&two.DisplayName.Contains("disabled"),"Disabled variants remain selectable and clearly identified");
        string validUser=File.ReadAllText(Path.Combine(fixture,sub+@"\user.json"));
        Write(sub+@"\user.json","""{"priority":"invalid","disabled":true}""");
        one=assets.Describe(variant+@"\one.hkx");
        Check(one.Priority==null&&one.Config.Contains("no valid priority")&&one.Disabled,"Invalid user priorities are diagnosed without inheriting author priority or crashing");
        Write(sub+@"\user.json","{ malformed");
        one=assets.Describe(variant+@"\one.hkx");
        Check(one.Config.Contains("PARSE ERROR")&&assets.Diagnostics.Count==1,"Malformed active JSON stays inspectable with a parse diagnostic");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"catalog-fixture-details.txt"),one.Config);
        Write(sub+@"\user.json",validUser);
        const string shared=parent+@"\Shared";
        const string first=parent+@"\First route";
        const string second=parent+@"\Second route";
        Write(shared+@"\_variants_attack\first.hkx","");
        Write(shared+@"\_variants_attack\second.hkx","");
        Write(first+@"\config.json","""{"name":"First route","priority":201,"overrideAnimationsFolder":"Shared","conditions":[{"condition":"FirstCondition"}]}""");
        Write(first+@"\ignored.hkx","");
        Write(second+@"\config.json","""{"name":"Second route","priority":202,"overrideAnimationsFolder":"Shared","conditions":[{"condition":"SecondCondition"}]}""");
        assets.RebuildClips();
        var routed=assets.Clips.Where(c=>c.Path.StartsWith(shared+"\\")).ToArray();
        Check(routed.Length==4&&routed.Count(c=>c.Priority==201)==2&&routed.Count(c=>c.Priority==202)==2,
            "Two OAR configurations independently expose the same shared variant assets");
        Check(!assets.Clips.Any(c=>c.Name=="ignored.hkx"),"An OAR animation-folder override replaces the owner's local animation folder");
        Check(routed.All(c=>c.VariantGroup=="attack.hkx")&&routed.Any(c=>c.Config.Contains("FirstCondition"))&&routed.Any(c=>c.Config.Contains("SecondCondition")),
            "Shared variants retain owner conditions and replacement identity");
        Check(routed.All(c=>c.Config.Contains("Resolved shared folder: "+shared)&&c.Config.Contains("Asset source:")),
            "Shared configuration and physical animation provenance are both readable");
        Write(second+@"\user.json","""{"priority":303,"overrideAnimationsFolder":"Subfolder"}""");
        assets.RebuildClips();
        Check(assets.Clips.Count(c=>c.Priority==303&&c.Path.StartsWith(variant+"\\")&&c.VariantGroup=="idle.hkx")==2,
            "An active user configuration can reroute the shared animation directory");
        Write(second+@"\user.json",JsonSerializer.Serialize(new{priority=303,overrideAnimationsFolder="Subfolder",
            disabledAnimations=new[]{new{projectName="DefaultMale",path=@"Data\"+variant}}}));
        assets.RebuildClips();
        Check(assets.Clips.Where(c=>c.Priority==303).All(c=>c.Disabled),"Legacy disabledAnimations metadata applies to shared variant folders");
        Write(first+@"\config.json","""{"name":"First route","priority":201,"overrideAnimationsFolder":"Absent"}""");
        assets.RebuildClips();
        Check(!assets.Clips.Any(c=>c.Priority==201)&&assets.Diagnostics.Any(d=>d.Contains("shared animation folder has no mounted HKX")),
            "Missing shared folders report a diagnostic instead of falling back to ignored local animations");
        Write(first+@"\config.json","""{"name":"First route","priority":201,"overrideAnimationsFolder":"Shared","conditions":[{"condition":"FirstCondition"}]}""");
        Write(second+@"\user.json","""{"priority":202,"overrideAnimationsFolder":"Shared"}""");
        // Overriding folders: the same animation file in several submods, a plain file, a disabled submod and a variant.
        const string animations=@"meshes\actors\character\animations";
        const string low=parent+@"\Override low",high=parent+@"\Override high",off=parent+@"\Override off",top=parent+@"\Override top",legacy=animations+@"\DynamicAnimationReplacer\_CustomConditions\650";
        Write(animations+@"\ovr_attack.hkx","");Write(animations+@"\ovr_alone.hkx","");
        Write(low+@"\config.json","""{"name":"Low","priority":100}""");Write(low+@"\ovr_attack.hkx","");Write(low+@"\ovr_only_low.hkx","");
        Write(high+@"\config.json","""{"name":"High","priority":500}""");Write(high+@"\ovr_attack.hkx","");
        Write(off+@"\config.json","""{"name":"Off","priority":900,"disabled":true}""");Write(off+@"\ovr_attack.hkx","");
        Write(top+@"\config.json","""{"name":"Top","priority":700}""");Write(top+@"\_variants_ovr_attack\a.hkx","");Write(top+@"\_variants_ovr_attack\b.hkx","");
        Write(legacy+@"\_conditions.txt","IsInCombat()");Write(legacy+@"\ovr_attack.hkx","");
        assets.RebuildClips();
        var index=ClipOverrides.Index(assets.Clips);
        ClipEntry Clip(string path)=>assets.Clips.Single(c=>c.Path.Equals(path,StringComparison.OrdinalIgnoreCase));
        string Names(string path)=>string.Join(",",ClipOverrides.For(Clip(path),index).Select(o=>o.Priority));
        Check(Clip(low+@"\ovr_attack.hkx").ReplacementKey.Equals(animations+@"\ovr_attack.hkx",StringComparison.OrdinalIgnoreCase)&&Clip(top+@"\_variants_ovr_attack\a.hkx").ReplacementKey.Equals(animations+@"\ovr_attack.hkx",StringComparison.OrdinalIgnoreCase),
            "An OAR clip and a variant are keyed by the animation file they replace");
        Check(Names(animations+@"\ovr_attack.hkx")=="700,650,500,100","A plain file is overridden by every enabled OAR and DAR folder holding it, highest priority first, a variant folder counted once");
        Check(Names(low+@"\ovr_attack.hkx")=="700,650,500"&&Names(high+@"\ovr_attack.hkx")=="700,650"&&Names(legacy+@"\ovr_attack.hkx")=="700","Only folders with a higher priority override a submod's clip");
        Check(Names(top+@"\_variants_ovr_attack\a.hkx")==""&&Names(off+@"\ovr_attack.hkx")=="","The highest enabled priority is not overridden: a disabled submod does not count, nor do its own variants");
        Check(Names(low+@"\ovr_only_low.hkx")==""&&Names(animations+@"\ovr_alone.hkx")=="","A file no other folder holds has no overrides");
        string described=ClipOverrides.Describe(Clip(low+@"\ovr_attack.hkx"),ClipOverrides.For(Clip(low+@"\ovr_attack.hkx"),index),2);
        Check(described.StartsWith("Overridden by 3 higher-priority folders holding ovr_attack.hkx")&&described.Contains("Top")&&described.Contains(legacy)&&!described.Contains("High")&&described.Contains("and 1 more"),"The hover text names each overriding submod and folder, and says how many are left out");
    }
}
