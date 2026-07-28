# Plan: Ghim class THẬT của trang trả hàng + vá 2 lỗi luật nhận diện

- **Ngày:** 2026-07-28
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh — đã có HTML THẬT của dòng trả hàng

Plan trước (`2026-07-28-check-don-tra-hang.md`) phải nhận diện mã **theo NHÃN** vì class khối "mã yêu cầu trả
hàng" chưa xác nhận (hai mẫu người dùng gửi đều bị cắt ở 50.000 ký tự và mọi dòng đọc được đều là đơn HỦY —
chỗ mã yêu cầu là `<!---->`).

**Nay đã có HTML thật của một dòng trả hàng đầy đủ.** Cấu trúc chốt được:

```html
<a class="return-row-item" href="/portal/sale/return/235778510235654">
  <div class="return-row-item-head">
    <div class="user-view-item …"> … <div class="username text-overflow">ttd911</div> … </div>
    <div class="id order-id">
      <span>Mã đơn hàng</span><span class="id-content">260619GSNQ36U7</span>
      <div class="copy-button"><i class="eds-icon…"><svg …><path d="M13 1H4.625…"/></svg></i></div>
    </div>
    <div class="id return-id">
      <span>Mã yêu cầu trả hàng</span><span class="id-content">2606220PN1D6X06</span>
      <div class="copy-button">…</div>
    </div>
    <!---->
  </div>
```

Ba điều xác nhận được:
- Khối mã yêu cầu là **`class="id return-id"`**, khối mã đơn là **`class="id order-id"`**.
- Cả hai **dùng chung** `class="id-content"` cho ô giá trị ⇒ vòng quét `id-content` hiện tại vẫn đúng.
- Nhãn thật là **"Mã yêu cầu trả hàng"** và **"Mã đơn hàng"**.

Chạy thử luật hiện tại trên đúng HTML này: **ra đúng cả hai mã**. Nhưng lộ ra **2 lỗi thật** (đã kiểm chứng
bằng mô phỏng, không phải suy đoán):

### Lỗi 1 (NẶNG) — tên người mua lọt vào NHÃN → ghi SAI mã lên Google Sheet

`TachMa` lấy nhãn của một khối = **toàn bộ text từ cuối khối trước tới thẻ mở khối này**. Với khối ĐẦU, "khối
trước" là đầu chuỗi ⇒ nhãn nuốt luôn **tên người mua**:

```
NHÃN khối 1 = 'ttd911 Mã đơn hàng'      ← username dính vào
NHÃN khối 2 = 'Mã yêu cầu trả hàng'
```

Tên người mua là dữ liệu người dùng tự đặt. Nếu nó chứa `return` / `request` / `yêu cầu` thì `LaNhanYeuCau`
khớp TRƯỚC (nhánh yêu cầu xét trước) và **mã ĐƠN HÀNG bị gán làm mã yêu cầu trả hàng**:

| username | maDon | maYeuCau | kết quả |
|---|---|---|---|
| `ttd911` | 260619GSNQ36U7 | 2606220PN1D6X06 | đúng |
| `returnking88` | **null** | **260619GSNQ36U7** | **SAI** |
| `shop_request_vn` | **null** | **260619GSNQ36U7** | **SAI** |

Hậu quả: cột "Mã đơn trả hàng" trên sheet nhận **mã đơn hàng** — đúng cái kịch bản "ghi mã sai còn tệ hơn để
trống" mà thiết kế ban đầu muốn tránh.

### Lỗi 2 (VỪA) — avatar base64 làm phình HTML, cắt mất khối mã yêu cầu

`pageScanReturnRows` gửi `outerHTML` của cả khối head, cắt cứng ở `MAX_RETURN_HEAD_HTML = 4000`.

- Head với avatar là URL thường: **2133** ký tự — thoải mái.
- Nhưng Shopee có dòng avatar là **data URI base64** (thấy thật trong mẫu người dùng gửi): riêng chuỗi base64
  đó **1122** ký tự ⇒ head thành **3197**, chỉ còn ~800 ký tự dư.
- Avatar lớn hơn (mẫu bắt được chỉ là ảnh nhỏ) sẽ vượt 4000 ⇒ HTML bị cắt. Khối `return-id` nằm **CUỐI** head
  ⇒ mất đúng thứ cần lấy, **âm thầm**: dòng đó rơi vào danh sách "thiếu mã yêu cầu", không ai biết vì sao.

Hai cái SVG icon copy (mỗi cái ~450 ký tự path) cũng là rác thuần, chiếm gần nửa head.

### Ghi chú thêm — `shopeeOrderId` luôn RỖNG

Extension dò `href.match(/\/portal\/sale\/order\/(\d+)/)`, nhưng href dòng trả hàng là
`/portal/sale/return/235778510235654`. Trường `shopeeOrderId` vì thế **luôn rỗng**. Không gây lỗi (ghép cặp chỉ
dùng `headHtml`, không đụng field này) nhưng đang là bẫy cho người đọc sau.

## 2. Phạm vi

**Làm:**
- Nhận diện khối **theo CLASS trước** (`return-id` / `order-id` — nay đã xác nhận), nhãn chỉ còn là **dự phòng**.
- Thu hẹp phạm vi nhãn để **tên người mua không lọt vào**.
- Extension: **bỏ `<img>` và `<svg>`** khỏi HTML gửi về trước khi cắt trần.
- Test bằng **đúng HTML thật** ở mục 1 (làm fixture), kèm ca username độc.
- Làm rõ `shopeeOrderId` trong comment/DTO.

**Không làm:**
- KHÔNG đổi luật đếm số yêu cầu (`QuyetDinhCheck`), `ParseSoYeuCau`, `ParseKetQua` — đang đúng.
- KHÔNG đổi lưu DB / cờ đẩy GSheet-hub / cột UI — đã nghiệm thu ở plan trước.
- KHÔNG đổi vị trí bước trong flow, không đổi cách bọc try/catch.
- KHÔNG commit, KHÔNG deploy, KHÔNG release. KHÔNG đụng `%LOCALAPPDATA%\Programs\ShopeeSuite`.

## 3. Các bước thực hiện

### Bước 1 — `TraHangParser.TachMa`: class trước, nhãn sau

File `orders/XuLyDonShopee.Core/Services/TraHangParser.cs`.

Với mỗi khối `id-content` tìm được, xác định loại theo thứ tự:

1. **Theo CLASS của thẻ bao (ưu tiên cao nhất).** Thẻ `<div class="id return-id">` / `<div class="id order-id">`
   là thẻ mở **gần nhất phía trước** thẻ `id-content`. Dò ngược từ vị trí thẻ `id-content` về đầu chuỗi, lấy thẻ
   mở `<div …>` gần nhất có class chứa token `order-id` hoặc `return-id`:
   - token `return-id` → **mã yêu cầu trả hàng**
   - token `order-id` → **mã đơn hàng**
   So theo **token** (dùng lại `ClassChuaToken` sẵn có), không phải "chứa chuỗi".
2. **Theo NHÃN (dự phòng, khi class không khớp).** Giữ `LaNhanYeuCau` / `LaNhanDon` như cũ (kể cả thứ tự xét
   yêu-cầu-trước), NHƯNG nhãn phải được **thu hẹp**: chỉ lấy text của **thẻ `<span>` mở gần nhất phía trước**
   thẻ `id-content` (trong HTML thật đó chính là `<span>Mã đơn hàng</span>`), thay vì "mọi text từ cuối khối
   trước". Tên người mua nằm trong `<div class="username">` nên sẽ không còn lọt vào.
   - Không tìm được `<span>` nào phía trước → nhãn rỗng → coi như không khớp nhãn.
3. **Dự phòng VỊ TRÍ**: giữ nguyên luật cũ — chỉ khi có đúng 2 khối và **không** khối nào xác định được bằng
   class LẪN nhãn thì khối 1 = mã đơn, khối 2 = mã yêu cầu.

Giữ nguyên: khối rỗng (`<!---->` Vue chưa render) vẫn bị bỏ qua và vẫn dời mốc; chuỗi chẩn đoán khi thiếu mã.
Chuỗi chẩn đoán nên ghi thêm **class dò được** của từng khối (không chỉ nhãn) để lần sau soi log nhanh hơn.

### Bước 2 — Extension: bỏ `<img>`/`<svg>` trước khi cắt

File `extensions/shopee-orders/background.js`, hàm `pageScanReturnRows`.

Trước khi lấy `outerHTML`: **clone** node head (`cloneNode(true)` — TUYỆT ĐỐI không sửa DOM thật của trang, sẽ
làm hỏng giao diện người dùng đang xem), rồi xoá mọi `img` và `svg` trong bản clone, sau đó mới `outerHTML`.

Kết quả mong đợi: head từ ~2100–3200 ký tự xuống còn **vài trăm** ⇒ trần 4000 thành dư dả thật sự, và chuỗi
chẩn đoán trong log đọc được bằng mắt. Giữ nguyên trần 4000 (không cần nới) và giữ nguyên `MAX_RETURN_ROWS`.

`cloneNode` lỗi (node lạ) → bắt lỗi, lùi về `outerHTML` gốc như cũ; không được làm hỏng cả lượt quét.

### Bước 3 — `shopeeOrderId`: nói đúng sự thật

Href dòng trả hàng là `/portal/sale/return/<returnId>`, KHÔNG phải `/portal/sale/order/<id>` ⇒ field luôn rỗng.
Chọn một trong hai, ghi rõ lý do trong báo cáo:
- (khuyến nghị) giữ field nhưng sửa comment ở cả `background.js` lẫn `DongTraHang` nói rõ "trên trang trả hàng
  href là `/portal/sale/return/…` nên field này thường RỖNG; ghép cặp chỉ dùng `headHtml`"; hoặc
- bỏ hẳn field nếu bỏ được gọn gàng (kéo theo sửa `ParseKetQua` + test).

**Đừng** đổi regex thành bắt `/return/(\d+)` rồi nhét return-id vào field tên `shopeeOrderId` — sai ngữ nghĩa.

### Bước 4 — Test (`orders/XuLyDonShopee.Tests/TraHangParserTests.cs`)

Thêm, dùng **HTML THẬT** ở mục 1 làm fixture (chép nguyên văn, giữ cả `data-v-*`, cả `<!---->` cuối, cả 2 SVG):

- [ ] HTML thật → `MaDon = "260619GSNQ36U7"`, `MaYeuCau = "2606220PN1D6X06"`. (Ca xương sống.)
- [ ] **Username độc**: thay `ttd911` bằng `returnking88`, rồi bằng `shop_request_vn` → **vẫn** ra đúng hai mã
      (đây là ca hồi quy cho Lỗi 1 — hiện tại đang SAI).
- [ ] Username chứa "yêu cầu" (vd `yeucaushop`) → vẫn đúng.
- [ ] Thứ tự khối ĐẢO (`return-id` đứng trước `order-id`) → vẫn đúng (class quyết định, không phải vị trí).
- [ ] Class đổi mà nhãn còn (bỏ `return-id`/`order-id`, giữ `<span>Mã yêu cầu trả hàng</span>`) → dự phòng nhãn
      vẫn ra đúng.
- [ ] Đơn HỦY (khối `return-id` là `<!---->`) → `MaDon` có, `MaYeuCau` null, dòng vào `ThieuMaYeuCau`.
- [ ] Giữ nguyên mọi test cũ đang xanh (66 test của đợt trước) — **không được sửa test cũ để nó xanh**; test cũ
      nào mâu thuẫn với luật mới thì báo lại, đừng tự ý đổi kỳ vọng.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` + `dotnet build server/Shopee.Hub.Web` sạch, 0 warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` xanh, số test **≥ 1219 + số test mới**, không test cũ nào bị sửa
      kỳ vọng.
- [ ] `node --check extensions/shopee-orders/background.js` OK.
- [ ] Đo được và ghi vào báo cáo: độ dài `headHtml` **trước và sau** khi bỏ `img`/`svg` trên HTML thật ở mục 1.
- [ ] Ca `returnking88` / `shop_request_vn` chuyển từ SAI sang ĐÚNG (chứng minh Lỗi 1 đã vá).

## 5. Rủi ro & lưu ý

- **`cloneNode` rồi mới xoá.** Xoá `img`/`svg` trực tiếp trên DOM thật sẽ làm trang của người dùng mất ảnh —
  họ đang nhìn màn hình đó.
- **Class có thể đổi tiếp.** Vì vậy giữ đủ 3 tầng (class → nhãn → vị trí), đừng bỏ tầng nhãn khi đã có class.
- **Vẫn giữ nguyên tắc "thà trống còn hơn sai"**: không xác định được thì để null + log chẩn đoán, tuyệt đối
  không đoán bừa.
- Thay đổi nằm ở client + extension ⇒ chỉ có hiệu lực sau khi build lại. Hub không bị ảnh hưởng.

---

## Báo cáo thực thi (Opus điền sau khi xong)
