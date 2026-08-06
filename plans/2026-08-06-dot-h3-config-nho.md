# Plan: Đợt H3 — Config nhỏ (tab log per-acc, khoảng nghỉ Scrape, tham số per-shop Update)

- **Ngày:** 2026-08-06
- **Trạng thái:** chờ làm (sau H2)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

3 mục nhỏ chốt đợt: (1) hiển thị dữ liệu log per-account đã được ghi sẵn mà chưa màn nào bind; (2) đưa 2 nhóm giá trị cứng thành cấu hình — khoảng nghỉ Scrape và bộ 3 tham số điền form Update (đổi shop/ngành là phải build lại app, vô lý).

## 2. Phạm vi

- **Làm:** 3 mục phần 3.
- **Không làm:** không thêm tính năng khác; không deploy/release.

### 2b. HIỆN TRẠNG CÂY (cập nhật 06/08 sau A–G + H1/H2 — dò theo symbol)

- **Bo góc suite đã chuẩn hoá 4/6** (đợt G2) và có style chip dùng chung `headerStatusChip` (G1): dải chip
  của H3.1 phải theo đúng 2 nấc bo đó, tái dùng style `subtabItem`/`subtabTray` sẵn có.
- **`LauncherRunnerLoop` đã bị đợt A sửa** (chỉ ghi `LastCompletedRow` khi `scrapeOk`) và đợt B gỡ cờ ma
  `preferSuggestedResume` — H3.2 đụng đúng file này, đọc kỹ trước khi thêm tham số nghỉ.
- **`AppSession`/`PortAllocator` đã dồn về `Shopee.Core/Infrastructure`** (đợt C1) — module không còn bản riêng.
- **`BigSellerProductUpdateRunner` đã tách 5 partial** (D1: `.Fields/.Save/.Selectors/.Process/.Overlay/.Listing`):
  3 hằng `StockValue/WeightValue/'Nhanh'` của H3.3 nằm ở partial nào thì sửa đúng chỗ đó, đừng dồn về file gốc.
- **Tham số hub-run-params hiện có** (Processes/FrameSize/Reload, quy ước 0 = dùng cấu hình client) là khuôn bắt
  buộc cho H3.2; `/dispatch` vừa được F8 rút nhãn + chuyển chú thích vào `title` — field mới theo đúng kiểu đó.
- **Hợp đồng sync field shop BigSeller**: H1/H2 KHÔNG đụng `SharedSignature`; H3.3 là chỗ đụng đầu tiên trong
  loạt này — đọc memory `bigseller-shop-field-sync-contract` trước khi viết dòng nào.
- **Test nền để so:** orders 1506 · Core 83 · hub 80 (+ phần H2 thêm). Số chỉ được TĂNG.

## 3. Các bước thực hiện

### H3.1 (Suite) Tab log theo từng tài khoản BigSeller
- Hạ tầng có sẵn và đang chạy: `ModuleViewModelBase.AccountLogs` + `LogAcc` ghi buffer riêng mỗi acc; comment ModuleViewModelBase (~:27–29) ghi rõ "tab log per-acc đợt sau bind vào đây".
- Làm UI ở panel log của các màn module (làm Scrape trước, khuôn dùng lại được thì áp thêm Update/Import nếu rẻ): dải chip/tab ngang trên panel log — "Tất cả" + mỗi acc một chip (tên acc + đếm dòng); chọn chip lọc log theo acc đó. Style chip theo theme (subtab). Vẫn giữ hành vi hiện tại khi chọn "Tất cả".
- Buffer per-acc có trần sẵn (theo khuôn LogBuffer 500 dòng) — chỉ bind, không đổi cơ chế ghi.

### H3.2 (Suite + Hub) Khoảng nghỉ giữa link của Scrape thành tham số
- Hiện `MinRestMs`/`MaxRestMs` hardcode 120–240s (`LauncherRunnerLoop.cs` ~:8–9).
- Client: thành setting của Scrape (cùng chỗ Processes/FrameSize/Reload trong config client — đọc khuôn hiện có), UI ô nhập ở màn cấu hình Scrape (giây, min ≤ max, validate).
- Hub giao việc kèm tham số: thêm RestMinSec/RestMaxSec vào bộ tham số lượt giao (khuôn `hub-run-params` sẵn: Processes/FrameSize/Reload với quy ước **0 = dùng cấu hình client** — theo memory `hub-run-params-brave-budget`), UI ô nhập ở panel /dispatch.
- Quy ước 0=client-default phải giữ nguyên khuôn; client cũ nhận field mới phải bỏ qua an toàn (đọc cách các tham số hiện có được truyền để làm y hệt).

### H3.3 (Hub + Suite) Bộ 3 tham số điền form Update thành cấu hình per-shop trên Hub
- Hiện hardcode trong `BigSellerProductUpdateRunner`: tồn kho `StockValue='30069'`, cân nặng `WeightValue='500'`, kênh vận chuyển `'Nhanh'`.
- Đây là **cấu hình CHUNG toàn fleet, per-shop, chủ sở hữu = Hub** → thêm 3 field vào model shop BigSeller đồng bộ Hub→client. **ĐỌC KỸ memory `bigseller-shop-field-sync-contract` trước khi viết**: Hub pull đè `Shops` nguyên khối; field CHUNG (như 3 field này) thêm vào SharedSignature bình thường, KHÔNG thuộc nhóm per-máy phải graft — nhưng lớp lỗi quanh hợp đồng này đã lặp 5 lần, làm xong phải kiểm tra: nhập ở Hub → client thấy không cần restart (bẫy UI-không-vẽ-lại b13ed00).
- UI nhập: hub Fleet, tab Cấu hình của shop (nơi các field shop đang sửa được). Giá trị rỗng = dùng mặc định hiện tại (30069/500/Nhanh — thành hằng DEFAULT có tên, khớp luật "số trần phải có tên").
- Runner đọc từ shop config với fallback default; log giá trị dùng ở đầu lượt (1 dòng) để chẩn đoán.

## 4. Tiêu chí nghiệm thu

- [ ] Build 2 solution 0 warning; 3 bộ test xanh.
- [ ] H3.2: test/kiểm chứng quy ước 0=client-default đúng khuôn tham số cũ (đọc + đối chiếu code path, có test phía hub nếu bộ test assignments sẵn khuôn).
- [ ] H3.3: field mới đi qua đúng hợp đồng sync (chỉ ra trong báo cáo: SharedSignature ở đâu, graft không cần vì là field chung); test sync nếu có khuôn test HubConfigSync.
- [ ] H3.1: chạy app, màn Scrape hiện chip acc khi có log per-acc (kiểm bằng chạy giả lập/log tay nếu không chạy scrape thật được — ghi rõ đã kiểm tới đâu).
- [ ] Hành vi mặc định KHÔNG đổi khi user chưa đụng config mới (nghỉ vẫn 120–240s; form vẫn 30069/500/Nhanh).

## 5. Rủi ro & lưu ý

- H3.3 là hợp đồng sync nhiều sẹo nhất repo — nếu thấy phải đụng nhóm per-máy/graft thì DỪNG, ghi lại, hỏi phiên chính.
- H3.2 đừng đổi đơn vị âm thầm (ms trong code, giây trên UI — quy đổi tại biên UI, đặt tên biến có đơn vị).
- KHÔNG commit/deploy/release.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Người thực thi:** `opus-executor` · **Ngày:** 2026-08-06 · **Cây:** commit `ae8427a`, KHÔNG commit/deploy/release.

### A. Đã hoàn thành

#### H3.1 — Tab log theo từng tài khoản BigSeller

Hạ tầng `AccountLogRegistry`/`LogAcc` giữ NGUYÊN (chỉ bind, không đổi cơ chế ghi).

| File | Việc |
|---|---|
| `suite/Shopee.Suite/Modules/Workspace/LogFilterChipViewModel.cs` (mới) | 1 ô chip = (accountId, tên, `LogBuffer`); nhãn "tên (số dòng)" tự cập nhật theo `CollectionChanged`; `Dispose()` gỡ handler khi dựng lại dải |
| `suite/Shopee.Suite/Modules/Workspace/WorkspaceViewModel.cs` | `ScrapeLogChips`/`UpdateLogChips` + `SelectedScrapeLogChip`/`SelectedUpdateLogChip` + `ScrapeLogHeader`/`UpdateLogHeader`; `RebuildLogChips()` gọi trong `Rebuild()`; `SyncLogChipsToAccount()` gọi khi đổi tk; 2 lệnh "mở file log" bám CHIP thay vì bám tk cột trái |
| `suite/Shopee.Suite/Modules/Workspace/WorkspaceView.xaml` | style `logChipItem` + `logChipList` (chép template `subtabItem`, bo 4, khay `subtabTray` bo 6 — đúng 2 nấc đợt G); dải chip trên panel log của TAB 4 (Scrape) và TAB 5 (Update); ô log bind `SelectedXLogChip.Buffer` |

Hành vi mặc định giữ nguyên: chip đang chọn **bám theo tk đang chọn ở cột trái** (= đúng thứ 2 tab log vẫn hiện
trước đây). Bấm "Tất cả" → xem buffer GỘP, và chế độ này DÍNH qua các lần đổi tk. Có guard `_rebuildingLogChips`:
lúc `Clear()` dải chip, ListBox tự ghi ngược `SelectedItem = null` — không chặn thì chế độ "Tất cả" bị xoá ở MỌI
lần kho BigSeller bắn `Changed` (Save mỗi phím cũng bắn).

#### H3.2 — Khoảng nghỉ giữa link của Scrape thành tham số

| File | Việc |
|---|---|
| `suite/Shopee.Core/Scrape/ScrapeRestWindow.cs` (mới) | Nơi DUY NHẤT giữ mặc định 120/240s + luật hợp lệ hoá (`Resolve`: ≤0 → mặc định, kẹp [1,3600], max<min → kéo max lên) + quy đổi giây→ms (`MinMs`/`MaxMs`) |
| `suite/Shopee.Core/BigSeller/BigSellerRunConfig.cs` | `RestMinSeconds`/`RestMaxSeconds` (mặc định 120/240) — RIÊNG-MÁY, nằm sẵn ngoài `SharedSignature` |
| `suite/Shopee.Suite/Modules/Scrape/ScrapeTargetViewModel.cs` | Property proxy `RestMinSeconds`/`RestMaxSeconds` (setter tự `Resolve` + ghi cả cặp + raise cả 2 để ô nhập vẽ lại số đã hợp lệ hoá) + `PendingRestMinSeconds`/`PendingRestMaxSeconds` (override 1 lượt, khuôn `PendingFrameSize`) |
| `suite/Shopee.Suite/Modules/Workspace/WorkspaceView.xaml` | 2 ô "Nghỉ min (s) · scrape" / "Nghỉ max (s) · scrape" trong khối CẤU HÌNH CHẠY |
| `suite/Shopee.Suite/Modules/Scrape/ScrapeViewModel.cs` | `RunSingleAsync(..., restMinSeconds, restMaxSeconds)`; `RunOneJobAsync` resolve (Pending ?? cấu hình máy) rồi xoá Pending; truyền `restWindow` vào `ScrapeRunner`; log 1 dòng "nghỉ giữa link 120–240s" đầu job |
| `suite/Shopee.Module.MultiBrave/ScrapeRunner.cs` | Field `_restWindow` (ctor optional, mặc định `Default`); `BuildConfig` thành instance method, rót vào `InstanceConfig` |
| `suite/Shopee.Module.MultiBrave/Engine/InstanceConfig.cs` | `RestMinSeconds`/`RestMaxSeconds` (mặc định 120/240) |
| `suite/Shopee.Module.MultiBrave/Engine/LauncherRunnerLoop.cs` | XOÁ 2 hằng `MinRestMs`/`MaxRestMs`; đọc `ScrapeRestWindow.Resolve(cfg.RestMinSeconds, cfg.RestMaxSeconds)` + log khoảng nghỉ ở dòng "Đang tải dữ liệu" |
| `suite/Shopee.Core/Coordination/HubDtos.cs` | `Assignment.RestMinSec/RestMaxSec` + 2 param CUỐI của `CreateAssignmentRequest` (giữ luật "thêm sau Payload" vì call-site cũ truyền positional) |
| `server/Shopee.Hub.Web/Data/HubDatabase.cs` | Cột `rest_min_sec`/`rest_max_sec` trong `CREATE TABLE` + `MigrateSchema` (DB cũ tự thêm cột, DEFAULT 0) |
| `server/Shopee.Hub.Web/Data/HubDatabase.Assignments.cs` | INSERT + UPDATE (giao lại việc còn `queued`) + `ReadAssignmentRow` |
| `server/Shopee.Hub.Web/Components/Pages/Dispatch.razor(.cs)` | `_optRestMin`/`_optRestMax` + 2 ô nhập (nhãn NGẮN, chú thích trong `title` — đúng khuôn F8); `CreateJob` chỉ ghi cho op `scrape`, op khác truyền 0 |
| `suite/Shopee.Suite/Infrastructure/AssignmentWorker.cs` | Truyền `a.RestMinSec/RestMaxSec > 0 ? … : null` xuống `RunSingleAsync` |
| `server/Shopee.Hub.Web/Shopee.Hub.Web.csproj` | LINK `ScrapeRestWindow.cs` (hub LINK file chứ không ref project — `BigSellerRunConfig` đã tham chiếu hằng mặc định) |

Quy ước **0 = dùng cấu hình client** giữ y khuôn `Processes/FrameSize/Reload`; client CŨ nhận field mới thì bỏ
qua an toàn (JSON thừa field → `System.Text.Json` bỏ qua; cấu hình máy vẫn quyết định khoảng nghỉ).

#### H3.3 — Bộ 3 tham số điền form Update thành cấu hình per-shop trên Hub

**Đã đọc memory `bigseller-shop-field-sync-contract` + code trước khi viết.** Kết luận: 3 field này là field
**CHUNG (Hub-owned)** ⇒ **KHÔNG cần graft, KHÔNG đụng nhóm per-máy** ⇒ không phải dừng theo mục 5 của plan.

| File | Việc |
|---|---|
| `suite/Shopee.Core/BigSeller/BigSellerShop.cs` | `UpdateStockValue`/`UpdateWeightValue`/`UpdateShippingChannel` (mặc định `""`) + 3 hằng có tên `DefaultUpdateStock="30069"`, `DefaultUpdateWeight="500"`, `DefaultUpdateShippingChannel="Nhanh"` + helper `OrDefault` |
| `suite/Shopee.Core/Infrastructure/BackupService.cs` | 3 field vào `SharedSignature` (chiếu Shops) + chép TẠI CHỖ trong `MergeShopsKeepInstance`; 2 hàm đổi `private` → `internal` (đã có `InternalsVisibleTo Shopee.Core.Tests`) để test đối chiếu trực tiếp |
| `server/Shopee.Hub.Web/Components/Shared/ShopConfigPanel.razor` | Khối "Update product — giá trị điền form (để trống = mặc định)" với 3 ô, `placeholder` = hằng mặc định, `title` giải thích |
| `server/Shopee.Hub.Web/Services/FileStoreConfigService.cs` | Chú thích CHẶN: 3 field **cố ý KHÔNG nhận từ client** ở `UpdateSharedShopFields` + `FreshShopFromClient` |
| `suite/Shopee.Module.UpdateProduct/Engine/BigSellerWorkflowSettings.cs` | 3 property (mặc định = 3 hằng) |
| `suite/Shopee.Module.UpdateProduct/UpdateProductRunner.cs` | 3 param optional CUỐI của `UpdateProductContext` + hợp lệ hoá (rỗng → hằng) tại biên `BuildWorkflow` |
| `suite/Shopee.Module.UpdateProduct/Engine/BigSellerProductUpdateRunner.cs` | XOÁ 2 hằng `StockValue`/`WeightValue`; 3 property đọc từ `_settings`; **log 1 dòng đầu lượt**: `Giá trị điền form: tồn kho=… · cân nặng=…g · vận chuyển='…'` |
| `…UpdateRunner.Process.cs` | `[8] stock` dùng `StockValue`; `[10]` lọc theo `ShippingChannel` (log lỗi cũng in tên kênh); `[11]` dùng `WeightValue` |
| `suite/Shopee.Suite/Modules/UpdateProduct/UpdateProductViewModel.cs` | `BuildContext` truyền `s.UpdateStockValue/…` xuống context |

**Hợp đồng sync — chỉ rõ theo yêu cầu tiêu chí nghiệm thu:**
- `SharedSignature` ở `suite/Shopee.Core/Infrastructure/BackupService.cs`, phần chiếu `Shops` — đã thêm 3 field.
- **Không cần graft**: graft (`KeepLocalRunConfig` ngày xưa) chỉ dành cho field RIÊNG-MÁY; hơn nữa
  `MergeShopsKeepInstance` đã giữ nguyên object shop cũ nên KHÔNG có biến thể 4 (shop mồ côi).
- **Bẫy b13ed00 (UI không vẽ lại)**: 3 field được chép TẠI CHỖ lên object shop cũ ⇒ VM/UI đang cầm reference
  thấy giá trị mới; runner đọc `t.SelectedShop` (object live) nên không cần restart. Đã có test khoá
  `Assert.Same(cu, shop)`.
- **Chiều client → Hub CỐ Ý chặn** (khuôn `DataSource`): client không có UI cho 3 field ⇒ bản push của client
  cũ mang giá trị rỗng, nhận vào là mỗi nhịp upsert xoá trắng cấu hình admin. Đã test + kiểm live.

### B. Kết quả kiểm chứng (lệnh + kết quả THẬT)

| # | Lệnh | Kết quả |
|---|---|---|
| 1 | `dotnet build ShopeeSuite.sln --no-incremental` | **Build succeeded · 0 Warning · 0 Error** |
| 2 | `dotnet build server/ShopeeHub.sln --no-incremental` | **Build succeeded · 0 Warning · 0 Error** |
| 3 | `dotnet test orders/XuLyDonShopee.Tests` | **Passed! 1550/1550** (nền 1550 — không đổi, đúng: đợt này không đụng file nào trong `orders/`) |
| 4 | `dotnet test suite/Shopee.Core.Tests` | **Passed! 103/103** (nền 83 → **+20**) |
| 5 | `dotnet test server/Shopee.Hub.Web.Tests` | **Passed! 120/120** (nền 113 → **+7**) |

⚠ **Ghi nhận trung thực**: lượt chạy orders ĐẦU TIÊN báo `Failed: 1, Passed: 1549` nhưng chạy ở chế độ `-v q`
nên KHÔNG bắt được tên ca. Chạy lại **5 lần liên tiếp đều 1550/1550 xanh**. Diff của đợt này KHÔNG chạm file nào
trong `orders/` (xem `git status` — 0 file orders) nên đây là ca **flaky sẵn có**, không do đợt H3. Chưa xác định
được tên ca ⇒ ghi lại để phiên chính biết mà soi khi gặp lại.

**Test mới (đã THỬ PHÁ rồi khôi phục — ghi cả 2 lượt):**

| Phá thử | Test đỏ | Sau khôi phục |
|---|---|---|
| `ScrapeRestWindow.Resolve` bỏ nhánh `>0 ? … : Default` (0 → 1s thay vì 120s) | **Failed 4 / Passed 99** (`KhongDat_VeMacDinh` ×3, `ChiDatMin_MaxGiuMacDinh`) | 103/103 xanh |
| `SharedSignature` bỏ 3 field + `MergeShopsKeepInstance` bỏ 3 dòng chép | **Failed 5 / Passed 98**: `DoiGiaTriTrenHub_LamLechChuKySync(stock/weight/ship)`, `MergeTuHub_ChepTaiCho_GiuNguyenObjectShopCu`, `HubXoaVeRong_ClientVeMacDinh` | 103/103 xanh |
| `UpdateSharedShopFields` NHẬN 3 field từ client (đúng cái lỗi đã lặp 5 lần) | **Failed 1 / Passed 119**: `ClientDayShopLen_KhongXoaTrangCauHinhHub` | 120/120 xanh |
| INSERT assignment ghi cứng `rest_*=0` | **Failed 2 / Passed 118**: `KhoangNghi_LuuVaDocLaiDung`, `DbCu_ThieuCot_MigrationThemVaDocRaKhong` | 120/120 xanh |

Đã `grep "PHÁ THỬ"` toàn repo sau khi khôi phục → **0 kết quả**.

**Danh sách test mới:**
- `suite/Shopee.Core.Tests/ScrapeRestWindowTests.cs` (11 ca): mặc định = đúng 2 hằng cũ · 0/âm → mặc định ·
  chỉ đặt 1 vế · max<min → kéo lên · kẹp trần/sàn · `NextRestMs` luôn trong khoảng (500 vòng) · min=max không ném ·
  `BigSellerRunConfig` mới mang sẵn 120/240 · **JSON cũ thiếu field vẫn nghỉ 120–240s** · khoảng nghỉ KHÔNG lọt
  vào chữ ký sync (field riêng-máy).
- `suite/Shopee.Core.Tests/BigSellerShopUpdateFormSyncTests.cs` (9 ca): 3 hằng mặc định đúng `30069/500/Nhanh` ·
  `OrDefault` (trắng/trim) · **JSON cũ thiếu field vẫn chạy 30069/500/Nhanh** · đổi từng field làm lệch chữ ký ×3 ·
  merge chép tại chỗ + `Assert.Same` giữ object + giữ field riêng-máy · Hub xoá về rỗng thì client về mặc định ·
  shop mới từ Hub nhận đủ 3 field.
- `server/Shopee.Hub.Web.Tests/DispatchRunParamsTests.cs` (7 ca): khoảng nghỉ round-trip · không đặt → 0 và
  client resolve về 120–240s · giao LẠI việc `queued` cập nhật khoảng nghỉ · **DB CŨ thiếu cột → migration thêm,
  bản ghi cũ đọc ra 0** · hub lưu được 3 field điền form · **client đẩy shop lên KHÔNG xoá trắng cấu hình hub** ·
  shop mới từ client → 3 field để trống.

**Chạy Hub LOCAL (`HUB_DATA_DIR` = thư mục tạm trong scratchpad, đã dọn sau khi xong):**

Hub chạy `dotnet run --project server/Shopee.Hub.Web` với `HUB_DATA_DIR=<scratchpad>/hub-data-h3` +
`HUB_API_TOKEN=h3-test-token`. (Ghi chú: Kestrel trong `appsettings.json` ghi đè `--urls` → hub lên ở
`127.0.0.1:8088` chứ không phải cổng truyền vào; đã kiểm `server/Shopee.Hub.Web/hub-data` của repo GIỮ NGUYÊN
mtime 28/07 ⇒ **không đụng hub-data thật**.) Không đụng hub production trên VM.

1. `POST /bigseller/upsert` tạo acc `acc-h3` + shop `shop-h3` (đường client đăng ký acc) → `{"added":1}`.
2. `POST /setup` + `/login` tạo admin, mở UI Hub trong Browser pane → Fleet → shop → **⚙** → panel shop hiện
   đúng 3 ô mới với placeholder `30069` / `500` / `Nhanh`.
3. Nhập `50000` / `1200` / `Tiết kiệm` → **💾 Lưu**.
4. **Client pull** `GET /files/config/bigseller.json` (kèm `X-Api-Token`) →
   `UpdateStockValue='50000'`, `UpdateWeightValue='1200'`, `UpdateShippingChannel='Tiết kiệm'` ✔
5. Mô phỏng **client cũ đẩy lên** `POST /bigseller/upsert` (3 field rỗng, đổi tên shop) → pull lại: tên shop
   NHẬN từ client (`'Shop H3 doi ten o client'`) nhưng **3 field Hub-owned GIỮ NGUYÊN** `50000/1200/Tiết kiệm` ✔
6. `POST /machines/heartbeat` giả 1 máy online → `/dispatch` → chọn máy → panel tham số hiện **đủ 2 ô mới**
   ("Nghỉ min (giây)" / "Nghỉ max (giây)") kèm `title` đúng. Nhập 45/90 → bấm **▶ Scrape** rồi **▶ Import** →
   đọc thẳng `hub.db`:
   `('scrape', 45, 90)` và `('import', 0, 0)` ⇒ đúng luật "chỉ scrape mang khoảng nghỉ" ✔

**Kiểm hành vi mặc định KHÔNG đổi** (yêu cầu riêng của phiên chính):
- Nghỉ Scrape: `ScrapeRestWindowTests.MacDinh_DungBangHaiHangCu` + `KhongDat_VeMacDinh` + `RunConfigMoi_MangSanMacDinh`
  + `JsonCu_ThieuField_VanNghi120Den240` (deserialize bigseller.json KHÔNG có field → vẫn 120_000/240_000 ms).
- Form Update: `Rong_DungMacDinh_BangDungHangCu` + `JsonCu_ThieuField_VanChay30069_500_Nhanh`; và live bước 1
  ở trên cho thấy shop mới có 3 field rỗng → runner rơi về `30069/500/Nhanh`.

### C. Vướng mắc / chưa làm được

1. **KHÔNG chạy app WPF thật** (bị cấm theo prompt: heartbeat Hub production + giành lease). Vì vậy H3.1 mới
   **kiểm tới mức: build 0 warning + XAML parse được ở compile-time + logic chọn chip đã đọc lại kỹ**, CHƯA nhìn
   thấy dải chip vẽ ra bằng mắt. Phần này cần phiên chính (hoặc lượt nghiệm thu có quyền chạy app) mở màn
   Workspace → tab "Theo dõi Scrape" xác nhận thị giác. Cụ thể nên soi 3 điểm: (a) chip "Tất cả" + mỗi acc 1 chip
   kèm số dòng; (b) bấm chip đổi nội dung ô log; (c) đổi tk ở cột trái khi đang ở "Tất cả" thì vẫn giữ "Tất cả".
2. **Ca test flaky trong `orders/XuLyDonShopee.Tests`** (1 lần đỏ / 6 lần chạy, không bắt được tên) — xem mục B.
3. Không mục nào của plan bị bỏ; không phải DỪNG mục nào theo điều khoản rủi ro (H3.3 không đụng nhóm per-máy).

### D. Đề xuất (điểm plan/hệ thống nên xem lại)

1. **⚠ RỦI RO CÒN LẠI của H3.3 — đường `PUT /files/config/bigseller.json` vẫn ghi đè NGUYÊN FILE.**
   `HubConfigSync.PushAsync` (client) vẫn upload nguyên `shared/bigseller.json` lên `config/bigseller.json` qua
   `PUT /files/{*name}`; route này KHÔNG đi qua `UpsertBigSellerAccounts` nên **bỏ qua toàn bộ lớp bảo vệ
   hub-owned**. Đã **kiểm chứng live trên hub tạm**: PUT một file kiểu client-cũ (không có 3 field) →
   `UpdateStockValue/UpdateWeightValue/UpdateShippingChannel` biến mất khỏi cấu hình hub.
   - Đây là lỗ **CÓ SẴN**, áp cho MỌI field hub-owned (kể cả `DataSource`), không phải do đợt H3 tạo ra; cờ
     `Hub:AllowClientConfigPush` sinh ra để bịt nhưng production đang để **`true`**.
   - Hệ quả thực tế: chỉ nguy trong **cửa sổ lệch phiên bản** — client CŨ (binary chưa có 3 property) deserialize
     rồi serialize lại là mất field. Client MỚI đã pull về thì đẩy lại đúng giá trị (vô hại).
   - **Đề xuất thứ tự phát hành cho riêng H3.3 (NGƯỢC với thói quen "deploy hub trước")**: phát hành **client
     trước**, đợi fleet lên bản mới, **rồi** admin mới bắt đầu đặt giá trị trên Hub. Hoặc dứt điểm: đặt
     `Hub:AllowClientConfigPush=false` — nhưng đó là thay đổi hành vi toàn fleet (chặn cả `accounts.json`,
     `scrape-targets.json`, `kiot-proxies.json`), **vượt phạm vi plan nên tôi KHÔNG tự làm**; cần phiên chính quyết.
2. **Kênh vận chuyển khớp theo CHỮ** (`Filter(HasTextString)`) nên nếu BigSeller đổi ngôn ngữ UI thì giá trị
   `"Nhanh"` không khớp — hành vi này y hệt trước đợt H3 (hằng cũ cũng là chuỗi "Nhanh"), nhưng giờ admin **sửa
   được từ Hub mà không phải build lại**, tức là đã tốt hơn. Nếu muốn bền hơn thì đợt sau nhận diện theo cấu trúc
   (khuôn memory `bigseller-language-guide-structural-detect`) — ngoài phạm vi đợt này.
3. **Ô nhập khoảng nghỉ ở client dùng `TextBox` mặc định (`UpdateSourceTrigger=LostFocus`)** — giá trị chỉ được
   hợp lệ hoá khi rời ô. Đúng khuôn mọi ô số sẵn có trong khối CẤU HÌNH CHẠY nên tôi giữ nguyên cho nhất quán;
   nếu muốn phản hồi tức thì thì phải đổi cả khối (ngoài phạm vi).
4. Plan mô tả H3.1 là "panel log của các màn module" và giả định panel đang hiện log GỘP; thực tế repo hiện tại
   chỉ còn **màn gộp Workspace** với 2 tab log và panel đó **đã** hiện log per-acc theo tk đang chọn. Tôi bám ý
   đồ (chọn chip lọc log) và giữ mặc định = hành vi cũ; nếu kiến trúc sư muốn mặc định là "Tất cả" thì chỉ cần
   đổi giá trị khởi tạo `_scrapeLogShowAll`/`_updateLogShowAll` = `true`.
