# Quy trình làm việc

- Việc triển khai KHÔNG tầm thường (tính năng mới, sửa nhiều file, refactor): phiên chính đóng vai kiến trúc sư — tự khảo sát code và viết plan chi tiết, KHÔNG tự code. Sau đó giao từng hạng mục cho subagent `opus-dev` (model Opus) triển khai + build + test, rồi phiên chính review diff thật và nghiệm thu. Quy trình đầy đủ: xem `.claude/commands/lam.md` (lệnh `/lam`).
- Việc vặt (sửa vài dòng, đổi chuỗi, trả lời câu hỏi, đọc code): làm trực tiếp, không giao subagent.

# Build

- .NET/C#: app desktop WPF trong `suite/`, hub web Blazor trong `server/Shopee.Hub.Web/`.
- Build: `dotnet build` project bị ảnh hưởng; sửa nhiều project thì build cả solution.

# Deploy

- **Hub web** (`server/Shopee.Hub.Web`) chạy trên VM Ubuntu — systemd service `shopee-hub`, thư mục `/opt/shopee-hub`, health `curl 127.0.0.1:8088/health` (public: `https://api.schedra.net/health`). SSH từ máy dev đã cài key + alias: **`ssh vps-muinx`** (vào thẳng, không cần mật khẩu; alias trong `~/.ssh/config` của máy dev). Quy trình: `dotnet publish server/Shopee.Hub.Web -c Release -p:PublishProfile=linux-x64` → scp `Shopee.Hub.Web.dll` (+ `wwwroot/app.css` nếu đổi, nhớ bump `app.css?v=N` trong `Components/App.razor`) lên `vps-muinx:/tmp/` → sudo backup bản cũ + `install` vào `/opt/shopee-hub` + `systemctl restart shopee-hub` (bước sudo cần mật khẩu — hỏi user) → check health.
- **App desktop client**: KHÔNG build từng máy — phát hành qua Velopack + GitHub Releases: bump `version.txt` + ghi `CHANGELOG.md` + commit, rồi theo các bước trong `release-suite.cmd` (vpk download → dotnet publish → vpk pack → vpk upload github, token lấy từ `gh auth token`); client tự tải delta, bấm "Cập nhật & khởi động lại" trong Settings → Hiệu năng.
