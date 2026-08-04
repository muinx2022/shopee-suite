using CommunityToolkit.Mvvm.ComponentModel;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>
/// Một dòng lưới tab "Kết quả": tên Shop (hiển thị) + số đơn đã Chuẩn bị hàng của ngày đang lọc + cột tiến độ
/// (vòng quay khi đang check shop đó / dấu tick khi đã check xong shop đó trong lượt chạy).
/// <para>
/// Là LỚP quan sát được (không còn <c>record</c> bất biến) vì các cờ tiến độ đổi TẠI CHỖ trong lúc chạy — dựng
/// lại dòng mỗi lần đổi cờ sẽ làm lưới nháy. Kéo theo: hết value-equality, so sánh dòng là so THAM CHIẾU.
/// </para>
/// </summary>
public sealed partial class ShopPrepareRow : ObservableObject
{
    /// <param name="shopName">Tên hiển thị ở cột "Shop" (<c>account_shops.shop_name</c>, thiếu thì chính login).</param>
    /// <param name="shopLogin">KHÓA shop (<c>account_shops.shop_login</c> = khóa <c>prepare_daily</c>) — dùng để khớp
    /// nhãn shop mà phiên báo về; KHÁC <paramref name="shopName"/> khi shop có tên hiển thị riêng.</param>
    /// <param name="preparedCount">Số đơn đã Chuẩn bị hàng trong ngày đang lọc.</param>
    public ShopPrepareRow(string shopName, string shopLogin, int preparedCount)
    {
        ShopName = shopName;
        ShopLogin = shopLogin;
        PreparedCount = preparedCount;
    }

    /// <summary>Tên shop hiển thị ở cột "Shop" (không đổi trong đời dòng).</summary>
    public string ShopName { get; }

    /// <summary>Khóa shop dùng khớp với nhãn phiên báo về (không đổi trong đời dòng).</summary>
    public string ShopLogin { get; }

    /// <summary>Số đơn đã Chuẩn bị hàng của ngày đang lọc.</summary>
    [ObservableProperty]
    private int _preparedCount;

    /// <summary>Shop này đã được kiểm tra XONG trong lượt chạy hiện tại (kể cả lỗi/bỏ qua) — nguồn của dấu tick.
    /// Xóa sạch khi lượt chạy mới bắt đầu (phiên đọc lại danh sách shop).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTick))]
    [NotifyPropertyChangedFor(nameof(ShowLoiDiaChi))]
    private bool _daKiemTra;

    /// <summary>Đang check chính shop này (vòng quay + chữ "đang kiểm tra…" thay cho số).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTick))]
    [NotifyPropertyChangedFor(nameof(ShowLoiDiaChi))]
    private bool _isChecking;

    /// <summary>Shop đang có banner lỗi địa chỉ chưa đóng (X) — hiện dấu X đỏ trước tên shop / cột tiến độ.
    /// Bền tới khi user bấm X trên banner (không theo vòng chạy).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTick))]
    [NotifyPropertyChangedFor(nameof(ShowLoiDiaChi))]
    private bool _coLoiDiaChi;

    /// <summary>Hiện dấu X đỏ lỗi địa chỉ: có lỗi và không đang quay (đang quay thì vòng quay thế chỗ).</summary>
    public bool ShowLoiDiaChi => CoLoiDiaChi && !IsChecking;

    /// <summary>Hiện dấu TICK: đã kiểm tra xong shop này NHƯNG không còn quay và KHÔNG đang lỗi địa chỉ
    /// (lỗi địa chỉ ưu tiên hiện X đỏ).</summary>
    public bool ShowTick => DaKiemTra && !IsChecking && !CoLoiDiaChi;
}
