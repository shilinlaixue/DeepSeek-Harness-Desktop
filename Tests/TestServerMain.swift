// 无头冒烟测试：验证 ServerManager 的 spawn / token URL / HTTP / shutdown 逻辑
// 编译：swiftc -swift-version 5 -parse-as-library Sources/ServerManager.swift Tests/TestServerMain.swift -o Tests/test-server
// 运行：DSH_DESKTOP_HOME=<writable-dir> ./Tests/test-server
import Foundation

@main
struct TestServerMain {
    static func main() async {
        let manager = ServerManager.shared
        await manager.ensureServer()

        switch manager.state {
        case .ready(let endpoint):
            print("TEST: state = ready(\(endpoint.absoluteString))")
            do {
                let (data, resp) = try await URLSession.shared.data(from: endpoint)
                let http = resp as? HTTPURLResponse
                let body = String(data: data, encoding: .utf8) ?? ""
                let hasBoot = body.contains("__DSH_BOOT__") || body.contains("DeepSeek")
                print("TEST: http=\(http?.statusCode ?? -1) bytes=\(data.count) hasBoot=\(hasBoot)")
            } catch {
                print("TEST: fetch failed: \(error)")
            }
        case .starting:
            print("TEST: state = starting (unexpected)")
        case .failed(let msg):
            print("TEST: state = failed: \(msg)")
        }

        let port = manager.port ?? 0
        try? await Task.sleep(nanoseconds: 1_000_000_000)
        manager.shutdown()
        try? await Task.sleep(nanoseconds: 500_000_000)

        if port > 0 {
            do {
                var req = URLRequest(url: URL(string: "http://127.0.0.1:\(port)/")!)
                req.timeoutInterval = 2
                _ = try await URLSession.shared.data(for: req)
                print("TEST: after shutdown port still responds (BAD if we spawned)")
            } catch {
                print("TEST: after shutdown port closed (OK)")
            }
        }
        print("TEST: done")
        exit(0)
    }
}
