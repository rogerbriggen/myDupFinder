// Roger Briggen license this file to you under the MIT license.
//

using System;
using System.Windows.Forms;

namespace RogerBriggen.MyDupFinderWin;

public partial class StartScreen : Form
{
    public StartScreen() => InitializeComponent();

    private void button1_Click(object sender, EventArgs e)
    {
        // Display a wait cursor while the TreeNodes are being created.
        Cursor.Current = Cursors.WaitCursor;

        // Suppress repainting the TreeView until all the objects have been created.
        tv.BeginUpdate();

        if (tv.SelectedNode is not null)
        {
            TreeNode newNode = new TreeNode("Text for \\new subnode");
            newNode.BackColor = System.Drawing.Color.AliceBlue;
            tv.SelectedNode.Nodes.Add(newNode);
            newNode.ToolTipText = $"FullPath: {newNode.FullPath} \n Found: 2 Times\nThis is nice";
        }
        else
        {
            TreeNode newNode = new TreeNode("Text for new node");
            newNode.BackColor = System.Drawing.Color.Red;
            tv.Nodes.Add(newNode);
        }


        // Reset the cursor to the default for all controls.
        Cursor.Current = Cursors.Default;

        // Begin repainting the TreeView.
        tv.EndUpdate();
    }

    private void btnCollapseAll_Click(object sender, EventArgs e) => tv.CollapseAll();

    private void btnExpandAll_Click(object sender, EventArgs e) => tv.ExpandAll();
}
