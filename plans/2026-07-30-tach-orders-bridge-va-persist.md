# Plan: Tách OrdersBridgeSession + OrderPersistPipeline khỏi AccountSession + bổ sung test bridge (đợt 4 — orders A)

- **Ngày:** 2026-07-30
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

Hai file lớn nhất còn lại phía orders sau các đợt dọn/sửa:
- `orders/XuLyDonShopee.Core/Services/OrdersBridgeSession.cs` (~1.500 dòng) — vòng đời phiên cầu nối extension (cổng WS, PocCleanLauncher, login subaccount, flow từng shop: sync → arrange → check trả hàng, StageWaiter). **0 test** — vùng rủi ro nhất repo.
- `orders/XuLyDonShopee.App/Services/AccountSession.cs` (~1.250 dòng) — lifecycle account + persist đơn + GSheet + hub push + notify + NenXoaDonKetThuc.

Mục tiêu (refactor thuần, KHÔNG đổi hành vi):
1. Tách `AccountSession` phần "persist + hậu xử lý" thành **`OrderPersistPipeline`** (class thuần DTO/DB/HTTP, KHÔNG dính UI/browser — TEST ĐƯỢC): nhận kết quả sync/arrange/mã trả từ bridge → ghi OrdersRepository/ReturnCodesRepository → GSheet outbox → hub push → notify (giữ nguyên luật lọc đơn-đã-dọn vừa làm ở B1) → NenXoaDonKetThuc. Kèm tách **`SlipFiles`** (static helper file phiếu). `AccountSession` còn lại lifecycle + vòng bridge (~600-700 dòng).
2. `OrdersBridgeSession`: tách theo trục trách nhiệm, session giữ làm facade mỏng: đề xuất `OrdersBridgeLauncher` (cổng + PocCleanLauncher + login subaccount + teardown), `ShopFlowRunner` (vòng từng shop: sync/arrange/trả hàng — phần gọi `QuyetDinhLuotTraHang` giữ nguyên), giữ `StageWaiter` hiện có. Cấu trúc cụ thể được phép điều chỉnh theo thực tế code, ghi rõ trong báo cáo; mục tiêu: không file nào > ~800 dòng.
3. **Test cho bridge** (quan trọng ngang phần tách): dùng `Shopee.Toolkit.Ws.WebSocketServer` thật trên cổng loopback ngẫu nhiên + client WS giả làm "extension": bắn kịch bản — captcha fan-out, error fan-out, timeout từng chặng (StageWaiter chỉ fault đúng chặng đang chờ — hành vi fix 1B.3), callback persist gọi 2 lần không ghi trùng, `redownloadSlip` roundtrip, đọc-trả-hàng với `tabTraHang=false` → bỏ lượt. Đặt trong `orders/XuLyDonShopee.Tests`.

## 2. Phạm vi

- **Làm:** 3 việc trên; chỉ đụng `orders/**`.
- **Không làm:** không đổi hành vi/thông điệp log nghiệp vụ; không đụng `extensions/**`, `suite/**`, `server/**`, `shared/**`; không đổi hợp đồng message bridge; KHÔNG commit.

## 3. Các bước thực hiện

1. Đọc kỹ 2 file + test hiện có (`TraHangBoLuotSaiTabTests`, `NotifyDonTraKhoMaTests`, `HubPushGenRaceTests`…) để nắm hợp đồng.
2. Tách `OrderPersistPipeline` + `SlipFiles` (bước 1 mục tiêu) — di chuyển nguyên khối, đổi tối thiểu chữ ký; DI qua constructor (repo, outbox, notify callback) để test mock được.
3. Tách `OrdersBridgeSession` (bước 2 mục tiêu) — mỗi lần tách 1 khối: build + test ngay.
4. Viết bộ test bridge (bước 3 mục tiêu) — tối thiểu 8-10 ca như liệt kê.
5. Build + test toàn bộ; grep xác nhận không còn method mồ côi sau tách.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` 0 lỗi 0 warning; `dotnet test orders/XuLyDonShopee.Tests` ≥ 1440 pass + số test mới (ghi rõ).
- [ ] `AccountSession.cs` ≤ ~750 dòng; `OrdersBridgeSession.cs` ≤ ~800; không file mới > ~800.
- [ ] `OrderPersistPipeline` có test riêng (persist 2 lần không trùng, luật notify B1, NenXoaDonKetThuc chỉ xoá khi DaDayHub).
- [ ] Diff không đổi hành vi: các chuỗi log nghiệp vụ + thứ tự thao tác giữ nguyên (báo cáo liệt kê những gì di chuyển đi đâu).

## 5. Rủi ro & lưu ý

- Đây là code vừa sửa bug đợt B — GIỮ NGUYÊN các fix (QuyetDinhLuotTraHang, hub_push_gen, luật lọc notify). Mọi di chuyển phải giữ thứ tự gọi.
- Test WS dùng cổng ngẫu nhiên (bind port 0 hoặc dò cổng trống) để không đụng 47821.
- KHÔNG commit; điền "Báo cáo thực thi" + báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Kết quả:** `dotnet build ShopeeSuite.sln` 0 lỗi 0 cảnh báo · `dotnet test orders/XuLyDonShopee.Tests` **1459 pass** (1440 cũ + **19 test mới**). KHÔNG commit.

### 1. Tách `AccountSession` (App)

| File | Việc |
|---|---|
| `orders/XuLyDonShopee.App/Services/OrderPersistPipeline.cs` (MỚI, 644 dòng) | Nhận `internal sealed class` — di chuyển NGUYÊN KHỐI: `PersistOrdersResult`, `PersistSyncedOrdersAsync`, `Start{Gsheet,Hub,HubSlip,SoldCount,Notify,NotifyDonTra,CanhBaoDiaChi}InBackground`, `PushPendingToHubAsync`, `HubPushBatchSize`, `HubSlipPushBatchSize`, `NenBaoThieuGsheetUrl`, `CoNenGuiCanhBao`/`NguongCanhBaoDiaChi`/`_mocCanhBaoDiaChi`, `CoNenGuiNotifyLocal`, `KindDonTra`, `MoTaCapDonTra`, `LocCapDonDaDon`, `NenXoaDonKetThuc`. THÊM: shop-context (`SetShopContext`/`CurrentShopLogin`, thay 2 field `_currentShopId/_currentShopLogin` của phiên) và `LuuMaTraHang(cap, log, ct)` = nguyên thân lambda `saveReturnCodes` bê từ `RunBridgeContinuousAsync`. |
| `orders/XuLyDonShopee.App/Services/SlipFiles.cs` (MỚI, 99 dòng) | `internal static` — `MaxSlipBytes`, `TryReadSlipBase64`, `BytesLookPdf`, `SlipFileIsValidPdf`, `ThieuPhieu`. |
| `orders/XuLyDonShopee.App/Services/AccountSession.cs` | 1292 → **620 dòng**. Còn lifecycle (Start/Stop/MarkQueued/State), khóa chạy xuyên máy, `RunBridgeContinuousAsync`, `RedownloadSlipAsync`, `SetStatus/SetError`. Thêm field `_persist`; 3 chỗ gọi qua pipeline. |
| `HubOutbox.cs`, `OrderRowViewModel.cs`, `.csproj` (comment) | Đổi tiền tố `AccountSession.` → `OrderPersistPipeline.` / `SlipFiles.` (không đổi lời gọi). |

### 2. Tách `OrdersBridgeSession` (Core) — 1516 → 5 file, file lớn nhất 569 dòng

| File | Nội dung |
|---|---|
| `OrdersBridgeSession.cs` (**457**) | Facade: 3 record kết quả, ctor (chữ ký PUBLIC KHÔNG đổi), `StartBridgeAndLaunch`, `LoginAndReachPickerAsync`, `RunAllShopsAsync`, `RunSliceCoreAsync`, `RunLoginThenSliceAsync`, `RedownloadSlipAsync` (uỷ quyền), `Fail`, `Dispose`. |
| `OrdersBridgeChannel.cs` (MỚI, **317**) | `StageWaiter` (thành lớp top-level internal, cơ chế GIỮ NGUYÊN) + kênh WS: 13 TCS chặng, `Start(port)`, `SendAsync`, `AwaitAsync`, `ResetStages`, `CaptchaSeen`, `OnMessage` (captcha fan-out + error chỉ-fault-chặng-đang-chờ bê nguyên), `PrepareResult`. **Cổng tham số hoá** (mặc định 47821) để test bind cổng trống. |
| `OrdersBridgeLauncher.cs` (MỚI, **129**) | `Launch()` + `PrepareFreshExtensionCopy` / `KillBrowsersOnProfile` / `ClearProfileSessionAndLocks` (giữ nguyên thứ tự chép ext → kill → xoá khoá → mở). |
| `ShopFlowRunner.cs` (MỚI, **569**) | Enum `SauDatDiaChi`/`SauDocTraHang`/`LuotDocTraHang`, `RunShopOrdersAsync`, `CheckDonTraHangAsync`, `DongTabShopAsync`, `RedownloadSlipAsync`, `QuyetDinhSauDatDiaChi`, `QuyetDinhLuotTraHang`, `TrySaveSlip`, cờ `PickupFailedShop`. |
| `UocTinhDon.cs` (MỚI, **207**) | `MaxBuUocTinh`, `SoNgayBuUocTinh`, `NgayDonTuMa`, `ChonDonLayUocTinh`, `MergeFinalAmounts`, `LyDoHutUocTinh`. |

Caller đổi tiền tố: `TraHangParser` (1 lời gọi `NgayDonTuMa` + 3 doc), `OrderNotifyService` (doc), 6 file test.

**Đối chiếu máy móc "không đổi hành vi"**: so tập dòng code (bỏ doc/blank, sort) giữa bản cũ và các file mới — 100% dòng nghiệp vụ còn nguyên; chênh lệch DUY NHẤT là các dòng bị viết lại một cách máy móc (`_ws!.SendAsync`→`_ch.SendAsync`, `_waiter.AwaitAsync(_xTcs,…)`→`_ch.AwaitAsync(xTcs,…)`, `_captchaSeen`→`CaptchaSeen`, `_pickupFailedShop`→`PickupFailedShop`, `ResetTcs`→`ResetStages`, đổi visibility). Bộ timeout từng chặng khớp 1-1 (30/45/60/90/120/180/300 + công thức finals). Chuỗi log nghiệp vụ giữ nguyên từng ký tự.

### 3. Test mới (19)

- `BridgeTestRig.cs` (hạ tầng): `OrdersBridgeChannel` THẬT trên cổng loopback trống (bind port 0 → nhả) + `ClientWebSocket` giả làm extension, bắt tay bằng `ready` như production.
- `OrdersBridgeChannelTests.cs` (7): captcha bật cờ + fan-out; `ResetStages` xoá cờ + thay chặng; error chỉ fault chặng đang chờ (chặng khác pending); error khi không ai chờ = no-op; hết giờ 1 chặng không phá kênh; `pageData` về đúng chặng theo `kind` (cả data chuỗi lẫn data mảng); gửi lệnh khi chưa mở cổng → ném ngay.
- `OrdersBridgeFlowTests.cs` (8): `redownloadSlip` roundtrip (lệnh đúng → base64 %PDF → file `SN.pdf`), trả rác không phải PDF → false + không ghi file, trả rỗng → false; check trả hàng **`tabTraHang=false` → BỎ LƯỢT, không ghi mốc, không lưu mã** (fix đợt B), không đọc được số → bỏ lượt, đúng tab lần đầu → lưu mã + chốt mốc, dính captcha trước bước → bỏ hẳn (không gửi lệnh); `RunShopOrdersAsync` gọi callback lưu đúng 1 lần với đơn đã parse.
- `OrderPersistPipelineTests.cs` (4): lưu 2 lần không đẻ dòng trùng; đơn MỚI không phải "chuẩn bị hàng" → BỎ QUA (luật B1 chống lặp ghi-xóa); đơn đã theo dõi vẫn cập nhật khi rời trạng thái; shop-context gắn vào đơn + nhãn rỗng → null. (`NenXoaDonKetThuc`/luật notify đã có sẵn `AccountSessionCleanupTests`/`NotifyDonTra*Tests`, nay trỏ sang `OrderPersistPipeline`.)

### 4. Điểm cần phiên chính soi

1. **Test cũ đổi tiền tố kiểu**: 6 file test bridge + 5 file test persist chỉ đổi `OrdersBridgeSession.X` → `ShopFlowRunner/UocTinhDon.X`, `AccountSession.X` → `OrderPersistPipeline/SlipFiles.X`. Tên FILE test giữ nguyên (`AccountSessionCleanupTests`, `AccountSessionHubPushTests`) dù nội dung nay test `OrderPersistPipeline` — cân nhắc đổi tên file ở đợt sau nếu muốn.
2. **Một thay đổi hành vi nhỏ, có chủ đích**: gửi lệnh khi cầu nối CHƯA mở cổng nay ném `InvalidOperationException("Cầu nối chưa khởi động…")` thay vì `NullReferenceException` (đường cũ dùng `_ws!`). Không đường chạy thật nào tới được nhánh này.
3. **Test flaky CÓ SẴN (không phải do đợt này)**: 1/5 lượt chạy đầu, `HubOutboxGsheetSheet2Tests.ChuaCauHinh_VanGuiSheet2Rong` fail `ObjectDisposedException: SQLitePCL.sqlite3` ngay trong `new AppServices(...)`. Nguyên nhân: `TempDatabase.Dispose` gọi `SqliteConnection.ClearAllPools()` — chốt TOÀN TIẾN TRÌNH — trong khi lớp test khác đang chạy SONG SONG dùng connection từ pool. 4 lượt chạy sau xanh sạch. Đợt này có thêm `OrderPersistPipelineTests` cũng dùng `TempDatabase` (4 lần dispose) nên xác suất trúng tăng nhẹ. Cách trị dứt (ngoài phạm vi plan này, cần phiên chính quyết): `[assembly: CollectionBehavior(DisableTestParallelization = true)]` hoặc bỏ `ClearAllPools` khỏi `TempDatabase.Dispose`.
4. **Code chết CÓ SẴN, KHÔNG đụng tới**: `AccountSession.TryClearVerifyFailedAfterLogin` và `AccountSession.TrySaveCookie` là private nhưng KHÔNG còn ai gọi (tàn dư đường Playwright cũ); `SlipFiles.ThieuPhieu` chỉ còn test dùng; `PrepareResult.SlipTabUrl` set mà không đọc. Đều có từ trước đợt tách — báo để phiên chính quyết dọn hay giữ.
5. `sed` dọn tiền tố ban đầu làm rụng CRLF của ~50 file test không đổi nội dung → đã `git checkout --` khôi phục; `git status` hiện chỉ còn đúng các file thuộc việc này.
