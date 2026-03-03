using System.Text.Json;
using OneNoteToNotion.Application;
using OneNoteToNotion.Domain;
using OneNoteToNotion.Infrastructure;

namespace OneNoteToNotion;

public partial class Form1
{
    private void LoadSyncHistory()
    {
        try
        {
            if (!File.Exists(SyncHistoryFilePath))
            {
                return;
            }

            var json = File.ReadAllText(SyncHistoryFilePath);
            var records = JsonSerializer.Deserialize<List<SyncHistoryRecord>>(json) ?? new List<SyncHistoryRecord>();
            _syncHistory.Clear();
            _syncHistory.AddRange(records
                .OrderByDescending(r => r.StartedAt)
                .Take(MaxSyncHistoryRecords));
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn($"加载同步历史失败: {ex.Message}");
        }
    }

    private void SaveSyncHistory()
    {
        try
        {
            var dir = Path.GetDirectoryName(SyncHistoryFilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_syncHistory, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SyncHistoryFilePath, json);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn($"保存同步历史失败: {ex.Message}");
        }
    }

    private void RecordSyncHistory(
        string operation,
        string status,
        SyncOptions? options,
        DateTime startedAt,
        DateTime finishedAt,
        string summary,
        SyncResult? result,
        string? topLevelError = null)
    {
        var failedItems = result?.FailedPages
            .Select(f => new SyncHistoryFailedItem
            {
                NodeId = f.Node.Id,
                NodeName = f.Node.Name,
                NodeType = f.Node.NodeType.ToString(),
                ParentPageId = f.ParentPageId,
                ErrorMessage = f.ErrorMessage
            })
            .ToList() ?? new List<SyncHistoryFailedItem>();

        if (!string.IsNullOrWhiteSpace(topLevelError) && failedItems.Count == 0)
        {
            failedItems.Add(new SyncHistoryFailedItem
            {
                NodeId = string.Empty,
                NodeName = "(Global)",
                NodeType = "Global",
                ParentPageId = options?.ParentPageId ?? string.Empty,
                ErrorMessage = topLevelError
            });
        }

        var record = new SyncHistoryRecord
        {
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            Operation = operation,
            Status = status,
            DryRun = options?.DryRun ?? false,
            CreatedPages = result?.CreatedPages ?? 0,
            SyncedContentPages = result?.SyncedContentPages ?? 0,
            FailedCount = failedItems.Count,
            Summary = summary,
            TopLevelError = topLevelError,
            FailedItems = failedItems,
            LogEntries = result?.LogEntries.TakeLast(500).ToList() ?? new List<string>()
        };

        _syncHistory.Insert(0, record);
        if (_syncHistory.Count > MaxSyncHistoryRecords)
        {
            _syncHistory.RemoveRange(MaxSyncHistoryRecords, _syncHistory.Count - MaxSyncHistoryRecords);
        }

        SaveSyncHistory();
    }

    private void ShowSyncHistoryDialog()
    {
        if (_syncHistory.Count == 0)
        {
            MessageBox.Show("暂无同步历史记录。");
            return;
        }

        using var dialog = new Form
        {
            Text = "同步历史记录",
            Size = new Size(980, 620),
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 230
        };

        var historyView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            Font = new Font("Microsoft YaHei", 9F)
        };
        historyView.Columns.Add("时间", 150);
        historyView.Columns.Add("操作", 70);
        historyView.Columns.Add("状态", 80);
        historyView.Columns.Add("DryRun", 70);
        historyView.Columns.Add("新建", 70);
        historyView.Columns.Add("内容页", 80);
        historyView.Columns.Add("失败", 70);
        historyView.Columns.Add("耗时(s)", 80);
        historyView.Columns.Add("摘要", 330);

        foreach (var record in _syncHistory)
        {
            var duration = (record.FinishedAt - record.StartedAt).TotalSeconds;
            var lvi = new ListViewItem(new[]
            {
                record.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                ToOperationLabel(record.Operation),
                ToStatusLabel(record.Status),
                record.DryRun ? "是" : "否",
                record.CreatedPages.ToString(),
                record.SyncedContentPages.ToString(),
                record.FailedCount.ToString(),
                duration.ToString("F1"),
                record.Summary
            })
            {
                Tag = record
            };
            historyView.Items.Add(lvi);
        }

        var failedView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            CheckBoxes = true,
            Font = new Font("Microsoft YaHei", 9F)
        };
        failedView.Columns.Add("节点名称", 180);
        failedView.Columns.Add("类型", 90);
        failedView.Columns.Add("ParentPageId", 260);
        failedView.Columns.Add("错误信息", 420);

        void RefreshFailedItems()
        {
            failedView.Items.Clear();
            if (historyView.SelectedItems.Count == 0)
            {
                return;
            }

            if (historyView.SelectedItems[0].Tag is not SyncHistoryRecord selected)
            {
                return;
            }

            foreach (var failed in selected.FailedItems)
            {
                var lvi = new ListViewItem(new[]
                {
                    failed.NodeName,
                    failed.NodeType,
                    failed.ParentPageId,
                    failed.ErrorMessage
                })
                {
                    Tag = failed,
                    Checked = true
                };
                failedView.Items.Add(lvi);
            }
        }

        historyView.SelectedIndexChanged += (_, _) => RefreshFailedItems();

        var panelButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(8, 6, 8, 6)
        };

        var btnClose = new Button { Text = "关闭", Width = 90 };
        btnClose.Click += (_, _) => dialog.Close();

        var btnViewLog = new Button { Text = "查看日志", Width = 90 };
        btnViewLog.Click += (_, _) =>
        {
            if (historyView.SelectedItems.Count == 0 || historyView.SelectedItems[0].Tag is not SyncHistoryRecord selected)
            {
                MessageBox.Show("请先选择一条历史记录。");
                return;
            }

            var logText = selected.LogEntries.Count == 0
                ? "(无日志)"
                : string.Join(Environment.NewLine, selected.LogEntries);

            using var logDialog = new Form
            {
                Text = $"运行日志 - {selected.StartedAt:yyyy-MM-dd HH:mm:ss}",
                Size = new Size(900, 600),
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
                Font = new Font("Consolas", 9F)
            };

            var closeButton = new Button
            {
                Text = "关闭",
                Dock = DockStyle.Bottom,
                Height = 35
            };
            closeButton.Click += (_, _) => logDialog.Close();

            logDialog.Controls.Add(textBox);
            logDialog.Controls.Add(closeButton);
            logDialog.ShowDialog(dialog);
        };

        var btnRetry = new Button { Text = "重试选中错误", Width = 120 };
        btnRetry.Click += (_, _) =>
        {
            if (historyView.SelectedItems.Count == 0)
            {
                MessageBox.Show("请先选择一条历史记录。");
                return;
            }

            if (!TryGetAuthInputs(out var token, out var parentPageId))
            {
                return;
            }

            var selectedFailures = new List<SyncHistoryFailedItem>();
            foreach (ListViewItem lvi in failedView.CheckedItems)
            {
                if (lvi.Tag is SyncHistoryFailedItem failed)
                {
                    selectedFailures.Add(failed);
                }
            }

            if (selectedFailures.Count == 0)
            {
                MessageBox.Show("请勾选需要重试的错误项。");
                return;
            }

            var nodeIndex = BuildNodeIndexFromTreeView();
            var retryItems = new List<FailedSyncItem>();
            var unresolved = new List<string>();

            foreach (var failed in selectedFailures)
            {
                if (string.IsNullOrWhiteSpace(failed.NodeId))
                {
                    unresolved.Add($"{failed.NodeName}: 节点ID为空");
                    continue;
                }

                if (!nodeIndex.TryGetValue(failed.NodeId, out var node))
                {
                    unresolved.Add($"{failed.NodeName}: 节点已不存在或尚未加载");
                    continue;
                }

                retryItems.Add(new FailedSyncItem(
                    node,
                    string.IsNullOrWhiteSpace(failed.ParentPageId) ? parentPageId : failed.ParentPageId,
                    failed.ErrorMessage));
            }

            if (retryItems.Count == 0)
            {
                MessageBox.Show("没有可重试的错误项。请先刷新 OneNote 层级后再试。");
                return;
            }

            if (unresolved.Count > 0)
            {
                var brief = string.Join(Environment.NewLine, unresolved.Take(5));
                var more = unresolved.Count > 5 ? $"{Environment.NewLine}...共 {unresolved.Count} 条未解析" : string.Empty;
                MessageBox.Show($"以下错误项无法重试：{Environment.NewLine}{brief}{more}");
            }

            var options = new SyncOptions(token, parentPageId, checkBoxDryRun.Checked);
            dialog.Close();
            _ = RunRetrySyncAsync(retryItems, options);
        };

        panelButtons.Controls.Add(btnClose);
        panelButtons.Controls.Add(btnRetry);
        panelButtons.Controls.Add(btnViewLog);

        split.Panel1.Controls.Add(historyView);
        split.Panel2.Controls.Add(failedView);
        dialog.Controls.Add(split);
        dialog.Controls.Add(panelButtons);

        if (historyView.Items.Count > 0)
        {
            historyView.Items[0].Selected = true;
            historyView.Select();
            RefreshFailedItems();
        }

        dialog.ShowDialog(this);
    }

    private Dictionary<string, OneNoteTreeNode> BuildNodeIndexFromTreeView()
    {
        var index = new Dictionary<string, OneNoteTreeNode>(StringComparer.Ordinal);
        foreach (TreeNode root in treeViewOneNote.Nodes)
        {
            if (root.Tag is OneNoteTreeNode node)
            {
                IndexOneNoteNode(node, index);
            }
        }

        return index;
    }

    private static void IndexOneNoteNode(OneNoteTreeNode node, Dictionary<string, OneNoteTreeNode> index)
    {
        index[node.Id] = node;
        foreach (var child in node.Children)
        {
            IndexOneNoteNode(child, index);
        }
    }

    private static string ToOperationLabel(string operation)
    {
        return operation switch
        {
            "retry" => "重试",
            "sync" => "同步",
            _ => operation
        };
    }

    private static string ToStatusLabel(string status)
    {
        return status switch
        {
            "success" => "成功",
            "partial" => "部分成功",
            "failed" => "失败",
            "canceled" => "取消",
            _ => status
        };
    }
}
