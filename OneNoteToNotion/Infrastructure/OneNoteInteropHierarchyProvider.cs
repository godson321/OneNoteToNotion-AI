using System;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using OneNoteToNotion.Domain;
using OneNoteToNotion.Parsing;
using OneNote = Microsoft.Office.Interop.OneNote;

namespace OneNoteToNotion.Infrastructure;

public sealed class OneNoteInteropHierarchyProvider : IOneNoteHierarchyProvider, IOneNotePageContentProvider
{
    private static readonly XNamespace OneNs = "http://schemas.microsoft.com/office/onenote/2013/onenote";

    private const int RetryableEFail = unchecked((int)0x80004005);
    private const int RetryableServerExecFailure = unchecked((int)0x80080005);
    private const int RetryableServerBusy = unchecked((int)0x8001010A);
    private const int RetryableCallRejected = unchecked((int)0x80010001);
    private const int RetryableRpcServerUnavailable = unchecked((int)0x800706BA);
    private const int RetryableRpcCallFailed = unchecked((int)0x800706BE);

    // OneNote COM is STA — must serialize all access when sync runs concurrently
    private readonly SemaphoreSlim _comLock = new(1, 1);
    private OneNote.Application? _oneNoteApp;

    public OneNoteInteropHierarchyProvider()
    {
        DiagnosticLogger.Info("OneNoteInteropHierarchyProvider 已创建（延迟初始化 COM，强类型调用）");
    }

    private OneNote.Application GetOneNoteApp()
    {
        if (_oneNoteApp is null)
        {
            DiagnosticLogger.Info("开始初始化 OneNote COM 实例（Microsoft.Office.Interop.OneNote.Application）");
            _oneNoteApp = new OneNote.Application();
            DiagnosticLogger.Info("OneNote COM 实例初始化成功");
        }

        return _oneNoteApp;
    }

    private void ResetOneNoteApp()
    {
        if (_oneNoteApp is null)
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(_oneNoteApp);
            DiagnosticLogger.Info("已释放 OneNote COM 实例");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn($"释放 OneNote COM 实例时忽略异常: {ex.Message}");
        }

        _oneNoteApp = null;
    }

    public async Task<IReadOnlyList<OneNoteTreeNode>> GetNotebookHierarchyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiagnosticLogger.Info("GetNotebookHierarchyAsync 开始");

        var xmlHierarchy = await GetHierarchyXmlWithRetryAsync(cancellationToken);
        DiagnosticLogger.Info($"GetHierarchy 返回 XML 长度={xmlHierarchy.Length}");

        // Save hierarchy XML for debugging
        try
        {
            var dumpDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics");
            Directory.CreateDirectory(dumpDir);
            var timestamp = DateTime.Now.ToString("HHmmss");
            var xmlPath = Path.Combine(dumpDir, $"hierarchy_{timestamp}.xml");
            File.WriteAllText(xmlPath, xmlHierarchy, System.Text.Encoding.UTF8);
            DiagnosticLogger.Info($"层级 XML 已保存: {xmlPath}");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn($"保存层级 XML 失败: {ex.Message}");
        }

        var document = XDocument.Parse(xmlHierarchy);
        var notebooks = document
            .Descendants(OneNs + "Notebook")
            .Select(ParseNotebook)
            .ToList();

        DiagnosticLogger.Info($"GetNotebookHierarchyAsync 完成, notebookCount={notebooks.Count}");
        return notebooks;
    }

    public async Task<string> GetPageContentXmlAsync(string pageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiagnosticLogger.Info($"GetPageContentXmlAsync 开始, pageId={pageId}");

        // Serialize COM access — OneNote COM is STA and not thread-safe
        await _comLock.WaitAsync(cancellationToken);
        try
        {
            return await GetPageContentCoreAsync(pageId, cancellationToken);
        }
        finally
        {
            _comLock.Release();
        }
    }

    private async Task<string> GetPageContentCoreAsync(string pageId, CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                GetOneNoteApp().GetPageContent(pageId, out string pageXml, OneNote.PageInfo.piAll);
                var normalized = pageXml ?? string.Empty;
                DiagnosticLogger.Info($"GetPageContentXmlAsync 成功, pageId={pageId}, xmlLength={normalized.Length}");
                return normalized;
            }
            catch (COMException ex) when (IsRetryableComError(ex))
            {
                lastException = ex;
                DiagnosticLogger.Warn($"GetPageContent 尝试 #{attempt} COM 异常, pageId={pageId}, hresult=0x{ex.HResult:X8}");
                ResetOneNoteApp();

                if (attempt < 4)
                {
                    await Task.Delay(700, cancellationToken);
                }
            }
            catch (COMException ex)
            {
                DiagnosticLogger.Error($"GetPageContent COM 异常, pageId={pageId}, hresult=0x{ex.HResult:X8}", ex);
                throw;
            }
        }

        DiagnosticLogger.Error($"GetPageContent 多次重试后失败, pageId={pageId}", lastException!);
        throw new InvalidOperationException(
            $"调用 OneNote.GetPageContent 失败（pageId={pageId}）。请先打开并登录 OneNote 桌面版，然后重试。",
            lastException);
    }

    private async Task<string> GetHierarchyXmlWithRetryAsync(CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiagnosticLogger.Info($"GetHierarchy 尝试 #{attempt}");

            try
            {
                if (TryGetHierarchyXml(null, out var xmlHierarchy))
                {
                    DiagnosticLogger.Info($"GetHierarchy 尝试 #{attempt} 成功, xmlLength={xmlHierarchy.Length}");
                    return xmlHierarchy;
                }

                lastException = new InvalidOperationException("OneNote 返回了空层级结果。");
                DiagnosticLogger.Warn($"GetHierarchy 尝试 #{attempt} 返回空结果");
            }
            catch (COMException ex) when (IsRetryableComError(ex))
            {
                lastException = ex;
                DiagnosticLogger.Warn($"GetHierarchy 尝试 #{attempt} COM 异常, hresult=0x{ex.HResult:X8}");

                // 失败后重建 COM 会话，尽量贴近老项目“释放后重连”的行为。
                ResetOneNoteApp();

                if (attempt == 1)
                {
                    TryLaunchOneNoteDesktop();
                }
            }

            if (attempt < 6)
            {
                await Task.Delay(700, cancellationToken);
            }
        }

        DiagnosticLogger.Error("GetHierarchy 多次重试后失败", lastException ?? new InvalidOperationException("未知异常"));
        throw new InvalidOperationException(
            "调用 OneNote.GetHierarchy 失败。请先打开并登录 OneNote 桌面版，然后点击“重新加载”。",
            lastException);
    }

    private bool TryGetHierarchyXml(string? startNodeId, out string xmlHierarchy)
    {
        xmlHierarchy = string.Empty;
        var startNodeForLog = startNodeId ?? "<null>";

        try
        {
            DiagnosticLogger.Info($"调用 GetHierarchy(startNodeId={startNodeForLog}, scope=hsPages)");
            GetOneNoteApp().GetHierarchy(startNodeId, OneNote.HierarchyScope.hsPages, out xmlHierarchy);
        }
        catch (COMException ex) when (startNodeId is null)
        {
            DiagnosticLogger.Warn($"GetHierarchy 使用 null startNode 失败, hresult=0x{ex.HResult:X8}，尝试 empty string");
            GetOneNoteApp().GetHierarchy(string.Empty, OneNote.HierarchyScope.hsPages, out xmlHierarchy);
        }

        DiagnosticLogger.Info($"GetHierarchy 返回 xmlLength={xmlHierarchy.Length}");
        return !string.IsNullOrWhiteSpace(xmlHierarchy);
    }

    private static bool IsRetryableComError(COMException ex)
    {
        return ex.HResult == RetryableEFail ||
               ex.HResult == RetryableServerExecFailure ||
               ex.HResult == RetryableServerBusy ||
               ex.HResult == RetryableCallRejected ||
               ex.HResult == RetryableRpcServerUnavailable ||
               ex.HResult == RetryableRpcCallFailed;
    }

    private static void TryLaunchOneNoteDesktop()
    {
        try
        {
            if (System.Diagnostics.Process.GetProcessesByName("ONENOTE").Length > 0 ||
                System.Diagnostics.Process.GetProcessesByName("OneNote").Length > 0)
            {
                DiagnosticLogger.Info("TryLaunchOneNoteDesktop: 检测到 OneNote 已在运行");
                return;
            }

            DiagnosticLogger.Info("TryLaunchOneNoteDesktop: 尝试通过 onenote: 协议启动桌面 OneNote");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "onenote:",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn($"TryLaunchOneNoteDesktop 失败: {ex.Message}");
        }
    }

    private OneNoteTreeNode ParseNotebook(XElement notebookElement)
    {
        var notebookNode = CreateNode(notebookElement, OneNoteNodeType.Notebook);
        foreach (var child in ParseChildren(notebookElement))
        {
            notebookNode.Children.Add(child);
        }

        return notebookNode;
    }

    private IEnumerable<OneNoteTreeNode> ParseChildren(XElement parent)
    {
        foreach (var sectionGroup in parent.Elements(OneNs + "SectionGroup"))
        {
            var node = CreateNode(sectionGroup, OneNoteNodeType.SectionGroup);
            foreach (var child in ParseChildren(sectionGroup))
            {
                node.Children.Add(child);
            }

            yield return node;
        }

        foreach (var section in parent.Elements(OneNs + "Section"))
        {
            var sectionNode = CreateNode(section, OneNoteNodeType.Section);
            
            // Parse pages and build hierarchy based on pageLevel attribute
            var pages = section.Elements(OneNs + "Page").ToList();
            var topLevelPages = BuildPageHierarchy(pages);
            foreach (var page in topLevelPages)
            {
                sectionNode.Children.Add(page);
            }

            yield return sectionNode;
        }
    }

    /// <summary>
    /// Build page hierarchy based on pageLevel attribute.
    /// In OneNote XML, all pages are siblings, but use pageLevel to indicate parent-child relationships.
    /// pageLevel="1" = top-level page
    /// pageLevel="2" = subpage of the previous pageLevel="1" page
    /// pageLevel="3" = sub-subpage, etc.
    /// </summary>
    private List<OneNoteTreeNode> BuildPageHierarchy(List<XElement> pageElements)
    {
        var result = new List<OneNoteTreeNode>();
        var stack = new Stack<OneNoteTreeNode>();

        foreach (var pageElement in pageElements)
        {
            var pageNode = CreateNode(pageElement, OneNoteNodeType.Page);
            var pageLevelStr = pageElement.Attribute("pageLevel")?.Value;
            var pageLevel = int.TryParse(pageLevelStr, out var level) ? level : 1;

            // Pop stack until we find the parent level
            while (stack.Count > 0 && stack.Count >= pageLevel)
            {
                stack.Pop();
            }

            if (stack.Count == 0)
            {
                // Top-level page
                result.Add(pageNode);
            }
            else
            {
                // Add as child of the current parent
                stack.Peek().Children.Add(pageNode);
            }

            // Push current page to stack for potential children
            stack.Push(pageNode);
        }

        return result;
    }

    private static OneNoteTreeNode CreateNode(XElement element, OneNoteNodeType nodeType)
    {
        return new OneNoteTreeNode
        {
            Id = element.Attribute("ID")?.Value ?? string.Empty,
            Name = element.Attribute("name")?.Value ?? "Unnamed",
            NodeType = nodeType,
            Path = element.Attribute("path")?.Value
        };
    }
}
