namespace Shopee.Core.Accounts;

/// <summary>
/// Theo dõi tình trạng dùng tk Shopee theo THỜI GIAN CHẠY (runtime), dùng chung cho Scrape + Search.
/// CHỈ có ý nghĩa khi đang có ít nhất 1 lượt chạy; không chạy gì → mọi tk coi như "Chưa dùng".
/// 3 trạng thái: "Đang dùng" (đang mở/chiếm), "Đã dùng" (đã dùng trong lượt này rồi nhả), "Chưa dùng".
/// </summary>
public sealed class ShopeeAccountUsage
{
    public static ShopeeAccountUsage Shared { get; } = new();

    private readonly object _lock = new();
    private int _activeRuns;                                   // số lượt chạy (scrape/search) đang mở
    private readonly HashSet<string> _inUse = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);   // đã dùng ít nhất 1 lần trong giai đoạn đang chạy
    private readonly HashSet<string> _captcha = new(StringComparer.Ordinal); // đang dính captcha trong lượt chạy này

    // SỔ GIỮ CHỖ CHÉO-MODULE (cross-module reservation). KHÁC _inUse (chỉ để hiển thị tk đang mở trong 1
    // cửa sổ NGAY lúc này): _reserved là quyền SỞ HỮU tk giữa các module chạy song song.
    //  • Scrape giữ CẢ KHUNG suốt 1 job (kể cả tk đang nghỉ trong khung — vì sẽ xoay vòng tới).
    //  • Search giữ 1 tk khi đang crawl 1 link/keyword.
    // Module khác hỏi TryReserve/IsReserved TRƯỚC khi mượn → tk module kia đang giữ thì bỏ qua, lấy tk khác
    // → KHÔNG bao giờ 2 module mở CÙNG 1 tk Shopee cùng lúc (tránh Shopee thấy 1 tk ở 2 phiên → captcha/khóa).
    private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);

    // SỔ "ĐANG GIỮ LEASE HUB" (per-MÁY, mọi module gộp chung). KHÁC _reserved (per-borrow, Search nhả sớm giữa
    // các link): _hubLeased giữ SUỐT thời gian máy này còn giữ lease Hub của tk (khung Scrape / nhóm Search +
    // tk BÙ). Điểm LẤY TK MỚI (bù / đóng khung) phải loại tk _hubLeased để module khác trên CÙNG máy KHÔNG
    // "cướp" tk mà module này đang giữ lease Hub → tránh: module kia xong trước xóa dòng lease (machine-scoped,
    // 1 dòng/tk) khiến module còn chạy mất lease → máy KHÁC lấy trùng tk → Shopee thấy 2 phiên → captcha/khóa.
    private readonly HashSet<string> _hubLeased = new(StringComparer.Ordinal);

    /// <summary>Phát khi trạng thái đổi → UI làm mới cột "Tình trạng".</summary>
    public event Action? Changed;

    /// <summary>Có lượt chạy nào đang mở không (quyết định có hiển thị trạng thái hay coi tất cả "Chưa dùng").</summary>
    public bool Active { get { lock (_lock) return _activeRuns > 0; } }

    /// <summary>Bắt đầu 1 lượt chạy (Scrape/Search). Gọi đôi với <see cref="EndRun"/>.</summary>
    public void BeginRun()
    {
        lock (_lock) _activeRuns++;
        Changed?.Invoke();
    }

    /// <summary>Kết thúc 1 lượt chạy. Khi không còn lượt nào → xoá hết dấu (mọi tk về "Chưa dùng").</summary>
    public void EndRun()
    {
        lock (_lock)
        {
            if (--_activeRuns <= 0)
            {
                _activeRuns = 0;
                _inUse.Clear();
                _used.Clear();
                _captcha.Clear();
                _reserved.Clear();   // không còn lượt chạy nào → nhả mọi giữ chỗ (lưới an toàn chống rò)
                _hubLeased.Clear();  // và mọi dấu "đang giữ lease Hub" (lưới an toàn)
            }
        }
        Changed?.Invoke();
    }

    public void MarkInUse(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_lock) { _inUse.Add(id); _used.Add(id); }
        Changed?.Invoke();
    }

    public void MarkInUse(IEnumerable<string> ids)
    {
        lock (_lock) foreach (var id in ids) if (!string.IsNullOrEmpty(id)) { _inUse.Add(id); _used.Add(id); }
        Changed?.Invoke();
    }

    public void MarkReleased(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_lock) _inUse.Remove(id);
        Changed?.Invoke();
    }

    public void MarkReleased(IEnumerable<string> ids)
    {
        lock (_lock) foreach (var id in ids) _inUse.Remove(id);
        Changed?.Invoke();
    }

    /// <summary>Đánh dấu tk đang DÍNH CAPTCHA trong lượt chạy này (hiển thị "⚠ Captcha" ở cột Tình trạng).
    /// Không còn "đang dùng" nữa (đã bị tách khỏi vòng để xử lý tay).</summary>
    public void MarkCaptcha(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_lock) { _captcha.Add(id); _inUse.Remove(id); _used.Add(id); }
        Changed?.Invoke();
    }

    // ── GIỮ CHỖ CHÉO-MODULE ──────────────────────────────────────────────────────────
    /// <summary>Giành quyền dùng 1 tk. true = giành được; false = module khác đang giữ (hãy lấy tk khác).</summary>
    public bool TryReserve(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (_lock) return _reserved.Add(id);
    }

    /// <summary>Giành NHIỀU tk (best-effort) — trả về danh sách tk GIÀNH ĐƯỢC (bỏ tk module khác đang giữ).
    /// Dùng cho Scrape đóng cả khung 1 lần.</summary>
    public List<string> TryReserveMany(IEnumerable<string> ids)
    {
        var got = new List<string>();
        lock (_lock)
            foreach (var id in ids)
                if (!string.IsNullOrEmpty(id) && _reserved.Add(id)) got.Add(id);
        return got;
    }

    /// <summary>tk có đang được module nào giữ chỗ không (để bỏ qua khi mượn).</summary>
    public bool IsReserved(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (_lock) return _reserved.Contains(id);
    }

    /// <summary>Nhả quyền dùng 1 tk (cho module khác mượn được).</summary>
    public void ReleaseReservation(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_lock) _reserved.Remove(id);
    }

    /// <summary>Nhả quyền dùng nhiều tk (vd cả khung khi 1 job Scrape kết thúc).</summary>
    public void ReleaseReservation(IEnumerable<string> ids)
    {
        lock (_lock) foreach (var id in ids) if (!string.IsNullOrEmpty(id)) _reserved.Remove(id);
    }

    // ── GIỮ LEASE HUB (per-MÁY, gộp mọi module) ──────────────────────────────────────
    /// <summary>Đánh dấu máy này ĐANG giữ lease Hub các tk (gọi khi Reserve trên Hub thành công). Điểm lấy tk
    /// MỚI của module khác sẽ né các tk này (chống module kia xóa nhầm dòng lease đang dùng).</summary>
    public void MarkHubLeased(IEnumerable<string> ids)
    {
        lock (_lock) foreach (var id in ids) if (!string.IsNullOrEmpty(id)) _hubLeased.Add(id);
    }

    /// <summary>Gỡ dấu giữ lease Hub (gọi khi nhả lease Hub ở finally). An toàn gọi cả tk không có dấu.</summary>
    public void UnmarkHubLeased(IEnumerable<string> ids)
    {
        lock (_lock) foreach (var id in ids) if (!string.IsNullOrEmpty(id)) _hubLeased.Remove(id);
    }

    /// <summary>tk có đang được máy này giữ lease Hub không (để điểm lấy tk MỚI né, khỏi cướp lease chéo-module).</summary>
    public bool IsHubLeased(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (_lock) return _hubLeased.Contains(id);
    }

    /// <summary>Trạng thái hiển thị của 1 tk: "⚠ Captcha" | "Đang dùng" | "Đã dùng" | "Chưa dùng".</summary>
    public string Status(string id)
    {
        lock (_lock)
        {
            if (_activeRuns <= 0) return "Chưa dùng";
            if (_captcha.Contains(id)) return "⚠ Captcha";   // ưu tiên cao nhất — đang vướng captcha
            if (_inUse.Contains(id)) return "Đang dùng";
            if (_used.Contains(id)) return "Đã dùng";
            return "Chưa dùng";
        }
    }
}
