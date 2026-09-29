#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RESOURCES="$ROOT/linux/Resources"
mkdir -p "$RESOURCES"

cp "$ROOT/src/Assets/translator.js" "$RESOURCES/translator.js"
cp "$ROOT/translation/translation-pack.json" "$RESOURCES/translation-pack.json"
cp "$ROOT/translation/manifest.json" "$RESOURCES/translation-manifest.json"
cp "$ROOT/src/Assets/assistant-icon.png" "$RESOURCES/assistant-icon.png"

cd "$ROOT/linux"

echo "Building standalone Linux binary with PyInstaller..."
python3 -m PyInstaller \
  --noconfirm \
  --onefile \
  --name CCZhAssistant-linux-x64 \
  --add-data "Resources:Resources" \
  cc_zh_assistant.py

DIST_DIR="$ROOT/dist"
mkdir -p "$DIST_DIR"

cp "$ROOT/linux/dist/CCZhAssistant-linux-x64" "$DIST_DIR/CCZhAssistant-linux-x64"
chmod +x "$DIST_DIR/CCZhAssistant-linux-x64"

# Package portable release ZIP
STAGE="$DIST_DIR/linux_stage"
rm -rf "$STAGE"
mkdir -p "$STAGE/Resources"

cp "$DIST_DIR/CCZhAssistant-linux-x64" "$STAGE/CCZhAssistant-linux-x64"
cp "$ROOT/linux/cc_zh_assistant.py" "$STAGE/cc_zh_assistant.py"
cp "$ROOT/linux/install.sh" "$STAGE/install.sh"
chmod +x "$STAGE/install.sh" "$STAGE/CCZhAssistant-linux-x64" "$STAGE/cc_zh_assistant.py"
cp "$RESOURCES/"* "$STAGE/Resources/"
cp "$ROOT/README.md" "$STAGE/README.md"
cp "$ROOT/LICENSE" "$STAGE/LICENSE"

ZIP="$DIST_DIR/CCZhAssistant-linux.zip"
rm -f "$ZIP"
(cd "$STAGE" && zip -r "$ZIP" .)
rm -rf "$STAGE" "$ROOT/linux/build" "$ROOT/linux/dist" "$ROOT/linux/CCZhAssistant-linux-x64.spec"

echo "Successfully built and packaged: $ZIP"
