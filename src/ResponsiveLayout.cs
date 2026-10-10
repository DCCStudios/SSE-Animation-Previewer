using System.Runtime.InteropServices;
namespace SSEPreview;
internal static class ResponsiveLayout {
    public static void Fit(FlowLayoutPanel panel,int width){
        panel.SuspendLayout();
        foreach(Control c in panel.Controls){
            int available=Math.Max(60,width-c.Margin.Horizontal);
            if(c is Label label){label.MaximumSize=new Size(available,0);}
            else if(c is not Button && c is not CheckBox && c is not NumericUpDown)c.Width=available;
        }
        panel.ResumeLayout();
    }
    public static TextFormatFlags Flags=>TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix|TextFormatFlags.TextBoxControl;
}
internal class WrappedListBox:ListBox {
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd,uint message,IntPtr wparam,IntPtr lparam);
    bool reflowing;
    public WrappedListBox(){DrawMode=DrawMode.OwnerDrawVariable;HorizontalScrollbar=false;IntegralHeight=false;ScrollAlwaysVisible=true;}
    internal int RequiredHeight(int index)=>Math.Clamp(TextRenderer.MeasureText(GetItemText(Items[index]),Font,new Size(Math.Max(40,ClientSize.Width-22),4096),ResponsiveLayout.Flags).Height+8,Font.Height+8,255);
    public void Reflow(){
        if(!IsHandleCreated||reflowing)return;reflowing=true;
        try{for(int i=0;i<Items.Count;i++)SendMessage(Handle,0x01A0,(IntPtr)i,(IntPtr)RequiredHeight(i));Invalidate();}finally{reflowing=false;}
    }
    protected override void OnMeasureItem(MeasureItemEventArgs e){if(e.Index>=0&&e.Index<Items.Count)e.ItemHeight=RequiredHeight(e.Index);}
    protected override void OnDrawItem(DrawItemEventArgs e){if(e.Index<0)return;e.DrawBackground();var area=new Rectangle(e.Bounds.Left+3,e.Bounds.Top+4,Math.Max(1,ClientSize.Width-22),e.Bounds.Height-8);TextRenderer.DrawText(e.Graphics,GetItemText(Items[e.Index]),Font,area,e.ForeColor,ResponsiveLayout.Flags);e.DrawFocusRectangle();}
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);Reflow();}
    protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);Reflow();}
    protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);Reflow();}
}
internal class WrappedCheckedListBox:CheckedListBox {
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd,uint message,IntPtr wparam,IntPtr lparam);
    void ResizeRows(){if(IsHandleCreated)SendMessage(Handle,0x01A0,IntPtr.Zero,(IntPtr)(Font.Height*(ClientSize.Width<220?3:2)+6));Invalidate();}
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);ResizeRows();}
    protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);ResizeRows();}
    protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);ResizeRows();}
    protected override void OnDrawItem(DrawItemEventArgs e){
        if(e.Index<0)return;e.DrawBackground();
        var mark=new Rectangle(e.Bounds.Left+2,e.Bounds.Top+3,14,14);
        ControlPaint.DrawCheckBox(e.Graphics,mark,GetItemChecked(e.Index)?ButtonState.Checked:ButtonState.Normal);
        var text=new Rectangle(e.Bounds.Left+22,e.Bounds.Top+2,Math.Max(1,e.Bounds.Width-24),e.Bounds.Height-4);
        TextRenderer.DrawText(e.Graphics,GetItemText(Items[e.Index]),Font,text,e.ForeColor,ResponsiveLayout.Flags);e.DrawFocusRectangle();
    }
}
internal class ResponsiveTreeView:TreeView {
    protected override CreateParams CreateParams {get{var p=base.CreateParams;p.Style|=0x8000;return p;}}
    public ResponsiveTreeView(){DrawMode=TreeViewDrawMode.OwnerDrawText;ItemHeight=Font.Height+6;}
    // Explorer-style multiple selection: Ctrl+click toggles a node, Shift+click marks the visible range from the
    // last clicked node. A plain click selects one node as before. Marked nodes are drawn highlighted.
    readonly List<TreeNode> marked=[];TreeNode? anchor;
    // A row colour that replaces the normal and the selection background (the playing clip in the animation library).
    public Func<TreeNode,Color?>? RowColor;
    public IReadOnlyList<TreeNode> Marked=>marked.Where(n=>n.TreeView==this).ToList();
    public bool IsMarked(TreeNode node)=>marked.Contains(node);
    public void ClearMarks(){marked.Clear();anchor=null;Invalidate();}
    public void Mark(TreeNode node,bool additive)
    {
        if(!additive){marked.Clear();}
        if(additive&&marked.Remove(node)){anchor=node;Invalidate();return;}
        if(!marked.Contains(node))marked.Add(node);anchor=node;Invalidate();
    }
    public void MarkRange(TreeNode node)
    {
        var start=anchor??SelectedNode??node;
        var visible=new List<TreeNode>();for(var n=Nodes.Count>0?Nodes[0]:null;n!=null;n=n.NextVisibleNode)visible.Add(n);
        int a=visible.IndexOf(start),b=visible.IndexOf(node);if(a<0||b<0){Mark(node,true);return;}
        marked.Clear();for(int i=Math.Min(a,b);i<=Math.Max(a,b);i++)marked.Add(visible[i]);
        Invalidate();
    }
    protected override void OnBeforeSelect(TreeViewCancelEventArgs e)
    {
        var keys=ModifierKeys;
        if(e.Node!=null&&(keys&(Keys.Control|Keys.Shift))!=0){
            // The first modified click keeps the node that was already selected in the set.
            if(marked.Count==0&&SelectedNode!=null)marked.Add(SelectedNode);
            if((keys&Keys.Shift)!=0)MarkRange(e.Node);else Mark(e.Node,true);
            e.Cancel=true;return;
        }
        base.OnBeforeSelect(e);
    }
    protected override void OnAfterSelect(TreeViewEventArgs e){if(e.Node!=null)Mark(e.Node,false);base.OnAfterSelect(e);}
    // Clicking the node that is already selected raises no BeforeSelect, so modified clicks are caught here too.
    protected override void OnNodeMouseClick(TreeNodeMouseClickEventArgs e)
    {
        if(e.Button==MouseButtons.Left&&e.Node==SelectedNode&&(ModifierKeys&(Keys.Control|Keys.Shift))!=0){
            if((ModifierKeys&Keys.Shift)!=0)MarkRange(e.Node);else Mark(e.Node,true);
        }
        base.OnNodeMouseClick(e);
    }
    protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);ItemHeight=Font.Height+6;}
    protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);Invalidate();}
    protected override void OnDrawNode(DrawTreeNodeEventArgs e){
        if(e.Node==null)return;
        var area=new Rectangle(e.Bounds.X,e.Bounds.Y,Math.Max(1,ClientSize.Width-e.Bounds.X-4),ItemHeight);
        bool selected=marked.Count>1?marked.Contains(e.Node):(e.State&TreeNodeStates.Selected)!=0;
        var row=RowColor?.Invoke(e.Node);
        using var brush=new SolidBrush(row??(selected?SystemColors.Highlight:BackColor));e.Graphics.FillRectangle(brush,area);
        // A marked or selected row keeps a selection-coloured frame over its own colour.
        if(row!=null&&selected&&marked.Count>1){using var pen=new Pen(SystemColors.Highlight,2);e.Graphics.DrawRectangle(pen,area.X+1,area.Y+1,area.Width-2,area.Height-2);}
        TextRenderer.DrawText(e.Graphics,e.Node.Text,Font,area,row!=null?Color.White:selected?SystemColors.HighlightText:(e.Node.ForeColor.IsEmpty?ForeColor:e.Node.ForeColor),TextFormatFlags.SingleLine|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
    }
}
