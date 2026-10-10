namespace SSEPreview;

// Tab strip drawn in the application's dark theme. Each page carries its own accent colour (TabPage.Tag as a
// Color): the selected tab merges into the page below with a thick accent bar on top and white bold text, the
// others sit darker with a thin accent line and dimmer text, so the open tab and each tab's identity read at a glance.
internal sealed class ThemedTabControl:TabControl
{
    public static readonly Color Strip=Color.FromArgb(17,20,26),Body=Color.FromArgb(28,32,40),Idle=Color.FromArgb(36,42,53),Hover=Color.FromArgb(47,56,70);
    public static readonly Color TextIdle=Color.FromArgb(168,178,194),TextActive=Color.White,DefaultAccent=Color.FromArgb(64,156,255);
    int hot=-1;
    public ThemedTabControl()
    {
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        Padding=new Point(24,8);
        // Narrow layouts wrap the tabs to a second row instead of showing the unthemed scroll arrows.
        Multiline=true;
    }
    // Headers are always bold, whatever font the theme pass hands down; pages set their own fonts.
    public override Font Font{get=>base.Font;set=>base.Font=new Font(value.FontFamily,10f,FontStyle.Bold);}
    public static Color Accent(TabPage page)=>page.Tag is Color color?color:DefaultAccent;
    protected override void OnMouseMove(MouseEventArgs e)
    {
        int index=-1;for(int i=0;i<TabCount;i++)if(GetTabRect(i).Contains(e.Location)){index=i;break;}
        if(index!=hot){hot=index;Invalidate();}
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e){if(hot!=-1){hot=-1;Invalidate();}base.OnMouseLeave(e);}
    protected override void OnSelectedIndexChanged(EventArgs e){Invalidate();base.OnSelectedIndexChanged(e);}
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics;g.Clear(Body);
        int stripBottom=ItemSize.Height+4;for(int i=0;i<TabCount;i++)stripBottom=Math.Max(stripBottom,GetTabRect(i).Bottom+1);
        using(var strip=new SolidBrush(Strip))g.FillRectangle(strip,0,0,Width,stripBottom);
        for(int i=0;i<TabCount;i++){
            var page=TabPages[i];var accent=Accent(page);bool selected=i==SelectedIndex;
            var r=GetTabRect(i);var area=new Rectangle(r.X+1,r.Y,r.Width-3,r.Height+(r.Bottom+1>=stripBottom?1:-2));
            using(var fill=new SolidBrush(selected?Body:i==hot?Hover:Idle))g.FillRectangle(fill,area);
            using(var bar=new SolidBrush(selected?accent:Color.FromArgb(i==hot?200:110,accent)))
                g.FillRectangle(bar,selected?new Rectangle(area.X,area.Y,area.Width,4):new Rectangle(area.X,area.Bottom-2,area.Width,2));
            TextRenderer.DrawText(g,page.Text,Font,new Rectangle(area.X,area.Y+(selected?2:0),area.Width,area.Height-2),selected?TextActive:i==hot?Color.FromArgb(222,229,240):TextIdle,
                TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.EndEllipsis);
        }
        // A line in the selected tab's colour joins the strip to the page it opens.
        if(SelectedIndex>=0&&SelectedIndex<TabCount)using(var line=new SolidBrush(Color.FromArgb(150,Accent(TabPages[SelectedIndex]))))g.FillRectangle(line,0,stripBottom,Width,1);
    }
}
