# Plan: Đợt B — Dọn dead-code toàn repo (~2.500 dòng)

- **Ngày:** 2026-08-06
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Đợt rà soát 05/08 (mọi phát hiện đều được agent phản biện độc lập grep/đọc lại xác nhận) tìm ra lượng lớn code chết còn sót sau các đợt refactor 25–31/07. User đã chốt 4 quyết định: (1) XÓA hẳn flow-keyword/flow-shop, (2) DỌN SẠCH nhánh PAC proxy-riêng-BigSeller, (3) XÓA ProxyRepository (đổi quyết định giữ của plan 30/07), (4) chế độ "máy Hub" GIỮ nhánh `Enabled` — chỉ dọn property chết.

Số dòng dưới đây theo cây tại 05/08 (sau đợt A có thể xê dịch nhẹ) — **dò theo tên symbol, không tin số dòng**. Trước khi xóa BẤT KỲ symbol nào: grep lại toàn repo (cả .razor/.js/.xaml/.cmd/plans) xác nhận 0 caller — nếu thấy caller mới xuất hiện (đợt A có thể đã thêm) thì GIỮ và ghi vào báo cáo.

## 2. Phạm vi

- **Làm:** 13 mục ở phần 3 — chỉ XÓA code chết + thêm log cảnh báo legacy endpoint. Không đổi hành vi nào đang chạy.
- **Không làm:** hợp nhất trùng lặp (đợt C), tách file (đợt D), sửa UI, thêm tính năng. KHÔNG xóa endpoint server nào (chỉ thêm log — xem B7). KHÔNG đụng bảng DB nào (bảng `proxies` giữ nguyên cho DB cũ).

## 3. Các bước thực hiện

### B1. Xóa project `shared/Shopee.Proxy.Kiot`
- 0 caller production: chỉ `orders/XuLyDonShopee.Tests/KiotApiClientTests.cs` (12 test) tự gọi; suite dùng bản riêng `suite/Shopee.Core/Proxy/KiotProxyClient.cs`; orders production chỉ dùng `KiotProxyKeyParser` (nằm trong orders, KHÔNG thuộc project này — kiểm chứng lại trước khi xóa).
- Xóa: thư mục `shared/Shopee.Proxy.Kiot/` (git rm), `<ProjectReference>` trong `orders/XuLyDonShopee.Core/XuLyDonShopee.Core.csproj` (~dòng 16), entry trong `ShopeeSuite.sln` (~dòng 30), file test `KiotApiClientTests.cs`.
- Lưu ý: `SettingsRepository.KiotProxyApiKeys` + `ProxyKeyPoolMigration` phía orders vẫn GIỮ (dữ liệu settings cũ, không thuộc phạm vi).

### B2. Xóa `ProxyRepository` + `ProxyEntry` + `ProxyRepositoryTests` (orders)
- 3 file, ~320 dòng, chỉ test tự chứng minh; `AppServices.cs` (~:322) đã tự khai gỡ wiring. Giữ bảng `proxies` trong `Database.cs`.

### B3. Xóa 2 flow Search chết trong extension (quyết định user: XÓA)
- `extensions/shopee-search/flow-keyword.js` (393 dòng) + `flow-shop.js` (225 dòng): chỉ kích hoạt qua `msg.mode` = 'keyword'/'shopFromLink' mà phía C# duy nhất tạo SearchConfig (`FileRunCoordinator.cs:224`) luôn đặt `Mode="categoryFromLink"`.
- Gỡ import + nhánh dispatch theo mode trong `extensions/shopee-search/background.js`. Grep toàn extension xem còn tham chiếu hàm nào của 2 file này không.
- Chạy `sync-shared.cmd --check` sau khi sửa (2 file này KHÔNG thuộc shared/ nhưng check cho chắc).

### B4. Dọn sạch nhánh PAC proxy-riêng-BigSeller (quyết định user: BỎ HẲN)
Toàn bộ chuỗi đang unreachable vì `BigSellerTokenGuard.ResolveProxyServerAsync` (BigSellerTokenGuard.cs:49) hardcode trả `null` (tắt chủ đích, comment :44–48):
- `BraveProfileManager`: nhánh `bigSellerProxyServer` trong `BuildBraveArguments` (~:113–130), `WriteBigSellerSplitPac` (~:267–290), `ToPacProxy` (~:294–305); tham số `bigSellerProxyServer` của `BuildBraveArguments` + caller `BraveInstanceSession.Profile.cs:74` (`ResolveProxyServerAsync` call :73).
- `BigSellerTokenGuard`: `ResolveProxyServerAsync`, `SetProxy`, field `_proxyKey/_proxyRegion/_proxyType` (:33–35) + comment 44–48.
- `BraveInstanceSession.SetBigSellerProxy` + caller `ScrapeRunner.cs:460`.
- Sau khi gỡ, build phải xanh và Brave vẫn phóng đúng args như cũ (nhánh chết không từng chạy nên hành vi không đổi).

### B5. `BackupService` (suite/Shopee.Core/Infrastructure) — bỏ phần backup .zip chết
- Xóa `Export` (~29–49), `Import` (~51–91), record `BackupOptions` (~:10), `AddFile` (~310–313), `Deserialize` (~315–319), `using System.IO.Compression`. GIỮ `MergeBigSeller`/`MergeShopee`/`ImportResult` (HubConfigSync dùng :118/:130/:72,167). Sửa xmldoc class cho khớp vai trò thật (bộ merge đồng bộ Hub).

### B6. `HubServerConfig` — dọn xác hub nhúng, GIỮ chế độ máy-Hub
- Xóa 5 property chết: `Domain`, `PublicUrl`, `CloudflareApiToken`, `TunnelToken`, `DataDir`; xóa `HubServerConfigStore.Save` (~61–71) + event `Changed` (:46); xóa `HubDefaults.Domain` (HubDefaults.cs:13) nếu sau đó 0 caller.
- GIỮ `Enabled` + `ApiToken` + `Port` (đang được đọc: HttpCoordinationHub.cs:136,151; HubConfigSync.cs:78; CoordinationRuntime.cs:124–126). File `hub-server.json` cũ có member lạ → System.Text.Json bỏ qua, vô hại.

### B7. `HubClient` 4 method chết + log cảnh báo legacy endpoint (KHÔNG xóa endpoint)
- Xóa 4 method 0-caller trong `suite/Shopee.Core/Coordination/HubClient.cs`: `SearchProductsAsync` (:135), `SearchProductCountAsync` (:137), `ClearSearchProductsAsync` (:139), `PostProductAppendAsync` (:176). GIỮ `PushSearchProductsAsync` (SearchViewModel.cs:427 đang dùng).
- Phía server **CHỈ THÊM LOG, KHÔNG XÓA** (soak 2–3 tuần theo tiền lệ /accounts/append trước 30/07): thêm `LogWarning("legacy endpoint hit: {path} tu {ip}")` vào 6 endpoint hết consumer: `GET /api/orders`, `GET /api/shops` (ClientApiEndpoints.cs:219,373), `GET /search-products`, `GET /search-products/count`, `POST /search-products/clear` (:110–112), `POST /products/rows/append` (ProductApiEndpoints.cs:72). DTO `HubOrderDtos.cs` + helper `ToHubOrderItem/ToHubShopItem` GIỮ (endpoint còn dùng).

### B8. Cụm MultiBrave
- `Engine/ShopConfig.cs`: xóa CẢ FILE (AccountConfig + ShopConfig namespace OpenMultiBraveLauncherV3, sót từ launcher v31).
- `ScrapeRunner.RunAsync` chế độ manual (~94–125) + event `AccountErrored` (:58, chỉ Invoke trong hàm chết :120) + wiring subscriber `ScrapeViewModel.RunnerEvents.cs:37` + `ScrapeAccountSpec.StartRow/EndRow` (chỉ hàm chết đọc — xóa field + mọi chỗ gán).
- `Shopee.Module.CheckAccount/ShopeeAccountChecker.LoginThenManualSolveAsync` (~156–207, 52 dòng). Module còn sống — CHỈ xóa method này.
- `InstanceConfig`: `PendingScrapeLinks` + class `PendingScrapeLink` (~:86, 241–248), `ExportShopee` (:30), `UsePersistentSharedProfile` (:28), `ShopId` (:9).
- Tàn dư API Python trong `AppSession` (MultiBrave): `ApiPort` (:23,:34), `ProjectSourceDirectory` (:17), `ResolveDataPath` (:38–44), chỗ giữ port 8012 trong `PortsLookFree` (:117); biến `payload` chết trong `LauncherRunnerLoop.DownloadBestVideoAsync` (~634–643); `ScrapeNativeSettings.WorkbookPath` (:10); comment '127.0.0.1:8012' ở BraveProfileManager (~:117).
- Cờ ma `preferSuggestedResume`: tham số của `LauncherRunnerLoop.RunAsync` (:36, thân không đọc) + biểu thức truyền ở `BraveInstanceSession.RunnerLoop.cs:128` + comment sai :187 (mô tả cơ chế không tồn tại — resume thật qua `config.GetEffectiveRunRow()`).
- 4 API 0-caller: event `BraveInstanceSession.ExtensionInterrupted` (:47 + Invoke ở Progress.cs:105 — đang bắn vào hư không), `OpenShopeeAccountLoginAsync` (:109), `ExtensionProgressCoordinator.PushFormConfigAsync` (:39–54), `ExtensionProgressReader.TryGetRunnerExtensionId` (:39–44).

### B9. Cụm UpdateProduct + Search (C#)
- Dây chuyền `ShopeeApiConfig` chết: `ShopeeApiConfig.cs` (cả file), `AppSettingsService.SaveApiConfig`/`GetApiConfigJson` (:52,:59), `SearchConfig.FilterPriceClientSide` (:29), khối payload `apis` + `filters` gửi sang extension trong `SearchOrchestrator` (~:68) — extension KHÔNG đọc (`grep 'apis|filters|minPrice|minSold|checkStock' extensions/` chỉ trúng flow-keyword.js:99 đã xóa ở B3). Xóa cả `suite/Shopee.Module.Search/appsettings.json` nếu đang track và chỉ phục vụ ApiConfig (kiểm chứng trước).
- Dây chuyền profile-Import-riêng chết: `BigSellerWorkflowSettings.ImportProfileDir/ImportDebugPort` (gán 3 chỗ, 0 reader — comment UpdateProductRunner ~:214–215 tự nhận), `ShopConfig.BigSellerImportProfileRelativePath`/`UseSharedProfiles`. **CẨN THẬN**: `BigSellerImportDebugPort` có 2 chỗ ĐỌC trong phép gom port-đã-dùng — giá trị luôn = port chính nên gỡ khỏi phép gom không đổi kết quả, nhưng phải sửa 2 chỗ đó cho build xanh, đọc kỹ trước.
- Cụm chết trong `BigSellerCrawlHelper`: `DeleteBrokenRowAsync` (24 dòng), wrapper `SelectClaimedTabAsync`, pattern regex thứ 3 `i\.\d+\.(\d+)\?` trong `ExtractShopeeId` (unreachable — pattern 1 là tiền tố không neo luôn khớp trước), hằng `VideoBoxes`, hằng `DescriptionSystemPrompt` (16 dòng prompt mồ côi — prompt thật từ Hub `cfg.EffectiveDescriptionPrompt`), `ClaimStore.IsClaimed`, `RewritePlan.SkuByOriginalName` (chỉ gán không đọc).
- `NameRewriteEngine.RewriteAsync` (suite/Shopee.Core/Ai, ~75–100) + xmldoc nhắc nó (:13). Caller thật của engine chỉ đi `RewriteTitlesAsync` + `ComposeFinalName` (ProductNameRewriteRunner + RewriteJobService hub — file này từng chứa NUL, đợt A đã sửa, grep giờ thấy được).

### B10. Core lặt vặt
- `CdpClient.NavigatePageTargetsAsync` (:177) — 0 caller sau hợp nhất CDP 3C.
- `ShopeeAccountUsage.TryReserveMany` (:109–116) + xmldoc.

### B11. Suite shell — màn Chào + ComingSoon
- Xóa: `WelcomeViewModel.cs`, `ModuleItem.cs`, `Views/WelcomeView.xaml(+.cs)`, DataTemplate App.xaml (~:41–43); `ComingSoonViewModel.cs`, `Views/ComingSoonView.xaml(+.cs)` + template (~:44–46) + comment mô tả sai. Đã xác nhận: không reflection/DI theo tên, mọi module-VM có DataTemplate riêng (App.xaml:47–79).

### B12. Orders — converter + form props + rác Avalonia
- Xóa 5 file converter + 5 dòng đăng ký `ModuleResources.xaml` (~:22–26): `VietnameseEnumConverter`, `StatusColorConverter`, `DateTimeDisplayConverter`, `InitialConverter`, `StatusPillConverter`. GIỮ `OrderStatusPillConverter` (OrdersView.xaml:240–245 dùng) + `BrushPalette` + bộ `VisibilityConverters`.
- `AccountsViewModel.Form.cs`: xóa `CookieSizeText` (0 reader, kể cả OnPropertyChanged :119 bắn vào hư không); `EditPhone`/`EditNote`/`EditProxyKey` thay bằng đọc thẳng từ `existing` khi Save (GIỮ NGUYÊN dữ liệu Phone/Note/ProxyKey trong DB — không được làm mất data khi Save).
- `git rm orders/XuLyDonShopee.App/Assets/avalonia-logo.ico` (đang bị nhúng vào DLL qua `<Resource Include="Assets\**"/>`, 0 tham chiếu). Nếu Assets/ rỗng sau đó, kiểm tra build vẫn xanh (glob rỗng là hợp lệ).

### B13. Tools
- `git rm tools/split_shops.py tools/split_shops_5k.py tools/split_shops_5k_ttn.py` (387 dòng, one-off 21/06, hardcode path Downloads; plans/ giữ lịch sử quyết định).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` + `dotnet build server/ShopeeHub.sln` 0 error 0 warning.
- [ ] `dotnet test` xanh 3 project; số test orders GIẢM đúng số test bị xóa chủ đích (12 KiotApiClientTests + toàn bộ ProxyRepositoryTests) — ghi số trước/sau vào báo cáo; KHÔNG test nào khác biến mất.
- [ ] Grep từng symbol đã xóa (danh sách phần 3) = 0 hit trong code (plans/ + CHANGELOG được phép còn nhắc).
- [ ] 6 endpoint legacy có LogWarning; `rg 'legacy endpoint hit' server/` = 6 vị trí.
- [ ] `sync-shared.cmd --check` pass.
- [ ] Đếm tổng dòng xóa (git diff --stat) ghi vào báo cáo.
- [ ] `git status` chỉ chứa thay đổi thuộc phạm vi.

## 5. Rủi ro & lưu ý

- **Xóa nhầm thứ còn sống là rủi ro số 1**: luật sắt — grep lại TỪNG symbol ngay trước khi xóa (đợt A vừa đổi code, bằng chứng 05/08 có thể lệch). Symbol nào grep ra caller thật → GIỮ, ghi vào báo cáo, không tự suy diễn.
- File từng chứa NUL (`RewriteJobService.cs`) đã sửa ở đợt A — nếu đợt A CHƯA commit thì grep bằng python cho chắc.
- B12 phần Save từ `existing`: viết cẩn thận — hồi quy dữ liệu account là loại lỗi user rất khó chịu; nếu orders có test Form/Save thì chạy kỹ, cân nhắc thêm 1 test giữ-nguyên-Phone/Note/ProxyKey khi Save.
- KHÔNG commit — phiên chính đối chiếu và commit.

---

## Báo cáo thực thi (Opus, 2026-08-06)

**Tổng: 82 file thay đổi, +124 / −3009 dòng** (`git diff --cached --shortstat`). Build 2 solution 0 error
0 warning; 3 project test xanh. KHÔNG commit (theo yêu cầu). KHÔNG đụng `extensions/shopee-orders/`.

### Kết quả kiểm chứng (số thật)

| Lệnh | Trước | Sau |
|---|---|---|
| `dotnet build ShopeeSuite.sln` (--no-incremental) | 0 warn / 0 err | **0 warn / 0 err** |
| `dotnet build server/ShopeeHub.sln` (--no-incremental) | 0 warn / 0 err | **0 warn / 0 err** |
| `dotnet test orders/XuLyDonShopee.Tests` | 1506 pass | **1489 pass** (−17) |
| `dotnet test suite/Shopee.Core.Tests` | 71 pass | **71 pass** |
| `dotnet test server/Shopee.Hub.Web.Tests` | 53 pass | **53 pass** |
| `extensions\sync-shared.cmd --check` | — | **OK, exit 0** |

Δ test orders = −17 khớp chính xác: −12 `KiotApiClientTests` (B1) −6 `ProxyRepositoryTests` (B2)
**+1 test mới** `Save_KhongCoONhap_GiuNguyenPhoneNoteProxyKey` (B12). Không test nào khác biến mất.

`rg 'legacy endpoint hit' server/` = **6 vị trí** (ClientApiEndpoints.cs:117/122/127/240/398 +
ProductApiEndpoints.cs:77) — đúng 6 endpoint plan yêu cầu, KHÔNG endpoint nào bị xoá.

**Test mới đã thử phá**: chèn `existing.Phone = null; existing.Note = null; existing.ProxyKey = null;` vào
nhánh update của `Save()` → test **FAIL** đúng như mong đợi; gỡ sabotage → PASS lại. Test thật sự canh luật.

### Đã hoàn thành — 13/13 hạng mục

- **B1** Xoá `shared/Shopee.Proxy.Kiot/` (5 file) + `<ProjectReference>` trong `orders/XuLyDonShopee.Core/XuLyDonShopee.Core.csproj` + 7 dòng trong `ShopeeSuite.sln` + `orders/XuLyDonShopee.Tests/KiotApiClientTests.cs`. Đã kiểm chứng `KiotProxyKeyParser` nằm ở `orders/XuLyDonShopee.Core/Services/` (KHÔNG thuộc project bị xoá) và `ProxyFleetWideFailureTests` dùng `Shopee.Core.Proxy` của suite → cả hai GIỮ nguyên.
- **B2** Xoá `orders/XuLyDonShopee.Core/Data/ProxyRepository.cs`, `Models/ProxyEntry.cs`, `Tests/ProxyRepositoryTests.cs`. Bảng `proxies` trong `Database.cs:83` GIỮ nguyên (đã xác nhận).
- **B3** Xoá `extensions/shopee-search/flow-keyword.js` + `flow-shop.js`; `background.js` bỏ 2 import + rút dispatch còn `case 'start': startCategoryFromLink(msg)`. Xác nhận `FileRunCoordinator.cs:226` là nơi DUY NHẤT tạo `SearchConfig` và luôn đặt `Mode="categoryFromLink"`.
- **B4** Gỡ toàn bộ nhánh PAC: `BraveProfileManager` (tham số + nhánh `bigSellerProxyServer`, `WriteBigSellerSplitPac`, `ToPacProxy`), `BigSellerTokenGuard` (`ResolveProxyServerAsync`, `SetProxy`, 3 field `_proxy*`), `BraveInstanceSession.SetBigSellerProxy`, caller `ScrapeRunner.cs`, caller `BraveInstanceSession.Profile.cs`. **Nhánh `else if (proxyServer)` giữ NGUYÊN** → args Brave sinh ra y hệt trước.
- **B5** `BackupService`: xoá `Export`/`Import`/`BackupOptions`/`AddFile`/`Deserialize` + `using System.IO.Compression` + `using Shopee.Core.Ai` + `JsonOpts`. Giữ `MergeBigSeller`/`MergeShopee`/`ImportResult`. Sửa xmldoc class cho khớp vai trò thật (bộ gộp đồng bộ Hub).
- **B6** `HubServerConfig`: xoá `Domain`/`PublicUrl`/`CloudflareApiToken`/`TunnelToken`/`DataDir`, `HubServerConfigStore.Save` + event `Changed`, `HubDefaults.Domain`. Giữ `Enabled`/`ApiToken`/`Port` (7 điểm đọc còn nguyên).
- **B7** `HubClient`: xoá 4 method 0-caller; giữ `PushSearchProductsAsync` (SearchViewModel.cs:429). Server thêm 6 `LogWarning("legacy endpoint hit: …")`, KHÔNG xoá endpoint/DTO nào.
- **B8** Xoá `MultiBrave/Engine/ShopConfig.cs` (cả file); `ScrapeRunner.RunAsync` manual + event `AccountErrored` + subscriber ở `ScrapeViewModel.RunnerEvents.cs` + `ScrapeAccountSpec.StartRow/EndRow` (sửa `ShopeeAccountSpecFactory`); `ShopeeAccountChecker.LoginThenManualSolveAsync` (60 dòng); `InstanceConfig` (`ShopId`, `UsePersistentSharedProfile`, `ExportShopee`, `PendingScrapeLinks` + class `PendingScrapeLink`); `AppSession` MultiBrave (`ApiPort`, `ProjectSourceDirectory`, `ResolveDataPath`, bỏ 8012 khỏi `PortsLookFree`); biến `payload` chết trong `DownloadBestVideoAsync`; `ScrapeNativeSettings.WorkbookPath`; cờ ma `preferSuggestedResume` (xoá HẾT 7 vị trí) + sửa comment sai :187; 4 API 0-caller (`ExtensionInterrupted` + Invoke, `OpenShopeeAccountLoginAsync`, `PushFormConfigAsync`, `ExtensionProgressReader.TryGetRunnerExtensionId`).
- **B9** Xoá `ShopeeApiConfig.cs` + `Shopee.Module.Search/appsettings.json` (+ dòng `<None Include>` trong csproj) + `AppSettingsService.SaveApiConfig/GetApiConfigJson/ApiConfig/_apiConfigPath` + khối `apis`/`filters` trong `SearchOrchestrator.SendStartCommandAsync` + `SearchConfig.FilterPriceClientSide`. Dây chuyền profile-Import-riêng: `BigSellerWorkflowSettings.ImportProfileDir/ImportDebugPort`, `ShopConfig.BigSellerImportProfileRelativePath/BigSellerImportDebugPort/UseSharedProfiles` + 4 điểm gán/đọc (2 phép gom port trong `BigSellerProfileManager` đổi `SelectMany` → `Select`, kết quả không đổi vì 2 port luôn bằng nhau). `BigSellerCrawlHelper`: `DeleteBrokenRowAsync`, wrapper `SelectClaimedTabAsync`, pattern regex thứ 3, `VideoBoxes`, `DescriptionSystemPrompt` (17 dòng), `ClaimStore.IsClaimed`, `RewritePlan.SkuByOriginalName` (+ 2 local `skuByName`). `NameRewriteEngine.RewriteAsync` + xmldoc.
- **B10** `CdpClient.NavigatePageTargetsAsync`, `ShopeeAccountUsage.TryReserveMany` + xmldoc.
- **B11** Xoá 7 file (`WelcomeViewModel`, `ModuleItem`, `ComingSoonViewModel`, `WelcomeView.xaml(+.cs)`, `ComingSoonView.xaml(+.cs)`) + 2 DataTemplate + 2 xmlns `vm`/`views` trong `App.xaml` (thư mục `Views/` của Shopee.Suite nay rỗng và đã biến mất).
- **B12** Xoá 5 converter + 5 dòng đăng ký `ModuleResources.xaml`; giữ `OrderStatusPillConverter`/`BrushPalette`/`VisibilityConverters`. `AccountsViewModel.Form.cs`: xoá `CookieSizeText` (+ `OnPropertyChanged` bắn hư không), xoá `EditPhone`/`EditNote`/`EditProxyKey` — nhánh update KHÔNG gán lại 3 cột (giữ nguyên giá trị `existing` đọc từ DB), nhánh insert để null như mặc định model. `git rm Assets/avalonia-logo.ico` (glob `<Resource Include="Assets\**"/>` để nguyên, rỗng vẫn build xanh).
- **B13** `git rm tools/split_shops.py`, `split_shops_5k.py`, `split_shops_5k_ttn.py` (387 dòng).

### Ngoài danh sách plan nhưng BẮT BUỘC phải làm kèm (khai báo rõ)

1. **`ScrapeRunner`: 3 field `_bigSellerKiotKey/_bigSellerRegion/_bigSellerProxyType` + 3 tham số ctor + 1 dòng ở `ScrapeViewModel.cs:370`.** Sau khi gỡ `SetBigSellerProxy` (B4) chúng thành 0-reader; plan không liệt kê nhưng giữ lại là để lại rác đúng loại B4 đang dọn. `BigSellerProxyResolver` **vẫn sống** (BigSellerViewModel dùng cho cửa sổ login BigSeller) — KHÔNG đụng.
2. **`ResumeContinueAsync(… preferSuggestedResume …)` + 4 call site.** Plan chỉ nêu tham số của `LauncherRunnerLoop.RunAsync`, nhưng tiêu chí nghiệm thu đòi grep symbol = 0 hit; nếu chỉ gỡ một tầng thì cờ ma vẫn còn 5 hit ở tầng trên. Đã gỡ hết.
3. **`SearchOrchestrator._appSettings` + tham số ctor + call site duy nhất `SearchSession.cs:132`.** Field này tồn tại CHỈ để cấp `ApiConfig` cho payload vừa xoá ở B9; giữ lại là field gán-không-đọc.
4. **3 sửa tài liệu/comment thành sai sau khi xoá:** `orders/CLAUDE.md:11` (còn ghi Core ref `shared/Shopee.Proxy.Kiot`), `orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj:29` (nhắc test KiotApiClient đã xoá), `OrderStatusPillConverter.cs:14` (`<see cref="StatusPillConverter"/>` trỏ vào class vừa xoá — cref treo).
5. **Bỏ 2 `using` mồ côi** ở `ScrapeViewModel.RunnerEvents.cs` (`Shopee.Core.Accounts`, `Shopee.Suite.Infrastructure`) sau khi xoá subscriber `AccountErrored`.

### Kiểm chứng "grep từng symbol trước khi xoá" — các trường hợp đáng lưu ý

- `AccountErrorReporter.Report` và `ShopeeAccountUsage.MarkCaptcha`: subscriber `AccountErrored` bị xoá là **không phải** caller duy nhất (SearchViewModel.cs:481 + FileRunCoordinator.cs:404 vẫn gọi) → 2 API này GIỮ.
- `ShopeeSessionBootstrapper.OpenAccountLoginAsync` (:125) vẫn được gọi nội bộ ở :94 → GIỮ; chỉ xoá wrapper `BraveInstanceSession.OpenShopeeAccountLoginAsync`.
- `ExtensionRunnerAutomation.TryApplyFormConfigAsync` KHÔNG mồ côi sau khi xoá `PushFormConfigAsync` — `LauncherRunnerLoop.cs:97` vẫn gọi → GIỮ.
- `RunnerExtensionTargets.TryGetRunnerExtensionIdFromProfile` (tên gần giống) vẫn sống → GIỮ; chỉ xoá `ExtensionProgressReader.TryGetRunnerExtensionId`.
- `SearchRunner.AccountErrored` (module Search) là event KHÁC, còn subscriber → GIỮ.
- `UpdateProduct.ShopConfig` / `BigSellerAccountConfig` (namespace `UpdateProduct`) là bộ KHÁC với `OpenMultiBraveLauncherV3.ShopConfig` vừa xoá → GIỮ.
- `RewriteJobService.cs` đã hết NUL byte (đợt A sửa) — kiểm bằng python, grep đọc được, xác nhận chỉ dùng `RewriteTitlesAsync`.
- `extensions/shopee-orders/flow-shop.js` là file KHÁC (của agent đợt E) — 2 hit grep còn lại thuộc file đó, **không đụng**.

### Vướng mắc / cố ý bỏ dở

- **Không có hạng mục nào phải dừng.** 13/13 làm được, không phát hiện symbol nào trong plan hoá ra còn caller.
- `git status` chỉ chứa file thuộc phạm vi (82 file, `grep -c shopee-orders` = 0). Đã `git add` để phiên chính xem `git diff --cached`; **chưa commit**.

### Đề xuất cho phiên chính / đợt sau

1. **`suite/Shopee.Module.UpdateProduct/Engine/AppSession.cs` có bản sao y hệt các thành viên chết vừa dọn ở MultiBrave**: `ProjectSourceDirectory` (:15), `ApiPort` (:20, :32 — cổng 8112), `ResolveDataPath` (:36) đều 0 reader. Plan B8 ghi rõ "(MultiBrave)" nên tôi KHÔNG đụng. Nên gom vào đợt sau.
2. `BigSellerProfileManager.EnsureWorkflowProfile(…, ShopConfig shop)` có tham số `shop` không dùng (đã vậy từ trước đợt này) — ứng viên dọn tiếp.
3. Sau khi xoá `flow-keyword.js`/`flow-shop.js`, một số helper trong `extensions/shopee-search/` có thể mồ côi (`isProductNotFoundPage` ở `detect.js`, `typeAndSearch`/`getCurrentTabUrl` ở `page-funcs.js`/`tabs.js`). Chưa kiểm kỹ vì ngoài phạm vi plan — nên rà ở đợt C/D.
4. `SearchConfig.Mode` vẫn mặc định `"keyword"` dù chỉ còn 1 flow sống. Không đổi (ngoài phạm vi), nhưng để mặc định là giá trị không còn flow xử lý là một cái bẫy — cân nhắc đổi mặc định thành `"categoryFromLink"`.
5. `<Resource Include="Assets\**" />` trong `XuLyDonShopee.App.csproj` giờ trỏ vào thư mục không tồn tại (build vẫn xanh, đúng như plan dự liệu). Xoá hẳn ItemGroup đó nếu không định thêm asset lại.

---

## Nghiệm thu (Fable tổng hợp sau phản biện, 2026-08-06)

`nghiem-thu` chấm **ĐẠT CÓ ĐIỀU KIỆN** — 13/13 đúng, KHÔNG xóa nhầm thứ còn sống (tự quét lại 62 symbol;
xác nhận B4 args Brave bất biến, B11 không gãy StaticResource/binding nào, B12 các prop bị xóa chưa từng có
binding XAML, test mới canh thật). Nó còn bắt được executor đoán SAI ở đề xuất: `getCurrentTabUrl` vẫn sống
(crawl.js:4 + detect.js:3) — may là executor không xóa. Cố ý KHÔNG chạy app suite vì app khởi động sẽ
heartbeat lên Hub production + có thể giành lease — đúng quyết định.

Điều kiện đã sửa ngay (phiên chính): LogWarning ở `/products/rows/append` nằm TRONG `WithPg` → Pg chưa sẵn
sàng là mất vết soak; đã đưa ra ngoài, build + test server lại xanh (0 warning, 53/53).

7 "xác mới" nghiệm thu liệt kê (ChunkResult.CaptchaUrl chỉ-ghi, BackupService tham số replace/rebaseDir chết,
enum ProxyType/ProxyStatus, typeAndSearch/typeAndSearchSynthetic/isProductNotFoundPage mồ côi, 3 resource
Theme.xaml mồ côi, HubServerConfig.Load nên private, tài liệu AppIcons/SearchConfig nói sai) → GOM VÀO ĐỢT C
(đã thêm mục C9 vào plan C), không chặn commit đợt này.
