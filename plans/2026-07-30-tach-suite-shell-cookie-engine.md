# Plan: Tách ScrapeViewModel + OrdersModuleHost + BigSellerCookieEngine partial (đợt 4 — suite shell)

- **Ngày:** 2026-07-30
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

3 file quá cỡ còn lại phía suite shell/Core (đo 30/07): `suite/Shopee.Suite/Modules/Scrape/ScrapeViewModel.cs` ~875 dòng; `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs` ~1.073 dòng (host module Đơn hàng trong suite: wiring services, heartbeat/hub, prepare-stats — phần vừa sửa B2, ActivityLog.Dispose ở StopAsync); `suite/Shopee.Core/BigSeller/BigSellerCookieEngine.cs` ~800 dòng (sau 3C + vá UnauthorizedAccessException).

Mục tiêu (refactor thuần, KHÔNG đổi hành vi):
1. `ScrapeViewModel` → dời `SessionAccountPool` + `RunSession` (class lồng/khối lớn) ra file riêng cùng thư mục; VM còn ≤ ~600 dòng.
2. `OrdersModuleHost` → tách theo trục thực tế (đọc file rồi quyết, đề xuất: wiring/bootstrap services riêng, cụm hub (heartbeat/prepare-stats/push) riêng, lifecycle giữ ở host); mỗi file ≤ ~600. GIỮ NGUYÊN các fix B2 (WirePrepareStatsRead cộng dồn, Log.Dispose).
3. `BigSellerCookieEngine` → 3 partial cùng class: `BigSellerCookieEngine.CookieFile.cs` (đọc/ghi/parse file + WriteAtomic), `BigSellerCookieEngine.Importer.cs` (import 2 transport + write-back), `BigSellerCookieEngine.SessionPolicy.cs` (luật giữ token/so iat) — thuần di chuyển member, KHÔNG đổi API/hành vi.

## 2. Phạm vi

- Khu: `suite/Shopee.Suite/**` + `suite/Shopee.Core/BigSeller/**`. KHÔNG đụng `suite/Shopee.Module.*` (agent khác đang tách MultiBrave), `suite/Shopee.Core/Scrape/**`, `suite/Shopee.Core.Tests/**` (khu agent MB), `orders/**`, `server/**`, `extensions/**`, `shared/**`.
- KHÔNG commit.

## 3. Nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` 0/0; test orders 1440 + Core.Tests 43 giữ nguyên (chú ý: agent MB có thể thêm test Core song song — chạy con số của worktree bạn, không tụt so lúc bắt đầu).
- [ ] 3 file gốc đạt mốc dòng nêu trên; không file mới > ~700.
- [ ] Bảng "khối → file" trong báo cáo; XAML binding của ScrapeViewModel không đổi property công khai.

## 5. Rủi ro & lưu ý

- Bạn ở worktree riêng — bước 0: `git log --oneline -1` phải là commit chứa plan này hoặc mới hơn, không thì `git merge --ff-only main`.
- OrdersModuleHost là chỗ 2 đợt bug vừa sửa — di chuyển nguyên khối, giữ thứ tự wiring.
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

(chưa)
