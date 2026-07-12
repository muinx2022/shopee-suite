using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Shopee.Core.Coordination;

namespace Shopee.Suite.Modules.Data;

/// <summary>
/// Bao 1 dòng của lưới "Dữ liệu sản phẩm": giữ nguyên <see cref="AllDataRow"/> gốc + các nhãn đã resolve từ
/// cấu hình (tài khoản/shop), và cờ tick chọn. Nhãn acc/shop resolve SẴN ở VM lúc dựng dòng (VM có snapshot
/// BigSellerStore) nên item không tự đọc kho. Đổi tick → gọi callback cho VM cập nhật tập chọn + đếm lại.
/// </summary>
public sealed partial class DataRowItem : ObservableObject
{
    // Nền dòng đã-bán: xanh lá nhạt (khớp tông "đang chạy" ở Workspace) — cell trong suốt để lộ màu này.
    private static readonly IBrush SoldBrush = new SolidColorBrush(Color.Parse("#EAF8F0"));

    private readonly Action<DataRowItem> _onSelectionChanged;

    public DataRowItem(AllDataRow model, string accLabel, string shopLabel, Action<DataRowItem> onSelectionChanged)
    {
        Model = model;
        AccLabel = accLabel;
        ShopLabel = shopLabel;
        _onSelectionChanged = onSelectionChanged;
    }

    /// <summary>Dòng gốc từ Hub (giữ ĐỦ 17 ô để mở form sửa).</summary>
    public AllDataRow Model { get; }

    /// <summary>Khoá vị trí (acc, sheet, rowNo) — dùng cho tập chọn + các thao tác mark/regen/xoá.</summary>
    public ProductRowKey Key => new(Model.AccountId, Model.Sheet, Model.RowNo);

    [ObservableProperty] private bool _isSelected;
    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged(this);

    // ── Cột hiển thị ──
    public string AccLabel { get; }
    public string ShopLabel { get; }
    public int RowNo => Model.RowNo;
    public string Link => Model.Data.Link;
    public string PriceSale => Model.Data.PriceSale;
    public string Sku => Model.Data.Sku;
    public string ItemId => Model.Data.ItemId;
    public string NameOriginal => Model.Data.NameOriginal;
    public string NameRewritten => Model.Data.NameRewritten;

    /// <summary>Đã có bản ghi đã-bán (sold_count &gt; 0) → tô nền dòng.</summary>
    public bool IsSold => Model.SoldCount > 0;
    /// <summary>Số đã bán — chuỗi rỗng khi 0 (khớp lưới hub).</summary>
    public string SoldCount => Model.SoldCount > 0 ? Model.SoldCount.ToString() : "";
    /// <summary>Lúc sửa (giờ máy).</summary>
    public string UpdatedLocal => Model.UpdatedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm");

    /// <summary>Nền dòng theo trạng thái đã-bán (bind ở DataGridRow).</summary>
    public IBrush RowBackground => IsSold ? SoldBrush : Brushes.Transparent;
}
