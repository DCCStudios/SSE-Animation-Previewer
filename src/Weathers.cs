using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SSEPreview;

/// <summary>Streams records of chosen types out of a plugin: every top-level group of another type is skipped by its size,
/// so scanning a large load order costs little. Deleted records are skipped and compressed ones inflated.</summary>
public static class PluginReader
{
    /// <summary>Calls <paramref name="record"/> with (type, FormID as stored in the file, body, the plugin's masters) for each
    /// record in the top-level groups named in <paramref name="groups"/>. Returns the plugin's master list.</summary>
    public static List<string> Scan(string file,IReadOnlySet<string> groups,Action<string,uint,byte[],IReadOnlyList<string>> record)
    {
        using var s=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read,1<<16);
        var header=new byte[24];
        if(s.Read(header)!=24||Encoding.ASCII.GetString(header,0,4)!="TES4")throw new InvalidDataException("not a plugin");
        var tes4=new byte[BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4))];s.ReadExactly(tes4);
        var masters=Subrecords(tes4).Where(r=>r.Type=="MAST").Select(r=>Text(r.Data.Span)).ToList();
        while(s.Position+24<=s.Length){
            if(s.Read(header)!=24)break;
            uint size=BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if(Encoding.ASCII.GetString(header,0,4)!="GRUP"){s.Seek(size,SeekOrigin.Current);continue;}
            long end=s.Position-24+size;string label=Encoding.ASCII.GetString(header,8,4);
            if(!groups.Contains(label)){s.Seek(end,SeekOrigin.Begin);continue;}
            while(s.Position+24<=end){
                if(s.Read(header)!=24)break;
                string type=Encoding.ASCII.GetString(header,0,4);
                uint dataSize=BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)),flags=BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)),formId=BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                if(type=="GRUP"){s.Seek(dataSize-24,SeekOrigin.Current);continue;}
                var body=new byte[dataSize];s.ReadExactly(body);
                if(type!=label||(flags&0x20)!=0)continue;   // 0x20: deleted
                if((flags&0x40000)!=0){
                    using var z=new ZLibStream(new MemoryStream(body,4,body.Length-4),CompressionMode.Decompress);using var o=new MemoryStream();z.CopyTo(o);body=o.ToArray();
                }
                record(type,formId,body,masters);
            }
            s.Seek(end,SeekOrigin.Begin);
        }
        return masters;
    }

    /// <summary>A record body's subrecords, with XXXX (the size of the next oversized subrecord) applied.</summary>
    public static IEnumerable<(string Type,ReadOnlyMemory<byte> Data)> Subrecords(byte[] body)
    {
        int i=0;uint big=0;
        while(i+6<=body.Length){
            string type=Encoding.ASCII.GetString(body,i,4);int n=BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(i+4));i+=6;
            if(type=="XXXX"){big=BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i));i+=n;continue;}
            if(big!=0){n=(int)big;big=0;}
            if(i+n>body.Length)yield break;
            yield return(type,new ReadOnlyMemory<byte>(body,i,n));
            i+=n;
        }
    }

    public static string Text(ReadOnlySpan<byte> b){int zero=b.IndexOf((byte)0);return Encoding.Latin1.GetString(zero<0?b:b[..zero]);}

    /// <summary>A path stored in a record ("Data\Textures\Water\X.dds", "Sky\Dome.nif"), as a game path under
    /// <paramref name="folder"/> ("textures", "meshes").</summary>
    public static string GamePath(string path,string folder)
    {
        path=Assets.Normalize(path);
        if(path.StartsWith(@"data\",StringComparison.OrdinalIgnoreCase))path=path[5..];
        return path.Length==0||path.StartsWith(folder+@"\",StringComparison.OrdinalIgnoreCase)?path.ToLowerInvariant():(folder+@"\"+path).ToLowerInvariant();
    }

    /// <summary>The plugin that defines a FormID as stored in <paramref name="plugin"/>: the top byte indexes that plugin's
    /// master list, and an index past the list is the plugin itself. With the low 24 bits this names a record across plugins.</summary>
    public static (string Owner,uint ObjectId) Identity(uint formId,string plugin,IReadOnlyList<string> masters)
    {
        int index=(int)(formId>>24);
        return(index<masters.Count?masters[index]:plugin,formId&0xFFFFFF);
    }
}

/// <summary>The WTHR record parts the view capture uses (TESWeather in CommonLib): NAM0 colours, FNAM fog, TNAM sky
/// statics and DALC directional ambient.</summary>
public sealed class Weather
{
    public const int SkyUpper=0,FogNear=1,Ambient=3,Sunlight=4,Sun=5,Stars=6,SkyLower=7,Horizon=8,FogFar=12,SkyStatics=13,WaterMultiplier=14;
    public static readonly string[] TimeNames=["sunrise","day","sunset","night"];
    public string EditorId="",Plugin="";
    /// <summary>NAM0 [colour type, time] as sRGB 0..1; times are sunrise, day, sunset, night.</summary>
    public readonly (float R,float G,float B)[,] Colors=new (float,float,float)[17,4];
    public int ColorTypes;
    /// <summary>FNAM: day near, day far, night near, night far, day power, night power, day max, night max.</summary>
    public readonly float[] Fog=[0,100000,0,50000,1,1,1,1];
    /// <summary>TNAM sky statics with the masters of the plugin they were read from, for resolving them.</summary>
    public readonly List<uint> SkyStaticIds=[];
    public IReadOnlyList<string> Masters=[];
    /// <summary>DALC per time: the +Z (sky) and -Z (ground) directional ambient colours, sRGB 0..1; null when absent.</summary>
    public readonly ((float R,float G,float B) Up,(float R,float G,float B) Down)?[] DirectionalAmbient=new ((float,float,float),(float,float,float))?[4];

    /// <summary>A colour type at this time of day, from the four time weights.</summary>
    public (float R,float G,float B) Color(int type,IReadOnlyList<float> weights)
    {
        float r=0,g=0,b=0;
        for(int t=0;t<4;t++){var c=Colors[type,t];r+=c.R*weights[t];g+=c.G*weights[t];b+=c.B*weights[t];}
        return(r,g,b);
    }
}

/// <summary>The CLMT timing: sunrise and sunset begin/end, in hours (stored as tens of minutes in TNAM).</summary>
public sealed class Climate
{
    public string EditorId="",Plugin="";
    public double SunriseBegin=5.5,SunriseEnd=10,SunsetBegin=16,SunsetEnd=20.5;
    /// <summary>SkyrimClimate in Skyrim.esm (TNAM 33, 60, 96, 123), used when no climate is named.</summary>
    public static Climate Default=>new(){EditorId="SkyrimClimate",Plugin="built-in"};

    /// <summary>How much of each colour set (sunrise, day, sunset, night) applies at <paramref name="hour"/>: night until
    /// sunrise begins, night to sunrise to day across the sunrise window, day until sunset begins, day to sunset to night
    /// across the sunset window. This follows the editor's description of the four sets; the engine's exact curve is not
    /// read from the game.</summary>
    public float[] Weights(double hour)
    {
        hour=((hour%24)+24)%24;
        double riseMid=(SunriseBegin+SunriseEnd)/2,setMid=(SunsetBegin+SunsetEnd)/2;
        float[] w=new float[4];
        float T(double from,double to)=>(float)Math.Clamp((hour-from)/Math.Max(to-from,1e-6),0,1);
        if(hour<SunriseBegin||hour>=SunsetEnd)w[3]=1;
        else if(hour<riseMid){float t=T(SunriseBegin,riseMid);w[3]=1-t;w[0]=t;}
        else if(hour<SunriseEnd){float t=T(riseMid,SunriseEnd);w[0]=1-t;w[1]=t;}
        else if(hour<SunsetBegin)w[1]=1;
        else if(hour<setMid){float t=T(SunsetBegin,setMid);w[1]=1-t;w[2]=t;}
        else{float t=T(setMid,SunsetEnd);w[2]=1-t;w[3]=t;}
        return w;
    }

    /// <summary>Where the directional light comes from at <paramref name="hour"/>, as the preview light rig's angles
    /// (azimuth 0 = north, 90 = east; elevation above the horizon). By day the sun crosses from east through south to west
    /// between sunrise begin and sunset end; by night the moon light does the same over the night. The path and its height
    /// are estimates, not read from the game. Elevation stays at 15 degrees or more so the light still casts shadows.</summary>
    public (float AzimuthDeg,float ElevationDeg,bool Night) LightPosition(double hour)
    {
        hour=((hour%24)+24)%24;
        bool day=hour>=SunriseBegin&&hour<SunsetEnd;
        double length=day?SunsetEnd-SunriseBegin:24-(SunsetEnd-SunriseBegin);
        double since=day?hour-SunriseBegin:((hour-SunsetEnd)%24+24)%24;
        double f=Math.Clamp(since/Math.Max(length,1e-6),0,1);
        float azimuth=(float)(90+180*f),elevation=(float)Math.Max(15,(day?65:45)*Math.Sin(Math.PI*f));
        return(azimuth,elevation,!day);
    }

    internal static void Test(Action<bool,string> check)
    {
        var c=Default;
        bool Is(float[] w,float sunrise,float day,float sunset,float night)=>MathF.Abs(w[0]-sunrise)<1e-4f&&MathF.Abs(w[1]-day)<1e-4f&&MathF.Abs(w[2]-sunset)<1e-4f&&MathF.Abs(w[3]-night)<1e-4f;
        check(Is(c.Weights(0),0,0,0,1)&&Is(c.Weights(24),0,0,0,1),"Weather colours at midnight are the night set");
        check(Is(c.Weights(12),0,1,0,0),"Weather colours at noon are the day set");
        check(Is(c.Weights(7.75),1,0,0,0)&&Is(c.Weights(18.25),0,0,1,0),"Weather colours at the middle of sunrise and sunset are those sets alone");
        check(Is(c.Weights(6.625),.5f,0,0,.5f),"Weather colours blend night into sunrise across the first half of sunrise");
        check(Enumerable.Range(0,97).All(i=>MathF.Abs(c.Weights(i/4.0).Sum()-1)<1e-4f),"Weather colour weights sum to 1 at every hour");
        var noon=c.LightPosition(12);var midnight=c.LightPosition(0);
        check(!noon.Night&&noon.AzimuthDeg>150&&noon.AzimuthDeg<190&&noon.ElevationDeg>60&&midnight.Night&&midnight.ElevationDeg>=15,"Weather light: high southern sun at noon, moon light at midnight");
        check(PluginReader.Identity(0x01000804,"Stage.esp",["Skyrim.esm"])==("Stage.esp",0x804u)&&PluginReader.Identity(0x0000081A,"Stage.esp",["Skyrim.esm"])==("Skyrim.esm",0x81Au),"Plugin FormIDs resolve to their defining plugin through the master list");
    }
}

/// <summary>WTHR and CLMT records by EditorID, read from plugins in load order (a later plugin's record with the same
/// EditorID replaces the earlier one), and the sky statics a weather lists, resolved to their models.</summary>
public sealed class Weathers
{
    readonly Dictionary<string,Weather> weathers=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,Climate> climates=new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> files=[];
    public readonly List<string> Diagnostics=[];
    public IEnumerable<string> PluginsRead=>files.Select(Path.GetFileName)!;
    public int WeatherCount=>weathers.Count;

    public void Load(IEnumerable<string> pluginFiles)
    {
        foreach(var file in pluginFiles){
            string plugin=Path.GetFileName(file);
            try{
                PluginReader.Scan(file,new HashSet<string>{"WTHR","CLMT"},(type,_,body,masters)=>{
                    if(type=="WTHR"){var w=ParseWeather(body,plugin,masters);if(w.EditorId.Length>0)weathers[w.EditorId]=w;}
                    else{var c=ParseClimate(body,plugin);if(c.EditorId.Length>0)climates[c.EditorId]=c;}
                });
                files.Add(file);
            }catch(Exception ex){Diagnostics.Add($"weather: {plugin}: {ex.Message}");}
        }
    }

    public Weather? FindWeather(string editorId)=>weathers.GetValueOrDefault(editorId);
    public Climate? FindClimate(string editorId)=>climates.GetValueOrDefault(editorId);

    /// <summary>The weather's TNAM sky statics, each with its STAT model (meshes\...) or null and a note on why. Every plugin
    /// read is scanned for STAT records; a later plugin's override of the same record wins.</summary>
    public List<(string Id,string? Model,string Note)> ResolveSkyStatics(Weather weather)
    {
        var wanted=new Dictionary<(string,uint),string?>();
        var order=weather.SkyStaticIds.Select(id=>PluginReader.Identity(id,weather.Plugin,weather.Masters)).Select(k=>(k.Owner.ToLowerInvariant(),k.ObjectId)).ToList();
        foreach(var key in order)wanted[key]=null;
        var known=new HashSet<string>(files.Select(f=>Path.GetFileName(f).ToLowerInvariant()));
        foreach(var file in files){
            string plugin=Path.GetFileName(file);
            try{
                PluginReader.Scan(file,new HashSet<string>{"STAT"},(_,formId,body,masters)=>{
                    var (owner,objectId)=PluginReader.Identity(formId,plugin,masters);var key=(owner.ToLowerInvariant(),objectId);
                    if(!wanted.ContainsKey(key))return;
                    foreach(var (type,data) in PluginReader.Subrecords(body))if(type=="MODL"){wanted[key]=PluginReader.GamePath(PluginReader.Text(data.Span),"meshes");break;}
                });
            }catch(Exception ex){Diagnostics.Add($"sky statics: {plugin}: {ex.Message}");}
        }
        return order.Select(k=>{
            string id=$"{k.Item1}:{k.ObjectId:X6}";var model=wanted[k];
            string note=model!=null?"":known.Contains(k.Item1)?"no STAT with a model found for it":$"{k.Item1} is not among the plugins read";
            return(id,model,note);
        }).ToList();
    }

    static (float,float,float) Rgb(ReadOnlySpan<byte> d,int offset)=>(d[offset]/255f,d[offset+1]/255f,d[offset+2]/255f);

    static Weather ParseWeather(byte[] body,string plugin,IReadOnlyList<string> masters)
    {
        var w=new Weather{Plugin=plugin,Masters=masters};int dalc=0;
        foreach(var (type,memory) in PluginReader.Subrecords(body)){
            var d=memory.Span;
            switch(type){
                case "EDID":w.EditorId=PluginReader.Text(d);break;
                case "NAM0":
                    // [type][time] of 4-byte RGBA colours; older plugins have fewer types.
                    w.ColorTypes=Math.Min(17,d.Length/16);
                    for(int t=0;t<w.ColorTypes;t++)for(int time=0;time<4;time++)w.Colors[t,time]=Rgb(d,(t*4+time)*4);
                    break;
                case "FNAM":for(int i=0;i<8&&i*4+4<=d.Length;i++)w.Fog[i]=BinaryPrimitives.ReadSingleLittleEndian(d[(i*4)..]);break;
                case "TNAM":for(int i=0;i+4<=d.Length;i+=4)w.SkyStaticIds.Add(BinaryPrimitives.ReadUInt32LittleEndian(d[i..]));break;
                case "DALC":
                    // X+, X-, Y+, Y-, Z+, Z- (RGBA each), specular, fresnel power: one per time.
                    if(dalc<4&&d.Length>=24)w.DirectionalAmbient[dalc]=(Rgb(d,16),Rgb(d,20));
                    dalc++;break;
            }
        }
        return w;
    }

    static Climate ParseClimate(byte[] body,string plugin)
    {
        var c=new Climate{Plugin=plugin};
        foreach(var (type,memory) in PluginReader.Subrecords(body)){
            var d=memory.Span;
            if(type=="EDID")c.EditorId=PluginReader.Text(d);
            else if(type=="TNAM"&&d.Length>=4){c.SunriseBegin=d[0]/6.0;c.SunriseEnd=d[1]/6.0;c.SunsetBegin=d[2]/6.0;c.SunsetEnd=d[3]/6.0;}
        }
        return c;
    }
}
