# Plan: tab "Kết quả" — thứ tự shop theo subaccount + dấu tick shop đã kiểm tra

- **Ngày:** 2026-07-26
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

Màn **Shopee → Tài khoản → chi tiết tài khoản → tab "Kết quả"** hiện có lưới: cột tiến độ (hẹp, 40px) · cột
Shop · cột "Chuẩn bị hàng". Người dùng nêu 2 việc:

1. **Thứ tự shop trong app khác với thứ tự trên trang subaccount của Shopee.** Nguyên nhân:
   `ResultsRepository.GetShops` sắp `ORDER BY COALESCE(shop_name, shop_login)` (theo bảng chữ cái), trong khi
   phiên đọc `/portal/shop` trả shop theo ĐÚNG thứ tự Shopee hiển thị rồi truyền vào `UpsertShops` — thứ tự đó
   đang bị vứt đi. Yêu cầu: **subaccount có thứ tự nào thì app hiện đúng thứ tự đó.**

2. **Cần biết những shop nào ĐÃ kiểm tra xong trong lượt chạy**, hiện chỉ có chấm tròn cho biết đang/vừa check
   tới shop nào.

**Người dùng đã CHỐT cách hiển thị cột tiến độ** (không được tự đổi):

- Mỗi dòng chỉ có **MỘT** biểu tượng, theo thứ tự ưu tiên:
  - đang kiểm tra → **vòng quay** (giữ nguyên như hiện tại);
  - đã kiểm tra xong trong lượt chạy này → **dấu tick ✓** (màu `SuccessBrush`);
  - chưa tới → **để trống**.
- **BỎ chấm tròn** (`Ellipse` + `ShowDot`) — dấu tick đã thay trọn nghĩa của nó (shop cuối cùng có tick chính là
  shop vừa xong).

### Hiện trạng code liên quan

- `orders/XuLyDonShopee.Core/Data/Database.cs` — bảng `account_shops (account_id, shop_login, shop_name,
  updated_at, UNIQUE(account_id, shop_login))`; migration cho DB cũ dùng helper `EnsureColumn(conn, bảng, cột, kiểu)`.
- `orders/XuLyDonShopee.Core/Data/ResultsRepository.cs` — `UpsertShops` (INSERT … ON CONFLICT DO UPDATE, duyệt
  `IEnumerable<ShopListItem>` theo thứ tự nguồn), `GetShops` (đang `ORDER BY COALESCE(shop_name, shop_login)`).
- `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs`:
  - `_shopCheck`: `Dictionary<long, (string ShopLabel, bool IsChecking)>` — nhớ shop đang/vừa check theo tài khoản.
  - `OnShopCheckChanged(accountId, shopLabel, checking)` (~dòng 1206) — cập nhật `_shopCheck` rồi
    `ApplyShopCheckFlags()`.
  - `ApplyShopCheckFlags()` (~dòng 453) — set `row.IsCurrent` / `row.IsChecking` cho mọi dòng.
  - `MatchesShopLabel(row, label)` (~dòng 472) — khớp nhãn phiên với dòng lưới (ưu tiên `ShopLogin`, chấp cả
    `ShopName`, bỏ khoảng trắng, không phân biệt hoa/thường). **DÙNG LẠI, không viết hàm khớp mới.**
  - `OnShopListChanged(long accountId)` — phiên đọc xong danh sách shop → nạp lại lưới.
  - `ShopPrepareRow` (~dòng 1447) — `ShopName`, `ShopLogin`, `[ObservableProperty] PreparedCount/IsCurrent/IsChecking`,
    `ShowDot => IsCurrent && !IsChecking`.
- `orders/XuLyDonShopee.App/Views/AccountsView.axaml` (~dòng 623–638) — ô cột tiến độ: `Panel 18x18` chứa
  `Ellipse` (bind `ShowDot`) + `PathIcon Classes="spin"` (bind `IsChecking`).
- `orders/XuLyDonShopee.App/Styles/Icons.axaml` — đã có `IconCheck` (một dấu tích). **Không tạo icon mới.**
- Phiên phát sự kiện: `OrdersBridgeSession` gọi `onShopCheckStarted(shopLogin)` khi bắt đầu một shop và
  `onShopCheckFinished(shopLogin)` khi xong shop đó (kể cả shop lỗi/captcha/bỏ qua) → `AccountSession` (~816–817)
  → `AppServices.RaiseShopCheckChanged`. Danh sách shop đọc xong thì gọi `onShopListRead` → `RaiseShopListChanged`.

## 2. Phạm vi

- **Làm:** giữ thứ tự shop theo nguồn; thêm tập "shop đã kiểm tra trong lượt chạy" + hiển thị tick; bỏ chấm tròn.
- **Không làm:**
  - KHÔNG xóa/dọn shop cũ không còn trong subaccount (đã chốt trước đây: nguồn có gì hiện nấy, không tự dọn local).
  - KHÔNG đổi lưới nào khác, không đổi cột "Chuẩn bị hàng", không đụng tab "Thông tin tài khoản".
  - KHÔNG đổi giao thức hub / GSheet. KHÔNG đụng `server/`.
  - KHÔNG lưu trạng thái "đã kiểm tra" xuống DB — đây là trạng thái CỦA LƯỢT CHẠY, chỉ sống trong bộ nhớ.

## 3. Các bước thực hiện

### Bước 1 — `Database.cs`: cột thứ tự cho `account_shops`

- Thêm `sort_order INTEGER` vào câu `CREATE TABLE IF NOT EXISTS account_shops` (cho DB mới).
- Thêm `EnsureColumn(conn, "account_shops", "sort_order", "INTEGER");` cho DB cũ, kèm comment ngắn giải thích:
  vị trí shop theo ĐÚNG thứ tự trang `/portal/shop` của subaccount; NULL = dữ liệu cũ chưa biết thứ tự.

### Bước 2 — `ResultsRepository.UpsertShops`: ghi thứ tự nguồn

- Duyệt danh sách kèm chỉ số tăng dần (0, 1, 2, … theo đúng thứ tự `IEnumerable` nhận được — đây CHÍNH LÀ thứ tự
  Shopee trả). **Chỉ tăng chỉ số cho shop THỰC SỰ được ghi** (shop bị bỏ vì không có cả login lẫn tên thì không
  chiếm số thứ tự).
- Ghi `sort_order` ở cả nhánh INSERT và `DO UPDATE SET` (lượt đọc sau Shopee đổi thứ tự thì app đổi theo).

### Bước 3 — `ResultsRepository.GetShops`: sắp theo thứ tự nguồn

Đổi `ORDER BY` thành:

```sql
ORDER BY CASE WHEN sort_order IS NULL THEN 1 ELSE 0 END, sort_order, COALESCE(shop_name, shop_login)
```

Nghĩa là: shop đã biết thứ tự nguồn đứng trước theo đúng thứ tự đó; shop dữ liệu CŨ (`sort_order` NULL, chưa
đọc lại lần nào) xuống cuối theo tên như trước. Ghi comment giải thích.

### Bước 4 — `AccountsViewModel`: tập "đã kiểm tra trong lượt chạy"

- Thêm field: `private readonly Dictionary<long, HashSet<string>> _shopDaCheck = new();` — theo tài khoản, chứa
  NHÃN shop đã kiểm tra XONG trong lượt chạy hiện tại. Dùng `StringComparer.OrdinalIgnoreCase`. Ghi doc-comment
  theo văn phong các field sẵn có (nêu rõ: chỉ đọc/ghi trên UI thread nên không cần khóa).
- `OnShopCheckChanged(..., checking: false)` → thêm `shopLabel` vào tập của tài khoản đó (tạo tập nếu chưa có).
  Nhánh `checking: true` giữ nguyên hành vi hiện có.
- **Reset đầu lượt chạy:** trong `OnShopListChanged(accountId)` — phiên vừa đọc xong danh sách shop, tức lượt
  chạy mới bắt đầu (log thực tế: "Đọc được 12 shop — bắt đầu lặp qua từng shop") — **xóa sạch** tập của tài khoản
  đó trước khi nạp lại lưới. Ghi comment nêu rõ lý do chọn mốc này.
- `ApplyShopCheckFlags()`: set thêm `row.DaKiemTra` = tập của tài khoản đang mở có khớp dòng này không — khớp
  bằng **`MatchesShopLabel(row, nhãn)` sẵn có** (duyệt các nhãn trong tập, không tự so chuỗi kiểu khác). Không có
  tài khoản đang mở / chưa có tập → `false` cho mọi dòng, đúng như cách hàm này đang xóa sạch cờ.

### Bước 5 — `ShopPrepareRow`: thay `ShowDot` bằng `ShowTick`

- Thêm `[ObservableProperty] private bool _daKiemTra;` với `[NotifyPropertyChangedFor(nameof(ShowTick))]`.
- `IsChecking` cũng phải `[NotifyPropertyChangedFor(nameof(ShowTick))]`.
- Thêm `public bool ShowTick => DaKiemTra && !IsChecking;` (đang kiểm tra thì vòng quay thắng, không hiện tick).
- **XÓA** `ShowDot` và property `IsCurrent` cùng mọi chỗ dùng chúng (`ApplyShopCheckFlags` không set `IsCurrent`
  nữa) — chấm tròn đã bỏ. Nếu `IsCurrent` còn được dùng ở chỗ khác thì giữ lại đúng chỗ đó và ghi rõ trong báo cáo.
- Cập nhật doc-comment của lớp/property cho khớp ngữ nghĩa mới.

### Bước 6 — `AccountsView.axaml`: ô cột tiến độ

Trong `Panel` 18x18 của cột tiến độ (~dòng 625–636):

- **Xóa** `<Ellipse … IsVisible="{Binding ShowDot}" />`.
- **Thêm** tick:

```xml
<PathIcon Data="{DynamicResource IconCheck}"
          Width="13" Height="13"
          Foreground="{StaticResource SuccessBrush}"
          VerticalAlignment="Center" HorizontalAlignment="Center"
          IsVisible="{Binding ShowTick}" />
```

- Giữ nguyên `PathIcon Classes="spin"` bind `IsChecking`.
- Cập nhật comment mô tả cột cho khớp (tick = đã kiểm tra xong lượt này; vòng quay = đang kiểm tra).

### Bước 7 — Test

Thêm test theo phong cách sẵn có (tên hàm tiếng Việt không dấu):

1. `ResultsRepositoryTests`: `UpsertShops` với thứ tự C, A, B ⇒ `GetShops` trả **đúng C, A, B** (KHÔNG sắp theo
   bảng chữ cái).
2. `ResultsRepositoryTests`: gọi `UpsertShops` lần 2 với thứ tự ĐẢO ⇒ `GetShops` trả theo thứ tự mới.
3. `ResultsRepositoryTests`: shop không có login lẫn tên nằm giữa danh sách ⇒ bị bỏ và **không làm lệch** thứ tự
   các shop còn lại.
4. `ResultsRepositoryTests`: dòng cũ `sort_order` NULL (INSERT thẳng bằng SQL trong test) + dòng mới có
   `sort_order` ⇒ dòng có thứ tự đứng trước, dòng NULL xuống cuối.
5. `ShopPrepareRow`: `DaKiemTra=true, IsChecking=false` ⇒ `ShowTick` true; `DaKiemTra=true, IsChecking=true`
   ⇒ `ShowTick` false. Nếu đã có file test cho lớp này thì thêm vào đó.

### Bước 8 — Build & test

- `dotnet build ShopeeSuite.sln` → 0 error, 0 warning.
- `dotnet test` → 100% xanh (mốc hiện tại **1014 test**).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build`: 0 error, 0 warning.
- [ ] `dotnet test`: 100% xanh, số test > 1014, có đủ 5 nhóm ca ở Bước 7.
- [ ] `GetShops` trả shop theo ĐÚNG thứ tự `UpsertShops` nhận (kiểm bằng test 1 & 2).
- [ ] Grep toàn repo: **không còn** `ShowDot`; `Ellipse` trong ô cột tiến độ đã bị xóa.
- [ ] `AccountsView.axaml` cột tiến độ chỉ còn 2 biểu tượng loại trừ nhau: `spin` (IsChecking) và tick (ShowTick).
- [ ] `_shopDaCheck` được xóa trong `OnShopListChanged` (lượt chạy mới không kế thừa tick của lượt trước).
- [ ] Khớp nhãn shop dùng lại `MatchesShopLabel`, không có hàm so chuỗi mới song song.

## 5. Rủi ro & lưu ý

- **Thứ tự chỉ số ở Bước 2:** phải tăng theo shop ĐƯỢC GHI, không phải theo vòng lặp — shop bị bỏ mà vẫn tăng số
  sẽ để lại lỗ hổng (vô hại về hiển thị nhưng test 3 sẽ bắt).
- **Nhãn shop lệch hoa/thường** giữa phiên và `account_shops` là chuyện đã biết — đó là lý do bắt buộc dùng
  `MatchesShopLabel` thay vì `HashSet.Contains` trực tiếp trên `ShopLogin`.
- `onShopCheckFinished` được gọi cả khi shop lỗi/captcha/bỏ qua → shop đó vẫn nhận tick. **Đúng ý đồ** ("đã kiểm
  tra qua"), không thêm phân biệt lỗi/không-lỗi.
- Reset tick ở `OnShopListChanged` nghĩa là: mở app xong chưa chạy gì thì lưới không có tick nào — đúng, vì tick
  là trạng thái của LƯỢT CHẠY, không lưu xuống DB.
- Agent KHÔNG commit, KHÔNG deploy, KHÔNG bump version.

---

## Báo cáo thực thi (Opus điền sau khi xong)
