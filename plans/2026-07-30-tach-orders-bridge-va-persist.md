# Plan: Tách OrdersBridgeSession + OrderPersistPipeline khỏi AccountSession + bổ sung test bridge (đợt 4 — orders A)

- **Ngày:** 2026-07-30
- **Trạng thái:** đang làm
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

(chưa)
