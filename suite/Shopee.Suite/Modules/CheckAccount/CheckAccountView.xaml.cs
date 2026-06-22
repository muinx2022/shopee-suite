using System.Collections.Specialized;
using System.Windows.Controls;

namespace Shopee.Suite.Modules.CheckAccount;

public partial class CheckAccountView : UserControl
{
    private CheckAccountViewModel? _vm;

    public CheckAccountView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
            _vm.LogLines.CollectionChanged -= OnLogChanged;

        _vm = DataContext as CheckAccountViewModel;
        LogBox.Clear();

        if (_vm is not null)
        {
            _vm.LogLines.CollectionChanged += OnLogChanged;
            // Hiển thị lại log đã có (nếu quay lại module sau khi đã chạy).
            foreach (var line in _vm.LogLines) LogBox.AppendText(line + "\n");
            LogBox.ScrollToEnd();
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            LogBox.Clear();
            return;
        }
        if (e.NewItems is null) return;
        foreach (var item in e.NewItems)
            LogBox.AppendText(item + "\n");
        LogBox.ScrollToEnd();
    }

    // Tải lại lưới "TK OK" mỗi khi chuyển sang tab đó (account mới check xong sẽ xuất hiện).
    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (Tabs.SelectedIndex == 1)
            _vm?.LoadOkGrid();
    }
}
