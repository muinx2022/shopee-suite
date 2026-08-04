# Plan: Lỗi địa chỉ lấy hàng → bỏ qua shop, vẫn chạy shop kế

- **Ngày:** 2026-08-04
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Auto

## 1. Bối cảnh & mục tiêu

**Hiện trạng (plan 2026-07-28):** khi `pickupOk == false` (không đặt được địa chỉ lấy hàng):

1. Shop đó **không** in phiếu (đúng — giữ).
2. `OrdersBridgeSession.RunAllShopsAsync` **return sớm** → bỏ hết shop còn lại của tài khoản.
3. `AccountSession` gửi cảnh báo Slack/Hub (`StartCanhBaoDiaChiInBackground`).
4. Tin Slack nói *"đã dừng vòng, chưa in phiếu nào"*.

**Người dùng chốt lại (2026-08-04):** lỗi địa chỉ thường chỉ của **một shop** (modal/địa chỉ shop đó), không phải cả tài khoản. Muốn:

- Shop lỗi → **dừng shop đó** (không in phiếu), gửi cảnh báo Slack như cũ.
- Vòng lặp duyệt shop **vẫn chạy tiếp** sang shop kế tiếp.
- Captcha vẫn dừng cả vòng (không đổi).

## 2. Phạm vi

**Làm:**

- Đổi nhánh `PickupFailedShop` trong `RunAllShopsAsync`: ghi nhận shop lỗi → đóng tab shop → `continue` sang shop kế (không `return`).
- Cập nhật log + nội dung tin Slack cho khớp hành vi mới ("bỏ qua shop này, vẫn chạy shop khác").
- `AccountSession`: sau vòng xong vẫn gửi cảnh báo nếu có shop lỗi địa chỉ; log không còn nói "dừng cả vòng / sẽ nghỉ rồi thử lại" như thể vòng bị cắt sớm.
- Cập nhật test nội dung tin + comment/xmldoc liên quan.

**Không làm:**

- KHÔNG đổi nhánh captcha (vẫn dừng cả vòng).
- KHÔNG đổi luật `QuyetDinhSauDatDiaChi` (pickupOk=false → không bao giờ `XuDon` / không in phiếu).
- KHÔNG đổi chống spam 60'/tài khoản, kênh Hub/webhook local, extension.
- KHÔNG đụng nhánh "đóng tab shop hỏng → dừng vòng" (lỗi picker khác).
- KHÔNG release / deploy.

## 3. Các bước thực hiện

### Bước 1 — `OrdersBridgeSession.RunAllShopsAsync`

File: `orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs` (~dòng 316–324).

Thay khối:

```csharp
if (_flow.PickupFailedShop is not null)
{
    // ... return sớm PickupAddressFailed: true ...
}
```

bằng logic:

1. Gom nhãn shop lỗi vào `List<string> pickupFailedShops` (khai báo trước vòng `for`).
2. Log: `⛔ Không đặt được địa chỉ lấy hàng ở shop {tên} — BỎ QUA shop này, KHÔNG in phiếu; sang shop kế.`
3. Cộng `totalOrders += orders` (Phần A có thể đã sync đơn; slips = 0). **Không** `shopsDone++`.
4. Gán `_flow.PickupFailedShop = null` để shop sau không bị dính cờ cũ.
5. Gọi `_flow.DongTabShopAsync(ct)` y như shop thường (phải về picker trước khi mở Chi tiết shop kế). Nếu đóng tab fail và còn shop sau → giữ hành vi dừng vòng hiện có của nhánh đó.
6. `continue` (không `return`).

Cuối vòng (sau `for`, chỗ return thành công): nếu `pickupFailedShops.Count > 0` → trả

```csharp
new OrdersBridgeRunResult(
    shopCount, shopsDone, totalOrders, totalSlips, false,
    $"Không đặt được địa chỉ lấy hàng ({_province}) ở shop {string.Join(", ", pickupFailedShops)} — đã bỏ qua shop đó, chưa in phiếu; các shop khác vẫn chạy.",
    PickupAddressFailed: true,
    PickupFailedShop: string.Join(", ", pickupFailedShops));
```

Cập nhật xmldoc `OrdersBridgeRunResult.PickupAddressFailed`: không còn nghĩa "dừng cả vòng", mà "có ≥1 shop bị bỏ qua vì địa chỉ".

**Lát cắt "Chạy thử"** (`RunSliceCoreAsync` ~437): chỉ 1 shop — giữ dừng lát cắt đó; chỉ sửa chữ Error cho khớp ("đã bỏ qua shop, KHÔNG in phiếu").

### Bước 2 — `ShopFlowRunner` (log + comment)

File: `orders/XuLyDonShopee.Core/Services/ShopFlowRunner.cs`.

- Log dòng ~216: đổi *"DỪNG vòng"* → *"BỎ QUA shop này, sang shop kế (nếu còn)"*.
- Comment enum `SauDatDiaChi.DungViDiaChi` / xmldoc `QuyetDinhSauDatDiaChi`: nghĩa là dừng **shop** (không in phiếu), không còn dừng cả vòng tài khoản. Tên enum giữ nguyên (tránh refactor lan).

Vẫn set `PickupFailedShop` như hiện tại — tín hiệu cho `RunAllShopsAsync`.

### Bước 3 — Tin Slack + log chống spam

File: `orders/XuLyDonShopee.Core/Services/OrderNotifyService.cs` — `TaoTinNhanLoiDiaChi`:

- Dòng đầu: đổi *"đã dừng vòng, chưa in phiếu nào"* → *"đã bỏ qua shop này (chưa in phiếu), vẫn chạy shop khác"*.
- Dòng việc cần làm: giữ hướng dẫn sửa modal "Sửa Địa chỉ" của shop đó.

File: `orders/XuLyDonShopee.App/Services/OrderPersistPipeline.cs` — `StartCanhBaoDiaChiInBackground`:

- Xmldoc + các chuỗi log còn nói *"vòng vẫn dừng"* / *"vòng VẪN dừng"* → sửa thành *"shop bị bỏ qua, vòng vẫn chạy shop khác"* (hoặc tương đương ngắn).

### Bước 4 — `AccountSession`

File: `orders/XuLyDonShopee.App/Services/AccountSession.cs` (~496–501).

Nhánh `result.PickupAddressFailed`:

- Vẫn gọi `_persist.StartCanhBaoDiaChiInBackground(...)` (giữ Slack/Hub).
- Log: không còn *"Sửa địa chỉ… sẽ thử lại sau khi nghỉ"* như thể cắt vòng sớm. Ví dụ: `⛔ {result.Error} Đã bỏ qua shop lỗi địa chỉ; các shop khác trong vòng này vẫn chạy.`
- **Không** `break` / không bỏ `Task.Delay` nghỉ interval — vòng tài khoản vẫn nghỉ rồi chạy chu kỳ sau như bình thường (đã vậy nếu không return sớm từ bridge).

Nếu vừa `PickupAddressFailed` vừa vòng đã chạy trọn các shop còn lại: có thể log thêm dòng status kiểu vòng xong (shopsDone/shopCount) — tùy đọc code hiện tại; ưu tiên một log rõ, không spam hai thông điệp mâu thuẫn.

### Bước 5 — Test

File: `orders/XuLyDonShopee.Tests/PickupAddressStopTests.cs`:

- Đổi assert tin cảnh báo: không còn `"đã dừng vòng, chưa in phiếu nào"`; assert chuỗi mới ("bỏ qua shop" / "vẫn chạy shop khác").
- Giữ nguyên test `QuyetDinhSauDatDiaChi` (pickupOk=false → `DungViDiaChi`, không ra `XuDon`).
- Cập nhật comment đầu class cho đúng hành vi mới.
- (Tuỳ chọn, nếu tách được hàm thuần quyết định "continue vs return" trong bridge thì test thêm; **không bắt buộc** nếu chỉ đổi khối if — tránh đẻ mock cầu nối nặng.)

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build orders/XuLyDonShopee.App/XuLyDonShopee.App.csproj` — 0 warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj` — xanh, kể cả `PickupAddressStopTests`.
- [ ] `pickupOk == false` ở shop i → không gửi `prepareNextOrder` cho shop đó; vòng vẫn mở shop i+1 (đọc code `RunAllShopsAsync`: không `return` sớm vì địa chỉ).
- [ ] Vẫn gửi cảnh báo Slack/Hub khi có shop lỗi địa chỉ; nội dung tin nói bỏ qua shop, không nói dừng cả vòng.
- [ ] Captcha vẫn `return` sớm cả vòng (không đổi).

## 5. Rủi ro & lưu ý

- **Đóng tab bắt buộc** trước khi `continue` — quên thì shop kế chết với lỗi "chờ tab shop mở" (đã từng gặp production).
- Nhiều shop lỗi trong cùng vòng: `PickupFailedShop` nối bằng `", "`; chống spam vẫn 1 tin / tài khoản / 60' → chỉ báo 1 lần (đã liệt kê đủ tên shop trong tin). Chấp nhận được.
- Giả thuyết cũ (modal hỏng = mọi shop hỏng) **bỏ**: nếu thật sự mọi shop lỗi địa chỉ, vòng sẽ bỏ qua lần lượt từng shop + 1 tin Slack — chậm hơn dừng sớm nhưng an toàn hơn (shop khỏe vẫn được xử).
- Không đổi spam key / ngưỡng trừ khi user yêu cầu sau.
