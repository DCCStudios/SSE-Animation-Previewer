using System.Text;

namespace SSEPreview;
public static class CharacterTests
{
    public static void Run()
    {
        List<string> log=[];
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"character-tests.txt"),log);if(!ok)throw new InvalidDataException(message);}
        var records=new Records();
        GameRecord Head(uint id,byte flags,string mesh){
            var record=new GameRecord{Kind="HDPT",Plugin="fixture.esp",Id=id,Key=$"fixture.esp|{id:X6}",
                Fields=[new("DATA",[flags]),new("MODL",Encoding.UTF8.GetBytes(mesh+"\0"))]};
            records.All[record.Key]=record;return record;
        }
        var male=Head(1,3,"male.nif");var female=Head(2,5,"female.nif");var both=Head(3,7,"both.nif");
        var extra=Head(4,8,"hairline.nif");male.Fields.Add(new("HNAM",BitConverter.GetBytes(4u)));
        extra.Fields.Add(new("HNAM",BitConverter.GetBytes(1u)));
        Check(records.HeadParts(null,false).Select(h=>h.Id).SequenceEqual(new uint[]{1,3}),"Playable bit is independent of male head-part compatibility");
        Check(records.HeadParts(null,true).Select(h=>h.Id).SequenceEqual(new uint[]{2,3}),"Female compatibility uses flag 0x04 and omits male-only parts");
        Check(records.HeadPartModels([male,male]).SequenceEqual(new[]{@"meshes\male.nif",@"meshes\hairline.nif"}),"Linked extra head parts load once and recursive links terminate");

        var textureSet=new GameRecord{Kind="TXST"};
        for(int i=0;i<8;i++)textureSet.Fields.Add(new($"TX{i:00}",Encoding.UTF8.GetBytes($"slot{i}.dds\0")));
        Check(records.TexturePaths(textureSet)!.SequenceEqual(new[]{0,1,3,4,5,2,6,7}.Select(i=>@"textures\slot"+i+".dds")),
            "Texture-set record channels map to the correct NIF diffuse, normal, glow, height, environment and mask slots");
        textureSet.Plugin="fixture.esp";textureSet.Id=20;textureSet.Key="fixture.esp|000014";records.All[textureSet.Key]=textureSet;
        var femaleSet=new GameRecord{Kind="TXST",Plugin="fixture.esp",Id=21,Key="fixture.esp|000015",Fields=[new("TX00",Encoding.UTF8.GetBytes("female.dds\0"))]};
        records.All[femaleSet.Key]=femaleSet;
        var addon=new GameRecord{Kind="ARMA",Plugin="fixture.esp",Fields=[new("NAM0",BitConverter.GetBytes(20u)),new("NAM1",BitConverter.GetBytes(21u))]};
        Check(records.ArmorSkinTextures(addon,false)![0]==@"textures\slot0.dds"&&records.ArmorSkinTextures(addon,true)![0]==@"textures\female.dds",
            "Armor skin texture links select the requested gender");
        Check(records.ArmorSkinTextures(new GameRecord{Kind="ARMA"},false)==null,"Absent armor skin link preserves NIF materials");
        var nif=new FO4_Base_Library.Nifcontent_Class_Manolo();nif.Create(NiflySharp.NiVersion.GetSSE(),true);
        var shared=new NiflySharp.Blocks.BSShaderTextureSet{Textures=[new("original.dds")]};
        int sharedId=nif.AddBlock(shared);
        var skinShader=new NiflySharp.Blocks.BSLightingShaderProperty{Type=NiflySharp.Helpers.ShaderHelper.ShaderGameType.SK,ShaderType_SK_FO4=NiflySharp.Enums.BSLightingShaderType.SkinTint,TextureSetRef=new(sharedId)};
        var metalShader=new NiflySharp.Blocks.BSLightingShaderProperty{Type=NiflySharp.Helpers.ShaderHelper.ShaderGameType.SK,TextureSetRef=new(sharedId)};
        nif.AddBlock(skinShader);nif.AddBlock(metalShader);
        Check(MaterialOverrides.Apply(nif,skinTextures:records.ArmorSkinTextures(addon,true))==1,
            "Armor texture override applies to skin shaders only");
        Check(ReferenceEquals(nif.GetBlock(metalShader.TextureSetRef),shared)&&!ReferenceEquals(nif.GetBlock(skinShader.TextureSetRef),shared)&&shared.Textures[0].Content=="original.dds",
            "Skin texture replacement detaches shared texture blocks without modifying metal materials");
        Check(MaterialOverrides.Apply(nif,allTextures:records.TexturePaths(textureSet))==2,
            "Full head-part texture override still applies to every lighting shader");
        string game=Path.Combine(SelfTest.Mods,@"..\Game Root\Data\Skyrim.esm");
        foreach(var record in Records.Parse(File.ReadAllBytes(game),"Skyrim.esm"))records.All[record.Key]=record;
        var race=records.Of("RACE").Single(r=>r.EditorId=="NordRace");
        var maleDefaults=records.DefaultHeadParts(race,false).ToArray();
        var femaleDefaults=records.DefaultHeadParts(race,true).ToArray();
        Check(maleDefaults.Any(h=>h.First("PNAM")?.UInt()==1)&&maleDefaults.All(h=>!h.EditorId.Contains("Manekin",StringComparison.OrdinalIgnoreCase)),
            "Nord male face defaults come from the race's own HEAD records");
        Check(femaleDefaults.Any(h=>h.First("PNAM")?.UInt()==1)&&!maleDefaults.Select(h=>h.Key).SequenceEqual(femaleDefaults.Select(h=>h.Key)),
            "Male and female race head defaults are read from their separate sections");
        var defaultHair=records.HeadHairColor(maleDefaults.First(),race,false);
        Check(defaultHair.HasValue&&defaultHair.Value!=Color.White,"Race default hair color resolves from the gendered CLFM link");
        var skin=records.Resolve(race,race.First("WNAM")!.UInt())!;
        var naked=records.ArmorModels(skin,race,false).ToArray();
        var cuirass=records.Of("ARMO").Single(r=>r.EditorId=="ArmorIronCuirass");
        uint occupied=records.ArmorAddons(cuirass,race).Aggregate(0u,(mask,a)=>mask|a.Slots);
        var covered=records.ArmorModels(skin,race,false,occupied).ToArray();
        Check(naked.Length>=3&&covered.Length<naked.Length&&covered.Any(p=>p.Contains("hands",StringComparison.OrdinalIgnoreCase))&&covered.Any(p=>p.Contains("feet",StringComparison.OrdinalIgnoreCase)),
            "Real iron cuirass replaces the Nord body skin while preserving uncovered hands and feet");
        Check(records.ArmorModels(cuirass,race,false).Any()&&records.ArmorModels(cuirass,race,true).Any(),"Real iron armor resolves both male and female race-compatible addons");
        var femaleHeads=records.HeadParts(race,true).ToArray();
        Check(femaleHeads.Length>0&&femaleHeads.All(h=>(h.First("DATA")!.Bytes[0]&6)==0||(h.First("DATA")!.Bytes[0]&4)!=0),
            "Actual Nord female choices contain no male-only head parts");
        var hair=records.HeadParts(race,false).First(h=>h.First("PNAM")?.UInt()==3&&h.All("HNAM").Any());
        Check(records.HeadPartModels([hair]).Count()>1,"An actual Skyrim hairstyle brings in its linked extra geometry");
        foreach(var record in Records.Parse(File.ReadAllBytes(Path.Combine(SelfTest.Mods,@"SC_FF7R_Cloud\SC_FF7R_Cloud.esp")),"SC_FF7R_Cloud.esp"))records.All[record.Key]=record;
        var cloud=records.Of("RACE").Single(r=>r.EditorId=="SC_FF7R_Cloud_Race");
        var cloudHair=records.DefaultHeadParts(cloud,false).Single(h=>h.First("PNAM")?.UInt()==3);
        Check(records.HeadHairColor(cloudHair,cloud,false)==Color.FromArgb(255,92,88,80),"Cloud sole allowed PlatinumBlond CLFM resolves when HCLF is empty");
        Check(records.RaceSkinColor(cloud,false)?.ToArgb()==Color.White.ToArgb(),"Cloud male default skin color is read from the correct tint group");
        Check(records.RaceSkinColor(cloud,true)!=null,"Female skin tone resolves independently");
        var overrideColor=new GameRecord{Kind="CLFM",Plugin="fixture.esp",Id=40,Key="fixture.esp|000028",Fields=[new("CNAM",[20,60,90,0])]};records.All[overrideColor.Key]=overrideColor;
        male.Fields.Add(new("CNAM",BitConverter.GetBytes(40u)));
        Check(records.HeadHairColor(male,cloud,false)==Color.FromArgb(255,20,60,90),"Explicit head-part color overrides race fallback");
        var clock=new PlaybackClock(()=>0){Duration=2,Loop=true};clock.Seek(2);
        Check(clock.Time==2,"Paused looped animation holds its scrubbed final frame");clock.Play();Check(clock.Time==0,"Looped playback resumes at the beginning from its end");
        log.Add("Naked: "+string.Join("; ",naked));log.Add("With iron cuirass: "+string.Join("; ",covered));
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"character-tests.txt"),log);
    }
}
