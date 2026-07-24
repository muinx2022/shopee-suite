using System.Collections.Specialized;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using XuLyDonShopee.App.ViewModels;

namespace XuLyDonShopee.App.Views;

public partial class AccountsView : UserControl
{
    // Đang lắng nghe collection nào (để gỡ đăng ký khi DataContext đổi, tránh rò rỉ / cuộn nhầm).
    private INotifyCollectionChanged? _watchedLog;

    public AccountsView()
    {
        InitializeComponent();
        // DataContext gắn sau khi khởi tạo → theo dõi để đăng ký cuộn khi có VM.
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Khi DataContext (AccountsViewModel) gắn/đổi: đăng ký lắng nghe <c>FilteredLogEntries</c> (collection
    /// ĐANG HIỂN THỊ của panel — đã lọc theo tài khoản) để mỗi khi có dòng mới thì tự cuộn xuống dòng cuối.
    /// Gỡ đăng ký cũ trước để không rò rỉ. Nuốt lỗi an toàn.
    /// </summary>
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_watchedLog is not null)
        {
            _watchedLog.CollectionChanged -= OnLogEntriesChanged;
            _watchedLog = null;
        }

        if (DataContext is AccountsViewModel vm && vm.FilteredLogEntries is INotifyCollectionChanged incc)
        {
            _watchedLog = incc;
            incc.CollectionChanged += OnLogEntriesChanged;
        }
    }

    /// <summary>
    /// Bấm (Tapped) bất kỳ đâu trên một dòng tài khoản (kể cả vùng checkbox — nay cho click xuyên qua) →
    /// TOGGLE tick của ĐÚNG dòng đó (giữ khả năng tick nhiều để chạy nhóm). Việc chọn dòng + đổ Chi tiết/log
    /// do <c>ListBox</c> tự lo qua binding <c>SelectedItem = SelectedRow</c>, nên handler này CHỈ toggle tick.
    /// Lấy row từ DataContext của control bắn sự kiện (Tapped bubble từ control con lên Grid gốc của dòng).
    /// </summary>
    private void OnAccountRowTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AccountRowViewModel row
            && DataContext is AccountsViewModel vm)
        {
            vm.ToggleRowTick(row);
        }
    }

    /// <summary>
    /// Bấm nút "Truy cập TK" trên một dòng TK chưa xác nhận → chọn tài khoản đó + tự mở phiên trình duyệt để
    /// xác minh tay (VM lo). <c>e.Handled = true</c> để click KHÔNG bubble thành Tapped trên Grid dòng (khỏi
    /// vô tình toggle tick). Lấy row từ DataContext của nút (thừa kế từ dòng).
    /// </summary>
    private void OnTruyCapTkClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AccountRowViewModel row
            && DataContext is AccountsViewModel vm)
        {
            vm.TruyCapTk(row);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Có dòng log mới (Add) → cuộn ListBox xuống dòng cuối để luôn thấy hoạt động mới nhất. Marshal về UI
    /// thread cho chắc (FilteredLogEntries chỉ mutate trên UI thread nhưng vẫn phòng hờ). Nuốt mọi lỗi (panel
    /// có thể chưa gắn xong).
    /// </summary>
    /// <summary>Nút "Copy": chép toàn bộ nhật ký đang hiển thị (FilteredLogEntries → Display) vào clipboard để dán
    /// ra ngoài (log nằm trong ListBox từng dòng nên bôi đen không được). Clipboard lấy qua TopLevel. Nuốt lỗi.</summary>
    private async void CopyLog_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountsViewModel vm)
        {
            return;
        }
        try
        {
            var text = string.Join("\n", vm.FilteredLogEntries.Select(x => x.Display));
            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip is null)
            {
                vm.BusyStatus = "Copy log: không lấy được clipboard.";
                return;
            }
            await clip.SetTextAsync(text);
            vm.BusyStatus = $"Đã copy {vm.FilteredLogEntries.Count} dòng log vào clipboard.";
        }
        catch (System.Exception ex)
        {
            vm.BusyStatus = "Copy log lỗi: " + ex.Message;
        }
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var list = this.FindControl<ListBox>("LogList");
                if (list?.ItemCount > 0)
                {
                    list.ScrollIntoView(list.ItemCount - 1);
                }
            }
            catch
            {
                // Bỏ qua: panel chưa dựng xong / control đã tháo.
            }
        });
    }
}
