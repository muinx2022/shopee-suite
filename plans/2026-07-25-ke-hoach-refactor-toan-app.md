# Plan: Kế hoạch refactor toàn app (đợt review 2026-07-25)

- **Ngày:** 2026-07-25
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (mỗi đợt sẽ có plan con riêng khi giao việc)

## 1. Bối cảnh & mục tiêu

Người dùng yêu cầu review toàn bộ code của app, chỉ rõ điểm cần cải thiện / trùng lặp / cần refactor, và lập kế hoạch sửa (chưa code). Đã chạy 6 lượt review song song (mỗi lượt một khu, đều kiểm chứng bằng đọc code + Grep toàn repo):

| Khu | Quy mô | Tình trạng chung |
|---|---|---|
| `suite/Shopee.Core` + `shared/` | ~90 file | Nền tốt sau 3 đợt dọn, nhưng còn 2 bug hành vi thật + 13 store JSON lặp khuôn |
| `suite/Shopee.Suite` (shell) | ~14.5k dòng | MVVM kỷ luật tốt; ~550 dòng code chết di sản hub nhúng |
| 4 module engine (MultiBrave/UpdateProduct/Search/CheckAccount) | — | Trùng lặp nặng nhất repo: bộ Shopee-login 3 bản LỆCH ngữ nghĩa, parse `/json/list` ~12 chỗ, 2 god class ~1.900 dòng |
| `orders/` (XuLyDonShopee) | ~26.5k dòng | ~4.000+ dòng Playwright chết sau pivot sang extension bridge; KHÔNG ref Shopee.Core nên chép tay lại hạ tầng; 1 nút UI hỏng thật |
| `server/Shopee.Hub.Web` | ~10k dòng | Sạch về security (không SQLi/XSS); nghẽn mutex toàn cục; Fleet.razor 1.240 dòng |
| `extensions/` (3 extension) | ~4.9k dòng | 2 race thật (reconnect search, FIFO waiter scrape); debugger orders không nhả; nhiều code chết pivot |

**Mục tiêu:** sửa hết bug hành vi đã phát hiện → dọn code chết → hợp nhất trùng lặp về Core/shared → tách god class → chuẩn hoá nhất quán. Thứ tự này là bắt buộc: dọn chết trước thì hợp nhất/tách nhẹ đi rất nhiều (vd `ShopeeLoginService` 6.739 dòng tự co về ~2.000 dòng chỉ nhờ dọn chết).

## 2. Phạm vi

- **Làm:** 5 đợt bên dưới, mỗi đợt chia thành các plan con độc lập, giao Opus lần lượt, mỗi plan con build + test + commit riêng.
- **Không làm:** viết lại kiến trúc; hợp nhất 3 extension làm một (khác footprint quyền — chủ đích giữ riêng); đổi hành vi nghiệp vụ; gỡ package Playwright của orders (bước login còn dùng).

## 3. Lộ trình 5 đợt

### Đợt 1 — Sửa bug hành vi (ưu tiên cao nhất, các mục độc lập nhau)

Plan con dự kiến: `don1a-core-suite`, `don1b-orders`, `don1c-extensions`, `don1d-hub`.

**1A. Core + Suite:**
1. `suite/Shopee.Core/Coordination/CoordinationRuntime.cs:345-353` — `Reconnect` không `Dispose()` HttpCoordinationHub cũ → poller 12s cũ sống mãi (máy "Ngắt kết nối" vẫn heartbeat; đổi URL/token có 2 poller song song). Sửa: giữ ref cũ, `old?.Dispose()` trước khi dựng mới; cân nhắc HubClient : IDisposable.
2. `suite/Shopee.Core/Coordination/HubConfigSync.cs:104,270` — ghi cookie bằng `File.WriteAllBytesAsync` KHÔNG atomic, vi phạm bất biến WriteAtomic (`BigSellerCookieEngine.cs:219-220`, torn-read từng gây hỏng cookie đa máy). Sửa: expose `BigSellerCookieEngine.TryWriteCookieFileBytes(path, bytes)` đi qua WriteAtomic, dùng ở 2 chỗ.
3. `suite/Shopee.Module.MultiBrave/Engine/BraveInstanceSession.cs:129-133` — timer async-void không bọc try/catch toàn thân → exception lọt = sập process. Bọc catch + log.
4. `BraveInstanceSession.cs:322-341` — guard `ResumeContinueAsync` không nguyên tử → 2 vòng runner trên cùng profile. Sửa bằng `Interlocked.CompareExchange` (như `_syncBusy`).
5. `suite/Shopee.Suite` — race "dừng êm để update" từ Hub: `Services/RemoteUpdateService.cs:54` chạy thread-pool đụng `AssignmentWorker._inflight` (Dictionary thường, `AssignmentWorker.cs:27`) mà Tick (UI thread) cũng đọc/ghi. Sửa: marshal về UiThread hoặc ConcurrentDictionary.
6. `suite/Shopee.Core/Cdp/CdpSession.cs:169-192` — timeout/cancel không remove entry khỏi `_pending` → rò dictionary phiên dài. Sửa: `Register` remove khi cancel. Kèm `CdpSession.cs:99-108` bỏ `new HttpClient` mỗi lần (dùng `AppServices.DirectHttp`) — đang bị poll 3s suốt phiên login.
7. `suite/Shopee.Suite/Infrastructure/AssignmentWorker.cs:120` — `catch { }` trong Tick 10s nuốt mọi lỗi vĩnh viễn ("máy không nhận việc" câm lặng). Thêm log có throttle. Kèm helper `FireAndForget(task, tag)` cho các chỗ `try { _ = XxxAsync(); } catch { }` (`ScrapeViewModel.cs:428`, `AssignmentWorker.cs:403`, `AccountLeaseScope.cs:130`) và catch cho `BigSellerViewModel.LoginAsync:245-318`.

**1B. Orders:**
1. Nút "Tải phiếu" HỎNG thật: `AccountSession.cs:869-891` (`RedownloadSlipAsync`) luôn fail vì `_session` chỉ được gán trong `RunAsync` đã chết; UI vẫn gọi (`OrdersViewModel.cs:423`). Sửa: thêm action `redownloadSlip` qua bridge (extension shopee-orders), hoặc tạm gỡ nút + thông báo đúng — hỏi user chọn hướng khi giao việc.
2. Guard 1-bridge-một-lúc ở `AccountSessionManager`: cổng 47821 cố định + `KillBrowsersOnProfile` (`OrdersBridgeSession.cs:143,226-227`) giết chéo trình duyệt account khác khi "Chạy đã chọn" nhiều tài khoản. Xếp hàng tuần tự các account.
3. `OrdersWebSocketServer.cs:112-115` — `SendAsync` nuốt lệnh khi socket chưa nối → caller chờ 30-300s timeout với thông điệp sai hướng. Sửa fail-fast. Kèm fix fault-11-TCS (`OrdersBridgeSession.cs:871-888` → chỉ fault TCS đang chờ).
4. `AccountSession.cs:213,240` — `StopAsync` đặt Stopped sau 8s dù vòng nền chưa xong → phiên mới tranh cổng/profile với phiên đang tháo dỡ. Chỉ đặt Stopped khi `_runTask` thật xong.

**1C. Extensions:**
1. `shopee-search/background.js:17-36` — reconnect không guard socket sống + không huỷ timer → C# gửi lại "start", run tự khởi động lại giữa chừng. Sửa theo mẫu orders (`shopee-orders/background.js:41-63`).
2. `shopee-search/background.js:57-80` — reject toàn bộ `_gestPending` khi WS đóng (hiện treo 30s/cú).
3. `shopee-scrape/background.js:964-973` — FIFO waiter nhận nhầm kết quả cũ sau re-inject → báo ok/fail sai dòng. Gắn token (rowNumber + nonce) vào mỗi lần inject.
4. `shopee-orders/background.js:794-807` — debugger attach giữ mãi (`releaseDbg` 0 caller, banner "đang gỡ lỗi" treo suốt). Gọi `releaseDbg()` cuối `doPrepareNextOrder`/`gotoSellerCentre`.
5. `shopee-search/background.js:1696,1861,792` — selector theo index (`sortButtons[2]`) dễ click nhầm khi Shopee đổi thứ tự → đảo ưu tiên text-match trước, index làm fallback.

**1D. Hub web:**
1. `Components/Pages/Orders.razor:27` — query DB mỗi phím gõ, đồng bộ, dưới `lock (_gate)` toàn cục của cả fleet. Debounce ~300ms hoặc tìm khi Enter.
2. `Data/HubDatabase.Assignments.cs:33,46,469` — chuỗi tiếng Việt `'hết nhịp (máy nhận có thể đã thoát)'` làm cờ so khớp SQL → `const string StaleSweepError` dùng cả 3 chỗ.
3. `RewriteJobService.cs:240-249` + `Fleet.razor:330-334` (và `LoginState.Log` + `AccountConfigPanel.razor:42`) — render `List<>` xuyên thread → snapshot dưới lock.
4. `Data/HubDatabase.Shops.cs:126-141` (`DeleteShop`), `Machines.cs:64-97` (`ResetMachineWork`) — bọc transaction.
5. `Program.cs:110-116` — `KnownNetworks/KnownProxies.Clear()` → giới hạn về 127.0.0.0/8; prune `_hits` trong `LoginRateLimit`.

### Đợt 2 — Dọn code chết (sau đợt 1; mỗi khu 1 plan con, mỗi khối 1 commit)

**2A. Orders (~4.500+ dòng, lớn nhất):** `AccountSession.RunAsync` (1892-2478) + 5 method sống nhờ `_session` chết (`ProcessOrdersAsync:287`, `CheckOrdersAsync:587`, `SyncOrdersAsync:674`, `SyncFullAsync:941`, `ChayFlowMotShopAsync:981`) + thu hẹp `IAccountSession`; ~4.000 dòng flow Playwright trong `LoginSession` chỉ được gọi từ đường chết (giữ: `OpenAsync`, `TryLoginSubaccountAsync`, proxy-auth, human-input, MS-mail-login, parsers — đường bridge còn dùng); `PlaywrightProxyMapper` + test; forwarder mồ côi `OpenMailboxSignedInAsync:376`; test neo code chết (`AccountSessionLoopTests`, phần Playwright của `ShopeeShippingNavTests`/`SlipRedownloadTests`). Quyết định kèm: số phận hệ proxy trên đường bridge (nối vào hoặc ẩn màn Proxy) — hỏi user. LƯU Ý: dọn xong `ShopeeLoginService` tự co về ~2.000 dòng — điều kiện tiên quyết cho đợt 4.

**2B. Suite shell (~800+ dòng):** FleetViewModel nhánh hub-board + Search-đa-máy (~550 dòng, liệt kê chi tiết trong báo cáo review: `IsHubBoard`, `MachineRows/Queue/Pin*`, khối Search đa máy 119-151 + 538-773, 5 class row, `SearchExportFilterWindow`); cả class `Infrastructure/HubDispatcher.cs`; 2 khối `AccountsViewModel` (panel "Acc client báo lỗi" 445-499 + flow `CheckErrored` 163-312); đường batch `UpdateProductViewModel` (158-167, 339-401, 478-506 — giữ `IsRunning` vì `WorkspaceViewModel.AnyRunning:49` đọc); lệnh mồ côi `ScrapeViewModel` (`SelectAll/UnselectAll:103-107`, `Run/Resume:110-115`, `ToggleAccountDuringRun:619-641`).

**2C. Core:** cụm `IHubSync`/`NoOpHubSync`/`Coordination.Sync` + `ILeaseStore`/`ILedgerStore` (`ICoordinationHub.cs`, `Coordination.cs`); `CookieFileHelper.ParseCookiesRoot` + `TryWriteCookieFile` (bản trùng WriteAtomic); `ScrapeWorkbook.ListSheets:21-33`; `SuitePaths.RepoRoot/ResolveRepoRoot:68-79` + `ResolveHubRelative:57-58`; `BrowserLauncher.DetectExePath:28`; `BraveFleet.AutoMaxWindows:73`.

**2D. Modules:** `MB/Engine/ApiNotRunningException.cs` + nhánh catch `BraveInstanceSession.cs:502-511`; `ApiBase` (2 AppSession) + `RepoRootDirectory/FindRepoRoot` (UP); `UP/Engine/PortAllocator` các method `AllocateInstancePort/AllocateCookiePort` + field kèm; `SearchTaskStore.GetShopProducts:495-526`; biến thừa `ExtensionRunnerAutomation.cs:1134`; tham số chết `:1567-1574`; dòng lặp vô ích `SearchRunner.cs:150`.

**2E. Hub web:** 7 endpoint không consumer (`/api/shops`, `/api/orders`, `/accounts/append`, `/accounts/remove`, `GET/POST /dispatcher`, `/products/import-xlsx`, `/products/export-xlsx`) — **PHẢI soi log hub trên VM xác nhận không client cũ nào gọi trước khi xoá** (chắc chắn nhất: `/dispatcher` + cặp xlsx); `HubDatabase.GetShop`, `FleetStateService.Presence`, `RowRangeMath.MaxRow/Complement` (bản server), 8 key `HubIcons` mồ côi.

**2F. Extensions:** search — máy `pause`/`resume` + `waitWhilePaused` (C# không bao giờ gửi; hoặc xoá hoặc nối nút Pause — hỏi user), `shopInfo`, `DELAY_MS`/`getPageHtml`/`rawSleep`, `state.filters`, content.js no-op; orders — `withDebugger`/`keyInfo`/`dbgType`/`dbgEnter`, nhánh `hello`, field `invoiceDir` phía C#; scrape — 3 const thừa; quyết định số phận `shopee-orders-test/` (vẫn được `BraveLaunchArgs.cs:131` + `PocCleanLauncher` nạp — hỏi user).

### Đợt 3 — Hợp nhất trùng lặp về Core/shared (sau đợt 2)

**3A. Bộ Shopee-login về Core (ưu tiên nhất — 3 bản đang LỆCH ngữ nghĩa parse tài khoản):** class `ShopeeSession` trong Shopee.Core gồm ParseLoginLine / IsLoggedInAsync (SPC_ST/SPC_EC) / SetSpcFAsync / JS tìm nút login / vòng chờ login. Thay 3 bản: `MB/Engine/ShopeeLoginAutomation.cs:5-55` + `BraveInstanceSession.cs:1457-1782`, `SE/Engine/ShopeeLoginService.cs`, `CA/ShopeeAccountChecker.cs:283-572`. Kèm hằng `SPC_ST/SPC_EC/SPC_F`, URL login.

**3B. Human-input CDP về Core:** `Shopee.Core/Cdp/HumanInput.cs` từ 2 bản C# byte-tương-đương (`SE/Engine/CdpInputController.cs:140-277`, `CA/ShopeeAccountChecker.cs:452-529`) + cân nhắc bản JS (`BraveInstanceSession.cs:1689-1741`).

**3C. `CdpClient.ListTargetsAsync()`** trả `record CdpTarget(Id, Type, Url, WsUrl)` + `CloseTargetAsync` — thay ~12 chỗ tự parse `/json/list` (8 trong `ExtensionRunnerAutomation`, `BraveInstanceSession:1426-1452`, `SE/BraveManager:77-143`, 4 method trong `CdpClient` 49-190). Kèm: bổ sung `sessionId`/`receiveTimeout` vào `CdpClient.SendAsync` rồi xoá `ExtensionRunnerAutomation.SendCdpAsync:1813-1891`; helper `CdpEndpoints` (quy tắc "127.0.0.1 không localhost" hiện chỉ ghi 1 chỗ).

**3D. Hợp nhất hạ tầng module:** `AppSession` + `PortAllocator` MB↔UP về Core (tham số hoá base-port; Core PortAllocator đã có, Search đang dùng); kịch bản kill Brave `BraveTeardown.KillAndReap` (4 bản: `BraveInstanceSession:848-885`, `BigSellerBraveRunner:206-218`, `BigSellerImportToStoreRunner:364-379`, `SE/BraveManager:272-302`); helper `IsTransientNavigationError` (3 danh sách gần trùng).

**3E. Core nội bộ:** `JsonAtomicFile.TryLoad<T>/TrySave` thay khuôn Load/SaveLocked của 13 store (chuẩn hoá luôn API: trả bool, event ngoài lock); LoginRunner tham chiếu hằng/predicate/payload của Engine (4 cặp trùng); `AiChat` tách `BuildRequest/ParseText` dùng chung 2 method; Core Kiot client chuyển về dùng `shared/Shopee.Proxy.Kiot` (orders đã làm đúng mẫu); `NoBom`, `JsonElement.ToClrValue()`, `ExcelColumn.ToLetter`, `StartupJanitor` gộp lặt vặt.

**3F. Orders dùng chung hạ tầng (mẫu `shared/Shopee.Proxy.Kiot` — orders không ref Shopee.Core để né dây Avalonia):** đưa về `shared/`: WebSocketServer (orders tự khai "chép khuôn module Search"), Brave launch args (trùng `BraveArgsBuilder`), BrowserLocator (2 bản đã lệch hành vi — bản suite có registry fallback), MS-mail-login (2 bộ selector Microsoft phải bảo trì song song: `ShopeeLoginService.LoginHotmailAsync:1607` ↔ `HotmailOtpReader`).

**3G. Extensions `extensions/shared/` copy lúc build** (gắn vào `release-suite.cmd`/publish): ws-bridge (mẫu guarded của orders), tab-wait (bản scrape chuẩn nhất), verify/network-error detect (hợp nhất marker — 2 danh sách hiện bổ khuyết nhau), dbg-input, sleep/rand. Search manifest đã `"type":"module"` nên import được; orders/scrape dùng `importScripts` hoặc chuyển module.

### Đợt 4 — Tách god class (sau đợt 2-3 đã làm nhẹ file)

1. `BraveInstanceSession` (1.956 dòng) → `BraveProcessController`, `KiotProxyRotator`, `ShopeeSessionBootstrapper` (teo nhờ 3A), `BigSellerTokenGuard`, `SessionMonitor`; session giữ làm facade.
2. `ExtensionRunnerAutomation` (1.908 dòng) → `CdpTargetDirectory` (vào Core, trùng 3C), `RunnerSwLifecycle`, `RunnerExtensionRpc`; dời `ResolveEndRowAsync/FetchSheetLinksAsync:1675-1742` sang lớp dữ liệu (cạnh ScrapeWorkbook).
3. `ShopeeLoginService` (sau dọn ~2.000 dòng) → `BrowserBootstrap`, `HumanInput` (dùng chung 3B nếu được), `MicrosoftMailLogin` (3F), `SubaccountLoginFlow`, `Parsers`.
4. `AccountSession` (2.520 dòng) → tách `OrderPersistPipeline` (persist + GSheet + hub + sold + notify + `NenXoaDonKetThuc` — thuần DTO/DB/HTTP, TEST ĐƯỢC) + `SlipFiles` static; còn lại lifecycle + vòng bridge ~600-700 dòng.
5. `Fleet.razor` (1.240 dòng) → code-behind `Fleet.razor.cs` trước (0 rủi ro), rồi `ShopActionTab.razor`, `AcctDashboard.razor`, `ShopStatsCards.razor`, `WorkspaceShopList.razor`, model + `Rebuild()` sang `FleetRowsBuilder`.
6. `shopee-search/background.js` (2.455 dòng) → ~7 module ES (ws / tabs / detect / flow-keyword / flow-shop / flow-category / page-funcs-synthetic / extract); page-func synthetic dùng pattern `pageInstallHelpers` của orders để khử 4 bản helper chuột.
7. `ScrapeViewModel` → dời `SessionAccountPool`/`RunSession` ra file riêng; `BigSellerCookieEngine` (788) → 3 partial (CookieFile / CookieImporter 2 transport / SessionPolicy); `HotmailOtpReader` tách helper DOM generic.
8. Bổ sung test cho vùng rủi ro nhất chưa có test: `OrdersBridgeSession` + `OrdersWebSocketServer` (0 test hiện tại — fake WS server bắn message: captcha fan-out, error fan-out, timeout từng chặng, callback persist 2 lần).

### Đợt 5 — Nhất quán (làm dần, ưu tiên thấp)

1. Hằng `AssignmentOps`/`AssignmentStatus` trong `Shopee.Core.Coordination` dùng chung client + hub (magic string ~54 chỗ/8 file phía suite + ~40 chỗ phía hub; `CoordOp` đã có nhưng dùng lẫn literal).
2. Hub URL-state cho `/orders` (nghiệp vụ nhất, làm trước), `/logs-view`, `/config/accounts`; helper `UrlState` chung (2 bản chép tay `Fleet.razor:873-934` ↔ `AllData.razor:230-269`); thống nhất tên param `p/ps` vs `page/size`; component `ProductGridPager` + `ProductGridActions` (trùng `AllData` ↔ `ProductGridPanel`); hằng `OnlineThreshold` 45s (3 nơi); endpoint filter Pg-not-ready (21 khối lặp `ProductApiEndpoints`).
3. Magic number/timeout đặt tên theo mẫu `LauncherRunnerLoop.cs:7-10` (CDP-ready attempts 20/40/90/30, chờ login 90/70/25s, retry trần 2-12, timeout bridge 30-300s, chốt chặn đơn 50 vs 200); thống nhất nhịp token write-back (throttle 90s dùng chung `BigSellerTokenWriteBack`).
4. `SearchTaskStore` → `DateTime.UtcNow` (7 chỗ `Now` làm khoá ORDER BY); log `catch {}` các bước điền SP (`BigSellerProductUpdateRunner` 7 chỗ); `ex.ToString()` cho nhánh catch bất ngờ phía orders; sửa mojibake comment `ExtensionRunnerAutomation.cs`; thống nhất envelope message extension (`action`/`kind`/`type`/`cmd`) khi làm 3G; hằng cổng 47821/9111 một nơi mỗi phía; `ConfigureAwait(false)` nhất quán trong Core; ghi quy ước "tên Việt cho luật nghiệp vụ" vào CLAUDE.md của orders.

## 4. Tiêu chí nghiệm thu (mức lộ trình)

- [ ] Mỗi plan con: build sạch (`dotnet build` solution), test xanh (`dotnet test` orders + suite), hành vi module liên quan không đổi (trừ bug được sửa chủ đích).
- [ ] Đợt 1 xong: máy Ngắt kết nối không còn heartbeat; nút Tải phiếu hoạt động hoặc gỡ; chạy 2 account orders không kill chéo; search không tự restart khi WS chập chờn.
- [ ] Đợt 2 xong: `ShopeeLoginService` ≤ ~2.200 dòng; grep các symbol đã xoá = 0 hit; app + hub chạy bình thường (smoke test tay).
- [ ] Đợt 3 xong: bộ Shopee-login/human-input/ListTargets chỉ còn 1 bản trong Core; orders dùng shared/ cho WS + Brave args + locator + MS-login.
- [ ] Đợt 4 xong: không file code nào > ~800 dòng trừ ngoại lệ khai báo rõ; `OrdersBridgeSession` có test.
- [ ] Sau mỗi đợt: chạy production vài ngày trước khi sang đợt sau (bài học từ các đợt dọn trước).

## 5. Rủi ro & lưu ý

- **Code automation anti-bot rất nhạy hành vi** — mọi hợp nhất (3A/3B) phải giữ nguyên tham số delay/easing/thứ tự thao tác từng byte; đổi = dễ dính captcha. So sánh trước/sau bằng cách diff logic, không "tiện tay cải thiện".
- **Hub endpoints (2E)**: bắt buộc soi log trên VM (client cũ ngoài fleet có thể còn gọi `/accounts/append`).
- **3 bản parse tài khoản Shopee đang LỆCH** (MB đòi ≥3 phần + prefix `SPC_F=`; CA chấp nhận 2 phần) — khi hợp nhất phải chọn ngữ nghĩa đúng và kiểm tra lại file tài khoản thật của user, tránh làm tk đang chạy được bỗng bị loại.
- Các quyết định để ngỏ cần hỏi user trước khi giao việc: hướng sửa nút Tải phiếu (bridge action vs gỡ nút); số phận hệ proxy orders; pause/resume search (xoá hay nối nút); giữ hay gỡ `shopee-orders-test/`.
- Worktree song song: các plan con cùng đợt khác khu (vd 1B orders vs 1D hub) chạy song song được; cùng khu thì tuần tự.
- Không xoá worktree khi còn thay đổi chưa commit; commit sau mỗi plan con nghiệm thu đạt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

(chưa bắt đầu — đây là plan tổng, mỗi đợt sẽ có plan con riêng)
