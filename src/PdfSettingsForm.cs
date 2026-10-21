using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace WindowsWorkAssistant
{
    internal sealed class PdfSettingsForm : Form
    {
        private readonly TextBox _folder=new TextBox { Dock=DockStyle.Fill };
        private readonly CheckBox _recursive=new CheckBox { Text="包含所有子文件夹",AutoSize=true };
        private readonly NumericUpDown _timeout=new NumericUpDown { Minimum=30,Maximum=3600,Increment=30,Value=180,Width=90 };
        public Dictionary<string,object> Config { get; private set; }
        public PdfSettingsForm(Dictionary<string,object> config)
        {
            Text="Excel 监控设置";Font=new Font("Microsoft YaHei UI",9F);AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
            ClientSize=new Size(540,220);MinimumSize=new Size(500,250);StartPosition=FormStartPosition.CenterParent;MaximizeBox=false;MinimizeBox=false;
            Config=new Dictionary<string,object>(config);
            object value;if(config.TryGetValue("monitor_folder",out value))_folder.Text=Convert.ToString(value);
            if(config.TryGetValue("recursive",out value))_recursive.Checked=Convert.ToBoolean(value);
            if(config.TryGetValue("task_timeout",out value))_timeout.Value=Math.Max(30,Math.Min(3600,Convert.ToInt32(value)));
            var grid=new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=2,RowCount=5 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text="监控文件夹",AutoSize=true },0,0);
            grid.Controls.Add(_folder,0,1);var browse=new Button { Text="选择…",AutoSize=true };
            browse.Click+=delegate { using(var picker=new FolderBrowserDialog { SelectedPath=_folder.Text })if(picker.ShowDialog(this)==DialogResult.OK)_folder.Text=picker.SelectedPath; };
            grid.Controls.Add(browse,1,1);grid.Controls.Add(_recursive,0,2);
            var timeoutRow=new FlowLayoutPanel { AutoSize=true,Dock=DockStyle.Fill };
            timeoutRow.Controls.Add(new Label { Text="单文件超时（秒）",AutoSize=true,Padding=new Padding(0,6,0,0) });timeoutRow.Controls.Add(_timeout);
            grid.Controls.Add(timeoutRow,0,3);grid.SetColumnSpan(timeoutRow,2);
            var save=new Button { Text="保存",AutoSize=true };
            save.Click+=delegate {
                if(string.IsNullOrWhiteSpace(_folder.Text)||!Directory.Exists(_folder.Text)) { MessageBox.Show(this,"请选择存在的监控目录");return; }
                Config["monitor_folder"]=Path.GetFullPath(_folder.Text.Trim());Config["recursive"]=_recursive.Checked;Config["task_timeout"]=(int)_timeout.Value;
                DialogResult=DialogResult.OK;Close();
            };
            grid.Controls.Add(save,1,4);Controls.Add(grid);AcceptButton=save;
        }
    }
}
