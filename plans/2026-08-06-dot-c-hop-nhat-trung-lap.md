# Plan: Đợt C — Hợp nhất trùng lặp còn sót

- **Ngày:** 2026-08-06
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Đợt rà soát 05/08 tìm ra các bản chép đôi/ba mà những đợt refactor trước bỏ sót. Nguyên tắc: **pure refactor — hành vi không đổi**, mỗi mục gộp về MỘT nguồn sự thật. Repo này đã dính nhiều lần lỗi "sửa 1 quên 1" nên đợt này đáng làm dù không có bug hiện hữu. Số dòng dò theo symbol (cây đã qua đợt A+B).

## 2. Phạm vi

- **Làm:** 8 mục phần 3.
- **Không làm:** tách file dài (đợt D), sửa UI, thêm tính năng, đổi hành vi. Không gộp 2 API surface trong KiotProxyClient (giữ chủ đích — comment :93–99).

## 3. Các bước thực hiện

### C1. Hợp nhất `AppSession` + `PortAllocator` MB↔UP về Core (mục 3D plan 25/07 chưa làm)
- Hiện trạng: `AppSession` 2 bản (MultiBrave 165 dòng, UpdateProduct 155 dòng — diff chỉ khác namespace, base port 8012/8112, danh sách port probe, 1 dòng CreateDirectory, chuỗi lỗi); `PortAllocator` 2 bản (khác namespace + hằng 9330/600 vs 10000/400) **và Core đã có 1 bản `PortAllocator` thứ ba đang được Search dùng** (SearchSession.cs:91).
- Làm: dồn về `suite/Shopee.Core` MỘT `AppSession` (tham số hóa base-port + danh sách probe + cờ tạo persistent dir) và MỘT `PortAllocator` (bản Core hiện có — mở rộng nhận range làm tham số nếu chưa; **mang theo fix re-enqueue port bận của đợt A** — kiểm tra bản Core có cùng bug không, có thì sửa cùng khuôn). MB/UP/Search cùng dùng bản Core; xóa các bản module.
- Đối chiếu diff 2 bản cũ TRƯỚC khi gộp để không nuốt mất khác biệt có chủ đích (vd UpdateProduct có `Directory.CreateDirectory(ResolvePersistentDataPath())` trong Initialize).

### C2. `BigSellerAutoLogin` — gộp 3 khối login lặp
- 3 method (`ForceLoginInBraveAsync` ~29–72, `EnsureFreshSessionAsync` ~80–129, `LoginHeadlessAsync` ~136–194) lặp nguyên khối: Playwright.CreateAsync → ConnectOverCDPAsync(30000) → context/page bigseller → HubAiConfig.GetAsync → RunFormLoginAsync → Map → Success thì MarkLoggedIn + GetBigSellerCookiesAsync + HasAuthCookie + TryWriteCookieFile.
- Gộp thành 1 hàm private `LoginViaCdpAsync(port, …)`; bản headless giữ phần riêng (tự phóng Brave, delay 4s, fail→Failed) quanh lời gọi hàm chung.

### C3. `CategoryAiUpdater` (Search) → dùng `AiChat` của Core
- CategoryAiUpdater (~:142–233) tự cài BuildRequest 3 provider + ExtractContent + retry 429 + ExtractJsonObject (~150 dòng) — Core `AiChat` đã là client thống nhất 3 provider, các nơi khác đã dồn về.
- Chuyển sang AiChat; nếu AiChat thiếu option mà CategoryAiUpdater cần (`response_format: json_object` cho OpenAI, `responseMimeType` cho Gemini, cách truyền key) thì THÊM option vào AiChat (AiChat được cả hub web link — build cả 2 solution). Giữ nguyên prompt + parse JSON kết quả.

### C4. UpdateProduct — bộ tứ "nạp dòng sheet" + cặp mark-Hub
- `BigSellerImportToStoreRunner.LoadImportItemIdSetAsync` (~73–127) trùng khung `WorkbookRecordCache.LoadRecordMapAsync` (~49–118): khóa file → XLWorkbook → chọn sheet → duyệt StartRow→EndRow → id từ ItemIdColumn hoặc ExtractShopeeId(Link). Cặp hub-mode `LoadImportItemIdSetFromHubAsync`/`LoadRecordMapFromHubAsync` cùng khung. `MarkImportedHubAsync` (~746–755) vs `MarkUpdatedHubAsync` (~436–445) chỉ khác endpoint + chữ log.
- Gộp: 1 helper duyệt-sheet nhận delegate xử-lý-dòng, 1 helper mark-Hub tham số hóa op. Đặt tại chỗ hợp lý trong module (không cần lên Core).

### C5. Chuỗi JS nhúng chép đôi/ba trong UpdateProduct
- Khối JS normalize/compact/labelText/query-label (~15 dòng) y hệt ở `SelectImportShopAndConfirmAsync` (~456–468) và `IsImportShopCheckedAsync` (~523–535) → 1 hằng chuỗi chung.
- Hàm khóa ảnh 3 bản: C# `ImgKey` (~608–614) + JS trong `GetVisibleImageKeysAsync` (:636) + JS trong `CheckMatchingRowsOnPageAsync` (:661) — cùng logic split('?')[0] → đoạn cuối path → 1 hằng JS chung + C# giữ 1 bản (comment trỏ nhau).
- Danh sách ~14 selector tab "Đã nhận" + luật items[2] chép đôi `BigSellerCrawlHelper` (~162–176 vs ~225–236) → 1 hằng.

### C6. `TraHangParser.KhongDau` → forwarder về Toolkit
- Bản chép thứ 3 của bỏ-dấu (~643–661); `MsLoginSelectors.NormalizeForMatch` (Toolkit :90–117) cho cùng kết quả (khác thứ tự hạ chữ — không đổi kết quả, đã kiểm chứng 05/08). orders Core đã ref Toolkit. Đổi thân KhongDau thành forward 1 dòng (khuôn `LoginParsers.NormalizeForMatch` hiện có). Chạy `TraHangParserTests` (832 dòng test) xác nhận không vỡ.

### C7. `AccountsView.FindRow` (orders) → `VisualTreeSearch.FindAncestor<DataGridRow>`
- xaml.cs:24–31 tự viết lại vòng leo cây y hệt Infrastructure/VisualTreeSearch.cs:19–31; WorkspaceView/DataView đã dùng bản chung. Thay 8 dòng bằng 1 lời gọi.

### C8. Magic PDF về một helper
- Core `ShopFlowRunner.TrySaveSlip` (~568–571) kiểm 4 byte `%PDF` khi GHI; App `SlipFiles.BytesLookPdf` (~57–59) đòi 5 byte `%PDF-` khi ĐỌC. Đặt helper ở XuLyDonShopee.Core (vd `SlipMagic.LooksPdf`, chuẩn 5 byte `%PDF-` — chặt hơn, PDF hợp lệ luôn có); cả 2 nơi gọi chung. Ghi rõ vào báo cáo việc siết Core từ 4→5 byte (khác biệt hành vi lý thuyết: file 4-byte-đúng 5-byte-sai trước đây được lưu rồi App từ chối đọc — giờ từ chối ngay từ lúc lưu, hợp lý hơn).

### C9. Dọn "xác mới" lộ ra sau đợt B (danh sách từ nghiệm thu đợt B + đề xuất executor B)
Grep xác nhận lại từng cái trước khi xóa (luật như đợt B):
- `ScrapeRunner.ChunkResult.CaptchaUrl` — chỉ-ghi-không-đọc (reader là RunAsync manual đã xóa).
- `BackupService`: tham số `replace` luôn false ở cả 2 call site → nhánh replace bất khả đạt; `rebaseDir` luôn null; xmldoc còn nhắc "import-zip". Rút chữ ký `MergeBigSeller`/`MergeShopee` cho khớp thực tế.
- `orders/.../Enums.cs`: `ProxyType` + `ProxyStatus` 0 consumer (GIỮ `AccountStatus`).
- `extensions/shopee-search`: `typeAndSearch` + `typeAndSearchSynthetic` (page-funcs.js, ~130 dòng) + `isProductNotFoundPage` (detect.js) mồ côi. **`getCurrentTabUrl` CÒN SỐNG (crawl.js:4, detect.js:3) — KHÔNG xóa.**
- `Theme.xaml`: `BrandBadgeBrush`, `cardButton`, `emoji` 0 StaticResource dùng.
- `HubServerConfig.Load()` hạ private (chỉ ctor gọi).
- `AppSession` (UpdateProduct): `ProjectSourceDirectory`/`ApiPort` 8112/`ResolveDataPath` 0 reader — bản sao thành viên chết đã dọn ở MultiBrave (nếu C1 hợp nhất AppSession thì tự khắc biến mất — đừng làm 2 lần).
- `BigSellerProfileManager.EnsureWorkflowProfile` tham số `shop` không dùng.
- `SearchConfig.Mode` đổi mặc định `"keyword"` → `"categoryFromLink"` (mặc định đang trỏ vào flow không tồn tại) + sửa xmldoc SearchConfig.cs:17,22 và comment AppIcons.cs:5,22 còn nhắc màn Welcome.
- `XuLyDonShopee.App.csproj`: xóa `<Resource Include="Assets\**"/>` (thư mục không còn tồn tại).

## 4. Tiêu chí nghiệm thu

- [ ] Build 2 solution 0 error 0 warning; 3 bộ test xanh, số test KHÔNG giảm.
- [ ] Grep: không còn bản chép nào của các khối đã gộp (AppSession/PortAllocator chỉ còn ở Core; 1 bản selector tab Đã nhận; 1 bản khóa ảnh JS; KhongDau chỉ còn forwarder…).
- [ ] C1: MB/UP/Search build và chạy trên bản Core; khác biệt có chủ đích giữa 2 bản cũ được liệt kê trong báo cáo kèm cách xử lý từng cái.
- [ ] C3: CategoryAiUpdater không còn HttpClient/provider riêng; option mới của AiChat (nếu thêm) có xmldoc.
- [ ] C6: TraHangParserTests xanh nguyên bộ.
- [ ] Báo cáo ghi tổng dòng giảm.

## 5. Rủi ro & lưu ý

- C1 đụng engine cấp port của cả 3 module — sai là Brave không phóng được. Làm từng module một, build sau mỗi bước.
- C3: hợp đồng thông điệp lỗi/retry của AiChat khác bản tự chế (AiHttpException.IsPermanent) — đọc kỹ chỗ SearchRunner (:170–171) tiêu thụ lỗi để hành vi retry không đổi ngoài ý muốn.
- C5: chuỗi JS là hợp đồng với DOM BigSeller — gộp phải BYTE-ĐÚNG với bản đang chạy, đừng "tiện tay" sửa selector.
- KHÔNG commit.

---

## Báo cáo thực thi (Opus điền sau khi xong)

Thực thi 2026-08-06 trên cây sạch tại `5c5a6e7`. **KHÔNG commit.** Tổng: **9/9 mục xong**,
(Số cuối cùng sau 2 lượt phản biện + sửa: **37 file sửa/xoá tracked, +698/−1266, kèm 7 file mới** —
AppSession/ScrapeSession/UpdateProductSession/WorkbookSheetReader/SlipMagic + 2 file test. Con số
"35 file, +735/−1253, net 518" trong báo cáo gốc của executor là của bản TRƯỚC khi phiên chính sửa
theo phản biện — giữ đoạn dưới nguyên trạng làm hồ sơ, số đúng lấy ở commit.)

### Kết quả kiểm chứng (chạy thật, sau cùng)

| Lệnh | Kết quả |
|---|---|
| `dotnet build ShopeeSuite.sln --no-incremental` | Build succeeded, **0 Warning, 0 Error** |
| `dotnet build server/ShopeeHub.sln --no-incremental` | Build succeeded, **0 Warning, 0 Error** |
| `dotnet test orders/XuLyDonShopee.Tests` | **1489/1489 pass** (bằng số ở HEAD — không mất test nào) |
| ⤷ lọc `TraHangParserTests` | **89/89 pass** |
| `dotnet test suite/Shopee.Core.Tests` | **71/71 pass** |
| `dotnet test server/Shopee.Hub.Web.Tests` | **53/53 pass** |
| `node --check` toàn bộ `extensions/shopee-search/*.js` + `shared/*.js` | tất cả OK |

Kiểm chứng thêm (ngoài yêu cầu, vì C5 là hợp đồng DOM không có compiler canh):

- **So byte chuỗi JS lắp ráp**: viết script đọc file .cs, phân giải các hằng chuỗi rồi ghép lại
  đúng như compiler, so với bản HEAD. **13/15 chuỗi TRÙNG BYTE tuyệt đối**; 2 chuỗi khác là hai
  chuỗi tab "Đã nhận" (khác biệt có chủ đích, xem C5 bên dưới). Cả 15 chuỗi ghép xong đều
  `node --check` PASS.
- **Mutation test cho C6**: đổi `KhongDau` thành `s ?? ""` → `TraHangParserTests` **8/89 FAIL**;
  hoàn nguyên → 89/89 pass. Tức bộ test thật sự canh đúng luật bỏ dấu, không phải xanh vì lý do khác.
- **Grep chứng minh**: `class AppSession` / `class PortAllocator` / `class AppSessionOptions` chỉ
  còn 3 hit, tất cả ở `suite/Shopee.Core/Infrastructure/`. Selector modal shop · khoá ảnh JS ·
  danh sách selector tab "Đã nhận" · luật `items[2]` — mỗi thứ đúng **1 hit**. Các symbol C9
  (`enum ProxyType`, `enum ProxyStatus`, `typeAndSearch`, `isProductNotFoundPage`,
  `BrandBadgeBrush`, `cardButton`, `x:Key="emoji"`, `rebaseDir`, `Assets\**`) — **0 hit**.
  `getCurrentTabUrl` vẫn còn sống (crawl.js:4, detect.js:3) — KHÔNG đụng.

### C1 — AppSession + PortAllocator về Core

File mới `suite/Shopee.Core/Infrastructure/AppSession.cs` (`AppSession` + `AppSessionOptions`);
`suite/Shopee.Core/Infrastructure/PortAllocator.cs` được mở rộng. Xoá 4 file bản module.
Mỗi module giữ một lớp GIỮ CHỖ nhỏ: `Engine/ScrapeSession.cs` (`ScrapeSession` + `ScrapePorts`) và
`Engine/UpdateProductSession.cs` (`UpdateProductSession` + `BigSellerPorts`).

**Quyết định thiết kế cần biết — `AppSession` thành lớp THỂ HIỆN, không phải static.** Plan viết
"tham số hoá base-port + danh sách probe + cờ tạo persistent dir", mà MB và UP `Initialize()` **trong
CÙNG một process** (`App.xaml.cs:54,55`). Nếu gộp thành một static duy nhất thì module gọi sau bị guard
`if (RootDirectory != "") return;` chặn, và hai module dùng CHUNG một block port — **đổi hành vi**
(hiện tại mỗi module chiếm một block riêng nhờ file-lock `FileShare.None` ở
`runtime-sessions/_port-locks`). Nên: một LỚP duy nhất ở Core, **hai thể hiện** → giữ nguyên cơ chế
hai block, hai file-lock, hai thư mục session như trước.

Khác biệt giữa 2 bản cũ và cách xử lý từng cái:

| Khác biệt | Xử lý |
|---|---|
| namespace `OpenMultiBraveLauncherV3` vs `UpdateProduct` | Bỏ, về `Shopee.Core.Infrastructure` |
| Danh sách probe: MB `9330/9430/10000/10400/9700` (+offset) vs UP `10000/10400` (+offset) **và** `8112` (+chỉ-số-block) | Tách 2 tham số `ProbePortsAtOffset` / `ProbePortsAtBlockIndex` — giữ ĐÚNG từng bản, kể cả kiểu cộng khác nhau |
| Chuỗi lỗi hết block ("…cho phien v3 moi." vs "…cho Update Product.") | Tham số `NoFreeBlockMessage`, giữ nguyên văn cả hai |
| UP có thêm `Directory.CreateDirectory(ResolvePersistentDataPath())` trong `Initialize` | **Thực chất là no-op**: `ResolvePersistentDataPath()` → `SuitePaths.ModuleDir("persistent-data")` mà `ModuleDir` đã `CreateDirectory` sẵn. Vẫn giữ đúng khuôn qua cờ `CreatePersistentDataDir=true` cho UP để không đổi hành vi; đã ghi rõ trong xmldoc là dư |
| UP có `ProjectSourceDirectory`, `ApiPort` (8112), `ResolveDataPath` + helper `Combine` | **0 reader** (grep) → bỏ hẳn (đây chính là mục C9 về AppSession, làm một lần) |
| MB `ResolvePersistentDataPath` viết dạng mảng, UP viết qua `Combine` | Cùng kết quả → giữ một bản; để **static** vì đường dẫn không phụ thuộc phiên |
| `PortAllocator` MB `9330/600` + `AllocateInstancePort`, UP `10000/400` + `AllocateBigSellerPort` | Thành tham số ctor `(basePort, count, session, label)`; hai method đổi tên chung `Allocate()` |

Lưu ý về plan: plan ghi "base port 8012/8112" — **sai**, MB không có `ApiPort` nào cả (grep `8012`
toàn repo: 0 hit trong mã nguồn). Chỉ UP có `ApiPort = 8112 + block`.

**Probe 8112 GIỮ LẠI** dù `ApiPort` đã xoá: cổng đó không ai bind nhưng nó THAM GIA quyết định block
nào được nhận — bỏ đi là đổi kết quả chọn block. Đã ghi comment.

`PortAllocator` giờ là MỘT lớp với HAI lối dùng (theo plan): static `Reserve`/`Release` (ephemeral,
cho `BrowserLauncher` + Search — **không đổi một dòng nào**) và thể hiện `Allocate`/`Free` (pool theo
dải, cho Scrape/Update). Instance phải đặt tên `Free` thay vì `Release` vì C# không cho một lớp có cả
`static Release(int)` lẫn `Release(int)` (CS0111). Bản Core **không dính bug re-enqueue của đợt A**
(nó không có hàng đợi, mỗi lần hỏi OS một cổng ephemeral mới) — bug đó chỉ có ở nhánh pool và đã được
mang nguyên vào bản gộp kèm comment.

`ScrapePorts`/`BigSellerPorts` cố ý là **lớp riêng** chứ không phải property trong `ScrapeSession`:
static ctor chỉ chạy khi lần đầu chạm tới, nếu gộp chung một lớp thì `MultiBraveRuntime.Initialize()`
sẽ dựng pool với `PortOffset` = 0 (chưa Initialize xong) → sai dải cổng. Đã ghi comment tại chỗ.

### C2 — BigSellerAutoLogin gộp 3 khối login

Thêm `LoginViaCdpAsync(...)` private làm lõi (attach CDP → chọn page → `RunFormLoginAsync` → Success
thì mark + xuất cookie). Ba lối vào giữ nguyên chữ ký công khai và **nguyên văn từng chuỗi log**.

Điểm tinh vi đã xử lý: lõi trả `AutoLoginOutcome?`, `null` = "chưa chạy tới bước điền form"
(không có context, hoặc exception đã nuốt). Nhờ vậy `EnsureFreshSessionAsync` phân biệt được
"chạy form rồi trượt" (log "thất bại lần này…") với "chưa chạy được form" (chỉ log lỗi attach) —
đúng như bản cũ, không đẻ thêm dòng log. Các khác biệt còn lại tham số hoá:
`preferBigSellerPage` (headless lấy tab đầu, 2 lối kia lọc URL `bigseller`), `postLoginDelayMs`
(4000 cho headless), `cookieFailureIsFatal` (headless: không có cookie auth → Failed; 2 lối kia nuốt),
`noContextMessage` / `errorPrefix` / `errorSuffix`. `LoginHeadlessAsync` giữ nguyên
`catch(OperationCanceled) → throw` + `catch(Exception) → log + Failed` + `finally launcher.Kill()`
bao ngoài (nếu bỏ, lỗi phóng Brave sẽ ném ra ngoài và giết vòng "Đăng nhập tất cả").

### C3 — CategoryAiUpdater dùng AiChat

`CategoryAiUpdater` giờ nhận thẳng `AiConfig`; bỏ `HttpClient` riêng, `Provider` enum, `ParseProvider`,
`ProviderName` (0 consumer — UI dùng `SearchViewModel.AiProviderName` đọc thẳng `AiConfigStore`),
`BuildRequest`, `ExtractContent`, `JsonBody`, `RetryDelay`, `Trunc`. Giữ nguyên prompt (byte-đúng),
cách gom lô và `ExtractJsonObject`. `SearchRunner.MakeUpdater` còn 1 dòng.

**Option MỚI thêm vào `AiChat.CompleteAsync`: `bool jsonMode = false`** (có xmldoc, đã build cả 2
solution vì hub web cũng link AiChat):
- OpenAI → thêm `response_format: {type:"json_object"}`;
- Gemini → thêm `generationConfig.responseMimeType = "application/json"`;
- Anthropic → **không có công tắc tương đương**, cờ không tác dụng (đã ghi rõ trong xmldoc).
Dựng payload bằng `Dictionary<string,object?>` thay anonymous type để **bỏ hẳn khoá** khi
`jsonMode=false` (gửi `null` là API từ chối). Mặc định `false` ⇒ 3 caller AiChat sẵn có
(NameRewriteEngine, mô tả SP, giải captcha) **không đổi một byte payload nào**.

**Khác biệt hành vi phải khai (không tránh được khi bỏ client tự chế):**
1. **Retry**: bản cũ chỉ retry 429 (tối đa 8 lần) với `Retry-After` → "try again in Xs" → backoff mũ
   trần 30s; lỗi khác ném ngay. Nay dùng `AiChat.ExecuteWithRetryAsync` với `maxAttempts: 9` (giữ
   đúng "1 + 8 lần"): 400/401/403/404 ném NGAY (`AiHttpException.IsPermanent`), còn lại (429, 5xx,
   mạng, timeout, JSON hỏng) retry với backoff tuyến tính 15s×lần (429/5xx) hoặc 2s×lần.
   ⇒ **Mất khả năng đọc `Retry-After`** (ĐÃ SỬA sau phản biện — xem mục Nghiệm thu cuối file); đổi lại 5xx/mạng giờ được retry (trước ném ngay). Đã kiểm
   `SearchRunner` chỉ `await` rồi để exception nổi lên (không bắt theo chuỗi thông điệp) nên đường
   tiêu thụ lỗi không gãy.
2. **Timeout HTTP 180s → 120s** (`AiChat` dùng `HttpClient` chung). Không nâng 120s lên vì sẽ kéo dài
   cả 3 caller khác (nhất là giải captcha lúc đăng nhập). Lô lớn quá 120s giờ sẽ timeout rồi được
   retry thay vì chờ tiếp.
3. **Claude mất `temperature = 0`**: nhánh Anthropic của `AiChat` KHÔNG gửi `temperature` (dùng mặc
   định 1.0 của Anthropic). Cố tình KHÔNG sửa vì sẽ đổi hành vi 3 caller khác. Xem mục Đề xuất.
   (ĐÃ SỬA sau phản biện — xem mục Nghiệm thu cuối file.)
4. Gemini: key chuyển từ query-string sang header `x-goog-api-key` (bản AiChat) — tương đương về chức
   năng, an toàn hơn (key không lọt vào URL trong log/exception).
5. Chuỗi lỗi đổi từ `"{provider} lỗi {code}: …400 ký tự"` sang `"AI {provider} lỗi {code}: …300 ký tự"`.
6. Thêm guard `if (!_cfg.HasActiveKey) throw` ở đầu `ClassifyAsync`: thiếu key là lỗi cấu hình, để
   `AiChat` ném `InvalidOperationException` bên trong action thì `ExecuteWithRetryAsync` sẽ coi là lỗi
   tạm và thử lại 9 lần (~72s) mới báo.

### C4 — UpdateProduct: nạp dòng sheet + mark-Hub

File mới `Engine/WorkbookSheetReader.cs`: `ForEachDataRowAsync` (khoá file → mở workbook → chọn sheet →
tính `[start..end]` → gọi delegate từng dòng, trả lại `(start,end)` cho caller log), `Cell` (đọc ô,
cột 0 = rỗng, không gọi `Cell(0)`), `RowId` (ItemId → fallback `ExtractShopeeId(Link)`),
`BeginHubRead` (lấy `HubClient` + `[start..end]` cho nhánh hub-mode). Áp cho cả 4 chỗ:
`WorkbookRecordCache.LoadRecordMapAsync` / `LoadRecordMapFromHubAsync` /
`BigSellerImportToStoreRunner.LoadImportItemIdSetAsync` / `LoadImportItemIdSetFromHubAsync`.
Hệ quả: `using ClosedXML.Excel` không còn cần ở 2 runner (đã bỏ).

Mark-Hub: thêm `MarkStoreProgressHubAsync(send, ids, opLabel, localSourceLabel)` **protected ở lớp nền
`BigSellerBraveRunner`** (cả 2 runner đều kế thừa). `MarkImportedHubAsync` / `MarkUpdatedHubAsync` còn
3 dòng mỗi cái, chuỗi log giữ nguyên văn ("mark-imported … tiến độ local là chính" /
"mark-updated … store local là chính").

### C5 — Chuỗi JS nhúng

3 hằng chuỗi mới, đều `private const string` (nối chuỗi hằng ⇒ vẫn là hằng biên dịch):
- `BigSellerImportToStoreRunner.ShopLabelJsPrelude` + `ShopLabelSelector` — thay 2 bản chép ở
  `SelectImportShopAndConfirmAsync` / `IsImportShopCheckedAsync`. **Cả 2 chuỗi ghép lại trùng byte
  100% với HEAD.**
- `BigSellerImportToStoreRunner.ImgKeyJs` — thay 2 bản JS `key(src)` ở `GetVisibleImageKeysAsync` /
  `CheckMatchingRowsOnPageAsync`; hàm C# `ImgKey` giữ 1 bản, hai bên có comment trỏ nhau.
  **Cả 2 chuỗi trùng byte 100%.**
- `BigSellerCrawlHelper.ClaimedTabJsPrelude` — normalize + 14 selector tab + khử trùng + chọn theo text
  "da nhan" + luật `items[2]`, thay 2 bản ở `SelectClaimedTabByTextAsync` / `IsClaimedTabActiveAsync`.

**Hai chuỗi tab "Đã nhận" KHÔNG trùng byte — khác biệt có chủ đích, tương đương ngữ nghĩa:**
1. `SelectClaimedTabByTextAsync`: `if (items.length === 0) return 'no-items';` chuyển xuống SAU
   `items.find(...)` + `items[2]`. Tương đương: `find` trên mảng rỗng trả `undefined` không side-effect,
   `items[2]` cần `length >= 3` nên không chạy; nhánh trả về của cả 3 ca (rỗng / không thấy / thấy)
   giữ nguyên.
2. `IsClaimedTabActiveAsync`: `normalize` đổi từ dạng gộp 3 dòng sang dạng 8 dòng (**cùng đúng chuỗi
   lệnh**), thêm 2 dòng trống, và biến vòng `find` đổi tên `i` → `item`. Không đổi hành vi.
Danh sách selector và mọi selector khác **không sửa một ký tự**.

### C6 — TraHangParser.KhongDau

Thân đổi thành 1 dòng forward về `MsLoginSelectors.NormalizeForMatch` (Toolkit), thêm
`using Shopee.Toolkit.MsLogin`. Đối chiếu: bản chung hạ chữ ở BƯỚC CUỐI còn bản cũ hạ ở bước đầu —
cùng kết quả (`Đ` không tách được bằng FormD nên có nhánh riêng `Đ→D`, hạ chữ sau ra `d`; `đ→d` sẵn).
Xác nhận bằng 89/89 test + mutation test nêu trên.

### C7 — AccountsView.FindRow

`suite/Shopee.Suite/Modules/Accounts/AccountsView.xaml.cs`: bỏ `FindRow` (8 dòng), gọi
`VisualTreeSearch.FindAncestor<DataGridRow>(…)`. Bỏ `using System.Windows.Media` (hết dùng), thêm
`using Shopee.Suite.Infrastructure`.
Lưu ý: **plan ghi nhầm đường dẫn là "(orders)"** — file thật nằm ở `suite/Shopee.Suite/Modules/Accounts/`
(module Đơn hàng không có bản chép nào; `orders/.../AccountsView.xaml.cs` là file khác hẳn, không đụng).

### C8 — Magic PDF

File mới `orders/XuLyDonShopee.Core/Services/SlipMagic.cs` (`SlipMagic.LooksPdf`, chuẩn 5 byte `%PDF-`).
`SlipFiles.BytesLookPdf` (App) xoá, gọi bản chung. `ShopFlowRunner.TrySaveSlip` **siết 4 → 5 byte**.
Khác biệt hành vi lý thuyết đúng như plan mô tả: file "4 byte đầu đúng, byte thứ 5 sai" trước đây được
GHI xuống đĩa rồi phía ĐỌC mới từ chối; nay từ chối ngay lúc ghi.
**Còn một bản thứ ba chưa gộp** (ngoài phạm vi plan): `ClientApiEndpoints.LooksPdf` ở
`server/Shopee.Hub.Web` — khác solution, hub không tham chiếu `XuLyDonShopee.Core` (chỉ `Compile`-link
vài file của Toolkit). Đã để nguyên, xem Đề xuất.

### C9 — Dọn xác mới

| Mục | Đã làm |
|---|---|
| `ScrapeRunner.ChunkResult.CaptchaUrl` | Xoá field + đối số; để lại comment chỉ chỗ URL captcha thật sự sống (`cfg.CaptchaUrl` ở `LauncherRunnerLoop`) |
| `BackupService` | Rút `MergeBigSeller(list, mirror)` và `MergeShopee(list, mirror)`; bỏ nhánh `replace` bất khả đạt, bỏ `rebaseDir` + nhánh rebase `WorkbookPath`; sửa xmldoc hết nhắc "import-zip"/"append = replace:false". 2 call site ở `HubConfigSync` cập nhật theo |
| `orders/.../Enums.cs` | Xoá `ProxyType` + `ProxyStatus` (0 consumer); **giữ `AccountStatus`** |
| `extensions/shopee-search` | Xoá `typeAndSearch` + `typeAndSearchSynthetic` (page-funcs.js). **Xoá kèm `resolveSearchInputPoint`** — nó CHỈ được 2 hàm trên gọi, giữ lại là tự đẻ xác mới (ngoài chữ của plan, khai ở đây). Bỏ import `cdpGesture` hết dùng + sửa comment đầu file. Xoá `isProductNotFoundPage` (detect.js) + sửa comment đầu file. **`getCurrentTabUrl` giữ nguyên** |
| `Theme.xaml` | Xoá `BrandBadgeBrush`, `cardButton`, `emoji`; sửa comment đầu file còn nhắc `cardButton`. Đã kiểm `EmojiFont` vẫn sống (MessageDialog.xaml) và `BrandTintBrush` vẫn sống (nhiều chỗ) nên KHÔNG đụng |
| `HubServerConfigStore.Load()` | `public` → `private` + xmldoc |
| `AppSession` (UP) dead members | Làm gọn trong C1, không làm 2 lần |
| `BigSellerProfileManager.EnsureWorkflowProfile` | Bỏ tham số `shop`; sửa call site `BigSellerContextFactory` |
| `SearchConfig.Mode` | Mặc định `"keyword"` → `"categoryFromLink"`; viết lại xmldoc `Mode` + `ProductLink`. Sửa comment `AppIcons.cs` (2 chỗ) hết nhắc màn Welcome |
| `XuLyDonShopee.App.csproj` | Xoá `<Resource Include="Assets\**"/>` (thư mục `Assets` không tồn tại) |

### Vướng mắc / lưu ý cho người nghiệm thu

- **Một test FLAKY, KHÔNG do đợt này**: `NotifyDonTraKhoMaTests.BadgeChoDay_DemCaMaTraHangConTon`
  đổ **1 lần trong ~14 lượt chạy full-suite** (`Assert.False() Failure — Actual: null`).
  Nguyên nhân đã truy được: `MotLuotAsync` trả `null` khi `PushGate.TryEnter(accountId, kind)` thua.
  `PushGate` là `static ConcurrentDictionary` **toàn process** khoá theo `(accountId, kind)`; mọi test
  đều dựng DB tạm mới nên `accountId` = 1, và xUnit chạy các class SONG SONG — `HubOutboxWorkerRoundTests`
  cũng gọi `MotLuotAsync` (luôn đi qua `PushGate(1, Gsheet)`). Đây là đua chéo-test có sẵn trong thiết kế
  test, không nằm trên bất kỳ đường mã nào đợt C đụng tới. Đã kiểm chứng thêm: **20 lượt full-suite chạy
  trên HEAD (stash toàn bộ thay đổi) đều pass**, và sau khi `git stash pop` đã đối chiếu `git diff` +
  `git status` khớp 100% với trước khi stash. Vẫn nên vá (xem Đề xuất) vì nó sẽ còn quấy CI.
- Không có hạng mục nào của plan bị bỏ dở.
- Không commit, không đụng file ngoài phạm vi plan, không cài đặt gì.

### Đề xuất (không tự làm — ngoài phạm vi plan)

1. **`AiChat` nhánh Anthropic bỏ qua `temperature`** — nhiều khả năng là lỗi tiềm ẩn, không phải chủ đích:
   `CompleteVisionAsync` có xmldoc ghi rõ "temperature 0 + maxTokens nhỏ cho OCR" nhưng nếu provider là
   Anthropic thì tham số đó rơi vào hư không (giải captcha chạy ở nhiệt độ mặc định 1.0). Nên vá riêng
   một đợt, có đo, vì nó đổi hành vi 3 caller.
2. **Bản thứ ba của magic PDF** ở `server/Shopee.Hub.Web/Api/ClientApiEndpoints.cs:LooksPdf` — muốn gộp
   nốt thì theo khuôn `MsLoginSelectors`: đưa `SlipMagic` vào `shared/Shopee.Toolkit` rồi cho hub
   `Compile`-link đúng file đó (hub không ref được `XuLyDonShopee.Core`).
3. **Vá flaky test**: cho mỗi test một `accountId` riêng, hoặc thêm hook reset `PushGate` giữa các test,
   hoặc gom các class đụng `PushGate` vào cùng một xUnit `Collection` để chúng không chạy song song.
4. **`PortAllocator` gánh 2 khái niệm khác hẳn nhau** (ephemeral static vs pool-theo-dải instance) trong
   một lớp — làm đúng chữ plan, nhưng nếu muốn sạch hơn thì tách pool ra `PortPool` (vẫn ở Core, vẫn
   thoả "chỉ còn bản Core").
5. Đợt sau nếu đụng `BigSellerCrawlHelper`: 3 dòng kiểm "tab đang active"
   (`classList.contains('active') || 'ant-tabs-tab-active' || aria-selected`) vẫn chép đôi — cố ý chưa
   gộp vì plan chỉ nêu selector + luật `items[2]`, và gộp thêm sẽ làm 2 chuỗi lệch bản HEAD nhiều hơn.

---

## Nghiệm thu (Fable tổng hợp sau 2 lượt phản biện, 2026-08-06)

**Lượt 1 — ĐẠT CÓ ĐIỀU KIỆN.** Refactor trung thành: nghiệm thu tự kiểm C1 từng khối (probe/lock/cleanup
khớp nguyên xi 2 bản cũ, luận điểm 2-instance đúng), C5 bằng cách so **chuỗi đã gấp trong DLL biên dịch**
(2 chuỗi tab "Đã nhận" khớp từng ký tự sau lắp ráp), C6 chứng minh tương đương (ca Đ/İ/ẞ). Nhưng lôi ra
2 hồi quy hành vi thật trong C3 mà "pure refactor" không được phép có:
1. **Nhánh Anthropic của AiChat rơi mất `temperature`** → phân loại danh mục bằng Claude chạy nhiệt độ 1.0
   (và xác nhận lỗi CÓ SẴN ở CompleteVisionAsync — xmldoc hứa "temperature 0" nhưng không gửi).
2. **Bao lỗi mạng phình**: 9 lượt × 120s ≈ 19 phút treo màn danh mục (bản cũ ném ngay); mất đọc `Retry-After`.

**Phiên chính sửa (cùng đợt):** thêm `temperature` vào payload Anthropic ở CẢ CompleteAsync lẫn
CompleteVisionAsync; `AiHttpException.RetryAfterMs` + `ReadRetryAfterMs` (header Delta/Date, trần 120s) và
vòng retry ưu tiên nó cho 429/5xx; tham số mới `ExecuteWithRetryAsync(maxAttemptsTransient)` — trần riêng
cho lỗi tạm không-phải-429/5xx (0 = hành vi cũ, caller cũ không đổi), CategoryAiUpdater dùng 9/3; log in
trần thực của lớp lỗi; test mới `AiChatRetryTests` (5 ca control-flow retry) + `SlipMagicTests` (6 ca canh
luật siết 4→5 byte).

**Lượt 2 — ĐẠT.** Xác nhận từng điểm: pattern `RetryAfterMs: > 0` hợp lệ, mọi caller temperature nằm trong
0..1 (soi đủ 5 chỗ), `maxAttemptsTransient=0` giữ nhịp cũ từng-lần (NameRewrite named-args không đổi binding),
test SlipMagic có ca "%PDFX" đúng là ca luật cũ cho qua.

**Đánh đổi CHẤP NHẬN, ghi lại cho người sau:**
- 5xx vẫn nằm ngoài trần transient (đi chung nhánh 429): provider sập 500 liên tục sẽ thử đủ 9 lượt (~9 phút/lô).
  Chấp nhận vì 5xx thường thoáng qua và giờ đã chờ theo `Retry-After` nếu server gửi.
- `Retry-After` chỉ đọc HEADER, chưa khôi phục fallback regex "try again in Xs" trong body (OpenAI hay để số
  ở body) — các ca đó rơi về backoff 15s×lần: chờ dài hơn tối ưu, không sai.
- Kết quả cuối: build 2 sln 0 warning; test **1495 (orders, +6) / 76 (Core, +5) / 53 (hub)** xanh.
