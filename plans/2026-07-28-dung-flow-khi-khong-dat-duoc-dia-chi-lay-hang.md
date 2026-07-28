# Plan: Không đặt được địa chỉ lấy hàng → DỪNG vòng + cảnh báo ra kênh ngoài

- **Ngày:** 2026-07-28
- **Trạng thái:** hoàn thành (client chờ release 1.6.9)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh — lỗi thật vừa quan sát được trên production

Log máy `Hoàng DH - TH`, tài khoản `hoangdh200392:muinx`, shop `deilca.store` (28/07):

```
10:11:34  Có 1 đơn Chờ Lấy Hàng — đặt địa chỉ lấy hàng (Thanh Hóa) rồi xử từng đơn...
10:11:49  extension: không mở được modal Sửa Địa chỉ.
10:11:49  Không đặt được địa chỉ lấy hàng — vẫn thử xử đơn (phiếu có thể sai địa chỉ).
10:12:17  Đã chuẩn bị đơn 260728T47N5KSS — lưu phiếu OK, mã vận đơn SPXVN064177931497.
```

App **biết** địa chỉ lấy hàng chưa đặt được, tự cảnh báo *"phiếu có thể sai địa chỉ"*, rồi **vẫn in phiếu và giao
đơn cho đơn vị vận chuyển**. Hậu quả: shipper tới sai chỗ lấy hàng, và không ai biết cho tới lúc đó.

Mã hiện tại (`orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs`, trong `RunShopOrdersAsync` — Phần B):

```csharp
var pickupOk = await _waiter.AwaitAsync(_pickupTcs, TimeSpan.FromSeconds(90), ct);
if (_captchaSeen) { L("PHÁT HIỆN captcha khi đặt địa chỉ lấy hàng."); return (orders.Count, 0); }
if (!pickupOk)
{
    L("Không đặt được địa chỉ lấy hàng — vẫn thử xử đơn (phiếu có thể sai địa chỉ).");   // ← CHỖ SAI
}
```

**Người dùng chốt:** *"nếu không set được địa chỉ đơn hàng thì phải gửi cảnh báo tới kênh bên ngoài (phần cài đặt đã
có, sẽ sử dụng Slack), và dừng flow"* — phạm vi dừng đã chọn: **DỪNG CẢ VÒNG của tài khoản đó** (bỏ luôn các shop
còn lại), vì modal địa chỉ hỏng thường do Shopee đổi giao diện hoặc extension lỗi ⇒ shop sau cũng hỏng, chạy tiếp
chỉ tổ in thêm phiếu sai.

**Kênh ngoài đã có sẵn, dùng lại — đừng xây mới:** `orders/XuLyDonShopee.Core/Services/OrderNotifyService.cs`
- `SendAsync(string webhookUrl, string text, Action<string> log, CancellationToken ct)` — gửi văn bản bất kỳ, tự
  nhận diện **Slack / Discord / Telegram** theo URL, tự chia tin theo giới hạn ký tự, nuốt lỗi mạng (trả `false`).
- URL lấy từ `_services.Settings.GetNotifyWebhookUrl()` (Cài đặt → thông báo đơn mới). Rỗng = người dùng chưa dùng.
- Khuôn đấu dây sẵn có: `AccountSession.StartNotifyInBackground` (fire-and-forget, nuốt exception, không phá luồng).

## 2. Phạm vi

**Làm:**
- Không đặt được địa chỉ lấy hàng → **KHÔNG chạy bước chuẩn bị hàng** cho shop đó, và **dừng cả vòng** của tài khoản.
- Gửi cảnh báo ra webhook đang cấu hình (Slack/Discord/Telegram), nội dung đủ để xử lý ngay.
- Chặn spam: tối đa **1 cảnh báo / tài khoản / 60 phút** (vòng lặp tự chạy lại sau ~30' — không chặn thì mỗi vòng
  một tin).
- Ghi log client rõ ràng, đúng giọng "đã dừng để không in phiếu sai".

**Không làm:**
- KHÔNG đổi hành vi nhánh `_captchaSeen` (đã `return` sẵn, đúng rồi).
- KHÔNG đổi `OrderNotifyService` phần lõi gửi/nhận diện kênh — chỉ thêm hàm dựng nội dung.
- KHÔNG đụng module BigSeller / hub UI.
- KHÔNG commit, KHÔNG deploy, KHÔNG release.

## 3. Các bước thực hiện

### Bước 1 — `OrdersBridgeSession`: dừng thay vì chạy tiếp

Trong `RunShopOrdersAsync` (Phần B), thay nhánh `if (!pickupOk)`:
- KHÔNG chạy vòng `prepareNextOrder`.
- Ghi log: `⛔ Không đặt được địa chỉ lấy hàng ({tỉnh}) — DỪNG vòng, KHÔNG in phiếu (tránh phiếu sai địa chỉ).`
- Báo ngược lên `AccountSession` để (a) gửi cảnh báo, (b) dừng cả vòng.

**Cách báo ngược:** soi cách `RunShopOrdersAsync` / `RunAllShopsAsync` đang trả kết quả và cách `_captchaSeen` được
đẩy lên (`OrdersBridgeRunResult` có field `Captcha` + `Message`). Dùng ĐÚNG khuôn đó — thêm một trạng thái dừng
mới (vd `PickupAddressFailed` + shop đang lỗi), **đừng đẻ kênh sự kiện thứ hai**.

**Kiểm tra revert địa chỉ:** cuối flow shop có bước *"Set địa chỉ lấy hàng về địa chỉ khác"*. Đọc code xác định khi
dừng sớm ở đây thì có cần revert không (địa chỉ có thể đã đổi một phần?). Cần thì vẫn revert trước khi thoát; không
cần thì ghi rõ lý do trong báo cáo. **Đừng đoán.**

### Bước 2 — Dừng cả vòng ở `RunAllShopsAsync`

Gặp trạng thái dừng mới → **thoát vòng lặp shop ngay**, không sang shop kế, trả kết quả kèm lý do. Đúng khuôn nhánh
captcha đang làm (`return new OrdersBridgeRunResult(..., true, "Rơi vào captcha khi đọc/xử đơn.")`).

### Bước 3 — Nội dung cảnh báo (`OrderNotifyService`)

Thêm hàm **static thuần** cạnh `TaoTinNhanDonMoi` (để test không cần mạng):

```csharp
/// <summary>Tin cảnh báo "không đặt được địa chỉ lấy hàng" — vòng đã DỪNG, chưa in phiếu nào cho shop này.</summary>
public static string TaoTinNhanLoiDiaChi(string tenTaiKhoan, string tenShop, string tinh, string tenMay, DateTime luc);
```
Nội dung phải trả lời đủ 4 câu hỏi của người trực: **máy nào · tài khoản/shop nào · lỗi gì · app đã làm gì**. Ví dụ:

```
⛔ KHÔNG ĐẶT ĐƯỢC ĐỊA CHỈ LẤY HÀNG — đã dừng vòng, chưa in phiếu nào.
Máy: muinx-nuc · Tài khoản: hoangdh200392 · Shop: deilca.store
Địa chỉ định đặt: Thanh Hóa · Lúc: 28/07/2026 10:11
Việc cần làm: mở Shopee kiểm tra modal "Sửa Địa chỉ" của shop này rồi chạy lại.
```
(Emoji + xuống dòng dùng đúng cách `TaoTinNhanDonMoi` đang dùng để hiển thị đẹp trên cả 3 kênh.)

### Bước 4 — Gửi + chặn spam (`AccountSession`)

Theo khuôn `StartNotifyInBackground`: đọc URL, rỗng thì im lặng; fire-and-forget; nuốt mọi exception; log 1 dòng
khi gửi được.

**Chặn spam:** nhớ mốc gửi gần nhất **theo tài khoản** (tĩnh, sống qua các vòng trong cùng lần chạy app); trong
vòng 60 phút thì chỉ log, không gửi lại. Ghi rõ trong log khi bị chặn (`đã báo lúc HH:mm, không gửi lại trong 60'`)
— im lặng hoàn toàn sẽ khiến người ta tưởng hết lỗi.

### Bước 5 — Test

Đặt ở `orders/XuLyDonShopee.Tests`:
- `TaoTinNhanLoiDiaChi`: có đủ tên máy / tài khoản / shop / tỉnh / mốc thời gian; không rỗng; không ném với tham số rỗng.
- Quy tắc chặn spam (tách hàm thuần dạng `CoNenGuiCanhBao(mocGanNhat, bayGio, ngưỡng)`): lần đầu → gửi; trong 60' →
  không; sau 60' → gửi lại.
- Nhánh quyết định: `pickupOk == false` → **không** gọi bước chuẩn bị (dùng test double/giả lập cầu nối nếu
  `OrdersBridgeSession` đã có khuôn test sẵn; chưa có khuôn thì tách hàm quyết định ra rồi test hàm đó, **đừng bỏ test**).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` sạch, 0 warning mới; `dotnet test orders/XuLyDonShopee.Tests` xanh kèm test mới.
- [ ] `pickupOk == false` → **không** có lệnh `prepareNextOrder` nào được gửi cho shop đó (khẳng định bằng test).
- [ ] Vòng dừng ngay: các shop còn lại **không** được xử; kết quả trả về mang lý do đọc được.
- [ ] Webhook chưa cấu hình → im lặng, không lỗi, vòng vẫn dừng (dừng KHÔNG phụ thuộc việc gửi được tin hay không).
- [ ] Gửi lỗi mạng → nuốt + log, không phá luồng, vòng vẫn dừng.
- [ ] Chặn spam: hai lần lỗi liên tiếp trong 60' → chỉ **một** tin gửi đi, lần hai có log giải thích.
- [ ] Nhánh captcha giữ nguyên hành vi cũ (không hồi quy).
- [ ] Log client có dòng nói rõ đã dừng và vì sao.

## 5. Rủi ro & lưu ý

- **Dừng là mục tiêu chính, gửi tin là phụ.** Webhook chưa cấu hình / mạng hỏng vẫn PHẢI dừng. Đừng để việc gửi tin
  nằm trên đường quyết định dừng.
- Đây là đánh đổi có chủ ý: thà **không giao đơn** còn hơn **giao sai địa chỉ**. Người dùng đã chốt.
- Đừng nhầm với nhánh captcha — hai nguyên nhân khác nhau, thông điệp phải khác nhau, kẻo người trực xử nhầm.
- `OrderNotifyService.SendAsync` ném `OperationCanceledException` xuyên khi hủy chủ động — giữ nguyên cách
  `StartNotifyInBackground` đang xử.
- Thay đổi này nằm ở client → **chỉ có hiệu lực sau khi release** (dự kiến 1.6.9).

---

## Báo cáo thực thi (Opus điền sau khi xong)
