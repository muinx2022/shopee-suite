# Plan: Shop còn cảnh báo lỗi địa chỉ ⇒ vòng chạy TỰ thử lại bước địa chỉ, hết lỗi thì tự gỡ banner

- **Ngày:** 2026-08-10
- **Trạng thái:** đang làm
- **Người lập:** Opus 5 (phiên chính)

## 1. Bối cảnh — lỗ hổng thật, có bằng chứng từ nhật ký

Ảnh màn hình 10/08 (v1.8.6, tài khoản `hoangdh200392:muinx`): 4 banner "Cảnh báo: Lỗi địa chỉ"
(`hanily.store`, `minoa.store`, `rudi.store`, `piko.store1`) nằm lại trên tab Shops. Nhật ký cùng vòng cho thấy
`piko.store1` ĐÃ được check trọn vẹn lúc 09:25:

```
09:25:11 [Shop 4/12] piko.store1 — mở Chi tiết...
09:25:15 [Shop 4] Chờ Lấy Hàng: 0.
09:25:25 Đọc được 54 đơn (Tất cả).
09:25:59 Check đơn trả hàng [piko.store1]: 5 yêu cầu ...
```

Không có dòng nào về địa chỉ. Vì `ShopFlowRunner.ThanShopAsync` chỉ chạy bước đặt địa chỉ khi
`toShip > 0 && có thư mục phiếu` (dòng 329). Shop **0 đơn Chờ Lấy Hàng thì bước địa chỉ KHÔNG chạy**, mà
`PickupOkShop` — tín hiệu DUY NHẤT để `GoBannerLoiDiaChi` gỡ banner — chỉ được đặt khi bước đó chạy và trả ok.

⇒ **Banner của shop ít đơn không bao giờ có đường tự hết.** Nó chỉ hết khi người dùng tự bấm ✕ hoặc bấm nút
"Check" trên banner. Đây đúng là cái người dùng vừa yêu cầu: *"nếu thấy có lỗi phần địa chỉ, cần phải thử lại
phần địa chỉ và fix lỗi nếu modal mở ra, sau đó nếu đã fix rồi thì gỡ banner và thông báo hub, gỡ client khác"*.

Phần "fix lỗi nếu modal mở ra" **đã có sẵn** từ v1.8.4/v1.8.6 và không phải làm lại: bên trong extension, bước
`setPickupAddress` tự gọi `dongModalChan` (dọn modal TOS + tour `.on-boarding`) rồi thử lại một lượt. Phần "gỡ
banner + báo Hub + gỡ ở client khác" cũng đã có sẵn nguyên đường:
`PickupOkShop` → `OrdersBridgeRunResult.PickupOkShops` → `OrderPersistPipeline.GoBannerLoiDiaChi` (dismiss local
+ `RaiseAddressAlertsChanged` + đẩy Hub theo rev ⇒ máy khác tự gỡ).

**Việc duy nhất còn thiếu: cho bước địa chỉ CHẠY với shop đang có banner, dù vòng đó 0 đơn Chờ Lấy Hàng.**

## 2. Phạm vi

### Làm

1. `ShopFlowRunner` biết được "shop này đang có cảnh báo lỗi địa chỉ" qua **callback do App rót**
   (`dangCoCanhBaoDiaChi`) — Core không ref DB/App, giống hệt khuôn `returnCountLast` / `layDonThieuPhieu`.
2. **Hàm THUẦN** `QuyetDinhBuocDiaChi(toShip, coThuMucPhieu, dangCoCanhBao)` → 3 nhánh: `Bo` /
   `DatRoiXuDon` (như cũ) / `ThuLaiChoCanhBao` (MỚI). Luật nằm ở hàm thuần để test ma trận không cần trình duyệt.
3. Nhánh `ThuLaiChoCanhBao`: chạy **đúng** `DatDiaChiAsync` (cùng lệnh với vòng thường và với nút Check — không
   chép luật thứ ba), rồi:
   - ok ⇒ đặt `PickupOkShop` + **trả địa chỉ về địa chỉ khác** (`setPickupAddressToOther`) y như cuối flow thường.
   - không ok ⇒ **KHÔNG** đặt `PickupFailedShop` (xem mục 5), chỉ ghi nhật ký; banner ở lại.
   - captcha ⇒ không kết luận, thoát shop như nhánh cũ.
4. Tách `TraDiaChiVeKhacAsync` khỏi đoạn inline cuối Phần B để hai đường dùng chung.
5. Rót callback ở `OrdersBridgeSession` → `AccountSession` (đọc `_services.PickupAlerts.ListActive`).

### Không làm

- KHÔNG đổi nhánh có đơn (`toShip > 0`) — hành vi in phiếu giữ nguyên tuyệt đối.
- KHÔNG đẻ đường gỡ banner thứ hai. Dùng lại `GoBannerLoiDiaChi` đang có.
- KHÔNG đổi nút "Check" thủ công.
- KHÔNG commit / phát hành (chờ người dùng bảo).

## 3. Tiêu chí nghiệm thu

- [ ] Ma trận `QuyetDinhBuocDiaChi`: (3,có,không)→DatRoiXuDon · (0,có,CÓ)→ThuLaiChoCanhBao · (0,có,không)→Bo ·
      (3,KHÔNG có thư mục,CÓ)→ThuLaiChoCanhBao · (3,KHÔNG,không)→Bo.
- [ ] Shop 0 đơn + đang có banner + extension trả `pickupDone ok:true` ⇒ có gửi `setPickupAddress`, có gửi
      `setPickupAddressToOther`, `PickupOkShop` = nhãn shop, **không** gửi `prepareNextOrder`.
- [ ] Shop 0 đơn + đang có banner + `ok:false` ⇒ `PickupOkShop` null **và** `PickupFailedShop` null (banner ở
      lại, không báo động lần hai, không đẩy Hub vô ích mỗi vòng).
- [ ] Shop 0 đơn + KHÔNG có banner ⇒ tuyệt đối không gửi `setPickupAddress` (giữ nguyên test cũ
      `ShopKhongCoDonChoLayHang_KhongChayBuocDiaChi_PickupOkShop_Null`).
- [ ] Callback ném lỗi (DB hỏng) ⇒ coi như KHÔNG có banner, shop vẫn chạy bình thường.
- [ ] Build 0 lỗi / 0 warning; test orders không tụt; mỗi test mới đều THỬ PHÁ được (sửa ngược code → đỏ).
- [ ] Chạy thật 1 vòng đủ 12 shop, nhật ký cho thấy shop có banner được thử lại địa chỉ.

## 4. Rủi ro

- **Đặt địa chỉ cho shop không có đơn là thao tác GHI thật trên Seller Centre.** Vì vậy phải trả địa chỉ về
  "địa chỉ khác" ngay sau đó — kết thúc y hệt trạng thái của một shop chạy trọn vòng bình thường. Không làm bước
  trả về thì 4 shop kia bị treo tag "lấy hàng" ở địa chỉ tỉnh, lệch với mọi shop khỏe.
- **Thêm 2 chặng cầu nối cho mỗi shop có banner** (~vài chục giây/shop). Chỉ áp cho shop ĐANG có banner nên
  không đội thời gian vòng của shop khỏe.
- Callback đọc SQLite trên thread nền của phiên — phải bọc try/catch, hỏng thì coi như không có banner (thà bỏ
  một lượt thử lại còn hơn làm chết cả shop).

## 5. Vì sao FAIL không đặt `PickupFailedShop`

`PickupFailedShop` hiện mang ba hệ quả ở vòng ngoài: bỏ qua shop (không in phiếu), `shopsDone` không tăng, và
bắn `StartCanhBaoDiaChiInBackground` + `GhiBannerLoiDiaChi` (ghi lại banner với `cho_day=1` ⇒ đẩy Hub lại).
Ở nhánh mới thì **không có gì để in** (0 đơn) và **banner đã có sẵn** — đặt cờ chỉ tạo ra: một shop khỏe bị
đếm là hỏng, một tin Slack lặp, và một lượt đẩy Hub vô ích mỗi vòng. Nên: FAIL = im lặng giữ nguyên hiện trạng,
chỉ ghi nhật ký.

---

## Báo cáo thực thi (2026-08-10)

### Đã làm

**Core — `ShopFlowRunner`.** Enum `BuocDiaChi` + hàm THUẦN `QuyetDinhBuocDiaChi(toShip, coThuMucPhieu,
dangCoCanhBao)`. `ThanShopAsync` đổi `if (toShip > 0 && có thư mục)` thành rẽ theo hàm thuần đó, thêm nhánh
`ThuLaiChoCanhBao`. Tách `TraDiaChiVeKhacAsync` khỏi đoạn inline cuối Phần B — nhánh mới và nhánh cũ gọi CÙNG
một hàm. Callback `dangCoCanhBaoDiaChi` bọc trong `DangCoCanhBaoDiaChi()`: nhãn shop rỗng → không hỏi; callback
ném → ghi log rồi coi như không có banner.

**Core — `OrdersBridgeSession`.** Thêm tham số ctor `dangCoCanhBaoDiaChi` (mặc định null ⇒ đường "Chạy thử" và
nút "Check" thủ công giữ nguyên hành vi cũ), rót thẳng xuống `ShopFlowRunner`.

**App — `AccountSession`.** Rót callback: `_services.PickupAlerts.ListActive(_accountId)` so nhãn shop
**OrdinalIgnoreCase** (khóa SQL so BINARY — đúng cạm bẫy đã ghi ở `GoBannerLoiDiaChi`).

Đường gỡ banner **không viết mới một dòng nào**: `PickupOkShop` → `PickupOkShops` → `GoBannerLoiDiaChi` (dismiss
local + `RaiseAddressAlertsChanged` + đẩy Hub theo rev ⇒ máy khác tự gỡ) đã có sẵn từ v1.8.4.

### Kiểm chứng

`dotnet build ShopeeSuite.sln` **0 lỗi / 0 warning**. Test: orders **1708/1708** (trước 1696, +12), suite core
**114/114**, hub **120/120**.

File test mới `orders/XuLyDonShopee.Tests/ThuLaiDiaChiChoBannerTests.cs`: ma trận 6 ca cho hàm thuần + 6 ca chạy
thật qua cầu nối giả.

**THỬ PHÁ (3 lượt, đều in bằng chứng file ĐÃ đổi trước khi chạy, đều đỏ đúng chỗ rồi khôi phục):**

| Phá | Test đỏ |
|---|---|
| Bỏ nhánh banner trong `QuyetDinhBuocDiaChi` (luôn trả `Bo`) | 4 đỏ: 2 ca ma trận + `ShopKhongDon_CoBanner_DatDuocDiaChi_...` + `ShopKhongDon_CoBanner_VanLoi_...` |
| Bỏ `TraDiaChiVeKhacAsync` ở nhánh đặt được | 1 đỏ: `ShopKhongDon_CoBanner_DatDuocDiaChi_VaTraDiaChiVeKhac` |
| Đặt `PickupFailedShop` ở nhánh thử lại thất bại | 1 đỏ: `ShopKhongDon_CoBanner_VanLoi_GiuBanner_KhongDatCoLoi` |

### Chưa xong

- **Nghiệm thu bằng vòng chạy THẬT** — đã mở app (PID 19188), bấm `Chọn tất cả` + `Chạy đã chọn` lúc 09:44:45,
  đọc được 12 shop; một subagent đang theo dõi trọn vòng. Chưa có kết quả.
- Chưa commit, chưa bump phiên bản (chờ người dùng bảo).
- **Bất đối xứng còn để lại:** nút "Check" thủ công (`KiemTraLaiDiaChiAsync`) vẫn KHÔNG trả địa chỉ về địa chỉ
  khác sau khi đặt được, còn nhánh tự động thì có. Cố ý không sửa nút trong đợt này (ngoài phạm vi yêu cầu) —
  nhưng đây là câu cần hỏi người dùng: bấm Check xong có muốn trả địa chỉ về chỗ khác không.
