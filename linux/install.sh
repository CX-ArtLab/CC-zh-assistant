#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_NAME="CCZhAssistant-linux-x64"

echo "=========================================================="
echo "    Claude Code 桌面版中文助手 (Linux / Ubuntu 安装助手)"
echo "=========================================================="

if [ -f "$SCRIPT_DIR/$BIN_NAME" ]; then
    RUNNER="$SCRIPT_DIR/$BIN_NAME"
    chmod +x "$RUNNER"
elif [ -f "$SCRIPT_DIR/cc_zh_assistant.py" ]; then
    RUNNER="python3 $SCRIPT_DIR/cc_zh_assistant.py"
else
    echo "[-] 错误：未找到可执行程序或 Python 脚本。"
    exit 1
fi

echo "[*] 检测 Claude Desktop 并应用汉化..."
$RUNNER --apply

# 询问是否安装至应用菜单
if [ -n "$XDG_DATA_HOME" ]; then
    APP_DIR="$XDG_DATA_HOME/applications"
else
    APP_DIR="$HOME/.local/share/applications"
fi

mkdir -p "$APP_DIR"
DESKTOP_FILE="$APP_DIR/claude-desktop-zh-assistant.desktop"

ICON_PATH="$SCRIPT_DIR/Resources/assistant-icon.png"
if [ ! -f "$ICON_PATH" ]; then
    ICON_PATH="$SCRIPT_DIR/assistant-icon.png"
fi

cat <<EOF > "$DESKTOP_FILE"
[Desktop Entry]
Type=Application
Version=1.0
Name=Claude Code 中文助手
Comment=Claude Desktop / Claude Code 界面汉化伴侣
Exec="$SCRIPT_DIR/$BIN_NAME"
Icon=$ICON_PATH
Terminal=false
Categories=Utility;Development;
EOF

chmod +x "$DESKTOP_FILE"
echo "[+] 桌面启动快捷方式已创建: $DESKTOP_FILE"
echo "[+] 汉化安装完成！您可以在应用程序菜单中启动“Claude Code 中文助手”。"
