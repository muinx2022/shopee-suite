# Plan: Đợt H3 — Config nhỏ (tab log per-acc, khoảng nghỉ Scrape, tham số per-shop Update)

- **Ngày:** 2026-08-06
- **Trạng thái:** chờ làm (sau H2)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

3 mục nhỏ chốt đợt: (1) hiển thị dữ liệu log per-account đã được ghi sẵn mà chưa màn nào bind; (2) đưa 2 nhóm giá trị cứng thành cấu hình — khoảng nghỉ Scrape và bộ 3 tham số điền form Update (đổi shop/ngành là phải build lại app, vô lý).

## 2. Phạm vi

- **Làm:** 3 mục phần 3.
- **Không làm:** không thêm tính năng khác; không deploy/release.

## 3. Các bước thực hiện

### H3.1 (Suite) Tab log theo từng tài khoản BigSeller
- Hạ tầng có sẵn và đang chạy: `ModuleViewModelBase.AccountLogs` + `LogAcc` ghi buffer riêng mỗi acc; comment ModuleViewModelBase (~:27–29) ghi rõ "tab log per-acc đợt sau bind vào đây".
- Làm UI ở panel log của các màn module (làm Scrape trước, khuôn dùng lại được thì áp thêm Update/Import nếu rẻ): dải chip/tab ngang trên panel log — "Tất cả" + mỗi acc một chip (tên acc + đếm dòng); chọn chip lọc log theo acc đó. Style chip theo theme (subtab). Vẫn giữ hành vi hiện tại khi chọn "Tất cả".
- Buffer per-acc có trần sẵn (theo khuôn LogBuffer 500 dòng) — chỉ bind, không đổi cơ chế ghi.

### H3.2 (Suite + Hub) Khoảng nghỉ giữa link của Scrape thành tham số
- Hiện `MinRestMs`/`MaxRestMs` hardcode 120–240s (`LauncherRunnerLoop.cs` ~:8–9).
- Client: thành setting của Scrape (cùng chỗ Processes/FrameSize/Reload trong config client — đọc khuôn hiện có), UI ô nhập ở màn cấu hình Scrape (giây, min ≤ max, validate).
- Hub giao việc kèm tham số: thêm RestMinSec/RestMaxSec vào bộ tham số lượt giao (khuôn `hub-run-params` sẵn: Processes/FrameSize/Reload với quy ước **0 = dùng cấu hình client** — theo memory `hub-run-params-brave-budget`), UI ô nhập ở panel /dispatch.
- Quy ước 0=client-default phải giữ nguyên khuôn; client cũ nhận field mới phải bỏ qua an toàn (đọc cách các tham số hiện có được truyền để làm y hệt).

### H3.3 (Hub + Suite) Bộ 3 tham số điền form Update thành cấu hình per-shop trên Hub
- Hiện hardcode trong `BigSellerProductUpdateRunner`: tồn kho `StockValue='30069'`, cân nặng `WeightValue='500'`, kênh vận chuyển `'Nhanh'`.
- Đây là **cấu hình CHUNG toàn fleet, per-shop, chủ sở hữu = Hub** → thêm 3 field vào model shop BigSeller đồng bộ Hub→client. **ĐỌC KỸ memory `bigseller-shop-field-sync-contract` trước khi viết**: Hub pull đè `Shops` nguyên khối; field CHUNG (như 3 field này) thêm vào SharedSignature bình thường, KHÔNG thuộc nhóm per-máy phải graft — nhưng lớp lỗi quanh hợp đồng này đã lặp 5 lần, làm xong phải kiểm tra: nhập ở Hub → client thấy không cần restart (bẫy UI-không-vẽ-lại b13ed00).
- UI nhập: hub Fleet, tab Cấu hình của shop (nơi các field shop đang sửa được). Giá trị rỗng = dùng mặc định hiện tại (30069/500/Nhanh — thành hằng DEFAULT có tên, khớp luật "số trần phải có tên").
- Runner đọc từ shop config với fallback default; log giá trị dùng ở đầu lượt (1 dòng) để chẩn đoán.

## 4. Tiêu chí nghiệm thu

- [ ] Build 2 solution 0 warning; 3 bộ test xanh.
- [ ] H3.2: test/kiểm chứng quy ước 0=client-default đúng khuôn tham số cũ (đọc + đối chiếu code path, có test phía hub nếu bộ test assignments sẵn khuôn).
- [ ] H3.3: field mới đi qua đúng hợp đồng sync (chỉ ra trong báo cáo: SharedSignature ở đâu, graft không cần vì là field chung); test sync nếu có khuôn test HubConfigSync.
- [ ] H3.1: chạy app, màn Scrape hiện chip acc khi có log per-acc (kiểm bằng chạy giả lập/log tay nếu không chạy scrape thật được — ghi rõ đã kiểm tới đâu).
- [ ] Hành vi mặc định KHÔNG đổi khi user chưa đụng config mới (nghỉ vẫn 120–240s; form vẫn 30069/500/Nhanh).

## 5. Rủi ro & lưu ý

- H3.3 là hợp đồng sync nhiều sẹo nhất repo — nếu thấy phải đụng nhóm per-máy/graft thì DỪNG, ghi lại, hỏi phiên chính.
- H3.2 đừng đổi đơn vị âm thầm (ms trong code, giây trên UI — quy đổi tại biên UI, đặt tên biến có đơn vị).
- KHÔNG commit/deploy/release.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
