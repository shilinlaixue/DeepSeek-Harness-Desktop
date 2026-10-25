using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace DeepSeekDesktop;

public partial class App : Application
{
    private const string MutexName = @"Local\DeepSeekDesktop.Singleton";
    private const string ActivateEventName = @"Local\DeepSeekDesktop.Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private Thread? _activateThread;
    private ServerManager? _server;
    private SplashWindow? _splash;
    private MainWindow? _main;
    private bool _startupRunning;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Headless smoke test used by CI: launches the real bundled runtime,
        // verifies the tokenized URL, then exits with 0/1.
        if (e.Args.Any(arg => arg.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            RunSelfTest();
            return;
        }

        if (!AcquireSingleInstance())
        {
            // Another copy owns the app: ask it to come to the foreground, then leave.
            try { _activateEvent?.Set(); } catch { }
            Shutdown(0);
            return;
        }

        _splash = new SplashWindow();
        _splash.RetryRequested += () => _ = StartupAsync();
        _splash.QuitRequested += () => Shutdown(0);
        _splash.Show();

        _server = new ServerManager();
        _ = StartupAsync();
    }

    private async void RunSelfTest()
    {
        var output = Environment.GetEnvironmentVariable("DSH_DESKTOP_SELFTEST_OUT")
                     ?? Path.Combine(AppPaths.DataRoot, "selftest.txt");
        try
        {
            AppPaths.EnsureDataRoot();
            _server = new ServerManager();
            await _server.EnsureServerAsync();
            File.WriteAllText(output, $"OK {_server.DisplayUrl}{Environment.NewLine}log={_server.LogPath}");
            _server.Dispose();
            Shutdown(0);
        }
        catch (Exception ex)
        {
            try
            {
                AppPaths.EnsureDataRoot();
                File.WriteAllText(output, $"FAIL {ex}{Environment.NewLine}");
            }
            catch { }
            _server?.Dispose();
            Shutdown(1);
        }
    }

    private async Task StartupAsync()
    {
        if (_startupRunning || _splash is null) return;
        _startupRunning = true;
        var clock = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                try
                {
                    _server ??= new ServerManager();
                    _splash.SetStatus("正在启动内置 dsh 服务…");
                    await _server.EnsureServerAsync();

                    // Keep the launch animation from flashing: total minimum ~2.4s.
                    var remaining = TimeSpan.FromMilliseconds(2400) - clock.Elapsed;
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining);

                    await Dispatcher.InvokeAsync(ShowMainWindow);
                    return;
                }
                catch (Exception ex)
                {
                    LogCrash("startup", ex);
                    _splash.ShowError(ShortMessage(ex));
                    return; // stay on splash, wait for Retry
                }
            }
        }
        finally
        {
            _startupRunning = false;
        }
    }

    private void ShowMainWindow()
    {
        if (_splash is null || _server?.Url is null) return;

        _main = new MainWindow(_server);
        _main.Closed += (_, _) => Shutdown(0);
        _main.Show();
        _main.Activate();

        _splash.Close();
        _splash = null;
    }

    private static string ShortMessage(Exception ex)
    {
        var message = ex.Message;
        if (message.Length > 700) message = message[..700] + "…";
        return message;
    }

    private bool AcquireSingleInstance()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            if (!createdNew) return false;

            _activateThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        if (!_activateEvent.WaitOne()) return;
                        Dispatcher.BeginInvoke(ActivateExistingWindow);
                    }
                    catch (ObjectDisposedException) { return; }
                    catch (ThreadInterruptedException) { return; }
                }
            })
            { IsBackground = true, Name = "DeepSeek activation listener" };
            _activateThread.Start();
            return true;
        }
        catch
        {
            // Named-kernel objects can be denied by hardened policy; a single
            // instance is a nicety, not a reason to refuse to start.
            return true;
        }
    }

    private void ActivateExistingWindow()
    {
        var window = _main ?? (Window?)_splash;
        if (window is null) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("ui", e.Exception);
        MessageBox.Show(
            $"DeepSeek 遇到未处理的错误：\n\n{e.Exception.Message}\n\n日志：{_server?.LogPath ?? "见 %LOCALAPPDATA%\\DeepSeek"}",
            "DeepSeek", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    internal static void LogCrash(string source, Exception ex)
    {
        try
        {
            AppPaths.EnsureDataRoot();
            File.AppendAllText(AppPaths.CrashLog,
                $"{DateTime.Now:O} [{source}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _server?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _activateEvent?.Dispose();
        base.OnExit(e);
    }
}
