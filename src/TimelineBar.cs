using System.Drawing.Drawing2D;

namespace SSEPreview;

// The playback timeline: a tall bar with a large scrubber. Pressing anywhere on it moves the scrubber to that point
// and raises Scroll; holding the button and moving keeps scrubbing. It keeps TrackBar's Minimum / Maximum / Value /
// Scroll shape so the window code treats it the same way.
public sealed class TimelineBar:Control
{
    public static readonly Color Track=Color.FromArgb(58,60,68),Played=Color.FromArgb(64,156,255),Thumb=Color.FromArgb(236,238,242),ThumbHover=Color.White,Grip=Color.FromArgb(90,96,110);
    public const int ThumbWidth=24,ThumbHeight=48,TrackHeight=16;
    int minimum,maximum=10000,value;bool dragging,hover;
    public event EventHandler? Scroll;
    public TimelineBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint|ControlStyles.ResizeRedraw|ControlStyles.Selectable,true);
        Height=60;Cursor=Cursors.Hand;TabStop=true;
    }
    public int Minimum{get=>minimum;set{minimum=value;if(maximum<minimum)maximum=minimum;Value=this.value;Invalidate();}}
    public int Maximum{get=>maximum;set{maximum=Math.Max(value,minimum);Value=this.value;Invalidate();}}
    public int Value{get=>value;set{int v=Math.Clamp(value,minimum,maximum);if(v==this.value)return;this.value=v;Invalidate();}}
    // Horizontal pixel range the scrubber's centre travels over.
    int Left0=>ThumbWidth/2+4;int Right0=>Math.Max(Left0+1,ClientSize.Width-ThumbWidth/2-4);
    public int ValueAt(int x)=>minimum+(int)Math.Round(Math.Clamp((x-Left0)/(double)(Right0-Left0),0,1)*(maximum-minimum));
    public int XOf(int v)=>Left0+(int)Math.Round((v-minimum)/(double)Math.Max(1,maximum-minimum)*(Right0-Left0));
    void MoveTo(int x){int v=ValueAt(x);bool changed=v!=value;Value=v;if(changed||dragging)Scroll?.Invoke(this,EventArgs.Empty);}
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if(e.Button!=MouseButtons.Left)return;
        Focus();dragging=true;MoveTo(e.X);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool over=Math.Abs(e.X-XOf(value))<=ThumbWidth;if(over!=hover){hover=over;Invalidate();}
        if(dragging&&(e.Button&MouseButtons.Left)!=0)MoveTo(e.X);
    }
    protected override void OnMouseUp(MouseEventArgs e){dragging=false;base.OnMouseUp(e);}
    protected override void OnMouseCaptureChanged(EventArgs e){if(!Capture)dragging=false;base.OnMouseCaptureChanged(e);}
    // Value units covered by one pixel of travel.
    internal double ValuePerPixel=>(maximum-minimum)/(double)(Right0-Left0);
    protected override void OnMouseLeave(EventArgs e){hover=false;Invalidate();base.OnMouseLeave(e);}
    // Arrow keys nudge by half a percent, Page Up / Down by a tenth, Home / End jump to the ends.
    protected override bool IsInputKey(Keys keyData)=>keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown||base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int small=Math.Max(1,(maximum-minimum)/200),large=Math.Max(1,(maximum-minimum)/10);
        int? target=e.KeyCode switch{Keys.Left=>value-small,Keys.Right=>value+small,Keys.PageDown=>value-large,Keys.PageUp=>value+large,Keys.Home=>minimum,Keys.End=>maximum,_=>null};
        if(target is int t){Value=t;Scroll?.Invoke(this,EventArgs.Empty);e.Handled=true;}
    }
    // Test hooks that run the same paths as a real left-button press, drag and release at a client X.
    internal void Press(int x)=>OnMouseDown(new MouseEventArgs(MouseButtons.Left,1,x,Height/2,0));
    internal void DragTo(int x)=>OnMouseMove(new MouseEventArgs(MouseButtons.Left,0,x,Height/2,0));
    internal void Release(int x)=>OnMouseUp(new MouseEventArgs(MouseButtons.Left,1,x,Height/2,0));
    protected override void OnGotFocus(EventArgs e){Invalidate();base.OnGotFocus(e);}
    protected override void OnLostFocus(EventArgs e){Invalidate();base.OnLostFocus(e);}
    static GraphicsPath Rounded(RectangleF r,float radius)
    {
        var path=new GraphicsPath();float d=Math.Min(radius*2,Math.Min(r.Width,r.Height));
        path.AddArc(r.Left,r.Top,d,d,180,90);path.AddArc(r.Right-d,r.Top,d,d,270,90);path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(r.Left,r.Bottom-d,d,d,90,90);
        path.CloseFigure();return path;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(BackColor);
        float mid=ClientSize.Height/2f;int x=XOf(value);
        var track=new RectangleF(Left0-ThumbWidth/2f,mid-TrackHeight/2f,Right0-Left0+ThumbWidth,TrackHeight);
        using(var path=Rounded(track,TrackHeight/2f))using(var brush=new SolidBrush(Track))g.FillPath(brush,path);
        var played=new RectangleF(track.Left,track.Top,Math.Max(TrackHeight,x-track.Left),TrackHeight);
        using(var path=Rounded(played,TrackHeight/2f))using(var brush=new SolidBrush(Played))g.FillPath(brush,path);
        float height=Math.Min(ThumbHeight,ClientSize.Height-4);
        var thumb=new RectangleF(x-ThumbWidth/2f,mid-height/2f,ThumbWidth,height);
        using(var path=Rounded(thumb,6)){
            using(var brush=new SolidBrush(hover||dragging?ThumbHover:Thumb))g.FillPath(brush,path);
            using(var pen=new Pen(Focused?Played:Grip,2))g.DrawPath(pen,path);
        }
        using(var pen=new Pen(Grip,2))for(int i=-1;i<=1;i++)g.DrawLine(pen,x+i*5,mid-height/4,x+i*5,mid+height/4);
    }
}
