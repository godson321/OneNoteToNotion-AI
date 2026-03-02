using OneNoteToNotion.Application;
using OneNoteToNotion.Infrastructure;
using OneNoteToNotion.Mapping;
using OneNoteToNotion.Notion;
using OneNoteToNotion.Parsing;

namespace OneNoteToNotion;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        DiagnosticLogger.Info("程序启动");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                DiagnosticLogger.Error("AppDomain 未处理异常", ex);
            }
            else
            {
                DiagnosticLogger.Error($"AppDomain 未处理异常: {args.ExceptionObject}");
            }
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagnosticLogger.Error("Task 未观察异常", args.Exception);
        };

        System.Windows.Forms.Application.ThreadException += (_, args) =>
        {
            DiagnosticLogger.Error("UI 线程异常", args.Exception);
        };

        ApplicationConfiguration.Initialize();
        DiagnosticLogger.Info($"ApplicationConfiguration 已初始化，日志文件: {DiagnosticLogger.LogFilePath}");

        IOneNoteHierarchyProvider hierarchyProvider;
        try
        {
            DiagnosticLogger.Info("准备创建 OneNoteHierarchyProvider");
            hierarchyProvider = new OneNoteInteropHierarchyProvider();
            DiagnosticLogger.Info("OneNoteHierarchyProvider 创建完成");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error("创建 OneNoteHierarchyProvider 失败", ex);
            System.Windows.Forms.MessageBox.Show(
                $"无法连接到 OneNote：{ex.Message}\n\n日志文件：{DiagnosticLogger.LogFilePath}",
                "OneNote 初始化失败",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
            return;
        }

        IOneNotePageContentProvider pageContentProvider = (IOneNotePageContentProvider)hierarchyProvider;
        ISemanticDocumentParser semanticParser = new OneNoteXmlSemanticParser();
        INotionBlockMapper blockMapper = new NotionBlockMapper();
        var handler = new SocketsHttpHandler
        {
            // Force new connections periodically to avoid stale SSL sessions
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 5
        };
        var notionApiClient = new NotionApiClient(new HttpClient(handler));

        var syncOrchestrator = new NotionSyncOrchestrator(
            hierarchyProvider,
            pageContentProvider,
            semanticParser,
            blockMapper,
            notionApiClient);

        DiagnosticLogger.Info("启动主窗体");
        System.Windows.Forms.Application.Run(new Form1(hierarchyProvider, syncOrchestrator, notionApiClient));
        DiagnosticLogger.Info("程序退出");
    }
}
