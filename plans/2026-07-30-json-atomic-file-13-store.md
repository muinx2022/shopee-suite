# Plan: `JsonAtomicFile` — khử khuôn Load/Save lặp ở 13 store JSON (3E)

- **Ngày:** 2026-07-30
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

Kiểm chứng 30/07: 13 store JSON trong `suite/Shopee.Core` vẫn lặp cùng khuôn "Load (đọc file, deserialize, nuốt lỗi) + lock + Save (serialize, ghi atomic .tmp→Move)": `AccountStore`, `AiConfigStore`, `BigSellerStore`, `HubClientConfig`, `HubServerConfig`, `MachineIdentity`, `AppModeStore`, `PerformanceSettingsStore`, `UpdateProductUiStore`, `OpProgressStore`, `KiotProxyPoolStore`, `ScrapeProgressStore`, `ScrapeTargetConfigStore` (tên method chứa "SaveLocked" còn ở 6 file). `PendingRewriteJournal` dạng journal — NGOÀI phạm vi.

Mục tiêu: helper `JsonAtomicFile` dùng chung, mỗi store chỉ còn khai báo đường dẫn + type + (tuỳ chọn) JsonSerializerOptions.

## 2. Phạm vi

- **Làm:** helper mới + refactor NỘI BỘ 13 store.
- **Không làm (QUAN TRỌNG):** KHÔNG đổi public API của bất kỳ store nào (signature, hành vi trả về, sự kiện) — caller khắp suite không được phải sửa (tránh đụng khu các agent khác đang làm song song: `suite/Shopee.Suite/**`, `suite/Shopee.Core/Coordination/OrderDtos.cs|HubRoutes.cs|HubClient.cs`, 4 module, orders, server). Việc "chuẩn hoá API trả bool / event ngoài lock" của plan 25/07 mục 3E → DỜI sang đợt 5.
- KHÔNG đổi format JSON trên đĩa (round-trip y hệt: options serialize, NoBom, indent…) — file config production đang dùng.

## 3. Các bước thực hiện

1. `suite/Shopee.Core/Infrastructure/JsonAtomicFile.cs`: `TryLoad<T>(path, options?) → T?` (file thiếu/hỏng → default + không ném; giữ đúng hành vi nuốt-lỗi hiện tại của từng store — bản nào ĐANG log lỗi thì cho callback log), `Save<T>(path, value, options?)` ghi atomic (.tmp + Move, tạo thư mục cha) — đối chiếu cách WriteAtomic hiện có (BigSellerCookieEngine) để nhất quán.
2. Từng store một: thay ruột Load/Save bằng helper, GIỮ nguyên lock hiện có của store, giữ nguyên tên file/đường dẫn/options serialize. Store nào có biến thể (vd OpProgressStore có phần cột PG/logic riêng) → chỉ thay đúng phần file-JSON, phần khác giữ.
3. So sánh round-trip: với mỗi store, test load file mẫu → save → nội dung tương đương (bỏ qua khác biệt whitespace nếu options y hệt thì phải BẰNG byte).

## 4. Tiêu chí nghiệm thu

- [ ] Build 2 solution 0 lỗi 0 warning; test không tụt.
- [ ] Public API 13 store không đổi: `git diff` không có thay đổi nào ngoài ruột private + using.
- [ ] Grep khuôn cũ (File.ReadAllText + JsonSerializer.Deserialize trong store) chỉ còn qua helper; "SaveLocked" tự viết = 0 (còn tên method cũ thì giữ tên, ruột gọi helper).
- [ ] Test round-trip cho ≥3 store đại diện (AccountStore, BigSellerStore, OpProgressStore).

## 5. Rủi ro & lưu ý

- Bạn làm trong worktree riêng; không đọc/ghi cây chính; tránh mọi file ngoài 13 store + helper mới.
- Đây là refactor thuần — nếu store nào có hành vi Load/Save "lạ" không khớp khuôn (migrate version, backup…), GIỮ NGUYÊN store đó + ghi vào báo cáo thay vì ép vào helper.
- KHÔNG commit; xong điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

(chưa)
