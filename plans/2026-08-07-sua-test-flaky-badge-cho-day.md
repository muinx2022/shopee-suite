# Plan: Sửa test chập chờn `BadgeChoDay_DemCaMaTraHangConTon`

- **Ngày:** 2026-08-07
- **Trạng thái:** đang làm
- **Người lập:** Opus 5 (phiên chính) · **Người thực thi:** phiên chính (việc gọn, 3 file test)

## 1. Bối cảnh & mục tiêu

`XuLyDonShopee.Tests.NotifyDonTraKhoMaTests.BadgeChoDay_DemCaMaTraHangConTon` đỏ **lác đác** trong lượt
`dotnet test orders/XuLyDonShopee.Tests` rồi xanh lại ở mọi lượt sau, kể cả trên cây mã nguồn nguyên vẹn.
Nhiều đợt trước đã ghi nhận nhưng chưa vá:

| Nơi ghi | Ghi nhận |
|---|---|
| `plans/2026-08-06-dot-a-va-loi-ra-soat.md` | 1 lượt đỏ / 8 lượt; đã nêu **giả thuyết** PushGate + accountId=1 nhưng "chưa tái hiện được" |
| `plans/2026-08-06-dot-c-hop-nhat-trung-lap.md` | 1 lượt đỏ / ~14 lượt; ghi được **thông điệp lỗi**: `Assert.False() Failure — Expected: False, Actual: null` |
| `plans/2026-08-06-dot-g-ui-wpf.md` | 1 lượt đỏ, chạy lại riêng 10/10 xanh |
| `plans/2026-08-06-dot-h3-config-nho.md` | 1 lượt đỏ / 6 lượt, không bắt được tên ca |

### Nguyên nhân — ĐÃ CHỨNG MINH bằng thí nghiệm, không còn là giả thuyết

1. `PushGate` (`orders/XuLyDonShopee.App/Services/PushGate.cs`) là `static ConcurrentDictionary` **toàn tiến
   trình**, khoá theo cặp `(accountId, kind)`. Đó là thiết kế ĐÚNG cho sản phẩm: app chỉ có MỘT `AppServices`,
   gate ngăn phiên và `HubOutboxWorker` cùng đẩy một tài khoản (nguy hiểm nhất là +2 "Đã bán").
2. Mọi test đều dựng `TempDatabase` rỗng nên `Accounts.Insert` đầu tiên luôn cấp **`accountId = 1`**.
3. xUnit chạy các **lớp** test SONG SONG (không có `xunit.runner.json`, không tắt parallel). Hai lớp cùng gọi
   `HubOutboxWorker.MotLuotAsync` trên "tài khoản 1" là `NotifyDonTraKhoMaTests` và `HubOutboxWorkerRoundTests`
   — mỗi lượt `MotLuotAsync` đều đi qua `ChayQuaGateAsync(accountId, PushKind.Gsheet)` cho lượt đẩy mã trả hàng.
4. Lớp thua cuộc nhận `TryEnter == false` → `ChayQuaGateAsync` trả `null` ("chưa biết, không tính lỗi").
5. **Chỉ riêng `BadgeChoDay` gãy vì điều đó**: test này không có đơn nào (`ton.Orders/Slips/SheetRows = 0`,
   không SKU) nên kết quả cả lượt CHÍNH LÀ kết quả lượt mã trả hàng. Mất gate ⇒ `MotLuotAsync` trả `null` ⇒
   `Assert.False(null)` đổ. Các ca của `HubOutboxWorkerRoundTests` không nhạy: kết quả lượt của chúng do nhánh
   Hub/SoldCount quyết định.

**Thí nghiệm tái hiện (đã chạy hôm nay):** thêm tạm một lớp test giữ `PushGate.TryEnter(1, PushKind.Gsheet)`
suốt 20 giây rồi chạy full suite →
`Failed XuLyDonShopee.Tests.NotifyDonTraKhoMaTests.BadgeChoDay_DemCaMaTraHangConTon` với **đúng thông điệp
đã ghi ở đợt C**: `Assert.False() Failure / Expected: False / Actual: null`. Đúng **1** ca đỏ trên 1648 —
xác nhận luôn rằng không test nào khác nhạy với gate này.

**Không phải lỗi sản phẩm.** `MotLuotAsync` trả `null` khi gate bận là hợp đồng cố ý (lượt sau đẩy bù, không
tính vào backoff). Trong app thật chỉ có một `AppServices` nên tình huống "hai lượt tranh gate của cùng một
tài khoản" chính là thứ gate sinh ra để chặn. Lỗi nằm ở **bộ test dùng chung state tĩnh với id trùng nhau**.

**Mục tiêu:** bỏ hẳn nguồn phi tất định, KHÔNG tắt/skip test, KHÔNG bỏ bớt assert.

## 2. Phạm vi

- **Làm:**
  - Thêm helper tạo tài khoản với **id chỉ định** cho test (kèm xmldoc nói rõ luật và vì sao).
  - Cho `NotifyDonTraKhoMaTests` và `HubOutboxWorkerRoundTests` mỗi lớp một `accountId` riêng — đúng nếp đã có
    trong repo (`OrderPersistPipelineTests` = 4001, `PushGateTests` = 900_10x).
- **Không làm:**
  - KHÔNG sửa `PushGate` / `HubOutboxWorker` / bất kỳ mã sản phẩm nào (không có lỗi sản phẩm ở đây).
  - KHÔNG tắt chạy song song của xUnit (che vấn đề, làm chậm bộ test).
  - KHÔNG gỡ `Assert.False(...)` khỏi test.
  - KHÔNG đụng các lớp test khác (id 1 của chúng không giao với ai qua PushGate).

## 3. Các bước thực hiện

1. `orders/XuLyDonShopee.Tests/TempDatabase.cs` — thêm hàm static
   `TempDatabase.ThemTaiKhoanIdRieng(Database db, long id, string email)`:
   Insert tài khoản qua `AccountRepository` rồi `UPDATE accounts SET Id = $moi WHERE Id = $cu` (bảng accounts
   `INTEGER PRIMARY KEY AUTOINCREMENT`, chưa có bản ghi nào tham chiếu tới nó ở thời điểm gọi), trả về `id`.
   xmldoc ghi rõ: DB tạm nào cũng cấp id 1 ⇒ lớp test nào đi qua `PushGate` phải chọn id riêng, kèm dẫn chứng
   ca hỏng.
2. `orders/XuLyDonShopee.Tests/NotifyDonTraKhoMaTests.cs` — thêm `private const long AccId = 4101;` và dùng
   helper ở 2 ca có `AppServices` + worker (`BadgeChoDay_DemCaMaTraHangConTon`,
   `ChuaCoUrlSheet_ThiKhongDemMaTra_BadgeTat`) cùng ca `DonDaBiDon_KhoMaVanChoCapMoi_DuongCuThiRong`
   (giữ nguyên hành vi, chỉ đổi id).
3. `orders/XuLyDonShopee.Tests/HubOutboxWorkerRoundTests.cs` — helper `Dung()` dùng `AccId = 4201` qua cùng hàm.
4. Xoá lớp tái hiện tạm (`ZzTamThoiGateHogTests.cs`) sau khi nghiệm thu xong — KHÔNG commit file này.

## 4. Tiêu chí nghiệm thu

- [ ] **Tái hiện có kiểm soát:** với lớp hog giữ `PushGate(1, Gsheet)` 20s, chạy full suite → **trước khi sửa**
      đỏ đúng ca `BadgeChoDay_DemCaMaTraHangConTon` (`Actual: null`); **sau khi sửa** cùng lớp hog đó → xanh
      toàn bộ. Đây là bằng chứng trực tiếp phần phi tất định đã bị gỡ.
- [ ] Thêm lớp hog thứ hai giữ `PushGate(4101, Gsheet)` → ca đó đỏ lại (chứng minh test vẫn ăn theo đúng id
      mới, không phải "xanh vì lý do khác").
- [ ] **Thử phá luật test canh:** tạm cho `HubOutboxWorker.DemTon` bỏ đếm `tonMaTra` → `BadgeChoDay` phải ĐỎ.
      Khôi phục rồi chạy lại xanh.
- [ ] `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj` xanh **10 lượt liên tiếp**
      (1647 test, không có ca nào đỏ).
- [ ] `dotnet build ShopeeSuite.sln` — **0 warning, 0 error**.
- [ ] `git status`: chỉ 3 file test trong diff, không còn file tạm.

## 5. Rủi ro & lưu ý

- Đổi `Id` của tài khoản phải làm **ngay sau Insert**, trước khi ghi đơn/mã trả hàng — không có khoá ngoại
  nhưng các bảng khác lưu `account_id` dạng số rời, đổi sau sẽ mồ côi dữ liệu.
- `PushGateTests` (id 900_10x) và `OrderPersistPipelineTests` (id 4001) đã có id riêng — đừng chọn trùng.
- Sau khi sửa, id 1 vẫn còn "trống": một lớp test mới dựng `TempDatabase` + `MotLuotAsync` sẽ lấy id 1 và
  KHÔNG đụng ai. Muốn đua lại phải có HAI lớp mới cùng quên — an toàn hơn cách gom `[Collection]`.

---

## Báo cáo thực thi

<điền sau khi xong>
