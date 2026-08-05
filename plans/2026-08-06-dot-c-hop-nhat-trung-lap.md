# Plan: Đợt C — Hợp nhất trùng lặp còn sót

- **Ngày:** 2026-08-06
- **Trạng thái:** chờ làm (sau đợt B)
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

<chưa có>
