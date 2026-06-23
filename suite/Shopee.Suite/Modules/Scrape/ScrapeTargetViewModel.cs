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
    [ObservableProperty] private int _rowsPerAccount = 30;
    partial void OnRowsPerAccountChanged(int value) => Persist();
    /// <summary>Số process chạy đồng thời = số tk Shopee acc BigSeller này dùng (mỗi process 1 tk).</summary>
    [ObservableProperty] private int _maxProcess = 2;
    partial void OnMaxProcessChanged(int value) => Persist();

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
