using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Modules.Search;

namespace Shopee.Suite.Modules.Search;

/// <summary>Một dòng link trong file đã nạp — tick chọn để đưa vào search.</summary>
public sealed partial class SearchFileLinkRow : ObservableObject
{
    public int Row { get; }
    public string Link { get; }
    public string SourceFile { get; }

    public SearchFileLinkRow(int row, string link, string sourceFile, string status)
    {
        Row = row;
        Link = link;
        SourceFile = sourceFile;
        _status = status ?? "";
        // Link đã "Processed" → mặc định bỏ tick (chạy tiếp phần còn lại).
        _isSelected = !string.Equals(status, "Processed", StringComparison.OrdinalIgnoreCase);
    }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _status;

    /// <summary>Tiến độ lượt chạy gần nhất của link (trạng thái + danh mục + trang + số SP) — để biết
    /// link đang duyệt tới đâu / đã xong chưa / còn resume được.</summary>
    [ObservableProperty] private string _progress = "";
}

/// <summary>Một tab cho 1 LINK category đang chạy — tài khoản + trạng thái + sản phẩm crawl được.</summary>
public sealed partial class SearchFileTab : ObservableObject
{
    public int Row { get; }
    public string Link { get; }
    public string SourceFile { get; }
    public string Label { get; }

    public SearchFileTab(int row, string link, string sourceFile, string label)
    {
        Row = row;
        Link = link;
        SourceFile = sourceFile;
        Label = label;
    }

    public ObservableCollection<SearchProductRow> Products { get; } = [];

    /// <summary>Link SP đã hiện TRONG TAB NÀY. Khử trùng phải theo TỪNG tab, không dùng chung một sổ toàn
    /// phiên: một SP nằm ở 2 danh mục thì sổ chung chỉ cho nó hiện ở tab của link chạy trước, tab kia đứng
    /// im như đang hỏng; chạy lại một link trong cùng phiên cũng không ra dòng nào.</summary>
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>Thêm 1 SP vào tab, bỏ qua nếu tab này đã có (theo link SP). SP không có link thì luôn thêm
    /// (không có khoá để so). Trả về true nếu đã thêm thật.</summary>
    public bool AddProduct(SearchProductRow p)
    {
        if (!string.IsNullOrEmpty(p.Link) && !_seen.Add(p.Link)) return false;
        Products.Add(p);
        ProductCount = Products.Count;
        return true;
    }

    /// <summary>Xoá sạch SP của tab ("Xóa dữ liệu"): sổ khử trùng phải sạch THEO, kẻo lượt cào sau tưởng SP
    /// cũ là trùng và tab ở lại trống.</summary>
    public void ClearProducts()
    {
        Products.Clear();
        _seen.Clear();
        ProductCount = 0;
    }

    [ObservableProperty] private string _account = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    private string _status = "chờ";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    private int _productCount;

    public string Header => $"{Label} · {ProductCount} SP";
}
