using OneNoteToNotion.Application;
using OneNoteToNotion.Domain;
using OneNoteToNotion.Infrastructure;
using OneNoteToNotion.Mapping;
using OneNoteToNotion.Notion;
using OneNoteToNotion.Parsing;

const string targetPageTitle = "同时连接Midea和VPN情况下，让指定IP走Midea网络(指定IP走指定网卡)";

var configPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "OneNoteToNotion",
    "config.json");

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"配置文件不存在: {configPath}");
    return 1;
}

var (token, parentPageId) = LoadAuthConfig(configPath);
if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(parentPageId))
{
    Console.Error.WriteLine("配置文件缺少 notionToken 或 parentPageId。");
    return 1;
}

IOneNoteHierarchyProvider hierarchyProvider = new OneNoteInteropHierarchyProvider();
IOneNotePageContentProvider pageContentProvider = (IOneNotePageContentProvider)hierarchyProvider;
ISemanticDocumentParser semanticParser = new OneNoteXmlSemanticParser();
INotionBlockMapper blockMapper = new NotionBlockMapper();
var handler = new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
    MaxConnectionsPerServer = 5
};
var notionApiClient = new NotionApiClient(new HttpClient(handler));

var orchestrator = new NotionSyncOrchestrator(
    hierarchyProvider,
    pageContentProvider,
    semanticParser,
    blockMapper,
    notionApiClient);

var cancellationToken = CancellationToken.None;
var roots = await hierarchyProvider.GetNotebookHierarchyAsync(cancellationToken);
var targetNode = FindPageByTitle(roots, targetPageTitle);
if (targetNode is null)
{
    Console.Error.WriteLine($"未找到目标页面: {targetPageTitle}");
    return 1;
}

var syncOptions = new SyncOptions(token, parentPageId, DryRun: false);
var syncResult = await orchestrator.SyncAsync(targetNode, syncOptions, progress: null, cancellationToken);

Console.WriteLine($"CreatedPages={syncResult.CreatedPages}, SyncedContentPages={syncResult.SyncedContentPages}, Failed={syncResult.FailedPages.Count}");
if (syncResult.FailedPages.Count > 0)
{
    foreach (var failed in syncResult.FailedPages)
    {
        Console.WriteLine($"FAILED: {failed.Node.Name} -> {failed.ErrorMessage}");
    }
}

return 0;

static (string token, string parentPageId) LoadAuthConfig(string configPath)
{
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
    var root = doc.RootElement;

    var token = root.TryGetProperty("notionToken", out var tokenEl)
        ? tokenEl.GetString() ?? string.Empty
        : string.Empty;
    var parentPageId = root.TryGetProperty("parentPageId", out var pageEl)
        ? pageEl.GetString() ?? string.Empty
        : string.Empty;

    return (token, parentPageId);
}

static OneNoteTreeNode? FindPageByTitle(IEnumerable<OneNoteTreeNode> roots, string targetTitle)
{
    foreach (var root in roots)
    {
        var found = FindInNode(root, targetTitle);
        if (found is not null)
        {
            return found;
        }
    }

    return null;
}

static OneNoteTreeNode? FindInNode(OneNoteTreeNode node, string targetTitle)
{
    if (node.NodeType == OneNoteNodeType.Page &&
        string.Equals(node.Name, targetTitle, StringComparison.OrdinalIgnoreCase))
    {
        return node;
    }

    foreach (var child in node.Children)
    {
        var found = FindInNode(child, targetTitle);
        if (found is not null)
        {
            return found;
        }
    }

    return null;
}
