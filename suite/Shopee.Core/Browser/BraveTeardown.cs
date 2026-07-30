namespace Shopee.Core.Browser;

/// <summary>
/// Các bước LỆCH giữa 4 kịch bản kill Brave của app — mỗi bước chỉ một vài bản có, nên là tuỳ chọn:
/// bản không bật thì <see cref="BraveTeardown.KillAndReap"/> bỏ qua ĐÚNG như bản cũ (timing quanh kill Brave
/// rất nhạy — xem BraveJobObject/BraveProcessReaper).
/// </summary>
public sealed record BraveTeardownOptions
{
    /// <summary>Thử đóng ÊM trước khi kill (CloseMainWindow → CDP Browser.close). Chỉ MultiBrave có; phần
    /// thao tác CDP nằm ở caller vì cần kết nối/WS riêng của phiên đó.</summary>
    public Action? GracefulClose { get; init; }

    /// <summary>Chờ tiến trình thoát sau <c>Kill</c> (ms). 0 = không chờ (UpdateProduct/Search).</summary>
    public int WaitForExitMs { get; init; }

    /// <summary>Giết thêm crashpad_handler mồ côi còn giữ profile. Chỉ Search bật (xem BraveProcessReaper).</summary>
    public bool IncludeCrashpadOrphans { get; init; }

    /// <summary>Ngủ ngắn sau khi reaper CÓ giết được tiến trình — chờ khoá profile (delete-pending) buông
    /// trước khi mở lại cùng profile. Chỉ Search (400ms).</summary>
    public int SleepAfterReapMs { get; init; }

    /// <summary>Log của reaper (bản Search không log).</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Kịch bản DỪNG Brave dùng chung: giết tiến trình launcher đang giữ → dọn tận gốc theo
/// <c>--user-data-dir</c> qua <see cref="BraveProcessReaper"/>. Gộp 4 bản bọc ngoài gần trùng
/// (MultiBrave <c>BraveInstanceSession</c>, UpdateProduct <c>BigSellerBraveRunner</c> +
/// <c>BigSellerImportToStoreRunner</c>, Search <c>BraveManager</c>).
/// Các móc RIÊNG của từng luồng (PortCdpHub.ResetSoon, BraveFleet.UnregisterActiveProfile,
/// PortAllocator.Release) vẫn ở caller — chúng không thuộc việc "giết Brave".
/// </summary>
public static class BraveTeardown
{
    /// <summary>
    /// Giết <paramref name="process"/> (nếu còn sống) rồi quét-giết mọi Brave còn sót của
    /// <paramref name="userDataDir"/>. Best-effort, KHÔNG ném. <paramref name="process"/> luôn được
    /// Dispose + đặt <c>null</c>. Trả về số tiến trình reaper đã giết.
    /// </summary>
    public static int KillAndReap(
        ref Process? process, string? userDataDir, BraveTeardownOptions? options = null)
    {
        var opt = options ?? new BraveTeardownOptions();

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    opt.GracefulClose?.Invoke();
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        if (opt.WaitForExitMs > 0)
                            process.WaitForExit(opt.WaitForExitMs);
                    }
                }
            }
            catch { }

            try { process.Dispose(); } catch { }
            process = null;
        }

        // Brave hay fork rồi thoát tiến trình gốc → browser thật + GPU/renderer/utility chạy ở PID khác,
        // Kill(tree) ở trên bỏ sót. Quét & giết theo --user-data-dir duy nhất của profile để không tích tụ
        // zombie qua mỗi vòng xoay.
        return Reap(userDataDir, opt.IncludeCrashpadOrphans, opt.SleepAfterReapMs, opt.Log);
    }

    /// <summary>
    /// CHỈ quét-giết theo <c>--user-data-dir</c> (không có tiến trình nào trong tay để kill trực tiếp) —
    /// dùng khi profile đang bị khoá bởi Brave/crashpad mồ côi. <paramref name="sleepAfterReapMs"/> &gt; 0 và
    /// CÓ giết được thì ngủ chừng ấy để khoá profile (delete-pending) kịp buông. Best-effort, KHÔNG ném.
    /// </summary>
    public static int Reap(
        string? userDataDir,
        bool includeCrashpadOrphans = false,
        int sleepAfterReapMs = 0,
        Action<string>? log = null)
    {
        var killed = 0;
        try
        {
            killed = BraveProcessReaper.KillByUserDataDir(userDataDir, log, includeCrashpadOrphans);
        }
        catch { }

        if (killed > 0 && sleepAfterReapMs > 0)
            Thread.Sleep(sleepAfterReapMs);

        return killed;
    }
}
