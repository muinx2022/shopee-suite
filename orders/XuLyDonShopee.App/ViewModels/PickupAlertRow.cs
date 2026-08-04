using CommunityToolkit.Mvvm.ComponentModel;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>Một dòng banner "Cảnh báo: Lỗi địa chỉ. Shop …" trên tab Kết quả — mỗi shop một dòng + nút X.</summary>
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
