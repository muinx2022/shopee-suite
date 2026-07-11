#!/usr/bin/env bash
# Sao lưu Postgres kho sản phẩm (container shopee-pg) → /var/lib/shopee-hub/backups/pg/, giữ 7 ngày.
# Cài cron root (crontab -e của root):
#   15 3 * * * bash /opt/shopee-hub/pg-backup.sh
set -euo pipefail

BACKUP_DIR=/var/lib/shopee-hub/backups/pg
mkdir -p "$BACKUP_DIR"

docker exec shopee-pg pg_dump -U shopee shopee | gzip > "$BACKUP_DIR/shopee-$(date +%F).sql.gz"

# Xoá bản cũ hơn 7 ngày.
find "$BACKUP_DIR" -name '*.sql.gz' -mtime +7 -delete
