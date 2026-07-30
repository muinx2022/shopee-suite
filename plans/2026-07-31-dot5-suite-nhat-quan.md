# Plan: Đợt 5 — suite: UtcNow, log nuốt lỗi, mojibake, ConfigureAwait

- **Ngày:** 2026-07-31
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Các việc

**A. `SearchTaskStore` → `DateTime.UtcNow`:** `suite/Shopee.Module.Search/Engine/SearchTaskStore.cs` có ~7 chỗ `DateTime.Now` dùng làm khoá thời gian/ORDER BY trong SQLite → đổi `UtcNow`. RÀ TÁC ĐỘNG trước: dữ liệu cũ trong DB đang là giờ local — nếu cột được so sánh/sắp xếp trộn cũ-mới thì ghi rõ hệ quả (một lần lệch 7h khi chuyển) + nếu có chỗ HIỂN THỊ trực tiếp giá trị này ra UI thì quy đổi lại giờ local khi hiển thị. Liệt kê từng chỗ.

**B. Log các `catch {}` bước điền sản phẩm:** `suite/Shopee.Module.UpdateProduct/Engine/BigSellerProductUpdateRunner.cs` ~7 chỗ `catch { }` nuốt im trong các bước điền form → thêm log qua logger sẵn có của runner (throttle nếu trong vòng lặp chặt). KHÔNG đổi luồng điều khiển (vẫn nuốt, chỉ thêm log).

**C. Dọn mojibake:** ~50 ký tự U+FFFD trong comment cũ của MB (nay nằm trong `BraveInstanceSession*.cs`/`SessionMonitor.cs`/`RunnerSwLifecycle.cs`… — grep `�`) → viết lại comment tiếng Việt đúng nghĩa theo ngữ cảnh code (chỉ sửa COMMENT, không sửa code).

**D. `ConfigureAwait(false)` trong `suite/Shopee.Core`:** rà các `await` thiếu `.ConfigureAwait(false)` trong project Core (thư viện, không UI) → bổ sung cho nhất quán. CHỈ Shopee.Core, không đụng Shopee.Suite/module (có UI context). Nếu chỗ nào await xong đụng callback UI-marshal thì giữ nguyên + ghi chú.

**E. Hằng cổng 9111:** xác nhận cổng WS Search chỉ còn khai báo 1 nơi (default `WebSocketServer` Toolkit = 9111 sau 3F); nếu còn literal 9111 rải rác phía suite → trỏ về 1 const. (Phía orders 47821 do agent khác lo.)

## 2. Phạm vi & nghiệm thu

- Khu: `suite/Shopee.Module.Search/**`, `suite/Shopee.Module.UpdateProduct/**`, `suite/Shopee.Module.MultiBrave/**` (chỉ comment), `suite/Shopee.Core/**` (chỉ ConfigureAwait + hằng). KHÔNG đụng `orders/**`, `server/**`, `extensions/**`, `shared/**`, `suite/Shopee.Suite/**`, `suite/Shopee.Core/Coordination/**` (agent khác đang sửa song song).
- [x] Build ShopeeSuite.sln 0/0; test orders 1471 + Core.Tests 61 giữ nguyên.
- [x] Grep `�` trong suite = 0; `DateTime.Now` trong SearchTaskStore = 0.
- [x] Bảng liệt kê từng chỗ đổi trong báo cáo.
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Build:** `dotnet build ShopeeSuite.sln --no-incremental` → 0 Warning / 0 Error. Thêm `server/Shopee.Hub.Web` (không nằm trong sln nhưng link thẳng `ProductGridEngine.cs` + `IProductDataOps.cs` từ Core) → 0/0.
**Test:** Shopee.Core.Tests 61/61 xanh; XuLyDonShopee.Tests 1471/1471 xanh.

### A. `SearchTaskStore` → UTC

Gom 7 chỗ `DateTime.Now.ToString("O")` về 1 helper `Stamp()` = `DateTime.UtcNow.ToString("O")`:

| Dòng (bản mới) | Hàm | Cột ghi |
|---|---|---|
| 35 | `CreateTask` | `search_tasks.created_at` + `updated_at` |
| 93 | `SaveProduct` | `search_tasks.updated_at` |
| 120 | `UpdateCheckpoint` | `search_tasks.updated_at` |
| 140 | `UpdateStatus` | `search_tasks.updated_at` |
| 300 | `UpsertCategories` | `categories.first_seen` / `last_seen` |
| 486 | `SaveShopProducts` | `shop_products.scanned_at` |
| 608 | `AddProductParams` | `task_products.updated_at` |

Rà tác động:
- **So sánh/ORDER BY:** 3 chỗ — `GetResumableTaskId` và `GetLinkProgress` (`ORDER BY updated_at DESC, id DESC`), `GetAllShopProducts` (`ORDER BY scanned_at DESC, id DESC` để khử trùng itemId giữ bản mới nhất). Đây là so sánh CHUỖI. Hệ quả 1 lần khi chuyển: bản ghi cũ là giờ local không hậu tố (`2026-07-31T14:00:00…`), bản mới là UTC có Z (`2026-07-31T07:00:00…Z`) → trong cửa sổ ~7h, dòng cũ có thể xếp TRÊN dòng vừa ghi (resume nhầm task cũ / thấy tiến độ lượt cũ / giữ bản scrape cũ hơn). Tự hết ngay khi mọi dòng liên quan đã được ghi lại theo UTC. Đã ghi chú ngay trên `Stamp()`.
- **Hiển thị:** đúng 1 chỗ — `SearchView.axaml:339` bind thẳng `CategoryRow.LastSeen` (cột "Lần cuối"). `SearchView.axaml` nằm ngoài khu được sửa nên đã quy đổi ở NGUỒN: `GetCategories()` bọc `first_seen`/`last_seen` qua `ToLocalDisplay()` (parse `RoundtripKind`; `Kind=Utc` → `ToLocalTime()`, `Kind=Unspecified` = bản ghi cũ đã là giờ local → giữ nguyên; parse hỏng → trả nguyên chuỗi).
- **Không đụng:** `SearchTaskRecord.CreatedAt/UpdatedAt` (chỉ có `FileRunCoordinator` đọc `ResumeCategoryIndex`/`CurrentPage`/`ProductCount`, không đọc 2 mốc thời gian, không hiển thị).

### B. Log `catch { }` bước điền form (`BigSellerProductUpdateRunner.cs`)

7 chỗ trong `ProcessProductAsync`, đổi `catch { }` → `catch (Exception ex) { _log($"  ↳ … lỗi: {ex.Message}"); }`, GIỮ nguyên luồng (vẫn nuốt, không rethrow):

| Bước | Dòng | Nội dung log |
|---|---|---|
| [2] radio "Tải lên hình ảnh" | 841 | `Tick radio 'Tải lên hình ảnh' lỗi` |
| [4] MD5 | 890 | `Đồng bộ ảnh (MD5) lỗi` |
| [5] SKU cha | 906 | `Điền SKU cha lỗi` |
| [6] brand | 909-910 | `Chọn thương hiệu 'No Brand' lỗi` |
| [10] shipping | 952 | `Chọn vận chuyển 'Nhanh' lỗi` |
| [11] weight | 966 | `Điền cân nặng lỗi` |
| [12] video (vòng 3 lượt) | 977-978 | `Upload video lượt {n}/3 lỗi` — kèm số lượt, trần 3 dòng/SP nên không cần throttle |

2 `catch { }` lồng bên trong ([2] chờ `spc_box`, [4] chờ `Md5CompleteStatus`) là chờ best-effort có Timeout → KHÔNG log, chỉ thêm chú thích `/* chờ best-effort */` (theo lối `catch { /* overlay best-effort */ }` sẵn có trong file).

### C. Mojibake

16 dòng comment ở 4 file MultiBrave (`PageCdpHelper.cs` 1, `RunnerExtensionRpc.cs` 3, `RunnerSwLifecycle.cs` 8, `UnpackedExtensionId.cs` 1) → viết lại tiếng Việt theo ngữ cảnh code, KHÔNG đụng code. Grep `U+FFFD` trong mã nguồn `suite/` = 0 (còn lại chỉ là nhị phân Playwright trong `bin/`).

### D. `ConfigureAwait(false)` trong `Shopee.Core`

Thêm 121 chỗ ở 11 file: `Ai/NameRewriteEngine.cs` (4), `BigSeller/BigSellerLoginForm.cs` (27), `BigSeller/BigSellerLoginRunner.cs` (26), `Cdp/CdpClient.cs` (5), `Cdp/CdpHumanInput.cs` (24), `Cdp/CdpSession.cs` (18), `Products/HubApiProductDataOps.cs` (3), `Proxy/KiotProxyClient.cs` (7), `Proxy/ProxyPool.cs` (3), `Scrape/VideoDownloader.cs` (6). `Coordination/**` để nguyên theo phạm vi.

**NGOẠI LỆ có chủ đích — `Products/ProductGridEngine.cs` giữ nguyên 22 `await`:** hàm confirm do UI tiêm vào PHẢI chạy trên ngữ cảnh UI (client Avalonia = `Dialogs.ConfirmAsync` mở cửa sổ; hub web Blazor = `JS.InvokeAsync<bool>("confirm", …)`). Trong `SaveRowAsync`, `await _ops.SkuExistsAsync(...)` đứng TRƯỚC `await _confirm(...)` → bỏ ngữ cảnh ở await đó sẽ gọi confirm sai thread/circuit. Đã ghi chú lý do trong XML doc của class. (Sự kiện `Changed` thì cả 2 phía đều tự marshal — `UiThread.Post` / `InvokeAsync(StateHasChanged)` — nên không phải lý do chính.)

`await using` giữ nguyên (không gắn `ConfigureAwait` lên chính disposable) theo lối sẵn có ở `BigSellerCookieEngine.cs:70`; await LỒNG bên trong vẫn được gắn (`VideoDownloader.cs:48`).

### E. Hằng cổng 9111

Kết luận: **không có literal 9111 nào còn tác dụng phía suite**. Cổng WS Search là cổng ĐỘNG cấp qua `PortAllocator.Reserve()` trong `SearchSession.RunAsync`, truyền thẳng vào `new WebSocketServer(port)` và vào URL `#_ss_ws={port}` — nên default `9111` của `Shopee.Toolkit.Ws.WebSocketServer` không bao giờ được dùng. Chỗ duy nhất còn literal ở suite là `LauncherSettings.WsPort = 9111`, và property này **không được đọc ở bất kỳ đâu trong repo** (đã grep toàn repo `WsPort`/`9111`). KHÔNG trỏ về const (trỏ một field chết vào const không giải quyết gì) và KHÔNG xoá (là field serialize trong settings.json) — chỉ thêm XML doc đánh dấu nó đã chết + chỉ ra 2 nơi 9111 còn ý nghĩa (`extensions/shopee-search/core.js` `DEFAULT_WS_PORT`, Toolkit `WebSocketServer`), cả hai đều NGOÀI khu được giao. Phiên chính quyết định có xoá field hay không.

### Cần soi lại

1. **A — đổi format cột "Lần cuối":** trước đây bind thẳng chuỗi ISO `2026-07-31T14:23:45.1234567` (27 ký tự, cột 120px cắt cụt); nay `ToLocalDisplay` trả `31/07/2026 14:23` (theo lối `DataRowItem.UpdatedLocal`). Đây là thay đổi hiển thị vượt ngoài chữ nghĩa của plan — khai báo để phiên chính duyệt. `FirstSeen` cũng đổi theo cho nhất quán (không được bind ở đâu).
2. **B — log khi huỷ:** giữ đúng yêu cầu "không đổi luồng điều khiển" nên `catch (Exception ex)` vẫn nuốt cả `OperationCanceledException`; lúc user bấm Dừng có thể ra vài dòng `… lỗi: A task was canceled.`. Muốn im thì phải thêm `catch (OperationCanceledException) { throw; }` — nhưng đó là ĐỔI luồng, plan cấm.
3. **D — quét bằng script + soi tay:** script chèn `ConfigureAwait` tự động, đã soi toàn bộ 126 dòng thêm; 6 chỗ script đặt sai vị trí (`EvaluateAsync<T>` × 4 ở `HotmailOtpReader.cs`, `_ws!.SendAsync` × 2 ở `CdpSession.cs`) đã sửa tay. Script cũng làm mất CRLF ở 11 file → đã khôi phục CRLF (git diff sau đó sạch, `HotmailOtpReader.cs` trở về y hệt bản gốc).
4. **Test lẻ chập chờn:** một lượt chạy `XuLyDonShopee.Tests` fail 1 ca `ProxyRepositoryTests.InsertMany_ThemNhieu`; chạy riêng + 2 lượt full sau đó đều xanh 1471/1471. Project test orders KHÔNG tham chiếu `suite/Shopee.Core` (chỉ orders + shared, đều không bị đụng) → nhiều khả năng flaky sẵn có, không do đợt này.
