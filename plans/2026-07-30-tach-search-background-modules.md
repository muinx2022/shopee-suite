# Plan: Tách `shopee-search/background.js` thành module ES (đợt 4 — extension search)

- **Ngày:** 2026-07-30
- **Trạng thái:** hoàn thành (chờ phiên chính nghiệm thu — xem "Báo cáo thực thi" cuối file)
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

`extensions/shopee-search/background.js` ~2.400 dòng — service worker MV3 (đã `"type":"module"` + đã import `shared/` sau 3G: ws-bridge, util, tab-wait, net-detect). Phần còn lại vẫn một file: flow keyword-search, flow shop, flow category, page-functions synthetic (bơm vào trang), extract kết quả, quản lý tab.

Mục tiêu (refactor thuần): tách thành ~6-7 module ES trong `shopee-search/` (KHÔNG phải shared/ — đây là code riêng của search): đề xuất `sw-main.js` (entry: đăng ký listener top-level + điều phối), `tabs.js`, `detect.js` (những gì chưa nằm ở shared/net-detect), `flow-keyword.js`, `flow-shop.js`, `flow-category.js`, `page-funcs.js` (hàm bơm vào trang — LƯU Ý các hàm này bị serialize độc lập, `const sleep`/helper bên trong thân hàm PHẢI GIỮ), `extract.js`. Manifest trỏ service worker sang entry mới (hoặc background.js giữ làm entry mỏng chỉ import — chọn cách ít đổi manifest nhất, ghi rõ). Khử 4 bản helper chuột trong page-funcs bằng pattern `pageInstallHelpers` (mẫu orders) NẾU các bản thật sự trùng — lệch thì giữ, ghi rõ.

## 2. Phạm vi & ràng buộc

- Chỉ đụng `extensions/shopee-search/**` (+ `extensions/sync-shared` nếu cần thêm đường check — thường không).
- MV3: mọi `chrome.*.addListener` phải ở top-level của module được import ngay từ entry (không đăng ký trong async callback).
- KHÔNG đổi hành vi/delay/selector; KHÔNG đụng shared/, extension khác, C#.
- KHÔNG commit.

## 3. Nghiệm thu

- [ ] `node --check` (qua bản .mjs) sạch mọi file; rig nạp module với `chrome.*` giả lập (mẫu 3G — dựng lại được từ mô tả trong plan 3G) nạp entry OK.
- [ ] Không file nào > ~600 dòng.
- [ ] `git diff --stat`: tổng số dòng không tăng quá +50 (chỉ tách + import, không viết mới).
- [ ] Bảng "hàm nào → module nào" + xác nhận listener top-level trong báo cáo.

## 5. Rủi ro & lưu ý

- Page-functions serialize: hàm truyền vào `chrome.scripting.executeScript({func})` không nhìn thấy import của module — mọi helper nó dùng phải nằm TRONG thân hàm hoặc install qua pattern pageInstallHelpers. Đây là chỗ dễ gãy nhất — soi từng page-func sau khi tách.
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Trạng thái: xong.** Nền: worktree bị cắt từ `0d7918c` → đã `git merge --ff-only main` về `ba653ed` theo bước 0.

### Manifest: KHÔNG đổi

`background.js` giữ nguyên vai trò entry (`"service_worker": "background.js"`, `"type": "module"`) — cách ít
đổi manifest nhất. Nó thành entry MỎNG (52 dòng): chỉ import + 3 khối listener top-level + `handleMessage`.
`suite/Shopee.Suite.csproj` gom extension bằng glob `**\*` nên file module mới tự được chép, không phải sửa.

### Hàm nào → module nào

| Module | Dòng | Nội dung (theo thứ tự trong file) |
|---|---:|---|
| `background.js` (entry) | 52 | keep-alive alarm · `handleMessage` + `setAppMessageHandler` · `chrome.tabs.onUpdated` (bắt cổng `_ss_ws=`) · khôi phục `_wsPort` từ `storage.local` rồi `connectWs` |
| `core.js` | 109 | `DEFAULT_WS_PORT` · `ctx` (state) · `setAppMessageHandler` · `bridge`/`connectWs`/`send`/`log`/`reportNetworkError` · `cdpGesture`/`resolveGesture`/`cdpClickAt` · `stopSearch` · `sessionPace`+`sleep` |
| `tabs.js` | 55 | `resolveSearchTab`, `isUsableShopeeTab`, `closeApiTabs`, `closeOtherTabs`, `getCurrentTabUrl`, `waitForTabLoad`, `waitForUrlChange`, `waitForUrl` |
| `detect.js` | 49 | `isVerifyPage`, `isProductNotFoundPage`, `isNetworkErrorPage` |
| `crawl.js` | 551 | `readScrollState`, `humanScrollPage`, `humanScrollPageSynthetic`, `cdpScrollToLoadThenTop`, `hasNextSearchPage`, `getTotalPages`, `resolveNextPagePoint`, `clickNextSearchPage(+Synthetic)`, `crawlPagesForCurrentState` |
| `extract.js` | 251 | `extractPageData` |
| `page-funcs.js` | 440 | `resolveBestSellingPoint`, `applySalesSortFallbackIfNeeded`, `prepareBestSelling(+Synthetic)`, `resolveSearchInputPoint`, `typeAndSearch(+Synthetic)` |
| `flow-keyword.js` | 393 | `startSearch`, `buildSearchUrl`, `collectSearchCategories`, `resolveCategoryToggle`, `resolveCategoryLabelPoint`, `selectSearchCategory(+Synthetic)` |
| `flow-shop.js` | 225 | `startShopFromLink`, `clickResolvedAnchor`, `resolveViewShopPoint`, `clickViewShop`, `readShopName`, `resolveAllProductsPoint`, `clickAllProducts`, `resolveTopSalesShopPoint`, `clickTopSalesShop` |
| `flow-category.js` | 317 | `startCategoryFromLink`, `collectSubCategories`, `parseCatId`, `dismissHomePopups`, `clickHomeCategory`, `clickSubCategory`, `resolveLocationToggle`, `resolveLocationCheckboxPoint`, `applyLocationFilter` |

Chiều import (không có vòng): `core ← tabs ← detect ← extract ← crawl ← page-funcs ← flow-* ← background`.

### Hai điểm buộc phải đổi (ngoài việc cắt file)

1. **`ctx` thay 3 biến module-scope.** `let searchTabId/initialTabId/searchState` → `export const ctx = {…}`;
   mọi tham chiếu thành `ctx.*`. Lý do: binding import KHÔNG gán lại được từ module khác, phải chung một ô nhớ.
2. **`onMessage` của bridge đi qua handler đăng ký.** `core.js` giữ `bridge` (vì `log`/`cdpGesture` cần), nhưng
   `handleMessage` ở entry → `setAppMessageHandler(handleMessage)`. Không có gói tin nào tới trước lúc đăng ký:
   entry chạy `setAppMessageHandler` đồng bộ, còn `connectWs` chỉ chạy ở dòng cuối entry.

### Listener top-level (luật MV3) — đã xác nhận

Cả 3 chỗ đăng ký đều ở top-level của entry, không nằm trong async callback: `chrome.alarms.onAlarm` (bg:10),
`chrome.tabs.onUpdated` (bg:28), và `chrome.storage.local.get('_wsPort', …)` (bg:49 — gọi ngay, không phải listener).
Thân module phụ thuộc được đánh giá đồng bộ TRƯỚC thân entry nên listener vẫn đăng ký trong lượt eval đầu tiên.
Rig xác nhận: sau `import` là đã có 1 listener alarm + 1 listener tabs.onUpdated.

### 4 bản helper chuột trong page-func: GIỮ NGUYÊN (không dùng pageInstallHelpers)

Đối chiếu bằng máy 4 bản (A `humanScrollPageSynthetic`, B `selectSearchCategorySynthetic`,
C `prepareBestSellingSynthetic`, D `clickNextSearchPageSynthetic`):

| Helper | Có ở | Kết luận |
|---|---|---|
| `elementAt`, `mouseEvent` | A, B, C | **trùng khít** (~14 dòng/bản) |
| `moveMouseTo` | A, B, C | A=B; **C lệch** (`rand(22,48)` bước vs `rand(18,42)`, `sin*2.7 rand(-5,5)` vs `sin*3 rand(-4,4)`, nghỉ `rand(9,30)` vs `rand(8,28)`) |
| `clickElement` | B, C | **lệch** (lề `8/10` vs `10/12`, nghỉ `rand(180,520)`/`rand(55,150)` vs `rand(180,550)`/`rand(60,160)`) |
| `wheel` | A, C | **lệch** (`rand(-25,25)/rand(-20,20)`, `deltaX rand(-8,8)` vs `rand(-20,20)/rand(-16,16)`, `deltaX rand(-6,6)`) |
| — | D | không có helper nào: quỹ đạo viết thẳng (28 bước, `sin*4`/`cos*3`), dispatch trực tiếp lên `next` |

Chỉ phần trùng thật là `elementAt`+`mouseEvent`; các hằng số lệch chính là **nút vặn chống bot** (jitter/nhịp),
khử đi là đổi hành vi. Ngoài ra `pageInstallHelpers` cần thêm 1 lượt `executeScript` trước MỖI page-func và để
lại biến global trên `window` của shopee.vn — cả hai đều là bề mặt lộ mới. ⇒ giữ nguyên 4 bản, đúng nhánh
"lệch thì giữ, ghi rõ" của plan.

### Nghiệm thu

- **Không viết mới dòng logic nào.** Máy đã đối chiếu: mọi dòng trong 10 file mới đều truy được về đúng dòng
  cũ của `background.js` (kể cả sau phép đổi tên `ctx.*`), trừ **52 dòng** là header/import/`setAppMessageHandler`.
  Phủ dòng: 0 dòng bị chép 2 lần; 42 dòng gốc không được chép = dòng trắng ngăn cách + 5 dòng import/header cũ +
  banner `── Config ──`/`── Helpers ──` + khối `let searchTabId/initialTabId/searchState` (đã thành `ctx`).
- **Page-func còn nguyên**: đếm trước/sau khớp tuyệt đối — 32 `func:`, 33 `world: 'MAIN'`, 7 `const sleep = ms =>`,
  3 `mouseEvent`, 3 `elementAt`, 3 `moveMouseTo`, 2 `clickElement`, 2 `wheel`, 4 `const rand`.
- `node --check` (chép sang `.mjs`): **14/14 file OK** (10 module + 4 file `shared/`).
- **Rig nạp thật** với `chrome.*` + `WebSocket` giả lập (mẫu 3G), timer kẹp về 1ms: nạp `background` OK, bridge mở
  `ws://localhost:9111`, `onUpdated` đổi được sang cổng 9333. Chạy **6 lượt flow** (keyword / shopFromLink /
  categoryFromLink × {có ack CDP + DOM đầy đủ, không ack + DOM rỗng}) → **51 bước log riêng biệt**, đi qua cả nhánh
  CDP lẫn nhánh synthetic, vào tới vòng `crawlPagesForCurrentState` (Category 1/2, 2/2 + Page 1/9, 1/50),
  `collectSubCategories`, `applyLocationFilter`. **0 `is not defined` / `is not a function` / unhandledRejection.**
- Quét tĩnh riêng (tên hàm cấp module cũ dùng mà không khai báo/import; và mọi `import {X} from './y.js'` phải khớp
  `export` của y): sạch. Bắt được 1 lỗi thật lúc dựng (`cdpClickAt` thiếu ở `crawl.js`) đã sửa.
- Kích thước: file lớn nhất `crawl.js` **551** dòng (< 600). Tổng **2442** dòng / 10 file so với **2396** / 1 file
  ⇒ **+46 dòng** (ngưỡng +50).
- `dotnet build suite/Shopee.Suite`: **0 warning, 0 error**; đã kiểm `bin/Debug/net8.0/extensions/shopee-search/`
  có đủ 10 file `.js` + `manifest.json` + `shared/`. `dotnet test suite/Shopee.Core.Tests`: **43/43 passed**.
- `extensions/sync-shared.sh --check`: exit 0 (không đụng `shared/`).

### Điểm cần phiên chính soi

- **Chưa chạy thử trên Brave thật** — refactor thuần, nhưng đây là service worker MV3 nên nên mở 1 lượt Search
  (keyword) + 1 lượt link shop để chắc. Rig chỉ giả lập `chrome.*`.
- **Lệch so với đề xuất trong plan: 10 module thay vì ~6-7.** Trần 600 dòng ép phải tách thêm — gộp theo đúng
  đề xuất thì `page-funcs.js` sẽ ~682 dòng. Cụ thể: thêm `core.js` (plan không kể tên nhưng bắt buộc: state +
  WS + CDP + sleep dùng chung), và `crawl.js` gánh luôn phần cuộn (plan gọi là mảng của `page-funcs`). `page-funcs.js`
  vì thế chỉ còn phần "chuẩn bị trang tìm kiếm" (sắp Bán chạy + gõ từ khoá) — tên giữ theo plan.
- **`background.js` mất BOM.** File cũ mở đầu bằng U+FEFF, file mới không có. Vô hại với Chrome/`node --check`,
  nhưng nếu phiên chính muốn giữ nguyên thì phải thêm lại thủ công.
- Banner cũ `// ── Search — type keyword + Enter, collect DOM data ──` được dùng làm dòng đầu `flow-keyword.js`,
  còn banner `// ── Type keyword… ──` nằm giữa `page-funcs.js` như dấu ngăn mục — hai cái này (và các banner khác)
  vẫn là **mojibake sẵn có trong file gốc**, cố ý chép nguyên chứ không sửa (ngoài phạm vi plan).
