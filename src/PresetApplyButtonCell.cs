using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;

namespace Ncp
{
    sealed class PresetApplyButtonCell:DataGridViewButtonCell
    {
        public bool Enabled=true;

        public override object Clone()
        {
            var cell=(PresetApplyButtonCell)base.Clone();
            cell.Enabled=Enabled;
            return cell;
        }

        protected override void Paint(Graphics graphics,Rectangle clipBounds,Rectangle cellBounds,int rowIndex,
            DataGridViewElementStates elementState,object value,object formattedValue,string errorText,
            DataGridViewCellStyle cellStyle,DataGridViewAdvancedBorderStyle advancedBorderStyle,DataGridViewPaintParts paintParts)
        {
            if(Enabled) {
                base.Paint(graphics,clipBounds,cellBounds,rowIndex,elementState,value,formattedValue,errorText,cellStyle,advancedBorderStyle,paintParts);
                return;
            }
            base.Paint(graphics,clipBounds,cellBounds,rowIndex,elementState,value,formattedValue,errorText,cellStyle,advancedBorderStyle,
                paintParts & ~DataGridViewPaintParts.ContentBackground & ~DataGridViewPaintParts.ContentForeground);
            var border=BorderWidths(advancedBorderStyle);
            var bounds=new Rectangle(cellBounds.X+border.X+1,cellBounds.Y+border.Y+1,
                Math.Max(0,cellBounds.Width-border.X-border.Width-2),Math.Max(0,cellBounds.Height-border.Y-border.Height-2));
            if((paintParts & DataGridViewPaintParts.ContentBackground)!=0)
                ButtonRenderer.DrawButton(graphics,bounds,PushButtonState.Disabled);
            if((paintParts & DataGridViewPaintParts.ContentForeground)!=0)
                TextRenderer.DrawText(graphics,Convert.ToString(formattedValue),cellStyle.Font,bounds,SystemColors.GrayText,
                    TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis);
        }

        protected override void OnMouseDown(DataGridViewCellMouseEventArgs e) {if(Enabled)base.OnMouseDown(e);}
        protected override void OnMouseUp(DataGridViewCellMouseEventArgs e) {if(Enabled)base.OnMouseUp(e);}
        protected override void OnKeyDown(KeyEventArgs e,int rowIndex) {if(Enabled)base.OnKeyDown(e,rowIndex);else e.Handled=true;}
        protected override void OnKeyUp(KeyEventArgs e,int rowIndex) {if(Enabled)base.OnKeyUp(e,rowIndex);else e.Handled=true;}
    }
}
