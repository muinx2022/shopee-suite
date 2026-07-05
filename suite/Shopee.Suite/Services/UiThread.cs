using System.Windows;
using System.Windows.Threading;

namespace Shopee.Suite.Services;

/// <summary>
/// Lớp đệm UI-thread — điểm chạm duy nhất của ViewModel với threading của framework UI (hiện là WPF
/// Dispatcher). Khi chuyển Avalonia chỉ đổi ruột file này, ViewModel giữ nguyên. Callback qua
/// <see cref="Post"/>/<see cref="Enqueue"/>/<see cref="UiTimer"/> được bọc try/catch nối vào
/// <see cref="OnError"/> — lưới đỡ lỗi UI không phụ thuộc DispatcherUnhandledException (Avalonia không có).
/// </summary>
public static class UiThread
{
    /// <summary>Lỗi lọt từ callback — App nối vào filter teardown lành tính + crash log + popup.</summary>
    public static Action<Exception>? OnError { get; set; }

    public static bool CheckAccess()
    {
        var d = Application.Current?.Dispatcher;
        return d is null || d.CheckAccess();
    }

    /// <summary>Chạy NGAY nếu đang ở UI thread (hoặc app chưa/không còn Dispatcher), ngược lại xếp hàng
    /// sang UI thread. Thay cho mẫu <c>var d = Application.Current?.Dispatcher; if (d is null || d.CheckAccess()) X(); else d.BeginInvoke(X);</c>.</summary>
    public static void Post(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Run(action);
        else d.BeginInvoke(() => Run(action));
    }

    /// <summary>LUÔN xếp hàng (kể cả đang ở UI thread) — dùng khi cần thoát khỏi call stack hiện tại,
    /// ví dụ tránh chạy trong nested modal pump của dialog đang mở.</summary>
    public static void Enqueue(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) Run(action);
        else d.BeginInvoke(() => Run(action));
    }

    /// <summary>Chạy trên UI thread và đợi xong (await được từ luồng nền).</summary>
    public static Task InvokeAsync(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) { Run(action); return Task.CompletedTask; }
        return d.InvokeAsync(action).Task;
    }

    /// <summary>Chạy trên UI thread và trả kết quả (await được từ luồng nền).</summary>
    public static Task<T> InvokeAsync<T>(Func<T> func)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) return Task.FromResult(func());
        return d.InvokeAsync(func).Task;
    }

    /// <summary>Timer tick trên UI thread (thay DispatcherTimer). Chưa Start — caller tự Start().</summary>
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
