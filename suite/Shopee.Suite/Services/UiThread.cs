using System.Windows;
using System.Windows.Threading;

namespace Shopee.Suite.Services;

/// <summary>
/// Lớp đệm UI-thread — điểm chạm duy nhất của ViewModel với threading của framework UI (Dispatcher của WPF).
/// Callback qua <see cref="Post"/>/<see cref="Enqueue"/>/<see cref="UiTimer"/> được bọc try/catch nối vào
/// <see cref="OnError"/> (cùng chính sách với DispatcherUnhandledException — xem App.xaml.cs).
/// <para>
/// Chưa có <see cref="Application"/> (unit test / lỗi rất sớm lúc boot) → chạy THẲNG trên luồng gọi thay vì
/// ném NullReference.
/// </para>
/// </summary>
public static class UiThread
{
    /// <summary>Lỗi lọt từ callback — App nối vào filter teardown lành tính + crash log + popup.</summary>
    public static Action<Exception>? OnError { get; set; }

    private static Dispatcher? Ui => Application.Current?.Dispatcher;

    public static bool CheckAccess() => Ui?.CheckAccess() ?? true;

    /// <summary>Chạy NGAY nếu đang ở UI thread, ngược lại xếp hàng sang UI thread.</summary>
    public static void Post(Action action)
    {
        var ui = Ui;
        if (ui is null || ui.CheckAccess()) Run(action);
        else ui.BeginInvoke(new Action(() => Run(action)));
    }

    /// <summary>LUÔN xếp hàng (kể cả đang ở UI thread) — dùng khi cần thoát khỏi call stack hiện tại
    /// (vd tránh chạy trong nested pump của dialog đang mở).</summary>
    public static void Enqueue(Action action)
    {
        var ui = Ui;
        if (ui is null) Run(action);
        else ui.BeginInvoke(new Action(() => Run(action)));
    }

    /// <summary>Chạy trên UI thread và đợi xong (await được từ luồng nền).</summary>
    public static Task InvokeAsync(Action action)
    {
        var ui = Ui;
        if (ui is null || ui.CheckAccess()) { Run(action); return Task.CompletedTask; }
        return ui.InvokeAsync(() => Run(action)).Task;
    }

    /// <summary>Chạy trên UI thread và trả kết quả (await được từ luồng nền).</summary>
    public static Task<T> InvokeAsync<T>(Func<T> func)
    {
        var ui = Ui;
        if (ui is null || ui.CheckAccess()) return Task.FromResult(func());
        return ui.InvokeAsync(func).Task;
    }

    /// <summary>Timer tick trên UI thread. Chưa Start — caller tự Start().</summary>
    public static UiTimer Interval(TimeSpan interval, Action tick) => new(interval, tick);

    private static void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { OnError?.Invoke(ex); }
    }

    /// <summary>Timer trên UI thread; tick được bọc try/catch → <see cref="OnError"/>.</summary>
    public sealed class UiTimer : IDisposable
    {
        private readonly DispatcherTimer _timer;

        internal UiTimer(TimeSpan interval, Action tick)
        {
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += (_, _) => Run(tick);
        }

        public bool IsEnabled => _timer.IsEnabled;
        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
        public void Dispose() => Stop();
    }
}
