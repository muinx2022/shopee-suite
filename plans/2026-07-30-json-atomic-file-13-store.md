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

**Xong toàn bộ. Build 2 solution 0 lỗi 0 warning; test 1449 (orders, đúng baseline) + 15 (Core, mới).**

### File tạo mới

| File | Nội dung |
|---|---|
| `suite/Shopee.Core/Infrastructure/JsonAtomicFile.cs` | Helper: `TryLoad<T>(path, options?, log?)` → `T?` (thiếu file / đọc lỗi / JSON hỏng / JSON `null` → `null`, không ném); `Save<T>(path, value, options?, log?)` → bool; `SaveText(path, json, log?)` = lõi ghi nguyên tử (tạo thư mục cha → `<file>.tmp` → `File.Move(overwrite)`). |
| `suite/Shopee.Core.Tests/` (csproj + 2 file test) | Project test đầu tiên của Shopee.Core (xunit, cùng version với `XuLyDonShopee.Tests`), `ProjectReference` thẳng Shopee.Core. 15 test. |
| `ShopeeSuite.sln` | Thêm `Shopee.Core.Tests` (thuần cộng thêm, nằm trong solution folder `suite`). |

### 13 store đã thay ruột (public API KHÔNG đổi dòng nào)

Cả 13 đều: `Load` → `JsonAtomicFile.TryLoad<T>`, `Save/SaveLocked` → `JsonAtomicFile.Save`/`SaveText`. Giữ nguyên tên method (kể cả `SaveLocked`), lock, đường dẫn file, `JsonSerializerOptions`, kiểu trả về, thứ tự phát event.

`AccountStore` · `AiConfigStore` · `BigSellerStore` · `HubClientConfigStore` · `HubServerConfigStore` · `MachineIdentity` · `AppModeStore` · `PerformanceSettingsStore` · `UpdateProductUiStore` · `OpProgressStore` · `KiotProxyPoolStore` · `ScrapeProgressStore` · `ScrapeTargetConfigStore`.

Tổng: **+59 / −203 dòng** ở 13 file store.

### 3 điểm phải giữ nguyên có chủ đích (đừng "dọn" ở đợt sau nếu chưa cân nhắc)

1. **UTF-8 CÓ BOM.** Cả 13 store ghi bằng `File.WriteAllText(..., Encoding.UTF8)` ⇒ file production ĐANG có BOM. Helper giữ đúng vậy. Đã kiểm chứng bằng cách tạm sửa helper thành `File.WriteAllText(tmp, json)` (không BOM, tức cách viết "hiển nhiên" nhất): **5/15 test đỏ ngay** → nếu lỡ tay, mọi file cấu hình trên máy người dùng đổi byte.
2. **`Changed?.Invoke()` nằm TRONG `try`** ở `AccountStore` / `BigSellerStore` / `KiotProxyPoolStore`: handler ném ⇒ `SaveLocked` trả false ⇒ `Add/Remove/ReplaceAll` HOÀN TÁC. Hành vi này hơi lạ nhưng là hành vi hiện có → giữ nguyên (đã ghi comment tại chỗ).
3. **`AiConfigStore` / `HubClientConfigStore` / `HubServerConfigStore` serialize TRONG lock, ghi đĩa NGOÀI lock.** Vì vậy 3 store này gọi `SaveText` (nhận sẵn chuỗi JSON) chứ không phải `Save<T>` — nếu ép dùng `Save<T>` thì lượt ghi đĩa bị kéo vào trong lock (đổi hành vi khoá).

Ngoài ra 5 store phân biệt "chưa có file" (GIỮ giá trị đang có) với "file hỏng" (về mặc định) nên vẫn còn `File.Exists` ở đầu `Load` — cố ý, vì `TryLoad` trả `null` cho cả hai ca: `AiConfigStore`, `HubClientConfigStore`, `HubServerConfigStore`, `PerformanceSettingsStore`, `UpdateProductUiStore`.

### Test (15, đều mới)

- `JsonAtomicFileTests` (11): thiếu file / JSON hỏng (+ gọi log) / nội dung `null` / dùng đúng `options` truyền vào / đọc được file có BOM / tạo thư mục cha / **ghi ra UTF-8 có BOM** / không để lại `.tmp` / ghi đè bản cũ / `SaveText` / đường dẫn hỏng → false + log (không ném).
- `JsonAtomicFileRoundTripTests` (4): ghi "file mẫu" bằng ĐÚNG khuôn cũ (chép nguyên `Directory.CreateDirectory` + `WriteAllText(tmp, …, Encoding.UTF8)` + `Move`) → đọc qua helper → ghi lại qua helper → **so BẰNG BYTE**. Dùng kiểu thật: `List<ShopeeAccount>` (AccountStore), `List<BigSellerAccount>` (BigSellerStore, có Shops/ColumnMap/RunConfig lồng nhau), `List<OpProgress>` (OpProgressStore, có `Dictionary<string,string?>` + `DateTimeOffset` + giá trị `null`), và bản sao DTO của AppModeStore (store DUY NHẤT có options ĐỌC khác options GHI).
- Test ghi vào thư mục tạm riêng, KHÔNG chạm `%AppData%\ShopeeSuite` và KHÔNG chạm singleton `*.Shared`.

### Lệch so với spec / cần phiên chính soi

1. **Worktree bị lệch nền 21 commit** — nhánh `worktree-agent-af5c998b4d741b873` đang ở `0d7918c` (v1.6.14), chưa có commit chứa plan nên `plans/2026-07-30-json-atomic-file-13-store.md` KHÔNG tồn tại. Cây sạch và HEAD là tổ tiên thật của `main` nên đã `git merge --ff-only main` (chỉ tua nhanh, không tạo commit) rồi mới làm. **Nếu quy trình tạo worktree đáng lẽ phải cắt từ `main` thì chỗ đó đang hỏng** — 3 agent còn lại có thể cũng đang ngồi trên nền cũ.
2. **Chỗ để test là project MỚI, không phải `XuLyDonShopee.Tests`.** Shopee.Core chưa từng có project test; `XuLyDonShopee.Tests` cố tình KHÔNG ref Shopee.Core mà LINK từng file (né Playwright/ClosedXML) — đi đường đó phải link ~7 file model (kể cả `OpProgressStore.cs`) vào project test của orders, vừa bẩn vừa đụng đúng file mà plan 3F sẽ sửa. → chọn tạo `suite/Shopee.Core.Tests`. **Hệ quả: `dotnet test orders/XuLyDonShopee.Tests` KHÔNG chạy 15 test này**; chạy cả hai bằng `dotnet test ShopeeSuite.sln`, hoặc thêm `dotnet test suite/Shopee.Core.Tests`.
3. **`ShopeeSuite.sln` là file dùng chung** — plan 3F cũng sẽ thêm project (`shared/Shopee.Toolkit`) vào đây. Thay đổi của tôi thuần cộng thêm nhưng lúc merge 2 nhánh có thể conflict văn bản ở `.sln`; gỡ dễ (giữ cả hai khối).
4. **`OpProgressStore` KHÔNG có phần "cột PG" như plan mô tả** — file `suite/Shopee.Core/Progress/OpProgressStore.cs` là store JSON thuần, khớp khuôn 100%; phần `store_*` cột Postgres nằm bên `server/`. Nên đã refactor bình thường, không phải ca đặc biệt. Không có store nào trong 13 phải bỏ lại vì "hành vi lạ".
5. ~~Cố ý KHÔNG bê cơ chế của `BigSellerCookieEngine.WriteAtomic`~~ → **ĐÃ SỬA TẠI CHỖ** (phiên chính giao thêm sau nghiệm thu sơ bộ). Chi tiết ở mục "Bổ sung" cuối báo cáo.
6. `PendingRewriteJournal` — ngoài phạm vi theo plan, không đụng. Rà lại toàn Shopee.Core: 4 chỗ còn `JsonSerializer.Deserialize` (`BackupService`, `PendingRewriteJournal`, `HubConfigSync`, `HubAiConfig`) đều KHÔNG phải store-file (payload mạng / backup / journal) → đúng là 13 store đã quét sạch.

---

### Bổ sung (giao thêm sau nghiệm thu sơ bộ): chống 2 tiến trình giẫm cùng file tạm

Sửa DUY NHẤT `JsonAtomicFile.SaveText`, không đụng gì khác:

- Tên tạm `<file>.<pid>-<guid:N>.tmp` thay cho `<file>.tmp` cố định.
- Retry `File.Move(overwrite: true)`: **4 lần, nghỉ 150ms** — lấy đúng tham số của `BigSellerCookieEngine.WriteAtomic`.
- Ghi hỏng → xoá tmp mồ côi (`try { File.Delete(tmp); } catch { }`), như WriteAtomic.

**Phát hiện quan trọng — KHÔNG copy được nguyên si bộ lọc exception của WriteAtomic.** WriteAtomic bắt `catch (IOException) when (attempt < 4)`. Nhưng khi 2 luồng/tiến trình cùng thay MỘT file đích, Windows trả `ERROR_ACCESS_DENIED` ⇒ .NET ném **`UnauthorizedAccessException`, KHÔNG phải `IOException`** ⇒ retry không bao giờ chạy, đúng vào ca nó sinh ra để đỡ. Bản copy nguyên si làm test đỏ ngay vòng đầu với thông báo `Access to the path is denied`. Đã nới thành `catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 4)`, giữ nguyên số lần/nhịp nghỉ.

⚠️ **`BigSellerCookieEngine.WriteAtomicBytes` đang dính đúng lỗ này** (chỉ bắt `IOException`) — tức cơ chế chống race của file cookie hiện KHÔNG hiệu lực với ca 2 tiến trình. Ngoài phạm vi việc này, **đề nghị mở việc riêng** để nới cùng kiểu.

Test thêm: `Save_HaiLuongCungGhiMotFile_CaHaiThanhCong_KhongMatFile` — 30 vòng × 2 luồng đồng bộ bằng `Barrier`, khẳng định **cả hai lượt `Save` trả true** (không phải chỉ "file còn tồn tại": hỏng ở đây nghĩa là `AccountStore.Add` hoàn tác oan), file đọc lại nguyên vẹn và là 1 trong 2 bản, không sót tmp mồ côi. Đã kiểm chứng test có răng bằng 2 phép đột biến: quay lại tên tmp dùng chung → đỏ; chỉ bắt `IOException` → đỏ. Chạy lại 3 lần liên tiếp đều xanh (không flake).

Kết quả sau bổ sung: **16 test Core** (thêm 1), orders vẫn **1449**, 2 solution 0 lỗi 0 warning.
