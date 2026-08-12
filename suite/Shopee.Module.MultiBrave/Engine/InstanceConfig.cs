using System.Text.Json.Serialization;

namespace OpenMultiBraveLauncherV3;

public sealed class InstanceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountId { get; set; } = "";
    public string Label { get; set; } = "";
    public string KiotProxyKey { get; set; } = "";
    public string Region { get; set; } = "random";
    public string ProxyType { get; set; } = "http";

    /// <summary>Proxy thủ công (host:port hoặc http://…) — dùng khi không có KiotProxy key.</summary>
    public string ManualProxy { get; set; } = "";

    /// <summary>
    /// Bắt buộc có proxy sống mới mở profile. Khi bật: không có proxy / proxy chết
    /// → KHÔNG mở Brave (tránh login Shopee bằng IP máy). Mặc định bật.
    /// </summary>
    public bool RequireProxy { get; set; } = true;
    public string ProfileRelativePath { get; set; } = "";

    public bool CreateNewProfileOnNextStart { get; set; }
    public string ShopeeAccountLogin { get; set; } = "";
    public bool OpenWithShopeeAccount { get; set; }

    /// <summary>Tên tài khoản BigSeller (kho đích) của lượt scrape này — hiển thị trên overlay thông báo.</summary>
    public string BigSellerAccountName { get; set; } = "";

    /// <summary>Tự đóng profile (tắt Brave) khi chạy xong hết link.</summary>
    public bool AutoCloseProfileOnFinish { get; set; } = true;

    /// <summary>Sheet data.xlsx — nhập trên launcher (nguồn đúng).</summary>
    public string DataSheet { get; set; } = "";

    /// <summary>Đường dẫn workbook của instance (per-instance để chạy SONG SONG nhiều BigSeller mỗi
    /// workbook khác nhau — thay cho biến static dùng chung).</summary>
    public string WorkbookPath { get; set; } = "";

    /// <summary>Tk BigSeller lấy link scrape từ kho Hub (Postgres) thay vì <see cref="WorkbookPath"/>. Bật →
    /// LauncherRunnerLoop nạp link/tổng-dòng qua HubClient theo <see cref="HubAccountId"/>. RIÊNG-MÁY: chỉ mang
    /// từ ScrapeRunner sang engine trong 1 lượt chạy (không persist — không đưa vào chữ ký sync).</summary>
    public bool UseHubData { get; set; }

    /// <summary>Id tk BigSeller dùng KHOÁ kho Hub khi <see cref="UseHubData"/> bật (tham số acct của
    /// /products/*). TÁCH khỏi <see cref="AccountId"/> (vốn là khoá auto-login BigSeller trong scrape) để
    /// KHÔNG chạm luồng đăng nhập hiện có — cờ này chỉ đổi NGUỒN DỮ LIỆU link.</summary>
    public string HubAccountId { get; set; } = "";

    /// <summary>Khoảng nghỉ giữa 2 link, tính bằng GIÂY — trước đây là 2 hằng cứng trong LauncherRunnerLoop.
    /// Nguồn: cấu hình Scrape của máy hoặc tham số Hub giao cho lượt việc; quy đổi giây→ms CHỈ ở
    /// <see cref="Shopee.Core.Scrape.ScrapeRestWindow"/>. RIÊNG-LƯỢT-CHẠY (không persist, không vào chữ ký sync).
    /// Mặc định = đúng 120/240s (2 hằng cũ) để bản không đặt gì chạy y như trước.</summary>
    public int RestMinSeconds { get; set; } = Shopee.Core.Scrape.ScrapeRestWindow.DefaultMinSeconds;
    public int RestMaxSeconds { get; set; } = Shopee.Core.Scrape.ScrapeRestWindow.DefaultMaxSeconds;

    /// <summary>Từ dòng — nhập trên launcher (nguồn đúng).</summary>
    public int? StartRow { get; set; }

    /// <summary>Đến dòng — nhập trên launcher (nguồn đúng).</summary>
    public int? EndRow { get; set; }

    /// <summary>Dòng sẽ chạy khi bấm Chạy / auto (sửa được; gợi ý từ tiến độ).</summary>
    public int? NextRunRow { get; set; }

    /// <summary>Dòng hoàn thành gần nhất (từ extension).</summary>
    public int? LastCompletedRow { get; set; }

    /// <summary>Dòng đang xử lý / dừng giữa chừng (từ extension).</summary>
    public int? CurrentRow { get; set; }

    public string? LastSku { get; set; }
    public string? RunnerPhase { get; set; }
    public bool? RunnerRunning { get; set; }
    public string? LastRunnerMessage { get; set; }

    /// <summary>
    /// Profile bị captcha chặn TRONG LÚC CHẠY AUTO → đánh dấu lỗi và đẩy sang tab Error.
    /// CHỈ bật ở phiên auto; chạy manual có người kiểm soát nên không đánh dấu (giữ ở tab Normal).
    /// Xoá khi user bấm "Đã giải captcha" hoặc khi instance được chạy lại.
    /// </summary>
    public bool CaptchaError { get; set; }
    /// <summary>URL trang lúc dính captcha (để lưu vào tk Shopee → "Kiểm tra tk lỗi" mở đúng trang đó).</summary>
    public string? CaptchaUrl { get; set; }
    public List<RunnerLogEntry> RunLog { get; set; } = [];
    public DateTimeOffset? ProgressSyncedAt { get; set; }

    // ── SỔ DÒNG BỎ QUA (skip-ledger) ────────────────────────────────────────────────────────────────
    // Dòng mà runner loop KHÔNG cào được nhưng vẫn chạy tiếp sang dòng kế (nhóm lỗi thường). Không ghi
    // lại thì dòng đó nằm gọn trong khoảng [from..lastDone] mà ScrapeRunner báo là "đã cào" → không bao
    // giờ chạy lại, không dấu vết. Ghi từ luồng engine, đọc (diff) từ luồng runner → phải khoá.
    // KHÔNG persist (chỉ sống trong 1 lượt chạy của chunk): field private nên System.Text.Json bỏ qua.
    private readonly object _failedRowsLock = new();
    private readonly List<(int Row, string Reason)> _failedRows = [];

    /// <summary>Ghi nhận 1 dòng KHÔNG cào được (dedup theo dòng, giữ lý do lần đầu). Thread-safe.</summary>
    public void AddFailedRow(int row, string reason)
    {
        if (row <= 0) return;
        lock (_failedRowsLock)
        {
            if (_failedRows.Any(x => x.Row == row)) return;
            _failedRows.Add((row, string.IsNullOrWhiteSpace(reason) ? "không cào được" : reason.Trim()));
        }
    }

    /// <summary>Bản sao sổ dòng bỏ qua (để runner diff ra phần MỚI rồi báo ra ngoài). Thread-safe.</summary>
    public IReadOnlyList<(int Row, string Reason)> SnapshotFailedRows()
    {
        lock (_failedRowsLock) return _failedRows.ToArray();
    }

    [JsonIgnore]
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Label) ? $"Instance {Id[..Math.Min(8, Id.Length)]}" : Label.Trim();

    public void EnsureProfileRelativePath()
    {
        if (string.IsNullOrWhiteSpace(ProfileRelativePath))
            ProfileRelativePath = Path.Combine("profiles", Id);
    }

    public static InstanceConfig CreateNew(int index)
    {
        var id = Guid.NewGuid().ToString("N");
        return new InstanceConfig
        {
            Id = id,
            Label = $"Instance {index}",
            ProfileRelativePath = Path.Combine("profiles", id),
        };
    }

    /// <summary>Chỉ cập nhật tiến độ chạy — không ghi đè sheet / từ dòng / đến dòng trên form.
    /// <paramref name="log"/> (tuỳ chọn) nhận 1 dòng khi tiến độ đọc được bị BỎ vì không thuộc khối này.</summary>
    public void ApplyExtensionProgress(ExtensionRunnerState state, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(DataSheet) && !string.IsNullOrWhiteSpace(state.SheetName))
            DataSheet = state.SheetName.Trim();

        if (StartRow is null or < 1 && state.StartRow is > 0)
            StartRow = state.StartRow;

        if (EndRow is null or < 1 && state.EndRow is > 0)
            EndRow = state.EndRow;

        // Tiến độ đọc từ extension chỉ được nhận khi CHẮC là của KHỐI NÀY. Profile Brave dùng lại theo tk
        // Shopee nên runnerState còn nguyên tiến độ lượt trước (sheet khác / khối khác); nhận mù thì
        // lastCompletedRow=5000 của hôm qua thành "đã xong" cho khối 2–12 → cả khối bị coi là đã cào.
        if (!ProgressBelongsToThisBlock(state, out var why))
        {
            log?.Invoke($"⚠ Bỏ qua tiến độ extension KHÔNG thuộc khối đang chạy ({why}) — giữ tiến độ hiện có.");
        }
        else
        {
            if (state.LastCompletedRow is > 0)
                LastCompletedRow = state.LastCompletedRow;

            if (state.CurrentRow is > 0)
                CurrentRow = state.CurrentRow;
        }

        if (!string.IsNullOrWhiteSpace(state.LastSku))
            LastSku = state.LastSku.Trim();

        if (!string.IsNullOrWhiteSpace(state.Phase))
            RunnerPhase = state.Phase.Trim();

        if (state.Running is not null)
            RunnerRunning = state.Running;

        if (!string.IsNullOrWhiteSpace(state.LastMessage))
            LastRunnerMessage = state.LastMessage.Trim();

        ProgressSyncedAt = DateTimeOffset.Now;
    }

    /// <summary>Tiến độ (LastCompletedRow/CurrentRow) đọc từ extension có ĐÚNG là của khối đang chạy không.
    /// Hai điều kiện, chỉ kiểm khi có đủ dữ liệu để kiểm (thiếu mốc thì cho qua như trước):
    ///  • sheet của state (nếu có) trùng <see cref="DataSheet"/> (nếu có), so KHÔNG phân biệt hoa thường;
    ///  • mọi giá trị dòng nằm trong <c>[StartRow-1 .. EndRow]</c> (chỉ khi cả 2 mốc đã set).</summary>
    private bool ProgressBelongsToThisBlock(ExtensionRunnerState state, out string why)
    {
        why = "";
        var stateSheet = state.SheetName?.Trim() ?? "";
        var mySheet = DataSheet?.Trim() ?? "";
        if (stateSheet.Length > 0 && mySheet.Length > 0 &&
            !string.Equals(stateSheet, mySheet, StringComparison.OrdinalIgnoreCase))
        {
            why = $"sheet \"{stateSheet}\" ≠ \"{mySheet}\"";
            return false;
        }

        if (StartRow is not > 0 || EndRow is not > 0) return true;   // chưa đủ mốc để kiểm khoảng
        var lo = StartRow.Value - 1;
        var hi = EndRow.Value;
        if (state.LastCompletedRow is { } lc && lc > 0 && (lc < lo || lc > hi))
        {
            why = $"dòng xong {lc} ngoài khối {StartRow}–{EndRow}";
            return false;
        }
        if (state.CurrentRow is { } cur && cur > 0 && (cur < lo || cur > hi))
        {
            why = $"dòng hiện tại {cur} ngoài khối {StartRow}–{EndRow}";
            return false;
        }
        return true;
    }

    [JsonIgnore]
    public bool IsInterruptedMidRun => new ExtensionRunnerState
    {
        SheetName = DataSheet,
        StartRow = StartRow,
        EndRow = EndRow,
        LastCompletedRow = LastCompletedRow,
        CurrentRow = CurrentRow,
        Phase = RunnerPhase,
        Running = RunnerRunning,
    }.IsInterruptedMidRun();

    [JsonIgnore]
    public int? StoppedAtRow => new ExtensionRunnerState
    {
        LastCompletedRow = LastCompletedRow,
        CurrentRow = CurrentRow,
    }.StoppedAtRow;

    [JsonIgnore]
    public string ProgressSummary
    {
        get
        {
            if (StartRow is null &&
                EndRow is null &&
                LastCompletedRow is null)
                return "";

            var from = StartRow?.ToString() ?? "?";
            var to = EndRow?.ToString() ?? "h\u1ebft";
            var next = GetEffectiveRunRow()?.ToString() ?? "-";
            return $"{from}-{to} (ti\u1ebfp {next})";
        }
    }

    /// <summary>Gợi ý dòng tiếp theo từ tiến độ extension.</summary>
    public int? ComputeSuggestedNextRow()
    {
        if (LastCompletedRow is > 0)
            return LastCompletedRow.Value + 1;
        if (CurrentRow is > 0)
            return CurrentRow.Value + 1;
        return StartRow;
    }

    public int? GetEffectiveRunRow()
    {
        var suggested = ComputeSuggestedNextRow();
        if (suggested is > 0 && !IsRowInsideConfiguredRange(suggested.Value))
            suggested = StartRow;

        if (NextRunRow is > 0)
        {
            if (!IsRowInsideConfiguredRange(NextRunRow.Value))
                return suggested;
            return NextRunRow;
        }
        return suggested;
    }

    private bool IsRowInsideConfiguredRange(int row)
    {
        if (StartRow is > 0 && row < StartRow.Value)
            return false;
        if (EndRow is > 0 && row > EndRow.Value)
            return false;
        return true;
    }

    public bool TryValidateRunRow(int row, out string? error)
    {
        if (row < 2)
        {
            error = "Dòng chạy phải ≥ 2.";
            return false;
        }

        if (StartRow is > 0 && row < StartRow.Value)
        {
            error = $"Dòng {row} nhỏ hơn \"Từ dòng\" ({StartRow}).";
            return false;
        }

        if (EndRow is > 0 && row > EndRow.Value)
        {
            error = $"Dòng {row} lớn hơn \"Đến dòng\" ({EndRow}).";
            return false;
        }

        error = null;
        return true;
    }

    [JsonIgnore]
    public int? SuggestedResumeRow => ComputeSuggestedNextRow();
}
