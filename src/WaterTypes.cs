using System.Buffers.Binary;

namespace SSEPreview;

/// <summary>One WATR record: the look Skyrim's water shader reads (TESWaterForm / WaterShaderData, DNAM).</summary>
public sealed class WaterType
{
    public string EditorId="";
    public string Plugin="";
    /// <summary>ANAM opacity, 0..1. Used only by water without the DEPTH shader flag.</summary>
    public float Alpha=.5f;
    /// <summary>FNAM: 1 causes damage, 8 enable flowmap, 16 blend normals.</summary>
    public byte Flags;
    public bool Flowmap=>(Flags&8)!=0;
    public bool BlendNormals=>(Flags&16)!=0;
    /// <summary>NAM2..NAM4 noise (normal) layers and NAM5 flow normals, as game paths ("textures\water\...").</summary>
    public string[] NoiseTextures=["","","",""];
    public float SunSpecularPower=1021,ReflectionAmount=.8f,FresnelAmount=.1f,FogNear=-10,FogFar=150;
    /// <summary>sRGB colours, 0..1.</summary>
    public (float R,float G,float B) Shallow=(38/255f,39/255f,24/255f),Deep=(5/255f,14/255f,18/255f),Reflection=(119/255f,140/255f,157/255f);
    public float NoiseFalloff=4007;
    public float[] WindDirection=[270,210,225],WindSpeed=[.019f,.013f,.096f],UvScale=[1920,6703,488],Amplitude=[.6957f,.6304f,.4746f];
    public float FogAmount=.7f,RefractionMagnitude=40,SpecularPower=543,SpecularRadius=2148,SpecularBrightness=46;
    public float ReflectionMagnitude=.42f,SunSparkleMagnitude=2.89f,SunSpecularMagnitude=4.65f,SunSparklePower=3521,FlowmapScale=1;
    /// <summary>DepthControl: how much reflections, refraction, normals and specular fade toward the shore.</summary>
    public float[] DepthControl=[.3f,.95f,.1f,.9f];

    /// <summary>Update.esm's DefaultWater, used when no plugin supplies a water type.</summary>
    public static WaterType Default=>new(){EditorId="DefaultWater",Plugin="built-in"};
}

/// <summary>Reads WATR records from plugins. A plugin is streamed: only its WATR group is read; every other top-level
/// group is skipped by its size, so scanning a large load order costs little.</summary>
public sealed class WaterTypes
{
    readonly Dictionary<string,WaterType> byEditorId=new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Diagnostics=[];
    public int Count=>byEditorId.Count;
    public IEnumerable<string> PluginsRead=>pluginsRead;
    readonly List<string> pluginsRead=[];

    static readonly string[] OfficialMasters=["Skyrim.esm","Update.esm","Dawnguard.esm","HearthFires.esm","Dragonborn.esm"];

    /// <summary>Plugin files in a folder, official masters first in their load order, then .esm, .esl and .esp by name.</summary>
    public static IEnumerable<string> PluginsIn(string folder)
    {
        if(!Directory.Exists(folder))yield break;
        var files=Directory.EnumerateFiles(folder,"*.*",SearchOption.TopDirectoryOnly)
            .Where(p=>Path.GetExtension(p).ToLowerInvariant() is ".esm" or ".esp" or ".esl").ToList();
        foreach(var master in OfficialMasters){var hit=files.FirstOrDefault(f=>Path.GetFileName(f).Equals(master,StringComparison.OrdinalIgnoreCase));if(hit!=null){files.Remove(hit);yield return hit;}}
        int Rank(string f)=>Path.GetExtension(f).ToLowerInvariant() switch{".esm"=>0,".esl"=>1,_=>2};
        foreach(var f in files.OrderBy(Rank).ThenBy(f=>Path.GetFileName(f),StringComparer.OrdinalIgnoreCase))yield return f;
    }

    /// <summary>Reads the plugins in order; a later plugin's record with the same EditorID replaces the earlier one.</summary>
    public void Load(IEnumerable<string> pluginFiles)
    {
        foreach(var file in pluginFiles){
            string plugin=Path.GetFileName(file);
            try{
                PluginReader.Scan(file,new HashSet<string>{"WATR"},(_,_,body,_)=>{var water=Parse(body,plugin);if(water.EditorId.Length>0)byEditorId[water.EditorId]=water;});
                pluginsRead.Add(plugin);
            }catch(Exception ex){Diagnostics.Add($"water types: {plugin}: {ex.Message}");}
        }
    }

    public WaterType? Find(string editorId)=>byEditorId.GetValueOrDefault(editorId);

    static string Text(ReadOnlySpan<byte> b)=>PluginReader.Text(b);
    /// <summary>"Data\Textures\Water\X.dds" as stored in WATR, as a game path.</summary>
    static string GamePath(string path)=>PluginReader.GamePath(path,"textures");

    static WaterType Parse(byte[] body,string plugin)
    {
        var w=new WaterType{Plugin=plugin};
        foreach(var (type,memory) in PluginReader.Subrecords(body)){
            var d=memory.Span;int n=d.Length;
            switch(type){
                case "EDID":w.EditorId=Text(d);break;
                case "ANAM":if(n>=1)w.Alpha=d[0]/100f;break;
                case "FNAM":if(n>=1)w.Flags=d[0];break;
                case "NAM2":w.NoiseTextures[0]=GamePath(Text(d));break;
                case "NAM3":w.NoiseTextures[1]=GamePath(Text(d));break;
                case "NAM4":w.NoiseTextures[2]=GamePath(Text(d));break;
                case "NAM5":w.NoiseTextures[3]=GamePath(Text(d));break;
                case "DNAM":ReadVisual(w,d);break;
            }
        }
        return w;
    }

    static void ReadVisual(WaterType w,ReadOnlySpan<byte> span)
    {
        byte[] d=span.ToArray();
        float F(int offset,float fallback)=>offset+4<=d.Length?BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(offset)):fallback;
        (float,float,float) C(int offset,(float,float,float) fallback)=>offset+3<=d.Length?(d[offset]/255f,d[offset+1]/255f,d[offset+2]/255f):fallback;
        w.SunSpecularPower=F(0x10,w.SunSpecularPower);w.ReflectionAmount=F(0x14,w.ReflectionAmount);w.FresnelAmount=F(0x18,w.FresnelAmount);
        w.FogNear=F(0x20,w.FogNear);w.FogFar=F(0x24,w.FogFar);
        w.Shallow=C(0x28,w.Shallow);w.Deep=C(0x2C,w.Deep);w.Reflection=C(0x30,w.Reflection);
        w.NoiseFalloff=F(0x60,w.NoiseFalloff);
        for(int k=0;k<3;k++){w.WindDirection[k]=F(0x64+4*k,w.WindDirection[k]);w.WindSpeed[k]=F(0x70+4*k,w.WindSpeed[k]);w.UvScale[k]=F(0xAC+4*k,w.UvScale[k]);w.Amplitude[k]=F(0xB8+4*k,w.Amplitude[k]);}
        w.FogAmount=F(0x84,w.FogAmount);w.RefractionMagnitude=F(0x98,w.RefractionMagnitude);w.SpecularPower=F(0x9C,w.SpecularPower);
        w.SpecularRadius=F(0xA4,w.SpecularRadius);w.SpecularBrightness=F(0xA8,w.SpecularBrightness);
        w.ReflectionMagnitude=F(0xC4,w.ReflectionMagnitude);w.SunSparkleMagnitude=F(0xC8,w.SunSparkleMagnitude);w.SunSpecularMagnitude=F(0xCC,w.SunSpecularMagnitude);
        for(int k=0;k<4;k++)w.DepthControl[k]=F(0xD0+4*k,w.DepthControl[k]);
        w.SunSparklePower=F(0xE0,w.SunSparklePower);w.FlowmapScale=F(0xE4,w.FlowmapScale);
    }
}
