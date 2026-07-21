# Plan: Gộp phase 1a — nhập 3 project đơn hàng vào ShopeeSuite.sln

- **Ngày:** 2026-07-21
- **Trạng thái:** hoàn thành (2026-07-21 — Fable nghiệm thu: tự build 0 lỗi + 720/720 test xanh, sln chỉ thêm 23 dòng, repo nguồn không bị đụng)
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

**Ngày thực thi:** 2026-07-21 · **Kết quả:** đạt toàn bộ tiêu chí nghiệm thu.

### Đã hoàn thành

1. **Copy 3 project (bước 1).** Robocopy từ working tree nguồn nguyên trạng (HEAD `86fa802`
   + 3 file WIP Proxies), loại `bin`/`obj`:
   - `d:\Projects\Xu-ly-don-shopee\src\XuLyDonShopee.Core` → `orders\XuLyDonShopee.Core`
   - `d:\Projects\Xu-ly-don-shopee\src\XuLyDonShopee.App` → `orders\XuLyDonShopee.App`
   - `d:\Projects\Xu-ly-don-shopee\src\XuLyDonShopee.Tests` → `orders\XuLyDonShopee.Tests`
   - Lệnh: `robocopy <src> <dst> /E /XD bin obj` (mỗi project exit code 1 = copy thành công).
   - Kết quả: `orders/` = 1.6M, 141 file source, `find` xác nhận KHÔNG có thư mục `bin`/`obj` nào lọt vào.

2. **Kiểm tra tham chiếu tương đối (bước 2).** Đọc cả 3 csproj sau copy: App trỏ
   `..\XuLyDonShopee.Core\XuLyDonShopee.Core.csproj`; Tests trỏ `..\XuLyDonShopee.Core\…` và
   `..\XuLyDonShopee.App\…`; Core không có ProjectReference. Toàn bộ là tham chiếu anh em, cấu
   trúc `src/` → `orders/` giữ nguyên nên resolve đúng. KHÔNG csproj nào trỏ ra ngoài cây
   (`..\..\`). => Không cần sửa file nào.

3. **Sửa `ShopeeSuite.sln` (bước 3).** Thêm solution folder `orders`
   (`{6A33D9C6-A4C7-4D06-9C37-98349F3A11D9}`, type folder) + 3 project (GUID mới sinh bằng
   `[guid]::NewGuid()`):
   - Core `{1E21C536-A41B-4F0E-BBB7-E4A5C3F8571C}`
   - App `{037C15A4-E501-4C7E-B313-C90F1353D847}`
   - Tests `{A181BEDF-A1B7-4CE8-BD85-09C427A28615}`
   Đã thêm đủ khai báo Project, 4 dòng ProjectConfigurationPlatforms (Debug/Release Any CPU,
   ActiveCfg + Build.0) cho mỗi project, và 3 dòng NestedProjects trỏ 3 project về folder
   `orders`. Dùng đúng tab thụt lề như các project sẵn có. `dotnet sln list` xác nhận đủ 9
   project (6 suite + 3 orders).

### Kết quả kiểm chứng

- **Build:** `dotnet build ShopeeSuite.sln -c Release` → `Build succeeded. 0 Warning(s), 0 Error(s)`
  (~12,7s). Cả 9 project build ra DLL, gồm Core/App/Tests đơn hàng. Không phát sinh warning mới
  ở project suite cũ.
- **Test:** `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj -c Release` →
  `Passed! - Failed: 0, Passed: 720, Skipped: 0, Total: 720` (~2s). KHÔNG dính WDAC/ISG
  `0x800711C7` lần chạy này.
- **Vệ sinh git repo này:** `git status --short` = `M ShopeeSuite.sln` + `?? orders/`. Liệt kê
  đầy đủ (`--untracked-files=all`): 141 file source dưới `orders/`, KHÔNG file `bin`/`obj` nào;
  `git check-ignore` xác nhận `orders/**/bin` và `orders/**/obj` đã bị `.gitignore` gốc
  (pattern `[Bb]in/`, `[Oo]bj/`) bao phủ — KHÔNG cần bổ sung `.gitignore`. File plan `plans/**`
  đã được commit sẵn nên không hiện trong status (đúng như tiêu chí "đã có sẵn").
- **Repo nguồn không đổi:** `git -C d:\Projects\Xu-ly-don-shopee status --short` TRƯỚC và SAU
  y hệt nhau (`M` ProxiesViewModel.cs / ProxiesView.axaml / ProxiesViewModelTests.cs + `??`
  plans/2026-07-21-go-buoc-distribute-khi-luu-key-proxy.md), HEAD vẫn `86fa802`. Chỉ ĐỌC repo
  nguồn, không ghi gì.

### Vướng mắc / bỏ dở

Không có. Toàn bộ 5 tiêu chí nghiệm thu đạt. Chưa commit (theo plan, Fable nghiệm thu rồi commit).

### Đề xuất

- `XuLyDonShopee.App` vẫn là `WinExe` thứ 2 trong sln (đúng như plan đã lường trước cho phase
  1a); phase 1b sẽ chuyển thành Library — không xử lý ở đây.
- Trường `Trạng thái` ở đầu plan đang để `đang làm` cho Fable cập nhật khi nghiệm thu.
