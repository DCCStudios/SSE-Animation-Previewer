using FO4_Base_Library;
namespace SSEPreview;

public sealed class Viewport : PreviewControl
{
    public Action? FrameRequested;
    public Action? ManualCameraRequested;
    public bool RequireAlt {get;set;}=true;
    internal bool CanStartDrag(Keys modifiers)=>!RequireAlt||(modifiers&Keys.Alt)!=0;
    // Closest the camera gets to its focus point, in game units. The engine's own limit is 20, which stops well short
    // of a face or a hand.
    public const float ClosestZoom=2;
    Point last;
    MouseButtons drag;
    public void Zoom(float delta){camera.MinDistance=ClosestZoom;camera.Zoom(delta);}
    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();last=e.Location;
        if(CanStartDrag(ModifierKeys)) {ManualCameraRequested?.Invoke();drag=e.Button;Capture=true;}
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if(drag==MouseButtons.None)return;
        int dx=e.X-last.X,dy=e.Y-last.Y;last=e.Location;
        if(drag==MouseButtons.Left)camera.Rotate(-dx,dy);
        if(drag==MouseButtons.Middle)camera.Pan(dx,dy);
        if(drag==MouseButtons.Right)Zoom((dx-dy)*.08f);
        UpdateRequired=true;Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e) {drag=MouseButtons.None;Capture=false;}
    protected override void OnMouseWheel(MouseEventArgs e) {ManualCameraRequested?.Invoke();Zoom(e.Delta/120f);UpdateRequired=true;Invalidate();}
    protected override void OnKeyDown(KeyEventArgs e) {if(e.KeyCode==Keys.F){if(FrameRequested!=null)FrameRequested();else ResetCamera(true);e.Handled=true;}else base.OnKeyDown(e);}
}
