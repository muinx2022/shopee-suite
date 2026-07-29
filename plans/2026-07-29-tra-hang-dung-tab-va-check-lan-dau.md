# Plan: Check đơn trả hàng — bấm đúng tab "Đơn Trả hàng Hoàn tiền" + lần đầu PHẢI check

- **Ngày:** 2026-07-29
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh — hai lỗi làm tính năng gần như không chạy

Từ lúc phát hành tới nay, số mã trả hàng lấy được thực tế là **0**. Log production 29/07 cho thấy đủ nguyên nhân.

### Lỗi 1 — đọc nhầm tab: số bị đơn Hủy thổi lên

App điều hướng tới trang Trả hàng/Hoàn tiền/Hủy rồi **đọc thẳng ô tổng, không chọn tab nào** ⇒ lấy con số của tab
mặc định **"Tất cả"**, vốn gộp cả ba loại:

```
Tất cả │ Đơn Trả hàng Hoàn tiền │ Đơn Hủy │ Đơn Giao hàng không thành công
  ↑ đang đọc ở đây
```

**Đơn Hủy** và **Đơn Giao hàng không thành công** KHÔNG có mã yêu cầu trả hàng. Nên mỗi lần một đơn hủy mới xuất
hiện, số tăng → app đi quét → về tay không. Log bắt đúng cảnh đó:

```
11:14  deilca.store: 141 yêu cầu — TĂNG 1 so với mốc 140, check 1 dòng đầu.
11:14  đọc 1 dòng → 0 cặp đủ hai mã, 1 dòng THIẾU mã yêu cầu.
11:14  260729US91P2N2: khối=[class='id order-id' nhãn='Mã đơn hàng']     ← chỉ có order-id, không có return-id
```

Người dùng đã xác nhận trên giao diện: **"cần phải click vào ô Đơn trả hàng hoàn tiền chứ không phải tất cả"**.

### Lỗi 2 — lần đầu chỉ ghi mốc, KHÔNG check dòng nào

`TraHangParser.QuyetDinhCheck`: `mocCu is null` → `LuatSoYeuCau.LanDau` với `SoDongCanCheck = 0`.

Hệ quả: mọi shop chốt mốc ở lần đầu mà không đọc dòng nào, các lần sau số không đổi nên bỏ qua mãi ⇒ **43 yêu cầu
của onicom, 340 của cicily, 141 của deilca chưa từng được đọc lấy một dòng**. Chỉ yêu cầu phát sinh SAU mốc mới
được quét — mà lần duy nhất phát sinh lại rơi vào Lỗi 1.

Người dùng: *"lần đầu check số và check đơn, lần sau mới bỏ qua chứ?"* — đúng, đây là lỗi thiết kế ban đầu.

### Đọc bao nhiêu dòng ở lần đầu — dữ liệu quyết định

DB client chỉ giữ **26 đơn**, khoảng mã `260725 → 260729` (5 ngày; đơn kết thúc bị dọn sau khi ghi sheet):

```
cicily.store: 340 yêu cầu trong danh sách  ↔  1 đơn trong DB
deilca.store: 141 yêu cầu                  ↔  5 đơn
onicom.store:  43 yêu cầu                  ↔  0 đơn
```

`OrdersRepository.SetReturnRequestCodes` **bỏ qua** mã không khớp đơn nào trong DB. Nên đọc hết lịch sử là vô ích:
đọc 340 dòng cũng chỉ có tối đa 1 dòng gắn được. ⇒ Lần đầu đọc **trang đầu, trần 50 dòng** (`MAX_RETURN_ROWS`).
**KHÔNG phân trang.**

### Chặn thêm theo THỜI GIAN (người dùng chốt 29/07)

> *"nhưng cũng phải chốt lại time chứ, không lẽ lấy ra toàn bộ những đơn cũ?"*

Trần 50 dòng mới chặn được CHI PHÍ, chưa chặn được PHẠM VI. Thêm cửa sổ **7 ngày**, dùng lại đúng khuôn đã có ở
nhánh lấy bù ước tính (`NgayDonTuMa` + `SoNgayBuUocTinh`) — **một khái niệm, một con số**, đừng đẻ hằng thứ hai.

Ngày suy từ **6 ký tự đầu mã đơn** (`yyMMdd`): `260729US91P2N2` → 29/07/2026. Không cần cào thêm trường ngày nào.

**⚠ Điểm tinh tế phải hiểu đúng:** danh sách được sắp theo **NGÀY YÊU CẦU** (mới → cũ), còn mã đơn cho **NGÀY ĐẶT
ĐƠN**. Hai thứ KHÁC nhau — một yêu cầu trả hàng hôm nay có thể thuộc đơn đặt từ 20 ngày trước. Hệ quả:
- **KHÔNG được dừng sớm** khi gặp dòng đầu tiên quá 7 ngày (dòng sau vẫn có thể trong hạn) ⇒ đọc đủ trang rồi
  **LỌC**, không `break`.
- Mã đơn không parse được ngày → **bỏ qua dòng đó** (không đoán), đếm riêng để log.

Lọc theo ngày đặt là hợp lý vì đơn cũ hơn cửa sổ đó gần như chắc chắn đã bị dọn khỏi DB (đơn kết thúc bị xoá sau
khi ghi sheet) ⇒ có lấy mã cũng không gắn vào đâu được.

### HTML tab-strip (người dùng gửi 29/07)

```html
<div class="return-case-tab-wrapper">
  <div class="case-tab-container">
    <div class="eds-tabs eds-tabs-line eds-tabs-normal eds-tabs-top">
      <div class="eds-tabs__nav"><div class="eds-tabs__nav-warp">
        <div class="eds-tabs__nav-tabs" style="transform: translateX(0px);">
          <div class="eds-tabs__nav-tab" style="white-space: normal;">Tất cả <!----></div>
          <div class="eds-tabs__nav-tab active" style="white-space: normal;">Đơn Trả hàng Hoàn tiền <!----></div>
          <div class="eds-tabs__nav-tab" style="white-space: normal;">Đơn Hủy <!----></div>
          <div class="eds-tabs__nav-tab" style="white-space: normal;">Đơn Giao hàng không thành công <!----></div>
```

## 2. ⚠ Bốn cái bẫy

1. **Tab KHÔNG có `data-testid`** — buộc phải nhận theo TEXT. Đây là ngoại lệ so với tab điều hướng trái
   (`[data-testid="l1-tab-return_refund_cancel"]`) vẫn giữ nguyên.
2. **Phải THU HẸP phạm vi tìm.** Thanh điều hướng trái có mục **"Đơn Trả hàng/Hoàn tiền hoặc Đơn hủy"** — tìm text
   "trả hàng" trên cả trang là bấm nhầm sang đó. Chỉ duyệt `.eds-tabs__nav-tab` **bên trong**
   `.return-case-tab-wrapper`.
3. **Đã đúng tab rồi thì ĐỪNG bấm.** Tab đang chọn mang class `active`. Bấm lại rồi ngồi chờ danh sách vẽ lại (mà
   nó không vẽ lại) là tự đốt thời gian mỗi shop.
4. **Đổi tab xong, danh sách và ô tổng vẽ LẠI** ⇒ phải chờ rồi mới đọc số, và **sắp xếp phải áp SAU khi đổi tab**
   (đổi tab nhiều khả năng reset sắp xếp về mặc định "Ngày đến hạn").

## 3. Phạm vi

**Làm:**
- Bấm tab "Đơn Trả hàng Hoàn tiền" trước khi đọc số + quét dòng.
- Lần đầu (chưa có mốc) → **CHECK** `min(số yêu cầu, 50)` dòng đầu, rồi mới chốt mốc.
- Mốc cũ (đếm theo "Tất cả") phải mất hiệu lực — xem Bước 3.

**Không làm:**
- KHÔNG phân trang (chỉ trang đầu, trần `MAX_RETURN_ROWS` hiện có).
- KHÔNG đụng luật `KhongDoi` / `Giam` / `Tang` — đang đúng.
- KHÔNG đụng bước A/B/C/D của flow shop (đọc đơn, ước tính, sản phẩm, chuẩn bị hàng, in phiếu). Nhánh trả hàng là
  mắt xích CUỐI, độc lập.
- KHÔNG commit, KHÔNG deploy, KHÔNG release. KHÔNG đụng `%LOCALAPPDATA%\Programs\ShopeeSuite`.

## 4. Các bước

### Bước 1 — Extension: chọn tab trước khi đọc

`extensions/shopee-orders/background.js`, trong `doReadReturnRequests`, **sau** khi trang trả hàng đã mở và
**trước** bước đổi sắp xếp:

- Page-func mới, vd `pageLocateReturnCaseTab(reSrc)`: duyệt
  `.return-case-tab-wrapper .eds-tabs__nav-tab` (xem bẫy #2), so text đã chuẩn hoá **không dấu** với `reSrc`.
  - Tab khớp mà **đã** có class `active` → trả `{ daDung: true }` ⇒ caller KHÔNG bấm (bẫy #3).
  - Khớp mà chưa active → trả toạ độ tâm để `trustedClick`.
  - Không tìm thấy tab nào khớp → trả `null` ⇒ caller **vẫn đi tiếp** với tab hiện tại, nhưng gắn cờ
    `tabTraHang: false` để C# log CẢNH BÁO (số có thể lẫn đơn hủy). Đừng làm hỏng cả bước chỉ vì Shopee đổi nhãn.
- Hằng biểu thức khớp, cạnh `SORT_NEWEST_RE`:
  `const RETURN_TAB_RE = "don tra hang hoan tien";` — so trên text đã bỏ dấu + hạ chữ (dùng `_na` sẵn có).
- Bấm xong: chờ danh sách vẽ lại (poll ô tổng đổi giá trị **hoặc** số dòng đổi, trần ~8s) rồi mới sang bước sắp xếp.

Trả thêm `tabTraHang` trong payload `kind:"returns"` để C# biết đã đúng tab hay chưa.

### Bước 2 — `TraHangParser`: lần đầu PHẢI check

`QuyetDinhCheck(int? mocCu, int soMoi)` — nhánh `mocCu is null`:

```
LanDau → SoDongCanCheck = min(soMoi, tranDong)
```

Thêm tham số `tranDong` (mặc định 50, khớp `MAX_RETURN_ROWS`) — **đừng chôn số 50 ở hai nơi**; nêu rõ trong báo
cáo bạn đồng bộ hai hằng này thế nào.

Giữ nguyên `KhongDoi` / `Giam` → 0, `Tang` → hiệu số.

**Lọc theo cửa sổ 7 ngày** (áp cho CẢ `LanDau` lẫn `Tang`): sau khi `GhepCap` ra danh sách cặp, loại cặp có ngày
đặt (suy từ mã đơn) cũ hơn `homNay - SoNgayBuUocTinh`. Tách thành hàm THUẦN riêng để test được, vd
`LocTheoCuaSo(cap, homNay, soNgay)` → `(GiuLai, BoQuaVìCu, BoQuaVìKhongDocDuocNgay)`. **Đừng** lọc bên trong
`GhepCap` (giữ hàm đó thuần về tách mã).

Cập nhật doc của `LuatSoYeuCau.LanDau` (hiện ghi *"CHỈ ghi nhớ số, không check dòng nào"* — sẽ sai).

Test:
- [ ] `mocCu = null, soMoi = 12` → `LanDau`, check **12**.
- [ ] `mocCu = null, soMoi = 340` → `LanDau`, check **50** (kẹp trần).
- [ ] `mocCu = null, soMoi = 0` → `LanDau`, check 0 (không có gì để đọc).
- [ ] Ba nhánh còn lại giữ nguyên hành vi cũ — **test cũ không được sửa**.

Test cho `LocTheoCuaSo`:
- [ ] Cặp có mã `260729…` với hôm nay 29/07 → GIỮ.
- [ ] Cặp có mã `260701…` (28 ngày trước) → BỎ vì cũ.
- [ ] Dòng CŨ nằm GIỮA hai dòng mới → chỉ bỏ đúng dòng đó, **không dừng sớm** (⚠ mục 1).
- [ ] Mã đơn không parse được ngày (vd `ABCDEF…`) → bỏ, đếm vào nhóm riêng, KHÔNG ném.
- [ ] Danh sách rỗng → trả rỗng, không ném.

### Bước 3 — Mốc cũ phải mất hiệu lực (đếm theo tab khác)

Mốc đang lưu ở `account_shops.return_count_last` là số của tab **"Tất cả"** — sau khi đổi tab, so nó với số mới là
so hai đại lượng khác nhau (vd 141 → 12 sẽ thành nhánh `Giam`, không check gì, mà mốc mới lại chốt sai).

**Cách làm, ưu tiên phương án tự lành, KHÔNG viết script reset:** thêm cột MỚI
`account_shops.return_count_last_tra_hang` (đúng khuôn `EnsureColumn` sẵn có) và dùng nó thay cột cũ. Cột mới bắt
đầu `NULL` ⇒ mọi shop rơi vào `LanDau` ⇒ **tự quét trang đầu một lượt** rồi vào nếp. Cột cũ giữ nguyên, không đọc
nữa (đừng xoá — dữ liệu chẩn đoán, và xoá cột trong SQLite phiền).

Ghi rõ trong doc vì sao có hai cột, kẻo người sau tưởng trùng lặp.

### Bước 4 — Log cho đọc được

- Khi `tabTraHang == false`: `⚠ Check đơn trả hàng: KHÔNG chọn được tab "Đơn Trả hàng Hoàn tiền" — số có thể lẫn đơn hủy.`
- Nhánh `LanDau` đổi thông điệp: `LẦN ĐẦU — check N dòng đầu rồi ghi mốc.` (hiện đang ghi "chỉ ghi nhớ mốc, không
  check dòng nào" — sẽ sai).
- Sau khi lọc cửa sổ: `bỏ k dòng vì đơn cũ hơn 7 ngày` (và số dòng không đọc được ngày, nếu có) — để nhìn log biết
  vì sao đọc 50 dòng mà chỉ lưu vài mã, khỏi tưởng hỏng.

### Bước 5 — Kiểm chứng bằng jsdom trên HTML THẬT

Khuôn có sẵn: `scratchpad/chay3sp.js` — tách **nguyên văn** thân hàm từ `background.js` rồi chạy trong jsdom.
Dùng đúng HTML tab-strip ở mục 1. Ca cần phủ:

- [ ] Tab "Đơn Trả hàng Hoàn tiền" **đang active** → trả `daDung: true`, KHÔNG trả toạ độ (bẫy #3).
- [ ] Tab "Tất cả" đang active → trả **toạ độ của tab "Đơn Trả hàng Hoàn tiền"**, không phải tab khác.
- [ ] Trang có thêm mục sidebar `"Đơn Trả hàng/Hoàn tiền hoặc Đơn hủy"` → **KHÔNG** khớp vào đó (bẫy #2).
- [ ] Không có `.return-case-tab-wrapper` → trả `null`, không ném.

Dán kết quả chạy thật vào báo cáo.

## 5. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` sạch, 0 warning mới; `dotnet test orders/XuLyDonShopee.Tests` xanh, **không
      sửa kỳ vọng test cũ nào**.
- [ ] `node --check extensions/shopee-orders/background.js` OK.
- [ ] Đủ 4 ca jsdom ở Bước 5, có kết quả chạy thật.
- [ ] Số lượt `chrome.tabs.create` không đổi.
- [ ] Khẳng định trong báo cáo: trần 50 dòng chỉ khai báo ở MỘT chỗ (hoặc chỉ rõ cách hai hằng được giữ đồng bộ).

## 6. Rủi ro & lưu ý

- **Đừng bấm tab khi đã đúng tab** — mỗi shop tốn thêm một vòng chờ vô ích, nhân với 12 shop mỗi vòng.
- **Đừng tìm text trên cả trang** — sidebar có mục tên gần giống, bấm nhầm là lạc trang.
- Lượt đầu sau khi cài bản này sẽ **nặng hơn bình thường**: mỗi shop quét trang đầu một lượt (≤50 dòng). Từ lượt
  hai trở đi chỉ quét phần tăng thêm. Đây là chủ ý, không phải lỗi.
- Mã trả hàng của đơn **không còn trong DB** (đã kết thúc và bị dọn) sẽ không gắn được — đếm vào `KhongCoDon` và
  chỉ hiện trong log. Chấp nhận; đây là lý do không phân trang.

---

## Báo cáo thực thi (Opus điền sau khi xong)
