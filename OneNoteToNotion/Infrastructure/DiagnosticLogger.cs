using System;
using System.IO;
using System.Text;

namespace OneNoteToNotion.Infrastructure;

internal static class DiagnosticLogger
{
    private static readonly object SyncRoot = new();
    private static readonly AsyncLocal<string?> OriginalPathContext = new();
    private static readonly string LogDirectory = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "logs");
    private static readonly string LegacyLogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OneNoteToNotion",
        "logs");
    private static readonly string CurrentLogFilePath = Path.Combine(
        LogDirectory,
        $"onenote-notion-{DateTime.Now:yyyyMMdd}.log");

    static DiagnosticLogger()
    {
        InitializeLogDirectory();
    }

    public static string LogFilePath => CurrentLogFilePath;

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public static IDisposable BeginOriginalPathScope(string? originalPath)
    {
        var previous = OriginalPathContext.Value;
        if (!string.IsNullOrWhiteSpace(originalPath))
        {
            OriginalPathContext.Value = originalPath;
        }

        return new OriginalPathScope(previous);
    }

    public static string DescribeException(Exception exception)
    {
        var builder = new StringBuilder();
        var current = exception;
        var depth = 0;
        while (current is not null)
        {
            if (depth > 0)
            {
                builder.Append(" --> ");
            }

            builder.Append($"[{current.GetType().Name} 0x{current.HResult:X8}] {current.Message}");
            current = current.InnerException;
            depth++;
        }

        return builder.ToString();
    }

    private static void Write(string level, string message, Exception? exception)
    {
        var originalPath = OriginalPathContext.Value;
        if (string.IsNullOrWhiteSpace(originalPath))
        {
            originalPath = "-";
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {message} | 原始路径={originalPath}";
        if (exception is not null)
        {
            line += $" | {DescribeException(exception)}";
        }

        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(CurrentLogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Ignore logging I/O failures to avoid affecting primary flow.
        }

        System.Diagnostics.Trace.WriteLine(line);
    }

    private static void InitializeLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            MigrateLegacyLogs();
        }
        catch
        {
            // Ignore initialization failures to avoid affecting primary flow.
        }
    }

    private static void MigrateLegacyLogs()
    {
        if (!Directory.Exists(LegacyLogDirectory))
        {
            return;
        }

        foreach (var sourcePath in Directory.EnumerateFiles(LegacyLogDirectory, "*.log"))
        {
            try
            {
                var fileName = Path.GetFileName(sourcePath);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }

                var targetPath = Path.Combine(LogDirectory, fileName);
                if (File.Exists(targetPath))
                {
                    continue;
                }

                File.Move(sourcePath, targetPath);
            }
            catch
            {
                // Ignore migration failures for a single file.
            }
        }
    }

    private sealed class OriginalPathScope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public OriginalPathScope(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            OriginalPathContext.Value = _previous;
            _disposed = true;
        }
    }
}

