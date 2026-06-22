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

    [ObservableProperty] private string _account = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    private string _status = "chờ";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    private int _productCount;

    public string Header => $"{Label} · {ProductCount} SP";
}
