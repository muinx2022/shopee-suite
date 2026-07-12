using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.BigSeller;
using Shopee.Core.Coordination;
using Shopee.Suite.Services;

namespace Shopee.Suite.Modules.BigSeller;

/// <summary>Bao một <see cref="BigSellerAccount"/>: thông tin account + danh sách shop + danh
/// sách sheet đọc từ workbook để chọn cho từng shop.</summary>
public sealed class BigSellerAccountItemViewModel : ObservableObject
{
    public BigSellerAccount Model { get; }

    public ObservableCollection<BigSellerShopViewModel> Shops { get; } = [];
    /// <summary>Tên các sheet đọc từ workbook (đổ vào combo chọn sheet của shop).</summary>
    public ObservableCollection<string> SheetOptions { get; } = [];

    public BigSellerAccountItemViewModel(BigSellerAccount model)
    {
        Model = model;
        foreach (var s in model.Shops) Shops.Add(new BigSellerShopViewModel(s, model.UsesHubData));
        RefreshSheets();
    }

    // Mọi chỉnh sửa account tự LƯU (tránh mất khi chuyển module / khởi động lại). GỘP nhiều lần sửa liên tiếp
    // (gõ từng phím) thành 1 lần ghi đĩa ~500ms sau lần sửa cuối — thay UpdateSourceTrigger=LostFocus của WPF
    // (Avalonia bind Text cập nhật mỗi phím). Model đã cập nhật ngay nên chuyển module/lưu tay vẫn đúng dữ liệu.
    internal static void Persist() => PersistDebounce.Schedule();

    public string Label
    {
        get => Model.Label;
        set { if (Model.Label != value) { Model.Label = value; OnChanged(nameof(Label), nameof(DisplayName)); Persist(); } }
    }

    public string Email
    {
        get => Model.Email;
        set { if (Model.Email != value) { Model.Email = value; OnChanged(nameof(Email), nameof(DisplayName)); Persist(); } }
    }

    /// <summary>Mật khẩu BigSeller (plain) — cho auto-login tự mint token mỗi máy. Sync qua Hub như Email.</summary>
    public string Password
    {
        get => Model.Password;
        set { if (Model.Password != value) { Model.Password = value; OnPropertyChanged(); Persist(); } }
    }

    public string WorkbookPath
    {
        get => Model.WorkbookPath;
        set { if (Model.WorkbookPath != value) { Model.WorkbookPath = value; OnPropertyChanged(); RefreshSheets(); Persist(); } }
    }

    public string CookieFile
    {
        get => Model.CookieFile;
        set { if (Model.CookieFile != value) { Model.CookieFile = value; OnChanged(nameof(CookieFile), nameof(CookieStatus)); Persist(); } }
    }

    /// <summary>KiotProxy key RIÊNG cho tk BigSeller này. Có key → bigseller.com (login + scrape) đi qua
    /// IP của proxy này → mỗi tk 1 IP → chạy SONG SONG nhiều tk không bị đá phiên. Trống → đi IP máy
    /// (khi đó các tk BigSeller chạy LẦN LƯỢT).</summary>
    public string KiotProxyKey
    {
        get => Model.KiotProxyKey;
        set { if (Model.KiotProxyKey != value) { Model.KiotProxyKey = value; OnChanged(nameof(KiotProxyKey), nameof(ProxyStatus)); Persist(); } }
    }

    public string Region
    {
        get => Model.Region;
        set { if (Model.Region != value) { Model.Region = value; OnPropertyChanged(); Persist(); } }
    }

    public string ProxyType
    {
        get => Model.ProxyType;
        set { if (Model.ProxyType != value) { Model.ProxyType = value; OnPropertyChanged(); Persist(); } }
    }

    public string ProxyStatus => Model.HasProxy
        ? "✓ Có proxy riêng → chạy song song nhiều tk"
        : "ⓘ Không proxy → đi IP máy, các tk chạy lần lượt";

    public string DisplayName => Model.DisplayName;
    public int ShopCount => Shops.Count;
    public string CookieStatus => Model.HasCookie ? "✓ Đã có cookie BigSeller" : "⚠ Chưa đăng nhập BigSeller";

    /// <summary>Tk này lấy dữ liệu SP từ kho Hub (Postgres) thay vì workbook Excel local. CHỈ HIỂN THỊ ở client —
    /// đổi chế độ làm trên web Hub. Đọc từ Model (đặt lúc dựng VM / khi Hub sync + rebuild projection).</summary>
    public bool IsHubData => Model.UsesHubData;
    /// <summary>Nhãn chế độ kho hiện cạnh ô workbook (rỗng ở chế độ Excel để không chiếm chỗ).</summary>
    public string DataSourceLabel => Model.UsesHubData
        ? "🗄 Kho Hub (Postgres) — dữ liệu SP lấy từ Hub; đổi chế độ trên web Hub."
        : "";
    /// <summary>Chỉ chế độ Excel mới cho chọn file workbook (hub-mode không dùng file → khoá nút chọn).</summary>
    public bool CanPickWorkbook => !Model.UsesHubData;
    /// <summary>Hint cạnh ô workbook cho acc excel-mode: file workbook giờ là FILE LOCAL của máy (đường chuyển
    /// tiếp) — KHÔNG còn đồng bộ qua Hub (kho SP đã sang Postgres). Rỗng ở hub-mode (đã có DataSourceLabel).</summary>
    public string WorkbookSyncHint => Model.UsesHubData ? "" : "(file local — không còn đồng bộ qua Hub)";

    public BigSellerShopViewModel AddShop()
    {
        var shop = new BigSellerShop { Name = "Shop mới" };
        // Acc hub-mode: "sheet" chỉ là tên ngăn dữ liệu nội bộ hệ thống tự quản → gán = shop.Id (GUID ổn định,
        // không đổi khi rename shop). CÙNG luật với AccountConfigPanel.AddShop trên web Hub. Acc excel-mode để
        // trống (user gán sheet workbook sau). Shop mới sẽ được đẩy lên Hub qua HubBigSellerUpsert.
        if (Model.UsesHubData) shop.ShopeeDataSheet = shop.Id;
        Model.Shops.Add(shop);
        var vm = new BigSellerShopViewModel(shop, Model.UsesHubData);
        Shops.Add(vm);
        OnPropertyChanged(nameof(ShopCount));
        return vm;
    }

    public void RemoveShop(BigSellerShopViewModel shop)
    {
        Model.Shops.Remove(shop.Model);
        Shops.Remove(shop);
        OnPropertyChanged(nameof(ShopCount));
    }

    /// <summary>Đối chiếu lưới <see cref="Shops"/> với <see cref="BigSellerAccount.Shops"/> của model SAU khi
    /// Hub sync sửa danh sách shop (thêm/bớt/đổi thứ tự) trên acc ĐANG có trong danh sách. Guard SetEquals ở
    /// <c>BigSellerViewModel.SyncFromStore</c> chỉ dựng lại khi TẬP tài khoản đổi → ca "chỉ thêm shop vào acc
    /// cũ" bị bỏ sót, shop mới từ Hub không hiện tới khi khởi động lại app. Khớp theo THAM CHIẾU model: tái
    /// dùng VM cũ cho shop còn giữ (không phá ô đang gõ / selection), tạo VM mới cho shop Hub thêm, bỏ VM cho
    /// shop Hub gỡ. Có fast-path khi đã khớp sẵn — Save mỗi-phím cũng bắn Changed nên phải rẻ + không đụng
    /// collection khi tập shop không đổi (chỉ sửa thuộc tính).</summary>
    public void SyncShopsFromModel()
    {
        if (Shops.Count == Model.Shops.Count)
        {
            var same = true;
            for (var i = 0; i < Shops.Count; i++)
                if (!ReferenceEquals(Shops[i].Model, Model.Shops[i])) { same = false; break; }
            if (same) return;   // danh sách shop y nguyên → khỏi đụng lưới (giữ focus/selection khi đang gõ)
        }

        // Bỏ VM shop mà model đã bị Hub gỡ khỏi danh sách.
        for (var i = Shops.Count - 1; i >= 0; i--)
            if (!Model.Shops.Contains(Shops[i].Model)) Shops.RemoveAt(i);

        // Duyệt theo thứ tự Model.Shops: chèn shop mới / chuyển shop sai chỗ (tái dùng VM cũ theo tham chiếu).
        for (var i = 0; i < Model.Shops.Count; i++)
        {
            var m = Model.Shops[i];
            if (i < Shops.Count && ReferenceEquals(Shops[i].Model, m)) continue;
            var at = -1;
            for (var j = i + 1; j < Shops.Count; j++)
                if (ReferenceEquals(Shops[j].Model, m)) { at = j; break; }
            if (at >= 0) Shops.Move(at, i);
            else Shops.Insert(i, new BigSellerShopViewModel(m, Model.UsesHubData));
        }

        OnPropertyChanged(nameof(ShopCount));
    }

    public void RefreshSheets()
    {
        // HUB-MODE: danh sách sheet lấy từ kho Hub (async) thay vì đọc file workbook. Fire-and-forget: nuốt lỗi
        // → rỗng (UI không vỡ; runner mới là chỗ chặn cứng khi Hub chưa sẵn sàng).
        if (Model.UsesHubData) { _ = RefreshSheetsFromHubAsync(); return; }

        // Danh sách mong muốn = sheet trong workbook + sheet đang gán cho các shop (để combo
        // SelectedItem luôn khớp, không bị reset null).
        var desired = WorkbookSheets.ListSheetNames(Model.WorkbookPath);
        MergeShopSheets(desired);
        ApplySheetOptions(desired);
    }

    // Nạp tên sheet từ kho Hub (GetProductSheetsAsync) → SheetOptions. Nuốt MỌI lỗi → dùng phần rỗng (chỉ còn
    // sheet đang gán cho shop). Cập nhật collection trên UI thread (SheetOptions bind trực tiếp vào combo).
    private async Task RefreshSheetsFromHubAsync()
    {
        var desired = new List<string>();
        try
        {
            var client = CoordinationRuntime.Client;
            if (client is not null)
            {
                var sheets = await client.GetProductSheetsAsync(Model.Id).ConfigureAwait(false);
                if (sheets is not null)
                    foreach (var s in sheets)
                        if (!string.IsNullOrWhiteSpace(s.Sheet) && !desired.Contains(s.Sheet))
                            desired.Add(s.Sheet);
            }
        }
        catch { desired.Clear(); }

        MergeShopSheets(desired);
        UiThread.Post(() => ApplySheetOptions(desired));
    }

    /// <summary>Bổ sung sheet đang gán cho các shop (để combo SelectedItem luôn khớp, không bị reset null).</summary>
    private void MergeShopSheets(List<string> desired)
    {
        foreach (var s in Model.Shops)
            if (!string.IsNullOrWhiteSpace(s.ShopeeDataSheet) && !desired.Contains(s.ShopeeDataSheet))
                desired.Add(s.ShopeeDataSheet);
    }

    /// <summary>Cập nhật TĂNG DẦN (KHÔNG Clear) — nếu Clear thì combo mất giá trị giữa chừng → ghi null ngược +
    /// auto-save làm MẤT sheet đã chọn. Chỉ xoá cái thừa, thêm cái thiếu.</summary>
    private void ApplySheetOptions(List<string> desired)
    {
        for (var i = SheetOptions.Count - 1; i >= 0; i--)
            if (!desired.Contains(SheetOptions[i])) SheetOptions.RemoveAt(i);
        foreach (var name in desired)
            if (!SheetOptions.Contains(name)) SheetOptions.Add(name);
    }

    public void NotifyCookieChanged() => OnPropertyChanged(nameof(CookieStatus));

    private void OnChanged(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
    }
}
