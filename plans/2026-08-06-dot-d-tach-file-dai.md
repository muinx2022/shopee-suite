# Plan: Đợt D — Tách 3 file C# dài (pure move)

- **Ngày:** 2026-08-06
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

3 file dài nhất còn lại sau các đợt refactor gom nhiều tầng trách nhiệm vào một file, làm khổ mọi lần review/sửa. Tách **thuần cơ học** (pure move — cắt dán nguyên văn sang partial/file mới, KHÔNG đổi logic, KHÔNG đổi chữ ký, KHÔNG "tiện tay" sửa gì). Khuôn mẫu sẵn có trong repo: HubDatabase 13 partial, ScrapeViewModel 4 partial, OrdersModuleHost nhiều partial.

## 2. Phạm vi

- **Làm:** 3 mục phần 3.
- **Không làm:** mọi thay đổi hành vi/logic/tên/chữ ký; không format lại code không liên quan; không tách file nào khác ngoài 3 file này.

## 3. Các bước thực hiện

### D1. `suite/Shopee.Module.UpdateProduct/Engine/BigSellerProductUpdateRunner.cs` (~1.269 dòng, class đã partial 4 file)
- Tách 3 type độc lập đang "ở nhờ" ra file riêng: `LaneAbortedException` (~:16), record `WorkbookRecord` (~:19), `WorkbookRecordCache` (~:27–119) → `Engine/WorkbookRecordCache.cs` (lưu ý đợt C có thể đã sửa LoadRecordMapAsync — lấy bản hiện tại).
- Dời `ProcessProductAsync` (~806–993, chuỗi 14 bước điền form) sang partial mới `BigSellerProductUpdateRunner.Process.cs` (cùng tầng với partial Fields — các bước 1/6/12/13 đã bên Fields).
- Dời cụm overlay `OverlayJs`/`OverlayAsync`/`StepAsync` (~995–1036) + helpers modal (~1096–1177) sang partial `BigSellerProductUpdateRunner.Overlay.cs`.
- Đích: file chính còn ~600 dòng, đúng một tầng (vòng đời lane + claim).

### D2. `orders/XuLyDonShopee.Core/Data/OrdersRepository.cs` (~1.313 dòng, 6 mảng nghiệp vụ)
- Đổi class thành `partial`, tách theo mảng (khuôn HubDatabase): `OrdersRepository.Sync.cs` (UpsertMany/DetectNewlyDelivered), `OrdersRepository.Gsheet.cs` (GetForGsheetPush/MarkGsheetSynced/GetGsheetTabs/CountForGsheetPush…), `OrdersRepository.Hub.cs` (GetForHubPush/MarkHubSynced/phiếu slip/hub_push_gen), `OrdersRepository.SoldCount.cs` (GetForSoldCountRetry/MarkSoldCounted), `OrdersRepository.Query.cs` (Query/Count/AppendFilter/AllStatuses + mảng UI), file gốc giữ: ctor, record top-level (`GsheetPendingOrder`, `NewlyDelivered`…), `SetReturnRequestCodes` + helper chung.
- Ranh giới cụ thể theo mảng đã liệt kê — nếu một method dùng chung nhiều mảng thì để file gốc, ghi chú.

### D3. `suite/Shopee.Suite/Modules/Search/SearchViewModel.cs` (~647 dòng)
- Tách partial theo 3 mảng đã xác định: `SearchViewModel.Hub.cs` (mảng Hub-assignment ~292–432 — nhiều bẫy lease/outcome, giữ nguyên comment), `SearchViewModel.Ai.cs` (~525–593), `SearchViewModel.Settings.cs` (UI settings ~595–636). File gốc giữ state + ctor + phần chạy/lưới kết quả. (Mốc dòng là của cây 05/08 — đợt A đã thêm ReloadCategoryFilterFromDb, tự dò lại.)

## 4. Tiêu chí nghiệm thu

- [ ] Build 2 solution 0 error 0 warning; 3 bộ test xanh, số test không đổi.
- [ ] `git diff` đọc được như pure move: phần xóa ở file cũ = phần thêm ở file mới (nghiệm thu sẽ đối chiếu từng hunk); KHÔNG hunk nào đổi logic.
- [ ] `wc -l`: BigSellerProductUpdateRunner.cs ≤ ~700; OrdersRepository.cs ≤ ~400; SearchViewModel.cs ≤ ~350.
- [ ] Mỗi file mới có header xmldoc 1–2 dòng nói file giữ mảng gì (theo khuôn HubDatabase partial).

## 5. Rủi ro & lưu ý

- Cám dỗ lớn nhất của việc tách là "tiện tay sửa" — CẤM. Phát hiện bug trong lúc tách thì ghi vào báo cáo, không sửa.
- D2: OrdersRepositoryTests 1.396 dòng phải xanh nguyên bộ — đó là lưới an toàn chính.
- Dùng `git add -N` + `git diff --find-renames` khi tự kiểm để dễ nhìn move.
- KHÔNG commit.

---

## Báo cáo thực thi (Opus điền sau khi xong)

Thực thi trên cây chính tại commit `970eaf8` (sau đợt B+C). **KHÔNG commit.** Mọi mốc dòng dò lại theo
symbol trên cây hiện tại, không dùng số dòng của plan.

Làm **2 lượt**: lượt 1 = đúng danh sách hạng mục gốc của plan; lượt 2 = phần mở rộng do kiến trúc sư
DUYỆT CHÍNH THỨC sau khi đọc báo cáo lượt 1 (chỉ tiêu `wc -l` của plan gốc là lỗi số học — xem mục
"Chỉ tiêu số dòng…" bên dưới). Kết quả cuối: **cả 3 chỉ tiêu `wc -l` đều đạt.**

| File gốc | Trước | Sau lượt 1 | **Sau lượt 2** | Chỉ tiêu |
|---|---|---|---|---|
| `BigSellerProductUpdateRunner.cs` | 1231 | 820 | **672** | ≤ ~700 ✔ |
| `OrdersRepository.cs` | 1319 | 257 | **257** | ≤ ~400 ✔ |
| `SearchViewModel.cs` | 702 | 429 | **347** | ≤ ~350 ✔ |

### D1 — `BigSellerProductUpdateRunner.cs` (1231 → 820 → **672** dòng)

| File | Nội dung dời (mốc dòng ở bản 1231 dòng) | Dòng |
|---|---|---|
| `suite/Shopee.Module.UpdateProduct/Engine/WorkbookRecordCache.cs` (mới) | `LaneAbortedException` + record `WorkbookRecord` + `WorkbookRecordCache` (11–104, **bản hiện tại** đã có `LoadRecordMapAsync` dùng `WorkbookSheetReader` + nhánh `LoadRecordMapFromHubAsync` của đợt C) | 100 |
| `…/BigSellerProductUpdateRunner.Process.cs` (mới) | `ProcessProductAsync` + comment mục `// ── process one product ──` (767–955) | 198 |
| `…/BigSellerProductUpdateRunner.Overlay.cs` (mới) | `OverlayJs`/`OverlayAsync`/`StepAsync` (957–998) + `DismissBlockingModalAsync`/`CloseVisibleAntModalAsync`/`ClosePageAcceptingDialogAsync`/`ForEachVisibleAsync`/`FirstVisibleAsync` (1058–1139) | 134 |

Khoảng "helpers modal ~1096–1177" của plan được ánh xạ ngược về file bản 05/08 (`git show 5276650`):
1096 = `DismissBlockingModalAsync`, 1177 = dấu `}` đóng `FirstVisibleAsync` → dời đúng 5 hàm đó.

**Lượt 2** (mở rộng đã duyệt) — `…/BigSellerProductUpdateRunner.Listing.cs` (mới, **159** dòng): dời TRỌN
khối 672–818 của bản 820 dòng, tức đúng cụm đã đề xuất và không hơn — `// ── helpers ──` +
`DraftRowKeyAsync`, `DeleteListingRowAsync` (**kèm trọn cặp `#pragma warning disable/restore IDE0051`**,
disable ở dòng 45 / restore ở dòng 68 của file mới — không cắt đôi), `PickListingPage`, `IsDraftPage`,
`GoToListingPageAsync`, rồi `// ── string utils ──` + `Normalize`, `ParsePrice`, `ExtractEditId`,
`TrimDescriptionForShopee`. File gốc còn lại đúng một tầng: vòng đời lane + claim + điều phối dọn media
+ `InspectEditPageAsync`.

### D2 — `OrdersRepository.cs` (1319 → **257** dòng)

`public class OrdersRepository` → `public partial class OrdersRepository` (transform duy nhất ngoài header).

| File | Thành viên |
|---|---|
| `OrdersRepository.Sync.cs` (309) | `UpsertMany`, `GetOrderSnsWithFinalAmount`, `GetOrderSns`, `DetectNewlyDelivered`, `BindData` |
| `OrdersRepository.Gsheet.cs` (143) | `GetForGsheetPush`, `MarkGsheetSynced`, `GetGsheetTabs`, `CountForGsheetPush` |
| `OrdersRepository.Hub.cs` (259) | `GetForHubPush`, `GetShopLoginsByOrderSns`, `MarkHubSynced`, `GetForHubSlipPush`, `MarkHubSlipSynced`, `CountForHubPush`, `CountForHubSlipPush` |
| `OrdersRepository.SoldCount.cs` (93) | `GetForSoldCountRetry`, `MarkSoldCounted` |
| `OrdersRepository.Query.cs` (298) | `CountByAccount`, `Query`, `Count`, `AppendFilter`, `AllStatuses`, `AllShopLogins`, `UpdateStatus`, `EscapeLike`, `MapRow` |
| **file gốc** (257) | usings, `GsheetPendingOrder`, `SoldTransitionResult`, xmldoc class + `_db` + ctor, `DeleteOrders`, `MarkPrepared`, `ReturnCodeSaveResult`, `SetReturnRequestCodes`, `MaxSyncedAtByAccount` |

**Ghi chú ranh giới** (theo luật "dùng chung nhiều mảng thì để file gốc" của plan):
- `DeleteOrders` — điều kiện dọn đơn kết thúc bắc qua cả gsheet + sold-count + hub → giữ gốc.
- `MarkPrepared` — nghiệp vụ "chuẩn bị hàng" (không thuộc 5 mảng) nhưng có reset `hub_synced_at`/`hub_push_gen` → giữ gốc.
- `MaxSyncedAtByAccount` — đọc `synced_at` (mảng Sync) nhưng phục vụ gương danh bạ đẩy Hub → giữ gốc.
- `GetOrderSnsWithFinalAmount`/`GetOrderSns` — chỉ dùng để lọc trong lượt sync → xếp vào `Sync.cs`.
- `BindData` chỉ `UpsertMany` gọi → `Sync.cs`; `EscapeLike`/`MapRow` chỉ `AppendFilter`/`Query` gọi → `Query.cs`.

### D3 — `SearchViewModel.cs` (702 → 429 → **347** dòng)

| File | Nội dung dời |
|---|---|
| `SearchViewModel.Hub.cs` (156) | trọn mục `// ── Việc Search Hub giao (đa máy) ──` (294–434): `IsRunningAssignment`, `StopAssignment`, `TakeAssignmentOutcome`, `RunAssignmentAsync`, `PushNewCollectedAsync` — comment giữ nguyên từng chữ |
| `SearchViewModel.Ai.cs` (100) | trọn mục `// ── Tab Danh mục (AI) ──` (530–616) |
| `SearchViewModel.Settings.cs` (53) | trọn mục `// ── Lưu/nạp cấu hình UI nhỏ ──` (618–659), kèm cặp `#pragma MVVMTK0034` nằm trọn trong `LoadUiSettings` |

Hai hàm đợt A thêm: `NapDanhMucLucKhoiDong` nằm TRONG mục "Tab Danh mục (AI)" → **đi theo `Ai.cs`** (dời trọn khối
liền mạch); `ReloadCategoryFilterFromDb` nằm ở mục `// ── Helpers ──` → **ở lại file gốc**. Hai file là partial của
cùng một class nên ctor vẫn gọi được.

**Lượt 2** (mở rộng đã duyệt) — `SearchViewModel.Links.cs` (mới, **92** dòng): dời TRỌN mục
`// ── Nạp file link + chọn link ──` (129–209 của bản 429 dòng): `ChooseFilesAsync`, `ClearFiles`,
`LoadLinks`, `SelectAllLinks`, `UnselectAllLinks`, `RemoveSelectedLinks`, `FormatLinkProgress`,
`RefreshLinkProgress`. File gốc còn: state + ctor + mục `// ── Chạy ──` + `// ── Xuất Excel ──` + `// ── Helpers ──`.

### Kết quả kiểm chứng (chạy thật SAU LƯỢT 2 — chạy lại đủ bộ, dán nguyên văn phần đuôi)

1. `dotnet build ShopeeSuite.sln --no-incremental` → `Build succeeded. 0 Warning(s) 0 Error(s)`
   (ở LƯỢT 1, lượt build đầu đỏ 4 lỗi CS0246/CS0103 vì thiếu `using ShopeeStatApp.Models/Services` ở 2 partial
   Search — `SearchTaskStore`/`FileRunCoordinator` ở ns `ShopeeStatApp.Services`, `ProductResult` ở
   `ShopeeStatApp.Models`; đã bổ sung using, KHÔNG đụng thân hàm. Lượt 2 xanh ngay từ lần build đầu.)
2. `dotnet build server/ShopeeHub.sln --no-incremental` → `Build succeeded. 0 Warning(s) 0 Error(s)`
3. `dotnet test orders/XuLyDonShopee.Tests` → `Failed: 0, Passed: 1495, Skipped: 0, Total: 1495` (nền trước khi sửa: **1495** — không đổi)
4. `dotnet test suite/Shopee.Core.Tests` → `Failed: 0, Passed: 76, Skipped: 0, Total: 76` (nền: **76**)
5. `dotnet test server/Shopee.Hub.Web.Tests` → `Failed: 0, Passed: 53, Skipped: 0, Total: 53` (nền: **53**)
6. `wc -l` sau lượt 2: `BigSellerProductUpdateRunner.cs` **672** · `OrdersRepository.cs` **257** · `SearchViewModel.cs` **347**
7. Tự chứng minh pure-move — script `verify_puremove.py` so **multiset dòng code** (bỏ dòng trắng, bỏ khoảng
   trắng cuối dòng) của file gốc TRƯỚC KHI ĐỘNG VÀO (bản `970eaf8`) với hợp của (file gốc SAU + **toàn bộ**
   file mới của cả 2 lượt):

   ```
   D1  dòng code trước: 1119 · sau: 1147 · THIẾU: 0 · THÊM: 28
   D2  dòng code trước: 1234 · sau: 1271 · THIẾU: 0 · THÊM: 37
   D3  dòng code trước:  632 · sau:  674 · THIẾU: 0 · THÊM: 42
   KET LUAN: PURE MOVE OK (khong dong code nao bien mat)
   ```

   Toàn bộ phần "THÊM" đã in ra và soi từng dòng: chỉ gồm `using`, `namespace`, xmldoc mô tả file, dòng khai báo
   partial, `{`, `}`. Transform duy nhất được whitelist: `public class OrdersRepository` →
   `public partial class OrdersRepository` (1 dòng).
   Đối chứng độc lập bằng git: `git diff --stat` trên 3 file bị sửa = **1 insertion, 1977 deletions**, và
   `git diff -U0 | grep '^+'` trả về đúng MỘT dòng `+public partial class OrdersRepository` — tức 3 file cũ chỉ
   MẤT dòng, không có dòng nào bị sửa nội dung.
   13 file mới đều thuần CRLF (CR count == LF count), khớp quy ước repo.

### Tiêu chí nghiệm thu — đối chiếu (sau lượt 2)

- [x] Build 2 solution 0 error 0 warning; 3 bộ test xanh, số test không đổi (1495/76/53).
- [x] `git diff` đọc được như pure move (1 insertion / 1977 deletions, insertion duy nhất là từ khoá `partial`).
- [x] **`wc -l`: ĐẠT 3/3.** `BigSellerProductUpdateRunner.cs` **672** ≤ ~700 ✔; `OrdersRepository.cs` **257** ≤ ~400 ✔; `SearchViewModel.cs` **347** ≤ ~350 ✔. (Sau lượt 1 mới chỉ đạt 1/3 — 820 / 257 / 429.)
- [x] Mỗi file mới có header 1–2 dòng nói file giữ mảng gì (khuôn `/// <summary>Phần X: …</summary>` của HubDatabase).
      *Ngoại lệ hình thức:* `WorkbookRecordCache.cs` chứa 3 type top-level (không phải partial) nên header là comment
      `//` đặt dưới `namespace` — không thể dùng xmldoc ở vị trí đó vì mỗi type đã có xmldoc riêng của nó.

### Chỉ tiêu số dòng của plan gốc MÂU THUẪN với chính danh sách hạng mục của plan (đã xử lý ở lượt 2)

Ở lượt 1 tôi DỪNG, không tự mở rộng phạm vi để "chữa cháy" chỉ tiêu (theo luật "không tự ý mở rộng phạm vi"),
mà báo lại số học. Kiến trúc sư đã xác nhận đây là **lỗi số học của plan** và **duyệt chính thức** phần mở rộng
→ làm ở lượt 2. Ghi lại số học để lưu vết quyết định:

- **D1**: file bản 05/08 (commit `5276650`) dài **1269** dòng. Cộng đúng 4 khoảng plan liệt kê:
  16–119 (104) + 806–993 (188) + 995–1036 (42) + 1096–1177 (82) = **416** dòng → còn lại **853**.
  Plan lại ghi "đích ~600 dòng" và tiêu chí "≤ ~700". Trên cây hiện tại (1231 dòng) các khối đó là 411 dòng → **820**.
- **D3**: file bản 05/08 dài **647** dòng. Cộng 3 khoảng plan liệt kê: 292–432 (141) + 525–593 (69) + 595–636 (42)
  = **252** → còn lại **395**, trong khi tiêu chí ghi "≤ ~350". Trên cây hiện tại (702 dòng) → **429**.
- **D2** thì nhất quán: 1319 → 257, thừa sức dưới 400.

Nói cách khác: dù thực thi ĐÚNG 100% danh sách hạng mục của plan gốc, hai chỉ tiêu `wc -l` kia vẫn không thể đạt
— chúng được ước lượng lạc quan chứ không suy ra từ các khoảng dòng mà chính plan chỉ định.

### Phạm vi mở rộng lượt 2 (đã duyệt) — kết quả thật vs ước lượng

| Hạng mục | Ước lượng lúc đề xuất | Thực tế |
|---|---|---|
| `BigSellerProductUpdateRunner.Listing.cs` | ≈145 dòng dời → gốc ~675 | 147 dòng dời (159 kể header) → gốc **672** |
| `SearchViewModel.Links.cs` | ≈81 dòng dời → gốc ~348 | 81 dòng dời (92 kể header) → gốc **347** |

Không có hạng mục nào khác được thêm ngoài đúng 2 cái trên.

### Ghi chú thêm (không sửa gì, chỉ báo)

- **Using thừa cố ý để lại**: sau khi cắt, file gốc `OrdersRepository.cs` không còn dùng `System.Text`,
  `Microsoft.Data.Sqlite`, `XuLyDonShopee.Core.Models`, `XuLyDonShopee.Core.Services`; `BigSellerProductUpdateRunner.cs`
  (thêm `System.Globalization`, `System.Text.RegularExpressions` sau lượt 2) và `SearchViewModel.cs` cũng có vài
  using tương tự. **Cố ý KHÔNG xoá**: xoá chúng sẽ là dòng biến mất khỏi cây, làm hỏng bằng chứng pure-move ở
  mục 7; và C# không cảnh báo using thừa khi build (repo không có `.editorconfig` / `EnforceCodeStyleInBuild`)
  nên tiêu chí 0-warning vẫn đạt. Nếu muốn dọn, nên làm ở một đợt riêng.
- **Không phát hiện bug mới** trong lúc tách (không có gì để "tiện tay sửa" mà phải kìm lại).

### Danh sách 13 file mới (tổng kết)

`suite/Shopee.Module.UpdateProduct/Engine/`: `WorkbookRecordCache.cs` (100) · `BigSellerProductUpdateRunner.Process.cs` (198)
· `BigSellerProductUpdateRunner.Overlay.cs` (134) · `BigSellerProductUpdateRunner.Listing.cs` (159)

`orders/XuLyDonShopee.Core/Data/`: `OrdersRepository.Sync.cs` (309) · `OrdersRepository.Gsheet.cs` (143)
· `OrdersRepository.Hub.cs` (259) · `OrdersRepository.SoldCount.cs` (93) · `OrdersRepository.Query.cs` (298)

`suite/Shopee.Suite/Modules/Search/`: `SearchViewModel.Hub.cs` (156) · `SearchViewModel.Ai.cs` (100)
· `SearchViewModel.Settings.cs` (53) · `SearchViewModel.Links.cs` (92)

---

## Nghiệm thu (Fable tổng hợp sau phản biện, 2026-08-06)

`nghiem-thu` chấm **ĐẠT** cả 4 tiêu chí. Tự chứng minh pure-move bằng 3 lớp độc lập, mạnh hơn cả executor:
(1) git diff -U0 = đúng 1 dòng `+partial`; (2) multiset THIẾU=0; (3) **phân hoạch khối liên tục** — mỗi file
mới là hợp các đoạn liên tục không chồng lấn của bản 970eaf8, phần bù khớp file gốc từng dòng đúng thứ tự
(bắt được cả đảo thứ tự mà multiset mù). So thân 8 hàm bắt buộc: giống hệt từng ký tự. Pragma IDE0051 +
MVVMTK0034 nằm trọn trong 1 file; 0 chữ ký trùng; không field-initializer nào bị dời (rủi ro riêng của
partial mà plan không nêu — nghiệm thu tự kiểm thêm).

Đính chính câu chữ báo cáo executor (không đổi code):
- `DeleteOrders` giữ gốc vì "không thuộc mảng nào trong 5 mảng" (thân hàm chỉ DELETE thuần; phần bắc-cầu
  nằm ở caller `NenXoaDonKetThuc`) — KHÔNG phải vì "bắc qua nhiều mảng" như báo cáo ghi.
- `MarkPrepared` thực chất chạm đúng mảng Hub (reset hub_synced_at + hub_push_gen); xếp Hub.cs sẽ nhất quán
  hơn nhưng giữ gốc chấp nhận được — ghi lại cho lần dọn sau.
- Multiset "THIẾU=0" của D2 là sau whitelist transform; câu chính xác: 1 dòng đổi nội dung (`class`→`partial class`).

Lưu ý cho người sau: `SearchViewModel.cs` còn 347/350 — đừng lấy file này làm chỗ đổ code mới; using thừa
ở 3 file gốc để lại CÓ CHỦ ĐÍCH (giữ bằng chứng pure-move) — dọn ở đợt riêng.
