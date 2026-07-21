# Plan: Scrape captcha — xóa profile tk dính captcha + bỏ đánh dấu tk lỗi captcha

- **Ngày:** 2026-07-22
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Trong màn **Workspace** (logic ở `ScrapeViewModel`, module Shopee Scrape), khi 1 tài khoản Shopee dính **captcha** lúc đang scrape, hành vi **hiện tại** là:

- **Lần captcha 1:** `SessionAccountPool.CaptchaGrace(spec)` trả `false` → tk ở lại khung nhưng bị **cooldown 3'**; đoạn dòng đang cào dở `[nextFrom, to]` được đẩy vào hàng **patch** cho **tk khác cào tiếp** (`ScrapeRunner.AddPatch`). Frame thiếu tk → tự **bù tk thay thế** (`BorrowAsync` → `TryTopUpAsync`).
- **Lần captcha 2:** `CaptchaGrace` trả `true` → tk bị **loại khỏi khung** (`_dropped`) + bắn sự kiện `AccountErrored` → handler `ScrapeViewModel` gọi:
  - `AccountErrorReporter.Report(...)` → đặt `acc.Disabled = true`, `acc.LastError`, `acc.CaptchaUrl`, `AccountStore.Save()`, **upsert 1 dòng vào lưới `ErroredAccounts`** (khu "tài khoản bị lỗi"), và (nếu là CLIENT) **báo Hub** `ReportErroredAccountAsync(..., "captcha")`.
  - `ShopeeAccountUsage.Shared.MarkCaptcha(id)` (đánh dấu runtime "⚠ Captcha").

**Yêu cầu người dùng (đã chốt qua hỏi đáp):**

1. Khi tk dính captcha lúc scrape: **đổi sang tk khác** (giữ nguyên cơ chế đang có) **+ xóa profile của tk dính captcha NGAY lần captcha đầu tiên** (không giữ grace 2 lần) để lần sau tk **tự đăng nhập lại**.
2. Kiểu login lại sau khi xóa: **ép login mới hoàn toàn** — xóa CẢ nguồn cookie đã lưu để lần sau buộc nhập lại tài khoản/mật khẩu (phiên mới tinh), không nạp lại phiên cookie cũ.
3. **Bỏ hẳn việc đánh dấu tk "lỗi captcha"**: không đặt `Disabled`, không đưa vào lưới `ErroredAccounts`, không `MarkCaptcha`, không báo Hub là captcha — **CHỈ cho trường hợp captcha**. Tk vẫn nằm trong pool để dùng lại (với profile mới).
4. **Link/dòng đang dở vẫn được tk mới cào tiếp** — hành vi này **đã có sẵn** (cơ chế patch), phải **giữ nguyên**.

**Ràng buộc quan trọng (đừng làm hỏng):**

- Khu "tài khoản bị lỗi" (`ErroredAccounts` + bộ lọc "Bị lỗi / captcha" = `Where(a => a.Disabled)`) là khu **dùng chung cho MỌI loại lỗi** (login fail, banned…) và **dùng chung cả module Search**. → **CHỈ gỡ nhánh captcha**, tuyệt đối không xóa lưới/không đụng đường xử lý lỗi non-captcha.
- Ghi chú kinh nghiệm: xóa profile + login mới nhiều lần dễ bị Shopee coi là bot → captcha nhiều hơn. Người dùng chấp nhận đánh đổi này để tự phục hồi; ta chỉ cần triển khai đúng, không tự ý thêm cơ chế khác.

## 2. Phạm vi

- **Làm:**
  1. Captcha (lần đầu) → **loại tk khỏi khung ngay** (bù tk thay thế như cũ) + **xóa profile** của tk đó (cả 2 thư mục, ép login mới) + **giữ cơ chế patch** để tk khác cào tiếp đoạn dở.
  2. **Gỡ nhánh captcha** khỏi đánh dấu tk lỗi: không `Disabled`/`ErroredAccounts`/`MarkCaptcha`/báo Hub cho captcha.
- **Không làm:**
  - Không đụng xử lý lỗi **non-captcha** (login fail, banned, lỗi mạng…) — giữ nguyên `AccountErrorReporter` cho các lỗi đó.
  - Không xóa lưới `ErroredAccounts` / bộ lọc "Bị lỗi" (vẫn phục vụ lỗi khác + module Search).
  - Không đụng module **Search**, module **CheckAccount** (luồng "Kiểm tra tk lỗi" thủ công) — chỉ sửa luồng **Scrape/Workspace**.
  - Không đổi cơ chế phát hiện captcha ở tầng engine (extension/`LauncherRunnerLoop`/`ScrapeRunner.Classify`).

## 3. Các bước thực hiện

> Tất cả đường dẫn tương đối từ gốc repo. Vị trí code lấy từ khảo sát; kiểm lại số dòng vì có thể lệch chút.

### Bước 1 — Captcha lần đầu = loại tk + xóa profile (bỏ grace 2 lần)

- File: `suite/Shopee.Suite/Modules/Scrape/ScrapeViewModel.cs` — hàm `SessionAccountPool.CaptchaGrace` (~dòng 349–368) và handler `runner.AccountErrored` (~dòng 739–747); phối hợp với `ScrapeRunner.RunAutoAsync` (`suite/Shopee.Module.MultiBrave/ScrapeRunner.cs:268–321`, chỗ bắn `AccountErrored` ~`:274–279`).
- Đổi hành vi: **ngay lần captcha đầu tiên**, coi tk là "bỏ khỏi vòng chạy hiện tại" (thêm vào `_dropped` như nhánh lần-2 hiện nay), để cơ chế bù tk thay thế + patch dòng dở cho tk khác chạy như cũ. Không còn bước "cooldown 3' rồi thử lại".
  - Cân nhắc cách gọn nhất: cho `CaptchaGrace` trả `true` **ngay lần đầu** (n>=1) thay vì n>=2 — nhưng phải RÀ lại: khi trả `true`, luồng hiện bắn `AccountErrored` (đánh dấu lỗi). Ta cần **tách** "loại tk khỏi khung + xóa profile" ra khỏi "đánh dấu tk lỗi" (Bước 3). Nếu tách khó, thêm một đường sự kiện riêng cho captcha (vd `AccountCaptchaDropped(id)`) không đi qua `AccountErrorReporter`.
- **Xóa profile** của tk vừa dính captcha, dựng đường dẫn từ `account.Id`:
  - Brave scrape profile: `persistent-data/profiles/{Id}` — gốc lấy từ `BraveProfileManager.GetProfileRootDirectory` (`suite/Shopee.Module.MultiBrave/Engine/BraveProfileManager.cs:16–22`, dùng `AppSession.ResolvePersistentDataPath()`).
  - Nguồn cookie (ép login mới): `shared/profiles/{Id}` — `ShopeeAccountSpecFactory.SharedProfileDir` (`suite/Shopee.Suite/Infrastructure/ShopeeAccountSpecFactory.cs:46–47`).
  - Dùng hàm xóa sẵn có trong Core: `BraveCachePolicy.DeleteDirBestEffort(dir)` (`suite/Shopee.Core/Browser/BraveCachePolicy.cs:95`) — best-effort, không ném lỗi làm chết vòng scrape.
- **Thời điểm xóa an toàn:** Brave của chunk đã được `session.StopAsync` ở `finally` của `RunChunk` (`ScrapeRunner.cs:465–468`) trước khi worker xử lý captcha → thư mục thường đã hết khóa. Vẫn dùng `DeleteDirBestEffort` (có retry) để chắc; nếu vẫn khóa, **degrade êm** (log cảnh báo, không chặn scrape) — lần khởi động sau `StartupJanitor`/`EnsureProfile` sẽ dọn nốt.

### Bước 2 — Giữ nguyên: tk mới cào tiếp đoạn dở + bù tk thay thế

- KHÔNG sửa cơ chế patch (`ScrapeRunner.AddPatch`, `ScrapeRunner.cs:304–321`) và bù tk (`ScrapeViewModel.BorrowAsync`/`TryTopUpAsync`, ~`:259–319`). Chỉ đảm bảo đổi ở Bước 1 không làm mất các bước này (tk bị loại ngay lần đầu vẫn phải kích hoạt patch + top-up y như nhánh lần-2 cũ).

### Bước 3 — Bỏ đánh dấu tk lỗi cho trường hợp captcha

- File: `suite/Shopee.Suite/Modules/Scrape/ScrapeViewModel.cs` handler `AccountErrored` (~`:739–747`) và `suite/Shopee.Suite/Infrastructure/AccountErrorReporter.cs` (`Flag`/`Report` ~`:20–46`).
- Với **captcha**: KHÔNG gọi `AccountErrorReporter.Report(...)`, KHÔNG `MarkCaptcha`, KHÔNG báo Hub. Tk **không** bị `Disabled`, **không** hiện trong lưới `ErroredAccounts`.
- **Phân biệt captcha vs lỗi khác:** RÀ xem sự kiện `AccountErrored` hiện có dùng cho lỗi non-captcha không. Nếu `AccountErrored` chỉ bắn cho captcha (như khảo sát cho thấy, sau `CaptchaGrace`) → gỡ hẳn phần đánh dấu ở handler. Nếu dùng chung → **rẽ nhánh theo captcha** (vd `captchaUrl != null` hoặc reason chứa "captcha"): captcha thì bỏ đánh dấu, còn lại giữ nguyên `AccountErrorReporter.Report`.
- Kiểm tra thêm: có UI/nhãn nào **riêng cho captcha** trong khu lỗi không (vd cột/nhãn "captcha"). Nếu có phần **chỉ dành cho captcha** thì gỡ; nếu là khu lỗi chung thì **giữ nguyên**, chỉ cần captcha không còn đẩy dòng vào.

### Bước 4 — Build + test

- Build: `dotnet build suite/Shopee.Suite/Shopee.Suite.csproj` (và cả solution nếu đụng nhiều project: `dotnet build ShopeeSuite.sln`). Phải **xanh, không warning mới đáng kể**.
- Nếu có test liên quan (vd quanh `ScrapeRunner`/pool), chạy `dotnet test` project tương ứng.

## 4. Tiêu chí nghiệm thu

- [ ] Build `dotnet build ShopeeSuite.sln` thành công.
- [ ] Đọc lại code xác nhận: captcha **lần đầu** → tk bị loại khỏi khung + **xóa cả `persistent-data/profiles/{Id}` lẫn `shared/profiles/{Id}`** (qua `DeleteDirBestEffort`), có log rõ ràng.
- [ ] Đọc lại code xác nhận: nhánh captcha **KHÔNG** gọi `AccountErrorReporter.Report` / `MarkCaptcha` / báo Hub; tk **không** bị `Disabled` và **không** vào lưới `ErroredAccounts`.
- [ ] Đọc lại code xác nhận: lỗi **non-captcha** vẫn đi qua `AccountErrorReporter` như cũ (không bị gỡ nhầm); lưới `ErroredAccounts` + bộ lọc "Bị lỗi" còn nguyên; module **Search** không đổi hành vi.
- [ ] Đọc lại code xác nhận: cơ chế **patch** (tk khác cào tiếp đoạn dở) + **bù tk thay thế** vẫn chạy khi tk bị loại ngay lần đầu.
- [ ] (Nếu chạy được app) Thử: giả lập/đợi 1 tk dính captcha khi scrape → quan sát log thấy "xóa profile {Id}" + tk đổi sang tk khác + dòng dở tiếp tục; tk KHÔNG xuất hiện ở khu "tài khoản bị lỗi".

## 5. Rủi ro & lưu ý

- **Tách "loại tk" khỏi "đánh dấu lỗi":** đây là điểm dễ sai nhất — hiện 2 việc này dính nhau ở nhánh `CaptchaGrace → AccountErrored`. Phải tách sạch để captcha loại tk + xóa profile **mà không** quarantine, trong khi lỗi khác vẫn quarantine.
- **Khóa thư mục khi xóa:** Brave có thể còn giữ file (đặc biệt `shared/profiles`). Luôn best-effort + log, không để ném lỗi chết vòng scrape.
- **`shared/profiles/{Id}` có thể do luồng khác quản lý** (cookie engine/Hub sync). Xác nhận xóa nó chỉ ảnh hưởng "phải login lại", KHÔNG làm mất dữ liệu tài khoản (Id/credential nằm ở `AccountStore`, không nằm trong thư mục profile). Nếu phát hiện `shared/profiles` được Hub đồng bộ/khôi phục lại thì báo lại Fable trước khi xóa.
- **Vòng lặp re-captcha trong cùng 1 lần chạy:** giữ tk trong `_dropped` cho hết lần chạy hiện tại (như hiện nay) để tránh mượn lại tk vừa xóa profile rồi lại dính captcha ngay. "Lần sau tự login lại" = lần **chạy scrape sau**, không phải trong cùng run.
- Không tự ý đổi ngưỡng/cooldown ở chỗ khác ngoài phạm vi captcha.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chờ thực thi>
