# Plan: Đọc SẢN PHẨM của từng đơn ở trang chi tiết (nhiều SP lấy đủ)

- **Ngày:** 2026-07-28
- **Trạng thái:** hoàn thành (chờ release client) — đường NHIỀU sản phẩm mới chỉ phủ bằng test + rig jsdom, CHƯA đối chiếu trang thật vì dữ liệu production chưa có đơn nào >1 SP
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

> Đây là **plan 1/2**. Plan này chỉ lo **cào + lưu + đẩy lên GSheet**. Phần hiển thị bung-dòng trên app và hub
> nằm ở plan 2, viết sau khi dữ liệu ở plan này đã chạy thật.

## 1. Bối cảnh

Yêu cầu: *"khi kiểm tra thì sẽ kiểm tra sản phẩm của đơn hàng luôn, nếu đơn hàng nhiều sản phẩm, cũng phải lấy
tất cả về"*, hiển thị **SKU + Phân loại** ở app / hub / GSheet.

### Trang chi tiết ĐÃ được mở sẵn — thêm việc này gần như miễn phí

`extensions/shopee-orders/background.js` → `doSyncOrderFinals` **đã** mở tab chi tiết từng đơn
(`ORDER_DETAIL_PREFIX + shopeeOrderId`, `active:false`) để đọc "Số tiền cuối cùng", rồi đóng tab. Ta **đọc thêm
danh sách sản phẩm ngay trong lần mở đó** ⇒ KHÔNG thêm lượt mở trang, KHÔNG thêm rủi ro captcha, KHÔNG đổi trần
30 đơn/lượt.

**Hệ quả cần biết và chấp nhận:** chỉ đơn nào đi qua đường lấy "Số tiền cuối cùng" mới có dữ liệu sản phẩm. Mọi
đơn MỚI đều đi qua đường này đúng một lần (số tiền cuối cùng chỉ lấy được ở đây), nên từ bản này trở đi đơn mới
có đủ. Đơn CŨ đã có `final_amount` sẽ KHÔNG có — đúng ý người dùng đã chốt cho cột Phân loại: *"tính từ đơn mới
nhất, không cần sửa"*. **KHÔNG được tự ý thêm lượt mở trang để vá đơn cũ.**

### Dữ liệu hiện tại kém hơn trang chi tiết

| Thứ | Hiện tại (trang DANH SÁCH) | Trang CHI TIẾT |
|---|---|---|
| SKU | **ĐOÁN** từ tên SP — `ShopeeShippingNav.ExtractSku` lấy cụm ASCII chữ+số cuối tên | `SKU phân loại: A141` — **SKU thật** |
| Phân loại | `"Kem,36 [A141 A141]"` — dính SKU lặp, phải cắt đuôi bằng luật | `Phân loại:&nbsp;Kem,36` — sạch |
| Đơn giá / SL / Thành tiền từng SP | **không có** | có đủ |

`items_json` hiện có dạng `[{name, variation, amount, image}]` (quét ở trang danh sách).
Kiểm dữ liệu thật: 17 đơn trong DB, **không đơn nào >1 sản phẩm** ⇒ đường nhiều-SP **chưa từng chạy thật**.

### HTML trang chi tiết (người dùng gửi, đã rút gọn phần thừa)

```html
<div class="product-list">
  <div class="product-list-item product-list-head">      <!-- DÒNG TIÊU ĐỀ: cũng mang class product-list-item! -->
    <div class="no">STT</div><div class="product">Sản phẩm</div>
    <div class="price">Đơn Giá</div><div class="qty">Số lượng</div><div class="subtotal">Thành tiền</div>
  </div>
  <div>
    <div class="product-list-item">                       <!-- MỘT sản phẩm -->
      <div class="no">1</div>
      <div class="product-item product">
        <div class="product-image" style="background-image: url(&quot;https://cf.shopee.vn/file/...&quot;);"></div>
        <div class="product-detail">
          <div class="product-name" title="Giày Boots Da Nữ - ... - A141"><!----><!----> Giày Boots Da Nữ - ...</div>
          <div class="product-labels"><!----><!----></div>
          <div class="product-meta">
            <div>Phân loại:&nbsp;Kem,36</div>
            <div>SKU phân loại: A141</div>
          </div>
        </div>
      </div>
      <div class="price">303.050</div>
      <div class="qty">1</div>
      <div class="subtotal">303.050</div>
    </div>
  </div>
</div>
```

### ⚠ Ba cái bẫy trong HTML này

1. **Dòng tiêu đề mang CẢ class `product-list-item`** (`class="product-list-item product-list-head"`) ⇒ chọn
   `.product-list-item` trần sẽ lấy nhầm dòng "STT / Sản phẩm / Đơn Giá…" thành một sản phẩm. Phải loại
   `.product-list-head`.
2. **`"SKU phân loại"` CHỨA chuỗi `"phân loại"`.** Luật "dòng nào chứa *phân loại* → là phân loại" sẽ ăn nhầm
   dòng SKU. **Xét nhãn SKU TRƯỚC**, hoặc neo từ đầu chuỗi. (Đúng lớp lỗi vừa dính ở trang trả hàng — xem
   `plans/2026-07-28-ghim-class-that-trang-tra-hang.md`.)
3. **Tên SP nằm ở thuộc tính `title`**, còn text bên trong có `<!---->` của Vue + khoảng trắng thừa. Lấy `title`
   trước, không có thì mới lấy textContent đã dọn.

Thêm: ảnh là **`background-image` trong style**, không phải `<img src>`.

## 2. Phạm vi

**Làm:**
- Extension: trong `doSyncOrderFinals`, đọc thêm danh sách sản phẩm; trả kèm payload `finals` sẵn có.
- Hàm THUẦN phía C# tách sản phẩm; ghi vào `items_json` (mở rộng, KHÔNG bảng mới).
- GSheet: `sku` và `phanLoai` thành chuỗi **nhiều dòng** khi đơn nhiều SP, hai cột **khớp cặp theo dòng**.

**Không làm:**
- KHÔNG thêm lượt mở trang chi tiết nào (kể cả để vá đơn cũ).
- KHÔNG thêm bảng `order_items`, KHÔNG thêm cột DB mới ở client lẫn hub.
- KHÔNG đụng phần hiển thị app/hub (plan 2).
- KHÔNG đụng bước check đơn trả hàng, chuẩn bị hàng, in phiếu.
- KHÔNG commit, KHÔNG deploy, KHÔNG release. KHÔNG đụng `%LOCALAPPDATA%\Programs\ShopeeSuite`.

## 3. Các bước

### Bước 1 — Extension: page-func đọc danh sách sản phẩm

`extensions/shopee-orders/background.js`, thêm page-func (vd `pageReadOrderProducts`) chạy trong tab chi tiết
đang mở sẵn ở `doSyncOrderFinals`:

- Chọn `.product-list .product-list-item:not(.product-list-head)`. **Trần 20 SP/đơn**; vượt thì cắt + gắn cờ
  `bicat: true` để C# log (đừng im lặng).
- Mỗi dòng trả: `{ stt, ten, phanLoai, sku, donGia, soLuong, thanhTien, anh, metaLa }`
  - `ten`: `.product-name` → `getAttribute("title")`, rỗng thì `textContent` đã gộp khoảng trắng.
  - `.product-meta > div`: với mỗi dòng, **so nhãn theo thứ tự SKU TRƯỚC** (xem bẫy #2). Đổi `\u00A0` (`&nbsp;`)
    thành khoảng trắng trước khi so. Nhãn không khớp cái nào → nhét nguyên văn vào mảng `metaLa` để C# log.
  - `donGia` / `soLuong` / `thanhTien`: text thô của `.price` / `.qty` / `.subtotal` — **để C# parse số**, đừng
    parse trong JS (để test được, đúng nếp `soYeuCauText` của bước trả hàng).
  - `anh`: bóc URL từ `style.backgroundImage` (`url("...")`), lỗi → chuỗi rỗng.
- Không tìm thấy `.product-list` → trả mảng rỗng, **không ném**.

Trong `doSyncOrderFinals`: sau khi có `finalText` (kể cả khi rỗng), gọi thêm page-func này rồi đẩy vào phần tử
đang push: `out.push({ orderSn, finalText, sanPham: [...] })`. Bọc try/catch riêng — lỗi đọc sản phẩm **không**
được làm mất `finalText` đã lấy được.

### Bước 2 — C# hàm thuần tách sản phẩm

File mới `orders/XuLyDonShopee.Core/Services/SanPhamDonParser.cs`:

- `public static IReadOnlyList<SanPhamDon> Parse(string? json)` — JSON rỗng/hỏng/không phải mảng → danh sách
  RỖNG, KHÔNG ném (dữ liệu từ web, phải chịu rác).
- Số tiền: dùng lại `ShopeeShippingNav.ParseVndAmount` (đã có, xử `.` ngăn nghìn). Không parse được → `null`,
  KHÔNG ném.
- `soLuong`: số nguyên, không parse được → `null`. Bỏ tiền tố `x`/`×` nếu có.
- Bỏ phần tử không có **cả** `ten` lẫn `sku` lẫn `phanLoai` (dòng rác).

Test (`orders/XuLyDonShopee.Tests`), dùng **đúng HTML/giá trị thật** ở mục 1:
- [ ] 1 SP: `ten` đủ, `phanLoai = "Kem,36"`, `sku = "A141"`, `donGia = 303050`, `soLuong = 1`, `thanhTien = 303050`.
- [ ] **Bẫy #2**: dòng `"SKU phân loại: A141"` KHÔNG được nhận thành `phanLoai`; `phanLoai` phải là `"Kem,36"`.
- [ ] `&nbsp;` sau dấu hai chấm được dọn (`"Phân loại:\u00A0Kem,36"` → `"Kem,36"`).
- [ ] Nhiều SP → ra đủ, **giữ đúng thứ tự** STT.
- [ ] JSON hỏng / `"[]"` / `null` / thiếu field → danh sách rỗng, không ném.
- [ ] Giá text lạ → `null`, không ném.

### Bước 3 — Ghi vào `items_json` (mở rộng, tương thích ngược)

Chỗ hiện gộp `finalText` vào đơn: `OrdersBridgeSession.MergeFinalAmounts` — **đọc kỹ trước khi sửa**, mở rộng
để gộp luôn `sanPham`.

Quy tắc ghi, phải giữ TƯƠNG THÍCH NGƯỢC:
- `items_json` vẫn là **mảng cùng khuôn cũ**; **GIỮ NGUYÊN** các khóa `name`, `variation`, `amount`, `image`
  (hub và `PhanLoaiExtractor` đang đọc `variation`).
- **THÊM** khóa mới cho mỗi phần tử khi trang chi tiết có: `phanLoai`, `sku`, `donGia`, `thanhTien`.
- Trang chi tiết có dữ liệu → **thay cả mảng** bằng bản của trang chi tiết (nguồn chuẩn, đủ SP hơn), nhưng vẫn
  điền `variation` = `phanLoai` và `amount` = `soLuong` để phần đọc cũ không vỡ. Trang chi tiết rỗng → **giữ
  nguyên** mảng cũ từ trang danh sách, không xoá.
- `PhanLoaiExtractor.TuItemsJson`: **ưu tiên `phanLoai`**, không có mới quay về `variation` (giữ nguyên luật cắt
  đuôi `[SKU SKU]` cho dữ liệu cũ). Sửa tối thiểu, giữ mọi test cũ xanh.

### Bước 4 — GSheet: SKU và Phân loại nhiều dòng, khớp cặp

`orders/XuLyDonShopee.App/Services/HubOutbox.cs` (chỗ dựng `GsheetOrderRow`):

- Dựng **cả hai** chuỗi từ **CÙNG một danh sách sản phẩm đã sắp thứ tự** để dòng thứ *i* của `sku` khớp dòng thứ
  *i* của `phanLoai`. Sản phẩm thiếu `sku` → để **dòng trống** ở cột SKU (đừng nhảy dòng, sẽ lệch cặp).
- Nối bằng `"\n"` (Apps Script `setValue` với `\n` cho ra ô nhiều dòng).
- **Số lượng**: gắn hậu tố `" ×N"` vào phân loại **chỉ khi `N ≥ 2`** — đơn 1 cái giữ nguyên `"Kem,36"` như các
  dòng đã có trong sheet, khỏi lệch định dạng với ~30 dòng cũ. *(Fable chốt; đổi được ở một chỗ.)*
- Đơn 1 SP → chuỗi **không có** `\n`, y hệt hiện tại ⇒ không có thay đổi nào nhìn thấy được với đơn thường.
- Không có sản phẩm nào → giữ nguyên đường cũ (`PhanLoaiExtractor` + `p.Sku`), **không** gửi chuỗi rỗng.

Test:
- [ ] 1 SP → `sku = "A141"`, `phanLoai = "Kem,36"` (không xuống dòng, không `×1`).
- [ ] 2 SP → `sku = "A141\nA322"`, `phanLoai = "Kem,36\nNâu Be,39 ×2"` — **cùng số dòng**.
- [ ] SP giữa thiếu SKU → cột SKU có dòng trống ở đúng vị trí, hai cột vẫn **bằng số dòng**.
- [ ] Không đọc được sản phẩm → payload y như trước khi có plan này (hồi quy).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` + `dotnet build server/Shopee.Hub.Web` sạch, 0 warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` xanh, **không sửa kỳ vọng test cũ nào**.
- [ ] `node --check extensions/shopee-orders/background.js` OK.
- [ ] Số lượt `chrome.tabs.create` mỗi lượt sync **không đổi** (chứng minh bằng cách chỉ ra chỗ gọi, không thêm chỗ mới).
- [ ] Đơn 1 SP: `items_json` vẫn đọc được bằng `PhanLoaiExtractor` như cũ (test hồi quy).

## 5. Rủi ro & lưu ý

- **Bẫy nhãn `SKU phân loại` ⊃ `phân loại`** — xem mục ⚠. Sai chỗ này là SKU chui vào cột Phân loại trên sheet.
- **Dòng tiêu đề cũng mang class `product-list-item`** — không loại là có một "sản phẩm" tên *Sản phẩm* giá *Đơn Giá*.
- **Chưa có đơn nhiều SP nào trong dữ liệu thật** ⇒ test là chỗ dựa duy nhất; viết test nhiều-SP cho tử tế, và
  báo rõ trong báo cáo rằng đường này chưa được xác nhận trên trang thật.
- Đừng phá `finalText`: sản phẩm là phần THÊM, lỗi ở đó không được làm mất số tiền cuối cùng đã đọc được.
- Thay đổi nằm ở client + extension ⇒ chỉ có hiệu lực sau khi release.

---

## Báo cáo thực thi (Opus điền sau khi xong)
