# Plan: tự tải lại phiếu thiếu khi check shop + dọn UI ô Phiếu / tab Shops / ô lịch

- **Ngày:** 2026-08-06
- **Trạng thái:** đang làm
- **Người lập:** phiên chính · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Người dùng phát hiện 3 chuyện khi dùng bản v1.7.8:

1. **Ô "Phiếu" loằng ngoằng.** Có dòng chỉ "In phiếu", có dòng "In phiếu / Tải phiếu"; bấm "Tải phiếu" thì báo
   phải chạy shop đó trước, mà app **không có** luồng chạy riêng một shop.
2. Hỏi lại: *"sao không tự tải lại khi check lại shop?"* → **đây là hướng đã chốt**.
3. Tab "Kết quả" nên đổi tên thành **"Shops"**; ô chọn ngày (màn Tài khoản) **nhìn xấu**.

**Hiện trạng đã kiểm chứng bằng code:**
- Phiếu chỉ được lưu ĐÚNG MỘT LẦN, ngay lúc arrange: `ShopFlowRunner` dòng ~249 `TrySaveSlip(prep.SlipBase64, …)`
  trong vòng `prepareNextOrder`. Lưu hỏng / đơn đã arrange từ vòng trước / từ máy khác ⇒ **không có đường thử lại**
  (vòng sau chỉ hỏi "đơn KẾ cần chuẩn bị hàng", đơn đã arrange không quay lại).
- Nút "Tải phiếu" (`OrderRowViewModel.ShowRedownloadSlip`, dòng ~165) hiện khi *có mã vận đơn NHƯNG thiếu file
  PDF*. Bấm được thì cần **ba** điều kiện mà UI chỉ nói một: phiên tài khoản đang chạy
  (`OrdersViewModel.cs:564`), phiên rảnh, **và tab đang mở đúng shop của đơn** (`ShopFlowRunner.cs:501` —
  extension quét danh sách trên tab đang mở; đơn của shop khác → base64 rỗng → false).
- `CanPrintSlip` (dòng ~94) chỉ loại đơn "hủy", **không xét file có tồn tại không** ⇒ dòng thiếu phiếu vẫn hiện
  nút "In phiếu" hỏng.
- Không có luồng chạy riêng một shop: chỉ `OrdersBridgeSession.RunAllShopsAsync`; `RunSliceCoreAsync` ("Chạy
  thử") cứng nhắc lấy **shop đầu tiên**.
- Không có style nào cho `DatePicker`/`Calendar` ở CẢ module lẫn `suite/Themes/Theme.xaml` ⇒ 5 ô ngày của module
  rơi về theme Aero2 mặc định.

**Quyết định đã chốt với người dùng:**
- Tự tải lại phiếu thiếu **ngay trong vòng check shop** (không làm lệnh "chạy riêng 1 shop").
- Ô "Phiếu" chỉ còn **MỘT** hành động: chưa có phiếu → "Tải lại"; đã có phiếu → "In phiếu".

## 2. Phạm vi

- **Làm:** 4 việc ở mục 3.
- **Không làm:**
  - KHÔNG thêm lệnh "chạy riêng một shop" (người dùng chọn hướng tự-tải-lại thay cho việc này).
  - KHÔNG đổi luật dọn đơn kết thúc, không đổi hợp đồng cầu nối/extension (`extensions/shopee-orders` giữ nguyên
    — action `redownloadSlip` đã có sẵn, chỉ gọi thêm).
  - KHÔNG đụng `server/Shopee.Hub.Web`.
  - KHÔNG đổi `suite/Shopee.Suite/Themes/Theme.xaml` (style ô lịch nằm ở module).
  - KHÔNG đổi tên file `AccountsViewModel.KetQua.cs` (đổi tên file là việc riêng, rủi ro merge).

## 3. Các bước thực hiện

### Bước 1 — Tự tải lại phiếu THIẾU ngay trong vòng check shop (việc chính)

Chỗ làm: `orders/XuLyDonShopee.Core/Services/ShopFlowRunner.cs`, **sau** khi vòng `prepareNextOrder` của shop kết
thúc và **sau** `_syncCallback` đã lưu đơn (để danh sách thiếu phiếu tính trên dữ liệu vừa cập nhật).

- Thêm callback do App rót (giống mẫu `_syncCallback`, `_onOrderPrepared`):
  `Func<string /*shopLogin*/, CancellationToken, Task<IReadOnlyList<string>>>? _layDonThieuPhieu`
  → App trả danh sách `order_sn` **của đúng shop này** đang: có mã vận đơn **và** thiếu file PDF hợp lệ
  (dùng `SlipFiles.SlipFileIsValidPdf`, đúng bộ luật nút "Tải phiếu" đang dùng — hai nơi không được tự tính khác nhau).
- Với mỗi mã, gọi `RedownloadSlipAsync(orderSn, ct)` (đã có sẵn, tab đang mở ĐÚNG shop nên chạy được).
- **Trần có tên:** `TranTaiLaiPhieuMoiShop = 20` — đơn thiếu phiếu có thể tồn đọng hàng trăm cái ở lần đầu; lấy
  **mới nhất trước**, phần còn lại để vòng sau. Log rõ số bỏ lại (**không được im lặng cắt**).
- Bỏ qua cả bước này khi: không có `_invoiceDir`, đã thấy captcha (`_ch.CaptchaSeen`), hoặc `ct` đã hủy.
- Đơn quá cũ không còn trong danh sách "Tất cả" → extension trả rỗng → `false`: log "không thấy đơn", **đi tiếp**,
  không retry trong cùng vòng (kẻo mỗi vòng đều tốn 20 lượt cho các đơn không bao giờ lấy được).
- Log tổng kết một dòng: `Tải lại phiếu thiếu shop X: n/m thành công (còn k đơn để vòng sau).`
- App rót callback: `AccountSession` (chỗ đang rót `_syncCallback`/`_onOrderPrepared`) → đọc từ
  `OrdersRepository` các đơn của shop đó có `tracking_number` khác rỗng, rồi lọc bằng `SlipFiles.SlipFileIsValidPdf`.
  **Hàm chọn danh sách phải THUẦN và test được** (vào: list (order_sn, tracking, có-file) + trần → ra: list cần tải).

### Bước 2 — Ô "Phiếu" chỉ còn MỘT hành động

`orders/XuLyDonShopee.App/ViewModels/OrderRowViewModel.cs` + `Views/OrdersView.xaml`:
- `CanPrintSlip` thêm điều kiện `HasSlipFile` ⇒ thiếu file thì KHÔNG hiện "In phiếu" nữa.
- Đổi nhãn nút `Tải phiếu` → **`Tải lại`** (đúng chữ người dùng dùng), giữ `RedownloadSlipCommand`.
- Hai nút loại trừ nhau: đã có phiếu → chỉ "In phiếu"; chưa có → chỉ "Tải lại".
- ToolTip nút "Tải lại" nói ĐỦ ba điều kiện thật: *"Tải lại phiếu bị thiếu — cần phiên tài khoản đang chạy và
  đang mở đúng shop này. Bình thường vòng check shop sẽ tự tải lại."*
- Test: ma trận (có/không tracking) × (có/không file) × (hủy/không hủy) → đúng một nút hiện (hoặc không nút nào).

### Bước 3 — Đổi tên tab "Kết quả" → "Shops"

- `orders/XuLyDonShopee.App/Views/AccountsView.xaml`: nhãn `TabItem.Header` (dòng ~662) và các comment mô tả tab
  trong CHÍNH file này (dòng ~147, ~432, ~657).
- Quét thay cụm `tab "Kết quả"` → `tab "Shops"` trong comment/xmldoc của `orders/` (thuần comment, không đụng
  logic). **Chỉ đổi đúng cụm nói về TAB** — "Kết quả xin KHÓA CHẠY", "Kết quả thống kê dùng chung"… giữ nguyên.
- Badge số bên cạnh nhãn giữ nguyên.

### Bước 4 — Ô chọn ngày nhìn phẳng, đúng định dạng Việt

`orders/XuLyDonShopee.App/Styles/Controls.xaml` — thêm style IMPLICIT (áp cho cả 5 ô ngày của module: 1 ở
AccountsView, 2 ở OrderStatisticsView, 2 ở OrdersView):
- `{x:Type DatePicker}`: `Language="vi-VN"` + `SelectedDateFormat="Short"` đặt NGAY TRONG style (WPF lấy culture
  hiển thị từ `FrameworkElement.Language`; để mặc định thì máy đặt vùng Mỹ hiện `8/6/2026` trong khi cả app viết
  `06/08/2026`). Template phẳng: `Border` bo 4, viền `Border010` 1px, nền `InputBg`, `MinHeight` 30, `FontSize`
  12.5 — **khớp `fieldCombo`** (Controls.xaml ~306). Nút mở lịch = icon lịch vẽ bằng `Path`, nền trong suốt,
  hover `ButtonHoverBg`. Trigger: hover → viền đậm hơn; `IsKeyboardFocusWithin` → viền `AccentBrush`;
  `IsEnabled=False` → mờ. Template PHẢI giữ đúng tên part: `PART_TextBox`, `PART_Button`, `PART_Popup`.
- `{x:Type DatePickerTextBox}`: template gọn còn `ScrollViewer x:Name="PART_ContentHost"`, nền trong suốt, viền 0
  (mặc định Aero2 tự vẽ hộp riêng → không dẹp thì lồng hai khung).
- Lịch bung ra: style `{x:Type Calendar}` (nền `CardBg`, viền `Border010`) + `{x:Type CalendarDayButton}` phẳng
  (bỏ gradient Aero: hover `ButtonHoverBg`, hôm nay + đang chọn dùng `AccentBrush`, ngày tháng khác mờ).
- Bỏ `Language="vi-VN"` khai lẻ ở `OrderStatisticsView.xaml` (style lo rồi) — nhưng **kiểm chứng lại** 2 ô đó vẫn
  hiện `dd/MM/yyyy` sau khi bỏ.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` — **0 warning, 0 error**.
- [ ] `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj` — xanh, **không giảm số test** (hiện 1604).
- [ ] Test mới: hàm thuần chọn đơn cần tải lại (trần + ưu tiên mới nhất + bỏ đơn đã có file); ma trận ô "Phiếu"
      (đúng MỘT nút hiện); `ShopFlowRunner` bỏ qua bước tải lại khi thiếu `_invoiceDir` / gặp captcha.
- [ ] **Thử phá từng test mới** → phải ĐỎ. Báo rõ từng mutant.
- [ ] Phiên chính render bằng harness: ô ngày hiện `dd/MM/yyyy`, khung phẳng khớp ô ComboBox bên cạnh; lịch bung
      ra không còn gradient Aero.
- [ ] `git diff` không chạm file ngoài mục 2 (đặc biệt: không đụng `server/`, không đụng `extensions/`).

## 5. Rủi ro & lưu ý

- **Bước 1 là chỗ đắt nhất:** mỗi lượt `redownloadSlip` là một vòng điều hướng thật trên Seller Centre. Trần 20
  và "không retry trong cùng vòng" là hai cái phanh — **đừng bỏ**, kẻo vòng check shop dài gấp đôi vì ngồi tải
  lại mấy đơn đã mất khỏi danh sách.
- Luật "thiếu phiếu" phải dùng CHUNG `SlipFiles.SlipFileIsValidPdf` với nút "Tải lại" — hai nơi tự tính riêng là
  lệch (đơn hiện nút mà vòng không tải, hoặc ngược lại).
- Bước 1 chạy SAU `_syncCallback`: chạy trước thì danh sách thiếu phiếu tính trên DB cũ, sót đúng các đơn vừa arrange.
- **Đường "Chạy thử"** (`RunSliceCoreAsync`, callback null) KHÔNG được kéo theo bước tải lại — nó chỉ đọc, không lưu.
- Bước 4: retemplate `DatePicker` mà sai tên part (`PART_TextBox`/`PART_Button`/`PART_Popup`) thì ô ngày **chết
  câm** (bấm không bung lịch) mà build vẫn xanh — phải soi bằng harness, không tin build.
- Bước 3: chỉ đổi cụm nói về TAB. `Kết quả` còn xuất hiện ở nghĩa khác trong `AppServices.cs` — đổi nhầm là làm
  sai xmldoc của thứ khác.

---

## Báo cáo thực thi

<chưa có>
