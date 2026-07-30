# Plan: Đợt 5 — orders: dọn code chết sau tách + nhất quán + fix test flaky

- **Ngày:** 2026-07-31
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh

Đợt 4 vừa tách xong ShopeeLoginService/AccountsViewModel/OrdersBridgeSession/AccountSession; các agent tách đã LIỆT KÊ code chết di chuyển nguyên chưa xoá (refactor thuần không được xoá). Giờ dọn + vài món nhất quán.

## 2. Các việc

**A. Xoá code chết (grep 0-caller xác nhận từng cái ngay trước khi xoá):**
- `LoginSession.SetWorkPage`/`WorkPage`/`_workPage`; `LoginSelectors.UserSelectors`/`PasswordSelectors`/`SubmitSelectors`/`UsePasswordRegex`/`OtherWaysRegex`/`KmsiYesRegex`/`ShopDetailRegex`; `LoginPageProbe.FindFirstVisibleAsync`; `LoginParsers.ScanShopListJs` (chỉ còn trong doc); const `ShopeeLoginService.SellerUrl`/`ShopListUrl`.
- `AccountSession.TryClearVerifyFailedAfterLogin`, `AccountSession.TrySaveCookie`; `PrepareResult.SlipTabUrl`.
- `SlipFiles.ThieuPhieu`: chỉ test dùng → chuyển thành helper trong test project (hoặc xoá cả test nếu test chỉ test chính nó — đọc rồi quyết, ghi rõ).
- `shared/Shopee.Toolkit/MsLogin/MsLoginSelectors.cs`: xoá `PasswordOption` (code chết cả 2 phía, xmldoc đã tự khai).

**B. `NormalizeForMatch` về Toolkit:** hiện trùng byte giữa `orders/.../LoginParsers.cs` và `suite/Shopee.Core/BigSeller/HotmailOtpReader.cs` — đưa về `shared/Shopee.Toolkit/MsLogin/` (hoặc file text-util riêng trong Toolkit), 2 phía gọi chung. So byte 2 bản trước khi gộp; lệch thì DỪNG + báo.

**C. `ex.ToString()` cho catch bất ngờ phía orders:** grep các `catch (Exception ex)` đang log `ex.Message` ở nhánh "bất ngờ" (không phải lỗi nghiệp vụ đã phân loại) trong `orders/**/Services` → đổi sang `ex.ToString()` (giữ nguyên nhánh lỗi nghiệp vụ có thông điệp gọn chủ đích). Liệt kê từng chỗ đổi.

**D. Đặt tên magic number tại chỗ (KHÔNG đổi giá trị):** bộ timeout từng chặng trong `OrdersBridgeChannel` (30/45/60/90/120/180/300 + công thức finals) → const có tên; chốt chặn đơn 50 vs 200 (grep trong orders) → const có tên + comment vì sao 2 mức.

**E. Fix test flaky ĐÃ TÌM RA NGUYÊN NHÂN:** `TempDatabase.Dispose` gọi `SqliteConnection.ClearAllPools()` (chốt toàn tiến trình) đua với lớp test song song → `ObjectDisposedException` lác đác. Sửa: dùng `SqliteConnection.ClearPool(connection)` cho ĐÚNG connection của TempDatabase (per-pool, không toàn cục) — vẫn nhả file lock để xoá file db. Chạy test 3 lượt liên tiếp xác nhận ổn định.

**F. `orders/CLAUDE.md`:** tạo file ngắn (nếu chưa có) ghi: stack (WPF net8, XuLyDonShopee.App/Core/Tests), lệnh build/test, quy ước "tên tiếng Việt không dấu cho luật nghiệp vụ" (NenXoaDonKetThuc, QuyetDinhLuotTraHang, LuuMaTraHang…) — đúng nếp code hiện có.

## 3. Phạm vi & nghiệm thu

- Khu: `orders/**` + `shared/Shopee.Toolkit/**` + `suite/Shopee.Core/BigSeller/HotmailOtpReader.cs` (chỉ mục B). KHÔNG đụng khu khác (2 agent khác đang chạy song song ở suite modules + hub/coordination).
- [ ] Build 2 solution 0/0; test orders ≥ 1471 (trừ test xoá chủ đích ở A — ghi rõ), Core.Tests 61, hub 44.
- [ ] Grep từng symbol mục A = 0 hit source.
- [ ] Test orders chạy 3 lượt liên tiếp không flake.
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

(chưa)
