# Plan: Tách god class module MultiBrave + test ParseLoginLine (đợt 4 — suite MB)

- **Ngày:** 2026-07-30
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

Sau 3A-3D, hai file lớn nhất module MultiBrave vẫn quá cỡ:
- `suite/Shopee.Module.MultiBrave/Engine/BraveInstanceSession.cs` (~1.900 dòng) — vòng đời 1 cửa sổ Brave scrape: process, kho account, proxy Kiot xoay vòng, login Shopee (flow đã gọi ShopeeAuth), guard token BigSeller, monitor.
- `suite/Shopee.Module.MultiBrave/Engine/ExtensionRunnerAutomation.cs` (~1.850 dòng) — điều khiển extension runner qua CDP (đã dùng CdpClient.ListTargets sau 3C).

Mục tiêu (refactor thuần): tách theo trục plan 25/07, session giữ làm facade:
1. `BraveInstanceSession` → **`BraveProcessController`** (launch/kill/teardown — đã gọi BraveTeardown), **`KiotProxyRotator`** (xoay proxy Kiot), **`ShopeeSessionBootstrapper`** (flow login Shopee — phần gọi ShopeeAuth/FillShopeeLoginFormAsync, GIỮ NGUYÊN JS typeHuman), **`BigSellerTokenGuard`** (import/write-back muc_token qua BigSellerCookieEngine), **`SessionMonitor`** (timer giám sát). Cấu trúc được phép chỉnh theo thực tế — ghi rõ.
2. `ExtensionRunnerAutomation` → **`RunnerSwLifecycle`** (wake/discover/attach service worker), **`RunnerExtensionRpc`** (gửi lệnh + chờ kết quả), dời **`ResolveEndRowAsync`/`FetchSheetLinksAsync`** sang lớp dữ liệu cạnh `ScrapeWorkbook` (Shopee.Core/Scrape).
3. **Test `ShopeeAuth.ParseLoginLine`** vào `suite/Shopee.Core.Tests` (món nợ 3A): bộ case theo plan 3A mục 4 — dòng MB chuẩn (SPC_F= + '|' trong cookie), dòng SE (không prefix, '=' thứ 2), dòng CA 2 phần không cookie, thiếu password (SE pass, MB/CA fail), cookie prefix lạ (MB nay nhận), ≥8 ca.

## 2. Phạm vi

- **Làm:** 3 việc trên; khu `suite/Shopee.Module.MultiBrave/**` + `suite/Shopee.Core/Scrape/**` (chỗ nhận 2 method dời) + `suite/Shopee.Core.Tests/**`.
- **Không làm:** KHÔNG đổi hành vi/delay/thứ tự thao tác (anti-bot); KHÔNG đụng `orders/**`, `server/**`, `extensions/**`, `shared/**`, module khác; KHÔNG commit.

## 3. Các bước & tiêu chí

1. Đọc 2 file; tách từng khối, build sau mỗi khối; DI qua constructor, session giữ field/property công khai cũ (caller ngoài không phải đổi trừ using).
2. Test ParseLoginLine (mục 3).
3. Nghiệm thu:
- [ ] Build 0/0; `dotnet test suite/Shopee.Core.Tests` ≥ 43 + test mới; orders 1440 giữ nguyên.
- [ ] `BraveInstanceSession.cs` ≤ ~700 dòng (facade), `ExtensionRunnerAutomation.cs` ≤ ~700; không file mới > ~800.
- [ ] Bảng "khối → file mới" + cam kết delay/thứ tự không đổi trong báo cáo.

## 5. Rủi ro & lưu ý

- Vùng anti-bot nhạy nhất repo — chỉ DI CHUYỂN. Timer/monitor có race đã sửa (1A.3/1A.4 — Interlocked, try/catch async-void): giữ nguyên các guard đó.
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Xong cả 3 việc. 3/3 tiêu chí nghiệm thu ĐẠT.** Nền: worktree đã `git merge --ff-only main` lên `ba653ed` trước khi làm.

### A. Bảng "khối → file mới"

**Việc 1 — `BraveInstanceSession` (1.901 dòng) → 5 cộng tác viên + 4 file partial**

| Khối tách ra | File mới | Nội dung |
|---|---|---|
| Tiến trình Brave | `BraveProcessController.cs` (152) | `_braveProcess`, LaunchBrave→`Launch`, KillBraveProcess→`Kill`, TryCloseBraveGracefully, KillBraveAndWaitPortFreeAsync, IsCdpPortReachableAsync, BringWindowToFront |
| Xoay proxy Kiot | `KiotProxyRotator.cs` (145) | ResolveProxyForLaunchAsync→`ResolveForLaunchAsync`, GetWorkingProxyAsync, GetProxyAsync, GetCurrentProxyAsync, BuildProxyServer/BuildFingerprint/IsProxyExpiredError/NormalizeManualProxy |
| Login Shopee | `ShopeeSessionBootstrapper.cs` (327) | EnsureShopeeLoggedInAsync→`EnsureLoggedInAsync`, OpenShopeeAccountLoginAsync→`OpenAccountLoginAsync`, IsShopeeLoggedInAsync, InjectShopeeSessionCookiesAsync, SetShopeeSpcFCookieAsync, OpenShopeeLoginPageAsync, **FillShopeeLoginFormAsync (JS `typeHuman` NGUYÊN XI)**, ClearShopeeLoginPendingFlag, `_shopeeSessionProfileDir` |
| Token BigSeller | `BigSellerTokenGuard.cs` (182) | `_bigSellerCookieFile`+`_bigSellerProxy*`, SetBigSellerCookieFile/SetBigSellerProxy, ResolveBigSellerProxyServerAsync, HasBigSellerAuthAsync, WriteBackBigSellerTokenAsync, HasBigSellerPassword, TryAutoLoginBigSellerAsync, ImportBigSellerCookiesIfConfiguredAsync |
| Timer giám sát | `SessionMonitor.cs` (287) | `_monitorTimer`+`_progressTimer`, **`_restarting`** (chỉ dùng trong 2 bước này nên chuyển hẳn), watchdog state, CheckRunnerStallAndRecoverAsync, CheckProxyAndRestartIfNeededAsync, HasChromeProxyErrorPageAsync + interface `ISessionMonitorHost` |
| — | `BraveInstanceSession.cs` (**343**) | State + ctor + public surface (facade) + Start/Stop/Dispose + Log/SetStatus + impl `ISessionMonitorHost` |
| — | `BraveInstanceSession.RunnerLoop.cs` (366) | ResumeContinueAsync, StopRunnerAsync, StopRunningWorkAsync, SW pinner, IsExtensionConnectionError |
| — | `BraveInstanceSession.Profile.cs` (198) | BringUpProfileAsync, RelaunchGate/RelaunchProfileAsync, Restart* family, ResolveProfileRoot, EnsureProfile, BuildBraveArguments |
| — | `BraveInstanceSession.Progress.cs` (165) | SyncExtensionProgress* family, TrySyncFromFileOnly, RefreshRunStatusFromConfig |

**Việc 2 — `ExtensionRunnerAutomation` (1.698 dòng) → 4 file + lớp dữ liệu ở Core**

| Khối | File mới | Nội dung |
|---|---|---|
| Vòng đời SW | `RunnerSwLifecycle.cs` (515) | EnsureRunnerExtensionReadyAsync, TryWakeServiceWorkerAsync, TryReloadExtensionAsync, PinSwWithFlatSessionAsync, ResolveExtensionIdAsync + cache, ProbeExtensionWithReasonAsync |
| Gửi lệnh + chờ kết quả | `RunnerExtensionRpc.cs` (724) | 10 method lệnh (ExecuteScrapeStep…TryBroadcastRunnerState), Build*Expression, EvaluateExtension{Method,Raw}Async, TryEvaluateOnServiceWorkerAsync, phân loại lỗi, MapStateFromCdp, 2 record kết quả |
| Tra cứu target | `RunnerExtensionTargets.cs` (303) | Discover* (Preferences/browser), GetSw{DebuggerUrl,TargetId}FromList, FindExtensionPopup*, GetAllSwTargetsSummary, ConnectBrowserWebSocket, IsCdpPortReachable |
| Dọn tab | `RunnerExtensionTabs.cs` (118) | CloseAll/CloseRunner popup, TrimAuxiliaryTabsAsync + predicate URL |
| Đọc dữ liệu link | `suite/Shopee.Core/Scrape/ScrapeLinkSource.cs` (95) **MỚI** | ResolveEndRowAsync + FetchSheetLinksAsync + record SheetLinkItem/SheetLinkFetchResult |
| — | `ExtensionRunnerAutomation.cs` (**128**) | Còn lại là **facade** uỷ quyền — 20 call site ở LauncherRunnerLoop/BraveInstanceSession/ExtensionProgressCoordinator KHÔNG phải sửa |

**Việc 3** — `suite/Shopee.Core.Tests/ShopeeAuthParseLoginLineTests.cs` (185 dòng, **18 test**): dòng MB chuẩn (`SPC_F=` + `'|'` trong cookie) · prefix chữ thường · dòng SE không prefix lấy `=` thứ 2 · dòng CA 2 phần không cookie (MB cùng dòng → fail) · thiếu password (SE pass, MB+CA fail) · thiếu username · **cookie prefix lạ (MB nay nhận)** · cookie không có `=` (MB lỗi, CA bỏ qua) · cookie rỗng · không có tên cookie · SPC_F rỗng · khoảng trắng thừa · null/rỗng/1-phần × 3 bộ cờ.

### B. Cam kết delay / thứ tự thao tác KHÔNG ĐỔI

Kiểm chứng **bằng máy**, không bằng mắt: script so **túi dòng code** (đã trim) của file gốc ở `HEAD` với hợp của các file mới.

| File gốc | Dòng code không khớp | Bản chất 100% số chênh |
|---|---|---|
| `ExtensionRunnerAutomation.cs` | 66 | đổi access modifier (`private`→`public`/`internal`) · thêm tiền tố lớp (`ResolveExtensionIdAsync`→`RunnerSwLifecycle.ResolveExtensionIdAsync`) · bỏ tên đủ (`Shopee.Core.Scrape.ScrapeWorkbook`→`ScrapeWorkbook`, do đã cùng namespace) · bỏ `using System.Text` không dùng |
| `BraveInstanceSession.cs` | 205 | như trên + đổi tên field/tham số khi ra lớp riêng (`_bigSellerCookieFile`→`_cookieFile`, `_config`→`config()`, `_running`→`isRunning()`, `Log`→`log`) + rút gọn tên method theo ngữ cảnh lớp (`IsShopeeLoggedInAsync`→`IsLoggedInAsync`) |

**KHÔNG có** dòng nào chứa `Task.Delay`, hằng thời gian, ngưỡng retry, hay thứ tự thao tác bị mất/đổi. Mọi guard giữ nguyên: `Interlocked.CompareExchange`/`Exchange` (claim vòng runner, `_syncBusy`), try/catch toàn thân trong `Elapsed` async-void, tự-Dispose CTS theo vòng, `RelaunchGate` (2), `WatchdogStallTimeout` 8', warmup acquire/release, quy tắc "không nạp đè token BigSeller đang sống".

**1 điểm script BẮT ĐƯỢC và đã sửa:** catch cuối của `_monitorTimer.Elapsed` bản gốc dùng logger **THÔ** `_log(...)` chứ không phải `Log(...)` (Log còn bắn event + ghi `ScrapeFileLog` → có thể ném lại ngay trong catch của async-void). Bản đầu của tôi map nhầm sang `Log`; đã thêm `ISessionMonitorHost.LogRaw` để giữ đúng `_log` như cũ.

### C. Nghiệm thu

| Tiêu chí | Kết quả |
|---|---|
| `dotnet build ShopeeSuite.sln` 0/0 | **ĐẠT** — Build succeeded, 0 Warning(s), 0 Error(s) |
| `dotnet test suite/Shopee.Core.Tests` ≥ 43 + test mới | **ĐẠT** — 61/61 pass (43 nền + 18 mới) |
| orders 1440 giữ nguyên | **ĐẠT** — 1440/1440 pass |
| `BraveInstanceSession.cs` ≤ ~700 | **ĐẠT — 343** |
| `ExtensionRunnerAutomation.cs` ≤ ~700 | **ĐẠT — 128** |
| Không file mới > ~800 | **ĐẠT** — lớn nhất `RunnerExtensionRpc.cs` 724 |
| Bảng khối→file + cam kết delay | **ĐẠT** — mục A + B |

### D. Điểm lệch so với plan / cần phiên chính soi lại

1. **`ExtensionRunnerAutomation` GIỮ LẠI làm facade** (plan viết "→" nên có thể hiểu là xoá hẳn). Chọn giữ vì: tiêu chí nghiệm thu ra điều kiện độ dài cho chính file này (ngụ ý file còn tồn tại), và giữ facade thì **20 call site không phải sửa** — trong đó có `LauncherRunnerLoop`, đường scrape nóng nhất. Nếu phiên chính muốn xoá hẳn facade thì chỉ là đổi tên ở 20 chỗ, compiler bắt hết.
2. **Tách 4 file thay vì 2 cho việc 2**: bản 2-file cho `RunnerSwLifecycle` = 908 dòng, **vượt trần ~800**. Đã tách tiếp phần tra-cứu-target (`RunnerExtensionTargets`) và dọn-tab (`RunnerExtensionTabs`).
3. **`BraveInstanceSession` dùng partial 4 file** (plan chỉ nói "session giữ làm facade"). Lý do: sau khi bóc 5 cộng tác viên, thân còn ~1.070 dòng — vẫn vượt trần 700. Vòng chạy runner (`ResumeContinueAsync`) quấn quá chặt với state phiên (`_runnerLoopActive`/`_runnerLoopRequested`/`_runnerResuming`/warmup) nên bóc ra lớp riêng sẽ cần ~15 delegate = rủi ro cao ở đúng vùng anti-bot → chọn partial (đúng house style: `BigSellerProductUpdateRunner.*.cs`, `HubDatabase` 8 partial).
4. **`ResolveEndRowAsync`/`FetchSheetLinksAsync` KHÔNG forward qua facade** — 2 call site trong `LauncherRunnerLoop` gọi thẳng `ScrapeLinkSource`. Cố ý: đó là việc đọc dữ liệu, không nên với tới qua cửa "extension automation".
5. **`SessionMonitor` nói chuyện với phiên qua interface `ISessionMonitorHost`** (11 thành viên) thay vì ~10 delegate rời — phiên hiện thực **explicit** nên API công khai của `BraveInstanceSession` không phình thêm.
6. **50 ký tự U+FFFD (mojibake) trong comment** của `ExtensionRunnerAutomation` cũ: **giữ nguyên xi** khi chuyển (không đoán lại chữ) để diff là move thuần. Nên dọn ở một đợt riêng.
7. **Chưa chạy thật.** Đây là refactor thuần đã kiểm chứng bằng build + 1.501 test, nhưng module MultiBrave không có test tự động và tôi không phóng được Brave/CDP trong worktree. Vùng đáng liếc mắt khi chạy thật: watchdog mở lại profile, đường "mất phiên BigSeller giữa chừng", và `Stop`/`Dispose` (chỗ tôi đổi `_monitorTimer`/`_progressTimer` thành `_monitor.StopTimers()`/`_monitor.Dispose()`).
