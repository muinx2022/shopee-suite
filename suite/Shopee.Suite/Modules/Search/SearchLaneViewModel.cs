using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Modules.Search;

namespace Shopee.Suite.Modules.Search;

/// <summary>Một lane tìm kiếm (1 process/Edge) — header tab + lưới sản phẩm của lane đó (chế độ từ khóa).</summary>
public sealed partial class SearchLaneViewModel : ObservableObject
{
    public SearchLaneViewModel(int laneId) => LaneId = laneId;

    public int LaneId { get; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    private string _keyword = "";
    [ObservableProperty] private string _account = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    private string _status = "Chờ";
    [ObservableProperty] private bool _connected;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    private int _count;

    /// <summary>Sản phẩm lane này tìm được (live) — lưới trong tab của lane.</summary>
    public ObservableCollection<SearchProductRow> Products { get; } = [];

    /// <summary>Tiêu đề tab: "L{n} · {từ khóa} ({số SP})"; lane id 0 = tab "Đã lưu" (phiên trước).</summary>
    public string Header =>
        (LaneId == 0 ? "📁 Đã lưu" : $"L{LaneId}" + (string.IsNullOrWhiteSpace(Keyword) ? "" : $" · {Keyword}"))
        + $" ({Count})";
}
