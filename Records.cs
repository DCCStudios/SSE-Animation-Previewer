using System.IO.Compression;
using System.Text;

namespace SSEPreview;

public sealed record Field(string Tag, byte[] Bytes)
{
    public string Text => Encoding.UTF8.GetString(Bytes).TrimEnd('\0');
    public uint UInt(int offset=0) => Bytes.Length>=offset+4 ? BitConverter.ToUInt32(Bytes,offset) : 0;
    public float Float(int offset=0) => Bytes.Length>=offset+4 ? BitConverter.ToSingle(Bytes,offset) : 0;
}
public sealed class GameRecord
{
    public string Kind="", Plugin="", Key="";
    public uint Id, Flags;
    public List<string> Masters=[];
    public List<Field> Fields=[];
    public string EditorId => First("EDID")?.Text ?? Key;
    public Field? First(string tag) => Fields.FirstOrDefault(x=>x.Tag==tag);
    public IEnumerable<Field> All(string tag) => Fields.Where(x=>x.Tag==tag);
    public uint Slots => First("BOD2")?.UInt() ?? First("BODT")?.UInt() ?? 0;
    public string Reference(uint id) => $"{((id>>24)<Masters.Count?Masters[(int)(id>>24)]:Plugin).ToLowerInvariant()}|{id & 0xFFFFFF:X6}";
    public override string ToString() => $"{EditorId}  [{Plugin}]";
}
public sealed class Records
{
    public Dictionary<string,GameRecord> All {get;}=new(StringComparer.OrdinalIgnoreCase);
    public List<string> Diagnostics {get;}=[];
    // Bumped by every Load, so caches built from the records know when to rebuild.
    public long Revision {get;private set;}
    public IEnumerable<GameRecord> Of(string kind) => All.Values.Where(x=>x.Kind==kind).OrderBy(x=>x.EditorId);
    public GameRecord? Resolve(GameRecord owner,uint id) => id==0?null:All.GetValueOrDefault(owner.Reference(id));
    public GameRecord? Resolve(string plugin,uint id) => All.GetValueOrDefault($"{plugin.ToLowerInvariant()}|{id & 0xffffff:X6}");
    public void Load(Assets assets, string? pluginsFile)
    {
        All.Clear(); Diagnostics.Clear(); Revision++;
        var names=assets.Paths.Where(p=>!p.Contains('\\') && Path.GetExtension(p).ToLowerInvariant() is ".esm" or ".esp" or ".esl").ToList();
        var order=new List<string>();
        if(!string.IsNullOrEmpty(pluginsFile) && File.Exists(pluginsFile))
        {
            order.AddRange(new[]{"Skyrim.esm","Update.esm","Dawnguard.esm","HearthFires.esm","Dragonborn.esm"}.Where(n=>names.Contains(n,StringComparer.OrdinalIgnoreCase)));
            order.AddRange(ImplicitPlugins.Read(assets.Roots));
            order.AddRange(File.ReadLines(pluginsFile).Select(l=>l.Trim()).Where(l=>l.StartsWith('*')).Select(l=>l[1..].Trim()));
        }
        else {
            // A small loose mod fixture has no profile. Dependency ordering is read from TES4.
            var pending=names.ToHashSet(StringComparer.OrdinalIgnoreCase);
            void Visit(string n,HashSet<string> chain) {
                if(!pending.Contains(n))return;
                if(!chain.Add(n))throw new InvalidDataException("Cyclic plugin masters: "+n);
                var data=assets.Read(n);var header=Parse(data,n).FirstOrDefault(r=>r.Kind=="TES4");
                foreach(var m in header?.All("MAST") ?? []) {if(pending.Contains(m.Text))Visit(m.Text,chain);else if(!order.Contains(m.Text,StringComparer.OrdinalIgnoreCase))Diagnostics.Add($"{n}: missing master {m.Text}");}
                chain.Remove(n);pending.Remove(n);order.Add(n);
            }
            foreach(var name in names.OrderBy(n=>n,StringComparer.OrdinalIgnoreCase))Visit(name,new(StringComparer.OrdinalIgnoreCase));
        }
        if(!string.IsNullOrEmpty(pluginsFile)&&File.Exists(pluginsFile))assets.ApplyArchiveLoadOrder(order.Distinct(StringComparer.OrdinalIgnoreCase).Where(assets.Exists),pluginsFile);
        foreach(var name in order.Distinct(StringComparer.OrdinalIgnoreCase)) {
            if(!assets.Exists(name)) {Diagnostics.Add("Active plugin is missing: "+name);continue;}
            try {foreach(var r in Parse(assets.Read(name),name)) {if(r.Kind=="TES4")continue; if((r.Flags&0x20)!=0)All.Remove(r.Key); else All[r.Key]=r;}}
            catch(Exception ex) {Diagnostics.Add(name+": "+ex.Message);}
        }
    }
    public static List<GameRecord> Parse(byte[] bytes,string plugin)
    {
        List<GameRecord> records=[];List<string> masters=[];
        void Walk(int start,int end) {
            int pos=start;
            while(pos<end) {
                if(end-pos<24)throw new InvalidDataException("Truncated record header");
                string tag=Encoding.ASCII.GetString(bytes,pos,4);uint size=BitConverter.ToUInt32(bytes,pos+4);
                if(tag=="GRUP") {if(size<24 || (long)pos+size>end)throw new InvalidDataException("Invalid GRUP size");Walk(pos+24,pos+(int)size);pos+=(int)size;continue;}
                if((long)pos+24+size>end)throw new InvalidDataException("Invalid record size");
                if(tag is not ("TES4" or "RACE" or "HDPT" or "ARMO" or "ARMA" or "WEAP" or "SPEL" or "MGEF" or "PROJ" or "ARTO" or "TXST" or "FLST" or "EXPL" or "ADDN" or "IPCT" or "IPDS" or "MATT" or "HAZD" or "EFSH" or "CLFM" or "RFCT" or "ACTI" or "SOUN" or "SNDR" or "SNCT" or "SOPM")){pos+=24+(int)size;continue;}
                uint flags=BitConverter.ToUInt32(bytes,pos+8),id=BitConverter.ToUInt32(bytes,pos+12);
                byte[] payload=bytes.AsSpan(pos+24,(int)size).ToArray();
                if((flags&0x40000)!=0) {
                    if(payload.Length<4)throw new InvalidDataException("Truncated compressed record");
                    uint expanded=BitConverter.ToUInt32(payload);if(expanded>256*1024*1024)throw new InvalidDataException("Record exceeds decompression limit");
                    using var z=new ZLibStream(new MemoryStream(payload,4,payload.Length-4),CompressionMode.Decompress);using var dest=new MemoryStream();z.CopyTo(dest);payload=dest.ToArray();
                    if(payload.Length!=expanded)throw new InvalidDataException("Compressed record size mismatch");
                }
                var r=new GameRecord {Kind=tag,Plugin=plugin,Id=id,Flags=flags,Masters=masters};int p=0,extended=0;
                while(p<payload.Length) {
                    if(payload.Length-p<6)throw new InvalidDataException("Truncated subrecord");
                    string st=Encoding.ASCII.GetString(payload,p,4);int len=BitConverter.ToUInt16(payload,p+4);p+=6;
                    if(st=="XXXX") {if(len!=4 || p+4>payload.Length)throw new InvalidDataException("Invalid XXXX");extended=checked((int)BitConverter.ToUInt32(payload,p));p+=4;continue;}
                    if(extended>0) {len=extended;extended=0;}
                    if((long)p+len>payload.Length)throw new InvalidDataException("Invalid subrecord length");
                    r.Fields.Add(new(st,payload.AsSpan(p,len).ToArray()));p+=len;
                }
                if(tag=="TES4") {masters=r.All("MAST").Select(f=>f.Text).ToList();r.Masters=masters;}
                r.Key=r.Reference(id);records.Add(r);pos+=24+(int)size;
            }
        }
        Walk(0,bytes.Length);return records;
    }
    public IEnumerable<GameRecord> DefaultHeadParts(GameRecord? race,bool female)
    {
        if(race==null)yield break;
        int gender=-1;
        foreach(var field in race.Fields){
            if(field.Tag=="MNAM")gender=0;
            else if(field.Tag=="FNAM")gender=1;
            else if(field.Tag=="HEAD"&&gender==(female?1:0)){
                var part=Resolve(race,field.UInt());if(part?.Kind=="HDPT")yield return part;
            }
        }
    }    public IEnumerable<GameRecord> HeadParts(GameRecord? race,bool female)
    {
        foreach(var hd in Of("HDPT")) {
            var flags=hd.First("DATA")?.Bytes.FirstOrDefault() ?? 0;
            if((flags&8)!=0)continue; // Extra parts are expanded from their owning head part.
            if((flags&6)!=0 && (flags&(female?4:2))==0)continue;
            var valid=Resolve(hd,hd.First("RNAM")?.UInt() ?? 0);
            if(race!=null && valid!=null && !valid.All("LNAM").Any(f=>valid.Reference(f.UInt())==race.Key))continue;
            yield return hd;
        }
    }
    public IEnumerable<GameRecord> ArmorAddons(GameRecord armor,GameRecord? race)
    {
        foreach(var f in armor.All("MODL")) {
            if(f.Bytes.Length!=4)continue;
            var addon=Resolve(armor,f.UInt());if(addon?.Kind!="ARMA")continue;
            var supported=addon.All("MODL").Where(a=>a.Bytes.Length==4).Select(a=>addon.Reference(a.UInt())).Append(addon.Reference(addon.First("RNAM")?.UInt() ?? 0));
            if(race!=null && !supported.Contains(race.Key,StringComparer.OrdinalIgnoreCase)) {
                var parent=Resolve(race,race.First("RNAM")?.UInt() ?? 0);
                if(parent==null || !supported.Contains(parent.Key,StringComparer.OrdinalIgnoreCase))continue;
            }
            yield return addon;
        }
    }
    public IEnumerable<string> ArmorModels(GameRecord armor,GameRecord? race,bool female,uint hiddenSlots=0)
    {
        foreach(var addon in ArmorAddons(armor,race)){
            if((addon.Slots&hiddenSlots)!=0)continue;
            var model=addon.First(female?"MOD3":"MOD2")?.Text;
            if(!string.IsNullOrWhiteSpace(model))yield return ProjectileVisuals.ModelPath(model);
        }
    }
    public IEnumerable<GameRecord> HeadPartRecords(IEnumerable<GameRecord> selected)
    {
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<GameRecord> Expand(GameRecord head){
            if(!seen.Add(head.Key))yield break;
            yield return head;
            foreach(var extra in head.All("HNAM")){
                var part=Resolve(head,extra.UInt());if(part?.Kind!="HDPT")continue;
                foreach(var nested in Expand(part))yield return nested;
            }
        }
        foreach(var head in selected)foreach(var part in Expand(head))yield return part;
    }
    public IEnumerable<string> HeadPartModels(IEnumerable<GameRecord> selected)=>HeadPartRecords(selected)
        .Select(h=>h.First("MODL")?.Text).Where(p=>!string.IsNullOrWhiteSpace(p)).Select(p=>@"meshes\"+p);
    public string[]? ArmorSkinTextures(GameRecord addon,bool female) =>
        TexturePaths(Resolve(addon,addon.First(female?"NAM1":"NAM0")?.UInt()??0));
    // Vanilla sheath node per WEAP DNAM animation type. XPMSSE keeps these names as its defaults.
    public static string? WeaponHolsterNode(GameRecord weapon)=>(weapon.First("DNAM")?.Bytes.FirstOrDefault()??0) switch {
        1=>"WeaponSword",2=>"WeaponDagger",3=>"WeaponAxe",4=>"WeaponMace",5 or 6=>"WeaponBack",7=>"WeaponBow",8=>"WeaponStaff",9=>"WeaponCrossBow",_=>null
    };
    public static int WeaponBipedIndex(GameRecord weapon)=>(weapon.First("DNAM")?.Bytes.FirstOrDefault()??0) switch {
        0=>32,1=>33,2=>34,3=>35,4=>36,5 or 6=>37,7=>38,8=>39,9=>40,_=>-1
    };
    // Hair colors the race offers for this sex (AHCM / AHCF), in record order.
    public GameRecord[] RaceHairColors(GameRecord? race,bool female)=>race==null?[]:
        race.All(female?"AHCF":"AHCM").Select(f=>Resolve(race,f.UInt())).Where(c=>c?.Kind=="CLFM").Select(c=>c!).Distinct().ToArray();
    // The color a new actor of this race gets: the race default (HCLF), else the only color the race allows.
    public GameRecord? RaceHairColorRecord(GameRecord? race,bool female)
    {
        if(race==null)return null;
        var color=Resolve(race,race.First("HCLF")?.UInt(female?4:0)??0);
        if(color?.Kind=="CLFM")return color;
        var allowed=RaceHairColors(race,female);
        return allowed.Length==1?allowed[0]:null;
    }
    public Color? RaceHairColor(GameRecord? race,bool female)=>ColorValue(RaceHairColorRecord(race,female));
    public Color? HeadHairColor(GameRecord head,GameRecord? race,bool female)
    {
        var color=Resolve(head,head.First("CNAM")?.UInt()??0);
        return ColorValue(color?.Kind=="CLFM"?color:RaceHairColorRecord(race,female));
    }
    internal static Color? ColorValue(GameRecord? color){
        var bytes=color?.Kind=="CLFM"?color.First("CNAM")?.Bytes:null;
        return bytes is {Length:>=3}?Color.FromArgb(255,bytes[0],bytes[1],bytes[2]):null;
    }
    public Color? RaceSkinColor(GameRecord? race,bool female){
        if(race==null)return null;int gender=-1,type=-1;
        foreach(var field in race.Fields){
            if(field.Tag=="MNAM"){gender=0;type=-1;}else if(field.Tag=="FNAM"){gender=1;type=-1;}
            else if(field.Tag=="TINI")type=-1;
            else if(field.Tag=="TINP"&&field.Bytes.Length>=2)type=BitConverter.ToUInt16(field.Bytes);
            else if(field.Tag=="TIND"&&gender==(female?1:0)&&type==6)return ColorValue(Resolve(race,field.UInt()));
        }
        return null;
    }
    public string[]? TexturePaths(GameRecord? textureSet)
    {
        if(textureSet?.Kind!="TXST")return null;
        // TXST record order differs from BSShaderTextureSet at slots 2..5.
        int[] nifSlots=[0,1,5,2,3,4,6,7];
        string[] textures=new string[8];
        for(int i=0;i<8;i++){
            string value=textureSet.First($"TX{i:00}")?.Text??"";
            textures[nifSlots[i]]=value.Length==0?"":Assets.GamePath(value.StartsWith(@"textures\",StringComparison.OrdinalIgnoreCase)?value:@"textures\"+value);
        }
        return textures;
    }
}