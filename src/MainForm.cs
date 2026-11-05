using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Ncp
{
    sealed class RangeRow:UserControl
    {
        readonly TrackBar slider;
        readonly NumericUpDown number;
        readonly decimal factor;
        bool loading;
        public event EventHandler Changed;
        public double Value
        {
            get {return (double)number.Value;}
            set {number.Value=Math.Max(number.Minimum,Math.Min(number.Maximum,(decimal)value));}
        }
        public RangeRow(string name,int min,int max,int initial,bool gamma=false)
        {
            factor=gamma?100:1;
            Dock=DockStyle.Fill; Margin=new Padding(0);
            var table=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=new Padding(0)};
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,132));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,76));
            table.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            table.Controls.Add(new Label {Text=name,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Margin=new Padding(0)},0,0);
            slider=new TrackBar {Minimum=min,Maximum=max,Value=initial,TickStyle=TickStyle.None,
                SmallChange=1,LargeChange=gamma?10:5,AutoSize=false,Height=32,
                Anchor=AnchorStyles.Left|AnchorStyles.Right,Margin=new Padding(0,0,6,0),AccessibleName=name+" 슬라이더"};
            number=new NumericUpDown {Minimum=min/factor,Maximum=max/factor,Value=initial/factor,
                DecimalPlaces=gamma?2:0,Increment=1/factor,TextAlign=HorizontalAlignment.Right,
                Anchor=AnchorStyles.Left|AnchorStyles.Right,Margin=new Padding(0),AccessibleName=name+" 값"};
            table.Controls.Add(slider,1,0);table.Controls.Add(number,2,0);Controls.Add(table);
            slider.ValueChanged+=delegate {
                if(loading)return; loading=true; number.Value=slider.Value/factor; loading=false; Notify();
            };
            number.ValueChanged+=delegate {
                if(loading)return; loading=true; slider.Value=(int)(number.Value*factor); loading=false; Notify();
            };
        }
        void Notify() {if(Changed!=null)Changed(this,EventArgs.Empty);}
    }

    sealed class PresetListItem
    {
        public Preset Preset;
    }

    public sealed class MainForm:Form
    {
        readonly Controller controller;
        readonly bool demo;
        readonly string startupError;
        readonly ToolTip noticeTip=new ToolTip();
        readonly Icon appIcon;
        TableLayoutPanel root;
        ComboBox display;
        ComboBox profiles;
        DataGridView presets;
        TextBox name,iccName;
        IccProfileRef selectedIcc;
        Label notice;
        RangeRow brightness,contrast,gamma,vibrance,hue;
        Button save,duplicate,delete,import,export;
        Button profileDelete,profileRename,profileInitial,iccClear;
        Preset selected;
        bool loading,dirty,pendingDelete;
        bool pendingProfileDelete;
        Rectangle dragStartBounds;
        string dragPresetId;
        bool dragging,suppressPresetClick;
        int dropInsertionIndex=-1;
        readonly string dragToken="NCP.PresetOrder:"+Guid.NewGuid().ToString("N")+":";

        public MainForm(Controller controller,string error,bool demo)
        {
            this.controller=controller; startupError=error; this.demo=demo;
            using(var stream=typeof(MainForm).Assembly.GetManifestResourceStream("Ncp.AppIcon"))
            using(var embeddedIcon=new Icon(stream)) appIcon=(Icon)embeddedIcon.Clone();
            Icon=appIcon; ShowIcon=true; ShowInTaskbar=true;
            Text="NVIDIA Color Preset"+(demo?" (데모)":"");
            Font=SystemFonts.MessageBoxFont; BackColor=SystemColors.Control; ForeColor=SystemColors.ControlText;
            AutoScaleMode=AutoScaleMode.Dpi;
            ClientSize=new Size(900,550); MinimumSize=new Size(860,570); StartPosition=FormStartPosition.CenterScreen;
            Build();
            Shown+=delegate {Run(delegate {
                if(demo && controller.Data.Presets.Count==0)
                    controller.SavePreset(null,"FPS",new ColorValues {Brightness=53,Contrast=58,Gamma=1.12,Vibrance=72,Hue=0});
                if(startupError==null)controller.RefreshDisplays();
                ReloadDisplays();ReloadProfiles();
                SelectPreset(controller.LastAppliedPreset() ?? controller.DefaultPreset());
                RebuildPresets();
                controller.CleanupIccBackups();
                if(startupError!=null)ShowError(startupError);
                else if(controller.StorageError!=null)ShowError(controller.StorageError);
            });};
        }

        static Button MakeButton(string text)
        {
            return new Button {Text=text,Dock=DockStyle.Fill,Margin=new Padding(3),UseVisualStyleBackColor=true};
        }
        void Build()
        {
            root=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(12),ColumnCount=1,RowCount=4};
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,0)); Controls.Add(root);

            var profileBar=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=9,RowCount=1,Margin=new Padding(0,0,0,6)};
            profileBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,86));profileBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach(int width in new[]{86,76,60,116,76,76,60})profileBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,width));
            profileBar.Controls.Add(new Label {Text="프로필",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,0);
            profiles=new ComboBox {Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="프로필 목록",Margin=new Padding(3,4,6,3)};
            profiles.SelectedIndexChanged+=delegate {
                if(loading || profiles.SelectedItem==null)return;
                Run(delegate {controller.SwitchProfile(((PresetProfile)profiles.SelectedItem).Id);SelectPreset(controller.LastAppliedPreset()??controller.DefaultPreset());RebuildPresets();pendingProfileDelete=false;profileDelete.Text="삭제";});
            };
            profileBar.Controls.Add(profiles,1,0);
            profileRename=MakeButton("이름 변경");profileRename.Click+=delegate {RenameProfile();};profileBar.Controls.Add(profileRename,2,0);
            var profileNew=MakeButton("새 프로필");profileNew.Click+=delegate {CreateProfile(false);};profileBar.Controls.Add(profileNew,3,0);
            var profileCopy=MakeButton("복사");profileCopy.Click+=delegate {CreateProfile(true);};profileBar.Controls.Add(profileCopy,4,0);
            profileInitial=MakeButton("초기 프로필 추가");profileInitial.Click+=delegate {AddInitialProfile();};profileBar.Controls.Add(profileInitial,5,0);
            var profileImport=MakeButton("가져오기");profileImport.Click+=delegate {ImportProfile();};profileBar.Controls.Add(profileImport,6,0);
            var profileExport=MakeButton("내보내기");profileExport.Click+=delegate {ExportProfile();};profileBar.Controls.Add(profileExport,7,0);
            profileDelete=MakeButton("삭제");profileDelete.Click+=delegate {DeleteProfile();};profileBar.Controls.Add(profileDelete,8,0);root.Controls.Add(profileBar,0,0);

            var monitors=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=new Padding(0,0,0,6)};
            monitors.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,86));
            monitors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            monitors.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,94));
            monitors.Controls.Add(new Label {Text="디스플레이",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,0);
            display=new ComboBox {Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="대상 디스플레이",Margin=new Padding(3,4,6,3)};
            display.SelectedIndexChanged+=delegate {
                if(loading || display.SelectedItem==null)return;
                Run(delegate {
                    controller.Select((DisplayTarget)display.SelectedItem);
                    SelectPreset(controller.LastAppliedPreset() ?? controller.DefaultPreset()); RebuildPresets();
                });
            };
            monitors.Controls.Add(display,1,0);
            var refresh=MakeButton("새로고침");
            refresh.Click+=delegate {Run(delegate {
                controller.RefreshDisplays(); ReloadDisplays();
                if(!dirty)SelectPreset(controller.LastAppliedPreset() ?? controller.DefaultPreset());
                RebuildPresets(); UpdateEditor();
            });};
            monitors.Controls.Add(refresh,2,0); root.Controls.Add(monitors,0,1);

            var body=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0)};
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,280));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); root.Controls.Add(body,0,2);

            var listGroup=new GroupBox {Text="프리셋",Dock=DockStyle.Fill,Padding=new Padding(9),Margin=new Padding(0,0,8,0)};
            var listLayout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=new Padding(0)};
            listLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            listLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,36)); listGroup.Controls.Add(listLayout);
            presets=new DataGridView {Dock=DockStyle.Fill,AccessibleName="프리셋 목록",Margin=new Padding(0,3,0,6),AllowDrop=true,
                AllowUserToAddRows=false,AllowUserToDeleteRows=false,AllowUserToResizeRows=false,AllowUserToResizeColumns=false,
                AutoGenerateColumns=false,ReadOnly=true,MultiSelect=false,RowHeadersVisible=false,ColumnHeadersVisible=false,
                SelectionMode=DataGridViewSelectionMode.FullRowSelect,BackgroundColor=SystemColors.Window,
                BorderStyle=BorderStyle.Fixed3D,CellBorderStyle=DataGridViewCellBorderStyle.SingleHorizontal};
            presets.RowTemplate.Height=30;
            presets.Columns.Add(new DataGridViewTextBoxColumn {Name="Preset",AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill,SortMode=DataGridViewColumnSortMode.NotSortable});
            presets.Columns.Add(new DataGridViewButtonColumn {Name="Apply",CellTemplate=new PresetApplyButtonCell(),UseColumnTextForButtonValue=false,Width=70,SortMode=DataGridViewColumnSortMode.NotSortable});
            presets.SelectionChanged+=delegate {
                if(loading)return;
                var item=presets.SelectedRows.Count==1?presets.SelectedRows[0].Tag as PresetListItem:null;
                if(item!=null && (selected==null || selected.Id!=item.Preset.Id))SelectPreset(item.Preset);
            };
            presets.CellMouseClick+=delegate(object sender,DataGridViewCellMouseEventArgs e) {
                if(dragging || suppressPresetClick || e.Button!=MouseButtons.Left || e.RowIndex<0 || e.ColumnIndex!=1)return;
                var button=presets.Rows[e.RowIndex].Cells[1] as PresetApplyButtonCell;
                if(button==null || !button.Enabled)return;
                var item=presets.Rows[e.RowIndex].Tag as PresetListItem;
                if(item!=null)ApplyPreset(item.Preset);
            };
            presets.MouseDown+=PresetMouseDown;
            presets.MouseMove+=PresetMouseMove;
            presets.MouseUp+=delegate {dragPresetId=null;};
            presets.DragEnter+=PresetDragOver;
            presets.DragOver+=PresetDragOver;
            presets.DragLeave+=delegate {SetDropInsertionIndex(-1);};
            presets.DragDrop+=PresetDragDrop;
            presets.Paint+=PaintDropInsertion;
            listLayout.Controls.Add(presets,0,0);
            var sharing=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0)};
            sharing.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50)); sharing.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            import=MakeButton("가져오기"); import.Click+=delegate {Import();};
            export=MakeButton("내보내기"); export.Click+=delegate {Export();};
            sharing.Controls.Add(import,0,0); sharing.Controls.Add(export,1,0); listLayout.Controls.Add(sharing,0,1); body.Controls.Add(listGroup,0,0);

            var editorGroup=new GroupBox {Text="색상 설정",Dock=DockStyle.Fill,Padding=new Padding(10),Margin=new Padding(0)};
            var editor=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=9,Margin=new Padding(0)};
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute,30));
            for(int i=0;i<5;i++)editor.RowStyles.Add(new RowStyle(SizeType.Percent,20));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute,8));
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute,36)); editorGroup.Controls.Add(editor);

            var naming=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0)};
            naming.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,132)); naming.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            naming.Controls.Add(new Label {Text="이름",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Margin=new Padding(0)},0,0);
            name=new TextBox {Dock=DockStyle.Fill,MaxLength=60,AccessibleName="프리셋 이름",Margin=new Padding(0,3,0,3)};
            name.TextChanged+=delegate {Edited();}; naming.Controls.Add(name,1,0); editor.Controls.Add(naming,0,0);

            brightness=new RangeRow("밝기",0,100,50); contrast=new RangeRow("대비",0,100,50);
            gamma=new RangeRow("감마",30,280,100,true); vibrance=new RangeRow("디지털 바이브런스",0,100,50);
            hue=new RangeRow("색조",0,359,0);
            RangeRow[] rows={brightness,contrast,gamma,vibrance,hue};
            for(int i=0;i<rows.Length;i++) {editor.Controls.Add(rows[i],0,i+1); rows[i].Changed+=delegate {Edited();};}

            var iccRow=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=4,RowCount=1,Margin=new Padding(0)};
            iccRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,44));iccRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            iccRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,84));iccRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,56));
            iccRow.Controls.Add(new Label {Text="ICC",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Margin=new Padding(0)},0,0);
            iccName=new TextBox {ReadOnly=true,Dock=DockStyle.Fill,Margin=new Padding(0,7,3,3),AccessibleName="ICC 파일"};iccRow.Controls.Add(iccName,1,0);
            var iccImport=MakeButton("불러오기");iccImport.Click+=delegate {ImportIcc();};iccRow.Controls.Add(iccImport,2,0);
            iccClear=MakeButton("해제");iccClear.Click+=delegate {selectedIcc=null;UpdateIccName();Edited();};iccRow.Controls.Add(iccClear,3,0);editor.Controls.Add(iccRow,0,6);

            var editing=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=new Padding(0)};
            for(int i=0;i<3;i++)editing.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100/3f));
            save=MakeButton("변경 저장"); save.Click+=delegate {Save(false);};
            duplicate=MakeButton("새 프리셋 저장"); duplicate.Click+=delegate {Save(true);};
            delete=MakeButton("삭제"); delete.Click+=delegate {Delete();};
            editing.Controls.Add(save,0,0); editing.Controls.Add(duplicate,1,0); editing.Controls.Add(delete,2,0); editor.Controls.Add(editing,0,8);
            body.Controls.Add(editorGroup,1,0);

            notice=new Label {Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,Margin=new Padding(0)};
            root.Controls.Add(notice,0,3);
        }

        void Run(Action action)
        {
            UseWaitCursor=true; notice.Text=""; root.RowStyles[3].Height=0;
            try {action();}
            catch(Exception e) {RebuildPresets(); UpdateEditor(); ShowError(e.Message);}
            finally {UseWaitCursor=false;}
        }
        void ShowError(string text)
        {
            notice.Text=text.Replace("\r","").Replace("\n","  ");
            noticeTip.SetToolTip(notice,text); root.RowStyles[3].Height=34;
        }
        void ReloadProfiles()
        {
            loading=true;profiles.Items.Clear();foreach(var profile in controller.Data.Profiles)profiles.Items.Add(profile);
            profiles.SelectedItem=controller.ActiveProfile;loading=false;pendingProfileDelete=false;profileDelete.Text="삭제";
            profileDelete.Enabled=controller.CanSave && controller.Data.Profiles.Count>1;
            profileInitial.Enabled=controller.CanSave && controller.Data.Profiles.Count<50;
            profileRename.Enabled=controller.CanSave;
        }
        void ReloadDisplays()
        {
            loading=true; display.Items.Clear(); foreach(var d in controller.Displays)display.Items.Add(d);
            if(controller.Selected!=null)display.SelectedItem=controller.Selected; loading=false;
        }
        void RebuildPresets()
        {
            string selectedId=selected==null?null:selected.Id;
            var last=controller.LastAppliedPreset();
            bool matched=controller.HasReadableMatch;
            loading=true; presets.SuspendLayout(); presets.Rows.Clear();
            int selectedIndex=-1;
            foreach(var p in controller.Presets()) {
                bool applied=matched && last!=null && last.Id==p.Id;
                int index=presets.Rows.Add(p.Name,applied?"적용 중":"적용");
                presets.Rows[index].Tag=new PresetListItem {Preset=p};
                ((PresetApplyButtonCell)presets.Rows[index].Cells[1]).Enabled=!applied && controller.Selected!=null && controller.CanSave;
                if(p.Id==selectedId)selectedIndex=index;
            }
            presets.ClearSelection();
            if(selectedIndex>=0) {
                presets.CurrentCell=presets.Rows[selectedIndex].Cells[0];
                presets.Rows[selectedIndex].Selected=true;
            }
            presets.ResumeLayout(); loading=false;
        }
        void PresetMouseDown(object sender,MouseEventArgs e)
        {
            suppressPresetClick=false; dragPresetId=null;
            if(e.Button!=MouseButtons.Left || !controller.CanSave)return;
            var hit=presets.HitTest(e.X,e.Y);
            if(hit.RowIndex<0 || hit.ColumnIndex!=0)return;
            var item=presets.Rows[hit.RowIndex].Tag as PresetListItem;
            if(item==null)return;
            dragPresetId=item.Preset.Id;
            var size=SystemInformation.DragSize;
            dragStartBounds=new Rectangle(e.X-size.Width/2,e.Y-size.Height/2,size.Width,size.Height);
        }
        void PresetMouseMove(object sender,MouseEventArgs e)
        {
            if(e.Button!=MouseButtons.Left || dragPresetId==null || dragging || dragStartBounds.Contains(e.Location))return;
            var payload=new DataObject(DataFormats.UnicodeText,dragToken+dragPresetId);
            dragPresetId=null; dragging=true; suppressPresetClick=true;
            try {presets.DoDragDrop(payload,DragDropEffects.Move);}
            finally {dragging=false; dragPresetId=null; SetDropInsertionIndex(-1);}
        }
        string OwnDrag(IDataObject data)
        {
            if(data==null || !data.GetDataPresent(DataFormats.UnicodeText,false))return null;
            var value=data.GetData(DataFormats.UnicodeText,false) as string;
            if(value==null || !value.StartsWith(dragToken,StringComparison.Ordinal) || value.Length==dragToken.Length)return null;
            return value.Substring(dragToken.Length);
        }
        int DropInsertionIndex(Point point)
        {
            if(!presets.ClientRectangle.Contains(point))return -1;
            var hit=presets.HitTest(point.X,point.Y);
            if(hit.RowIndex>=0) {
                var bounds=presets.GetRowDisplayRectangle(hit.RowIndex,false);
                return hit.RowIndex+(point.Y>=bounds.Top+bounds.Height/2?1:0);
            }
            if(presets.Rows.Count==0)return 0;
            int first=Math.Max(0,presets.FirstDisplayedScrollingRowIndex);
            if(point.Y<=presets.GetRowDisplayRectangle(first,false).Top)return first;
            int last=first;
            while(last+1<presets.Rows.Count && presets.Rows[last+1].Displayed)last++;
            return last+1;
        }
        void PresetDragOver(object sender,DragEventArgs e)
        {
            e.Effect=DragDropEffects.None;
            if(!controller.CanSave || (e.AllowedEffect & DragDropEffects.Move)==0 || OwnDrag(e.Data)==null) {
                SetDropInsertionIndex(-1); return;
            }
            var point=presets.PointToClient(new Point(e.X,e.Y));
            if(presets.ClientRectangle.Contains(point) && presets.Rows.Count>0) {
                int first=presets.FirstDisplayedScrollingRowIndex;
                if(point.Y<20 && first>0)presets.FirstDisplayedScrollingRowIndex=first-1;
                else if(point.Y>presets.ClientSize.Height-20 && first>=0 && first<presets.Rows.Count-1
                    && !presets.Rows[presets.Rows.Count-1].Displayed)presets.FirstDisplayedScrollingRowIndex=first+1;
            }
            int index=DropInsertionIndex(point);
            SetDropInsertionIndex(index);
            if(index>=0)e.Effect=DragDropEffects.Move;
        }
        void PresetDragDrop(object sender,DragEventArgs e)
        {
            e.Effect=DragDropEffects.None;
            var data=OwnDrag(e.Data);
            int index=DropInsertionIndex(presets.PointToClient(new Point(e.X,e.Y)));
            SetDropInsertionIndex(-1);
            if(!controller.CanSave || data==null || index<0 || (e.AllowedEffect & DragDropEffects.Move)==0)return;
            suppressPresetClick=true;
            Run(delegate {controller.MovePreset(data,index);RebuildPresets();});
            e.Effect=DragDropEffects.Move;
        }
        void SetDropInsertionIndex(int index)
        {
            if(dropInsertionIndex==index)return;
            dropInsertionIndex=index; presets.Invalidate();
        }
        void PaintDropInsertion(object sender,PaintEventArgs e)
        {
            if(dropInsertionIndex<0 || presets.Rows.Count==0)return;
            int row=Math.Min(dropInsertionIndex,presets.Rows.Count-1);
            var bounds=presets.GetRowDisplayRectangle(row,false);
            int y=dropInsertionIndex==presets.Rows.Count?bounds.Bottom-1:bounds.Top;
            if(y<0 || y>=presets.ClientSize.Height)return;
            using(var pen=new Pen(SystemColors.ControlDarkDark,2))e.Graphics.DrawLine(pen,1,y,presets.ClientSize.Width-2,y);
        }
        void SelectPreset(Preset preset)
        {
            selected=preset; loading=true; name.Text=preset.Name;
            brightness.Value=preset.Values.Brightness; contrast.Value=preset.Values.Contrast; gamma.Value=preset.Values.Gamma;
            vibrance.Value=preset.Values.Vibrance; hue.Value=preset.Values.Hue;
            selectedIcc=preset.Icc;UpdateIccName();
            loading=false; dirty=false; pendingDelete=false; UpdateEditor();
        }
        ColorValues EditorValues()
        {
            return new ColorValues {Brightness=brightness.Value,Contrast=contrast.Value,Gamma=gamma.Value,Vibrance=(int)vibrance.Value,Hue=(int)hue.Value};
        }
        void Edited()
        {
            if(loading || selected==null)return;
            dirty=!EditorValues().Same(selected.Values) || name.Text!=selected.Name || !IccProfileRef.Equal(selectedIcc,selected.Icc); pendingDelete=false; UpdateEditor();
        }
        void UpdateEditor()
        {
            if(selected==null)return;
            save.Enabled=selected.Id!="default" && dirty && controller.CanSave;
            duplicate.Enabled=controller.CanSave; import.Enabled=controller.CanSave;
            delete.Enabled=selected.Id!="default" && controller.CanSave; delete.Text=pendingDelete?"삭제 확인":"삭제";
            export.Enabled=true;
            iccClear.Enabled=selectedIcc!=null;
        }
        void Save(bool asNew)
        {
            Run(delegate {
                string title=name.Text;
                if(asNew && selected!=null && title==selected.Name)
                    title=selected.Id=="default"?"새 프리셋":title.Substring(0,Math.Min(54,title.Length))+" 복사";
                var saved=controller.SavePreset(asNew?null:selected.Id,title,EditorValues(),selectedIcc);
                SelectPreset(saved);
                if(!asNew) {
                    try {controller.Apply(saved);}
                    catch(Exception e) {throw new InvalidOperationException("변경 내용은 저장되었습니다.\n"+e.Message,e);}
                }
                RebuildPresets();
            });
        }
        void Delete()
        {
            if(selected==null || selected.Id=="default")return;
            if(!pendingDelete) {pendingDelete=true; UpdateEditor(); return;}
            Run(delegate {controller.Delete(selected.Id); SelectPreset(controller.LastAppliedPreset() ?? controller.DefaultPreset()); RebuildPresets();});
        }
        void ApplyPreset(Preset preset)
        {
            Run(delegate {controller.Apply(preset); SelectPreset(controller.LastAppliedPreset() ?? preset); RebuildPresets();});
        }
        void Import()
        {
            using(var dialog=new OpenFileDialog {Title="NCP 프리셋 가져오기",Filter="NCP 프리셋 (*.ncpreset;*.json)|*.ncpreset;*.json",Multiselect=false})
                if(dialog.ShowDialog(this)==DialogResult.OK)Run(delegate {SelectPreset(controller.Import(dialog.FileName)); RebuildPresets();});
        }
        void Export()
        {
            if(selected==null)return;
            using(var dialog=new SaveFileDialog {Title="NCP 프리셋 내보내기",Filter="NCP 프리셋 (*.ncpreset)|*.ncpreset",FileName=ExportFileName(selected.Name,".ncpreset"),DefaultExt="ncpreset",AddExtension=true})
                if(dialog.ShowDialog(this)==DialogResult.OK)Run(delegate {controller.ExportPreset(dialog.FileName,selected);});
        }
        void UpdateIccName() {iccName.Text=selectedIcc==null?"원래 설정":selectedIcc.Name;}
        void ImportIcc()
        {
            using(var dialog=new OpenFileDialog {Title="ICC 파일 불러오기",Filter="ICC 색상 프로필 (*.icc;*.icm)|*.icc;*.icm",Multiselect=false})
                if(dialog.ShowDialog(this)==DialogResult.OK)Run(delegate {selectedIcc=controller.ImportIcc(dialog.FileName);UpdateIccName();Edited();});
        }
        static string AskProfileName(IWin32Window owner,string initial)
        {
            using(var dialog=new Form {Text="프로필 이름",ClientSize=new Size(340,88),FormBorderStyle=FormBorderStyle.FixedDialog,StartPosition=FormStartPosition.CenterParent,MaximizeBox=false,MinimizeBox=false,ShowInTaskbar=false,Font=SystemFonts.MessageBoxFont}) {
                var input=new TextBox {Text=initial,MaxLength=60,Left=12,Top=12,Width=316};
                var ok=new Button {Text="확인",DialogResult=DialogResult.OK,Left=174,Top=48,Width=72};
                var cancel=new Button {Text="취소",DialogResult=DialogResult.Cancel,Left=256,Top=48,Width=72};
                dialog.Controls.AddRange(new Control[]{input,ok,cancel});dialog.AcceptButton=ok;dialog.CancelButton=cancel;
                return dialog.ShowDialog(owner)==DialogResult.OK?input.Text:null;
            }
        }
        void CreateProfile(bool copy)
        {
            string current=controller.ActiveProfile.Name;
            string value=AskProfileName(this,copy?current.Substring(0,Math.Min(57,current.Length))+" 복사":"새 프로필");if(value==null)return;
            Run(delegate {controller.CreateProfile(value,copy);ReloadProfiles();SelectPreset(controller.DefaultPreset());RebuildPresets();});
        }
        void DeleteProfile()
        {
            if(!pendingProfileDelete) {pendingProfileDelete=true;profileDelete.Text="삭제 확인";return;}
            Run(delegate {controller.DeleteProfile();ReloadProfiles();SelectPreset(controller.LastAppliedPreset()??controller.DefaultPreset());RebuildPresets();});
        }
        void RenameProfile()
        {
            string value=AskProfileName(this,controller.ActiveProfile.Name);if(value==null)return;
            Run(delegate {controller.RenameProfile(value);ReloadProfiles();});
        }
        void ImportProfile()
        {
            using(var dialog=new OpenFileDialog {Title="NCP 프로필 가져오기",Filter="NCP 프로필 (*.ncpprofile)|*.ncpprofile",Multiselect=false})
                if(dialog.ShowDialog(this)==DialogResult.OK)Run(delegate {controller.ImportProfile(dialog.FileName);ReloadProfiles();SelectPreset(controller.DefaultPreset());RebuildPresets();});
        }
        void AddInitialProfile()
        {
            Run(delegate {controller.AddInitialProfile();ReloadProfiles();SelectPreset(controller.DefaultPreset());RebuildPresets();});
        }
        void ExportProfile()
        {
            using(var dialog=new SaveFileDialog {Title="NCP 프로필 내보내기",Filter="NCP 프로필 (*.ncpprofile)|*.ncpprofile",FileName=ExportFileName(controller.ActiveProfile.Name,".ncpprofile"),DefaultExt="ncpprofile",AddExtension=true})
                if(dialog.ShowDialog(this)==DialogResult.OK)Run(delegate {controller.ExportProfile(dialog.FileName);});
        }
        static string ExportFileName(string presetName,string extension=".json")
        {
            var invalid=Path.GetInvalidFileNameChars();
            string fileName=new string(presetName.Select(c=>invalid.Contains(c)?'_':c).ToArray()).Trim().TrimEnd('.');
            if(string.IsNullOrWhiteSpace(fileName))fileName="프리셋";
            if(System.Text.RegularExpressions.Regex.IsMatch(fileName,@"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                fileName="_"+fileName;
            return fileName+extension;
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing)noticeTip.Dispose(); base.Dispose(disposing);
            if(disposing && appIcon!=null)appIcon.Dispose();
        }
    }
}
