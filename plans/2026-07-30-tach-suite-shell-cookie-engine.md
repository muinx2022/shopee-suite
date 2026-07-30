# Plan: Tách ScrapeViewModel + OrdersModuleHost + BigSellerCookieEngine partial (đợt 4 — suite shell)

- **Ngày:** 2026-07-30
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

3 file quá cỡ còn lại phía suite shell/Core (đo 30/07): `suite/Shopee.Suite/Modules/Scrape/ScrapeViewModel.cs` ~875 dòng; `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs` ~1.073 dòng (host module Đơn hàng trong suite: wiring services, heartbeat/hub, prepare-stats — phần vừa sửa B2, ActivityLog.Dispose ở StopAsync); `suite/Shopee.Core/BigSeller/BigSellerCookieEngine.cs` ~800 dòng (sau 3C + vá UnauthorizedAccessException).

Mục tiêu (refactor thuần, KHÔNG đổi hành vi):
1. `ScrapeViewModel` → dời `SessionAccountPool` + `RunSession` (class lồng/khối lớn) ra file riêng cùng thư mục; VM còn ≤ ~600 dòng.
2. `OrdersModuleHost` → tách theo trục thực tế (đọc file rồi quyết, đề xuất: wiring/bootstrap services riêng, cụm hub (heartbeat/prepare-stats/push) riêng, lifecycle giữ ở host); mỗi file ≤ ~600. GIỮ NGUYÊN các fix B2 (WirePrepareStatsRead cộng dồn, Log.Dispose).
3. `BigSellerCookieEngine` → 3 partial cùng class: `BigSellerCookieEngine.CookieFile.cs` (đọc/ghi/parse file + WriteAtomic), `BigSellerCookieEngine.Importer.cs` (import 2 transport + write-back), `BigSellerCookieEngine.SessionPolicy.cs` (luật giữ token/so iat) — thuần di chuyển member, KHÔNG đổi API/hành vi.

## 2. Phạm vi

- Khu: `suite/Shopee.Suite/**` + `suite/Shopee.Core/BigSeller/**`. KHÔNG đụng `suite/Shopee.Module.*` (agent khác đang tách MultiBrave), `suite/Shopee.Core/Scrape/**`, `suite/Shopee.Core.Tests/**` (khu agent MB), `orders/**`, `server/**`, `extensions/**`, `shared/**`.
- KHÔNG commit.

## 3. Nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` 0/0; test orders 1440 + Core.Tests 43 giữ nguyên (chú ý: agent MB có thể thêm test Core song song — chạy con số của worktree bạn, không tụt so lúc bắt đầu).
- [ ] 3 file gốc đạt mốc dòng nêu trên; không file mới > ~700.
- [ ] Bảng "khối → file" trong báo cáo; XAML binding của ScrapeViewModel không đổi property công khai.

## 5. Rủi ro & lưu ý

- Bạn ở worktree riêng — bước 0: `git log --oneline -1` phải là commit chứa plan này hoặc mới hơn, không thì `git merge --ff-only main`.
- OrdersModuleHost là chỗ 2 đợt bug vừa sửa — di chuyển nguyên khối, giữ thứ tự wiring.
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

Refactor thuần bằng `partial` — mọi thành viên giữ NGUYÊN văn bản, chỉ đổi chỗ ở; 3 class gốc thêm từ khoá
`partial`. Đã kiểm chứng "pure move" bằng cách so tập dòng (bỏ khoảng trắng cuối + dòng trống, sort) giữa bản
HEAD và hợp các file mới: chỉ có dòng THÊM (header partial / `namespace` / `using` / `{` `}` / đoạn `<para>` chỉ
đường trong doc lớp), KHÔNG có dòng nào biến mất.

### 1. `suite/Shopee.Suite/Modules/Scrape/ScrapeViewModel.cs` 875 → 560

| Khối | File đích |
|---|---|
| `SessionAccountPool` (kho đóng khung, bù tk thay thế) | `ScrapeViewModel.AccountPool.cs` (151) |
| `RunSession` (+ `ClaimFrame`) + `JobHandle` | `ScrapeViewModel.Session.cs` (83) |
| `WireRunner` + `DeleteAccountProfilesBestEffort` | `ScrapeViewModel.RunnerEvents.cs` (116) |
| còn lại: state/ctor/`Reload`/`StartAsync`/`RunOneJobAsync`/`StartJob`/`Stop`/`ValidateTarget`/API công khai | `ScrapeViewModel.cs` (560) |

Chỉ dời `SessionAccountPool` + `RunSession` thì VM còn ~659 dòng (vượt mốc ≤600), nên dời thêm cụm đấu event
runner — vẫn là một khối liền mạch, KHÔNG đụng property/command công khai nên XAML binding không đổi
(`ScrapeTargets`, `Instances`, `ErroredAccounts`, `VideoDir`, `SelectedTarget`, `HasSelectedTarget`, `IsBusy`,
`IsIdle`, `ReloadCommand`, `StopCommand`, `ShowStatsCommand`, `RunSingleAsync`, `StopSingleAsync`,
`BringInstanceToFront`, `TakeJobFatal`, `CanDispatchScrape` đều ở nguyên file gốc).

### 2. `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs` 1082 → 122

| Khối | File đích |
|---|---|
| `WireHubPush` (+`ReportAppAlertToHub`), `WireIncrementSoldBySku`, `WireHubSlipPush`, `ResolveShopUsername`, `ToPushItem` | `OrdersModuleHost.HubPush.cs` (266) |
| `WireOrderStatisticsRead`, `MapSharedStats`, `WirePrepareStatsRead`, `WireOrdersDirectory` | `OrdersModuleHost.HubRead.cs` (167) |
| hằng/state lease + `LeaseKey` + `WireAccountLease` + `HeartbeatLeases` + `TenMayDangGiuAsync` | `OrdersModuleHost.AccountLease.cs` (202) |
| `GsheetPullEvery`/`_gsheetTimer`/`_gsheetPulling` + `WireGsheetConfig` + `ApplyGsheetFromHubAsync` | `OrdersModuleHost.GsheetConfig.cs` (135) |
| state gương + `WireOrdersMirror`/`MirrorTickAsync`/`PushOrdersMirrorAsync`/`BuildOrdersMirror`/`MirrorSessionState` + dedup/`RunOrdersCommands`/`ExecuteOrdersCommand`/`AckOrdersCommandAsync` | `OrdersModuleHost.Mirror.cs` (260) |
| vòng đời: `Services`/`_stopped`/`_outboxWorker`/`_mainVm`, `TryCreate`, `WireBrowserLifetime`, `StopAsync` | `OrdersModuleHost.cs` (122) |

Fix B2 giữ nguyên từng ký tự: `WirePrepareStatsRead` vẫn khoá map `OrdinalIgnoreCase` + CỘNG DỒN
(`map[khoa] = dangCo + s.Count`), và `StopAsync` vẫn `svc.Log.Dispose()` SAU `Sessions.StopAllAsync()`. Thứ tự
10 lời gọi `Wire*` trong `TryCreate` không đổi.

### 3. `suite/Shopee.Core/BigSeller/BigSellerCookieEngine.cs` 788 → 147

| Khối | File đích |
|---|---|
| `FileJsonOpts`, `GetFileAuthTokenInfo`, `TryWriteCookieFile` ×2, `TryWriteCookieFileBytes`, `WriteAtomic`, `WriteAtomicBytes` | `.CookieFile.cs` (100) |
| import transport CdpSession + `BuildCookiePayload`/`TryBuildProPayload`/`SanitizeCookiePayloadForCdp`; `WriteBackLiveTokenAsync`; transport CdpClient (`IsBigSellerUrl`/`IsLoginUrl`/`ImportFromFileAsync` 6-tham-số/`ProbeLoggedInAsync`/`TryExportProfileCookiesToFileAsync`/`SetBigSellerCookiesViaCdpClientAsync`/`TrySetCookieWithBrowserStorageAsync`/`NavigateBigSellerTabsAsync`) | `.Importer.cs` (484) |
| `GetJwtIssuedAt`, `ShouldImportFromFile`, `ImportKeepingLiveTokenAsync` | `.SessionPolicy.cs` (74) |
| hằng + predicate (`IsBigSellerCookie`/`HasAuthCookie`/`AuthTokenInfo`/`ToAuthTokenInfo`) + đọc cookie từ browser | `BigSellerCookieEngine.cs` (147) |

Vá `UnauthorizedAccessException` trong `WriteAtomicBytes` giữ nguyên (`ex is IOException or
UnauthorizedAccessException`). Cả HAI transport CDP giữ nguyên, không hợp nhất.

### Build / test (trong worktree)

- `dotnet build ShopeeSuite.sln` — **0 Warning, 0 Error** (baseline trước khi sửa cũng 0/0).
- `dotnet test orders/XuLyDonShopee.Tests` — **1440 passed**, 0 failed.
- `dotnet test suite/Shopee.Core.Tests` — **43 passed**, 0 failed.
- Không file mới nào > 484 dòng; 3 file gốc còn 560 / 122 / 147.
- KHÔNG commit.

### Điểm cần soi lại

1. **`WriteBackLiveTokenAsync` nằm ở `.Importer.cs`** theo đúng chữ trong plan ("import 2 transport +
   write-back") — nó tách khỏi `ShouldImportFromFile`/`ImportKeepingLiveTokenAsync` (nay ở `.SessionPolicy.cs`)
   dù cả ba cùng thuộc banner "CHÍNH SÁCH GIỮ PHIÊN" của bản cũ. Nếu muốn cả cụm về `.SessionPolicy.cs` thì chỉ
   là chuyển 1 method.
2. **`ScrapeViewModel` dời thêm cụm `WireRunner`** ngoài 2 khối plan nêu — lý do ở mục 1 (không thì không đạt
   mốc ≤600 dòng).
3. `BigSellerCookieEngine.cs` bỏ `using System.Net.WebSockets;` (không còn `ClientWebSocket` trong file đó); các
   partial dùng `ClientWebSocket` ăn theo `global using` trong `suite/Shopee.Core/GlobalUsings.cs`.
4. Chỉ chạy build + 2 bộ test tự động; KHÔNG chạy app thật (refactor thuần, không có nhánh logic nào đổi).
