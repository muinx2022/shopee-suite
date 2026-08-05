# Plan: Đợt A — Vá lỗi thật từ đợt rà soát toàn repo 05/08

- **Ngày:** 2026-08-06
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Đợt rà soát toàn repo 05/08 (13 agent, mọi phát hiện đều được phản biện độc lập xác nhận) tìm ra một số **lỗi đang chạy sai thật** — không phải code thừa. Đợt A vá đúng các lỗi này + vài mục hygiene nhỏ, KHÔNG dọn dead-code, KHÔNG refactor (các đợt sau lo).

Mọi phát hiện dưới đây đã được kiểm chứng bằng grep/hex-dump; số dòng là tại commit hiện tại (`09bb9e8`). Nếu số dòng lệch nhẹ thì tự dò theo tên symbol.

## 2. Phạm vi

- **Làm:** 11 mục ở phần 3.
- **Không làm:** xóa dead-code, hợp nhất trùng lặp, tách file, sửa UI, thêm tính năng. Không sửa file nào ngoài danh sách (trừ khi bắt buộc để build xanh — ghi rõ vào báo cáo). Không đổi hành vi nào ngoài mô tả.

## 3. Các bước thực hiện

### A1. Search: lọc khu vực loại nhầm tỉnh có chữ Đ (bug 1 ký tự)
`suite/Shopee.Module.Search/Engine/SearchOrchestrator.cs` — hàm `NormalizeRegionText`, dòng ~336 có `.Replace("d", "d")` (cả hai tham số đều là ASCII 'd' 0x64 — no-op; đã hex-dump xác nhận). Ý đồ là thay **'đ' (U+0111) → 'd'** vì 'đ' không phân rã qua NFD nên bước bỏ dấu không xử lý được. Sửa thành `"đ" → "d"` (và đảm bảo 'Đ' U+0110 cũng về 'd' — xem thứ tự lower-case trong hàm; bản đúng tham khảo: `BigSellerSaveSuccessHelper.cs:176` của UpdateProduct).
- Hệ quả mong muốn: filter "da nang" khớp location "Đà Nẵng"; "dong nai" khớp "Đồng Nai".

### A2. Scrape: dòng lỗi tạm bị ghi nhận "đã xong" → mất dữ liệu im lặng
`suite/Shopee.Module.MultiBrave/Engine/LauncherRunnerLoop.cs` — dòng ~385–386 set `config.LastCompletedRow`/`NextRunRow` **vô điều kiện** sau bước video, kể cả khi `scrapeOk == false`. Trong khi dòng ~244–252 đã ghi nhận sớm CHỈ khi `scrapeOk`. Ca lỗi tạm không thuộc nhóm Aborted/ProxyError/Captcha/NeedLogin/"No SW" (vd "Tab scrape tạm mất kết nối — giữ tab để thử lại" `RunnerExtensionRpc.cs:104`, "Extension không phản hồi." dòng 84) rơi vào nhánh else (~268–271) rồi vẫn tới 385 → bị đánh dấu đã xong, resume + cơ chế vá + stall-retry (`MaxStallRetries` chỉ kích hoạt khi lastDone KHÔNG tiến) đều bỏ qua dòng đó vĩnh viễn.
- Sửa: chỉ set LastCompletedRow/NextRunRow ở 385–386 khi `scrapeOk` (đọc kỹ ngữ cảnh: đừng phá đường ghi-sớm 244–252 và các nhánh terminal). Đọc cả `ScrapeRunner` phía tiêu thụ (`LastDoneOf`) để chắc dòng lỗi giờ được thử lại đúng cơ chế stall.
- Ghi vào báo cáo: giải thích vì sao 385–386 tồn tại (có phải chỉ để cover đường không-ghi-sớm?) và vì sao sửa không phá ca thành công.

### A3. Orders: đẩy bù GSheet ghi cột Shop = email subaccount
Chuỗi lỗi: `HubOutboxWorker.cs:213–216` gọi `PushOrdersToGsheetAsync(accountId, shopId: null, shopLogin: null)` → `HubOutbox.cs:~290` `tenShop` rơi về `Accounts.GetById(accountId)?.Email` và dòng ~407 gán `TenShop` đó CHUNG cho mọi row, dù bảng `orders` có cột `shop_login` per-đơn (`Database.cs:259`) — `GetForGsheetPush` không SELECT nó và `GsheetPendingOrder` không có field.
- Sửa: thêm `shop_login` vào SELECT của `GetForGsheetPush` + field `ShopLogin` vào `GsheetPendingOrder`; trong `PushOrdersToGsheetAsync` tính tên shop **theo từng đơn**: ưu tiên `ShopLogin` của đơn, fallback logic hiện tại. Đường đẩy chính theo shop (shopLogin truyền vào) giữ nguyên hành vi.
- **Viết test mới** trong `orders/XuLyDonShopee.Tests`: đơn có `shop_login` khác nhau trong cùng account, worker-path (shopLogin=null) phải ra tên shop đúng từng dòng, không phải email. Theo luật dự án: viết xong test phải **thử phá** (tạm revert logic per-row → test phải đỏ) rồi khôi phục.

### A4. MultiBrave: PortAllocator làm teo pool vĩnh viễn
`suite/Shopee.Module.MultiBrave/Engine/PortAllocator.cs:39–47` — port dequeue ra mà `IsPortFree()==false` thì `continue` bỏ luôn (không re-enqueue, không vào `_leased`); `Release` chỉ trả port có trong `_leased` → port bận tạm (TIME_WAIT sau khi đóng Brave) mất hẳn khỏi pool 600.
- Sửa: port bận đưa vào danh sách tạm, enqueue lại sau vòng; giới hạn vòng quét = số phần tử ban đầu của queue để không lặp vô hạn khi mọi port đều bận (khi đó vẫn ném lỗi "hết port" như cũ).
- `suite/Shopee.Module.UpdateProduct/Engine/PortAllocator.cs` là bản chép gần y hệt — **kiểm tra và sửa cùng lỗi nếu có** (đợt C mới hợp nhất, đợt này chỉ vá).

### A5. .gitignore thiếu — nguy cơ commit key + cookie lên GitHub public
`.gitignore` chưa ignore `server/Shopee.Hub.Web/hub-data/` (chứa `dp-keys/` — key Data Protection XML, và `files/` — kho cookie client sync) và `test-results/` ở gốc. Đã tái lập: `git check-ignore` trả rc=1 cho cả 3 đường dẫn.
- Thêm rule cho 2 thư mục trên. Xác nhận `git ls-files` không có file nào thuộc 2 thư mục này đang bị track (nếu có thì dừng, báo lại — KHÔNG tự `git rm`).

### A6. Suite/Search: ComboBox lọc Danh mục tab Xuất Excel
`suite/Shopee.Suite/Modules/Search/SearchViewModel.cs` — `Categories` chỉ được nạp từ `_all` (dữ liệu phiên, `RefreshCategories()`), không nạp từ DB khi mở màn, và `ClearDataAsync` (~513–523) không reset. Trong khi `ExportAll` xuất từ DB (dòng ~494) — DB có sẵn hàm đọc danh mục (`Db.GetCategoryRows` hoặc tương đương — tự dò trong module).
- Sửa: khi khởi tạo VM (hoặc lần đầu mở tab Xuất) nạp danh mục distinct từ DB vào `Categories`; `ClearDataAsync` reset `Categories` về `{"(Tất cả)"}` + nạp lại từ DB sau khi xóa.

### A7. Hub: byte NUL thô làm cả file tàng hình khỏi grep
`server/Shopee.Hub.Web/Services/RewriteJobService.cs` dòng 66 (offset 3852) — chuỗi phân tách key chứa **ký tự NUL literal** thay vì escape. Đổi thành `"\0"` (escape trong string C#). Hành vi runtime giữ nguyên byte-đúng.
- Kiểm chứng: script python đếm byte 0x00 trong file = 0; `rg RewriteTitlesAsync server/` giờ PHẢI liệt kê được file này.

### A8. Mojibake trong chuỗi báo lỗi
`suite/Shopee.Module.UpdateProduct/Engine/ProductNameRewriteRunner.cs:433` — `"Không tìm th?y sheet"` (byte 0x3F thật trong source) → `"Không tìm thấy sheet"`. Sau đó quét nhanh toàn repo tìm mojibake tương tự trong chuỗi tiếng Việt (pattern chữ-thường + `?` + chữ-thường trong string literal .cs/.razor) — sửa nốt nếu tìm thấy, liệt kê vào báo cáo.

### A9. Search: AddResultIfNew O(n²)
`suite/Shopee.Module.Search/Engine/SearchOrchestrator.cs` — `_results` là `List<ProductResult>`, `AddResultIfNew` (~237) quét `FirstOrDefault` theo `(ItemId, ShopId)` cho MỖI item trong `HandlePageData`, chạy trên handler message WS.
- Sửa tối thiểu: thêm `HashSet<(long, long)>` (hoặc `Dictionary`) làm index cạnh List (giữ List nếu nơi khác cần thứ tự — tự kiểm tra các chỗ đọc `_results`). Nhớ đồng bộ index ở mọi chỗ mutate `_results` (kể cả clear/reset nếu có).

### A10. orders/CLAUDE.md chỉ sai stack
`orders/CLAUDE.md` khẳng định App là "Avalonia 11.3 (KHÔNG phải WPF)" — sai từ khi port WPF xong (31/07). Sửa mô tả stack: WPF `net8.0-windows` + CommunityToolkit.Mvvm, build ra DLL cho shell (đối chiếu `XuLyDonShopee.App.csproj`). Đọc lướt phần còn lại của file, sửa luôn câu nào còn nhắc Avalonia như hiện trạng.

### A11. TOI-UU-HOA-APP-KHI-CHAY.md + suite/README.md còn hướng dẫn quy trình trước Velopack
`TOI-UU-HOA-APP-KHI-CHAY.md` (dòng 13, 16, 22–26, 46) hướng dẫn máy mới tự build bằng `publish-suite.cmd` và chạy từ `publish\ShopeeSuite\` — mâu thuẫn CLAUDE.md mục Deploy (phát hành Velopack + GitHub Releases). Máy cài theo doc này không bao giờ nhận delta/lệnh update từ Hub.
- Viết lại mục TL;DR + mục cài đặt: cài qua `ShopeeSuite-win-Setup.exe` từ GitHub Releases, update qua nút "Cập nhật & khởi động lại"; Defender exclusion đổi sang đường dẫn cài `%LocalAppData%` (tự xác định đường dẫn Velopack thật). GIỮ phần Defender exclusions tổng quát + ngân sách cửa sổ Brave (vẫn đúng).
- `suite/README.md:14` còn chỉ `publish-suite.cmd` làm đường chạy chính — sửa tương ứng (publish-suite.cmd vẫn giữ cho dev local, ghi rõ vai trò).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` 0 error, 0 warning mới.
- [ ] `dotnet build server/ShopeeHub.sln` 0 error, 0 warning mới (LƯU Ý: sln chính KHÔNG chứa server — phải build cả hai).
- [ ] `dotnet test` xanh cả 3 project test: `orders/XuLyDonShopee.Tests`, `suite/Shopee.Core.Tests`, `server/Shopee.Hub.Web.Tests`.
- [ ] Test mới A3 tồn tại, xanh, và ĐÃ chứng minh phá-được (báo cáo ghi lại lần chạy đỏ khi revert logic).
- [ ] A1: grep hex xác nhận `Replace` dùng U+0111; không còn `.Replace("d", "d")`.
- [ ] A5: `git check-ignore server/Shopee.Hub.Web/hub-data/dp-keys/x.xml test-results/x.trx` đều match; `git ls-files` không track file nào trong 2 thư mục đó.
- [ ] A7: file RewriteJobService.cs 0 byte NUL; `rg -l RewriteTitlesAsync server/Shopee.Hub.Web/Services/` liệt kê được file.
- [ ] A2/A4: báo cáo giải thích rõ vì sao sửa không phá ca thành công (scrapeOk=true) và không gây lặp vô hạn (allocator).
- [ ] `git status` không có file lạ ngoài phạm vi.

## 5. Rủi ro & lưu ý

- **A2 là khu resume nhạy cảm** (memory dự án có nhiều vết sẹo quanh resume/claim): đọc trọn `LauncherRunnerLoop.RunAsync` + `ScrapeRunner.LastDoneOf`/cơ chế vá trước khi sửa; nếu phát hiện thiết kế chủ đích "lỗi thì bỏ qua dòng" thì DỪNG mục này, ghi lại bằng chứng và báo — đừng đoán.
- **A3**: chỉ sửa đường worker (tham số null); đường đẩy theo shop đang chạy đúng — đừng đổi chữ ký công khai nếu không cần.
- **A9**: `_results` có thể được đọc từ nhiều thread (WS handler) — giữ nguyên kỷ luật lock hiện có của class.
- Repo có file từng chứa NUL (A7) — grep có thể sót file đó; khi quét mojibake A8 dùng python cho chắc.
- KHÔNG commit — phiên chính sẽ tự đối chiếu diff, chạy lại kiểm chứng rồi commit.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
