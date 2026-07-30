using Shopee.Suite.Views;

namespace Shopee.Suite.Modules.Accounts;

/// <summary>
/// Cửa sổ "Import tài khoản / proxy" — TẠM là placeholder (port ở ĐỢT 2). Giữ nguyên API mà
/// <see cref="AccountsViewModel"/> đang dùng (<see cref="Logins"/>, <see cref="ProxyKeys"/>, DialogResult)
/// nên VM không phải sửa; đóng bằng nút Đóng → DialogResult=false → VM thoát sớm như người dùng bấm Hủy.
/// </summary>
public sealed class ImportAccountsWindow : PortingWindow
{
    public string Logins { get; private set; } = "";
    public string ProxyKeys { get; private set; } = "";

    public ImportAccountsWindow()
        : base("Import tài khoản", "Import tài khoản & proxy", "đợt 2")
    {
    }
}
