using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// Chốt tuần tự upsert/dismiss Hub theo (accountLogin, shopLogin) trong cùng process —
/// tránh dismiss xong rồi upsert chậm (cùng sự kiện cũ) đè lên Hub.
/// </summary>
internal static class PickupAlertHubGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, byte> DangDay = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string accountLogin, string shopLogin)
        => (accountLogin ?? "").Trim() + "\0" + (shopLogin ?? "").Trim();

    /// <summary>
    /// Nhận chỗ để đẩy một shop; đã có lượt đang bay cho shop đó → trả false (bỏ qua, đừng xếp thêm).
    /// Cờ <c>cho_day</c> chỉ hạ khi Hub NHẬN được, nên không có chốt này thì mỗi nhịp 60s lại xếp thêm một
    /// lệnh cho cùng shop và mỗi lệnh bơm <c>rev</c> của Hub thêm 1.
    /// Gọi <see cref="NhaCho"/> trong <c>finally</c>.
    /// </summary>
    public static bool GiuCho(string accountLogin, string shopLogin)
        => DangDay.TryAdd(Key(accountLogin, shopLogin), 0);

    /// <summary>Nhả chỗ đã giữ bằng <see cref="GiuCho"/>.</summary>
    public static void NhaCho(string accountLogin, string shopLogin)
        => DangDay.TryRemove(Key(accountLogin, shopLogin), out _);

    public static async Task<T> RunAsync<T>(string accountLogin, string shopLogin, Func<Task<T>> action)
    {
        var sem = Gates.GetOrAdd(Key(accountLogin, shopLogin), static _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync().ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    public static Task RunAsync(string accountLogin, string shopLogin, Func<Task> action)
        => RunAsync(accountLogin, shopLogin, async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        });
}
