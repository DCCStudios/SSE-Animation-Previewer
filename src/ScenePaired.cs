using FO4_Base_Library;
using M4=OpenTK.Mathematics.Matrix4;

namespace SSEPreview;

/// <summary>What makes a clip a Skyrim paired clip (killmoves, hugs, mounting): one file holds both actors. Every
/// track is named; one actor's tracks carry its bone names, the other's the same names after "2_". The tracks named
/// "NPC" and "2_" are each actor's placement in the pair's own space (its mark), constant over the clip; the game
/// samples them at time 0 (SkyrimSE 1.6.1170 0x140B948E0) and snaps the actors to them (0x140546C10).</summary>
public sealed class PairedClip
{
    public const string Second="2_";
    public string[] TrackNames=[];
    /// <summary>Marks as row-vector matrices (v x M places a point of the actor in pair space).</summary>
    public System.Numerics.Matrix4x4 FirstMark=System.Numerics.Matrix4x4.Identity,SecondMark=System.Numerics.Matrix4x4.Identity;
    public bool HasFirstMark,HasSecondMark;
    public System.Numerics.Matrix4x4 Mark(string prefix)=>prefix==Second?SecondMark:FirstMark;
    public static string Other(string prefix)=>prefix==Second?"":Second;
    /// <summary>How many of these bones have a track under the prefix.</summary>
    public int Bound(IEnumerable<string> bones,string prefix){var names=TrackNames.ToHashSet(StringComparer.Ordinal);return bones.Count(b=>names.Contains(prefix+b));}
    /// <summary>Bone names the file animates under the prefix: track names without it, less the mark tracks.</summary>
    public IEnumerable<string> Bones(string prefix)=>prefix==Second
        ?TrackNames.Where(n=>n.Length>2&&n.StartsWith(Second,StringComparison.Ordinal)).Select(n=>n[2..])
        :TrackNames.Where(n=>n.Length>0&&!n.StartsWith(Second,StringComparison.Ordinal)&&n is not ("PairedRoot" or "NPC"));
    public static PairedClip? Read(HkxAnimacionDescomprimida_Class animation)
    {
        var names=animation.TrackNames;
        if(!HkxPoseImportSession.IsPairedClip(names))return null;
        var paired=new PairedClip{TrackNames=names.Take(animation.Animacion.NumberOfTransformTracks).ToArray()};
        System.Numerics.Matrix4x4? Sample(string name){
            int track=Array.IndexOf(paired.TrackNames,name);
            if(track<0||animation.GetTransform(0,track) is not {Length:>=8} f)return null;
            var rotation=new System.Numerics.Quaternion(f[4],f[5],f[6],f[7]);
            if(rotation.LengthSquared()<1e-8f)rotation=System.Numerics.Quaternion.Identity;
            return System.Numerics.Matrix4x4.CreateFromQuaternion(System.Numerics.Quaternion.Normalize(rotation))*System.Numerics.Matrix4x4.CreateTranslation(f[0],f[1],f[2]);
        }
        if(Sample("NPC") is {} first){paired.FirstMark=first;paired.HasFirstMark=true;}
        if(Sample(Second) is {} second){paired.SecondMark=second;paired.HasSecondMark=true;}
        return paired;
    }
    public static string Describe(System.Numerics.Matrix4x4 mark)
    {
        if(mark.IsIdentity)return "identity";
        double yaw=Math.Atan2(mark.M12,mark.M11)*180/Math.PI;
        return FormattableString.Invariant($"({mark.M41:0.#}, {mark.M42:0.#}, {mark.M43:0.#}) yaw {yaw:0}");
    }
}

// The second actor of a paired clip: a body on a skeleton of its own, posed from the same file through the other
// track prefix and placed by the two marks:
//   bone world = bone in actor space x Mark(actor) x pair world
// The scene's own actor stays where it is, so pair world = inverse(Mark(own)) x own actor world.
public sealed partial class Scene
{
    SkeletonInstance partnerSkeleton=new();
    readonly HashSet<IRenderableShape> partnerShapes=new(ReferenceEqualityComparer.Instance);
    HkxPoseImportSession? partnerSession;string partnerHkx="";bool addingPartner;
    /// <summary>The user's choice to play the other role of a paired clip than the one the skeleton fits best.</summary>
    public bool PairedSwap;
    /// <summary>The track prefix the scene's own actor plays in a paired clip: empty or "2_".</summary>
    public string ActorPrefix{get;private set;}="";
    public string PartnerPrefix=>PairedClip.Other(ActorPrefix);
    public int PartnerShapeCount=>partnerShapes.Count;
    public bool PartnerPosed=>partnerSession!=null;
    /// <summary>One line on the paired clip in the scene: roles, bound bones and marks. Empty for a solo clip.</summary>
    public string PairedStatus{get;private set;}="";

    /// <summary>Picks the role of the scene's actor in a paired clip: the prefix under which more of its skeleton's
    /// bones have tracks (the plain names on a tie), or the other one when <see cref="PairedSwap"/> is set.</summary>
    public string ChoosePairedPrefix(AnimationClip? clip)
    {
        ActorPrefix="";
        if(clip?.Paired is not {} paired||!Skeleton.HasSkeleton)return ActorPrefix;
        var bones=Skeleton.SkeletonDictionary.Keys.ToArray();
        string best=paired.Bound(bones,PairedClip.Second)>paired.Bound(bones,"")?PairedClip.Second:"";
        return ActorPrefix=PairedSwap?PairedClip.Other(best):best;
    }
    /// <summary>Starts the partner's body: models added until <see cref="EndPartner"/> go on the partner's skeleton.
    /// False when the skeleton cannot be loaded; the reason is in Diagnostics.</summary>
    public bool BeginPartner(string skeletonNif,string skeletonHkx)
    {
        partnerShapes.Clear();partnerSession=null;
        if(!assets.Exists(skeletonNif)){Diagnostics.Add("Paired partner not drawn: no skeleton at "+skeletonNif);return false;}
        partnerSkeleton=new SkeletonInstance();
        if(!partnerSkeleton.LoadFromBytes(assets.Read(skeletonNif))){Diagnostics.Add("Paired partner not drawn: "+skeletonNif+" did not load.");return false;}
        partnerHkx=skeletonHkx;
        if(assets.Exists(partnerHkx))partnerSkeleton.MergeHkxSkeleton(assets.Read(partnerHkx));
        addingPartner=true;return true;
    }
    public void EndPartner()=>addingPartner=false;
    ISkeletonResolver ActorResolver()
    {
        if(partnerShapes.Count==0)return new SingleInstanceSkeletonResolver(Skeleton);
        var map=new Dictionary<IRenderableShape,SkeletonInstance>(ReferenceEqualityComparer.Instance);
        foreach(var shape in partnerShapes)map[shape]=partnerSkeleton;
        return new MultiInstanceSkeletonResolver(map,Skeleton);
    }
    void BindPartner()
    {
        partnerSession=null;PairedStatus="";
        if(Clip?.Paired is not {} paired)return;
        string Role(string prefix)=>prefix==PairedClip.Second?"2_ tracks":"plain tracks";
        int own=Session?.BoundTrackCount??0;
        PairedStatus=$"Paired clip. This actor: {Role(ActorPrefix)}, {own} bones bound, mark {PairedClip.Describe(paired.Mark(ActorPrefix))}.";
        if(partnerShapes.Count==0){PairedStatus+=" Partner not shown.";return;}
        try{
            partnerSession=HkxPoseImportSession.Create(assets.Exists(partnerHkx)?assets.Read(partnerHkx):null,Clip.Bytes,partnerSkeleton,"Paired partner",partnerHkx,trackPrefix:PartnerPrefix);
            PairedStatus+=$" Partner: {Role(PartnerPrefix)}, {partnerSession.BoundTrackCount} bones bound, mark {PairedClip.Describe(paired.Mark(PartnerPrefix))}.";
            if(partnerSession.BoundTrackCount==0)Diagnostics.Add("The partner's skeleton has no bone the paired clip animates for that role; choose the partner race the clip was made for.");
        }catch(Exception ex){Diagnostics.Add("Paired partner binding: "+ex.Message);}
    }
    void PosePartner(double time)
    {
        if(partnerSession==null||Clip==null)return;
        partnerSkeleton.ApplyPose(partnerSession.BuildPoseAtTime((float)Math.Clamp(time,0,Math.Max(0,Clip.Duration)),"Paired partner",false).Pose);
    }
    static M4 ToM4(System.Numerics.Matrix4x4 m)=>new(m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44);
    // Where the partner's actor root is, given where the scene draws its own actor.
    M4 PartnerWorld(M4 actor)
    {
        if(Clip?.Paired is not {} paired)return actor;
        return ToM4(paired.Mark(PartnerPrefix))*ToM4(paired.Mark(ActorPrefix)).Inverted()*actor;
    }
    /// <summary>World position of a bone of the scene's actor or of the partner at the scene's time, or null.</summary>
    public System.Numerics.Vector3? PairedBoneWorld(bool partner,string bone)
    {
        var skeleton=partner?partnerSkeleton:Skeleton;
        if(partner&&partnerSession==null||!skeleton.HasSkeleton||!skeleton.SkeletonDictionary.TryGetValue(bone,out var found))return null;
        var actor=ToM4(ActorPlacement(Time));
        var world=found.GetGlobalTransform.ToMatrix4()*(partner?PartnerWorld(actor):actor);
        return new(world.M41,world.M42,world.M43);
    }
}
