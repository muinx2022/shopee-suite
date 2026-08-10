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

/// <summary>Có chạy bước "đặt địa chỉ lấy hàng" trong lượt này không, và chạy để LÀM GÌ
/// (xem <see cref="ShopFlowRunner.QuyetDinhBuocDiaChi"/>).</summary>
internal enum BuocDiaChi
{
    /// <summary>Không chạy bước địa chỉ (shop không có đơn chờ lấy hàng và cũng không có cảnh báo nào cần gỡ).</summary>
    Bo,

    /// <summary>Có đơn Chờ Lấy Hàng + có thư mục phiếu → đặt địa chỉ RỒI xử đơn (in phiếu) như xưa nay.</summary>
    DatRoiXuDon,

    /// <summary>Shop đang có BANNER lỗi địa chỉ mà lượt này không xử đơn được → vẫn chạy bước địa chỉ, nhưng CHỈ
    /// để biết shop còn lỗi hay không (đặt được thì vòng ngoài tự gỡ banner + báo Hub). Không chuẩn bị hàng,
    /// không in phiếu.</summary>
    ThuLaiChoCanhBao,
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
/// chỉ → bước BÙ: tự tải lại phiếu THIẾU của shop (<see cref="TaiLaiPhieuThieuAsync"/>) → bước PHỤ cuối: check đơn
/// trả hàng. Kèm hai thao tác lẻ trên cùng tab đó: đóng tab shop về picker
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
    // Tab "Shops": gọi mỗi khi chuẩn bị xong 1 đơn (nhãn shop, MÃ ĐƠN) → App +1 prepare_daily + đánh dấu đơn đó.
    private readonly Action<string, string>? _onOrderPrepared;
    // Bước CUỐI flow shop — check đơn trả hàng (callback do App rót vì Core không biết accountId). Null → bỏ hẳn bước.
    private readonly Func<string, int?>? _returnCountLast;
    private readonly Action<string, int>? _saveReturnCount;
    private readonly Func<IReadOnlyList<YeuCauTraHang>, string>? _saveReturnCodes;
    // Đếm mã CHƯA có trong kho `return_codes` — tín hiệu quyết định còn lật trang trả hàng nữa hay không
    // (Core không biết accountId nên App rót vào). Null → KHÔNG lật trang, chỉ đọc trang đầu.
    private readonly Func<IReadOnlyList<YeuCauTraHang>, int>? _demMaTraChuaBiet;
    // TỰ TẢI LẠI PHIẾU THIẾU: App trả danh sách order_sn của ĐÚNG shop đang mở đang có mã vận đơn NHƯNG thiếu file
    // PDF hợp lệ, đã xếp MỚI NHẤT TRƯỚC (Core không biết accountId/thư mục phiếu của App). Null → bỏ HẲN bước —
    // đó cũng là đường "Chạy thử" (RunSliceCoreAsync): nó chỉ đọc, không lưu, nên không được kéo theo bước này.
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>>? _layDonThieuPhieu;
    // Shop này có đang treo BANNER lỗi địa chỉ trên tab Shops không (App rót — Core không biết accountId/DB).
    // Null → coi như không shop nào có banner ⇒ hành vi y hệt trước 10/08/2026 (đường "Chạy thử" không rót).
    private readonly Func<string, bool>? _dangCoCanhBaoDiaChi;

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

    /// <summary>
    /// CHỐT CHẶN số đơn TỰ TẢI LẠI PHIẾU trong MỘT lượt của MỘT shop. Mỗi lượt <c>redownloadSlip</c> là một vòng
    /// điều hướng THẬT trên Seller Centre (về danh sách "Tất cả" → duyệt trang tìm card → bấm In phiếu giao → chờ
    /// tab phiếu), tốn tới <see cref="OrdersBridgeChannel.ChoChang.Redownload"/> mỗi đơn — lần đầu bật tính năng có
    /// thể tồn đọng hàng trăm đơn thiếu phiếu, tải hết trong một vòng là shop này ngốn cả buổi còn shop khác chưa
    /// được sờ tới. Lấy <b>MỚI NHẤT TRƯỚC</b> (xem <see cref="ChiaTheoTranTaiLaiPhieu"/>), phần còn lại để vòng sau
    /// và LOG rõ số bỏ lại — cấm im lặng cắt.
    /// <para>Thấp hơn <see cref="TranDonMoiLuotShop"/> (50) vì đây là việc BÙ (đơn đáng lẽ đã có phiếu từ vòng
    /// trước), không phải việc chính của lượt.</para>
    /// </summary>
    private const int TranTaiLaiPhieuMoiShop = 20;

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
        Func<IReadOnlyList<YeuCauTraHang>, string>? saveReturnCodes,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? layDonThieuPhieu = null,
        Func<IReadOnlyList<YeuCauTraHang>, int>? demMaTraChuaBiet = null,
        Func<string, bool>? dangCoCanhBaoDiaChi = null)
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
        _layDonThieuPhieu = layDonThieuPhieu;
        _demMaTraChuaBiet = demMaTraChuaBiet;
        _dangCoCanhBaoDiaChi = dangCoCanhBaoDiaChi;
    }

    /// <summary>Nhãn shop KHÔNG đặt được địa chỉ lấy hàng (null = chưa dính). Cùng khuôn cờ với
    /// <see cref="OrdersBridgeChannel.CaptchaSeen"/>: <see cref="RunShopOrdersAsync"/> đặt, vòng ngoài (phiên) đọc
    /// để BỎ QUA shop này (không in phiếu) rồi sang shop kế — KHÔNG đẻ kênh sự kiện thứ hai.</summary>
    public string? PickupFailedShop { get; set; }

    /// <summary>Nhãn shop ĐẶT ĐƯỢC địa chỉ lấy hàng trong lượt này (null = chưa/không chạy bước đặt địa chỉ).
    /// Đối xứng <see cref="PickupFailedShop"/> — vòng ngoài (phiên) đọc để TỰ GỠ banner lỗi địa chỉ cũ của shop
    /// đó. CHỈ đặt khi bước đặt địa chỉ THỰC SỰ chạy và trả ok: shop 0 đơn chờ lấy hàng không chạy bước này nên
    /// KHÔNG được coi là "đã hết lỗi" (chưa chứng minh được gì).</summary>
    public string? PickupOkShop { get; set; }

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

    /// <summary>
    /// HÀM THUẦN (test được, không cần trình duyệt) — lượt này có chạy bước "đặt địa chỉ lấy hàng" không, và
    /// chạy để LÀM GÌ:
    /// <list type="bullet">
    /// <item>Có đơn Chờ Lấy Hàng VÀ có thư mục lưu phiếu → <see cref="BuocDiaChi.DatRoiXuDon"/> (luật cũ, không đổi).</item>
    /// <item>Không xử đơn được, NHƯNG shop đang treo banner lỗi địa chỉ → <see cref="BuocDiaChi.ThuLaiChoCanhBao"/>.
    /// Đây là lỗ hổng vá ngày 10/08/2026: banner chỉ tự hết khi bước địa chỉ CHẠY và trả ok, mà bước đó xưa nay chỉ
    /// chạy khi có đơn chờ lấy hàng ⇒ shop ít đơn (vd <c>piko.store1</c>, 0 đơn suốt nhiều vòng) treo banner vĩnh
    /// viễn dù địa chỉ có thể đã đặt được từ lâu.</item>
    /// <item>Còn lại → <see cref="BuocDiaChi.Bo"/>. Shop không lỗi thì tuyệt đối KHÔNG đụng vào địa chỉ của nó —
    /// mỗi lượt đặt địa chỉ là một thao tác GHI thật trên Seller Centre.</item>
    /// </list>
    /// <para>Có đơn mà THIẾU thư mục phiếu (in phiếu không nổi) + đang có banner ⇒ vẫn
    /// <see cref="BuocDiaChi.ThuLaiChoCanhBao"/>: không in được phiếu không phải lý do để bỏ luôn cơ hội gỡ banner.</para>
    /// </summary>
    internal static BuocDiaChi QuyetDinhBuocDiaChi(int toShip, bool coThuMucPhieu, bool dangCoCanhBao)
        => toShip > 0 && coThuMucPhieu ? BuocDiaChi.DatRoiXuDon
            : dangCoCanhBao ? BuocDiaChi.ThuLaiChoCanhBao
            : BuocDiaChi.Bo;

    /// <summary>Hỏi App "shop này có đang treo banner lỗi địa chỉ không". Callback đọc SQLite trên thread nền của
    /// phiên nên phải bọc kín: hỏng thì coi như KHÔNG có banner — thà bỏ một lượt thử lại còn hơn làm chết cả shop.</summary>
    private bool DangCoCanhBaoDiaChi(string shopLogin)
    {
        if (_dangCoCanhBaoDiaChi is null || string.IsNullOrWhiteSpace(shopLogin))
        {
            return false;
        }
        try { return _dangCoCanhBaoDiaChi(shopLogin); }
        catch (Exception ex)
        {
            L("Không đọc được danh sách cảnh báo địa chỉ: " + ex.ToString() + " — coi như shop không có cảnh báo.");
            return false;
        }
    }

    /// <summary>
    /// BƯỚC ĐẶT ĐỊA CHỈ LẤY HÀNG trên tab shop ĐANG MỞ — tách riêng để hai đường dùng CHUNG: vòng shop thường
    /// (<see cref="ThanShopAsync"/>) và lượt kiểm tra lại theo lệnh người dùng
    /// (<see cref="KiemTraLaiDiaChiAsync"/>). Hai nơi tự gửi lệnh riêng là hai luật trôi lệch — mà bên trong
    /// extension bước này còn có dọn modal chắn + thử lại một lượt, càng không được chép đôi.
    /// </summary>
    private async Task<SauDatDiaChi> DatDiaChiAsync(CancellationToken ct)
    {
        var pickupTcs = _ch.ArmPickup();
        await _ch.SendAsync(new { action = "setPickupAddress", province = _province }).ConfigureAwait(false);
        var pickupOk = await _ch.AwaitAsync(pickupTcs, OrdersBridgeChannel.ChoChang.Pickup, ct).ConfigureAwait(false);
        return QuyetDinhSauDatDiaChi(pickupOk, _ch.CaptchaSeen);
    }

    /// <summary>
    /// TRẢ ĐỊA CHỈ LẤY HÀNG VỀ ĐỊA CHỈ KHÁC — giữ tag "trả hàng" ở địa chỉ mặc định. Chạy sau MỌI lượt đặt địa chỉ
    /// THÀNH CÔNG, kể cả lượt chỉ chạy để gỡ banner (<see cref="BuocDiaChi.ThuLaiChoCanhBao"/>): đặt xong mà không
    /// trả về là shop bị treo tag lấy hàng ở địa chỉ tỉnh, lệch với mọi shop chạy trọn vòng bình thường.
    /// <para>Best-effort: hết giờ thì bỏ qua (đã đặt được địa chỉ rồi, đó mới là việc chính của bước này).</para>
    /// </summary>
    private async Task TraDiaChiVeKhacAsync(CancellationToken ct)
    {
        L("Set địa chỉ lấy hàng về địa chỉ khác (hoàn tất flow shop)...");
        var pickupOtherTcs = _ch.ArmPickupOther();
        await _ch.SendAsync(new { action = "setPickupAddressToOther" }).ConfigureAwait(false);
        try { await _ch.AwaitAsync(pickupOtherTcs, OrdersBridgeChannel.ChoChang.PickupOther, ct).ConfigureAwait(false); }
        catch (TimeoutException) { L("Set địa chỉ khác: quá hạn — bỏ qua."); }
    }

    /// <summary>
    /// LƯỢT KIỂM TRA LẠI ĐỊA CHỈ theo lệnh người dùng (nút "Check" trên banner lỗi địa chỉ): CHỈ chạy bước đặt
    /// địa chỉ trên tab shop đang mở — KHÔNG đọc đơn, KHÔNG chuẩn bị hàng, KHÔNG in phiếu, KHÔNG check trả hàng.
    /// <para>
    /// Đặt <see cref="PickupOkShop"/> / <see cref="PickupFailedShop"/> y HỆT vòng shop thường, để vòng ngoài
    /// dùng lại NGUYÊN đường gỡ banner + báo Hub sẵn có (<c>GoBannerLoiDiaChi</c>). Cố ý KHÔNG viết đường gỡ
    /// banner thứ hai: hai đường gỡ là hai luật rev/tombstone trôi lệch nhau, mà lớp bug đó đã cắn hai lần.
    /// </para>
    /// <para>
    /// <b>Captcha thì KHÔNG kết luận</b>: không đặt cả hai cờ. Coi captcha là "vẫn lỗi" thì banner bị giữ oan;
    /// coi là "hết lỗi" thì gỡ banner của shop chưa hề kiểm được. Không biết thì nói không biết.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> = shop ĐẶT ĐƯỢC địa chỉ (đủ căn cứ gỡ banner).</returns>
    public async Task<bool> KiemTraLaiDiaChiAsync(string shopLogin, CancellationToken ct)
    {
        // Nhãn có thể RỖNG (picker không đọc được tên) — vẫn phải là chuỗi KHÁC null, y như vòng shop thường,
        // kẻo tín hiệu (null = không có gì) mất theo cái nhãn.
        var nhan = string.IsNullOrWhiteSpace(shopLogin) ? "(không rõ shop)" : shopLogin;
        PickupOkShop = null;
        PickupFailedShop = null;

        L($"Kiểm tra lại địa chỉ lấy hàng ({_province}) cho shop {nhan}...");
        var quyetDinh = await DatDiaChiAsync(ct).ConfigureAwait(false);

        if (quyetDinh == SauDatDiaChi.DungViCaptcha)
        {
            L($"PHÁT HIỆN captcha khi kiểm tra lại địa chỉ shop {nhan} — KHÔNG kết luận được, GIỮ NGUYÊN banner.");
            return false;
        }
        if (quyetDinh == SauDatDiaChi.DungViDiaChi)
        {
            PickupFailedShop = nhan;
            L($"⛔ Kiểm tra lại: VẪN không đặt được địa chỉ lấy hàng ({_province}) cho shop {nhan} — giữ banner.");
            return false;
        }

        PickupOkShop = nhan;
        L($"✓ Kiểm tra lại: shop {nhan} ĐẶT ĐƯỢC địa chỉ lấy hàng ({_province}) — gỡ banner + báo Hub.");
        return true;
    }

    // ── GĐ3: đọc đơn (Phần A) + xử đơn (Phần B) trên tab shop đang mở ───────────────────────────────────

    /// <summary>
    /// Flow MỘT shop = <see cref="ThanShopAsync"/> (đọc đơn → địa chỉ → chuẩn bị hàng → in phiếu → bù phiếu
    /// thiếu) + mắt xích CUỐI là bước PHỤ <see cref="CheckDonTraHangAsync"/>.
    /// <para>
    /// <b>Vì sao bước phụ nằm ở ĐÂY chứ không ở cuối thân:</b> thân có 3 nhánh <c>return</c> sớm (captcha khi
    /// đọc đơn, captcha khi đặt địa chỉ, KHÔNG đặt được địa chỉ lấy hàng). Đặt ở cuối thân thì shop nào dính lỗi
    /// địa chỉ — lỗi thường trực, có hẳn banner riêng — <b>không bao giờ</b> được check trả hàng. Trang trả hàng
    /// chẳng liên quan gì tới địa chỉ lấy hàng, không có lý do gì bỏ theo.
    /// </para>
    /// <para>
    /// Ngoại lệ NÉM ra từ thân (cầu nối chết / hết giờ / hủy) thì KHÔNG chạy bước phụ: lúc đó gửi thêm lệnh chỉ
    /// là ngồi chờ hết hạn. Captcha thì <see cref="CheckDonTraHangAsync"/> tự bỏ (trang đang là <c>/verify</c>).
    /// </para>
    /// </summary>
    public async Task<(int Orders, int Slips)> RunShopOrdersAsync(string shopId, string shopLogin, int toShip, CancellationToken ct)
    {
        var kq = await ThanShopAsync(shopId, shopLogin, toShip, ct).ConfigureAwait(false);

        // Bọc kín: lỗi/timeout/captcha ở bước phụ KHÔNG được phá phần chuẩn bị hàng + in phiếu đã xong ở trên,
        // cũng KHÔNG được dừng vòng shop. Chạy cả khi toShip = 0 (yêu cầu trả hàng không liên quan đơn chờ lấy hàng).
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

        return kq;
    }

    /// <summary>THÂN flow shop — mọi thứ TRỪ bước phụ check trả hàng (xem <see cref="RunShopOrdersAsync"/>).
    /// Có 3 nhánh <c>return</c> sớm; đó chính là lý do bước phụ không được đặt ở cuối hàm này.</summary>
    private async Task<(int Orders, int Slips)> ThanShopAsync(string shopId, string shopLogin, int toShip, CancellationToken ct)
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

        // Phần B — chỉ khi có đơn Chờ Lấy Hàng VÀ có thư mục lưu phiếu; shop KHÔNG xử đơn được mà đang treo banner
        // lỗi địa chỉ thì vẫn chạy riêng bước địa chỉ để banner có đường tự hết (xem QuyetDinhBuocDiaChi).
        var slips = 0;
        var buocDiaChi = QuyetDinhBuocDiaChi(toShip, !string.IsNullOrWhiteSpace(_invoiceDir), DangCoCanhBaoDiaChi(shopLogin));
        if (buocDiaChi == BuocDiaChi.DatRoiXuDon)
        {
            L($"Có {toShip} đơn Chờ Lấy Hàng — đặt địa chỉ lấy hàng ({_province}) rồi xử từng đơn...");
            var quyetDinh = await DatDiaChiAsync(ct).ConfigureAwait(false);
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

            // Lọt qua cả hai nhánh dừng ⇒ bước đặt địa chỉ ĐÃ CHẠY và trả ok. Đây là tín hiệu duy nhất chứng
            // minh shop này KHÔNG còn lỗi địa chỉ — vòng ngoài đọc để tự gỡ banner cũ. Nhãn shop có thể RỖNG
            // (picker không đọc được tên) → vẫn phải là chuỗi KHÁC null, y như PickupFailedShop.
            PickupOkShop = string.IsNullOrWhiteSpace(shopLogin) ? "(không rõ shop)" : shopLogin;

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

                // Tab "Shops": mỗi prep = 1 đơn arrange xong → App +1 đếm theo (shop, ngày) VÀ đánh dấu ĐÚNG đơn
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
            await TraDiaChiVeKhacAsync(ct).ConfigureAwait(false);
        }
        else if (buocDiaChi == BuocDiaChi.ThuLaiChoCanhBao)
        {
            // Shop đang treo banner lỗi địa chỉ mà lượt này không xử đơn được (thường là 0 đơn Chờ Lấy Hàng).
            // Vẫn chạy ĐÚNG bước địa chỉ của vòng thường — bên trong extension bước này tự dọn modal chắn trang
            // (TOS/tour) rồi thử lại một lượt, nên "fix lỗi khi modal mở ra" đi kèm sẵn, không phải viết lại.
            var nhan = string.IsNullOrWhiteSpace(shopLogin) ? "(không rõ shop)" : shopLogin;
            L($"Shop {nhan} đang treo cảnh báo lỗi địa chỉ mà lượt này không xử đơn — vẫn thử lại bước đặt địa chỉ "
              + $"({_province}) để cảnh báo có đường TỰ hết...");
            var quyetDinh = await DatDiaChiAsync(ct).ConfigureAwait(false);
            if (quyetDinh == SauDatDiaChi.DungViCaptcha)
            {
                L("PHÁT HIỆN captcha khi thử lại địa chỉ — KHÔNG kết luận được, giữ nguyên cảnh báo.");
                return (orders.Count, 0);
            }
            if (quyetDinh == SauDatDiaChi.XuDon)
            {
                PickupOkShop = nhan;
                L($"✓ Shop {nhan} ĐẶT ĐƯỢC địa chỉ lấy hàng ({_province}) — cuối vòng sẽ gỡ cảnh báo, báo Hub và gỡ ở máy khác.");
                await TraDiaChiVeKhacAsync(ct).ConfigureAwait(false);
            }
            else
            {
                // CỐ Ý không đặt PickupFailedShop: banner đã treo sẵn rồi. Đặt cờ ở đây là đếm một shop khỏe
                // thành shop hỏng, bắn lại tin Slack và đẩy Hub một lượt vô ích ở MỖI vòng. Không có đơn nào để
                // in nên cũng chẳng có gì để "bỏ qua" — im lặng giữ nguyên hiện trạng là đúng.
                L($"⛔ Thử lại: shop {nhan} VẪN không đặt được địa chỉ lấy hàng ({_province}) — giữ cảnh báo. "
                  + "Lượt này không xử đơn nên không ảnh hưởng việc in phiếu.");
            }
        }

        // ── Bước BÙ: TỰ TẢI LẠI PHIẾU THIẾU của shop này ────────────────────────────────────────────────
        // Chạy SAU cả Phần B và SAU mọi lượt _syncCallback: danh sách "thiếu phiếu" do App tính trên DB, phải là DB
        // VỪA cập nhật (mã vận đơn bắt lúc chuẩn bị hàng đã lưu ở trên) — chạy trước thì sót đúng những đơn vừa
        // arrange. Cũng chạy khi toShip = 0: đơn thiếu phiếu là DI SẢN của vòng/máy trước, không liên quan đơn chờ
        // lấy hàng của lượt này. Bọc kín như bước check trả hàng: hỏng ở đây KHÔNG được phá phần đã xong.
        try
        {
            await TaiLaiPhieuThieuAsync(shopLogin, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            L("Tự tải lại phiếu thiếu lỗi: " + ex.ToString() + " — bỏ qua bước này, phần đã xong không bị ảnh hưởng.");
        }

        return (orders.Count, slips);
    }

    /// <summary>
    /// HÀM THUẦN (test được, không cần trình duyệt) — chia danh sách đơn thiếu phiếu của MỘT shop (App đã lọc +
    /// xếp <b>MỚI NHẤT TRƯỚC</b>) thành phần LÀM NGAY (tối đa <paramref name="tran"/> đơn đầu = mới nhất) và số đơn
    /// ĐỂ LẠI vòng sau. Bỏ mã rỗng + trùng lặp (giữ lần xuất hiện đầu = bản mới nhất) trước khi cắt, kẻo trần bị
    /// một mã lặp ăn mất suất.
    /// <para><paramref name="tran"/> ≤ 0 → không tải đơn nào, cả danh sách tính là "còn lại" (không nuốt số).</para>
    /// </summary>
    internal static (IReadOnlyList<string> CanTai, int ConLai) ChiaTheoTranTaiLaiPhieu(
        IReadOnlyList<string>? ds, int tran)
    {
        if (ds is null || ds.Count == 0)
        {
            return (Array.Empty<string>(), 0);
        }

        var sach = ds
            .Where(sn => !string.IsNullOrWhiteSpace(sn))
            .Select(sn => sn.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (tran <= 0)
        {
            return (Array.Empty<string>(), sach.Count);
        }

        var canTai = sach.Take(tran).ToList();
        return (canTai, sach.Count - canTai.Count);
    }

    /// <summary>
    /// <b>Bước BÙ của flow shop — TỰ TẢI LẠI PHIẾU THIẾU.</b> Hỏi App danh sách <c>order_sn</c> của ĐÚNG shop đang
    /// mở đang có mã vận đơn nhưng thiếu file PDF hợp lệ (đã xếp mới nhất trước), cắt theo
    /// <see cref="TranTaiLaiPhieuMoiShop"/> rồi gọi <see cref="RedownloadSlipAsync"/> cho từng mã trên chính tab
    /// shop này (extension quét danh sách trên tab đang mở nên chỉ ở đây mới tải được).
    /// <list type="bullet">
    /// <item>BỎ HẲN bước khi: chưa rót callback (đường "Chạy thử"), không có thư mục lưu phiếu, nhãn shop rỗng, đã
    /// thấy captcha, hoặc <paramref name="ct"/> đã hủy.</item>
    /// <item><b>KHÔNG thử lại trong CÙNG vòng:</b> mỗi mã đúng MỘT lượt. Đơn quá cũ đã rơi khỏi danh sách "Tất cả"
    /// thì extension trả rỗng mãi mãi — thử lại chỉ đốt thời gian của shop khác.</item>
    /// <item>Hết giờ chờ extension → DỪNG cả bước (extension không phản hồi thì các đơn sau cũng vậy, mỗi lượt
    /// chờ tốn <see cref="OrdersBridgeChannel.ChoChang.Redownload"/>); lỗi lẻ khác → log rồi đi tiếp đơn kế.</item>
    /// </list>
    /// </summary>
    internal async Task TaiLaiPhieuThieuAsync(string shopLogin, CancellationToken ct)
    {
        if (_layDonThieuPhieu is null || string.IsNullOrWhiteSpace(shopLogin))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(_invoiceDir))
        {
            L("Tải lại phiếu thiếu: chưa cấu hình thư mục lưu phiếu — bỏ bước này.");
            return;
        }
        if (_ch.CaptchaSeen)
        {
            L("Tải lại phiếu thiếu: đã dính captcha từ bước trước — bỏ bước này (để vòng sau).");
            return;
        }
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<string>? ds;
        try
        {
            ds = await _layDonThieuPhieu(shopLogin, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            L("Tải lại phiếu thiếu: không đọc được danh sách đơn thiếu phiếu: " + ex.ToString());
            return;
        }

        var (canTai, conLai) = ChiaTheoTranTaiLaiPhieu(ds, TranTaiLaiPhieuMoiShop);
        if (canTai.Count == 0)
        {
            // conLai > 0 chỉ xảy ra khi trần ≤ 0 (không có ở production) — vẫn log để không im lặng cắt.
            if (conLai > 0)
            {
                L($"Tải lại phiếu thiếu shop {shopLogin}: 0/0 thành công (còn {conLai} đơn để vòng sau).");
            }
            return;
        }

        L($"Tải lại phiếu thiếu shop {shopLogin}: {canTai.Count} đơn trong lượt này (mới nhất trước)"
          + (conLai > 0 ? $", để lại {conLai} đơn cho vòng sau (trần {TranTaiLaiPhieuMoiShop})." : "."));

        var ok = 0;
        var thu = 0;
        foreach (var sn in canTai)
        {
            ct.ThrowIfCancellationRequested();
            if (_ch.CaptchaSeen)
            {
                L("Tải lại phiếu thiếu: gặp captcha — dừng bước này, các đơn còn lại để vòng sau.");
                break;
            }

            thu++;
            try
            {
                if (await RedownloadSlipAsync(sn, ct).ConfigureAwait(false))
                {
                    ok++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException)
            {
                L($"Tải lại phiếu thiếu: quá hạn chờ extension ở đơn {sn} — dừng bước này (các đơn sau cũng sẽ chờ vô ích).");
                break;
            }
            catch (InvalidOperationException ex)
            {
                // Cầu nối chưa khởi động / extension rụng kết nối (fail-fast của SendAsync) — mọi đơn sau cũng thế.
                L("Tải lại phiếu thiếu: cầu nối/extension không sẵn sàng (" + ex.Message + ") — dừng bước này.");
                break;
            }
            catch (Exception ex)
            {
                L($"Tải lại phiếu đơn {sn} lỗi: " + ex.ToString() + " — đi tiếp đơn kế.");
            }
        }

        // "còn k đơn" = số đơn CHƯA THỬ trong lượt này (phần vượt trần + phần bị cắt vì captcha/hết giờ). Đơn đã
        // thử mà trượt thì m-n ở vế trước đã nói, và nó vẫn thiếu phiếu nên vòng sau tự gặp lại.
        L($"Tải lại phiếu thiếu shop {shopLogin}: {ok}/{thu} thành công "
          + $"(còn {canTai.Count - thu + conLai} đơn chưa thử — để vòng sau).");
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

        // CHỐT THEO DỮ LIỆU, không tin lời extension. Extension báo "đang ở tab Đơn Trả hàng/Hoàn tiền" mà đọc ra
        // toàn ĐƠN HỦY thì nó đang đứng nhầm tab — dù cờ tabTraHang=true. Ca thật 10/08/2026: alina99.store báo
        // đúng tab nhưng 33/33 dòng đều là đơn hủy, và số 33 (của tab "Tất cả") bị ghi thẳng vào mốc ⇒ từ đó mọi
        // yêu cầu trả hàng mới của shop bị nuốt vĩnh viễn. Nhận diện markup tab là cuộc rượt đuổi với Shopee;
        // luật này KHÔNG phụ thuộc markup nên còn đúng cả khi họ đổi giao diện lần nữa.
        if (TraHangParser.NghiSaiTabTheoDuLieu(doc.Dong))
        {
            L($"⚠ Check đơn trả hàng [{shopLogin}]: extension báo đúng tab nhưng {doc.Dong.Count} dòng đọc được ĐỀU là "
              + $"ĐƠN HỦY (0 dòng có mã yêu cầu) — {luot.SoMoi} nhiều khả năng là số của tab khác → BỎ LƯỢT, mốc giữ nguyên.");
            return;
        }

        var soMoi = luot.SoMoi;
        var quyetDinh = TraHangParser.QuyetDinhCheck(mocCu, soMoi);
        var mocCuText = mocCu?.ToString() ?? "chưa có";
        switch (quyetDinh.Luat)
        {
            case LuatSoYeuCau.LanDau:
                L($"Check đơn trả hàng [{shopLogin}]: {soMoi} yêu cầu — LẦN ĐẦU của shop này, quét sâu rồi ghi mốc.");
                break;
            case LuatSoYeuCau.KhongDoi:
                L($"Check đơn trả hàng [{shopLogin}]: {soMoi} yêu cầu — không đổi so với mốc {mocCuText} (số không đổi KHÔNG có nghĩa là không có mã mới).");
                break;
            case LuatSoYeuCau.Giam:
                L($"Check đơn trả hàng [{shopLogin}]: {soMoi} yêu cầu — GIẢM so với mốc {mocCuText} (có yêu cầu đã xử xong, rớt khỏi danh sách).");
                break;
            default:
                L($"Check đơn trả hàng [{shopLogin}]: {soMoi} yêu cầu — TĂNG {quyetDinh.SoDongMoiUocTinh} so với mốc {mocCuText}.");
                break;
        }

        // ĐỌC THÊM TRANG SÂU — độ sâu do DỮ LIỆU quyết định, KHÔNG do mốc: lật tiếp chừng nào trang vừa đọc còn
        // ra mã MỚI so với kho `return_codes`. Hết cái mới thì dừng.
        //
        // Vì sao KHÔNG suy độ sâu từ mốc (bản đầu 09/08 làm vậy và hỏng): mốc chỉ null ở lần check ĐẦU TIÊN của
        // mỗi shop, mà mốc được ghi ở cuối MỌI lượt từ 29/07 và không có migration nào reset ⇒ mọi shop đang
        // chạy đều có mốc ≠ null ⇒ nhóm shop TỒN ĐỌNG (nhóm duy nhất cần quét sâu) không bao giờ được quét sâu.
        //
        // Chi phí: lượt thường trang đầu không có mã mới ⇒ KHÔNG lật trang nào, đúng bằng chi phí trước đây.
        // Shop tồn đọng ⇒ lật cho tới khi hết mã mới, tự rút cạn qua một hai lượt rồi vào nếp.
        var dong = new List<DongTraHang>(doc.Dong);
        var trangDaLat = 0;
        var docDuSau = true;   // false = lượt này BIẾT là còn sót (chạm trần / lật trượt / captcha) ⇒ giữ nguyên mốc
        var coTrangSau = doc.CoTrangSau;
        if (!doc.SortApplied && coTrangSau)
        {
            // Thứ tự không tin được ⇒ lật trang chỉ là nhặt ngẫu nhiên trong lịch sử. Đọc trang đầu rồi thôi,
            // nhưng phải coi là ĐỌC THIẾU: mốc không được nhảy, kẻo lượt sau tưởng đã đọc hết.
            L("Check đơn trả hàng: KHÔNG đổi được sắp xếp — chỉ đọc trang đầu, KHÔNG lật trang (mốc giữ nguyên).");
            docDuSau = false;
        }
        else if (coTrangSau && _demMaTraChuaBiet is not null && MaMoiTrong(doc.Dong) > 0)
        {
            while (trangDaLat < TraHangParser.TranTrangTraHang)
            {
                // Trần DÒNG áp cho CẢ LƯỢT (gộp mọi trang) — extension chỉ kẹp trần trong phạm vi MỘT lệnh nên
                // nếu ở đây không kẹp thì lật 10 trang có thể vượt xa TranDongMoiLuot.
                if (dong.Count >= TraHangParser.TranDongMoiLuot)
                {
                    L($"Check đơn trả hàng: chạm trần {TraHangParser.TranDongMoiLuot} dòng/lượt — dừng lật, còn sót, để lượt sau đọc tiếp.");
                    docDuSau = false;
                    break;
                }
                var them = await DocThemTrangTraHangAsync(1, ct).ConfigureAwait(false);
                if (them.Dong.Count == 0)
                {
                    // Hết trang thật thì `CoTrangSau` đã false ở lượt trước; tới đây mà rỗng là lật TRƯỢT
                    // (bấm nhầm nút / danh sách không vẽ lại) hoặc captcha ⇒ còn sót, đừng chốt mốc.
                    L($"Check đơn trả hàng: lật sang trang {trangDaLat + 2} KHÔNG đọc được dòng nào — dừng lật, coi như còn sót.");
                    docDuSau = false;
                    break;
                }
                trangDaLat++;
                dong.AddRange(them.Dong);
                var maMoi = MaMoiTrong(them.Dong);
                L($"Check đơn trả hàng: trang {trangDaLat + 1} → thêm {them.Dong.Count} dòng ({maMoi} mã mới), tổng {dong.Count}.");
                if (maMoi == 0)
                {
                    break; // trang này không còn gì mới ⇒ các trang sau (cũ hơn) cũng vậy
                }
                coTrangSau = them.CoTrangSau;
                if (!coTrangSau)
                {
                    break; // hết trang thật — đã đọc tới đáy
                }
                if (trangDaLat >= TraHangParser.TranTrangTraHang)
                {
                    L($"Check đơn trả hàng: chạm trần {TraHangParser.TranTrangTraHang} trang mà vẫn còn mã mới — còn sót, để lượt sau đọc tiếp.");
                    docDuSau = false;
                }
            }
        }

        // Parse HẾT dòng đọc được, MỌI nhánh luật — kể cả KhongDoi/Giam. Bản trước cắt theo hiệu số rồi vẫn ghi
        // mốc: "+3 mới, −3 xử xong" ra số y hệt nên 3 mã mới bị vứt VĨNH VIỄN. Đọc thừa không hại: chống trùng
        // nằm ở ReturnCodesRepository.LuuMaTraHang (mã cũ không đụng dòng ⇒ không đẩy lại, không notify lại).
        if (dong.Count > 0)
        {
            var ghep = TraHangParser.GhepCap(dong);
            var phanDonHuy = ghep.BoQuaDonHuy > 0 ? $", bỏ {ghep.BoQuaDonHuy} dòng vì href là ĐƠN HỦY" : string.Empty;
            L($"Check đơn trả hàng: đọc {dong.Count} dòng → {ghep.Cap.Count} cặp đủ hai mã, {ghep.ThieuMaYeuCau.Count} dòng THIẾU mã yêu cầu{phanDonHuy}.");
            // In nguyên văn tối đa 3 dòng thiếu (kèm nhãn + HTML thô): nếu luật nhận diện theo nhãn trượt thì
            // nhật ký lần chạy thật lộ ngay class/nhãn thật — class khối mã yêu cầu tới giờ vẫn CHƯA xác nhận.
            foreach (var mo in ghep.ThieuMaYeuCau.Take(3))
            {
                L("Check đơn trả hàng — dòng thiếu mã yêu cầu → " + mo);
            }

            // Một đơn có TỪ HAI yêu cầu: giữ mã mới nhất (user chốt 09/08), mã còn lại chỉ ghi nhật ký — kho mã
            // khoá theo (tài khoản, mã đơn) và cột trên sheet cũng chỉ có MỘT ô mỗi đơn. In tối đa 3 dòng: số
            // này lớn bất thường nghĩa là nhiều đơn đang có nhiều yêu cầu cùng lúc, lúc đó mới cần bàn cách chứa.
            var trung = ghep.TrungMaDon ?? Array.Empty<string>();
            if (trung.Count > 0)
            {
                L($"Check đơn trả hàng: {trung.Count} đơn có NHIỀU HƠN MỘT yêu cầu — giữ mã mới nhất, bỏ phần còn lại.");
                foreach (var mo in trung.Take(3))
                {
                    L("Check đơn trả hàng — đơn nhiều yêu cầu → " + mo);
                }
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

        // Cập nhật mốc — CHỈ khi lượt này đọc đủ sâu. Lượt BIẾT là còn sót (không đổi được sắp xếp / lật trượt /
        // chạm trần / captcha giữa chừng) mà vẫn chốt mốc thì lượt sau nhìn vào mốc tưởng "không đổi" rồi thôi —
        // đúng cái bẫy mốc-nhảy-khi-chưa-đọc-hết mà cả đợt này đi vá. Giữ mốc cũ thì cùng lắm chậm một vòng.
        if (docDuSau)
        {
            _saveReturnCount!(shopLogin, soMoi);
        }
        else
        {
            L($"Check đơn trả hàng [{shopLogin}]: lượt này đọc CHƯA đủ sâu — GIỮ NGUYÊN mốc {mocCuText} để lượt sau đọc tiếp.");
        }
    }

    /// <summary>
    /// Số mã trong <paramref name="dong"/> là MỚI với kho <c>return_codes</c> — tín hiệu quyết định còn lật trang
    /// nữa hay không. Đi qua ĐÚNG đường xử lý thật (ghép cặp → lọc cửa sổ ngày) rồi mới đếm: một trang toàn mã
    /// CŨ HƠN cửa sổ thì dù chưa có trong kho cũng không đáng lật tiếp — chúng sẽ bị lọc bỏ ở bước lưu.
    /// <para>Chưa rót callback (đường "Chạy thử") → 0 ⇒ không lật trang nào.</para>
    /// </summary>
    private int MaMoiTrong(IReadOnlyList<DongTraHang> dong)
    {
        if (_demMaTraChuaBiet is null || dong.Count == 0)
        {
            return 0;
        }
        var cap = TraHangParser.GhepCap(dong).Cap;
        var giu = TraHangParser.LocTheoCuaSo(cap, DateTime.Now, TraHangParser.SoNgayCuaSoTraHang).GiuLai;
        return giu.Count == 0 ? 0 : _demMaTraChuaBiet(giu);
    }

    /// <summary>
    /// Lượt ĐỌC THÊM của bước check trả hàng: bảo extension lật tiếp tối đa <paramref name="soTrangThem"/> trang
    /// TRÊN CHÍNH trang đang mở (không điều hướng, không chọn lại tab, không đổi lại sắp xếp) rồi trả các dòng
    /// đọc thêm. Chỉ dùng phần <see cref="KetQuaDocTraHang.Dong"/> của phản hồi — ô tổng/tab/sắp xếp đã chốt ở
    /// lượt đầu.
    /// <para>
    /// <b>KHÔNG bao giờ ném:</b> hết giờ / extension đời cũ không biết lệnh này / captcha giữa chừng đều trả
    /// danh sách RỖNG. Phần trang đầu đã đọc được là thứ phải giữ bằng mọi giá — thà thiếu phần sâu còn hơn
    /// mất cả lượt vì một lệnh mở rộng.
    /// </para>
    /// </summary>
    private async Task<(IReadOnlyList<DongTraHang> Dong, bool CoTrangSau)> DocThemTrangTraHangAsync(
        int soTrangThem, CancellationToken ct)
    {
        try
        {
            var tcs = _ch.ArmReturns();
            await _ch.SendAsync(new { action = "readReturnRequestsMore", maxPages = soTrangThem }).ConfigureAwait(false);
            var json = await _ch.AwaitAsync(tcs, OrdersBridgeChannel.ChoChang.Returns, ct).ConfigureAwait(false);
            if (_ch.CaptchaSeen)
            {
                L("Check đơn trả hàng: gặp captcha khi lật thêm trang — giữ phần trang đầu, bỏ phần sâu.");
                return (Array.Empty<DongTraHang>(), false);
            }
            var kq = TraHangParser.ParseKetQua(json);
            return (kq.Dong, kq.CoTrangSau);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            L("Check đơn trả hàng: lật thêm trang lỗi: " + ex.ToString() + " — giữ phần trang đầu.");
            return (Array.Empty<DongTraHang>(), false);
        }
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
