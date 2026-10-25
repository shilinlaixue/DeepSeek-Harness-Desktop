using System.IO;

namespace DeepSeekDesktop;

/// <summary>
/// Writable per-user locations. <c>DSH_DESKTOP_DATA</c> makes the app portable
/// (and lets CI point logs/WebView cache at a scratch directory).
/// </summary>
internal static class AppPaths
{
    public static string DataRoot
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("DSH_DESKTOP_DATA");
            return !string.IsNullOrWhiteSpace(custom)
                ? custom
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeek");
        }
    }

    public static string WebViewData => Path.Combine(DataRoot, "WebView2");
    public static string ServerLog => Path.Combine(DataRoot, "server.log");
    public static string CrashLog => Path.Combine(DataRoot, "crash.log");

    public static void EnsureDataRoot() => Directory.CreateDirectory(DataRoot);
}
