# Plan: Sửa test chập chờn `BadgeChoDay_DemCaMaTraHangConTon`

- **Ngày:** 2026-08-07
- **Trạng thái:** hoàn thành (2026-08-07 — 2 vòng `nghiem-thu` đều ĐẠT; vòng 1 tìm thêm 1 bom hẹn giờ đã sửa luôn)
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
5. **Chỉ riêng `BadgeChoDay` gãy vì CUỘC ĐUA NÀY**: test này không có đơn nào (`ton.Orders/Slips/SheetRows = 0`,
   không SKU) nên kết quả cả lượt CHÍNH LÀ kết quả lượt mã trả hàng. Mất gate ⇒ `MotLuotAsync` trả `null` ⇒
   `Assert.False(null)` đổ. Các ca của `HubOutboxWorkerRoundTests` không nhạy với gate **`Gsheet`** (kết quả lượt
   của chúng do nhánh Hub/SoldCount quyết định) — nên thực tế chỉ có một bên thua là hỏng.
   > Đính chính sau vòng phản biện 2: **không** được đọc thành "lớp kia miễn nhiễm". Agent đo thật: giữ **cả 4
   > loại** gate trên id của lớp đó → **5/7 ca đỏ**. Chúng chỉ miễn nhiễm với riêng loại `Gsheet` — tức id riêng
   > cho lớp đó là BẮT BUỘC, không phải "cho chắc".

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

- [x] **Tái hiện có kiểm soát:** với lớp hog giữ `PushGate(1, Gsheet)` 20s, chạy full suite → **trước khi sửa**
      đỏ đúng ca `BadgeChoDay_DemCaMaTraHangConTon` (`Actual: null`); **sau khi sửa** cùng lớp hog đó → xanh
      toàn bộ. Đây là bằng chứng trực tiếp phần phi tất định đã bị gỡ.
- [x] Thêm lớp hog thứ hai giữ `PushGate(4101, Gsheet)` → ca đó đỏ lại (chứng minh test vẫn ăn theo đúng id
      mới, không phải "xanh vì lý do khác").
- [x] **Thử phá luật test canh:** tạm cho `HubOutboxWorker.DemTon` bỏ đếm `tonMaTra` → `BadgeChoDay` phải ĐỎ.
      Khôi phục rồi chạy lại xanh.
- [x] `dotnet test orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj` xanh **10 lượt liên tiếp**
      (1647 test, không có ca nào đỏ).
- [x] `dotnet build ShopeeSuite.sln` — **0 warning, 0 error**.
- [x] `git status`: chỉ 3 file test trong diff, không còn file tạm.

## 5. Rủi ro & lưu ý

- Đổi `Id` của tài khoản phải làm **ngay sau Insert**, trước khi ghi đơn/mã trả hàng — không có khoá ngoại
  nhưng các bảng khác lưu `account_id` dạng số rời, đổi sau sẽ mồ côi dữ liệu.
- `PushGateTests` (id 900_10x) và `OrderPersistPipelineTests` (id 4001) đã có id riêng — đừng chọn trùng.
- Sau khi sửa, id 1 vẫn còn "trống": một lớp test mới dựng `TempDatabase` + `MotLuotAsync` sẽ lấy id 1 và
  KHÔNG đụng ai. Muốn đua lại phải có HAI lớp mới cùng quên — an toàn hơn cách gom `[Collection]`.

---

## Báo cáo thực thi (phiên chính, 2026-08-07)

### Đã sửa — 3 file, KHÔNG đụng mã sản phẩm

| File | Thay đổi |
|---|---|
| `orders/XuLyDonShopee.Tests/TempDatabase.cs` | Thêm `static ThemTaiKhoanIdRieng(Database db, long id, string email = "shop-test@example.com")`: Insert qua `AccountRepository` rồi `UPDATE accounts SET Id = …`; đổi không đúng 1 dòng thì **ném** (im lặng = test chạy trên id khác id nó tưởng ⇒ đua lại như cũ). xmldoc ghi luật + bảng id đang dùng |
| `orders/XuLyDonShopee.Tests/NotifyDonTraKhoMaTests.cs` | `const long AccId = 4101`; 3 ca dựng `AppServices` đổi sang helper; bỏ `using XuLyDonShopee.Core.Models` không còn dùng |
| `orders/XuLyDonShopee.Tests/HubOutboxWorkerRoundTests.cs` | `const long AccId = 4201`; helper `Dung()` đổi sang helper chung |

### Kiểm chứng (lệnh thật, kết quả thật)

| # | Việc | Kết quả |
|---|---|---|
| 1 | **Tái hiện TRƯỚC khi sửa** — lớp tạm giữ `PushGate(1, Gsheet)` 20s + full suite | `Failed: 1, Passed: 1647` — đúng ca `BadgeChoDay_DemCaMaTraHangConTon`, đúng thông điệp đã ghi ở đợt C: `Assert.False() Failure / Expected: False / Actual: null`. Chỉ 1 ca đỏ ⇒ không test nào khác nhạy với gate này |
| 2 | **SAU khi sửa**, giữ nguyên lớp hog `PushGate(1, Gsheet)` | `Passed! Failed: 0, Passed: 1648` — hết phụ thuộc |
| 3 | Đổi hog sang giữ `PushGate(4101, Gsheet)` | Đỏ lại đúng ca đó, đúng `Actual: null` ⇒ test THẬT SỰ chạy trên id mới, không phải "xanh vì lý do khác" |
| 4 | **Thử phá luật test canh**: tạm cho `DemTon` bỏ đếm `tonMaTra` | `BadgeChoDay` ĐỎ với `Assert.Equal() Expected: 1, Actual: 0` ⇒ test vẫn canh đúng luật badge. Đã khôi phục nguyên trạng (`git diff` chỉ còn 3 file test) |
| 5 | `dotnet test orders/XuLyDonShopee.Tests` × **10 lượt** | 10/10 `Passed! Failed: 0, Passed: 1647` |
| 6 | `dotnet build ShopeeSuite.sln` | **0 Warning(s), 0 Error(s)** |

Mốc đối chứng trước khi sửa: 9 lượt full-suite liên tiếp trên cây nguyên vẹn đều xanh (đúng bản chất
"~1 đỏ / 10–14 lượt" — brute force không phải cách tái hiện, nên đã dựng lớp hog để ép đua).

Lớp tạm `ZzTamThoiGateHogTests.cs` đã **xoá**, không commit.

### Không làm (đúng phạm vi)

- Không sửa `PushGate` / `HubOutboxWorker` — hợp đồng `MotLuotAsync` trả `null` khi gate bận là cố ý.
- Không tắt chạy song song của xUnit, không gỡ assert nào, không skip test nào.

---

## Vòng phản biện 1 (`nghiem-thu`, 2026-08-07) — chấm ĐẠT, nhưng tìm thêm 1 lỗi thật

Agent tự dựng lại thí nghiệm bằng `[ModuleInitializer]` (chắc chắn phủ trọn lượt chạy hơn `[Fact]` + `Sleep`),
chạy 4 chiều: trước-sửa/sau-sửa × gate id 1 / 4101, thêm ca giữ **cả 4 `PushKind`** trên id 1 và id 4001 →
số liệu khớp báo cáo, và xác nhận **không lớp test nào khác** đi qua `PushGate` với id 1
(`OrdersAccountLeaseTests` có `new AccountSession(1, …)` nhưng không bao giờ gọi `PersistSyncedOrdersAsync`).

### ĐÃ SỬA THEO PHẢN BIỆN — mở rộng phạm vi so với plan gốc (ghi rõ vì đây là đổi hướng giữa chừng)

1. **[Trung bình] BOM HẸN GIỜ — nguồn phi tất định THỨ HAI, cùng một ca test, CÙNG thông điệp lỗi.**
   `Luc` đóng cứng `2026-07-30`, trong khi `PushReturnCodesToGsheetAsync` gọi
   `ReturnCodes.DonDep(UtcNow - SoNgayGiuMac)` ngay dòng ĐẦU (trước cả cửa kiểm URL) ⇒ từ **2026-10-28** bản
   ghi bị xoá ngay trước khi đếm.
   **Tự kiểm chứng bằng cách mô phỏng tương lai** (`Luc = UtcNow.AddDays(-91)`), kết quả THẬT:
   - `BadgeChoDay_DemCaMaTraHangConTon` → `Assert.False() Failure — Actual: null` (**y hệt** lỗi đua vừa vá)
   - `ChuaCoUrlSheet_ThiKhongDemMaTra_BadgeTat` → `Assert.Single() Failure: The collection was empty`
   ⇒ Đã sửa `Luc` thành mốc TƯƠNG ĐỐI `DateTime.UtcNow.AddDays(-1)` + xmldoc ghi rõ vì sao không được đóng cứng.
   Sửa luôn trong đợt này (không tách plan) vì: cùng ca test, cùng thông điệp lỗi, và để lại thì tháng 10 sẽ
   tốn nguyên một đợt điều tra nữa cho đúng cái vừa điều tra xong.
   > **Đính chính một phần của báo cáo phản biện:** agent nói `MaTraHangDocLapTests` "cùng bệnh" ở 5 chỗ gọi
   > `PushReturnCodesToGsheetAsync` (dòng 224/235/282/298/313). **Không đúng** — 5 ca đó gieo dữ liệu bằng
   > `DateTime.UtcNow` (dòng 222/252/280/296/311), không dùng `Luc`; `Luc` của lớp đó chỉ xuất hiện trong các ca
   > repository thuần, và ca `DonDep` ở dòng 177 dùng mốc `Luc.AddDays(-90)` (tương đối với chính `Luc`).
   > Đã đọc lại từng dòng trước khi kết luận nên KHÔNG sửa lớp đó.
2. **[Thấp] "Sổ ghi tay" dễ tái phát khi chép-dán lớp test.** Thêm CHỐT trong `ThemTaiKhoanIdRieng`:
   ghi `id → file test đang giữ` (`[CallerFilePath]`), hai FILE khác nhau cùng đòi một id thì **ném ngay**.
   Thử phá: tạm cho `HubOutboxWorkerRoundTests.AccId = 4101` → 3 ca đỏ ngay lượt đầu với
   `InvalidOperationException: Id tài khoản 4101 đã thuộc về HubOutboxWorkerRoundTests.cs; NotifyDonTraKhoMaTests.cs
   phải chọn id KHÁC` — lỗi chép-dán thành lỗi TẤT ĐỊNH, không còn thành flaky. Đã khôi phục 4201.
   Thêm cảnh báo "chép lớp này thì PHẢI đổi số" ngay tại xmldoc của hằng `AccId` ở cả 2 lớp.
3. **[Thấp] Sửa mô tả sai trong plan:** `OrderPersistPipelineTests` (4001) **không** tạo bản ghi tài khoản, chỉ
   truyền số vào constructor — helper mới phải Insert + đổi Id vì `MotLuotAsync` duyệt `Accounts.GetAll()`.
   Câu "đúng nếp đã có" ở mục 2 chỉ đúng ở phần *chọn id riêng*, không đúng ở phần *cách tạo tài khoản*.
4. **[Ghi chú] Sửa thông điệp guard** `!= 1`: bỏ phần "(id đã tồn tại?)" vì ca đó SQLite ném PRIMARY KEY ngay
   trong `ExecuteNonQuery`; nay ghi đúng nguyên nhân "UPDATE khớp 0 dòng".

### Kiểm chứng lại sau các sửa đổi trên

| # | Việc | Kết quả |
|---|---|---|
| 7 | `dotnet build ShopeeSuite.sln` | **0 Warning(s), 0 Error(s)** |
| 8 | `dotnet test orders/XuLyDonShopee.Tests` × **6 lượt** | 6/6 `Passed! Failed: 0, Passed: 1647` |

Tổng cộng bộ test orders đã chạy **16 lượt** sau khi sửa, 16/16 xanh.

---

## Vòng phản biện 2 (`nghiem-thu`, 2026-08-07) — ĐẠT

Agent tự chạy lại toàn bộ: build 0/0, 6 lượt test 1647 pass, tự mô phỏng lại tương lai (`Luc = -91 ngày` → đúng
2 ca đỏ, khớp từng chữ), tự dò chốt chống chép-dán bằng 4 ca thăm dò. **Tự đính chính vòng 1**: xác nhận
`MaTraHangDocLapTests` KHÔNG dính bom hẹn giờ (5 ca đó gieo bằng `UtcNow`), quyết định không sửa lớp đó là đúng.

### ĐÃ SỬA THEO VÒNG 2

Agent chứng minh bằng thăm dò rằng chốt bản đầu (khoá theo `[CallerFilePath]`) có **2 lỗ thật**: (a) mù ngay khi
ai đó gom các hàm `Dung()` vào một file helper dùng chung — đúng kiểu dọn dẹp người sau thấy là *cải thiện*;
(b) hai lớp test nằm CÙNG một file thì lọt, trong khi đơn vị chạy song song của xUnit là **LỚP**. Cộng thêm
(c) chốt không biết `4001` (lớp `OrderPersistPipelineTests` chiếm gate mà không đi qua helper).

- **Đổi chốt sang khoá theo TYPE**: `ThemTaiKhoanIdRieng<TLopTest>(...)`, sổ ghi `id → typeof(TLopTest).FullName`.
  Đóng cả (a) và (b): di chuyển chỗ gọi đi đâu cũng không tắt được chốt.
- **Gieo sẵn `4001`** vào sổ với ghi chú "không qua helper" → đóng (c).
  Thử phá: tạm cho `NotifyDonTraKhoMaTests.AccId = 4001` → 3 ca đỏ ngay với
  `InvalidOperationException: Id tài khoản 4001 đã thuộc về XuLyDonShopee.Tests.OrderPersistPipelineTests (không
  qua helper); XuLyDonShopee.Tests.NotifyDonTraKhoMaTests phải chọn id KHÁC`. Đã khôi phục 4101.
- **`Luc` → `DateTime.UtcNow`** (bỏ `AddDays(-1)`): cắt hẳn ghép ngầm với giả định "cửa sổ giữ ≥ 1 ngày",
  và trùng nếp `MaTraHangDocLapTests` đang dùng.
- Sửa câu sai ở mục 1 điểm 5 của plan (xem ghi chú "Đính chính sau vòng phản biện 2" ở trên).

### Kiểm chứng cuối

| # | Việc | Kết quả |
|---|---|---|
| 9 | `dotnet build ShopeeSuite.sln` | **0 Warning(s), 0 Error(s)** |
| 10 | `dotnet test orders/XuLyDonShopee.Tests` × **5 lượt** | 5/5 `Passed! Failed: 0, Passed: 1647` |

**Tổng: 21 lượt full-suite sau khi sửa, 21/21 xanh** (mốc đối chứng trước khi sửa: 9 lượt xanh — flake này
vốn ~1 đỏ / 10–14 lượt nên brute force không bao giờ là bằng chứng; bằng chứng nằm ở thí nghiệm giữ gate).

### Còn để ngỏ (KHÔNG chặn, ghi để khỏi quên)

- `HubOutboxWorkerRoundTests.DemDaBan_DonVUA_GHI_ChuaNguoi_WorkerKhongDem` phụ thuộc "chưa quá 60 giây"
  (`OnDinhTruocKhiDemBu`) giữa `UpsertMany(…, UtcNow)` và `MotLuotAsync`. Có sẵn từ trước, biên 60 giây so với
  vài mili-giây thực tế nên an toàn — chỉ nêu cho đủ.
- `AppServices` không được `Dispose` trong các lớp test ⇒ timer flush của `ActivityLog` sống tới hết tiến trình,
  ghi vào `%TEMP%\logs` dùng chung. KHÔNG phải nguồn lỗi (mọi I/O bị nuốt, `Append` không thể ném) — chỉ là nhiễu.
