# Plan: Hợp nhất CDP targets (3C) + kịch bản kill Brave (3D) + fix ngưỡng 120s

- **Ngày:** 2026-07-30
- **Trạng thái:** hoàn thành (đã nghiệm thu 31/07)
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

- [x] Build 0 lỗi 0 warning; test không tụt (Core 43 ↑ từ 16, orders 1440 giữ nguyên).
- [x] Grep `"/json/list"`: URL chỉ dựng trong `CdpEndpoints.List`; các hit còn lại đều là comment/log-string.
- [x] Grep `SendCdpAsync` = 0 hit toàn repo.
- [x] 4 chỗ kill Brave đều gọi `BraveTeardown`; `KillByUserDataDir` chỉ còn trong Core
      (`BraveProcessReaper`, `BraveTeardown`, `BrowserLauncher`).
- [x] Báo cáo: bảng 20 điểm + xác nhận logic lọc target giữ nguyên (mục dưới).

## 5. Rủi ро & lưu ý

- Chờ 3A+3B merge xong mới chạy (chung BraveInstanceSession/BraveManager/SearchSession) — số dòng sẽ trôi thêm, tìm theo symbol.
- Timing quanh kill Brave nhạy (chống rò cửa sổ — memory BraveJobObject): giữ nguyên thứ tự bước + sleep của từng bản qua option.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Ngày:** 2026-07-30 · **Build:** `dotnet build ShopeeSuite.sln --no-incremental` = 0 lỗi 0 warning ·
**Test:** `suite/Shopee.Core.Tests` 43 xanh (16 cũ + 27 mới), `orders/XuLyDonShopee.Tests` 1440 xanh (không tụt).

### File mới (Core)

| File | Nội dung |
|---|---|
| `suite/Shopee.Core/Cdp/CdpEndpoints.cs` | Giữ quy tắc "luôn 127.0.0.1, KHÔNG localhost" + dựng `/json/list`, `/json/version`, `/json` , `/json/new`, `/json/close` |
| `suite/Shopee.Core/Cdp/CdpTarget.cs` | `record CdpTarget(Id, Type, Url, WsUrl, Title="")` + `IsPage`/`IsServiceWorker`/`HasWsUrl` + `ParseList(json)` |
| `suite/Shopee.Core/Cdp/CdpErrors.cs` | `IsTransientNavigationError(string?/Exception?)` — HỢP marker của 3 bản cũ |
| `suite/Shopee.Core/Browser/BraveTeardown.cs` | `KillAndReap(ref Process?, userDataDir, options)` + `Reap(...)`; `BraveTeardownOptions` = các bước lệch |
| `suite/Shopee.Core.Tests/CdpTargetsTests.cs` | CdpEndpoints + ParseList + ListTargets/TryListTargets/CloseTarget qua server HTTP giả (TcpListener 127.0.0.1) |
| `suite/Shopee.Core.Tests/CdpErrorsTests.cs` | Mỗi marker của 3 bản cũ đều còn nhận diện |
| `suite/Shopee.Core.Tests/BraveTeardownTests.cs` | Nhánh không tiến trình / tiến trình đã thoát / Reap không ngủ khi killed=0 |

### 3C — 20 điểm parse `/json/list` (logic lọc/chọn target GIỮ NGUYÊN ở từng chỗ)

| # | Điểm | Thay bằng | Ghi chú lọc |
|---|---|---|---|
| 1 | `CdpClient.GetPageWebSocketUrlAsync` | `ListTargetsAsync` | page + có ws — nguyên |
| 2 | `CdpClient.FindPageWebSocketUrlAsync` | `ListTargetsAsync` | page + urlMatches + có ws — nguyên |
| 3 | `CdpClient.ReloadPageTargetsAsync` | `TryListTargetsAsync` | như trên (HTTP lỗi → im lặng) |
| 4 | `CdpClient.NavigatePageTargetsAsync` | `TryListTargetsAsync` | như trên |
| 5 | `BigSellerCookieEngine.NavigateBigSellerTabsAsync` | `ListTargetsAsync` | page + IsBigSellerUrl + ws — nguyên |
| 6 | `ExtensionRunnerAutomation.TryWakeServiceWorkerAsync` (activateTarget) | `TryListTargetsAsync` | service_worker + url chứa extId + id ≠ rỗng |
| 7 | `DiscoverExtensionIdsFromBrowserAsync` | `TryListTargetsAsync` | mọi url → TryAddExtensionIdFromUrl |
| 8 | `CloseAllExtensionPopupTabsAsync` | `TryListTargetsAsync` + `CloseTargetAsync` | url bắt đầu `chrome-extension://` |
| 9 | `CloseRunnerExtensionPopupTabsAsync` | như trên | url == popup.html của runnerIds |
| 10 | `TrimAuxiliaryTabsAsync` | như trên | page + id ≠ rỗng, giữ ≥1 tab — nguyên |
| 11 | `GetSwDebuggerUrlFromListAsync` | `TryListTargetsAsync` | service_worker + extId + ws |
| 12 | `GetSwTargetIdFromListAsync` | `TryListTargetsAsync` | service_worker + extId + id |
| 13 | `GetAllSwTargetsSummaryAsync` | `ListTargetsAsync` | dump type/ws±/url — "(HTTP fail)" giữ qua `catch (HttpRequestException)` |
| 14 | `FindExtensionPopupDebuggerUrlAsync` | `TryListTargetsAsync` | url == popup.html + có ws |
| 15 | `FindExtensionPopupTargetIdAsync` | `TryListTargetsAsync` | url == popup.html + id ≠ rỗng |
| 16 | `PageCdpHelper.FindWorkPageTargetIdAsync` | `TryListTargetsAsync` | page, bỏ chrome:// & chrome-extension://, xếp theo hint — nguyên |
| 17 | `PageCdpHelper.EvaluateOnPageAsync` | `ListTargetsAsync` | như trên (EnsureSuccess cũ → vẫn ném) |
| 18 | `BraveInstanceSession.HasChromeProxyErrorPageAsync` | `ListTargetsAsync` | page + chrome-error:// / title No internet / ERR_PROXY… — nguyên (nhờ `Title` trong record) |
| 19 | `SE/BraveManager.CleanupRestoredTabsAsync` | `TryListTargetsAsync(timeoutMs:3000)` + `CloseTargetAsync` | page + id; thứ tự ưu tiên giữ tab — nguyên |
| 20 | `SE/SearchSession.WatchForVerifyAsync` | `ListTargetsAsync(timeoutMs:3000)` | page + url chứa `/verify/`, đủ 2 nhịp — nguyên |

Kèm: `/json/new`, `/json/close`, `/json/version`, `/json` và `TcpClient("127.0.0.1")` đều qua `CdpEndpoints`
(gồm `CdpSession` — nơi giữ comment gốc về quy tắc 127.0.0.1).

**Bước 3 — bỏ `ExtensionRunnerAutomation.SendCdpAsync`:** `CdpClient.SendAsync` thêm `sessionId` +
`receiveTimeoutMs`; 11 call site chuyển sang `CdpClient.SendAsync(..., receiveTimeoutMs: CdpReceiveTimeoutMs)`
(hằng 20s của module giữ nguyên, `receiveTimeoutOverride` 600s của scrape-step qua `ReceiveTimeoutMsOf`).
Alias `BraveInstanceSession.SendCdpAsync` cũng bỏ (7 call site gọi thẳng) để `SendCdpAsync` = 0 hit.

### 3D — 4 kịch bản kill Brave

| Chỗ | Option dùng | Móc riêng giữ ở caller |
|---|---|---|
| `BraveInstanceSession.KillBraveProcess` | `GracefulClose` (CloseMainWindow → CDP Browser.close), `WaitForExitMs=maxWaitMs`, `Log` | `PortCdpHub.ResetSoon()` trước |
| `UP/BigSellerBraveRunner.DisposeAsync` | `Log` | `BraveFleet.UnregisterActiveProfile` sau |
| `UP/BigSellerImportToStoreRunner.RestartBrowserAsync` | `Log` | — |
| `SE/BraveManager.Kill` | `IncludeCrashpadOrphans=true`, `SleepAfterReapMs=400` | `PortAllocator.Release` sau |

Không thêm bước cho bản không có: đóng-êm/WaitForExit CHỈ MultiBrave, crashpad+sleep CHỈ Search.
`SE/AppSettingsService` (reap khi CreateDirectory bị khoá) chuyển sang `BraveTeardown.Reap` → `KillByUserDataDir`
chỉ còn trong Core.

### Bước 5 — `IsTransientNavigationError` (marker = HỢP; bản nào thiếu gì)

| Marker | ImportToStore | BraveInstanceSession | IsTransientSwError |
|---|---|---|---|
| Execution context was destroyed | ✔ | ✔ | thiếu |
| most likely because of a navigation | ✔ | thiếu | thiếu |
| Cannot find context | ✔ (`…with specified id`) | ✔ | ✔ |
| Target closed | thiếu | ✔ | ✔ |
| Inspected target navigated or closed | thiếu | thiếu | ✔ |
| WebSocket | thiếu | ✔ (trần) | ✔ (`remote party closed the WebSocket`) |

⇒ 2 chỗ dùng trực tiếp (ImportToStore, BraveInstanceSession) NỚI RỘNG đúng theo yêu cầu plan;
`IsTransientSwError` (MultiBrave) giữ marker riêng của SW + gọi thêm `CdpErrors` nên cũng nới theo.

### Bước 6 — fix ngưỡng 120s

Chọn **gia hạn deadline vòng ngoài** (không hạ hằng số): trong nhánh CDP-unreachable, sau khi kiểm tra ngưỡng,
`deadline = max(deadline, cdpUnreachableSince + 120s)` — giống cách nhánh chờ-captcha đang làm. Lý do: 120s là
con số cố ý cho "Brave đang khởi động lại", hạ xuống 80s sẽ rút ngắn cửa sổ tái nối; gia hạn chỉ đụng 6 dòng và
không đổi hành vi khi CDP vẫn sống (deadline chỉ nới, không rút).

### Điểm lệch / cần soi lại

1. `CdpClient.SendAsync` nay ném `TimeoutException` (thay `OperationCanceledException`) khi hết trần chờ —
   giữ đúng hành vi bản module (chuỗi "quá thời gian" là tín hiệu retry của runner). Caller cũ của CdpClient
   (CookieService, BigSellerCookieEngine, BraveInstanceSession) đều bắt `Exception` chung → không đổi luồng.
2. Thông điệp lỗi hợp nhất theo bản Core: `"CDP error: {err}"` (module cũ `"CDP: {err}"`), `"CDP socket dong."`
   (cũ `"CDP đóng khi gọi extension."`). Không có logic nào so khớp 2 chuỗi này.
3. Phản hồi CDP thiếu `result`: bản module cũ trả `default(JsonElement)`, nay ném `"CDP result thieu."` như Core.
   Thực tế CDP luôn có `result` khi không lỗi (và `default` cũng ném ở bước TryGetProperty kế tiếp).
4. `params = null` nay KHÔNG gửi khoá `"params"` nữa (trước Core gửi `"params": null`) — theo bản module,
   đúng chuẩn CDP hơn. Ảnh hưởng: `Storage.getCookies`, `Runtime.enable`, `Browser.close`.
5. `FindPageWebSocketUrlAsync` (Core) trước trả `null` khi body không phải mảng, nay ném như các chỗ khác —
   chỉ khác ở ca không thể xảy ra (endpoint luôn trả mảng).
6. `BraveManager.CleanupRestoredTabsAsync`: vòng chờ target giờ ngủ 300ms cả khi list rỗng (trước chỉ ngủ khi
   HTTP lỗi → spin 6s). Vẫn tối đa 6s, timeout 3s/lần đọc giữ nguyên.
7. Còn 4 chỗ tự nội suy `http://127.0.0.1:{port}` cho Playwright `ConnectOverCDPAsync`
   (`BigSellerAutoLogin` ×3, `BigSellerBraveRunner`) — KHÔNG đổi vì ngoài phạm vi "/json/*"; muốn gom thì
   thay bằng `CdpEndpoints.Base(port)`.
8. `extensions/**` không có chỗ nào gọi `/json/list` (đã grep) nên không phải liệt kê thêm.
