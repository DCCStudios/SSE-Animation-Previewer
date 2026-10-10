namespace SSEPreview;

// Drop-down list with a filter box above the choices. The ComboBox API (Items, SelectedIndex,
// SelectedItem, DisplayMember, SelectedIndexChanged) is unchanged, so existing bindings and
// smoke tests keep working; only the native popup is replaced by a searchable one.
public class SearchableComboBox : ComboBox
{
    ToolStripDropDown? popup;ToolStripControlHost? host;TextBox? filter;ListBox? list;
    readonly List<int> visible=[];DateTime closedAt;
    public string SearchPlaceholder {get;set;}="Type to filter…";
    // When set, the filter typed last time is still there the next time the list opens, also after the items change.
    public bool KeepFilter {get;set;}
    public string FilterText=>filter?.Text??"";
    public bool Matches(object item)=>FilterTerms().All(t=>(GetItemText(item)??"").Contains(t,StringComparison.OrdinalIgnoreCase));
    string[] FilterTerms()=>(filter?.Text??"").Split(' ',StringSplitOptions.RemoveEmptyEntries);
    public SearchableComboBox(){DropDownStyle=ComboBoxStyle.DropDownList;}
    internal int VisibleCount=>visible.Count;
    internal bool PopupOpen=>popup?.Visible==true;
    internal Bitmap? CapturePopup(){if(popup==null||!popup.Visible)return null;var image=new Bitmap(popup.Width,popup.Height);popup.DrawToBitmap(image,new Rectangle(Point.Empty,popup.Size));return image;}
    protected override void WndProc(ref Message m)
    {
        const int WM_LBUTTONDOWN=0x201,WM_LBUTTONDBLCLK=0x203;
        if((m.Msg==WM_LBUTTONDOWN||m.Msg==WM_LBUTTONDBLCLK)&&Enabled){if(!Focused)Focus();OpenPopup();return;}
        base.WndProc(ref m);
    }
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
    {
        if(keyData is Keys.F4 or (Keys.Alt|Keys.Down) or (Keys.Alt|Keys.Up)){OpenPopup();return true;}
        return base.ProcessCmdKey(ref msg,keyData);
    }
    internal void OpenPopup()
    {
        if(popup!=null&&popup.Visible)return;
        // The click that dismissed the popup lands on this control next; do not reopen from it.
        if((DateTime.UtcNow-closedAt).TotalMilliseconds<250)return;
        EnsurePopup();
        if(!KeepFilter)filter!.Text="";Refill();
        int width=Math.Max(Width,DropDownWidth);int height=Math.Max(260,DropDownHeight);
        host!.Size=new Size(width,height);popup!.Size=host.Size;
        popup.Show(this,new Point(0,Height));
        filter.Focus();
    }
    internal void SetFilter(string text){EnsurePopup();filter!.Text=text;if(!PopupOpen)Refill();}
    internal void CommitSelection()
    {
        if(list==null||list.SelectedIndex<0||list.SelectedIndex>=visible.Count)return;
        int index=visible[list.SelectedIndex];popup?.Close();
        if(index!=SelectedIndex)SelectedIndex=index;
    }
    void EnsurePopup()
    {
        if(popup!=null)return;
        filter=new TextBox{Dock=DockStyle.Top,PlaceholderText=SearchPlaceholder,BorderStyle=BorderStyle.FixedSingle,BackColor=BackColor,ForeColor=ForeColor,Font=Font};
        list=new WrappedListBox{Dock=DockStyle.Fill,BorderStyle=BorderStyle.None,IntegralHeight=false,BackColor=BackColor,ForeColor=ForeColor,Font=Font};
        var panel=new Panel{Padding=new Padding(1),BackColor=Color.FromArgb(70,80,95)};
        panel.Controls.Add(list);panel.Controls.Add(filter);
        host=new ToolStripControlHost(panel){Margin=Padding.Empty,Padding=Padding.Empty,AutoSize=false};
        popup=new ToolStripDropDown{Padding=Padding.Empty,Margin=Padding.Empty,AutoSize=false,DropShadowEnabled=true};
        popup.Items.Add(host);
        popup.Closed+=(_,_)=>closedAt=DateTime.UtcNow;
        filter.TextChanged+=(_,_)=>Refill();
        filter.KeyDown+=(_,e)=>{
            switch(e.KeyCode){
                case Keys.Down:MoveSelection(1);break;case Keys.Up:MoveSelection(-1);break;
                case Keys.PageDown:MoveSelection(10);break;case Keys.PageUp:MoveSelection(-10);break;
                case Keys.Enter:CommitSelection();break;case Keys.Escape:popup.Close();break;
                default:return;
            }
            e.Handled=true;e.SuppressKeyPress=true;
        };
        list.MouseClick+=(_,e)=>{if(list.IndexFromPoint(e.Location)>=0)CommitSelection();};
        list.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Enter){CommitSelection();e.Handled=true;e.SuppressKeyPress=true;}else if(e.KeyCode==Keys.Escape){popup.Close();e.Handled=true;}};
    }
    void MoveSelection(int delta)
    {
        if(list==null||list.Items.Count==0)return;
        list.SelectedIndex=Math.Clamp(list.SelectedIndex+delta,0,list.Items.Count-1);
    }
    void Refill()
    {
        if(list==null||filter==null)return;
        var terms=FilterTerms();
        list.BeginUpdate();list.Items.Clear();visible.Clear();
        for(int i=0;i<Items.Count;i++){
            string text=GetItemText(Items[i])??"";
            if(terms.All(t=>text.Contains(t,StringComparison.OrdinalIgnoreCase))){visible.Add(i);list.Items.Add(text);}
        }
        int current=visible.IndexOf(SelectedIndex);
        list.SelectedIndex=current>=0?current:list.Items.Count>0?0:-1;
        list.EndUpdate();((WrappedListBox)list).Reflow();
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing){popup?.Dispose();popup=null;}
        base.Dispose(disposing);
    }
}
