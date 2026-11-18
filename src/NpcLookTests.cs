using System.Drawing;
using System.Text;

namespace SSEPreview;

/// <summary>Checks of the NPC look rules on records built in memory; no game files are needed.</summary>
public static class NpcLookTests
{
    static Field F(string tag,params byte[] bytes)=>new(tag,bytes);
    static Field Id(string tag,uint id)=>new(tag,BitConverter.GetBytes(id));
    static Field Text(string tag,string text)=>new(tag,Encoding.UTF8.GetBytes(text+"\0"));
    static Field Float(string tag,params float[] values)=>new(tag,values.SelectMany(BitConverter.GetBytes).ToArray());
    static GameRecord Add(Records records,string kind,uint id,string editorId,params Field[] fields)
    {
        var record=new GameRecord{Kind=kind,Plugin="fixture.esp",Id=id,Key=$"fixture.esp|{id&0xFFFFFF:X6}",Fields=[Text("EDID",editorId),..fields]};
        records.All[record.Key]=record;return record;
    }
    static byte[] Acbs(bool female,ushort template){var b=new byte[24];b[0]=(byte)(female?1:0);BitConverter.GetBytes(template).CopyTo(b,18);return b;}
    public static void Test(Action<bool,string> check)
    {
        var records=new Records();
        Add(records,"CLFM",0x10,"Tone",F("CNAM",200,100,50,0));
        Add(records,"CLFM",0x11,"Black",F("CNAM",0,0,0,0));
        Add(records,"HDPT",0x20,"RaceHead",Id("PNAM",1),Text("MODL","head.nif"));
        Add(records,"HDPT",0x21,"RaceEyes",Id("PNAM",2));
        Add(records,"HDPT",0x22,"RaceScar",Id("PNAM",5));
        Add(records,"HDPT",0x23,"HairLine",Id("PNAM",0));
        Add(records,"HDPT",0x24,"OwnHair",Id("PNAM",3),Id("HNAM",0x23));
        Add(records,"HDPT",0x25,"OwnEyes",Id("PNAM",2));
        var race=Add(records,"RACE",0x30,"FixtureRace",
            F("NAM0"),F("MNAM"),
            F("TINI",1,0),Text("TINT",@"Actors\Character\Tints\SkinTone.dds"),F("TINP",6,0),Id("TIND",0x10),Id("TINC",0x11),Float("TINV",.25f),F("TIRS",0,0),Id("TINC",0x10),Float("TINV",.5f),F("TIRS",1,0),
            F("TINI",2,0),Text("TINT",@"Actors\Character\Tints\Paint.dds"),F("TINP",7,0),
            F("NAM0"),F("FNAM"),Id("HEAD",0x20),Id("HEAD",0x21),Id("HEAD",0x22),F("TINI",9,0),Text("TINT","female.dds"),F("TINP",6,0),Id("TIND",0x11));
        var tints=records.RaceTints(race,false);
        check(tints.Count==2&&tints[0].Index==1&&tints[0].Type==RaceTint.SkinTone&&tints[0].Presets.Count==2&&tints[1].Default==null&&records.RaceTints(race,true).Single().Index==9,"Race tint masks are read per sex with their presets");
        var fallback=tints[0].Fallback();
        check(fallback.Color==Color.FromArgb(255,200,100,50)&&Math.Abs(fallback.Alpha-.5f)<1e-6&&tints[1].Fallback().Alpha==0,"A mask's default is its TIND colour at the strength of the preset naming that colour, and nothing without a default");
        // lerp(0.5, colour, 0.5), truncated: (200/255*.5+.25)*255 = 163.75
        check(records.RaceSkinColor(race,false)==Color.FromArgb(255,163,113,88),"The race skin colour blends the default skin tone over mid grey by its preset strength");
        var template=Add(records,"NPC_",0x40,"Template",new Field("ACBS",Acbs(true,0)),Id("RNAM",0x30),Float("QNAM",.5f,1f,0f),Float("NAM7",75f),Id("PNAM",0x24),Id("PNAM",0x25),
            F("TINI",1,0),F("TINC",10,20,30,0),F("TINV",40,0,0,0),F("TIAS",0,0));
        var actor=Add(records,"NPC_",0x41,"Actor",new Field("ACBS",Acbs(false,1)),Id("TPLT",0x40),Float("QNAM",1f,1f,1f));
        var look=NpcLooks.Build(records,actor);
        check(look.Traits==template&&look.Female&&look.Race==race&&look.Weight==75,"Use Traits takes sex, race and weight from the template");
        check(look.HasSkin&&look.Skin==Color.FromArgb(255,127,255,0),"The skin colour is the traits record's QNAM, each float truncated to a byte");
        check(look.FaceGeom==@"meshes\actors\character\facegendata\facegeom\fixture.esp\00000040.nif"&&look.FaceTint.EndsWith(@"facetint\fixture.esp\00000040.dds"),"The baked head is named after the traits record and its defining plugin");
        check(look.Parts.Select(p=>p.EditorId).SequenceEqual(["OwnHair","HairLine","OwnEyes","RaceHead","RaceScar"]),"Head parts: own parts with their extras, then the race defaults whose type is still free; a scar is never blocked");
        var layers=NpcLooks.TintLayers(records,race,true,look.Layers);
        check(look.Layers.Count==1&&look.Layers[0].Index==1&&look.Layers[0].Color==Color.FromArgb(255,10,20,30)&&Math.Abs(look.Layers[0].Alpha-.4f)<1e-6,"NPC tint layers are read as index, colour and strength over 100");
        var male=NpcLooks.TintLayers(records,race,false,look.Layers);
        check(male.Count==2&&male[0].Color==Color.FromArgb(255,10,20,30)&&Math.Abs(male[0].Alpha-.4f)<1e-6&&male[1].Alpha==0&&layers.Single().Alpha==0,"A mask takes the actor's layer with its index, else its own default");
        check(NpcLooks.Build(records,template).Traits==template,"An NPC without Use Traits keeps its own looks");
        // Paired clips: one file, two actors, told apart by the "2_" prefix on track names.
        var paired=new PairedClip{TrackNames=["PairedRoot","2_","2_NPC Root [Root]","2_Bone","NPC","NPC Root [Root]","Bone","Other"]};
        string[] skeleton=["NPC Root [Root]","Bone","Other"];
        check(FO4_Base_Library.HkxPoseImportSession.IsPairedClip(paired.TrackNames)&&!FO4_Base_Library.HkxPoseImportSession.IsPairedClip(skeleton),"A clip with a PairedRoot or 2_ track is a paired clip; plain bone tracks are not");
        check(paired.Bound(skeleton,"")==3&&paired.Bound(skeleton,PairedClip.Second)==2,"Bones are counted per role by exact track name");
        check(paired.Bones(PairedClip.Second).SequenceEqual(["NPC Root [Root]","Bone"])&&paired.Bones("").SequenceEqual(["NPC Root [Root]","Bone","Other"]),"Each role's bones leave out the mark tracks and the other role");
    }
}
