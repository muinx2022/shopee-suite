# Plan: Đợt A — Vá lỗi thật từ đợt rà soát toàn repo 05/08

- **Ngày:** 2026-08-06
- **Trạng thái:** hoàn thành
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

**Kết luận: 11/11 mục ĐÃ LÀM, không mục nào phải dừng.** Không commit (đúng yêu cầu). 14 file sửa + 1 file test mới.

### A1 — Search lọc khu vực (`SearchOrchestrator.NormalizeRegionText`)
- `suite/Shopee.Module.Search/Engine/SearchOrchestrator.cs:344`: `.Replace("d", "d")` → `.Replace("đ", "d")`.
- Hex-dump trước khi sửa xác nhận cả 2 tham số đều là `0x64`; sau khi sửa, quét codepoint của dòng cho `['0x111']` = U+0111.
- **Không cần xử lý riêng 'Đ' (U+0110)**: dòng 326 đã `ToLowerInvariant()` TRƯỚC khi tới chuỗi `Replace`, mà
  U+0110 hạ chữ thành U+0111. Thêm `.Replace("Đ","d")` sẽ là code chết → không thêm.

### A2 — Scrape mất dòng im lặng (`LauncherRunnerLoop`)
- `suite/Shopee.Module.MultiBrave/Engine/LauncherRunnerLoop.cs`: bọc `LastCompletedRow`/`NextRunRow` ở cuối vòng
  lặp trong `if (scrapeOk)`. Không đụng đường ghi-sớm 251–252 và các nhánh terminal.

**Đây KHÔNG phải thiết kế chủ đích — bằng chứng:**
1. `ScrapeRunner.cs:173-175` ghi rõ ý đồ ngược lại: *"3 lần liên tiếp KHÔNG tiến được dòng nào… → coi dòng đó
   hỏng, BỎ QUA. 1-2 lần có thể do captcha/proxy nhất thời nên vẫn retry."* Với code cũ, dòng lỗi tạm được
   **bỏ qua ngay lần 1, không retry lần nào** — `MaxStallRetries` chỉ kích hoạt khi `lastDone` KHÔNG tiến, mà
   dòng 385 luôn đẩy nó tiến. Hai thiết kế mâu thuẫn nhau, cái có comment giải thích là cái đúng.
2. `RunnerExtensionRpc.cs:76` gọi thẳng việc bỏ qua dòng là hỏng: *"trước đây bỏ qua dòng (mất dữ liệu), giờ tự
   khôi phục + chạy lại"*.
3. Đường ghi-sớm 244–252 có comment dài giải thích vì sao ghi sớm; dòng 385–386 **không có comment nào**.
4. Git history không phân giải được (cả hai đã có từ commit đầu `7d964c8` — repo bị squash lúc import).

**Vì sao 385–386 tồn tại:** với `scrapeOk=true` nó **trùng lặp hoàn toàn** với 251–252 (cùng `rowNumber`, cùng
`rowNumber+1`, không có nhánh nào đặt scrapeOk=true mà bỏ qua 251). Đã kiểm chứng không có nguồn nào khác hạ
`LastCompletedRow` giữa 2 điểm: `InstanceConfig.ApplyExtensionProgress` chỉ nhận state do CHÍNH launcher đẩy
xuống extension qua `PushDisplayStateAsync` (round-trip), và nó chỉ gán khi `> 0` nên không bao giờ hạ.
⇒ **ca thành công không đổi hành vi**, ca lỗi mới thôi bị đánh dấu xong.

**Tác dụng thật + giới hạn (cần biết khi nghiệm thu):** fix cứu trọn ca "cả chunk hỏng" (extension chết → mọi
dòng fail → trước đây cả khối bị đánh dấu xong và mất sạch; giờ `lastDone` đứng yên → `AddPatch(from,to)` chạy
lại, kẹt 3 lần thì bỏ 1 dòng). NHƯNG nếu dòng N lỗi mà dòng N+1 chạy được thì `LastCompletedRow` vẫn nhảy lên
N+1 (do 251–252), nên **dòng N lẻ giữa chunk vẫn mất** — vá triệt để cần theo dõi danh sách dòng lỗi rời rạc,
nằm NGOÀI phạm vi plan này. Đề xuất ghi vào backlog.

**Quan sát ngoài phạm vi (chưa sửa):** dòng ~388 vẫn đặt `LastRunnerMessage = "Xong dòng {N}"` kể cả khi dòng
lỗi. Không gây sai lệch (`Classify` đọc chuỗi này chỉ để dò captcha/proxy; ca lỗi tạm rơi vào nhánh
"dừng giữa chừng" và vẫn được vá đúng), nhưng gây khó đọc log.

### A3 — Cột "Shop" trên GSheet = email subaccount
- `orders/XuLyDonShopee.Core/Data/OrdersRepository.cs`: thêm `shop_login` vào **cuối** SELECT của
  `GetForGsheetPush` (index 20 — cố ý đặt cuối để không lệch mọi `reader.Get*(i)` sẵn có) + field
  `string? ShopLogin = null` vào **cuối** record `GsheetPendingOrder` (có giá trị mặc định ⇒ 2 call-site đang
  dùng named-arg, kể cả `AccountSessionCleanupTests.Make`, không phải sửa).
- `orders/XuLyDonShopee.App/Services/HubOutbox.cs`: đổi `tenShop` (tính 1 lần/lượt) thành `tenShopFallback`, và
  tính **theo từng đơn** ngay trước khi dựng row: `p.ShopLogin` ưu tiên, rỗng thì mới fallback.
- Chữ ký công khai `PushOrdersToGsheetAsync` **giữ nguyên**; đường đẩy theo shop không đổi hành vi (đơn đã lọc
  theo `shopId` nên `p.ShopLogin` chính là `shopLogin` truyền vào).

**Test mới: `orders/XuLyDonShopee.Tests/HubOutboxGsheetTenShopTests.cs` (3 ca).**
| Lần chạy | Lệnh | Kết quả |
|---|---|---|
| Sau khi viết xong (code ĐÚNG) | `dotnet test … --filter FullyQualifiedName~HubOutboxGsheetTenShopTests` | **Passed: 3, Failed: 0** |
| **THỬ PHÁ** — tạm revert `var tenShop = string.IsNullOrWhiteSpace(p.ShopLogin) ? tenShopFallback : p.ShopLogin;` → `var tenShop = tenShopFallback;` | cùng lệnh | **Failed: 1, Passed: 2** — đỏ đúng ca `DuongWorker_MoiDonLayTenShopCuaChinhNo_KhongPhaiEmail` |
| Khôi phục | cùng lệnh | **Passed: 3, Failed: 0** |
2 ca kia (fallback đơn cũ, hồi quy đường-theo-shop) xanh ở cả 2 lần — đúng như mong đợi, vì revert không đụng 2
nhánh đó.

### A4 — PortAllocator làm teo pool
- Sửa **cả hai bản**: `suite/Shopee.Module.MultiBrave/Engine/PortAllocator.cs` và
  `suite/Shopee.Module.UpdateProduct/Engine/PortAllocator.cs` (đã kiểm chứng: lỗi giống hệt từng dòng).
- Port bận gom vào `List<int> busy`, enqueue lại **ở `finally`** (nên cả đường `return` giữa vòng cũng trả port).
  Trả về **cuối** hàng đợi để port TIME_WAIT có thời gian thoát thay vì bị thử lại ngay.
- Tách `_leased.Contains(port)` ra khỏi nhánh này: port đang cho mượn vẫn bỏ (Release sẽ đưa lại vào pool) —
  nếu enqueue lại sẽ nhân đôi phần tử trong queue.
- **Không lặp vô hạn:** `checkedCount = queue.Count` chụp TRƯỚC vòng và chỉ giảm, còn port bận chỉ được enqueue
  ở `finally` (sau khi vòng đã kết thúc) ⇒ tối đa đúng 1 lượt quét; mọi port đều bận thì vẫn rơi xuống
  `throw "Khong con port"` như cũ, chỉ khác là pool không bị bào mòn.

### A5 — .gitignore
- Thêm `server/Shopee.Hub.Web/hub-data/` và `test-results/`.
- `git check-ignore -v`: cả 3 đường dẫn thử đều match (`.gitignore:38` và `.gitignore:41`).
- `git ls-files | grep …` → **rỗng**: không có file nào của 2 thư mục đang bị track ⇒ không phải `git rm`.

### A6 — ComboBox lọc Danh mục (tab Xuất Excel)
- `suite/Shopee.Suite/Modules/Search/SearchViewModel.cs`: thêm `ReloadCategoryFilterFromDb()`, gọi ở
  **constructor** (ngay sau `RefreshCategoryGrid()`) và ở cuối `ClearDataAsync`.
- Nguồn dữ liệu: `Db.GetCategoryRows()`. Đã kiểm chứng đây ĐÚNG là từ điển danh mục của `shop_products`:
  `SearchTaskStore.SaveShopProducts` gọi `UpsertCategories(products.Select(p => p.Category))`, và
  `ClearFileSearchHistory` xoá `DELETE FROM categories` cùng lúc với `shop_products` ⇒ sau khi "Xóa dữ liệu",
  nạp lại cho đúng `{(Tất cả)}`.
- Giữ lựa chọn đang có nếu danh mục còn tồn tại, ngược lại về "(Tất cả)" — gán ở CUỐI hàm nên ComboBox không
  kẹt null khi `ItemsSource` bị clear.
- Gom chuỗi `"(Tất cả)"` (3 chỗ trong cùng file) thành hằng `TatCaDanhMuc` — thay đổi thuần đổi tên, 0 hành vi.
- Bọc `try/catch` quanh lượt đọc DB: hỏng CSDL không được giết constructor VM.

### A7 — Byte NUL thô
- `server/Shopee.Hub.Web/Services/RewriteJobService.cs:66`: NUL literal → `"\0"`. Runtime giữ nguyên byte-đúng.
- Đếm byte `0x00` trong file: **1 → 0**. `rg -l RewriteTitlesAsync server/Shopee.Hub.Web/Services/` **giờ liệt
  kê được file** (trước đây rg coi là binary nên bỏ qua).
- ⚠ **Lưu ý khi đọc diff:** `git diff` hiển thị file này là `Bin 13438 -> 13439 bytes` chứ không phải diff text,
  vì BẢN CŨ (trong index) chứa NUL nên git phân loại binary. Sau khi commit sẽ trở lại text bình thường.

### A8 — Mojibake
- `suite/Shopee.Module.UpdateProduct/Engine/ProductNameRewriteRunner.cs:433`: `"Không tìm th?y sheet"` →
  `"Không tìm thấy sheet"`.
- Quét toàn repo bằng python (regex `chữ + ? + chữ`, 7 đuôi file, bỏ bin/obj/node_modules…): 70 kết quả nghi ngờ,
  soi tay thì **68 là dương tính giả** (query string URL: `?key=`, `app.css?v=39`, `?bsStatus=1`…). Tìm thêm
  **đúng 1 ca thật**, đã sửa: `suite/Shopee.Module.MultiBrave/Engine/SessionMonitor.cs:224` —
  `"nhung Brave v?n hi?n"` → `"nhưng Brave vẫn hiện"` (là **comment**, không phải string literal).
- Script cũng kiểm luôn: **không còn file nào chứa byte NUL**, và **không có file nào không phải UTF-8** trong
  toàn repo. Script để ở scratchpad, không đưa vào repo.

### A9 — `AddResultIfNew` O(n²)
- `suite/Shopee.Module.Search/Engine/SearchOrchestrator.cs`: thêm
  `Dictionary<(long ItemId, long ShopId), ProductResult> _resultIndex` cạnh `_results`.
- Giữ `List` vì `Results` phơi ra ngoài theo thứ tự gặp. Đồng bộ index ở **cả 2** chỗ mutate (`Clear` trong
  `PrepareSearch`, `Add` cuối `AddResultIfNew`) — đã grep xác nhận chỉ có đúng 2 chỗ.
- Index trỏ tới CHÍNH object trong List nên nhánh cập-nhật-tại-chỗ giữ nguyên ngữ nghĩa.
- **Về lock:** class này **không có kỷ luật lock nào** (grep `lock (` → 0 kết quả); `_results` chỉ bị mutate từ
  handler message WS. Đã giữ nguyên hiện trạng, không thêm lock (thêm sẽ là đổi thiết kế ngoài phạm vi).
  `ItemId`/`ShopId` không bao giờ bị sửa sau khi add nên key không bị mồ côi.

### A10 — `orders/CLAUDE.md` sai stack
- Sửa dòng App: **WPF (`net8.0-windows` + `UseWPF`)** thay cho "Avalonia 11.3 (KHÔNG phải WPF)"; nói rõ
  `Views/` gồm `*.xaml` + `Styles/` + `Controls/`.
- Sửa luôn 2 chỗ sai kèm theo: Tests cũng là `net8.0-windows` + `UseWPF` (không phải `net8.0`), và câu
  "`net8.0` cả ba" → nêu đúng từng project (Core `net8.0`, App + Tests `net8.0-windows`). Đối chiếu trực tiếp
  3 file `.csproj`.
- Thêm 1 dòng blockquote ghi lịch sử (từng là Avalonia, port xong đợt 6 — 31/07/2026) để lần sau không sửa ngược.

### A11 — 2 tài liệu còn quy trình trước Velopack
- `TOI-UU-HOA-APP-KHI-CHAY.md`: viết lại TL;DR (5 việc → 4 việc, bỏ bước "build bản mới") và toàn bộ mục 1
  ("Build / cập nhật bản chạy" → "Cài đặt / cập nhật bản chạy"): cài bằng `ShopeeSuite-win-Setup.exe` từ GitHub
  Releases, cập nhật bằng **Cài đặt → Hiệu năng → "Cập nhật & khởi động lại"**, kèm cảnh báo máy cài kiểu chép
  thư mục `publish\` sẽ không nhận delta/lệnh Hub.
- Defender exclusion: bỏ dòng trỏ `publish\ShopeeSuite`, thay bằng `$env:LOCALAPPDATA\ShopeeSuite`.
  **Đường dẫn cài đã xác định bằng thực tế trên máy này**, không đoán: `packId ShopeeSuite` trong
  `release-suite.cmd`, và `%LocalAppData%\ShopeeSuite` đang tồn tại với đúng bộ khung Velopack
  (`ShopeeSuite.exe` stub / `Update.exe` / `current/` / `packages/`). Tên file Setup lấy từ `Releases/`
  (`ShopeeSuite-win-Setup.exe`).
- Lệnh kiểm tra phiên bản đổi từ `LastWriteTime` (vô nghĩa với bản cài) sang `VersionInfo.ProductVersion`.
- **GIỮ NGUYÊN** mục Defender tổng quát + toàn bộ mục 3 (ngân sách cửa sổ Brave) + mục 4/5/6 như plan yêu cầu.
- Ghi chú `%AppData%\ShopeeSuite\persistent-data` "bền qua các lần build" → "bền qua các lần cập nhật".
- `suite/README.md`: mục "Deploy sang máy khác" → "Phát hành cho máy khác (Velopack + GitHub Releases)", ghi rõ
  `release-suite.cmd` là đường phát hành và `publish-suite.cmd` **chỉ để chạy thử cục bộ ở máy dev**.

---

## Kết quả 5 lệnh kiểm chứng (chạy thật, dán nguyên văn)

| # | Lệnh | Kết quả |
|---|---|---|
| 1 | `dotnet build ShopeeSuite.sln` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| 2 | `dotnet build server/ShopeeHub.sln` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| 3 | `dotnet test orders/XuLyDonShopee.Tests` | `Passed! - Failed: 0, Passed: 1506, Total: 1506` (xem mục "1 lần đỏ" dưới) |
| 4 | `dotnet test suite/Shopee.Core.Tests` | `Passed! - Failed: 0, Passed: 71, Total: 71` |
| 5 | `dotnet test server/Shopee.Hub.Web.Tests` | `Passed! - Failed: 0, Passed: 53, Total: 53` |

Tiêu chí khác:
- A1: `rg 'Replace\("d", "d"\)'` → exit 1 (hết match); dòng 344 quét codepoint ra `['0x111']`.
- A5: `git check-ignore -v` match cả 3; `git ls-files` không track file nào trong 2 thư mục.
- A7: byte NUL trong file = **0**; `rg -l RewriteTitlesAsync server/Shopee.Hub.Web/Services/` liệt kê được file.
- `git status`: đúng 14 file `M` + 1 file `??` (file test mới). Không có file lạ.

### ⚠ 1 lần chạy ĐỎ ở lệnh 3 — phải báo, không giấu
Lần chạy **đầu tiên** của `dotnet test orders/XuLyDonShopee.Tests` đỏ 1 ca:
`NotifyDonTraKhoMaTests.BadgeChoDay_DemCaMaTraHangConTon` → `Failed: 1, Passed: 1505`.
Sau đó **7 lượt chạy full suite liên tiếp đều xanh 1506/1506**, và chạy riêng ca đó cũng xanh.

Đã điều tra thay vì bỏ qua:
- **Chứng minh diff KHÔNG với tới được ca này:** test không insert đơn nào (chỉ `return_codes`).
  `HubOutboxWorker.DemTon` đếm bằng `Orders.CountForGsheetPush` (SQL COUNT riêng) chứ **không** qua
  `GetForGsheetPush`; `ton.SheetRows = 0` ⇒ `PushOrdersToGsheetAsync` **không bao giờ được gọi**. Hai thứ tôi
  sửa ở A3 nằm trọn trong 2 hàm đó.
- **Đã chạy cây SẠCH để đối chứng:** `git stash push -u` → build lại → chạy full suite **6 lượt trên bản gốc**,
  xanh 6/6 (1503 test). `git stash pop` khôi phục, và đã `diff` bản patch backup với `git diff` sau khi pop →
  **khớp 100%**, không mất thay đổi nào.
- **Cơ chế nghi ngờ (chưa tái hiện được):** `PushGate` là `static ConcurrentDictionary` **toàn tiến trình**, khoá
  `(accountId, kind)`. Mọi test dựng `TempDatabase` mới đều ra `accountId = 1`. Hai lớp test cùng gọi
  `MotLuotAsync` (`NotifyDonTraKhoMaTests` và `HubOutboxWorkerRoundTests`) đều chiếm
  `PushGate.TryEnter(1, PushKind.Gsheet)`; xUnit chạy các lớp **song song** ⇒ lớp thua sẽ nhận
  `ChayQuaGateAsync → null`, làm `Assert.False(await worker.MotLuotAsync(...))` hỏng. Đây là **đua sẵn có trong
  bộ test** (static state + accountId cứng = 1), không phải logic sản phẩm.
- Đã thử ép tái hiện bằng cách chạy riêng đúng 2 lớp đó 8 lượt → xanh 8/8, **không tái hiện được**. Nên đây là
  giả thuyết có cơ sở mã nguồn chứ chưa phải kết luận đã chứng minh.
- Lớp test mới của tôi **không chiếm gate** (gọi thẳng `HubOutbox.PushOrdersToGsheetAsync`, không qua worker),
  nhưng nó có làm đổi lịch chạy song song — nên không loại trừ việc nó khiến cuộc đua sẵn có dễ lộ hơn.

**Đề nghị người nghiệm thu tự chạy lệnh 3 vài lượt** để tự đánh giá; nếu thấy đỏ lại đúng ca này thì đó là việc
của một plan riêng (sửa test cho hết dùng chung state tĩnh), không phải của đợt A.

---

## Vướng mắc / bỏ dở
- **Không mục nào bị dừng.** A2 đã điều tra kỹ theo cảnh báo ở mục 5 và kết luận là lỗi thật (bằng chứng ở trên),
  nên đã sửa thay vì dừng.
- Giới hạn còn lại của A2 (dòng lỗi LẺ giữa chunk vẫn mất khi dòng kế chạy được) — nêu rõ ở mục A2, **cố ý không
  làm** vì vượt phạm vi plan.

## Đề xuất điều chỉnh plan / việc tiếp theo
1. **Đợt sau nên có 1 mục cho A2 phần còn lại:** ghi nhận dòng lỗi rời rạc (danh sách dòng fail trong chunk) để
   `AddPatch` vá đúng những dòng đó, thay vì chỉ dựa vào một con số `lastDone`.
2. **Bộ test orders dùng chung state tĩnh:** `PushGate` static + mọi test đều `accountId = 1` + xUnit chạy lớp
   song song = đua sẵn có. Nên cho mỗi lớp test một `accountId` riêng (hoặc cho `PushGate` reset được).
3. **A4 đúng như plan dự đoán là 2 bản chép:** khi làm đợt C (hợp nhất `PortAllocator` về Core) nhớ mang theo
   `finally` re-enqueue này, đừng hợp nhất về bản cũ.
4. `LauncherRunnerLoop` dòng ~388 vẫn báo "Xong dòng N" cho dòng lỗi — nên đổi thành thông điệp theo `scrapeOk`
   ở một đợt dọn log.

---

## Nghiệm thu (Fable tổng hợp sau phản biện, 2026-08-06)

`nghiem-thu` chấm **ĐẠT CÓ ĐIỀU KIỆN** — 11/11 mục đúng, tự rebuild + chạy test 5 lượt xanh (1506/71/53), tự
kiểm chứng lại A2 (không phá ca thành công, stall-retry luôn tiến, không phá resume — `IsInterruptedMidRun`
trả false khi Phase=finished), A4 (finally re-enqueue đúng, mọi-port-bận vẫn ném lỗi với pool nguyên vẹn),
A3 (index reader 0–19 không lệch, migration `EnsureColumn shop_login` sẵn), A9 (đúng 2 chỗ mutate `_results`).
Ca test flaky `BadgeChoDay_DemCaMaTraHangConTon` không tái hiện (5 lượt) — kết luận: đua sẵn có của bộ test
(PushGate static + accountId=1 chung + xUnit song song), KHÔNG do diff đợt này.

Điều kiện + nit đã sửa ngay sau phản biện (phiên chính tự sửa):
1. Cả 4 chỗ tài liệu chỉ SAI TAB chứa nút cập nhật ("Cài đặt → Hiệu năng" → đúng là **"Cài đặt → Phiên bản &
   cập nhật"**, UnifiedSettingsView.xaml:468): `suite/README.md`, `TOI-UU-HOA-APP-KHI-CHAY.md:33`,
   `CLAUDE.md:26` (nguồn sai gốc), `CHANGELOG.md:5` (phần mô tả quy trình).
2. Comment `finally` của 2 bản PortAllocator nói sai lý do ("cuối queue" không bền vì Release dùng
   EnqueueSorted) — viết lại đúng.
3. SearchViewModel gọi `GetCategoryRows()` 2 lần lúc ctor + try/catch không cứu được ctor → gộp thành
   `NapDanhMucLucKhoiDong()` (1 truy vấn, bọc try/catch cho cả lưới + combo).

Điểm theo dõi sau phát hành (không sửa đợt này): (a) A2 làm ca "cả chunk hỏng vì lỗi không phân loại" chạy lại
tối đa 3 lượt/dòng thay vì kết thúc ngay — nếu thấy job scrape lâu bất thường, nhìn đây trước; (b) khe hẹp
`InstanceConfig.ApplyExtensionProgress` có thể NÂNG LastCompletedRow từ state cũ trong profile (nghiệm thu
không chứng minh được xảy ra thật — chỉ ghi để ý); (c) dòng lỗi LẺ giữa chunk vẫn mất (giới hạn đã khai báo,
ứng viên đợt sau: AddPatch theo danh sách dòng fail).
