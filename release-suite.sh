#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════════════════════════
#  Phát hành bản Ubuntu (AppImage) qua Velopack + GitHub Releases (repo PUBLIC → client khỏi token).
#  Chạy trên MÁY DEV LINUX (đóng gói AppImage phải làm trên Linux). Song đôi với release-suite.cmd (win).
#  Bump phiên bản: sửa version.txt rồi chạy script này.
# ═══════════════════════════════════════════════════════════════════════════════════════════
set -euo pipefail
cd "$(dirname "$0")"

VER=$(tr -d '[:space:]' < version.txt)
REPO="https://github.com/muinx2022/shopee-suite"
OUT="Releases"
PUB="publish/ShopeeSuite-linux"

echo "=== Phát hành ShopeeSuite v$VER (linux-x64) ==="

# 1) Kéo bản cũ về để tạo DELTA (repo public → không cần token). Lần đầu chưa có → bỏ qua.
vpk download github --repoUrl "$REPO" --channel linux --outputDir "$OUT" || echo "(chưa có bản trước — sẽ tạo full)"

# 2) Publish self-contained (R2R) — giữ nguyên như publish-suite.sh.
rm -rf "$PUB"
dotnet publish suite/Shopee.Suite/Shopee.Suite.csproj \
  -c Release -r linux-x64 --self-contained true -p:PublishReadyToRun=true -o "$PUB"

# 3) Đóng gói Velopack → .AppImage (+ delta). Icon PNG bắt buộc cho AppImage.
vpk pack \
  --packId ShopeeSuite \
  --packTitle "Shopee Suite" \
  --packAuthors "Shopee Suite" \
  --packVersion "$VER" \
  --packDir "$PUB" \
  --mainExe ShopeeSuite \
  --icon assets/app-icon.png \
  --runtime linux-x64 \
  --channel linux \
  --outputDir "$OUT"

# 4) Đẩy lên GitHub Releases nếu có GITHUB_TOKEN (quyền ghi repo). --merge để gộp cùng tag với bản win.
if [ -n "${GITHUB_TOKEN:-}" ]; then
  echo "=== Đang đẩy lên GitHub Releases ... ==="
  vpk upload github --repoUrl "$REPO" --channel linux --outputDir "$OUT" \
    --publish true --merge true --releaseName "Shopee Suite v$VER" --tag "v$VER" --token "$GITHUB_TOKEN"
  echo "=== ĐÃ PHÁT HÀNH v$VER (linux) lên GitHub ==="
else
  echo
  echo "Đã đóng gói xong vào $OUT/ nhưng CHƯA đẩy lên GitHub (thiếu GITHUB_TOKEN)."
  echo "Chạy tay: vpk upload github --repoUrl $REPO --channel linux --outputDir $OUT --publish true --merge true --releaseName \"Shopee Suite v$VER\" --tag v$VER --token <TOKEN>"
fi
