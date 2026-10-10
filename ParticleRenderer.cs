using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using NiflySharp.Structs;
using System.Runtime.InteropServices;
using static SSEPreview.NifAnimation;
using NVector3=System.Numerics.Vector3;
using NVector4=System.Numerics.Vector4;

namespace SSEPreview;

// Dedicated billboard pass. Uses authored DDS textures and alpha state, never substitute effect artwork.
public sealed class ParticleRenderer : IDisposable
{
    readonly Assets assets;
    readonly Dictionary<string,int> textures=new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Diagnostics=[];
    int program,vao,vbo;
    float[] vertexBuffer=new float[60*1024];int vertexUsed;
    void Push(float x,float y,float z,float s,float t,float r,float g,float b,float a,float o){
        if(vertexUsed+10>vertexBuffer.Length)Array.Resize(ref vertexBuffer,vertexBuffer.Length*2);
        var d=vertexBuffer;int i=vertexUsed;d[i]=x;d[i+1]=y;d[i+2]=z;d[i+3]=s;d[i+4]=t;d[i+5]=r;d[i+6]=g;d[i+7]=b;d[i+8]=a;d[i+9]=o;vertexUsed+=10;
    }
    bool texturesInvalidated;
    public void InvalidateTextures(){texturesInvalidated=true;Diagnostics.Clear();}
    public int LastDrawn {get;private set;}
    public void ClearDrawStatistics(){LastDrawn=0;DebugState="Drawn 0 particles; no visible particle systems";}
    public string DebugState="";
    public ParticleRenderer(Assets assets){this.assets=assets;}
    void Initialize()
    {
        if(program!=0)return;
        int Compile(ShaderType type,string text){
            int s=GL.CreateShader(type);GL.ShaderSource(s,text);GL.CompileShader(s);GL.GetShader(s,ShaderParameter.CompileStatus,out int ok);
            if(ok==0)throw new InvalidOperationException(GL.GetShaderInfoLog(s));return s;
        }
        int vs=Compile(ShaderType.VertexShader,"""
            #version 330 core
            layout(location=0) in vec3 position;
            layout(location=1) in vec2 uv;
            layout(location=2) in vec4 color;
            layout(location=3) in float angularOpacity;
            uniform mat4 vp;
            out vec2 texcoord;out vec4 tint;out float falloffOpacity;
            void main(){gl_Position=vp*vec4(position,1);texcoord=uv;tint=color;falloffOpacity=angularOpacity;}
            """);
        int fs=Compile(ShaderType.FragmentShader,"""
            #version 330 core
            in vec2 texcoord;in vec4 tint;in float falloffOpacity;
            uniform sampler2D image;uniform sampler2D palette;
            uniform int usePalette;uniform int paletteAlpha;uniform float alphaThreshold;uniform int alphaFunction;
            uniform vec2 uvScale;uniform vec2 uvOffset;
            uniform vec4 baseColor;uniform float baseScale;
            uniform vec3 effectLight;uniform float lightingInfluence;
            out vec4 frag;
            void main(){
                vec4 t=texture(image,texcoord*uvScale+uvOffset);
                
                frag=t*tint*baseColor;
                frag.rgb*=baseScale;
                frag.a*=falloffOpacity;
                // Palette lookup replaces RGB, so its result needs the authored scale too.
                if(usePalette!=0)frag.rgb=baseScale*texture(palette,vec2(t.g,baseColor.r*tint.r)).rgb;
                if(paletteAlpha!=0)frag.a=texture(palette,vec2(t.a,baseColor.a*tint.a*falloffOpacity)).a;
                frag.rgb=mix(frag.rgb,frag.rgb*effectLight,lightingInfluence);
                bool pass=alphaFunction==0 || (alphaFunction==1 && frag.a<alphaThreshold)
                    || (alphaFunction==2 && frag.a==alphaThreshold) || (alphaFunction==3 && frag.a<=alphaThreshold)
                    || (alphaFunction==4 && frag.a>alphaThreshold) || (alphaFunction==5 && frag.a!=alphaThreshold)
                    || (alphaFunction==6 && frag.a>=alphaThreshold);
                if(!pass)discard;
            }
            """);
        program=GL.CreateProgram();GL.AttachShader(program,vs);GL.AttachShader(program,fs);GL.LinkProgram(program);
        GL.GetProgram(program,GetProgramParameterName.LinkStatus,out int link);if(link==0)throw new InvalidOperationException(GL.GetProgramInfoLog(program));
        GL.DeleteShader(vs);GL.DeleteShader(fs);
        vao=GL.GenVertexArray();vbo=GL.GenBuffer();GL.BindVertexArray(vao);GL.BindBuffer(BufferTarget.ArrayBuffer,vbo);
        GL.EnableVertexAttribArray(0);GL.VertexAttribPointer(0,3,VertexAttribPointerType.Float,false,10*sizeof(float),0);
        GL.EnableVertexAttribArray(1);GL.VertexAttribPointer(1,2,VertexAttribPointerType.Float,false,10*sizeof(float),3*sizeof(float));
        GL.EnableVertexAttribArray(2);GL.VertexAttribPointer(2,4,VertexAttribPointerType.Float,false,10*sizeof(float),5*sizeof(float));
        GL.EnableVertexAttribArray(3);GL.VertexAttribPointer(3,1,VertexAttribPointerType.Float,false,10*sizeof(float),9*sizeof(float));
        GL.BindVertexArray(0);
    }
    int Texture(string path)
    {
        if(textures.TryGetValue(path,out int existing))return existing;
        if(string.IsNullOrWhiteSpace(path)){
            int white=GL.GenTexture();GL.BindTexture(TextureTarget.Texture2D,white);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba8,1,1,0,PixelFormat.Rgba,PixelType.UnsignedByte,new byte[]{255,255,255,255});
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
            return textures[path]=white;
        }
        try{
            var decoded=DirectXTexWrapperCLI.Loader.LoadTextures([assets.Read(path)],false,true)[0];
            if(!decoded.Loaded||decoded.Levels.Count==0)throw new InvalidDataException("DDS decoder returned no image");
            var level=decoded.Levels[0];int id=GL.GenTexture();GL.BindTexture(TextureTarget.Texture2D,id);
            GL.TexImage2D(TextureTarget.Texture2D,0,(PixelInternalFormat)decoded.GlInternalFormat,level.Width,level.Height,0,(PixelFormat)decoded.GlPixelFormat,(PixelType)decoded.GlPixelType,level.Data);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)TextureWrapMode.Repeat);
            return textures[path]=id;
        }catch(Exception ex){Diagnostics.Add("Particle texture "+path+": "+ex.Message);return textures[path]=0;}
    }
    public void Draw(IEnumerable<(ParticleNif System,double Time)> systems,Matrix4 projection,FO4_Base_Library.OrbitCamera camera)
    {
        LastDrawn=0;
        GL.GetInteger(GetPName.CurrentProgram,out int oldProgram);GL.GetInteger(GetPName.VertexArrayBinding,out int oldVao);
        GL.GetInteger(GetPName.ArrayBufferBinding,out int oldBuffer);GL.GetInteger(GetPName.ActiveTexture,out int oldUnit);
        GL.GetInteger(GetPName.BlendSrcRgb,out int oldSrc);GL.GetInteger(GetPName.BlendDstRgb,out int oldDst);
        GL.GetInteger(GetPName.BlendSrcAlpha,out int oldSrcAlpha);GL.GetInteger(GetPName.BlendDstAlpha,out int oldDstAlpha);
        bool blend=GL.IsEnabled(EnableCap.Blend),depth=GL.IsEnabled(EnableCap.DepthTest),cull=GL.IsEnabled(EnableCap.CullFace);
        GL.GetInteger(GetPName.DepthFunc,out int oldDepthFunc);
        GL.GetInteger(GetPName.CullFaceMode,out int oldCullMode);GL.GetInteger(GetPName.FrontFace,out int oldFrontFace);
        GL.GetInteger(GetPName.BlendEquationRgb,out int oldEquationRgb);GL.GetInteger(GetPName.BlendEquationAlpha,out int oldEquationAlpha);
        GL.GetBoolean(GetPName.DepthWritemask,out bool depthMask);
        bool[] colorMask=new bool[4];GL.GetBoolean(GetPName.ColorWritemask,colorMask);
        int[] bound=new int[2];for(int i=0;i<2;i++){GL.ActiveTexture(TextureUnit.Texture0+i);GL.GetInteger(GetPName.TextureBinding2D,out bound[i]);}
        try{
            if(texturesInvalidated){foreach(int texture in textures.Values)if(texture!=0)GL.DeleteTexture(texture);textures.Clear();texturesInvalidated=false;}
            Initialize();GL.UseProgram(program);GL.BindVertexArray(vao);GL.Disable(EnableCap.CullFace);GL.DepthMask(false);GL.ColorMask(true,true,true,true);
            GL.DepthFunc(DepthFunction.Lequal);GL.BlendEquation(BlendEquationMode.FuncAdd);
            var vp=camera.GetViewMatrix()*projection;GL.UniformMatrix4(GL.GetUniformLocation(program,"vp"),false,ref vp);
            GL.Uniform1(GL.GetUniformLocation(program,"image"),0);GL.Uniform1(GL.GetUniformLocation(program,"palette"),1);
            var right=new NVector3(camera.right.X,camera.right.Y,camera.right.Z);
            var up=new NVector3(camera.upPlane.X,camera.upPlane.Y,camera.upPlane.Z);
            var eye=camera.GetEyePosition();var nEye=new NVector3(eye.X,eye.Y,eye.Z);
            var batches=systems.SelectMany(item=>item.System.MeshArtwork is {} artwork
                ?artwork.Parts.Select(part=>(item.System,item.Time,Part:(ParticleMeshArtwork.Part?)part))
                :new[]{(item.System,item.Time,Part:(ParticleMeshArtwork.Part?)null)});
            foreach(var item in batches){
                var sys=item.System;if(!sys.Animation.Visible(sys.System))continue;var transform=sys.DisplayTransform(item.Time,camera);
                var part=item.Part;var shader=part?.Shader??sys.Shader;var alphaProperty=part==null?sys.Alpha:part.Alpha;
                GL.ActiveTexture(TextureUnit.Texture0);int texture=Texture(part?.Texture??sys.Texture);if(texture==0)continue;GL.BindTexture(TextureTarget.Texture2D,texture);
                GL.ActiveTexture(TextureUnit.Texture1);bool needsPalette=Get(shader,"HasGreyscaleToPaletteColor") is true||Get(shader,"HasGreyscaleToPaletteAlpha") is true;int palette=needsPalette&&!string.IsNullOrWhiteSpace(part?.Palette??sys.Palette)?Texture(part?.Palette??sys.Palette):0;GL.BindTexture(TextureTarget.Texture2D,palette);
                var color=BaseColor(shader);
                var uvScale=Get(shader,"UVScale");var uvOffset=Get(shader,"UVOffset");
                GL.Uniform2(GL.GetUniformLocation(program,"uvScale"),Number(Get(uvScale,"U"),1),Number(Get(uvScale,"V"),1));
                GL.Uniform2(GL.GetUniformLocation(program,"uvOffset"),Number(Get(uvOffset,"U")),Number(Get(uvOffset,"V")));
                int clamp=(int)Number(Get(shader,"TextureClampMode"),3);
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)((clamp&2)!=0?TextureWrapMode.Repeat:TextureWrapMode.ClampToEdge));
                GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)((clamp&1)!=0?TextureWrapMode.Repeat:TextureWrapMode.ClampToEdge));
                if(palette!=0){GL.ActiveTexture(TextureUnit.Texture1);
                    GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)TextureWrapMode.ClampToEdge);
                    GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)TextureWrapMode.ClampToEdge);}
                GL.Uniform4(GL.GetUniformLocation(program,"baseColor"),color.X,color.Y,color.Z,color.W);
                GL.Uniform1(GL.GetUniformLocation(program,"baseScale"),Number(Get(shader,"BaseColorScale"),1));
                bool gray=Get(shader,"HasGreyscaleToPaletteColor") is true;
                GL.Uniform1(GL.GetUniformLocation(program,"usePalette"),gray&&palette!=0?1:0);
                GL.Uniform1(GL.GetUniformLocation(program,"paletteAlpha"),Get(shader,"HasGreyscaleToPaletteAlpha") is true&&palette!=0?1:0);
                uint flags=Convert.ToUInt32(Get(Get(alphaProperty,"Flags"),"Value")??0);
                if(alphaProperty!=null&&flags==0)flags=ConvertFlags(Get(alphaProperty,"Flags"));
                // No authored NiAlphaProperty: assume the vanilla particle default (additive) rather than opaque quads. See ParticleNif diagnostics.
                if(alphaProperty==null&&shader is NiflySharp.Blocks.BSEffectShaderProperty)flags=AssumedAlphaFlags;
                bool blending=(flags&1)!=0;
                uint shaderFlags1=ConvertFlags(Get(shader,"ShaderFlags_SSPF1")),shaderFlags2=ConvertFlags(Get(shader,"ShaderFlags_SSPF2"));
                Set(EnableCap.CullFace,part!=null&&(shaderFlags2&16)==0);
                if(part!=null){GL.CullFace(CullFaceMode.Back);GL.FrontFace(FrontFaceDirection.Ccw);}
                var radiance=ParticleLighting.Studio(FO4_Base_Library.Config_App.Current.ActiveLights());
                GL.Uniform3(GL.GetUniformLocation(program,"effectLight"),radiance.X,radiance.Y,radiance.Z);
                GL.Uniform1(GL.GetUniformLocation(program,"lightingInfluence"),ParticleLighting.Influence(shaderFlags2,Number(Get(shader,"LightingInfluence"),255)));
                Set(EnableCap.Blend,blending);Set(EnableCap.DepthTest,shader==null||(shaderFlags1&0x80000000u)!=0);
                GL.DepthMask((shaderFlags2&1)!=0);
                GL.Uniform1(GL.GetUniformLocation(program,"alphaFunction"),(flags&512)!=0?(int)((flags>>10)&7):0);
                if(blending)GL.BlendFunc(Factor((int)(flags>>1)&15),Factor((int)(flags>>5)&15));
                GL.Uniform1(GL.GetUniformLocation(program,"alphaThreshold"),(flags&512)!=0?Number(Get(alphaProperty,"Threshold"))/255:0);
                float Angular(NVector3 position,NVector3 normal)=>(shaderFlags1&64)==0?1:ParticleFalloff.Evaluate(normal,nEye-position,Number(Get(shader,"FalloffStartAngle"),1),Number(Get(shader,"FalloffStopAngle"),1),Number(Get(shader,"FalloffStartOpacity")),Number(Get(shader,"FalloffStopOpacity")));
                vertexUsed=0;
                var live=sys.Simulation.Particles;var order=new int[live.Count];var sortDepth=new float[live.Count];
                for(int i=0;i<live.Count;i++){order[i]=i;sortDepth[i]=-NVector3.DistanceSquared(NVector3.Transform(live[i].Position,transform),nEye);}
                Array.Sort(sortDepth,order);
                var particles=order.Select(i=>live[i]);
                foreach(var p in particles){
                    if(part!=null){
                        if(ParticleMeshTransform.TemplateIndex(p.Generation,part.TemplateCount)!=part.Generation)continue;
                        int before=vertexUsed;
                        foreach(var triangle in ParticleMeshArtwork.Vertices(part,p,sys.MeshArtwork!.HasRotation,transform).Chunk(3)){
                            var normal=NVector3.Cross(triangle[1].Position-triangle[0].Position,triangle[2].Position-triangle[0].Position);
                            foreach(var vertex in triangle){var position=vertex.Position;var tint=vertex.Color;
                                Push(position.X,position.Y,position.Z,vertex.UV.X,vertex.UV.Y,tint.X,tint.Y,tint.Z,tint.W,Angular(position,normal));
                            }
                        }
                        if(vertexUsed>before)LastDrawn++;continue;
                    }

                    if(p.Strip is {} strip){
                        int before=vertexUsed;
                        foreach(var triangle in ParticleStripGeometry.Triangles(strip,sys.UV(p)).Chunk(3)){
                          var a=NVector3.Transform(triangle[0].Position,transform);var b=NVector3.Transform(triangle[1].Position,transform);var c=NVector3.Transform(triangle[2].Position,transform);
                          var normal=NVector3.Cross(b-a,c-a);
                          foreach(var vertex in triangle){
                            var position=NVector3.Transform(vertex.Position,transform);var sampleColor=vertex.Color;
                            sampleColor.W*=ParticleDistanceFade.At(NVector3.Distance(position,nEye),sys.System.FarBegin,sys.System.FarEnd,sys.System.NearBegin,sys.System.NearEnd);
                            sampleColor=ParticleVertexColor.Normalize(sampleColor);
                            Push(position.X,position.Y,position.Z,vertex.UV.X,vertex.UV.Y,sampleColor.X,sampleColor.Y,sampleColor.Z,sampleColor.W,Angular(position,normal));
                        }
                        }
                        if(vertexUsed>before)LastDrawn++;continue;
                    }

                    var center=NVector3.Transform(p.Position,transform);float radius=ParticleNif.BillboardRadius(p);
                    float alpha=p.Color.W*ParticleDistanceFade.At(NVector3.Distance(center,nEye),sys.System.FarBegin,sys.System.FarEnd,sys.System.NearBegin,sys.System.NearEnd);
                    // Native billboard generation collapses particles with nonpositive faded alpha.
                    if(!(alpha>0))continue;
                    if(!float.IsFinite(radius)||radius<=0)continue;
                    var velocity=NVector3.TransformNormal(p.Velocity,transform);
                    float vx=NVector3.Dot(velocity,right),vy=NVector3.Dot(velocity,up),len=MathF.Sqrt(vx*vx+vy*vy);
                    float aspect=sys.AspectAtSpeed(len);
                    int aspectFlags=Convert.ToInt32(Get(sys.Data,"AspectFlags")??0);
                    var (r,u)=ParticleOrientation.Axes(right,up,vx,vy,p.Angle,radius,aspect,aspectFlags);
                    if(r==NVector3.Zero&&u==NVector3.Zero)continue;
                    var uv=sys.UV(p);var vertexColor=ParticleVertexColor.Normalize(new NVector4(p.Color.X,p.Color.Y,p.Color.Z,alpha));
                    var facing=NVector3.Cross(r,u);
                    void Vertex(float x,float y,float s,float t){var v=center+r*x+u*y;Push(v.X,v.Y,v.Z,s,t,vertexColor.X,vertexColor.Y,vertexColor.Z,vertexColor.W,Angular(v,facing));}
                    Vertex(-1,-1,uv.X,uv.Y);Vertex(1,-1,uv.Z,uv.Y);Vertex(1,1,uv.Z,uv.W);
                    Vertex(-1,-1,uv.X,uv.Y);Vertex(1,1,uv.Z,uv.W);Vertex(-1,1,uv.X,uv.W);LastDrawn++;
                }
                GL.BindBuffer(BufferTarget.ArrayBuffer,vbo);GL.BufferData(BufferTarget.ArrayBuffer,vertexUsed*sizeof(float),vertexBuffer,BufferUsageHint.StreamDraw);
                GL.DrawArrays(PrimitiveType.Triangles,0,vertexUsed/10);
                DebugState=$"Drawn {LastDrawn} particles; GL status {GL.GetError()}";
            }
        }finally{
            for(int i=0;i<2;i++){GL.ActiveTexture(TextureUnit.Texture0+i);GL.BindTexture(TextureTarget.Texture2D,bound[i]);}
            GL.ActiveTexture((TextureUnit)oldUnit);GL.UseProgram(oldProgram);GL.BindVertexArray(oldVao);GL.BindBuffer(BufferTarget.ArrayBuffer,oldBuffer);
            GL.CullFace((CullFaceMode)oldCullMode);GL.FrontFace((FrontFaceDirection)oldFrontFace);
            GL.DepthFunc((DepthFunction)oldDepthFunc);GL.BlendEquationSeparate((BlendEquationMode)oldEquationRgb,(BlendEquationMode)oldEquationAlpha);
            GL.BlendFuncSeparate((BlendingFactorSrc)oldSrc,(BlendingFactorDest)oldDst,(BlendingFactorSrc)oldSrcAlpha,(BlendingFactorDest)oldDstAlpha);
            Set(EnableCap.Blend,blend);Set(EnableCap.DepthTest,depth);Set(EnableCap.CullFace,cull);GL.DepthMask(depthMask);GL.ColorMask(colorMask[0],colorMask[1],colorMask[2],colorMask[3]);
        }
    }
    // Effect shaders carry opacity in BaseColor.A; lighting shaders keep it in Alpha and have no base colour.
    internal static NVector4 BaseColor(object? shader)=>shader is NiflySharp.Blocks.BSLightingShaderProperty lit?new(1,1,1,lit.Alpha):ParticleNif.Color(Get(shader,"BaseColor"));
    internal const uint AssumedAlphaFlags=4109;
    static uint ConvertFlags(object? flags){try{return Convert.ToUInt32(flags);}catch{return 0;}}
    static void Set(EnableCap cap,bool on){if(on)GL.Enable(cap);else GL.Disable(cap);}
    static BlendingFactor Factor(int n)=>n switch{0=>BlendingFactor.One,1=>BlendingFactor.Zero,2=>BlendingFactor.SrcColor,3=>BlendingFactor.OneMinusSrcColor,4=>BlendingFactor.DstColor,5=>BlendingFactor.OneMinusDstColor,6=>BlendingFactor.SrcAlpha,7=>BlendingFactor.OneMinusSrcAlpha,8=>BlendingFactor.DstAlpha,9=>BlendingFactor.OneMinusDstAlpha,10=>BlendingFactor.SrcAlphaSaturate,_=>BlendingFactor.One};
    public void Dispose(){foreach(int t in textures.Values)if(t!=0)GL.DeleteTexture(t);textures.Clear();if(vbo!=0)GL.DeleteBuffer(vbo);if(vao!=0)GL.DeleteVertexArray(vao);if(program!=0)GL.DeleteProgram(program);program=vao=vbo=0;}
}
