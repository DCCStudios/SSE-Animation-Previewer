using System.Numerics;

namespace SSEPreview;

// Fixed simulation ticks, explicit random state, and reset/replay make seeking independent of draw FPS.
public sealed class ParticleSimulation
{
    public const double Step = 1d / 120;
    public sealed class Particle
    {
        public Vector3 Position, Velocity;
        public Vector3 RotationAxis=Vector3.UnitX;
        public ParticleStripHistory? Strip;
        public Vector4 Color = Vector4.One;
        public float Age, Life, Radius, BaseRadius, Angle, Spin;
        public float FrameSeed;
        public int Generation;
        public bool Dead;
        public Vector4 BirthColor = Vector4.One;
    }
    public sealed record Settings
    {
        public int Capacity = 1024;
        public bool WorldSpace = true;
        public string Shape = "Box";
        public Vector3 Dimensions;
        public float Speed, SpeedVariation, Declination, DeclinationVariation, PlanarAngle, PlanarVariation;
        public float Radius = 1, RadiusVariation, Life = 1, LifeVariation, Rotation, RotationVariation, Spin, SpinVariation;
        public bool RandomSpinSign,HasRotationAxis,RandomRotationAxis;
        public Vector3 RotationAxis=Vector3.UnitX;
        public Vector4 Color = Vector4.One;
    }
    public sealed record SpawnSettings
    {
        public int Generations,Minimum=1,Maximum=1;
        public float Probability=1,SpeedVariation,DirectionVariation,Life=1,LifeVariation;
    }
    public Func<double,bool> AgeEnabled=_=>true;
    public Func<double,bool> SpinEnabled=_=>true;
    public Func<Particle,double,SpawnSettings?>? DeathSpawn;
    readonly Queue<(Particle Particle,float Remaining)> descendants=[];
    public readonly List<Particle> Particles = [];
    public Settings Config {get;}
    public sealed class EmitterChannel
    {
        public string Name="";
        public bool RotateVelocityOnly;
        public Func<double,float> BirthRate=_=>0;
        public Func<double,bool> Active=_=>true;
        public Func<double,Matrix4x4> Transform=_=>Matrix4x4.Identity;
        public Func<double,Settings>? SettingsAt;
        public Func<ParticleSimulation,Vector3>? MeshPosition;
        public Func<double,(Vector3 Position,Vector3 Direction)>? MeshSample;
        internal double Remainder;
    }
    public EmitterChannel PrimaryEmitter {get;}=new();
    public List<EmitterChannel> AdditionalEmitters {get;}=[];
    public IEnumerable<EmitterChannel> Emitters=>new[]{PrimaryEmitter}.Concat(AdditionalEmitters);
    public Func<double,bool> EmissionEnabled=_=>true;
    public Func<double,float> BirthRate {get=>PrimaryEmitter.BirthRate;set=>PrimaryEmitter.BirthRate=value;}
    public Func<double,bool> EmitterActive {get=>PrimaryEmitter.Active;set=>PrimaryEmitter.Active=value;}
    public Func<double,Matrix4x4> EmitterTransform {get=>PrimaryEmitter.Transform;set=>PrimaryEmitter.Transform=value;}
    public Func<double,Settings>? AnimatedSettings {get=>PrimaryEmitter.SettingsAt;set=>PrimaryEmitter.SettingsAt=value;}
    public Action<Particle, float, double>? Update;
    public Action<Particle,double>? Initialize;
    public Action<double>? BeforeStep;
    public Action? ResetState;
    public Func<ParticleSimulation,Vector3>? MeshPosition {get=>PrimaryEmitter.MeshPosition;set=>PrimaryEmitter.MeshPosition=value;}
    public Func<double,(Vector3 Position,Vector3 Direction)>? MeshSample {get=>PrimaryEmitter.MeshSample;set=>PrimaryEmitter.MeshSample=value;}
    public long Tick {get;private set;}
    public long TotalBorn {get;private set;}
    public long CapacityDrops {get;private set;}
    readonly uint seed;
    uint random;
    public ParticleSimulation(Settings settings,uint seed=1){Config=settings;this.seed=seed==0?1:seed;Reset();}
    public void Reset(){Particles.Clear();descendants.Clear();Tick=0;TotalBorn=0;CapacityDrops=0;foreach(var emitter in Emitters)emitter.Remainder=0;random=seed;ResetState?.Invoke();}
    public float Random01(){random^=random<<13;random^=random>>17;random^=random<<5;return (random>>8)*(1f/16777216);}
    float Vary(float value,float spread)=>value+(Random01()*2-1)*spread;
    // AE emitter speed/lifespan variation describes the full interval width.
    internal static float BirthVariation(float value,float spread,float unitRandom)=>value+(unitRandom-.5f)*spread;
    float VaryBirth(float value,float spread)=>BirthVariation(value,spread,Math.Min(32767,(int)(Random01()*32768))/32767f);
    public void Seek(double seconds)
    {
        if(!double.IsFinite(seconds)||seconds<0)seconds=0;
        long target=(long)Math.Floor(Math.Min(seconds,3600)/Step+1e-7);
        if(target<Tick)Reset();
        while(Tick<target)Advance();
    }
    // Coarse profile buckets (ticks) for the clip profiler; cheap enough to leave on.
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string,long> Profile=new();
    internal static void Bucket(string name,long since){Profile.AddOrUpdate(name,System.Diagnostics.Stopwatch.GetTimestamp()-since,(_,v)=>v+System.Diagnostics.Stopwatch.GetTimestamp()-since);}
    // Native systems resolve modifier parameters and emitter placement once per update, not per particle;
    // callbacks that only need the step time read this instead of each particle's exact age time.
    public double StepTime {get;private set;}=double.NaN;
    void Advance()
    {
        double start=Tick*Step,end=(Tick+1)*Step;StepTime=end;
        long t0=System.Diagnostics.Stopwatch.GetTimestamp();
        BeforeStep?.Invoke(start);Bucket("beforeStep",t0);t0=System.Diagnostics.Stopwatch.GetTimestamp();
        for(int i=Particles.Count-1;i>=0;i--){
            var p=Particles[i];
            if(p.Strip!=null){if(!AdvanceStrip(p,(float)Step,end))Particles.RemoveAt(i);continue;}
            float live=AgeEnabled(end)?Math.Clamp(p.Life-p.Age,0,(float)Step):(float)Step;
            if(AgeEnabled(end))p.Age+=live;
            UpdateParticle(p,live,start+live);
            if(p.Dead){Particles.RemoveAt(i);continue;}
            if(AgeEnabled(end)&&p.Age>=p.Life){
                Particles.RemoveAt(i);QueueChildren(p,start+live,(float)Step-live);
            }
        }
        AdvanceChildren(end);Bucket("update",t0);t0=System.Diagnostics.Stopwatch.GetTimestamp();
        foreach(var emitter in Emitters)if(EmissionEnabled(start)&&emitter.Active(start)){
            double remainder=emitter.Remainder;
            double rate=Math.Clamp(emitter.BirthRate(start),0,100000);
            if(!double.IsFinite(rate))rate=0;
            double count=remainder+rate*Step;
            int births=(int)Math.Floor(count+1e-10);
            // Emission windows only change at step edges in practice; when both edges are open the per-birth
            // checks (reflection-backed interpolator samples) are skipped.
            bool openAll=births>0&&EmissionEnabled(start)&&emitter.Active(start)&&EmissionEnabled(end)&&emitter.Active(end);
            for(int i=0;i<births;i++){
                double fraction=rate>0?(i+1-remainder)/rate:Step;
                double born=start+Math.Clamp(fraction,0,Step);
                if(!openAll&&(!EmissionEnabled(Math.Max(start,born-1e-9))||!emitter.Active(Math.Max(start,born-1e-9))))continue;
                if(Particles.Count+descendants.Count>=Config.Capacity){CapacityDrops++;continue;}
                var p=Spawn(born,emitter);
                if(p.Strip!=null){if(AdvanceStrip(p,(float)(end-born),end))Particles.Add(p);AdvanceChildren(end);continue;}
                p.Age=(float)(end-born);
                if(!AgeEnabled(end))p.Age=0;
                if(p.Age<p.Life){UpdateParticle(p,(float)(end-born),end);if(!p.Dead)Particles.Add(p);}
                else {float alive=p.Life;p.Age=alive;UpdateParticle(p,alive,born+alive);if(!p.Dead)QueueChildren(p,born+alive,(float)(end-born)-alive);AdvanceChildren(end);}
            }
            emitter.Remainder=count-births;
        }
        AdvanceChildren(end);Bucket("emit",t0);
        Tick++;StepTime=double.NaN;
    }
    void QueueChildren(Particle parent,double time,float remaining)
    {
        SpawnChildren(parent,DeathSpawn?.Invoke(parent,time),remaining);
    }
    public void SpawnChildren(Particle parent,SpawnSettings? c,float remaining)
    {
        if(c==null||parent.Generation>=c.Generations||Random01()>=Math.Clamp(c.Probability,0,1))return;
        int min=Math.Clamp(c.Minimum,0,65535),max=Math.Clamp(c.Maximum,min,65535);
        int count=min+(int)(Random01()*(max-min+1));
        for(int i=0;i<count;i++){
            if(Particles.Count+descendants.Count-(parent.Dead&&Particles.Contains(parent)?1:0)>=Config.Capacity){CapacityDrops+=count-i;break;}
            Vector3 direction=parent.Velocity;
            float speed=direction.Length();
            direction=speed>1e-8f?direction/speed:Vector3.UnitZ;
            if(c.DirectionVariation>0){
                float angle=Random01()*MathF.Tau,cos=1-Random01()*(1-MathF.Cos(Math.Clamp(c.DirectionVariation,0,MathF.PI)));
                var axis=Vector3.Normalize(Vector3.Cross(direction,Math.Abs(direction.Z)<.9f?Vector3.UnitZ:Vector3.UnitX));
                var other=Vector3.Cross(direction,axis);
                direction=direction*cos+(axis*MathF.Cos(angle)+other*MathF.Sin(angle))*MathF.Sqrt(Math.Max(0,1-cos*cos));
            }
            var child=new Particle{
                Position=parent.Position,Velocity=direction*Math.Max(0,Vary(speed,speed*c.SpeedVariation)),
                Life=Math.Max(.0001f,Vary(c.Life,c.LifeVariation)),Generation=parent.Generation+1,
                Radius=parent.BaseRadius,BaseRadius=parent.BaseRadius,Color=parent.BirthColor,BirthColor=parent.BirthColor,
                Angle=parent.Angle,Spin=parent.Spin,RotationAxis=parent.RotationAxis,FrameSeed=Random01()
            };
            Initialize?.Invoke(child,(Tick+1)*Step-remaining);
            TotalBorn++;descendants.Enqueue((child,Math.Max(0,remaining)));
        }
    }
    void AdvanceChildren(double end)
    {
        while(descendants.TryDequeue(out var pending)){
            var p=pending.Particle;
            if(p.Strip!=null){if(AdvanceStrip(p,pending.Remaining,end))Particles.Add(p);continue;}
            float live=AgeEnabled(end)?Math.Min(p.Life,pending.Remaining):pending.Remaining;
            p.Age=AgeEnabled(end)?live:0;
            UpdateParticle(p,live,end-pending.Remaining+live);
            if(p.Dead)continue;
            if(p.Age>=p.Life)QueueChildren(p,end-pending.Remaining+live,pending.Remaining-live);
            else Particles.Add(p);
        }
    }
    bool AdvanceStrip(Particle p,float dt,double time)
    {
        // AE age/death runs before forces/position/strip update. StripData refuses
        // removal until at most one history point remains; age itself is not clamped.
        if(AgeEnabled(time))p.Age+=Math.Max(0,dt);
        if(AgeEnabled(time)&&p.Age>p.Life){
            // Native age/death attempts spawning before every deferred removal.
            // Our existing descendant sampler still uses fixed preview ticks.
            QueueChildren(p,time,0);
            if(p.Strip!.Points.Count<=1)return false;
        }
        UpdateParticle(p,dt,time);
        return !p.Dead;
    }
    void UpdateParticle(Particle p,float dt,double time)
    {
        p.Color=p.BirthColor;p.Radius=p.BaseRadius;
        if(Update!=null)Update(p,dt,time);else p.Position+=p.Velocity*dt;
        if(SpinEnabled(time))p.Angle=AdvanceRotation(p.Angle,p.Spin,dt);
    }
    internal static float AdvanceRotation(float angle,float spin,float dt)
    {
        float next=angle+spin*Math.Max(0,dt);
        // Native NiPSysRotationModifier caps large positive excursions, then wraps above 2*pi.
        if(next>BitConverter.Int32BitsToSingle(0x41FB53D2))return 0;
        while(next>MathF.Tau)next-=MathF.Tau;
        return next;
    }
    Particle Spawn(double time,EmitterChannel emitter)
    {
        long s0=System.Diagnostics.Stopwatch.GetTimestamp();
        var c=emitter.SettingsAt?.Invoke(time)??Config;Bucket("spawn:settings",s0);s0=System.Diagnostics.Stopwatch.GetTimestamp();
        Vector3 pos;
        if(c.Shape=="Sphere")pos=ParticleEmitterSampling.Sphere(c.Dimensions.X,Random01);
        else if(c.Shape=="Cylinder")pos=ParticleEmitterSampling.Cylinder(c.Dimensions.X,c.Dimensions.Z,Random01);
        else if(c.Shape=="Mesh"&&emitter.MeshPosition!=null)pos=emitter.MeshPosition(this);
        else pos=ParticleEmitterSampling.Box(c.Dimensions,Random01);
        float dec=ParticleEmitterSampling.SignedVariation(c.Declination,c.DeclinationVariation,Random01),planar=ParticleEmitterSampling.SignedVariation(c.PlanarAngle,c.PlanarVariation,Random01);
        float speed=VaryBirth(c.Speed,c.SpeedVariation);
        var velocity=ParticleEmitterSampling.LaunchVelocity(speed,dec,planar);
        if(c.Shape=="Mesh"&&emitter.MeshSample!=null){var sample=emitter.MeshSample(time);pos=sample.Position;velocity=sample.Direction*speed;}
        Bucket("spawn:mesh-sample",s0);s0=System.Diagnostics.Stopwatch.GetTimestamp();
        var transform=emitter.Transform(time);
        pos=Vector3.Transform(pos,transform);velocity=emitter.RotateVelocityOnly?ParticleEmitterTransform.RotateVelocity(velocity,transform):Vector3.TransformNormal(velocity,transform);
        float radius=Math.Max(0,ParticleEmitterSampling.SignedVariation(c.Radius,c.RadiusVariation,Random01));
        float spin=Vary(c.Spin,c.SpinVariation);
        if(c.RandomSpinSign&&Random01()<.5f)spin=-spin;
        TotalBorn++;
        var particle=new Particle{Position=pos,Velocity=velocity,Radius=radius,BaseRadius=radius,Life=Math.Max(.000001f,VaryBirth(c.Life,c.LifeVariation)),
            Color=c.Color,BirthColor=c.Color,Angle=Vary(c.Rotation,c.RotationVariation),Spin=spin,FrameSeed=Random01()};
        if(c.HasRotationAxis)particle.RotationAxis=c.RandomRotationAxis?ParticleEmitterSampling.RotationAxis(Random01):c.RotationAxis;
        Bucket("spawn:rest",s0);s0=System.Diagnostics.Stopwatch.GetTimestamp();
        Initialize?.Invoke(particle,time);Bucket("spawn:initialize",s0);return particle;
    }
}
