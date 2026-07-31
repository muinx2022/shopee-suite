using System;
using System.Windows;
using System.Windows.Threading;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// Marshal hành động về UI thread cho module Đơn hàng — điểm chạm DUY NHẤT với Dispatcher của WPF.
/// <para>
/// Chưa có <see cref="Application"/> (chạy trong unit test, hoặc lỗi rất sớm lúc boot) → chạy THẲNG trên
/// luồng gọi thay vì ném NullReference: ViewModel của module phải test được headless.
/// </para>
/// </summary>
internal static class UiDispatch
{
    private static Dispatcher? Ui => Application.Current?.Dispatcher;

    /// <summary>Đang ở UI thread (hoặc không có UI) → true.</summary>
    public static bool CheckAccess() => Ui?.CheckAccess() ?? true;

    /// <summary>Xếp hàng sang UI thread; đang ở UI thread thì vẫn xếp hàng (không chạy ngay).</summary>
    public static void Post(Action action)
    {
        var ui = Ui;
        if (ui is null) action();
        else ui.BeginInvoke(action);
    }

    /// <summary>Chạy NGAY nếu đang ở UI thread, ngược lại xếp hàng.</summary>
    public static void Run(Action action)
    {
        if (CheckAccess()) action();
        else Post(action);
    }
}
