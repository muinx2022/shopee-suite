# Plan: "Chạy" — vòng lặp qua nhiều shop trong một tài khoản subaccount

- **Ngày:** 2026-07-23
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Nhánh:** `feature/dang-nhap-subaccount` (worktree `d:\Projects\shopee-suite-wt-subaccount`)

## 1. Bối cảnh & mục tiêu

Nhánh này đã có luồng đăng nhập QUA Nền tảng tài khoản phụ (`TryEnterSellerViaSubaccountAsync` trong
`ShopeeLoginService.cs`): mở `subaccount.shopee.com` → điền form → mở hộp thư cho người dùng tự lấy mã →
chờ đăng nhập xong (nav "Tài khoản của tôi" hiện) → **hiện tại** click "Tài khoản của tôi" → "Kênh Người
bán" → chuẩn hóa tab banhang thành `Pages[0]`.

**Người dùng đổi yêu cầu (mô hình MỚI):** một lần đăng nhập subaccount có NHIỀU shop. Sau khi đăng nhập
xong, KHÔNG click "Tài khoản của tôi"/"Kênh Người bán" nữa mà **mở thẳng danh sách shop**
`https://banhang.shopee.vn/portal/shop`. Đó là bảng các shop của tài khoản. App **lặp qua từng shop**:

1. Ở bảng shop, mỗi dòng có nút **"Chi tiết"** → click để mở shop (mở **tab mới**).
2. Theo tab mới đó, **chạy đúng flow hiện tại** cho một shop: kiểm tra đơn → nếu có thì Chuẩn bị hàng →
   sync đơn về máy + Google Sheet (in phiếu như hiện có).
3. Xong shop → **đóng tab đó**, quay lại tab danh sách shop.
4. **Delay ngẫu nhiên 3–5 phút** rồi làm shop kế tiếp.
5. Hết danh sách → **quay lại từ đầu** (lặp mãi cho tới khi người dùng Dừng).

**Quyết định đã chốt với người dùng:**
- Nút "Chạy" chạy cho **1 tài khoản đang xem**, lặp mãi tới khi Dừng (không phải hàng loạt).
- Cột "Tên Shop" GSheet (cột E) = **Tên đăng nhập của shop** (cột 2 bảng shop, vd `alina99.store`,
  `shop9x.store`) — thay cho Email tài khoản.
- Địa chỉ lấy hàng khi Chuẩn bị hàng: **mọi shop dùng chung** `Account.PickupAddress` hiện có (1 kho).

DOM thật do người dùng cung cấp:
- Bảng shop: `table` trong `div[class*='shop-table']`; mỗi dòng `tr.eds-react-table-row[data-row-key]`
  (`data-row-key` = shop id, vd `1843718137`). Trong dòng:
  - Tên shop: `span[class*='shop-name-text']` (vd "Alina Store1").
  - Tên đăng nhập shop: ô `td` thứ 2 → `span` (vd "alina99.store").
  - Nút mở: `button.eds-react-button--link` chứa `<span>Chi tiết</span>`.

**Mô hình dữ liệu (đã chốt):** nhiều shop chung một `account_id`. Để cột "Tên Shop" GSheet đúng và không
đẩy nhầm đơn shop này với tên shop kia, **gắn shop id vào mỗi đơn** (`orders.shop_id`) và lọc theo shop
khi đẩy GSheet; TenShop lấy theo shop hiện tại.

## 2. Phạm vi

- **Làm:** đổi đuôi luồng đăng nhập, thêm primitive đọc/mở shop, viết vòng lặp shop trong
  `AccountSession.RunAsync`, cho các hàm flow chạy trên "trang shop đang mở" thay vì cứng `Pages[0]`,
  gắn shop id + tên shop vào đơn/GSheet, test. Toàn bộ trong `orders/`.
- **Không làm:**
  - KHÔNG đổi tên nút "Sync"→"Chạy" và KHÔNG gỡ màn "Chạy tự động" (việc đó ở PLAN RIÊNG kế tiếp —
    `plans/2026-07-23-nut-chay-bo-autorun.md`).
  - KHÔNG đụng Apps Script; giữ hợp đồng JSON GSheet.
  - KHÔNG làm địa chỉ lấy hàng theo từng shop (dùng chung — đã chốt).
  - KHÔNG đụng nhánh/worktree khác.

## 3. Các bước thực hiện

### Bước 1 — Đuôi đăng nhập: dừng ở "đã đăng nhập subaccount" (ShopeeLoginService.cs)

- Trong `TryEnterSellerViaSubaccountAsync`: BỎ phần "click 'Tài khoản của tôi' → 'Kênh Người bán' → chờ
  banhang → chuẩn hóa tab" (các bước 6–8 cũ). Method này giờ chỉ cần: điền form → mở hộp thư (giữ
  nguyên) → chờ tới khi nav "Tài khoản của tôi" HIỂN THỊ (đã đăng nhập subaccount) → **trả `true`**.
  Đổi tên method thành `TryLoginSubaccountAsync` (cập nhật khai báo trong `ILoginSession` + doc comment +
  mọi caller). Giữ tham số y cũ.
- Giữ helper `OpenMailboxSignedInAsync`, `MatchesMyAccountNav` (vẫn dùng làm tín hiệu logged-in). Selector
  nav/entry "Kênh Người bán" (`SellerChannelRegex`, `MatchesSellerChannelEntry`) không còn dùng cho luồng
  này — GIỮ lại (không xóa) để test cũ vẫn xanh; đánh dấu doc là "không dùng trong luồng mới".

### Bước 2 — "Trang làm việc" động cho các hàm flow (ShopeeLoginService.cs)

Các hàm flow đơn hiện đọc cứng `_context.Pages[0]`. Trong mô hình mới, flow chạy trên TAB shop (không
phải Pages[0] = trang danh sách shop). Thêm cơ chế "trang làm việc hiện tại":

- Trong `LoginSession`: thêm `private volatile IPage? _workPage;` + `internal void SetWorkPage(IPage? p)`
  + helper `private IPage? WorkPage() => _workPage ?? (_context.Pages.Count > 0 ? _context.Pages[0] : null);`
- Đổi `_context.Pages[0]` → `WorkPage()` trong CÁC HÀM FLOW ĐƠN sau (chỉ các hàm thao tác đơn, KHÔNG đổi
  các hàm login/detect): `ReadToShipCountAsync`, `GoHomeAndReadToShipCountAsync`,
  `OpenShippingAddressSettingsAsync`, `SetPickupAddressAsync` (và các bước Chuẩn bị hàng),
  `SyncAllOrdersAsync`, `ProcessFirstOrderAsync`/`ProcessOrdersAsync`, `RedownloadSlipsAsync`, đọc chi
  tiết đơn — đối chiếu 11 chỗ `_context.Pages[0]` (dòng ~744, 929, 987 là login/detect → GIỮ Pages[0];
  còn lại ~2156, 2214, 2326, 2725, 2895, 3541, 3822, 4019 là flow đơn → đổi sang `WorkPage()`).
- **QUAN TRỌNG:** các hàm flow đơn dùng `SellerUrl`/`AllOrdersUrl` Goto — vẫn giữ (Goto trên `WorkPage()`
  điều hướng chính tab shop tới trang đơn của shop đó). Không đổi các URL banhang.

### Bước 3 — Primitive đọc danh sách shop + mở/đóng tab shop (ShopeeLoginService.cs)

Model mới (Core, file mới `orders/XuLyDonShopee.Core/Services/ShopListItem.cs`):

```csharp
namespace XuLyDonShopee.Core.Services;
/// <summary>Một shop trong bảng /portal/shop của Nền tảng tài khoản phụ.</summary>
public sealed record ShopListItem(string ShopId, string ShopName, string LoginName);
```

Hằng + method trong `ShopeeLoginService`/`ILoginSession`:

- `public const string ShopListUrl = "https://banhang.shopee.vn/portal/shop";`
- `Task<IReadOnlyList<ShopListItem>> ReadShopListAsync(CancellationToken ct)`:
  - Goto `ShopListUrl` trên `Pages[0]` (tab danh sách = trang gốc; đặt lại `SetWorkPage(null)` trước khi
    đọc để mọi thứ về Pages[0]). Nuốt lỗi điều hướng.
  - Chờ bảng render (poll `tr[data-row-key]` tối đa ~20s). Đọc mỗi dòng bằng `EvaluateAsync` (một lần lấy
    cả mảng cho nhanh + bền): `data-row-key`, text `span[class*='shop-name-text']`, text ô `td` thứ 2.
  - Trả danh sách (rỗng nếu không có dòng nào / trang bounce về login → log `title/url`).
- `Task<bool> OpenShopDetailAsync(ShopListItem shop, CancellationToken ct)`:
  - Trên `Pages[0]`, định vị dòng `tr[data-row-key='<shop.ShopId>']`, tìm nút "Chi tiết" trong dòng
    (`button.eds-react-button--link` khớp text "Chi tiết"), click KIỂU NGƯỜI (`TryHumanClickVisibleAsync`).
  - Hứng **tab mới** (event `_context.Page` bắt trước click + quét `_context.Pages` sau click, timeout
    ~30s). Nếu mở CÙNG tab (Pages[0] điều hướng sang trang shop) → coi tab làm việc là Pages[0].
  - `SetWorkPage(tab)`; chờ `WaitForLoadState(DOMContentLoaded)` best-effort. Trả `true` nếu mở được, ghi
    `title/url` + `false` nếu không thấy nút / không mở được.
- `Task CloseShopTabAsync(CancellationToken ct)`:
  - Nếu `_workPage` KHÁC `Pages[0]` (tab riêng) → đóng `_workPage` (best-effort, retry ≤3). `SetWorkPage(null)`.
  - `Pages[0].BringToFrontAsync()` best-effort (quay lại tab danh sách).
- Tất cả method trên: Graceful, KHÔNG ném trừ hủy; log từng bước; không log dữ liệu nhạy cảm.

### Bước 4 — Gắn shop id + tên shop vào đơn & GSheet (Core/Data + App)

- `Database.cs`: `EnsureColumn(conn, "orders", "shop_id", "TEXT")` (+ thêm vào CREATE TABLE cho DB mới),
  cạnh nhóm cột hiện có. KHÔNG cần backfill (đơn cũ shop_id NULL — vẫn đẩy như trước theo account).
- Nơi lưu đơn sau khi sync (App, đường `SyncOrdersAsync` upsert đơn vào DB): set `shop_id` = shop hiện
  tại. Truyền shop id qua trạng thái phiên: `AccountSession` giữ `string? _currentShopId` +
  `string? _currentShopLogin` (đặt trước khi chạy flow của shop, xóa sau). Rà hàm upsert đơn
  (`OrdersRepository.Upsert…` — Opus tìm) để nhận thêm `shopId`.
- `OrdersRepository.GetForGsheetPush`: thêm tham số lọc `string? shopId` — khi có, chỉ trả đơn của shop
  đó (`shop_id = $shopId`); null → hành vi cũ (mọi đơn account). Cập nhật `MarkGsheetSynced` không cần
  shop (khóa theo account+order_sn vẫn đúng vì order_sn unique).
- `AccountSession.PushOrdersToGsheetAsync`: nhận (hoặc đọc từ field) `shopId` + `shopLogin` hiện tại;
  gọi `GetForGsheetPush(accountId, shopId)`; **TenShop = shopLogin** (thay cho `Account.Email`). Phần
  gộp-theo-tab (gsheet_tab) giữ nguyên.

### Bước 5 — Vòng lặp shop trong `AccountSession.RunAsync` (App)

Thay đoạn "nhịp theo dõi đơn Chờ Lấy Hàng" trong vòng poll (khoảng dòng 2007–2067, phần
`if (!_navigating && DateTime.UtcNow >= nextOrderCheck)`) bằng **vòng lặp shop**. Cụ thể:

- Sau khi điều phối đăng nhập xong và `entered == true` (đã đăng nhập subaccount) → BƯỚC VÀO vòng lặp
  shop thay cho việc đứng poll đọc số đơn. Nếu `entered == false` (không đăng nhập được) → giữ hành vi
  degrade như hiện tại (giữ cửa sổ, `_readyForActions=true`, KHÔNG chạy loop).
- Vòng lặp (đặt trong khối while phiên còn sống — vẫn tôn trọng `session.IsClosed`, `OpenPageCount==0`
  hai vòng liên tiếp, `ct`, `hardCap`):
  ```
  while (phiên còn sống && !ct):
      shops = await session.ReadShopListAsync(ct)
      lưu cookie nếu IsLoggedIn (giữ đoạn TrySaveCookie hiện có, chạy 1 lần/đầu vòng)
      if shops rỗng:
          log "Không đọc được danh sách shop — thử lại sau 1'."; delay 60s (ct-aware); continue
      foreach shop in shops:
          if ct || session.IsClosed || OpenPageCount==0: break
          _logLabel = shop.LoginName   // log per-shop
          _currentShopId = shop.ShopId; _currentShopLogin = shop.LoginName
          _navigating = true
          try:
              opened = await session.OpenShopDetailAsync(shop, ct)
              if opened:
                  // flow 1 shop = thân SyncFull: Check → (ToShip>0 ? Process) → SyncOrders (+ gsheet nền)
                  await ChayFlowMotShopAsync(session, ct)   // tách hàm dùng lại từ SyncFullAsync
              await session.CloseShopTabAsync(ct)
          finally:
              _navigating = false
              _currentShopId = null; _currentShopLogin = null
          // delay 3–5' giữa các shop (kể cả trước khi lặp lại từ đầu)
          await Task.Delay(rng 180_000..300_000, ct)
  ```
- `ChayFlowMotShopAsync`: bê thân `SyncFullAsync` (Check → Process nếu ToShip>0 → SyncOrders). Vì các
  hàm này giờ chạy trên `WorkPage()` (tab shop) nên không cần đổi logic; chỉ đảm bảo GSheet push dùng
  `_currentShopId`/`_currentShopLogin`. In phiếu vẫn theo đường sẵn có.
- Xóa/không dùng các biến nhịp cũ không còn ý nghĩa trong loop (`nextOrderCheck`, `orderIntervalMin`,
  `firstOrderCheck`, `ToShipCount` per-interval) ở nhánh loop — nhưng GIỮ watchdog proxy: chèn kiểm proxy
  ở đầu mỗi vòng foreach (hoặc mỗi vòng ngoài) như đoạn `nextProxyCheck` hiện có; proxy chết →
  `relaunchForProxy=true` → break ra dispose + relaunch (đăng nhập lại). Giữ nguyên cơ chế relaunch ngoài.
- `_readyForActions`: bật `true` sau khi đăng nhập xong (như hiện tại) để nút không kẹt; loop chạy nền.

### Bước 6 — Nút "Kiểm tra ngay"/"Xử lý đơn" thủ công (App) — giữ an toàn

Các nút thủ công (CheckOrders/ProcessOrders/GoHomeAndRead) chạy trên `WorkPage()`. Trong loop, giữa 2
shop `_workPage=null` → chúng chạy trên trang danh sách shop (không có to-do box) → trả null/no-op êm (đã
graceful). Không cần chặn cứng, nhưng **thêm guard nhẹ**: nếu đang trong loop (`_navigating` hoặc cờ mới
`_shopLoopRunning`) thì nút thủ công log "Đang chạy vòng lặp shop — bỏ qua thao tác tay lần này." rồi
thôi (tránh giẫm luồng). Không bắt buộc phức tạp — ưu tiên không phá loop.

### Bước 7 — Test (`orders/XuLyDonShopee.Tests`)

- File mới `ShopListParseTests.cs`: nếu tách được hàm thuần parse text-DOM → mảng `ShopListItem` (Opus
  nên tách phần "chuyển mảng {rowKey,name,login} (đọc từ Evaluate) → List<ShopListItem>" thành hàm thuần
  `internal static` để test), kiểm: 3 dòng như DOM mẫu → 3 item đúng ShopId/ShopName/LoginName; dòng
  thiếu login → vẫn nhận (LoginName rỗng); mảng rỗng → list rỗng.
- `OrdersRepositoryTests`: `GetForGsheetPush(accountId, shopId)` chỉ trả đơn khớp shop_id; null shopId =
  mọi đơn (giữ test cũ xanh). Upsert đơn set shop_id.
- `DatabaseMigrationTests`: DB cũ thiếu `shop_id` → migration thêm cột, đơn cũ còn nguyên, GetForGsheetPush
  không ném.
- Toàn bộ `dotnet test orders/XuLyDonShopee.Tests` xanh (hiện 919 sau merge; không làm đỏ test cũ).

## 4. Tiêu chí nghiệm thu

- [ ] Build 3 project orders 0 error; `dotnet test orders/XuLyDonShopee.Tests` xanh toàn bộ.
- [ ] `TryLoginSubaccountAsync` dừng ở "đã đăng nhập subaccount" (không còn click Tài khoản/Kênh Người bán).
- [ ] Có `ReadShopListAsync` (Goto `/portal/shop`, parse bảng → ShopListItem), `OpenShopDetailAsync`
      (click "Chi tiết" → tab mới → `_workPage`=tab), `CloseShopTabAsync` (đóng tab → về danh sách).
- [ ] Hàm flow đơn chạy trên `WorkPage()` (tab shop), không cứng `Pages[0]`.
- [ ] `RunAsync` sau đăng nhập chạy vòng lặp: đọc shop → từng shop mở tab → Check/Process/Sync → đóng tab
      → delay 3–5' → hết thì lặp lại; tôn trọng Dừng/đóng cửa sổ/hủy/hardCap; watchdog proxy còn hiệu lực.
- [ ] GSheet: cột Tên Shop = tên đăng nhập shop; đơn lọc theo `shop_id` (không đẩy nhầm tên shop).
- [ ] Nút thủ công không phá loop (guard nhẹ).

## 5. Rủi ro & lưu ý

- **DOM `/portal/shop` + trang chi tiết shop CHƯA soi bằng chạy thật** (chỉ có HTML bảng người dùng dán).
  Selector viết nhiều fallback + log `title/url` khi trượt để tinh chỉnh sau lần chạy thật đầu.
- **"Chi tiết" mở tab mới hay cùng tab chưa chắc** → xử cả hai (như bước 3).
- SPA React (eds-react-table) re-render → re-query dòng/nút tươi ngay trước click; không giữ handle qua
  chờ.
- Đơn của nhiều shop nằm chung `account_id`: đã tách bằng `shop_id`. Cẩn thận nơi upsert đơn phải set
  shop_id (nếu bỏ sót, đơn shop_id NULL → GetForGsheetPush(shopId) bỏ qua → không đẩy). Rà kỹ đường lưu đơn.
- `_workPage` phải được `SetWorkPage(null)` khi đóng tab/đầu vòng đọc danh sách — kẻo hàm flow trỏ vào tab
  đã đóng → lỗi. Mọi nhánh thoát shop đều clear ở `finally`.
- Delay 3–5' giữa shop phải `ct`-aware (hủy Dừng thoát ngay, không đợi hết delay).
- Giữ watchdog proxy + relaunch (đăng nhập lại sau relaunch chạy lại `TryLoginSubaccountAsync`).
- Cookie: subaccount + banhang — vẫn chỉ lưu khi `ShopeeLoginCookies.IsLoggedIn` (có SPC_*). Giữ đoạn lưu.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
