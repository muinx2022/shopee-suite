# Plan: Số "chuẩn bị hàng" tự sang ngày mới (client + kiểm chứng hub)

- **Ngày:** 2026-07-28
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh — lỗi người dùng báo

> *"Phần đơn hàng chuẩn bị, cả hub và client đều reset ngày mới nhé."*

Truy vết ở `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs`:

```csharp
private DateTimeOffset _resultDate = DateTimeOffset.Now;   // đặt MỘT LẦN lúc dựng ViewModel (mở app)

private void OnPrepareCountChanged(long accountId) => RunOnUi(() =>
{
    if (SelectedRow is null || SelectedRow.Id != accountId) return;
    if (ResultDate.Date != DateTimeOffset.Now.Date) return;   // ← qua nửa đêm là NGHỈ LUÔN
    LoadResults();
});
```

Máy chạy xuyên đêm (đúng cách module Đơn hàng vận hành — vòng lặp liên tục): lúc 00:00 ô ngày ở tab **Kết quả** vẫn
đứng ở **hôm qua**, số đóng băng ở con số hôm qua, và **đơn chuẩn bị của ngày mới không hiện ra nữa** vì hàm cập
nhật thoát sớm. Phải đóng/mở lại app mới thấy. `ViewModel` **không có timer nào** nên không bao giờ tự biết ngày đã sang.

**Phía hub:** `prepared_day` được đóng dấu **theo từng đơn** tại thời điểm chuẩn bị (giờ địa phương máy đã chuẩn bị
— `OrdersModuleHost` dòng ~975), và `HubDatabase.PrepareStatsByDay(day)` trả số theo ĐÚNG ngày được hỏi. Nghĩa là dữ
liệu hub vốn đã đúng ngày; gốc bệnh là **client hỏi nhầm ngày**. Vẫn phải kiểm chứng lại (Bước 4) chứ không mặc định đúng.

## 2. Phạm vi

**Làm:**
- Client tự chuyển ô ngày tab "Kết quả" sang ngày mới khi qua nửa đêm, kéo theo nạp lại số (cục bộ + hub).
- `OnPrepareCountChanged` không còn im lặng bỏ qua khi ngày vừa sang.
- Kiểm chứng đường hub thật sự trả số theo ngày được hỏi (không cache, không dính ngày cũ).

**Không làm:**
- KHÔNG tự đổi ngày khi người dùng đã **chủ động chọn một ngày cũ** để xem lại — chỉ tự chuyển khi họ đang xem
  "hôm nay".
- KHÔNG đụng cách đóng dấu `prepared_day` (đang đúng: theo giờ địa phương của máy chuẩn bị đơn).
- KHÔNG đụng số "đơn chờ lấy hàng" trên hub — đó là số theo TRẠNG THÁI, không theo ngày, không được reset.
- KHÔNG commit, KHÔNG deploy, KHÔNG release.

## 3. Các bước thực hiện

### Bước 1 — Nhịp phát hiện sang ngày (`AccountsViewModel`)

Thêm một timer nhẹ (khuyến nghị **60s** — đủ nhạy, gần như không tốn gì) + một mốc nhớ:

```csharp
/// <summary>Ngày mà ô lọc "Kết quả" coi là HÔM NAY ở lần cập nhật gần nhất. Dùng để phân biệt "người dùng đang
/// xem hôm nay" (→ tự chuyển sang ngày mới lúc qua nửa đêm) với "người dùng chủ động chọn ngày cũ để xem lại"
/// (→ TUYỆT ĐỐI không giật ngày khỏi tay họ).</summary>
private DateTime _ngayCoiLaHomNay = DateTimeOffset.Now.Date;
```

`private void KiemTraSangNgay()`:
- `var homNay = DateTimeOffset.Now.Date;` — bằng `_ngayCoiLaHomNay` thì thoát.
- Nếu `ResultDate.Date == _ngayCoiLaHomNay` (đang xem hôm-nay-cũ) → `ResultDate = DateTimeOffset.Now`.
  **Không gọi `LoadResults()` tay** — `OnResultDateChanged` đã tự làm (`LoadResults()` + `RefreshHubCountsAsync()`).
- Cập nhật `_ngayCoiLaHomNay = homNay` (làm ở CẢ hai nhánh — kể cả khi người dùng đang xem ngày cũ, để hôm sau
  không hiểu nhầm ngày họ chọn là "hôm nay").
- Timer phải marshal về UI thread trước khi đụng state (dùng đúng `RunOnUi` mà VM đang dùng cho sự kiện nền).
- Dispose timer khi VM dispose (theo đúng khuôn dọn tài nguyên VM đang có; nếu VM chưa có chỗ dọn thì tìm nơi phù
  hợp và ghi rõ trong báo cáo).

### Bước 2 — `OnPrepareCountChanged` không được câm

Hiện thoát sớm khi `ResultDate.Date != Now.Date`. Sửa: **gọi `KiemTraSangNgay()` TRƯỚC**, rồi mới xét điều kiện cũ.
Nhờ đó ngay đơn đầu tiên của ngày mới đã kéo ô ngày sang — không phải chờ hết một nhịp timer.

Giữ nguyên điều kiện `SelectedRow.Id != accountId` (chỉ vẽ lại khi đúng tài khoản đang mở).

### Bước 3 — Chỗ khác cũng phải sang ngày

Rà các chỗ so ngày/ghi ngày liên quan tới tab Kết quả rồi xử cho nhất quán (đọc code, đừng đoán):
- `ResultDayKey` (khoá `yyyy-MM-dd` gửi hub + tra `prepare_daily`) — tự đúng khi `ResultDate` đã chuyển.
- `_hubCountsDay` / `ClearHubCountsIfContextChanged` — bảo đảm map hub của ngày cũ bị quên khi sang ngày, không áp
  nhầm số hôm qua lên lưới hôm nay.
- `TongChuanBiHang` (dòng tổng trên lưới) — cộng từ `ResultRows` nên tự theo; **xác nhận** nó về đúng số ngày mới.

### Bước 4 — Kiểm chứng phía hub (đọc + thử, sửa CHỈ KHI thật sự sai)

- Đọc lại `HubDatabase.PrepareStatsByDay(day)` + endpoint `GET /prepare-stats?day=` và khẳng định: trả số theo đúng
  ngày được hỏi, **không cache**, không có ngày mặc định dính lúc khởi động.
- Thử thật: seed đơn có `prepared_day` = hôm qua và hôm nay → hỏi hai ngày, phải ra hai con số khác nhau; hỏi ngày
  không có đơn → rỗng (không phải lỗi).
- **Nếu phát hiện hub thật sự dính ngày cũ ở đâu đó** → sửa và ghi rõ; nếu hub đã đúng → ghi rõ "hub không cần sửa,
  gốc bệnh chỉ ở client" trong báo cáo.

### Bước 5 — Test

Đặt ở `orders/XuLyDonShopee.Tests`. Phần thuần logic là quy tắc quyết định "có tự chuyển ngày không" — **tách ra
một hàm thuần** để test được, đừng test bằng cách chờ timer:

```csharp
// (ngayDangXem, ngayCoiLaHomNay, homNayThat) → có chuyển không + ngày mới
public static (bool Chuyen, DateTime NgayMoi) QuyetDinhSangNgay(DateTime dangXem, DateTime coiLaHomNay, DateTime homNay);
```
Ca cần phủ:
- Đang xem hôm-nay-cũ, ngày đã sang → **chuyển**.
- Đang xem một ngày cũ do người dùng tự chọn, ngày đã sang → **KHÔNG chuyển** (nhưng mốc "coi là hôm nay" vẫn cập nhật).
- Ngày chưa sang → không làm gì.
- Máy bị chỉnh lùi đồng hồ (hôm nay < mốc cũ) → không rơi vào vòng lặp đổi qua đổi lại; ghi rõ hành vi chọn.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` sạch, 0 warning mới; `dotnet test orders/XuLyDonShopee.Tests` xanh kèm test mới.
- [ ] Test `QuyetDinhSangNgay` phủ đủ 4 ca ở Bước 5.
- [ ] Mô phỏng được (bằng test hoặc chạy tay có chỉnh mốc): đang xem hôm nay → qua nửa đêm → ô ngày sang ngày mới,
      lưới nạp lại, **dòng tổng về số của ngày mới** (không giữ số hôm qua).
- [ ] Người dùng chọn ngày cũ để xem lại → qua nửa đêm **KHÔNG bị giật** khỏi ngày đang xem.
- [ ] Hub: hỏi `?day=` hai ngày khác nhau ra hai con số khác nhau; kết luận rõ hub có cần sửa hay không.
- [ ] Không đụng số "đơn chờ lấy hàng" (theo trạng thái) và không đụng cách đóng dấu `prepared_day`.

## 5. Rủi ro & lưu ý

- **Đừng giật ngày khỏi tay người dùng.** Họ mở ngày cũ để đối chiếu mà app tự nhảy về hôm nay thì tệ hơn bug đang sửa.
- Timer chạy nền → **phải marshal về UI thread** trước khi đụng `ResultDate`/`ResultRows` (VM đã có `RunOnUi`).
- Đổi `ResultDate` sẽ kích `OnResultDateChanged` → `LoadResults()` + `RefreshHubCountsAsync()`. **Đừng gọi lại tay**
  kẻo nạp hai lần (một lượt gọi hub thừa qua tunnel).
- Múi giờ: mọi so sánh dùng giờ **địa phương**, khớp cách `prepared_day` được đóng dấu. Đừng lẫn UTC vào.
- Module Đơn hàng chạy vòng liên tục cả đêm — đây đúng là kịch bản thường gặp, không phải ca hiếm.

---

## Báo cáo thực thi (Opus điền sau khi xong)
