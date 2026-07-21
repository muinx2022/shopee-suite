using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Media;
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
public sealed class RibbonGroup
{
    public RibbonGroup(string title, IReadOnlyList<object> items)
    {
        Title = title;
        Items = items;
    }

    public string Title { get; }

    /// <summary>Các item trong nhóm (RibbonScreenItem | RibbonActionItem | RibbonToggleItem).</summary>
    public IReadOnlyList<object> Items { get; }
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
        Icon = StreamGeometry.Parse(iconData);
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

    /// <summary>Với module đơn hàng: index màn con (0-3) để set <c>MainViewModel.SelectedNavIndex</c>; -1 = không dùng.</summary>
    public int NavIndex { get; }

    /// <summary>Tab chứa nút (Shell gán sau khi dựng xong cây tab).</summary>
    internal RibbonTab? OwnerTab { get; set; }

    /// <summary>true khi màn này đang hiển thị → tô accent như Office.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Lệnh chuyển sang màn này (Shell gán sau khi dựng cây tab).</summary>
    public ICommand? ActivateCommand { get; set; }
}

/// <summary>
/// Nút HÀNH ĐỘNG trên ribbon (glyph to trên, nhãn dưới): bind thẳng một command CÓ SẴN của ViewModel.
/// Enable/Disable tự theo CanExecute của command (không chế fallback).
/// </summary>
public sealed class RibbonActionItem
{
    public RibbonActionItem(string title, string glyph, ICommand command, string? toolTip = null)
    {
        Title = title;
        Glyph = glyph;
        Command = command;
        ToolTip = toolTip;
    }

    public string Title { get; }

    /// <summary>Ký tự biểu tượng (glyph/emoji) hiển thị trên nút.</summary>
    public string Glyph { get; }

    public ICommand Command { get; }

    public string? ToolTip { get; }
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
