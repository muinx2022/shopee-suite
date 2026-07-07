# Shopee Hub Web (server độc lập)

Hub điều phối fleet đa máy, chạy như **web app độc lập** trên VM (Hyper-V). Thay cho chế độ "1 máy WPF làm
Hub" cũ. Client (WPF/Avalonia) kết nối **y hệt như trước** — cùng URL `api.schedra.net`, cùng header
`X-Api-Token`, giao thức REST không đổi.

- **UI**: web có đăng nhập (1 admin), điều khiển tại `https://api.schedra.net/` (Fleet, Config, Search, Files, Logs).
- **API client**: giữ nguyên các route `/fleet`, `/files`, `/leases`, `/assignments`, `/search-products`, `/logs`…
- **Lưu trữ**: SQLite `hub.db` + kho file `files/` trong `HUB_DATA_DIR`. Copy thẳng `hub-data/` cũ sang là chạy.

## Vì sao tách khỏi solution chính

Thư mục `server/` có **solution riêng** (`ShopeeHub.sln`), KHÔNG nằm trong `ShopeeSuite.sln`, và lấy code dùng
chung bằng **link file nguồn** (`<Compile Include="..\..\suite\..." Link="..."/>`) thay vì di chuyển — để
không giẫm chân agent đang migrate WPF→Avalonia. File gốc trong `suite\` giữ nguyên.

## Build & chạy thử (local, Windows)

```powershell
dotnet build server\ShopeeHub.sln -c Release
# chạy thử: tạo admin qua env rồi mở http://127.0.0.1:8088
$env:HUB_API_TOKEN="test-token"; $env:HUB_ADMIN_USER="admin"; $env:HUB_ADMIN_PASSWORD="admin123"
dotnet run --project server\Shopee.Hub.Web
```

Lần đầu vào `http://127.0.0.1:8088` → nếu chưa có admin sẽ vào `/setup` (hoặc seed sẵn qua env như trên).

## Publish cho VM

```powershell
# Linux guest (khuyến nghị)
dotnet publish server\Shopee.Hub.Web -c Release -p:PublishProfile=linux-x64
# → server\Shopee.Hub.Web\bin\publish\linux-x64\

# Windows guest
dotnet publish server\Shopee.Hub.Web -c Release -p:PublishProfile=win-x64
```

## Deploy Linux (systemd)

```bash
sudo useradd -r -s /usr/sbin/nologin shopeehub
sudo mkdir -p /opt/shopee-hub /var/lib/shopee-hub
# copy nội dung bin/publish/linux-x64/ vào /opt/shopee-hub/
sudo chmod +x /opt/shopee-hub/Shopee.Hub.Web
sudo chown -R shopeehub:shopeehub /opt/shopee-hub /var/lib/shopee-hub

# đặt token/admin trong unit rồi:
sudo cp deploy/shopee-hub.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now shopee-hub
curl -s http://127.0.0.1:8088/health   # {"ok":true,...}

# cloudflared (dùng LẠI tunnel token cũ; ingress api.schedra.net → localhost:8088 đã có sẵn trên dashboard)
cloudflared service install <TUNNEL_TOKEN>
```

## Deploy Windows guest

```powershell
sc.exe create ShopeeHub binPath= "C:\shopee-hub\Shopee.Hub.Web.exe" start= auto
# đặt biến môi trường máy: HUB_DATA_DIR, HUB_API_TOKEN (+ HUB_ADMIN_* lần đầu), ASPNETCORE_URLS=http://127.0.0.1:8088
sc.exe start ShopeeHub
cloudflared service install <TUNNEL_TOKEN>
```

## Cutover từ hub nhúng cũ (thứ tự QUAN TRỌNG — chống mất dữ liệu)

1. Backup máy hub cũ: zip `shared\` + copy `%AppData%\ShopeeSuite\hub-data\`.
2. Trên máy hub cũ (build cũ, hub còn chạy): bấm **"Đẩy cấu hình lên Hub"** lần cuối → kho file = bản tươi nhất.
   *Bỏ qua bước này = rủi ro lớn nhất*: máy cũ pull về bị rebase WorkbookPath sang bản cũ trên hub.
3. **Tắt WPF hub (tắt luôn cloudflared của nó)** — 2 connector cùng tunnel sẽ chia đôi traffic. Copy `hub-data\`
   sang VM (`HUB_DATA_DIR`), seed `HUB_API_TOKEN` = token cũ trong `hub-server.json`, start service, kiểm tra
   `/health` + `/manifest`.
4. Client KHÔNG phải đổi gì (cùng URL + token), tự kết nối lại trong ~12s.
5. Roll build client mới sau (build cũ vẫn chạy vì giao thức không đổi). Khi tất cả đã lên build mới →
   đặt `Hub:AllowClientConfigPush=false` (chặn client cũ đè config web đã sửa).

**CHỈ seed từ `hub-data` thật** — seed từ export của 1 client sẽ kích hoạt mirror-delete/Id-unification sai.

## Cấu hình

| Nguồn | Khoá | Ý nghĩa |
|---|---|---|
| Env / appsettings | `HUB_DATA_DIR` | thư mục dữ liệu (hub.db, files, dp-keys, backups, exports) |
| Env | `HUB_API_TOKEN` | token client (seed vào bảng settings nếu chưa có) |
| Env | `HUB_ADMIN_USER` / `HUB_ADMIN_PASSWORD` | seed admin lần đầu (hoặc dùng `/setup`) |
| appsettings | `Hub:AllowClientConfigPush` | cho client PUT đè `config/*.json` không (đặt false sau cutover) |
| appsettings | `Kestrel:Endpoints:Http:Url` | địa chỉ nghe (mặc định `127.0.0.1:8088`, chỉ tunnel vào) |
