# Plan: 3 webhook cố định — đơn mới / lỗi app / đơn trả

- **Ngày:** 2026-07-29
- **Trạng thái:** đang làm
- **Người lập:** Auto · **Người thực thi:** Auto (khi user yêu cầu)
- **Sửa lần 4 (chốt):** không UI động, không dropdown nhiều dòng. **Fix 3 ô webhook** gắn 3 sự kiện. Sau này thêm sự kiện = thêm ô + code.

## 1. Bối cảnh & mục tiêu

User thống nhất cấu hình đơn giản trên Hub + client:

| Ô Settings (nhãn cố định) | Sự kiện | Emitter hiện tại |
|---------------------------|---------|------------------|
| Webhook **có đơn mới** | Sync/push có đơn mới → channel kiểu `#shopee-suite` | Client + Hub — **đã có** |
| Webhook **lỗi app** | Lỗi vận hành app; nay: không đặt được địa chỉ lấy hàng; sau gom thêm lỗi khác vào cùng ô này | Client — **đã có** (địa chỉ) |
| Webhook **có đơn trả hàng** | Khi check flow đơn hàng phát hiện đơn trả | **Chưa emit** — chỉ ô cấu hình + lưu; chỗ gọi gửi để stub/TODO rõ rang hoặc bỏ trống cho plan sau |

Mỗi ô **một URL** (Slack/Discord/Telegram như `OrderNotifyService`). Trống = tắt sự kiện đó.

**Không làm** danh sách động / chọn event tự do.

## 2. Phạm vi

- **Làm:**
  - 3 key settings (client + Hub).
  - UI 3 ô nhập URL + validate + lưu.
  - Gửi đơn mới → ô 1; gửi lỗi địa chỉ → ô 2.
  - Migrate URL cũ → ô đơn mới **và** ô lỗi app (giữ hành vi một webhook nhận cả hai).
  - Ô đơn trả: lưu được; **chưa** nối emitter (flow trả hàng chưa có trong phạm vi này — chỉ chuẩn bị setting).
  - Test migrate + đọc 3 URL; build.
- **Không làm:**
  - Implement phát hiện / gửi tin đơn trả hàng.
  - Gom thêm loại lỗi mới vào ô “lỗi app” (ngoài địa chỉ) — chỉ thiết kế tên ô cho tương lai.
  - UI động nhiều dòng; release trừ khi user yêu cầu.

## 3. Các bước thực hiện

### 3.1. Settings keys

**Client** `SettingsRepository.cs`:

- `notify_webhook_url_don_moi`
- `notify_webhook_url_loi_app`
- `notify_webhook_url_don_tra`
- Get/Set từng cái (trim; trống → xóa key / null).
- **Migrate lazy:** nếu cả 3 key mới trống và `notify_webhook_url` cũ còn giá trị → `GetDonMoi` và `GetLoiApp` trả URL cũ; `GetDonTra` vẫn null. Khi user Lưu từ UI mới → ghi key mới, xóa key cũ.

**Hub** `HubOptions` / settings:

- `notify.webhook_don_moi`
- `notify.webhook_loi_app` (Hub hiện không emit lỗi app — vẫn cho cấu hình thống nhất UI; hoặc **chỉ hiện ô đơn mới + đơn trả trên Hub**, ô lỗi app chỉ client).
- **Chốt UI Hub:** 3 ô giống client cho đồng bộ tư duy; Hub chỉ **dùng** ô đơn mới khi `FireNotifyNewOrders`. Ô lỗi app / đơn trả trên Hub chưa có emitter → lưu thôi.
- Migrate: `notify.webhooks` multiline cũ → lấy **dòng đầu** làm `don_moi` (các dòng sau: hoặc bỏ, hoặc cũng coi là don_moi chỉ lấy dòng 1 — **chốt: dòng đầu → don_moi**; nếu nhiều dòng cũ, dòng 2+ bỏ kèm hint “bản mới mỗi sự kiện 1 URL”).

### 3.2. OrderNotifyService

- Không bắt buộc API mới nếu vẫn `SendAsync(url, …)`.
- Call site truyền đúng URL theo sự kiện.

### 3.3. Call sites

- `AccountSession.StartNotifyInBackground` → `Get…DonMoi()`.
- `AccountSession.StartCanhBaoDiaChiInBackground` → `Get…LoiApp()`; đổi log hint sang “Cài đặt → webhook lỗi app”.
- Hub `FireNotifyNewOrders` → setting `notify.webhook_don_moi` (một URL).
- Đơn trả: **không** gọi gửi trong plan này.

### 3.4. UI

**Client** `SettingsView.axaml` + ViewModel:

- Card thông báo: 3 TextBox + 1 nút Lưu.
- Nhãn:
  - Có đơn mới
  - Lỗi app (hiện: không đặt được địa chỉ lấy hàng; sau thêm lỗi khác cùng kênh)
  - Có đơn trả hàng (chưa gửi — chỉ cấu hình)
- Validate từng ô không trống bằng `KiemTraUrl`.

**Hub** `Settings.razor`: thay textarea multiline bằng 3 input tương ứng + lưu.

### 3.5. Tests

- Migrate client: URL cũ → don_moi + loi_app.
- Migrate Hub: dòng đầu webhooks cũ → don_moi.
- Set/Get 3 key độc lập.
- (Không bắt buộc test HTTP.)

`dotnet test orders/XuLyDonShopee.Tests` + build App/Core/Hub.

## 4. Tiêu chí nghiệm thu

- [ ] Settings client + Hub hiện đúng 3 ô; lưu/reload độc lập.
- [ ] Đơn mới chỉ dùng URL ô “có đơn mới”.
- [ ] Lỗi địa chỉ chỉ dùng URL ô “lỗi app”.
- [ ] Ô “đơn trả” lưu được; chưa có tin gửi tự động.
- [ ] User cũ một webhook: vẫn nhận cả đơn mới + lỗi địa chỉ cho tới khi cấu hình lại.
- [ ] Test + build pass.

## 5. Rủi ro & lưu ý

- Hub ô “lỗi app” có thể gây nhầm (Hub không gửi) — hint UI: “Client dùng ô này khi lỗi app; Hub chưa gửi loại tin này.”
- Thêm sự kiện sau = thêm key + ô + call site.
- Không commit webhook URL thật.

---

## Báo cáo thực thi (điền sau khi xong)

_(trống)_
