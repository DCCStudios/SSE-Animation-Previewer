using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;


namespace SSEPreview;

// Tiled DDS ground material at Z = 0, drawn before meshes and the ground-shadow overlay.
public sealed class GridFloor : IDisposable
{
    public const string TextureFile="testgrid_d.dds";
    public static string TexturePath=>Path.Combine(AppContext.BaseDirectory,"Resources",TextureFile);
    public bool Enabled=true;
    // Ground extent and repeat size in Skyrim units.
    public float Size=6144;
    public string? Diagnostic {get;private set;}
    public int LastDrawn {get;private set;}
    int program,vao,vbo;readonly int[] textures=new int[3];bool textureFailed;
    void Initialize()
    {
        if(program!=0)return;
        int Compile(ShaderType type,string text){
            int s=GL.CreateShader(type);GL.ShaderSource(s,text);GL.CompileShader(s);GL.GetShader(s,ShaderParameter.CompileStatus,out int ok);
            if(ok==0)throw new InvalidOperationException(GL.GetShaderInfoLog(s));return s;
        }
        int vs=Compile(ShaderType.VertexShader,"""
            #version 330 core
            layout(location=0) in vec2 corner;
            uniform mat4 vp;uniform float halfSize;
            out vec2 uv;out vec3 world;
            void main(){uv=corner*.5+.5;world=vec3(corner*halfSize,0);gl_Position=vp*vec4(world,1);}
            """);
        int fs=Compile(ShaderType.FragmentShader,"""
            #version 330 core
            in vec2 uv;in vec3 world;
            uniform sampler2D image;uniform sampler2D normalMap;uniform sampler2D specularMap;
            uniform float repeats;uniform vec3 eye;uniform vec3 ambient;
            uniform vec3 lightDir[4];uniform vec3 lightColor[4];
            out vec4 frag;
            void main(){
                if(length(uv-.5)>.49)discard;
                vec2 tiled=uv*repeats;
                vec3 albedo=pow(texture(image,tiled).rgb,vec3(2.2));
                vec3 n=normalize(texture(normalMap,tiled).xyz*2-1);
                vec3 spec=texture(specularMap,tiled).rgb;
                vec3 v=normalize(eye-world);vec3 color=albedo*ambient;
                for(int i=0;i<4;i++){
                    float ndl=max(dot(n,lightDir[i]),0);
                    vec3 h=normalize(lightDir[i]+v);
                    color+=lightColor[i]*(albedo*ndl+spec*pow(max(dot(n,h),0),32)*ndl*.25);
                }
                frag=vec4(pow(max(color,vec3(0)),vec3(1/2.2)),1);
            }
            """);
        program=GL.CreateProgram();GL.AttachShader(program,vs);GL.AttachShader(program,fs);GL.LinkProgram(program);
        GL.GetProgram(program,GetProgramParameterName.LinkStatus,out int link);if(link==0)throw new InvalidOperationException(GL.GetProgramInfoLog(program));
        GL.DeleteShader(vs);GL.DeleteShader(fs);
        float[] corners=[-1,-1,1,-1,1,1,-1,-1,1,1,-1,1];
        vao=GL.GenVertexArray();vbo=GL.GenBuffer();GL.BindVertexArray(vao);GL.BindBuffer(BufferTarget.ArrayBuffer,vbo);
        GL.BufferData(BufferTarget.ArrayBuffer,corners.Length*sizeof(float),corners,BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);GL.VertexAttribPointer(0,2,VertexAttribPointerType.Float,false,2*sizeof(float),0);
        GL.BindVertexArray(0);
    }
    bool LoadTextures()
    {
        if(textures[0]!=0||textureFailed)return textures[0]!=0;
        try{
            string[] names=[TextureFile,"testgrid_n.dds","testgrid_s.dds"];
            for(int i=0;i<3;i++){
                var decoded=DirectXTexWrapperCLI.Loader.LoadTextures([File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Resources",names[i]))],false,true)[0];
                if(!decoded.Loaded||decoded.Levels.Count==0)throw new InvalidDataException("DDS decoder returned no image: "+names[i]);
                var level=decoded.Levels[0];textures[i]=GL.GenTexture();GL.BindTexture(TextureTarget.Texture2D,textures[i]);
                GL.TexImage2D(TextureTarget.Texture2D,0,(PixelInternalFormat)decoded.GlInternalFormat,level.Width,level.Height,0,(PixelFormat)decoded.GlPixelFormat,(PixelType)decoded.GlPixelType,level.Data);
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
                GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.LinearMipmapLinear);
                GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)TextureWrapMode.Repeat);
                GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)TextureWrapMode.Repeat);
                GL.GetFloat((GetPName)0x84FF,out float maxAnisotropy);
                if(GL.GetError()==ErrorCode.NoError&&maxAnisotropy>1)GL.TexParameter(TextureTarget.Texture2D,(TextureParameterName)0x84FE,Math.Min(8,maxAnisotropy));
                while(GL.GetError()!=ErrorCode.NoError){}
            }
            return true;
        }catch(Exception ex){textureFailed=true;Diagnostic="Grid floor texture unavailable: "+ex.Message;foreach(int id in textures)if(id!=0)GL.DeleteTexture(id);Array.Clear(textures);return false;}
    }
    public void Draw(Matrix4 projection,FO4_Base_Library.OrbitCamera camera)
    {
        LastDrawn=0;if(!Enabled)return;
        GL.GetInteger(GetPName.CurrentProgram,out int oldProgram);GL.GetInteger(GetPName.VertexArrayBinding,out int oldVao);
        GL.GetInteger(GetPName.ArrayBufferBinding,out int oldBuffer);GL.GetInteger(GetPName.ActiveTexture,out int oldUnit);
        int[] oldTextures=new int[3];for(int i=0;i<3;i++){GL.ActiveTexture(TextureUnit.Texture0+i);GL.GetInteger(GetPName.TextureBinding2D,out oldTextures[i]);}
        bool blend=GL.IsEnabled(EnableCap.Blend),depth=GL.IsEnabled(EnableCap.DepthTest),cull=GL.IsEnabled(EnableCap.CullFace),offset=GL.IsEnabled(EnableCap.PolygonOffsetFill);
        GL.GetInteger(GetPName.DepthFunc,out int oldDepthFunc);GL.GetBoolean(GetPName.DepthWritemask,out bool depthMask);
        GL.GetFloat(GetPName.PolygonOffsetFactor,out float oldFactor);GL.GetFloat(GetPName.PolygonOffsetUnits,out float oldUnits);
        try{
            Initialize();if(!LoadTextures())return;
            GL.UseProgram(program);GL.BindVertexArray(vao);
            for(int i=0;i<3;i++){GL.ActiveTexture(TextureUnit.Texture0+i);GL.BindTexture(TextureTarget.Texture2D,textures[i]);}
            var vp=camera.GetViewMatrix()*projection;
            GL.UniformMatrix4(GL.GetUniformLocation(program,"vp"),false,ref vp);
            GL.Uniform1(GL.GetUniformLocation(program,"halfSize"),Size*.5f);
            GL.Uniform1(GL.GetUniformLocation(program,"image"),0);
            GL.Uniform1(GL.GetUniformLocation(program,"normalMap"),1);GL.Uniform1(GL.GetUniformLocation(program,"specularMap"),2);
            GL.Uniform1(GL.GetUniformLocation(program,"repeats"),Size/512f);
            var eye=camera.GetViewMatrix().Inverted().Row3.Xyz;GL.Uniform3(GL.GetUniformLocation(program,"eye"),eye);
            var rig=FO4_Base_Library.Config_App.Current.ActiveLights();
            GL.Uniform3(GL.GetUniformLocation(program,"ambient"),rig.AmbientSkyColor.ToVector3()*rig.AmbientIntensity);
            FO4_Base_Library.PreviewLight[] lights=[rig.KeyLight,rig.FillLeft,rig.FillRight,rig.BackLight];
            for(int i=0;i<4;i++){
                GL.Uniform3(GL.GetUniformLocation(program,$"lightDir[{i}]"),lights[i].Direction());
                var c=lights[i].Diffuse();GL.Uniform3(GL.GetUniformLocation(program,$"lightColor[{i}]"),new Vector3(MathF.Pow(c.X,2.2f),MathF.Pow(c.Y,2.2f),MathF.Pow(c.Z,2.2f)));
            }
            GL.Disable(EnableCap.Blend);GL.Enable(EnableCap.DepthTest);GL.DepthFunc(DepthFunction.Lequal);GL.DepthMask(true);GL.Disable(EnableCap.CullFace);
            // Push the floor slightly back so soles resting exactly on Z = 0 win the depth test.
            GL.Enable(EnableCap.PolygonOffsetFill);GL.PolygonOffset(1,1);
            GL.DrawArrays(PrimitiveType.Triangles,0,6);LastDrawn=1;
        }finally{
            for(int i=0;i<3;i++){GL.ActiveTexture(TextureUnit.Texture0+i);GL.BindTexture(TextureTarget.Texture2D,oldTextures[i]);}GL.ActiveTexture((TextureUnit)oldUnit);
            GL.UseProgram(oldProgram);GL.BindVertexArray(oldVao);GL.BindBuffer(BufferTarget.ArrayBuffer,oldBuffer);
            GL.DepthFunc((DepthFunction)oldDepthFunc);GL.DepthMask(depthMask);GL.PolygonOffset(oldFactor,oldUnits);
            Set(EnableCap.Blend,blend);Set(EnableCap.DepthTest,depth);Set(EnableCap.CullFace,cull);Set(EnableCap.PolygonOffsetFill,offset);
        }
    }
    static void Set(EnableCap cap,bool on){if(on)GL.Enable(cap);else GL.Disable(cap);}
    public void Dispose(){foreach(int id in textures)if(id!=0)GL.DeleteTexture(id);Array.Clear(textures);if(vbo!=0)GL.DeleteBuffer(vbo);if(vao!=0)GL.DeleteVertexArray(vao);if(program!=0)GL.DeleteProgram(program);vbo=vao=program=0;}
}
