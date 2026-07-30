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

**Kết quả build/test:** ShopeeSuite.sln 0W/0E, ShopeeHub.sln 0W/0E. Test: orders **1461** (= 1471 − 10 ca
`ThieuPhieu` xoá chủ đích, xem A), Core.Tests 61, hub 44 — đều giữ nguyên. Orders chạy **3 lượt liên tiếp**
1461/1461, không flake.

### A. Xoá code chết — 8/9 mục xong, 1 mục DỪNG

Grep 0-caller xác nhận từng cái ngay trước khi xoá. Đã xoá:

| Symbol | File |
|---|---|
| `_workPage` + `SetWorkPage` + `WorkPage()` | `ShopeeLoginService.cs` (lớp `LoginSession`) |
| const `SellerUrl`, `ShopListUrl` | `ShopeeLoginService.cs` |
| `UserSelectors`, `PasswordSelectors`, `SubmitSelectors`, `UsePasswordRegex`, `OtherWaysRegex`, `KmsiYesRegex`, `ShopDetailRegex` | `LoginSelectors.cs` |
| `FindFirstVisibleAsync` | `LoginPageProbe.cs` |
| `ScanShopListJs` | `LoginParsers.cs` |
| `TryClearVerifyFailedAfterLogin` | `AccountSession.cs` |
| `PrepareResult.SlipTabUrl` (+ chỗ đọc `slipTabUrl` khỏi JSON) | `OrdersBridgeChannel.cs` |
| `ThieuPhieu` | `SlipFiles.cs` |
| `PasswordOption` | `shared/Shopee.Toolkit/MsLogin/MsLoginSelectors.cs` |

Kèm theo: sửa 3 xmldoc trỏ vào symbol vừa xoá (`FindFirstVisibleByRectsAsync`, `ParseShopListJson`,
`SlipFileIsValidPdf`) + 1 comment `ShopFlowRunner.RedownloadSlipAsync`; bỏ `using` thành thừa ở `SlipFiles.cs`.

**`ThieuPhieu` — chọn XOÁ CẢ TEST (plan cho phép):** 0 caller production (chỉ còn nhắc trong comment
`ShopFlowRunner`), mà test lại chỉ test đúng chính nó ⇒ chuyển sang test project chỉ tạo bộ test tự-chứng-minh,
vô giá trị. Xoá 4 `[Fact]` + 1 `[Theory]` 6 ca = **10 ca** (1471 → 1461). GIỮ nguyên test
`SlipFileIsValidPdf` (còn dùng thật: `OrderRowViewModel.HasSlipFile`, `HubOutbox`) và test
`GetOrdersForSlipCheck`.

**DỪNG — `AccountSession.TrySaveCookie`:** đây là nơi DUY NHẤT phát event `CookieSaved`. Xoá xong build ra
`warning CS0067: The event 'AccountSession.CookieSaved' is never used` ⇒ **vỡ mốc 0 warning** (đã thử thật rồi
revert byte-đúng). Dọn sạch phải kéo theo cả dây, vượt xa phạm vi plan: `IAccountSession.CookieSaved` (interface),
`AccountSession.CookieSaved`, `AccountSessionManager.CookieSaved` + forwarder, `AccountsViewModel` (đăng ký +
`OnSessionCookieSaved` + `RefreshAfterCookieSaved`), 2 test double (`AccountSessionManagerTests`,
`AccountRowViewModelTests`) — **7+ file**. Cần Fable quyết trước khi làm.

### B. `NormalizeForMatch` về Toolkit — xong (có điểm lệch cần biết)

**So 2 bản:** KHÔNG trùng byte như plan ghi, nhưng **trùng logic token-đối-token**; khác đúng 3 điểm hình thức:
modifier (`internal` vs `private`), bản `HotmailOtpReader` bỏ ngoặc ở 2 câu `if` một dòng, bản `LoginParsers` có
thêm 1 comment. Không có lệch hành vi ⇒ gộp, không dừng.

**Đặt ở đâu:** nhét vào CHÍNH `MsLoginSelectors.cs` thay vì file text-util riêng trong Toolkit — vì
`Shopee.Hub.Web` **không** ref project Toolkit mà `Compile`-link đúng MỘT file `MsLoginSelectors.cs`; tách file
mới sẽ buộc phải sửa `server/Shopee.Hub.Web/Shopee.Hub.Web.csproj`, mà file đó **ngoài khu của plan này** và
đang do agent AssignmentOps sửa song song (plan hằng AssignmentOps mục 3). Hai phía giờ gọi chung:
`LoginParsers.NormalizeForMatch` thành forwarder một dòng (đúng khuôn alias selector sẵn có trong
`LoginSelectors`), `HotmailOtpReader` gọi thẳng `MsLoginSelectors.NormalizeForMatch` + xoá bản chép.

### C. `ex.ToString()` cho catch bất ngờ — 19 chỗ

Luật áp dụng: **đổi** khi là `catch (Exception ex)` catch-all mà giá trị CHỈ đi vào log; **giữ `ex.Message`** khi
chuỗi đi ra UI/`StatusText`/message của exception ném lên, hoặc khi nhánh đã phân loại lỗi nghiệp vụ.

Đổi (19): `EmailVerifyFlow`:132 · `OrderNotifyService`:331 · `OrdersBridgeSession`:180, 375 · `ShopFlowRunner`:171,
179, 267, 293 · `SubaccountLoginFlow`:129, 295 · `AccountSession`:342, 407 · `HubDirectoryPuller`:47, 106 ·
`HubOutbox`:92, 175, 219, 525, 623 · `HubOutboxWorker`:160 · `OrderPersistPipeline`:348, 449, 613.

Giữ nguyên (7, có chủ đích): `GoogleSheetSyncService`:191 và `ShopeeLoginService`:221 (dựng message cho exception
ném lên, đã truyền `ex` làm inner nên stack không mất) · `OrdersBridgeSession`:181, 376 (message trả về UI; dòng
log ngay cạnh đã có full) · `ProfileJanitor`:59 (`catch … when (ex is IOException or UnauthorizedAccessException)`
— đã phân loại) · `AccountSession`:317 (`catch (InvalidOperationException)` = extension chưa kết nối, thông điệp
gọn chủ đích) · `AccountSession`:534 (`SetError` → StatusText).

### D. Đặt tên magic number — xong (giá trị GIỮ NGUYÊN 100%)

Thêm lớp lồng `OrdersBridgeChannel.ChoChang` gom trần chờ 13 chặng: `Ready` 45s, `AtSeller` 120s, `ShopList` 30s,
`Detail` 45s, `ToShip` 30s, `Orders` 120s, `Pickup` 90s, `PickupOther` 60s, `Prepare` 300s, `CloseShop` 30s,
`Redownload` 180s, `Returns` 90s, `Finals(soDon)` = `min(300, 20 + 20×soDon)`. Thay 16 chỗ gọi trần ở
`OrdersBridgeSession` (8) + `ShopFlowRunner` (8).

Chốt chặn đơn: `while (guard++ < 50)` → `ShopFlowRunner.TranDonMoiLuotShop = 50` kèm xmldoc giải thích vì sao 50
(dây bảo hiểm chống vòng vô tận, mỗi đơn tốn tới `ChoChang.Prepare` 300s) và vì sao KHÁC 200. **Lưu ý cho Fable:**
"50 vs 200" trong plan — mốc 200 (`OrderPersistPipeline.HubPushBatchSize`) và mốc 50 còn lại
(`TraHangParser.TranDongMoiLuot`) **đã là const có tên + có doc từ trước**, nên đợt này chỉ còn đúng cái 50 trần
kia phải đặt tên; phần "vì sao 2 mức" ghi vào doc của const mới.

### E. Fix flaky — xong

`TempDatabase.Dispose`: `SqliteConnection.ClearAllPools()` → `ClearPool(conn)` với connection dựng lại ĐÚNG chuỗi
kết nối mà `Database` dùng (`SqliteConnectionStringBuilder{DataSource=Path}` — pool khoá theo chuỗi kết nối; không
cần Open). Vẫn nhả file lock để xoá được file. Chạy 3 lượt liên tiếp: 1461/1461 cả 3.

### F. `orders/CLAUDE.md` — đã tạo (SỬA 1 SAI SÓT CỦA PLAN)

Plan ghi "stack WPF net8". **Thực tế `XuLyDonShopee.App` là Avalonia 11.3 + CommunityToolkit.Mvvm, build ra DLL
(module cho shell `Shopee.Suite`), KHÔNG phải WPF, KHÔNG phải exe.** File đã viết theo đúng thực tế: 3 project +
vai trò, lệnh build/test, quy ước tên tiếng Việt không dấu cho luật nghiệp vụ (kèm ví dụ có thật), quy ước tách
hàm thuần, quy ước log `ToString()` vs `Message` (chốt ở mục C), và ghi chú cầu nối extension cổng 47821.

### Điểm cần Fable soi

1. **`TrySaveCookie` chưa xoá** — chờ quyết định về cả dây `CookieSaved` (xem A).
2. **Log dài ra:** 19 chỗ giờ in cả stack vào panel nhật ký. Nhánh mạng-hỏng thường gặp (`HubOutbox` đẩy hub/GSheet)
   sẽ dài hơn hẳn trước. Nếu thấy ồn thì dial back mấy chỗ HubOutbox về `ex.Message` — đã liệt kê đủ dòng ở mục C.
3. **Chạm `suite/Shopee.Core/BigSeller/HotmailOtpReader.cs`** (đúng khu plan giao) — nhưng agent "suite nhất quán"
   cũng quét `suite/Shopee.Core/**` thêm `ConfigureAwait`. Sửa của tôi ở file này: xoá hàm private cuối file, đổi
   1 call site (dòng 505), bỏ `using System.Globalization`. Có thể chạm nhau khi merge.
4. **Còn 2 hit `ScanShopListJs`** trong `extensions/shopee-orders/background.js` (dòng 73, 76) — chỉ là comment
   "port từ … phía C#", KHÔNG phải tham chiếu code. `extensions/**` ngoài khu plan nên tôi không sửa.
5. `OrdersRepository.GetOrdersForSlipCheck` cũng đã 0 caller production (chỉ test gọi) — cùng họ code chết với
   `ThieuPhieu` nhưng KHÔNG có trong danh sách plan nên tôi để nguyên. Đề xuất đưa vào đợt sau.
