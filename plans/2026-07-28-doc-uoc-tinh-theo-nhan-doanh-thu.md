# Plan: "Ước tính" hụt 1/3 số đơn — thẻ FinalAmount là remote-component tải chậm

- **Ngày:** 2026-07-28
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh

Người dùng thấy đơn `260728TV14FVU8` trên Google Sheet **thiếu cột "tiền bán"**. Soi ra vấn đề rộng hơn.

### Bằng chứng số: hụt 33% số đơn, và là lỗi CÓ TỪ TRƯỚC

Log production `hoatdong-20260728.log` (`muinx-nuc`, `hoangdh200392:muinx`):

```
12:34  Lấy Số tiền cuối cùng: 2/2 đơn.
12:39  Lấy Số tiền cuối cùng: 0/3 đơn.      ← hụt sạch
12:44  Lấy Số tiền cuối cùng: 1/1 đơn.
13:09  Lấy Số tiền cuối cùng: 3/3 đơn.
13:19  Lấy Số tiền cuối cùng: 7/10 đơn.
13:28  Lấy Số tiền cuối cùng: 0/1 đơn.      ← hụt sạch
15:15  Đọc sản phẩm trang chi tiết: 4 đơn, 10 sản phẩm.
15:15  Lấy Số tiền cuối cùng: 3/4 đơn.
                            ⇒ CẢ NGÀY: 16/24 đơn (66%), HỤT 8 đơn
```

Các lượt `0/3`, `0/1` từ 12:39 — **trước** mọi thay đổi hôm nay ⇒ lỗi cũ. Bước đọc sản phẩm chỉ làm nó lộ ra.

### Nguyên nhân THẬT: thẻ FinalAmount là remote-component tải bất đồng bộ

> **Ghi lại để không ai đi nhầm đường lần nữa:** ban đầu Fable kết luận sai rằng selector `[type='FinalAmount']`
> không tồn tại — vì hai mẫu HTML đầu người dùng gửi chỉ là khối `.product-payment-wrapper`, vốn KHÔNG chứa thẻ
> này. Người dùng gửi tiếp mẫu thứ ba và bác bỏ. **Selector hiện tại ĐÚNG, đừng đổi.**

HTML thật của thẻ:

```html
<div class="remote-component">
  <div class="cardStyle" type="FinalAmount" success="e=>h(e)" fail="e=>o('renderFail',e)">
    <div class="card-header">
      <i class="eds-icon">…</i>
      <div class="card-title">
        <div class="eds-popover eds-popover--light">
          <div class="eds-popover__ref">Số tiền cuối cùng <!--v-if--></div>
        </div>
      </div>
      <div class="amount">₫374.227</div>
    </div>
  </div>
</div>
```

Ba điểm quyết định:
- `class="remote-component"` — mảnh giao diện **tải riêng, bất đồng bộ**, KHÔNG cùng nhịp với trang chính.
- Có hẳn nhánh `fail="e=>o('renderFail',e)"` ⇒ Shopee tự lường trước việc nó **render hỏng**.
- Cùng lượt, cùng cách mở tab: **sản phẩm 4/4** (thuộc trang chính, render ngay) mà **ước tính 3/4** ⇒ trang đã
  render xong, vấn đề nằm ở riêng mảnh remote này.

⇒ Poll 15s hiện tại **không đủ** cho một phần số đơn. Đây là bài toán CHỜ + NGUỒN DỰ PHÒNG, không phải selector.

### Nguồn dự phòng có sẵn ngay trên trang chính

Bảng doanh thu (`.payment-info-details` → `.income-container`) thuộc **trang chính**, render cùng `.product-list`,
và có đúng con số đó ở dòng cuối:

```html
<div class="income-item income-subtotal strong highlighted">
  <div class="income-label"><div class="income-label-text">Doanh thu đơn hàng ước tính <i …>…</i></div></div>
  <div class="income-value"><!----> ₫374.227</div>
</div>
```

Đã đối chiếu trên mẫu thật: `₫374.227` ở bảng này **khớp** `₫374.227` ở thẻ remote. Dùng làm đường dự phòng khi
thẻ remote không về.

### ⚠ BẪY khi đọc bảng doanh thu: ba dòng cùng chứa chữ "ước tính"

```
Tổng phí vận chuyển ước tính   = ₫0
Phí vận chuyển ước tính        = -₫18.300
Doanh thu đơn hàng ước tính    = ₫374.227   ← thứ CẦN lấy
```

Khớp kiểu "nhãn chứa *ước tính*" sẽ lấy nhầm **phí vận chuyển** rồi ghi số sai lên Google Sheet — tệ hơn để
trống. Phải khớp **CẢ** `doanh thu` **VÀ** `uoc tinh`. Đây là **lần thứ ba trong tuần** dính lớp lỗi "nhãn chứa
nhãn khác" (xem `2026-07-28-ghim-class-that-trang-tra-hang.md`, `2026-07-28-doc-san-pham-tung-don.md`).

Thêm: nhãn có **tooltip lẫn vào text** — lấy thô ra `"Doanh thu đơn hàng ước tính .cls-1{fill-rule:evenodd;} question"`.

## 2. Phạm vi

**Làm:**
- Nới thời gian chờ thẻ remote, và **chờ đúng thứ cần** (`.amount` có nội dung) thay vì chờ mù.
- Thêm **đường dự phòng** đọc bảng doanh thu trên trang chính khi thẻ remote không về.
- Log rõ đơn nào hụt + hụt vì lý do gì, để lần sau soi được.

**Không làm:**
- KHÔNG đổi/bỏ hai đường dò hiện có — chúng ĐÚNG, đang chạy tốt cho ~2/3 số đơn.
- KHÔNG thêm lượt `chrome.tabs.create`, KHÔNG đổi trần 30 đơn/lượt.
- KHÔNG đụng bước đọc sản phẩm, bước trả hàng, cách lưu `final_amount` / cờ `gsheet_da_co_uoc_tinh`.
- KHÔNG commit, KHÔNG deploy, KHÔNG release. KHÔNG đụng `%LOCALAPPDATA%\Programs\ShopeeSuite`.

## 3. Các bước

### Bước 1 — Chờ thông minh hơn trong `doSyncOrderFinals`

Hiện tại: poll `pageReadFinalAmount` mỗi 500ms, trần **15s**, hụt thì bỏ.

Sửa:
- Nới trần lên **30s** — nhưng **chỉ khi đáng chờ**: nếu đã thấy thẻ `[type='FinalAmount']` trong DOM mà `.amount`
  chưa có nội dung thì remote đang tải ⇒ chờ tiếp. Nếu **quá 8s vẫn không thấy thẻ nào**, coi như bố cục không có
  thẻ đó ⇒ **thôi chờ**, sang đường dự phòng ngay (đừng đốt 30s vô ích cho mọi đơn).
- Cần một page-func nhỏ trả trạng thái, vd `pageFinalAmountState()` → `"co-so"` / `"dang-tai"` / `"khong-thay"`,
  để C#/JS quyết định chờ tiếp hay bỏ. Đừng nhét luật chờ vào `pageReadFinalAmount` (giữ hàm đó thuần đọc).

Cân nhắc chi phí: 30 đơn/lượt × thêm 15s = tối đa +7,5 phút/lượt **nếu mọi đơn đều hỏng**. Thực tế chỉ ~1/3 đơn
phải chờ thêm, và chỉ tới khi thẻ về. Vẫn phải nêu con số xấu nhất này trong báo cáo.

### Bước 2 — Đường dự phòng: bảng doanh thu trang chính

Thêm vào `pageReadFinalAmount` **sau** hai đường hiện có (đường 3):

- Duyệt `.income-item`; lấy nhãn từ `.income-label-text`, **bỏ node con** `<svg>`/`<i>`/`.eds-popover` trước khi
  lấy text (tooltip lẫn vào — xem ⚠).
- Khớp khi nhãn (bỏ dấu + hạ chữ) chứa **CẢ** `doanh thu` **VÀ** `uoc tinh`. Khối có class `highlighted` thì ưu tiên.
- Giá trị = text `.income-value` cùng khối, gộp khoảng trắng (chuỗi thật `" ₫374.227"`).
- Không thấy → trả `""` như cũ.

Trả **text thô** cho C# parse — giữ nếp hiện tại (`MergeFinalAmounts` gọi `ShopeeShippingNav.ParseVndAmount`).

### Bước 3 — Kiểm `ParseVndAmount` với chuỗi bố cục mới

**Đọc code trước rồi mới kết luận.** Nếu đã đúng thì CHỈ thêm test, KHÔNG sửa hàm:
- [ ] `"₫374.227"` → `374227`; `" ₫923.774"` → `923774`; `"₫0"` → `0`.
- [ ] `"-₫18.300"` → nêu rõ hành vi hiện tại trong báo cáo, **đừng đổi** nếu nơi khác đang dựa vào.

### Bước 4 — Kiểm chứng bằng jsdom trên HTML THẬT

Khuôn có sẵn: `scratchpad/kiemtra-sanpham.js`, `scratchpad/chay3sp.js` — tách **nguyên văn** thân hàm từ
`background.js` rồi chạy trong jsdom, KHÔNG chép tay. Ca cần phủ:

- [ ] **Thẻ remote ĐÃ về** (dựng đúng HTML ở mục 1) → ra `"₫374.227"` qua đường 1. *(hồi quy — đường đang chạy)*
- [ ] **Thẻ remote có mà `.amount` RỖNG** (đang tải) → `pageReadFinalAmount` trả `""`, và hàm trạng thái báo
      `"dang-tai"` để caller chờ tiếp.
- [ ] **KHÔNG có thẻ remote**, chỉ có bảng doanh thu → đường 3 ra đúng số của dòng "Doanh thu đơn hàng ước tính".
- [ ] **BẪY**: cùng bảng đó, KHÔNG được trả `₫0` (Tổng phí vận chuyển ước tính) hay `-₫18.300` (Phí vận chuyển
      ước tính) hay `₫982.000` (Tổng tiền sản phẩm).

Mẫu thật lưu ở `scratchpad/don3sp-full.html` (đơn 3 SP) và mẫu đơn 2 SP + mẫu thẻ FinalAmount người dùng gửi
28/07 (lấy từ transcript). **Không đọc được mẫu thì DỪNG và báo lại** — đừng bịa HTML rồi bảo đã kiểm chứng.

### Bước 5 — Log để lần sau soi được

Hiện chỉ có `Lấy Số tiền cuối cùng: 3/4 đơn.` — không biết đơn nào, vì sao. Thêm:
- Mã đơn hụt (tối đa 3) + lý do phân biệt được: `không thấy thẻ` / `thẻ đang tải, hết giờ` / `đọc được qua bảng
  doanh thu`.
- Đếm riêng số đơn lấy được **qua đường dự phòng** — để biết bố cục nào đang phổ biến.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` sạch, 0 warning mới; `dotnet test orders/XuLyDonShopee.Tests` xanh, không
      sửa kỳ vọng test cũ nào.
- [ ] `node --check extensions/shopee-orders/background.js` OK.
- [ ] Rig jsdom: đủ 4 ca ở Bước 4, dán kết quả chạy thật vào báo cáo.
- [ ] Số lượt `chrome.tabs.create` không đổi (chỉ ra chỗ gọi).
- [ ] Nêu rõ trong báo cáo: thời gian xấu nhất thêm vào mỗi lượt sync.

## 5. Rủi ro & lưu ý

- **Đừng đổi hai đường dò hiện có.** Chúng đúng; ~2/3 đơn đang đọc được nhờ chúng. Chỉ THÊM.
- **Bẫy "ước tính" phải khớp cả "doanh thu"** — ghi nhầm phí vận chuyển lên sheet còn tệ hơn để trống.
- **Đừng chờ mù 30s cho mọi đơn** — chỉ chờ khi thẻ có mặt mà chưa có số. Chờ mù làm chậm cả lượt sync.
- Bảng doanh thu nằm trong khối có nút gập ("Ẩn chi tiết doanh thu"). Nếu người dùng/Shopee để mặc định GẬP thì
  `.income-container` có thể không render ⇒ đường 3 trượt. **Không bấm nút gập** (thêm thao tác = thêm rủi ro);
  chỉ đọc khi có, không có thì thôi.
- Sửa xong, các đơn đang hụt **tự được điền bù** ở lượt sau (`gsheet_da_co_uoc_tinh = 0` vẫn đang chờ, và Apps
  Script bản mới điền được vào ô trống) ⇒ không cần backfill tay.

---

## Báo cáo thực thi (Opus điền sau khi xong)
