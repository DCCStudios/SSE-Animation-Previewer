using FO4_Base_Library;
using NiflySharp.Blocks;
using NiflySharp.Enums;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Mesh=FO4_Base_Library.PreviewModel.RenderableMesh;
using PixelFormat=OpenTK.Graphics.OpenGL4.PixelFormat;

namespace SSEPreview;

/// <summary>Draws BSWaterShaderProperty surfaces the way Skyrim's water shader does, between the opaque scene and the
/// blended effects. The math follows the vanilla paths of Community Shaders' reconstruction of Water.hlsl (read only):
/// three scrolling noise normal layers (or flowmap normals), fresnel, reflection, depth-tinted refraction and the sun
/// highlight, with the WATR record's values.
/// <para>What differs from the game: the reflection is a screen-space trace of the drawn scene over the water type's
/// reflection colour (the game renders a mirrored sky and world); the sun is the previewer's key light, not the weather's
/// sun; there is no distance fog, underwater view, rain, wading ripples or LOD water. Scroll and flow speeds use estimated
/// time scales.</para></summary>
public sealed class WaterRenderer(Assets assets):IDisposable
{
    readonly Dictionary<string,int> textures=new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Diagnostics=[];
    int program,vao,vbo,fbo,sceneColor,sceneDepth,width,height;
    bool invalidated;
    public int LastDrawn{get;private set;}
    /// <summary>Folder under textures\water that holds this worldspace's flowmaps ("skyrim.esm" for Tamriel); null for none.</summary>
    public string? FlowmapFolder;
    /// <summary>Flow textures found for the last frame, by path.</summary>
    public readonly HashSet<string> FlowmapsUsed=new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The NIF water shader flag sets drawn in the last frame, with surface counts.</summary>
    public readonly Dictionary<string,int> FlagSets=[];
    public void InvalidateTextures(){invalidated=true;Diagnostics.Clear();FlowmapsUsed.Clear();}
    void Warn(string value){if(!Diagnostics.Contains(value))Diagnostics.Add(value);}

    // Flow advection runs on the engine's water timer; the scale from seconds is an estimate (see the class remarks).
    const float FlowTimeScale=256f;
    const float CellSize=4096f;

    static int Compile(string vertex,string fragment)
    {
        int Stage(ShaderType type,string source){int s=GL.CreateShader(type);GL.ShaderSource(s,source);GL.CompileShader(s);GL.GetShader(s,ShaderParameter.CompileStatus,out int ok);if(ok==0){string error=GL.GetShaderInfoLog(s);GL.DeleteShader(s);throw new InvalidOperationException("water shader: "+error);}return s;}
        int vs=Stage(ShaderType.VertexShader,vertex),fs=Stage(ShaderType.FragmentShader,fragment),p=GL.CreateProgram();
        GL.AttachShader(p,vs);GL.AttachShader(p,fs);GL.LinkProgram(p);GL.DeleteShader(vs);GL.DeleteShader(fs);
        GL.GetProgram(p,GetProgramParameterName.LinkStatus,out int linked);if(linked==0)throw new InvalidOperationException("water shader: "+GL.GetProgramInfoLog(p));return p;
    }

    const string VertexSource="""
        #version 330 core
        layout(location=0) in vec3 position;
        layout(location=1) in vec2 uv;
        layout(location=2) in vec4 color;
        uniform mat4 model,view,projection;
        out vec3 worldPos;out vec2 meshUv;out vec4 vColor;
        void main(){vec4 w=model*vec4(position,1);worldPos=w.xyz;meshUv=uv;vColor=color;gl_Position=projection*view*w;}
        """;

    // Names follow Water.hlsl: distanceMul, depthControl, refractionMul, specularFraction.
    const string FragmentSource="""
        #version 330 core
        in vec3 worldPos;in vec2 meshUv;in vec4 vColor;
        uniform sampler2D sceneColor,sceneDepth,normals1,normals2,normals3,flowMap,flowNormals;
        uniform mat4 view,projection,inverseViewProjection;
        uniform vec2 screenSize;uniform vec3 eye;
        uniform vec3 shallowColor,deepColor,reflectionColor,sunColor,sunDirection,flatColor;
        uniform vec4 depthControl;uniform vec3 amplitude,uvScale;uniform vec2 scroll0,scroll1,scroll2;
        uniform float fresnelAmount,reflectionAmount,refractionMagnitude,reflectionMagnitude,alpha,noiseFalloff,sunSpecularPower;
        uniform float fogFar,fogRange,fogAmount,flowTime,flowTexels;uniform vec2 flowOrigin;
        uniform int useDepth,useReflections,useRefractions,vertexAlphaDepth,vertexUv,useFlowmap,blendNormals,useFlat;
        out vec4 result;

        vec3 WorldAt(vec2 uv,float depth){vec4 w=inverseViewProjection*vec4(uv*2.-1.,depth*2.-1.,1.);return w.xyz/w.w;}
        // Underwater path length (xy) and vertical depth (zw) to the scene behind uv, over the fog distance.
        vec4 DepthMul(vec2 uv){
            float d=texture(sceneDepth,uv).r;if(d>=.999999)return vec4(1.);
            vec3 bed=WorldAt(uv,d);float below=worldPos.z-bed.z;if(below<0.)return vec4(0.);
            float path=length(bed-worldPos);return clamp(vec4(path,path,below,below)/max(fogFar,1e-3),0.,1.);
        }
        vec3 FlowNormal(vec2 flowUv,vec2 baseUv,vec2 shift,float multiplier,float offset){
            vec4 c=textureLod(flowMap,flowUv+shift,0.);
            vec2 v=baseUv*sqrt(max(1.01-c.z,0.));vec2 sc=c.xy*2.-1.;
            vec2 rotated=vec2(sc.x*v.x-sc.y*v.y,sc.y*v.x+sc.x*v.y);
            vec2 uv=offset+rotated-vec2(multiplier*.001*flowTime*c.w,0.);
            return vec3(texture(flowNormals,uv).xy,c.z);
        }
        vec4 TraceReflection(vec3 origin,vec3 dir){
            const int steps=48;float maxDistance=8192.;
            for(int i=1;i<=steps;i++){
                float t=pow(float(i)/float(steps),1.6)*maxDistance;vec3 q=origin+dir*t;
                vec4 clip=projection*view*vec4(q,1.);if(clip.w<=1e-3)break;
                vec3 ndc=clip.xyz/clip.w;vec2 uv=ndc.xy*.5+.5;if(any(lessThan(uv,vec2(0.)))||any(greaterThan(uv,vec2(1.))))break;
                float d=texture(sceneDepth,uv).r;if(d>=.999999)continue;
                if(ndc.z*.5+.5>d){
                    vec3 hit=WorldAt(uv,d);float behind=length(q-eye)-length(hit-eye);
                    if(behind<max(48.,t*.08)){
                        float edge=1.-smoothstep(.8,1.,max(abs(ndc.x),abs(ndc.y)));
                        return vec4(texture(sceneColor,uv).rgb,edge*(1.-float(i)/float(steps)));
                    }
                }
            }
            return vec4(0.);
        }
        void main(){
            if(useFlat!=0){result=vec4(flatColor,1.);return;}
            vec2 screenUv=gl_FragCoord.xy/screenSize;
            vec3 toSurface=worldPos-eye;float distance=length(toSurface);vec3 v=toSurface/max(distance,1e-3);
            float distanceFactor=clamp((distance-8192.)/(noiseFalloff-8192.),0.,1.);
            vec4 distanceMul=vec4(clamp(mix(alpha,1.,1.-distanceFactor),0.,1.));
            if(useDepth!=0)distanceMul=vertexAlphaDepth!=0?vec4(clamp(vColor.a,0.,1.)):DepthMul(screenUv);
            vec4 control=depthControl*(distanceMul-1.)+1.;

            vec2 base=vertexUv!=0?meshUv*1000.:worldPos.xy;
            vec3 n1=texture(normals1,base/uvScale.x+scroll0).xyz*2.+vec3(-1.,-1.,-2.);
            vec3 n2=texture(normals2,base/uvScale.y+scroll1).xyz*2.-1.;
            vec3 n3=texture(normals3,base/uvScale.z+scroll2).xyz*2.-1.;
            vec3 normal=normalize(vec3(0.,0.,1.)+amplitude.x*n1+amplitude.y*n2+amplitude.z*n3);
            normal=normalize(mix(vec3(0.,0.,1.),normal,control.z));
            if(useFlowmap!=0){
                vec2 flowUv=(worldPos.xy-flowOrigin)/4096.;flowUv.y=1.-flowUv.y;
                vec2 baseUv=64.*worldPos.xy/4096.;float shift=.5/flowTexels;
                vec2 mul=1.-abs(fract(flowUv*flowTexels)*2.-1.);
                vec3 f0=FlowNormal(flowUv,baseUv,vec2(shift),9.92,0.);
                vec3 f1=FlowNormal(flowUv,baseUv,vec2(0.,shift),10.64,.27);
                vec3 f2=FlowNormal(flowUv,baseUv,vec2(0.),8.,0.);
                vec3 f3=FlowNormal(flowUv,baseUv,vec2(shift,0.),8.48,.62);
                vec2 weighted=mul.y*(mul.x*f2.xy+(1.-mul.x)*f3.xy)+(1.-mul.y)*(mul.x*f1.xy+(1.-mul.x)*f0.xy);
                vec2 denominator=sqrt(mul*mul+(1.-mul)*(1.-mul));
                vec3 flowNormal=vec3(((weighted-.5)/(denominator.x*denominator.y))*max(.4,control.z),0.);
                flowNormal.z=sqrt(max(1.-dot(flowNormal.xy,flowNormal.xy),0.));
                vec3 plain=n1+vec3(0.,0.,1.);
                if(blendNormals!=0){
                    float blend=mul.y*((1.-mul.x)*f3.z+mul.x*f2.z)+(1.-mul.y)*(mul.x*f1.z+(1.-mul.x)*f0.z);
                    normal=normalize(mix(plain,normalize(mix(normal,flowNormal,blend)),distanceFactor));
                }else normal=normalize(mix(plain,flowNormal,distanceFactor));
            }

            float fresnel=(1.-fresnelAmount)*pow(1.-clamp(dot(-v,normal),0.,1.),5.)+fresnelAmount;
            vec3 specularColor=reflectionColor*reflectionAmount;
            if(useReflections!=0){
                vec3 r=reflect(v,normalize(reflectionMagnitude*normal+vec3(0.,0.,1.-reflectionMagnitude)));
                vec4 traced=r.z>0.?TraceReflection(worldPos,r):vec4(0.);
                specularColor=mix(reflectionColor,traced.rgb,traced.a);
            }

            vec3 refractionColor,diffuse;float refractionMul=1.;
            if(useRefractions!=0){
                vec2 refractUv=screenUv;
                vec4 clip=projection*view*vec4(worldPos+vec3(refractionMagnitude*control.y*normal.xy,0.),1.);
                if(clip.w>1e-3){
                    vec2 candidate=clamp(clip.xy/clip.w*.5+.5,vec2(0.),vec2(1.));
                    float d=texture(sceneDepth,candidate).r;
                    // Something above the surface at the displaced point would bleed in: keep the undisplaced pixel.
                    if(d>=.999999||WorldAt(candidate,d).z<=worldPos.z)refractUv=candidate;
                }
                if(useDepth!=0&&vertexAlphaDepth==0)distanceMul=DepthMul(refractUv);
                refractionColor=texture(sceneColor,refractUv).rgb;
                refractionMul=1.-pow(clamp((fogFar-distanceMul.x*fogFar)/max(fogRange,1e-3),0.,1.),fogAmount);
                diffuse=mix(shallowColor,deepColor,distanceMul.y);
            }else{
                refractionColor=mix(shallowColor,deepColor,fresnel)*clamp(dot(sunDirection,normal),0.,1.);
                diffuse=refractionColor;
            }
            control=depthControl*(distanceMul-1.)+1.;
            vec3 reflectedView=reflect(v,normal);
            float sun=sunSpecularPower>0.?pow(clamp(dot(reflectedView,sunDirection),0.,1.),sunSpecularPower):1.;
            float specularFraction=mix(1.,fresnel,distanceFactor);
            vec3 surface=mix(diffuse,specularColor,specularFraction)+sunColor*sun*control.w;
            result=vec4(mix(refractionColor,surface,refractionMul),1.);
        }
        """;

    void Initialize()
    {
        if(program!=0)return;
        program=Compile(VertexSource,FragmentSource);
        vao=GL.GenVertexArray();vbo=GL.GenBuffer();GL.BindVertexArray(vao);GL.BindBuffer(BufferTarget.ArrayBuffer,vbo);
        GL.EnableVertexAttribArray(0);GL.VertexAttribPointer(0,3,VertexAttribPointerType.Float,false,9*4,0);
        GL.EnableVertexAttribArray(1);GL.VertexAttribPointer(1,2,VertexAttribPointerType.Float,false,9*4,3*4);
        GL.EnableVertexAttribArray(2);GL.VertexAttribPointer(2,4,VertexAttribPointerType.Float,false,9*4,5*4);
        fbo=GL.GenFramebuffer();sceneColor=GL.GenTexture();sceneDepth=GL.GenTexture();
    }

    void Resize(int w,int h)
    {
        if(width==w&&height==h)return;width=w;height=h;
        GL.BindTexture(TextureTarget.Texture2D,sceneColor);
        GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba8,w,h,0,PixelFormat.Rgba,PixelType.UnsignedByte,IntPtr.Zero);
        Clamp(TextureMinFilter.Linear);
        GL.BindTexture(TextureTarget.Texture2D,sceneDepth);
        GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.DepthComponent24,w,h,0,PixelFormat.DepthComponent,PixelType.UnsignedInt,IntPtr.Zero);
        Clamp(TextureMinFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,fbo);
        GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer,FramebufferAttachment.DepthAttachment,TextureTarget.Texture2D,sceneDepth,0);
        GL.DrawBuffer(DrawBufferMode.None);
        if(GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer)!=FramebufferErrorCode.FramebufferComplete)throw new InvalidOperationException("Water depth framebuffer is incomplete");
        static void Clamp(TextureMinFilter filter){
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)filter);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)(filter==TextureMinFilter.Nearest?TextureMagFilter.Nearest:TextureMagFilter.Linear));
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)TextureWrapMode.ClampToEdge);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)TextureWrapMode.ClampToEdge);
        }
    }

    /// <summary>Loads a DDS with its mip chain (water normals alias badly without one); 0 when missing.</summary>
    int Texture(string path,bool repeat,bool nearest=false)
    {
        if(path.Length==0)return 0;
        if(textures.TryGetValue(path,out int cached))return cached;
        if(!assets.Exists(path)){Warn("Water texture not found: "+path);return textures[path]=0;}
        try{
            var (texture,size)=DdsTexture.Load(assets.Read(path),repeat,nearest);
            if(path.Contains(@"\flow.",StringComparison.OrdinalIgnoreCase))flowSizes[texture]=size;
            return textures[path]=texture;
        }catch(Exception e){Warn($"Water texture {path}: {e.Message}");return textures[path]=0;}
    }
    readonly Dictionary<int,int> flowSizes=[];

    /// <summary>Draws the water surfaces. <paramref name="time"/> drives the scrolling layers; a still capture passes a
    /// fixed time. <paramref name="flatColor"/> draws each surface in one colour (shape captures).</summary>
    public void Draw(IReadOnlyList<(Mesh Mesh,WaterType Type)> surfaces,Matrix4 projection,OrbitCamera camera,double time,
        (Vector3 Direction,Vector3 Color) sun,Func<Mesh,Vector3?>? flatColor=null)
    {
        LastDrawn=0;if(surfaces.Count==0)return;
        GL.GetInteger(GetPName.DrawFramebufferBinding,out int oldDraw);GL.GetInteger(GetPName.ReadFramebufferBinding,out int oldRead);
        GL.GetInteger(GetPName.CurrentProgram,out int oldProgram);GL.GetInteger(GetPName.VertexArrayBinding,out int oldVao);GL.GetInteger(GetPName.ArrayBufferBinding,out int oldBuffer);
        GL.GetInteger(GetPName.ActiveTexture,out int oldUnit);GL.GetInteger(GetPName.DepthFunc,out int oldDepth);
        int[] viewport=new int[4];GL.GetInteger(GetPName.Viewport,viewport);
        GL.GetBoolean(GetPName.DepthWritemask,out bool depthMask);
        var caps=new[]{EnableCap.Blend,EnableCap.DepthTest,EnableCap.CullFace,EnableCap.FramebufferSrgb};var states=caps.Select(GL.IsEnabled).ToArray();
        const int units=7;int[] bound=new int[units];for(int i=0;i<units;i++){GL.ActiveTexture(TextureUnit.Texture0+i);GL.GetInteger(GetPName.TextureBinding2D,out bound[i]);}
        try{
            GL.ActiveTexture(TextureUnit.Texture0);
            if(invalidated){foreach(int t in textures.Values)if(t!=0)GL.DeleteTexture(t);textures.Clear();flowSizes.Clear();invalidated=false;}
            Initialize();Resize(viewport[2],viewport[3]);
            // The opaque scene as it stands: colour for refraction and reflection, depth for water depth.
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer,oldDraw);
            GL.BindTexture(TextureTarget.Texture2D,sceneColor);GL.CopyTexSubImage2D(TextureTarget.Texture2D,0,0,0,viewport[0],viewport[1],width,height);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,fbo);
            GL.BlitFramebuffer(viewport[0],viewport[1],viewport[0]+width,viewport[1]+height,0,0,width,height,ClearBufferMask.DepthBufferBit,BlitFramebufferFilter.Nearest);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,oldDraw);GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer,oldRead);GL.Viewport(viewport[0],viewport[1],viewport[2],viewport[3]);

            GL.Disable(EnableCap.Blend);GL.Disable(EnableCap.CullFace);GL.Disable(EnableCap.FramebufferSrgb);
            GL.Enable(EnableCap.DepthTest);GL.DepthFunc(DepthFunction.Lequal);GL.DepthMask(false);
            GL.UseProgram(program);GL.BindVertexArray(vao);GL.BindBuffer(BufferTarget.ArrayBuffer,vbo);
            var view=camera.GetViewMatrix();var eye=camera.GetEyePosition();var inverse=(view*projection).Inverted();
            int U(string name)=>GL.GetUniformLocation(program,name);
            GL.UniformMatrix4(U("view"),false,ref view);GL.UniformMatrix4(U("projection"),false,ref projection);GL.UniformMatrix4(U("inverseViewProjection"),false,ref inverse);
            GL.Uniform2(U("screenSize"),(float)width,(float)height);GL.Uniform3(U("eye"),eye);
            string[] samplers=["sceneColor","sceneDepth","normals1","normals2","normals3","flowMap","flowNormals"];
            for(int i=0;i<samplers.Length;i++)GL.Uniform1(U(samplers[i]),i);
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,sceneColor);
            GL.ActiveTexture(TextureUnit.Texture1);GL.BindTexture(TextureTarget.Texture2D,sceneDepth);
            var sunDirection=sun.Direction.LengthSquared>0?sun.Direction.Normalized():Vector3.UnitZ;
            // The key light's colour reaches the shader linear; the water composes in display space like the game.
            var sunDisplay=new Vector3(MathF.Pow(MathF.Min(sun.Color.X,4),1/2.2f),MathF.Pow(MathF.Min(sun.Color.Y,4),1/2.2f),MathF.Pow(MathF.Min(sun.Color.Z,4),1/2.2f));
            GL.Uniform3(U("sunDirection"),sunDirection);
            FlowmapsUsed.Clear();FlagSets.Clear();

            foreach(var (mesh,type) in surfaces){
                var geo=mesh.MeshData.Meshgeometry;
                if(geo.Vertices==null||geo.Indices==null||geo.Vertices.Length==0){Warn($"Water surface {mesh.MeshData.Shape?.ShapeName}: no vertex data");continue;}
                var shader=mesh.MeshData.Shape.NifShader as BSWaterShaderProperty;
                var flags=shader?.WaterShaderFlags??(WaterShaderPropertyFlags.DEPTH|WaterShaderPropertyFlags.REFLECTIONS|WaterShaderPropertyFlags.REFRACTIONS);
                bool hasColors=geo.VertexColors!=null&&geo.VertexColors.Length==geo.Vertices.Length;
                string flagText=FlagNames(flags);FlagSets[flagText]=FlagSets.GetValueOrDefault(flagText)+1;
                var flat=flatColor?.Invoke(mesh);
                GL.Uniform1(U("useFlat"),flat!=null?1:0);if(flat is {} f)GL.Uniform3(U("flatColor"),f);

                int n1=Texture(Layer(type,0),true),n2=Texture(Layer(type,1),true),n3=Texture(Layer(type,2),true);
                GL.ActiveTexture(TextureUnit.Texture2);GL.BindTexture(TextureTarget.Texture2D,n1);
                GL.ActiveTexture(TextureUnit.Texture3);GL.BindTexture(TextureTarget.Texture2D,n2);
                GL.ActiveTexture(TextureUnit.Texture4);GL.BindTexture(TextureTarget.Texture2D,n3);

                // Flowmaps are per cell: textures\water\<worldspace plugin>\flow.<x>.<y>.dds, with the flow normals in NAM5.
                int flowTexture=0,flowNormals=0;Vector2 flowOrigin=default;
                if(type.Flowmap&&FlowmapFolder!=null){
                    // The engine does not compute bounds for hidden meshes: take the centre from the vertices.
                    var low=new Vector3(float.MaxValue);var high=new Vector3(float.MinValue);
                    foreach(var p in geo.Vertices){var v=new Vector3((float)p.X,(float)p.Y,(float)p.Z);low=Vector3.ComponentMin(low,v);high=Vector3.ComponentMax(high,v);}
                    var center=Vector3.TransformPosition((low+high)*.5f,mesh.MeshData.Transform);int cx=(int)MathF.Floor(center.X/CellSize),cy=(int)MathF.Floor(center.Y/CellSize);
                    string flowPath=$@"textures\water\{FlowmapFolder}\flow.{cx}.{cy}.dds";
                    if(!assets.Exists(flowPath))Warn($"Water flowmap not found for {mesh.MeshData.Shape?.ShapeName}: {flowPath} (no flow in that cell)");
                    else{flowTexture=Texture(flowPath,false,true);flowNormals=Texture(type.NoiseTextures[3].Length>0?type.NoiseTextures[3]:@"textures\water\riverflow.dds",true);flowOrigin=new(cx*CellSize,cy*CellSize);if(flowTexture!=0)FlowmapsUsed.Add(flowPath);}
                }
                bool useFlow=flowTexture!=0&&flowNormals!=0;
                GL.ActiveTexture(TextureUnit.Texture5);GL.BindTexture(TextureTarget.Texture2D,flowTexture);
                GL.ActiveTexture(TextureUnit.Texture6);GL.BindTexture(TextureTarget.Texture2D,flowNormals);
                GL.Uniform1(U("useFlowmap"),useFlow?1:0);GL.Uniform1(U("blendNormals"),type.BlendNormals?1:0);
                GL.Uniform2(U("flowOrigin"),flowOrigin);GL.Uniform1(U("flowTexels"),(float)(useFlow?flowSizes.GetValueOrDefault(flowTexture,64):64));
                GL.Uniform1(U("flowTime"),(float)(time*FlowTimeScale));

                GL.Uniform1(U("useDepth"),flags.HasFlag(WaterShaderPropertyFlags.DEPTH)?1:0);
                GL.Uniform1(U("useReflections"),flags.HasFlag(WaterShaderPropertyFlags.REFLECTIONS)?1:0);
                GL.Uniform1(U("useRefractions"),flags.HasFlag(WaterShaderPropertyFlags.REFRACTIONS)?1:0);
                GL.Uniform1(U("vertexAlphaDepth"),flags.HasFlag(WaterShaderPropertyFlags.VERTEX_ALPHA_DEPTH)&&hasColors?1:0);
                GL.Uniform1(U("vertexUv"),flags.HasFlag(WaterShaderPropertyFlags.VERTEX_UV)&&geo.Uvs_Weight!=null?1:0);

                GL.Uniform3(U("shallowColor"),new Vector3(type.Shallow.R,type.Shallow.G,type.Shallow.B));
                GL.Uniform3(U("deepColor"),new Vector3(type.Deep.R,type.Deep.G,type.Deep.B));
                GL.Uniform3(U("reflectionColor"),new Vector3(type.Reflection.R,type.Reflection.G,type.Reflection.B));
                GL.Uniform4(U("depthControl"),type.DepthControl[0],type.DepthControl[1],type.DepthControl[2],type.DepthControl[3]);
                GL.Uniform3(U("amplitude"),type.Amplitude[0],type.Amplitude[1],type.Amplitude[2]);
                GL.Uniform3(U("uvScale"),MathF.Max(type.UvScale[0],1),MathF.Max(type.UvScale[1],1),MathF.Max(type.UvScale[2],1));
                for(int k=0;k<3;k++){
                    // Wind direction is a compass angle (0 = north, +Y); speed is in layer tiles per second.
                    double a=type.WindDirection[k]*Math.PI/180;
                    GL.Uniform2(U("scroll"+k),(float)(Math.Sin(a)*type.WindSpeed[k]*time%1),(float)(Math.Cos(a)*type.WindSpeed[k]*time%1));
                }
                GL.Uniform1(U("fresnelAmount"),type.FresnelAmount);GL.Uniform1(U("reflectionAmount"),type.ReflectionAmount);
                GL.Uniform1(U("refractionMagnitude"),type.RefractionMagnitude);GL.Uniform1(U("reflectionMagnitude"),Math.Clamp(type.ReflectionMagnitude,0,1));
                GL.Uniform1(U("alpha"),type.Alpha);GL.Uniform1(U("noiseFalloff"),MathF.Abs(type.NoiseFalloff-8192)<1?8191:type.NoiseFalloff);
                GL.Uniform1(U("sunSpecularPower"),type.SunSpecularPower);
                GL.Uniform3(U("sunColor"),sunDisplay*(1-MathF.Exp(-type.SunSpecularMagnitude)));
                GL.Uniform1(U("fogFar"),type.FogFar);GL.Uniform1(U("fogRange"),type.FogFar-type.FogNear);GL.Uniform1(U("fogAmount"),type.FogAmount);

                var model=mesh.MeshData.Transform;GL.UniformMatrix4(U("model"),false,ref model);
                var data=new float[geo.Indices.Length*9];int o=0;
                foreach(uint index in geo.Indices){
                    int i=checked((int)index);if(i>=geo.Vertices.Length)throw new InvalidDataException("Water vertex index exceeds mesh bounds");
                    var p=geo.Vertices[i];data[o++]=(float)p.X;data[o++]=(float)p.Y;data[o++]=(float)p.Z;
                    var uv=geo.Uvs_Weight!=null&&i<geo.Uvs_Weight.Length?geo.Uvs_Weight[i]:default;data[o++]=uv.X;data[o++]=uv.Y;
                    var c=hasColors?geo.VertexColors[i]:new OpenTK.Mathematics.Vector4(1);data[o++]=c.X;data[o++]=c.Y;data[o++]=c.Z;data[o++]=c.W;
                }
                GL.BufferData(BufferTarget.ArrayBuffer,data.Length*4,data,BufferUsageHint.StreamDraw);GL.DrawArrays(PrimitiveType.Triangles,0,data.Length/9);LastDrawn++;
            }
        }finally{
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer,oldRead);GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,oldDraw);GL.Viewport(viewport[0],viewport[1],viewport[2],viewport[3]);
            GL.DepthMask(depthMask);GL.DepthFunc((DepthFunction)oldDepth);
            for(int i=0;i<caps.Length;i++){if(states[i])GL.Enable(caps[i]);else GL.Disable(caps[i]);}
            for(int i=0;i<units;i++){GL.ActiveTexture(TextureUnit.Texture0+i);GL.BindTexture(TextureTarget.Texture2D,bound[i]);}
            GL.ActiveTexture((TextureUnit)oldUnit);GL.UseProgram(oldProgram);GL.BindVertexArray(oldVao);GL.BindBuffer(BufferTarget.ArrayBuffer,oldBuffer);
        }
    }

    /// <summary>"DEPTH|REFLECTIONS|REFRACTIONS": the enum has no [Flags] attribute, so ToString prints a number.</summary>
    static string FlagNames(WaterShaderPropertyFlags flags)
    {
        var names=Enum.GetValues<WaterShaderPropertyFlags>().Where(f=>(uint)f!=0&&((uint)flags&(uint)f)==(uint)f).Select(f=>f.ToString()).ToList();
        return names.Count==0?"none":string.Join("|",names);
    }

    /// <summary>A noise layer's texture; the engine falls back to textures\water\defaultwater.dds when a WATR names none.</summary>
    static string Layer(WaterType type,int index)=>type.NoiseTextures[index].Length>0?type.NoiseTextures[index]:@"textures\water\defaultwater.dds";

    public void Dispose()
    {
        foreach(int t in textures.Values)if(t!=0)GL.DeleteTexture(t);textures.Clear();
        if(program!=0)GL.DeleteProgram(program);if(vao!=0)GL.DeleteVertexArray(vao);if(vbo!=0)GL.DeleteBuffer(vbo);if(fbo!=0)GL.DeleteFramebuffer(fbo);
        if(sceneColor!=0)GL.DeleteTexture(sceneColor);if(sceneDepth!=0)GL.DeleteTexture(sceneDepth);
        program=vao=vbo=fbo=sceneColor=sceneDepth=width=height=0;
    }
}
