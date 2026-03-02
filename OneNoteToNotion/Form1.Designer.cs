namespace OneNoteToNotion;

partial class Form1
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    ///  Clean up any resources being used.
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
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        toolStripTop = new ToolStrip();
        toolStripReload = new ToolStripButton();
        toolStripSync = new ToolStripButton();
        toolStripCancel = new ToolStripButton();
        toolStripBulkArchive = new ToolStripButton();
        toolStripBulkMove = new ToolStripButton();
        toolStripOpenNotion = new ToolStripButton();
        splitContainerMain = new SplitContainer();
        treeViewOneNote = new TreeView();
        panelRight = new Panel();
        labelEmbeddedHint = new Label();
        webViewNotion = new Microsoft.Web.WebView2.WinForms.WebView2();
        panelConfig = new Panel();
        textBoxMoveParentId = new TextBox();
        labelMoveParent = new Label();
        checkBoxDryRun = new CheckBox();
        numericRetryCount = new NumericUpDown();
        labelRetryCount = new Label();
        textBoxParentPageId = new TextBox();
        labelParentPage = new Label();
        textBoxNotionToken = new TextBox();
        labelToken = new Label();
        buttonTokenHelp = new Button();
        statusStripBottom = new StatusStrip();
        toolStripStatusLabel = new ToolStripStatusLabel();
        toolStripProgressBar = new ToolStripProgressBar();
        toolStripTop.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)splitContainerMain).BeginInit();
        splitContainerMain.Panel1.SuspendLayout();
        splitContainerMain.Panel2.SuspendLayout();
        splitContainerMain.SuspendLayout();
        panelRight.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)webViewNotion).BeginInit();
        panelConfig.SuspendLayout();
        statusStripBottom.SuspendLayout();
        SuspendLayout();
        // 
        // toolStripTop
        // 
        toolStripTop.ImageScalingSize = new Size(20, 20);
        toolStripTop.Items.AddRange(new ToolStripItem[] { toolStripReload, toolStripSync, toolStripCancel, toolStripBulkArchive, toolStripBulkMove, toolStripOpenNotion });
        toolStripTop.Location = new Point(0, 0);
        toolStripTop.Name = "toolStripTop";
        toolStripTop.Size = new Size(1422, 27);
        toolStripTop.TabIndex = 0;
        // 
        // toolStripReload
        // 
        toolStripReload.DisplayStyle = ToolStripItemDisplayStyle.Text;
        toolStripReload.Name = "toolStripReload";
        toolStripReload.Size = new Size(87, 24);
        toolStripReload.Text = "刷新OneNote";
        toolStripReload.Click += ToolStripButtonReload_Click;
        // 
        // toolStripSync
        // 
        toolStripSync.DisplayStyle = ToolStripItemDisplayStyle.Text;
        toolStripSync.Name = "toolStripSync";
        toolStripSync.Size = new Size(58, 24);
        toolStripSync.Text = "批量同步";
        toolStripSync.Click += ToolStripButtonSync_Click;
        // 
        // toolStripCancel
        // 
        toolStripCancel.DisplayStyle = ToolStripItemDisplayStyle.Text;
        toolStripCancel.Name = "toolStripCancel";
        toolStripCancel.Size = new Size(58, 24);
        toolStripCancel.Text = "取消同步";
        toolStripCancel.Enabled = false;
        toolStripCancel.Click += ToolStripButtonCancel_Click;
        // 
        // toolStripBulkArchive
        // 
        toolStripBulkArchive.DisplayStyle = ToolStripItemDisplayStyle.Text;
        toolStripBulkArchive.Name = "toolStripBulkArchive";
        toolStripBulkArchive.Size = new Size(74, 24);
        toolStripBulkArchive.Text = "批量删除";
        toolStripBulkArchive.Click += ToolStripButtonBulkArchive_Click;
        // 
        // toolStripBulkMove
        // 
        toolStripBulkMove.DisplayStyle = ToolStripItemDisplayStyle.Text;
        toolStripBulkMove.Name = "toolStripBulkMove";
        toolStripBulkMove.Size = new Size(74, 24);
        toolStripBulkMove.Text = "批量移动";
        toolStripBulkMove.Click += ToolStripButtonBulkMove_Click;
        // 
        // toolStripOpenNotion
        // 
        toolStripOpenNotion.DisplayStyle = ToolStripItemDisplayStyle.Text;
        toolStripOpenNotion.Name = "toolStripOpenNotion";
        toolStripOpenNotion.Size = new Size(74, 24);
        toolStripOpenNotion.Text = "打开Notion";
        toolStripOpenNotion.Click += ToolStripButtonOpenNotion_Click;
        // 
        // splitContainerMain
        // 
        splitContainerMain.Dock = DockStyle.Fill;
        splitContainerMain.Location = new Point(0, 27);
        splitContainerMain.Name = "splitContainerMain";
        // 
        // splitContainerMain.Panel1
        // 
        splitContainerMain.Panel1.Controls.Add(treeViewOneNote);
        // 
        // splitContainerMain.Panel2
        // 
        splitContainerMain.Panel2.Controls.Add(panelRight);
        splitContainerMain.Panel2.Controls.Add(panelConfig);
        splitContainerMain.Size = new Size(1422, 768);
        splitContainerMain.SplitterDistance = 474;
        splitContainerMain.TabIndex = 1;
        // 
        // treeViewOneNote
        // 
        treeViewOneNote.CheckBoxes = true;
        treeViewOneNote.Dock = DockStyle.Fill;
        treeViewOneNote.Location = new Point(0, 0);
        treeViewOneNote.Name = "treeViewOneNote";
        treeViewOneNote.Size = new Size(474, 768);
        treeViewOneNote.TabIndex = 0;
        treeViewOneNote.AfterCheck += TreeViewOneNote_AfterCheck;
        // 
        // panelRight
        // 
        panelRight.BorderStyle = BorderStyle.FixedSingle;
        panelRight.Controls.Add(labelEmbeddedHint);
        panelRight.Controls.Add(webViewNotion);
        panelRight.Dock = DockStyle.Fill;
        panelRight.Location = new Point(0, 130);
        panelRight.Name = "panelRight";
        panelRight.Size = new Size(944, 638);
        panelRight.TabIndex = 1;
        // 
        // labelEmbeddedHint
        // 
        labelEmbeddedHint.Dock = DockStyle.Fill;
        labelEmbeddedHint.Location = new Point(0, 0);
        labelEmbeddedHint.Name = "labelEmbeddedHint";
        labelEmbeddedHint.Size = new Size(942, 636);
        labelEmbeddedHint.TabIndex = 1;
        labelEmbeddedHint.Text = "Notion 内嵌加载中...";
        labelEmbeddedHint.TextAlign = ContentAlignment.MiddleCenter;
        // 
        // webViewNotion
        // 
        webViewNotion.AllowExternalDrop = true;
        webViewNotion.CreationProperties = null;
        webViewNotion.DefaultBackgroundColor = Color.White;
        webViewNotion.Dock = DockStyle.Fill;
        webViewNotion.Location = new Point(0, 0);
        webViewNotion.Name = "webViewNotion";
        webViewNotion.Size = new Size(942, 636);
        webViewNotion.TabIndex = 0;
        webViewNotion.ZoomFactor = 1D;
        // 
        // panelConfig
        // 
        panelConfig.Controls.Add(numericRetryCount);
        panelConfig.Controls.Add(labelRetryCount);
        panelConfig.Controls.Add(textBoxMoveParentId);
        panelConfig.Controls.Add(labelMoveParent);
        panelConfig.Controls.Add(checkBoxDryRun);
        panelConfig.Controls.Add(textBoxParentPageId);
        panelConfig.Controls.Add(labelParentPage);
        panelConfig.Controls.Add(buttonTokenHelp);
        panelConfig.Controls.Add(textBoxNotionToken);
        panelConfig.Controls.Add(labelToken);
        panelConfig.Dock = DockStyle.Top;
        panelConfig.Location = new Point(0, 0);
        panelConfig.Name = "panelConfig";
        panelConfig.Size = new Size(944, 130);
        panelConfig.TabIndex = 0;
        // 
        // textBoxMoveParentId
        // 
        textBoxMoveParentId.Location = new Point(159, 92);
        textBoxMoveParentId.Name = "textBoxMoveParentId";
        textBoxMoveParentId.Size = new Size(639, 27);
        textBoxMoveParentId.TabIndex = 6;
        // 
        // labelMoveParent
        // 
        labelMoveParent.AutoSize = true;
        labelMoveParent.Location = new Point(14, 95);
        labelMoveParent.Name = "labelMoveParent";
        labelMoveParent.Size = new Size(137, 20);
        labelMoveParent.TabIndex = 5;
        labelMoveParent.Text = "批量移动目标Page：";
        // 
        // checkBoxDryRun
        // 
        checkBoxDryRun.AutoSize = true;
        checkBoxDryRun.Location = new Point(804, 52);
        checkBoxDryRun.Name = "checkBoxDryRun";
        checkBoxDryRun.Size = new Size(111, 24);
        checkBoxDryRun.TabIndex = 4;
        checkBoxDryRun.Text = "Dry Run预演";
        checkBoxDryRun.UseVisualStyleBackColor = true;
        // 
        // labelRetryCount
        // 
        labelRetryCount.AutoSize = true;
        labelRetryCount.Location = new Point(804, 95);
        labelRetryCount.Name = "labelRetryCount";
        labelRetryCount.Size = new Size(75, 20);
        labelRetryCount.TabIndex = 8;
        labelRetryCount.Text = "重试次数：";
        // 
        // numericRetryCount
        // 
        numericRetryCount.Location = new Point(885, 92);
        numericRetryCount.Name = "numericRetryCount";
        numericRetryCount.Size = new Size(60, 27);
        numericRetryCount.Minimum = 0;
        numericRetryCount.Maximum = 20;
        numericRetryCount.Value = 3;
        numericRetryCount.TabIndex = 9;
        // 
        // textBoxParentPageId
        // 
        textBoxParentPageId.Location = new Point(159, 52);
        textBoxParentPageId.Name = "textBoxParentPageId";
        textBoxParentPageId.Size = new Size(639, 27);
        textBoxParentPageId.TabIndex = 3;
        // 
        // labelParentPage
        // 
        labelParentPage.AutoSize = true;
        labelParentPage.Location = new Point(14, 55);
        labelParentPage.Name = "labelParentPage";
        labelParentPage.Size = new Size(122, 20);
        labelParentPage.TabIndex = 2;
        labelParentPage.Text = "Notion父页面ID：";
        // 
        // textBoxNotionToken
        // 
        textBoxNotionToken.Location = new Point(159, 14);
        textBoxNotionToken.Name = "textBoxNotionToken";
        textBoxNotionToken.PasswordChar = '*';
        textBoxNotionToken.Size = new Size(700, 27);
        textBoxNotionToken.TabIndex = 1;
        // 
        // buttonTokenHelp
        // 
        buttonTokenHelp.Location = new Point(865, 13);
        buttonTokenHelp.Name = "buttonTokenHelp";
        buttonTokenHelp.Size = new Size(60, 29);
        buttonTokenHelp.TabIndex = 7;
        buttonTokenHelp.Text = "获取";
        buttonTokenHelp.UseVisualStyleBackColor = true;
        buttonTokenHelp.Click += ButtonTokenHelp_Click;
        // 
        // labelToken
        // 
        labelToken.AutoSize = true;
        labelToken.Location = new Point(14, 17);
        labelToken.Name = "labelToken";
        labelToken.Size = new Size(102, 20);
        labelToken.TabIndex = 0;
        labelToken.Text = "Notion Token:";
        // 
        // statusStripBottom
        // 
        statusStripBottom.ImageScalingSize = new Size(20, 20);
        statusStripBottom.Items.AddRange(new ToolStripItem[] { toolStripStatusLabel, toolStripProgressBar });
        statusStripBottom.Location = new Point(0, 795);
        statusStripBottom.Name = "statusStripBottom";
        statusStripBottom.Size = new Size(1422, 26);
        statusStripBottom.TabIndex = 2;
        // 
        // toolStripStatusLabel
        // 
        toolStripStatusLabel.Name = "toolStripStatusLabel";
        toolStripStatusLabel.Size = new Size(39, 20);
        toolStripStatusLabel.Text = "就绪";
        // 
        // toolStripProgressBar
        // 
        toolStripProgressBar.Name = "toolStripProgressBar";
        toolStripProgressBar.Size = new Size(120, 18);
        // 
        // Form1
        // 
        AutoScaleDimensions = new SizeF(8F, 20F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1422, 821);
        Controls.Add(splitContainerMain);
        Controls.Add(statusStripBottom);
        Controls.Add(toolStripTop);
        Name = "Form1";
        Text = "OneNote To Notion";
        Load += Form1_Load;
        toolStripTop.ResumeLayout(false);
        toolStripTop.PerformLayout();
        splitContainerMain.Panel1.ResumeLayout(false);
        splitContainerMain.Panel2.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)splitContainerMain).EndInit();
        splitContainerMain.ResumeLayout(false);
        panelRight.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)webViewNotion).EndInit();
        panelConfig.ResumeLayout(false);
        panelConfig.PerformLayout();
        statusStripBottom.ResumeLayout(false);
        statusStripBottom.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private ToolStrip toolStripTop;
    private ToolStripButton toolStripReload;
    private ToolStripButton toolStripSync;
    private ToolStripButton toolStripCancel;
    private ToolStripButton toolStripBulkArchive;
    private ToolStripButton toolStripBulkMove;
    private ToolStripButton toolStripOpenNotion;
    private SplitContainer splitContainerMain;
    private TreeView treeViewOneNote;
    private Panel panelConfig;
    private Label labelToken;
    private TextBox textBoxNotionToken;
    private TextBox textBoxParentPageId;
    private Label labelParentPage;
    private CheckBox checkBoxDryRun;
    private TextBox textBoxMoveParentId;
    private Label labelMoveParent;
    private Panel panelRight;
    private Label labelEmbeddedHint;
    private Microsoft.Web.WebView2.WinForms.WebView2 webViewNotion;
    private StatusStrip statusStripBottom;
    private ToolStripStatusLabel toolStripStatusLabel;
    private ToolStripProgressBar toolStripProgressBar;
    private Button buttonTokenHelp;
    private NumericUpDown numericRetryCount;
    private Label labelRetryCount;
}
