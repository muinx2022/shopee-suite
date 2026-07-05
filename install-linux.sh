#!/usr/bin/env bash
# Deploy bản Ubuntu theo đúng nếp Windows (close → publish → copy → mở lại):
#   dừng app đang chạy → publish self-contained → rsync vào thư mục cài → mở lại.
# Dùng:  ./install-linux.sh [thư-mục-cài]        (mặc định ~/apps/ShopeeSuite)
set -euo pipefail
cd "$(dirname "$0")"
INSTALL_DIR="${1:-$HOME/apps/ShopeeSuite}"

echo ">> Dừng ShopeeSuite đang chạy (nếu có)…"
pkill -f 'ShopeeSuite' 2>/dev/null || true
sleep 1

echo ">> Publish (self-contained linux-x64, R2R)…"
./publish-suite.sh

echo ">> Đồng bộ vào $INSTALL_DIR (rsync -a --delete)…"
mkdir -p "$INSTALL_DIR"
rsync -a --delete publish/ShopeeSuite-linux/ "$INSTALL_DIR/"
chmod +x "$INSTALL_DIR/ShopeeSuite"

echo ">> Mở lại app…"
( cd "$INSTALL_DIR" && nohup ./ShopeeSuite >/dev/null 2>&1 & )
echo "=== Xong. App ở $INSTALL_DIR/ShopeeSuite ==="
