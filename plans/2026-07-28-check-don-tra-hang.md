# Plan: Bước check ĐƠN TRẢ HÀNG ở cuối flow mỗi shop

- **Ngày:** 2026-07-28
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & yêu cầu

Yêu cầu (chuyển từ chat, kèm ảnh chụp Seller Centre):

> *"A thêm cho e 1 bước check đơn trả hàng… Chỉ cần check những đơn có **cả** mã yêu cầu trả hàng vs mã đơn hàng.
> Xem mã yêu cầu trả hàng đấy khớp vs mã đơn hàng nào thì a dán vào **cột đơn trả hàng ở sheet**."*
> *"A thêm bước này vào **cuối cùng** chỗ check đơn shop."*
> *"A cho tool nó **nhớ cái số lượng yêu cầu** này ở lần check cuối cùng, xem ko đổi thì bỏ qua, lớn hơn bn thì
> bắt đầu check bấy nhiêu đơn **từ dòng đầu tiên**."*

App hiện **chưa có gì** chạm tới trang này (`grep returnrefund` → 0 kết quả trong `extensions/` lẫn `orders/`).
Đây là bước cào hoàn toàn mới.

### Người dùng đã chốt

| Điểm | Chốt |
|---|---|
| Lần đầu chạy (chưa có mốc) | **Chỉ ghi nhớ số, KHÔNG check** — từ lần sau mới xử phần tăng thêm |
| Nơi hiển thị mã yêu cầu | **Cả 3**: Google Sheet, màn Đơn hàng (app), trang Đơn hàng (hub) |
| Sắp xếp trước khi đọc | **Tự đổi sang "Ngày yêu cầu (Mới - Cũ)"** |

### Luật phụ Fable chốt (đã báo người dùng)

- Số **giảm** (yêu cầu được xử xong, rớt khỏi danh sách) → chỉ cập nhật lại mốc, KHÔNG check.
- Mốc nhớ **theo từng shop**, không phải theo tài khoản.
- Chỉ nhận dòng có **đủ cả hai mã**; thiếu một trong hai → bỏ qua.

### Selector đã xác nhận từ HTML thật người dùng gửi

| Thứ cần | Selector |
|---|---|
| Tab mở trang trả hàng | `[data-testid="l1-tab-return_refund_cancel"]` (có testid — dùng cái này, ĐỪNG dò theo text) |
| Số yêu cầu | `.return-list-summary-title` → text dạng `"7 Yêu cầu"` |
| Danh sách | `.return-table-content` |
| Một dòng | `a.return-row-item` (href `/portal/sale/order/<shopeeOrderId>`) |
| Đầu dòng | `.return-row-item-head` |
| Mã đơn hàng | trong head: `.id.order-id .id-content` → `260723E428EY8X` |
| Dropdown sắp xếp | nút `.sort-button`, menu `.eds-dropdown-menu` → `li.eds-dropdown-item` có text `Ngày yêu cầu (Mới - Cũ)` |

### ⚠ MẢNH CÒN THIẾU — phải xác nhận trước khi code

**Mã yêu cầu trả hàng.** Trong HTML người dùng gửi, mọi dòng đều chỉ có *Mã đơn hàng*; chỗ đáng lẽ là mã yêu cầu
đang là `<!---->` (Vue chưa render — các dòng đó là **đơn hủy**, không phải yêu cầu trả hàng). Nên **chưa biết
class/cấu trúc** của khối mã yêu cầu.

Đang chờ người dùng gửi `outerHTML` của `.return-row-item-head` một dòng **có cả hai mã**
(vd `260713QNHP2887` + `260722BTY3YHV8`).

**Nếu tới lúc thực thi vẫn chưa có HTML đó**, dùng luật NHẬN DIỆN THEO NHÃN (không phụ thuộc class chưa biết):
- Trong `.return-row-item-head`, duyệt mọi phần tử con có chứa `.id-content`.
- Với mỗi khối: lấy **nhãn** = phần text của khối TRỪ đi text của `.id-content`; lấy **giá trị** = text `.id-content`.
- Phân loại theo nhãn, **bỏ dấu + hạ chữ** trước khi so (đề phòng UI đổi ngôn ngữ):
  - chứa `ma don hang` / `order` → **mã đơn hàng**
  - chứa `yeu cau` / `return` / `request` → **mã yêu cầu trả hàng**
- Dự phòng cuối: nếu có ĐÚNG 2 khối `.id-content` mà nhãn không khớp gì → khối 1 = mã đơn, khối 2 = mã yêu cầu.
- **Bắt buộc log** khi đọc được mã đơn mà KHÔNG có mã yêu cầu, kèm số dòng — để lần chạy thật lộ ngay nếu luật sai.

## 2. Phạm vi

**Làm:**
- Extension: action mới đọc trang trả hàng (mở tab → đổi sắp xếp → đọc số + N dòng đầu → trả JSON).
- Client: chạy bước này **CUỐI CÙNG** trong flow mỗi shop; nhớ mốc số yêu cầu theo shop; ghép mã → lưu DB.
- GSheet: gửi thêm trường mã yêu cầu trả hàng.
- App + hub: thêm cột hiển thị.

**Không làm:**
- KHÔNG đụng các bước hiện có của flow shop (đọc đơn, chuẩn bị hàng, in phiếu, đặt/hoàn địa chỉ).
- KHÔNG mở trang chi tiết từng yêu cầu — chỉ đọc danh sách.
- KHÔNG commit, KHÔNG deploy, KHÔNG release.

## 3. Các bước thực hiện

### Bước 1 — Extension: action `readReturnRequests`

`extensions/shopee-orders/background.js`, thêm vào bảng `case` (cạnh `readToShip`/`syncOrders`):

1. Trên tab shop đang mở, bấm `[data-testid="l1-tab-return_refund_cancel"]`, chờ URL về `/portal/sale/returnrefundcancel`
   + chờ `.return-list-summary-title` xuất hiện (poll, trần ~20s). Gặp `/verify` → `send({action:"captcha"})` như các
   action khác.
2. **Đổi sắp xếp**: bấm `.sort-button` → trong `.eds-dropdown-menu` tìm `li` có text `Ngày yêu cầu (Mới - Cũ)` → bấm.
   Chờ danh sách vẽ lại (poll ngắn). Không tìm thấy mục đó → **vẫn đọc tiếp** nhưng gắn cờ `sortApplied:false` để
   client log cảnh báo (đừng làm hỏng cả bước chỉ vì Shopee đổi nhãn).
3. Đọc `soYeuCau` = số nguyên đầu tiên trong `.return-list-summary-title`.
4. Đọc `list` = `.return-table-content a.return-row-item`, **cắt còn `max` dòng đầu** (client truyền xuống). Mỗi dòng:
   `{ maDon, maYeuCau, shopeeOrderId }` theo luật ở mục ⚠ trên. `shopeeOrderId` lấy từ `href`.
5. `send({ action:"pageData", kind:"returns", data: JSON.stringify({ soYeuCau, sortApplied, list }) })`.

Theo đúng khuôn các action sẵn có: tự chứa trong world MAIN, không dùng thư viện, luôn dọn tab nếu có mở thêm.

### Bước 2 — Client: gọi ở CUỐI flow shop

`orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs`, trong `RunShopOrdersAsync`, **sau** Phần B (chuẩn bị
hàng) và **sau** bước hoàn trả địa chỉ — tức mắt xích cuối cùng trước khi đóng tab shop:

- Đọc mốc cũ `soYeuCauLanTruoc` của shop này.
- Gửi `readReturnRequests` với `max` tính theo luật:

```
mốc cũ null (lần đầu)   → max = 0  (chỉ đọc số để ghi nhớ, KHÔNG đọc dòng nào)
số mới == mốc cũ        → BỎ QUA hẳn (không gửi lệnh đọc dòng)
số mới <  mốc cũ        → chỉ cập nhật mốc
số mới >  mốc cũ        → max = số mới − mốc cũ
```
Vì `max` phụ thuộc số mới mà số mới lại phải đọc từ trang, làm 2 nhịp: nhịp 1 chỉ lấy số (`max=0`), so xong mới
gửi nhịp 2 lấy dòng — HOẶC extension trả cả số lẫn toàn bộ dòng rồi client tự cắt. **Chọn cách 2 phần tuỳ Opus,
nhưng phải nêu rõ lựa chọn**: cách 1 tốn thêm 1 vòng WS, cách 2 tốn thêm DOM đọc thừa. Ưu tiên cách nào ít mở
trang hơn.
- Cập nhật mốc **sau khi xử xong** (kể cả khi không có dòng nào cần ghi) để lần sau so đúng.
- Toàn bộ bước này **bọc try/catch**: lỗi/timeout/captcha → log rồi **đi tiếp**, KHÔNG phá vòng shop và KHÔNG
  đụng tới kết quả chuẩn bị hàng đã làm xong trước đó.

### Bước 3 — Nhớ mốc theo shop

Nơi lưu: bảng `account_shops` (`orders/XuLyDonShopee.Core/Data/ResultsRepository.cs`) đã khoá theo
`(account_id, shop_login)` — thêm cột `return_count_last INTEGER` (nullable = chưa từng check) + hàm đọc/ghi.
Dùng đúng khuôn migration cột-mới-cho-DB-cũ mà `Database.cs` đang dùng; **đọc code trước, đừng đoán tên hàm**.

### Bước 4 — Lưu mã yêu cầu vào đơn

- `orders` (client): thêm cột `return_request_code TEXT` + cập nhật `SyncedOrder`/`OrderRow`/repo tương ứng.
  Ghi theo `order_sn`; đơn không có trong DB (cũ hơn thời gian giữ) → **bỏ qua + log**, không tạo đơn mới.
- Ghi bằng `COALESCE(cũ, mới)` hay ghi đè? → **ghi đè khi khác** (yêu cầu trả hàng có thể được tạo lại), nhưng
  KHÔNG ghi đè bằng rỗng.
- Đặt cờ để đơn đó được đẩy lại lên GSheet + hub ở lượt kế (tái dùng đúng cơ chế cờ sẵn có — xem cách
  `gsheet_da_co_van_don` làm; **đừng đẻ cơ chế mới**).

### Bước 5 — GSheet + hub + app

- `GsheetOrderRow`: thêm `string? DonTraHang` (JSON `donTraHang`), null → vắng khỏi payload (giữ nếp "chỉ điền ô trống").
- Hub: `OrderPushItem` + cột `orders.return_request_code` (migration `AddColumnIfMissing`) + `OrderRecord` + cột ở
  `Components/Pages/Orders.razor` (đặt cạnh cột Phân loại, cùng `m-hide`).
- App: cột ở `OrdersView.axaml` + property ở `OrderRowViewModel`.

### Bước 6 — Test

- Hàm thuần tách `{maDon, maYeuCau}` từ HTML mẫu (dựng chuỗi HTML trong test, không cần trình duyệt) — gồm ca
  **chỉ có mã đơn** (phải bỏ qua) và ca **có cả hai**.
- Hàm thuần tính `max` từ (mốc cũ, số mới): 4 nhánh ở Bước 2, gồm mốc null và số giảm.
- Parse `"7 Yêu cầu"` → `7`; `"42 Yêu cầu"` → `42`; text lạ → null (không ném).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` + `dotnet build server/Shopee.Hub.Web` sạch; `dotnet test` xanh kèm test mới.
- [ ] Test luật `max`: lần đầu → 0; không đổi → bỏ qua; giảm → chỉ cập nhật mốc; tăng k → đúng k.
- [ ] Test tách mã: dòng chỉ có mã đơn → **không** vào kết quả; dòng đủ hai mã → ra đúng cặp.
- [ ] Bước này nằm **CUỐI** flow shop; lỗi ở đây KHÔNG ảnh hưởng bước chuẩn bị hàng đã xong.
- [ ] Mốc lưu theo từng shop, sống qua khởi động lại app.
- [ ] Payload GSheet có `donTraHang`; rỗng → vắng khỏi JSON.
- [ ] Hub: DB cũ mở bằng bản mới → tự thêm cột, dữ liệu cũ nguyên vẹn; cột mới hiện ở trang Đơn hàng.
- [ ] Log rõ mỗi lượt: số yêu cầu đọc được, số dòng check, số cặp ghép được, số dòng thiếu mã yêu cầu.

## 5. Rủi ro & lưu ý

- **Đây là bước CUỐI và là bước phụ.** Mọi lỗi ở đây phải "log rồi đi tiếp" — không được làm hỏng phần chuẩn bị
  hàng/in phiếu đã chạy xong trước đó trong cùng lượt.
- **Sắp xếp mặc định là "Ngày đến hạn", KHÔNG phải "Ngày yêu cầu"** — không đổi sắp xếp thì luật "N dòng đầu" sẽ
  bỏ sót âm thầm. Đây là lý do người dùng chốt tự đổi.
- Trang này dùng nhiều `<!---->` (v-if của Vue) — **đừng giả định phần tử luôn tồn tại**.
- Selector mã yêu cầu **chưa xác nhận** (mục ⚠) — nếu người dùng gửi HTML thì PIN theo đúng class thật, và ghi rõ
  trong báo cáo là đã pin hay còn dùng luật theo nhãn.
- Thêm một lượt mở trang mỗi shop mỗi vòng → có tăng thời gian vòng và rủi ro captcha. Chỉ mở khi **số yêu cầu đổi**
  chính là cơ chế giảm thiểu — giữ đúng luật đó, đừng "tối ưu" thành mở mọi lượt.

---

## Báo cáo thực thi (Opus điền sau khi xong)
