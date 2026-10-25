using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace DeepSeekDesktop;

/// <summary>
/// Native WPF window hosting the Harness UI in WebView2.
/// WebView2's user-data folder is redirected to %LOCALAPPDATA% so the app still
/// works when installed under Program Files (the old "初始化失败:拒绝访问" bug).
/// </summary>
public partial class MainWindow : Window
{
    private readonly ServerManager _server;
    private readonly string _url;
    private bool _webViewReady;
    private bool _serviceLost;

    public MainWindow(ServerManager server)
    {
        InitializeComponent();
        _server = server;
        _url = server.Url ?? throw new InvalidOperationException("服务器尚未就绪");
        Loaded += OnLoaded;
        Closing += OnClosing;
        _server.ServerExited += OnServerExited;
    }

    private static string UserDataFolder => AppPaths.WebViewData;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(UserDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: UserDataFolder,
                options: null);

            await WebView.EnsureCoreWebView2Async(environment);
            var core = WebView.CoreWebView2;

            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;

            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindowRequested;
            core.PermissionRequested += OnPermissionRequested;
            core.ProcessFailed += OnProcessFailed;

            _webViewReady = true;
            WebView.Source = new Uri(_url);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowError(
                "系统缺少 Microsoft Edge WebView2 Runtime。\n\n" +
                "Windows 10/11 通常已自带；如果没有，请从微软官网安装 “WebView2 Runtime” 后重试。",
                canRetry: false);
        }
        catch (Exception ex)
        {
            App.LogCrash("webview2-init", ex);
            ShowError($"WebView2 初始化失败：{ex.Message}\n\n日志：{_server.LogPath}", canRetry: true);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
        if (uri.Host is "127.0.0.1" or "localhost" or "::1") return;

        // Anything outside the local Harness belongs in the system browser.
        e.Cancel = true;
        OpenExternal(e.Uri);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            if (!_serviceLost) ErrorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var detail = e.WebErrorStatus switch
        {
            CoreWebView2WebErrorStatus.ConnectionAborted => "连接被中断（本地服务可能已退出）",
            CoreWebView2WebErrorStatus.CannotConnect => "无法连接本地服务",
            CoreWebView2WebErrorStatus.ServerUnreachable => "本地服务不可达",
            CoreWebView2WebErrorStatus.Timeout => "连接本地服务超时",
            _ => $"WebView 加载失败：{e.WebErrorStatus}",
        };
        ShowError(detail + $"\n\n请查看日志：{_server.LogPath}", canRetry: true);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https")
        {
            OpenExternal(e.Uri);
        }
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        // Clipboard access is used by the UI's copy buttons; never auto-grant
        // camera/microphone/geolocation from a page.
        if (e.PermissionKind is CoreWebView2PermissionKind.ClipboardRead)
        {
            e.State = CoreWebView2PermissionState.Allow;
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        App.LogCrash("webview2-process", new InvalidOperationException(e.ProcessFailedKind.ToString()));
        ShowError(
            $"渲染进程异常退出（{e.ProcessFailedKind}）。\n\n可以重试，或重启应用。",
            canRetry: true);
    }

    private void OnServerExited()
    {
        _serviceLost = true;
        Dispatcher.BeginInvoke(() =>
        {
            ShowError("本地服务已退出。点击“重启应用”重新启动 DeepSeek。", canRetry: false);
            RetryButton.Visibility = Visibility.Collapsed;
            RestartButton.Visibility = Visibility.Visible;
        });
    }

    private void ShowError(string message, bool canRetry)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        RestartButton.Visibility = canRetry ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        if (!_webViewReady)
        {
            OnLoaded(sender, e);
            return;
        }
        WebView.CoreWebView2.Navigate(_url);
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e) => RestartApp();

    private void LogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_server.LogPath)!);
            if (!File.Exists(_server.LogPath)) File.WriteAllText(_server.LogPath, string.Empty);
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_server.LogPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开日志：{ex.Message}", "DeepSeek");
        }
    }

    private static void OpenExternal(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch { /* no default browser: ignore */ }
    }

    private void RestartApp()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
        }
        catch { }
        Application.Current.Shutdown(0);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _server.ServerExited -= OnServerExited;
        _server.Shutdown();
    }
}
