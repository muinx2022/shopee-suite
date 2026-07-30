using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using XuLyDonShopee.App.ViewModels;

namespace XuLyDonShopee.App.Views;

public partial class AccountsView : UserControl
{
    // Đang lắng nghe VM nào (để gỡ đăng ký khi DataContext đổi, tránh rò rỉ / cuộn nhầm).
    private INotifyPropertyChanged? _watchedVm;

    public AccountsView()
    {
        InitializeComponent();
        // DataContext gắn sau khi khởi tạo → theo dõi để đăng ký cuộn khi có VM.
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Khi DataContext (AccountsViewModel) gắn/đổi: đăng ký nghe <c>LogText</c> đổi để tự cuộn xuống dòng cuối.
    /// VM chỉ gán lại <c>LogText</c> mỗi nhịp gom của <c>ActivityLog</c> (~250ms) nên tự cuộn cũng chỉ chạy ngần
    /// ấy, dù log đang dội. Gỡ đăng ký cũ trước để không rò rỉ. Nuốt lỗi an toàn.
    /// </summary>
    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_watchedVm is not null)
        {
            _watchedVm.PropertyChanged -= OnVmPropertyChanged;
            _watchedVm = null;
        }

        if (DataContext is AccountsViewModel vm)
        {
            _watchedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
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

    /// <summary>Nút "Copy": chép toàn bộ nhật ký đang hiển thị (<c>LogText</c> — chính chuỗi TextBox đang vẽ) vào
    /// clipboard để dán ra ngoài. Clipboard lấy qua TopLevel. Nuốt lỗi.</summary>
    private async void CopyLog_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountsViewModel vm)
        {
            return;
        }
        try
        {
            var text = vm.LogText;
            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip is null)
            {
                vm.BusyStatus = "Copy log: không lấy được clipboard.";
                return;
            }
            await clip.SetTextAsync(text);
            var soDong = text.Length == 0 ? 0 : text.Split('\n').Length;
            vm.BusyStatus = $"Đã copy {soDong} dòng log vào clipboard.";
        }
        catch (System.Exception ex)
        {
            vm.BusyStatus = "Copy log lỗi: " + ex.Message;
        }
    }

    /// <summary>
    /// <c>LogText</c> vừa đổi (panel có log mới / đổi tài khoản) → cuộn xuống dòng cuối để luôn thấy hoạt động
    /// mới nhất. Marshal về UI thread cho chắc (VM chỉ gán trên UI thread nhưng vẫn phòng hờ) và ĐỌC độ dài từ
    /// chính TextBox sau khi binding đã áp. Nuốt mọi lỗi (panel có thể chưa gắn xong).
    /// </summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AccountsViewModel.LogText))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // Log là TextBox chỉ-đọc: đặt con trỏ về CUỐI → cuộn xuống dòng mới nhất.
                var box = this.FindControl<TextBox>("LogBox");
                if (box is not null)
                {
                    box.CaretIndex = box.Text?.Length ?? 0;
                }
            }
            catch
            {
                // Bỏ qua: panel chưa dựng xong / control đã tháo.
            }
        });
    }
}
