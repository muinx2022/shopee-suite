using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Shopee.Suite.ViewModels;

// ══════════════════════════════════════════════════════════════════════════════
// Mô hình dữ liệu cho dải RIBBON (kiểu Word/Excel): mỗi tab có nhiều NHÓM, mỗi nhóm
// có nhiều ITEM. Item chia 3 loại: điều hướng màn (RibbonScreenItem), nút hành động
// bind command sẵn có (RibbonActionItem), và toggle bool (RibbonToggleItem). Toàn bộ
// CHỈ là lớp trình bày — không chứa logic nghiệp vụ; command đều là command CÓ SẴN
// của ViewModel module, ShellViewModel chỉ ráp lại.
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>Một tab trên dải ribbon (Workspace / Cấu hình BigSeller / Đơn hàng / Cài đặt).</summary>
public sealed class RibbonTab
{
    public RibbonTab(string title, IReadOnlyList<RibbonGroup> groups)
    {
        Title = title;
        Groups = groups;
    }

    public string Title { get; }

    /// <summary>Các nhóm nút hiển thị trên dải ribbon khi tab này đang chọn.</summary>
    public IReadOnlyList<RibbonGroup> Groups { get; }
}

/// <summary>Một nhóm trên dải ribbon: khung có nhãn ở đáy + các nút bên trong, ngăn cách nhau bằng divider dọc.</summary>
public sealed partial class RibbonGroup : ObservableObject
{
    public RibbonGroup(string title, IReadOnlyList<object> items)
    {
        Title = title;
        Items = items;
    }

    public string Title { get; }

    /// <summary>Các item trong nhóm (RibbonScreenItem | RibbonActionItem | RibbonToggleItem).</summary>
    public IReadOnlyList<object> Items { get; }

    /// <summary>
    /// Bật/tắt CẢ NHÓM: container nhóm bind <c>IsEnabled</c> vào đây (xem MainWindow.xaml). <c>false</c> →
    /// WPF làm mờ (disable) mọi item con, KHÔNG ẩn. <c>true</c> (mặc định) → item con theo trạng thái
    /// riêng của chúng (nút hành động vẫn tự disable theo CanExecute của command). Dùng để khóa nhóm
    /// "Hành động"/"Tùy chọn" của tab Shopee khi KHÔNG ở màn "Tài khoản".
    /// </summary>
    [ObservableProperty] private bool _isEnabled = true;
}

/// <summary>
/// Nút ĐIỀU HƯỚNG MÀN trên ribbon (icon to trên, nhãn dưới): bấm để chuyển màn đang hiển thị; nút đang
/// mở được tô accent (<see cref="IsActive"/>). <see cref="ScreenVm"/> là ViewModel màn cần hiển thị;
/// riêng module đơn hàng dùng chung một VM (<c>MainViewModel</c>) và đổi màn con qua <see cref="NavIndex"/>.
/// </summary>
public sealed partial class RibbonScreenItem : ObservableObject
{
    public RibbonScreenItem(string title, string iconData, object screenVm, int navIndex = -1, string? toolTip = null)
    {
        Title = title;
        Icon = Geometry.Parse(iconData);
        ScreenVm = screenVm;
        NavIndex = navIndex;
        ToolTip = toolTip;
    }

    public string Title { get; }

    /// <summary>Icon vector (path 24×24) — render qua PathIcon, tô theo Foreground (đổi màu khi active).</summary>
    public Geometry Icon { get; }

    public string? ToolTip { get; }

    /// <summary>ViewModel màn cần hiển thị khi bấm nút này (với đơn hàng: luôn là MainViewModel).</summary>
    public object ScreenVm { get; }

    /// <summary>Với module đơn hàng: index màn con (0-2) để set <c>MainViewModel.SelectedNavIndex</c>; -1 = không dùng.</summary>
    public int NavIndex { get; }

    /// <summary>Tab chứa nút (Shell gán sau khi dựng xong cây tab).</summary>
    internal RibbonTab? OwnerTab { get; set; }

    /// <summary>true khi màn này đang hiển thị → tô accent như Office.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Lệnh chuyển sang màn này (Shell gán sau khi dựng cây tab).</summary>
    public ICommand? ActivateCommand { get; set; }
}

/// <summary>
/// Nút HÀNH ĐỘNG trên ribbon (icon to trên, nhãn dưới): bind thẳng một command CÓ SẴN của ViewModel.
/// Enable/Disable tự theo CanExecute của command (không chế fallback).
/// <para>
/// Icon nhận vào bằng KHÓA tài nguyên (<c>IconStop</c>, <c>IconPlay</c>… — bảng ánh xạ ở đầu
/// <c>suite/Shopee.Suite/Themes/Icons.xaml</c>) rồi tra sang <see cref="Geometry"/> NGAY lúc dựng.
/// Phải tra ở C# vì XAML không lồng được <c>{DynamicResource {Binding …}}</c>; dựng ở đây an toàn vì
/// ShellViewModel chỉ được tạo SAU khi App.xaml đã nạp xong Application.Resources.
/// </para>
/// <para>
/// <see cref="Title"/> và <see cref="ToolTip"/> QUAN SÁT ĐƯỢC (phát INPC) vì nút cập nhật là MỘT nút đổi
/// nhãn theo trạng thái: "Kiểm tra cập nhật" ⇄ "Cập nhật" khi đã tải xong bản mới (người dùng chốt
/// 08/08/2026 — gộp 2 nút trong tab "Phiên bản &amp; cập nhật" về đúng nút ribbon này). ShellViewModel gán
/// lại 2 property đó khi <c>SettingsViewModel.UpdateReady</c> đổi; DataTemplate ở MainWindow.xaml đã bind
/// thường nên nhãn tự cập nhật, không phải sửa XAML. Các nút hành động khác gán 1 lần lúc dựng — thêm INPC
/// KHÔNG đổi hành vi của chúng.
/// </para>
/// </summary>
public sealed partial class RibbonActionItem : ObservableObject
{
    public RibbonActionItem(string title, string iconKey, ICommand command, string? toolTip = null)
    {
        _title = title;
        Icon = LookupIcon(iconKey);
        Command = command;
        _toolTip = toolTip;
    }

    /// <summary>Nhãn dưới icon. Đổi được lúc chạy (nút cập nhật) — xem chú thích của lớp.</summary>
    [ObservableProperty] private string _title;

    /// <summary>Hình icon vector đã tra sẵn; null = không tìm thấy khóa (nút vẫn chạy, chỉ thiếu icon).</summary>
    public Geometry? Icon { get; }

    public ICommand Command { get; }

    /// <summary>Tooltip. Đổi được lúc chạy cùng <see cref="Title"/> (một nút, hai vai trò).</summary>
    [ObservableProperty] private string? _toolTip;

    /// <summary>Tra khóa tài nguyên → Geometry. Không tìm thấy thì trả null thay vì ném: thiếu icon không
    /// đáng làm sập cả dải ribbon lúc khởi động.</summary>
    private static Geometry? LookupIcon(string key)
    {
        if (Application.Current?.TryFindResource(key) is Geometry g)
        {
            return g;
        }
        // Tra hụt là lỗi IM LẶNG (nút vẫn bấm được, chỉ mất icon) → để lại dấu vết cho lần sau dò.
        System.Diagnostics.Trace.WriteLine($"[Ribbon] Không tìm thấy icon '{key}' trong Application.Resources.");
        return null;
    }
}

/// <summary>
/// Nút TOGGLE (checkbox) trên ribbon: bind HAI CHIỀU tới một property bool CÓ SẴN của ViewModel qua cặp
/// get/set delegate. Nghe PropertyChanged của VM nguồn để đồng bộ khi giá trị đổi từ nơi khác.
/// </summary>
public sealed partial class RibbonToggleItem : ObservableObject
{
    private readonly System.Func<bool> _get;
    private readonly System.Action<bool> _set;

    public RibbonToggleItem(string title, INotifyPropertyChanged source, string sourceProperty,
        System.Func<bool> get, System.Action<bool> set, string? toolTip = null)
    {
        Title = title;
        ToolTip = toolTip;
        _get = get;
        _set = set;
        source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == sourceProperty) OnPropertyChanged(nameof(IsChecked));
        };
    }

    public string Title { get; }

    public string? ToolTip { get; }

    public bool IsChecked
    {
        get => _get();
        set
        {
            if (_get() == value) return;
            _set(value);
            OnPropertyChanged();
        }
    }
}
