# Plan: Gộp phase 1a — nhập 3 project đơn hàng vào ShopeeSuite.sln

- **Ngày:** 2026-07-21
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Theo lộ trình `plans/2026-07-21-lo-trinh-gop-xu-ly-don-vao-suite.md`: gộp app
`d:\Projects\Xu-ly-don-shopee` (xử lý đơn hàng Shopee) vào repo này (shopee-suite làm nền).

Phase 1a = bước THUẦN CƠ HỌC, additive: copy 3 project của app đơn hàng vào thư mục mới
`orders/` của repo này và đưa vào `ShopeeSuite.sln`. **Chưa nối gì** với code suite hiện có —
2 hệ chạy song song trong 1 solution. Mục tiêu: build sln 0 lỗi, 720 test của mảng đơn hàng
xanh, mọi project suite cũ nguyên vẹn.

Nguồn copy: working tree của `d:\Projects\Xu-ly-don-shopee` **nguyên trạng hiện tại**
(commit `86fa802` + 3 file WIP chưa commit ở ProxiesViewModel/ProxiesView/ProxiesViewModelTests —
bản working tree này build sạch và 720 test xanh; bản HEAD thì KHÔNG biên dịch được, nên
tuyệt đối không dùng `git archive`/checkout, copy thẳng từ working tree).

## 2. Phạm vi

- **Làm:**
  - Copy `src/XuLyDonShopee.Core`, `src/XuLyDonShopee.App`, `src/XuLyDonShopee.Tests`
    (từ repo nguồn) → `orders/XuLyDonShopee.Core`, `orders/XuLyDonShopee.App`,
    `orders/XuLyDonShopee.Tests` (repo này). Loại trừ `bin/`, `obj/`.
  - Thêm 3 project vào `ShopeeSuite.sln` trong solution folder mới `orders`.
- **Không làm:**
  - KHÔNG copy `hub/`, `plans/`, `.claude/`, `chay-app.cmd`, `.gitignore` … của repo nguồn.
  - KHÔNG sửa bất kỳ file nào của repo nguồn `Xu-ly-don-shopee` (chỉ đọc — đang có phiên
    làm việc khác trên repo đó).
  - KHÔNG sửa project suite hiện có, KHÔNG thêm ProjectReference giữa suite ⇄ orders.
  - KHÔNG đổi namespace/tên project, KHÔNG đổi version package (Avalonia 11.2.8 v.v. giữ
    nguyên — đồng bộ version là việc của phase 1b).
  - KHÔNG commit (Fable nghiệm thu rồi commit).

## 3. Các bước thực hiện

1. **Copy 3 project.** Ví dụ PowerShell, chạy từ gốc repo này:
   `robocopy d:\Projects\Xu-ly-don-shopee\src\XuLyDonShopee.Core orders\XuLyDonShopee.Core /E /XD bin obj`
   (lặp lại cho App, Tests; robocopy exit code 1–3 là thành công).
2. **Kiểm tra tham chiếu tương đối** trong 3 csproj sau copy: Tests tham chiếu
   `..\XuLyDonShopee.Core\…` và `..\XuLyDonShopee.App\…`, App tham chiếu Core — cấu trúc anh
   em giữ nguyên (`src/` → `orders/`) nên phải resolve được; nếu csproj nào trỏ ra ngoài cây
   project (đường dẫn `..\..\…`) thì sửa lại cho đúng vị trí mới và ghi vào báo cáo.
3. **Sửa `ShopeeSuite.sln`:** thêm solution folder `orders` (GUID mới, type
   `{2150E333-8FDC-42A3-9474-1A3956D46DE8}`) + 3 project (GUID mới sinh bằng `[guid]::NewGuid()`,
   type `{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}`), thêm đủ mục
   `ProjectConfigurationPlatforms` (Debug/Release Any CPU) và `NestedProjects` theo đúng
   format các project sẵn có trong sln.
4. **Build:** `dotnet build ShopeeSuite.sln -c Release` → 0 lỗi; không phát sinh warning mới
   ở các project suite cũ.
5. **Test:** `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj -c Release`
   → kỳ vọng 720 pass. Nếu test fail ĐỒNG LOẠT với `FileLoadException 0x800711C7` thì đó là
   WDAC/ISG của máy chặn DLL mới build (không phải lỗi code) — ghi rõ hiện tượng vào báo cáo,
   không cố lách.
6. **Vệ sinh:** `git status` repo này không được lộ `bin/`, `obj/` của orders (nếu lộ, xem lại
   `.gitignore` gốc repo — pattern hiện có phải cover được; cần thì bổ sung dòng cho `orders/`);
   xác nhận `git -C d:\Projects\Xu-ly-don-shopee status --short` TRƯỚC và SAU không đổi.

## 4. Tiêu chí nghiệm thu

- [ ] `orders/` có đủ 3 project, không kèm `bin/`/`obj/`.
- [ ] `dotnet build ShopeeSuite.sln -c Release` 0 lỗi.
- [ ] 720 test xanh (hoặc báo cáo WDAC chặn kèm bằng chứng lỗi 0x800711C7 đồng loạt).
- [ ] `git status` repo này chỉ có: `ShopeeSuite.sln` sửa + `orders/**` mới + `plans/**` (đã có sẵn).
- [ ] Repo nguồn `Xu-ly-don-shopee` không bị sửa gì.

## 5. Rủi ro & lưu ý

- Repo nguồn đang có phiên Claude khác sửa dở phần Proxies — copy MỘT LẦN rồi thôi, không
  đồng bộ lại; nếu lúc copy thấy trạng thái khác mô tả (thêm file lạ đang sửa) thì vẫn copy
  nguyên trạng và ghi nhận vào báo cáo.
- `XuLyDonShopee.App` là WinExe thứ hai trong sln — chấp nhận tạm ở phase 1a (phase 1b sẽ
  chuyển thành Library), không cấu hình publish/Velopack cho nó.
- Máy có WDAC/ISG: mọi fail hàng loạt 0x800711C7 khi chạy test là chính sách máy — báo cáo,
  đừng coi là lỗi code và đừng sửa code để "né".

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
