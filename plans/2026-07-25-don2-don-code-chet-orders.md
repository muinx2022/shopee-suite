# Plan: Đợt 2 — Dọn code chết app Đơn hàng (orders/) + gỡ proxy + gỡ POC

- **Ngày:** 2026-07-25
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Plan cha:** `plans/2026-07-25-ke-hoach-refactor-toan-app.md` (mục 2A)
- **Điều kiện tiên quyết:** plan `2026-07-25-don1-sua-bug-orders.md` đã nghiệm thu + commit (nút Tải phiếu đã đi qua bridge — không còn phụ thuộc `_session`).

## 1. Bối cảnh & mục tiêu

Sau pivot sang extension bridge, `AccountSession.StartAsync` chỉ gọi `RunBridgeContinuousAsync` (`orders/XuLyDonShopee.App/Services/AccountSession.cs:183`). Toàn bộ vòng Playwright cũ + các flow chỉ nó gọi là code chết (~4.500+ dòng). User đã chốt: (a) hệ proxy GỠ khỏi production orders (bridge chủ đích không dùng proxy); (b) GỠ extension POC `shopee-orders-test` + nút "Mở sạch" cả hai phía.

Đường SỐNG phải giữ (bridge còn dùng): `LoginSession.OpenAsync` (launch Brave + CDP attach, 467-646), `TryLoginSubaccountAsync` + chờ OTP (1117-1405), human-input engine (2215-2573), automation login Hotmail/Outlook + verify email (1406-2053), parser thuần (`ParseShopListJson`/`ParseOrdersJson`/`ParseVndAmount`), và mọi thứ `OrdersBridgeSession`/`RunBridgeContinuousAsync` tham chiếu. QUY TẮC VÀNG: trước khi xoá BẤT KỲ symbol nào, grep toàn repo (loại bin/obj) xác nhận 0 caller sống; caller chết đã nằm trong danh sách xoá thì xoá theo chuỗi.

## 2. Phạm vi

- **Làm:** các khối xoá dưới, trong `orders/` + thư mục `extensions/shopee-orders-test/`.
- **Không làm:** KHÔNG gỡ package `Microsoft.Playwright` (bước login còn dùng); KHÔNG đụng `suite/`, `server/`, `extensions/shopee-orders/` (trừ khi bước 4 buộc sửa tham chiếu), `extensions/shopee-search|scrape/`; không tách class (đợt 4).

## 3. Các bước thực hiện

### Bước 1 — Xoá vòng Playwright chết trong `AccountSession` + thu hẹp `IAccountSession`

- `AccountSession.RunAsync` (`AccountSession.cs:1892-2478`, ~590 dòng) — 0 caller.
- 5 method sống nhờ `_session` chết: `ProcessOrdersAsync` (287), `CheckOrdersAsync` (587), `SyncOrdersAsync` (674), `SyncFullAsync` (941), `ChayFlowMotShopAsync` (981); field `_session` nếu sau plan đợt-1 không còn ai dùng.
- `AccountsViewModel.RunOrAutoStartAsync` (`AccountsViewModel.cs:1044`) — tự ghi chú "chưa nút nào nối vào".
- Thu hẹp `IAccountSession` (`IAccountSession.cs:81-116`): bỏ 5 method trên khỏi interface + mọi implement.

### Bước 2 — Xoá flow Playwright chết trong `LoginSession` (ShopeeLoginService.cs)

Xoá các method chỉ được gọi từ đường chết bước 1 (grep từng cái trước khi xoá): `TryHumanLoginAsync`, `TryVerifyByEmailAsync` (nếu chỉ đường chết gọi — CHÚ Ý: verify email cho subaccount login là đường SỐNG, phân biệt kỹ 2 flow), `DetectPageStateAsync`, `ReadToShipCountAsync`, `GoHomeAndReadToShipCountAsync`, `OpenShippingAddressSettingsAsync`, `SetPickupAddressAsync`/`SetPickupAddressToOtherAsync` (flow Cài đặt vận chuyển ~1.200 dòng, 3084-4295), `ProcessFirstOrderAsync` (arrange + in phiếu, 4297-4566, 6060-6718), `SyncAllOrdersAsync` (quét đơn 4567-5719), `RedownloadSlipsAsync` (tải phiếu Playwright — đã thay bằng bridge), `ReadShopListAsync`, `OpenShopDetailAsync`, `CloseShopTabAsync`, `CaptureCookiesJsonAsync`.
- Forwarder mồ côi `OpenMailboxSignedInAsync` (`ShopeeLoginService.cs:376`) — tham chiếu class `OrdersMailboxSession` không tồn tại, 0 caller.
- `OrdersBridgeSession.RunSliceAsync` (GĐ1, `OrdersBridgeSession.cs:278`) — UI dùng `RunLoginThenSliceAsync`.
- Kỳ vọng: `ShopeeLoginService.cs` co từ 6.739 xuống ~2.000-2.200 dòng.

### Bước 3 — Gỡ hệ proxy khỏi production orders (quyết định của user)

- Xoá: `SelectProxyAsync` (caller duy nhất là `RunAsync` chết), `ProxyWatchdog`, `ProxySelector`, `ProxyHealthChecker` (bản runtime), pool acquire/release KiotProxy phía orders, `PlaywrightProxyMapper` + `PlaywrightProxyMapperTests` (test chỉ phục vụ nó).
- Màn Proxy trong UI: gỡ khỏi navigation (xoá View/ViewModel nếu không còn gì tham chiếu; nếu settings/DB có field proxy thì GIỮ data + model để không phá backup/serialize, chỉ gỡ UI + runtime).
- Kiểm tra `OpenAsync` (đường sống): nếu nhận tham số proxy, giữ tham số nhưng đường bridge truyền null (bridge tự khai KHÔNG dùng proxy — `AccountSession.cs:1778`); phần proxy-auth CDP Fetch (654-734) chỉ xoá nếu 0 caller sống sau khi gỡ.
- `orders/XuLyDonShopee.Core/Services/KiotProxyClient.cs` (adapter trên shared): xoá nếu 0 caller còn lại; GIỮ NGUYÊN project `shared/Shopee.Proxy.Kiot` (suite dùng).

### Bước 4 — Gỡ POC `shopee-orders-test` + nút "Mở sạch" (quyết định của user)

- Xoá thư mục `extensions/shopee-orders-test/`.
- Xoá `PocCleanLauncher.cs` + nút "Mở sạch" trong UI (grep caller của PocCleanLauncher).
- `BraveLaunchArgs.cs:131` (`ResolveOrdersExtension`): gỡ nhánh trỏ orders-test (giữ nhánh `shopee-orders` chính); `ShopeeLoginService.cs:496` gỡ tham chiếu tương ứng.
- Field `invoiceDir` gửi kèm `prepareNextOrder` (`OrdersBridgeSession.cs:634`) — extension không đọc, bỏ field phía C#.

### Bước 5 — Dọn test neo code chết

- Xoá: `PlaywrightProxyMapperTests` (theo bước 3), `AccountSessionLoopTests` (test `NextLoopDecision`/`ShouldSkipProcessing` — chỉ dùng bởi `ProcessOrdersAsync` chết; xoá luôn 2 hàm đó nếu 0 caller sống), phần test gắn flow Playwright chết trong `ShopeeShippingNavTests`/`SlipRedownloadTests` (giữ phần nào test parser/logic còn sống).
- KHÔNG xoá test của hành vi sống (OrdersRepositoryTests, AccountsViewModelTests, DatabaseMigrationTests…).

### Bước 6 — Build + test + đếm

- `dotnet build ShopeeSuite.sln` sạch; `dotnet test orders/XuLyDonShopee.Tests` toàn xanh.
- Báo cáo: số dòng `ShopeeLoginService.cs` và `AccountSession.cs` trước/sau; danh sách symbol đã xoá.

## 4. Tiêu chí nghiệm thu

- [ ] Build + test xanh; app Đơn hàng chạy được flow chính (login subaccount → sync đơn qua bridge) — smoke test tay do Fable làm sau.
- [ ] `ShopeeLoginService.cs` ≤ ~2.200 dòng.
- [ ] Grep các symbol đã xoá = 0 hit toàn repo (kể cả XAML).
- [ ] Không còn thư mục `extensions/shopee-orders-test/`; không còn nút "Mở sạch"; không còn màn Proxy trong navigation.
- [ ] Đường sống không đổi: `RunBridgeContinuousAsync`, `OpenAsync`, `TryLoginSubaccountAsync`, verify email subaccount, human-input, MS-mail-login, parsers còn nguyên và có caller.

## 5. Rủi ro & lưu ý

- RỦI RO LỚN NHẤT: xoá nhầm method mà đường bridge còn gọi gián tiếp. Bắt buộc grep từng symbol; nghi ngờ thì GIỮ và ghi chú trong báo cáo.
- Verify email có 2 ngữ cảnh: (a) verify khi login subaccount (SỐNG — bridge dùng) vs (b) verify trong flow Playwright cũ (chết). Đọc kỹ call-graph trước khi đụng `TryVerifyByEmailAsync`/các hàm Hotmail.
- Mỗi bước 1-5 là 1 commit riêng logic (Fable commit sau nghiệm thu, nhưng hãy giữ diff từng bước tách bạch trong báo cáo).
- Chạy trên cây chính (plan đợt-1 orders đã merge trước đó).

---

## Báo cáo thực thi (Opus điền sau khi xong)
