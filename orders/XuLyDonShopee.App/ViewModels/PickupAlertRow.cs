using CommunityToolkit.Mvvm.ComponentModel;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>
/// Một dòng banner "Cảnh báo: Lỗi địa chỉ. Shop …" trên tab Shops — mỗi shop một dòng + nút Đóng.
/// <para>
/// KHÔNG có nút "Check" thủ công nữa (bỏ 10/08/2026, người dùng chốt). Banner tự hết ở vòng chạy kế: shop nào
/// còn treo cảnh báo thì vòng lặp tự chạy lại bước đặt địa chỉ dù shop đó không có đơn nào — đặt được thì gỡ
/// banner + báo Hub + gỡ ở máy khác, vẫn lỗi thì banner ở lại. Xem
/// <c>ShopFlowRunner.QuyetDinhBuocDiaChi</c>. Nút Đóng giữ nguyên cho lúc người dùng muốn tự tay dẹp.
/// </para>
/// </summary>
public sealed partial class PickupAlertRow : ObservableObject
{
    public PickupAlertRow(string shopLogin)
    {
        ShopLogin = string.IsNullOrWhiteSpace(shopLogin) ? "(không rõ shop)" : shopLogin.Trim();
    }

    public string ShopLogin { get; }

    /// <summary>Chữ hiện trên banner.</summary>
    public string Message => $"Cảnh báo: Lỗi địa chỉ. Shop {ShopLogin}";
}
