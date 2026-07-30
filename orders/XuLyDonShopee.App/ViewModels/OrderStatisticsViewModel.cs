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
public partial class OrderStatisticsViewModel : ViewModelBase
{
    public const string AllShopsLabel = "Tất cả shop";
    /// <summary>Lượt hỏi Hub CÒN ĐANG BAY — số đang hiện là số local vẽ tạm. KHÔNG được nói "Hub không phản hồi"
    /// ở trạng thái này: lượt hỏi chưa xong thì chưa biết hub sống hay chết (đó là lỗi cũ — mỗi lần đổi ngày là
    /// hiện một dòng cáo buộc hub chết trong khi hub vẫn đang trả lời).</summary>
    private const string SourceDangHoiText = "Số trên MÁY NÀY — đang hỏi Hub số chung…";
    private const string SourceLocalText = "Số trên MÁY NÀY — Hub không phản hồi nên chưa gộp được số chung.";
    private const string SourceStandaloneText = "Số trên MÁY NÀY (app chạy độc lập, chưa nối Hub).";
    private const string SourceSharedText = "Số chung toàn hệ thống (từ Hub).";
    /// <summary>Đang GIỮ số chung của lượt hỏi trước mà lượt hỏi mới nhất không về được — nói thẳng thay vì lẳng
    /// lặng để nguyên dòng "Số chung (Hub)" như thể vừa cập nhật.</summary>
    private const string SourceSharedStaleText = "Số chung (Hub) của lượt hỏi trước — lượt này Hub không phản hồi.";
    private static readonly CultureInfo VnCulture = CultureInfo.GetCultureInfo("vi-VN");
    private readonly AppServices _services;
    private bool _reloadingOptions;

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

    public OrderStatisticsViewModel(AppServices services)
    {
        _services = services;
        _services.OrdersChanged += OnOrdersChanged;
        var today = DateTime.Today;
        _fromDate = new DateTime(today.Year, today.Month, 1);
        _toDate = today;
        Reload();
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
    /// Hub lỗi mà vẫn hiện số local như thể là số chung).</summary>
    [ObservableProperty] private string _sourceText = SourceStandaloneText;
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
        if (!_reloadingOptions)
            ApplyStatistics();
    }

    partial void OnFromDateChanged(DateTime? value) => ApplyStatistics();
    partial void OnToDateChanged(DateTime? value) => ApplyStatistics();

    private void OnOrdersChanged()
    {
        UiDispatch.Run(Reload);
    }

    [RelayCommand]
    public void Reload()
    {
        var previous = SelectedShop;
        _reloadingOptions = true;
        ShopOptions.Clear();
        ShopOptions.Add(AllShopsLabel);
        foreach (var shop in _services.Orders.AllShopLogins()) ShopOptions.Add(shop);
        SelectedShop = previous is not null && ShopOptions.Contains(previous) ? previous : AllShopsLabel;
        _reloadingOptions = false;
        ApplyStatistics();
    }

    /// <summary>
    /// Vẽ lại tab Thống kê. Số LOCAL vẽ NGAY (đồng bộ — không chặn luồng UI dù Hub chậm/chết), rồi mới hỏi Hub ở
    /// NỀN; có số chung thì thay vào. Mỗi lượt mang một số thứ tự để kết quả Hub về muộn của lượt cũ không đè lượt mới.
    /// </summary>
    private void ApplyStatistics()
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
            return;
        }

        // Đang hiện số chung ĐÚNG shop + ĐÚNG khoảng ngày này → GIỮ nguyên lưới, chỉ hỏi lại Hub. Vẽ local đè ở đây
        // là nguồn của "số nhảy": mỗi lượt sync bắn OrdersChanged → số tụt về số máy rồi lại vọt lên số chung.
        var giuSoHub = _dangHienSoHub
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
        SourceText = _services.QueryOrderStatistics is null ? SourceStandaloneText : SourceDangHoiText;

        var rows = _services.Orders.Query(
            shopLogin: shop,
            shopExact: shop is not null,
            createdFromUtc: range.CreatedFromUtc,
            createdBeforeUtc: range.CreatedBeforeUtc);
        HasData = rows.Count > 0;
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
            SourceText = _dangHienSoHub ? SourceSharedStaleText : SourceLocalText;
            return;
        }

        ApplyShared(shared, requestId, shop, range);
    }

    private void ApplyShared(SharedOrderStatistics shared, int requestId, string? shop, CreatedRange range)
    {
        if (requestId != _statsRequestId)
        {
            return; // lượt cũ về muộn (người dùng đã đổi ngày/shop) → bỏ, không đè lượt mới
        }

        SourceText = SourceSharedText;
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

        Replace(StatusRows, shared.StatusRows.Select(x => new OrderStatisticBreakdown(
            x.Label,
            x.OrderCount,
            Number(x.OrderCount),
            x.Value == 0 ? string.Empty : Money((long)x.Value),
            x.Percentage,
            x.Percentage.ToString("0.#", VnCulture) + "%")));
        Replace(ShopRows, shared.ShopRows.Select(x => new ShopStatisticRow(
            x.Shop,
            x.OrderCount,
            x.ItemCount,
            Money((long)x.Revenue),
            Money((long)x.Average),
            x.TrackingRate.ToString("0.#", VnCulture) + "%")));
        Replace(CarrierRows, shared.CarrierRows.Select(x => new OrderStatisticBreakdown(
            x.Label,
            x.OrderCount,
            Number(x.OrderCount),
            string.Empty,
            x.Percentage,
            x.Percentage.ToString("0.#", VnCulture) + "%")));
        Replace(PaymentRows, shared.PaymentRows.Select(x => new OrderStatisticBreakdown(
            x.Label,
            x.OrderCount,
            Number(x.OrderCount),
            string.Empty,
            x.Percentage,
            x.Percentage.ToString("0.#", VnCulture) + "%")));
    }

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
                var value = includeRevenue ? Money(g.Where(r => !IsCancelled(r)).Sum(RevenueOf)) : string.Empty;
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
