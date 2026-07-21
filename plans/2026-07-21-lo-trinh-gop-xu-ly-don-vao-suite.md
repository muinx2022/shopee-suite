# Lộ trình: gộp app "Xử lý đơn Shopee" vào Shopee Suite thành 1 app thống nhất

- **Ngày:** 2026-07-21
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus theo từng plan phase

> Đây là tài liệu LỘ TRÌNH (roadmap), không phải plan thực thi. Mỗi phase có plan riêng
> trong `plans/` khi đến lượt làm.

## Bối cảnh & quyết định đã chốt (với người dùng, 2026-07-21)

Hai app cùng chủ, cùng stack (.NET 8 + Avalonia 11.x + CommunityToolkit.Mvvm, MVVM không DI container):

| | `d:\Projects\Xu-ly-don-shopee` (~27k dòng) | `D:\Projects\shopee-suite` (~52k dòng) |
|---|---|---|
| Nghiệp vụ | **Đơn hàng**: sync/xử lý đơn, in phiếu PDF, GSheet, webhook | **Sản phẩm**: scrape, BigSeller import/update, search stats, kho SKU, fleet |
| Lưu trữ | SQLite `%APPDATA%\XuLyDonShopee\app.db` + profiles | JSON `%APPDATA%\ShopeeSuite` + Postgres trên hub |
| Test | 720 test xUnit | chưa có project test |
| Hub | `hub/` fork từ suite (Phase 0, local, CHƯA có logic, chưa có VM) | hub production `api.schedra.net` (VPS, fleet thật) |

Quyết định:
1. **shopee-suite làm nền** — đưa mảng đơn hàng vào thành module mới; `XuLyDonShopee.Core` + 720 test giữ nguyên giá trị, thành project trong `ShopeeSuite.sln`.
2. **Hạ tầng trùng (proxy KiotProxy, Brave/CDP/Playwright) hợp nhất DẦN theo phase** — đợt đầu 2 bộ chạy song song, không đổi hành vi.
3. **Hub:** fork `hub/` bên repo đơn hàng mới copy, chưa chạy logic, chưa build VM → KHÔNG phát triển tiếp fork; khi cần hub đơn hàng thì thêm domain Shops/Orders vào `server/Shopee.Hub.Web` của suite (tái dùng code fork Phase 0 làm nguyên liệu).
4. **Copy code sạch**, không nhập lịch sử git; repo `Xu-ly-don-shopee` giữ nguyên làm tham chiếu/lịch sử, về sau archive.

Lưu ý bản chất domain: "tài khoản Shopee" hai bên KHÁC nhau — suite quản lý acc cào hàng loạt
(xoay vòng, LRU, captcha flags), đơn hàng quản lý acc shop bán hàng (đăng nhập bền, xử lý đơn).
KHÔNG ép chung một kho tài khoản; chỉ hợp nhất hạ tầng bên dưới (proxy, launcher trình duyệt).

## Các phase

| Phase | Nội dung | Nghiệm thu chính |
|---|---|---|
| **1a** | Nhập 3 project `XuLyDonShopee.{Core,App,Tests}` vào `ShopeeSuite.sln` dưới thư mục `orders/` — copy nguyên trạng, chưa nối gì với suite | Build sln 0 lỗi; 720 test xanh; các project suite cũ không đổi |
| **1b** | Biến `XuLyDonShopee.App` thành module trong shell suite: OutputType Library, `ModuleItem` "Xử lý đơn" host MainView (giữ nav nội bộ 5 màn), wire vòng đời shutdown (`Sessions.StopAllAsync` + `AutoRun.StopAsync`) vào đường dừng-êm của suite; đồng bộ version package (Avalonia 11.3.0, CommunityToolkit 8.4.2, Playwright thống nhất — PHẢI kiểm tra automation thật sau khi bump Playwright 1.49→1.60); vẫn đọc dữ liệu `%APPDATA%\XuLyDonShopee` như cũ | Mở suite → module "Xử lý đơn" chạy ngang bản gốc (mở phiên Brave, sync đơn); test vẫn xanh; các module suite cũ không đổi hành vi |
| **2** | Hợp nhất lớp proxy KiotProxy: một client + pool dùng chung (`Shopee.Core/Proxy` ⇄ `XuLyDonShopee.Core` KiotKeyPool/KiotProxyClient/watchdog) | Cả 2 mảng nghiệp vụ dùng 1 lớp proxy, hành vi giữ nguyên, test xanh |
| **3** | Hợp nhất lớp trình duyệt: launcher/args/profile (BrowserLauncher ⇄ ShopeeLoginService phần spawn/connect CDP). GIỮ NGUYÊN automation like-human của mảng đơn hàng (chuột cong, gõ từng ký tự, hit-test) — chỉ gộp phần mở/kết nối | Hai bên mở Brave qua 1 đường chung; stealth không đổi (webdriver=false, giữ cờ `--disable-blink-features=AutomationControlled` cho mảng đơn) |
| **4** | Hub đơn hàng trong hub suite: port domain Shops/Orders (từ fork Phase 0 cũ) vào `server/Shopee.Hub.Web`, client đơn hàng nối hub; xóa `hub/` bên repo cũ | Hub production thêm trang/API đơn hàng, fleet cũ không ảnh hưởng |
| **5** | Dọn dẹp: cân nhắc đổi namespace `XuLyDonShopee.*` → `Shopee.Orders.*`, gộp màn Cài đặt, tùy chọn di trú dữ liệu về `%APPDATA%\ShopeeSuite`, archive repo cũ, cập nhật README/CLAUDE.md suite (đang ghi WPF — lỗi thời) | Codebase một danh tính thống nhất |

Nguyên tắc xuyên suốt: mỗi phase là một đơn vị bàn giao trọn vẹn, build + test xanh, commit xong mới sang phase sau. Không đổi hành vi nghiệp vụ trong phase hạ tầng.

**Quy tắc nhánh (người dùng chốt 2026-07-21):** toàn bộ việc gộp làm trên nhánh
`feature/gop-don-hang`, KHÔNG commit thẳng vào `main`; chỉ merge về `main` khi người dùng
duyệt (dự kiến sau khi phase 1b nghiệm thu chạy thật đạt).

## Rủi ro chung

- **Bump Playwright 1.49→1.60 (phase 1b)**: hành vi CDP/stealth nhạy version — phải chạy thật luồng đăng nhập + sync đơn trước khi chốt.
- **WDAC/ISG trên máy dev**: binary/test DLL mới build có thể bị chặn (0x800711C7) — là chính sách máy, không phải lỗi code; fallback nhờ người dùng chạy test/app.
- **Repo đơn hàng đang có phiên Claude khác làm việc song song** (WIP Proxies chưa commit, 2026-07-21). Copy phase 1a lấy nguyên trạng working tree (đã build sạch, 720 test xanh — bao gồm bản vá HEAD gãy). Sau thời điểm copy, mọi thay đổi mới bên repo cũ phải port tay sang suite → nên **đóng băng phát triển tính năng bên repo cũ** ngay khi phase 1a xong.
- **Velopack đóng gói**: từ phase 1b app hợp nhất là 1 exe duy nhất (`Shopee.Suite`); trước đó (1a) sln tạm có 2 WinExe — không phát hành trong giai đoạn này.

## Nhật ký phase

- 2026-07-21: chốt lộ trình; giao phase 1a (`plans/2026-07-21-gop-phase-1a-nhap-projects-don-hang-vao-sln.md`).
