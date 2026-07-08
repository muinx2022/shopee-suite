using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.Accounts;

namespace Shopee.Suite.Modules.Accounts;

/// <summary>Màu cột "Tình trạng" theo giá trị (thay DataTrigger của WPF — Avalonia DataGrid không có ElementStyle/trigger).</summary>
internal static class UsageBrushes
{
    public static readonly IBrush Success = new ImmutableSolidColorBrush(Color.Parse("#00783C"));
    public static readonly IBrush Accent = new ImmutableSolidColorBrush(Color.Parse("#0078D7"));
    public static readonly IBrush Danger = new ImmutableSolidColorBrush(Color.Parse("#C8463C"));
    public static readonly IBrush Muted = new ImmutableSolidColorBrush(Color.Parse("#6E727A"));
}

/// <summary>Bao một <see cref="ShopeeAccount"/> để bind 2 chiều trong lưới + form chi tiết.
/// Mọi setter ghi thẳng vào model nền nên chỉ cần gọi store.Save() là lưu được.</summary>
public sealed class AccountItemViewModel : ObservableObject
{
    public ShopeeAccount Model { get; }

    public AccountItemViewModel(ShopeeAccount model) => Model = model;

    public string Label
    {
        get => Model.Label;
        set { if (Model.Label != value) { Model.Label = value; OnChanged(nameof(Label), nameof(DisplayName)); } }
    }

    public string ShopeeAccountLogin
    {
        get => Model.ShopeeAccountLogin;
        set { if (Model.ShopeeAccountLogin != value) { Model.ShopeeAccountLogin = value; OnChanged(nameof(ShopeeAccountLogin), nameof(Username), nameof(DisplayName)); } }
    }

    public bool OpenWithShopeeAccount
    {
        get => Model.OpenWithShopeeAccount;
        set { if (Model.OpenWithShopeeAccount != value) { Model.OpenWithShopeeAccount = value; OnPropertyChanged(); } }
    }

    public string KiotProxyKey
    {
        get => Model.KiotProxyKey;
        set { if (Model.KiotProxyKey != value) { Model.KiotProxyKey = value; OnChanged(nameof(KiotProxyKey), nameof(ProxySummary)); } }
    }

    public string Region
    {
        get => Model.Region;
        set { if (Model.Region != value) { Model.Region = value; OnChanged(nameof(Region), nameof(ProxySummary)); } }
    }

    public string ProxyType
    {
        get => Model.ProxyType;
        set { if (Model.ProxyType != value) { Model.ProxyType = value; OnPropertyChanged(); } }
    }

    public string ManualProxy
    {
        get => Model.ManualProxy;
        set { if (Model.ManualProxy != value) { Model.ManualProxy = value; OnChanged(nameof(ManualProxy), nameof(ProxySummary)); } }
    }

    public bool RequireProxy
    {
        get => Model.RequireProxy;
        set { if (Model.RequireProxy != value) { Model.RequireProxy = value; OnPropertyChanged(); } }
    }

    public bool Disabled
    {
        get => Model.Disabled;
        set { if (Model.Disabled != value) { Model.Disabled = value; OnPropertyChanged(); } }
    }

    public string Username => Model.Username;
    public string DisplayName => Model.DisplayName;
    public string ProxySummary => Model.ProxySummary;
    public string LastError => Model.LastError ?? "";   // lý do dính captcha/lỗi (khi xem bộ lọc "Bị lỗi")

    /// <summary>Tình trạng dùng tk theo lượt chạy hiện tại (Đang/Đã/Chưa dùng). Không chạy gì → "Chưa dùng".</summary>
    public string UsageStatus => ShopeeAccountUsage.Shared.Status(Model.Id);

    /// <summary>Màu chữ cột Tình trạng theo giá trị (xanh=đang · accent=đã · đỏ=captcha · xám=chưa).</summary>
    public IBrush UsageStatusBrush => UsageStatus switch
    {
        "Đang dùng" => UsageBrushes.Success,
        "Đã dùng" => UsageBrushes.Accent,
        "⚠ Captcha" => UsageBrushes.Danger,
        _ => UsageBrushes.Muted,
    };

    /// <summary>Báo UI làm mới cột Tình trạng (gọi khi ShopeeAccountUsage đổi).</summary>
    public void RefreshUsage() { OnPropertyChanged(nameof(UsageStatus)); OnPropertyChanged(nameof(UsageStatusBrush)); }

    private void OnChanged(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
    }
}
