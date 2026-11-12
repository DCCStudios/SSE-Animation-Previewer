using FO4_Base_Library;
using NiflySharp.Blocks;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Mesh=FO4_Base_Library.PreviewModel.RenderableMesh;
using PixelFormat=OpenTK.Graphics.OpenGL4.PixelFormat;

namespace SSEPreview;

/// <summary>A weather's sky and fog at one time of day, as the view capture draws them. Colours are sRGB 0..1.</summary>
public sealed class SkyState
{
    public Vector3 Upper,Lower,Horizon,Statics=Vector3.One,FogNearColor,FogFarColor;
    public float FogNear,FogFar=100000,FogPower=1,FogMax=1;
    public bool Fog=true;
}

/// <summary>One sky static shape to draw: its mesh (hidden from the engine pass), base texture, the effect shader's colour
/// and UV transform, its NiAlphaProperty flags and threshold, and whether it is double-sided (SLSF2 Double_Sided).</summary>
public sealed record SkySurface(Mesh Mesh,string Texture,Vector4 BaseColor,float BaseColorScale,Vector2 UvOffset,Vector2 UvScale,uint AlphaFlags,byte AlphaThreshold,bool DoubleSided);

/// <summary>Draws a weather's sky behind the scene and its distance fog over it.
/// <para>The sky runs before any mesh: a gradient from the weather's lower sky through its horizon to its upper sky colour,
/// then the TNAM sky statics centred on the camera, tinted by the weather's sky-statics colour. Both write no depth, so
/// every mesh draws over them whatever the dome's size; the statics' vertices are pushed to the far plane, so a dome
/// smaller or larger than the scene is never clipped.</para>
/// <para>Fog runs after the opaque meshes and before water and blended meshes, from the depth buffer, with the formula of
/// the game's shaders: factor = min(max, pow(saturate(distance / (far - near) - near / (far - near)), power)), colour from
/// the fog-near to the fog-far colour by that factor. Pixels with no geometry (the sky) are not fogged.</para>
/// <para>What differs from the game: the gradient's shape is an estimate (the game draws a sky dome mesh with these
/// colours); there are no cloud layers, sun or moon discs, stars, aurora or precipitation; water and blended meshes are
/// not fogged (the game fogs them in their own shaders); the weather's imagespaces are not applied.</para></summary>
public sealed class SkyRenderer(Assets assets):IDisposable
{
    readonly Dictionary<string,(int Id,int Width)> textures=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<Mesh,(int Vao,int Vbo,int Count)> geometry=[];
    public readonly List<string> Diagnostics=[];
    public readonly HashSet<string> TexturesUsed=new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> TexturesMissing=new(StringComparer.OrdinalIgnoreCase);
    int gradientProgram,staticProgram,fogProgram,emptyVao,fbo,sceneDepth,width,height;
    public int StaticsDrawn{get;private set;}
    /// <summary>Per sky static shape in the last frame: triangles whose front (counter-clockwise) side faces the eye, and the total.
    /// A single-sided shape shows only the facing ones, as in the game.</summary>
    public readonly Dictionary<string,(int Facing,int Total,bool DoubleSided)> Facing=[];
    public bool FogDrawn{get;private set;}
    void Warn(string value){if(!Diagnostics.Contains(value))Diagnostics.Add(value);}

    const string FullscreenVertex="""
        #version 330 core
        out vec2 ndc;
        void main(){vec2 p[3]=vec2[](vec2(-1.,-1.),vec2(3.,-1.),vec2(-1.,3.));ndc=p[gl_VertexID];gl_Position=vec4(ndc,0.,1.);}
        """;

    // Below the horizon the lower-sky colour, above it the horizon colour rising into the upper-sky colour.
    const string GradientFragment="""
        #version 330 core
        in vec2 ndc;
        uniform mat4 inverseViewProjection;uniform vec3 eye,upper,lower,horizon;
        out vec4 result;
        void main(){
            vec4 w=inverseViewProjection*vec4(ndc,1.,1.);vec3 d=normalize(w.xyz/w.w-eye);
            vec3 c=d.z>=0.?mix(horizon,upper,smoothstep(0.,.5,d.z)):mix(horizon,lower,smoothstep(0.,.15,-d.z));
            result=vec4(c,1.);
        }
        """;

    const string StaticVertex="""
        #version 330 core
        layout(location=0) in vec3 position;layout(location=1) in vec2 uv;layout(location=2) in vec4 color;
        uniform mat4 model,view,projection;uniform vec2 uvOffset,uvScale;
        out vec2 meshUv;out vec4 vColor;
        void main(){gl_Position=projection*view*model*vec4(position,1.);gl_Position.z=gl_Position.w*.999999;meshUv=uv*uvScale+uvOffset;vColor=color;}
        """;

    const string StaticFragment="""
        #version 330 core
        in vec2 meshUv;in vec4 vColor;
        uniform sampler2D baseTexture;uniform vec4 tint;uniform float alphaThreshold;uniform int alphaFunction;
        out vec4 result;
        void main(){
            vec4 c=texture(baseTexture,meshUv)*vColor*tint;
            bool pass=alphaFunction==0||(alphaFunction==1&&c.a<alphaThreshold)||(alphaFunction==2&&c.a==alphaThreshold)||(alphaFunction==3&&c.a<=alphaThreshold)
                ||(alphaFunction==4&&c.a>alphaThreshold)||(alphaFunction==5&&c.a!=alphaThreshold)||(alphaFunction==6&&c.a>=alphaThreshold);
            if(!pass)discard;
            result=c;
        }
        """;

    const string FogFragment="""
        #version 330 core
        in vec2 ndc;
        uniform sampler2D sceneDepth;uniform mat4 inverseViewProjection;uniform vec3 eye,nearColor,farColor;uniform vec4 fogParam;
        out vec4 result;
        void main(){
            float d=texture(sceneDepth,ndc*.5+.5).r;if(d>=.999999)discard;
            vec4 w=inverseViewProjection*vec4(ndc,d*2.-1.,1.);float distance=length(w.xyz/w.w-eye);
            float f=min(fogParam.w,pow(clamp(distance*fogParam.y-fogParam.x,0.,1.),fogParam.z));
            result=vec4(mix(nearColor,farColor,f),f);
        }
        """;

    static int Compile(string vertex,string fragment)
    {
        int Stage(ShaderType type,string source){int s=GL.CreateShader(type);GL.ShaderSource(s,source);GL.CompileShader(s);GL.GetShader(s,ShaderParameter.CompileStatus,out int ok);if(ok==0){string error=GL.GetShaderInfoLog(s);GL.DeleteShader(s);throw new InvalidOperationException("sky shader: "+error);}return s;}
        int vs=Stage(ShaderType.VertexShader,vertex),fs=Stage(ShaderType.FragmentShader,fragment),p=GL.CreateProgram();
        GL.AttachShader(p,vs);GL.AttachShader(p,fs);GL.LinkProgram(p);GL.DeleteShader(vs);GL.DeleteShader(fs);
        GL.GetProgram(p,GetProgramParameterName.LinkStatus,out int linked);if(linked==0)throw new InvalidOperationException("sky shader: "+GL.GetProgramInfoLog(p));return p;
    }

    void Initialize()
    {
        if(gradientProgram!=0)return;
        gradientProgram=Compile(FullscreenVertex,GradientFragment);staticProgram=Compile(StaticVertex,StaticFragment);fogProgram=Compile(FullscreenVertex,FogFragment);
        emptyVao=GL.GenVertexArray();fbo=GL.GenFramebuffer();sceneDepth=GL.GenTexture();
    }

    /// <summary>Restores the GL state a pass changes, whatever it throws.</summary>
    static void Preserved(Action pass)
    {
        GL.GetInteger(GetPName.DrawFramebufferBinding,out int oldDraw);GL.GetInteger(GetPName.ReadFramebufferBinding,out int oldRead);
        GL.GetInteger(GetPName.CurrentProgram,out int oldProgram);GL.GetInteger(GetPName.VertexArrayBinding,out int oldVao);GL.GetInteger(GetPName.ArrayBufferBinding,out int oldBuffer);
        GL.GetInteger(GetPName.ActiveTexture,out int oldUnit);GL.GetInteger(GetPName.DepthFunc,out int oldDepth);
        GL.GetInteger(GetPName.CullFaceMode,out int oldCull);GL.GetInteger(GetPName.FrontFace,out int oldFront);
        GL.GetInteger(GetPName.BlendSrcRgb,out int srcRgb);GL.GetInteger(GetPName.BlendDstRgb,out int dstRgb);GL.GetInteger(GetPName.BlendSrcAlpha,out int srcAlpha);GL.GetInteger(GetPName.BlendDstAlpha,out int dstAlpha);
        int[] viewport=new int[4];GL.GetInteger(GetPName.Viewport,viewport);
        GL.GetBoolean(GetPName.DepthWritemask,out bool depthMask);
        var caps=new[]{EnableCap.Blend,EnableCap.DepthTest,EnableCap.CullFace,EnableCap.FramebufferSrgb};var states=caps.Select(GL.IsEnabled).ToArray();
        GL.ActiveTexture(TextureUnit.Texture0);GL.GetInteger(GetPName.TextureBinding2D,out int bound);
        try{pass();}
        finally{
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer,oldRead);GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,oldDraw);GL.Viewport(viewport[0],viewport[1],viewport[2],viewport[3]);
            GL.DepthMask(depthMask);GL.DepthFunc((DepthFunction)oldDepth);GL.CullFace((TriangleFace)oldCull);GL.FrontFace((FrontFaceDirection)oldFront);
            GL.BlendFuncSeparate((BlendingFactorSrc)srcRgb,(BlendingFactorDest)dstRgb,(BlendingFactorSrc)srcAlpha,(BlendingFactorDest)dstAlpha);
            for(int i=0;i<caps.Length;i++){if(states[i])GL.Enable(caps[i]);else GL.Disable(caps[i]);}
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,bound);
            GL.ActiveTexture((TextureUnit)oldUnit);GL.UseProgram(oldProgram);GL.BindVertexArray(oldVao);GL.BindBuffer(BufferTarget.ArrayBuffer,oldBuffer);
        }
    }

    /// <summary>The gradient and the sky statics, before any mesh.</summary>
    public void DrawSky(SkyState sky,IReadOnlyList<SkySurface> statics,Matrix4 projection,OrbitCamera camera)
    {
        StaticsDrawn=0;
        Preserved(()=>{
            Initialize();
            GL.Disable(EnableCap.DepthTest);GL.DepthMask(false);GL.Disable(EnableCap.CullFace);GL.Disable(EnableCap.FramebufferSrgb);GL.Disable(EnableCap.Blend);
            var view=camera.GetViewMatrix();var inverse=(view*projection).Inverted();var eye=camera.GetEyePosition();
            GL.UseProgram(gradientProgram);
            int G(string name)=>GL.GetUniformLocation(gradientProgram,name);
            GL.UniformMatrix4(G("inverseViewProjection"),false,ref inverse);GL.Uniform3(G("eye"),eye);
            GL.Uniform3(G("upper"),sky.Upper);GL.Uniform3(G("lower"),sky.Lower);GL.Uniform3(G("horizon"),sky.Horizon);
            GL.BindVertexArray(emptyVao);GL.DrawArrays(PrimitiveType.Triangles,0,3);

            if(statics.Count==0)return;
            GL.UseProgram(staticProgram);
            int S(string name)=>GL.GetUniformLocation(staticProgram,name);
            GL.UniformMatrix4(S("view"),false,ref view);GL.UniformMatrix4(S("projection"),false,ref projection);GL.Uniform1(S("baseTexture"),0);
            TexturesUsed.Clear();Facing.Clear();
            foreach(var surface in statics){
                if(!Upload(surface.Mesh,out var buffers))continue;
                CountFacing(surface,eye);
                // Back faces are culled unless the shape is double-sided, as the engine pass and the game do (front = counter-clockwise).
                if(surface.DoubleSided)GL.Disable(EnableCap.CullFace);else{GL.Enable(EnableCap.CullFace);GL.CullFace(TriangleFace.Back);GL.FrontFace(FrontFaceDirection.Ccw);}
                var (id,_)=Texture(surface.Texture);if(id==0)continue;
                TexturesUsed.Add(surface.Texture);
                GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,id);
                var model=surface.Mesh.MeshData.Transform;GL.UniformMatrix4(S("model"),false,ref model);
                GL.Uniform2(S("uvOffset"),surface.UvOffset);GL.Uniform2(S("uvScale"),surface.UvScale);
                var rgb=surface.BaseColor.Xyz*surface.BaseColorScale*sky.Statics;
                GL.Uniform4(S("tint"),new Vector4(rgb,surface.BaseColor.W));
                uint flags=surface.AlphaFlags;
                GL.Uniform1(S("alphaFunction"),(flags&512)!=0?(int)((flags>>10)&7):0);GL.Uniform1(S("alphaThreshold"),surface.AlphaThreshold/255f);
                if((flags&1)!=0){GL.Enable(EnableCap.Blend);GL.BlendFunc(ParticleRenderer.Factor((int)(flags>>1)&15),ParticleRenderer.Factor((int)(flags>>5)&15));}
                else GL.Disable(EnableCap.Blend);
                GL.BindVertexArray(buffers.Vao);GL.DrawArrays(PrimitiveType.Triangles,0,buffers.Count);StaticsDrawn++;
            }
        });
    }

    /// <summary>Distance fog over the opaque scene, before water and blended meshes.</summary>
    public void DrawFog(SkyState sky,Matrix4 projection,OrbitCamera camera)
    {
        FogDrawn=false;
        if(!sky.Fog||sky.FogFar<=sky.FogNear)return;
        Preserved(()=>{
            Initialize();
            int[] viewport=new int[4];GL.GetInteger(GetPName.Viewport,viewport);
            GL.GetInteger(GetPName.DrawFramebufferBinding,out int target);
            ResizeDepth(viewport[2],viewport[3]);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer,target);GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,fbo);
            GL.BlitFramebuffer(viewport[0],viewport[1],viewport[0]+width,viewport[1]+height,0,0,width,height,ClearBufferMask.DepthBufferBit,BlitFramebufferFilter.Nearest);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,target);GL.Viewport(viewport[0],viewport[1],viewport[2],viewport[3]);
            GL.Disable(EnableCap.DepthTest);GL.DepthMask(false);GL.Disable(EnableCap.CullFace);GL.Disable(EnableCap.FramebufferSrgb);
            GL.Enable(EnableCap.Blend);GL.BlendFunc(BlendingFactor.SrcAlpha,BlendingFactor.OneMinusSrcAlpha);
            GL.UseProgram(fogProgram);
            int F(string name)=>GL.GetUniformLocation(fogProgram,name);
            var view=camera.GetViewMatrix();var inverse=(view*projection).Inverted();
            GL.UniformMatrix4(F("inverseViewProjection"),false,ref inverse);GL.Uniform3(F("eye"),camera.GetEyePosition());
            GL.Uniform3(F("nearColor"),sky.FogNearColor);GL.Uniform3(F("farColor"),sky.FogFarColor);
            float range=sky.FogFar-sky.FogNear;
            GL.Uniform4(F("fogParam"),sky.FogNear/range,1/range,sky.FogPower,sky.FogMax);
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,sceneDepth);GL.Uniform1(F("sceneDepth"),0);
            GL.BindVertexArray(emptyVao);GL.DrawArrays(PrimitiveType.Triangles,0,3);
            FogDrawn=true;
        });
    }

    void CountFacing(SkySurface surface,Vector3 eye)
    {
        var geo=surface.Mesh.MeshData.Meshgeometry;var transform=surface.Mesh.MeshData.Transform;
        string name=surface.Mesh.MeshData.Shape?.ShapeName??"(unnamed)";
        if(geo.Vertices==null||geo.Indices==null)return;
        Vector3 At(uint i){var p=geo.Vertices[i];return Vector3.TransformPosition(new Vector3((float)p.X,(float)p.Y,(float)p.Z),transform);}
        bool mirrored=transform.Determinant<0;int facing=0,total=0;
        for(int t=0;t+2<geo.Indices.Length;t+=3){
            var a=At(geo.Indices[t]);var normal=Vector3.Cross(At(geo.Indices[t+1])-a,At(geo.Indices[t+2])-a);
            if((Vector3.Dot(normal,eye-a)>0)!=mirrored)facing++;total++;
        }
        var previous=Facing.GetValueOrDefault(name);Facing[name]=(previous.Facing+facing,previous.Total+total,surface.DoubleSided);
    }

    void ResizeDepth(int w,int h)
    {
        if(width==w&&height==h)return;width=w;height=h;
        GL.BindTexture(TextureTarget.Texture2D,sceneDepth);
        GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.DepthComponent24,w,h,0,PixelFormat.DepthComponent,PixelType.UnsignedInt,IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)TextureWrapMode.ClampToEdge);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)TextureWrapMode.ClampToEdge);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,fbo);
        GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer,FramebufferAttachment.DepthAttachment,TextureTarget.Texture2D,sceneDepth,0);
        GL.DrawBuffer(DrawBufferMode.None);
        if(GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer)!=FramebufferErrorCode.FramebufferComplete)throw new InvalidOperationException("Fog depth framebuffer is incomplete");
    }

    /// <summary>The sky static's triangles as position, UV and colour, uploaded once (a sky static does not move relative
    /// to its placement, which the model matrix carries).</summary>
    bool Upload(Mesh mesh,out (int Vao,int Vbo,int Count) buffers)
    {
        if(geometry.TryGetValue(mesh,out buffers))return true;
        var geo=mesh.MeshData.Meshgeometry;
        if(geo.Vertices==null||geo.Indices==null||geo.Vertices.Length==0){Warn($"Sky static {mesh.MeshData.Shape?.ShapeName}: no vertex data");return false;}
        bool hasColors=geo.VertexColors!=null&&geo.VertexColors.Length==geo.Vertices.Length;
        var data=new float[geo.Indices.Length*9];int o=0;
        foreach(uint index in geo.Indices){
            int i=checked((int)index);if(i>=geo.Vertices.Length)throw new InvalidDataException("Sky static vertex index exceeds mesh bounds");
            var p=geo.Vertices[i];data[o++]=(float)p.X;data[o++]=(float)p.Y;data[o++]=(float)p.Z;
            var uv=geo.Uvs_Weight!=null&&i<geo.Uvs_Weight.Length?geo.Uvs_Weight[i]:default;data[o++]=uv.X;data[o++]=uv.Y;
            var c=hasColors?geo.VertexColors[i]:new Vector4(1);data[o++]=c.X;data[o++]=c.Y;data[o++]=c.Z;data[o++]=c.W;
        }
        int vao=GL.GenVertexArray(),vbo=GL.GenBuffer();GL.BindVertexArray(vao);GL.BindBuffer(BufferTarget.ArrayBuffer,vbo);
        GL.BufferData(BufferTarget.ArrayBuffer,data.Length*4,data,BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);GL.VertexAttribPointer(0,3,VertexAttribPointerType.Float,false,9*4,0);
        GL.EnableVertexAttribArray(1);GL.VertexAttribPointer(1,2,VertexAttribPointerType.Float,false,9*4,3*4);
        GL.EnableVertexAttribArray(2);GL.VertexAttribPointer(2,4,VertexAttribPointerType.Float,false,9*4,5*4);
        buffers=geometry[mesh]=(vao,vbo,data.Length/9);
        return true;
    }

    /// <summary>The sky static's base texture at full size with its mip chain: a sky plate covers the whole view, so the
    /// view capture's texture cap does not apply to it.</summary>
    (int Id,int Width) Texture(string path)
    {
        if(textures.TryGetValue(path,out var cached))return cached;
        if(path.Length==0||!assets.Exists(path)){TexturesMissing.Add(path.Length==0?"(none)":path);Warn("Sky static texture not found: "+(path.Length==0?"(the shape names none)":path));return textures[path]=(0,0);}
        try{return textures[path]=DdsTexture.Load(assets.Read(path),true,false);}
        catch(Exception e){TexturesMissing.Add(path);Warn($"Sky static texture {path}: {e.Message}");return textures[path]=(0,0);}
    }

    public void Invalidate()
    {
        foreach(var t in textures.Values)if(t.Id!=0)GL.DeleteTexture(t.Id);textures.Clear();
        foreach(var g in geometry.Values){GL.DeleteVertexArray(g.Vao);GL.DeleteBuffer(g.Vbo);}geometry.Clear();
        Diagnostics.Clear();TexturesUsed.Clear();TexturesMissing.Clear();
    }

    public void Dispose()
    {
        Invalidate();
        if(gradientProgram!=0)GL.DeleteProgram(gradientProgram);if(staticProgram!=0)GL.DeleteProgram(staticProgram);if(fogProgram!=0)GL.DeleteProgram(fogProgram);
        if(emptyVao!=0)GL.DeleteVertexArray(emptyVao);if(fbo!=0)GL.DeleteFramebuffer(fbo);if(sceneDepth!=0)GL.DeleteTexture(sceneDepth);
        gradientProgram=staticProgram=fogProgram=emptyVao=fbo=sceneDepth=width=height=0;
    }
}

/// <summary>Uploads a DDS with its whole mip chain, at full size.</summary>
static class DdsTexture
{
    public static (int Id,int Width) Load(byte[] data,bool repeat,bool nearest)
    {
        var image=DirectXTexWrapperCLI.Loader.LoadTextures([data],false,true)[0];
        if(!image.Loaded||image.Levels.Count==0)throw new InvalidDataException("DDS decoding failed");
        int texture=GL.GenTexture();GL.BindTexture(TextureTarget.Texture2D,texture);
        for(int level=0;level<image.Levels.Count;level++){var l=image.Levels[level];GL.TexImage2D(TextureTarget.Texture2D,level,(PixelInternalFormat)image.GlInternalFormat,l.Width,l.Height,0,(PixelFormat)image.GlPixelFormat,(PixelType)image.GlPixelType,l.Data);}
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMaxLevel,image.Levels.Count-1);
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)(nearest?TextureMinFilter.Nearest:image.Levels.Count>1?TextureMinFilter.LinearMipmapLinear:TextureMinFilter.Linear));
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)(nearest?TextureMagFilter.Nearest:TextureMagFilter.Linear));
        var wrap=(int)(repeat?TextureWrapMode.Repeat:TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,wrap);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,wrap);
        return(texture,image.Levels[0].Width);
    }
}
