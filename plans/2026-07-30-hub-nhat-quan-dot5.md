# Plan: Hub web — nhất quán đợt 5 (component trùng, hằng, dọn nốt)

- **Ngày:** 2026-07-30
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

Các món nhất quán còn lại phía `server/Shopee.Hub.Web` (sau B2 + P4-hub):

1. **Cặp component trùng lưới sản phẩm:** `Components/Pages/AllData.razor` ↔ `Components/Shared/ProductGridPanel.razor` trùng khối phân trang + khối nút hành động → tách `ProductGridPager.razor` + `ProductGridActions.razor` dùng chung (markup kết quả PHẢI y hệt từng bên — 2 bên có thể lệch nhẹ, khác biệt tham số hoá).
2. **Hằng `OnlineThreshold` 45s** đang ghi ~3 nơi (grep `45` quanh online/last_seen — xác định chính xác) → 1 hằng dùng chung (vd trong `FleetStateService` hoặc `HubOptions`).
3. **`ProductApiEndpoints`**: ~21 khối lặp guard "Postgres chưa sẵn sàng" → helper/guard clause chung (1 method), hành vi + thông điệp trả về giữ nguyên.
4. **Xoá `FileStoreConfigService.RemoveShopeeAccount`** — hết caller sau khi endpoint `/accounts/remove` bị xoá ở B2 (grep xác nhận 0 caller trước khi xoá; `AppendShopeeAccounts` đã xoá rồi).
5. **`Logs.razor` hardening** (finding review bị bác về mức thấp nhưng vá rẻ): bọc thân vòng `PeriodicTimer` bằng try/catch (giữ `OperationCanceledException` thoát vòng; lỗi DB nhất thời → giữ bản cũ, tick sau thử lại); `DisposeAsync` bắt Exception chung quanh `await _pollTask`.
6. **`?status=all` của `/orders`**: giữ nguyên thiết kế sentinel (P4-hub đã làm đúng), chỉ bổ sung 1 dòng xmldoc giải thích vì sao cần sentinel (rỗng bị loại khỏi query).

## 2. Phạm vi

- Khu: CHỈ `server/Shopee.Hub.Web/**` (+ `server/Shopee.Hub.Web.Tests` nếu thêm test). KHÔNG đụng khu khác. KHÔNG đổi hành vi/markup/URL. KHÔNG commit.

## 3. Nghiệm thu

- [ ] `dotnet build server/ShopeeHub.sln` 0/0; `dotnet test server/Shopee.Hub.Web.Tests` ≥ 44.
- [ ] Grep: khối phân trang/nút chỉ còn 1 bản trong 2 component mới; `RemoveShopeeAccount` = 0 hit; guard Pg lặp = 0 (chỉ còn helper).
- [ ] So markup trước/sau 2 trang dùng lưới (mẫu cách agent P4-hub đã làm — đếm tag + literal class/style): lệch 0.
- [ ] Báo cáo: bảng từng mục + vị trí.

## 5. Rủi ro & lưu ý

- Bước 0: worktree phải ở commit chứa plan này hoặc mới hơn (`git merge --ff-only main` nếu không).
- ProductGridPanel nằm trong workspace master-detail (bẫy tick-clobber) — component mới nhận state qua `[Parameter]`, không giữ state riêng.
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

(chưa)
