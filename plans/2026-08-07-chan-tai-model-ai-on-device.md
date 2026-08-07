# Plan: chặn hồ sơ trình duyệt tự tải model AI on-device (OptGuideOnDeviceModel ~4 GB/hồ sơ)

- **Ngày:** 2026-08-07
- **Trạng thái:** hoàn thành (đã qua phản biện `nghiem-thu` 07/08/2026 + sửa 4 điểm; còn MỘT việc kiểm chứng
  bằng tay chưa làm được ở máy dev — xem mục "Sau phản biện" cuối file)
- **Người lập:** Opus 5 (phiên chính) · **Người thực thi:** phiên chính (theo `CLAUDE.md` của repo) · **Phản biện:** `nghiem-thu`

## 1. Bối cảnh & mục tiêu

Đo thật trên máy dev 2026-08-07:

```
%AppData%\XuLyDonShopee\profiles\23-chrome\OptGuideOnDeviceModel\2025.8.8.1141\weights.bin = 3,98 GB
%AppData%\XuLyDonShopee\profiles\30-chrome\OptGuideOnDeviceModel\2025.8.8.1141\weights.bin = 3,98 GB
tổng %AppData%\XuLyDonShopee\profiles = 16,06 GB / 25 hồ sơ (riêng 2 file trên ~8 GB)
```

Chrome tự tải model AI on-device (Gemini Nano / Optimization Guide) về **gốc user-data-dir** của từng hồ sơ do
app tạo. Hồ sơ là loại dùng-rồi-bỏ và số lượng tăng dần ⇒ 25 hồ sơ × 4 GB có thể lên ~100 GB. App **không dùng**
tính năng AI nào của trình duyệt nên đây là rò rỉ dung lượng thuần.

Hai việc phải làm: (a) **chặn tải** bằng cờ dòng lệnh ở MỌI nơi app phóng trình duyệt; (b) **dọn** thư mục đã tải
về khi chuẩn bị hồ sơ.

### Hiện trạng code (đã khảo sát)

Mọi lệnh phóng trình duyệt của repo đều đi qua **một builder chung**: `shared/Shopee.Toolkit/Browser/BraveArgs.cs`
(grep `--disable-features`, `--user-data-dir`, `BraveArgs.` — không sót nhánh nào). Năm call-site:

> **Đính chính sau phản biện (07/08/2026):** thực tế là **6** call-site đi qua builder — bảng dưới gộp
> `BrowserLauncher` và `BigSellerBraveRunner` làm một dòng (#5) nhưng chúng là hai nơi phóng khác nhau. Ngoài ra
> còn **một đường thứ 7 KHÔNG đi qua builder**: hub web phóng Chromium bằng Playwright
> (`BigSellerLoginService`) — xem bước 4 và mục "Sau phản biện".

| # | Call-site | Chuỗi `--disable-features` hiện tại |
|---|---|---|
| 1 | `orders/XuLyDonShopee.Core/Services/BraveLaunchArgs.cs:80` (`BuildBraveArgs`, cầu nối + login Playwright) | `Translate,CalculateNativeWinOcclusion,IntensiveWakeUpThrottling` (+`,DisableLoadExtensionCommandLineSwitch` khi có extension) |
| 2 | `orders/XuLyDonShopee.Core/Services/BraveLaunchArgs.cs:109` (`BuildCleanPocArgs`, POC mở sạch) | như trên, luôn kèm `DisableLoadExtensionCommandLineSwitch` |
| 3 | `suite/Shopee.Module.Search/Engine/BraveManager.cs:211` | `DisableLoadExtensionCommandLineSwitch` |
| 4 | `suite/Shopee.Module.MultiBrave/Engine/BraveProfileManager.cs:110` (scrape) | `CalculateNativeWinOcclusion,IntensiveWakeUpThrottling,DisableLoadExtensionCommandLineSwitch` |
| 5 | `suite/Shopee.Core/Browser/BrowserLauncher.cs:70` (check tk / BigSeller login) và `suite/Shopee.Module.UpdateProduct/Engine/BigSellerBraveRunner.cs:77` (Update/Import) | **KHÔNG CÓ cờ `--disable-features` nào** |

Hai chỗ ở dòng cuối bảng hiện không có cờ nào ⇒ nếu chỉ "nối thêm vào chuỗi đang có" thì chúng vẫn hở.

**Ràng buộc kỹ thuật quyết định thiết kế:** Chromium lưu switch trong một map theo tên ⇒ có hai cờ
`--disable-features=` thì **chỉ một cái ăn**, cái kia mất trắng. Vì vậy tuyệt đối KHÔNG được "thêm một cờ
`--disable-features` thứ hai" cho xong — phải **gộp vào đúng một cờ duy nhất**.

### Quyết định thiết kế

Đặt luật ở **một nơi duy nhất là `BraveArgs`**, ngay tại bước `Build()`/`BuildList()`:

- Hằng `OnDeviceAiModelFeatures` = `OptimizationGuideOnDeviceModel`, `OptimizationGuideModelDownloading`,
  `TextSafetyClassifier`.
- Khi dựng kết quả: gom MỌI phần `--disable-features=` thành **một** (đặt ở vị trí phần đầu tiên), khử trùng lặp,
  **luôn** nối 3 feature trên; nếu call-site không khai cờ nào thì tự chèn một cờ mới (chèn **trước** tham số
  positional cuối, để `StartUrl` vẫn nằm ở dòng cuối).

Lý do chọn "chặn tại Build" thay vì sửa tay 5 chỗ: hai call-site không có cờ nào (BrowserLauncher, BigSeller
runner) nằm ở project **không có project test nào với tới** (`Shopee.Core.Tests` chỉ ref `Shopee.Core`;
`BuildArgs` của Search/BrowserLauncher là `private`). Chốt bất biến ở builder chung ⇒ một điểm test phủ cả 5 nơi,
và call-site mới trong tương lai không thể quên. Ngoài ra vẫn thêm hàm `DisableFeatures(...)` để call-site khai
cờ riêng của mình một cách tường minh (đọc code thấy ngay, thay cho `.Add("--disable-features=…")` chép tay).

## 2. Phạm vi

- **Làm:**
  - `shared/Shopee.Toolkit/Browser/BraveArgs.cs`: hằng feature chặn model AI, hằng tên thư mục model, hàm
    `DisableFeatures(...)`, chuẩn hoá `--disable-features` tại `Build()`/`BuildList()`.
  - 4 call-site đang có `--disable-features` chuyển sang `DisableFeatures(...)` (giữ nguyên feature cũ).
  - Dọn thư mục `OptGuideOnDeviceModel` đã tải: phía suite thêm vào danh sách cache tái tạo được
    (`BraveCachePolicy`), phía orders thêm bước dọn trong `BrowserProfileGuard.FreeProfile`.
  - `server/Shopee.Hub.Web/Services/BigSellerLoginService.cs`: thêm cờ vào `Args` của Playwright (1 dòng).
  - Test mới/sửa ở `suite/Shopee.Core.Tests` + `orders/XuLyDonShopee.Tests`, kèm bước **thử phá** từng test mới.
- **Không làm:**
  - KHÔNG đổi bất kỳ feature/cờ nào đang có (chỉ nối thêm) — không đụng `DisableLoadExtensionCommandLineSwitch`,
    `Translate`, `CalculateNativeWinOcclusion`, `IntensiveWakeUpThrottling`, nhóm chống-treo-nền, proxy, cache-limit.
  - KHÔNG xoá hồ sơ, KHÔNG đụng cookie/đăng nhập (chỉ xoá đúng thư mục `OptGuideOnDeviceModel`).
  - KHÔNG deploy hub, KHÔNG phát hành client trong việc này (bump version/CHANGELOG để đợt phát hành sau).
  - KHÔNG dọn hộ 8 GB đang nằm trên máy dev bằng code quét toàn ổ — hồ sơ nào được mở lại thì tự dọn; phần còn
    lại báo user xoá tay (nêu ở mục 5).

## 3. Các bước thực hiện

### Bước 1 — `shared/Shopee.Toolkit/Browser/BraveArgs.cs`

1. Thêm hằng công khai:
   ```csharp
   /// Feature của Chromium phải TẮT ở mọi hồ sơ do app tạo: chúng tải model AI on-device (~4 GB/hồ sơ,
   /// đo 07/08/2026: 2 hồ sơ = 8 GB). App không dùng AI của trình duyệt ⇒ rác thuần.
   public static readonly IReadOnlyList<string> OnDeviceAiModelFeatures = new[]
   {
       "OptimizationGuideOnDeviceModel",
       "OptimizationGuideModelDownloading",
       "TextSafetyClassifier",
   };

   /// Tên thư mục model đã tải, nằm ngay GỐC user-data-dir. Dùng chung cho các bước dọn hai phía.
   public const string OnDeviceAiModelDirName = "OptGuideOnDeviceModel";
   ```
2. Thêm `public BraveArgs DisableFeatures(params string[] features)`: nối `--disable-features=<danh sách>`;
   nhận cả chuỗi đã ghép sẵn bằng dấu phẩy (tách theo `,`, bỏ phần tử rỗng/khoảng trắng).
3. Thêm hàm **thuần** `public static IReadOnlyList<string> NormalizeDisableFeatures(IReadOnlyList<string> parts)`:
   - Tìm mọi phần bắt đầu bằng `--disable-features=`; gom token theo đúng thứ tự xuất hiện, khử trùng lặp
     (`StringComparer.Ordinal` — tên feature của Chromium phân biệt hoa/thường).
   - Nối thêm các feature trong `OnDeviceAiModelFeatures` còn thiếu.
   - Kết quả: **đúng một** phần `--disable-features=…` đặt tại vị trí phần đầu tiên tìm được; các phần
     `--disable-features` khác bị bỏ.
   - Nếu không có phần nào: chèn cờ mới **trước tham số positional đầu tiên** (phần không bắt đầu bằng `-`,
     vd URL của `StartUrl`, kể cả bản bọc ngoặc `"https://…"`); không có positional thì thêm cuối.
   - Không sửa `_parts` (gọi `Build()` nhiều lần cho cùng kết quả).
4. `Build()` và `BuildList()` chạy qua `NormalizeDisableFeatures` trước khi trả kết quả. Ghi xmldoc nói rõ **vì sao**
   (bẫy Chromium map switch: hai cờ thì một cái mất) để người sau không "tối ưu" bỏ đi.

### Bước 2 — 4 call-site chuyển sang `DisableFeatures(...)` (giữ nguyên feature cũ)

- `orders/XuLyDonShopee.Core/Services/BraveLaunchArgs.cs:80` và `:109`.
- `suite/Shopee.Module.Search/Engine/BraveManager.cs:211`.
- `suite/Shopee.Module.MultiBrave/Engine/BraveProfileManager.cs:110`.

`suite/Shopee.Core/Browser/BrowserLauncher.cs` và `suite/Shopee.Module.UpdateProduct/Engine/BigSellerBraveRunner.cs`
**không sửa** — bước 1 tự chèn cờ cho chúng (có test chứng minh).

### Bước 3 — dọn thư mục model đã tải

1. `suite/Shopee.Core/Browser/BraveCachePolicy.cs`: thêm `BraveArgs.OnDeviceAiModelDirName` vào
   `RegenerableCacheRelPaths` (kèm comment: nằm ở GỐC profile, không phải trong `Default`). Nhờ đó cả
   `StartupJanitor` (quét định kỳ mọi hồ sơ suite) lẫn `BraveProfileManager.PrepareProfileForLaunch` (scrape)
   tự dọn.
2. `orders/XuLyDonShopee.Core/Services/ProfileJanitor.cs`: thêm
   `public static long XoaModelAiOnDevice(string userDataDir, Action<string>? log = null)` — xoá đệ quy
   `<userDataDir>/OptGuideOnDeviceModel` (best-effort, nuốt lỗi), trả số byte đã giải phóng; thư mục không tồn
   tại → trả 0, không đụng đĩa.
3. `orders/XuLyDonShopee.Core/Services/BrowserProfileGuard.cs`: trong `FreeProfile`, sau
   `ClearProfileSessionAndLocks`, gọi `ProfileJanitor.XoaModelAiOnDevice(userDataDir, log)`; khi giải phóng > 0
   thì log một dòng (`Dọn hồ sơ: đã xoá model AI on-device (~X MB).`). `FreeProfile` là cửa duy nhất của cả hai
   đường phóng phía orders (`OrdersBridgeLauncher:34`, `LoginBrowserBootstrap:125`) và bước dọn cuối vòng
   (`OrdersBridgeSession:711`) ⇒ phủ hết.

### Bước 4 — hub web (1 dòng, chỉ có hiệu lực ở lần deploy hub kế tiếp)

`server/Shopee.Hub.Web/Services/BigSellerLoginService.cs:120` — thêm
`"--disable-features=OptimizationGuideOnDeviceModel,OptimizationGuideModelDownloading,TextSafetyClassifier"` vào
`Args`. Project này **không** ref `Shopee.Toolkit` (chỉ link file nguồn) nên ghi chuỗi thẳng + comment trỏ về
`BraveArgs.OnDeviceAiModelFeatures`. Rủi ro thấp: Chromium bỏ qua feature lạ; đây là bản headless ephemeral nên
chỉ là phòng xa cho ổ 37 GB của VM.

### Bước 5 — test

**Mới `suite/Shopee.Core.Tests/BraveArgsDisableFeaturesTests.cs`:**
- `CallSiteKhongKhaiGi_VanCoDu3FeatureChanModelAi` — `BraveArgs.Window("C:/p").StartUrl("https://x").Build()`.
- `StartUrlVanODongCuoi_KhiPhaiChenCoMoi` — phần tử cuối vẫn là URL.
- `GopMoiDisableFeaturesThanhDungMotCo` — builder có 2 cờ `--disable-features` → kết quả đúng 1 cờ, giữ đủ token
  của cả hai (đây là bẫy "cờ thứ hai nuốt cờ thứ nhất").
- `KhongTrungLapKhiCallSiteTuKhaiFeatureAi`.
- `GoiBuildHaiLan_KetQuaGiongNhau` (không tích luỹ).

**Mới `suite/Shopee.Core.Tests/BraveCachePolicyOptGuideTests.cs`:** dựng thư mục tạm
`<tmp>/profiles/x/OptGuideOnDeviceModel/2025.8.8.1141/weights.bin` → `PruneProfileCache` xoá đúng thư mục đó và
trả số byte > 0; `Default/Network/Cookies` (file giả) **còn nguyên**.

**Sửa `orders/XuLyDonShopee.Tests/BraveLaunchArgsTests.cs`:**
- Dòng 44 (assert chuỗi CHÍNH XÁC) đổi thành
  `--disable-features=Translate,CalculateNativeWinOcclusion,IntensiveWakeUpThrottling,OptimizationGuideOnDeviceModel,OptimizationGuideModelDownloading,TextSafetyClassifier`.
- Thêm test: args chỉ có **đúng một** phần tử bắt đầu bằng `--disable-features`.
- Thêm test: khi có `extensionPath`, chuỗi vẫn giữ `DisableLoadExtensionCommandLineSwitch` **và** đủ 3 feature AI.

**Sửa `orders/XuLyDonShopee.Tests/BraveCleanPocArgsTests.cs`:** bổ sung assert 3 feature AI (test "startUrl ở cuối"
đã có sẵn — giữ, nó chính là chốt cho luật chèn cờ).

**Mới trong `orders/XuLyDonShopee.Tests/ProfileJanitorTests.cs`:** `XoaModelAiOnDevice` xoá đúng thư mục model,
giữ nguyên các file khác trong hồ sơ; thư mục không tồn tại → trả 0, không ném.

### Bước 6 — thử phá test (bắt buộc, theo `d:\Projects\CLAUDE.md`)

Lần lượt hoàn tác tạm từng thay đổi và xác nhận **đúng** test tương ứng đỏ, rồi khôi phục:
1. Bỏ `OnDeviceAiModelFeatures` khỏi `NormalizeDisableFeatures` → test orders (chuỗi chính xác) + test
   `CallSiteKhongKhaiGi…` phải đỏ.
2. Đổi "gộp thành một cờ" thành "thêm cờ thứ hai" → `GopMoiDisableFeaturesThanhDungMotCo` + test "đúng một phần tử"
   của orders phải đỏ.
3. Chèn cờ mới ở CUỐI thay vì trước positional → `StartUrlVanODongCuoi…` và
   `BraveCleanPocArgsTests.CoUserDataDir_VaStartUrlOCuoi` phải đỏ.
4. Bỏ `OnDeviceAiModelDirName` khỏi `RegenerableCacheRelPaths` → `BraveCachePolicyOptGuideTests` đỏ.
5. Bỏ lời gọi `XoaModelAiOnDevice` trong thân hàm → test `ProfileJanitorTests` mới đỏ.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` — 0 lỗi, **0 warning**.
- [ ] `dotnet build server/Shopee.Hub.Web/Shopee.Hub.Web.csproj` — 0 lỗi, 0 warning (project này **KHÔNG** nằm
      trong solution nên `dotnet build ShopeeSuite.sln` không phủ).
- [ ] `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj` — xanh toàn bộ (bao gồm test mới).
- [ ] `dotnet test suite/Shopee.Core.Tests/Shopee.Core.Tests.csproj` — xanh toàn bộ (bao gồm test mới).
- [ ] Grep `--disable-features` trong `*.cs`: chỉ còn ở `BraveArgs.cs` (định nghĩa + chuẩn hoá), ở
      `BigSellerLoginService.cs` (hub) và trong test — **không** còn `.Add("--disable-features=…")` chép tay ở
      call-site nào.
- [ ] Chạy tay kiểm chứng cuối: một đoạn C# nhỏ (hoặc test tạm) in args của cả 5 call-site — mỗi bộ có **đúng một**
      `--disable-features` và bộ nào cũng chứa đủ 3 feature chặn model AI; feature cũ của từng nơi còn nguyên.
- [ ] Bước 6 (thử phá) làm đủ 5 mục, ghi kết quả vào mục báo cáo.
- [ ] Không có file nào ngoài phạm vi bị sửa (`git status` sạch sẽ theo danh sách file trong plan).

## 5. Rủi ro & lưu ý

- **Bẫy chính (đã tính trước):** hai cờ `--disable-features` thì Chromium chỉ nhận một ⇒ nếu ai đó "sửa nhanh"
  bằng cách `.Add()` thêm một cờ nữa, extension sẽ ngừng nạp (`DisableLoadExtensionCommandLineSwitch` bị nuốt) —
  triệu chứng sẽ là cầu nối/Search chết câm chứ không báo lỗi. Đó là lý do phải gộp và có test canh.
- **Thứ tự tham số:** `StartUrl` bắt buộc nằm cuối (positional). Luật chèn cờ mới phải tôn trọng điều này —
  có test riêng.
- **Fingerprint/anti-bot:** ba feature này chỉ tắt model AI cục bộ, không đụng `AutomationControlled`,
  `navigator.webdriver`, hay locale ⇒ không đổi dấu vết mà Shopee/BigSeller soi. Trang web không đọc được danh
  sách `--disable-features`.
- **Feature lạ với Brave:** Brave có thể không có `OptimizationGuide*`; Chromium **bỏ qua** feature không tồn tại,
  không lỗi. Hồ sơ dính lỗi thực tế là `*-chrome` nên đằng nào cũng phải phủ nhánh Chrome.
- **8 GB đang nằm trên máy dev:** hồ sơ `23-chrome`/`30-chrome` sẽ tự sạch ở lần mở phiên kế tiếp (bước dọn trong
  `FreeProfile`). Hồ sơ không bao giờ mở lại thì vẫn còn — báo user xoá tay bằng lệnh dọn thủ công sau khi việc
  này nghiệm thu xong (không code quét toàn ổ trong việc này).
- **Không tự tin về `TextSafetyClassifier`** đứng một mình có chặn được hay không; nó đi kèm bộ Optimization Guide
  nên giữ theo đúng yêu cầu của user. Tắt `OptimizationGuideModelDownloading` mới là cờ chặn tải.

---

## Báo cáo thực thi

Người thực thi: phiên chính (Opus 5). Làm đúng plan, không đổi hướng. File đã sửa:

| File | Thay đổi |
|---|---|
| `shared/Shopee.Toolkit/Browser/BraveArgs.cs` | `OnDeviceAiModelFeatures`, `OnDeviceAiModelDirName`, `DisableFeatures(params)`, `NormalizeDisableFeatures` (gộp + bổ sung, chèn trước positional); `Build`/`BuildList` đi qua chuẩn hoá |
| `orders/…/Services/BraveLaunchArgs.cs` | 2 chỗ `.Add("--disable-features=…")` → `.DisableFeatures(…)` |
| `suite/Shopee.Module.Search/Engine/BraveManager.cs` | như trên |
| `suite/Shopee.Module.MultiBrave/Engine/BraveProfileManager.cs` | như trên |
| `suite/Shopee.Core/Browser/BraveCachePolicy.cs` | `RegenerableCacheRelPaths` += `OptGuideOnDeviceModel` |
| `orders/…/Services/ProfileJanitor.cs` | thêm `XoaModelAiOnDevice` (+ `DoKichThuoc`) |
| `orders/…/Services/BrowserProfileGuard.cs` | `FreeProfile` gọi thêm bước xoá model + log số MB |
| `server/…/Services/BigSellerLoginService.cs` | thêm cờ vào `Args` Playwright |
| 4 file test sửa + 2 file test mới | xem mục 3 bước 5 |

### Kết quả kiểm chứng THẬT

- `dotnet build ShopeeSuite.sln` → **0 lỗi, 0 warning**.
- `dotnet build server/Shopee.Hub.Web` → **0 lỗi, 0 warning**.
- `dotnet test suite/Shopee.Core.Tests` → **111/111 xanh**.
- `dotnet test orders/XuLyDonShopee.Tests` → **1644/1644 xanh**.
- Grep `--disable-features` trong `*.cs`: chỉ còn ở `BraveArgs.cs`, `BigSellerLoginService.cs` (hub) và test —
  không còn `.Add("--disable-features=…")` chép tay ở call-site nào.

**In args THẬT của từng call-site** (harness console riêng, gọi cả hàm `private` qua reflection —
`scratchpad/argcheck`). Mỗi bộ đều có **đúng 1** cờ `--disable-features`:

| Call-site | Chuỗi `--disable-features` | Phần tử cuối |
|---|---|---|
| orders `BuildBraveArgs` (không ext) | `Translate,CalculateNativeWinOcclusion,IntensiveWakeUpThrottling,OptimizationGuideOnDeviceModel,OptimizationGuideModelDownloading,TextSafetyClassifier` | `--disable-popup-blocking` |
| orders `BuildBraveArgs` (có ext) | như trên + `DisableLoadExtensionCommandLineSwitch` (chèn trước nhóm AI) | `--load-extension=C:\ext` |
| orders `BuildCleanPocArgs` | như trên | `https://banhang.shopee.vn/portal/shop` ✔ |
| Search `BraveManager.BuildArgs` | `DisableLoadExtensionCommandLineSwitch` + 3 feature AI | `"https://shopee.vn/#_ss_ws=8123"` ✔ |
| scrape `BraveProfileManager.BuildBraveArguments` | `CalculateNativeWinOcclusion,IntensiveWakeUpThrottling,DisableLoadExtensionCommandLineSwitch` + 3 feature AI | `--disable-component-update` |
| `BrowserLauncher.BuildArgs` | **trước đây KHÔNG có cờ nào** → nay `OptimizationGuideOnDeviceModel,OptimizationGuideModelDownloading,TextSafetyClassifier` | `"https://shopee.vn/"` ✔ |

**Call-site thứ 6 — `BigSellerBraveRunner.StartBrave` (Update/Import) — KHÔNG in được args thật**: chuỗi cờ nằm
inline trong hàm phóng Brave, gọi là mở trình duyệt thật. Phần bảo đảm cho nó là test mức builder
(`CallSiteKhongKhaiGi_VanCoDu3FeatureChanModelAi` + `StartUrlVanODongCuoi_KhiPhaiChenCoMoi`) vì chuỗi của nó
cùng hình dạng `BraveArgs.Create()…StartUrl(StartUrl).Build()` (đọc `BigSellerBraveRunner.cs:77-100`) — tức
verify bằng suy luận + test builder, KHÔNG phải chạy thật. Ghi rõ để không nhận vơ.

### Thử phá test (5/5 mục, mỗi lần sửa hỏng → chạy test → khôi phục)

| Sửa hỏng | Test đỏ |
|---|---|
| 1. Bỏ nhóm feature AI khỏi `NormalizeDisableFeatures` | Core: `CallSiteKhongKhaiGi…`, `GopMoi…`, `KhongTrungLap…`; orders: `CoDisableFeaturesOnDinh…` (chuỗi chính xác), `CoExtension_VanGiu…`, POC `ChanTaiModelAi…` |
| 2. Thêm cờ `--disable-features` THỨ HAI thay vì gộp | Core: `GopMoi…`, `KhongTrungLap…`, `GoiBuildHaiLan…`; orders: `ChiCoDungMotCoDisableFeatures`, `CoExtension…`, POC `ChanTaiModelAi…` **và** POC `CoUserDataDir_VaStartUrlOCuoi` |
| 3. Chèn cờ mới ở CUỐI thay vì trước positional | Core: `StartUrlVanODongCuoi_KhiPhaiChenCoMoi` |
| 4. Bỏ `OnDeviceAiModelDirName` khỏi `RegenerableCacheRelPaths` | Core: `PruneProfileCache_XoaThuMucModelAi_GiuNguyenCookie` |
| 5. Bỏ lời gọi `XoaModelAiDaTai` trong `FreeProfile` | orders: `FreeProfile_XoaLuonModelAiOnDevice_VaBaoNhatKy` |

**Sai lệch so với plan (khai báo, không sửa plan cho khớp):**
- Plan đoán mục 3 sẽ làm đỏ cả `BraveCleanPocArgsTests.CoUserDataDir_VaStartUrlOCuoi` — **sai**: đường POC vốn
  đã khai cờ `--disable-features` nên đi nhánh "gộp tại chỗ", không có bước chèn. Luật chèn chỉ được canh bởi
  test mức builder (đã đỏ đúng như mong đợi). Không hở, nhưng ghi lại cho đúng sự thật.
- Plan viết mục 5 sẽ làm đỏ "test `ProfileJanitorTests` mới" — **sai**: test đó chỉ canh bản thân hàm dọn.
  Phần NỐI DÂY được canh bằng test mới thêm `BrowserProfileGuardTests.FreeProfile_XoaLuonModelAiOnDevice_VaBaoNhatKy`
  (test tích hợp Windows-only), và chính nó đỏ khi bỏ lời gọi.

### Còn lại

- 8 GB trên máy dev: hồ sơ `23-chrome`/`30-chrome` tự sạch ở lần mở phiên kế tiếp; hồ sơ không mở lại nữa thì
  user xoá tay (lệnh gợi ý ở phần trả lời).
- Chưa bump `version.txt`/CHANGELOG, chưa deploy hub, chưa phát hành client — theo đúng phạm vi.

---

## Sau phản biện (`nghiem-thu`, 07/08/2026)

Kết luận của người phản biện: **đạt có điều kiện** — phần lõi chắc, nhưng thay đổi ở hub là lỗi thật. Phiên chính
tự đối chiếu lại từng điểm (đọc thẳng driver Playwright, đọc code call-site) trước khi nhận. Bốn điểm đã sửa:

### 1. (NGHIÊM TRỌNG — đã revert) Thay đổi ở hub tự tạo ra đúng cái bẫy hai cờ mà plan cấm

`server/Shopee.Hub.Web/Services/BigSellerLoginService.cs` — bước 4 của plan **SAI**, đã bỏ hẳn.

Bằng chứng đọc thẳng driver (`.playwright/package/lib/coreBundle.js`, Playwright 1.60):

```js
chromiumSwitches = (options) => [ …, "--disable-component-update", …,
                                  "--disable-features=" + disabledFeatures.join(","), … ];
_innerDefaultArgs(options) { const chromeArguments = [...chromiumSwitches()];
                             … chromeArguments.push(...args);  // Args của mình nối SAU, KHÔNG gộp }
```

Playwright **đã tự truyền một cờ `--disable-features`** rồi mới nối `Args` của ta vào sau ⇒ browser nhận hai cờ
cùng tên, Chromium chỉ giữ một. Danh sách mặc định của Playwright có `OptimizationHints` kèm đúng comment
*"Prevents downloading optimization hints on startup."*, và nó cũng đã truyền sẵn `--disable-component-update`.
Tức hub **vốn đã được che**, còn dòng thêm vào thì hoặc là no-op, hoặc (nếu cờ của ta thắng) **xoá sổ** danh sách
của Playwright — bật lại `OptimizationHints` (phản tác dụng) và mất các feature giữ ổn định phiên headless
(`AvoidUnnecessaryBeforeUnloadCheckSync`, `DestroyProfileOnBrowserClose`…).

Đã trả `Args` về nguyên trạng 3 cờ cũ + comment cảnh báo dài để không ai thêm lại.

### 2. (MỞ RỘNG PHẠM VI — có chủ đích) Thêm `--disable-component-update` cho hai đường phóng của orders

`orders/XuLyDonShopee.Core/Services/BraveLaunchArgs.cs` — hằng `ChanComponentUpdater`, thêm vào cả
`BuildBraveArgs` và `BuildCleanPocArgs`.

Lý do: model AI on-device được **cài qua component updater** về gốc user-data-dir. Bằng chứng tại chỗ mạnh hơn
mọi suy luận về tên feature: hai hồ sơ rò 3,98 GB đều là hồ sơ **của orders**, và orders là đường phóng **DUY
NHẤT** thiếu cờ này — 4 đường phía suite (`BrowserLauncher`, `BigSellerBraveRunner`, `BraveManager`,
`BraveProfileManager`) đều đã có sẵn qua `BraveArgs.DiskCacheLimit()` và **không hồ sơ nào của suite bị rò**.
Nhóm feature AI vẫn giữ làm lớp thứ hai. Đây là mở rộng ngoài plan gốc — ghi rõ ở đây, không sửa plan cho khớp.

**Chưa lấp:** orders vẫn KHÔNG có trần cache đĩa (3 cờ còn lại của `DiskCacheLimit`) — cố ý để ngoài phạm vi việc
này; đó là một hạng mục riêng.

### 3. (TRUNG BÌNH — đã sửa) Hàm xoá mới thiếu sanity check mà chính file đó đặt ra

`ProfileJanitor.XoaModelAiOnDevice` nay chặn bằng `HasProfilesSegment` giống `TryResetDirectory` (cùng là thao
tác phá huỷ). Đã kiểm: mọi hồ sơ thật đều là `<baseDir>/profiles/<id>-<kind>` (`BrowserProfilePaths.ForAccount`,
là nguồn duy nhất của cả 3 nơi gọi `FreeProfile`) ⇒ luật này không cắt mất ca hợp lệ nào.
`BrowserProfileGuardTests` đã sửa dùng đường dẫn có segment `profiles` như hồ sơ thật.

### 4. (THẤP — đã sửa) Log làm tròn thành "~0 MB"

`BrowserProfileGuard` in `{mb:0.##}` thay cho chia nguyên.

### Kiểm chứng lại sau khi sửa (phiên chính tự chạy)

- `dotnet build ShopeeSuite.sln` → **0 lỗi, 0 warning**; `dotnet build server/Shopee.Hub.Web` → **0 lỗi, 0 warning**.
- `dotnet test suite/Shopee.Core.Tests` → **111/111**; `dotnet test orders/XuLyDonShopee.Tests` → **1647/1647** (+3 test mới).
- **Thử phá 3 test mới** (sửa hỏng → build → chạy → khôi phục, hash file khớp bản gốc):

  | Phá gì | Test đỏ |
  |---|---|
  | Bỏ `.Add(ChanComponentUpdater)` ở `BuildBraveArgs` | `BraveLaunchArgsTests.CoChanComponentUpdater` |
  | Bỏ `.Add(ChanComponentUpdater)` ở `BuildCleanPocArgs` | `BraveCleanPocArgsTests.CoChanComponentUpdater` |
  | Bỏ `!HasProfilesSegment(...)` trong `XoaModelAiOnDevice` | `ProfileJanitorTests.XoaModelAiOnDevice_DuongDanNgoaiProfiles_KhongXoaGi` |

  **Bẫy gặp phải, ghi lại để lần sau khỏi dính:** khôi phục file bằng `Copy-Item` từ bản backup làm
  `LastWriteTime` cũ hơn DLL đã build ⇒ MSBuild coi là up-to-date, **bỏ qua biên dịch**, và lượt test "xác nhận
  khôi phục" chạy trên chính bản đã phá (1 test đỏ giả). Phải `touch` lại file rồi build mới ra kết quả thật.

### Việc kiểm chứng CÒN LẠI (không làm được ở máy dev, phải làm khi chạy thật)

**Chưa ai chứng minh thư mục 4 GB không mọc lại.** Toàn bộ việc này mới chỉ được chứng minh ở mức "chuỗi args
đúng như thiết kế" + "bước dọn xoá đúng thư mục". Cách kiểm: xoá `OptGuideOnDeviceModel` ở một hồ sơ orders, chạy
một vòng bình thường, rồi kiểm tra thư mục có mọc lại không.
