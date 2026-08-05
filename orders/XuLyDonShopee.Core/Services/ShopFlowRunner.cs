using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>Quyết định sau bước "đặt địa chỉ lấy hàng" (xem <see cref="ShopFlowRunner.QuyetDinhSauDatDiaChi"/>).</summary>
internal enum SauDatDiaChi
{
    /// <summary>Đã đặt được địa chỉ → chạy tiếp vòng Chuẩn bị hàng (in phiếu).</summary>
    XuDon,

    /// <summary>Rơi vào captcha/verify → dừng shop này (hành vi cũ, không đổi).</summary>
    DungViCaptcha,

    /// <summary>KHÔNG đặt được địa chỉ lấy hàng → dừng SHOP này (không in phiếu); vòng ngoài vẫn chạy shop kế.</summary>
    DungViDiaChi,
}

/// <summary>Quyết định sau khi ĐỌC xong trang trả hàng, TRƯỚC khi chạy luật đếm
/// (xem <see cref="ShopFlowRunner.QuyetDinhLuotTraHang"/>).</summary>
internal enum SauDocTraHang
{
    /// <summary>Không đọc được số yêu cầu (trang chưa render / đổi giao diện) → bỏ lượt, mốc GIỮ NGUYÊN.</summary>
    BoLuotKhongCoSo,

    /// <summary>Đọc được số nhưng KHÔNG chọn được tab "Đơn Trả hàng Hoàn tiền" → số đang là của tab "Tất cả" →
    /// bỏ lượt, mốc GIỮ NGUYÊN.</summary>
    BoLuotSaiTab,

    /// <summary>Số đọc được là của ĐÚNG tab trả hàng → chạy luật đếm + chốt mốc.</summary>
    XuLy,
}

/// <summary>Kết quả <see cref="ShopFlowRunner.QuyetDinhLuotTraHang"/>: nhánh xử lý + số yêu cầu đọc được
/// (0 ở nhánh <see cref="SauDocTraHang.BoLuotKhongCoSo"/>).</summary>
internal readonly record struct LuotDocTraHang(SauDocTraHang Nhanh, int SoMoi);

/// <summary>
/// <b>FLOW của MỘT shop trên tab đang mở</b>: đọc đơn tab "Tất cả" → lấy "Số tiền cuối cùng" → callback lưu
/// DB/GSheet/hub → (nếu có đơn Chờ Lấy Hàng) đặt địa chỉ lấy hàng + Chuẩn bị hàng từng đơn + in phiếu + revert địa
/// chỉ → bước PHỤ cuối: check đơn trả hàng. Kèm hai thao tác lẻ trên cùng tab đó: đóng tab shop về picker
/// (<see cref="DongTabShopAsync"/>) và tải lại phiếu một đơn (<see cref="RedownloadSlipAsync"/>).
/// <para>
/// Tách khỏi <see cref="OrdersBridgeSession"/> (đợt dọn 2026-07-30): phiên chỉ còn lo vòng đời (đăng nhập → SSO →
/// lặp shop → nghỉ), còn mọi thứ xảy ra BÊN TRONG một shop nằm ở đây. Trao đổi với extension đi qua
/// <see cref="OrdersBridgeChannel"/> nên test được bằng client WebSocket giả (không cần trình duyệt).
/// </para>
/// </summary>
internal sealed class ShopFlowRunner
{
    private readonly OrdersBridgeChannel _ch;
    private readonly Action<string>? _log;
    private readonly string? _invoiceDir;
    private readonly string _province;
    // Callback do App rót — gọi sau khi đọc xong đơn MỖI shop để App lưu DB/GSheet/hub (Core không ref App).
    private readonly Func<string, string, IReadOnlyList<SyncedOrder>, CancellationToken, Task>? _syncCallback;
    // App rót tập order_sn ĐÃ có "Số tiền cuối cùng" trong DB → bỏ qua, không mở lại chi tiết mỗi chu kỳ. null → không lọc.
    private readonly Func<IReadOnlySet<string>>? _finalDoneSns;
    // Tab "Kết quả": gọi mỗi khi chuẩn bị xong 1 đơn (nhãn shop, MÃ ĐƠN) → App +1 prepare_daily + đánh dấu đơn đó.
    private readonly Action<string, string>? _onOrderPrepared;
    // Bước CUỐI flow shop — check đơn trả hàng (callback do App rót vì Core không biết accountId). Null → bỏ hẳn bước.
    private readonly Func<string, int?>? _returnCountLast;
    private readonly Action<string, int>? _saveReturnCount;
    private readonly Func<IReadOnlyList<YeuCauTraHang>, string>? _saveReturnCodes;

    // Tập rỗng dùng khi _finalDoneSns null (tránh cấp phát mỗi shop).
    private static readonly IReadOnlySet<string> EmptyFinalSet = new HashSet<string>();

    /// <summary>
    /// CHỐT CHẶN số đơn Chuẩn bị hàng trong MỘT lượt của MỘT shop — vòng <c>prepareNextOrder</c> dừng theo tín hiệu
    /// "hết đơn" của extension, hằng này chỉ là dây bảo hiểm để một extension kẹt (luôn trả đơn) KHÔNG quay vô tận.
    /// Để thấp (50) vì mỗi đơn tốn tới <see cref="OrdersBridgeChannel.ChoChang.Prepare"/>: shop nhiều đơn hơn thì
    /// lượt sau xử tiếp, chứ không ngồi một shop cả buổi trong khi các shop khác chưa được sờ tới.
    /// <para>KHÁC <c>OrderPersistPipeline.HubPushBatchSize</c> (200): đó là cỡ LÔ đẩy đơn lên hub — thao tác HTTP
    /// thuần, rẻ, nên chia to cho ít lượt gọi. Hai con số ở hai tầng khác nhau, KHÔNG liên quan.</para>
    /// </summary>
    private const int TranDonMoiLuotShop = 50;

    public ShopFlowRunner(
        OrdersBridgeChannel channel,
        Action<string>? log,
        string? invoiceDir,
        string province,
        Func<string, string, IReadOnlyList<SyncedOrder>, CancellationToken, Task>? syncCallback,
        Func<IReadOnlySet<string>>? finalDoneSns,
        Action<string, string>? onOrderPrepared,
        Func<string, int?>? returnCountLast,
        Action<string, int>? saveReturnCount,
        Func<IReadOnlyList<YeuCauTraHang>, string>? saveReturnCodes)
    {
        _ch = channel;
        _log = log;
        _invoiceDir = invoiceDir;
        _province = province;
        _syncCallback = syncCallback;
        _finalDoneSns = finalDoneSns;
        _onOrderPrepared = onOrderPrepared;
        _returnCountLast = returnCountLast;
        _saveReturnCount = saveReturnCount;
        _saveReturnCodes = saveReturnCodes;
    }

    /// <summary>Nhãn shop KHÔNG đặt được địa chỉ lấy hàng (null = chưa dính). Cùng khuôn cờ với
    /// <see cref="OrdersBridgeChannel.CaptchaSeen"/>: <see cref="RunShopOrdersAsync"/> đặt, vòng ngoài (phiên) đọc
    /// để BỎ QUA shop này (không in phiếu) rồi sang shop kế — KHÔNG đẻ kênh sự kiện thứ hai.</summary>
    public string? PickupFailedShop { get; set; }

    private void L(string m) => _log?.Invoke(m);

    /// <summary>
    /// HÀM THUẦN (test được, không cần trình duyệt) — quyết định làm gì sau khi extension trả lời bước
    /// <c>setPickupAddress</c>:
    /// <list type="bullet">
    /// <item><paramref name="captchaSeen"/> → <see cref="SauDatDiaChi.DungViCaptcha"/> (ưu tiên: captcha nuốt luôn
    /// <c>pickupOk=false</c>, thông điệp phải là captcha kẻo người trực xử nhầm).</item>
    /// <item><paramref name="pickupOk"/> = false → <see cref="SauDatDiaChi.DungViDiaChi"/>: dừng SHOP, KHÔNG in phiếu;
    /// vòng ngoài vẫn chạy shop kế. Bài học 28/07: app biết chưa đặt được địa chỉ mà vẫn in phiếu + giao đơn
    /// ⇒ shipper tới sai chỗ lấy hàng.</item>
    /// <item>còn lại → <see cref="SauDatDiaChi.XuDon"/>.</item>
    /// </list>
    /// </summary>
    internal static SauDatDiaChi QuyetDinhSauDatDiaChi(bool pickupOk, bool captchaSeen)
        => captchaSeen ? SauDatDiaChi.DungViCaptcha
            : pickupOk ? SauDatDiaChi.XuDon
            : SauDatDiaChi.DungViDiaChi;

    // ── GĐ3: đọc đơn (Phần A) + xử đơn (Phần B) trên tab shop đang mở ───────────────────────────────────
    public async Task<(int Orders, int Slips)> RunShopOrdersAsync(string shopId, string shopLogin, int toShip, CancellationToken ct)
    {
        // Phần A — đọc đơn tab "Tất cả" (test được ngay, kể cả shop 0 đơn chờ).
        var ordersTcs = _ch.ArmOrders();
        await _ch.SendAsync(new { action = "syncOrders" }).ConfigureAwait(false);
        var ordersJson = await _ch.AwaitAsync(ordersTcs, OrdersBridgeChannel.ChoChang.Orders, ct).ConfigureAwait(false);
        if (_ch.CaptchaSeen)
        {
            L("PHÁT HIỆN captcha khi đọc đơn.");
            return (0, 0);
        }
        var orders = ShopeeLoginService.ParseOrdersJson(ordersJson);
        L($"Đọc được {orders.Count} đơn (Tất cả).");

        // Lấy "Số tiền cuối cùng" (cột Ước tính) cho đơn ĐANG chuẩn bị CHƯA có final: mở CHI TIẾT từng đơn (extension) →
        // đọc [type='FinalAmount'] .amount + DANH SÁCH SẢN PHẨM (SKU/phân loại thật) trong CÙNG lần mở đó →
        // MERGE vào DTO TRƯỚC callback persist (một lần upsert). finalDoneSns = tập
        // order_sn ĐÃ có final trong DB (App rót) → bỏ, không mở lại mỗi chu kỳ. Best-effort: lỗi/timeout/captcha → vẫn lưu phần đã có.
        var done = _finalDoneSns?.Invoke() ?? EmptyFinalSet;
        // Nhóm CHÍNH (đang chuẩn bị) + nhóm BÙ (đã rời trạng thái mà vẫn thiếu ước tính, ≤7 ngày, trần 5 đơn) —
        // xem UocTinhDon.ChonDonLayUocTinh. BÙ xếp SAU chính: extension cắt trần 30 đơn/lượt từ cuối nên phần bị cắt là bù.
        var (chinhFinal, buFinal) = UocTinhDon.ChonDonLayUocTinh(orders, done, DateTime.Now);
        var needFinal = chinhFinal.Concat(buFinal).ToList();
        if (needFinal.Count > 0)
        {
            try
            {
                var finalsTcs = _ch.ArmFinals();
                await _ch.SendAsync(new
                {
                    action = "syncOrderFinals",
                    orders = needFinal.Select(o => new { orderSn = o.OrderSn, shopeeOrderId = o.ShopeeOrderId }),
                }).ConfigureAwait(false);
                // Đủ thời gian mở tuần tự nhiều tab chi tiết (20s/đơn), trần cứng 300s.
                var timeout = OrdersBridgeChannel.ChoChang.Finals(needFinal.Count);
                var finalsJson = await _ch.AwaitAsync(finalsTcs, timeout, ct).ConfigureAwait(false);
                if (_ch.CaptchaSeen)
                {
                    L("PHÁT HIỆN captcha khi mở chi tiết lấy Số tiền cuối cùng — bỏ bước final (vẫn lưu phần đã có).");
                }
                else
                {
                    UocTinhDon.MergeFinalAmounts(orders, finalsJson, L);
                    // Đếm SAU khi merge: cả hai nhóm đều được chọn với FinalAmount là null nên khác null = vừa lấy được.
                    if (chinhFinal.Count > 0)
                    {
                        L($"Lấy Số tiền cuối cùng: {chinhFinal.Count(o => o.FinalAmount is not null)}/{chinhFinal.Count} đơn.");
                    }
                    if (buFinal.Count > 0)
                    {
                        L($"Lấy bù Số tiền cuối cùng (đơn đã rời trạng thái chuẩn bị): {buFinal.Count(o => o.FinalAmount is not null)}/{buFinal.Count} đơn.");
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { L("Lấy Số tiền cuối cùng lỗi: " + ex.ToString() + " — vẫn lưu phần đã có."); }
        }

        // GĐ4: App lưu DB/GSheet/hub cho shop này (callback do App rót; null ở đường "Chạy thử" → chỉ đọc, không lưu).
        if (_syncCallback is not null)
        {
            try { await _syncCallback(shopId, shopLogin, orders, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { L("Lưu đơn (DB/GSheet/hub) lỗi: " + ex.ToString()); }
        }

        // Phần B — chỉ khi có đơn Chờ Lấy Hàng VÀ có thư mục lưu phiếu.
        var slips = 0;
        if (toShip > 0 && !string.IsNullOrWhiteSpace(_invoiceDir))
        {
            L($"Có {toShip} đơn Chờ Lấy Hàng — đặt địa chỉ lấy hàng ({_province}) rồi xử từng đơn...");
            var pickupTcs = _ch.ArmPickup();
            await _ch.SendAsync(new { action = "setPickupAddress", province = _province }).ConfigureAwait(false);
            var pickupOk = await _ch.AwaitAsync(pickupTcs, OrdersBridgeChannel.ChoChang.Pickup, ct).ConfigureAwait(false);
            var quyetDinh = QuyetDinhSauDatDiaChi(pickupOk, _ch.CaptchaSeen);
            if (quyetDinh == SauDatDiaChi.DungViCaptcha)
            {
                L("PHÁT HIỆN captcha khi đặt địa chỉ lấy hàng.");
                return (orders.Count, 0);
            }
            if (quyetDinh == SauDatDiaChi.DungViDiaChi)
            {
                // KHÔNG chạy prepareNextOrder: in phiếu lúc này = phiếu sai địa chỉ lấy hàng, shipper tới sai chỗ
                // và không ai biết. Thà không giao đơn còn hơn giao sai địa chỉ (người dùng đã chốt 28/07).
                // KHÔNG revert (setPickupAddressToOther): mọi lối ok=false của extension đều CHƯA bấm "Lưu" trong
                // modal Sửa Địa chỉ ⇒ địa chỉ lấy hàng của shop còn NGUYÊN như trước vòng này, không có gì để trả về;
                // chạy revert lúc này chỉ là một lượt GHI nữa vào đúng màn hình đang hỏng.
                // Nhãn shop có thể RỖNG (picker không đọc được tên) — vẫn phải là chuỗi KHÁC null, kẻo tín hiệu
                // lỗi địa chỉ (null = không lỗi) mất theo cái nhãn.
                PickupFailedShop = string.IsNullOrWhiteSpace(shopLogin) ? "(không rõ shop)" : shopLogin;
                L($"⛔ Không đặt được địa chỉ lấy hàng ({_province}) — BỎ QUA shop này, sang shop kế (nếu còn), KHÔNG in phiếu (tránh phiếu sai địa chỉ).");
                return (orders.Count, 0);
            }

            // Lặp Chuẩn bị hàng tới khi hết đơn / chạm chốt chặn / captcha. Mã vận đơn bắt NGAY tại modal
            // "Thông Tin Chi Tiết" (extension đọc trước khi in phiếu) → gom theo mã đơn để cập nhật DB same-cycle.
            var capturedTracking = new Dictionary<string, string>(StringComparer.Ordinal);
            var guard = 0;
            while (guard++ < TranDonMoiLuotShop)
            {
                ct.ThrowIfCancellationRequested();
                var prepareTcs = _ch.ArmPrepare();
                await _ch.SendAsync(new { action = "prepareNextOrder" }).ConfigureAwait(false);
                // 300s: extension chờ Shopee tạo vận đơn (≤90s) TRƯỚC khi in, rồi chờ tab phiếu (≤120s) — nới hạn cho đủ.
                var prep = await _ch.AwaitAsync(prepareTcs, OrdersBridgeChannel.ChoChang.Prepare, ct).ConfigureAwait(false);
                if (_ch.CaptchaSeen)
                {
                    L("PHÁT HIỆN captcha khi xử đơn — dừng.");
                    break;
                }
                if (prep is null)
                {
                    L("Hết đơn cần Chuẩn bị hàng.");
                    break;
                }

                // Tab "Kết quả": mỗi prep = 1 đơn arrange xong → App +1 đếm theo (shop, ngày) VÀ đánh dấu ĐÚNG đơn
                // (prep.OrderCode = order_sn — cùng khóa đang dùng cho capturedTracking/tên file phiếu) đã chuẩn bị
                // hàng, để hub đếm chung. Đếm theo ĐƠN (không theo phiếu) nên đặt TRƯỚC TrySaveSlip: phiếu lưu lỗi
                // vẫn tính đã chuẩn bị. Null-safe, không đổi luồng.
                _onOrderPrepared?.Invoke(shopLogin, prep.OrderCode);

                var saved = TrySaveSlip(prep.SlipBase64, prep.OrderCode, _invoiceDir!);
                if (saved) slips++;
                if (!string.IsNullOrWhiteSpace(prep.Tracking) && !string.IsNullOrWhiteSpace(prep.OrderCode))
                {
                    capturedTracking[prep.OrderCode] = prep.Tracking!;
                }
                L($"Đã chuẩn bị đơn {prep.OrderCode} — {(saved ? "lưu phiếu OK" : "CHƯA lưu được phiếu (kiểm tra tay)")}"
                  + (string.IsNullOrWhiteSpace(prep.Tracking) ? "" : $", mã vận đơn {prep.Tracking}") + ".");
            }
            L($"Xử đơn xong: {slips} phiếu đã lưu.");

            // Mã vận đơn bắt được NGAY lúc chuẩn bị hàng → cập nhật DTO + LƯU LẠI (DB tracking + GSheet "vận đơn mới" +
            // hub) SAME-CYCLE, khỏi chờ chu kỳ sync sau. Best-effort (lỗi không phá flow revert địa chỉ bên dưới).
            if (capturedTracking.Count > 0 && _syncCallback is not null)
            {
                var updated = 0;
                foreach (var o in orders)
                {
                    if (capturedTracking.TryGetValue(o.OrderSn, out var tn) && !string.IsNullOrWhiteSpace(tn))
                    {
                        o.TrackingNumber = tn;
                        updated++;
                    }
                }
                if (updated > 0)
                {
                    L($"Cập nhật {updated} mã vận đơn (lúc chuẩn bị hàng) → lưu lại + đẩy GSheet/hub.");
                    try { await _syncCallback(shopId, shopLogin, orders, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { L("Lưu lại mã vận đơn lỗi: " + ex.ToString()); }
                }
            }

            // Hết đơn → set địa chỉ lấy hàng VỀ ĐỊA CHỈ KHÁC (giữ tag "trả hàng" ở địa chỉ mặc định) — hoàn tất 1 flow shop.
            L("Set địa chỉ lấy hàng về địa chỉ khác (hoàn tất flow shop)...");
            var pickupOtherTcs = _ch.ArmPickupOther();
            await _ch.SendAsync(new { action = "setPickupAddressToOther" }).ConfigureAwait(false);
            try { await _ch.AwaitAsync(pickupOtherTcs, OrdersBridgeChannel.ChoChang.PickupOther, ct).ConfigureAwait(false); }
            catch (TimeoutException) { L("Set địa chỉ khác: quá hạn — bỏ qua."); }
        }

        // ── Mắt xích CUỐI CÙNG của flow shop (bước PHỤ): check ĐƠN TRẢ HÀNG ──────────────────────────────
        // Bọc kín: lỗi/timeout/captcha ở đây KHÔNG được phá phần chuẩn bị hàng + in phiếu đã xong ở trên, cũng
        // KHÔNG được dừng vòng shop. Chạy cả khi toShip = 0 (yêu cầu trả hàng không liên quan đơn chờ lấy hàng).
        if (_returnCountLast is not null && _saveReturnCount is not null && _saveReturnCodes is not null
            && !string.IsNullOrWhiteSpace(shopLogin))
        {
            var captchaTruocBuoc = _ch.CaptchaSeen;
            try
            {
                await CheckDonTraHangAsync(shopLogin, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                L("Check đơn trả hàng lỗi: " + ex.ToString() + " — bỏ qua bước này, phần đã xong không bị ảnh hưởng.");
            }
            finally
            {
                // Captcha ở bước PHỤ này KHÔNG được dừng cả vòng (phần chính của shop đã xong xuôi) → trả cờ về
                // đúng như trước bước. Captcha THẬT vẫn lộ ngay ở shop kế: openShopDetail rơi /verify → dừng vòng
                // đúng chỗ, không mất mát gì.
                _ch.CaptchaSeen = captchaTruocBuoc;
            }
        }

        return (orders.Count, slips);
    }

    /// <summary>
    /// HÀM THUẦN (test được, không cần trình duyệt) — lượt đọc trang trả hàng vừa rồi có DÙNG ĐƯỢC để chạy luật
    /// đếm + chốt mốc không.
    /// <para>
    /// <b>Vì sao <c>tabTraHang</c> = false cũng phải BỎ LƯỢT</b> (chứ không chỉ cảnh báo như bản đầu): số đọc được
    /// lúc đó là của tab "Tất cả" — gộp cả Đơn Hủy / Giao hàng không thành công — nên LỚN HƠN hẳn số thật. Ghi nó
    /// vào mốc là ĐẦU ĐỘC mốc: lượt sau chọn được tab, số thật NHỎ hơn mốc rác ⇒ rơi nhánh
    /// <see cref="LuatSoYeuCau.Giam"/> "chỉ cập nhật mốc" ⇒ mọi yêu cầu phát sinh giữa chừng bị NUỐT vĩnh viễn
    /// (không vào <c>return_codes</c>, không lên Google Sheet). Bỏ lượt thì cùng lắm chậm một vòng.
    /// </para>
    /// </summary>
    internal static LuotDocTraHang QuyetDinhLuotTraHang(KetQuaDocTraHang doc)
    {
        if (doc?.SoYeuCau is not int soMoi)
        {
            return new LuotDocTraHang(SauDocTraHang.BoLuotKhongCoSo, 0);
        }
        return new LuotDocTraHang(doc.TabTraHang ? SauDocTraHang.XuLy : SauDocTraHang.BoLuotSaiTab, soMoi);
    }

    /// <summary>
    /// <b>Bước CUỐI flow shop — check ĐƠN TRẢ HÀNG.</b> Gửi <c>readReturnRequests</c>: extension mở trang
    /// "Trả hàng/Hoàn tiền/Hủy" của shop đang mở, CHỌN TAB "Đơn Trả hàng Hoàn tiền" (không chọn được thì ô tổng là
    /// của tab "Tất cả" — gộp cả Đơn Hủy / Đơn Giao hàng không thành công, hai loại KHÔNG có mã yêu cầu trả hàng →
    /// BỎ LƯỢT, xem <see cref="QuyetDinhLuotTraHang"/>), đổi
    /// sắp xếp sang "Ngày yêu cầu (Mới - Cũ)" (mặc định trang là "Ngày đến hạn" — không đổi thì luật "N dòng đầu"
    /// bỏ sót ÂM THẦM), rồi trả text ô tổng + HTML đầu mỗi dòng.
    /// C# parse (<see cref="TraHangParser"/>), so số với MỐC lần trước của shop để biết check bao nhiêu dòng ĐẦU,
    /// ghép cặp (mã đơn, mã yêu cầu), LỌC theo cửa sổ <see cref="TraHangParser.SoNgayCuaSoTraHang"/> ngày (theo
    /// NGÀY YÊU CẦU suy từ mã yêu cầu) rồi lưu. Cập nhật mốc ở CUỐI, kể cả lượt không check dòng nào.
    /// <para>
    /// KHÔNG ném ra ngoài phần thân — caller đã bọc try/catch; ở đây chỉ return sớm + log khi không đọc được.
    /// </para>
    /// </summary>
    internal async Task CheckDonTraHangAsync(string shopLogin, CancellationToken ct)
    {
        // ĐÃ dính captcha TRƯỚC bước này (vòng chuẩn bị hàng vừa break vì captcha) → bỏ hẳn, đừng gửi lệnh rồi
        // ngồi chờ 90s vô ích: trang đang là /verify nên extension không đọc được gì, mà mỗi shop tốn thêm 90s.
        if (_ch.CaptchaSeen)
        {
            L("Check đơn trả hàng: đã dính captcha từ bước trước — bỏ bước này (mốc giữ nguyên).");
            return;
        }

        var mocCu = _returnCountLast!(shopLogin);

        var returnsTcs = _ch.ArmReturns();
        await _ch.SendAsync(new { action = "readReturnRequests" }).ConfigureAwait(false);
        // 90s: điều hướng sang trang trả hàng (≤20s) + đổi sắp xếp + chờ danh sách vẽ lại.
        var json = await _ch.AwaitAsync(returnsTcs, OrdersBridgeChannel.ChoChang.Returns, ct).ConfigureAwait(false);
        if (_ch.CaptchaSeen)
        {
            L("Check đơn trả hàng: gặp captcha/verify — bỏ bước này (mốc giữ nguyên), đi tiếp.");
            return;
        }

        var doc = TraHangParser.ParseKetQua(json);
        var luot = QuyetDinhLuotTraHang(doc);
        if (luot.Nhanh == SauDocTraHang.BoLuotKhongCoSo)
        {
            L("Check đơn trả hàng: KHÔNG đọc được số yêu cầu (trang chưa render / Shopee đổi giao diện) — bỏ lượt, mốc giữ nguyên.");
            // 4 dấu hiệu extension thu ngay lúc bỏ lượt: phân biệt hết-giờ-THẬT / lạc-trang / sai-selector. Không
            // có nó thì nới thời gian chờ chỉ là đoán mò (xem pageChanDoanTraHang bên extension).
            if (!string.IsNullOrEmpty(doc.ChanDoan))
            {
                L("Check đơn trả hàng — chẩn đoán trang: " + doc.ChanDoan);
            }
            return;
        }
        if (luot.Nhanh == SauDocTraHang.BoLuotSaiTab)
        {
            L($"⚠ Check đơn trả hàng [{shopLogin}]: KHÔNG chọn được tab \"Đơn Trả hàng Hoàn tiền\" — {luot.SoMoi} là số của "
              + "tab \"Tất cả\" (gộp Đơn Hủy / Giao không thành công) → BỎ LƯỢT, mốc giữ nguyên.");
            return;
        }
        if (!doc.SortApplied)
        {
            L("⚠ Check đơn trả hàng: KHÔNG đổi được sắp xếp sang 'Ngày yêu cầu (Mới - Cũ)' — 'N dòng đầu' có thể sót.");
        }

        var soMoi = luot.SoMoi;
        var quyetDinh = TraHangParser.QuyetDinhCheck(mocCu, soMoi);
        var mocCuText = mocCu?.ToString() ?? "chưa có";
        switch (quyetDinh.Luat)
        {
            case LuatSoYeuCau.LanDau:
                L($"Check đơn trả hàng [{shopLogin}]: {soMoi} yêu cầu — LẦN ĐẦU, check {quyetDinh.SoDongCanCheck} dòng đầu rồi ghi mốc.");
                break;
            case LuatSoYeuCau.KhongDoi:
                L($"Check đơn trả hàng [{shopLogin}]: {soMoi} yêu cầu — không đổi so với mốc {mocCuText}, bỏ qua.");
                break;
            case LuatSoYeuCau.Giam:
                L($"Check đơn trả hàng [{shopLogin}]: {soMoi} yêu cầu — GIẢM so với mốc {mocCuText} (đã xử xong), chỉ cập nhật mốc.");
                break;
            default:
                L($"Check đơn trả hàng [{shopLogin}]: {soMoi} yêu cầu — TĂNG {quyetDinh.SoDongCanCheck} so với mốc {mocCuText}, check {quyetDinh.SoDongCanCheck} dòng đầu.");
                break;
        }

        if (quyetDinh.SoDongCanCheck > 0)
        {
            var canCheck = doc.Dong.Take(quyetDinh.SoDongCanCheck).ToList();
            if (canCheck.Count < quyetDinh.SoDongCanCheck)
            {
                L($"Check đơn trả hàng: cần {quyetDinh.SoDongCanCheck} dòng nhưng trang chỉ có {canCheck.Count} — check phần đọc được.");
            }

            var ghep = TraHangParser.GhepCap(canCheck);
            var phanDonHuy = ghep.BoQuaDonHuy > 0 ? $", bỏ {ghep.BoQuaDonHuy} dòng vì href là ĐƠN HỦY" : string.Empty;
            L($"Check đơn trả hàng: đọc {canCheck.Count} dòng → {ghep.Cap.Count} cặp đủ hai mã, {ghep.ThieuMaYeuCau.Count} dòng THIẾU mã yêu cầu{phanDonHuy}.");
            // In nguyên văn tối đa 3 dòng thiếu (kèm nhãn + HTML thô): nếu luật nhận diện theo nhãn trượt thì
            // nhật ký lần chạy thật lộ ngay class/nhãn thật — class khối mã yêu cầu tới giờ vẫn CHƯA xác nhận.
            foreach (var mo in ghep.ThieuMaYeuCau.Take(3))
            {
                L("Check đơn trả hàng — dòng thiếu mã yêu cầu → " + mo);
            }

            // Chặn theo THỜI GIAN trên NGÀY YÊU CẦU (suy từ mã yêu cầu), cửa sổ TraHangParser.SoNgayCuaSoTraHang
            // = 15 ngày chính sách Shopee + biên. CỐ Ý không dùng SoNgayBuUocTinh (7 ngày, đo trên ngày ĐẶT ĐƠN,
            // cho việc lấy bù "Số tiền cuối cùng") — khác trục, khác ý nghĩa.
            var loc = TraHangParser.LocTheoCuaSo(ghep.Cap, DateTime.Now, TraHangParser.SoNgayCuaSoTraHang);
            if (loc.BoQuaViCu > 0 || loc.GiuViKhongRoNgay > 0)
            {
                var themPhan = loc.GiuViKhongRoNgay > 0
                    ? $", GIỮ {loc.GiuViKhongRoNgay} mã không đọc được ngày yêu cầu (thà thừa còn hơn mất)"
                    : string.Empty;
                L($"Check đơn trả hàng: bỏ {loc.BoQuaViCu} dòng vì yêu cầu cũ hơn {TraHangParser.SoNgayCuaSoTraHang} ngày{themPhan} — còn {loc.GiuLai.Count} mã để lưu.");
            }

            if (loc.GiuLai.Count > 0)
            {
                L("Check đơn trả hàng — lưu mã: " + _saveReturnCodes!(loc.GiuLai));
            }
        }

        // Cập nhật mốc SAU khi xử xong (kể cả lượt không check dòng nào) để lần sau so đúng.
        _saveReturnCount!(shopLogin, soMoi);
    }

    /// <summary>Số lần gửi <c>closeShopTab</c> tối đa cho MỘT shop: lần đầu + đúng MỘT lần thử lại.</summary>
    private const int SoLanThuDongTabShop = 2;

    /// <summary>
    /// Đóng tab shop rồi đưa picker <c>/portal/shop</c> về trạng thái SẴN SÀNG cho shop kế. Trả <c>false</c> khi
    /// vẫn không sẵn sàng sau <see cref="SoLanThuDongTabShop"/> lần — caller DỪNG vòng kèm đúng lý do.
    /// <para>
    /// <b>Vì sao gửi LẠI chính <c>closeShopTab</c> làm bước hồi phục</b> (chứ không thêm lệnh mới): ở lần hai,
    /// <c>shopTabId</c> bên extension đã null nên <c>doCloseShopTab</c> rơi vào nhánh khác hẳn — điều hướng THẲNG
    /// <c>listTabId</c> về <c>/portal/shop</c>, chờ tab load xong rồi <c>ensureShopPicker</c>. Đó đúng là "đưa
    /// picker về trạng thái sạch", dùng ngay đường đã có thay vì đẻ thêm một lệnh cầu nối phải nuôi.
    /// </para>
    /// <para>Hết giờ (30s/lần) KHÔNG ném ra ngoài — nó chỉ là một lần thử trượt, cùng nghĩa với <c>ok=false</c>.</para>
    /// </summary>
    public async Task<bool> DongTabShopAsync(CancellationToken ct)
    {
        for (var lan = 1; lan <= SoLanThuDongTabShop; lan++)
        {
            var closeTcs = _ch.ArmCloseShop();
            await _ch.SendAsync(new { action = "closeShopTab" }).ConfigureAwait(false);

            var ok = false;
            try
            {
                ok = await _ch.AwaitAsync(closeTcs, OrdersBridgeChannel.ChoChang.CloseShop, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                L($"closeShopTab quá hạn (lần {lan}/{SoLanThuDongTabShop}).");
            }

            if (ok)
            {
                return true;
            }
            if (lan < SoLanThuDongTabShop)
            {
                L("closeShopTab báo CHƯA về được trang chọn shop — thử đưa picker về trạng thái sạch một lần nữa.");
            }
        }
        return false;
    }

    /// <summary>
    /// <b>Tải LẠI phiếu MỘT đơn qua cầu nối extension</b> (nút "Tải phiếu" màn Đơn hàng). Gửi action
    /// <c>redownloadSlip</c> (extension về danh sách "Tất cả" → định vị card theo <paramref name="orderSn"/> →
    /// bấm "In phiếu giao" → tải PDF trong tab awbprint → trả base64) rồi LƯU PDF vào thư mục phiếu đúng khuôn
    /// <see cref="TrySaveSlip"/> (tên file <c>SanitizeFileName(orderSn).pdf</c> — khớp chỗ
    /// <c>SlipFiles.TryReadSlipBase64</c>/<c>SlipFiles.SlipFileIsValidPdf</c> đọc, để cột "thiếu phiếu" tự hết đỏ). Trả
    /// <c>true</c> khi lưu được PDF hợp lệ. Ràng buộc mô hình cầu nối: phiên đang mở tab của shop nào thì tải lại
    /// được đơn của shop đó (extension quét danh sách trên tab đang mở) — đơn của shop khác/quá cũ → extension trả
    /// base64 rỗng → false.
    /// <para>
    /// FAIL-FAST: <see cref="OrdersBridgeChannel.SendAsync"/> ném <see cref="InvalidOperationException"/> khi
    /// extension chưa/không còn kết nối → NÉM ra ngoài cho caller (App) báo đúng "extension chưa kết nối", KHÔNG
    /// ngồi chờ timeout. Hủy chủ động (<paramref name="ct"/>) ném <see cref="OperationCanceledException"/>.
    /// </para>
    /// </summary>
    public async Task<bool> RedownloadSlipAsync(string orderSn, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orderSn))
        {
            return false;
        }
        if (!_ch.Started)
        {
            throw new InvalidOperationException("Cầu nối chưa khởi động (chưa mở phiên cầu nối) — không tải lại được phiếu.");
        }
        if (string.IsNullOrWhiteSpace(_invoiceDir))
        {
            L("Chưa cấu hình thư mục lưu phiếu — bỏ tải lại phiếu.");
            return false;
        }

        var redownloadTcs = _ch.ArmRedownload();
        L($"Tải lại phiếu đơn {orderSn} qua cầu nối extension...");
        await _ch.SendAsync(new { action = "redownloadSlip", orderSn }).ConfigureAwait(false); // ném nếu extension chưa kết nối

        // 180s: extension có thể phải duyệt vài trang danh sách + chờ tab phiếu (~30s) + tải PDF (~25s).
        var b64 = await _ch.AwaitAsync(redownloadTcs, OrdersBridgeChannel.ChoChang.Redownload, ct).ConfigureAwait(false);
        if (_ch.CaptchaSeen)
        {
            L($"Gặp captcha khi tải lại phiếu đơn {orderSn} — dừng.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(b64))
        {
            L($"Không nhận được phiếu đơn {orderSn} (không thấy đơn trong shop đang mở / chưa có nút In phiếu).");
            return false;
        }

        var ok = TrySaveSlip(b64, orderSn, _invoiceDir!);
        L(ok
            ? $"Đã lưu phiếu đơn {orderSn}."
            : $"Nhận được dữ liệu phiếu nhưng KHÔNG phải PDF hợp lệ (đơn {orderSn}).");
        return ok;
    }

    /// <summary>Ghi phiếu giao PDF từ <paramref name="slipBase64"/> (extension đã fetch NGAY TRONG tab awbprint —
    /// có cookie, same-origin blob — nên KHÔNG dùng HttpClient GET vô cookie như bản cũ). Kiểm magic <c>%PDF</c> rồi
    /// lưu <c>&lt;dir&gt;/&lt;SanitizeFileName(orderCode)&gt;.pdf</c>. Best-effort — mọi lỗi/không phải PDF → false.</summary>
    internal static bool TrySaveSlip(string? slipBase64, string orderCode, string dir)
    {
        if (string.IsNullOrWhiteSpace(slipBase64))
        {
            return false;
        }
        try
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(slipBase64); } catch { return false; }
            // Magic %PDF- — tránh lưu HTML/rác thành .pdf. Kiểm chung với phía đọc (SlipFiles) qua SlipMagic.
            if (!SlipMagic.LooksPdf(bytes))
            {
                return false;
            }
            System.IO.Directory.CreateDirectory(dir);
            var name = ShopeeShippingNav.SanitizeFileName(orderCode) + ".pdf";
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name), bytes);
            return true;
        }
        catch { return false; }
    }
}
