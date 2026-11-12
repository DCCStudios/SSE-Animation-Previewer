using System.Globalization;
using System.Text.RegularExpressions;
using FO4_Base_Library;
using NiflySharp.Blocks;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SSEPreview;

/// <summary>One [[TrailDefinition]] from Precision's TOML files (Settings.cpp): priority, All (every condition must
/// pass) or Any, the conditions and the visuals. Only WeaponNames can be checked here; a definition with other
/// conditions is listed in <see cref="Unsupported"/>.</summary>
public sealed class TrailDefinition
{
    public string Source="";public int Priority;public bool All;
    public List<string>? WeaponNames;
    public List<string> Unsupported{get;}=[];
    public float? LifetimeMult,BaseColorScaleMult;public System.Numerics.Vector4? BaseColor;public string? Mesh;

    /// <summary>AttackTrail::GetTrailDefinition: an All definition needs every condition (names by case-sensitive
    /// contains); an Any definition needs one. Conditions this previewer cannot check count as failed.</summary>
    public bool Matches(string? weaponName)
    {
        if(All){
            if(Unsupported.Count>0)return false;
            if(WeaponNames!=null&&(weaponName==null||!WeaponNames.All(n=>weaponName.Contains(n,StringComparison.Ordinal))))return false;
            return true;
        }
        return WeaponNames!=null&&weaponName!=null&&WeaponNames.Any(n=>weaponName.Contains(n,StringComparison.Ordinal));
    }
}

/// <summary>Precision's trail definitions from every SKSE\Plugins\Precision\*.toml in the mounted data, sorted by priority
/// (higher first), All definitions searched before Any ones, as Precision does. A small reader for the subset these
/// files use: tables, [[arrays of tables]], strings, numbers, booleans, string arrays and inline tables.</summary>
public sealed class TrailDefinitions
{
    public List<TrailDefinition> Definitions{get;}=[];
    public List<string> Sources{get;}=[];
    public List<string> Problems{get;}=[];
    public static TrailDefinitions Load(Assets assets)
    {
        var result=new TrailDefinitions();
        foreach(string path in assets.Paths.Where(p=>p.StartsWith(@"skse\plugins\precision\",StringComparison.OrdinalIgnoreCase)&&p.EndsWith(".toml",StringComparison.OrdinalIgnoreCase)).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase)){
            try{result.Parse(assets.Text(path),Path.GetFileName(path));result.Sources.Add(path);}
            catch(Exception ex){result.Problems.Add($"{path}: {ex.Message}");}
        }
        // std::sort by priority, higher first; All before Any (two lists in Precision).
        var sorted=result.Definitions.Where(d=>d.All).OrderByDescending(d=>d.Priority).Concat(result.Definitions.Where(d=>!d.All).OrderByDescending(d=>d.Priority)).ToList();
        result.Definitions.Clear();result.Definitions.AddRange(sorted);
        return result;
    }
    public TrailDefinition? Find(string? weaponName)=>Definitions.FirstOrDefault(d=>d.Matches(weaponName));

    internal static void Test(Action<bool,string> check)
    {
        var set=new TrailDefinitions();
        set.Parse("""
            # comment
            [[TrailDefinition]]
                Priority = 5
                [TrailDefinition.Conditions]
                    WeaponNames = ["Buster Sword", "Fusion Sword"]
                [TrailDefinition.Visuals]
                    LifetimeMult = 2.0
                    BaseColorOverride = { r = 1, g = 0.5, b = 0.25, a = 1 }
                    BaseColorScaleMult = 3.0
                    TrailMeshOverride = "Effects/WeaponTrails/FF7/FF7Trail_Remake.nif"
            [[TrailDefinition]]
                Priority = 9
                All = true
                [TrailDefinition.Conditions]
                    WeaponKeywords = ["WeapTypeGreatsword"]
            """,".toml");
        var buster=set.Definitions.FirstOrDefault(d=>d.Priority==5);
        check(set.Definitions.Count==2&&buster!=null&&buster.WeaponNames!.Count==2&&buster.LifetimeMult==2&&buster.BaseColorScaleMult==3&&buster.BaseColor==new System.Numerics.Vector4(1,.5f,.25f,1)&&buster.Mesh=="Effects/WeaponTrails/FF7/FF7Trail_Remake.nif","Precision trail TOML: priority, weapon names and visuals read");
        check(buster!.Matches("Cloud's Buster Sword")&&!buster.Matches("buster sword")&&!set.Definitions.First(d=>d.All).Matches("Buster Sword"),"Precision trail TOML: names match by case-sensitive contains; an All definition with an unchecked condition never matches");
        var d=PrecisionDefinition.Parse("Node(WEAPON)|TrailLifetimeMult(1.5)|TrailBaseColorOverride(0.2, 0.4, 1, 0.8)|TrailBaseColorScaleMult(2)|TrailMeshOverride(Effects\\WeaponTrails\\FF7\\FF7Trail_Limit02.nif)",out _);
        check(d!=null&&d.HasTrailOverride&&d.TrailLifetimeMult==1.5f&&d.TrailBaseColor==new System.Numerics.Vector4(.2f,.4f,1,.8f)&&d.TrailBaseColorScaleMult==2&&d.TrailMesh==@"Effects\WeaponTrails\FF7\FF7Trail_Limit02.nif","Collision_Add Trail* parameters parse");
        // A blade sweeping at 6 rad/s; capsule 1.633-1.733 s, 31 bones, 0.251 s lifetime, 0.1 s fade, 240 rows a second.
        double dt=1.0/60;
        TrailSimulation.Bone? Blade(int f){double a=f*dt*6;return new(new OpenTK.Mathematics.Vector3d(0,0,0),new OpenTK.Mathematics.Vector3d(Math.Cos(a),Math.Sin(a),0));}
        var early=TrailSimulation.At(1.633,1.733,1.65,dt,31,.251f,.1f,240,Blade);
        var live=TrailSimulation.At(1.633,1.733,1.797,dt,31,.251f,.1f,240,Blade);
        var full=TrailSimulation.At(1.633,1.733,1.9,dt,31,.251f,.1f,240,Blade);
        var gone=TrailSimulation.At(1.633,1.733,2.1,dt,31,.251f,.1f,240,Blade);
        check(early!=null&&early.LiveRows==0&&live!=null&&live.LiveRows==28&&live.Visibility>1&&full!=null&&full.LiveRows==30&&gone==null,"Precision trail replay: four-frame warm-up, 4 rows a frame, the tail capped at bones - 1, gone after lifetime and fade");
    }

    void Parse(string text,string source)
    {
        TrailDefinition? current=null;string section="";
        var lines=text.Replace("\r","").Split('\n');
        for(int i=0;i<lines.Length;i++){
            string line=StripComment(lines[i]).Trim();if(line.Length==0)continue;
            if(line.StartsWith("[[")){section=line.Trim('[',']').Trim();current=section.Equals("TrailDefinition",StringComparison.OrdinalIgnoreCase)?new TrailDefinition{Source=source}:null;if(current!=null)Definitions.Add(current);continue;}
            if(line.StartsWith("[")){section=line.Trim('[',']').Trim();continue;}
            int eq=line.IndexOf('=');if(eq<0||current==null)continue;
            string key=line[..eq].Trim();string value=line[(eq+1)..].Trim();
            // Arrays can span lines.
            while(value.StartsWith("[")&&Count(value,'[')>Count(value,']')&&i+1<lines.Length)value+=" "+StripComment(lines[++i]).Trim();
            string table=section.Contains('.')?section[(section.IndexOf('.')+1)..]:"";
            if(table.Length==0){
                if(key=="Priority"&&int.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out int priority))current.Priority=priority;
                else if(key=="All")current.All=value.Equals("true",StringComparison.OrdinalIgnoreCase);
            }else if(table.Equals("Conditions",StringComparison.OrdinalIgnoreCase)){
                if(key=="WeaponNames")current.WeaponNames=Strings(value);else current.Unsupported.Add(key);
            }else if(table.Equals("Visuals",StringComparison.OrdinalIgnoreCase)){
                switch(key){
                    case "LifetimeMult":current.LifetimeMult=Number(value);break;
                    case "BaseColorScaleMult":current.BaseColorScaleMult=Number(value);break;
                    case "TrailMeshOverride":current.Mesh=Strings(value).FirstOrDefault()??value.Trim('"');break;
                    case "BaseColorOverride":{
                        float C(string name)=>Regex.Match(value,$@"\b{name}\s*=\s*(-?[0-9.]+)") is {Success:true} m?float.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture):0;
                        current.BaseColor=new(C("r"),C("g"),C("b"),C("a"));break;}
                }
            }
        }
    }
    static int Count(string s,char c)=>s.Count(x=>x==c);
    static float? Number(string v)=>float.TryParse(v,NumberStyles.Float,CultureInfo.InvariantCulture,out float f)?f:null;
    static List<string> Strings(string v)=>Regex.Matches(v,"\"((?:[^\"\\\\]|\\\\.)*)\"").Select(m=>m.Groups[1].Value.Replace("\\\\","\\")).ToList();
    static string StripComment(string line)
    {
        bool quoted=false;
        for(int i=0;i<line.Length;i++){if(line[i]=='"')quoted=!quoted;else if(line[i]=='#'&&!quoted)return line[..i];}
        return line;
    }
}

/// <summary>A Precision trail NIF: the effect-shader strip, read the way its template is built (FxNif TrailMaker notes:
/// one bone per row under TrailRoot, rows a step apart along -Z with the oldest at the top, two vertices per row at
/// x = 0 (blade base) and x = about 100 (tip), skinned one bone per row). Each vertex keeps its row and its x, which the
/// bone's scale (weapon length / 100) carries out along the blade.</summary>
public sealed class TrailMesh
{
    public string Path="";public int Rows;
    public float[] X=[];public int[] Row=[];public (int Row,float Weight)[][] Influences=[];public Vector2[] Uv=[];public Vector4[] Color=[];public int[] Indices=[];
    public string Texture="";public Vector4 BaseColor=Vector4.One;public float BaseColorScale=1;public uint AlphaFlags;public byte AlphaThreshold;
    public bool VertexColors,VertexAlpha;
    /// <summary>Which way the texture runs on the strip, for logs.</summary>
    public string UvLayout="";
    /// <summary>How vertices were assigned to bones, for logs.</summary>
    public string RowSource="";
    /// <summary>Shader flags and the effect-shader features the preview leaves out, for logs.</summary>
    public string ShaderInfo="";

    public static TrailMesh Load(Assets assets,string meshPath)
    {
        string path=Assets.Normalize(meshPath);
        if(!path.StartsWith(@"meshes\",StringComparison.OrdinalIgnoreCase))path=@"meshes\"+path;
        if(!assets.Exists(path))throw new FileNotFoundException("trail mesh not found: "+path);
        var nif=new Nifcontent_Class_Manolo();nif.Load_Manolo(assets.Read(path));
        BSTriShape? strip=null;BSEffectShaderProperty? shader=null;
        foreach(var shape in nif.Blocks.OfType<BSTriShape>()){
            if(shape.ShaderPropertyRef!=null&&shape.ShaderPropertyRef.Index>=0&&nif.Blocks[shape.ShaderPropertyRef.Index] is BSEffectShaderProperty s){strip=shape;shader=s;break;}
        }
        if(strip==null||shader==null)throw new InvalidDataException("no effect-shader strip in "+path);
        var vertices=strip.VertexDataSSE;
        if((vertices==null||vertices.Count==0)&&strip.SkinInstanceRef!=null&&strip.SkinInstanceRef.Index>=0&&nif.Blocks[strip.SkinInstanceRef.Index] is NiSkinInstance skin&&skin.SkinPartition!=null&&skin.SkinPartition.Index>=0&&nif.Blocks[skin.SkinPartition.Index] is NiSkinPartition partition)
            vertices=partition.VertexData;
        if(vertices==null||vertices.Count==0)throw new InvalidDataException("the strip in "+path+" has no vertex data");
        // Precision drives trailRootNode->children by index: bone i is TrailRoot's i-th child, and the trail has as many
        // bones as TrailRoot has children. A vertex's row is the child index of the bone with its largest skin weight.
        var mesh=new TrailMesh{Path=path};
        mesh.X=vertices.Select(v=>v.Vertex.X).ToArray();
        mesh.Row=SkinRows(nif,strip,vertices,out int rows,out mesh.RowSource,out mesh.Influences);
        if(mesh.Row.Length!=vertices.Count){
            // No usable skin: rows by height, oldest (highest z) first, as the template lays them out.
            var levels=vertices.Select(v=>MathF.Round(v.Vertex.Z*20)/20).Distinct().OrderByDescending(z=>z).ToList();
            mesh.Row=vertices.Select(v=>levels.IndexOf(MathF.Round(v.Vertex.Z*20)/20)).ToArray();rows=levels.Count;
            mesh.Influences=mesh.Row.Select(r=>new[]{(r,1f)}).ToArray();
        }
        mesh.Rows=rows;
        mesh.Uv=vertices.Select(v=>new Vector2((float)v.UV.U,(float)v.UV.V)).ToArray();
        mesh.Color=vertices.Select(v=>new Vector4(v.VertexColors.R/255f,v.VertexColors.G/255f,v.VertexColors.B/255f,v.VertexColors.A/255f)).ToArray();
        mesh.Indices=strip.Triangles.SelectMany(t=>new[]{(int)t[(ushort)0],(int)t[(ushort)1],(int)t[(ushort)2]}).ToArray();
        string texture=Assets.Normalize(shader.SourceTexture?.Content??"");
        if(texture.Length>0&&!texture.StartsWith(@"textures\",StringComparison.OrdinalIgnoreCase))texture=@"textures\"+texture;
        mesh.Texture=texture;
        mesh.BaseColor=new(shader.BaseColor.R,shader.BaseColor.G,shader.BaseColor.B,shader.BaseColor.A);mesh.BaseColorScale=shader.BaseColorScale;
        mesh.VertexColors=((uint)shader.ShaderFlags_SSPF2&(1u<<5))!=0;mesh.VertexAlpha=((uint)shader.ShaderFlags_SSPF1&(1u<<3))!=0;
        // Effect-shader features the strip uses and this preview does not draw, for logs.
        {
            uint f1=(uint)shader.ShaderFlags_SSPF1,f2=(uint)shader.ShaderFlags_SSPF2;var unmodelled=new List<string>();
            if((f1&(1u<<6))!=0)unmodelled.Add(FormattableString.Invariant($"falloff (angles {shader.FalloffStartAngle:0.##} to {shader.FalloffStopAngle:0.##}, opacity {shader.FalloffStartOpacity:0.##} to {shader.FalloffStopOpacity:0.##})"));
            if((f1&(1u<<15))!=0)unmodelled.Add(FormattableString.Invariant($"refraction (power {shader.RefractionPower:0.###}, normal {shader.NormalTexture?.Content})"));
            if((f1&(1u<<30))!=0)unmodelled.Add(FormattableString.Invariant($"soft effect (depth {shader.SoftFalloffDepth:0.#})"));
            if((f1&(1u<<4))!=0||(f1&(1u<<5))!=0)unmodelled.Add($"greyscale palette {shader.GreyscaleTexture?.Content}");
            mesh.ShaderInfo=FormattableString.Invariant($"shader flags {f1:X8} {f2:X8}")+(unmodelled.Count>0?"; not drawn: "+string.Join("; ",unmodelled):"");
        }
        if(strip.AlphaPropertyRef!=null&&strip.AlphaPropertyRef.Index>=0&&nif.Blocks[strip.AlphaPropertyRef.Index] is NiAlphaProperty alpha){mesh.AlphaFlags=alpha.Flags.Value;mesh.AlphaThreshold=alpha.Threshold;}
        mesh.ShaderInfo=$"alpha flags {mesh.AlphaFlags}, "+mesh.ShaderInfo;
        // Texture direction, for logs: Precision's template runs u from 0 at the oldest row to 1 at the blade, and v from 0 at
        // the tip to 1 at the base.
        if(mesh.Rows>1&&mesh.X.Length>0){
            float RowU(int r){var u=Enumerable.Range(0,mesh.X.Length).Where(i=>mesh.Row[i]==r).Select(i=>mesh.Uv[i].X).DefaultIfEmpty().Average();return u;}
            float maxX=mesh.X.Max(),minX=mesh.X.Min();
            float tipV=Enumerable.Range(0,mesh.X.Length).Where(i=>mesh.X[i]>=maxX-(maxX-minX)*.05f).Select(i=>mesh.Uv[i].Y).DefaultIfEmpty().Average();
            float baseV=Enumerable.Range(0,mesh.X.Length).Where(i=>mesh.X[i]<=minX+(maxX-minX)*.05f).Select(i=>mesh.Uv[i].Y).DefaultIfEmpty().Average();
            float oldU=RowU(0),newU=RowU(mesh.Rows-1);
            mesh.UvLayout=FormattableString.Invariant($"u {oldU:0.##} oldest to {newU:0.##} at the blade, v {tipV:0.##} tip to {baseV:0.##} base, x {minX:0.#} to {maxX:0.#}");
            if(newU<oldU||tipV>baseV)mesh.UvLayout+=" (WARNING: runs the other way from Precision's template)";
            mesh.UvLayout+=$", {mesh.Rows} bones ({mesh.RowSource})";
        }
        return mesh;
    }

    static int[] SkinRows(Nifcontent_Class_Manolo nif,BSTriShape strip,List<NiflySharp.Structs.BSVertexDataSSE> vertices,out int rows,out string source,out (int Row,float Weight)[][] influences)
    {
        rows=0;source="rows by height, no skin";influences=[];
        if(strip.SkinInstanceRef==null||strip.SkinInstanceRef.Index<0||nif.Blocks[strip.SkinInstanceRef.Index] is not NiSkinInstance skin)return [];
        var skinBones=skin.Bones.Indices.ToList();if(skinBones.Count==0){source=$"rows by height, skin lists no bones ({skin.NumBones} declared)";return [];}
        // AttackTrail.cpp: fadeNode->GetObjectByName("TrailRoot"); the parent of the skin bones only as a fallback.
        var root=nif.Blocks.OfType<NiNode>().FirstOrDefault(n=>string.Equals(n.Name?.String,"TrailRoot",StringComparison.OrdinalIgnoreCase))??nif.Blocks.OfType<NiNode>().FirstOrDefault(n=>n.Children.Indices.Contains(skinBones[0]));
        if(root==null){source="rows by height, skin bones have no parent";return [];}
        var children=root.Children.Indices.ToList();
        // SSE keeps one shared vertex buffer for all partitions, and a vertex can sit in several partitions with different
        // bone lists, so its bone indices are indices into the skin instance's bone list, not the partition's.
        var result=new int[vertices.Count];var blend=new (int Row,float Weight)[vertices.Count][];int shared=0;
        for(int i=0;i<vertices.Count;i++){
            var v=vertices[i];var list=new List<(int Row,float Weight)>();float total=0;int row=-1;float strongest=-1;
            for(int k=0;k<4;k++){
                float w=(float)v.BoneWeights[k];if(w<=1e-3f)continue;
                int index=v.BoneIndices[k];
                if(index>=skinBones.Count){source=$"rows by height, vertex {i} names skin bone {index} of {skinBones.Count}";return [];}
                int r=children.IndexOf(skinBones[index]);
                if(r<0){
                    string bone=nif.Blocks[skinBones[index]] is NiNode n?n.Name?.String??"":"";
                    source=$"rows by height, vertex {i} follows skin bone {index} \"{bone}\", which is not a child of \"{root.Name?.String}\"";return [];
                }
                list.Add((r,w));total+=w;if(w>strongest){strongest=w;row=r;}
            }
            if(row<0){source=$"rows by height, vertex {i} has no bone weights";return [];}
            result[i]=row;
            blend[i]=list.Select(x=>(x.Row,x.Weight/total)).ToArray();if(blend[i].Length>1)shared++;
        }
        rows=children.Count;
        string name=root.Name?.String??"";
        int used=result.Distinct().Count();influences=blend;
        source=FormattableString.Invariant($"children of {(name.Length>0?name:"the skin root")}, {used} carry vertices, {shared} of {vertices.Count} vertices blend two or more")+(used<children.Count?$" (WARNING: bone(s) {string.Join(",",Enumerable.Range(0,children.Count).Where(c=>!result.Contains(c)))} carry no vertices; bone(s) {string.Join(",",result.GroupBy(r=>r).Where(g=>g.Count()>2).Select(g=>$"{g.Key} has {g.Count()}"))} vertices)":"");
        return result;
    }
}

/// <summary>Precision's AttackTrail::Update replayed at a fixed game frame, so a trail at any time is reproducible
/// (scrubbing, captures). Each frame pushes the capsule node's transform to the history; once four are kept, the frame
/// adds uTrailSegmentsPerSecond x dt rows, Catmull-Rom splined between the last four history entries (position and a
/// point 50 units along the node's forward axis); rows older than the lifetime move out from the tail, and when rows
/// would outnumber the bones the tail is forced out; unused bones sit on the latest transform. When the capsule ends,
/// visibility jumps to 1 + lifetime / fade and drops by dt / fade a frame; the trail's alpha is its own times
/// min(visibility, 1), and it ends at zero visibility or with no rows left.</summary>
public static class TrailSimulation
{
    public readonly record struct Bone(Vector3d Origin,Vector3d Direction);
    public sealed record State(Bone[] Bones,float Visibility,int LiveRows);

    static Vector3d CatmullRom(Vector3d p0,Vector3d p1,Vector3d p2,Vector3d p3,double t)
    {
        double t2=t*t,t3=t2*t;
        return .5*((2*p1)+(-p0+p2)*t+(2*p0-5*p1+4*p2-p3)*t2+(-p0+3*p1-3*p2+p3)*t3);
    }

    /// <summary>The trail at <paramref name="time"/> for a capsule alive from <paramref name="start"/> to
    /// <paramref name="end"/>; null before it starts or after it has faded. <paramref name="blade"/> gives the capsule
    /// node's origin and forward axis at a game frame (frame index on the global dt grid).</summary>
    public static State? At(double start,double end,double time,double dt,int rows,float lifetime,float fade,int segmentsPerSecond,Func<int,Bone?> blade)
    {
        if(time<start-1e-9||rows<2)return null;
        int first=(int)Math.Ceiling(start/dt-1e-6),last=(int)Math.Floor(time/dt+1e-6);
        if(last<first)return null;
        var history=new List<Bone>();var bones=new Bone[rows];var stamps=new List<double>();
        int current=0;double currentTime=0,offset=0,remainder=0;float visibility=1;bool expired=false;
        for(int frame=first;frame<=last;frame++){
            double tk=frame*dt;
            if(!expired&&tk>=end-1e-9){expired=true;visibility=fade>0?visibility+lifetime/fade:0;}
            var b=blade(frame);if(b==null)return null;
            history.Add(b.Value);
            if(expired)visibility=fade>0?Math.Max(visibility-(float)(dt/fade),0):0;
            if(history.Count>=4){
                double add=remainder+dt*segmentsPerSecond;int trunc=(int)Math.Truncate(add);remainder=add-trunc;
                if(stamps.Count>0){
                    int move=0;
                    for(int i=0;i<current;i++)if(stamps.Count>i&&currentTime+offset>stamps[i]+lifetime)move++;
                    int total=current+trunc-move;
                    if(total>=rows){move+=total-(rows-1);int index=stamps.Count>move?move:stamps.Count-1;offset=stamps[index]+lifetime-currentTime;}
                    move=Math.Min(move,stamps.Count);
                    if(move>0){
                        stamps.RemoveRange(0,move);
                        for(int i=0;i<current;i++)if(rows>i+move)bones[i]=bones[i+move];
                        current-=move;
                    }
                }
                if(add>0){
                    var p3=history[^1];var p2=history[^2];var p1=history[^3];var p0=history[^4];
                    for(int i=0;i<trunc;i++){
                        if(rows<=current)break;
                        double t=(i+1.0)/add;
                        var pos=CatmullRom(p0.Origin,p1.Origin,p2.Origin,p3.Origin,t);
                        var tip=CatmullRom(p0.Origin+p0.Direction*50,p1.Origin+p1.Direction*50,p2.Origin+p2.Direction*50,p3.Origin+p3.Direction*50,t);
                        var dir=tip-pos;dir=dir.LengthSquared>1e-12?dir.Normalized():p2.Direction;
                        bones[current]=new Bone(pos,dir);stamps.Add(currentTime+dt*t);current++;
                    }
                }
                if(expired&&(current==0||visibility==0))return null;
                for(int i=current;i<rows;i++)bones[i]=history[^1];
            }else for(int i=0;i<rows;i++)bones[i]=history[^1];
            currentTime+=dt;
        }
        return new State(bones,visibility,current);
    }
}

/// <summary>Draws trail strips as BSEffectShader would with these NIFs: texture x vertex colour (when the shader flags
/// use it) x base colour x base colour scale, alpha = texture x vertex alpha x base alpha, blended by the strip's
/// NiAlphaProperty (additive 4109, alpha 4333), depth tested, no depth write, both sides. Display-space colour like the
/// engine's effect shaders. Not drawn: greyscale palettes, falloff, soft-particle depth fade.</summary>
public sealed class TrailRenderer(Assets assets):IDisposable
{
    readonly Dictionary<string,int> textures=new(StringComparer.OrdinalIgnoreCase);
    int program,vao,vbo;
    public readonly List<string> Diagnostics=[];
    public int LastDrawn{get;private set;}

    const string Vertex="""
        #version 330 core
        layout(location=0) in vec3 position;layout(location=1) in vec2 uv;layout(location=2) in vec4 color;
        uniform mat4 viewProjection;
        out vec2 vUv;out vec4 vColor;
        void main(){gl_Position=viewProjection*vec4(position,1.);vUv=uv;vColor=color;}
        """;
    const string Fragment="""
        #version 330 core
        in vec2 vUv;in vec4 vColor;
        uniform sampler2D baseTexture;uniform vec4 baseColor;uniform float baseScale;uniform int useVertexColor,useVertexAlpha;
        uniform float alphaThreshold;uniform int alphaFunction;
        out vec4 result;
        void main(){
            vec4 t=texture(baseTexture,vUv);
            vec3 rgb=t.rgb*baseColor.rgb*baseScale*(useVertexColor!=0?vColor.rgb:vec3(1.));
            float a=t.a*baseColor.a*(useVertexAlpha!=0?vColor.a:1.);
            bool pass=alphaFunction==0||(alphaFunction==1&&a<alphaThreshold)||(alphaFunction==2&&a==alphaThreshold)||(alphaFunction==3&&a<=alphaThreshold)
                ||(alphaFunction==4&&a>alphaThreshold)||(alphaFunction==5&&a!=alphaThreshold)||(alphaFunction==6&&a>=alphaThreshold);
            if(!pass)discard;
            result=vec4(rgb,a);
        }
        """;

    static int Compile(string vs,string fs)
    {
        int Stage(ShaderType type,string source){int s=GL.CreateShader(type);GL.ShaderSource(s,source);GL.CompileShader(s);GL.GetShader(s,ShaderParameter.CompileStatus,out int ok);if(ok==0){string e=GL.GetShaderInfoLog(s);GL.DeleteShader(s);throw new InvalidOperationException("trail shader: "+e);}return s;}
        int a=Stage(ShaderType.VertexShader,vs),b=Stage(ShaderType.FragmentShader,fs),p=GL.CreateProgram();
        GL.AttachShader(p,a);GL.AttachShader(p,b);GL.LinkProgram(p);GL.DeleteShader(a);GL.DeleteShader(b);
        GL.GetProgram(p,GetProgramParameterName.LinkStatus,out int linked);if(linked==0)throw new InvalidOperationException("trail shader: "+GL.GetProgramInfoLog(p));return p;
    }

    int Texture(string path)
    {
        if(textures.TryGetValue(path,out int id))return id;
        if(path.Length==0||!assets.Exists(path)){string w="Trail texture not found: "+(path.Length==0?"(none)":path);if(!Diagnostics.Contains(w))Diagnostics.Add(w);return textures[path]=0;}
        try{return textures[path]=DdsTexture.Load(assets.Read(path),false,false).Id;}
        catch(Exception e){Diagnostics.Add($"Trail texture {path}: {e.Message}");return textures[path]=0;}
    }

    public sealed record Strip(TrailMesh Mesh,Vector3[] Positions,Vector4 BaseColor,float BaseScale);

    public void Draw(IReadOnlyList<Strip> strips,Matrix4 projection,OrbitCamera camera,int whiteTexture)
    {
        LastDrawn=0;if(strips.Count==0)return;
        GL.GetInteger(GetPName.CurrentProgram,out int oldProgram);GL.GetInteger(GetPName.VertexArrayBinding,out int oldVao);GL.GetInteger(GetPName.ArrayBufferBinding,out int oldBuffer);
        GL.GetInteger(GetPName.ActiveTexture,out int oldUnit);GL.GetInteger(GetPName.DepthFunc,out int oldDepth);
        GL.GetInteger(GetPName.BlendSrcRgb,out int srcRgb);GL.GetInteger(GetPName.BlendDstRgb,out int dstRgb);GL.GetInteger(GetPName.BlendSrcAlpha,out int srcAlpha);GL.GetInteger(GetPName.BlendDstAlpha,out int dstAlpha);
        GL.GetBoolean(GetPName.DepthWritemask,out bool depthMask);
        var caps=new[]{EnableCap.Blend,EnableCap.DepthTest,EnableCap.CullFace,EnableCap.FramebufferSrgb};var states=caps.Select(GL.IsEnabled).ToArray();
        GL.ActiveTexture(TextureUnit.Texture0);GL.GetInteger(GetPName.TextureBinding2D,out int bound);
        try{
            if(program==0){program=Compile(Vertex,Fragment);vao=GL.GenVertexArray();vbo=GL.GenBuffer();GL.BindVertexArray(vao);GL.BindBuffer(BufferTarget.ArrayBuffer,vbo);
                GL.EnableVertexAttribArray(0);GL.VertexAttribPointer(0,3,VertexAttribPointerType.Float,false,9*4,0);
                GL.EnableVertexAttribArray(1);GL.VertexAttribPointer(1,2,VertexAttribPointerType.Float,false,9*4,3*4);
                GL.EnableVertexAttribArray(2);GL.VertexAttribPointer(2,4,VertexAttribPointerType.Float,false,9*4,5*4);}
            GL.UseProgram(program);GL.BindVertexArray(vao);GL.BindBuffer(BufferTarget.ArrayBuffer,vbo);
            GL.Enable(EnableCap.DepthTest);GL.DepthFunc(DepthFunction.Lequal);GL.DepthMask(false);GL.Disable(EnableCap.CullFace);GL.Disable(EnableCap.FramebufferSrgb);
            int U(string n)=>GL.GetUniformLocation(program,n);
            var viewProjection=camera.GetViewMatrix()*projection;GL.UniformMatrix4(U("viewProjection"),false,ref viewProjection);GL.Uniform1(U("baseTexture"),0);
            foreach(var strip in strips){
                var m=strip.Mesh;
                int texture=Texture(m.Texture);
                GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,texture!=0?texture:whiteTexture);
                GL.Uniform4(U("baseColor"),strip.BaseColor);GL.Uniform1(U("baseScale"),strip.BaseScale);
                GL.Uniform1(U("useVertexColor"),m.VertexColors?1:0);GL.Uniform1(U("useVertexAlpha"),m.VertexAlpha?1:0);
                GL.Uniform1(U("alphaFunction"),(m.AlphaFlags&512)!=0?(int)((m.AlphaFlags>>10)&7):0);GL.Uniform1(U("alphaThreshold"),m.AlphaThreshold/255f);
                if((m.AlphaFlags&1)!=0){GL.Enable(EnableCap.Blend);GL.BlendFunc(ParticleRenderer.Factor((int)(m.AlphaFlags>>1)&15),ParticleRenderer.Factor((int)(m.AlphaFlags>>5)&15));}
                else GL.Disable(EnableCap.Blend);
                var data=new float[m.Indices.Length*9];int o=0;
                foreach(int i in m.Indices){
                    var p=strip.Positions[i];data[o++]=p.X;data[o++]=p.Y;data[o++]=p.Z;
                    data[o++]=m.Uv[i].X;data[o++]=m.Uv[i].Y;
                    var c=m.Color[i];data[o++]=c.X;data[o++]=c.Y;data[o++]=c.Z;data[o++]=c.W;
                }
                GL.BufferData(BufferTarget.ArrayBuffer,data.Length*4,data,BufferUsageHint.StreamDraw);
                GL.DrawArrays(PrimitiveType.Triangles,0,m.Indices.Length);LastDrawn++;
            }
        }finally{
            GL.DepthMask(depthMask);GL.DepthFunc((DepthFunction)oldDepth);
            GL.BlendFuncSeparate((BlendingFactorSrc)srcRgb,(BlendingFactorDest)dstRgb,(BlendingFactorSrc)srcAlpha,(BlendingFactorDest)dstAlpha);
            for(int i=0;i<caps.Length;i++){if(states[i])GL.Enable(caps[i]);else GL.Disable(caps[i]);}
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,bound);
            GL.ActiveTexture((TextureUnit)oldUnit);GL.UseProgram(oldProgram);GL.BindVertexArray(oldVao);GL.BindBuffer(BufferTarget.ArrayBuffer,oldBuffer);
        }
    }

    public void Invalidate(){foreach(int t in textures.Values)if(t!=0)GL.DeleteTexture(t);textures.Clear();Diagnostics.Clear();}
    public void Dispose(){Invalidate();if(program!=0)GL.DeleteProgram(program);if(vao!=0)GL.DeleteVertexArray(vao);if(vbo!=0)GL.DeleteBuffer(vbo);program=vao=vbo=0;}
}
