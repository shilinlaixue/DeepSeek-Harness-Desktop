# DeepSeek Desktop — Windows

C# WPF + WebView2 原生窗口，内置 Node.js + `dsh`，双击即用。与命令行 `dsh` 共用
`%USERPROFILE%\.dsh` 和默认工作区。

## 1.1.0 修复

- 捕获 `dsh web` 打印的带 `?token=` 地址；打开裸端口会导致 401/白屏，已修复。
- 默认 `--port 0`，不再和 3080 上的其他进程冲突。
- cwd 改为 `Documents\deepseek-harness\default-workspace`，避免安装目录只读导致沙箱报错。
- WebView2 缓存改到 `%LOCALAPPDATA%\DeepSeek\WebView2`，修复 Program Files 下拒绝访问。
- 单实例、失败可重试、加载失败错误页、日志入口。
- stdout 通过文件捕获（GUI 无控制台也能稳定启动）。
- Job Object 保证退出/崩溃后清理 `node.exe`。
- 自包含 .NET 8 发布，用户无需安装 .NET。

## 构建

```powershell
# 在仓库根目录，需要 .NET 8 SDK
pwsh scripts/build-windows.ps1
```

产物：`dist/DeepSeek-1.1.0-win-x64.zip`（自包含，解压双击 `DeepSeek.exe`）。

手动：

```powershell
dotnet publish windows -c Release -o .build/publish
# 再把 node.exe + dsh bundle 放到 .build/publish/runtime/
```

## 自检

```powershell
$env:DSH_DESKTOP_DATA="$env:TEMP\dsh-selftest"
$env:DSH_DESKTOP_SELFTEST_OUT="$env:TEMP\dsh-selftest.txt"
$env:DSH_DESKTOP_PORT="0"
.\DeepSeek.exe --self-test
Get-Content "$env:TEMP\dsh-selftest.txt"
```

真实启动内置 Node + dsh，解析 token URL，HTTP 校验后退出，CI 用它做冒烟测试。

## 环境变量

| 变量 | 用途 |
|------|------|
| `DSH_DESKTOP_PORT` | 固定端口，默认 0（系统分配） |
| `DSH_DESKTOP_WORKSPACE` | 覆盖工作区 |
| `DSH_DESKTOP_HOME` | 覆盖 `DSH_HOME`（测试/便携） |
| `DSH_DESKTOP_DATA` | 覆盖日志与 WebView2 缓存目录 |
| `DSH_DESKTOP_URL` | 直接使用已有的带 token 地址 |

## 已知限制

- 需要 WebView2 Runtime（Windows 10/11 一般已内置）；缺失时应用会明确提示。
- 全新 `~/.dsh` 首次需要完成一次登录；已有 CLI 数据的用户直接复用。
- 安装包未做商业代码签名，SmartScreen 可能提示未知发布者。
