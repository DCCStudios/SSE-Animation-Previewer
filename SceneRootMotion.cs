using OpenTK.Mathematics;
using M4=OpenTK.Mathematics.Matrix4;
using V3d=OpenTK.Mathematics.Vector3d;

namespace SSEPreview;

// Root motion marker: a flat arrow on the ground at the actor root, the point the AMR animmotion / animrotation
// annotations move (NPC Root [Root] hangs from it). The arrow's base sits on that point and it points along the
// actor's facing (+Y turned by the accumulated yaw). Shown while Root motion is ticked and a clip is loaded; parts the
// character covers show faintly through it.
public sealed partial class Scene
{
    readonly SmpDebugDraw rootArrow=new();
    public static readonly Color4 RootArrowFill=new(1f,.72f,.15f,.85f),RootArrowEdge=new(1f,.88f,.45f,1f);
    // Arrow size in game units: a character is about 128 tall.
    public const double RootArrowLength=34,RootArrowTail=6,RootArrowShaft=2.5,RootArrowHead=8,RootArrowHeadLength=13,RootArrowLift=.6;
    public int RootArrowTriangles=>rootArrow.LastTriangles;
    // Where the arrow is drawn for a scene time, or null when it is hidden.
    public (V3d Origin,V3d Forward)? RootArrowAt(double time)
    {
        if(!RootMotion||Clip==null)return null;
        var actor=ActorWorld(time);
        var forward=V3d.TransformVector(V3d.UnitY,actor);forward.Z=0;
        if(forward.LengthSquared<1e-12)forward=V3d.UnitY;
        return (actor.ExtractTranslation(),forward.Normalized());
    }
    // Follow camera with a clip: the focus keeps its offset from the actor root and moves with it, so the view travels
    // with the root motion without re-framing, zooming or turning. Anything else that moves the focus (Frame, a
    // rebuild, framing on load) sets a new offset.
    OpenTK.Mathematics.Vector3? followOffset;OpenTK.Mathematics.Vector3 lastFollowFocus;
    public OpenTK.Mathematics.Vector3 RootPosition=>(OpenTK.Mathematics.Vector3)(RootMotion&&Clip!=null?ActorWorld(Time).ExtractTranslation():V3d.Zero);
    void TrackRoot()
    {
        var root=RootPosition;var focus=view.camera.FocusPosition;
        // A focus that is not where tracking left it was moved by something else (a pan, a re-frame): that move is
        // added to the offset, so root travel in the same frame is not lost.
        if(followOffset==null)followOffset=focus-root;
        else if((focus-lastFollowFocus).LengthSquared>1e-6f)followOffset+=focus-lastFollowFocus;
        var target=root+followOffset.Value;lastFollowFocus=target;
        if((target-focus).LengthSquared<=1e-8f)return;
        view.camera.FocusPosition=target;view.UpdateProjection(true);view.UpdateRequired=true;
    }
    void DrawRootMotionArrow(M4 projection)
    {
        if(RootArrowAt(Time) is not {} arrow){rootArrow.Begin();rootArrow.End();return;}
        var (origin,forward)=arrow;var right=new V3d(forward.Y,-forward.X,0);var up=new V3d(0,0,RootArrowLift);
        V3d P(double along,double across)=>origin+up+forward*along+right*across;
        double neck=RootArrowLength-RootArrowHeadLength;
        V3d tailLeft=P(-RootArrowTail,-RootArrowShaft),tailRight=P(-RootArrowTail,RootArrowShaft),neckLeft=P(neck,-RootArrowShaft),neckRight=P(neck,RootArrowShaft);
        V3d headLeft=P(neck,-RootArrowHead),headRight=P(neck,RootArrowHead),tip=P(RootArrowLength,0);
        rootArrow.Begin();
        rootArrow.Triangle(tailLeft,tailRight,neckRight,RootArrowFill);rootArrow.Triangle(tailLeft,neckRight,neckLeft,RootArrowFill);
        rootArrow.Triangle(headLeft,headRight,tip,RootArrowFill);
        V3d[] outline=[tailLeft,neckLeft,headLeft,tip,headRight,neckRight,tailRight];
        for(int i=0;i<outline.Length;i++)rootArrow.Segment(outline[i],outline[(i+1)%outline.Length],RootArrowEdge);
        // A short post marks the root point itself.
        rootArrow.Segment(origin,origin+new V3d(0,0,9),RootArrowEdge);
        rootArrow.End();
        rootArrow.Draw(projection,view.camera,occluded:.3f);
    }
}
