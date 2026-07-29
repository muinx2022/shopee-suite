# Plan: Mã trả hàng sống độc lập với đơn + 4 chốt chặn cho bước check

- **Ngày:** 2026-07-29
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh — vì sao quét đúng vẫn không có kết quả

Bước check đơn trả hàng đã sửa hai lỗi (bấm đúng tab, lần đầu có check). Nhưng người dùng chỉ ra một điều làm
đổ toàn bộ giả định: **Shopee cho trả hàng trong 15 ngày.**

Ghép với luật dọn đơn của app (`AccountSession.NenXoaDonKetThuc`):

```csharp
terminal (hủy HOẶC đã giao) && gsheetSettled && đã đếm && đã đẩy hub && không còn phiếu chờ  →  XOÁ
```

Đơn giao xong, ghi sheet xong là bị dọn — thường trong một hai vòng. Mà yêu cầu trả hàng đến **sau đó nhiều
ngày**. Khi đó:

```csharp
// OrdersRepository.SetReturnRequestCodes
if (sel.ExecuteScalar() is null) { khongCoDon++; continue; }   // đơn không còn → BỎ mã
```

⇒ **Quét ra mã rồi vứt đi.** Nới cửa sổ hay quét sâu hơn đều vô ích — vấn đề không phải tìm thiếu, mà là tìm
được rồi không có chỗ ghi.

Kiểm trên 3 dòng THẬT người dùng gửi (tab "Đơn Trả hàng Hoàn tiền", 29/07):

```
260725JTBTAJVD  đặt 25/07  yêu cầu 28/07  → có thể còn trong DB
260715QNAP2587  đặt 15/07  yêu cầu 21/07  → gần như chắc đã bị dọn
260617ANE669U9  đặt 17/06  yêu cầu 21/06  → mất từ lâu
```

**Lối ra:** dòng trên Google Sheet **vẫn còn**, và Apps Script tra theo **mã đơn** rồi điền ô trống — nó không
cần máy còn giữ đơn. Vậy tách mã trả hàng khỏi vòng đời của đơn.

### Dữ liệu thật đã có (người dùng gửi 29/07)

**Dòng TRẢ HÀNG** — có đủ hai khối mã, href `/portal/sale/return/…`:
```html
<a class="return-row-item" href="/portal/sale/return/238920294239139">
  <div class="return-row-item-head">
    …<div class="id order-id">…260725JTBTAJVD…</div>
      <div class="id return-id"><span>Mã yêu cầu trả hàng</span><span class="id-content">2607280TS2VYAW3</span>…</div>
```

**Dòng ĐƠN HỦY** — CHỈ có khối mã đơn, href `/portal/sale/order/…`:
```html
<a class="return-row-item" href="/portal/sale/order/238153025271149">
  <div class="return-row-item-head">
    …<div class="id order-id">…260713HUBU75VU…</div><!----><!---->      ← không có return-id
```

⇒ **`href` phân biệt được hai loại**, độc lập với việc chọn tab. Mã đơn hủy cũng theo `yyMMdd` như thường.

## 2. Phạm vi

**Làm (5 việc):**
1. Lọc dòng theo `href` chứa `/portal/sale/return/` — chốt chặn thứ hai, không phụ thuộc tab.
2. Cửa sổ đo theo **NGÀY YÊU CẦU** (suy từ mã yêu cầu) thay vì ngày đặt đơn, nới lên **20 ngày**.
3. **Mã trả hàng sống độc lập với đơn** — bảng riêng + đường đẩy GSheet riêng.
4. Chẩn đoán khi trang trả hàng không render kịp.
5. Không nuốt cờ `closeShopTab` — một lần trượt không được giết cả vòng.

**Không làm:**
- KHÔNG phân trang trang trả hàng (giữ trần `MAX_RETURN_ROWS`).
- KHÔNG đụng luồng đẩy đơn thường lên GSheet/hub.
- KHÔNG đổi vòng ghi file chính/phụ của Apps Script ngoài đúng một cờ ở việc 3.
- KHÔNG commit, KHÔNG deploy, KHÔNG release. KHÔNG đụng `%LOCALAPPDATA%\Programs\ShopeeSuite`.

## 3. ⚠ Năm cái bẫy

1. **`daHuy` phải VẮNG trong payload chỉ-có-mã-trả.** Script xử: `daHuy === true` → tô đỏ; `=== false` → **XOÁ**
   nền đỏ; **vắng** → không đụng màu. `GsheetOrderRow.DaHuy` hiện là `bool` nên **luôn** serialize ⇒ đẩy dòng
   mã-trả cho một đơn ĐÃ HỦY sẽ **xoá sạch nền đỏ** ở CẢ hai file. Phải để field vắng hẳn.
2. **KHÔNG được tạo dòng mới trên sheet** cho đơn không tìm thấy. Nhánh append của script sẽ đẻ một dòng gần như
   rỗng (chỉ mã đơn + mã trả) cho đơn chưa từng ghi sheet. Cần cờ payload mới (vd `chiDienNeuCo: true`) →
   script **bỏ qua** đơn không tra thấy thay vì append.
3. **Mã yêu cầu ≠ mã đơn.** Cửa sổ đo trên **mã yêu cầu** (`2607280TS2VYAW3` → 28/07). Danh sách sắp theo ngày
   yêu cầu, nhưng **vẫn KHÔNG được `break` sớm** — hàng có thể không đơn điệu tuyệt đối; lọc, đừng dừng.
4. **Bảng mới KHÔNG được dọn theo đơn.** Cả điểm của việc này. Đừng gắn khoá ngoại tới `orders`, đừng xoá cùng.
   Tự dọn theo tuổi (vd > 90 ngày) để không phình vô hạn — nêu rõ lựa chọn.
5. **Đừng đẩy trùng.** Cờ `gsheet_synced_at` trên bảng mới quyết định đẩy hay chưa; mã ĐỔI → reset cờ (mẫu
   `SetReturnRequestCodes` đang dùng cho `hub_synced_at`/`gsheet_da_co_don_tra_hang`).

## 4. Các bước

### Bước 1 — Extension: lọc theo `href`

`pageScanReturnRows`: mỗi dòng thêm `laTraHang = /\/portal\/sale\/return\//.test(href)`. **Vẫn gửi** dòng
`/order/` (đơn hủy) kèm cờ `false` — để C# đếm và log được "bỏ k dòng vì là đơn hủy", đừng lọc câm ở JS.

### Bước 2 — `TraHangParser`: lọc + đổi trục cửa sổ

- `GhepCap`: bỏ dòng có `laTraHang === false`, đếm riêng để log. Dòng thiếu cờ (client cũ) → **giữ** như cũ.
- `LocTheoCuaSo`: đo trên **`MaYeuCau`** thay vì `MaDon`. Đổi hằng cửa sổ thành **20 ngày** (15 ngày chính sách
  Shopee + biên). Đặt hằng RIÊNG cho việc trả hàng, **đừng** dùng chung `SoNgayBuUocTinh` (7 ngày, việc khác,
  ý nghĩa khác) — sửa doc chỗ cũ nếu đang dùng chung.
- Mã yêu cầu không suy được ngày → **giữ** (thà thừa còn hơn mất mã), đếm riêng để log. *(Khác với đơn: mã yêu
  cầu là thứ ta cần, không được mạnh tay loại.)*

Test: 3 cặp thật ở mục 1 với hôm nay 29/07, cửa sổ 20 ngày → giữ **2** (28/07 và 21/07), bỏ 1 (21/06).

### Bước 3 — Bảng `return_codes` sống độc lập

`orders/XuLyDonShopee.Core/Data/Database.cs` — bảng mới (khuôn `EnsureColumn`/CREATE sẵn có):

```
return_codes(
  account_id INTEGER, order_sn TEXT, code TEXT, shop_login TEXT,
  created_at TEXT, gsheet_synced_at TEXT NULL,
  PRIMARY KEY (account_id, order_sn))
```

Repository mới (hoặc mở rộng `ResultsRepository`) với:
- `LuuMaTraHang(accountId, cặp…)` — upsert; **mã đổi → reset `gsheet_synced_at`** (bẫy #5).
- `LayMaTraHangChuaDay(accountId)` → danh sách `(OrderSn, Code)`.
- `DanhDauDaDay(accountId, orderSns, luc)`.
- `DonDep(truocNgay)` — xoá bản ghi cũ hơn 90 ngày (bẫy #4).

`SetReturnRequestCodes` **giữ nguyên** (vẫn ghi vào `orders` khi đơn còn, cho lưới app/hub) — nhưng nay **không
còn là đường duy nhất**; caller ghi vào CẢ hai.

### Bước 4 — Đường đẩy GSheet riêng cho mã trả hàng

`HubOutbox`: sau đường đẩy đơn thường, thêm một lượt nhẹ:
- Lấy `LayMaTraHangChuaDay`.
- Dựng payload **chỉ có `maDon` + `donTraHang`**, **KHÔNG có `daHuy`** (bẫy #1) và **có `chiDienNeuCo: true`**
  (bẫy #2).
- `GsheetOrderRow.DaHuy` là `bool` → cần đường riêng. **Đọc code rồi chọn**: đổi thành `bool?` (rủi ro lan rộng,
  phải rà mọi call-site) HOẶC thêm record/nhánh serialize riêng cho payload mã-trả. Nêu rõ lựa chọn + lý do.
- Tab đích: dùng `gsheet_tab` đã nhớ nếu có, không thì tab theo tháng hiện tại — script tra mã đơn trên MỌI tab
  nên tab chỉ là điểm vào.
- Đẩy xong → `DanhDauDaDay`. Lỗi → không đánh dấu, lượt sau thử lại (mẫu sẵn có).

### Bước 5 — Apps Script: cờ `chiDienNeuCo`

`orders/gsheet-apps-script/Code.gs`: khi `body.chiDienNeuCo === true`, đơn **không tra thấy** mã đơn ở bất kỳ tab
nào → **bỏ qua**, không tạo dòng (cả file chính lẫn file phụ), đếm vào phần trả về để soi. Mặc định (vắng cờ) giữ
nguyên hành vi append hiện tại.

### Bước 6 — Chẩn đoán khi trang không render

Khi hết 20s chưa đọc được ô tổng, **trước khi bỏ lượt** thu thập và gửi kèm để C# log:
- `url` thật của tab lúc đó, `document.title`
- `.return-list-summary-title` **có tồn tại mà rỗng** hay **không tồn tại**
- số `a.return-row-item` đang render
- có `.return-case-tab-wrapper` không (đã vào đúng trang chưa)

Bốn dấu hiệu này phân biệt dứt điểm: hết giờ thật / đọc nhầm tab / sai selector. **Đừng nới 20s** trong đợt này —
chưa có dữ liệu thì nới là đoán.

### Bước 7 — Không nuốt cờ `closeShopTab`

`OrdersBridgeSession`, chỗ:

```csharp
try { await _waiter.AwaitAsync(_closeShopTcs, TimeSpan.FromSeconds(30), ct); }
catch (TimeoutException) { L("closeShopTab quá hạn — vẫn tiếp shop kế."); }
```

Giá trị trả về (`ok`) đang **bị vứt**. Bằng chứng production — 3/3 lần, hễ trang trả hàng không render là shop kế
chết:

```
28/07 12:44 chưa render → 12:49 "chờ 30s chưa thấy tab shop mở"
29/07 10:12 chưa render → 10:17 (như trên)
29/07 13:34 chưa render → 13:38 (như trên)
```

Sửa: đọc `ok`; `false` → log rõ ràng và **thử đưa picker về trạng thái sạch** trước khi sang shop kế (gửi lại
`closeShopTab` một lần, hoặc lệnh sẵn có tương đương — **đọc code rồi chọn**). Vẫn không được → **dừng vòng kèm
đúng lý do**, thay vì chết một shop sau với thông báo lạc đề.

## 5. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` + `dotnet build server/Shopee.Hub.Web` sạch, 0 warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` xanh, **không sửa kỳ vọng test cũ nào**.
- [ ] `node --check extensions/shopee-orders/background.js` OK.
- [ ] Test khoá bẫy #1: payload mã-trả **không chứa** `daHuy` (khẳng định bằng serialize thật).
- [ ] Test khoá bẫy #2: sheet giả — mã đơn không có trên sheet + `chiDienNeuCo` → **không tạo dòng**.
- [ ] Test Bước 2 với 3 cặp thật: cửa sổ 20 ngày giữ 2, bỏ 1.
- [ ] Test: mã trả của đơn **KHÔNG còn trong bảng `orders`** vẫn được đẩy lên GSheet (đây là toàn bộ mục đích).

## 6. Rủi ro & lưu ý

- **Bẫy #1 là nguy nhất**: xoá nhầm nền đỏ ở cả hai file, im lặng, khó phát hiện. Phải có test.
- Việc 3–5 cần **dán đè Apps Script + Triển khai phiên bản mới** mới có tác dụng; việc 1,2,6,7 cần release client.
- Mã trả hàng của đơn đã dọn sẽ tới **GSheet** nhưng KHÔNG tới hub (hub cũng chỉ nhận qua đường đẩy đơn). Chấp
  nhận trong đợt này — nêu rõ khi báo cáo.
- Bảng mới là nguồn sự thật MỚI cho mã trả hàng; `orders.return_request_code` từ nay chỉ để hiển thị.

---

## Báo cáo thực thi (Opus điền sau khi xong)
