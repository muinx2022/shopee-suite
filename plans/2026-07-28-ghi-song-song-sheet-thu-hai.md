# Plan: Ghi song song sang Google Sheet thứ hai ("Quản Lý Đơn 2", cột A–E)

- **Ngày:** 2026-07-28
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh

Người dùng muốn đẩy thêm dữ liệu sang **file Google Sheet thứ hai**:

```
Tên  : Quản Lý Đơn 2
ID   : 1CK-mu-rtLw0QnGDZ2cuEIkRelEnZkNWuB7Ir_ZuRLhk
Chủ  : hoangdh200392@gmail.com
Tab  : "Trang tính1" (mới tạo, chỉ có dòng tiêu đề)
Dòng 1: A "Mã Đơn Hàng gửi" | B "mã vận đơn gửi" | C "ảnh mã vận đơn gửi" | D "mã đơn đặt" | E "Mã đơn trả hàng"
```

### Người dùng đã chốt

| Điểm | Chốt |
|---|---|
| Phạm vi | **Ghi song song cả hai file** — file cũ giữ nguyên đủ 13 cột, file mới nhận thêm A–E |
| Tab | **Tách theo tháng** như file cũ (`Tháng 07-2026`…), không dồn vào một tab |
| Trùng đơn | **Giống file cũ: chỉ điền ô trống**, không bao giờ đè thứ người dùng gõ tay |

### Câu hỏi "có cần copy Apps Script sang không?" — KHÔNG

Giữ **đúng một** script (bound ở file cũ), cho nó ghi sang file phụ bằng `SpreadsheetApp.openById(ID)`.
Lý do: hai bản script sẽ **trôi lệch nhau**. Chính hôm nay đã mất mấy ngày dữ liệu vì một script có hai nhánh mà
chỉ một nhánh được cập nhật khi thêm cột (xem đầu file `orders/gsheet-apps-script/Code.gs`). Hai file × hai bản
script = lỗi im lặng đó lặp lại nhanh gấp đôi. Một bản triển khai, một URL ⇒ client **không cần đổi cấu hình gì**.

**Điều kiện vận hành (người dùng lo, không phải việc của code):** tài khoản Google triển khai Web App phải có
**quyền sửa** file phụ, nếu không `openById` ném lỗi.

### Chỉ 4 trường được ghi

App cấp được **A, B, C, E**; **D "mã đơn đặt" người dùng tự điền** — y như file cũ. Không có cột tiền/ngày/shop/
phân loại/SKU ở file phụ.

Khác file cũ một điểm nhỏ có lợi: cột A ở file phụ **CÓ tiêu đề** (`Mã Đơn Hàng gửi`), file cũ để trống. Vẫn giữ
`COT_MA_DON = 1` cho cả hai (A là mã đơn ở cả hai file) — đơn giản và đúng.

## 2. Phạm vi

**Làm** — toàn bộ trong `orders/gsheet-apps-script/Code.gs` (bản sao trong repo; file thật nằm trên Google):
- Ghi thêm A/B/C/E của mỗi đơn sang file phụ, tab theo tháng, chống trùng theo mã đơn, chỉ điền ô trống.
- Lỗi ở file phụ **tuyệt đối không** phá đường ghi file chính.
- Báo kết quả file phụ về trong response để soi được.

**Không làm:**
- KHÔNG đổi bất kỳ hành vi nào của file chính (cột, màu, chống trùng, upload phiếu).
- KHÔNG upload file PDF lần thứ hai (xem ⚠ dưới).
- KHÔNG đụng code C#/extension — client không biết gì về file phụ, không thêm cấu hình.
- KHÔNG commit, KHÔNG deploy, KHÔNG release.

## 3. ⚠ Bốn cái bẫy

1. **ĐỪNG upload lại file phiếu.** Cột C của file phụ là *link* phiếu. Nếu gọi lại nhánh `don.fileBase64` thì
   Drive đẻ **hai bản PDF mỗi đơn**, tốn dung lượng và link hai nơi khác nhau. Phải **dùng lại đúng link** đã có
   ở file chính. Code hiện đã tính sẵn biến này để trả về `r.fileUrl` (link ở ô C sau khi ghi) — dùng chính nó.
2. **Lỗi file phụ không được phá file chính.** `openById` có thể ném (không đủ quyền / ID sai / file bị xoá). Bọc
   try/catch **riêng**, ghi vào `canhBao` của response; đơn vẫn phải ghi xong ở file chính và `ok = true`.
   Cũng đừng gọi `openById` lại cho từng đơn — mở **một lần** cho cả lô.
3. **Tab mẫu của file phụ là `"Trang tính1"`, KHÔNG phải `"tháng 4"`.** `taoTabTheoThang` hiện cứng
   `TEN_TAB_MAU`; phải cho nhận tham số tên tab mẫu, hoặc tách hàm. File phụ chưa có tab tháng nào ⇒ lượt đẩy đầu
   tiên sẽ tự tạo `Tháng 07-2026` bằng cách nhân bản `Trang tính1` (giữ tiêu đề + định dạng).
4. **Chống trùng phải dò trên chính file phụ**, không dùng lại bản đồ `viTri` của file chính (hai file có tập
   dòng khác nhau). Cùng luật: quét cột A **mọi tab**, ưu tiên tab đích.

## 4. Các bước

### Bước 1 — Hằng + mở file phụ một lần

```javascript
// File PHỤ: chỉ nhận A–E. Rỗng = tắt tính năng (không ghi file phụ, không báo lỗi).
const ID_FILE_PHU = '1CK-mu-rtLw0QnGDZ2cuEIkRelEnZkNWuB7Ir_ZuRLhk';
const TEN_TAB_MAU_PHU = 'Trang tính1';
```

Trong `doPost`, sau khi xử xong file chính cho cả lô: mở file phụ **một lần**, bọc try/catch. `ID_FILE_PHU` rỗng
→ bỏ qua im lặng (để tắt nhanh khi cần mà không phải xoá code).

### Bước 2 — Ghi sang file phụ

Dùng lại **nguyên các hàm sẵn có** (`chuanHoa`, `mapCot`, `ghiTruong`, `ghiNeuTrong`, `taoTabTheoThang`) — đừng
viết luật thứ hai, đó chính là cách hai bản script trôi lệch nhau.

Với mỗi đơn trong lô:
- Tìm tab đích theo `body.tab` trên file phụ; chưa có → tạo bằng nhân bản `TEN_TAB_MAU_PHU`.
- Dò mã đơn (cột A) trên mọi tab của file phụ → có thì lấy dòng đó, không thì `getLastRow() + 1`.
- Ghi bằng `ghiNeuTrong` / `ghiTruong`, đúng **4** trường:
  - A ← `don.maDon` (cột 1, như file chính)
  - B ← `don.maVanDon` (tiêu đề `mã vận đơn gửi`)
  - C ← **link phiếu đã có ở file chính** (tiêu đề `ảnh mã vận đơn gửi`) — xem bẫy #1
  - E ← `don.donTraHang` (tiêu đề `Mã đơn trả hàng`)
- **KHÔNG** ghi D. **KHÔNG** tô màu (file phụ không có cột nào cần đổi màu; giữ đơn giản).

### Bước 3 — Báo kết quả về

Thêm vào response (client bỏ qua field lạ, an toàn):
- `filePhu: { ghi: <số đơn đã chạm>, them: <số dòng mới>, loi: <chuỗi hoặc null> }`
- Không tìm thấy tiêu đề ở file phụ → gộp vào `canhBao` sẵn có, **đừng ghi bừa cột** (đúng nếp file chính).

### Bước 4 — Kiểm chứng bằng sheet GIẢ (bắt buộc)

Có sẵn khuôn: `scratchpad/sim.js` và `scratchpad/sim2.js` — dựng `SpreadsheetApp` giả bằng node, `eval` **nguyên
văn** file `Code.gs`, gọi `doPost` rồi in ra ô nào nhận gì. Mở rộng để có **hai** spreadsheet giả (chính + phụ,
phân biệt bằng `openById`). Ca cần phủ:

- [ ] Đơn mới → file chính đủ 8 trường như hiện tại **VÀ** file phụ có A/B/C/E, **không** có D.
- [ ] Link phiếu ở file phụ **trùng đúng** link ở file chính (chứng minh không upload lần hai).
- [ ] Đơn đã có ở file phụ, ô B trống → điền B; ô D người dùng gõ tay → **không đổi**.
- [ ] File phụ chưa có tab `Tháng 07-2026` → tự tạo từ `Trang tính1`, giữ dòng tiêu đề.
- [ ] `openById` ném lỗi → file chính **vẫn ghi đủ**, `results[].ok = true`, lỗi nằm ở `filePhu.loi`.
- [ ] `ID_FILE_PHU = ''` → không đụng file phụ, không lỗi, file chính không đổi (hồi quy).

Dán kết quả chạy thật vào báo cáo.

## 5. Tiêu chí nghiệm thu

- [ ] `node --check` trên `Code.gs` (chép sang `.js` để kiểm cú pháp) OK.
- [ ] Đủ 6 ca ở Bước 4, có kết quả chạy thật.
- [ ] Đọc lại diff: **không dòng nào** của đường ghi file chính bị đổi hành vi.
- [ ] `openById` được gọi **một lần cho cả lô**, không phải mỗi đơn một lần.

## 6. Rủi ro & lưu ý

- **Quyền:** tài khoản triển khai Web App phải có quyền sửa file phụ — nếu không, mọi lượt đẩy sẽ rơi vào
  `filePhu.loi`. Đây là lý do phải báo lỗi ra response chứ không nuốt im lặng.
- **Hạn mức Apps Script:** ghi hai file làm tăng số lệnh gọi Sheets API mỗi lượt. Lô hiện tối đa 10 đơn nên vẫn
  xa hạn mức, nhưng đừng thêm vòng lặp đọc/ghi thừa — dò cột A **một lần cho cả lô**, như file chính đang làm.
- **Đừng nhân bản luật.** Mọi thứ dùng chung hàm với file chính; thấy mình đang chép một hàm ra bản thứ hai là
  dấu hiệu đi sai hướng — đó đúng là cái đã gây lỗi hôm nay.
- File thật nằm trên Google: sửa xong người dùng phải **dán đè + Triển khai → Phiên bản mới** (chỉ Lưu là chưa đủ).

---

## Báo cáo thực thi (Opus điền sau khi xong)
