# Quy trình làm việc

Mục này **thay thế** mô hình 2 tầng (Fable plan → Opus thực thi) ở `d:\Projects\CLAUDE.md` — user chốt lại
2026-08-02, nới phần subagent 2026-08-04.

- **Mặc định: phiên chính vừa trao đổi vừa tự thực thi** — khảo sát, viết code, build, test đều do agent đang
  chat làm. Không tự ý giao việc triển khai cho subagent.
- **Subagent: dùng được khi có ích thật** (nghiệm thu/phản biện, audit một mảng riêng, khảo sát diện rộng)
  nhưng phải **tiết chế hạn mức** — mỗi subagent là một request riêng, đốt quota rất nhanh. Không thả bầy
  song song, không worktree-per-agent.
- **Xong việc thì nên gọi `nghiem-thu` phản biện đối kháng.** Đợt 2026-08-04 chính nó chặn được hai bản vá
  hỏng đã build xanh + test xanh: một bản làm banner kẹt vĩnh viễn, một bản làm Hub từ chối thao tác của user.
  Test xanh KHÔNG thay được lượt phản biện này.
- **Giao Opus triển khai: chỉ khi user gõ `/lam`** — khi đó phiên chính nhận việc + viết plan, giao `opus-dev`
  (Opus, effort high) code, rồi `nghiem-thu` phản biện. Xem `.claude/commands/lam.md`.
- Việc lớn vẫn viết plan trong `plans/` trước khi làm (để bám tiến độ và làm căn cứ chấm nghiệm thu).

# Build

- .NET/C#: app desktop WPF trong `suite/`, hub web Blazor trong `server/Shopee.Hub.Web/`.
- Build: `dotnet build` project bị ảnh hưởng; sửa nhiều project thì build cả solution.

# Deploy

- **Hub web** (`server/Shopee.Hub.Web`) chạy trên VM Ubuntu — systemd service `shopee-hub`, thư mục `/opt/shopee-hub`, health `curl 127.0.0.1:8088/health` (public: `https://api.schedra.net/health`). SSH từ máy dev đã cài key + alias: **`ssh vps-muinx`** (vào thẳng, không cần mật khẩu; alias trong `~/.ssh/config` của máy dev). Quy trình: `dotnet publish server/Shopee.Hub.Web -c Release -p:PublishProfile=linux-x64` → scp `Shopee.Hub.Web.dll` (+ `wwwroot/app.css` nếu đổi, nhớ bump `app.css?v=N` trong `Components/App.razor`) lên `vps-muinx:/tmp/` → sudo backup bản cũ + `install` vào `/opt/shopee-hub` + `systemctl restart shopee-hub` (bước sudo cần mật khẩu — hỏi user) → check health.
- **App desktop client**: KHÔNG build từng máy — phát hành qua Velopack + GitHub Releases: bump `version.txt` + ghi `CHANGELOG.md` + commit, rồi theo các bước trong `release-suite.cmd` (vpk download → dotnet publish → vpk pack → vpk upload github, token lấy từ `gh auth token`); client tự tải delta, bấm "Cập nhật & khởi động lại" trong Settings → Phiên bản & cập nhật.
