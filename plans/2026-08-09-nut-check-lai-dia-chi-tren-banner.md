# Plan: Nút "Check" trên banner lỗi địa chỉ — kiểm tra lại shop, tự gỡ banner khi hết lỗi

- **Ngày:** 2026-08-09
- **Trạng thái:** đang làm — người dùng chốt 09/08: làm nút Check NGAY; nút **chỉ chạy khi không có phiên**
  (đang chạy vòng thì khoá nút kèm chỉ dẫn) ⇒ **BỎ** hàng đợi ưu tiên + chèn giữa phiên ở mục 2/3 bên dưới,
  và bỏ luôn rủi ro "cắt ngang shop đang xử" ở mục 5.
- **Người lập:** Opus 5 (phiên chính)

## 1. Bối cảnh

Người dùng gửi ảnh màn hình máy chạy thật: 4 banner "Cảnh báo: Lỗi địa chỉ" (piko.store1, rudi.store,
minoa.store, hanily.store) nằm lại trên tab Shops, và yêu cầu 4 việc:

1. Có nút **Check** ngay trên banner để kiểm tra lại shop đó; chạy song song với flow check đơn, nếu flow không
   chạy thì dùng chính flow đó để check.
2. Lỗi đặt địa chỉ thường do Shopee bật modal cảnh báo (shop chính chủ) đè lên trang, nuốt cú bấm ⇒ phải **tìm
   và đóng modal khi bị lỗi**.
3. Khi check, **tự kiểm chứng** có đặt được địa chỉ không; PASS thì gỡ banner, báo hub, gỡ banner ở client khác.
4. Vòng lặp shop kế tiếp, shop nào không còn lỗi địa chỉ cũng phải gỡ banner + báo hub + cập nhật client khác.

### ⚠ Phát hiện quan trọng: máy đó đang chạy **v1.8.2**

Thanh trạng thái trong ảnh ghi `v1.8.2`. Hai trong bốn yêu cầu **đã được làm và đã phát hành ở v1.8.4**
(GitHub, 08/08) — máy này chưa cập nhật nên chưa có:

| Yêu cầu | Hiện trạng |
|---|---|
| (2) Tìm + đóng modal chắn trang | **CÓ từ v1.8.4** — `dongModalChan` (`extensions/shopee-orders/flow-address.js:24`), gọi ở 4 chỗ; hạn bước địa chỉ nới 90→240s cho đủ 2 lượt thử |
| (4) Vòng sau hết lỗi thì tự gỡ banner + báo hub + client khác | **CÓ từ v1.8.4** — `PickupOkShop` → `GoBannerLoiDiaChi` (`OrderPersistPipeline.cs:558`): gỡ local + `RaiseAddressAlertsChanged` + `DismissPickupAlertToHub` qua `PickupAlertHubGate` (theo rev) ⇒ hub broadcast, client khác tự gỡ |
| (1) Nút Check trên banner | **CHƯA CÓ** |
| (3) Check xong tự kiểm chứng rồi gỡ banner | **CHƯA CÓ** (nhưng đường gỡ + báo hub đã có sẵn, dùng lại được nguyên) |

⇒ Việc rẻ nhất và nhanh nhất là **cập nhật máy đó lên bản mới** rồi đọc lại nhật ký một vòng; rất có thể phần
lớn 4 banner kia tự hết. Bản mới cũng ghi sẵn tiêu đề + nhãn nút của modal không đóng được — đó chính là dữ
liệu cần để nhận diện modal "shop chính chủ".

### Ràng buộc kiến trúc: cầu nối chỉ có MỘT lane

`OrdersBridgeChannel.BridgePort = 47821` là cổng CỐ ĐỊNH, một phiên tại một thời điểm (mục B8 trong danh sách
tồn đọng: "chỉ chạy được 1 phiên một lúc"). Nên **"chạy song song" theo nghĩa mở một trình duyệt thứ hai là
không làm được** nếu chưa làm B8 (cấp cổng trống cho mỗi phiên + gỡ `KillBrowsersOnProfile` giết chéo).

Hai đường:

- **(A) Xen vào phiên đang chạy** — nút Check đẩy shop vào hàng đợi ưu tiên; vòng shop nhặt ở **ranh giới giữa
  hai shop** (điểm an toàn, không cắt ngang shop đang xử). Không có phiên → tự khởi động phiên ở chế độ
  "chỉ 1 shop, chỉ bước địa chỉ". Khớp đúng câu người dùng: *"nếu flow check đơn không chạy, sử dụng flow check
  đơn để check"*.
- **(B) Làm B8 trước** rồi mới song song thật. Việc lớn hơn nhiều, đụng vòng đời trình duyệt.

**Đề xuất: (A).**

## 2. Phạm vi

### Làm

1. **UI** — thêm nút "Check" cạnh "✕ Đóng" trên mỗi dòng banner (`PickupAlertRow` + XAML tab Shops). Trạng thái:
   `Check` → `Đang kiểm…` (khoá nút) → kết quả.
2. **Hàng đợi ưu tiên** — `YeuCauKiemTraDiaChi(accountId, shopLogin)`. Vòng shop trong `OrdersBridgeSession`
   nhặt ở ranh giới giữa hai shop; không có phiên → khởi động phiên chế độ **`ChiKiemTraDiaChi`** (bỏ đọc đơn,
   bỏ chuẩn bị hàng, bỏ in phiếu, bỏ check trả hàng).
3. **Lượt kiểm tra** = mở Cài đặt vận chuyển của shop → `dongModalChan` → thử đặt địa chỉ → đọc kết quả, dùng
   lại nguyên `QuyetDinhSauDatDiaChi`. Không viết luật mới.
4. **PASS** → đặt `PickupOkShop` → đi đúng đường sẵn có `GoBannerLoiDiaChi` (gỡ local + hub + client khác).
   **KHÔNG viết đường gỡ banner thứ hai.**
5. **FAIL** → giữ banner, và **hiện lý do đọc được** lên banner (tiêu đề modal + nhãn nút không đóng được),
   thay vì chỉ ghi nhật ký như hiện nay.
6. **Chẩn đoán modal khi FAIL** — extension gửi kèm tiêu đề + HTML rút gọn của modal đang chắn khi bước địa chỉ
   thất bại (hiện chỉ có `progress` chung). Đây là thứ để ghim selector modal "shop chính chủ" ở đợt sau.

### Không làm

- KHÔNG làm B8 (đa-lane) trong đợt này.
- KHÔNG đổi luật `QuyetDinhSauDatDiaChi`, không đổi hành vi "lỗi địa chỉ ⇒ bỏ shop, không in phiếu".
- KHÔNG mở rộng danh sách nhãn nút đóng modal khi CHƯA có nhãn thật từ nhật ký — đoán nhãn là cách chắc chắn
  nhất để bấm nhầm vào nút khác trên trang đang hỏng.
- KHÔNG commit / phát hành (theo yêu cầu người dùng).

## 3. Các bước

1. `PickupAlertRow`: thêm `CheckCommand`, `DangKiem`, `LyDoLoi`. XAML tab Shops thêm nút.
2. `AppServices`: hàng đợi `KiemTraDiaChiChoDoi` (thread-safe, khử trùng theo `(accountId, shopLogin)`).
3. `OrdersBridgeSession`: điểm nhặt hàng đợi ở ranh giới shop; chế độ `ChiKiemTraDiaChi` cho phiên khởi động
   theo yêu cầu Check.
4. `ShopFlowRunner`: tách `DatDiaChiAsync` khỏi `ThanShopAsync` để lượt kiểm tra dùng lại đúng bước đó.
5. Extension: khi bước địa chỉ FAIL, đính kèm chẩn đoán modal vào phản hồi `setPickupAddress`.
6. Test: xem mục 4.

## 4. Tiêu chí nghiệm thu

- [ ] Bấm Check khi **không có phiên** ⇒ phiên khởi động, chỉ chạy bước địa chỉ đúng 1 shop, không đọc đơn/không in phiếu.
- [ ] Bấm Check khi **đang có phiên** ⇒ xếp hàng, chạy ở ranh giới giữa hai shop, KHÔNG cắt ngang shop đang xử.
- [ ] Check PASS ⇒ banner biến mất tại chỗ **và** `DismissPickupAlertToHub` được gọi đúng 1 lần (client khác gỡ theo rev).
- [ ] Check FAIL ⇒ banner CÒN, kèm lý do đọc được; không đụng gì tới đơn/phiếu.
- [ ] Bấm Check 2 lần liên tiếp cùng shop ⇒ chỉ chạy 1 lượt (khử trùng hàng đợi).
- [ ] Build 2 solution 0 lỗi / 0 warning; test orders không tụt; mỗi tiêu chí trên có test THỬ PHÁ được.

## 5. Rủi ro

- **Cắt ngang phiên đang chạy là rủi ro lớn nhất** — chèn một lượt điều hướng giữa lúc đang xử đơn/in phiếu có
  thể làm hỏng shop đang chạy. Vì vậy điểm nhặt PHẢI ở ranh giới giữa hai shop, không phải "bất cứ lúc nào".
- Modal "shop chính chủ" **chưa có mẫu DOM thật** ⇒ đợt này chỉ làm phần *thu thập chẩn đoán*; ghim selector để
  đợt sau, sau khi có nhật ký thật.
- Máy trong ảnh chạy v1.8.2 ⇒ mọi kết luận "modal không đóng được" hiện nay **chưa có giá trị**: bản đó chưa hề
  có bước đóng modal.

---

## Báo cáo thực thi (2026-08-09)

### Đã làm

**Core.** Tách bước đặt địa chỉ thành `ShopFlowRunner.DatDiaChiAsync` — vòng shop thường và lượt kiểm tra đi
CÙNG một lệnh (bên trong extension bước này còn có dọn modal + thử lại một lượt; chép đôi là trôi lệch ngay).
Thêm `ShopFlowRunner.KiemTraLaiDiaChiAsync`: chỉ chạy bước địa chỉ, đặt `PickupOkShop`/`PickupFailedShop` y hệt
vòng thường. **Captcha thì KHÔNG đặt cờ nào** — coi là "vẫn lỗi" thì giữ banner oan, coi là "hết lỗi" thì gỡ
banner của shop chưa hề kiểm được. Thêm `OrdersBridgeSession.KiemTraDiaChiMotShopAsync` (đăng nhập → picker →
tra shop theo `LoginName` rồi `ShopName`, không phân biệt hoa/thường → mở Chi tiết → chỉ bước địa chỉ) +
record `OrdersKiemTraDiaChiResult`.

Hàm này gọi chính `LoginAndReachPickerAsync` nên **thừa hưởng luật v1.8.3**: cookie còn hạn thì bấm Check chỉ
mở bản sạch, không đụng Playwright, không phải nhập mã.

**App.** `PickupAlertRow` thêm `DangKiem` / `KetQuaCheck` / `NhanNutCheck`. `AccountsViewModel`: tách
`GoBannerVaBaoHub` dùng chung cho ✕ Đóng và Check (một đường gỡ duy nhất — luật rev/tombstone đã cắn hai lần,
viết đường thứ hai là mời cắn lần ba); tách `TaoPhienCauNoi` dùng chung với "Chạy thử (bridge)" (hai công thức
hồ sơ trôi lệch = mở nhầm profile = mất cookie); thêm `CheckAddressAlertCommand` — **từ chối kèm chỉ dẫn khi
đang có phiên** (cầu nối một lane), hiện kết quả ngay trên banner.

**XAML.** Nút "Check" cạnh "✕ Đóng", đổi nhãn thành "Đang kiểm…" + khoá khi đang chạy; dòng kết quả hiện dưới
chữ cảnh báo (bấm nút xong mà banner không đổi gì thì không phân biệt được "vẫn lỗi" với "nút hỏng").

### Kiểm chứng

`dotnet build ShopeeSuite.sln` **0 lỗi / 0 warning**; orders **1688/1688**, hub **120/120**.
4 test mới trong `OrdersBridgeFlowTests` (đặt được / vẫn lỗi / captcha không kết luận / nhãn shop rỗng), trong
đó ca "đặt được" còn canh **KHÔNG có lệnh nào khác được gửi** (không đọc đơn, không chuẩn bị hàng, không in phiếu).

**THỬ PHÁ (2 lượt, đều đỏ đúng chỗ rồi khôi phục):**

| Phá | Test đỏ |
|---|---|
| Vô hiệu chốt captcha (`if (false)`) | `KiemTraLaiDiaChi_Captcha_KhongKetLuan_CaHaiCoDeuNull` (3 ca kia vẫn xanh) |
| Bỏ xử lý nhãn shop rỗng | `KiemTraLaiDiaChi_NhanShopRong_VanRaChuoiKhacNull` (3 ca kia vẫn xanh) |

Lượt phá đầu tiên dùng regex `perl` **trượt** (không sửa được gì) mà test vẫn xanh — suýt ghi nhận nhầm thành
"đã thử phá". Đã làm lại bằng sửa trực tiếp; bài học: luôn in ra bằng chứng file ĐÃ đổi trước khi tin lượt phá.

### Chưa làm

- **Mục 6 phạm vi — chẩn đoán modal khi FAIL.** Hiện `dongModalChan` chỉ báo được khi nó TÌM THẤY modal mà
  không đóng nổi; modal "shop chính chủ" nếu KHÔNG khớp `pageLocateBlockingModalButton` thì **không để lại dấu
  vết nào**. Cần: khi bước địa chỉ thất bại, gửi kèm tiêu đề + HTML rút gọn của modal trên cùng dù không tìm
  ra nút đóng. Đây là thứ để ghim selector ở đợt sau.
- Bấm tay trên app thật (chưa ai nhìn thấy nút vẽ ra).
- Chưa commit (theo yêu cầu người dùng).
