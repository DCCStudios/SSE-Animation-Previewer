using System.Drawing;

namespace SSEPreview;

/// <summary>One tint mask of a race for one sex (RACE TINI/TINT/TINP/TIND and its TINC/TINV presets).</summary>
public sealed record RaceTint(int Index,string Texture,int Type,GameRecord? Default,IReadOnlyList<(GameRecord? Color,float Value)> Presets)
{
    public const int SkinTone=6;
    /// <summary>The colour and strength the game uses when an actor carries no layer for this mask: the default
    /// colour form, at the strength of the preset that names the same form (0 when none does).</summary>
    public (Color Color,float Alpha) Fallback()
    {
        if(Default==null||Records.ColorValue(Default) is not {} color)return (Color.White,0);
        foreach(var preset in Presets)if(preset.Color?.Key==Default.Key)return (color,preset.Value);
        return (color,0);
    }
}
/// <summary>One tint layer of an NPC (NPC_ TINI/TINC/TINV).</summary>
public readonly record struct NpcTintLayer(int Index,Color Color,float Alpha);

/// <summary>What an NPC record says about how the actor looks, resolved the way the game resolves it
/// (AE 1.6.1170: TESNPC::Load 24663, CopyFromTemplateForms 24720, head part list 24730, FaceGeom path 24726).</summary>
public sealed class NpcLook
{
    public required GameRecord Npc;
    /// <summary>The record the looks come from: the end of the "Use Traits" template chain (the game's faceNPC root).</summary>
    public required GameRecord Traits;
    public GameRecord? Race,HairColor,FaceTextures,SkinArmor;
    public bool Female;
    public int Weight=50;public float Height=1;
    /// <summary>QNAM as the game stores it (each float truncated to a byte). Black when the record has none.</summary>
    public Color Skin=Color.Black;public bool HasSkin;
    /// <summary>The record's own head parts (PNAM), in order.</summary>
    public List<GameRecord> OwnParts=[];
    /// <summary>Every part the game builds: own parts and their extras, then the race defaults a part type still lacks.</summary>
    public List<GameRecord> Parts=[];
    public List<NpcTintLayer> Layers=[];
    public List<GameRecord> Outfit=[];
    /// <summary>Game paths of the baked head and its tint, named by the plugin that first defines the traits record.</summary>
    public string FaceGeom="",FaceTint="";
    public List<string> Notes=[];
}

public static class NpcLooks
{
    static uint TemplateFlags(GameRecord npc)=>npc.First("ACBS") is {Bytes.Length:>=20} acbs?BitConverter.ToUInt16(acbs.Bytes,18):0u;
    // A template may be a levelled list, which the game rolls by level when the actor spawns. The first entry
    // stands in for it here.
    static GameRecord? TemplateTarget(Records records,GameRecord owner,List<string> notes,int depth=0)
    {
        var target=records.Resolve(owner,owner.First("TPLT")?.UInt()??0);
        while(target?.Kind=="LVLN"&&depth++<8){
            var list=target;var entry=list.All("LVLO").Select(f=>records.Resolve(list,f.UInt(4))).FirstOrDefault(r=>r!=null);
            notes.Add($"Template {list.EditorId} is a levelled list; its first entry {entry?.EditorId??"(none)"} is used. In game the entry depends on the player's level.");
            target=entry;
        }
        return target?.Kind=="NPC_"?target:null;
    }
    static GameRecord Follow(Records records,GameRecord npc,uint bit,string what,List<string> notes)
    {
        var current=npc;
        for(int i=0;i<16&&(TemplateFlags(current)&bit)!=0;i++){
            var next=TemplateTarget(records,current,notes);
            if(next==null||next==current)break;
            notes.Add($"{what} from template {next.EditorId} [{next.Plugin}].");current=next;
        }
        return current;
    }
    public static NpcLook Build(Records records,GameRecord npc)
    {
        var notes=new List<string>();
        var traits=Follow(records,npc,1,"Traits",notes);
        var look=new NpcLook{Npc=npc,Traits=traits,Notes=notes};
        look.Female=((traits.First("ACBS")?.UInt()??0)&1)!=0;
        look.Race=records.Resolve(traits,traits.First("RNAM")?.UInt()??0);
        if(look.Race?.Kind!="RACE"){look.Race=null;notes.Add("The race of "+traits.EditorId+" is not in the loaded plugins.");}
        if(traits.First("NAM7") is {Bytes.Length:>=4} weight)look.Weight=(int)Math.Clamp(MathF.Round(weight.Float()),0,100);
        if(traits.First("NAM6") is {Bytes.Length:>=4} height&&height.Float()>0)look.Height=height.Float();
        if(traits.First("QNAM") is {Bytes.Length:>=12} skin){
            int Channel(int at)=>Math.Clamp((int)(skin.Float(at)*255f),0,255);
            look.Skin=Color.FromArgb(255,Channel(0),Channel(4),Channel(8));look.HasSkin=true;
        }else notes.Add(traits.EditorId+" has no skin colour (QNAM); the game tints its skin with black, which darkens it.");
        var hair=records.Resolve(traits,traits.First("HCLF")?.UInt()??0);look.HairColor=hair?.Kind=="CLFM"?hair:null;
        var face=records.Resolve(traits,traits.First("FTST")?.UInt()??0);look.FaceTextures=face?.Kind=="TXST"?face:null;
        var skinArmor=records.Resolve(traits,traits.First("WNAM")?.UInt()??0);look.SkinArmor=skinArmor?.Kind=="ARMO"?skinArmor:null;
        foreach(var field in traits.All("PNAM")){var part=records.Resolve(traits,field.UInt());if(part?.Kind=="HDPT")look.OwnParts.Add(part);}
        look.Parts=HeadParts(records,look.OwnParts,look.Race,look.Female);
        // Tint layers: TINI opens a layer, TINC and TINV fill it.
        int index=-1;Color color=Color.White;
        foreach(var field in traits.Fields){
            if(field.Tag=="TINI"&&field.Bytes.Length>=2){index=BitConverter.ToUInt16(field.Bytes);color=Color.White;}
            else if(field.Tag=="TINC"&&field.Bytes.Length>=3)color=Color.FromArgb(255,field.Bytes[0],field.Bytes[1],field.Bytes[2]);
            else if(field.Tag=="TINV"&&index>=0&&field.Bytes.Length>=1){look.Layers.Add(new(index,color,(sbyte)field.Bytes[0]*.01f));index=-1;}
        }
        // The head files are named after the plugin that first defines the record, never after an override.
        string[] key=traits.Key.Split('|');string plugin=key[0];uint id=Convert.ToUInt32(key[1],16);
        if(records.IsLight(plugin))id&=0xFFF;
        look.FaceGeom=$@"meshes\actors\character\facegendata\facegeom\{plugin}\{id:X8}.nif";
        look.FaceTint=$@"textures\actors\character\facegendata\facetint\{plugin}\{id:X8}.dds";
        // Outfit: the "Use Inventory" template chain's default outfit. Levelled items take their first entry.
        var inventory=Follow(records,npc,0x100,"Outfit",notes);
        var outfit=records.Resolve(inventory,inventory.First("DOFT")?.UInt()??0);
        if(outfit?.Kind=="OTFT"&&outfit.First("INAM") is {} items)
            for(int i=0;i+4<=items.Bytes.Length;i+=4){
                var item=records.Resolve(outfit,items.UInt(i));
                for(int depth=0;item?.Kind=="LVLI"&&depth<8;depth++){var list=item;item=list.All("LVLO").Select(f=>records.Resolve(list,f.UInt(4))).FirstOrDefault(r=>r!=null);}
                // A shield (slot 39) hangs on the shield bone, which the worn-armor path does not do, so it is left out.
                if(item?.Kind=="ARMO"){if((item.Slots&(1u<<9))!=0)notes.Add("Outfit shield "+item.EditorId+" is not shown.");else look.Outfit.Add(item);}
            }
        return look;
    }
    /// <summary>The head part list the game builds (24730, 24818, 24856): the actor's own parts in order, each followed
    /// by its extra parts, then the race defaults. A part is left out when the list already has one of its type and
    /// the type is face, eyes, hair, facial hair or brows.</summary>
    public static List<GameRecord> HeadParts(Records records,IEnumerable<GameRecord> own,GameRecord? race,bool female)
    {
        var parts=new List<GameRecord>();var types=new HashSet<uint>();
        void Add(GameRecord part,int depth){
            uint type=part.First("PNAM")?.UInt()??0;
            if(types.Contains(type)&&type is 1 or 2 or 3 or 4 or 6)return;
            if(parts.Any(p=>p.Key==part.Key))return;
            parts.Add(part);types.Add(type);
            if(depth<8)foreach(var extra in part.All("HNAM"))if(records.Resolve(part,extra.UInt()) is {Kind:"HDPT"} child)Add(child,depth+1);
        }
        foreach(var part in own)Add(part,0);
        foreach(var part in records.DefaultHeadParts(race,female))Add(part,0);
        return parts;
    }
    /// <summary>The colour and strength of every race tint mask for this actor, in the race's order: the actor's
    /// layer when it has one for the mask, else the mask's default (24786).</summary>
    public static List<(RaceTint Mask,Color Color,float Alpha)> TintLayers(Records records,GameRecord? race,bool female,IReadOnlyList<NpcTintLayer>? layers)
    {
        var result=new List<(RaceTint,Color,float)>();
        foreach(var mask in records.RaceTints(race,female)){
            int found=layers==null?-1:layers.ToList().FindIndex(l=>l.Index==mask.Index);
            if(found>=0)result.Add((mask,layers![found].Color,layers[found].Alpha));
            else{var (color,alpha)=mask.Fallback();result.Add((mask,color,alpha));}
        }
        return result;
    }
}
