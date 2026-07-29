# Plan: Webhook theo sự kiện định nghĩa sẵn (dropdown) + nhiều channel

- **Ngày:** 2026-07-29
- **Trạng thái:** đang làm
- **Người lập:** Auto · **Người thực thi:** Auto (khi user yêu cầu)
- **Sửa lần 3:** không dùng tên sự kiện gõ tự do. Event **định nghĩa trong code**; UI chọn event + dán webhook.

## 1. Bối cảnh & mục tiêu

User muốn app **biết gửi cái gì cho ai**:

- Mỗi **sự kiện** trong app được **định nghĩa sẵn** (code + nhãn tiếng Việt trên UI).
- Settings (Hub + client): danh sách động các dòng  
  **`[Chọn sự kiện ▼]` : `[URL webhook]`**  
  Thêm/xóa bao nhiêu dòng cũng được; cùng một sự kiện có thể gắn nhiều webhook (nhiều channel).

Ví dụ cấu hình:

| Sự kiện (dropdown) | Webhook |
|--------------------|---------|
| Có đơn mới | webhook channel `#shopee-suite` |
| Không đặt được địa chỉ lấy hàng | webhook channel `#canh-bao-app` |
| Có đơn trả hàng | webhook channel trả hàng (cấu hình trước; emitter sau) |

Hành vi runtime:

- Sync/push có đơn mới → gửi tới mọi dòng chọn **Có đơn mới**.
- Không set được địa chỉ mặc định → gửi tới mọi dòng chọn **Không đặt được địa chỉ lấy hàng**.

### Danh sách sự kiện (định nghĩa sẵn — mở rộng sau bằng code)

| Id ổn định (lưu JSON) | Nhãn UI | Emitter hiện có |
|----------------------|---------|-----------------|
| `don_moi` | Có đơn mới | Client sync insert; Hub push `Added > 0` |
| `khong_dat_duoc_dia_chi` | Không đặt được địa chỉ lấy hàng | Client `StartCanhBaoDiaChiInBackground` |
| `don_tra_hang` | Có đơn trả hàng | **Chưa có** — vẫn cho chọn/lưu; chưa bắn tin |

Id dùng snake_case ASCII trong JSON (không phụ thuộc chữ có dấu). Nhãn chỉ để hiển thị.

**Kênh gửi:** Slack / Discord / Telegram qua `OrderNotifyService` như hiện tại.

## 2. Phạm vi

- **Làm:**
  - Enum/catalog sự kiện + nhãn; serialize list `{ eventId, url }`.
  - Hub `/settings`: UI dropdown + URL, thêm/xóa dòng; migrate từ `notify.webhooks`.
  - Client Settings + repo: cùng mô hình; migrate từ `notify_webhook_url`.
  - Gửi theo `eventId`; AccountSession + Hub `FireNotifyNewOrders`.
  - Test catalog/migrate/lọc URL; build.
- **Không làm:**
  - Implement emitter thật cho `don_tra_hang`.
  - Đồng bộ cấu hình Hub ↔ client.
  - Đổi nội dung tin nhắn; release/bump version (trừ khi user yêu cầu).

## 3. Các bước thực hiện

### 3.1. Catalog + model (Core)

File gợi ý: `orders/XuLyDonShopee.Core/Services/NotifyWebhookRoutes.cs` (cạnh `OrderNotifyService`).

```csharp
public enum NotifySuKien
{
    DonMoi,                 // don_moi
    KhongDatDuocDiaChi,     // khong_dat_duoc_dia_chi
    DonTraHang,             // don_tra_hang — chưa emit
}

public sealed class NotifyWebhookRoute
{
    public string EventId { get; set; } = ""; // "don_moi" | ...
    public string Url { get; set; } = "";
}

public static class NotifySuKienCatalog
{
    // IdToEnum / EnumToId / NhanUi / TatCaChoDropdown
}
```

- `ParseRoutes(json)` / `SerializeRoutes(list)`.
- `LocUrl(IEnumerable<NotifyWebhookRoute> routes, NotifySuKien suKien) → IReadOnlyList<string>`.
- `OrderNotifyService.SendNhieuAsync(urls, text, log, ct)` — gửi lần lượt; ≥1 OK → `true`.

### 3.2. Hub Settings

Files: `Settings.razor`, `HubOptions.cs` (`SettingKeys.NotifyWebhookRoutes = "notify.webhook_routes"`).

- UI: mỗi dòng `<select>` options từ catalog + `<input>` URL + xóa; nút Thêm dòng; Lưu.
- Hint: chọn sự kiện app định nghĩa sẵn; một sự kiện có thể nhiều dòng/webhook.
- Validate: `EventId` phải thuộc catalog; URL `KiemTraUrl` (bỏ dòng cả hai trống).
- Migrate: `notify.webhooks` multiline cũ → mỗi URL thành route `don_moi`.
- `FireNotifyNewOrders` → `LocUrl(..., DonMoi)` rồi gửi.

### 3.3. Client Settings + gửi

Files: `SettingsRepository.cs`, `SettingsViewModel.cs`, `SettingsView.axaml`, `AccountSession.cs`.

- Key `notify_webhook_routes` JSON.
- Migrate: URL cũ `notify_webhook_url` → **hai** route cùng URL: `don_moi` + `khong_dat_duoc_dia_chi` (giữ hành vi một webhook nhận cả hai).
- UI Avalonia: `ItemsControl` — ComboBox sự kiện + TextBox URL + Thêm/Xóa + Lưu.
- `StartNotifyInBackground` → `DonMoi`.
- `StartCanhBaoDiaChiInBackground` → `KhongDatDuocDiaChi`; không có route khớp → log nhắc cấu hình.

### 3.4. Tests

- Catalog: mọi enum có id + nhãn; id lạ khi parse → bỏ hoặc fail rõ (chốt: **bỏ dòng id lạ** khi đọc + log/test).
- `LocUrl` đúng event; nhiều URL cùng event.
- Migrate Hub + client như trên.
- Round-trip JSON.

`dotnet test orders/XuLyDonShopee.Tests` + build App/Core/Hub bị đụng.

## 4. Tiêu chí nghiệm thu

- [ ] Hub + client: dropdown đủ 3 sự kiện trên; thêm/xóa dòng; lưu/reload đúng.
- [ ] Đơn mới chỉ tới webhook(s) chọn `don_moi`.
- [ ] Cảnh báo địa chỉ chỉ tới webhook(s) chọn `khong_dat_duoc_dia_chi`.
- [ ] Hai dòng cùng `don_moi` → gửi cả hai URL.
- [ ] Chọn `don_tra_hang` + lưu OK; chưa có tin gửi (chưa emitter).
- [ ] Migrate user cũ: Hub URL → `don_moi`; client URL → cả `don_moi` + `khong_dat_duoc_dia_chi`.
- [ ] Test + build pass.

## 5. Rủi ro & lưu ý

- Thêm sự kiện mới sau này = thêm enum + nhãn + một chỗ `Send` — không đổi format JSON.
- Không hard-code nhãn vào JSON; chỉ lưu `eventId`.
- Không log/commit webhook thật.

---

## Báo cáo thực thi (điền sau khi xong)

_(trống)_
