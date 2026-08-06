using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;
using System.Threading;
using System.Threading.Tasks;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>Một dòng phân bổ dùng chung cho trạng thái, vận chuyển và thanh toán.</summary>
public sealed record OrderStatisticBreakdown(string Label, int OrderCount, string OrderCountText,
    string ValueText, double Percentage, string PercentageText);

/// <summary>Một dòng hiệu quả của shop trong ảnh chụp kho đơn hiện tại.</summary>
public sealed record ShopStatisticRow(string Shop, int OrderCount, int ItemCount, string RevenueText,
    string AverageText, string TrackingRateText);

/// <summary>
/// Dashboard thống kê kho đơn. Số vẽ NGAY từ kho đơn trên máy (đồng bộ, không chặn), rồi nếu có Hub thì gọi NỀN
/// xin số CHUNG toàn hệ thống và thay vào — <see cref="SourceText"/> luôn nói rõ đang xem số nào.
/// Doanh thu ước tính bỏ đơn hủy, ưu tiên <c>final_amount</c> và dùng <c>total_price</c> khi chưa có số cuối cùng.
/// </summary>
public partial class OrderStatisticsViewModel : ViewModelBase, IDisposable
{
    public const string AllShopsLabel = "Tất cả shop";

    /// <summary>
    /// Vế CẢNH BÁO ghép vào dòng nguồn số LOCAL — bản NGẮN, vì đây là dòng LUÔN hiện trên đầu màn. Kho
    /// <c>orders</c> trên máy KHÔNG phải kho lịch sử: <c>HubOutbox</c> xoá hẳn đơn Đã giao/Đã hủy sau khi xong
    /// nghĩa vụ đẩy, và <c>OrderPersistPipeline</c> chỉ INSERT đơn mới khi còn "Chuẩn bị hàng" — nên khi đang xem
    /// số của máy thì 3 thẻ ĐÃ GIAO / ĐÃ HỦY / DOANH THU luôn HỤT. Nói thẳng ra màn thay vì dựng thêm bảng tổng
    /// hợp ở client (đã chốt: Hub là kho lịch sử duy nhất).
    /// <para>Câu giải thích ĐẦY ĐỦ nằm ở <see cref="GiaiThichKhoMayHutText"/> (đổ vào ToolTip): bản dài ghép thẳng
    /// vào dòng nguồn làm nó tràn 2 dòng và chạy sát ô "Từ ngày" ở màn 1366px.</para>
    /// </summary>
    private const string CanhBaoKhoMayHutNganText =
        " ĐÃ GIAO / ĐÃ HỦY / DOANH THU bị HỤT — rê chuột để xem vì sao.";

    /// <summary>Bản ĐẦY ĐỦ của <see cref="CanhBaoKhoMayHutNganText"/> — đổ vào <see cref="SourceToolTip"/> để dòng
    /// luôn hiện được ngắn mà người dùng vẫn tra được lý do.</summary>
    private const string GiaiThichKhoMayHutText =
        "Kho đơn trên MÁY NÀY chỉ giữ đơn CHƯA kết thúc: đơn Đã giao/Đã hủy bị dọn khỏi máy ngay sau khi đã đẩy"
        + " lên Hub và Google Sheet. Vì vậy 3 thẻ ĐÃ GIAO / ĐÃ HỦY / DOANH THU ƯỚC TÍNH của số máy luôn hụt —"
        + " chỉ số CHUNG (Hub) mới giữ đủ lịch sử.";

    /// <summary>Lượt hỏi Hub CÒN ĐANG BAY — số đang hiện là số local vẽ tạm. KHÔNG được nói "Hub không phản hồi"
    /// ở trạng thái này: lượt hỏi chưa xong thì chưa biết hub sống hay chết (đó là lỗi cũ — mỗi lần đổi ngày là
    /// hiện một dòng cáo buộc hub chết trong khi hub vẫn đang trả lời).
    /// <para>Ba hằng dưới đây là câu NỀN: vế cảnh báo ghép thêm ở <see cref="DatDongNguonLocal"/>, và CHỈ ghép khi
    /// màn thật sự đang có thẻ số.</para></summary>
    private const string SourceDangHoiText = "Số trên MÁY NÀY — đang hỏi Hub số chung…";
    private const string SourceLocalText = "Số trên MÁY NÀY — Hub không phản hồi nên chưa gộp được số chung.";
    private const string SourceStandaloneText = "Số trên MÁY NÀY (app chạy độc lập, chưa nối Hub).";
    private const string SourceSharedText = "Số chung toàn hệ thống (từ Hub).";
    /// <summary>Đang GIỮ số chung của lượt hỏi trước mà lượt hỏi mới nhất không về được — nói thẳng thay vì lẳng
    /// lặng để nguyên dòng "Số chung (Hub)" như thể vừa cập nhật.</summary>
    private const string SourceSharedStaleText = "Số chung (Hub) của lượt hỏi trước — lượt này Hub không phản hồi.";

    /// <summary>Hub bảo khoảng này KHÔNG có đơn nào trong khi kho máy vừa đọc ra {0} đơn. Hai bên nói ngược nhau
    /// (thường vì đơn của máy CHƯA đẩy lên Hub xong) — vẽ đè màn rỗng của Hub lên số máy đang có là "hỏng im
    /// lặng": người dùng bấm "Làm mới", thấy số nháy một cái rồi màn trắng. Giữ số máy và nói thẳng ra.</summary>
    private const string SourceHubBaoRongFormat =
        "Hub báo 0 đơn cho khoảng này, nhưng máy này đang có {0} đơn — hiện SỐ MÁY NÀY"
        + " (thường do đơn của máy chưa đẩy hết lên Hub).";
    private static readonly CultureInfo VnCulture = CultureInfo.GetCultureInfo("vi-VN");
    private readonly AppServices _services;
    private bool _reloadingOptions;

    /// <summary>Shop mà HUB có số nhưng kho đơn trên máy KHÔNG còn (đơn kết thúc đã bị dọn). Không nhớ lại thì
    /// <c>AllShopLogins()</c> làm shop biến mất khỏi ComboBox và bộ lọc âm thầm tụt về "Tất cả shop" trong khi số
    /// đang xem là số CHUNG toàn hệ thống. So khớp KHÔNG phân biệt hoa/thường vì shop_login của máy và của hub có
    /// thể khác nhau ở chữ hoa. Chỉ đọc/ghi trên luồng UI.</summary>
    private readonly HashSet<string> _shopTuHub = new(StringComparer.CurrentCultureIgnoreCase);

    /// <summary>Kho đơn ĐÃ đổi trong lúc màn bị ẩn nên lưới đang mang số CŨ — hạ khi <see cref="Reload"/> vẽ lại.
    /// Xem <see cref="DangHienTrenMan"/>.</summary>
    private bool _canVeLai;

    /// <summary>
    /// Màn Thống kê ĐANG là màn hiển thị hay không (<c>MainViewModel.OnSelectedNavIndexChanged</c> đặt). VM sống
    /// suốt vòng đời app, mà <c>OrdersChanged</c> bắn sau MỖI shop của MỖI lượt sync — không có cờ này thì mỗi lượt
    /// sync là một lần quét kho đơn trên luồng UI + một lần bắn HTTP lên Hub, kể cả khi người dùng đang ở màn khác.
    /// <para>Mặc định <c>false</c> (màn chưa từng mở). Không sợ số mốc meo lúc mở lên: đường vào màn
    /// (<c>case 2</c>) gọi <see cref="Reload"/> nên lưới luôn được vẽ lại từ kho đơn hiện tại.</para>
    /// </summary>
    public bool DangHienTrenMan { get; set; }

    /// <summary>Cho test soi cờ "đã bỏ qua một lượt vẽ vì màn đang ẩn" (xem <see cref="DangHienTrenMan"/>).</summary>
    internal bool DangChoVeLai => _canVeLai;

    /// <summary>Số thứ tự lượt thống kê ĐANG hiển thị (tăng mỗi lần <see cref="ApplyStatistics"/>). Kết quả Hub về
    /// mà số thứ tự không còn khớp thì BỎ QUA — người dùng chỉnh ngày liên tục, lượt cũ về sau sẽ đè lượt mới.
    /// Chỉ đọc/ghi trên luồng UI.</summary>
    private int _statsRequestId;

    /// <summary>Số ĐANG hiển thị là số chung của Hub (đã vẽ xong một lượt <see cref="ApplyShared"/>), kèm shop +
    /// khoảng ngày của lượt đó. Dùng để lượt vẽ kế tiếp CÙNG shop/khoảng (vd <c>OrdersChanged</c> bắn sau mỗi lượt
    /// sync) KHÔNG vẽ đè số local lên nữa: người dùng đang thấy số nhảy xuống số máy rồi lại nhảy lên số chung mỗi
    /// lần đồng bộ. Đổi shop/ngày thì các giá trị này không còn khớp → vẽ local ngay như cũ (số cũ của khoảng khác
    /// còn sai hơn). Chỉ đọc/ghi trên luồng UI.</summary>
    private bool _dangHienSoHub;
    private string? _shopSoHub;
    private CreatedRange _rangeSoHub;

    /// <summary>Số đơn kho MÁY đọc ra ở lượt <see cref="ApplyLocal"/> gần nhất, kèm shop + khoảng ngày của lượt
    /// đó. Dùng để bắt ca "Hub báo 0 đơn mà máy đang có đơn" (xem <see cref="SourceHubBaoRongFormat"/>) — phải so
    /// CẢ shop lẫn khoảng, vì số của khoảng khác thì không nói lên điều gì. Chỉ đọc/ghi trên luồng UI.</summary>
    private int _soDonLocalLuotNay;
    private string? _shopLocalLuotNay;
    private CreatedRange _rangeLocalLuotNay;

    // ══════════ Nhịp tự sang ngày mới (app chạy 24/7) ══════════
    /// <summary>Nhịp dò "đã sang ngày mới chưa" — 60s: đủ nhạy để chip khoảng ngày trượt sang ngày mới gần như
    /// tức thì mà gần như không tốn gì (chỉ so hai <see cref="DateTime"/>, không đụng DB/hub khi ngày chưa đổi).
    /// Cùng con số với nhịp của tab "Kết quả" bên màn Tài khoản.</summary>
    private static readonly TimeSpan NhipDoSangNgay = TimeSpan.FromSeconds(60);

    /// <summary>Đồng hồ dò sang ngày (chạy trên thread nền → callback marshal về UI thread). Dựng ở ctor, dọn ở
    /// <see cref="Dispose"/> (shell gọi khi thoát app — <c>OrdersModuleHost.StopAsync</c>).</summary>
    private readonly System.Threading.Timer _timerSangNgay;

    /// <summary>Ngày mà màn coi là HÔM NAY ở lần dò gần nhất. Chỉ đụng trên UI thread (xem
    /// <see cref="KiemTraSangNgay"/>).</summary>
    private DateTime _ngayCoiLaHomNay;

    // ══════════ Chip chọn nhanh khoảng ngày (Hôm nay · 7 ngày · Tháng này) ══════════
    /// <summary>Chip "Hôm nay": Từ = Đến = hôm nay.</summary>
    public const string PresetHomNay = "hom-nay";
    /// <summary>Chip "7 ngày": 7 ngày GẦN NHẤT tính CẢ hôm nay (hôm nay − 6 → hôm nay).</summary>
    public const string PresetBayNgay = "7-ngay";
    /// <summary>Chip "Tháng này": ngày 1 của tháng → hôm nay (= mặc định lúc mở màn).</summary>
    public const string PresetThangNay = "thang-nay";

    /// <summary>Đang set 2 mốc ngày theo chip ⇒ setter FromDate/ToDate KHÔNG vẽ lại (vẽ MỘT lần sau khi đặt
    /// xong cả hai) và KHÔNG xoá dấu chip đang chọn.</summary>
    private bool _dangDatPreset;

    public OrderStatisticsViewModel(AppServices services)
    {
        _services = services;
        _services.OrdersChanged += OnOrdersChanged;
        var today = DateTime.Today;
        _ngayCoiLaHomNay = today;
        _fromDate = new DateTime(today.Year, today.Month, 1);
        _toDate = today;
        Reload();

        // App chạy VÒNG LIÊN TỤC CẢ ĐÊM: không có nhịp này thì qua nửa đêm chip "Hôm nay"/"7 ngày"/"Tháng này"
        // vẫn sáng nhưng khoảng ngày đứng ở HÔM QUA — người dùng đọc số cũ mà tưởng số của ngày mới. Callback
        // chạy trên thread nền nên tự marshal về UI thread (xem NhipSangNgay).
        _timerSangNgay = new System.Threading.Timer(_ => NhipSangNgay(), null, NhipDoSangNgay, NhipDoSangNgay);
    }

    /// <summary>Dọn nhịp dò sang ngày + gỡ đăng ký <c>OrdersChanged</c>. VM sống suốt vòng đời app (dựng một lần
    /// trong <see cref="MainViewModel"/>) nên điểm dọn là lúc THOÁT app — <c>OrdersModuleHost.StopAsync</c>, đúng
    /// chỗ đang dọn timer nền của các màn con khác.</summary>
    public void Dispose()
    {
        try { _timerSangNgay.Dispose(); } catch { /* bỏ qua khi thoát */ }
        // AppServices sống suốt vòng đời app → không gỡ là VM này còn bị giữ lại (rò bộ nhớ).
        try { _services.OrdersChanged -= OnOrdersChanged; } catch { /* bỏ qua khi thoát */ }
    }

    /// <summary>Một nhịp của <see cref="_timerSangNgay"/> (THREAD NỀN) → marshal về UI thread rồi dò sang ngày.
    /// Nuốt lỗi: ngoại lệ lọt ra khỏi callback <see cref="System.Threading.Timer"/> sẽ GIẾT tiến trình, mà nhịp
    /// này chỉ là tiện ích (vd đang tắt app, không có dispatcher) — bỏ một nhịp thì nhịp sau dò lại.</summary>
    private void NhipSangNgay()
    {
        try
        {
            UiDispatch.Run(() => KiemTraSangNgay(DateTime.Today));
        }
        catch
        {
            // bỏ nhịp này
        }
    }

    /// <summary>
    /// Dò sang ngày mới rồi cho khoảng ngày TRƯỢT theo: đang dùng CHIP (<see cref="DatePreset"/> khác rỗng) thì
    /// tính lại đúng chip đó cho ngày hôm nay; <see cref="DatePreset"/> RỖNG = người dùng tự chọn ngày trên lịch
    /// → <b>TUYỆT ĐỐI không giật ngày khỏi tay họ</b>.
    /// <para>Mốc <see cref="_ngayCoiLaHomNay"/> LUÔN được cập nhật (kể cả nhánh không đổi gì) — không thì mỗi
    /// nhịp 60s của hôm sau lại chạy lại một lượt vẽ + một lượt hỏi Hub vô ích.</para>
    /// Nhận <paramref name="homNay"/> từ bên gọi (thay vì tự đọc đồng hồ) để test mô phỏng được lúc qua nửa đêm.
    /// Trả <c>true</c> khi ĐÃ tính lại khoảng ngày. Chỉ chạy trên UI thread (bên gọi đã marshal).
    /// </summary>
    internal bool KiemTraSangNgay(DateTime homNay)
    {
        var nay = homNay.Date;
        if (nay == _ngayCoiLaHomNay)
        {
            return false; // chưa sang ngày → đường nóng, không đụng gì
        }

        _ngayCoiLaHomNay = nay;

        var chip = DatePreset;
        if (string.IsNullOrEmpty(chip))
        {
            return false; // người dùng tự chọn khoảng ngày → giữ nguyên, không giật khỏi tay họ
        }

        // Truyền NGÀY MỚI xuống (không để hàm kia tự đọc đồng hồ): có vậy khoảng ngày mới thật sự trượt sang
        // hôm nay, và test mới mô phỏng được lúc qua nửa đêm.
        ApDungChipNgay(chip, nay); // tự đặt lại CẢ HAI mốc + vẽ lại đúng MỘT lượt
        return true;
    }

    public ObservableCollection<string> ShopOptions { get; } = new();
    public ObservableCollection<OrderStatisticBreakdown> StatusRows { get; } = new();
    public ObservableCollection<ShopStatisticRow> ShopRows { get; } = new();
    public ObservableCollection<OrderStatisticBreakdown> CarrierRows { get; } = new();
    public ObservableCollection<OrderStatisticBreakdown> PaymentRows { get; } = new();

    [ObservableProperty] private string? _selectedShop;
    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _emptyMessage = "Chưa có đơn hàng để thống kê.";
    [ObservableProperty] private string _scopeText = "Ảnh chụp kho đơn trên máy";
    /// <summary>Dòng chữ dưới tiêu đề: số đang xem là của MÁY NÀY hay CHUNG toàn hệ thống (chống "hỏng im lặng" —
    /// Hub lỗi mà vẫn hiện số local như thể là số chung). RỖNG = không có gì để nói (khoảng ngày không hợp lệ) →
    /// XAML ẩn hẳn dòng, đừng để header vừa báo "hãy chọn ngày" vừa khẳng định đang xem số chung.</summary>
    [ObservableProperty] private string _sourceText = SourceStandaloneText;

    /// <summary>Chú giải dài của <see cref="SourceText"/> (vì sao số máy bị hụt) — <c>null</c> = KHÔNG có tooltip
    /// (WPF không hiện khung nào khi ToolTip null; chuỗi rỗng thì lại hiện một khung trắng).</summary>
    [ObservableProperty] private string? _sourceToolTip;

    /// <summary>
    /// Lưới đang hiện số LOCAL (số của MÁY NÀY) — XAML bám cờ này để dán ghi chú "chỉ đơn CÒN trên máy" lên 3 thẻ
    /// ĐÃ GIAO / ĐÃ HỦY / DOANH THU (ba thẻ luôn hụt khi xem số máy, xem <see cref="GiaiThichKhoMayHutText"/>).
    /// <para><b>KHÔNG gộp với <see cref="_dangHienSoHub"/>:</b> cái kia là field NỘI BỘ điều khiển việc có vẽ đè số
    /// local hay không (cơ chế chống "số nhảy" mỗi lượt sync); cái này chỉ để XAML biết đang hiện nguồn nào. Gộp
    /// hai thứ là làm hỏng chống-số-nhảy.</para>
    /// </summary>
    [ObservableProperty] private bool _dangXemSoMay = true;
    [ObservableProperty] private string _totalOrdersText = "0";
    [ObservableProperty] private string _totalItemsText = "0";
    [ObservableProperty] private string _needsActionText = "0";
    [ObservableProperty] private string _deliveredText = "0";
    [ObservableProperty] private string _cancelledText = "0";
    [ObservableProperty] private string _revenueText = "₫0";
    [ObservableProperty] private string _averageOrderText = "₫0";
    [ObservableProperty] private string _trackingText = "0/0";
    [ObservableProperty] private string _estimateCoverageText = "0/0";
    [ObservableProperty] private string _lastSyncedText = "Chưa đồng bộ";

    partial void OnSelectedShopChanged(string? value)
    {
        // Báo CẢ khi đang dựng lại danh sách (_reloadingOptions): lượt Reload có thể tự tụt về "Tất cả shop", lúc
        // đó khối "HIỆU QUẢ THEO SHOP" phải hiện lại.
        OnPropertyChanged(nameof(HienLuoiShop));
        if (!_reloadingOptions)
            ApplyStatistics();
    }

    /// <summary>Có vẽ khối "HIỆU QUẢ THEO SHOP" không. Lọc ĐÚNG MỘT shop thì lưới đó chỉ còn một dòng lặp lại y các
    /// thẻ số phía trên — bỏ đi, nhường cả chiều ngang cho "PHÂN BỔ TRẠNG THÁI".</summary>
    public bool HienLuoiShop => string.IsNullOrWhiteSpace(SelectedShop) || SelectedShop == AllShopsLabel;

    partial void OnFromDateChanged(DateTime? value) => SauKhiDoiNgay();
    partial void OnToDateChanged(DateTime? value) => SauKhiDoiNgay();

    /// <summary>Đổi một mốc ngày: người dùng tự chọn trên lịch ⇒ BỎ dấu chip (khoảng ngày không còn là preset
    /// nào) rồi vẽ lại. Đang chạy <see cref="ApplyDatePreset"/> thì bỏ qua — chip đó tự vẽ một lượt duy nhất
    /// sau khi đặt xong CẢ HAI mốc (đặt lần lượt sẽ lọt qua trạng thái Từ &gt; Đến = "khoảng không hợp lệ").</summary>
    private void SauKhiDoiNgay()
    {
        if (_dangDatPreset) return;
        DatePreset = string.Empty;
        ApplyStatistics();
    }

    /// <summary>Chip khoảng ngày đang chọn (<see cref="PresetHomNay"/>…); rỗng = người dùng tự chọn ngày trên
    /// lịch. CHỈ để tô chip đang chọn — nguồn sự thật của thống kê vẫn là FromDate/ToDate.</summary>
    [ObservableProperty] private string _datePreset = PresetThangNay;

    partial void OnDatePresetChanged(string value) => NotifyPresetFlags();

    private void NotifyPresetFlags()
    {
        OnPropertyChanged(nameof(IsPresetHomNay));
        OnPropertyChanged(nameof(IsPresetBayNgay));
        OnPropertyChanged(nameof(IsPresetThangNay));
    }

    // 3 cờ cho XAML tô chip đang chọn (view đẩy vào Tag của nút, style đọc ngược qua RelativeSource Self —
    // cùng lối với nút op của màn Workspace bên suite).
    public bool IsPresetHomNay => DatePreset == PresetHomNay;
    public bool IsPresetBayNgay => DatePreset == PresetBayNgay;
    public bool IsPresetThangNay => DatePreset == PresetThangNay;

    /// <summary>
    /// Bấm chip khoảng ngày: đặt CẢ HAI mốc rồi vẽ lại ĐÚNG MỘT lượt. Ngày lấy theo đồng hồ MÁY
    /// (<see cref="DateTime.Today"/>) — phải cùng đồng hồ với <see cref="TryBuildCreatedRange"/> (nó quy đổi
    /// mốc ngày sang UTC bằng <see cref="TimeZoneInfo.Local"/>); các máy chạy app đều để giờ Việt Nam.
    /// Tham số lạ/null → không làm gì (không đổi khoảng đang xem).
    /// </summary>
    [RelayCommand]
    private void ApplyDatePreset(string? preset) => ApDungChipNgay(preset, DateTime.Today);

    /// <summary>
    /// LÕI của chip khoảng ngày — nhận mốc <paramref name="homNay"/> từ bên gọi thay vì tự đọc đồng hồ, để nhịp
    /// sang ngày (<see cref="KiemTraSangNgay"/>) truyền ngày MỚI xuống được và để test mô phỏng được lúc qua nửa
    /// đêm. Người bấm chip trên màn đi qua <see cref="ApplyDatePresetCommand"/> nên vẫn dùng đồng hồ máy.
    /// </summary>
    internal void ApDungChipNgay(string? preset, DateTime homNay)
    {
        var today = homNay.Date;
        DateTime from;
        switch (preset)
        {
            case PresetHomNay: from = today; break;
            case PresetBayNgay: from = today.AddDays(-6); break;
            case PresetThangNay: from = new DateTime(today.Year, today.Month, 1); break;
            default: return;
        }

        _dangDatPreset = true;
        try
        {
            FromDate = from;
            ToDate = today;
        }
        finally
        {
            _dangDatPreset = false;
        }

        DatePreset = preset!;
        // Bấm lại ĐÚNG chip đang chọn: DatePreset không đổi ⇒ SetProperty không bắn PropertyChanged, mà chip
        // vẫn cần được xác nhận là đang chọn → ép thông báo lại 3 cờ.
        NotifyPresetFlags();
        ApplyStatistics();
    }

    /// <summary>Kho đơn vừa đổi (phiên sync ghi xong — CÓ THỂ từ thread nền) → vẽ lại. Đường TỰ ĐỘNG này KHÔNG ép
    /// vẽ số local (<c>epVeLocal = false</c>): đang hiện số chung thì giữ nguyên, kẻo mỗi lượt sync là một lần
    /// "số nhảy".
    /// <para>Màn đang ẨN thì chỉ ghi cờ rồi thôi — quét kho đơn trên luồng UI + bắn HTTP lên Hub cho một màn không
    /// ai nhìn là lãng phí thuần (sự kiện này bắn sau MỖI shop của MỖI lượt sync). Lúc người dùng chọn lại màn,
    /// <c>MainViewModel</c> gọi <see cref="Reload()"/> nên số vẫn tươi.</para></summary>
    private void OnOrdersChanged()
    {
        if (!DangHienTrenMan)
        {
            _canVeLai = true;
            return;
        }

        UiDispatch.Run(() => Reload());
    }

    /// <summary>
    /// Nút "Làm mới" trên màn — tooltip hứa "Đọc lại số liệu từ kho đơn" nên phải ĐỌC THẬT: ép vẽ lại số LOCAL
    /// ngay cả khi đang hiện số chung của Hub. Không có đường ép này thì bấm nút cũng vô nghĩa (mọi lối vào đều
    /// bị <c>giuSoHub</c> chặn) và Hub báo 0 đơn là màn kẹt rỗng dù kho đơn trên máy có đơn.
    /// <para>Đường TỰ ĐỘNG (<see cref="OnOrdersChanged"/> — bắn sau mỗi shop của mỗi lượt sync) vẫn gọi
    /// <see cref="Reload()"/> KHÔNG ép, để giữ nguyên cơ chế chống "số nhảy".</para>
    /// </summary>
    [RelayCommand]
    private void LamMoi() => Reload(epVeLocal: true);

    /// <summary>
    /// Dựng lại danh sách shop rồi vẽ lại thống kê. <paramref name="epVeLocal"/> = true (chỉ nút "Làm mới") ⇒ vẽ
    /// đè số LOCAL kể cả khi đang hiện số chung; false (mặc định — đổi màn, kho đơn đổi) ⇒ giữ số chung như cũ.
    /// </summary>
    public void Reload(bool epVeLocal = false)
    {
        _canVeLai = false; // lượt vẽ này đã cuốn theo mọi thay đổi kho đơn bỏ lỡ lúc màn còn ẩn
        var previous = SelectedShop;
        _reloadingOptions = true;
        ShopOptions.Clear();
        ShopOptions.Add(AllShopsLabel);
        foreach (var shop in DanhSachShop()) ShopOptions.Add(shop);
        SelectedShop = previous is not null && ShopOptions.Contains(previous) ? previous : AllShopsLabel;
        _reloadingOptions = false;
        ApplyStatistics(epVeLocal);
    }

    /// <summary>
    /// Danh sách shop cho ComboBox lọc = shop CÒN đơn trên máy GỘP shop mà Hub từng trả số về
    /// (<see cref="_shopTuHub"/>). Không gộp thì shop đã dọn hết đơn kết thúc sẽ biến mất khỏi danh sách và bộ lọc
    /// âm thầm tụt về "Tất cả shop" ngay giữa lúc người dùng đang xem shop đó bằng SỐ CHUNG.
    /// <para>Trùng tên KHÔNG phân biệt hoa/thường; giữ cách viết của MÁY (đứng trước trong chuỗi ghép) rồi sắp theo
    /// đúng comparer dùng cho mọi thứ tự khác của màn.</para>
    /// </summary>
    private IEnumerable<string> DanhSachShop()
        => _services.Orders.AllShopLogins()
            .Concat(_shopTuHub)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>
    /// Nhớ các shop Hub vừa trả về và bổ sung NGAY vào <see cref="ShopOptions"/> những shop còn thiếu (chèn đúng
    /// chỗ theo thứ tự, không dựng lại cả danh sách — dựng lại là ComboBox nhả mất lựa chọn đang có).
    /// <para><b>Bắt buộc bật <see cref="_reloadingOptions"/>:</b> quên là mỗi lần thêm mục lại kích
    /// <c>OnSelectedShopChanged</c> → <c>ApplyStatistics</c> → hỏi Hub → thêm mục… (vòng vẽ lại vô tận).</para>
    /// </summary>
    private void GopShopTuHub(IEnumerable<string> shopsTuHub)
    {
        foreach (var shop in shopsTuHub)
        {
            var ten = shop?.Trim();
            if (!string.IsNullOrEmpty(ten)) _shopTuHub.Add(ten);
        }

        var thieu = _shopTuHub
            .Where(x => !ShopOptions.Contains(x, StringComparer.CurrentCultureIgnoreCase))
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (thieu.Count == 0) return;

        var dangChon = SelectedShop;
        _reloadingOptions = true;
        try
        {
            foreach (var shop in thieu) ShopOptions.Insert(ViTriChenShop(shop), shop);
            // ComboBox có thể nhả SelectedItem khi ItemsSource đổi → giữ nguyên lựa chọn của người dùng.
            if (!string.Equals(SelectedShop, dangChon, StringComparison.Ordinal)) SelectedShop = dangChon;
        }
        finally
        {
            _reloadingOptions = false;
        }
    }

    /// <summary>Chỗ chèn một shop mới cho <see cref="ShopOptions"/> giữ được thứ tự A→Z. Mục 0 là
    /// "Tất cả shop" — LUÔN đứng đầu, không bao giờ chèn trước nó.</summary>
    private int ViTriChenShop(string shop)
    {
        for (var i = 1; i < ShopOptions.Count; i++)
        {
            if (StringComparer.CurrentCultureIgnoreCase.Compare(ShopOptions[i], shop) > 0) return i;
        }

        return ShopOptions.Count;
    }

    /// <summary>
    /// Vẽ lại tab Thống kê. Số LOCAL vẽ NGAY (đồng bộ — không chặn luồng UI dù Hub chậm/chết), rồi mới hỏi Hub ở
    /// NỀN; có số chung thì thay vào. Mỗi lượt mang một số thứ tự để kết quả Hub về muộn của lượt cũ không đè lượt mới.
    /// <para><paramref name="epVeLocal"/> = true (nút "Làm mới") ⇒ bỏ qua cơ chế giữ số Hub, vẽ đè số local.</para>
    /// </summary>
    private void ApplyStatistics(bool epVeLocal = false)
    {
        var requestId = ++_statsRequestId; // luôn ở luồng UI (đổi ngày/shop/Reload)
        var shop = string.IsNullOrWhiteSpace(SelectedShop) || SelectedShop == AllShopsLabel
            ? null
            : SelectedShop;

        if (!TryBuildCreatedRange(FromDate, ToDate, out var range, out var invalidMessage))
        {
            _dangHienSoHub = false;
            ResetStatistics();
            HasData = false;
            EmptyMessage = invalidMessage;
            ScopeText = invalidMessage;
            // Chưa lọc được gì thì KHÔNG có nguồn số nào để nói — để nguyên dòng cũ là header vừa bảo "hãy chọn
            // ngày" vừa khẳng định "Số chung toàn hệ thống (từ Hub)". Rỗng ⇒ XAML ẩn hẳn dòng.
            SourceText = string.Empty;
            SourceToolTip = null;
            return;
        }

        // Đang hiện số chung ĐÚNG shop + ĐÚNG khoảng ngày này → GIỮ nguyên lưới, chỉ hỏi lại Hub. Vẽ local đè ở đây
        // là nguồn của "số nhảy": mỗi lượt sync bắn OrdersChanged → số tụt về số máy rồi lại vọt lên số chung.
        // Trừ khi người dùng CHỦ ĐỘNG bấm "Làm mới" — lúc đó họ đang đòi đọc lại kho đơn, phải chiều.
        var giuSoHub = !epVeLocal
            && _dangHienSoHub
            && string.Equals(_shopSoHub, shop, StringComparison.Ordinal)
            && _rangeSoHub.Equals(range);
        if (!giuSoHub)
        {
            ApplyLocal(shop, range);
        }

        // Có hook Hub → hỏi số CHUNG ở nền (fire-and-forget); không có → app chạy độc lập, giữ số máy này.
        if (_services.QueryOrderStatistics is { } query)
        {
            _ = LoadSharedStatisticsAsync(query, requestId, shop, range);
        }
    }

    /// <summary>Gom số từ kho đơn TRÊN MÁY NÀY (đồng bộ) và vẽ lên màn — đường mặc định, luôn chạy trước.</summary>
    private void ApplyLocal(string? shop, CreatedRange range)
    {
        // Có hook Hub = lượt hỏi sắp bắn NGAY sau đây → "đang hỏi", KHÔNG phải "Hub không phản hồi" (chưa hỏi xong
        // thì chưa có quyền kết luận hub chết). Dòng "không phản hồi" chỉ đặt khi lượt hỏi thực sự trả null.
        _dangHienSoHub = false;
        // Đặt cờ ở ĐẦU hàm (không phải cuối): nhánh kho rỗng bên dưới return sớm, mà XAML vẫn cần biết đang xem
        // nguồn nào. Đây là cờ CHO XAML — khác hẳn _dangHienSoHub ở trên (điều khiển việc vẽ đè), đừng gộp.
        DangXemSoMay = true;

        var rows = _services.Orders.Query(
            shopLogin: shop,
            shopExact: shop is not null,
            createdFromUtc: range.CreatedFromUtc,
            createdBeforeUtc: range.CreatedBeforeUtc);
        // Nhớ lại số đơn của ĐÚNG (shop, khoảng) này để lượt Hub trả lời còn đối chiếu được (ca Hub báo 0 đơn).
        _soDonLocalLuotNay = rows.Count;
        _shopLocalLuotNay = shop;
        _rangeLocalLuotNay = range;
        HasData = rows.Count > 0;
        // Dòng nguồn đặt SAU khi biết kho có đơn hay không: vế cảnh báo trỏ vào 3 THẺ SỐ, mà kho rỗng thì bên dưới
        // chỉ có thẻ "chưa có dữ liệu" — dọa hụt số ở đó là trỏ vào chỗ trống.
        DatDongNguonLocal(ChayDocLap() ? SourceStandaloneText : SourceDangHoiText, rows.Count > 0);
        EmptyMessage = rows.Count > 0
            ? string.Empty
            : BuildEmptyMessage(shop, range.FromLocalDate, range.ToLocalDate, PhamViMay);
        ScopeText = BuildScopeText(rows.Count, shop, range.FromLocalDate, range.ToLocalDate, PhamViMay);

        if (rows.Count == 0)
        {
            ResetStatistics();
            return;
        }

        var cancelled = rows.Where(IsCancelled).ToList();
        var active = rows.Where(r => !IsCancelled(r)).ToList();
        var revenue = active.Sum(RevenueOf);
        var withTracking = rows.Count(r => !string.IsNullOrWhiteSpace(r.TrackingNumber));
        var withFinalAmount = active.Count(r => r.FinalAmount is not null);

        TotalOrdersText = Number(rows.Count);
        TotalItemsText = Number(rows.Sum(r => Math.Max(0, r.ItemCount)));
        NeedsActionText = Number(rows.Count(r => !IsCancelled(r) && ShopeeShippingNav.LaChuanBiHang(r.Status)));
        DeliveredText = Number(rows.Count(r => !IsCancelled(r) && ShopeeShippingNav.LaDaGiaoDaBan(r.Status)));
        CancelledText = Number(cancelled.Count);
        RevenueText = Money(revenue);
        AverageOrderText = Money(active.Count == 0 ? 0 : revenue / active.Count);
        TrackingText = $"{Number(withTracking)}/{Number(rows.Count)} đơn";
        EstimateCoverageText = $"{Number(withFinalAmount)}/{Number(active.Count)} đơn hiệu lực";
        var lastSynced = rows.Where(r => r.SyncedAt != default).Select(r => r.SyncedAt).DefaultIfEmpty().Max();
        LastSyncedText = lastSynced == default
            ? "Chưa đồng bộ"
            : lastSynced.ToLocalTime().ToString("dd/MM/yyyy HH:mm", VnCulture);

        Replace(StatusRows, BuildBreakdown(rows, r => Clean(r.Status, "Chưa rõ"), true));
        Replace(ShopRows, BuildShopRows(rows));
        Replace(CarrierRows, BuildBreakdown(rows, r => Clean(r.Carrier ?? r.Channel, "Chưa rõ"), false));
        Replace(PaymentRows, BuildBreakdown(rows, r => Clean(r.PaymentMethod, "Chưa rõ"), false));
    }

    private void ResetStatistics()
    {
        TotalOrdersText = "0";
        TotalItemsText = "0";
        NeedsActionText = "0";
        DeliveredText = "0";
        CancelledText = "0";
        RevenueText = "₫0";
        AverageOrderText = "₫0";
        TrackingText = "0/0 đơn";
        EstimateCoverageText = "0/0 đơn hiệu lực";
        LastSyncedText = "Chưa đồng bộ";
        Replace(StatusRows, Array.Empty<OrderStatisticBreakdown>());
        Replace(ShopRows, Array.Empty<ShopStatisticRow>());
        Replace(CarrierRows, Array.Empty<OrderStatisticBreakdown>());
        Replace(PaymentRows, Array.Empty<OrderStatisticBreakdown>());
    }

    /// <summary>
    /// Hỏi Hub số CHUNG ở NỀN rồi thay vào màn. KHÔNG chặn luồng UI (đây là lỗi cũ: <c>GetAwaiter().GetResult()</c>
    /// trên đường HTTP timeout 8s). Kết quả về được marshal lên luồng UI và chỉ áp khi <paramref name="requestId"/>
    /// vẫn là lượt mới nhất.
    /// <para>Lượt hỏi trả <c>null</c> (hub lỗi/offline) → GIỮ nguyên lưới đang hiện, chỉ đổi DÒNG NGUỒN cho đúng
    /// sự thật: đang hiện số local → "Hub không phản hồi"; đang giữ số chung của lượt trước → nói rõ là số CŨ.</para>
    /// </summary>
    private async Task LoadSharedStatisticsAsync(
        Func<DateTime, DateTime, string?, CancellationToken, Task<SharedOrderStatistics?>> query,
        int requestId, string? shop, CreatedRange range)
    {
        SharedOrderStatistics? shared;
        try
        {
            shared = await query(range.CreatedFromUtc, range.CreatedBeforeUtc, shop, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            shared = null; // hub lỗi/offline → dòng nguồn bên dưới nói rõ, KHÔNG đổi số đang hiện
        }

        UiDispatch.Run(() => ApDungKetQuaHub(shared, requestId, shop, range));
    }

    /// <summary>Áp kết quả một lượt hỏi Hub (trên luồng UI): có số → vẽ số chung; null → chỉ sửa dòng nguồn.</summary>
    private void ApDungKetQuaHub(SharedOrderStatistics? shared, int requestId, string? shop, CreatedRange range)
    {
        if (requestId != _statsRequestId)
        {
            return; // lượt cũ về muộn (người dùng đã đổi ngày/shop) → bỏ, không đè lượt mới
        }

        if (shared is null)
        {
            if (_dangHienSoHub)
            {
                SourceText = SourceSharedStaleText;
                SourceToolTip = null;
            }
            else
            {
                DatDongNguonLocal(NguonSoLocalText(), HasData);
            }

            return;
        }

        ApplyShared(shared, requestId, shop, range);
    }

    /// <summary>
    /// Máy này đang chạy ĐỘC LẬP (chưa nối Hub) hay không. Hook <c>QueryOrderStatistics</c> KHÔNG đủ để kết luận:
    /// shell suite rót nó VÔ ĐIỀU KIỆN, nên máy chưa cấu hình Hub vẫn có hook và bị màn tố "Hub không phản hồi".
    /// Cờ thật nằm ở <see cref="AppServices.HubDaCauHinh"/> — đọc TƯƠI mỗi lần (người dùng có thể cấu hình Hub
    /// rồi kết nối lại giữa chừng); hook đó null = không ai biết → coi như CÓ hub, giữ nguyên hành vi cũ.
    /// </summary>
    private bool ChayDocLap()
        => _services.QueryOrderStatistics is null || _services.HubDaCauHinh?.Invoke() == false;

    /// <summary>Câu NỀN của dòng nguồn khi đang hiện số LOCAL và lượt hỏi Hub đã kết thúc mà không có số: máy CHƯA
    /// cấu hình Hub thì nói "chạy độc lập" (không có gì để tố), có Hub mà im lặng thì nói thẳng Hub không phản hồi.</summary>
    private string NguonSoLocalText() => ChayDocLap() ? SourceStandaloneText : SourceLocalText;

    /// <summary>
    /// Đặt dòng nguồn cho số LOCAL: câu nền + vế cảnh báo NGẮN, bản giải thích dài đẩy vào ToolTip. Chỉ ghép cảnh
    /// báo khi <paramref name="coTheSo"/> — màn đang là thẻ "chưa có dữ liệu" mà vẫn nói "ĐÃ GIAO / ĐÃ HỦY /
    /// DOANH THU bị HỤT" là đang trỏ vào những thẻ KHÔNG tồn tại (nghiệm thu đợt 1 bắt được).
    /// </summary>
    private void DatDongNguonLocal(string cauNen, bool coTheSo)
    {
        SourceText = coTheSo ? cauNen + CanhBaoKhoMayHutNganText : cauNen;
        SourceToolTip = coTheSo ? GiaiThichKhoMayHutText : null;
    }

    private void ApplyShared(SharedOrderStatistics shared, int requestId, string? shop, CreatedRange range)
    {
        if (requestId != _statsRequestId)
        {
            return; // lượt cũ về muộn (người dùng đã đổi ngày/shop) → bỏ, không đè lượt mới
        }

        // Hub nói 0 đơn mà kho máy VỪA đọc ra đơn cho ĐÚNG shop + khoảng này → hai bên nói ngược nhau. Vẽ đè màn
        // rỗng của Hub lên số máy đang có là hỏng im lặng (bấm "Làm mới" thấy số nháy một cái rồi trắng màn).
        // GIỮ nguyên lưới số máy, chỉ đổi dòng nguồn cho đúng sự thật; _dangHienSoHub giữ false nên lượt vẽ sau
        // vẫn đọc lại kho máy như thường.
        if (shared.TotalOrders == 0
            && _soDonLocalLuotNay > 0
            && string.Equals(_shopLocalLuotNay, shop, StringComparison.Ordinal)
            && _rangeLocalLuotNay.Equals(range))
        {
            DatDongNguonLocal(string.Format(VnCulture, SourceHubBaoRongFormat, Number(_soDonLocalLuotNay)), HasData);
            return;
        }

        // Shop chỉ CÒN sống trên Hub (máy đã dọn hết đơn kết thúc của nó) vẫn phải nằm trong ô lọc — nếu không,
        // lượt Reload sau sẽ đá bộ lọc về "Tất cả shop" giữa lúc người dùng đang xem đúng shop đó.
        GopShopTuHub(shared.ShopRows.Select(x => x.Shop));

        SourceText = SourceSharedText;
        SourceToolTip = null; // số chung đủ lịch sử → không còn gì để giải thích
        DangXemSoMay = false; // lưới sắp mang số CHUNG → gỡ ghi chú "chỉ đơn CÒN trên máy" khỏi 3 thẻ
        // Nhớ "đang hiện số chung của (shop, khoảng) này" → lượt vẽ kế tiếp cùng shop/khoảng khỏi vẽ đè số local.
        _dangHienSoHub = true;
        _shopSoHub = shop;
        _rangeSoHub = range;
        HasData = shared.TotalOrders > 0;
        EmptyMessage = shared.TotalOrders > 0
            ? string.Empty
            : BuildEmptyMessage(shop, range.FromLocalDate, range.ToLocalDate, PhamViHeThong);
        ScopeText = BuildScopeText(shared.TotalOrders, shop, range.FromLocalDate, range.ToLocalDate, PhamViHeThong);
        TotalOrdersText = Number(shared.TotalOrders);
        TotalItemsText = Number(shared.TotalItems);
        NeedsActionText = Number(shared.NeedsAction);
        DeliveredText = Number(shared.Delivered);
        CancelledText = Number(shared.Cancelled);
        RevenueText = Money(shared.Revenue);
        AverageOrderText = Money(shared.AverageOrder);
        // Chuỗi hiển thị dựng TẠI ĐÂY: hub trả số thô vì máy chủ chạy giờ UTC, không biết định dạng của máy này.
        TrackingText = $"{Number(shared.WithTracking)}/{Number(shared.TotalOrders)} đơn";
        EstimateCoverageText = $"{Number(shared.WithFinalAmount)}/{Number(shared.ActiveOrders)} đơn hiệu lực";
        LastSyncedText = shared.LastSyncedUtc is { } lastSynced
            ? lastSynced.ToLocalTime().ToString("dd/MM/yyyy HH:mm", VnCulture)
            : "Chưa đồng bộ";

        // 4 lưới đều SẮP LẠI ở client: hub sắp theo Ordinal ("Giao hàng" trước "Đã giao"), số local sắp theo
        // CurrentCultureIgnoreCase — cùng một màn mà hai nguồn cho hai thứ tự khác nhau thì người dùng tưởng dữ
        // liệu đổi. Sắp ở đây chứ KHÔNG sửa hub: client cũ vẫn đang chạy với hợp đồng hiện tại.
        Replace(StatusRows, SapNhuSoLocal(shared.StatusRows.Select(x => new OrderStatisticBreakdown(
            x.Label,
            x.OrderCount,
            Number(x.OrderCount),
            x.Value == 0 ? string.Empty : Money((long)x.Value),
            x.Percentage,
            x.Percentage.ToString("0.#", VnCulture) + "%"))));
        Replace(ShopRows, SapNhuSoLocal(shared.ShopRows.Select(x => new ShopStatisticRow(
            x.Shop,
            x.OrderCount,
            x.ItemCount,
            Money((long)x.Revenue),
            Money((long)x.Average),
            x.TrackingRate.ToString("0.#", VnCulture) + "%"))));
        Replace(CarrierRows, SapNhuSoLocal(shared.CarrierRows.Select(x => new OrderStatisticBreakdown(
            x.Label,
            x.OrderCount,
            Number(x.OrderCount),
            string.Empty,
            x.Percentage,
            x.Percentage.ToString("0.#", VnCulture) + "%"))));
        Replace(PaymentRows, SapNhuSoLocal(shared.PaymentRows.Select(x => new OrderStatisticBreakdown(
            x.Label,
            x.OrderCount,
            Number(x.OrderCount),
            string.Empty,
            x.Percentage,
            x.Percentage.ToString("0.#", VnCulture) + "%"))));
    }

    /// <summary>Sắp dòng Hub theo ĐÚNG luật của số local: nhiều đơn trước, bằng nhau thì theo nhãn (so sánh theo
    /// văn hoá, bỏ qua hoa/thường) — xem <see cref="BuildBreakdown"/>.</summary>
    private static IEnumerable<OrderStatisticBreakdown> SapNhuSoLocal(IEnumerable<OrderStatisticBreakdown> rows)
        => rows.OrderByDescending(x => x.OrderCount).ThenBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>Bản cho lưới shop của <see cref="SapNhuSoLocal(IEnumerable{OrderStatisticBreakdown})"/> — cùng luật
    /// với <see cref="BuildShopRows"/>.</summary>
    private static IEnumerable<ShopStatisticRow> SapNhuSoLocal(IEnumerable<ShopStatisticRow> rows)
        => rows.OrderByDescending(x => x.OrderCount).ThenBy(x => x.Shop, StringComparer.CurrentCultureIgnoreCase);

    private static bool TryBuildCreatedRange(DateTime? fromDate, DateTime? toDate,
        out CreatedRange range, out string invalidMessage)
    {
        if (!fromDate.HasValue || !toDate.HasValue)
        {
            range = default;
            invalidMessage = "Hãy chọn đầy đủ Từ ngày và Đến ngày để xem thống kê.";
            return false;
        }

        var fromLocalDate = fromDate.Value.Date;
        var toLocalDate = toDate.Value.Date;
        if (fromLocalDate > toLocalDate)
        {
            range = default;
            invalidMessage =
                $"Khoảng ngày không hợp lệ: \"Từ ngày\" phải nhỏ hơn hoặc bằng \"Đến ngày\" ({FormatDate(fromLocalDate)} - {FormatDate(toLocalDate)}).";
            return false;
        }

        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(fromLocalDate, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        var toExclusiveUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(toLocalDate.AddDays(1), DateTimeKind.Unspecified), TimeZoneInfo.Local);

        range = new CreatedRange(fromLocalDate, toLocalDate, fromUtc, toExclusiveUtc);
        invalidMessage = string.Empty;
        return true;
    }

    /// <summary>Chỗ đơn được ghi nhận lần đầu, dùng trong câu mô tả phạm vi — số local đếm theo mốc trên MÁY NÀY,
    /// số chung đếm theo mốc trên HỆ THỐNG (hub). Hai mốc cùng nghĩa "lần đầu thấy đơn", khác chỗ ghi nhận.</summary>
    private const string PhamViMay = "trên máy";
    private const string PhamViHeThong = "trên hệ thống";

    private static string BuildScopeText(int count, string? shop, DateTime fromLocalDate, DateTime toLocalDate,
        string phamVi)
    {
        var period = $"từ {FormatDate(fromLocalDate)} đến {FormatDate(toLocalDate)}";
        return shop is null
            ? $"Đơn được ghi nhận lần đầu {phamVi} {period}: {Number(count)} đơn"
            : $"Đơn của shop {shop} được ghi nhận lần đầu {phamVi} {period}: {Number(count)} đơn";
    }

    private static string BuildEmptyMessage(string? shop, DateTime fromLocalDate, DateTime toLocalDate, string phamVi)
    {
        var period = $"từ {FormatDate(fromLocalDate)} đến {FormatDate(toLocalDate)}";
        return shop is null
            ? $"Không có đơn nào được ghi nhận lần đầu {phamVi} {period}. Hãy đổi ngày hoặc chạy đồng bộ Shopee."
            : $"Shop {shop} không có đơn nào được ghi nhận lần đầu {phamVi} {period}.";
    }

    private static IEnumerable<OrderStatisticBreakdown> BuildBreakdown(
        IReadOnlyCollection<OrderRow> rows, Func<OrderRow, string> selector, bool includeRevenue)
    {
        var total = rows.Count;
        return rows.GroupBy(selector, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var count = g.Count();
                var percent = total == 0 ? 0 : count * 100d / total;
                // Tổng = 0 ⇒ ô "Ước tính" để RỖNG, KHÔNG in "₫0": đường Hub đã bỏ trống ô này khi hub trả 0
                // (xem ApplyShared), hai nguồn phải cùng một luật hiển thị.
                var revenue = includeRevenue ? g.Where(r => !IsCancelled(r)).Sum(RevenueOf) : 0;
                var value = revenue == 0 ? string.Empty : Money(revenue);
                return new OrderStatisticBreakdown(g.Key, count, Number(count), value, percent,
                    percent.ToString("0.#", VnCulture) + "%");
            })
            .OrderByDescending(x => x.OrderCount)
            .ThenBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase);
    }

    private static IEnumerable<ShopStatisticRow> BuildShopRows(IReadOnlyCollection<OrderRow> rows)
        => rows.GroupBy(r => Clean(r.ShopLogin, "(shop chưa xác định)"), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var list = g.ToList();
                var active = list.Where(r => !IsCancelled(r)).ToList();
                var revenue = active.Sum(RevenueOf);
                var tracked = list.Count(r => !string.IsNullOrWhiteSpace(r.TrackingNumber));
                var rate = list.Count == 0 ? 0 : tracked * 100d / list.Count;
                return new ShopStatisticRow(g.Key, list.Count, list.Sum(r => Math.Max(0, r.ItemCount)),
                    Money(revenue), Money(active.Count == 0 ? 0 : revenue / active.Count),
                    rate.ToString("0.#", VnCulture) + "%");
            })
            .OrderByDescending(x => x.OrderCount)
            .ThenBy(x => x.Shop, StringComparer.CurrentCultureIgnoreCase);

    private static bool IsCancelled(OrderRow row)
        => ShopeeShippingNav.LaDonHuy(row.Status, row.StatusDescription, row.CancelReason);

    private static string FormatDate(DateTime value) => value.ToString("dd/MM/yyyy", VnCulture);
    private static long RevenueOf(OrderRow row) => Math.Max(0, row.FinalAmount ?? row.TotalPrice ?? 0);
    private static string Clean(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static string Number(long value) => value.ToString("N0", VnCulture);
    private static string Money(long value) => "₫" + value.ToString("N0", VnCulture);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    private readonly record struct CreatedRange(
        DateTime FromLocalDate,
        DateTime ToLocalDate,
        DateTime CreatedFromUtc,
        DateTime CreatedBeforeUtc);
}
