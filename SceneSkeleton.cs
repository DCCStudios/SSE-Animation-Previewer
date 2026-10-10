using FO4_Base_Library;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using M4=OpenTK.Mathematics.Matrix4;
using M4d=OpenTK.Mathematics.Matrix4d;
using V3d=OpenTK.Mathematics.Vector3d;

namespace SSEPreview;

// Character skeleton overlay: every bone of the loaded skeleton (plus bones merged in from equipment) as a joint
// marker with a line to its parent, at the current pose and root motion. Drawn over the meshes so bones inside the
// body stay visible. A filter limits it to chosen bones, which then also show their local axes (X red, Y green,
// Z blue); names can be shown beside each joint.
public sealed partial class Scene
{
    readonly SmpDebugDraw skeletonDraw=new();
    readonly BoneLabelRenderer boneLabels=new();
    public static readonly Color4 BoneLine=new(.35f,.9f,1f,.85f),BoneJoint=new(1f,.95f,.6f,.95f);
    bool showSkeleton,showBoneNames;
    public bool ShowSkeleton{get=>showSkeleton;set{showSkeleton=value;view.UpdateRequired=true;view.Invalidate();}}
    public bool ShowBoneNames{get=>showBoneNames;set{showBoneNames=value;view.UpdateRequired=true;view.Invalidate();}}
    // Bones to show; empty shows them all.
    public HashSet<string> BoneFilter {get;}=new(StringComparer.OrdinalIgnoreCase);
    public void BoneFilterChanged(){view.UpdateRequired=true;view.Invalidate();}
    public IEnumerable<string> BoneNames=>Skeleton.SkeletonDictionary.Keys.OrderBy(n=>n,StringComparer.OrdinalIgnoreCase);
    public int SkeletonLines=>skeletonDraw.LastLines;
    public int BoneLabelsDrawn=>boneLabels.LastDrawn;
    // World position (with root motion) of every bone the overlay shows at a time.
    public List<(string Name,V3d Position,HierarchiBone_class Bone)> VisibleBones(double time)
    {
        var list=new List<(string,V3d,HierarchiBone_class)>();
        var actor=ActorWorld(time);
        foreach(var (name,bone) in Skeleton.SkeletonDictionary){
            if(BoneFilter.Count>0&&!BoneFilter.Contains(name))continue;
            list.Add((name,(bone.GetGlobalTransform.ToMatrix4d()*actor).ExtractTranslation(),bone));
        }
        return list;
    }
    void DrawSkeletonOverlay(M4 projection)
    {
        if(!showSkeleton||Skeleton.SkeletonDictionary.Count==0){skeletonDraw.Begin();skeletonDraw.End();boneLabels.LastDrawn=0;return;}
        var actor=ActorWorld(Time);
        var bones=VisibleBones(Time);bool filtered=BoneFilter.Count>0;
        skeletonDraw.Begin();
        foreach(var (name,position,bone) in bones){
            var world=bone.GetGlobalTransform.ToMatrix4d()*actor;
            if(bone.Parent!=null){
                var parent=(bone.Parent.GetGlobalTransform.ToMatrix4d()*actor).ExtractTranslation();
                if((parent-position).LengthSquared>1e-8)skeletonDraw.Segment(parent,position,BoneLine);
            }
            double s=filtered?.9:.6;
            skeletonDraw.Segment(position-V3d.UnitX*s,position+V3d.UnitX*s,BoneJoint);skeletonDraw.Segment(position-V3d.UnitY*s,position+V3d.UnitY*s,BoneJoint);skeletonDraw.Segment(position-V3d.UnitZ*s,position+V3d.UnitZ*s,BoneJoint);
            if(filtered){
                // Local axes of a chosen bone, 5 units long, from its rotation only.
                V3d Axis(int row)=>new V3d(row==0?world.M11:row==1?world.M21:world.M31,row==0?world.M12:row==1?world.M22:world.M32,row==0?world.M13:row==1?world.M23:world.M33).Normalized();
                skeletonDraw.Segment(position,position+Axis(0)*5,new Color4(1f,.3f,.3f,1f));
                skeletonDraw.Segment(position,position+Axis(1)*5,new Color4(.3f,1f,.3f,1f));
                skeletonDraw.Segment(position,position+Axis(2)*5,new Color4(.35f,.55f,1f,1f));
            }
        }
        skeletonDraw.End();
        skeletonDraw.Draw(projection,view.camera);
        if(showBoneNames)boneLabels.Draw(bones.Select(b=>(b.Name,b.Position)),view.camera.GetViewMatrix()*projection);else boneLabels.LastDrawn=0;
    }
}

// Screen-space text labels: each name is drawn once with GDI into a small texture (white text, dark outline) and
// then placed beside its joint every frame.
public sealed class BoneLabelRenderer:IDisposable
{
    int program,vao,vbo;
    readonly Dictionary<string,(int Texture,int Width,int Height)> labels=new(StringComparer.Ordinal);
    public int LastDrawn;
    static readonly Font LabelFont=new("Segoe UI",8.5f,FontStyle.Regular,GraphicsUnit.Point);
    void Initialize()
    {
        if(program!=0)return;
        int Compile(ShaderType type,string text){int s=GL.CreateShader(type);GL.ShaderSource(s,text);GL.CompileShader(s);GL.GetShader(s,ShaderParameter.CompileStatus,out int ok);if(ok==0)throw new InvalidOperationException(GL.GetShaderInfoLog(s));return s;}
        int vs=Compile(ShaderType.VertexShader,"""
            #version 330 core
            layout(location=0) in vec2 pixel;layout(location=1) in vec2 uv;
            uniform vec2 screen;out vec2 tex;
            void main(){tex=uv;gl_Position=vec4(pixel/screen*2.0-1.0,0,1);}
            """);
        int fs=Compile(ShaderType.FragmentShader,"""
            #version 330 core
            in vec2 tex;uniform sampler2D label;out vec4 frag;
            void main(){frag=texture(label,tex);}
            """);
        program=GL.CreateProgram();GL.AttachShader(program,vs);GL.AttachShader(program,fs);GL.LinkProgram(program);GL.DeleteShader(vs);GL.DeleteShader(fs);
        vao=GL.GenVertexArray();vbo=GL.GenBuffer();GL.BindVertexArray(vao);GL.BindBuffer(BufferTarget.ArrayBuffer,vbo);
        GL.EnableVertexAttribArray(0);GL.VertexAttribPointer(0,2,VertexAttribPointerType.Float,false,4*sizeof(float),0);
        GL.EnableVertexAttribArray(1);GL.VertexAttribPointer(1,2,VertexAttribPointerType.Float,false,4*sizeof(float),2*sizeof(float));
        GL.BindVertexArray(0);
    }
    (int Texture,int Width,int Height) Label(string text)
    {
        if(labels.TryGetValue(text,out var cached))return cached;
        var size=TextRenderer.MeasureText(text,LabelFont,Size.Empty,TextFormatFlags.NoPadding);
        int w=size.Width+6,h=size.Height+4;
        using var bitmap=new Bitmap(w,h,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using(var g=Graphics.FromImage(bitmap)){
            g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            using var outline=new SolidBrush(Color.FromArgb(220,10,12,16));
            for(int dx=-1;dx<=1;dx++)for(int dy=-1;dy<=1;dy++)if(dx!=0||dy!=0)g.DrawString(text,LabelFont,outline,3+dx,2+dy);
            g.DrawString(text,LabelFont,Brushes.White,3,2);
        }
        // GDI rows run top-down; flip so row 0 is the bottom, as GL samples it.
        bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);
        var data=bitmap.LockBits(new Rectangle(0,0,w,h),ImageLockMode.ReadOnly,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        int texture=GL.GenTexture();GL.BindTexture(TextureTarget.Texture2D,texture);GL.PixelStore(PixelStoreParameter.UnpackAlignment,4);
        GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba8,w,h,0,OpenTK.Graphics.OpenGL4.PixelFormat.Bgra,PixelType.UnsignedByte,data.Scan0);
        bitmap.UnlockBits(data);
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)TextureWrapMode.ClampToEdge);GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)TextureWrapMode.ClampToEdge);
        return labels[text]=(texture,w,h);
    }
    public void Draw(IEnumerable<(string Name,V3d Position)> items,Matrix4 viewProjection)
    {
        LastDrawn=0;
        var viewportValues=new int[4];GL.GetInteger(GetPName.Viewport,viewportValues);var viewport=new Vector4i(viewportValues[0],viewportValues[1],viewportValues[2],viewportValues[3]);if(viewport.Z<=0||viewport.W<=0)return;
        GL.GetInteger(GetPName.CurrentProgram,out int oldProgram);GL.GetInteger(GetPName.VertexArrayBinding,out int oldVao);GL.GetInteger(GetPName.ArrayBufferBinding,out int oldBuffer);GL.GetInteger(GetPName.TextureBinding2D,out int oldTexture);
        GL.GetInteger(GetPName.ActiveTexture,out int oldUnit);
        bool blend=GL.IsEnabled(EnableCap.Blend),depth=GL.IsEnabled(EnableCap.DepthTest),cull=GL.IsEnabled(EnableCap.CullFace);
        GL.GetInteger(GetPName.BlendSrcRgb,out int srcRgb);GL.GetInteger(GetPName.BlendDstRgb,out int dstRgb);GL.GetInteger(GetPName.BlendSrcAlpha,out int srcAlpha);GL.GetInteger(GetPName.BlendDstAlpha,out int dstAlpha);
        GL.GetBoolean(GetPName.DepthWritemask,out bool depthMask);
        try{
            Initialize();
            GL.UseProgram(program);GL.BindVertexArray(vao);GL.BindBuffer(BufferTarget.ArrayBuffer,vbo);GL.ActiveTexture(TextureUnit.Texture0);
            GL.Uniform2(GL.GetUniformLocation(program,"screen"),(float)viewport.Z,(float)viewport.W);GL.Uniform1(GL.GetUniformLocation(program,"label"),0);
            GL.Disable(EnableCap.DepthTest);GL.DepthMask(false);GL.Disable(EnableCap.CullFace);GL.Enable(EnableCap.Blend);
            GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha,BlendingFactorDest.OneMinusSrcAlpha,BlendingFactorSrc.One,BlendingFactorDest.OneMinusSrcAlpha);
            var quad=new float[24];
            foreach(var (name,position) in items){
                var clip=new Vector4((float)position.X,(float)position.Y,(float)position.Z,1)*viewProjection;
                if(clip.W<=1e-4f)continue;
                float x=(clip.X/clip.W*.5f+.5f)*viewport.Z+5,y=(clip.Y/clip.W*.5f+.5f)*viewport.W+2;
                if(x>viewport.Z||y>viewport.W||x<-300||y<-30)continue;
                var (texture,w,h)=Label(name);x=MathF.Round(x);y=MathF.Round(y);
                float[] corners=[x,y,0,0, x+w,y,1,0, x+w,y+h,1,1, x,y,0,0, x+w,y+h,1,1, x,y+h,0,1];
                Array.Copy(corners,quad,24);
                GL.BindTexture(TextureTarget.Texture2D,texture);
                GL.BufferData(BufferTarget.ArrayBuffer,quad.Length*sizeof(float),quad,BufferUsageHint.StreamDraw);
                GL.DrawArrays(PrimitiveType.Triangles,0,6);LastDrawn++;
            }
        }finally{
            GL.UseProgram(oldProgram);GL.BindVertexArray(oldVao);GL.BindBuffer(BufferTarget.ArrayBuffer,oldBuffer);GL.ActiveTexture((TextureUnit)oldUnit);GL.BindTexture(TextureTarget.Texture2D,oldTexture);
            GL.BlendFuncSeparate((BlendingFactorSrc)srcRgb,(BlendingFactorDest)dstRgb,(BlendingFactorSrc)srcAlpha,(BlendingFactorDest)dstAlpha);GL.DepthMask(depthMask);
            if(blend)GL.Enable(EnableCap.Blend);else GL.Disable(EnableCap.Blend);if(depth)GL.Enable(EnableCap.DepthTest);else GL.Disable(EnableCap.DepthTest);if(cull)GL.Enable(EnableCap.CullFace);else GL.Disable(EnableCap.CullFace);
        }
    }
    public void Dispose()
    {
        foreach(var (texture,_,_) in labels.Values)GL.DeleteTexture(texture);labels.Clear();
        if(vbo!=0)GL.DeleteBuffer(vbo);if(vao!=0)GL.DeleteVertexArray(vao);if(program!=0)GL.DeleteProgram(program);vbo=vao=program=0;
    }
}
