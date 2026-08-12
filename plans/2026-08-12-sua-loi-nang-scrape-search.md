# Plan: Sửa 8 lỗi nặng mảng Scrape + Search (theo review 2026-08-12)

- **Ngày:** 2026-08-12
- **Trạng thái:** hoàn thành (đã qua nghiệm thu + 2 vòng phản biện; xem Báo cáo thực thi)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`, 2 đợt tuần tự) + phiên chính (vá theo phản biện)

## 1. Bối cảnh & mục tiêu

Review đối kháng 2026-08-12 (2 lượt phản biện độc lập, phiên chính đã đối chiếu từng phát hiện vào code) tìm ra
**8 lỗi nặng**: 3 ở mảng Scrape, 5 ở mảng Search. Điểm chung: **mất dữ liệu âm thầm** — kết luận "xong" được suy
ra (khoảng dòng liền / outcome cuối phiên) thay vì ghi nhận trực tiếp, cộng `catch { }` nuốt lỗi quanh chỗ ghi
file. Không lỗi nào có test đỡ: không project test nào phủ `Shopee.Module.Search`; mảng tiến độ scrape
(`ScrapeProgressStore`, `LastDoneOf`) cũng chưa có test.

Mục tiêu: sửa đúng 8 lỗi này + thêm test tối thiểu để chúng không tái phát. KHÔNG sửa nhóm VỪA/NHẸ trong đợt này
(trừ 2 điểm dính liền kể dưới).

### Danh sách lỗi (đã kiểm chứng, kèm vị trí)

**Scrape:**
- **S1** — Dòng hỏng GIỮA khối bị nuốt vào khoảng "đã xong": `LauncherRunnerLoop.cs:272-275` chỉ log rồi chạy
  tiếp khi `scrapeOk=false` (nhóm lỗi thường); `ScrapeRunner.cs:285` và `:406-410` báo tiến độ theo KHOẢNG liền
  `[from..lastDone]` → dòng hỏng nằm trong khoảng "đã cào", không bao giờ chạy lại, không dấu vết. Kéo theo:
  dòng bị "BỎ QUA vì kẹt" ở `ScrapeRunner.cs:291-298` cũng không được ghi nhận đâu cả → shop kẹt vĩnh viễn ở
  "chưa xong", mỗi Resume đốt 3 lượt mở Brave (phải sửa cùng vì chung một thiết kế ghi nhận).
- **S2** — `lastCompletedRow` RÁC từ profile cũ: `InstanceConfig.ApplyExtensionProgress` (`InstanceConfig.cs:122`)
  nhận `state.LastCompletedRow` không kiểm tra thuộc khối hiện tại; `ScrapeRunner.LastDoneOf` (`:356-361`) kẹp
  XUỐNG `to` → giá trị 5000 của hôm qua thành "xong tới dòng 12" khi login fail đầu khối. (Đường live `:406` có
  guard `lc <= to` đúng — hai chỗ đọc cùng field xử lý ngược nhau.) Nguồn rác: profile Brave theo tk Shopee dùng
  lại, `runnerState` trong `Local Extension Settings` không bị dọn; `__launcherApplyFormConfig`
  (`extensions/shopee-scrape/background.js` ~:822) chỉ ghi sheet/startRow/endRow, không reset tiến độ cũ.
- **S3** — "Xoá tiến độ" ở cửa sổ Thống kê là no-op khi có Hub: `ScrapeStatsViewModel.cs:84-88` chỉ
  `ScrapeProgressStore.Clear` local; ledger Hub còn nguyên → Resume sau (hoặc mở lại app, fold ledger) kéo tiến
  độ cũ về. Đường Reset chuẩn ở `ScrapeViewModel.cs:285-295` có gọi `SetLedgerStatusAsync(coordKey, Idle)` —
  cửa sổ Thống kê thiếu bước đó.

**Search:**
- **F1** — Bấm Dừng / outcome `Error` → toàn bộ SP đã cào mất đường xuất: `FileRunCoordinator.cs:254` return
  trước mọi chỗ lưu; `shop_products` (nguồn duy nhất của "Xuất tất cả") chỉ ghi ở nhánh `Completed` (`:260`);
  `task_products` không có truy vấn đọc nào.
- **F2** — Extension bỏ qua `resumeCategoryIndex`/`resumePage`: `flow-category.js:15` hard-code
  `resumeCategoryIndex: 1`, vòng `for (let i = 0; ...)` (`:93`), `crawlPagesForCurrentState` không được truyền
  `startPage` (`:86`, `:123`) → mọi lần nối lại WS / bấm Tiếp tục là cào lại từ danh mục #1, checkpoint bị đẩy lùi.
- **F3** — `shop_products.shop_id` bị ghi CAT ID: `FileRunCoordinator.cs:260` truyền `CatId(item.Link)` vào tham
  số `shopId`; `SearchTaskStore.cs:472` `shopId > 0 ? shopId : p.ShopId` mà cat id luôn > 0 → Shop ID sai toàn
  bộ, link `product/{catId}/{itemId}` chết 404. `shop_name` cũng bị ghi nhãn danh mục.
- **F4** — File link .xlsx mỗi lần đánh dấu đẻ MỘT cột mới: `SetDone` (`FileRunCoordinator.cs:315`) tạo
  `LinkFileStore` mới chưa `Load()` → `StatusColumn=0` → `MarkStatusXlsx` (`LinkFileStore.cs:152`) lấy
  `LastColumnUsed+1` mỗi lần; nạp lại chỉ đọc cột cuối → mất dấu Processed, link bị tick lại. Kèm race: nhiều
  lane cùng ghi 1 file (xlsx lẫn sidecar .txt dùng chung đường tmp), lỗi bị `catch { }` nuốt.
- **F5** — Nhánh trích xuất `__SC_DATA__` thiếu `location` (`extract.js:59-67`) → bộ lọc khu vực (mặc định
  "Hà Nội"; location rỗng = LOẠI, `SearchOrchestrator.cs:300`) loại sạch SP, nhưng vẫn "✔ Xong (0 sản phẩm)"
  và `SetDone` đánh dấu link đã xử lý (`FileRunCoordinator.cs:264-265`).

### Kiến trúc liên quan (người thực thi KHÔNG cần đọc lại chat)

- Tiến độ scrape: `ScrapeProgressStore` (`suite/Shopee.Core/Scrape/ScrapeProgressStore.cs`) key
  (BigSellerId, sheet, OrdinalIgnoreCase), `Completed` là list `RowRange` gộp qua `RowRangeMath.Merge`;
  `FinishRun` tính `Complement(Completed, start, total)` → rỗng = Completed, còn = Stopped.
- Đường báo tiến độ: `ScrapeViewModel.cs:384-389` — `runner.RowsCompleted += (from,to) => { MarkCompleted;
  Coordination.Hub.PublishProgress(coordKey, from, to); }`. `coordKey = new CoordKey(account.Id, shop.Id,
  sheet, CoordOp.Scrape)` (`:252`).
- Search: mỗi link chạy trong `FileRunCoordinator.RunLinkAsync`; SP về qua WS → `SearchOrchestrator` (lọc khu
  vực, dedup `AddResultIfNew`) → `SearchSession.Results` (RAM) + `SearchTaskStore.SaveProduct` (bảng
  `task_products`, chỉ để đếm). Xuất: "Xuất tất cả" đọc `shop_products` (`GetAllShopProducts`); file Excel
  per-link ghi từ RAM qua `SaveLinkExcel` → `ExcelExporter.Export` (tmp+rename).
- `ProductResult` CÓ sẵn `ShopId`, `ShopName`, `ShopLocation`; `Link` dựng từ `ShopId/ItemId`.
- Test JS: `Shopee.Core.Tests/ExtensionJsCuPhapTests.cs` parse mọi file `extensions/**/*.js` bằng Acornima —
  sửa JS xong test này phải còn xanh.
- Solution: `ShopeeSuite.sln` (gốc repo). Build `dotnet build ShopeeSuite.sln` — **0 warning là mốc**.

## 2. Phạm vi

- **Làm:** 8 lỗi trên + skip-ledger cho dòng bỏ qua (dính S1) + khoá ghi file link (dính F4) + test tối thiểu
  (project mới `Shopee.Module.Search.Tests`; test store/toán khoảng ở `Shopee.Core.Tests`).
- **Không làm:**
  - Nhóm VỪA/NHẸ còn lại của review (SelectedShop mutable, video fallback chết, AppSettingsService, dedup link
    trùng giữa 2 file, `_seenLinks` toàn cục, `ExportSafe` nuốt lỗi…) — đợt sau.
  - KHÔNG dọn dữ liệu `shop_products` cũ đã sai shop_id (không phân biệt được với dữ liệu đúng) — ghi chú cho
    user trong CHANGELOG khi phát hành; user có thể "Xóa dữ liệu" để làm lại sạch.
  - KHÔNG đổi `DiscardPendingWork` ở Workspace (ngữ nghĩa "bỏ việc dở" ≠ "xoá tiến độ", ledger giữ nguyên là đúng).
  - KHÔNG bump version / commit / phát hành (phiên chính chốt sau).

## 3. Các bước thực hiện

### Đợt 1 — Search (F1..F5)

1. **F3 trước (nền cho F1):** `SearchTaskStore`
   - Migration: `ALTER TABLE shop_products ADD COLUMN category_id INTEGER NOT NULL DEFAULT 0` +
     `category_label TEXT NOT NULL DEFAULT ''` (idempotent — check `PRAGMA table_info` hoặc catch duplicate
     column, theo cách migration hiện có của file).
   - `SaveShopProducts(categoryId, categoryLabel, sourceLink, results)`: đổi ngữ nghĩa tham số đầu thành
     category; ghi `shop_id = p.ShopId`, `shop_name = p.ShopName`, `category_id/category_label` = tham số.
     Cập nhật ON CONFLICT + mọi truy vấn đọc (`GetAllShopProducts`, thống kê danh mục) — **soát kỹ các JOIN/
     GROUP BY đang dựa trên shop_id-mang-nghĩa-cat-id, chuyển sang `category_id`/`category_label`**, nếu không
     tab Danh mục sẽ trống sau fix.
   - Caller `FileRunCoordinator.cs:260` giữ nguyên `CatId/CatLabel` (giờ đúng ngữ nghĩa tham số mới).
2. **F1:** `FileRunCoordinator.RunLinkAsync`
   - Tách hàm lưu chung `PersistResultsAsync(item, results)` = `SaveShopProducts` + `SaveLinkOnceAsync` (giữ
     nguyên xử lý lỗi từng bước, KHÔNG nuốt trắng — lỗi phải ra `LinkStatus`).
   - Nhánh `Cancelled` (`:254`): trước khi return, nếu `results.Count > 0` → persist + status
     "■ Đã dừng — đã lưu {N} SP."; KHÔNG `SetDone`.
   - Nhánh `Error` (`:299-304`): nếu `results.Count > 0` → persist, status ghi thêm "đã lưu {N} SP".
   - `catch (OperationCanceledException)` (`:248`): best-effort persist `session.Results` rồi `throw` tiếp
     (upsert nên chạy đôi vô hại).
3. **F5:** 
   - `extensions/shopee-search/extract.js` nhánh Try 2 (`__SC_DATA__`): thêm `location: b.shop_location || ''`
     (khớp tên field 2 nhánh kia đang dùng; đối chiếu Try 1/Try 3 để giữ cùng schema item).
   - `SearchOrchestrator`: thêm bộ đếm cộng dồn `SkippedByRegionTotal` (public, tăng trong handler pageData);
     `SearchSession` expose ra.
   - `FileRunCoordinator` nhánh `Completed`: nếu `results.Count == 0 && session.SkippedByRegionTotal > 0` →
     KHÔNG `SetDone`, status "⚠ 0 SP sau lọc khu vực ({N} bị loại) — link giữ lại, kiểm tra ô Khu vực.";
     0 SP mà không có gì bị loại → `SetDone` như cũ (link rỗng thật).
4. **F4:** `LinkFileStore`
   - Khoá ghi theo file: `static ConcurrentDictionary<string, object>` key = full path (case-insensitive),
     `lock` quanh toàn bộ read-modify-write của `MarkStatus*` + `ClearAllStatuses*` (cả xlsx lẫn sidecar .txt —
     sửa luôn đường tmp sidecar đang dùng chung tên).
   - `MarkStatusXlsx`: khi `StatusColumn == 0` → resolve bằng logic Load (KHÔNG lấy `LastColumnUsed+1` mù).
   - Resolve cột trạng thái chịu được file ĐÃ bị đẻ nhiều cột: quét từ cột cuối lùi về, lấy DÃY liên tiếp các
     cột "status-like"; cột trạng thái = cột TRÁI NHẤT của dãy; đọc trạng thái 1 dòng = giá trị non-empty đầu
     tiên trong dãy; ghi thì ghi vào cột trái nhất (các cột thừa để nguyên — tự lành dần).
   - `FileRunCoordinator.SetDone`: bỏ `catch { }` trắng → lỗi ra `LinkStatus` ("⚠ không đánh dấu được file link: …").
5. **F2:** `extensions/shopee-search/flow-category.js`
   - `startCategoryFromLink(msg)`: đọc `resumeIdx = Math.max(1, (msg.resumeCategoryIndex|0) || 1)`,
     `resumePage = Math.max(1, (msg.resumePage|0) || 1)`; `state.resumeCategoryIndex = resumeIdx`.
   - Vòng subs: `for (let i = resumeIdx - 1; ...)`; nếu `resumeIdx - 1 >= subs.length` → log cảnh báo + chạy từ 0
     (danh sách danh mục đã đổi). Truyền `startPage` cho `crawlPagesForCurrentState`: danh mục resume đầu tiên
     dùng `resumePage`, các danh mục sau dùng 1. Nhánh không có danh mục con (`:86`) cũng truyền `resumePage`.
   - Log rõ "⏯ Tiếp tục từ danh mục #{resumeIdx}, trang {resumePage}" khi resumeIdx > 1 || resumePage > 1.
   - KHÔNG sửa C# (SearchOrchestrator đã gửi đúng 2 field ở cả lượt đầu lẫn reconnect).
6. **Test mới:** project `suite/Shopee.Module.Search.Tests` (xunit, net8.0, ref `Shopee.Module.Search`), thêm
   vào `ShopeeSuite.sln`:
   - `SaveShopProducts` ghi `shop_id = p.ShopId` và `category_id` = tham số (DB SQLite file tạm).
   - `GetAllShopProducts` trả về đúng ShopId sau save (link SP dựng đúng).
   - `LinkFileStore` xlsx: đánh dấu 3 link → đúng MỘT cột trạng thái; reload đọc đủ 3 Processed. File "bẩn"
     dựng sẵn nhiều cột trạng thái → đọc merge đúng.
   - `LinkFileStore` song song: 2 luồng × 10 mark (xlsx + sidecar) → không exception, đủ 20 dấu.
   - **Luật thử phá:** mỗi test mới phải được chứng minh ĐỎ bằng cách hoàn nguyên tạm đúng dòng fix (ghi lại
     trong báo cáo đã thử phá test nào bằng cách nào), rồi khôi phục.

### Đợt 2 — Scrape (S1..S3)

7. **S1 — skip-ledger:** nguyên tắc: **mọi dòng trong vùng phủ phải hoặc cào OK hoặc nằm trong danh sách bỏ qua
   CÓ GHI NHẬN.**
   - `ScrapeProgress` thêm `List<int> SkippedRows` (persist JSON; `Clone` chép; `BeginFresh` xoá; `BeginResume`
     giữ). `ScrapeProgressStore.MarkSkipped(accountId, sheet, row)`: thêm row vào `SkippedRows` (dedup) +
     merge `[row..row]` vào `Completed` (vùng phủ) + Save/Changed.
   - `InstanceConfig`: thêm ghi nhận dòng fail thread-safe (vd `AddFailedRow(int)` + `SnapshotFailedRows()`).
     `LauncherRunnerLoop.cs:272-275` (nhánh else `scrapeOk=false`): gọi `config.AddFailedRow(rowNumber)` —
     giữ log hiện có.
   - `ScrapeRunner`: thêm event `RowSkipped(int row, string reason)`. Nguồn bắn: (a) cuối chunk + trong
     `ExtensionProgressSynced`, diff `SnapshotFailedRows()` với tập đã báo → bắn từng dòng mới, reason từ log
     extension; (b) đường "BỎ QUA dòng kẹt" (`:291-298`) bắn `RowSkipped(nextFrom, "kẹt N lần")`.
   - `ScrapeViewModel` (chỗ `:384-389`): wire `runner.RowSkipped += (row, reason) => { MarkSkipped;
     Coordination.Hub.PublishProgress(coordKey, row, row); LogA("⚠ BỎ QUA dòng {row}: {reason} — SP dòng này
     KHÔNG được cào; muốn cào lại hãy Chạy (reset)."); }` — publish để Hub không giao lại vòng vô tận.
   - Hiển thị: `ScrapeTargetViewModel.RefreshProgress` + dòng sheet ở `ScrapeStatsViewModel`: có skip → thêm
     "· bỏ {n} dòng" vào text tiến độ. FinishRun giữ nguyên (vùng phủ đủ → Completed; log tổng kết của
     `ScrapeViewModel` `:399-403` thêm số dòng bỏ nếu > 0).
8. **S2 — chặn tiến độ rác:**
   - `LastDoneOf` (`ScrapeRunner.cs:356-361`): `last > to` → trả `from - 1` (rác ngoài khối = coi như chưa làm
     gì). Tách phần toán thành hàm PURE test được (vd `ScrapeChunkMath.ClampLastDone(int? last, int from, int to)`
     đặt ở `Shopee.Core` cạnh `RowRangeMath`, hoặc internal + `InternalsVisibleTo` — miễn test gọi thẳng được).
   - `InstanceConfig.ApplyExtensionProgress`: chỉ nhận `LastCompletedRow`/`CurrentRow` khi state khớp khối hiện
     tại: `state.SheetName` (nếu có) trùng `DataSheet` (OrdinalIgnoreCase) VÀ giá trị nằm trong
     `[StartRow-1 .. EndRow]` (khi 2 mốc đã set). Không khớp → bỏ qua field tiến độ (giữ các field khác), log 1 dòng.
   - `extensions/shopee-scrape/background.js` `__launcherApplyFormConfig`: nếu (sheetName, startRow, endRow)
     nhận vào KHÁC bộ đang lưu trong `runnerState` → reset `lastCompletedRow/currentRow/stoppedAtRow` (khối
     mới); trùng cả 3 → giữ nguyên (watchdog relaunch cùng khối vẫn resume được — đường
     `SuggestedResumeRow` đang dựa vào đó, KHÔNG được phá).
9. **S3 — Xoá tiến độ phải xoá cả ledger Hub:**
   - Helper dùng chung (vd `ScrapeProgressReset.Reset(accountId, sheet)` đặt trong Suite): `Clear` local + tìm
     shop theo sheet trong `BigSellerStore.Shared` (account.Shops, `ShopeeDataSheet` OrdinalIgnoreCase) → có
     shop + `CoordinationRuntime.Hub != null` → FireAndForget `SetLedgerStatusAsync(new CoordKey(accId,
     shop.Id, sheet, CoordOp.Scrape), LedgerStatus.Idle)` (y đường Reset `ScrapeViewModel.cs:287-295`, kể cả
     message lỗi). Không tìm thấy shop → chỉ clear local + log.
   - `ScrapeStatsViewModel.ClearProgress` gọi helper. Đường Reset trong `ScrapeViewModel` cũng chuyển sang
     helper cho khỏi 2 dị bản.
10. **Test mới (Shopee.Core.Tests):**
    - `ClampLastDone`: (5000, 2, 12) → 1; (null, 2, 12) → 1; (7, 2, 12) → 7; (12, 2, 12) → 12; (1, 5, 9) → 4.
    - `ScrapeProgressStore`: `MarkSkipped` → row vào `SkippedRows` + vùng phủ; `BeginFresh` xoá skip;
      `BeginResume` giữ; `FinishRun` ra Completed khi phần thiếu chỉ toàn dòng đã skip.
    - **Luật thử phá** như đợt 1 (bỏ guard `last > to` → test đỏ; bỏ merge trong MarkSkipped → test đỏ).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln -c Debug` — 0 error, **0 warning**.
- [ ] `dotnet test` cho `Shopee.Core.Tests`, `Shopee.Module.Search.Tests`, `XuLyDonShopee.Tests` — xanh 100%,
      và tổng số test TĂNG so với trước (ghi số cụ thể trước/sau).
- [ ] **F1:** trong `FileRunCoordinator`, nhánh `Cancelled` và `Error` đều persist khi `results.Count > 0`
      (đọc diff thấy rõ); nhánh OCE cũng persist best-effort. Không còn đường nào vứt `results` khi count > 0.
- [ ] **F2:** grep `resumeCategoryIndex` trong `extensions/shopee-search/flow-category.js` thấy đọc từ `msg`;
      vòng subs bắt đầu từ `resumeIdx - 1`; `crawlPagesForCurrentState` được truyền `startPage` ở CẢ 2 chỗ gọi.
      `ExtensionJsCuPhapTests` xanh (parse được).
- [ ] **F3:** test `SaveShopProducts` chứng minh `shop_id = p.ShopId`; mọi truy vấn trong `SearchTaskStore`
      không còn chỗ nào coi shop_id là cat id (nghiệm thu grep + đọc từng query đổi).
- [ ] **F4:** test 3-link → 1 cột; test file bẩn nhiều cột đọc đúng; test song song đủ dấu.
- [ ] **F5:** `extract.js` Try 2 có `location`; coordinator không `SetDone` khi 0 SP + có skip khu vực (đọc diff).
- [ ] **S1:** test `MarkSkipped`; đọc diff xác nhận: nhánh else `LauncherRunnerLoop` ghi failed row, stall-skip
      bắn `RowSkipped`, ViewModel publish lên Hub + log cảnh báo rõ.
- [ ] **S2:** test `ClampLastDone` (ca 5000/2/12 → 1); `ApplyExtensionProgress` có guard sheet + khoảng;
      `__launcherApplyFormConfig` reset tiến độ khi đổi khối và GIỮ khi trùng khối (đọc diff).
- [ ] **S3:** `ScrapeStatsViewModel.ClearProgress` và đường Reset của `ScrapeViewModel` cùng đi qua MỘT helper
      có bước `SetLedgerStatusAsync(Idle)`.
- [ ] Báo cáo thực thi ghi rõ: từng test mới đã THỬ PHÁ bằng cách nào và đỏ ra sao.
- [ ] KHÔNG commit; không sửa file ngoài phạm vi (đặc biệt không đụng `Shopee.Hub.Web`, `orders/`).

## 5. Rủi ro & lưu ý

- **F3 là đổi ngữ nghĩa cột dữ liệu** — rủi ro lớn nhất của plan. Truy vấn thống kê danh mục hiện tại nhóm theo
  shop_id-mang-nghĩa-cat-id; đổi mà không sửa hết query là tab Danh mục trống. Phải grep mọi chỗ đọc
  `shop_products` trong toàn repo (cả `SearchViewModel`/`SearchRunner`) trước khi chốt.
- **S2 phần extension:** đừng reset tiến độ vô điều kiện trong `__launcherApplyFormConfig` — watchdog relaunch
  cùng khối dựa vào `lastCompletedRow` để resume (`BraveInstanceSession.Progress.cs` `SuggestedResumeRow`).
  Chỉ reset khi bộ (sheet, startRow, endRow) ĐỔI.
- **S1:** `ExtensionProgressSynced` bắn từ luồng ngoài UI — diff tập failed rows phải thread-safe; event
  `RowSkipped` bắn ngoài lock. `MarkSkipped` idempotent (dedup row) vì diff có thể bắn trùng khi retry chunk.
- **Skip row + Hub:** publish `[row..row]` lên Hub là QUYẾT ĐỊNH có chủ đích (để Hub không giao lại vòng vô
  tận); user muốn cào lại dòng bỏ qua thì bấm Chạy (reset) — log phải nói rõ điều đó.
- JS không có unit test hành vi — chỉ có parse test. Bù bằng nghiệm thu đọc diff + phản biện.
- File `.xlsx` link có thể đang bị user mở trong Excel khi MarkStatus — lock chỉ chống race NỘI process, không
  chống file bị khoá ngoài; lỗi phải ra `LinkStatus` (đã có trong bước F4), không nuốt.

---

## Báo cáo thực thi

### Thực thi (opus-dev, 2 đợt tuần tự — app đang chạy khoá bin cây chính nên mọi số đo dùng git worktree tách)

- **Đợt 1 (Search F1–F5):** đúng plan, 3 điểm lệch có chủ đích: (a) persist SAU MỌI outcome (kể cả captcha/
  reconnect/đổi account) vì `PrepareSearch` xoá `session.Results` mỗi lượt — chỉ persist 3 nhánh như plan vẫn
  còn đường vứt kết quả; (b) `ClearAllStatusesXlsx` xoá cả DÃY cột (hệ quả của luật đọc gộp); (c) extract.js
  thêm cả `rating`/`image` cho khớp schema các nhánh khác. Rủi ro số 1 của plan (query danh mục dựa
  shop_id-mang-nghĩa-cat) hoá ra không hiện hữu — mọi query nhóm theo cột `category` TEXT.
- **Đợt 2 (Scrape S1–S3):** đúng plan; `stoppedAtRow` không tồn tại trong extension nên chỉ reset 2 mốc thật;
  `DrainSkippedRows` chỉ báo dòng ≤ lastDone của chunk (dòng hỏng ở đuôi quay lại hàng vá, không báo oan).
- Test mới đợt 1+2: 21 (Search.Tests 9 + Core 12), 11 lượt thử phá đều đỏ.

### Nghiệm thu (chấm theo mục 4)

11/12 ĐẠT, 1 ĐẠT MỘT PHẦN (mục Báo cáo thực thi lúc đó chưa điền — chính là mục này). Nghiệm thu tự build
(`--no-incremental`, 0 warning), tự chạy test (1933/1933, baseline tự dựng 1912), tự thử phá 4 lượt độc lập —
đều đỏ đúng chỗ. Lưu ý quy trình nghiệm thu để lại: khôi phục file khi thử phá phải `touch` lại, không thì
MSBuild giữ DLL hỏng và "xanh trở lại" là giả.

### Phản biện vòng 1 → phiên chính vá thêm 7 điểm

Phản biện (worktree riêng, có viết test đối kháng chạy trên cả cây TRƯỚC vá để phân biệt hồi quy) tìm ra
4 NẶNG + 8 VỪA. Phiên chính xác nhận và vá trong cùng đợt:

1. **PB-1 (hồi quy F2):** reconnect gửi `resumePage` CŨ kèm idx MỚI → nhảy cóc bỏ trắng trang đầu danh mục.
   Vá: `HandlePageData` cập nhật CẢ CẶP (idx, page) cùng nhịp.
2. **PB-2/5/6 (hồi quy F4):** `IsStatusColumn` nhận tiền tố "Lỗi" → cột GHI CHÚ user bị nhận nhầm là cột trạng
   thái (ghi đè / xoá lan / che dấu Processed). Vá: chỉ nhận Processing/Processed + "Trạng thái" ở hàng 1.
3. **PB-3/9:** Excel per-link ghi từ results MỘT lượt → lượt Dừng 120 SP THAY file 3000 dòng hôm qua. Vá: xuất
   từ CSDL gộp theo `source_link` (method mới `GetShopProductsBySourceLink`, dedup itemId giữ bản mới nhất).
4. **PB-7:** dòng cũ (shop_id = cat id) + dòng mới cùng itemId → tab Danh mục đếm đôi. Vá: `SaveShopProducts`
   DELETE bản cũ khác shop_id cùng itemId trong cùng transaction.
5. **PB-8:** `shop_name` rỗng vĩnh viễn (extension không trả tên shop). Vá: reader fallback
   shop_name → category_label (đúng nội dung cột này hiển thị trước giờ).
6. **PB-12:** 2 shop trùng sheet → "Xoá tiến độ" chỉ Idle ledger shop đầu. Vá: Idle MỌI shop khớp sheet.
7. **PB-10:** docstring bất biến SkippedRows nói quá (dòng bị FetchLinks lọc không vào sổ) — đã chữa câu chữ.

Test thêm cho các vá này: +6 (LinkFileStoreTests +3, SearchTaskStoreShopIdTests +3), thử phá 3 lượt
(nới IsStatusColumn → 3 đỏ; bỏ DELETE / LIMIT 1 / bỏ fallback → đỏ đúng 3 test tương ứng).

### Phản biện vòng 2 (soi lại 7 điểm vá) → phiên chính vá thêm 3 điểm

Vòng 2 xác nhận 7/7 vá trúng gốc (chạy lại 5 test đối kháng vòng 1 trên bản vá → xanh), tìm thêm:

1. **Hồi quy MỚI do chính vòng vá:** khoá header "Trạng thái" vào hàng 1 làm file user có dòng tiêu đề bảng ở
   hàng 1 + tên cột ở hàng 2 MẤT SẠCH dấu Processed (phản biện chạy ca này trên cả 3 bản: base PASS, vá vòng 1
   PASS, vá vòng 2 FAIL). Vá: nhận "Trạng thái" ở mọi hàng, chỉ bỏ tiền tố "Lỗi".
2. **Hiệu năng thật:** DELETE dọn bản cũ theo item_id không có index → quét toàn bảng MỖI sản phẩm (phản biện
   đo: lưu 2000 SP vào bảng 20k dòng chậm ~60×, giữ khoá ghi SQLite, làm nút Dừng ì). Vá: thêm
   `ix_shop_products_item_id` trong Initialize (cả CSDL mới lẫn cũ nâng cấp).
3. Thông báo "✔ Xong (N)" là số của LƯỢT CUỐI còn file gộp nhiều hơn → vênh. Vá: PersistResultsAsync trả số
   dòng file gộp, thông báo kèm "(file gộp N SP)" khi lệch.

Kèm 2 test mới (header hàng 2; index tồn tại ở CSDL mới + cũ), thử phá cả hai → đỏ đúng test.

Vòng 2 cũng xác nhận: thứ tự cặp (idx, page) trong pageData an toàn (cùng một object, cùng một send); reconnect
giữa 2 danh mục chỉ lệch về phía cào THỪA, không bỏ trắng; DELETE theo item_id không xoá nhầm (orchestrator
chặn ShopId ≤ 0, itemId là định danh toàn cục toàn codebase).

### Giới hạn ghi nhận, KHÔNG sửa đợt này (quyết định có chủ đích)

- **PB-4 — RỦI RO CÒN MỞ (không chỉ là "ngoài phạm vi"):** sổ dòng bỏ qua chỉ sống local, nhưng dòng bỏ qua
  VẪN publish lên Hub như "đã xong" → máy KHÁC fold về sẽ báo "✔ Hoàn thành toàn bộ" không kèm "bỏ n dòng" —
  tức cross-machine vẫn "báo thành công khi thiếu", đúng lớp lỗi plan này nhắm. Hai chốt thuần client đều có
  giá: không-publish thì Hub giao lại dòng hỏng vòng vô tận; FinishRun không-Completed thì resume rơi vào ngõ
  cụt "không còn dòng để chạy". Chốt đúng là mở rộng ledger server (`Shopee.Hub.Web`) thêm mảng `skipped` +
  fold ngược — plan cấm đụng Hub đợt này → ghi thành việc kế tiếp, ưu tiên cao.
- **Excel per-link giờ "không mất" thay vì "tươi":** file gộp mọi lượt nên SP đã gỡ bán vẫn nằm lại trong file
  (muốn làm tươi phải "Xóa dữ liệu"). Đánh đổi có chủ đích — ghi CHANGELOG. Hai link khác cat.id nhưng trùng
  slug vẫn ghi đè cùng tên file (lỗi V3 cũ, thuộc nhóm VỪA đợt sau — hướng sửa: thêm cat.id vào tên file).
- **PB-11:** dòng kẹt 3 lần bị đóng dấu bỏ qua vĩnh viễn (kể cả nguyên nhân tạm thời) — muốn cào lại phải Chạy
  (reset) cả sheet. Đánh đổi đã chốt trong plan để diệt vòng resume vô tận; ý tưởng sau: nút "Cào lại các dòng
  đã bỏ".
- Dữ liệu `shop_products` cũ sai shop_id chỉ được thay dần khi SP được cào lại (DELETE-theo-itemId); dòng chưa
  cào lại vẫn mang cat id → ghi CHANGELOG khi phát hành.
- `extract.js` Try 4 (script_tag) vẫn thiếu `location` — lưới C# (0 SP + có loại → giữ link) che được; sửa
  nguồn để đợt sau.
- NHẸ còn để lại: `SheetNameRx` hẹp (sheet có `-`/`.` → guard S2 từ chối oan ở nhánh regex-fallback — hướng an
  toàn: chỉ cào lại thừa); thư mục `test-data` trong bin của Core.Tests không tự dọn; quét cột trạng thái
  O(cột×dòng) mỗi lần đánh dấu.

### Số kiểm chứng cuối (worktree tách `wt-verify`, cây chính bị app đang chạy khoá bin)

- `dotnet build ShopeeSuite.sln -c Debug`: **0 error, 0 warning**.
- Test: **Shopee.Core.Tests 151/151 · Shopee.Module.Search.Tests 17/17 · XuLyDonShopee.Tests 1773/1773**
  (tổng 1941; trước plan: 1912 — +29 test mới). XuLyDonShopee chạy trên diff trước 2 vá cosmetic cuối
  (orders/ không tham chiếu suite/ nên không ảnh hưởng).
- Tổng lượt thử phá: 11 (opus-dev) + 4 (nghiệm thu) + 5 (phiên chính, gồm 2 lượt vòng 2) — tất cả đỏ đúng test.
- Lưu ý kỹ thuật cho người sau: thử phá bằng PowerShell 5.1 phải dùng `[System.IO.File]::ReadAllText/WriteAllText`
  với UTF8 — `Get-Content`/`Set-Content` mặc định phá encoding tiếng Việt; khôi phục xong phải touch file,
  không thì MSBuild giữ DLL hỏng và "xanh trở lại" là giả.
