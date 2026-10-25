import Foundation
import Combine

/// 管理 DeepSeek Harness 本地 Web 服务器。
///
/// 新版 `dsh web` 会为每次进程生成一次性启动令牌，并把唯一可用地址打印为
/// `dsh web: http://127.0.0.1:PORT/?token=...`。直接打开裸端口会得到 401，
/// 这正是旧版桌面端白屏的根因；这里从日志中捕获完整 URL 后再交给 WKWebView。
@MainActor
final class ServerManager: ObservableObject {
    static let shared = ServerManager()

    enum ServerState: Equatable {
        case starting
        case ready(URL)
        case failed(String)
    }

    @Published var state: ServerState = .starting
    @Published private(set) var url: URL?

    private var serverProcess: Process?
    private var logURL: URL?
    private(set) var port: Int?

    private init() {}

    // MARK: - 配置

    /// 固定端口仅作为可选覆盖；默认 0 让系统选空闲端口，避免与用户自己的 dsh 冲突。
    private var requestedPort: Int {
        let env = ProcessInfo.processInfo.environment
        if let raw = env["DSH_DESKTOP_PORT"], let p = Int(raw), p >= 0, p <= 65535 {
            return p
        }
        return 0
    }

    private var homeOverride: String? {
        let env = ProcessInfo.processInfo.environment
        let v = env["DSH_DESKTOP_HOME"] ?? ""
        return v.isEmpty ? nil : v
    }

    // MARK: - 主流程

    func ensureServer() async {
        state = .starting
        url = nil
        shutdown()

        do {
            let endpoint = try await startServer()
            url = endpoint
            port = endpoint.port
            state = .ready(endpoint)
        } catch {
            state = .failed(error.localizedDescription)
        }
    }

    private func startServer() async throws -> URL {
        let runtime = try runtimeRoot()
        let node = runtime.appendingPathComponent("node")
        let bin = runtime.appendingPathComponent("bundle/node_modules/@deepseek-ai/dsh/lib/bin.js")
        guard FileManager.default.isExecutableFile(atPath: node.path),
              FileManager.default.fileExists(atPath: bin.path) else {
            throw ServerError.noRuntime
        }

        let workspace = try ensureWorkspace()
        let logFile = try prepareLog()

        let p = Process()
        p.executableURL = node
        p.arguments = ["web", "--host", "127.0.0.1", "--port", String(requestedPort), "--no-open"]
        p.currentDirectoryURL = workspace

        var env = ProcessInfo.processInfo.environment
        env["DSH_DESKTOP"] = "1"
        if let home = homeOverride { env["DSH_HOME"] = home }
        p.environment = env

        let handle = try FileHandle(forWritingTo: logFile)
        p.standardOutput = handle
        p.standardError = handle

        do {
            try p.run()
        } catch {
            try? handle.close()
            throw ServerError.spawn(error.localizedDescription)
        }
        // 子进程持有自己的句柄；父进程关掉，避免日志文件长期被占用。
        try? handle.close()
        serverProcess = p

        let deadline = Date().addingTimeInterval(45)
        while Date() < deadline {
            if !p.isRunning {
                throw ServerError.exited(p.terminationStatus, logFile.path)
            }
            if let endpoint = parseEndpoint(from: logFile) {
                try await verify(endpoint)
                return endpoint
            }
            try? await Task.sleep(nanoseconds: 250_000_000)
        }
        throw ServerError.timedOut(logFile.path)
    }

    /// 与 App/CLI 共用 `~/Documents/deepseek-harness/default-workspace`：
    /// dsh 自己会创建它，作为 cwd 也能保证沙箱根目录可写。
    private func ensureWorkspace() throws -> URL {
        if let custom = ProcessInfo.processInfo.environment["DSH_DESKTOP_WORKSPACE"],
           !custom.isEmpty {
            let url = URL(fileURLWithPath: custom, isDirectory: true)
            try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
            return url
        }
        guard let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first else {
            throw ServerError.noWorkspace
        }
        let workspace = documents
            .appendingPathComponent("deepseek-harness", isDirectory: true)
            .appendingPathComponent("default-workspace", isDirectory: true)
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        return workspace
    }

    private func prepareLog() throws -> URL {
        guard let appSupport = FileManager.default.urls(for: .applicationSupportDirectory,
                                                        in: .userDomainMask).first else {
            throw ServerError.noResources
        }
        let dir = appSupport.appendingPathComponent("DeepSeek", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let log = dir.appendingPathComponent("server.log")
        FileManager.default.createFile(atPath: log.path, contents: nil)
        // 单文件超过 ~1.5MB 时轮转，避免无限增长。
        if let attributes = try? FileManager.default.attributesOfItem(atPath: log.path),
           let size = attributes[.size] as? NSNumber,
           size.intValue > 1_500_000 {
            let old = dir.appendingPathComponent("server.log.1")
            try? FileManager.default.removeItem(at: old)
            try? FileManager.default.moveItem(at: log, to: old)
            FileManager.default.createFile(atPath: log.path, contents: nil)
        }
        logURL = log
        return log
    }

    private func parseEndpoint(from logFile: URL) -> URL? {
        guard let text = try? String(contentsOf: logFile, encoding: .utf8) else { return nil }
        // 只需要第一处完整地址；token 是单行内的非空白字符。
        guard let range = text.range(of: #"dsh web:\s+(https?://\S+)"#, options: .regularExpression) else {
            return nil
        }
        let line = String(text[range])
        guard let urlStart = line.range(of: "http") else { return nil }
        return URL(string: String(line[urlStart.lowerBound...]).trimmingCharacters(in: .whitespacesAndNewlines))
    }

    private func verify(_ endpoint: URL) async throws {
        var request = URLRequest(url: endpoint)
        request.timeoutInterval = 3
        for _ in 0..<30 {
            do {
                let (data, response) = try await URLSession.shared.data(for: request)
                if let http = response as? HTTPURLResponse, http.statusCode == 200,
                   let html = String(data: data, encoding: .utf8),
                   html.localizedCaseInsensitiveContains("DeepSeek") {
                    return
                }
            } catch {
                // 服务刚绑定端口时可能还没开始 accept，稍后重试。
            }
            try? await Task.sleep(nanoseconds: 300_000_000)
        }
        throw ServerError.notServing(endpoint.absoluteString)
    }

    private func runtimeRoot() throws -> URL {
        guard let res = Bundle.main.resourceURL else { throw ServerError.noResources }
        let runtime = res.appendingPathComponent("runtime", isDirectory: true)
        guard FileManager.default.fileExists(atPath: runtime.path) else { throw ServerError.noRuntime }
        return runtime
    }

    enum ServerError: LocalizedError {
        case noResources
        case noRuntime
        case noWorkspace
        case spawn(String)
        case exited(Int32, String)
        case timedOut(String)
        case notServing(String)

        var errorDescription: String? {
            switch self {
            case .noResources: return "应用资源缺失"
            case .noRuntime: return "内置运行时缺失（runtime/ 未找到）"
            case .noWorkspace: return "无法创建默认工作区"
            case .spawn(let detail): return "无法启动内置 Node：\(detail)"
            case .exited(let code, let log): return "dsh 进程提前退出（\(code)）。日志：\(log)"
            case .timedOut(let log): return "服务器启动超时。日志：\(log)"
            case .notServing(let url): return "本地服务未就绪：\(url)"
            }
        }
    }

    // MARK: - 进程树清理

    private func children(of pid: Int32) -> [Int32] {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/pgrep")
        p.arguments = ["-P", String(pid)]
        let pipe = Pipe()
        p.standardOutput = pipe
        try? p.run()
        p.waitUntilExit()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        let out = String(data: data, encoding: .utf8) ?? ""
        return out.split(separator: "\n").compactMap { Int32($0.trimmingCharacters(in: .whitespaces)) }
    }

    private func processAlive(_ pid: Int32) -> Bool {
        kill(pid, 0) == 0
    }

    private func termTree(_ pid: Int32) {
        for child in children(of: pid) { termTree(child) }
        kill(pid, SIGTERM)
    }

    private func killTree(_ pid: Int32) {
        for child in children(of: pid) { killTree(child) }
        kill(pid, SIGKILL)
    }

    private func treeAlive(_ pid: Int32) -> Bool {
        if processAlive(pid) { return true }
        return children(of: pid).contains { processAlive($0) }
    }

    /// 只清理本 App 拉起的服务器；用户自己启动的 `dsh web` 不受影响。
    func shutdown() {
        guard let p = serverProcess else { return }
        let root = p.processIdentifier
        termTree(root)
        let deadline = Date().addingTimeInterval(3)
        while Date() < deadline && treeAlive(root) {
            Thread.sleep(forTimeInterval: 0.15)
        }
        killTree(root)
        serverProcess = nil
        port = nil
        url = nil
    }
}
