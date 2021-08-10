// Roger Briggen license this file to you under the MIT license.
//


namespace RogerBriggen.MyDupFinderWin
{
    partial class StartScreen
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.tabMain = new System.Windows.Forms.TabControl();
            this.tabScan = new System.Windows.Forms.TabPage();
            this.tabFindDups = new System.Windows.Forms.TabPage();
            this.button1 = new System.Windows.Forms.Button();
            this.tv = new System.Windows.Forms.TreeView();
            this.statusStrip1 = new System.Windows.Forms.StatusStrip();
            this.btnExpandAll = new System.Windows.Forms.Button();
            this.btnCollapseAll = new System.Windows.Forms.Button();
            this.tabMain.SuspendLayout();
            this.tabFindDups.SuspendLayout();
            this.SuspendLayout();
            // 
            // tabMain
            // 
            this.tabMain.Controls.Add(this.tabScan);
            this.tabMain.Controls.Add(this.tabFindDups);
            this.tabMain.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tabMain.Location = new System.Drawing.Point(0, 0);
            this.tabMain.Name = "tabMain";
            this.tabMain.SelectedIndex = 0;
            this.tabMain.Size = new System.Drawing.Size(800, 575);
            this.tabMain.TabIndex = 1;
            // 
            // tabScan
            // 
            this.tabScan.Location = new System.Drawing.Point(4, 29);
            this.tabScan.Name = "tabScan";
            this.tabScan.Padding = new System.Windows.Forms.Padding(3);
            this.tabScan.Size = new System.Drawing.Size(792, 542);
            this.tabScan.TabIndex = 1;
            this.tabScan.Text = "Scan";
            this.tabScan.UseVisualStyleBackColor = true;
            // 
            // tabFindDups
            // 
            this.tabFindDups.Controls.Add(this.btnCollapseAll);
            this.tabFindDups.Controls.Add(this.btnExpandAll);
            this.tabFindDups.Controls.Add(this.button1);
            this.tabFindDups.Controls.Add(this.tv);
            this.tabFindDups.Location = new System.Drawing.Point(4, 29);
            this.tabFindDups.Name = "tabFindDups";
            this.tabFindDups.Padding = new System.Windows.Forms.Padding(3);
            this.tabFindDups.Size = new System.Drawing.Size(792, 542);
            this.tabFindDups.TabIndex = 0;
            this.tabFindDups.Text = "FindDups";
            this.tabFindDups.UseVisualStyleBackColor = true;
            // 
            // button1
            // 
            this.button1.Location = new System.Drawing.Point(638, 175);
            this.button1.Name = "button1";
            this.button1.Size = new System.Drawing.Size(94, 29);
            this.button1.TabIndex = 1;
            this.button1.Text = "button1";
            this.button1.UseVisualStyleBackColor = true;
            this.button1.Click += button1_Click;
            // 
            // tv
            // 
            this.tv.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tv.Location = new System.Drawing.Point(0, 0);
            this.tv.Name = "tv";
            this.tv.ShowNodeToolTips = true;
            this.tv.Size = new System.Drawing.Size(556, 521);
            this.tv.TabIndex = 0;
            // 
            // statusStrip1
            // 
            this.statusStrip1.ImageScalingSize = new System.Drawing.Size(20, 20);
            this.statusStrip1.Location = new System.Drawing.Point(0, 553);
            this.statusStrip1.Name = "statusStrip1";
            this.statusStrip1.Size = new System.Drawing.Size(800, 22);
            this.statusStrip1.TabIndex = 3;
            this.statusStrip1.Text = "statusStrip1";
            // 
            // btnExpandAll
            // 
            this.btnExpandAll.Location = new System.Drawing.Point(609, 25);
            this.btnExpandAll.Name = "btnExpandAll";
            this.btnExpandAll.Size = new System.Drawing.Size(94, 29);
            this.btnExpandAll.TabIndex = 2;
            this.btnExpandAll.Text = "Expand All";
            this.btnExpandAll.UseVisualStyleBackColor = true;
            this.btnExpandAll.Click += new System.EventHandler(this.btnExpandAll_Click);
            // 
            // btnCollapseAll
            // 
            this.btnCollapseAll.Location = new System.Drawing.Point(609, 70);
            this.btnCollapseAll.Name = "btnCollapseAll";
            this.btnCollapseAll.Size = new System.Drawing.Size(94, 29);
            this.btnCollapseAll.TabIndex = 3;
            this.btnCollapseAll.Text = "CollapseAll";
            this.btnCollapseAll.UseVisualStyleBackColor = true;
            this.btnCollapseAll.Click += new System.EventHandler(this.btnCollapseAll_Click);
            // 
            // StartScreen
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 20F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(800, 575);
            this.Controls.Add(this.statusStrip1);
            this.Controls.Add(this.tabMain);
            this.Name = "StartScreen";
            this.Text = "MyDupFinderWin";
            this.tabMain.ResumeLayout(false);
            this.tabFindDups.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.TabControl tabMain;
        private System.Windows.Forms.TabPage tabFindDups;
        private System.Windows.Forms.TabPage tabScan;
        private System.Windows.Forms.Button button1;
        private System.Windows.Forms.TreeView tv;
        private System.Windows.Forms.StatusStrip statusStrip1;
        private System.Windows.Forms.Button btnCollapseAll;
        private System.Windows.Forms.Button btnExpandAll;
    }
}
