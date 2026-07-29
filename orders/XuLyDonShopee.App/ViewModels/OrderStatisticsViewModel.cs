using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>Một dòng phân bổ dùng chung cho trạng thái, vận chuyển và thanh toán.</summary>
public sealed record OrderStatisticBreakdown(string Label, int OrderCount, string OrderCountText,
    string ValueText, double Percentage, string PercentageText);

/// <summary>Một dòng hiệu quả của shop trong ảnh chụp kho đơn hiện tại.</summary>
public sealed record ShopStatisticRow(string Shop, int OrderCount, int ItemCount, string RevenueText,
    string AverageText, string TrackingRateText);

/// <summary>
/// Dashboard thống kê kho đơn cục bộ. Đây là ảnh chụp vận hành của các đơn còn được lưu trên máy;
/// doanh thu ước tính bỏ đơn hủy, ưu tiên <c>final_amount</c> và dùng <c>total_price</c> khi chưa có số cuối cùng.
/// </summary>
public partial class OrderStatisticsViewModel : ViewModelBase
{
    public const string AllShopsLabel = "Tất cả shop";
    private static readonly CultureInfo VnCulture = CultureInfo.GetCultureInfo("vi-VN");
    private readonly AppServices _services;
    private bool _reloadingOptions;

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
        if (Dispatcher.UIThread.CheckAccess()) Reload();
        else Dispatcher.UIThread.Post(Reload);
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

    private void ApplyStatistics()
    {
        var shop = string.IsNullOrWhiteSpace(SelectedShop) || SelectedShop == AllShopsLabel
            ? null
            : SelectedShop;

        if (!TryBuildCreatedRange(FromDate, ToDate, out var range, out var invalidMessage))
        {
            ResetStatistics();
            HasData = false;
            EmptyMessage = invalidMessage;
            ScopeText = invalidMessage;
            return;
        }

        var rows = _services.Orders.Query(
            shopLogin: shop,
            shopExact: shop is not null,
            createdFromUtc: range.CreatedFromUtc,
            createdBeforeUtc: range.CreatedBeforeUtc);
        HasData = rows.Count > 0;
        EmptyMessage = rows.Count > 0
            ? string.Empty
            : BuildEmptyMessage(shop, range.FromLocalDate, range.ToLocalDate);
        ScopeText = BuildScopeText(rows.Count, shop, range.FromLocalDate, range.ToLocalDate);

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

    private static string BuildScopeText(int count, string? shop, DateTime fromLocalDate, DateTime toLocalDate)
    {
        var period = $"từ {FormatDate(fromLocalDate)} đến {FormatDate(toLocalDate)}";
        return shop is null
            ? $"Đơn được ghi nhận lần đầu trên máy {period}: {Number(count)} đơn"
            : $"Đơn của shop {shop} được ghi nhận lần đầu trên máy {period}: {Number(count)} đơn";
    }

    private static string BuildEmptyMessage(string? shop, DateTime fromLocalDate, DateTime toLocalDate)
    {
        var period = $"từ {FormatDate(fromLocalDate)} đến {FormatDate(toLocalDate)}";
        return shop is null
            ? $"Không có đơn nào được ghi nhận lần đầu trên máy {period}. Hãy đổi ngày hoặc chạy đồng bộ Shopee."
            : $"Shop {shop} không có đơn nào được ghi nhận lần đầu trên máy {period}.";
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
