using System.IO;

namespace SnapTranslate.Services;

/// <summary>
/// 简易文件日志：%APPDATA%\SnapTranslate\app.log
/// 弹窗只显示摘要，完整堆栈写日志（排查用户环境问题用）。
/// </summary>
internal static class Logger
{
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SnapTranslate", "app.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string context, Exception ex)
    {
        Write("ERROR", $"{context}: {ex.GetType().Name}: {ex.Message}");
        Write("     ", ex.StackTrace ?? "(无堆栈)");
        if (ex.InnerException != null)
        {
            Write("     ", $"内部异常: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
    }

    private static void Write(string level, string? message)
    {
        if (message is null) return;
        try
        {
            lock (Sync)
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败绝不能影响主流程
        }
    }
}
