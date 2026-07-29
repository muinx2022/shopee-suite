# Plan: 3 kênh notify — chặn gửi trùng, bỏ hỏng im lặng, lùi về webhook cũ

- **Ngày:** 2026-07-29
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh — soi trên hub production

Việc "Notify do Hub quyết" (`plans/2026-07-29-tach-webhook-don-moi-va-canh-bao.md`) đã xong và deploy. Soi lại
thấy hạ tầng đủ nhưng **2 trong 3 kênh đang chết âm thầm**, cộng một lỗ gửi trùng.

### Đã kiểm trên hub thật (`api.schedra.net`, dll deploy 29/07 04:31)

```
POST /api/orders/app-alert  →  HTTP 401  (401 chứ không phải 404 ⇒ route CÓ, chỉ đòi token)

logs:  orders/push shop=deilca.store: +1 mới, 0 cập nhật     ⇒ đường "đơn mới" CÓ chạy thật
       app-alert                                              ⇒ (chưa có lần nào)

settings:  notify.webhooks            = ĐÃ ĐIỀN (81 ký tự)   ← ô LEGACY
           notify.webhook_don_moi     = (không tồn tại)
           notify.webhook_loi_app     = (không tồn tại)
           notify.webhook_don_tra     = (không tồn tại)
```

### Lỗi 1 — hai kênh chết âm thầm vì ô webhook trống

Chỉ đường "đơn mới" có lối lùi về legacy:

```csharp
ResolveWebhooksDonMoi(db)   // key mới trống → dùng notify.webhooks (nhiều dòng)   ✔ vẫn chạy
FireNotifyLoiApp           // chỉ đọc NotifyWebhookLoiApp; trống → return, KHÔNG log   ✖
FireNotifyDonTra           // chỉ đọc NotifyWebhookDonTra;  trống → return, KHÔNG log   ✖
```

⇒ Sự kiện có xảy ra, hub im lặng bỏ qua, **không để lại dấu vết nào**. Nhìn từ ngoài tưởng client không gửi.
Đây đúng điều cấm trong ghi nhớ `external-service-config-sync-hub-client`: **cấm hỏng im lặng**.

### Lỗi 2 — client gửi TRÙNG tin đơn trả

`AccountSession.StartNotifyInBackground` (đơn mới) có chốt chặn:

```csharp
// Tránh trùng tin với Hub: khi hook push đã rót, Hub bắn tin sau orders/push.
if (_services.PushOrdersToHub is not null) return;
```

Nhưng `StartNotifyDonTraInBackground` (đơn trả) **KHÔNG có** — chỉ kiểm URL rỗng. Máy đã nối Hub mà ô "đơn trả"
ở Cài đặt client có URL ⇒ **cả hai cùng gửi, người trực nhận hai tin**.

Hiện chưa nổ vì `GetNotifyWebhookUrlDonTra()` dùng `dungLegacy: false` (không thừa hưởng URL cũ) và webhook không
nằm trong khối cấu hình đồng bộ hub→client. Nhưng chỉ cần điền ô đó ở một máy là dính.

## 2. Phạm vi

**Làm:**
- Client: thêm chốt chặn Hub cho đường đơn trả (đối xứng đường đơn mới).
- Hub: `lỗi app` + `đơn trả` **lùi về `notify.webhooks`** khi ô riêng trống (như đơn mới đang làm).
- Hub: có sự kiện mà **không giải ra được URL nào** → **ghi log cảnh báo**, không `return` lặng lẽ.

**Không làm:**
- KHÔNG đổi hành vi đường đơn mới (đang chạy đúng trên production).
- KHÔNG đổi cách client báo đơn mới / đơn trả (suy từ `orders/push`) sang event tường minh — hub so với DB nên
  chống trùng miễn phí; chuyển sang event là đẩy việc chống trùng về client, đẩy lại sau lỗi mạng sẽ sinh tin trùng.
- KHÔNG đụng bước check đơn trả hàng, GSheet, sản phẩm, ước tính.
- KHÔNG commit, KHÔNG deploy, KHÔNG release.

## 3. ⚠ Ba cái bẫy

1. **`notify.webhooks` là NHIỀU DÒNG** (mỗi dòng một URL) — `ResolveWebhooksDonMoi` đã tách sẵn. Khi dùng lại cho
   hai kênh kia phải giữ đúng ngữ nghĩa "gửi tới TẤT CẢ dòng", đừng lấy mỗi dòng đầu.
2. **Đừng biến log cảnh báo thành spam.** Sự kiện đơn mới xảy ra liên tục; mỗi lô push mà bắn một dòng "chưa cấu
   hình webhook" sẽ ngập log hub. Ghi **một dòng cho mỗi lô/sự kiện**, kèm rõ kênh nào thiếu ô nào — đủ để soi,
   không đủ để ngập.
3. **Chốt chặn client chỉ được chặn khi ĐÃ NỐI HUB.** Máy chạy độc lập (`PushOrdersToHub == null`) vẫn phải tự
   gửi, nếu không client độc lập mất hẳn thông báo.

## 4. Các bước

### Bước 1 — Client: chặn gửi trùng đơn trả

`orders/XuLyDonShopee.App/Services/AccountSession.cs`, đầu `StartNotifyDonTraInBackground`: thêm đúng khối chốt
chặn của `StartNotifyInBackground`, kèm comment cùng giọng (nêu rõ Hub bắn tin sau `orders/push` nhờ
`ReturnCodeChangedItems`).

Test: tách hàm quyết định thuần nếu cần, hoặc kiểm qua `AppServices` giả — **đừng bỏ test**:
- [ ] `PushOrdersToHub` khác null → KHÔNG gửi local (dù URL có).
- [ ] `PushOrdersToHub` null + URL có → CÓ gửi (client độc lập, bẫy #3).
- [ ] URL trống → không gửi, không ném.

### Bước 2 — Hub: lùi về legacy cho cả 3 kênh

`server/Shopee.Hub.Web/Api/ClientApiEndpoints.cs`:

- Tổng quát `ResolveWebhooksDonMoi` thành helper dùng chung, vd
  `ResolveWebhooks(db, keyRieng)` → key riêng có giá trị thì trả nó; trống → tách `notify.webhooks` theo dòng.
  **Giữ nguyên hành vi hiện tại của đơn mới** (đang chạy production).
- `FireNotifyLoiApp` và `FireNotifyDonTra` dùng helper đó thay vì `GetSetting` trực tiếp.

### Bước 3 — Hub: hết hỏng im lặng

Trong cả 3 hàm `FireNotify*`: khi **không giải ra URL nào**, gọi `db.AppendLog(...)` mức `warn` với nội dung nói
rõ kênh nào + thiếu key nào, vd:

```
notify: có 2 đơn trả (deilca.store) nhưng CHƯA cấu hình webhook — điền ô "đơn trả" ở Hub → Cài đặt
```

Dùng đúng khuôn `AppendLogRequest` các endpoint đang dùng. **Một dòng cho mỗi lô**, không phải mỗi đơn (bẫy #2).

Cũng ghi log khi **gửi thành công** (mức `info`, một dòng/lô) để soi được kênh nào đang sống — hiện tại gửi được
hay không đều không thấy gì trên hub.

### Bước 4 — Test

`orders/XuLyDonShopee.Tests` cho phần client (Bước 1). Phần hub: nếu có khuôn test sẵn cho
`ClientApiEndpoints`/`HubDatabase` thì thêm; **chưa có khuôn thì đừng dựng hạ tầng test mới** — thay vào đó tách
`ResolveWebhooks` thành hàm thuần static test được và test nó:
- [ ] Key riêng có URL → trả đúng 1 URL đó (không đụng legacy).
- [ ] Key riêng trống + legacy 3 dòng → trả đủ **3** URL, đúng thứ tự (bẫy #1).
- [ ] Cả hai trống → trả rỗng (caller sẽ log cảnh báo).
- [ ] Legacy có dòng trắng / `\r\n` → bỏ dòng rỗng, trim.

## 5. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` + `dotnet build server/Shopee.Hub.Web` sạch, 0 warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` xanh, **không sửa kỳ vọng test cũ nào**.
- [ ] Khẳng định trong báo cáo: hành vi đường **đơn mới** không đổi (nó đang chạy đúng trên production).
- [ ] Chỉ ra chỗ log cảnh báo, và giải thích vì sao không gây spam.

## 6. Rủi ro & lưu ý

- **Cây làm việc đang bẩn nhiều việc.** Chỉ sửa `AccountSession.cs` và `ClientApiEndpoints.cs` (+ file test).
  Không đụng file nào khác — có mạch việc khác chưa commit trong cùng cây.
- Thay đổi phía **hub có hiệu lực khi deploy**; phía **client cần release**. Deploy hub trước.
- Sau khi sửa, người dùng vẫn nên điền ô riêng cho từng kênh nếu muốn tách kênh Slack; lùi-về-legacy chỉ là lưới
  an toàn cho lúc chưa điền.

---

## Báo cáo thực thi (Opus điền sau khi xong)
