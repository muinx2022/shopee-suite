#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════════════════════════
#  Chép extensions/shared/*.js → extensions/shopee-*/shared/ (bản copy checked-in của từng ext).
#  Vì sao phải copy: extension được nạp THẲNG từ thư mục của nó (--load-extension), không import
#  ngược ra ngoài thư mục extension được → mỗi extension giữ một bản copy trong repo.
#
#    extensions/sync-shared.sh            → CHÉP (chạy sau mỗi lần sửa extensions/shared/)
#    extensions/sync-shared.sh --check    → chỉ KIỂM TRA lệch, exit 1 nếu có
#  (Nhánh Windows phát hành bằng release-suite.cmd → nó gọi bản .cmd cạnh file này; bản .sh giữ cho ai
#   chạy trên Linux/WSL và cho nhánh `avalonia`.)
# ═══════════════════════════════════════════════════════════════════════════════════════════
set -euo pipefail
cd "$(dirname "$0")"

MODE="copy"
if [ "${1:-}" = "--check" ]; then MODE="check"; fi

drift=0

# Bảng phân phối: extension nào dùng module nào (thêm module mới thì thêm dòng ở đây).
while read -r ext file; do
  [ -z "${ext:-}" ] && continue
  src="shared/$file"
  dst="$ext/shared/$file"
  if [ "$MODE" = "check" ]; then
    if [ ! -f "$dst" ]; then
      echo "  [thiếu] $dst"
      drift=$((drift + 1))
    elif ! cmp -s "$src" "$dst"; then
      echo "  [lệch]  $dst"
      drift=$((drift + 1))
    fi
  else
    mkdir -p "$ext/shared"
    cp "$src" "$dst"
  fi
done <<'MAP'
shopee-search util.js
shopee-search ws-bridge.js
shopee-search tab-wait.js
shopee-search net-detect.js
shopee-scrape util.js
shopee-scrape tab-wait.js
shopee-scrape net-detect.js
shopee-orders util.js
shopee-orders ws-bridge.js
shopee-orders tab-wait.js
shopee-orders dbg-input.js
MAP

if [ "$MODE" = "check" ]; then
  if [ "$drift" -ne 0 ]; then
    echo "*** LỆCH bản shared: $drift file. Chạy \`extensions/sync-shared.sh\` rồi commit lại. ***"
    exit 1
  fi
  echo "[sync-shared] OK - các bản copy khớp extensions/shared/."
else
  echo "[sync-shared] Đã chép xong shared/ cho 3 extension."
fi
