using Shopee.Suite.Views;

namespace Shopee.Suite.Modules.Data;

/// <summary>
/// Cửa sổ modal thêm/sửa 1 dòng dữ liệu — TẠM là placeholder (port ở ĐỢT 2). Giữ 2 hàm dựng như bản cũ để
/// <see cref="DataViewModel"/> không phải sửa; đóng → DialogResult=false nên VM coi như người dùng bấm Hủy
/// (không ghi gì xuống Hub).
/// </summary>
public sealed class RowEditWindow : PortingWindow
{
    public RowEditWindow()
        : base("Dòng dữ liệu", "Thêm / sửa dòng dữ liệu sản phẩm", "đợt 2")
    {
    }

    public RowEditWindow(RowEditViewModel vm) : this()
    {
        DataContext = vm;
    }
}
