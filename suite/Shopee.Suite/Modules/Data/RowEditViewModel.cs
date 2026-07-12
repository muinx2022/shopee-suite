using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Core.Products;

namespace Shopee.Suite.Modules.Data;

/// <summary>
/// ViewModel cửa sổ THÊM/SỬA 1 dòng dữ liệu. Lưu QUA <see cref="ProductGridEngine.SaveRowAsync"/> của VM cha
/// (engine tự validate SKU-trùng + Insert/Update + đặt Status), phơi kết quả (RowNo/Sku/Status) cho
/// <see cref="DataViewModel"/> đọc sau khi đóng.
///  · THÊM: chọn tài khoản + shop (bắt buộc), SKU trống → server tự sinh B#####.
///  · SỬA: acc/shop cố định (hiển thị nhãn), prefill đủ 17 ô, KHÔNG auto-gen SKU.
/// </summary>
public sealed partial class RowEditViewModel : ObservableObject
{
    public bool IsEdit { get; }
    public bool IsAddMode => !IsEdit;
    public string Title { get; }

    // ── THÊM: combo acc/shop ──
    public ObservableCollection<AccountOption> AccountOptions { get; } = new();
    public ObservableCollection<ShopOption> ShopOptions { get; } = new();
    [ObservableProperty] private AccountOption? _selectedAccount;
    [ObservableProperty] private ShopOption? _selectedShop;

    // ── SỬA: nhãn acc/shop cố định ──
    public string AccountLabel { get; } = "";
    public string ShopLabel { get; } = "";

    private readonly ProductGridEngine _engine;
    private readonly Action<Func<string, Task<bool>>?> _setConfirmOverride;   // bật/tắt confirm owner=modal ở VM cha
    private readonly IReadOnlyList<BigSellerAccount> _accounts;
    private readonly string _acct = "";     // đích SỬA
    private readonly string _sheet = "";
    private readonly int _rowNo = -1;
    private readonly string _origSku = "";

    // 17 ô dữ liệu.
    [ObservableProperty] private string _link = "";
    [ObservableProperty] private string _priceOriginal = "";
    [ObservableProperty] private string _priceSale = "";
    [ObservableProperty] private string _sku = "";
    [ObservableProperty] private string _itemId = "";
    [ObservableProperty] private string _nameOriginal = "";
    [ObservableProperty] private string _nameRewritten = "";
    [ObservableProperty] private string _category = "";
    [ObservableProperty] private string _shopName = "";
    [ObservableProperty] private string _rating = "";
    [ObservableProperty] private string _soldMonth = "";
    [ObservableProperty] private string _likes = "";
    [ObservableProperty] private string _reviews = "";
    [ObservableProperty] private string _region = "";
    [ObservableProperty] private string _image = "";
    [ObservableProperty] private string _metaShopId = "";
    [ObservableProperty] private string _metaItemId = "";

    /// <summary>Mở thêm 11 ô còn lại (nhập đủ 17 cột).</summary>
    [ObservableProperty] private bool _showAll;
    [ObservableProperty] private string _error = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isBusy;

    public bool CanSave => !IsBusy;

    /// <summary>Hàm confirm SỞ HỮU bởi cửa sổ (window gán) — để hộp "SKU trùng" bám chính modal, tránh nested
    /// modal owner=MainWindow bị khuất sau form. Null → engine dùng confirm mặc định của VM cha.</summary>
    public Func<string, Task<bool>>? ConfirmOwner { get; set; }

    // ── Kết quả (VM cha đọc sau khi đóng true) ──
    public string ResultAcct { get; private set; } = "";
    public string ResultSheet { get; private set; } = "";
    public int ResultRowNo { get; private set; }
    public string ResultSku { get; private set; } = "";
    public string ResultStatus { get; private set; } = "";

    /// <summary>Chế độ THÊM: prefill acc/shop từ bộ lọc đang áp (nếu có).</summary>
    public RowEditViewModel(ProductGridEngine engine, Action<Func<string, Task<bool>>?> setConfirmOverride,
        IReadOnlyList<BigSellerAccount> accounts, string? prefillAcct, string? prefillSheet)
    {
        _engine = engine;
        _setConfirmOverride = setConfirmOverride;
        IsEdit = false;
        _accounts = accounts;
        Title = "➕ Thêm dòng dữ liệu";

        AccountOptions.Add(new AccountOption(null, "— chọn tài khoản —"));
        foreach (var a in _accounts)
            AccountOptions.Add(new AccountOption(a.Id, a.DisplayName));
        SelectedAccount = AccountOptions[0];

        if (!string.IsNullOrEmpty(prefillAcct))
        {
            var opt = AccountOptions.FirstOrDefault(o => o.Id == prefillAcct);
            if (opt is not null)
            {
                SelectedAccount = opt;   // dựng lại option shop
                if (!string.IsNullOrEmpty(prefillSheet))
                {
                    var sopt = ShopOptions.FirstOrDefault(o => o.Sheet == prefillSheet);
                    if (sopt is not null) SelectedShop = sopt;
                }
            }
        }
    }

    /// <summary>Chế độ SỬA: đích = shop của dòng (cố định), prefill đủ 17 ô.</summary>
    public RowEditViewModel(ProductGridEngine engine, Action<Func<string, Task<bool>>?> setConfirmOverride,
        AllDataRow row, string acctLabel, string shopLabel)
    {
        _engine = engine;
        _setConfirmOverride = setConfirmOverride;
        IsEdit = true;
        _accounts = Array.Empty<BigSellerAccount>();
        _acct = row.AccountId;
        _sheet = row.Sheet;
        _rowNo = row.RowNo;
        _origSku = row.Data.Sku;
        AccountLabel = acctLabel;
        ShopLabel = shopLabel;
        Title = $"✏ Sửa dòng {row.RowNo}";
        LoadFields(row.Data);
    }

    // Đổi tài khoản (combo THÊM) → dựng lại option shop + reset shop (chỉ shop có ShopeeDataSheet).
    partial void OnSelectedAccountChanged(AccountOption? value)
    {
        ShopOptions.Clear();
        var acct = _accounts.FirstOrDefault(a => a.Id == value?.Id);
        if (acct is not null)
            foreach (var s in acct.Shops)
                if (!string.IsNullOrWhiteSpace(s.ShopeeDataSheet))
                    ShopOptions.Add(new ShopOption(s.ShopeeDataSheet, s.DisplayName));
        SelectedShop = ShopOptions.FirstOrDefault();
    }

    private void LoadFields(ProductRowData d)
    {
        Link = d.Link; PriceOriginal = d.PriceOriginal; PriceSale = d.PriceSale; Sku = d.Sku; ItemId = d.ItemId;
        NameOriginal = d.NameOriginal; NameRewritten = d.NameRewritten; Category = d.Category; ShopName = d.ShopName;
        Rating = d.Rating; SoldMonth = d.SoldMonth; Likes = d.Likes; Reviews = d.Reviews; Region = d.Region;
        Image = d.Image; MetaShopId = d.MetaShopId; MetaItemId = d.MetaItemId;
    }

    private ProductRowData BuildData(string sku) => new(
        Link, PriceOriginal, PriceSale, sku, ItemId, NameOriginal, NameRewritten, Category, ShopName, Rating,
        SoldMonth, Likes, Reviews, Region, Image, MetaShopId, MetaItemId);

    /// <summary>Lưu dòng. true = thành công (đóng window); false = ở lại form (đã đặt <see cref="Error"/>).</summary>
    public async Task<bool> SaveAsync()
    {
        if (IsBusy) return false;

        // Validate đích.
        string acct, sheet;
        if (IsEdit) { acct = _acct; sheet = _sheet; }
        else
        {
            acct = SelectedAccount?.Id ?? "";
            sheet = SelectedShop?.Sheet ?? "";
            if (string.IsNullOrEmpty(acct) || string.IsNullOrEmpty(sheet)) { Error = "Chọn tài khoản + shop."; return false; }
        }

        var sku = (Sku ?? "").Trim();
        Error = "";
        IsBusy = true;
        _setConfirmOverride(ConfirmOwner);   // hộp "SKU trùng" (do engine gọi) bám chính modal này, không MainWindow
        try
        {
            // Engine tự check SKU-trùng (+ confirm qua callback vừa trỏ về modal) + Insert/Update + đặt Status.
            var (ok, rowNo, resSku) = await _engine.SaveRowAsync(IsEdit, acct, sheet, _rowNo, _origSku, BuildData(sku));
            if (!ok)
            {
                // Kho chưa sẵn sàng / lỗi engine → hiện trong form; huỷ confirm SKU-trùng (không phải lỗi) → form im.
                Error = !_engine.PgReady ? "Kho Hub (Postgres) chưa sẵn sàng."
                      : _engine.Status.StartsWith("✘") ? _engine.Status
                      : "";
                return false;
            }
            ResultAcct = acct; ResultSheet = sheet; ResultRowNo = rowNo; ResultSku = resSku;
            ResultStatus = _engine.Status;   // chuỗi do engine đặt (thêm: "✔ Đã thêm dòng R (SKU S).")
            return true;
        }
        finally { IsBusy = false; _setConfirmOverride(null); }
    }
}
