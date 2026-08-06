using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Core.Scrape;
using Shopee.Suite.Infrastructure;

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

    // Cấu hình CHẠY giờ ở MỨC ACCOUNT (Account.RunConfig) — 1 bộ DÙNG CHUNG cho mọi op; các property dưới
    // là PROXY đọc/ghi thẳng RunConfig (map MaxProcess ↔ Processes). Setter LƯU xuống bigseller.json
    // (BigSellerStore); UpdateRunTargetViewModel proxy cùng object nên giá trị luôn nhất quán. RIÊNG-MÁY.
    private BigSellerRunConfig Cfg => Account.RunConfig ??= new BigSellerRunConfig();

    /// <summary>Bắt đầu từ dòng nào của sheet. Mặc định 2 vì dòng 1 là header (engine yêu cầu ≥ 2).</summary>
    public int StartRow
    {
        get => Cfg.StartRow;
        set { if (Cfg.StartRow != value) { Cfg.StartRow = value; OnPropertyChanged(); BigSellerStore.Shared.Save(); } }
    }
    /// <summary>Đến dòng nào thì DỪNG (0 = chạy hết sheet).</summary>
    public int EndRow
    {
        get => Cfg.EndRow;
        set { if (Cfg.EndRow != value) { Cfg.EndRow = value; OnPropertyChanged(); BigSellerStore.Shared.Save(); } }
    }

    /// <summary>Override khoảng dòng TẠM cho 1 lượt chạy (vd Hub giao việc) — KHÔNG persist, dùng-một-lần.
    /// null = dùng <see cref="StartRow"/>/<see cref="EndRow"/> của người dùng. Runner đọc rồi tự xoá.</summary>
    public int? PendingStartRow { get; set; }
    public int? PendingEndRow { get; set; }
    /// <summary>Override số cửa sổ / cỡ khung TẠM cho 1 lượt chạy (Hub giao việc) — KHÔNG persist, dùng-một-lần.
    /// null = dùng <see cref="MaxProcess"/>/<see cref="FrameSize"/> của người dùng. Runner đọc rồi tự xoá.</summary>
    public int? PendingMaxProcess { get; set; }
    public int? PendingFrameSize { get; set; }
    /// <summary>Override khoảng nghỉ giữa 2 link (GIÂY) TẠM cho 1 lượt chạy (Hub giao việc) — KHÔNG persist,
    /// dùng-một-lần. null = dùng <see cref="RestMinSeconds"/>/<see cref="RestMaxSeconds"/> của máy này.</summary>
    public int? PendingRestMinSeconds { get; set; }
    public int? PendingRestMaxSeconds { get; set; }
    /// <summary>Số dòng mỗi khối (mỗi tk Shopee nhận 1 khối kế tiếp theo số này).</summary>
    public int RowsPerAccount
    {
        get => Cfg.RowsPerAccount;
        set { if (Cfg.RowsPerAccount != value) { Cfg.RowsPerAccount = value; OnPropertyChanged(); BigSellerStore.Shared.Save(); } }
    }
    /// <summary>Số process chạy đồng thời = số cửa sổ Brave (áp dụng mọi op) — map RunConfig.Processes.</summary>
    public int MaxProcess
    {
        get => Cfg.Processes;
        set { if (Cfg.Processes != value) { Cfg.Processes = value; OnPropertyChanged(); BigSellerStore.Shared.Save(); } }
    }
    /// <summary>Số tk Shopee "đóng khung" cố định cho tk BigSeller này — chỉ xoay vòng trong khung này
    /// (cấp lúc bắt đầu chạy). Để BigSeller chỉ thấy ngần ấy thiết bị ổn định → không bị đá phiên.</summary>
    public int FrameSize
    {
        get => Cfg.FrameSize;
        set { if (Cfg.FrameSize != value) { Cfg.FrameSize = value; OnPropertyChanged(); BigSellerStore.Shared.Save(); } }
    }

    // ── Khoảng nghỉ giữa 2 link của scrape (GIÂY, riêng-máy). Setter TỰ HỢP LỆ HOÁ qua ScrapeRestWindow.Resolve
    //    rồi ghi CẢ CẶP: gõ min > max thì max được kéo lên theo min (và ngược lại) → ô nhập không bao giờ để lại
    //    cặp vô nghĩa cho engine. Ghi 0/để trống = về mặc định 120–240s. ──
    /// <summary>Nghỉ tối thiểu giữa 2 link (giây). 0 → mặc định 120s.</summary>
    public int RestMinSeconds
    {
        get => Cfg.RestMinSeconds;
        set => ApplyRestWindow(ScrapeRestWindow.Resolve(value, Cfg.RestMaxSeconds));
    }

    /// <summary>Nghỉ tối đa giữa 2 link (giây). 0 → mặc định 240s; nhỏ hơn min → bị kéo lên bằng min.</summary>
    public int RestMaxSeconds
    {
        get => Cfg.RestMaxSeconds;
        set => ApplyRestWindow(ScrapeRestWindow.Resolve(Cfg.RestMinSeconds, value));
    }

    private void ApplyRestWindow(ScrapeRestWindow w)
    {
        if (Cfg.RestMinSeconds == w.MinSeconds && Cfg.RestMaxSeconds == w.MaxSeconds)
        {
            // Giá trị sau hợp lệ hoá KHÔNG đổi (vd người dùng gõ 0 khi đang là mặc định) → vẫn phải raise để
            // TextBox vẽ lại con số ĐÃ hợp lệ hoá thay vì giữ chuỗi người dùng vừa gõ.
            OnPropertyChanged(nameof(RestMinSeconds));
            OnPropertyChanged(nameof(RestMaxSeconds));
            return;
        }
        Cfg.RestMinSeconds = w.MinSeconds;
        Cfg.RestMaxSeconds = w.MaxSeconds;
        OnPropertyChanged(nameof(RestMinSeconds));
        OnPropertyChanged(nameof(RestMaxSeconds));
        BigSellerStore.Shared.Save();
    }

    public ScrapeTargetViewModel(BigSellerAccount account)
    {
        Account = account;
        Shops = new ObservableCollection<BigSellerShop>(account.Shops);

        // Cấu hình CHẠY giờ ở mức account (Account.RunConfig) — migrate 1 lần từ nguồn cũ nếu chưa có; proxy
        // StartRow/EndRow/… đọc-ghi thẳng RunConfig nên KHỎI nạp vào field ở đây.
        Infrastructure.RunConfigMigration.EnsureRunConfig(account);

        // Nạp lựa chọn SHOP + tick chọn đã lưu (ScrapeTargetConfigStore CHỈ còn giữ 2 thứ này) → giữ qua reload/mở lại.
        _loading = true;
        var saved = ScrapeTargetConfigStore.Shared.Find(account.Id);
        if (saved is not null)
        {
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
                LedgerStatus.Completed => "✔ Hoàn thành",
                LedgerStatus.Running => "▶ Đang chạy",
                LedgerStatus.Stopped => "■ Dừng giữa chừng",
                _ => "—",
            };
            return $"{status} · đã xong tới dòng {p.LastRowReached}";
        }
    }

    /// <summary>Trạng thái scrape của TỪNG shop trong tk BigSeller này (đã / đang / chưa) — cho dải chip
    /// phía trên ô chọn shop. Tính lại mỗi lần <see cref="RefreshProgress"/> (job bắt đầu/xong, mỗi chunk).</summary>
    public IReadOnlyList<ShopScrapeStatus> ShopStatuses => Shops.Select(BuildShopStatus).ToList();

    /// <summary>Trạng thái scrape của ĐÚNG 1 shop (màn gộp v1.1 lấy theo từng dòng shop trong lưới).</summary>
    public ShopScrapeStatus StatusFor(BigSellerShop shop) => BuildShopStatus(shop);

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

        if (p is not null && string.Equals(p.Status, LedgerStatus.Completed, StringComparison.OrdinalIgnoreCase))
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

    private static Brush FrozenBrush(string hex) => AppBrushes.From(hex);
}
