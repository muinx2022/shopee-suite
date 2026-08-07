# Plan: mở bản sạch TRƯỚC, chỉ đăng nhập Playwright khi cần

- **Ngày:** 2026-08-07
- **Trạng thái:** đang làm
- **Người lập:** phiên chính (Opus 5) · **Người thực thi:** phiên chính; phản biện: `nghiem-thu`

## 1. Bối cảnh & mục tiêu

Mỗi vòng chạy của một tài khoản Đơn hàng đi qua `OrdersBridgeSession.LoginAndReachPickerAsync`
(`orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs`), thứ tự CỨNG hiện nay:

1. Playwright mở trình duyệt điều khiển trên hồ sơ `_userDataDir` → `TryLoginSubaccountAsync`.
2. Khi hồ sơ ĐÃ đăng nhập sẵn, luồng vẫn chạy hết: dò trạng thái (poll ≤15s), click "Tài khoản của tôi",
   click "Kênh Người bán", chờ URL banhang (≤90s), đóng tab subaccount
   (`SubaccountLoginFlow.RunAsync`, nhánh `loggedIn == true`).
3. Đóng trình duyệt điều khiển + `Task.Delay(800)`.
4. Mở lại bằng trình duyệt SẠCH + extension tại `/account` → `gotoSellerCentre` → **SSO lần thứ hai** → picker.

Tức khi cookie hồ sơ còn hạn, bước 1–3 là công toi: một lượt mở/đóng trình duyệt (~30–60s mỗi vòng, mỗi tài
khoản) và **một lần bàn giao hồ sơ** — chính là nguồn của lỗi "Trình duyệt thoát ngay khi khởi động"
(plan `2026-08-06-loi-trinh-duyet-thoat-ngay-khi-dang-nhap.md`).

**Yêu cầu người dùng (2026-08-07):** mở trình duyệt sạch TRƯỚC; có cookie thì chạy thẳng, chỉ khi gặp form
đăng nhập mới đóng lại và dùng đường Playwright như cũ.

**Quyết định đã chốt với người dùng:**

- **Fallback cho MỌI lỗi không-phải-captcha** (trang login, SSO trượt, không thấy "Kênh Người bán", picker
  không render, extension không nối cầu, timeout chặng) — không cố phân biệt nguyên nhân, vì so khớp câu chữ
  tiếng Việt trong message lỗi của extension là mong manh.
- **Captcha KHÔNG fallback**: `CaptchaSeen` → trả về như hiện tại (nghỉ vòng). Đẩy Playwright vào lúc Shopee
  đang nghi ngờ là tự khai bot.
- Fallback tối đa **một lần mỗi vòng**: thử sạch → (thất bại) → Playwright → mở sạch lại. Lần hai thất bại thì
  trả lỗi, nghỉ tới vòng sau.

Hạ tầng đã sẵn sàng, không phải làm mới:

- Extension đã phát hiện form login: `gotoSellerCentre` gọi `pageIsLoginForm` rồi gửi
  `{action:"error", message:"bản sạch gặp trang đăng nhập subaccount (cookie hết hạn) — cần đăng nhập lại"}`
  (`extensions/shopee-orders/flow-shop.js:46-52`).
- `OrdersBridgeChannel` biến `error` thành fault ĐÚNG chặng đang chờ (`StageWaiter.FaultCurrent`) →
  `InvalidOperationException`; `captcha` thì bật `CaptchaSeen` + `_atSellerTcs.TrySetResult(false)`.
- `WebSocketServer` giữ "kết nối mới nhất là sống" (`HandleConnectionAsync` Interlocked.Exchange) → cùng một
  server phục vụ được lượt trình duyệt thứ hai, không cần mở cổng lại.
- `DongTrinhDuyetSach()` đã đóng đúng bài (kill handle **rồi** quét theo `--user-data-dir`).

## 2. Phạm vi

- **Làm:**
  - Đảo thứ tự trong `LoginAndReachPickerAsync`: thử bản sạch trước, fallback Playwright khi cần.
  - Gác `_channel.Start()` để không mở cổng 47821 lần thứ hai trong cùng phiên.
  - Tách phần "SSO qua cầu nối" thành hàm dùng lại được cho cả hai lượt + hàm THUẦN quyết định hành động.
  - Test cho hàm thuần + test cầu nối thật (BridgeTestRig) cho ba ca: ok / captcha / lỗi.
- **Không làm:**
  - KHÔNG đổi `SubaccountLoginFlow` (luồng Playwright giữ nguyên 100%).
  - KHÔNG đổi extension (không thêm mã `needLogin` — phương án fallback-mọi-lỗi không cần).
  - KHÔNG đụng `RunLoginThenSliceAsync` ngoài việc nó hưởng lây thứ tự mới qua `LoginAndReachPickerAsync`.
  - KHÔNG đổi cách đóng trình duyệt cuối vòng (kill cứng) — xem mục 5.
  - KHÔNG đụng `ShopFlowRunner`, luồng shop, GSheet/hub.

## 3. Các bước thực hiện

### Bước 1 — `OrdersBridgeSession.StartBridgeAndLaunch`: mở cổng đúng một lần

`orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs`

- `_channel.Start()` chỉ gọi khi `!_channel.Started` (property đã có sẵn). Gọi lần hai sẽ tạo `HttpListener`
  mới trên cổng đang có đăng ký → ném; retry 5×400ms trong `Channel.Start` không cứu được vì listener cũ vẫn
  của chính ta.
- Ghi comment nêu rõ lý do: một phiên có thể phóng trình duyệt sạch HAI lần (thử trước + sau khi login).

### Bước 2 — tách `SsoVePickerAsync` (dùng chung hai lượt)

Thêm vào `OrdersBridgeSession` hàm `private async Task<KetQuaSso> SsoVePickerAsync(CancellationToken ct)`:

- `ResetState()` → `StartBridgeAndLaunch(ShopeeLoginService.SubaccountAccountUrl)` → chờ `Ready`
  (`ChoChang.Ready`) → `ArmAtSeller` + gửi `gotoSellerCentre` → chờ `AtSeller` (`ChoChang.AtSeller`).
- Trả `enum KetQuaSso { Ok, Captcha, Loi }` (đặt trong file này, `internal`):
  - `CaptchaSeen` → `Captcha`;
  - `atSeller == true` → `Ok`;
  - còn lại (atSeller false, `InvalidOperationException` từ error extension, `TimeoutException`) → `Loi`
    kèm câu lý do qua tham số `out string? lyDo` hoặc record `KetQuaSsoChiTiet(KetQuaSso Ket, string? LyDo)`.
    Chọn record cho gọn — không dùng `out` trong hàm async được.
- `OperationCanceledException` phải NÉM tiếp (người dùng bấm Dừng), không nuốt thành `Loi`.

### Bước 3 — hàm THUẦN quyết định (test được không cần trình duyệt)

`internal static HanhDongSauThuSach QuyetDinhSauThuBanSach(KetQuaSso ket, bool daFallback)` trả
`enum HanhDongSauThuSach { ChayTiep, DungVongCaptcha, DangNhapLai, BaoLoi }`:

| ket | daFallback | → |
|---|---|---|
| Ok | * | ChayTiep |
| Captcha | * | DungVongCaptcha |
| Loi | false | DangNhapLai |
| Loi | true | BaoLoi |

Đặt cạnh `LoginAndReachPickerAsync`, `internal` (Tests đã có `InternalsVisibleTo`).

### Bước 4 — viết lại `LoginAndReachPickerAsync`

Thứ tự mới:

1. Log "Mở trình duyệt sạch + extension (dùng cookie hồ sơ nếu còn hạn)..." → `SsoVePickerAsync`.
2. `QuyetDinhSauThuBanSach(ket, daFallback: false)`:
   - `ChayTiep` → `return null` (đã ở picker) — **bỏ hẳn lượt Playwright**;
   - `DungVongCaptcha` → `return null` (caller kiểm `CaptchaSeen` như cũ);
   - `DangNhapLai` → sang bước 3.
3. Log rõ lý do fallback (đưa `LyDo` vào log) → `DongTrinhDuyetSach()` → `await Task.Delay(800, ct)`
   (nhả khoá hồ sơ trước khi Playwright mở — đối xứng với settle đang có sau khi Playwright đóng).
4. Chạy NGUYÊN khối Playwright hiện tại (`ShopeeLoginService.OpenAsync` + `TryLoginSubaccountAsync`,
   try/finally dispose session). `!entered` → trả chuỗi lỗi hiện có
   ("Đăng nhập subaccount chưa xong (nhập mã?). Bấm lại để thử tiếp.").
5. `await Task.Delay(800, ct)` → `SsoVePickerAsync` lần hai →
   `QuyetDinhSauThuBanSach(ket, daFallback: true)`:
   - `ChayTiep` → `return null`; `DungVongCaptcha` → `return null`; `BaoLoi` → trả `LyDo` (chuỗi lỗi).

Giữ nguyên hợp đồng của hàm: trả `null` = đã ở picker HOẶC captcha (caller phân biệt bằng `CaptchaSeen`);
trả chuỗi = lỗi. Hai caller (`RunLoginThenSliceAsync`, `RunAllShopsAsync`) KHÔNG phải sửa.

### Bước 5 — test

File mới `orders/XuLyDonShopee.Tests/OrdersBridgeSsoTests.cs`:

- Ma trận hàm thuần `QuyetDinhSauThuBanSach` (6 ca của bảng trên).
- Cầu nối thật (BridgeTestRig, KHÔNG mở trình duyệt) cho phần SSO — vì `SsoVePickerAsync` có phóng trình
  duyệt nên test đi qua `channel` trực tiếp, mô phỏng đúng ba phản hồi extension và kiểm chặng `AtSeller`:
  - `atSellerCentre` → chặng trả `true`;
  - `captcha` → `CaptchaSeen == true` và chặng trả `false` (không treo);
  - `error` → chặng fault `InvalidOperationException` chứa message của extension (đây là ca "cần đăng nhập
    lại" ở đời thật).
- Test "phá luật rồi chạy lại" theo quy trình: đổi bảng quyết định cho `Loi/false` → `BaoLoi` phải làm test đỏ.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` — **0 warning, 0 error**.
- [ ] `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj` — xanh toàn bộ, gồm test mới.
- [ ] Đọc diff: `LoginAndReachPickerAsync` KHÔNG còn gọi Playwright ở đường đi thành công (grep
      `ShopeeLoginService` trong hàm chỉ nằm trong nhánh fallback).
- [ ] `_channel.Start()` chỉ chạy khi `!Started` — hai lượt phóng trình duyệt trong một vòng không ném
      "Không mở được cổng cầu nối 47821".
- [ ] Hợp đồng trả về của `LoginAndReachPickerAsync` không đổi: hai caller giữ nguyên code.
- [ ] `nghiem-thu` phản biện, không còn lỗi đúng-thật chưa xử.
- [ ] Chạy thật một tài khoản: vòng đầu (cookie còn hạn) log "Mở trình duyệt sạch..." và KHÔNG có dòng
      "Đăng nhập Nền tảng tài khoản phụ bằng trình duyệt điều khiển (Playwright)".

## 5. Rủi ro & lưu ý

- **Cổng 47821 (bẫy chính).** Không gác `Started` là hỏng ngay ca fallback — mà ca đó chỉ xảy ra khi cookie
  hết hạn nên rất dễ lọt qua test tay.
- **Đóng bản sạch trước khi Playwright mở.** Phải dùng `DongTrinhDuyetSach()` chứ không `Process.Kill` trần:
  Brave fork tiến trình browser thật sang PID khác, kill theo handle trượt → hồ sơ còn bị giữ → Playwright
  chết ngay ở bước mở ("Trình duyệt thoát ngay khi khởi động", memory `browser-profile-singleton-handoff`).
- **Cookie chưa flush.** Vòng hiện tại kết thúc bằng kill cứng bản sạch, nên session cookie có thể chưa ghi
  xuống đĩa → vòng sau gặp form login → fallback. Luồng tự chữa (đúng thiết kế), nhưng nếu nhật ký production
  cho thấy fallback LIÊN TỤC thì gốc nằm ở chỗ đóng trình duyệt, không phải ở việc đảo thứ tự — ghi nhận,
  không xử trong plan này.
- **Chi phí ca xấu.** Cookie hết hạn: tốn thêm một lượt mở/đóng bản sạch (~20–30s) so với hiện tại. Chấp nhận
  được vì ca đó đằng nào cũng phải chờ người nhập mã.
- **`OperationCanceledException` phải xuyên qua** `SsoVePickerAsync` và nhánh fallback — nuốt nhầm thành `Loi`
  sẽ khiến bấm Dừng lại đi mở Playwright.
- **Không sửa `ResetState()` thành xoá cờ ngoài ý muốn**: nó gọi `_channel.ResetStages()` (đặt lại
  `CaptchaSeen = false`), nên phải đọc `CaptchaSeen` NGAY sau chặng, trước lượt `SsoVePickerAsync` kế tiếp.

---

## Báo cáo thực thi

<điền sau khi xong>
