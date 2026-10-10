using System.Text;

namespace SSEPreview;

// Havok 2010 binary tagfile support. Some animation tools save Skyrim SE clips as tagfiles
// (magic CA B0 0D 1E / D0 11 FA CE) rather than packfiles; the game loads both, the engine
// library only parses packfiles. This reader decodes the self-describing tagfile object graph
// and re-serialises it as an hk_2010.2.0-r1 x64 packfile in memory, so every downstream
// consumer (clip catalog, pose import, annotations) keeps working on one format.
//
// Format facts were reverse engineered from the shipped files, not taken from an SDK:
//   packed int   bit0 sign, bits1-6 value, bit7 continue; later bytes 7 value bits each
//   string       packed length then bytes; negative length -n refers to string n-2 read earlier
//   tags         1 file info, 2 type, 3 object, 4 object with id, 7 end
//   type         name, version, parent index, members (name, flags, tuple size, class name)
//   member flags low nibble: 0 void 1 byte 2 int 3 real 4 vec4 5 vec8 6 vec12 7 vec16
//                8 object 9 struct 10 cstring; 0x10 array, 0x20 tuple
//   object       type index, bitfield of present members (flattened, parents first), values
//   int array    count, packed width, packed ints; struct array: bitfield once, then columns
//   vec4 array   count, packed component count (3 in shipped files), floats; vec12 arrays have
//                no header and 48 bytes per element; scalar vec4 is 16 bytes
//   references   packed object id; bodies follow in id order
public static class HkxTagfile
{
    static readonly byte[] Magic=[0x1e,0x0d,0xb0,0xca,0xce,0xfa,0x11,0xd0];
    public static bool IsTagfile(byte[] bytes)=>bytes.Length>8&&bytes.AsSpan(0,8).SequenceEqual(Magic);
    // Returns packfile bytes unchanged, or the converted packfile for a tagfile.
    public static byte[] EnsurePackfile(byte[] bytes)=>IsTagfile(bytes)?ToPackfile(bytes):bytes;

    sealed record Member(string Name,int Flags,int Tuple,string? Class);
    sealed class TypeDef{public string Name="";public int Version,Parent;public List<Member> Members=[];public List<Member>? Flat;}
    sealed record Ref(int Id);
    sealed class Obj{public TypeDef Type=null!;public readonly Dictionary<string,object?> Values=[];public int Id;}

    sealed class Reader
    {
        readonly byte[] d;int pos=8;readonly List<string> strings=[];
        public readonly List<TypeDef> Types=[new TypeDef()];
        public readonly Dictionary<int,Obj> Objects=[];public Obj? Root;int nextId=1;
        public Reader(byte[] data){d=data;}
        long Int()
        {
            if(pos>=d.Length)throw new InvalidDataException("Tagfile ends inside a value.");
            byte b=d[pos++];bool neg=(b&1)!=0;long v=(b>>1)&0x3f;
            if((b&0x80)!=0){int shift=6;do{if(pos>=d.Length)throw new InvalidDataException("Tagfile ends inside a packed integer.");b=d[pos++];v|=(long)(b&0x7f)<<shift;shift+=7;}while((b&0x80)!=0);}
            return neg?-v:v;
        }
        string? Str()
        {
            long n=Int();
            if(n<0)return n==-1?null:strings[checked((int)(-n-2))];
            if(pos+n>d.Length)throw new InvalidDataException("Tagfile string runs past the end.");
            string s=Encoding.Latin1.GetString(d,pos,(int)n);pos+=(int)n;strings.Add(s);return s;
        }
        float Real(){if(pos+4>d.Length)throw new InvalidDataException("Tagfile ends inside a float.");float v=BitConverter.ToSingle(d,pos);pos+=4;return v;}
        List<Member> Flat(TypeDef t)=>t.Flat??=(t.Parent>0?Flat(Types[t.Parent]):[]).Concat(t.Members).ToList();
        ulong Bits(int count){int bytes=(count+7)/8;ulong bits=0;for(int i=0;i<bytes;i++)bits|=(ulong)d[pos++]<<(8*i);return bits;}
        TypeDef TypeNamed(string name)=>Types.FirstOrDefault(t=>t.Name==name)??throw new InvalidDataException("Tagfile references undeclared type "+name);
        public void Parse()
        {
            while(true){
                long tag=Int();
                switch(tag){
                    case 1:Int();break;
                    case 2:{
                        var t=new TypeDef{Name=Str()??""};t.Version=(int)Int();t.Parent=(int)Int();int n=(int)Int();
                        for(int i=0;i<n;i++){string name=Str()??"";int flags=(int)Int();int tuple=(flags&0x20)!=0?(int)Int():0;string? cls=(flags&0xF) is 8 or 9?Str():null;t.Members.Add(new(name,flags,tuple,cls));}
                        Types.Add(t);break;
                    }
                    case 3:case 4:{
                        int type=(int)Int();var obj=Struct(Types[type]);
                        if(tag==4){obj.Id=nextId++;Objects[obj.Id]=obj;}
                        Root??=obj;break;
                    }
                    case 7:return;
                    default:throw new InvalidDataException($"Unsupported tagfile tag {tag} at offset {pos}.");
                }
            }
        }
        Obj Struct(TypeDef t)
        {
            var obj=new Obj{Type=t};var members=Flat(t);ulong bits=Bits(members.Count);
            for(int i=0;i<members.Count;i++)if((bits>>i&1)!=0)obj.Values[members[i].Name]=Value(members[i]);
            return obj;
        }
        object? Value(Member m)
        {
            int kind=m.Flags&0xF;
            if((m.Flags&0x10)!=0)return Array(kind,(int)Int(),m.Class);
            if((m.Flags&0x20)!=0)return Array(kind,m.Tuple,m.Class);
            return Scalar(kind,m.Class);
        }
        object? Scalar(int kind,string? cls)=>kind switch{
            0=>null,1=>(long)d[pos++],2=>Int(),3=>Real(),
            4=>Floats(4),5=>Floats(8),6=>Floats(12),7=>Floats(16),
            8=>new Ref((int)Int()),9=>Struct(TypeNamed(cls!)),10=>Str(),
            _=>throw new InvalidDataException("Unsupported tagfile member kind "+kind)};
        float[] Floats(int n){var v=new float[n];for(int i=0;i<n;i++)v[i]=Real();return v;}
        object Array(int kind,int n,string? cls)
        {
            switch(kind){
                case 1:{var v=new byte[n];System.Array.Copy(d,pos,v,0,n);pos+=n;return v;}
                case 2:{Int();var v=new long[n];for(int i=0;i<n;i++)v[i]=Int();return v;}
                case 3:return Floats(n);
                case 4:{int components=(int)Int();if(components is<1 or>4)throw new InvalidDataException("Tagfile vector array declares "+components+" components.");var v=new float[n*4];for(int i=0;i<n;i++)for(int c=0;c<components;c++)v[i*4+c]=Real();return v;}
                case 5:return Floats(n*8);case 6:return Floats(n*12);case 7:return Floats(n*16);
                case 8:{var v=new Ref[n];for(int i=0;i<n;i++)v[i]=new Ref((int)Int());return v;}
                case 10:{var v=new string?[n];for(int i=0;i<n;i++)v[i]=Str();return v;}
                case 9:{
                    var t=TypeNamed(cls!);var members=Flat(t);ulong bits=Bits(members.Count);
                    var items=new Obj[n];for(int i=0;i<n;i++)items[i]=new Obj{Type=t};
                    for(int k=0;k<members.Count;k++)if((bits>>k&1)!=0)for(int i=0;i<n;i++)items[i].Values[members[k].Name]=Value(members[k]);
                    return items;
                }
                default:throw new InvalidDataException("Unsupported tagfile array kind "+kind);
            }
        }
    }

    // SSE (hk_2010.2.0-r1, 64-bit) class layouts, copied from the engine's reflection-derived table:
    // member,offset,type,subtype,count,class,flags,width,elementWidth.
    sealed record Field(string Name,int Offset,string Type,string Subtype,string Class,int Width,int ElementWidth);
    sealed class Layout{public string Name="";public string Parent="";public int Size;public uint Signature;public List<Field> Fields=[];}
    static readonly Dictionary<string,Layout> Layouts=Parse([
        "hkBaseObject||8|0|",
        "hkReferencedObject|hkBaseObject|10|0|memSizeAndFlags,8,uint16,,0,,400,2,2;referenceCount,A,int16,,0,,400,2,2",
        "hkRootLevelContainer||10|0|namedVariants,0,array,struct,0,hkRootLevelContainerNamedVariant,0,10,18",
        "hkRootLevelContainerNamedVariant||18|0|name,0,stringptr,,0,,0,8,8;className,8,stringptr,,0,,0,8,8;variant,10,pointer,struct,0,hkReferencedObject,0,8,8",
        "hkaAnimationContainer|hkReferencedObject|60|1|skeletons,10,array,pointer,0,hkaSkeleton,0,10,8;animations,20,array,pointer,0,hkaAnimation,0,10,8;bindings,30,array,pointer,0,hkaAnimationBinding,0,10,8;attachments,40,array,pointer,0,hkaBoneAttachment,0,10,8;skins,50,array,pointer,0,hkaMeshBinding,0,10,8",
        "hkaAnimation|hkReferencedObject|38|1|type,10,enum,int32,0,,0,4,4;duration,14,real,,0,,0,4,4;numberOfTransformTracks,18,int32,,0,,0,4,4;numberOfFloatTracks,1C,int32,,0,,0,4,4;extractedMotion,20,pointer,struct,0,hkaAnimatedReferenceFrame,0,8,8;annotationTracks,28,array,struct,0,hkaAnnotationTrack,0,10,18",
        "hkaInterleavedUncompressedAnimation|hkaAnimation|58|0|transforms,38,array,qstransform,0,,0,10,30;floats,48,array,real,0,,0,10,4",
        "hkaAnimationBinding|hkReferencedObject|48|1|originalSkeletonName,10,stringptr,,0,,0,8,8;animation,18,pointer,struct,0,hkaAnimation,0,8,8;transformTrackToBoneIndices,20,array,int16,0,,0,10,2;floatTrackToFloatSlotIndices,30,array,int16,0,,0,10,2;blendHint,40,enum,int8,0,,0,1,1",
        "hkaAnnotationTrack||18|0|trackName,0,stringptr,,0,,0,8,8;annotations,8,array,struct,0,hkaAnnotationTrackAnnotation,0,10,10",
        "hkaAnnotationTrackAnnotation||10|0|time,0,real,,0,,0,4,4;text,8,stringptr,,0,,0,8,8",
        "hkaSkeleton|hkReferencedObject|78|3|name,10,stringptr,,0,,0,8,8;parentIndices,18,array,int16,0,,0,10,2;bones,28,array,struct,0,hkaBone,0,10,10;referencePose,38,array,qstransform,0,,0,10,30;referenceFloats,48,array,real,0,,0,10,4;floatSlots,58,array,stringptr,0,,0,10,8;localFrames,68,array,struct,0,hkaSkeletonLocalFrameOnBone,0,10,10",
        "hkaBone||10|0|name,0,stringptr,,0,,0,8,8;lockTranslation,8,bool,,0,,0,1,1",
        "hkaAnimatedReferenceFrame|hkReferencedObject|10|0|",
        "hkaDefaultAnimatedReferenceFrame|hkaAnimatedReferenceFrame|50|0|up,10,vector4,,0,,0,10,10;forward,20,vector4,,0,,0,10,10;duration,30,real,,0,,0,4,4;referenceFrameSamples,38,array,vector4,0,,0,10,10",
        "hkaSplineCompressedAnimation|hkaAnimation|B0|0|numFrames,38,int32,,0,,0,4,4;numBlocks,3C,int32,,0,,0,4,4;maxFramesPerBlock,40,int32,,0,,0,4,4;maskAndQuantizationSize,44,int32,,0,,0,4,4;blockDuration,48,real,,0,,0,4,4;blockInverseDuration,4C,real,,0,,0,4,4;frameDuration,50,real,,0,,0,4,4;blockOffsets,58,array,uint32,0,,0,10,4;floatBlockOffsets,68,array,uint32,0,,0,10,4;transformOffsets,78,array,uint32,0,,0,10,4;floatOffsets,88,array,uint32,0,,0,10,4;data,98,array,uint8,0,,0,10,1;endian,A8,int32,,0,,0,4,4",
    ]);
    // Class signatures as written by the game's own packfiles (from vanilla and mod files).
    static readonly Dictionary<string,uint> Signatures=new(){
        ["hkClass"]=0x75585ef6,["hkClassMember"]=0x5c7ea4c2,["hkClassEnum"]=0x8a3609cf,["hkClassEnumItem"]=0xce6f8a6c,
        ["hkRootLevelContainer"]=0x2772c11e,["hkaAnimationContainer"]=0x8dc20333,["hkaInterleavedUncompressedAnimation"]=0x930af031,
        ["hkaAnimationBinding"]=0x66eac971,["hkaDefaultAnimatedReferenceFrame"]=0x6d85e445,["hkaSplineCompressedAnimation"]=0x792ee0bb,
    };
    static Dictionary<string,Layout> Parse(string[] rows)
    {
        var result=new Dictionary<string,Layout>();
        foreach(var row in rows){
            var parts=row.Split('|');var layout=new Layout{Name=parts[0],Parent=parts[1],Size=Convert.ToInt32(parts[2],16)};
            foreach(var field in parts[4].Split(';',StringSplitOptions.RemoveEmptyEntries)){
                var f=field.Split(',');layout.Fields.Add(new(f[0],Convert.ToInt32(f[1],16),f[2],f[3],f[5],Convert.ToInt32(f[7],16),f[8].Length>0?Convert.ToInt32(f[8],16):0));
            }
            result[layout.Name]=layout;
        }
        return result;
    }
    static IEnumerable<Field> AllFields(Layout l)=>(l.Parent.Length>0?AllFields(Layouts[l.Parent]):[]).Concat(l.Fields);

    sealed class Writer
    {
        readonly Reader source;byte[] data=new byte[4096];int length;
        readonly List<(int Src,int Dst)> local=[];readonly List<(int Src,int Id)> global=[];readonly List<(int Offset,string Class)> virt=[];
        readonly Dictionary<int,int> placed=[];readonly Queue<Obj> pending=new();readonly Dictionary<Obj,int> inlinePlacement=new(ReferenceEqualityComparer.Instance);
        public Writer(Reader r){source=r;}
        int Reserve(int size){int at=(length+15)&~15;Ensure(at+size);length=at+size;return at;}
        void Ensure(int size){if(size<=data.Length)return;int n=data.Length;while(n<size)n*=2;System.Array.Resize(ref data,n);}
        void Put(int at,ReadOnlySpan<byte> bytes){Ensure(at+bytes.Length);bytes.CopyTo(data.AsSpan(at));}
        void Put32(int at,int v)=>Put(at,BitConverter.GetBytes(v));
        void PutF(int at,float v)=>Put(at,BitConverter.GetBytes(v));
        Layout LayoutOf(Obj o)=>Layouts.TryGetValue(o.Type.Name,out var l)?l:throw new InvalidDataException("Tagfile class "+o.Type.Name+" is not supported by the packfile converter.");
        Obj Deref(Ref r)=>source.Objects.TryGetValue(r.Id,out var o)?o:throw new InvalidDataException("Tagfile references missing object "+r.Id);
        int Place(Obj o)
        {
            if(o.Id>0){if(!placed.ContainsKey(o.Id)){placed[o.Id]=-1;pending.Enqueue(o);}return o.Id;}
            // A struct written inline where the packfile wants a pointer gets its own block.
            if(!inlinePlacement.TryGetValue(o,out int id)){id=-(inlinePlacement.Count+1);inlinePlacement[o]=id;source.Objects[id]=o;placed[id]=-1;o.Id=id;pending.Enqueue(o);}
            return id;
        }
        public byte[] Build()
        {
            var root=source.Root??throw new InvalidDataException("Tagfile has no root object.");
            if(root.Type.Name!="hkRootLevelContainer")throw new InvalidDataException("Tagfile root is "+root.Type.Name+", not hkRootLevelContainer.");
            Place(root);
            while(pending.Count>0){var o=pending.Dequeue();var layout=LayoutOf(o);int at=Reserve(layout.Size);placed[o.Id]=at;virt.Add((at,layout.Name));WriteFields(o,layout,at);}
            length=(length+15)&~15;
            var classes=new List<string>{"hkClass","hkClassMember","hkClassEnum","hkClassEnumItem"};
            foreach(var v in virt)if(!classes.Contains(v.Class))classes.Add(v.Class);
            var names=new MemoryStream();var entryOffsets=new Dictionary<string,int>();
            foreach(var cls in classes){
                if(!Signatures.TryGetValue(cls,out uint signature))throw new InvalidDataException("No packfile class signature is known for "+cls+".");
                entryOffsets[cls]=(int)names.Length;names.Write(BitConverter.GetBytes(signature));names.WriteByte(9);names.Write(Encoding.ASCII.GetBytes(cls));names.WriteByte(0);
            }
            while(names.Length%16!=0)names.WriteByte(0xFF);
            var localBytes=new MemoryStream();foreach(var f in local.OrderBy(f=>f.Src)){localBytes.Write(BitConverter.GetBytes(f.Src));localBytes.Write(BitConverter.GetBytes(f.Dst));}
            Pad(localBytes);
            var globalBytes=new MemoryStream();foreach(var f in global.OrderBy(f=>f.Src)){globalBytes.Write(BitConverter.GetBytes(f.Src));globalBytes.Write(BitConverter.GetBytes(2));globalBytes.Write(BitConverter.GetBytes(placed[f.Id]));}
            Pad(globalBytes);
            var virtualBytes=new MemoryStream();foreach(var v in virt.OrderBy(v=>v.Offset)){virtualBytes.Write(BitConverter.GetBytes(v.Offset));virtualBytes.Write(BitConverter.GetBytes(0));virtualBytes.Write(BitConverter.GetBytes(entryOffsets[v.Class]));}
            Pad(virtualBytes);
            const int headerSize=64,sectionHeaderSize=48;int classNamesStart=headerSize+3*sectionHeaderSize;int dataStart=classNamesStart+(int)names.Length;
            var file=new MemoryStream();
            file.Write(BitConverter.GetBytes(0x57e0e057u));file.Write(BitConverter.GetBytes(0x10c0c010u));file.Write(BitConverter.GetBytes(0));file.Write(BitConverter.GetBytes(8));
            file.Write([8,1,0,1]);file.Write(BitConverter.GetBytes(3));file.Write(BitConverter.GetBytes(2));file.Write(BitConverter.GetBytes(0));
            file.Write(BitConverter.GetBytes(0));file.Write(BitConverter.GetBytes(entryOffsets["hkRootLevelContainer"]+5));
            var version=new byte[16];Encoding.ASCII.GetBytes("hk_2010.2.0-r1").CopyTo(version,0);version[15]=0xFF;file.Write(version);
            file.Write(BitConverter.GetBytes(0));file.Write(BitConverter.GetBytes(-1));
            void Section(string tag,int start,int[] offsets){var name=new byte[20];Encoding.ASCII.GetBytes(tag).CopyTo(name,0);name[19]=0xFF;file.Write(name);file.Write(BitConverter.GetBytes(start));foreach(int o in offsets)file.Write(BitConverter.GetBytes(o));}
            int n=(int)names.Length;Section("__classnames__",classNamesStart,[n,n,n,n,n,n]);
            Section("__types__",dataStart,[0,0,0,0,0,0]);
            int localAt=length,globalAt=localAt+(int)localBytes.Length,virtualAt=globalAt+(int)globalBytes.Length,end=virtualAt+(int)virtualBytes.Length;
            Section("__data__",dataStart,[localAt,globalAt,virtualAt,end,end,end]);
            file.Write(names.ToArray());file.Write(data,0,length);file.Write(localBytes.ToArray());file.Write(globalBytes.ToArray());file.Write(virtualBytes.ToArray());
            return file.ToArray();
        }
        static void Pad(MemoryStream s){while(s.Length%16!=0)s.WriteByte(0xFF);}
        void WriteFields(Obj o,Layout layout,int at)
        {
            var fields=AllFields(layout).ToList();
            foreach(var (name,value) in o.Values){
                if(value==null)continue;
                var field=fields.FirstOrDefault(f=>f.Name==name);
                if(field==null){if(name is "memSizeAndFlags" or "referenceCount")continue;throw new InvalidDataException($"Tagfile member {o.Type.Name}.{name} has no packfile layout.");}
                WriteField(field,value,at+field.Offset);
            }
        }
        void WriteField(Field f,object value,int at)
        {
            switch(f.Type){
                case "bool":case "int8":case "uint8":data[at]=(byte)ToLong(value);break;
                case "int16":case "uint16":Put(at,BitConverter.GetBytes((short)ToLong(value)));break;
                case "int32":case "uint32":Put32(at,(int)ToLong(value));break;
                case "enum":if(f.Subtype=="int8")data[at]=(byte)ToLong(value);else if(f.Subtype=="int16")Put(at,BitConverter.GetBytes((short)ToLong(value)));else Put32(at,(int)ToLong(value));break;
                case "real":PutF(at,Convert.ToSingle(value));break;
                case "vector4":case "quaternion":{var v=(float[])value;for(int i=0;i<4;i++)PutF(at+4*i,i<v.Length?v[i]:0);break;}
                case "qstransform":{var v=(float[])value;for(int i=0;i<12;i++)PutF(at+4*i,v[i]);break;}
                case "stringptr":{if(value is string s){var bytes=Encoding.Latin1.GetBytes(s+"\0");int blob=Reserve(bytes.Length);Put(blob,bytes);local.Add((at,blob));}break;}
                case "pointer":{var target=value is Ref r?Deref(r):(Obj)value;global.Add((at,Place(target)));break;}
                case "array":WriteArray(f,value,at);break;
                default:throw new InvalidDataException("Unsupported packfile field type "+f.Type+" for "+f.Name);
            }
        }
        static long ToLong(object v)=>v switch{long l=>l,byte b=>b,float fl=>(long)fl,_=>Convert.ToInt64(v)};
        void WriteArray(Field f,object value,int at)
        {
            int count;int width=f.ElementWidth;
            int blob;
            switch(f.Subtype){
                case "int16":case "int32":case "int8":case "uint8":case "uint16":case "uint32":{
                    var v=value is long[] l?l:value is byte[] b?b.Select(x=>(long)x).ToArray():throw new InvalidDataException("Array of "+f.Subtype+" expects integers in "+f.Name);
                    count=v.Length;blob=Reserve(count*width);
                    for(int i=0;i<count;i++){if(width==1)data[blob+i]=(byte)v[i];else if(width==2)Put(blob+2*i,BitConverter.GetBytes((short)v[i]));else Put32(blob+4*i,(int)v[i]);}
                    break;
                }
                case "real":{var v=(float[])value;count=v.Length;blob=Reserve(count*4);for(int i=0;i<count;i++)PutF(blob+4*i,v[i]);break;}
                case "vector4":case "quaternion":{var v=(float[])value;count=v.Length/4;blob=Reserve(count*16);for(int i=0;i<count*4;i++)PutF(blob+4*i,v[i]);break;}
                case "qstransform":{var v=(float[])value;count=v.Length/12;blob=Reserve(count*48);for(int i=0;i<count*12;i++)PutF(blob+4*i,v[i]);break;}
                case "stringptr":{var v=(string?[])value;count=v.Length;blob=Reserve(count*8);for(int i=0;i<count;i++)if(v[i]!=null)WriteField(new("",0,"stringptr","","",8,8),v[i]!,blob+8*i);break;}
                case "pointer":{var v=(Ref[])value;count=v.Length;blob=Reserve(count*8);for(int i=0;i<count;i++)global.Add((blob+8*i,Place(Deref(v[i]))));break;}
                case "struct":{
                    var items=value switch{Obj[] o=>o,Ref[] refs=>refs.Select(Deref).ToArray(),_=>throw new InvalidDataException("Struct array "+f.Name+" holds an unexpected value")};
                    var layout=Layouts.TryGetValue(f.Class,out var l)?l:throw new InvalidDataException("Struct "+f.Class+" has no packfile layout.");
                    count=items.Length;blob=Reserve(count*layout.Size);
                    for(int i=0;i<count;i++)WriteFields(items[i],layout,blob+i*layout.Size);
                    break;
                }
                default:throw new InvalidDataException("Unsupported packfile array subtype "+f.Subtype+" for "+f.Name);
            }
            if(count>0)local.Add((at,blob));
            Put32(at+8,count);Put32(at+12,unchecked((int)(0x80000000u|(uint)count)));
        }
    }

    public static byte[] ToPackfile(byte[] tagfile)
    {
        if(!IsTagfile(tagfile))throw new InvalidDataException("Not a Havok binary tagfile.");
        var reader=new Reader(tagfile);reader.Parse();
        return new Writer(reader).Build();
    }
    // Class names the tagfile declares that the converter would need; used for diagnostics.
    public static string Describe(byte[] tagfile)
    {
        var reader=new Reader(tagfile);reader.Parse();
        return string.Join(", ",reader.Objects.Values.Where(o=>o.Id>0).Select(o=>o.Type.Name).Distinct());
    }
}
