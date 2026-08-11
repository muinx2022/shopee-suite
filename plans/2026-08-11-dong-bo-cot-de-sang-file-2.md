# Plan: Gõ tay cột D/E ở file 1 → tự đồng bộ sang file 2 (key = cột A)

- **Ngày:** 2026-08-11
- **Trạng thái:** hoàn thành — chờ người dùng dán đè Apps Script + bấm menu "Bật đồng bộ (cài trigger)"
- **Người lập / thực thi:** phiên chính

## 1. Bối cảnh & mục tiêu

`orders/gsheet-apps-script/Code.gs` hiện chỉ có **một** đường vào: `doPost` — app desktop đẩy lô đơn lên, script
ghi file chính rồi ghi song song A/B/C/E sang file phụ "Quản Lý Đơn 2" (`ID_FILE_PHU`).

Hai cột người dùng **gõ tay** ở file 1 thì file 2 không bao giờ thấy:

| Cột file 1 | Tiêu đề | Ai điền |
|---|---|---|
| D | `mã đơn đặt` | **người dùng gõ tay** — plan 28/07 ghi rõ "script KHÔNG đụng tới" |
| E | `Mã Đơn Trả Hàng` | app đẩy được, nhưng người dùng cũng gõ/sửa tay |

Yêu cầu: **nhập D hoặc E ở file 1 → tự sync sang file 2**, **khớp dòng theo cột A (mã đơn)**.

`doPost` không giúp được vì nó chỉ chạy khi app POST. Cần một đường thứ hai: **trigger "khi chỉnh sửa"**.

### Vì sao phải là trigger CÀI ĐẶT, không phải `onEdit` đơn giản

`onEdit(e)` simple trigger chạy **không có quyền uỷ nhiệm** ⇒ `SpreadsheetApp.openById()` **ném lỗi**, không mở
được file 2. Bắt buộc dùng **installable trigger** (`ScriptApp.newTrigger(...).forSpreadsheet(ss).onEdit()`),
chạy dưới quyền người bấm cài — người đó phải có **quyền sửa file 2**.

## 2. Phạm vi

**Làm** — toàn bộ trong `orders/gsheet-apps-script/Code.gs`:
- Trigger `onEditDongBoFilePhu(e)`: sửa ô ở cột có tiêu đề thuộc `COT_DONG_BO_TAY` → ghi sang file 2 đúng dòng
  có cùng mã đơn ở cột A.
- Menu `onOpen`: bật trigger · đồng bộ lại toàn bộ · kiểm tra cấu hình.
- `dongBoLaiToanBo()`: quét ngược toàn bộ D/E đã gõ trước khi có trigger (dữ liệu cũ không tự sang được).
- Nhớ ID file phụ mà `doPost` đang dùng vào Script Properties, để trigger dùng **đúng cái file đó**.

**Không làm:**
- KHÔNG đổi bất kỳ hành vi nào của `doPost` (ngoài một dòng ghi nhớ ID file phụ).
- KHÔNG đồng bộ ngược file 2 → file 1 (một chiều, đúng yêu cầu).
- KHÔNG tạo dòng mới ở file 2 (xem bẫy #2).
- KHÔNG đụng code C#/extension. KHÔNG commit, KHÔNG deploy, KHÔNG release.

## 3. ⚠ Năm cái bẫy

1. **Tra cột THEO TIÊU ĐỀ, không theo số cột D/E.** Đây đúng là lỗi đã mất mấy ngày dữ liệu hồi 28/07: chèn một
   cột vào giữa là mọi số cột cứng lệch **âm thầm**. Watch theo tiêu đề (`mã đơn đặt`, `mã đơn trả hàng`) thì
   chèn/đổi chỗ cột bao nhiêu lần cũng bám đúng cột. Hôm nay hai tiêu đề đó nằm ở D và E.
2. **KHÔNG tra thấy mã đơn ở file 2 ⇒ BỎ QUA, không append.** Đơn **đã hủy** cố ý không có dòng ở file 2
   (`filePhu.boQuaHuy`); tạo dòng ở đây là phá đúng luật đó, lại đẻ dòng gần-như-rỗng. Phải **báo ra toast** chứ
   không nuốt im lặng.
3. **Ô đích có CÔNG THỨC thì không đụng** — cùng lằn ranh với `ghiNeuTrong`/`ghiDeNeuKhac` sẵn có.
4. **Ghi ĐÈ khi khác, không phải "chỉ điền ô trống".** Người dùng sửa lại D ở file 1 mà file 2 giữ giá trị cũ =
   lệch **im lặng** — đúng lớp lỗi mà `ghiDeNeuKhac` đã phải sinh ra để vá (mã trả hàng đổi, 30/07).
5. **Dán nhiều dòng một lúc** (`e.range` trải nhiều dòng × nhiều cột) phải xử đủ, có **trần** `TRAN_O_DONG_BO` và
   trần đó phải **báo ra**, không cắt lặng.

## 4. Quyết định đã chốt

| Điểm | Chốt | Lý do |
|---|---|---|
| Khớp dòng | **cột A (mã đơn)**, dò MỌI tab file 2 | user chốt |
| Không thấy mã ở file 2 | bỏ qua + toast | bẫy #2 |
| Xóa ô ở file 1 | **xóa theo** ở file 2 | sync nửa vời = lệch im lặng; Ctrl+Z ở file 1 bắn lại trigger nên tự lành |
| Xóa ô khi chạy `dongBoLaiToanBo` | **KHÔNG xóa**, chỉ ghi ô có giá trị | quét 3000 dòng cũ: ô trống hầu hết là "chưa từng điền", không phải "vừa cố ý xoá" |
| ID file 2 | Script Properties (do `doPost` ghi) → lùi về `ID_FILE_PHU` | app đổi cấu hình `sheet2` thì trigger đi theo, không ghi nhầm file |

## 5. Tiêu chí nghiệm thu

- [ ] `node --check` trên `Code.gs` OK.
- [ ] Sim `scratchpad/sim-dongbo-de.js` phủ đủ, có kết quả chạy thật:
  - [ ] Sửa D → file 2 nhận đúng dòng cùng mã đơn.
  - [ ] Sửa E đè giá trị CŨ KHÁC → file 2 đổi theo.
  - [ ] Mã đơn không có ở file 2 → **không** đẻ dòng mới, có báo.
  - [ ] Dán khối D:E nhiều dòng → sync đủ mọi ô.
  - [ ] Ô đích có công thức → giữ nguyên công thức.
  - [ ] Xóa ô ở file 1 → file 2 xóa theo.
  - [ ] Sửa cột khác (tiền đặt) / sửa dòng tiêu đề → file 2 **không đổi**.
  - [ ] Chèn thêm cột vào giữa file 1 (D/E dịch phải) → vẫn bám đúng tiêu đề.
  - [ ] `dongBoLaiToanBo` điền dữ liệu cũ, **không** xoá ô file 2 khi nguồn trống.
- [ ] Hồi quy: `node scratchpad/sim-file2.js` + `node scratchpad/sim-ma-tra-hang.js` ra kết quả y như trước.
- [ ] Đọc lại diff: đường ghi của `doPost` không đổi hành vi.

## 6. Rủi ro & lưu ý

- File thật nằm trên Google: **dán đè + Lưu**, rồi bấm menu **"Đồng bộ file 2 → Bật đồng bộ (cài trigger)"**.
  Chỉ dán mà không cài trigger thì không có gì chạy. (Đây là đường KHÁC với "Triển khai → Phiên bản mới" của
  Web App — muốn `doPost` đổi theo thì vẫn phải triển khai lại như cũ.)
- Người bấm cài trigger phải có **quyền sửa file 2**, vì trigger chạy dưới quyền người đó.
- Trigger chỉ chạy khi **người dùng gõ tay**. Ô đổi do **công thức tính lại** hoặc do script khác ghi thì
  Google **không** bắn `onEdit` — đó là giới hạn của nền tảng, không phải bug.

---

## Báo cáo thực thi

**Đã xong.** Diff: `orders/gsheet-apps-script/Code.gs` +397 / −1 (dòng bị xoá duy nhất là dòng `banDoMaDon`
được thay bằng bản chấp nhận `tabDich = null`). Đường ghi của `doPost` chỉ thêm đúng **một** dòng
`nhoIdFilePhu(idPhu)`. Sim mới: `scratchpad/sim-dongbo-de.js`.

### Kiểm chứng THẬT (chạy trên máy dev, 2026-08-11)

- `node --check` trên `Code.gs`: **OK**.
- `node scratchpad/sim-dongbo-de.js`: **ĐẠT 52 / TRƯỢT 0**, phủ 17 ca (đủ mọi tiêu chí ở mục 5).
- **Thử phá — 6 kiểu bẻ gãy, cả 6 đều làm sim ĐỎ** (bản gãy sinh bằng `scratchpad/pha.js` trong thư mục tạm,
  sim nhận biến môi trường `CODE_GS` để trỏ sang bản gãy):

  | Bẻ gãy | Kết quả |
  |---|---|
  | Bỏ guard "không thấy mã đơn" ⇒ đẻ dòng ở file 2 | 4 ca đỏ |
  | `datOFilePhu` quay về "chỉ điền ô trống" | 4 ca đỏ |
  | Tra cột theo SỐ CỘT CỨNG D/E thay vì tiêu đề | 3 ca đỏ |
  | Cắt trần lặng (không đếm, không báo) | 2 ca đỏ |
  | `dongBoLaiToanBo` xoá ô khi nguồn trống | 1 ca đỏ |
  | Bỏ lọc dòng tiêu đề | 2 ca đỏ |

- Hồi quy `node scratchpad/sim-file2.js`: kết quả **y như trước** (file phụ nhận A/B/C/E, D vẫn trống).
- Hồi quy `node scratchpad/sim-ma-tra-hang.js`: **1 ca đỏ — CÓ SẴN TỪ TRƯỚC, không phải do việc này.** Đã
  chạy đối chứng trên `git show HEAD:...Code.gs` và đỏ y hệt. Assert của sim đòi màu chữ phủ tới cột **M**,
  còn code cố ý chỉ tô `SO_COT_TO_MAU = 11` (A..K) — **assert trong scratchpad bị lệch, không phải lỗi code**.
  Chưa sửa (ngoài phạm vi việc này).

### Còn lại — người dùng phải tự làm trên Google

1. Dán đè `Code.gs` lên script.google.com → **Lưu**.
2. Menu mới **"Đồng bộ file 2"** hiện sau khi tải lại sheet → bấm **"Bật đồng bộ (cài trigger)"**, chấp nhận
   uỷ quyền. Tài khoản bấm cài phải có quyền **sửa** file 2.
3. Bấm **"Đồng bộ lại toàn bộ"** một lần cho dữ liệu D/E đã gõ từ trước.
4. Muốn `doPost` cũng chạy bản mới thì vẫn phải **Triển khai → Phiên bản mới** như cũ (đường riêng, không liên
   quan tới trigger).

Chưa commit, chưa dán lên Google.
