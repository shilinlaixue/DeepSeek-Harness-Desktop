# DeepSeek Desktop

把 DeepSeek Harness Web UI 打包成双击即用的原生桌面应用，支持 **Windows** 与 **macOS**。
应用内置 Node.js 运行时和完整 `dsh` 包，不依赖浏览器、不需要用户安装 Node/.NET；
启动时拉起本地 `dsh web`，用系统原生 WebView（Windows WebView2 / macOS WKWebView）承载完整界面，
数据目录与命令行 `dsh` 完全共用。

## 当前版本

**1.1.0** — 2026-10-25

这一版修复了旧桌面端最常见的三个“打不开/白屏”根因：

1. **认证令牌**：新版 `dsh web` 会为每个进程生成一次性 token，并只把可用地址打印为
   `dsh web: http://127.0.0.1:PORT/?token=...`。旧代码打开裸端口，服务返回 401，表现为白屏。
   现在启动器会从输出中捕获完整 URL 再交给 WebView。
2. **端口冲突 / 复用失败**：默认使用 `--port 0` 由系统分配空闲端口，不再和用户自己的
   `dsh web`、残留进程或其他软件抢 3080。
3. **工作目录不可写**：服务器不再以 `Program Files` 下的安装目录为 cwd，而是以
   `Documents\deepseek-harness\default-workspace` 为工作区，避免沙箱写文件报错。

其他修复：

- WebView2 用户数据目录固定到 `%LOCALAPPDATA%\DeepSeek\WebView2`，解决安装到
  `Program Files` 后「WebView2 初始化失败：拒绝访问」。
- 单实例：重复双击只唤起已有窗口，不会堆叠多个进程。
- 文件级 stdout 捕获（不用匿名管道），GUI 无控制台 / 杀软环境也不会卡在服务启动。
- Windows Job Object + `Kill(entireProcessTree)` 双保险，退出/崩溃都不留 `node.exe`。
- 启动失败不再闪退：启动页显示真实错误、日志路径，并提供「重试 / 退出」。
- 加载失败不再白屏：主窗口有可恢复错误页，可重试/打开日志/重启。
- 自包含发布：Windows 包内置 .NET 8 运行时，用户无需安装任何环境。

## 平台支持

| 平台 | 技术栈 | 发行物 | 系统要求 |
|------|--------|--------|----------|
| Windows | C# WPF + WebView2 | `DeepSeek-1.1.0-win-x64.zip`（免安装） / Inno Setup 安装包 | Windows 10/11 x64 |
| macOS | SwiftUI + WKWebView | `.dmg`（由 macOS CI 构建） | macOS 13+，Apple Silicon / Intel |

> Windows 版需要系统自带的 **Microsoft Edge WebView2 Runtime**（Windows 10/11 一般已预装）。
> 如果缺失，应用会明确提示，不会只显示白屏。

## 下载 / 使用

### Windows（已验证）

任选一种：

- **安装版**：运行 `DeepSeek-Setup-1.1.0.exe`，一路 Next（默认装到 Program Files，创建开始菜单/桌面快捷方式）。
- **免安装版**：解压 `DeepSeek-1.1.0-win-x64.zip` 到任意可写目录，双击 `DeepSeek.exe`。

首次启动约 10–40 秒（dsh 初始化 profile，杀软扫描新解压文件时更慢）；之后一般 8–12 秒进入界面。

如果首次进入显示「等待登录」，按页面提示完成 DeepSeek 登录即可；登录凭据写入
`%USERPROFILE%\.dsh`，与命令行 `dsh` 共用。

### macOS

使用 GitHub Actions 的 `mac-v*` 标签构建 `.dmg`，或在本机执行：

```bash
./build.sh          # 需要 Xcode 命令行工具 + 构建机上的 Node.js
./build.sh --install
```

## 与 CLI 数据互通

| 平台 | 数据目录 |
|------|----------|
| Windows | `%USERPROFILE%\.dsh` |
| macOS | `~/.dsh` |
| 默认工作区 | `~/Documents/deepseek-harness/default-workspace` |

桌面版不设置 `DSH_HOME` 时，会直接复用用户已有的 `~/.dsh`（会话、凭据、插件）。
只有测试/便携模式才会用环境变量覆盖。

## 环境变量

| 变量 | 说明 |
|------|------|
| `DSH_DESKTOP_PORT` | 固定端口；默认 `0`（系统分配）。端口被占用时启动会明确报错 |
| `DSH_DESKTOP_WORKSPACE` | 覆盖默认工作区目录 |
| `DSH_DESKTOP_HOME` | 覆盖 `DSH_HOME`（测试/便携用；正常不要设置） |
| `DSH_DESKTOP_DATA` | 覆盖日志/WebView2 缓存目录（Windows） |
| `DSH_DESKTOP_URL` | 高级用法：直接使用已有的带 token URL |

## 从源码构建

### Windows

需要 .NET 8 SDK（构建机），运行时会被打进安装包：

```powershell
pwsh scripts/build-windows.ps1
```

脚本会：下载固定版本的 Node、安装固定版本的 `dsh`、`dotnet publish` 自包含发布、
运行 `DeepSeek.exe --self-test` 冒烟测试、生成 zip + SHA256。

仅编译：

```powershell
dotnet publish windows -c Release -o .build/publish
```

### macOS

```bash
./build.sh          # 编译 + 捆绑 Node/dsh + ad-hoc 签名 + DMG
```

`build.sh` 会自动使用 `PATH` 中的 Node，并在 `.build/bundle` 安装固定版本的 `dsh`。

### 冒烟测试

Windows 构建产物支持无界面自检（CI 也用它）：

```powershell
$env:DSH_DESKTOP_DATA="$env:TEMP\dsh-selftest"
$env:DSH_DESKTOP_SELFTEST_OUT="$env:TEMP\dsh-selftest.txt"
.\DeepSeek.exe --self-test
Get-Content "$env:TEMP\dsh-selftest.txt"   # OK http://127.0.0.1:xxxxx
```

它会真实启动内置 Node + dsh、解析 token URL、HTTP 校验后退出。

## 防篡改（macOS）

macOS 构建时会把关键文件 SHA256 写入 `Sources/Integrity.generated.swift`，启动时校验；
不匹配则拒绝启动。Windows 版目前以代码签名/哈希校验安装包为主，未做运行时自校验。

## 目录结构

```
Sources/                 macOS Swift 源码
Resources/               macOS 图标与启动素材
windows/                 Windows WPF 源码
scripts/build-windows.ps1 Windows 一键构建
build.sh                 macOS 一键构建
.github/workflows/       双平台 CI / Release
```

## 已知限制

- macOS 版在 Windows 上无法编译验证；本次交付包含源码与 CI，Windows 版已实机验证。
- 全新数据目录首次登录需要走一次 DeepSeek 登录流程；已有 `~/.dsh` 的用户直接复用。
- 安装包未做商业代码签名；Windows SmartScreen 可能提示“未知发布者”，选择“仍要运行”即可。
