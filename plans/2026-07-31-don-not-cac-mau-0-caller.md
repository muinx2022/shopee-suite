# Plan: Dọn nốt các mẩu 0-caller đã đánh dấu trong đợt refactor

- **Ngày:** 2026-07-31
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh

Đợt refactor 30-31/07 đã xong; các agent để lại vài mẩu code chết cần quyết định — nay đã chốt: DỌN HẾT.

## 2. Các việc

**A. Dây event `CookieSaved` (orders — nửa sống nửa chết):** `AccountSession.TrySaveCookie` 0 caller và là nơi DUY NHẤT phát `CookieSaved` ⇒ event không bao giờ bắn ⇒ toàn bộ dây là chết. Xoá trọn: `AccountSession.TrySaveCookie` + event `CookieSaved`; `IAccountSession.CookieSaved`; forwarder trong `AccountSessionManager`; phía `AccountsViewModel` (đăng ký + `OnSessionCookieSaved` + `RefreshAfterCookieSaved` — kiểm tra `RefreshAfterCookieSaved` có caller khác không, có thì chỉ gỡ phần event); 2 test double (`AccountSessionManagerTests`, `AccountRowViewModelTests`). Trước khi xoá từng mảnh: grep 0-caller. Mốc 0 warning phải giữ.

**B. `OrdersRepository.GetOrdersForSlipCheck`:** 0 caller production (chỉ test gọi) → xoá method + test tương ứng (ghi rõ số test giảm).

**C. `LauncherSettings.WsPort`** (suite — field serialize chết, đã có xmldoc đánh dấu): xoá property (property thừa trong settings.json cũ sẽ bị deserializer bỏ qua — vô hại; xác nhận store nạp settings dùng System.Text.Json mặc định bỏ qua unknown member).

**D. `SearchOrchestrator.StopAsync`** (suite Search — 3F đã xác nhận 0 caller): grep lại rồi xoá.

**E. Rà lần cuối:** grep các symbol đã bị báo cáo là chết trong các plan 2026-07-30/31 (mục "cần soi"/"code chết") mà chưa ai xoá — nếu còn sót cái nào 0-caller thì xoá nốt, liệt kê trong báo cáo. KHÔNG mở rộng sang suy đoán mới.

## 3. Phạm vi & nghiệm thu

- Khu: `orders/**` + `suite/**`. Không đụng `server/**`, `extensions/**`, `shared/**`.
- [ ] Build 2 solution 0 lỗi 0 warning; test: orders/Core/hub không tụt ngoài số test xoá chủ đích (ghi rõ từng con số).
- [ ] Grep các symbol đã xoá = 0 hit source.
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

(chưa)
