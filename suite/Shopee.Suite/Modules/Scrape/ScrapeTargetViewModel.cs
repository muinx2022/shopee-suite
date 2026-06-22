using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.BigSeller;
using Shopee.Core.Scrape;

namespace Shopee.Suite.Modules.Scrape;

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

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SheetName), nameof(ShopChosen), nameof(ProgressText))]
    private BigSellerShop? _selectedShop;

    // Cấu hình RIÊNG cho từng tk BigSeller (sửa ở panel detail bên phải).
    /// <summary>Bắt đầu từ dòng nào của sheet. Mặc định 2 vì dòng 1 là header (engine yêu cầu ≥ 2).</summary>
    [ObservableProperty] private int _startRow = 2;
    /// <summary>Đến dòng nào thì DỪNG (0 = chạy hết sheet).</summary>
    [ObservableProperty] private int _endRow;
    /// <summary>Số dòng mỗi khối (mỗi tk Shopee nhận 1 khối kế tiếp theo số này).</summary>
    [ObservableProperty] private int _rowsPerAccount = 30;
    /// <summary>Số tk Shopee tk BigSeller này CHIẾM (slice).</summary>
    [ObservableProperty] private int _shopeeCount = 2;
    /// <summary>Số process chạy đồng thời trong tk BigSeller này (≤ số tk Shopee).</summary>
    [ObservableProperty] private int _maxProcess = 2;

    public ScrapeTargetViewModel(BigSellerAccount account)
    {
        Account = account;
        Shops = new ObservableCollection<BigSellerShop>(account.Shops);
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
            if (p is null || (p.Completed.Count == 0 && p.ReservedShopeeAccountIds.Count == 0))
                return "Chưa có tiến độ.";
            var status = p.Status switch
            {
                "completed" => "✔ Hoàn thành",
                "running" => "▶ Đang chạy",
                "stopped" => "■ Dừng giữa chừng",
                _ => "—",
            };
            var keep = p.ReservedShopeeAccountIds.Count > 0 ? $" · giữ {p.ReservedShopeeAccountIds.Count} tk Shopee" : "";
            return $"{status} · đã xong tới dòng {p.LastRowReached}{keep}";
        }
    }

    public void RefreshProgress() => OnPropertyChanged(nameof(ProgressText));
}
