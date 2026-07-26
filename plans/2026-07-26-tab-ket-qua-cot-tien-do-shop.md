# Plan: Tab "Kết quả" — thêm cột tiến độ (chấm shop đang check + vòng quay)

- **Ngày:** 2026-07-26
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`) — CÂY CHÍNH

## 1. Bối cảnh & mục tiêu

Người dùng (nguyên văn): *"phần shopee, chi tiết tài khoản, thêm 1 cột ở đầu, cột ngắn thôi, nếu đang chạy đến
shop nào thì hiển thị 1 chấm tròn là đã check đến shop đó. nếu đang check, thêm biểu tượng quay quay và trạng
thái đang kiểm tra. nếu kiểm tra xong thì update số đơn. khi bắt đầu sang shop mới thì mới chuyển cái chấm xanh
đó sang shop bắt đầu check"*.

⇒ Lưới **Shop | Chuẩn bị hàng** ở tab "Kết quả" (màn `orders/…/Views/AccountsView.axaml`) thêm **cột đầu, hẹp**:
- **Chấm tròn** đánh dấu shop mà phiên đang chạy đã check tới.
- Shop **đang check**: **vòng quay** + chữ **"đang kiểm tra"**.
- Check xong shop đó: **số đơn cập nhật** (đã có sẵn — xem §2), vòng quay tắt, **chấm VẪN Ở LẠI**.
- **Chỉ khi shop MỚI bắt đầu** thì chấm mới chuyển sang shop đó.

## 2. Hiện trạng (đã khảo sát — bám theo)

- Vòng lặp shop nằm ở `orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs`:
  `L($"[Shop {i+1}/{shops.Count}] {shopName} — mở Chi tiết...")` (~:454); nhãn shop dùng cho đếm tính ở ~:474
  (`shop.LoginName` rỗng thì lấy `shop.ShopName`) — **phải dùng ĐÚNG nhãn này** để khớp khoá `prepare_daily`
  và khớp dòng trong lưới.
- Bridge đã có **khuôn callback** sẵn: `_onShopListRead`, `_onOrderPrepared` (ctor param optional, `?.Invoke`).
  → thêm callback mới theo đúng khuôn đó.
- `AccountSession` có `_currentShopId`/`_currentShopLogin` (:57-58) nhưng **chỉ dùng nội bộ**, không phát ra UI.
- `AppServices` đã có khuôn event bắn-từ-thread-nền: `OrdersChanged`, `AccountsChanged`, `PendingOutboxChanged`,
  và `PrepareCountChanged(long accountId)` (vừa thêm) — **người nghe PHẢI marshal về UI thread**.
- **Số đơn đã tự cập nhật rồi**: `PrepareCountChanged` bắn sau mỗi đơn arrange → `AccountsViewModel` nạp lại
  lưới nếu đúng tài khoản đang mở + ngày đang lọc là hôm nay. ⇒ Yêu cầu "kiểm tra xong thì update số đơn"
  **KHÔNG cần làm thêm gì**, chỉ cần không phá.
- `ShopPrepareRow` hiện là `record ShopPrepareRow(string ShopName, int PreparedCount)`
  (`AccountsViewModel.cs:1334`) — **bất biến**, không báo đổi được → phải chuyển thành lớp quan sát được.

## 3. Phạm vi

- **Làm:** 2 callback mới ở bridge → event ở `AppServices` → trạng thái ở `AccountsViewModel` → cột mới ở XAML.
- **KHÔNG làm:** không đụng luồng chạy/xử lý đơn (chỉ THÊM lời gọi callback); không đụng màn khác; không đụng
  `suite/**`.

## 4. Các bước thực hiện

### Bước 1 — Bridge: 2 callback mới (`OrdersBridgeSession.cs`)
Theo đúng khuôn `_onShopListRead`/`_onOrderPrepared` (ctor param optional, null-safe `?.Invoke`, có doc comment):
- `Action<string>? onShopCheckStarted` — gọi **ngay khi bắt đầu** xử lý một shop, tham số = **nhãn shop** (dùng
  ĐÚNG biểu thức `LoginName` rỗng→`ShopName` như chỗ đếm, để khớp dòng lưới).
- `Action<string>? onShopCheckFinished` — gọi khi **xong shop đó** (trước khi nghỉ để sang shop kế / hoặc kết
  thúc vòng). Phải gọi **cả khi shop đó lỗi/bỏ qua**, đừng để vòng quay quay mãi.
- Đặt lời gọi ở **cả 2 đường** nếu có (`RunAllShopsAsync` và `RunSliceCoreAsync` — kiểm cả hai như
  `_onShopListRead` đang làm).

### Bước 2 — `AppServices`: event mới
```csharp
/// accountId + nhãn shop + đang-check hay không. Bắn từ THREAD NỀN → người nghe PHẢI marshal về UI thread.
public event Action<long, string, bool>? ShopCheckChanged;
public void RaiseShopCheckChanged(long accountId, string shopLabel, bool checking) => ShopCheckChanged?.Invoke(accountId, shopLabel, checking);
```

### Bước 3 — `AccountSession`: rót callback
Chỗ đang rót `onShopListRead`/`onOrderPrepared` (~:800): rót thêm 2 callback mới, mỗi cái gọi
`_services.RaiseShopCheckChanged(_accountId, shopLabel, checking: true/false)`.

### Bước 4 — `AccountsViewModel`: trạng thái + áp vào dòng
- Đổi `ShopPrepareRow` từ `record` → **lớp `ObservableObject`** với: `ShopName` (giữ), `PreparedCount` (giữ,
  nay `[ObservableProperty]`), **`IsCurrent`** (chấm tròn), **`IsChecking`** (vòng quay + chữ).
- Thêm field nhớ **shop đang/đã check gần nhất** của tài khoản đang mở: `_checkingShopLabel` + `_isChecking`.
- Nghe `ShopCheckChanged`: bỏ qua nếu **không phải tài khoản đang mở**; ngược lại (marshal UI thread):
  - `checking == true` → đặt `_checkingShopLabel = shopLabel`, `_isChecking = true` (⇒ **chấm CHUYỂN sang shop
    mới ngay lúc bắt đầu** — đúng yêu cầu).
  - `checking == false` → **GIỮ NGUYÊN `_checkingShopLabel`**, chỉ `_isChecking = false` (⇒ chấm **ở lại** shop
    vừa xong cho tới khi shop kế bắt đầu — đúng yêu cầu).
  - Rồi áp lại cờ lên các dòng: dòng có `ShopName` khớp nhãn → `IsCurrent = true`, các dòng khác `false`;
    `IsChecking = IsCurrent && _isChecking`.
- **`LoadResults()` phải áp lại cờ** sau khi dựng lại danh sách (nếu không, mỗi lần số đơn cập nhật là chấm/vòng
  quay biến mất — đây là bẫy chính của việc này).
- So khớp nhãn shop: **trim + không phân biệt hoa/thường** (nhãn từ bridge và tên trong `account_shops` có thể
  lệch hoa/thường).

### Bước 5 — XAML: cột đầu hẹp (`Views/AccountsView.axaml`, lưới tab "Kết quả")
- Thêm cột đầu **rộng ~40**, không tiêu đề (hoặc tiêu đề rỗng):
  - `Ellipse` 8px màu `SuccessBrush`, `IsVisible="{Binding IsCurrent}"` — nhưng **ẩn khi đang quay**
    (`IsCurrent && !IsChecking`) để không chồng lên vòng quay.
  - `PathIcon` `{DynamicResource IconRefresh}` 14px màu `AccentBrush`, `IsVisible="{Binding IsChecking}"`, **quay
    liên tục**: `Style.Animations` + `RotateTransform` 0→360°, `Duration=0:0:1`, `IterationCount=Infinite`
    (khuôn animation đã có ở chấm nhấp nháy thanh trạng thái — `suite/…/Themes/Theme.axaml`, `Ellipse.statusDot`).
- Cột "Chuẩn bị hàng": khi `IsChecking` → hiện chữ **"đang kiểm tra…"** (11px, muted) THAY cho số; hết check →
  hiện lại số.
- Dùng đúng hệ màu/icon hiện hành (không hard-code hex nếu đã có token).

## 5. Tiêu chí nghiệm thu

- [ ] `dotnet build` 0 error; `dotnet test XuLyDonShopee.Tests` xanh + **test mới** cho logic chuyển chấm:
      bắt đầu shop A → A có `IsCurrent`; xong A → A vẫn `IsCurrent` nhưng hết `IsChecking`; bắt đầu shop B →
      chấm chuyển sang B, A hết `IsCurrent`; sự kiện của **tài khoản khác** → không đổi gì.
- [ ] Test: gọi `LoadResults()` (mô phỏng số đơn vừa cập nhật) **KHÔNG làm mất** chấm/vòng quay.
- [ ] Chạy thật 1 phiên: chấm chạy theo đúng shop đang check; đang check có vòng quay + "đang kiểm tra";
      shop xong thì số đơn cập nhật, vòng quay tắt, chấm ở lại; sang shop mới chấm mới nhảy.
- [ ] Shop lỗi/bỏ qua vẫn tắt được vòng quay (không quay vĩnh viễn).
- [ ] Không đụng `suite/**`; không đổi luồng xử lý đơn.

## 6. Rủi ro & lưu ý

- **Bẫy lớn nhất:** `LoadResults()` dựng lại `ResultRows` → mất cờ. Phải áp lại cờ ngay sau khi dựng. Số đơn giờ
  tự cập nhật sau MỖI đơn nên `LoadResults()` chạy rất thường xuyên khi đang chạy.
- Nhãn shop phải **khớp đúng** biểu thức đang dùng cho `prepare_daily`, nếu không chấm sẽ không tìm thấy dòng.
- Callback bắn từ **thread nền** → marshal UI thread (khuôn `RunOnUi` đã có trong `AccountsViewModel`).
- Đổi `ShopPrepareRow` từ `record` sang lớp: kiểm mọi nơi khởi tạo/so sánh (record có value-equality, lớp thì
  không) — đặc biệt chỗ dựng `ResultRows` trong `LoadResults`.
- Vòng quay là animation chạy liên tục: chỉ bật khi `IsChecking` để không tốn CPU vô ích khi rảnh.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa thực thi>
