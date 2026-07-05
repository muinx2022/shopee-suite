#!/usr/bin/env bash
# Publish bản Ubuntu (self-contained linux-x64, ReadyToRun) — song đôi với publish-suite.cmd (Windows).
# App Avalonia chạy cross-platform; engine đã net8.0. Không cần cài .NET trên máy đích.
set -euo pipefail
cd "$(dirname "$0")"

OUT="publish/ShopeeSuite-linux"
dotnet publish suite/Shopee.Suite/Shopee.Suite.csproj \
  -c Release -r linux-x64 --self-contained true -p:PublishReadyToRun=true \
  -o "$OUT"

echo
echo "=== Xong: $OUT/ShopeeSuite ==="

# Playwright: các runner đều ConnectOverCDPAsync (không LaunchAsync) → chỉ cần node driver, KHÔNG cần tải browser
# (không chạy 'playwright install'); browser là Brave hệ thống. Chỉ kiểm node driver linux-x64 có trong output.
if [ -d "$OUT/.playwright/node/linux-x64" ]; then
  echo "OK: .playwright/node/linux-x64 có mặt (Playwright.CreateAsync sẽ chạy)."
else
  echo "CẢNH BÁO: thiếu $OUT/.playwright/node/linux-x64 → Playwright.CreateAsync() sẽ lỗi. Kiểm PackageReference Microsoft.Playwright."
fi

# Nhắc phụ thuộc hệ thống trên Ubuntu (client): Brave cài sẵn (brave-browser) + font emoji cho glyph sidebar.
echo "Nhắc trên Ubuntu: cần 'brave-browser' (apt/snap/flatpak) + 'fonts-noto-color-emoji' để hiện icon."
