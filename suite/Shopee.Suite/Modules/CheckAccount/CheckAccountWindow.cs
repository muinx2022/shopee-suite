using Shopee.Suite.Views;

namespace Shopee.Suite.Modules.CheckAccount;

/// <summary>
/// Cửa sổ riêng bọc màn "Check tài khoản" (mở từ nút "Check Acc" trong màn Tài khoản &amp; Proxy) —
/// TẠM là placeholder (port ở ĐỢT 4). DataContext vẫn được gán từ ngoài như cũ.
/// </summary>
public sealed class CheckAccountWindow : PortingWindow
{
    public CheckAccountWindow()
        : base("Check tài khoản", "Check tài khoản Shopee", "đợt 4")
    {
    }
}
