using CommunityToolkit.Mvvm.ComponentModel;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>Một dòng banner "Cảnh báo: Lỗi địa chỉ. Shop …" trên tab Shops — mỗi shop một dòng + nút X.</summary>
public sealed partial class PickupAlertRow : ObservableObject
{
    public PickupAlertRow(string shopLogin)
    {
        ShopLogin = string.IsNullOrWhiteSpace(shopLogin) ? "(không rõ shop)" : shopLogin.Trim();
    }

    public string ShopLogin { get; }

    /// <summary>Chữ hiện trên banner.</summary>
    public string Message => $"Cảnh báo: Lỗi địa chỉ. Shop {ShopLogin}";

    /// <summary>Đang chạy lượt kiểm tra lại địa chỉ cho shop này (nút "Check") — khoá nút, đổi nhãn.</summary>
    [ObservableProperty]
    private bool _dangKiem;

    /// <summary>
    /// Kết quả lượt Check gần nhất, hiện NGAY trên banner. Rỗng = chưa check lần nào trong phiên app này.
    /// <para>Cố ý hiện tại chỗ chứ không chỉ ghi nhật ký: người dùng bấm nút xong mà banner không đổi gì thì
    /// không phân biệt được "vẫn lỗi" với "nút hỏng" — và đó chính là lúc họ bấm lại lần nữa.</para>
    /// </summary>
    [ObservableProperty]
    private string? _ketQuaCheck;

    public bool CoKetQuaCheck => !string.IsNullOrWhiteSpace(KetQuaCheck);

    partial void OnKetQuaCheckChanged(string? value) => OnPropertyChanged(nameof(CoKetQuaCheck));

    /// <summary>Nhãn nút: đổi theo trạng thái để bấm xong thấy ngay là máy đã nhận lệnh.</summary>
    public string NhanNutCheck => DangKiem ? "Đang kiểm…" : "Check";

    partial void OnDangKiemChanged(bool value) => OnPropertyChanged(nameof(NhanNutCheck));
}
