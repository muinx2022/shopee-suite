# Plan: Webhook notify động (tên sự kiện do user đặt)

- **Ngày:** 2026-07-29
- **Trạng thái:** đang làm
- **Người lập:** Auto · **Người thực thi:** Auto (khi user yêu cầu)
- **Sửa:** thay plan cũ “2 ô cứng đơn mới / cảnh báo” — user muốn UI động kiểu Hub.

## 1. Bối cảnh & mục tiêu

User muốn cấu hình dạng **danh sách động**, mỗi dòng:

| Tên sự kiện (ô text do user nhập) | Webhook (ô URL) |
|-----------------------------------|-----------------|
| cảnh báo                          | `https://hooks.slack.com/...` |
| đơn mới                           | `https://hooks.slack.com/...` |
| đơn trả                           | `https://hooks.slack.com/...` |

- **Không** hard-code nhãn trên UI (“cảnh báo”, “đơn mới”, “đơn trả”…).
- Thêm / xóa bao nhiêu dòng cũng được.
- Cùng một tên có thể lặp nhiều dòng → gửi tới nhiều channel.

**Cách ghép tên ↔ tin app gửi (bắt buộc có):**

App/Hub khi bắn tin gọi `SendTheoTen("đơn mới", text)` / `SendTheoTen("cảnh báo", text)`.  
Chỉ các dòng có **tên khớp** (sau chuẩn hoá) mới nhận tin.

- Chuẩn hoá khớp: `Trim` + không phân biệt hoa thường + **bỏ dấu tiếng Việt**  
  → `"Đơn Mới"`, `"don moi"`, `"đơn mới"` đều khớp topic `"đơn mới"`.
- Code giữ **hằng topic** (không phải dropdown UI), hiện có:
  - `"đơn mới"` — client sync có đơn insert; Hub khi push `Added > 0`.
  - `"cảnh báo"` — client khi dừng vì không đặt được địa chỉ lấy hàng.
- `"đơn trả"`: user **được phép** thêm dòng sẵn; **chưa có chỗ gửi** trong code → chưa bắn tin cho tới khi có tính năng sau (ngoài phạm vi plan này). Hint UI liệt kê topic đang hoạt động.

**Nơi cấu hình:**

1. **Hub** `/settings` — thay textarea URL-only bằng bảng động tên + webhook (nguồn chính user mô tả).
2. **Client** Settings — cùng mô hình (cảnh báo chỉ chạy trên client; đơn mới local cũng dùng list này).

**Kênh:** Slack / Discord / Telegram như `OrderNotifyService` hiện có.

## 2. Phạm vi

- **Làm:**
  - Model + serialize JSON danh sách `{ name, url }`.
  - Hub Settings: UI thêm/xóa dòng, lưu, migrate từ `notify.webhooks` cũ.
  - Client Settings + SettingsRepository: cùng mô hình, migrate từ `notify_webhook_url`.
  - `OrderNotifyService.SendTheoTen` / lọc URL theo tên; AccountSession + Hub `FireNotifyNewOrders` dùng topic.
  - Test parse/match/migrate; hint topic đang hỗ trợ.
- **Không làm:**
  - Implement sự kiện `"đơn trả"` (chỉ cho phép cấu hình sẵn).
  - Đồng bộ list webhook Hub → client (mỗi bên settings riêng).
  - Đổi nội dung tin nhắn.
  - Release / bump version (trừ khi user yêu cầu sau).

## 3. Các bước thực hiện

### 3.1. Model + helper khớp tên (Core, dùng chung Hub + client)

File mới gợi ý: `orders/XuLyDonShopee.Core/Services/NotifyWebhookEntry.cs` (hoặc nested trong `OrderNotifyService`).

```csharp
public sealed class NotifyWebhookEntry
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

// Hằng topic (code emit) — KHÔNG hiện dropdown cứng trên UI
public static class NotifyTopics
{
    public const string DonMoi = "đơn mới";
    public const string CanhBao = "cảnh báo";
    // DonTra = "đơn trả"; // thêm khi có tính năng
}
```

- `ChuanHoaTen(string?)` → trim, lower, bỏ dấu (dùng cùng cách normalize đã có trong repo nếu có; không thì helper nhỏ).
- `TenKhop(a, b)` → `ChuanHoaTen(a) == ChuanHoaTen(b)`.
- `ParseDanhSach(string? json) → List<NotifyWebhookEntry>`
- `SerializeDanhSach(IEnumerable<NotifyWebhookEntry>) → string`
- `LocUrlTheoTen(entries, topic) → IReadOnlyList<string>` — bỏ dòng name/url trống; URL phải qua `KiemTraUrl` khi **lưu**, lúc gửi bỏ qua URL lỗi + log.
- `SendTheoTenAsync(entries | urls đã lọc, text, log, ct)` — gửi lần lượt, lỗi 1 URL không chặn URL sau; `true` nếu ≥1 thành công.

### 3.2. Hub — Settings động + migrate

Files:

- `server/Shopee.Hub.Web/Components/Pages/Settings.razor`
- `server/Shopee.Hub.Web/Services/HubOptions.cs` (key mới hoặc tái dùng)

- Key lưu: `notify.webhook_routes` = JSON mảng `[{ "name", "url" }, ...]`.
- Migrate đọc: nếu key mới trống và `notify.webhooks` (multiline URL cũ) còn dữ liệu → mỗi URL cũ thành 1 dòng `name = "đơn mới"` (vì Hub trước đây chỉ báo đơn mới).
- UI:
  - Tiêu đề kiểu “Thông báo webhook (Slack / Discord / Telegram)”.
  - Hint: tên do bạn đặt; app gửi theo topic đang hỗ trợ: **đơn mới** (Hub + client), **cảnh báo** (chỉ client). Có thể thêm dòng tên khác (vd đơn trả) để dùng sau.
  - Mỗi dòng: `<input name>` + `<input url>` + nút xóa; nút **Thêm dòng**.
  - Lưu: name trim không bắt buộc unique; URL không trống → `KiemTraUrl`; name trống + url trống = bỏ dòng; name có mà url trống (hoặc ngược) → lỗi.
- `ClientApiEndpoints.FireNotifyNewOrders`: lấy routes, `LocUrlTheoTen(..., NotifyTopics.DonMoi)`, gửi như hiện tại (fire-and-forget).

### 3.3. Client — SettingsRepository + UI

Files:

- `orders/XuLyDonShopee.Core/Data/SettingsRepository.cs`
- `orders/XuLyDonShopee.App/ViewModels/SettingsViewModel.cs`
- `orders/XuLyDonShopee.App/Views/SettingsView.axaml`
- `orders/XuLyDonShopee.App/Services/AccountSession.cs`

- Key: `notify_webhook_routes` (JSON).
- Migrate: nếu mới trống và `notify_webhook_url` cũ có giá trị → **2 dòng** cùng URL: `đơn mới` + `cảnh báo` (giữ hành vi cũ: một URL nhận cả hai).
- UI Avalonia: `ItemsControl` / danh sách bind `ObservableCollection` row (Name, Url) + Thêm / Xóa + 1 nút Lưu; cùng rule validate Hub.
- `StartNotifyInBackground` → topic `DonMoi`.
- `StartCanhBaoDiaChiInBackground` → topic `CanhBao`; log nhắc nếu không có dòng khớp.

### 3.4. Tests

- `ChuanHoaTen` / `TenKhop`: hoa thường, có dấu/không dấu.
- `LocUrlTheoTen`: khớp đúng; không khớp; nhiều dòng cùng tên.
- Migrate Hub-style (URLs → name đơn mới) và client-style (1 URL → 2 topic).
- Serialize round-trip JSON.
- (Nhẹ) list rỗng / không khớp → không ném.

`dotnet test orders/XuLyDonShopee.Tests` + build Hub nếu đụng.

## 4. Tiêu chí nghiệm thu

- [ ] Hub Settings: thêm 3 dòng tên khác nhau + webhook, xóa 1 dòng, lưu/reload đúng JSON.
- [ ] Hub push đơn mới chỉ gửi tới dòng tên khớp “đơn mới” (không gửi “cảnh báo”).
- [ ] Client: đơn mới → chỉ webhook dòng “đơn mới”; cảnh báo địa chỉ → chỉ “cảnh báo”.
- [ ] Hai dòng cùng tên “đơn mới” → gửi cả hai URL.
- [ ] User cũ: migrate Hub (mọi URL cũ = đơn mới); client (URL cũ = đơn mới + cảnh báo).
- [ ] Dòng tên “đơn trả” lưu được nhưng chưa có tin (chưa có emitter) — không lỗi.
- [ ] Test + build pass.

## 5. Rủi ro & lưu ý

- User gõ lệch tên (vd “canh bao app”) → không nhận tin; hint phải nêu đúng topic đang hỗ trợ và quy tắc bỏ dấu.
- Không log/commit webhook URL thật.
- Sau khi user xác nhận plan này → mới thực thi (plan trước 2 ô cứng **bỏ**).

---

## Báo cáo thực thi (điền sau khi xong)

_(trống)_
