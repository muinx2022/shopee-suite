# Plan: Đợt B — Dọn dead-code toàn repo (~2.500 dòng)

- **Ngày:** 2026-08-06
- **Trạng thái:** chờ làm (sau đợt A)
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

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
