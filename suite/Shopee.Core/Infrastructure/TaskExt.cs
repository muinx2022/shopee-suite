using Shopee.Core.Coordination;

namespace Shopee.Core.Infrastructure;

/// <summary>
/// Tiện ích cho fire-and-forget: chạy 1 <see cref="Task"/> NỀN mà VẪN ghi log khi task lỗi. Khuôn cũ
/// <c>try { _ = XxxAsync(); } catch { }</c> KHÔNG bắt được gì — lỗi nằm TRONG Task (chưa await), không phải
/// tại lúc gọi → mọi exception chìm im lặng. Dùng <see cref="FireAndForget"/> để lỗi ít nhất hiện ở tab Log Hub.
/// </summary>
public static class TaskExt
{
    /// <summary>Bắn <paramref name="task"/> chạy nền; nếu lỗi (không phải huỷ) thì log qua <c>HubLog.Warn</c>
    /// kèm <paramref name="tag"/> để lần được nguồn. Huỷ (OperationCanceledException) coi là bình thường → không log.</summary>
    public static void FireAndForget(Task task, string tag)
    {
        _ = Awaited(task, tag);

        static async Task Awaited(Task t, string tag)
        {
            try { await t.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* huỷ bình thường — không log */ }
            catch (Exception ex) { HubLog.Warn($"⚠ {tag}: {ex.Message}"); }
        }
    }
}
