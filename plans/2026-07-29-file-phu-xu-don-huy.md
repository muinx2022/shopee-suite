# Plan: File Sheet phụ — không nhận đơn hủy mới, và tô đỏ đơn bị hủy sau

- **Ngày:** 2026-07-29
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh

Người dùng phát hiện: đơn ở trạng thái **đã hủy** vẫn được ghi sang **file Sheet phụ**.

Đúng — vòng ghi file phụ trong `orders/gsheet-apps-script/Code.gs` **không hề lọc** trạng thái hủy:

```javascript
for (let i = 0; i < dsDon.length; i++) {
  const r = results[i];
  if (!r || !r.ok) continue;   // chỉ bỏ đơn HỎNG ở file chính
  …                            // không có nhánh nào xét don.daHuy
```

Nặng hơn: file phụ **cố ý không tô màu** (plan trước chốt *"KHÔNG tô màu, giữ đơn giản"*) ⇒ đơn hủy nằm đó trông
**y hệt đơn còn sống**, không dấu hiệu nào. Trong khi file chính tô đỏ.

Đối chiếu hai file cho thấy chỗ lệch:

| | File CHÍNH | File PHỤ (hiện tại) |
|---|---|---|
| Hủy + chưa có vận đơn + chưa từng ghi | bỏ hẳn, không tạo dòng | **ghi tuốt** |
| Hủy nhưng đã vào sổ theo dõi | ghi + **TÔ ĐỎ** | ghi, **không dấu hiệu** |

### Người dùng chốt (29/07)

> *"nếu hủy trước khi vào file 2 thì không sync sang nữa, nếu sang rồi mà file 1 hủy thì hủy luôn file 2"*

Fable diễn giải **"hủy luôn file 2" = ĐÁNH DẤU hủy ở file phụ (tô đỏ như file chính)**, KHÔNG xoá dòng — xoá làm
lệch mọi công thức tham chiếu theo số dòng và mất cột "mã đơn đặt" người dùng gõ tay.

**Người dùng đã xác nhận đúng cách hiểu này:** *"đúng rồi, nếu file 1 x.đ trạng thái hủy thì file 2 cũng x.đ
trạng thái hủy luôn"* ⇒ hai file **cùng một trạng thái**, đánh dấu chứ không xoá.

## 2. Phạm vi

**Làm** — toàn bộ trong `orders/gsheet-apps-script/Code.gs`, chỉ vòng ghi FILE PHỤ:
- Đơn hủy **chưa có dòng** ở file phụ → **KHÔNG tạo dòng**.
- Đơn hủy **đã có dòng** → **tô đỏ** dòng đó (giống file chính).
- Hết hủy (`daHuy === false` tường minh) mà dòng đang mang đúng màu đỏ script tô → **xóa nền** (2 chiều, y file chính).

**Không làm:**
- KHÔNG đụng vòng ghi file CHÍNH (đang chạy đúng production).
- KHÔNG xoá dòng ở file phụ.
- KHÔNG đụng client/C# — đây thuần Apps Script.
- KHÔNG commit, KHÔNG deploy, KHÔNG release.

## 3. ⚠ Bốn cái bẫy

1. **Không được tạo dòng cho đơn hủy chưa có mặt.** Đây là *toàn bộ* mục đích. Hiện code tạo dòng ngay khi
   `viTriPhu[key]` null — phải xét `daHuy` **TRƯỚC** nhánh tạo dòng đó, không phải sau.
2. **Kẹp số cột tô màu theo lưới THẬT của file phụ.** File chính tô 13 cột (`SO_COT_TO_MAU`), file phụ chỉ có 5
   (A–E). `getRange` vượt lưới sẽ NÉM, mà cả khối phụ nằm trong một try/catch ⇒ **một đơn hủy làm hỏng cả lô phụ**.
   Dùng hằng riêng cho file phụ + `Math.min(..., sh.getMaxColumns())` như file chính đang làm.
3. **Chỉ xóa ĐÚNG màu đỏ script tô.** File chính so `getBackground() === MAU_DO_HUY` rồi mới `setBackground(null)`
   — giữ nguyên luật đó, tuyệt đối không xoá nền người dùng tự tô.
4. **`daHuy` phải xét TƯỜNG MINH `=== true` / `=== false`.** Field vắng (client đời cũ) → **không đụng màu, không
   chặn ghi** — giữ nguyên hành vi hiện tại, đừng để client cũ bỗng nhiên ngừng ghi file phụ.

## 4. Các bước

### Bước 1 — Hằng số cột tô màu cho file phụ

Cạnh `SO_COT_TO_MAU`:

```javascript
const SO_COT_TO_MAU_PHU = 5;   // file phụ chỉ có A–E
```

Ghi rõ trong comment vì sao khác file chính.

### Bước 2 — Lọc + tô màu trong vòng ghi file phụ

Trong vòng `for` của khối file phụ, sau `if (!r || !r.ok) continue;`:

```
daHuy = don.daHuy === true
chưa có dòng (viTriPhu[key] == null):
    daHuy  → continue           ← KHÔNG tạo dòng (bẫy #1)
    ngược lại → tạo dòng như hiện tại
đã có dòng:
    ghi các trường như hiện tại (ghiNeuTrong vẫn chỉ điền ô trống)
cuối cùng, với dòng ĐANG CÓ MẶT:
    daHuy === true  → setBackground(MAU_DO_HUY) trên A..min(5, maxCols)
    daHuy === false → nếu nền ô A đúng bằng MAU_DO_HUY thì setBackground(null)
    daHuy vắng      → không đụng màu (bẫy #4)
```

Câu hỏi cần tự quyết + nêu rõ trong báo cáo: **đơn hủy đã có dòng thì có ghi tiếp các trường không?** Đề xuất:
**CÓ** — vẫn `ghiNeuTrong` các ô trống (vd mã vận đơn về muộn), vì ô đã có thì không bị đụng, và để trống vĩnh
viễn thì dòng đỏ đó thiếu thông tin đối chiếu.

### Bước 3 — Đếm cho đúng

`filePhu.ghi` / `filePhu.them` hiện đếm mọi đơn chạm tới. Đơn hủy bị bỏ qua **không được** tính vào `them`.
Cân nhắc thêm `filePhu.boQuaHuy` để soi được — nêu rõ lựa chọn trong báo cáo.

### Bước 4 — Kiểm chứng bằng sheet giả (BẮT BUỘC)

Khuôn có sẵn: `scratchpad/sim-file2.js` (và `sim3.js`/`sim4.js` ở scratchpad phiên nếu còn) — dựng hai
spreadsheet giả, `eval` **nguyên văn** `Code.gs`, gọi `doPost` thật. Ca cần phủ:

- [ ] Đơn hủy **chưa có** ở file phụ → file phụ **KHÔNG có dòng nào thêm**; file chính vẫn ghi như cũ.
- [ ] Đơn hủy **đã có** dòng ở file phụ → dòng đó **nền đỏ**, các ô cũ không bị đổi giá trị.
- [ ] Đơn **hết hủy** (`daHuy:false`), dòng đang đỏ do script tô → **xoá nền**.
- [ ] Dòng đang có nền màu **KHÁC** (người dùng tự tô) + `daHuy:false` → **KHÔNG đụng** (bẫy #3).
- [ ] Payload **thiếu** field `daHuy` → hành vi y như trước khi sửa (vẫn ghi, không đụng màu) (bẫy #4).
- [ ] File phụ chỉ có 5 cột → tô màu **không ném**, không làm hỏng cả lô (bẫy #2).
- [ ] HỒI QUY: đơn thường (không hủy) → file chính + file phụ y hệt trước khi sửa.

Dán kết quả chạy thật vào báo cáo.

## 5. Tiêu chí nghiệm thu

- [ ] `node --check` trên `Code.gs` OK.
- [ ] Đủ 7 ca ở Bước 4, có kết quả chạy thật.
- [ ] Khẳng định: vòng ghi **file chính** không đổi hành vi (chứng minh bằng ca hồi quy / test vi sai như đợt trước).

## 6. Rủi ro & lưu ý

- **Bẫy #2 là chỗ nguy nhất**: cả khối phụ nằm trong MỘT try/catch, nên `getRange` vượt lưới sẽ nuốt luôn các đơn
  còn lại trong lô, chỉ để lại `filePhu.loi`. Phải kẹp số cột.
- Dòng đơn hủy **đã lỡ vào** file phụ trước bản sửa sẽ không tự dọn; chúng chỉ được tô đỏ khi còn được đẩy lại.
  Đơn đã đóng hết cờ thì không đẩy nữa ⇒ người dùng tự xoá tay. Nêu rõ điều này khi báo cáo cho người dùng.
- Sửa xong người dùng phải **dán đè Apps Script + Triển khai → Phiên bản mới** (chỉ Lưu là chưa đủ).

---

## Báo cáo thực thi (Opus điền sau khi xong)
