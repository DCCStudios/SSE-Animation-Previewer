using M4d=OpenTK.Mathematics.Matrix4d;
using Qd=OpenTK.Mathematics.Quaterniond;
using V3d=OpenTK.Mathematics.Vector3d;

namespace SSEPreview;

// Checks for the HDT-SMP port that need no game files: transform conventions, the XML reader's FSMP quirks,
// constraint behaviour under gravity, and the sphere and plate collision tests.
internal static class SmpTests
{
    sealed class TestNode(string name,M4d world,int depth=0):ISmpNode
    {
        public M4d Animated=world;public M4d? Written;
        public string Name=>name;
        public int Depth=>depth;
        public M4d World=>Written??Animated;
        public void Write(M4d world)=>Written=world;
        public void Clear()=>Written=null;
        public V3d Origin=>World.Row3.Xyz;
    }
    static int passed;static readonly List<string> lines=[];
    static void Check(bool ok,string message)
    {
        lines.Add((ok?"PASS ":"FAIL ")+message);File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"smp-tests.txt"),lines);
        if(!ok)throw new InvalidDataException("SMP test failed: "+message);
        passed++;
    }
    static Dictionary<string,TestNode> Chain(string prefix,int count,V3d start,V3d step)
    {
        var nodes=new Dictionary<string,TestNode>(StringComparer.OrdinalIgnoreCase);
        for(int i=0;i<count;i++)nodes[$"{prefix}{i:00}"]=new TestNode($"{prefix}{i:00}",M4d.CreateTranslation(start+step*i),i);
        return nodes;
    }
    const string ChainXml="""
        <?xml version="1.0" encoding="UTF-8"?>
        <system>
          <bone name="link_00"/>
          <bone-default><mass>0.5</mass><inertia x="20" y="20" z="20"/><linearDamping>0.65</linearDamping><angularDamping>0.65</angularDamping><friction>0.2</friction><gravity-factor>1</gravity-factor></bone-default>
          <bone-default><margin-multiplier>1</margin-multiplier></bone-default>
          <bone name="link_01"/><bone name="link_02"/><bone name="link_03"/><bone name="link_04"/>
          <generic-constraint-default>
            <frameInB><basis x="0" y="0" z="0" w="1"/><origin x="0" y="0" z="0"/></frameInB>
            <linearLowerLimit x="0" y="0" z="0"/><linearUpperLimit x="0" y="0" z="0"/>
            <angularLowerLimit x="-1" y="-1" z="-1"/><angularUpperLimit x="1" y="1" z="1"/>
            <linearDamping x="2" y="2" z="2"/>
          </generic-constraint-default>
          <constraint-group>
            <generic-constraint bodyA="link_01" bodyB="link_00"/>
            <generic-constraint bodyA="link_02" bodyB="link_01"/>
            <generic-constraint bodyA="link_03" bodyB="link_02"/>
            <generic-constraint bodyA="link_04" bodyB="link_03"/>
          </constraint-group>
        </system>
        """;
    static SmpSystem Build(SmpWorld world,string xml,Dictionary<string,TestNode> nodes,List<string> log,Func<string,SmpMeshSource?>? mesh=null)
    {
        var system=SmpSystemBuilder.Build("test.xml",xml.Trim(),name=>nodes.GetValueOrDefault(name),mesh??(_=>null),world.EmptyShape,log)??throw new InvalidDataException("SMP test XML built no system: "+string.Join(" | ",log));
        world.Add(system);return system;
    }
    static void Run(SmpWorld world,double seconds,Action<int>? each=null){int frames=(int)Math.Round(seconds*60);for(int i=0;i<frames;i++){each?.Invoke(i);world.Update(1/60d);}}

    internal static int Run()
    {
        passed=0;lines.Clear();
        // Conventions: quaternion rotation, row-vector matrices and FSMP composition order agree.
        var q=Qd.FromAxisAngle(new V3d(.3,-.5,.8).Normalized(),1.1);var v=new V3d(1.5,-2,.75);
        Check((V3d.Transform(v,q)-V3d.TransformPosition(v,M4d.CreateFromQuaternion(q))).Length<1e-12,"Quaternion rotation matches the row-vector matrix built from it");
        var a=new SmpTransform(q,new V3d(4,5,6),1.5);var b=new SmpTransform(Qd.FromAxisAngle(V3d.UnitZ,.4),new V3d(-1,2,3),.8);
        Check(((a*b).Apply(v)-a.Apply(b.Apply(v))).Length<1e-12,"a*b applies b first");
        Check((V3d.TransformPosition(v,(a*b).ToMatrix())-(a*b).Apply(v)).Length<1e-9,"ToMatrix reproduces Apply, scale included");
        Check((V3d.TransformPosition(v,b.ToMatrix()*a.ToMatrix())-(a*b).Apply(v)).Length<1e-9,"Composition equals the row-vector matrix product in reverse order");
        var round=SmpTransform.From(a.ToMatrix());Check((round.Origin-a.Origin).Length<1e-12&&Math.Abs(round.Scale-1.5)<1e-12&&(round.Apply(v)-a.Apply(v)).Length<1e-9,"Matrix round trip keeps rotation, origin and scale");
        Check(((a*a.Inverse()).Apply(v)-v).Length<1e-9,"Inverse undoes a scaled transform");
        Check((V3d.TransformPosition(v,SmpTransformToMatrix(a.Rigid.ToBullet()))-a.Rigid.Apply(v)).Length<1e-9,"Bullet matrices carry the same row-vector layout");

        // XML reader: document order, templates, kinematic and dynamic bones.
        var log=new List<string>();
        using(var world=new SmpWorld(new SmpSolverSettings{Iterations=16})){
            var nodes=Chain("link_",5,new V3d(0,0,100),new V3d(0,0,-10));
            var system=Build(world,ChainXml,nodes,log);
            Check(system.Bones.Count==5&&system.DynamicBones==4&&system.Bones[0].Kinematic&&system.Bones[0].Name=="link_00","First bone takes the initial mass-0 default and stays kinematic; later bones take the new default");
            Check(system.Links.Count==4&&system.Constraints.Count==4,"Constraints inside a constraint-group are built");
            Check(system.Bones[1].MarginMultiplier==1&&Math.Abs(system.Bones[1].LinearDamping-.65)<1e-12,"An unnamed bone-default extends the previous unnamed default instead of replacing it");
            // A hanging chain stays a chain: locked linear limits hold each link 10 units from its parent.
            Run(world,2);
            for(int i=1;i<5;i++){
                double gap=(nodes[$"link_{i:00}"].Origin-nodes[$"link_{i-1:00}"].Origin).Length;
                Check(Math.Abs(gap-10)<.6,$"Link {i} stays about 10 units from its parent while hanging (measured {gap:F3})");
            }
            Check(nodes["link_04"].Written!=null&&nodes["link_00"].Written==null,"Simulated bones are written back; kinematic bones are not");
            Check(Math.Abs(nodes["link_04"].Origin.X)<1&&nodes["link_04"].Origin.Z<65,"A chain hanging straight down stays under its anchor");
            // Move the anchor sideways: the chain follows and trails behind it.
            var anchor=nodes["link_00"];double lag=0;
            Run(world,1,i=>{anchor.Animated=M4d.CreateTranslation(i*1.5,0,100);if(i is >0 and <20)lag=Math.Max(lag,anchor.Origin.X-nodes["link_04"].Origin.X);});
            Check(nodes["link_04"].Origin.X>40&&lag>1,$"The chain follows a moving anchor and trails it at first (largest early lag {lag:F1}, anchor x {anchor.Origin.X:F1}, tip x {nodes["link_04"].Origin.X:F1})");
            for(int i=1;i<5;i++)Check(Math.Abs((nodes[$"link_{i:00}"].Origin-nodes[$"link_{i-1:00}"].Origin).Length-10)<1.5,"Links stay attached while the anchor moves");
            // Reset keeps the hanging pose relative to the anchor and zeroes velocity.
            int resets=world.Resets;world.RequestReset();world.Update(1/60d);
            Check(world.Resets==resets+1&&system.Bones.Where(x=>!x.Kinematic).All(x=>x.Rig.LinearVelocity.Length<60),"A reset re-reads every bone and clears velocity");
            // Carry: a rigid move of the whole simulation leaves relative positions untouched.
            var before=system.Bones.Select(x=>SmpTransform.From(x.Rig.WorldTransform).Origin).ToArray();
            world.Carry(M4d.CreateTranslation(500,0,0));
            var after=system.Bones.Select(x=>SmpTransform.From(x.Rig.WorldTransform).Origin).ToArray();
            Check(Enumerable.Range(0,before.Length).All(i=>(after[i]-before[i]-new V3d(500,0,0)).Length<1e-6),"Carry moves every body by the same rigid transform");
        }

        // Reader details that differ from what an author might expect.
        log.Clear();
        using(var world=new SmpWorld(new SmpSolverSettings())){
            var nodes=Chain("n_",4,new V3d(0,0,50),new V3d(0,5,0));
            const string xml="""
                <system>
                  <bone-default name="heavy"><mass>2,5</mass></bone-default>
                  <bone name="n_00"/>
                  <bone name="n_01" template="heavy"><gravity-factor>3</gravity-factor></bone>
                  <bone name="n_01"/>
                  <bone name="missing_bone"/>
                  <mystery/>
                  <generic-constraint bodyA="n_00" bodyB="n_01"><frameInLerp><translationLerp>0.5</translationLerp><rotationLerp>0.5</rotationLerp></frameInLerp></generic-constraint>
                  <generic-constraint bodyA="n_00" bodyB="n_02"/>
                  <stiffspring-constraint bodyA="n_01" bodyB="n_03"><stiffness>10</stiffness></stiffspring-constraint>
                </system>
                """;
            var system=Build(world,xml,nodes,log);
            var heavy=system.Bones.Single(x=>x.Name=="n_01");
            Check(!heavy.Kinematic&&Math.Abs(1/heavy.Rig.InvMass-2.5)<1e-9,"A decimal comma is read as a decimal point, as FSMP does");
            Check(heavy.GravityFactor==1,"gravity-factor is clamped to 0..1");
            Check(system.Bones.Single(x=>x.Name=="n_00").Kinematic,"A named template does not change the unnamed default");
            Check(log.Any(l=>l.Contains("already exists"))&&log.Any(l=>l.Contains("missing_bone")&&l.Contains("does not exist"))&&log.Any(l=>l.Contains("<mystery>")),"Duplicate bones, missing nodes and unknown elements are reported and skipped");
            Check(log.Any(l=>l.Contains("two kinematic bones"))&&system.Constraints.Count==1,"A constraint between two kinematic bones is skipped; a missing bone is created from the default template first");
            Check(system.Bones.Any(x=>x.Name=="n_03"&&x.Kinematic)&&system.StiffSprings.Count==1&&Math.Abs(system.StiffSprings[0].MinDistance-10)<1e-9,"A stiff spring takes its rest length from the reference pose");
            Run(world,.5);
            Check(system.Bones.All(x=>SmpTransform.From(x.Rig.WorldTransform).Finite),"Mixed constraint types stay finite");
        }
        log.Clear();
        using(var world=new SmpWorld(new SmpSolverSettings())){
            var nodes=Chain("n_",2,V3d.Zero,V3d.UnitZ);
            Check(SmpSystemBuilder.Build("bad.xml","<system><bone-default><mass>heavy</mass></bone-default><bone name=\"n_00\"/></system>",n=>nodes.GetValueOrDefault(n),_=>null,world.EmptyShape,log)==null&&log.Any(l=>l.Contains("not a number")),"A value that is not a number aborts the whole file, as in the plugin");
            log.Clear();
            Check(SmpSystemBuilder.Build("root.xml","<hdt-smp><bone name=\"n_00\"/></hdt-smp>",n=>nodes.GetValueOrDefault(n),_=>null,world.EmptyShape,log)==null&&log.Any(l=>l.Contains("not <system>")),"Only a <system> root is loaded");
            log.Clear();
            Check(SmpSystemBuilder.Build("broken.xml","<system><bone name=\"n_00\"></system>",n=>nodes.GetValueOrDefault(n),_=>null,world.EmptyShape,log)==null&&log.Any(l=>l.Contains("not well formed")),"Malformed XML is reported with its line");
        }
        var solver=SmpSolverSettings.FromConfig("""{"solver":{"numIterations":500,"erp":0.3,"min-fps":90,"maxSubSteps":2}}""","test");
        Check(solver.Iterations==128&&solver.Erp==.3&&solver.MinFps==90&&solver.MaxSubSteps==2,"configs.json solver values are read with FSMP's clamps");

        CollisionTests(log);
        return passed;
    }
    static M4d SmpTransformToMatrix(BulletSharp.Math.Matrix m)=>new(m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44);

    // A flat plate skinned to one bone and a small sphere cluster skinned to another.
    static SmpMeshSource Plate(string name,string bone,double size,double z)=>new(name,[bone],[SmpTransform.Identity],[(V3d.Zero,size*2)],
        [new(-size,-size,z),new(size,-size,z),new(size,size,z),new(-size,size,z)],[1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0],new int[16],[0,1,2,0,2,3]);
    static SmpMeshSource Ball(string name,string bone)=>new(name,[bone],[SmpTransform.Identity],[(V3d.Zero,1)],[V3d.Zero],[1,0,0,0],new int[4],[]);

    static void CollisionTests(List<string> log)
    {
        // A simulated bone with one collision sphere falls onto a kinematic plate and comes to rest on it.
        string Xml(string penetration,string extra="")=>$"""
            <system>
              <bone name="ground"/>
              <bone-default><mass>1</mass><inertia x="10" y="10" z="10"/><linearDamping>0.2</linearDamping><margin-multiplier>1</margin-multiplier></bone-default>
              <bone name="ball"/>
              <per-triangle-shape name="plate"><margin>0.5</margin><penetration>{penetration}</penetration><tag>ground</tag><can-collide-with-tag>cloth</can-collide-with-tag>{extra}</per-triangle-shape>
              <per-vertex-shape name="sphere"><margin>2</margin><tag>cloth</tag><can-collide-with-tag>ground</can-collide-with-tag></per-vertex-shape>
            </system>
            """;
        SmpMeshSource? Mesh(string name)=>name=="plate"?Plate("plate","ground",50,0):name=="sphere"?Ball("sphere","ball"):null;
        Dictionary<string,TestNode> Nodes(double height)=>new(StringComparer.OrdinalIgnoreCase){["ground"]=new("ground",M4d.Identity),["ball"]=new("ball",M4d.CreateTranslation(0,0,height),1)};
        log.Clear();
        using(var world=new SmpWorld(new SmpSolverSettings{Iterations=16})){
            var nodes=Nodes(20);var system=Build(world,Xml("1"),nodes,log,Mesh);
            Check(system.Bodies.Count==2&&system.Bodies[0].Shape.Triangles&&system.Bodies[0].Shape.Colliders.Length==2&&system.Bodies[1].Shape.Colliders.Length==1,"Shapes build two plates and one sphere");
            Check(system.Bodies[0].IsKinematic&&!system.Bodies[1].IsKinematic&&system.Bodies[1].Shape.Colliders[0].Flexible==1,"A shape is kinematic only when every bone it is skinned to is");
            Check(SmpCollision.NeedsCollision(system.Bodies[0],system.Bodies[1]),"Tags allow the pair");
            Run(world,3);
            double rest=nodes["ball"].Origin.Z;
            // The sphere surface (radius 2) rests the plate margin (0.5) above the plate.
            Check(rest>1.8&&rest<3.2,$"The sphere rests on the plate at about margin + radius above it (z {rest:F3})");
            Check(world.PeakContacts>=1,"Contacts were generated");
        }
        using(var world=new SmpWorld(new SmpSolverSettings{Iterations=16})){
            // Without a collision partner the same bone falls through.
            var nodes=Nodes(20);Build(world,Xml("1").Replace("<can-collide-with-tag>cloth</can-collide-with-tag>","<no-collide-with-tag>cloth</no-collide-with-tag>"),nodes,log,Mesh);
            Run(world,1.5);
            Check(nodes["ball"].Origin.Z<-50&&world.PeakContacts==0,"no-collide-with-tag removes the pair, so the bone falls through");
        }
        using(var world=new SmpWorld(new SmpSolverSettings{Iterations=16})){
            // Negative penetration flips the plate's working side: a sphere coming from above is now behind it.
            var nodes=Nodes(1.5);Build(world,Xml("-10"),nodes,log,Mesh);
            Run(world,.5);
            Check(nodes["ball"].Origin.Z<-1,$"A negative penetration pushes toward the other side of the plate (z {nodes["ball"].Origin.Z:F3})");
        }
        using(var world=new SmpWorld(new SmpSolverSettings{Iterations=16})){
            // Starting just under the plate but within the penetration depth, the sphere is pushed back out on top.
            var nodes=Nodes(-1);Build(world,Xml("4"),nodes,log,Mesh);
            Run(world,2);
            Check(nodes["ball"].Origin.Z>1.5,$"A vertex within the penetration depth behind a plate is pushed back out (z {nodes["ball"].Origin.Z:F3})");
        }
        using(var world=new SmpWorld(new SmpSolverSettings{Iterations=16})){
            // weight-threshold above the plate's only weight removes its colliders.
            var nodes=Nodes(20);var system=Build(world,Xml("1","<weight-threshold bone=\"ground\">1.5</weight-threshold>"),nodes,log,Mesh);
            Check(system.Bodies[0].Shape.Colliders.Length==0,"weight-threshold clips colliders whose bone weights do not exceed it");
        }
        using(var world=new SmpWorld(new SmpSolverSettings{Iterations=16})){
            // Sphere against sphere: a kinematic ball under a simulated one holds it up.
            const string xml="""
                <system>
                  <bone name="ground"/>
                  <bone-default><mass>1</mass><inertia x="10" y="10" z="10"/><linearDamping>0.2</linearDamping></bone-default>
                  <bone name="ball"/>
                  <per-vertex-shape name="base"><margin>3</margin><tag>a</tag></per-vertex-shape>
                  <per-vertex-shape name="sphere"><margin>2</margin><tag>b</tag></per-vertex-shape>
                </system>
                """;
            var nodes=Nodes(12);
            var system=Build(world,xml,nodes,log,name=>name=="base"?Ball("base","ground"):name=="sphere"?Ball("sphere","ball"):null);
            Run(world,2);
            double rest=nodes["ball"].Origin.Z;
            Check(rest>4.3&&rest<5.6,$"A simulated sphere rests on a kinematic sphere at the sum of their radii (z {rest:F3})");
            // disable-tag: a body switches off when another body carries that tag.
            system.Bodies[1].DisableTag="a";SmpCollision.UpdateActiveState(system.Bodies);
            Check(system.Bodies[1].Disabled&&!SmpCollision.NeedsCollision(system.Bodies[0],system.Bodies[1]),"disable-tag switches a body off while another body carries the tag");
        }
        // Collider tree: bones a vertex is weighted to form its path, so vertices sharing bones share a node.
        using(var world=new SmpWorld(new SmpSolverSettings())){
            var nodes=Chain("b_",3,V3d.Zero,V3d.UnitX*5);
            var source=new SmpMeshSource("cloth",["b_00","b_01","b_02"],[SmpTransform.Identity,SmpTransform.Identity,SmpTransform.Identity],[(V3d.Zero,1),(V3d.Zero,1),(V3d.Zero,1)],
                [new(0,0,0),new(1,0,0),new(2,0,0),new(3,0,0)],[1,0,0,0, .25f,.75f,0,0, .75f,.25f,0,0, .5f,.5f,0,0],[0,0,0,0, 0,1,0,0, 1,2,0,0, 2,1,0,0],[]);
            const string xml="""
                <system><bone name="b_00"/><bone-default><mass>1</mass></bone-default><bone name="b_01"/><bone name="b_02"/>
                <per-vertex-shape name="cloth"><margin>1</margin></per-vertex-shape></system>
                """;
            var system=Build(world,xml,nodes,log,name=>name=="cloth"?source:null);var body=system.Bodies[0];
            Check(body.Weights[4]==.75f&&body.BoneIndex[4]==1,"Vertex weights are sorted heaviest first");
            Check(Math.Abs(body.Flexible(0))<1e-6&&Math.Abs(body.Flexible(1)-.75f)<1e-6&&Math.Abs(body.Flexible(2)-1)<1e-6,"Flexible is the weight carried by simulated bones");
            Check(body.Shape.Colliders.Length==4&&body.Shape.Colliders[0].Flexible>=body.Shape.Colliders[^1].Flexible,"Every weighted vertex becomes a collider");
            world.UpdateAllBodies=true;world.Update(1/60d);
            Check(body.Pos.Length==4&&body.Broadphase.Valid&&body.Broadphase.Max.X-body.Broadphase.Min.X>=3,"Skinned positions and bounds are produced each step");
        }
    }
}
