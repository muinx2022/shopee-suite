# Plan: GSheet ghi tiền bán = "Ước tính" (số tiền cuối cùng) thay vì Tổng tiền

- **Ngày:** 2026-07-26
- **Trạng thái:** hoàn thành (ngữ nghĩa Bước 3 ĐÃ ĐỔI khi thực thi — xem báo cáo)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`) — CÂY CHÍNH

## 1. Bối cảnh & mục tiêu

Người dùng yêu cầu: **cột tiền bán trên Google Sheet phải là số "Ước tính"** (tức `final_amount` — "Số tiền cuối
cùng" đọc từ trang chi tiết đơn, hiển thị ở cột "Ước tính" màn Đơn hàng), KHÔNG phải tổng tiền niêm yết.

**Hiện trạng:** `orders/XuLyDonShopee.App/Services/AccountSession.cs:933` đang gửi
`DoanhThu: p.TotalPrice` — tổng tiền. `GsheetPendingOrder` (`orders/XuLyDonShopee.Core/Data/OrdersRepository.cs:25`)
và câu SELECT của `GetForGsheetPush` (~:296) **KHÔNG có** `final_amount` → phải bổ sung.

**Vấn đề đi kèm (quan trọng, đừng bỏ):** đơn thường được ghi sheet ở lượt sync ĐẦU, lúc đó **chưa có** ước tính
(chưa mở trang chi tiết). Nếu chỉ đổi field mà không thêm điều kiện đẩy lại thì đơn đã ghi sẽ **kẹt số cũ VĨNH
VIỄN** — đúng lỗi vừa gặp bên Hub (cột "Cuối cùng" trống mãi, đã sửa ở commit trước bằng cách reset cờ khi
`final_amount` chuyển NULL→có).

**Đã có sẵn khuôn để bắt chước:** cột `gsheet_da_co_van_don` (0/1 = lần đẩy gần nhất có gửi mã vận đơn chưa) +
điều kiện `vanDonMoi` (`AccountSession.cs:912`) — dùng để tự điền cột B khi vận đơn xuất hiện SAU. Ta làm y hệt
cho ước tính.

## 2. Phạm vi

- **Làm:** đẩy `final_amount` lên GSheet làm cột tiền; thêm cơ chế **đẩy lại khi ước tính vừa có**.
- **KHÔNG làm:** không đổi giao diện; không đụng phần đồng bộ cấu hình GSheet vừa xong; không đụng redesign
  (Theme.axaml / MainWindow.axaml / Modules/Workspace); không đổi hợp đồng với Apps Script (vẫn cùng field
  `DoanhThu`, chỉ đổi GIÁ TRỊ đưa vào).

## 3. Các bước thực hiện

### Bước 1 — DB: cột mới (`orders/XuLyDonShopee.Core/Data/Database.cs`)
Thêm `EnsureColumn(conn, "orders", "gsheet_da_co_uoc_tinh", "INTEGER");` kèm comment giải thích (theo khuôn
comment của `gsheet_da_co_van_don` ngay trên đó): 0/1 = lần đẩy sheet gần nhất ĐÃ gửi kèm số ước tính chưa;
NULL = chưa từng đẩy. Dùng để đẩy lại điền số khi ước tính xuất hiện sau.

### Bước 2 — Repo (`orders/XuLyDonShopee.Core/Data/OrdersRepository.cs`)
- `GsheetPendingOrder` (:25): thêm 2 field `long? FinalAmount` và `int? GsheetDaCoUocTinh`.
- `GetForGsheetPush` (~:296): thêm `final_amount`, `gsheet_da_co_uoc_tinh` vào SELECT + đọc vào record (giữ đúng
  thứ tự cột ↔ chỉ số reader, cẩn thận lệch index).
- `MarkGsheetSynced` (~:343): thêm tham số `bool coUocTinh` và ghi cột `gsheet_da_co_uoc_tinh` (khuôn y
  `gsheet_da_co_van_don` đang có). Cập nhật MỌI nơi gọi.

### Bước 3 — Đẩy (`orders/XuLyDonShopee.App/Services/AccountSession.cs`)
- Dòng ~933: `DoanhThu: p.FinalAmount ?? p.TotalPrice`.
  **Ngữ nghĩa chốt:** có ước tính → ghi ước tính; CHƯA có → tạm ghi tổng tiền (dòng sheet không bị trống), rồi
  **tự ghi đè bằng ước tính** ở lượt sau nhờ Bước 3b. (KHÔNG để trống — người dùng cần thấy dòng đầy đủ ngay.)
- Bước 3b — thêm điều kiện đẩy lại, cạnh `vanDonMoi` (:912):
  ```csharp
  var coUocTinh = p.FinalAmount is not null;
  var uocTinhMoi = coUocTinh && p.GsheetDaCoUocTinh != 1;   // đã ghi dòng lúc chưa có ước tính → gửi lại
  ```
  và đưa `uocTinhMoi` vào biểu thức chọn gửi (:913) + cập nhật comment (a)…(d) thành (a)…(e).
- Ghi nhớ `coUocTinh` theo mã đơn (khuôn `coVanDonByMaDon` :920) rồi truyền vào `MarkGsheetSynced` ở nhánh
  thành công (~:948).

### Bước 4 — Test (`orders/XuLyDonShopee.Tests/`)
- Migration: DB CŨ (chưa có cột) → `new Database(path)` thêm cột, dữ liệu cũ còn nguyên (khuôn
  `DatabaseMigrationTests`).
- `GetForGsheetPush` trả đúng `FinalAmount` + `GsheetDaCoUocTinh`.
- `MarkGsheetSynced(coUocTinh: false)` rồi đơn có ước tính → lần sau `GsheetDaCoUocTinh != 1` (đủ điều kiện đẩy lại);
  `MarkGsheetSynced(coUocTinh: true)` → không đẩy lại nữa (chống đẩy vô hạn).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build` solution 0 error; `dotnet test XuLyDonShopee.Tests` xanh (kèm test mới).
- [ ] Đơn CÓ ước tính → sheet nhận số ước tính (không phải tổng tiền).
- [ ] Đơn CHƯA có ước tính → ghi tạm tổng tiền; khi ước tính xuất hiện ở lượt sync sau → **tự đẩy lại**, sheet
      cập nhật đúng số (kiểm bằng test: sau khi mark `coUocTinh: false`, đơn có `final_amount` phải nằm trong
      diện đẩy lại).
- [ ] Đã đẩy kèm ước tính rồi → KHÔNG đẩy lại mỗi lượt sync (không spam Apps Script).
- [ ] Migration DB cũ an toàn, không mất dữ liệu.
- [ ] Không đụng file redesign giao diện.

## 5. Rủi ro & lưu ý

- **Lệch index reader** khi thêm cột vào SELECT của `GetForGsheetPush` — đọc kỹ, cột thêm phải khớp đúng thứ tự.
- `MarkGsheetSynced` đổi chữ ký → cập nhật hết nơi gọi, kể cả test cũ.
- Đơn đã ghi sheet TRƯỚC bản này có `gsheet_da_co_uoc_tinh` NULL → `!= 1` → sẽ được đẩy lại MỘT lần để điền số
  ước tính. Đây là hành vi MONG MUỐN (sửa số cũ), nhưng lưu ý lượt sync đầu sau khi cập nhật sẽ đẩy hơi nhiều
  dòng — nêu rõ trong báo cáo để Fable biết.
- Apps Script cập nhật dòng theo mã đơn (log thực tế có "thêm 0 dòng mới, **bổ sung** 1") nên đẩy lại là AN TOÀN,
  không nhân đôi dòng.

---

## Báo cáo thực thi (Opus điền sau khi xong)

Hoàn thành. Build 0 error, **951 test xanh** (+7 test mới).

**ĐỔI NGỮ NGHĨA so với plan (Fable quyết khi review — plan gốc SAI, đừng theo mục 3 Bước 3 nữa):**
Plan ghi "chưa có ước tính → tạm ghi tổng tiền rồi ghi đè sau". Executor cảnh báo hợp đồng Apps Script có thể là
**"chỉ ghi ô đang TRỐNG"** (`GoogleSheetSyncService.cs:9-14, :185-186`) — nếu đúng vậy thì ghi tạm tổng tiền sẽ
**chiếm chỗ ô tiền vĩnh viễn**, số ước tính không đè được ⇒ hỏng đúng mục tiêu. Code Apps Script nằm trên tài
khoản Google của user, KHÔNG kiểm chứng được.

⇒ Chọn phương án đúng dưới **CẢ HAI** cách hiểu, gom vào hàm thuần `GsheetMoney.Chon(finalAmount, totalPrice, daHuy)`:
- có ước tính → ghi ước tính;
- chưa có + đơn THƯỜNG → **null ⇒ field bị bỏ khỏi JSON ⇒ ô tiền để TRỐNG**, lượt sau ước tính về sẽ điền
  (test sẵn có `TaoJsonBody_...BoFieldNullKhac` xác nhận null bị bỏ, script không chạm ô);
- chưa có + đơn HỦY → tổng tiền (đơn hủy không bao giờ có ước tính nên không sợ chiếm chỗ; giữ hành vi cũ).

Đánh đổi: đơn thường chưa lấy được ước tính sẽ **trống ô tiền ~1 lượt sync**. Thực tế log cho thấy ước tính
thường lấy ngay trong cùng lượt trước khi đẩy sheet nên hiếm khi trống.

Cơ chế đẩy lại: cột `gsheet_da_co_uoc_tinh` + điều kiện `uocTinhMoi` (khuôn y `gsheet_da_co_van_don`/`vanDonMoi`).
Đơn cũ có cột NULL → đẩy lại ĐÚNG MỘT LẦN để điền số (máy này đếm được: 1 dòng).
