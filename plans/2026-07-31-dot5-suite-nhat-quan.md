# Plan: Đợt 5 — suite: UtcNow, log nuốt lỗi, mojibake, ConfigureAwait

- **Ngày:** 2026-07-31
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Các việc

**A. `SearchTaskStore` → `DateTime.UtcNow`:** `suite/Shopee.Module.Search/Engine/SearchTaskStore.cs` có ~7 chỗ `DateTime.Now` dùng làm khoá thời gian/ORDER BY trong SQLite → đổi `UtcNow`. RÀ TÁC ĐỘNG trước: dữ liệu cũ trong DB đang là giờ local — nếu cột được so sánh/sắp xếp trộn cũ-mới thì ghi rõ hệ quả (một lần lệch 7h khi chuyển) + nếu có chỗ HIỂN THỊ trực tiếp giá trị này ra UI thì quy đổi lại giờ local khi hiển thị. Liệt kê từng chỗ.

**B. Log các `catch {}` bước điền sản phẩm:** `suite/Shopee.Module.UpdateProduct/Engine/BigSellerProductUpdateRunner.cs` ~7 chỗ `catch { }` nuốt im trong các bước điền form → thêm log qua logger sẵn có của runner (throttle nếu trong vòng lặp chặt). KHÔNG đổi luồng điều khiển (vẫn nuốt, chỉ thêm log).

**C. Dọn mojibake:** ~50 ký tự U+FFFD trong comment cũ của MB (nay nằm trong `BraveInstanceSession*.cs`/`SessionMonitor.cs`/`RunnerSwLifecycle.cs`… — grep `�`) → viết lại comment tiếng Việt đúng nghĩa theo ngữ cảnh code (chỉ sửa COMMENT, không sửa code).

**D. `ConfigureAwait(false)` trong `suite/Shopee.Core`:** rà các `await` thiếu `.ConfigureAwait(false)` trong project Core (thư viện, không UI) → bổ sung cho nhất quán. CHỈ Shopee.Core, không đụng Shopee.Suite/module (có UI context). Nếu chỗ nào await xong đụng callback UI-marshal thì giữ nguyên + ghi chú.

**E. Hằng cổng 9111:** xác nhận cổng WS Search chỉ còn khai báo 1 nơi (default `WebSocketServer` Toolkit = 9111 sau 3F); nếu còn literal 9111 rải rác phía suite → trỏ về 1 const. (Phía orders 47821 do agent khác lo.)

## 2. Phạm vi & nghiệm thu

- Khu: `suite/Shopee.Module.Search/**`, `suite/Shopee.Module.UpdateProduct/**`, `suite/Shopee.Module.MultiBrave/**` (chỉ comment), `suite/Shopee.Core/**` (chỉ ConfigureAwait + hằng). KHÔNG đụng `orders/**`, `server/**`, `extensions/**`, `shared/**`, `suite/Shopee.Suite/**`, `suite/Shopee.Core/Coordination/**` (agent khác đang sửa song song).
- [ ] Build ShopeeSuite.sln 0/0; test orders 1471 + Core.Tests 61 giữ nguyên.
- [ ] Grep `�` trong suite = 0; `DateTime.Now` trong SearchTaskStore = 0.
- [ ] Bảng liệt kê từng chỗ đổi trong báo cáo.
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

(chưa)
