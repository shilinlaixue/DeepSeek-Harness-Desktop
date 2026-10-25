using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DeepSeekDesktop;

/// <summary>
/// Owns the bundled <c>dsh web</c> process.
///
/// Newer Harness builds protect the UI with a per-process launch token and print
/// the only usable URL as <c>dsh web: http://127.0.0.1:PORT/?token=...</c>.
/// Opening the bare port returns HTTP 401 (the old white-screen bug), so the
/// launcher captures that line and gives the tokenized URL to WebView2.
///
/// Output is redirected to a file by a tiny .cmd shim rather than an anonymous
/// pipe: a GUI app has no console, and file tailing also survives antivirus /
/// policy environments where inheriting pipe handles is unreliable.
/// </summary>
public sealed class ServerManager : IDisposable
{
    private static readonly Regex UrlPattern = new(@"dsh web:\s+(?<url>https?://\S+)", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly object _logLock = new();
    private readonly StringBuilder _tail = new();
    private Process? _serverProcess;
    private JobHandle? _job;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private string? _stdoutPath;
    private string? _cmdPath;
    private bool _shuttingDown;

    public ServerManager()
    {
        _http = new HttpClient(new HttpClientHandler
        {
            // Localhost probes must never go through a system/corporate proxy.
            UseProxy = false,
            UseCookies = true,
            AllowAutoRedirect = true,
            CookieContainer = new CookieContainer(),
        })
        {
            Timeout = TimeSpan.FromSeconds(3),
        };
    }

    /// <summary>Tokenized URL that the WebView must open. Null until ready.</summary>
    public string? Url { get; private set; }

    /// <summary>Actual listening port chosen by dsh.</summary>
    public int Port { get; private set; }

    /// <summary>Human-readable URL without the secret launch token.</summary>
    public string DisplayUrl => Port > 0 ? $"http://127.0.0.1:{Port}" : "(starting)";

    public string LogPath { get; } = AppPaths.ServerLog;
    public string StdoutLogPath => _stdoutPath ?? Path.Combine(AppPaths.DataRoot, "dsh-stdout.log");

    public bool IsRunning => _serverProcess is { HasExited: false };

    /// <summary>Raised on a worker thread if the server we spawned exits.</summary>
    public event Action? ServerExited;

    public async Task EnsureServerAsync(CancellationToken cancellationToken = default)
    {
        if (Url is not null && IsRunning) return;
        Shutdown();

        // Advanced/portable escape hatch: attach to an already-authorized URL
        // (for example a dsh web instance the user started explicitly).
        var preset = Environment.GetEnvironmentVariable("DSH_DESKTOP_URL");
        if (!string.IsNullOrWhiteSpace(preset) &&
            Uri.TryCreate(preset, UriKind.Absolute, out var presetUri) && presetUri.Port > 0)
        {
            await VerifyServingAsync(preset, cancellationToken);
            Url = preset;
            Port = presetUri.Port;
            AppendLog($"using DSH_DESKTOP_URL: {DisplayUrl}");
            return;
        }

        var workspace = EnsureWorkspace();
        var requestedPort = ReadPortOverride();
        AppPaths.EnsureDataRoot();
        RotateLog(LogPath);

        _stdoutPath = Path.Combine(AppPaths.DataRoot, "dsh-stdout.log");
        RotateLog(_stdoutPath);
        _cmdPath = Path.Combine(AppPaths.DataRoot, $"launch-{Guid.NewGuid():N}.cmd");

        AppendLog($"=== DeepSeek Desktop {AppInfo.Version} starting ===");
        AppendLog($"workspace: {workspace}");
        AppendLog($"log: {LogPath}");

        var (node, bin) = ResolveRuntime();
        AppendLog($"node: {node}");
        AppendLog($"dsh: {bin}");

        WriteLaunchScript(node, bin, requestedPort, _stdoutPath, _cmdPath);

        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec")
                       ?? Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            WorkingDirectory = workspace,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/d");
        psi.ArgumentList.Add("/c");
        // Let .NET quote the path once; adding our own quotes makes cmd see \"...\".
        psi.ArgumentList.Add(_cmdPath);
        // Keep the real HOME so sessions/credentials stay shared with the CLI.
        psi.Environment["DSH_DESKTOP"] = "1";
        var homeOverride = Environment.GetEnvironmentVariable("DSH_DESKTOP_HOME");
        if (!string.IsNullOrWhiteSpace(homeOverride))
        {
            psi.Environment["DSH_HOME"] = homeOverride;
            AppendLog($"DSH_HOME override: {homeOverride}");
        }

        try
        {
            _serverProcess = Process.Start(psi)
                ?? throw new InvalidOperationException("无法创建 dsh 进程");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法启动内置 Node 运行时：{ex.Message}", ex);
        }

        _job = JobHandle.CreateKillOnClose();
        _job?.Assign(_serverProcess);
        _serverProcess.EnableRaisingEvents = true;

        var urlReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _serverProcess.Exited += (_, _) =>
        {
            exited.TrySetResult();
            if (!_shuttingDown)
            {
                AppendLog($"dsh exited code={SafeExitCode()}");
                ServerExited?.Invoke();
            }
        };

        _pumpCts = new CancellationTokenSource();
        _pumpTask = PumpOutputAsync(_stdoutPath, urlReady, exited, _pumpCts.Token);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // First launch on a cold/antivirus-scanned machine can be slow: dsh
        // bootstraps its profile and loads hundreds of modules.
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var delay = Task.Delay(Timeout.Infinite, timeout.Token);
        var completed = await Task.WhenAny(urlReady.Task, exited.Task, delay);
        if (completed == exited.Task)
        {
            await Task.Delay(300); // let the pump flush the final lines
            throw new InvalidOperationException(
                $"dsh 进程提前退出（exit code {SafeExitCode()}）。\n\n{RecentLogTail()}\n\n日志：{LogPath}");
        }
        if (completed != urlReady.Task)
        {
            throw new TimeoutException($"等待 dsh 启动超时。\n\n{RecentLogTail()}\n\n日志：{LogPath}");
        }

        var url = await urlReady.Task;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Port <= 0)
        {
            throw new InvalidOperationException($"dsh 返回了无法解析的地址：{Redact(url)}");
        }
        Port = uri.Port;

        await VerifyServingAsync(url, cancellationToken);
        Url = url;
        AppendLog($"ready: {DisplayUrl}");
    }

    private async Task VerifyServingAsync(string url, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await _http.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var html = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (html.Length > 200 && html.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    last = new InvalidOperationException("本地服务返回的页面不是 DeepSeek Harness UI");
                }
                else
                {
                    last = new HttpRequestException($"HTTP {(int)response.StatusCode}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }
            await Task.Delay(300, cancellationToken);
        }
        throw new InvalidOperationException(
            $"本地服务拒绝连接：{last?.Message}\n\n{RecentLogTail()}\n\n日志：{LogPath}");
    }

    /// <summary>
    /// Tails the redirected output file, copies it into server.log and watches
    /// for the one URL line dsh prints when the HTTP listener is ready.
    /// </summary>
    private async Task PumpOutputAsync(string path, TaskCompletionSource<string> urlReady,
        TaskCompletionSource exited, CancellationToken cancellationToken)
    {
        var offset = 0L;
        var carry = string.Empty;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(path))
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length > offset)
                    {
                        stream.Seek(offset, SeekOrigin.Begin);
                        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
                        var chunk = await reader.ReadToEndAsync(cancellationToken);
                        offset = stream.Length;
                        AppendDshOutput(chunk);
                        carry += chunk;
                        var lines = carry.Split('\n');
                        carry = lines.Length > 0 ? lines[^1] : string.Empty;
                        if (carry.Length > 8192) carry = carry[^8192..];
                        for (var i = 0; i < lines.Length - 1; i++)
                        {
                            var match = UrlPattern.Match(lines[i]);
                            if (match.Success) urlReady.TrySetResult(match.Groups["url"].Value.Trim());
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (exited.Task.IsCompleted || _serverProcess is { HasExited: true })
            {
                exited.TrySetResult();
                return;
            }

            try { await Task.Delay(200, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void AppendDshOutput(string chunk)
    {
        foreach (var line in chunk.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line)) AppendLog("dsh: " + Redact(line.TrimEnd('\r')));
        }
    }

    private static int ReadPortOverride()
    {
        var raw = Environment.GetEnvironmentVariable("DSH_DESKTOP_PORT");
        return int.TryParse(raw, out var port) && port is >= 0 and <= 65535 ? port : 0;
    }

    /// <summary>
    /// dsh creates <c>Documents\deepseek-harness\default-workspace</c> itself; use the
    /// same directory as the process cwd so the sandbox root is writable and identical
    /// to the official desktop / CLI default.
    /// </summary>
    public static string EnsureWorkspace()
    {
        var custom = Environment.GetEnvironmentVariable("DSH_DESKTOP_WORKSPACE");
        var workspace = !string.IsNullOrWhiteSpace(custom)
            ? custom
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "deepseek-harness", "default-workspace");
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    private static (string node, string bin) ResolveRuntime()
    {
        var root = AppContext.BaseDirectory;
        var node = Path.Combine(root, "runtime", "node.exe");
        var bundle = Path.Combine(root, "runtime", "bundle");
        var bin = Path.Combine(bundle, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");

        if (!File.Exists(node))
            throw new FileNotFoundException($"内置 Node 运行时缺失：{node}");
        if (!File.Exists(bin))
            throw new FileNotFoundException($"内置 dsh 包缺失：{bin}");
        return (node, bin);
    }

    private static void WriteLaunchScript(string node, string bin, int port, string stdoutPath, string cmdPath)
    {
        string Q(string value) => "\"" + value.Replace("%", "%%") + "\"";
        var content = string.Join("\r\n",
        [
            "@echo off",
            "chcp 65001 >nul",
            // < nul is essential: a WinExe has no console/stdin, and dsh may
            // otherwise sit waiting on an invalid stdin handle before it binds.
            $"{Q(node)} {Q(bin)} web --host 127.0.0.1 --port {port} --no-open < nul > {Q(stdoutPath)} 2>&1",
            "exit /b %ERRORLEVEL%",
            string.Empty,
        ]);
        File.WriteAllText(cmdPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private int SafeExitCode()
    {
        try { return _serverProcess?.ExitCode ?? -1; }
        catch { return -1; }
    }

    // --- logging ---------------------------------------------------------

    private static void RotateLog(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var length = new FileInfo(path).Length;
            if (length > 1_500_000)
            {
                var old = path + ".1";
                File.Delete(old);
                File.Move(path, old);
            }
        }
        catch { }
    }

    private void AppendLog(string line)
    {
        lock (_logLock)
        {
            _tail.AppendLine(line);
            if (_tail.Length > 12_000)
            {
                _tail.Remove(0, _tail.Length - 8_000);
            }
            try
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}", Encoding.UTF8);
            }
            catch { /* logging must never break startup */ }
        }
    }

    private string RecentLogTail()
    {
        lock (_logLock)
        {
            var text = _tail.ToString();
            return text.Length <= 3000 ? text : text[^3000..];
        }
    }

    private static string Redact(string value) =>
        Regex.Replace(value, @"([?&]token=)[^&\s]+", "$1***");

    // --- lifetime --------------------------------------------------------

    public void Shutdown()
    {
        _shuttingDown = true;
        try { _pumpCts?.Cancel(); } catch { }
        try { _pumpTask?.Wait(500); } catch { }
        _pumpCts?.Dispose();
        _pumpCts = null;
        _pumpTask = null;

        var process = _serverProcess;
        _serverProcess = null;
        Url = null;
        Port = 0;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch { }
            process.Dispose();
        }

        _job?.Dispose();
        _job = null;
        if (_cmdPath is not null)
        {
            try { File.Delete(_cmdPath); } catch { }
            _cmdPath = null;
        }
        _shuttingDown = false;
    }

    public void Dispose()
    {
        Shutdown();
        _http.Dispose();
    }

    /// <summary>KILL_ON_JOB_CLOSE backstop: even a crash cannot orphan node.exe.</summary>
    private sealed class JobHandle : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        private IntPtr _handle;

        private JobHandle(IntPtr handle) => _handle = handle;

        public static JobHandle? CreateKillOnClose()
        {
            try
            {
                var handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero) return null;
                var info = new JobObjectExtendedLimitInformationStructure
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose,
                    },
                };
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info,
                        (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationStructure>()))
                {
                    CloseHandle(handle);
                    return null;
                }
                return new JobHandle(handle);
            }
            catch
            {
                return null;
            }
        }

        public void Assign(Process process)
        {
            try { AssignProcessToJobObject(_handle, process.Handle); }
            catch { }
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformationStructure
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass,
            ref JobObjectExtendedLimitInformationStructure info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}

internal static class AppInfo
{
    public const string Version = "1.1.0";
}
