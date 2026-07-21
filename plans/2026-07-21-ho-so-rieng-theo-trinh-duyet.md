# Plan: Hồ sơ trình duyệt RIÊNG theo từng trình duyệt (mỗi tài khoản × mỗi browser) — module đơn hàng

- **Ngày:** 2026-07-21
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Nơi làm:** worktree riêng của repo shopee-suite (nhánh `task/ho-so-rieng-theo-browser` tách từ `feature/gop-don-hang`). Mọi đường dẫn dưới đây tương đối từ gốc worktree. TUYỆT ĐỐI không đọc/ghi cây làm việc chính hay repo khác.

> Chuyển thể từ plan cùng tên bên repo cũ `Xu-ly-don-shopee` (commit `fb4b364`, chưa thực thi) —
> nay code đơn hàng sống ở `orders/` của repo này nên làm tại đây. Plan bên repo cũ coi như bị thay thế.

## 1. Bối cảnh & mục tiêu

Trong module đơn hàng, mỗi tài khoản dùng **1 thư mục hồ sơ DUY NHẤT** `profiles/<id>`
(`BrowserProfilePaths.ForAccount(baseDir, accountId)`) — **dùng chung cho MỌI trình duyệt**.
Hệ quả: login bằng Brave rồi đổi sang Chrome trong Cài đặt → Chrome mở đúng hồ sơ Brave cũ →
lẫn fingerprint giữa 2 trình duyệt (dễ bị Shopee soi). Người dùng chốt: **mỗi trình duyệt một
hồ sơ riêng** → đổi trình duyệt = phiên sạch, đăng nhập lại bằng đúng fingerprint trình duyệt đó.

Ràng buộc: khóa hồ sơ theo **trình duyệt THỰC được dùng** (kết quả
`BrowserLocator.ResolveExecutable(choice)` — khớp cái `OpenAsync` sẽ launch), KHÔNG theo lựa
chọn thô — vì `Auto` phân giải ra Chrome/Edge/Brave tùy máy.

## 2. Phạm vi

- **Làm:** `orders/XuLyDonShopee.Core/Services/BrowserLocator.cs`,
  `orders/XuLyDonShopee.Core/Services/BrowserProfilePaths.cs`,
  `orders/XuLyDonShopee.App/Services/AccountSession.cs`,
  tests `orders/XuLyDonShopee.Tests/{BrowserLocatorTests,BrowserProfilePathsTests}.cs`.
- **KHÔNG làm:** KHÔNG migrate/di chuyển hồ sơ `profiles/<id>` cũ (để orphaned — lần đầu mở
  mỗi trình duyệt sẽ login lại, người dùng đã chấp nhận); KHÔNG tự xóa hồ sơ cũ; KHÔNG đụng
  `suite/`, `server/`, hay lớp proxy (một agent khác đang hợp nhất proxy trên cây chính —
  phase 2); KHÔNG commit (Fable commit trong worktree rồi merge).

## 3. Các bước

### 3.1. `BrowserLocator` — phân giải "loại" trình duyệt
- Thêm `public static string ResolveBrowserKind(BrowserChoice choice)`:
  - Gọi `ResolveExecutable(choice)` → exe path; phân loại bằng so path với
    `FindChromeExecutable()`/`FindEdgeExecutable()`/`FindBraveExecutable()` (nên tách helper
    `ClassifyExe(path) → slug` dùng chung với `DescribeBrowser` nếu tiện — không bắt buộc,
    miễn không đổi hành vi cũ):
    - khớp Chrome → `"chrome"`; Edge → `"edge"`; Brave → `"brave"`; null/không khớp → `"chromium"`.
  - Trả slug ngắn, an toàn cho tên thư mục.

### 3.2. `BrowserProfilePaths` — thư mục theo (tài khoản × trình duyệt)
- Đổi `ForAccount(string baseDir, long accountId)` →
  `ForAccount(string baseDir, long accountId, string browserKind)`:
  - Trả `Path.Combine(baseDir, "profiles", $"{accountId}-{browserKind}")`
    (vd `profiles/12-chrome`, `profiles/12-brave`).
  - Chuẩn hóa `browserKind` (trim, lowercase).
- Cập nhật doc comment.

### 3.3. `AccountSession` — dùng hồ sơ theo trình duyệt đã chọn
- LƯU Ý THỨ TỰ HIỆN TẠI: `userDataDir` đang tính ở dòng ~1079 TRƯỚC khi đọc
  `browserChoice = _services.Settings.GetBrowserChoice()` ở dòng ~1091. Phải **hoặc** dời phép
  đọc `browserChoice` lên trước chỗ tính `userDataDir`, **hoặc** dời phép tính `userDataDir`
  xuống sau — miễn cả hai nằm TRƯỚC vòng relaunch, cùng scope, để mọi lần mở lại trong phiên
  (đổi proxy → relaunch) dùng CÙNG hồ sơ + CÙNG trình duyệt.
- `var browserKind = BrowserLocator.ResolveBrowserKind(browserChoice);`
  `var userDataDir = BrowserProfilePaths.ForAccount(baseDir, _accountId, browserKind);`
- Không đổi phần còn lại (`OpenAsync` vẫn nhận browserChoice + tự resolve exe như cũ — hồ sơ
  và exe cùng theo một choice nên khớp).

### 3.4. Tests
- `BrowserProfilePathsTests`: sửa 3 chỗ gọi cũ (dòng 10/19/20) theo chữ ký mới;
  case mới: `ForAccount(base, 12, "chrome")` → `...\profiles\12-chrome`; hai kind khác nhau →
  hai đường dẫn khác nhau; kind viết hoa/space → chuẩn hóa lowercase.
- `BrowserLocatorTests`: test `ClassifyExe` thuần với path giả (khớp chrome/edge/brave/null→chromium)
  nếu đã tách helper; nếu không tách thì test `ResolveBrowserKind` ở mức khả thi (không đụng máy thật).

### 3.5. Build + test (trong worktree)
- `dotnet build ShopeeSuite.sln -c Release` → 0 lỗi, không warning mới.
- `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj -c Release` → 720 baseline
  + test mới đều pass. Fail đồng loạt `0x800711C7` = WDAC máy → báo cáo, đừng né bằng sửa code.

## 4. Tiêu chí nghiệm thu

- [ ] Build 0 lỗi; toàn bộ test pass (≥720).
- [ ] `ForAccount` tạo `profiles/<id>-<kind>` theo trình duyệt thực; hai trình duyệt khác nhau
      = hai hồ sơ khác nhau.
- [ ] `AccountSession` phân giải kind từ `browserChoice`, hồ sơ + exe khớp nhau, giữ nguyên
      suốt phiên kể cả relaunch đổi proxy.
- [ ] Không sửa file nào ngoài 5 file trong phạm vi.

## 5. Rủi ro & lưu ý

- **Hồ sơ cũ `profiles/<id>` orphaned:** sau thay đổi, mỗi tài khoản mở lần đầu trên MỖI trình
  duyệt (kể cả Brave) sẽ cần đăng nhập lại một lần — chủ đích của người dùng, kèm rủi ro gặp
  captcha khi login lại (đã có quy tắc: captcha để người dùng tự giải).
- `ResolveBrowserKind` phải KHỚP trình duyệt `OpenAsync` thực mở (cùng nguồn
  `ResolveExecutable(choice)`), nếu không hồ sơ và exe lệch nhau.
- Trong worktree này chỉ có việc này — nhưng vẫn giữ kỷ luật: đúng 5 file phạm vi.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
