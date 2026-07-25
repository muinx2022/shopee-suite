# Plan: Chi tiết tài khoản (Đơn hàng) — thêm tab "Kết quả" (chuẩn bị hàng theo shop/ngày)

- **Ngày:** 2026-07-26
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`, worktree)

## 1. Bối cảnh & mục tiêu

Trong module Đơn hàng (`orders/XuLyDonShopee.*`), màn **chi tiết tài khoản** (panel phải, master-detail) hiện
chỉ có 1 form thông tin. Người dùng muốn tách thành **2 tab**:
- **Tab 1 "Thông tin tài khoản"** = form hiện tại (giữ nguyên).
- **Tab 2 "Kết quả"** = lưới 2 cột **Shop | Chuẩn bị hàng**: mỗi shop của tài khoản + số đơn đã "chuẩn bị hàng"
  cộng dồn trong NGÀY. Tự reset theo ngày. Có **lịch chọn ngày** để lọc (mặc định hôm nay).

**Quyết định đã chốt với người dùng:** cột Shop hiện **TẤT CẢ shop của tài khoản** (kể cả shop 0 đơn trong
ngày) → phải LƯU danh sách shop. Cách đếm: mỗi đơn arrange xong = +1 (mỗi đơn arrange 1 lần nên cộng dồn không
trùng).

**Hiện trạng code (đã khảo sát kỹ):**
- **UI** `orders/XuLyDonShopee.App/Views/AccountsView.axaml`: master-detail, `Grid ColumnDefinitions="340,*"`
  (dòng ~184). Cột phải chia `ColumnDefinitions="3*,2*"` (~291): 3* = form (`Grid Grid.Column="0"` ~294) chứa
  chồng: placeholder `IsVisible="{Binding ShowPlaceholder}"` (~297) + **form** `ScrollViewer IsVisible="{Binding
  IsEditing}"` (~303, header "Chi tiết tài khoản" ~311); 2* = panel log. → Bọc **TabControl** vào trong
  `Grid Grid.Column="0"` (~294), `IsVisible="{Binding IsEditing}"`, Tab1 = ScrollViewer form hiện tại, Tab2 =
  Kết quả. **Module CHƯA có TabControl nào** → tự style (phẳng, khớp theme mới).
- **VM** `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs`: `[ObservableProperty] AccountRowViewModel?
  _selectedRow` (~206), `_editingId` (~301), `OnSelectedRowChanged`→`LoadIntoForm`. `x:DataType` = AccountsViewModel.
- **"Chuẩn bị hàng" đếm ở đâu:** luồng thật ở `orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs`,
  `RunShopOrdersAsync` (~604), vòng `while (guard++ < 50)` (~684-710): mỗi vòng nhận `PrepareResult? prep`
  (~690); `prep is null` → hết đơn, break; else đã chuẩn bị xong 1 đơn (biết `prep.OrderCode` + tham số `shopId`
  /`shopLogin` của hàm). Hiện chỉ tăng biến tạm `slips` + log; **KHÔNG lưu**. (Enum `ArrangeShipmentResult` là
  code CHẾT — bỏ qua.)
- **Danh sách shop:** đọc runtime từ `/portal/shop` → `ShopeeLoginService.ParseShopListJson` → `IReadOnlyList<
  ShopListItem>` (`ShopListItem(ShopId, ShopName, LoginName)`) tại OrdersBridgeSession (~427-429/561-563), lặp
  xong bỏ, **không persist**. `orders.shop_login`/`shop_id` chỉ có per-đơn (đơn xong bị dọn).
- **DB** `orders/XuLyDonShopee.Core/Data/Database.cs`: SQLite, tạo bảng bằng `CREATE TABLE IF NOT EXISTS` trong
  `Initialize()`; thêm cột cũ bằng `EnsureColumn`. Thêm BẢNG mới = thêm 1 câu CREATE vào `Initialize()`.
  `OrdersRepository` khóa (account_id, order_sn). Wiring repo ở `AppServices` ctor (~90-110), expose property.
- **Callback Core→App:** Core KHÔNG có DB → cần callback bơm từ App (mẫu `_syncCallback` rót ở
  `AccountSession` ~1176). AccountSession biết `account_id`.

## 2. Phạm vi

- **Làm:** (a) 2 bảng SQLite mới + repo; (b) 2 callback Core→App (đọc-danh-sách-shop, arrange-xong-1-đơn) để
  App lưu shop + tăng đếm; (c) UI 2 tab + tab Kết quả (lịch chọn ngày + lưới Shop|Chuẩn bị hàng).
- **KHÔNG làm:** không đổi luồng arrange/sync hiện có (chỉ THÊM lời gọi callback); không đụng suite/hub; không
  đổi form thông tin tài khoản (chỉ bọc vào Tab1).

## 3. Các bước thực hiện

### Bước 1 — DB: 2 bảng mới (`Core/Data/Database.cs`, trong `Initialize()`)

```sql
CREATE TABLE IF NOT EXISTS account_shops (
    account_id INTEGER NOT NULL,
    shop_login TEXT NOT NULL,
    shop_name  TEXT,
    updated_at TEXT NOT NULL,
    UNIQUE(account_id, shop_login)
);
CREATE TABLE IF NOT EXISTS prepare_daily (
    account_id INTEGER NOT NULL,
    shop_login TEXT NOT NULL,
    day        TEXT NOT NULL,          -- yyyy-MM-dd (giờ địa phương)
    count      INTEGER NOT NULL DEFAULT 0,
    UNIQUE(account_id, shop_login, day)
);
```

### Bước 2 — Repo mới `Core/Data/ResultsRepository.cs`

- `void UpsertShops(long accountId, IEnumerable<ShopListItem> shops)` — UPSERT từng shop (LoginName→shop_login,
  ShopName→shop_name, updated_at=now). `ON CONFLICT(account_id,shop_login) DO UPDATE SET shop_name, updated_at`.
- `IReadOnlyList<(string ShopLogin, string? ShopName)> GetShops(long accountId)` — order by shop_name/shop_login.
- `void IncrementPrepared(long accountId, string shopLogin, string day)` — `INSERT ... ON CONFLICT(account_id,
  shop_login,day) DO UPDATE SET count = count + 1`. (Cũng nên `UpsertShops` shop này để chắc shop có trong
  account_shops — hoặc để callback shop-list lo; xem Bước 4.)
- `Dictionary<string,int> GetPreparedByDay(long accountId, string day)` — map shop_login→count cho ngày đó.
- Đăng ký ở `AppServices` ctor: `Results = new ResultsRepository(Database);` + property `public ResultsRepository
  Results { get; }`.

### Bước 3 — Callback trong Core (`Core/Services/OrdersBridgeSession.cs`)

- Thêm 2 callback (Action) — theo mẫu `_syncCallback` (ctor param hoặc property được AccountSession rót):
  - `Action<IReadOnlyList<ShopListItem>>? OnShopListRead` — gọi NGAY sau khi parse được shop list (~427-429 và
    561-563; gọi ở nhánh dùng thật, tránh gọi trùng vô hại).
  - `Action<string /*shopLogin*/>? OnOrderPrepared` — gọi mỗi khi chuẩn bị xong 1 đơn: đặt ở vòng
    `while` NGAY sau khi có `prep` hợp lệ (mỗi `prep` = 1 đơn arrange). Truyền `shopLogin` của shop đang chạy.
- CHỈ THÊM lời gọi; không đổi logic hiện có. Callback null-safe (`?.Invoke`).

### Bước 4 — Wiring App (`App/Services/AccountSession.cs`)

- Khi tạo/chạy `OrdersBridgeSession`, rót 2 callback (chỗ đã rót `_syncCallback` ~1176):
  - `OnShopListRead = shops => _services.Results.UpsertShops(accountId, shops);`
  - `OnOrderPrepared = shopLogin => _services.Results.IncrementPrepared(accountId, shopLogin,
    DateTime.Now.ToString("yyyy-MM-dd"));`
  - `accountId` = id tài khoản của session này. (Nếu IncrementPrepared cũng tự UpsertShops thì shop có đơn luôn
    có trong danh sách kể cả khi OnShopListRead lỡ.)

### Bước 5 — VM tab Kết quả (`App/ViewModels/AccountsViewModel.cs`)

- Thêm:
  - `[ObservableProperty] DateTimeOffset _resultDate = DateTimeOffset.Now;` (ngày đang lọc, mặc định hôm nay).
  - `public ObservableCollection<ShopPrepareRow> ResultRows { get; } = [];`
  - `record ShopPrepareRow(string ShopName, int PreparedCount);` (ShopName = shop_name ?? shop_login).
  - `void LoadResults()`: nếu `SelectedRow` null → clear; else `accountId = SelectedRow.Id`,
    `day = ResultDate.ToString("yyyy-MM-dd")`; lấy `GetShops(accountId)` + `GetPreparedByDay(accountId, day)`;
    dựng rows cho MỌI shop (LEFT JOIN: count = map.GetValueOrDefault(shopLogin, 0)); gán vào ResultRows.
  - Gọi `LoadResults()` trong `OnSelectedRowChanged` VÀ `OnResultDateChanged` (partial của ObservableProperty).
  - (Nếu 2 shop cùng login: gộp. Nếu shop có đơn nhưng chưa trong account_shops: UNION thêm từ map để không sót.)

### Bước 6 — UI 2 tab (`App/Views/AccountsView.axaml`)

- Trong `Grid Grid.Column="0"` (~294): thêm `TabControl IsVisible="{Binding IsEditing}"` (đặt cạnh/thay chỗ
  ScrollViewer form — placeholder giữ nguyên ngoài TabControl):
  - **TabItem "Thông tin tài khoản"**: chứa ĐÚNG ScrollViewer form hiện tại (di chuyển vào, giữ nguyên nội dung
    + `IsVisible` nội tại bỏ đi vì TabControl đã có IsVisible=IsEditing).
  - **TabItem "Kết quả"**: 
    - Hàng lọc: `DatePicker SelectedDate="{Binding ResultDate}"` (lịch chọn ngày; nhãn "Ngày:").
    - `DataGrid ItemsSource="{Binding ResultRows}"` AutoGenerateColumns=False, 2 cột: "Shop" (ShopName, `*`),
      "Chuẩn bị hàng" (PreparedCount, Auto, canh phải). Hoặc lưới ItemsControl nếu DataGrid nặng — DataGrid ok
      (module đã dùng ở màn Đơn hàng/Proxy).
  - Style TabControl/TabItem gọn phẳng (khớp theme mới: bo 4, chữ rõ). Tự thêm style cục bộ trong View.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build` solution 0 error; `dotnet test XuLyDonShopee.Tests` xanh (thêm test cho ResultsRepository
      nếu tiện: UpsertShops/GetShops, IncrementPrepared cộng dồn, GetPreparedByDay theo ngày).
- [ ] Mở 1 tài khoản → panel phải có 2 tab; Tab1 = form cũ nguyên vẹn (lưu/hủy vẫn chạy); Tab2 = Kết quả.
- [ ] Tab Kết quả: chọn ngày (mặc định hôm nay) → lưới hiện MỌI shop của tài khoản (kể cả 0) + số chuẩn bị hàng
      của ngày đó. Đổi ngày → số đổi theo. (Kiểm bằng cách seed DB: UpsertShops vài shop + IncrementPrepared vài
      lần → thấy cộng dồn đúng, ngày khác = 0.)
- [ ] Chạy 1 lượt bridge thật (nếu test được) → account_shops có shop, prepare_daily tăng theo mỗi đơn arrange.
- [ ] Migration an toàn với DB CŨ (CREATE TABLE IF NOT EXISTS không phá dữ liệu).

## 5. Rủi ro & lưu ý

- **Core không có DB** → BẮT BUỘC đi qua callback App; đặt lời gọi callback ĐÚNG chỗ (arrange xong 1 đơn / parse
  xong shop list), null-safe, không đổi luồng.
- **Đếm trùng:** mỗi `prep` = 1 đơn arrange 1 lần → +1 là đúng. KHÔNG tăng theo `slips` (slip-save best-effort,
  có thể lệch). Tăng theo mỗi đơn được chuẩn bị.
- **"day" theo giờ ĐỊA PHƯƠNG** (khớp cảm nhận "hôm nay" của người dùng), yyyy-MM-dd; lọc bằng so khớp chuỗi.
- **DatePicker Avalonia** trả `DateTimeOffset?` — cẩn thận null (giữ giá trị cũ nếu người dùng xóa).
- Shop mới xuất hiện giữa ngày vẫn hiện (UpsertShops mỗi lượt). Shop đổi tên → cập nhật shop_name.
- Toàn bộ thay đổi trong `orders/` — worktree: mọi đường dẫn quy về thư mục làm việc của agent, KHÔNG đụng cây chính.

---

## Báo cáo thực thi (Opus điền sau khi xong)

Hoàn thành trong worktree, merge về main (d704a2a). Build 0 error, 908 test xanh (10 test ResultsRepository mới).
8 file (2 mới: ResultsRepository.cs + test). Khóa shop = LoginName fallback ShopName (khớp nhãn callback đếm).
LoadResults chạy khi đổi SelectedRow/ResultDate (không live lúc đang arrange — đổi ngày/chọn lại để cập nhật).
Cần soi mắt: style tab phẳng + hiển thị DataGrid/DatePicker (build sạch nhưng chưa chạy app thật).
