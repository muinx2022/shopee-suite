# Plan: Tách webhook đơn mới và cảnh báo (nhiều channel)

- **Ngày:** 2026-07-29
- **Trạng thái:** đang làm
- **Người lập:** Auto · **Người thực thi:** Auto (khi user yêu cầu)

## 1. Bối cảnh & mục tiêu

Hiện client chỉ có **1** setting `notify_webhook_url`: vừa báo **đơn mới**, vừa báo **cảnh báo sự cố** (vd. không đặt được địa chỉ lấy hàng). Người dùng muốn:

- Đơn mới → channel Slack `#shopee-suite` (có thể thêm channel khác sau).
- Sự cố / cảnh báo → channel `#canh-bao-app` (có thể thêm channel khác sau).

**Quyết định UX (chốt):** 2 ô cấu hình riêng trên Settings client:

1. **Webhook đơn mới** — mỗi dòng 1 URL.
2. **Webhook cảnh báo sự cố** — mỗi dòng 1 URL.

Muốn thêm bao nhiêu channel cũng được: thêm dòng URL (mỗi channel Slack = 1 Incoming Webhook riêng). Không làm UI “danh sách gắn tag” phức tạp.

**Kênh gửi:** giữ nhận diện Slack / Discord / Telegram như `OrderNotifyService` hiện có (cùng `SendAsync`). Ưu tiên kiểm thử Slack; không bỏ Discord/Telegram.

**Hub:** đã có textarea nhiều dòng cho **đơn mới** (`notify.webhooks`) — **không đổi** trong plan này. Cảnh báo địa chỉ chỉ chạy trên **client**, không qua Hub.

## 2. Phạm vi

- **Làm:**
  - Settings client: 2 textarea (đơn mới / cảnh báo), validate từng dòng, lưu nhiều URL.
  - Repository: 2 key mới + migrate từ key cũ.
  - Gửi đơn mới → tất cả URL ô “đơn mới”; gửi cảnh báo → tất cả URL ô “cảnh báo”.
  - Test parse/migrate/validate; cập nhật hint UI.
- **Không làm:**
  - Đổi Hub Settings / `notify.webhooks`.
  - UI drag-drop / bảng động từng dòng có checkbox.
  - Thêm loại sự kiện notify mới ngoài đơn mới + cảnh báo địa chỉ hiện có.
  - Đổi nội dung tin nhắn Slack.

## 3. Các bước thực hiện

### 3.1. SettingsRepository — 2 danh sách URL + migrate

File: `orders/XuLyDonShopee.Core/Data/SettingsRepository.cs`

- Thêm key:
  - `notify_webhook_urls_don_moi` — chuỗi nhiều dòng (mỗi dòng 1 URL).
  - `notify_webhook_urls_canh_bao` — tương tự.
- Helper thuần (có thể để static trên repo hoặc `OrderNotifyService`):
  - `ParseWebhookLines(string? raw) → IReadOnlyList<string>`: tách `\r\n`/`\n`, trim, bỏ dòng trống, **giữ thứ tự**, không dedupe bắt buộc (dedupe optional theo URL trim).
- API mới:
  - `GetNotifyWebhookUrlsDonMoi()` / `SetNotifyWebhookUrlsDonMoi(string? multiline)`
  - `GetNotifyWebhookUrlsCanhBao()` / `SetNotifyWebhookUrlsCanhBao(string? multiline)`
  - Khi **get**: nếu key mới trống/null **và** key cũ `notify_webhook_url` còn giá trị → coi URL cũ thuộc **cả hai** danh sách (đọc runtime, không bắt buộc ghi lại ngay — hoặc one-shot migrate khi get lần đầu: ghi 2 key mới + có thể xóa key cũ sau khi save từ UI). **Chốt:** khi `Get*DonMoi` / `Get*CanhBao` thấy key mới trống và key cũ có URL → trả list 1 phần tử = URL cũ (lazy). Khi user **Lưu** từ UI mới → ghi key mới; khi cả hai ô lưu xong có thể xóa `notify_webhook_url` để tránh nhầm.
- Giữ `GetNotifyWebhookUrl` / `SetNotifyWebhookUrl` tạm thời **obsolete** hoặc chuyển internal: mọi caller App đổi sang API mới. Không để code production còn gọi single-URL.

### 3.2. OrderNotifyService — gửi nhiều URL

File: `orders/XuLyDonShopee.Core/Services/OrderNotifyService.cs`

- Thêm `SendNhieuAsync(IReadOnlyList<string> urls, string text, Action<string>? log, CancellationToken ct)`:
  - `urls` rỗng → return `false` (hoặc no-op + log ngắn).
  - Với mỗi URL: gọi lại logic `SendAsync` hiện có; lỗi 1 URL **không** chặn URL sau; log từng URL.
  - Trả `true` nếu **ít nhất 1** URL gửi thành công (giữ tinh thần “đã báo” cho log AccountSession).
- Cập nhật XML doc: không còn “chỉ một URL”.

### 3.3. AccountSession — đọc đúng danh sách

File: `orders/XuLyDonShopee.App/Services/AccountSession.cs`

- `StartNotifyInBackground`: lấy `GetNotifyWebhookUrlsDonMoi()`; rỗng → return; else `SendNhieuAsync`.
- `StartCanhBaoDiaChiInBackground`: lấy `GetNotifyWebhookUrlsCanhBao()`; rỗng → log nhắc cấu hình ô cảnh báo (đổi text hint cho đúng UI mới); else `SendNhieuAsync` với `TaoTinNhanLoiDiaChi`.

### 3.4. Settings UI + ViewModel

Files:

- `orders/XuLyDonShopee.App/ViewModels/SettingsViewModel.cs`
- `orders/XuLyDonShopee.App/Views/SettingsView.axaml`

- Đổi property:
  - `NotifyWebhookUrl` → `NotifyWebhookUrlsDonMoi` + `NotifyWebhookUrlsCanhBao` (string multiline bind TextBox).
- Card “THÔNG BÁO…”:
  - Label + TextBox (AcceptsReturn, MinHeight ~80) **Webhook đơn mới**.
  - Label + TextBox tương tự **Webhook cảnh báo sự cố**.
  - Hint: “Mỗi dòng 1 URL Slack/Discord/Telegram. Đơn mới và cảnh báo tách channel riêng.”
- `SaveNotifyUrl` → validate **từng dòng không trống** bằng `OrderNotifyService.KiemTraUrl`; dòng lỗi báo số dòng; lưu cả hai ô (một nút Lưu cho cả card, hoặc 2 nút — **chốt: 1 nút Lưu** cho cả card thông báo).
- Thông điệp lưu: tóm tắt số URL mỗi loại + kênh nhận diện dòng đầu (hoặc “N URL đơn mới, M URL cảnh báo”).

### 3.5. Docs / hint

- Nếu có `docs/thong-bao-webhook-huong-dan.md` (hint UI đang trỏ tới): cập nhật 2 ô + ví dụ 2 channel Slack.
- Không đụng CHANGELOG/`version.txt` trừ khi user yêu cầu release.

### 3.6. Tests

Files: `orders/XuLyDonShopee.Tests/` (mở rộng `OrderNotifyServiceTests` và/hoặc test SettingsRepository nếu đã có pattern SQLite temp).

- `ParseWebhookLines`: trống; 1 URL; nhiều dòng; dòng trắng giữa; CRLF.
- Lazy migrate: DB chỉ có `notify_webhook_url` → cả `GetDonMoi` và `GetCanhBao` trả đúng URL đó.
- Sau `Set` key mới: không còn phụ thuộc key cũ.
- `KiemTraUrl` vẫn dùng từng dòng (không đổi hành vi).
- (Tuỳ chọn nhẹ) `SendNhieuAsync` với list rỗng không ném.

Chạy: `dotnet test orders/XuLyDonShopee.Tests` (hoặc filter tên test liên quan) + `dotnet build` project App/Core bị ảnh hưởng.

## 4. Tiêu chí nghiệm thu

- [ ] Settings hiện 2 ô multiline; lưu 2 URL khác nhau (1 đơn mới, 1 cảnh báo) thành công.
- [ ] Sync có đơn mới chỉ POST tới URL(s) ô đơn mới (không gửi ô cảnh báo).
- [ ] Cảnh báo địa chỉ chỉ POST tới URL(s) ô cảnh báo.
- [ ] Mỗi ô có ≥2 dòng URL → gửi đủ tất cả URL của ô đó.
- [ ] User cũ chỉ có `notify_webhook_url`: trước khi mở Settings lưu lại, đơn mới **và** cảnh báo vẫn gửi đúng URL cũ (lazy migrate).
- [ ] URL dòng sai → không lưu, hiện lỗi rõ số dòng.
- [ ] `dotnet test` phần notify/settings liên quan pass; build OK.
- [ ] Hub webhook đơn mới không bị phá (không sửa, hoặc smoke đọc Settings Hub không đổi).

## 5. Rủi ro & lưu ý

- Lazy migrate “URL cũ → cả hai loại” tránh mất cảnh báo sau update; sau khi user cấu hình tách channel và Lưu, nên xóa key cũ để không “hồi sinh” URL cũ nếu xóa hết dòng ở UI.
- Không commit / log nguyên webhook URL trong test output nếu có fixture thật.
- Không đụng `scratchpad/`.

---

## Báo cáo thực thi (điền sau khi xong)

_(trống)_
