using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.BigSeller;
using Shopee.Core.Scrape;

namespace Shopee.Suite.Modules.Scrape;

/// <summary>Một chip trạng thái scrape của 1 shop (đã / đang / chưa) — hiện phía trên ô chọn shop.</summary>
public sealed class ShopScrapeStatus
{
    public string DisplayName { get; init; } = "";
    public string StatusText { get; init; } = "";
    public Brush Background { get; init; } = Brushes.Transparent;
    public Brush Foreground { get; init; } = Brushes.Black;
    public string Tooltip { get; init; } = "";
}

/// <summary>
/// Một "đích scrape" = 1 tài khoản BigSeller + 1 shop (↔ sheet/workbook). Tick chọn nhiều tk để chạy
/// LẦN LƯỢT: mỗi tk dùng CẢ kho tài khoản Shopee (xoay vòng) cào workbook của nó rồi đẩy sang đúng
/// BigSeller đó. Không chạy đồng thời vì các tk sẽ tranh nhau kho Shopee + profile (cùng tk Shopee →
/// cùng profile dir → xung đột Brave).
/// </summary>
public sealed partial class ScrapeTargetViewModel : ObservableObject
{
    public BigSellerAccount Account { get; }
    public ObservableCollection<BigSellerShop> Shops { get; }

    /// <summary>Do ScrapeViewModel cấp: TRẢ true nếu đang có job LIVE cào đúng shop (sheet) này → hiện
    /// "đang scrape". Dựa vào job sống thay vì cờ "running" trong file tiến độ (cờ này có thể kẹt sau crash).</summary>
    public Func<BigSellerShop, bool>? IsShopRunning { get; set; }

    // Khi true (đang nạp config đã lưu lúc khởi tạo) thì KHÔNG lưu lại — tránh ghi đè vòng vo.
    private bool _loading;

    [ObservableProperty] private bool _isSelected;
    partial void OnIsSelectedChanged(bool value) => Persist();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SheetName), nameof(ShopChosen), nameof(ProgressText))]
    private BigSellerShop? _selectedShop;
    partial void OnSelectedShopChanged(BigSellerShop? value) => Persist();

    // Cấu hình RIÊNG cho từng tk BigSeller (sửa ở panel detail bên phải). Được LƯU theo Account.Id
    // (ScrapeTargetConfigStore) → giữ lại qua reload + khởi động lại app.
    /// <summary>Bắt đầu từ dòng nào của sheet. Mặc định 2 vì dòng 1 là header (engine yêu cầu ≥ 2).</summary>
    [ObservableProperty] private int _startRow = 2;
    partial void OnStartRowChanged(int value) => Persist();
    /// <summary>Đến dòng nào thì DỪNG (0 = chạy hết sheet).</summary>
    [ObservableProperty] private int _endRow;
    partial void OnEndRowChanged(int value) => Persist();
    /// <summary>Số dòng mỗi khối (mỗi tk Shopee nhận 1 khối kế tiếp theo số này).</summary>
    [ObservableProperty] private int _rowsPerAccount = 60;
    partial void OnRowsPerAccountChanged(int value) => Persist();
    /// <summary>Số process chạy đồng thời = số tk Shopee acc BigSeller này dùng (mỗi process 1 tk).</summary>
    [ObservableProperty] private int _maxProcess = 2;
    partial void OnMaxProcessChanged(int value) => Persist();
    /// <summary>Số tk Shopee "đóng khung" cố định cho tk BigSeller này — chỉ xoay vòng trong khung này
    /// (cấp lúc bắt đầu chạy). Để BigSeller chỉ thấy ngần ấy thiết bị ổn định → không bị đá phiên.</summary>
    [ObservableProperty] private int _frameSize = 10;
    partial void OnFrameSizeChanged(int value) => Persist();

    public ScrapeTargetViewModel(BigSellerAccount account)
    {
        Account = account;
        Shops = new ObservableCollection<BigSellerShop>(account.Shops);

        // Nạp config đã lưu (nếu có) → giữ lựa chọn người dùng qua reload/khởi động lại.
        _loading = true;
        var saved = ScrapeTargetConfigStore.Shared.Find(account.Id);
        if (saved is not null)
        {
            StartRow = saved.StartRow;
            EndRow = saved.EndRow;
            RowsPerAccount = saved.RowsPerAccount;
            MaxProcess = saved.MaxProcess;
            FrameSize = saved.FrameSize > 0 ? saved.FrameSize : 10;
            IsSelected = saved.IsSelected;
            SelectedShop = saved.SelectedShopId is not null
                ? Shops.FirstOrDefault(s => s.Id == saved.SelectedShopId) ?? Shops.FirstOrDefault()
                : Shops.FirstOrDefault();
        }
        else
        {
            SelectedShop = Shops.FirstOrDefault();
        }
        _loading = false;
    }

    /// <summary>Lưu config hiện tại của đích này (bỏ qua khi đang nạp lúc khởi tạo).</summary>
    private void Persist()
    {
        if (_loading) return;
        ScrapeTargetConfigStore.Shared.Save(new ScrapeTargetConfig
        {
            AccountId = Account.Id,
            SelectedShopId = SelectedShop?.Id,
            IsSelected = IsSelected,
            StartRow = StartRow,
            EndRow = EndRow,
            RowsPerAccount = RowsPerAccount,
            MaxProcess = MaxProcess,
            FrameSize = FrameSize,
        });
    }

    public string DisplayName => Account.DisplayName;
    public string CookieStatus => Account.HasCookie ? "✓ cookie" : "⚠ chưa cookie";
    public string WorkbookPath => Account.WorkbookPath;
    public string SheetName => SelectedShop?.ShopeeDataSheet ?? "";
    public bool ShopChosen => SelectedShop is not null;

    /// <summary>Tóm tắt tiến độ đã lưu của (BigSeller + sheet) hiện chọn — cho nhãn ở panel chi tiết.</summary>
    public string ProgressText
    {
        get
        {
            var p = ScrapeProgressStore.Shared.Find(Account.Id, SelectedShop?.ShopeeDataSheet ?? "");
            if (p is null || (p.Completed.Count == 0 && p.LastRowReached == 0))
                return "Chưa có tiến độ.";
            var status = p.Status switch
            {
                "completed" => "✔ Hoàn thành",
                "running" => "▶ Đang chạy",
                "stopped" => "■ Dừng giữa chừng",
                _ => "—",
            };
            return $"{status} · đã xong tới dòng {p.LastRowReached}";
        }
    }

    /// <summary>Trạng thái scrape của TỪNG shop trong tk BigSeller này (đã / đang / chưa) — cho dải chip
    /// phía trên ô chọn shop. Tính lại mỗi lần <see cref="RefreshProgress"/> (job bắt đầu/xong, mỗi chunk).</summary>
    public IReadOnlyList<ShopScrapeStatus> ShopStatuses => Shops.Select(BuildShopStatus).ToList();

    private ShopScrapeStatus BuildShopStatus(BigSellerShop shop)
    {
        var sheet = shop.ShopeeDataSheet ?? "";
        if (string.IsNullOrWhiteSpace(sheet))
            return new ShopScrapeStatus
            {
                DisplayName = shop.DisplayName,
                StatusText = "○ chưa scrape",
                Background = TodoBg, Foreground = TodoFg,
                Tooltip = "Shop chưa gán sheet dữ liệu Shopee.",
            };

        // "Đang" lấy từ job LIVE (chuẩn xác, không kẹt sau crash); "Đã/Chưa" lấy từ tiến độ đã lưu.
        var live = IsShopRunning?.Invoke(shop) ?? false;
        var p = ScrapeProgressStore.Shared.Find(Account.Id, sheet);

        if (live)
            return new ShopScrapeStatus
            {
                DisplayName = shop.DisplayName,
                StatusText = "⏳ đang scrape",
                Background = RunningBg, Foreground = RunningFg,
                Tooltip = p is not null ? $"Đang chạy · đã xong tới dòng {p.LastRowReached}." : "Đang chạy…",
            };

        if (p is not null && string.Equals(p.Status, "completed", StringComparison.OrdinalIgnoreCase))
            return new ShopScrapeStatus
            {
                DisplayName = shop.DisplayName,
                StatusText = "✓ đã scrape",
                Background = DoneBg, Foreground = DoneFg,
                Tooltip = $"Hoàn thành · đã xong tới dòng {p.LastRowReached}.",
            };

        // Còn lại = chưa scrape. Nếu dở dang (đã chạy một phần) thì gợi ý "Tiếp tục".
        var hasPartial = p is not null && p.LastRowReached > 0;
        return new ShopScrapeStatus
        {
            DisplayName = shop.DisplayName,
            StatusText = hasPartial ? "◐ chưa xong" : "○ chưa scrape",
            Background = TodoBg, Foreground = TodoFg,
            Tooltip = hasPartial
                ? $"Dở dang · đã xong tới dòng {p!.LastRowReached} — bấm Tiếp tục để chạy nốt."
                : "Chưa chạy lần nào.",
        };
    }

    public void RefreshProgress()
    {
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ShopStatuses));
    }

    // Màu chip (nền nhạt + chữ đậm) cho 3 trạng thái — freeze để tái dùng, không tạo brush mỗi lần.
    private static readonly Brush DoneBg = FrozenBrush("#E8F5E9"), DoneFg = FrozenBrush("#2E7D32");
    private static readonly Brush RunningBg = FrozenBrush("#FFF3E0"), RunningFg = FrozenBrush("#E65100");
    private static readonly Brush TodoBg = FrozenBrush("#ECEFF1"), TodoFg = FrozenBrush("#546E7A");

    private static Brush FrozenBrush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }
}
