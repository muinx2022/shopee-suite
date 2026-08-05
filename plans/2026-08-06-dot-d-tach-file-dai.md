# Plan: Đợt D — Tách 3 file C# dài (pure move)

- **Ngày:** 2026-08-06
- **Trạng thái:** chờ làm (sau đợt C)
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

<chưa có>
