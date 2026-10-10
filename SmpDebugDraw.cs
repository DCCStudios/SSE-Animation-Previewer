using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using V3d=OpenTK.Mathematics.Vector3d;

namespace SSEPreview;

// Line overlay for the HDT-SMP simulation: collision spheres and plates, bones and their constraints, live contacts.
// Drawn over the scene without depth testing so proxies inside the body stay visible.
public sealed class SmpDebugDraw:IDisposable
{
    public static readonly Color4 KinematicBone=new(.45f,.75f,1f,.95f),DynamicBone=new(1f,.55f,.1f,.95f),Link=new(.95f,.35f,.95f,.8f);
    public static readonly Color4 KinematicSphere=new(.3f,.8f,.9f,.45f),DynamicSphere=new(1f,.9f,.2f,.6f);
    public static readonly Color4 KinematicPlate=new(.3f,.9f,.45f,.5f),DynamicPlate=new(.75f,1f,.3f,.6f),PenetrationSide=new(.9f,.3f,.3f,.55f),Contact=new(1f,.15f,.15f,1f);
    int program,vao,vbo;float[] vertices=new float[1<<16];int used;
    // Filled triangles, drawn after the lines from their own buffer.
    int triangleVao,triangleVbo;float[] triangles=new float[1<<10];int trianglesUsed;
    public int LastTriangles=>trianglesUsed/21;
    public int LastLines {get;private set;}
    public int Builds {get;private set;}
    // The line buffer goes to the GPU only after it changed.
    bool uploaded;
    public int LastSpheres {get;private set;}
    public int LastPlates {get;private set;}
    void Initialize()
    {
        if(program!=0)return;
        int Compile(ShaderType type,string text){
            int s=GL.CreateShader(type);GL.ShaderSource(s,text);GL.CompileShader(s);GL.GetShader(s,ShaderParameter.CompileStatus,out int ok);
            if(ok==0)throw new InvalidOperationException(GL.GetShaderInfoLog(s));return s;
        }
        int vs=Compile(ShaderType.VertexShader,"""
            #version 330 core
            layout(location=0) in vec3 position;layout(location=1) in vec4 tint;
            uniform mat4 vp;uniform float alpha=1.0;out vec4 color;
            void main(){color=vec4(tint.rgb,tint.a*alpha);gl_Position=vp*vec4(position,1);}
            """);
        int fs=Compile(ShaderType.FragmentShader,"""
            #version 330 core
            in vec4 color;out vec4 frag;
            void main(){frag=color;}
            """);
        program=GL.CreateProgram();GL.AttachShader(program,vs);GL.AttachShader(program,fs);GL.LinkProgram(program);
        GL.GetProgram(program,GetProgramParameterName.LinkStatus,out int link);if(link==0)throw new InvalidOperationException(GL.GetProgramInfoLog(program));
        GL.DeleteShader(vs);GL.DeleteShader(fs);
        vao=GL.GenVertexArray();vbo=GL.GenBuffer();GL.BindVertexArray(vao);GL.BindBuffer(BufferTarget.ArrayBuffer,vbo);
        GL.EnableVertexAttribArray(0);GL.VertexAttribPointer(0,3,VertexAttribPointerType.Float,false,7*sizeof(float),0);
        GL.EnableVertexAttribArray(1);GL.VertexAttribPointer(1,4,VertexAttribPointerType.Float,false,7*sizeof(float),3*sizeof(float));
        triangleVao=GL.GenVertexArray();triangleVbo=GL.GenBuffer();GL.BindVertexArray(triangleVao);GL.BindBuffer(BufferTarget.ArrayBuffer,triangleVbo);
        GL.EnableVertexAttribArray(0);GL.VertexAttribPointer(0,3,VertexAttribPointerType.Float,false,7*sizeof(float),0);
        GL.EnableVertexAttribArray(1);GL.VertexAttribPointer(1,4,VertexAttribPointerType.Float,false,7*sizeof(float),3*sizeof(float));
        GL.BindVertexArray(0);
    }
    public void Triangle(V3d a,V3d b,V3d c,Color4 color)
    {
        if(trianglesUsed+21>triangles.Length)Array.Resize(ref triangles,triangles.Length*2);
        foreach(var p in new[]{a,b,c}){triangles[trianglesUsed++]=(float)p.X;triangles[trianglesUsed++]=(float)p.Y;triangles[trianglesUsed++]=(float)p.Z;triangles[trianglesUsed++]=color.R;triangles[trianglesUsed++]=color.G;triangles[trianglesUsed++]=color.B;triangles[trianglesUsed++]=color.A;}
    }
    public void Segment(V3d a,V3d b,Color4 c)=>Line(a,b,c);
    void Line(V3d a,V3d b,Color4 c)
    {
        if(used+14>vertices.Length)Array.Resize(ref vertices,vertices.Length*2);
        vertices[used++]=(float)a.X;vertices[used++]=(float)a.Y;vertices[used++]=(float)a.Z;vertices[used++]=c.R;vertices[used++]=c.G;vertices[used++]=c.B;vertices[used++]=c.A;
        vertices[used++]=(float)b.X;vertices[used++]=(float)b.Y;vertices[used++]=(float)b.Z;vertices[used++]=c.R;vertices[used++]=c.G;vertices[used++]=c.B;vertices[used++]=c.A;
    }
    void Circle(V3d center,V3d u,V3d v,double radius,int segments,Color4 c)
    {
        var previous=center+u*radius;
        for(int i=1;i<=segments;i++){
            double angle=i*Math.Tau/segments;var next=center+(u*Math.Cos(angle)+v*Math.Sin(angle))*radius;
            Line(previous,next,c);previous=next;
        }
    }
    void Cross(V3d center,double size,Color4 c)
    {
        Line(center-V3d.UnitX*size,center+V3d.UnitX*size,c);Line(center-V3d.UnitY*size,center+V3d.UnitY*size,c);Line(center-V3d.UnitZ*size,center+V3d.UnitZ*size,c);
    }
    // Free-form overlays (Precision attack capsules) fill the buffer between Begin and End.
    public void Begin(){used=0;trianglesUsed=0;LastSpheres=LastPlates=0;Builds++;uploaded=false;}
    public void End()=>LastLines=used/14;
    // Wire capsule: a ring at each end, four side lines and two half rings over each cap.
    public void Capsule(V3d a,V3d b,double radius,Color4 c,int segments=16)
    {
        var axis=b-a;double length=axis.Length;
        if(length<1e-6){Circle(a,V3d.UnitX,V3d.UnitY,radius,segments,c);Circle(a,V3d.UnitX,V3d.UnitZ,radius,segments,c);Circle(a,V3d.UnitY,V3d.UnitZ,radius,segments,c);return;}
        var w=axis/length;var u=V3d.Cross(w,Math.Abs(w.Z)<.9?V3d.UnitZ:V3d.UnitX).Normalized();var v=V3d.Cross(w,u);
        Circle(a,u,v,radius,segments,c);Circle(b,u,v,radius,segments,c);
        foreach(var side in new[]{u,-u,v,-v})Line(a+side*radius,b+side*radius,c);
        void Arc(V3d center,V3d across,V3d outward){
            var previous=center+across*radius;
            for(int i=1;i<=segments/2;i++){double angle=i*Math.PI/(segments/2);var next=center+(across*Math.Cos(angle)+outward*Math.Sin(angle))*radius;Line(previous,next,c);previous=next;}
        }
        Arc(b,u,w);Arc(b,v,w);Arc(a,u,-w);Arc(a,v,-w);
    }
    // Builds this frame's lines from the simulation state after its last substep.
    public void Build(SmpWorld world,bool colliders,bool bones,V3d cameraRight,V3d cameraUp)
    {
        used=0;LastSpheres=LastPlates=0;Builds++;uploaded=false;
        if(colliders){
            int sphereCount=world.Systems.SelectMany(s=>s.Bodies).Where(b=>!b.Disabled&&!b.Shape.Triangles).Sum(b=>b.Shape.Colliders.Length);
            // Three rings per sphere up to a few thousand spheres, then one ring facing the camera.
            bool full=sphereCount<=2500;int segments=sphereCount<=800?16:sphereCount<=6000?10:6;
            foreach(var system in world.Systems)foreach(var body in system.Bodies){
                if(body.Disabled||body.Pos.Length==0)continue;
                var shape=body.Shape;
                if(!shape.Triangles){
                    foreach(var collider in shape.Colliders){
                        var center=body.Pos[collider.V0];double radius=body.MarginMultiplier[collider.V0]*shape.Margin;
                        var color=collider.Flexible>SmpCollision.Epsilon?DynamicSphere:KinematicSphere;LastSpheres++;
                        if(!(radius>1e-4)){Cross(center,.15,color);continue;}
                        Circle(center,cameraRight,cameraUp,radius,segments,color);
                        if(full){Circle(center,V3d.UnitX,V3d.UnitY,radius,segments,color);Circle(center,V3d.UnitX,V3d.UnitZ,radius,segments,color);}
                    }
                    continue;
                }
                foreach(var collider in shape.Colliders){
                    V3d p0=body.Pos[collider.V0],p1=body.Pos[collider.V1],p2=body.Pos[collider.V2];
                    var color=collider.Flexible>SmpCollision.Epsilon?DynamicPlate:KinematicPlate;LastPlates++;
                    Line(p0,p1,color);Line(p1,p2,color);Line(p2,p0,color);
                    // The stub at the centroid shows the collision range on each side of the plate: the margin
                    // out along the working normal, and the penetration depth behind it.
                    var raw=V3d.Cross(p1-p0,p2-p0);double length=raw.Length;if(length<1e-9)continue;
                    var normal=raw/length;var centre=(p0+p1+p2)/3;
                    double multiplier=(body.MarginMultiplier[collider.V0]+body.MarginMultiplier[collider.V1]+body.MarginMultiplier[collider.V2])/3;
                    double penetration=shape.Penetration*multiplier,margin=shape.Margin*multiplier;
                    if(penetration<0){normal=-normal;penetration=-penetration;}
                    if(margin>1e-4)Line(centre,centre+normal*margin,color);
                    if(penetration>=SmpCollision.Epsilon)Line(centre,centre-normal*penetration,PenetrationSide);
                    else if(margin>1e-4)Line(centre,centre-normal*margin,color);
                }
            }
            foreach(var contact in world.Contacts){Line(contact.WorldA,contact.WorldB,Contact);Cross(contact.WorldB,.35,Contact);}
        }
        if(bones){
            foreach(var system in world.Systems){
                foreach(var (a,b) in system.Links)Line(a.Current.Origin,b.Current.Origin,Link);
                foreach(var bone in system.Bones){
                    var origin=bone.Current.Origin;var color=bone.Kinematic?KinematicBone:DynamicBone;double size=bone.Kinematic?.6:1.1;
                    Line(origin,origin+SmpTransform.Rotate(bone.Current.Rotation,V3d.UnitX)*size,color);
                    Line(origin,origin+SmpTransform.Rotate(bone.Current.Rotation,V3d.UnitY)*size,color);
                    Line(origin,origin+SmpTransform.Rotate(bone.Current.Rotation,V3d.UnitZ)*size,color);
                    if(!bone.Kinematic)Circle(origin,cameraRight,cameraUp,.45,8,color);
                }
            }
        }
        LastLines=used/14;
    }
    // occluded: alpha for the parts the scene's depth hides. Below 1 the overlay is drawn twice, once faint through
    // everything and once at full strength where nothing covers it; 1 draws it over everything as before.
    // solid: the full-strength pass writes depth, so a closed shape's near faces hide its far ones.
    public void Draw(Matrix4 projection,FO4_Base_Library.OrbitCamera camera,float occluded=1,bool solid=false)
    {
        if(used==0&&trianglesUsed==0)return;
        GL.GetInteger(GetPName.CurrentProgram,out int oldProgram);GL.GetInteger(GetPName.VertexArrayBinding,out int oldVao);GL.GetInteger(GetPName.ArrayBufferBinding,out int oldBuffer);
        bool blend=GL.IsEnabled(EnableCap.Blend),depth=GL.IsEnabled(EnableCap.DepthTest),cull=GL.IsEnabled(EnableCap.CullFace);
        GL.GetInteger(GetPName.BlendSrcRgb,out int srcRgb);GL.GetInteger(GetPName.BlendDstRgb,out int dstRgb);GL.GetInteger(GetPName.BlendSrcAlpha,out int srcAlpha);GL.GetInteger(GetPName.BlendDstAlpha,out int dstAlpha);
        GL.GetBoolean(GetPName.DepthWritemask,out bool depthMask);GL.GetInteger(GetPName.DepthFunc,out int depthFunc);
        try{
            Initialize();
            GL.UseProgram(program);
            if(!uploaded){
                GL.BindVertexArray(vao);GL.BindBuffer(BufferTarget.ArrayBuffer,vbo);GL.BufferData(BufferTarget.ArrayBuffer,used*sizeof(float),vertices,BufferUsageHint.StreamDraw);
                GL.BindVertexArray(triangleVao);GL.BindBuffer(BufferTarget.ArrayBuffer,triangleVbo);GL.BufferData(BufferTarget.ArrayBuffer,trianglesUsed*sizeof(float),triangles,BufferUsageHint.StreamDraw);
                uploaded=true;
            }
            var vp=camera.GetViewMatrix()*projection;GL.UniformMatrix4(GL.GetUniformLocation(program,"vp"),false,ref vp);
            GL.DepthMask(false);GL.Disable(EnableCap.CullFace);GL.Enable(EnableCap.Blend);
            GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha,BlendingFactorDest.OneMinusSrcAlpha,BlendingFactorSrc.One,BlendingFactorDest.OneMinusSrcAlpha);
            int alpha=GL.GetUniformLocation(program,"alpha");
            void Pass(float strength){
                GL.Uniform1(alpha,strength);
                if(trianglesUsed>0){GL.BindVertexArray(triangleVao);GL.DrawArrays(PrimitiveType.Triangles,0,trianglesUsed/7);}
                if(used>0){GL.BindVertexArray(vao);GL.DrawArrays(PrimitiveType.Lines,0,used/7);}
            }
            GL.Disable(EnableCap.DepthTest);Pass(occluded);
            if(occluded<1){GL.Enable(EnableCap.DepthTest);GL.DepthFunc(DepthFunction.Lequal);GL.DepthMask(solid);Pass(1);}
        }finally{
            GL.UseProgram(oldProgram);GL.BindVertexArray(oldVao);GL.BindBuffer(BufferTarget.ArrayBuffer,oldBuffer);
            GL.BlendFuncSeparate((BlendingFactorSrc)srcRgb,(BlendingFactorDest)dstRgb,(BlendingFactorSrc)srcAlpha,(BlendingFactorDest)dstAlpha);GL.DepthMask(depthMask);GL.DepthFunc((DepthFunction)depthFunc);
            Set(EnableCap.Blend,blend);Set(EnableCap.DepthTest,depth);Set(EnableCap.CullFace,cull);
        }
    }
    static void Set(EnableCap cap,bool on){if(on)GL.Enable(cap);else GL.Disable(cap);}
    public void Dispose(){uploaded=false;if(vbo!=0)GL.DeleteBuffer(vbo);if(vao!=0)GL.DeleteVertexArray(vao);if(triangleVbo!=0)GL.DeleteBuffer(triangleVbo);if(triangleVao!=0)GL.DeleteVertexArray(triangleVao);if(program!=0)GL.DeleteProgram(program);vbo=vao=triangleVbo=triangleVao=program=0;}
}
