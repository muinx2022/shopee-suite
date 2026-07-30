# Plan: Hợp nhất CDP targets (3C) + kịch bản kill Brave (3D) + fix ngưỡng 120s

- **Ngày:** 2026-07-30
- **Trạng thái:** chờ (chạy SAU khi plan 3A+3B merge — chung file module)
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

**3C.** Kiểm chứng 30/07: chỗ tự fetch+parse `http://127.0.0.1:<port>/json/list` đã TĂNG lên **20 điểm**: `suite/Shopee.Module.MultiBrave/Engine/ExtensionRunnerAutomation.cs` 10 chỗ (~313, 519, 591, 629, 680, 1331, 1359, 1607, 1755, 1789); `BraveInstanceSession.cs:~1440`; `MB/Engine/PageCdpHelper.cs:~186, ~256`; `SE/Engine/BraveManager.cs:~93`; `SE/Engine/SearchSession.cs:~255`; Core `Cdp/CdpClient.cs:~17, 51, 131, 165`; `BigSeller/BigSellerCookieEngine.cs:~764`. Mỗi chỗ tự dựng HttpClient/parse JSON riêng.

**3D.** 4 bản kịch bản kill Brave (đều đã gọi chung `Core BraveProcessReaper.KillByUserDataDir` nhưng phần bọc ngoài vẫn 4 bản): `BraveInstanceSession.KillBraveProcess` ~857-894 (thêm PortCdpHub.ResetSoon + TryCloseBraveGracefully + WaitForExit); `UP/BigSellerBraveRunner.DisposeAsync` khối kill ~206-218 (kill tree + Dispose + reaper + BraveFleet.UnregisterActiveProfile); `UP/BigSellerImportToStoreRunner.RestartBrowserAsync` ~366-379; `SE/BraveManager.Kill` ~272-302 (PortAllocator.Release + reaper includeCrashpadOrphans:true + Sleep 400).

**Kèm 1 bug low đã confirm:** `ExtensionRunnerAutomation.cs:~90,119` — `CdpUnreachableTimeoutSeconds` 120s là nhánh chết vì deadline vòng ngoài dùng `timeoutSeconds=90` mặc định ở cả 2 call site (`LauncherRunnerLoop.cs:88` + `:804` nội bộ) → hết 90s ném lỗi chung với hướng dẫn SAI, nhánh thông điệp "mở lại cùng profile để nối tiếp phiên" không bao giờ chạy.

## 2. Phạm vi

- **Làm:** 3 việc trên. Khu: `suite/Shopee.Core/**` (Cdp, BigSeller, Browser) + 4 module `suite/Shopee.Module.*`.
- **Không làm:** không đụng `orders/**`, `server/**`, `extensions/**`, `suite/Shopee.Suite/**`; không đổi hành vi retry/timing hiện có ngoài fix 120s nêu trên; không gộp transport WS CDP của các module (chỉ phần HTTP /json/list).

## 3. Các bước thực hiện

1. **Core `CdpClient`:** thêm `record CdpTarget(string Id, string Type, string Url, string? WsUrl)`; `ListTargetsAsync(int port, CancellationToken)` (+ overload endpoint) dùng `AppServices.DirectHttp`; `CloseTargetAsync(int port, string id)`. Helper `CdpEndpoints` gom quy tắc "luôn 127.0.0.1, không localhost" (hiện chỉ ghi 1 chỗ comment) + dựng URL /json/list, /json/new, ws.
2. Thay 20 điểm parse bằng `ListTargetsAsync` (giữ nguyên logic lọc/chọn target của từng chỗ — chỉ thay phần fetch+parse). 4 method nội bộ CdpClient tự parse → dùng chung đường mới.
3. `CdpClient.SendAsync`: bổ sung tham số tuỳ chọn `sessionId` + `receiveTimeout` → xoá `ExtensionRunnerAutomation.SendCdpAsync` (~1813-1891 bản 25/07 — tìm theo tên), caller chuyển sang CdpClient.
4. **`BraveTeardown.KillAndReap`** trong Core (cạnh BraveProcessReaper): tham số hoá các bước lệch (graceful-close trước, reaper flags, sleep sau, WaitForExit); 4 chỗ gọi chuyển sang bản chung, phần móc riêng (UnregisterActiveProfile, PortAllocator.Release, PortCdpHub.ResetSoon) giữ ở caller. So sánh kỹ 4 bản trước khi viết — bước nào chỉ 1 bản có thì thành option, KHÔNG thêm bước cho bản không có.
5. **Helper `IsTransientNavigationError`**: grep 3 danh sách lỗi navigation transient gần trùng trong các module (plan 25/07 ghi nhận 3) → 1 hàm Core, tập marker = HỢP của các bản (ghi rõ trong báo cáo bản nào thiếu gì).
6. **Fix 120s:** khi nhánh CDP-unreachable kích hoạt, gia hạn deadline vòng ngoài (như nhánh captcha đang làm) tới đủ `CdpUnreachableTimeoutSeconds` (120s) để nhánh thông điệp riêng chạy được; hoặc nếu cách đó đụng nhiều — hạ hằng xuống 80s và sửa message khớp thực tế. Chọn 1, ghi lý do.

## 4. Tiêu chí nghiệm thu

- [ ] Build 0 lỗi 0 warning; test không tụt.
- [ ] Grep `"/json/list"` chỉ còn trong `CdpEndpoints`/`CdpClient` (+ extensions/ nếu có — ngoài phạm vi thì liệt kê).
- [ ] Grep `SendCdpAsync` = 0 hit ngoài CdpClient.
- [ ] 4 chỗ kill Brave đều gọi `BraveTeardown` — grep `KillByUserDataDir` chỉ còn trong Core.
- [ ] Báo cáo: bảng từng điểm thay + xác nhận logic lọc target giữ nguyên.

## 5. Rủi ро & lưu ý

- Chờ 3A+3B merge xong mới chạy (chung BraveInstanceSession/BraveManager/SearchSession) — số dòng sẽ trôi thêm, tìm theo symbol.
- Timing quanh kill Brave nhạy (chống rò cửa sổ — memory BraveJobObject): giữ nguyên thứ tự bước + sleep của từng bản qua option.

---

## Báo cáo thực thi (Opus điền sau khi xong)

(chưa)
