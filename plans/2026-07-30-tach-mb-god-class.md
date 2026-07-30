# Plan: Tách god class module MultiBrave + test ParseLoginLine (đợt 4 — suite MB)

- **Ngày:** 2026-07-30
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

Sau 3A-3D, hai file lớn nhất module MultiBrave vẫn quá cỡ:
- `suite/Shopee.Module.MultiBrave/Engine/BraveInstanceSession.cs` (~1.900 dòng) — vòng đời 1 cửa sổ Brave scrape: process, kho account, proxy Kiot xoay vòng, login Shopee (flow đã gọi ShopeeAuth), guard token BigSeller, monitor.
- `suite/Shopee.Module.MultiBrave/Engine/ExtensionRunnerAutomation.cs` (~1.850 dòng) — điều khiển extension runner qua CDP (đã dùng CdpClient.ListTargets sau 3C).

Mục tiêu (refactor thuần): tách theo trục plan 25/07, session giữ làm facade:
1. `BraveInstanceSession` → **`BraveProcessController`** (launch/kill/teardown — đã gọi BraveTeardown), **`KiotProxyRotator`** (xoay proxy Kiot), **`ShopeeSessionBootstrapper`** (flow login Shopee — phần gọi ShopeeAuth/FillShopeeLoginFormAsync, GIỮ NGUYÊN JS typeHuman), **`BigSellerTokenGuard`** (import/write-back muc_token qua BigSellerCookieEngine), **`SessionMonitor`** (timer giám sát). Cấu trúc được phép chỉnh theo thực tế — ghi rõ.
2. `ExtensionRunnerAutomation` → **`RunnerSwLifecycle`** (wake/discover/attach service worker), **`RunnerExtensionRpc`** (gửi lệnh + chờ kết quả), dời **`ResolveEndRowAsync`/`FetchSheetLinksAsync`** sang lớp dữ liệu cạnh `ScrapeWorkbook` (Shopee.Core/Scrape).
3. **Test `ShopeeAuth.ParseLoginLine`** vào `suite/Shopee.Core.Tests` (món nợ 3A): bộ case theo plan 3A mục 4 — dòng MB chuẩn (SPC_F= + '|' trong cookie), dòng SE (không prefix, '=' thứ 2), dòng CA 2 phần không cookie, thiếu password (SE pass, MB/CA fail), cookie prefix lạ (MB nay nhận), ≥8 ca.

## 2. Phạm vi

- **Làm:** 3 việc trên; khu `suite/Shopee.Module.MultiBrave/**` + `suite/Shopee.Core/Scrape/**` (chỗ nhận 2 method dời) + `suite/Shopee.Core.Tests/**`.
- **Không làm:** KHÔNG đổi hành vi/delay/thứ tự thao tác (anti-bot); KHÔNG đụng `orders/**`, `server/**`, `extensions/**`, `shared/**`, module khác; KHÔNG commit.

## 3. Các bước & tiêu chí

1. Đọc 2 file; tách từng khối, build sau mỗi khối; DI qua constructor, session giữ field/property công khai cũ (caller ngoài không phải đổi trừ using).
2. Test ParseLoginLine (mục 3).
3. Nghiệm thu:
- [ ] Build 0/0; `dotnet test suite/Shopee.Core.Tests` ≥ 43 + test mới; orders 1440 giữ nguyên.
- [ ] `BraveInstanceSession.cs` ≤ ~700 dòng (facade), `ExtensionRunnerAutomation.cs` ≤ ~700; không file mới > ~800.
- [ ] Bảng "khối → file mới" + cam kết delay/thứ tự không đổi trong báo cáo.

## 5. Rủi ro & lưu ý

- Vùng anti-bot nhạy nhất repo — chỉ DI CHUYỂN. Timer/monitor có race đã sửa (1A.3/1A.4 — Interlocked, try/catch async-void): giữ nguyên các guard đó.
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

(chưa)
