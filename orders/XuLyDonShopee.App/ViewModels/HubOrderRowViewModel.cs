using System.Globalization;
using System.Linq;
using XuLyDonShopee.App.Services;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>
/// Một dòng hiển thị (CHỈ ĐỌC) trên bảng màn "Đơn toàn hệ thống": bọc <see cref="HubOrderView"/> (đơn ĐỌC THẲNG
/// từ Hub, của MỌI máy) + nhãn shop đã tra từ <c>shopId</c>, tính sẵn chuỗi hiển thị để DataGrid bind thẳng.
/// Không cần INotifyPropertyChanged: mỗi lượt tải, các dòng được TẠO LẠI thay vì sửa tại chỗ.
/// <para>Dùng lại <see cref="OrderRowViewModel.BuildProduct"/>/<see cref="OrderRowViewModel.BuildTotal"/> để
/// định dạng Y HỆT màn "Đơn hàng" — cùng dữ liệu thì hai màn phải hiện giống nhau.</para>
/// <para>KHÔNG có hành động nghiệp vụ nào (in phiếu / đổi trạng thái / xuất): đơn ở đây thuộc máy khác.</para>
/// </summary>
public sealed class HubOrderRowViewModel
{
    private readonly HubOrderView _row;

    public HubOrderRowViewModel(HubOrderView row, string shopLabel)
    {
        _row = row;
        ShopLabel = shopLabel;
    }

    /// <summary>Nhãn shop: tên shop tra từ danh sách shop của Hub; không tra được → "shop #{id}".</summary>
    public string ShopLabel { get; }

    public string OrderSn => _row.OrderSn;
    public string Buyer => _row.BuyerUsername ?? string.Empty;

    /// <summary>"Tên SP đầu (+n)" với n = số sản phẩm còn lại khi đơn có &gt;1 sản phẩm.</summary>
    public string Product => OrderRowViewModel.BuildProduct(_row.ItemSummary, _row.ItemCount);

    public string Sku => _row.Sku ?? string.Empty;

    /// <summary>Tổng tiền: ưu tiên số đã parse (₫1.234.567), thiếu thì dùng nguyên văn.</summary>
    public string Total => OrderRowViewModel.BuildTotal(_row.TotalPrice, _row.TotalPriceText);

    /// <summary>Cột "Ước tính" = "Số tiền cuối cùng" (final_amount) — rỗng nếu máy chủ đơn chưa lấy được.</summary>
    public string Estimate => OrderRowViewModel.BuildTotal(_row.FinalAmount, _row.FinalAmountText);

    public string Status => _row.Status ?? string.Empty;

    /// <summary>Cột "Vận chuyển": đơn vị vận chuyển + mã vận đơn ("SPX · SPXVN123"); thiếu vế nào thì bỏ vế đó.</summary>
    public string Shipping => string.Join(" · ", new[] { _row.Carrier, _row.TrackingNumber }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>Giờ đơn được đẩy lên Hub, theo giờ địa phương (dd/MM/yyyy HH:mm); rỗng nếu chưa có.</summary>
    public string SyncedAtDisplay => _row.SyncedAt == default
        ? string.Empty
        : _row.SyncedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
}
