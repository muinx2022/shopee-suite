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

    private static string Key(string accountLogin, string shopLogin)
        => (accountLogin ?? "").Trim() + "\0" + (shopLogin ?? "").Trim();

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
