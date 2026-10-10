using FO4_Base_Library;
using NiflySharp.Blocks;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using static SSEPreview.NifAnimation;
using Mesh=FO4_Base_Library.PreviewModel.RenderableMesh;

namespace SSEPreview;

// Utility RENDER_NORMAL + ISRefraction. Native input evidence is recorded in
// verification/refraction-layer-diagnostics/distortion-pass-research.md.
public sealed class RefractionRenderer(Assets assets):IDisposable
{
    readonly Dictionary<string,int> textures=new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Diagnostics=[];
    int geometryProgram,composeProgram,vao,vbo,fbo,sceneTexture,normalTexture,depthBuffer,width,height;
    bool invalidated;
    public int LastDrawn {get;private set;}
    public void InvalidateTextures(){invalidated=true;Diagnostics.Clear();}
    void Warn(string value){if(!Diagnostics.Contains(value))Diagnostics.Add(value);}
    static uint Flags(object? value){try{return Convert.ToUInt32(value);}catch{return 0;}}
    static void Enable(EnableCap cap,bool value){if(value)GL.Enable(cap);else GL.Disable(cap);}
    static int Program(string vertex,string fragment){
        int Compile(ShaderType type,string source){int s=GL.CreateShader(type);GL.ShaderSource(s,source);GL.CompileShader(s);GL.GetShader(s,ShaderParameter.CompileStatus,out int ok);if(ok==0){string error=GL.GetShaderInfoLog(s);GL.DeleteShader(s);throw new InvalidOperationException(error);}return s;}
        int vs=Compile(ShaderType.VertexShader,vertex),fs=Compile(ShaderType.FragmentShader,fragment),p=GL.CreateProgram();
        GL.AttachShader(p,vs);GL.AttachShader(p,fs);GL.LinkProgram(p);GL.DeleteShader(vs);GL.DeleteShader(fs);GL.GetProgram(p,GetProgramParameterName.LinkStatus,out int linked);if(linked==0)throw new InvalidOperationException(GL.GetProgramInfoLog(p));return p;
    }
    void Initialize(){
        if(geometryProgram!=0)return;
        geometryProgram=Program("""
            #version 330 core
            layout(location=0) in vec3 position;
            layout(location=1) in vec3 normal;
            layout(location=2) in vec2 uv;
            layout(location=3) in float alpha;
            uniform mat4 model,view,projection;
            uniform vec3 eye;
            uniform vec2 uvScale,uvOffset;
            uniform int clampNormal,falloff,vertexColors;
            out vec4 coordinates;out vec3 viewNormal;out float strength;
            void main(){
                vec4 world=model*vec4(position,1);gl_Position=projection*view*world;
                viewNormal=(view*model*vec4(normal,0)).xyz;
                if(clampNormal!=0)viewNormal=clamp(viewNormal,vec3(-.1),vec3(.1));
                float angular=1;
                if(falloff!=0)angular=dot(normal,normalize((inverse(model)*vec4(eye,1)).xyz-position));
                // Convert OpenGL clip z to the D3D [0,w] convention before depth attenuation.
                coordinates=vec4(uv*uvScale+uvOffset,max(1.,.0013333333*((gl_Position.z+gl_Position.w)*.5)+.8),angular);
                strength=vertexColors!=0?alpha:1.;
            }
            ""","""
            #version 330 core
            in vec4 coordinates;in vec3 viewNormal;in float strength;
            uniform sampler2D normalMap;uniform float power;uniform int clampNormal;
            out vec4 result;
            void main(){vec2 n=2.*(texture(normalMap,coordinates.xy).rg-.5);
                if(clampNormal!=0)n=clamp(n,vec2(-.1),vec2(.1));
                result=vec4(((n*.9+viewNormal.xy)/coordinates.z)*.5+.5,strength*power*coordinates.w,1.);
            }
            """);
        composeProgram=Program("""
            #version 330 core
            out vec2 uv;
            void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);uv=p;gl_Position=vec4(p*2.-1.,0,1);}
            ""","""
            #version 330 core
            in vec2 uv;out vec4 result;uniform sampler2D sceneImage,distortion;
            void main(){vec4 n=texture(distortion,uv);
                vec2 original=vec2(uv.x,1.-uv.y);
                vec2 displaced=original+vec2(-1,1)*(.1*n.z*(n.xy-.5));
                vec2 softened=displaced;
                for(int i=0;i<2;i++){if(displaced[i]>.85)softened[i]=mix(.85,displaced[i],.78);if(displaced[i]<.15)softened[i]=mix(.15,displaced[i],.78);}
                vec2 target=mix(displaced,softened,n.z);target.y=1.-target.y;
                result=mix(texture(sceneImage,uv),texture(sceneImage,target),texture(distortion,target).a);
            }
            """);
        vao=GL.GenVertexArray();vbo=GL.GenBuffer();GL.BindVertexArray(vao);GL.BindBuffer(BufferTarget.ArrayBuffer,vbo);
        for(int i=0;i<4;i++){GL.EnableVertexAttribArray(i);GL.VertexAttribPointer(i,i<2?3:i==2?2:1,VertexAttribPointerType.Float,false,9*4,(i==0?0:i==1?3:i==2?6:8)*4);}
        fbo=GL.GenFramebuffer();sceneTexture=GL.GenTexture();normalTexture=GL.GenTexture();depthBuffer=GL.GenRenderbuffer();
    }
    int Texture(string path){
        if(textures.TryGetValue(path,out int cached))return cached;
        try{var image=DirectXTexWrapperCLI.Loader.LoadTextures([assets.Read(path)],false,true)[0];if(!image.Loaded||image.Levels.Count==0)throw new InvalidDataException("DDS decoding failed");
            var level=image.Levels[0];int texture=GL.GenTexture();GL.BindTexture(TextureTarget.Texture2D,texture);
            GL.TexImage2D(TextureTarget.Texture2D,0,(PixelInternalFormat)image.GlInternalFormat,level.Width,level.Height,0,(PixelFormat)image.GlPixelFormat,(PixelType)image.GlPixelType,level.Data);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);return textures[path]=texture;
        }catch(Exception e){Warn($"Refraction texture {path}: {e.Message}");return textures[path]=0;}
    }
    void Resize(int w,int h){
        if(width==w&&height==h)return;width=w;height=h;
        foreach(int texture in new[]{sceneTexture,normalTexture}){GL.BindTexture(TextureTarget.Texture2D,texture);GL.TexImage2D(TextureTarget.Texture2D,0,texture==normalTexture?PixelInternalFormat.Rgba16f:PixelInternalFormat.Rgba8,w,h,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)TextureWrapMode.ClampToEdge);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)TextureWrapMode.ClampToEdge);}
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer,depthBuffer);GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer,RenderbufferStorage.DepthComponent24,w,h);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,fbo);GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,normalTexture,0);
        GL.FramebufferRenderbuffer(FramebufferTarget.DrawFramebuffer,FramebufferAttachment.DepthAttachment,RenderbufferTarget.Renderbuffer,depthBuffer);
        if(GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer)!=FramebufferErrorCode.FramebufferComplete)throw new InvalidOperationException("Refraction framebuffer is incomplete");
    }
    public void Draw(IReadOnlyList<Mesh> meshes,Matrix4 projection,OrbitCamera camera,Func<Mesh,float>? normalizedFadeDistance=null){
        LastDrawn=0;if(meshes.Count==0)return;
        GL.GetInteger(GetPName.DrawFramebufferBinding,out int oldDraw);GL.GetInteger(GetPName.ReadFramebufferBinding,out int oldRead);
        GL.GetInteger(GetPName.CurrentProgram,out int oldProgram);GL.GetInteger(GetPName.VertexArrayBinding,out int oldVao);GL.GetInteger(GetPName.ArrayBufferBinding,out int oldBuffer);
        GL.GetInteger(GetPName.RenderbufferBinding,out int oldRenderbuffer);GL.GetInteger(GetPName.ActiveTexture,out int oldUnit);GL.GetInteger(GetPName.DepthFunc,out int oldDepth);
        int[] viewport=new int[4];GL.GetInteger(GetPName.Viewport,viewport);float[] clear=new float[4];GL.GetFloat(GetPName.ColorClearValue,clear);
        bool[] mask=new bool[4];GL.GetBoolean(GetPName.ColorWritemask,mask);GL.GetBoolean(GetPName.DepthWritemask,out bool depthMask);
        var caps=new[]{EnableCap.Blend,EnableCap.DepthTest,EnableCap.CullFace,EnableCap.ScissorTest,EnableCap.StencilTest,EnableCap.FramebufferSrgb};var states=caps.Select(GL.IsEnabled).ToArray();
        int[] bound=new int[2];for(int i=0;i<2;i++){GL.ActiveTexture(TextureUnit.Texture0+i);GL.GetInteger(GetPName.TextureBinding2D,out bound[i]);}
        try{
            GL.ActiveTexture(TextureUnit.Texture0);if(invalidated){foreach(int t in textures.Values)if(t!=0)GL.DeleteTexture(t);textures.Clear();invalidated=false;}
            Initialize();Resize(viewport[2],viewport[3]);foreach(var cap in caps)GL.Disable(cap);GL.ColorMask(true,true,true,true);GL.DepthMask(false);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer,oldDraw);GL.BindTexture(TextureTarget.Texture2D,sceneTexture);
            GL.CopyTexSubImage2D(TextureTarget.Texture2D,0,0,0,viewport[0],viewport[1],width,height);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,fbo);GL.Viewport(0,0,width,height);GL.ClearColor(.5f,.5f,0,0);GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.BlitFramebuffer(viewport[0],viewport[1],viewport[0]+width,viewport[1]+height,0,0,width,height,ClearBufferMask.DepthBufferBit,BlitFramebufferFilter.Nearest);
            GL.UseProgram(geometryProgram);GL.BindVertexArray(vao);GL.BindBuffer(BufferTarget.ArrayBuffer,vbo);GL.DepthFunc(DepthFunction.Lequal);
            var view=camera.GetViewMatrix();var eye=camera.GetEyePosition();GL.UniformMatrix4(GL.GetUniformLocation(geometryProgram,"view"),false,ref view);GL.UniformMatrix4(GL.GetUniformLocation(geometryProgram,"projection"),false,ref projection);GL.Uniform3(GL.GetUniformLocation(geometryProgram,"eye"),eye);
            GL.Uniform1(GL.GetUniformLocation(geometryProgram,"normalMap"),0);
            foreach(var mesh in meshes){
                if(mesh.MeshData.Shape.NifShader is not BSLightingShaderProperty shader){Warn($"Refraction layer {mesh.MeshData.Shape.ShapeName}: effect-shader distortion remains unsupported.");continue;}
                uint sf1=Flags(Get(shader,"ShaderFlags_SSPF1")),sf2=Flags(Get(shader,"ShaderFlags_SSPF2"));

                if((sf2&8)==0)Warn($"Refraction layer {mesh.MeshData.Shape.ShapeName}: fade uses native default category 0 and preview model bounds; game-assigned categories and native bound merging remain unverified.");
                float fade=1;
                if((sf2&8)==0&&normalizedFadeDistance!=null){var state=RefractionFade.At(normalizedFadeDistance(mesh));if(!state.Render)continue;fade=state.Strength;}
                var material=mesh.MeshData.Material.MaterialBase;string path=material.Diffuse_or_Base_Texture;
                int texture=Texture(path);if(texture==0)continue;GL.BindTexture(TextureTarget.Texture2D,texture);
                int clamp=(int)Number(Get(shader,"TextureClampMode"),3);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)((clamp&2)!=0?TextureWrapMode.Repeat:TextureWrapMode.ClampToEdge));GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)((clamp&1)!=0?TextureWrapMode.Repeat:TextureWrapMode.ClampToEdge));
                Enable(EnableCap.DepthTest,(sf1&0x80000000)!=0);Enable(EnableCap.CullFace,(sf2&16)==0);
                var model=mesh.MeshData.Transform;GL.UniformMatrix4(GL.GetUniformLocation(geometryProgram,"model"),false,ref model);
                GL.Uniform2(GL.GetUniformLocation(geometryProgram,"uvScale"),shader.UVScale.U,shader.UVScale.V);GL.Uniform2(GL.GetUniformLocation(geometryProgram,"uvOffset"),shader.UVOffset.U,shader.UVOffset.V);
                GL.Uniform1(GL.GetUniformLocation(geometryProgram,"clampNormal"),(sf1&0x2000)!=0?1:0);GL.Uniform1(GL.GetUniformLocation(geometryProgram,"falloff"),(sf1&0x10000)!=0?1:0);GL.Uniform1(GL.GetUniformLocation(geometryProgram,"vertexColors"),(sf2&32)!=0?1:0);GL.Uniform1(GL.GetUniformLocation(geometryProgram,"power"),shader.RefractionStrength*fade);
                var geo=mesh.MeshData.Meshgeometry;var data=new List<float>();
                var positions=geo.Vertices;OpenTK.Mathematics.Vector3d[]? posedNormals=null;
                if((sf1&2)!=0&&geo.PerVertexSkinMatrix!=null){positions=SkinningHelper.GetWorldVertices(ref geo);posedNormals=SkinningHelper.GetWorldNormals(ref geo);mesh.MeshData.Meshgeometry=geo;}
                foreach(uint index in geo.Indices){int i=checked((int)index);if(i>=geo.Vertices.Length)throw new InvalidDataException("Refraction vertex index exceeds mesh bounds");var p=positions[i];var n=posedNormals==null?geo.Normals[i]:new Vector3((float)posedNormals[i].X,(float)posedNormals[i].Y,(float)posedNormals[i].Z);var uv=geo.Uvs_Weight[i];float alpha=geo.VertexColors!=null&&i<geo.VertexColors.Length?geo.VertexColors[i].W:1;data.AddRange([(float)p.X,(float)p.Y,(float)p.Z,n.X,n.Y,n.Z,uv.X,uv.Y,alpha]);}
                var array=data.ToArray();GL.BufferData(BufferTarget.ArrayBuffer,array.Length*4,array,BufferUsageHint.StreamDraw);GL.DrawArrays(PrimitiveType.Triangles,0,array.Length/9);LastDrawn++;
            }
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,oldDraw);GL.Viewport(viewport[0],viewport[1],width,height);GL.Disable(EnableCap.DepthTest);GL.Disable(EnableCap.CullFace);GL.UseProgram(composeProgram);
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,sceneTexture);GL.ActiveTexture(TextureUnit.Texture1);GL.BindTexture(TextureTarget.Texture2D,normalTexture);
            GL.Uniform1(GL.GetUniformLocation(composeProgram,"sceneImage"),0);GL.Uniform1(GL.GetUniformLocation(composeProgram,"distortion"),1);GL.DrawArrays(PrimitiveType.Triangles,0,3);
        }finally{
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer,oldRead);GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,oldDraw);GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer,oldRenderbuffer);GL.Viewport(viewport[0],viewport[1],viewport[2],viewport[3]);
            GL.ClearColor(clear[0],clear[1],clear[2],clear[3]);GL.ColorMask(mask[0],mask[1],mask[2],mask[3]);GL.DepthMask(depthMask);GL.DepthFunc((DepthFunction)oldDepth);
            for(int i=0;i<caps.Length;i++)Enable(caps[i],states[i]);for(int i=0;i<2;i++){GL.ActiveTexture(TextureUnit.Texture0+i);GL.BindTexture(TextureTarget.Texture2D,bound[i]);}
            GL.ActiveTexture((TextureUnit)oldUnit);GL.UseProgram(oldProgram);GL.BindVertexArray(oldVao);GL.BindBuffer(BufferTarget.ArrayBuffer,oldBuffer);
        }
    }
    public void Dispose(){foreach(int texture in textures.Values)if(texture!=0)GL.DeleteTexture(texture);textures.Clear();if(geometryProgram!=0)GL.DeleteProgram(geometryProgram);if(composeProgram!=0)GL.DeleteProgram(composeProgram);if(vao!=0)GL.DeleteVertexArray(vao);if(vbo!=0)GL.DeleteBuffer(vbo);if(fbo!=0)GL.DeleteFramebuffer(fbo);if(depthBuffer!=0)GL.DeleteRenderbuffer(depthBuffer);if(sceneTexture!=0)GL.DeleteTexture(sceneTexture);if(normalTexture!=0)GL.DeleteTexture(normalTexture);geometryProgram=composeProgram=vao=vbo=fbo=depthBuffer=sceneTexture=normalTexture=width=height=0;}
}
