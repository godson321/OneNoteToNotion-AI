using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using OneNoteToNotion.Application;
using OneNoteToNotion.Domain;
using OneNoteToNotion.Infrastructure;
using OneNoteToNotion.Parsing;

namespace OneNoteToNotion;

public partial class Form1 : Form
{
    private static readonly Regex NotionPageIdRegex = new(@"[a-f0-9]{32}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string ConfigFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OneNoteToNotion", "config.json");

    private readonly IOneNoteHierarchyProvider _hierarchyProvider;
    private readonly NotionSyncOrchestrator _syncOrchestrator;
    private readonly NotionApiClient _notionApiClient;
    private readonly Dictionary<string, string> _lastSyncedPageMap = new();

    private CancellationTokenSource? _syncCts;
    private bool _webViewReady;

    public Form1(IOneNoteHierarchyProvider hierarchyProvider, NotionSyncOrchestrator syncOrchestrator, NotionApiClient notionApiClient)
    {
        _hierarchyProvider = hierarchyProvider;
        _syncOrchestrator = syncOrchestrator;
        _notionApiClient = notionApiClient;
        InitializeComponent();
        LoadConfig();

        DiagnosticLogger.Info("Form1 构造完成");
    }

    private async void Form1_Load(object sender, EventArgs e)
    {
        DiagnosticLogger.Info($"Form1_Load 开始, ApartmentState={System.Threading.Thread.CurrentThread.GetApartmentState()}");
        await InitializeWebViewAsync();

        try
        {
            await LoadHierarchyAsync();
            DiagnosticLogger.Info("Form1_Load 完成");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error("Form1_Load 加载层级失败", ex);
            HandleOneNoteInitializationFailure(ex);
        }
    }

    private async void ToolStripButtonReload_Click(object sender, EventArgs e)
    {
        DiagnosticLogger.Info("点击 刷新OneNote");

        if (!TryEnsureOneNoteNotRunning())
        {
            return;
        }

        try
        {
            await LoadHierarchyAsync();
            DiagnosticLogger.Info("刷新OneNote 成功");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error("刷新OneNote 失败", ex);
            HandleOneNoteInitializationFailure(ex);
        }
    }

    private async void ToolStripButtonSync_Click(object sender, EventArgs e)
    {
        var checkedNodes = GetCheckedNodes(treeViewOneNote.Nodes);
        if (checkedNodes.Count == 0)
        {
            MessageBox.Show("请先勾选需要同步的节点。");
            return;
        }

        if (!TryGetAuthInputs(out var token, out var parentPageId))
        {
            return;
        }

        var options = new SyncOptions(token, parentPageId, checkBoxDryRun.Checked);
        await RunSyncAsync(checkedNodes, options);
    }

    private void ToolStripButtonCancel_Click(object sender, EventArgs e)
    {
        _syncCts?.Cancel();
        toolStripCancel.Enabled = false;
        toolStripStatusLabel.Text = "正在取消...";
    }

    private async Task RunSyncAsync(List<OneNoteTreeNode> nodes, SyncOptions options)
    {
        _notionApiClient.MaxRetries = (int)numericRetryCount.Value;
        var totalNodes = NotionSyncOrchestrator.CountAllNodes(nodes);

        toolStripProgressBar.Style = ProgressBarStyle.Blocks;
        toolStripProgressBar.Minimum = 0;
        toolStripProgressBar.Maximum = totalNodes;
        toolStripProgressBar.Value = 0;
        toolStripStatusLabel.Text = $"正在同步... 0/{totalNodes}";
        SetToolbarEnabled(false);
        toolStripCancel.Enabled = true;

        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;

        var progress = new Progress<SyncProgress>(p =>
        {
            toolStripProgressBar.Value = Math.Min(p.Current, totalNodes);
            toolStripStatusLabel.Text = $"正在同步「{p.CurrentPageName}」 {p.Current}/{p.Total}";
        });

        try
        {
            var aggregateResult = new SyncResult();
            foreach (var node in nodes)
            {
                var result = await _syncOrchestrator.SyncAsync(node, options, progress, ct);
                aggregateResult.Merge(result);
            }

            if (!options.DryRun)
            {
                foreach (var pair in aggregateResult.SyncedPageMap)
                {
                    _lastSyncedPageMap[pair.Key] = pair.Value;
                }
            }

            var summary = $"同步完成：新建 {aggregateResult.CreatedPages}，内容页 {aggregateResult.SyncedContentPages}";
            if (aggregateResult.FailedPages.Count > 0)
            {
                summary += $"，失败 {aggregateResult.FailedPages.Count}";
            }
            toolStripStatusLabel.Text = summary;
            toolStripProgressBar.Value = totalNodes;

            if (options.DryRun && aggregateResult.LogEntries.Count > 0)
            {
                ShowDryRunLog(aggregateResult, summary);
            }
            else if (aggregateResult.FailedPages.Count > 0)
            {
                ShowFailedPagesDialog(aggregateResult.FailedPages.ToList(), options);
            }
            else
            {
                KillOneNoteProcess();
                NavigateToNotionPage(options.ParentPageId);
            }
        }
        catch (OperationCanceledException)
        {
            toolStripStatusLabel.Text = "同步已取消";
        }
        catch (Exception ex)
        {
            toolStripStatusLabel.Text = "同步失败";
            MessageBox.Show($"同步失败: {ex.Message}");
        }
        finally
        {
            _syncCts?.Dispose();
            _syncCts = null;
            toolStripCancel.Enabled = false;
            SetToolbarEnabled(true);
            toolStripProgressBar.Style = ProgressBarStyle.Blocks;
        }
    }

    private async Task RunRetrySyncAsync(List<FailedSyncItem> failedItems, SyncOptions options)
    {
        _notionApiClient.MaxRetries = (int)numericRetryCount.Value;
        var totalNodes = failedItems.Sum(f => NotionSyncOrchestrator.CountNodes(f.Node));

        toolStripProgressBar.Style = ProgressBarStyle.Blocks;
        toolStripProgressBar.Minimum = 0;
        toolStripProgressBar.Maximum = totalNodes;
        toolStripProgressBar.Value = 0;
        toolStripStatusLabel.Text = $"正在重试... 0/{totalNodes}";
        SetToolbarEnabled(false);
        toolStripCancel.Enabled = true;

        _syncCts = new CancellationTokenSource();
        var ct = _syncCts.Token;

        var progress = new Progress<SyncProgress>(p =>
        {
            toolStripProgressBar.Value = Math.Min(p.Current, totalNodes);
            toolStripStatusLabel.Text = $"正在重试「{p.CurrentPageName}」 {p.Current}/{p.Total}";
        });

        try
        {
            var result = await _syncOrchestrator.RetrySyncAsync(failedItems, options, progress, ct);

            if (!options.DryRun)
            {
                foreach (var pair in result.SyncedPageMap)
                {
                    _lastSyncedPageMap[pair.Key] = pair.Value;
                }
            }

            var summary = $"重试完成：新建 {result.CreatedPages}，内容页 {result.SyncedContentPages}";
            if (result.FailedPages.Count > 0)
            {
                summary += $"，失败 {result.FailedPages.Count}";
            }
            toolStripStatusLabel.Text = summary;
            toolStripProgressBar.Value = totalNodes;

            if (result.FailedPages.Count > 0)
            {
                ShowFailedPagesDialog(result.FailedPages.ToList(), options);
            }
        }
        catch (OperationCanceledException)
        {
            toolStripStatusLabel.Text = "重试已取消";
        }
        catch (Exception ex)
        {
            toolStripStatusLabel.Text = "重试失败";
            MessageBox.Show($"重试失败: {ex.Message}");
        }
        finally
        {
            _syncCts?.Dispose();
            _syncCts = null;
            toolStripCancel.Enabled = false;
            SetToolbarEnabled(true);
            toolStripProgressBar.Style = ProgressBarStyle.Blocks;
        }
    }

    private async void ToolStripButtonBulkArchive_Click(object sender, EventArgs e)
    {
        if (!TryGetTokenOnly(out var token))
        {
            return;
        }

        var pageIds = GetCheckedMappedNotionPageIds();
        if (pageIds.Count == 0)
        {
            MessageBox.Show("未找到可删除的 Notion 页面。请先完成同步。\n批量操作只处理“已同步映射”的页面。");
            return;
        }

        toolStripStatusLabel.Text = "正在批量删除...";
        toolStripProgressBar.Style = ProgressBarStyle.Marquee;
        SetToolbarEnabled(false);

        try
        {
            await _syncOrchestrator.ArchivePagesAsync(pageIds, token, CancellationToken.None);
            toolStripStatusLabel.Text = $"批量删除完成：{pageIds.Count} 个页面";
        }
        catch (Exception ex)
        {
            toolStripStatusLabel.Text = "批量删除失败";
            MessageBox.Show($"批量删除失败: {ex.Message}");
        }
        finally
        {
            SetToolbarEnabled(true);
            toolStripProgressBar.Style = ProgressBarStyle.Blocks;
        }
    }

    private async void ToolStripButtonBulkMove_Click(object sender, EventArgs e)
    {
        if (!TryGetTokenOnly(out var token))
        {
            return;
        }

        var targetParent = textBoxMoveParentId.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetParent))
        {
            MessageBox.Show("请填写批量移动目标 Page ID。");
            return;
        }

        var pageIds = GetCheckedMappedNotionPageIds();
        if (pageIds.Count == 0)
        {
            MessageBox.Show("未找到可移动的 Notion 页面。请先完成同步。\n批量操作只处理“已同步映射”的页面。");
            return;
        }

        toolStripStatusLabel.Text = "正在批量移动...";
        toolStripProgressBar.Style = ProgressBarStyle.Marquee;
        SetToolbarEnabled(false);

        try
        {
            await _syncOrchestrator.MovePagesAsync(pageIds, targetParent, token, CancellationToken.None);
            toolStripStatusLabel.Text = $"批量移动完成：{pageIds.Count} 个页面";
            NavigateToNotionPage(targetParent);
        }
        catch (Exception ex)
        {
            toolStripStatusLabel.Text = "批量移动失败";
            MessageBox.Show($"批量移动失败: {ex.Message}");
        }
        finally
        {
            SetToolbarEnabled(true);
            toolStripProgressBar.Style = ProgressBarStyle.Blocks;
        }
    }

    private void ToolStripButtonOpenNotion_Click(object sender, EventArgs e)
    {
        var target = textBoxParentPageId.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            NavigateToNotionHome();
            return;
        }

        NavigateToNotionPage(target);
    }

    private void ButtonTokenHelp_Click(object sender, EventArgs e)
    {
        const string helpText =
            "获取 Notion Integration Token 步骤：\r\n\r\n" +
            "1. 打开 https://www.notion.so/my-integrations\r\n" +
            "2. 点击 \"New integration\"，输入名称后创建\r\n" +
            "3. 复制 Internal Integration Secret（以 secret_ 开头）\r\n" +
            "4. 粘贴到本工具的 Token 输入框\r\n\r\n" +
            "⚠ 重要：还需要在 Notion 中给目标页面授权！\r\n" +
            "→ 打开目标页面 → 右上角 \"...\" → \"Connect to\" → 选择你创建的 Integration";

        using var dialog = new Form
        {
            Text = "如何获取 Notion Token",
            Size = new Size(520, 340),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var textBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Text = helpText,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window,
            Font = new Font("微软雅黑", 10F)
        };

        var panelButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 45,
            Padding = new Padding(8, 6, 8, 6)
        };

        var btnClose = new Button { Text = "关闭", Width = 80 };
        btnClose.Click += (_, _) => dialog.Close();

        var btnOpen = new Button { Text = "打开 Integrations 页面", Width = 160 };
        btnOpen.Click += (_, _) =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://www.notion.so/my-integrations",
                UseShellExecute = true
            });
        };

        panelButtons.Controls.Add(btnClose);
        panelButtons.Controls.Add(btnOpen);
        dialog.Controls.Add(textBox);
        dialog.Controls.Add(panelButtons);
        dialog.ShowDialog(this);
    }

    private async Task InitializeWebViewAsync()
    {
        DiagnosticLogger.Info("开始初始化 WebView2");

        try
        {
            await webViewNotion.EnsureCoreWebView2Async();
            _webViewReady = true;
            labelEmbeddedHint.Visible = false;
            webViewNotion.CoreWebView2.SourceChanged += WebViewNotion_SourceChanged;
            NavigateToNotionHome();
            DiagnosticLogger.Info("WebView2 初始化成功");
        }
        catch (Exception ex)
        {
            _webViewReady = false;
            labelEmbeddedHint.Visible = true;
            labelEmbeddedHint.Text = $"WebView2 初始化失败：{ex.Message}";
            DiagnosticLogger.Error("WebView2 初始化失败", ex);
        }
    }

    private void WebViewNotion_SourceChanged(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2SourceChangedEventArgs e)
    {
        var url = webViewNotion.Source?.AbsolutePath ?? string.Empty;
        var pageId = ExtractNotionPageId(url);
        if (!string.IsNullOrEmpty(pageId))
        {
            textBoxParentPageId.Text = pageId;
            DiagnosticLogger.Info($"从 WebView2 URL 自动提取 PageId={pageId}");
        }
    }

    private static string? ExtractNotionPageId(string urlPath)
    {
        // Notion URL: /workspace/Page-Title-<32hex> or /<32hex>
        var match = NotionPageIdRegex.Match(urlPath);
        return match.Success ? match.Value : null;
    }

    private async Task LoadHierarchyAsync()
    {
        toolStripStatusLabel.Text = "正在加载 OneNote 层级...";
        DiagnosticLogger.Info("LoadHierarchyAsync 开始");

        treeViewOneNote.Nodes.Clear();
        var notebooks = await _hierarchyProvider.GetNotebookHierarchyAsync(CancellationToken.None);
        DiagnosticLogger.Info($"OneNote 层级拉取完成，notebookCount={notebooks.Count}");

        foreach (var notebook in notebooks)
        {
            treeViewOneNote.Nodes.Add(ToTreeNode(notebook));
        }

        treeViewOneNote.Enabled = true;
        toolStripReload.Enabled = true;
        toolStripSync.Enabled = true;
        toolStripStatusLabel.Text = $"已加载 {notebooks.Count} 个笔记本";
        DiagnosticLogger.Info("LoadHierarchyAsync 完成");
    }

    private bool TryGetAuthInputs(out string token, out string parentPageId)
    {
        token = textBoxNotionToken.Text.Trim();
        parentPageId = textBoxParentPageId.Text.Trim();

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(parentPageId))
        {
            MessageBox.Show("请填写 Notion Integration Token 和目标父页面 ID。");
            return false;
        }

        return true;
    }

    private bool TryGetTokenOnly(out string token)
    {
        token = textBoxNotionToken.Text.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            MessageBox.Show("请先填写 Notion Integration Token。");
            return false;
        }

        return true;
    }

    private List<string> GetCheckedMappedNotionPageIds()
    {
        var selectedNodes = GetCheckedNodes(treeViewOneNote.Nodes);
        var ids = selectedNodes
            .Select(node => _lastSyncedPageMap.TryGetValue(node.Id, out var notionPageId) ? notionPageId : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return ids;
    }

    /// <summary>
    /// Collect Notion page IDs for the top-level nodes only.
    /// Archiving a parent page in Notion automatically hides its children,
    /// so we only need to delete the root-level mapped pages.
    /// </summary>
    private List<string> CollectMappedNotionPageIds(List<OneNoteTreeNode> nodes)
    {
        return nodes
            .Select(n => _lastSyncedPageMap.TryGetValue(n.Id, out var pid) ? pid : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Remove all mapping entries for a set of nodes and all their descendants.
    /// </summary>
    private void RemoveMappings(List<OneNoteTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            RemoveNodeMappings(node);
        }
    }

    private void RemoveNodeMappings(OneNoteTreeNode node)
    {
        _lastSyncedPageMap.Remove(node.Id);
        foreach (var child in node.Children)
        {
            RemoveNodeMappings(child);
        }
    }

    private void NavigateToNotionHome()
    {
        if (!_webViewReady)
        {
            return;
        }

        webViewNotion.Source = new Uri("https://www.notion.so/");
    }

    private void NavigateToNotionPage(string rawId)
    {
        if (!_webViewReady)
        {
            return;
        }

        var normalized = rawId.Replace("-", string.Empty).Trim();
        if (normalized.Length < 32)
        {
            NavigateToNotionHome();
            return;
        }

        webViewNotion.Source = new Uri($"https://www.notion.so/{normalized}");
    }

    private void SetToolbarEnabled(bool enabled)
    {
        toolStripReload.Enabled = enabled;
        toolStripSync.Enabled = enabled;
        toolStripBulkArchive.Enabled = enabled;
        toolStripBulkMove.Enabled = enabled;
        toolStripOpenNotion.Enabled = enabled;
    }

    private void HandleOneNoteInitializationFailure(Exception ex)
    {
        DisableOneNoteActions();

        DiagnosticLogger.Error("OneNote 初始化失败", ex);
        var message = BuildOneNoteFriendlyMessage(ex);
        toolStripStatusLabel.Text = "OneNote 不可用";
        MessageBox.Show(
            message + $"\n\n日志文件：{DiagnosticLogger.LogFilePath}",
            "OneNote 初始化失败",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void DisableOneNoteActions()
    {
        toolStripReload.Enabled = true;
        toolStripSync.Enabled = false;
        treeViewOneNote.Enabled = false;

        DiagnosticLogger.Warn("OneNote 操作已禁用，仅保留刷新按钮");
    }

    private static string BuildOneNoteFriendlyMessage(Exception ex)
    {
        var root = ex.GetBaseException();
        var hresult = root.HResult;

        if (hresult == unchecked((int)0x8002801D) || hresult == unchecked((int)0x80040154))
        {
            var bitnessTip = Environment.Is64BitProcess
                ? "当前进程为 x64，建议切换为 x86 运行。"
                : "当前进程为 x86。";

            return "检测到 OneNote COM 类型库未注册。\n\n" +
                   "请先安装或修复 OneNote 桌面版（Office 365 / OneNote 2016），\n" +
                   "然后重启本工具。\n\n" +
                   bitnessTip + "\n" +
                   "建议操作：控制面板 -> 程序和功能 -> Microsoft Office -> 更改 -> 快速修复。";
        }

        if (hresult == unchecked((int)0x80004005))
        {
            return "OneNote 已启动，但暂时无法返回层级（E_FAIL）。\n\n" +
                   "请先手动打开 OneNote 桌面版并确认能正常看到笔记本/分区，\n" +
                   "再回到本工具点击顶部“重新加载”。";
        }

        return $"加载 OneNote 层级失败：{root.Message}";
    }

    private static TreeNode ToTreeNode(OneNoteTreeNode source)
    {
        var treeNode = new TreeNode(source.Name) { Tag = source };
        foreach (var child in source.Children)
        {
            treeNode.Nodes.Add(ToTreeNode(child));
        }

        return treeNode;
    }

    private void TreeViewOneNote_AfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (e.Action == TreeViewAction.Unknown)
        {
            return;
        }

        SetChildNodesChecked(e.Node!, e.Node!.Checked);
    }

    private static void SetChildNodesChecked(TreeNode parent, bool isChecked)
    {
        foreach (TreeNode child in parent.Nodes)
        {
            child.Checked = isChecked;
            SetChildNodesChecked(child, isChecked);
        }
    }

    private static List<OneNoteTreeNode> GetCheckedNodes(TreeNodeCollection nodes)
    {
        var checkedNodes = new List<OneNoteTreeNode>();
        foreach (TreeNode treeNode in nodes)
        {
            if (treeNode.Checked && treeNode.Tag is OneNoteTreeNode node)
            {
                // Only add this node if it's a top-level checked node
                // (i.e., its parent is not checked)
                // This prevents duplicate syncing since SyncAsync recursively syncs children
                checkedNodes.Add(node);
            }
            else if (!treeNode.Checked)
            {
                // If this node is not checked, recurse into children
                // to find any checked descendants
                checkedNodes.AddRange(GetCheckedNodes(treeNode.Nodes));
            }
            // If treeNode.Checked is true, we don't recurse because
            // SyncAsync will handle all descendants
        }

        return checkedNodes;
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigFilePath)) return;

            var json = File.ReadAllText(ConfigFilePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("notionToken", out var tokenEl))
                textBoxNotionToken.Text = tokenEl.GetString() ?? string.Empty;
            if (root.TryGetProperty("parentPageId", out var pageEl))
                textBoxParentPageId.Text = pageEl.GetString() ?? string.Empty;
            if (root.TryGetProperty("moveParentId", out var moveEl))
                textBoxMoveParentId.Text = moveEl.GetString() ?? string.Empty;
            if (root.TryGetProperty("retryCount", out var retryEl) && retryEl.TryGetInt32(out var retryVal))
                numericRetryCount.Value = Math.Clamp(retryVal, (int)numericRetryCount.Minimum, (int)numericRetryCount.Maximum);

            DiagnosticLogger.Info("已加载本地配置");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn($"加载配置失败: {ex.Message}");
        }
    }

    private void SaveConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigFilePath)!;
            Directory.CreateDirectory(dir);

            var config = new Dictionary<string, object>
            {
                ["notionToken"] = textBoxNotionToken.Text.Trim(),
                ["parentPageId"] = textBoxParentPageId.Text.Trim(),
                ["moveParentId"] = textBoxMoveParentId.Text.Trim(),
                ["retryCount"] = (int)numericRetryCount.Value
            };

            File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            DiagnosticLogger.Info("已保存本地配置");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn($"保存配置失败: {ex.Message}");
        }
    }

    private void ShowDryRunLog(SyncResult result, string summary)
    {
        var logText = string.Join("\r\n", result.LogEntries)
                      + "\r\n\r\n---\r\n"
                      + $"预演{summary}";

        using var dialog = new Form
        {
            Text = "Dry Run 预演结果",
            Size = new Size(620, 450),
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var textBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Text = logText,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = SystemColors.Window,
            Font = new Font("微软雅黑", 10F)
        };

        var btnClose = new Button
        {
            Text = "关闭",
            Dock = DockStyle.Bottom,
            Height = 35
        };
        btnClose.Click += (_, _) => dialog.Close();

        dialog.Controls.Add(textBox);
        dialog.Controls.Add(btnClose);
        dialog.ShowDialog(this);
    }

    private void ShowFailedPagesDialog(List<FailedSyncItem> failedPages, SyncOptions options)
    {
        using var dialog = new Form
        {
            Text = $"同步失败列表（{failedPages.Count} 项）",
            Size = new Size(700, 480),
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            CheckBoxes = true,
            Font = new Font("微软雅黑", 9.5F)
        };

        listView.Columns.Add("节点名称", 200);
        listView.Columns.Add("类型", 80);
        listView.Columns.Add("错误信息", 380);

        foreach (var item in failedPages)
        {
            var nodeTypeLabel = item.Node.NodeType switch
            {
                OneNoteNodeType.Notebook => "笔记本",
                OneNoteNodeType.SectionGroup => "分区组",
                OneNoteNodeType.Section => "分区",
                OneNoteNodeType.Page => "页面",
                _ => item.Node.NodeType.ToString()
            };

            var lvi = new ListViewItem(new[] { item.Node.Name, nodeTypeLabel, item.ErrorMessage })
            {
                Tag = item,
                Checked = true
            };
            listView.Items.Add(lvi);
        }

        var panelButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 45,
            Padding = new Padding(8, 6, 8, 6)
        };

        var btnClose = new Button { Text = "关闭", Width = 80 };
        btnClose.Click += (_, _) => dialog.Close();

        var btnRetry = new Button { Text = "重试选中项", Width = 110 };
        btnRetry.Click += (_, _) =>
        {
            var retryItems = new List<FailedSyncItem>();
            foreach (ListViewItem lvi in listView.CheckedItems)
            {
                if (lvi.Tag is FailedSyncItem failed)
                {
                    retryItems.Add(failed);
                }
            }

            if (retryItems.Count == 0)
            {
                MessageBox.Show("请勾选需要重试的项目。");
                return;
            }

            dialog.Close();
            // Fire-and-forget on the UI thread since this is from a dialog callback
            _ = RunRetrySyncAsync(retryItems, options);
        };

        var btnSelectAll = new Button { Text = "全选", Width = 60 };
        btnSelectAll.Click += (_, _) =>
        {
            foreach (ListViewItem lvi in listView.Items)
                lvi.Checked = true;
        };

        var btnDeselectAll = new Button { Text = "全不选", Width = 60 };
        btnDeselectAll.Click += (_, _) =>
        {
            foreach (ListViewItem lvi in listView.Items)
                lvi.Checked = false;
        };

        panelButtons.Controls.Add(btnClose);
        panelButtons.Controls.Add(btnRetry);
        panelButtons.Controls.Add(btnDeselectAll);
        panelButtons.Controls.Add(btnSelectAll);
        dialog.Controls.Add(listView);
        dialog.Controls.Add(panelButtons);
        dialog.ShowDialog(this);
    }

    private static bool TryEnsureOneNoteNotRunning()
    {
        var processes = Process.GetProcessesByName("ONENOTE");
        if (processes.Length == 0)
        {
            return true;
        }

        var result = MessageBox.Show(
            "检测到 OneNote 正在运行，COM 调用可能冲突。\n\n是否关闭 OneNote 后继续？",
            "OneNote 进程冲突",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result != DialogResult.Yes)
        {
            return false;
        }

        KillOneNoteProcess();
        return true;
    }

    private static void KillOneNoteProcess()
    {
        foreach (var proc in Process.GetProcessesByName("ONENOTE"))
        {
            try
            {
                // Try graceful close first (WM_CLOSE) to avoid "failed to start" recovery dialog
                if (proc.CloseMainWindow())
                {
                    if (proc.WaitForExit(8000))
                    {
                        DiagnosticLogger.Info($"已正常关闭 OneNote 进程 PID={proc.Id}");
                        continue;
                    }
                }

                // Graceful close failed or timed out, force kill
                proc.Kill();
                proc.WaitForExit(3000);
                DiagnosticLogger.Info($"已强制终止 OneNote 进程 PID={proc.Id}");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Warn($"终止 OneNote 进程失败: {ex.Message}");
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveConfig();
        base.OnFormClosing(e);
    }
}
