using OpenTK.Mathematics;
using M4=OpenTK.Mathematics.Matrix4;

namespace SSEPreview;

/// <summary>What the view does with a clip's camera bone. Off unless chosen.</summary>
public enum AnimatedCameraMode{Off,Engine,LookThrough}

/// <summary>Animated camera playback from the skeleton's camera bones.
/// <para>Engine follows Skyrim 1.6.1170 (Ghidra, see the skyrim-camera-bone-facts note): the camera bone
/// <c>Camera3rd [Cam3]</c> is an offset on the normal camera, not a placement. Camera translation += weight x
/// (camera rotation x the bone's LOCAL translate); camera rotation *= slerp(identity, the bone's local rotate, weight).
/// The camera's axes are x right, y forward, z up. <c>Camera Control</c> local translate z is copied into the FOV,
/// taken here as the horizontal angle in degrees and used only when it is between 5 and 170.</para>
/// <para>LookThrough views along the bone's world transform (+Y forward, +Z up, +X right), which is where the FF7
/// Maya library places it: compare it with the Maya shot.</para>
/// <para>With <see cref="AnimatedCameraEvents"/> on, a clip's StartAnimatedCamera / StartAnimatedCameraDelta /
/// EndAnimatedCamera annotations (or the handler names AnimatedCameraStart / AnimatedCameraDeltaStart /
/// AnimatedCameraEnd) gate the camera: Start applies at full weight at once, Delta ramps in over
/// <see cref="DeltaRampSeconds"/> (an estimate; the game's rate is not read), End stops it. In game a behavior sends
/// these with the payload "Camera3rd [Cam3]"; a clip annotation carries no payload, which the game ignores, so the
/// gate shows the timing a behavior would give, not what the clip alone does. The game's transition into the
/// animated state is not modelled.</para></summary>
public sealed partial class Scene
{
    public AnimatedCameraMode AnimatedCamera;
    public bool AnimatedCameraEvents;
    public const double DeltaRampSeconds=1.0;
    public const string CameraBone="Camera3rd [Cam3]",FovBone="Camera Control";
    /// <summary>One line on what the animated camera did last frame (for the status bar and capture logs).</summary>
    public string AnimatedCameraStatus{get;private set;}="";
    float? fovBeforeAnimatedCamera;

    static readonly string[] StartNames=["StartAnimatedCamera","AnimatedCameraStart"];
    static readonly string[] DeltaNames=["StartAnimatedCameraDelta","AnimatedCameraDeltaStart"];
    static readonly string[] EndNames=["EndAnimatedCamera","AnimatedCameraEnd"];

    /// <summary>Weight of the camera bone at <paramref name="time"/> from the clip's camera annotations, and why.</summary>
    double AnnotationWeight(double time,out string why)
    {
        var events=Clip!.Events.Where(e=>{string t=e.Text.Trim();return StartNames.Concat(DeltaNames).Concat(EndNames).Any(n=>t.Equals(n,StringComparison.OrdinalIgnoreCase));}).OrderBy(e=>e.Time).ToList();
        if(events.Count==0){why="no camera annotations in the clip";return 0;}
        var last=events.LastOrDefault(e=>e.Time<=time+1e-9);
        if(last==null){why="before the first camera annotation";return 0;}
        string text=last.Text.Trim();
        if(EndNames.Any(n=>text.Equals(n,StringComparison.OrdinalIgnoreCase))){why=$"ended by {text} at {last.Time:0.###} s";return 0;}
        if(DeltaNames.Any(n=>text.Equals(n,StringComparison.OrdinalIgnoreCase))){double w=Math.Clamp((time-last.Time)/DeltaRampSeconds,0,1);why=$"{text} at {last.Time:0.###} s, weight {w:0.##}";return w;}
        why=$"{text} at {last.Time:0.###} s";return 1;
    }

    void RestoreAnimatedCameraFov()
    {
        if(fovBeforeAnimatedCamera is float fov){view.FieldOfViewYDegrees=fov;fovBeforeAnimatedCamera=null;view.UpdateProjection(true);}
    }

    /// <summary>Applies the animated camera on top of this frame's orbit or follow camera. Called once the pose and the
    /// follow framing are done.</summary>
    void ApplyAnimatedCamera(M4 actor)
    {
        var camera=view.camera;
        camera.ClearViewOverride();
        if(AnimatedCamera==AnimatedCameraMode.Off||Clip==null){RestoreAnimatedCameraFov();AnimatedCameraStatus="";return;}
        if(!Skeleton.SkeletonDictionary.TryGetValue(CameraBone,out var bone)){RestoreAnimatedCameraFov();AnimatedCameraStatus=$"animated camera: the skeleton has no {CameraBone} bone";return;}
        double weight=1;string gate="whole clip (annotations not used)";
        if(AnimatedCameraEvents)weight=AnnotationWeight(Time,out gate);
        if(weight<=0){RestoreAnimatedCameraFov();AnimatedCameraStatus=$"animated camera {AnimatedCamera}: off, {gate}";return;}

        Vector3 Row(M4 m,int i)=>i switch{0=>new(m.M11,m.M12,m.M13),1=>new(m.M21,m.M22,m.M23),2=>new(m.M31,m.M32,m.M33),_=>new(m.M41,m.M42,m.M43)};
        if(AnimatedCamera==AnimatedCameraMode.LookThrough){
            var world=bone.GetGlobalTransform.ToMatrix4()*actor;
            camera.SetViewOverride(Row(world,3),Row(world,1),Row(world,2));
        }else{
            // Base camera axes, in Skyrim's camera convention: x right, y forward (the view direction), z up.
            var right=camera.right;var forward=-camera.Forward;var up=camera.upPlane;
            var local=bone.LocaLTransform.ToMatrix4();
            var t=Row(local,3);
            var eye=camera.GetOrbitEyePosition()+(float)weight*(t.X*right+t.Y*forward+t.Z*up);
            // Rows of the bone's local rotation are its axes in camera space; slerped toward identity by the weight.
            var rot=new Matrix3(Row(local,0).Normalized(),Row(local,1).Normalized(),Row(local,2).Normalized());
            var q=Quaternion.Slerp(Quaternion.Identity,Quaternion.FromMatrix(rot),(float)weight);
            var r=Matrix3.CreateFromQuaternion(q);
            Vector3 Axis(int i)=>i==1?r.M21*right+r.M22*forward+r.M23*up:r.M31*right+r.M32*forward+r.M33*up;
            camera.SetViewOverride(eye,Axis(1),Axis(2));
        }

        string fovText="FOV unchanged";
        if(Skeleton.SkeletonDictionary.TryGetValue(FovBone,out var fovBone)){
            float h=fovBone.LocaLTransform.Translation.Z;
            if(h>=5&&h<=170){
                fovBeforeAnimatedCamera??=view.FieldOfViewYDegrees;
                double aspect=Math.Max(1e-3,view.Width/(double)Math.Max(1,view.Height));
                view.FieldOfViewYDegrees=(float)(2*Math.Atan(Math.Tan(h*Math.PI/360)/aspect)*180/Math.PI);view.UpdateProjection(true);
                fovText=$"FOV {h:0.#} horizontal from {FovBone}";
            }else{RestoreAnimatedCameraFov();fovText=$"{FovBone} z {h:0.#} is not an angle; FOV unchanged";}
        }
        AnimatedCameraStatus=$"animated camera {AnimatedCamera}: {gate}; {fovText}";
    }
}
