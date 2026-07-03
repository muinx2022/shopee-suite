namespace Shopee.Core.Coordination;

/// <summary>
/// Gửi 1 dòng log lên Hub để xem TẬP TRUNG ở tab Log (nhiều máy cùng gửi về 1 chỗ). Best-effort,
/// fire-and-forget; no-op khi chưa kết nối Hub. Gọi từ bất kỳ đâu — tự gắn danh tính máy này.
/// </summary>
public static class HubLog
{
    public static void Info(string text) => Report("info", text);
    public static void Ok(string text) => Report("ok", text);
    public static void Warn(string text) => Report("warn", text);
    public static void Error(string text) => Report("error", text);

    public static void Report(string level, string text)
    {
        var client = CoordinationRuntime.Client;
        if (client is null || string.IsNullOrWhiteSpace(text)) return;
        var m = MachineIdentity.Shared;
        var name = string.IsNullOrWhiteSpace(m.DisplayName) ? m.Hostname : m.DisplayName;
        _ = PostSafe(client, new AppendLogRequest(m.MachineId, name, level, text));
    }

    private static async Task PostSafe(HubClient client, AppendLogRequest req)
    {
        try { await client.AppendLogAsync(req); } catch { /* best-effort */ }
    }
}
