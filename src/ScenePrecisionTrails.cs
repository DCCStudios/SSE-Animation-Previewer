using FO4_Base_Library;
using OpenTK.Mathematics;
using M4=OpenTK.Mathematics.Matrix4;

namespace SSEPreview;

/// <summary>Precision blade trails (2.0.4 AttackTrail.cpp, PrecisionHandler.cpp) for a clip's Collision_Add capsules on
/// WEAPON or SHIELD, unless NoTrail. The trail is chosen as Precision chooses it: the capsule's Trail* parameters when it
/// has any (the rest from the MCM defaults), else the first matching [[TrailDefinition]] from the TOMLs, else
/// Effects/WeaponTrails/AttackTrail.nif. Settings come from Precision's MCM ini in the mounted data. Precision steps at
/// the game's frame rate; this replays it at <see cref="TrailFrameSeconds"/> (60 fps).</summary>
public sealed partial class Scene
{
    bool showTrails=true;
    public bool ShowTrails{get=>showTrails;set{showTrails=value;view.UpdateRequired=true;view.Invalidate();}}
    /// <summary>The equipped weapon's display name, for TOML WeaponNames conditions.</summary>
    public Func<string?>? WeaponName;
    public double TrailFrameSeconds=1.0/60;
    public const string DefaultTrailMesh="Effects/WeaponTrails/AttackTrail.nif";
    TrailRenderer? trailRenderer;
    TrailDefinitions? trailDefinitions;long trailDefinitionsRevision=-1;
    readonly Dictionary<string,TrailMesh?> trailMeshes=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(PrecisionCollision,int),TrailSimulation.Bone?> trailBlade=[];
    object? trailCacheKey;
    readonly List<TrailRenderer.Strip> trailStrips=[];
    /// <summary>One line per trail drawn last frame, and problems found, for logs.</summary>
    public List<string> TrailStatus{get;}=[];
    public int DrawnTrails=>trailRenderer?.LastDrawn??0;
    public TrailDefinitions TrailDefinitionSet{get{
        if(trailDefinitions==null||trailDefinitionsRevision!=assets.Revision){trailDefinitions=TrailDefinitions.Load(assets);trailDefinitionsRevision=assets.Revision;trailMeshes.Clear();}
        return trailDefinitions;
    }}

    TrailMesh? TrailMeshFor(string path,out string? problem)
    {
        problem=null;
        if(trailMeshes.TryGetValue(path,out var cached)){if(cached==null)problem="could not load "+path;return cached;}
        try{return trailMeshes[path]=TrailMesh.Load(assets,path);}
        catch(Exception ex){problem=ex.Message;trailMeshes[path]=null;return null;}
    }

    /// <summary>The capsule node's origin and forward axis at a global trail frame. One pose per frame serves every
    /// trailing capsule, and is cached for the clip.</summary>
    TrailSimulation.Bone? TrailBlade(PrecisionTimeline timeline,PrecisionCollision collision,int frame)
    {
        if(trailBlade.TryGetValue((collision,frame),out var known))return known;
        double time=Math.Max(0,frame*TrailFrameSeconds);
        var old=Skeleton.Pose;
        try{
            if(HasPose)Skeleton.ApplyPose(PoseAt(time,"Precision trail"));
            var actor=ActorWorld(time);
            foreach(var c in timeline.Collisions){
                if(!c.Definition.IsWeapon||c.Definition.NoTrail)continue;
                if(PrecisionBone(c.Definition.Node) is not {} bone){trailBlade[(c,frame)]=null;continue;}
                var world=bone.GetGlobalTransform.ToMatrix4d()*actor;
                var placed=Place(c,false,PrecisionShape(c,bone),world);
                var dir=placed.A-placed.B;
                trailBlade[(c,frame)]=dir.LengthSquared>1e-12?new TrailSimulation.Bone(placed.B,dir.Normalized()):null;
            }
        }finally{Skeleton.ApplyPose(old);}
        if(trailBlade.Count>200000)trailBlade.Clear();
        return trailBlade.GetValueOrDefault((collision,frame));
    }

    /// <summary>Builds this frame's trail strips. Runs at the end of the frame update, with the clip's pose current.</summary>
    void UpdateTrails()
    {
        trailStrips.Clear();TrailStatus.Clear();
        if(!showTrails||Clip==null)return;
        var timeline=Precision;if(timeline==null)return;
        var settings=PrecisionConfig;
        if(!settings.DisplayTrails){TrailStatus.Add("trails off: bDisplayTrails = 0 in Precision's ini");return;}
        var key=(Clip,timeline,RootMotion,WeaponMeshLength(),TrailFrameSeconds);
        if(!Equals(key,trailCacheKey)){trailBlade.Clear();trailCacheKey=key;}
        var definitions=TrailDefinitionSet;
        string? weaponName=WeaponName?.Invoke();
        foreach(var c in timeline.Collisions){
            var d=c.Definition;
            if(!d.IsWeapon||d.NoTrail)continue;
            if(Time<c.Start-1e-9)continue;
            // Which trail: annotation parameters, else a TOML definition, else Precision's default mesh.
            string meshPath=DefaultTrailMesh;float lifetimeMult=1;var baseColor=settings.TrailDefaultBaseColor;float? scaleMult=null;string chosen;
            if(d.HasTrailOverride){
                meshPath=d.TrailMesh??meshPath;lifetimeMult=d.TrailLifetimeMult??1;baseColor=d.TrailBaseColor??baseColor;scaleMult=d.TrailBaseColorScaleMult;chosen="annotation";
            }else if(definitions.Find(weaponName) is {} definition){
                meshPath=definition.Mesh??meshPath;lifetimeMult=definition.LifetimeMult??1;baseColor=definition.BaseColor??baseColor;scaleMult=definition.BaseColorScaleMult;chosen=$"{definition.Source} priority {definition.Priority}";
            }else chosen=weaponName==null?"Precision default, no weapon name":$"Precision default, no TOML match for \"{weaponName}\"";
            var mesh=TrailMeshFor(meshPath,out string? problem);
            if(mesh==null){string line=$"{d.Node} trail at {c.Start:0.###} s: {problem}";if(!TrailStatus.Contains(line))TrailStatus.Add(line);continue;}
            float lifetime=settings.TrailSegmentLifetime*lifetimeMult;
            var state=TrailSimulation.At(c.Start,c.End,Time,TrailFrameSeconds,mesh.Rows,lifetime,settings.TrailFadeOutTime,settings.TrailSegmentsPerSecond,frame=>TrailBlade(timeline,c,frame));
            if(state==null)continue;
            // Strip length: the weapon mesh's reach unless TrailUseTrueLength or no weapon, then the capsule's length.
            double length=0;
            if(PrecisionBone(d.Node) is {} bone){
                var shape=PrecisionShape(c,bone);double capsuleLength=(shape.A-shape.B).Length;
                if(!d.TrailUseTrueLength&&d.Node=="WEAPON")length=WeaponMeshLength();
                if(length<=0)length=capsuleLength;
            }
            float scale=(float)(length*.01);
            var positions=new Vector3[mesh.X.Length];
            for(int i=0;i<positions.Length;i++){
                // Linear blend over the vertex's weighted bones: each bone carries x out along its own blade axis.
                var p=Vector3d.Zero;
                foreach(var (row,weight) in mesh.Influences[i]){var b=state.Bones[Math.Clamp(row,0,state.Bones.Length-1)];p+=(b.Origin+b.Direction*(mesh.X[i]*scale))*weight;}
                positions[i]=new((float)p.X,(float)p.Y,(float)p.Z);
            }
            float baseScale=(scaleMult??mesh.BaseColorScale)*settings.TrailBaseColorScaleMult;
            var color=new Vector4(baseColor.X,baseColor.Y,baseColor.Z,baseColor.W*Math.Min(state.Visibility,1f));
            trailStrips.Add(new TrailRenderer.Strip(mesh,positions,color,baseScale));
            TrailStatus.Add(FormattableString.Invariant($"clip time {Time:0.000} s, {d.Node} trail at {c.Start:0.###}-{(double.IsFinite(c.End)?c.End:Clip.Duration):0.###} s: {Path.GetFileName(mesh.Path)} ({chosen}), {state.LiveRows}/{mesh.Rows} rows live, visibility {Math.Min(state.Visibility,1f):0.##}, length {length:0.#}, colour {baseColor.X:0.##},{baseColor.Y:0.##},{baseColor.Z:0.##},{baseColor.W:0.##} x {baseScale:0.##}, lifetime {lifetime:0.###} s, {mesh.UvLayout}, {mesh.ShaderInfo}"));
        }
    }

    void DrawTrails(M4 projection)
    {
        if(trailStrips.Count==0||view.FlatShapeColors)return;
        trailRenderer??=new TrailRenderer(assets);
        trailRenderer.Draw(trailStrips,projection,view.camera,view.defaultWhiteTex);
        foreach(var d in trailRenderer.Diagnostics)if(!Diagnostics.Contains(d))Diagnostics.Add(d);
    }

    void ClearTrails(){trailBlade.Clear();trailCacheKey=null;trailStrips.Clear();trailMeshes.Clear();trailRenderer?.Invalidate();}
}
