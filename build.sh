#!/bin/bash
# ============================================================
# DeepSeek Desktop — macOS build (.app -> ad-hoc sign -> .dmg)
# 用法: ./build.sh [--install]
#
# 构建机要求：Xcode Command Line Tools + Node.js/npm。
# 运行时会复制构建机的 node，并把固定版本 dsh 安装到 .build/bundle。
#
# 可覆盖：
#   RUNTIME_NODE     指定 node 可执行文件
#   DSH_BUNDLE_DIR   指定已安装 dsh 的 bundle 目录（含 node_modules）
#   DSH_VERSION      dsh 版本，默认 0.2.1-rc.2
# ============================================================
set -euo pipefail
cd "$(dirname "$0")"

APP_NAME="DeepSeek"
EXEC_NAME="DeepSeek"
VERSION="1.1.0"
BUNDLE_ID="com.deepseek.desktop"
DSH_VERSION="${DSH_VERSION:-0.2.1-rc.2}"

if [ "$(uname -m)" = "arm64" ]; then
    ARCH="arm64"
    TARGET="arm64-apple-macosx13.0"
else
    ARCH="x86_64"
    TARGET="x86_64-apple-macosx13.0"
fi

RUNTIME_NODE="${RUNTIME_NODE:-$(command -v node || true)}"
DSH_BUNDLE_DIR="${DSH_BUNDLE_DIR:-$PWD/.build/bundle}"

BUILD_DIR="build"
APP_BUNDLE="$BUILD_DIR/$APP_NAME.app"
DMG_NAME="$APP_NAME-$VERSION-$ARCH.dmg"

if [ -z "$RUNTIME_NODE" ] || [ ! -x "$RUNTIME_NODE" ]; then
    echo "错误：找不到 node。请先安装 Node.js，或设置 RUNTIME_NODE=/path/to/node" >&2
    exit 1
fi

DO_INSTALL=0
for arg in "$@"; do [ "$arg" = "--install" ] && DO_INSTALL=1; done

if [ ! -f "$DSH_BUNDLE_DIR/node_modules/@deepseek-ai/dsh/lib/bin.js" ]; then
    echo "==> 安装 dsh@$DSH_VERSION 到 $DSH_BUNDLE_DIR"
    mkdir -p "$DSH_BUNDLE_DIR"
    if [ ! -f "$DSH_BUNDLE_DIR/package.json" ]; then
        (cd "$DSH_BUNDLE_DIR" && npm init -y >/dev/null)
    fi
    (cd "$DSH_BUNDLE_DIR" && npm install --omit=dev --no-audit --no-fund "@deepseek-ai/dsh@$DSH_VERSION")
fi

echo "==> 清理旧产物"
rm -rf "$BUILD_DIR" "$DMG_NAME"
mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources"

echo "==> 生成防篡改校验常量 (Integrity.generated.swift)"
GEN="Sources/Integrity.generated.swift"
hash_file() { shasum -a 256 "$1" | awk '{print $1}'; }
cat > "$GEN" <<EOF
import Foundation

/// 由 build.sh 自动生成 —— 关键文件构建时的 SHA256，App 启动时自校验防篡改。
enum IntegrityChecks {
    static let list: [(path: String, sha256: String)] = [
        ("Contents/Info.plist", "$(hash_file Resources/Info.plist)"),
        ("Contents/Resources/AppIcon.icns", "$(hash_file Resources/AppIcon.icns)"),
        ("Contents/Resources/whale.png", "$(hash_file Resources/whale.png)"),
        ("Contents/Resources/runtime/bundle/node_modules/@deepseek-ai/dsh/lib/bin.js", "$(hash_file "$DSH_BUNDLE_DIR/node_modules/@deepseek-ai/dsh/lib/bin.js")"),
    ]
}
EOF
echo "    已生成 $(wc -l < "$GEN") 行"

echo "==> 编译 Swift 源码"
swiftc \
    -swift-version 5 -parse-as-library -O \
    -target "$TARGET" \
    -framework SwiftUI -framework AppKit -framework WebKit \
    Sources/*.swift \
    -o "$APP_BUNDLE/Contents/MacOS/$EXEC_NAME"

echo "==> 组装 .app"
cp Resources/Info.plist "$APP_BUNDLE/Contents/Info.plist"
printf 'APPL????' > "$APP_BUNDLE/Contents/PkgInfo"
if [ -f Resources/AppIcon.icns ]; then
    cp Resources/AppIcon.icns "$APP_BUNDLE/Contents/Resources/AppIcon.icns"
fi
if [ -f Resources/whale.png ]; then
    cp Resources/whale.png "$APP_BUNDLE/Contents/Resources/whale.png"
fi

echo "==> 捆绑 Node 运行时 + dsh 包（约 400MB，首次稍慢）"
mkdir -p "$APP_BUNDLE/Contents/Resources/runtime"
cp "$RUNTIME_NODE" "$APP_BUNDLE/Contents/Resources/runtime/node"
chmod +x "$APP_BUNDLE/Contents/Resources/runtime/node"
mkdir -p "$APP_BUNDLE/Contents/Resources/runtime/bundle"
cp -R "$DSH_BUNDLE_DIR/node_modules" "$APP_BUNDLE/Contents/Resources/runtime/bundle/"
cp "$DSH_BUNDLE_DIR/package.json" "$APP_BUNDLE/Contents/Resources/runtime/bundle/" 2>/dev/null || true
cp "$DSH_BUNDLE_DIR/package-lock.json" "$APP_BUNDLE/Contents/Resources/runtime/bundle/" 2>/dev/null || true

echo "==> 本地签名 (ad-hoc)"
codesign --force --deep --sign - "$APP_BUNDLE"
codesign --verify --verbose=1 "$APP_BUNDLE" 2>&1 | sed 's/^/    /' || true

echo "==> 制作 DMG"
DMG_STAGE="$BUILD_DIR/dmg"
rm -rf "$DMG_STAGE"; mkdir -p "$DMG_STAGE"
cp -R "$APP_BUNDLE" "$DMG_STAGE/"
ln -s /Applications "$DMG_STAGE/Applications"
if [ -f README.md ]; then cp README.md "$DMG_STAGE/使用说明.md"; fi
hdiutil create -volname "$APP_NAME" -srcfolder "$DMG_STAGE" -ov -format UDZO -quiet "$DMG_NAME"
rm -rf "$DMG_STAGE"

if [ "$DO_INSTALL" = "1" ]; then
    echo "==> 安装到 /Applications"
    rm -rf "/Applications/$APP_NAME.app" || true
    cp -R "$APP_BUNDLE" "/Applications/" && echo "    已安装: /Applications/$APP_NAME.app"
fi

echo ""
echo "构建完成 ✅"
echo "  App : $(pwd)/$APP_BUNDLE"
echo "  DMG : $(pwd)/$DMG_NAME  ($(du -h "$DMG_NAME" | cut -f1))"
echo "安装: 双击 DMG，把「${APP_NAME}」拖进 Applications（首次打开若提示无法验证开发者，右键→打开即可）"
