using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.BigSeller;

namespace Shopee.Suite.Modules.BigSeller;

/// <summary>Bao một <see cref="BigSellerShop"/> để chỉnh sửa trực tiếp trong lưới shop. Mỗi lần
/// sửa (tên / sheet / cấu hình) TỰ LƯU ngay xuống kho — tránh mất khi chuyển module hoặc khởi
/// động lại app (trước đây phải bấm thêm/xóa shop mới trigger Save).</summary>
public sealed class BigSellerShopViewModel : ObservableObject
{
    public BigSellerShop Model { get; }

    /// <summary>Tk cha ở chế độ kho Hub (Postgres)? Chỉ dùng để ẨN các khái niệm Excel (sheet / map cột) khỏi
    /// mắt user — sheet ở hub-mode chỉ là tên ngăn dữ liệu nội bộ (= shop.Id). Logic runner KHÔNG đổi.</summary>
    public bool UsesHubData { get; }

    public BigSellerShopViewModel(BigSellerShop model, bool usesHubData = false)
    {
        Model = model;
        UsesHubData = usesHubData;
        Model.ColumnMap ??= new BigSellerColumnMap();   // dữ liệu cũ (json thiếu columnMap) → tránh null
    }

    /// <summary>Giá trị hiển thị cột "Sheet" trong lưới shop: hub-mode để TRỐNG (sheet = GUID ngăn nội bộ,
    /// không phô cho user); excel-mode hiện tên sheet như cũ. ShopeeDataSheet (dùng cho runner) KHÔNG đổi.</summary>
    public string SheetDisplay => UsesHubData ? "" : Model.ShopeeDataSheet;

    // Gộp ghi đĩa (Avalonia bind cập nhật mỗi phím) — xem PersistDebounce, thay LostFocus của WPF. Model cập nhật ngay.
    private static void Persist() => PersistDebounce.Schedule();

    public string Name
    {
        get => Model.Name;
        set { if (Model.Name != value) { Model.Name = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); Persist(); } }
    }

    public string DisplayName => Model.DisplayName;

    public string ShopeeDataSheet
    {
        get => Model.ShopeeDataSheet;
        set
        {
            // ComboBox trong DataGrid (ảo hóa hàng / đổi account / chuyển module) hay GHI null ngược khi
            // ItemsSource tạm rỗng lúc teardown → BỎ QUA null để không xóa mất sheet đã lưu. User chọn
            // sheet thật luôn là chuỗi != null; muốn bỏ sheet thì chọn item rỗng (chuỗi "", vẫn cho qua).
            if (value is null) return;
            if (Model.ShopeeDataSheet == value) return;
            Model.ShopeeDataSheet = value; OnPropertyChanged(); OnPropertyChanged(nameof(SheetDisplay)); Persist();
        }
    }

    public string OpenAiModel
    {
        get => Model.OpenAiModel;
        set { if (Model.OpenAiModel != value) { Model.OpenAiModel = value; OnPropertyChanged(); Persist(); } }
    }

    public int OpenAiBatchSize
    {
        get => Model.OpenAiBatchSize;
        set { if (Model.OpenAiBatchSize != value) { Model.OpenAiBatchSize = value; OnPropertyChanged(); Persist(); } }
    }

    public string OpenAiApiKeyFile
    {
        get => Model.OpenAiApiKeyFile;
        set { if (Model.OpenAiApiKeyFile != value) { Model.OpenAiApiKeyFile = value; OnPropertyChanged(); Persist(); } }
    }

    public string BigSellerCrawlUrl
    {
        get => Model.BigSellerCrawlUrl;
        set { if (Model.BigSellerCrawlUrl != value) { Model.BigSellerCrawlUrl = value; OnPropertyChanged(); Persist(); } }
    }

    // ── Ánh xạ field ↔ cột Excel (nhập theo CHỮ cột: A, B, …, AA) ───────────────────
    public string LinkColumnLetter
    {
        get => ExcelColumn.ToLetter(Model.ColumnMap.LinkColumn);
        set => SetColumn(value, v => Model.ColumnMap.LinkColumn = v, Model.ColumnMap.LinkColumn);
    }

    public string ItemIdColumnLetter
    {
        get => ExcelColumn.ToLetter(Model.ColumnMap.ItemIdColumn);
        set => SetColumn(value, v => Model.ColumnMap.ItemIdColumn = v, Model.ColumnMap.ItemIdColumn);
    }

    public string PriceColumnLetter
    {
        get => ExcelColumn.ToLetter(Model.ColumnMap.PriceColumn);
        set => SetColumn(value, v => Model.ColumnMap.PriceColumn = v, Model.ColumnMap.PriceColumn);
    }

    public string SkuColumnLetter
    {
        get => ExcelColumn.ToLetter(Model.ColumnMap.SkuColumn);
        set => SetColumn(value, v => Model.ColumnMap.SkuColumn = v, Model.ColumnMap.SkuColumn);
    }

    public string ProductNameColumnLetter
    {
        get => ExcelColumn.ToLetter(Model.ColumnMap.ProductNameColumn);
        set => SetColumn(value, v => Model.ColumnMap.ProductNameColumn = v, Model.ColumnMap.ProductNameColumn);
    }

    public string RewrittenNameColumnLetter
    {
        get => ExcelColumn.ToLetter(Model.ColumnMap.RewrittenNameColumn);
        set => SetColumn(value, v => Model.ColumnMap.RewrittenNameColumn = v, Model.ColumnMap.RewrittenNameColumn);
    }

    private void SetColumn(string? letter, Action<int> apply, int current, [System.Runtime.CompilerServices.CallerMemberName] string? prop = null)
    {
        var col = ExcelColumn.FromLetter(letter);
        if (col == current) return;
        apply(col);
        OnPropertyChanged(prop);
        Persist();
    }
}
