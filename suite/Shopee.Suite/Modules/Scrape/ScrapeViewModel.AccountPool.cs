using Shopee.Core.Accounts;
using Shopee.Modules.MultiBrave;
using Shopee.Suite.Infrastructure;

namespace Shopee.Suite.Modules.Scrape;

// Partial của ScrapeViewModel: KHO ĐÓNG KHUNG tk Shopee cấp cho MỘT job BigSeller — pure move.
public sealed partial class ScrapeViewModel
{
    // KHO ĐÓNG KHUNG cho 1 job BigSeller: nhận MỘT khung tk Shopee CỐ ĐỊNH (cấp lúc start, đã gỡ khỏi kho
    // chung nên các job RỜI nhau), CHỈ xoay vòng TRONG khung → BigSeller chỉ thấy ngần ấy thiết bị ổn định,
    // tái dùng profile bền (import 1 lần) → KHÔNG churn → không bị đá phiên. Captcha → LOẠI khỏi khung NGAY
    // (bỏ grace) + bù tk thay thế; hết tk dư → BorrowAsync trả null → worker dừng → "hết tk → dừng job".
    private sealed class SessionAccountPool : IScrapeAccountPool
    {
        private readonly string _sheet;
        private readonly object _lock = new();
        private readonly List<ShopeeAccount> _frame;
        private readonly HashSet<string> _borrowed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dropped = new(StringComparer.Ordinal);   // captcha → loại khỏi khung
        private readonly Dictionary<string, DateTimeOffset> _cooldown = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _fail = new(StringComparer.Ordinal);
        private long _lru;
        private const int ShortCdSec = 15, LongCdSec = 90, SetAsideAfter = 2;

        // BÙ TK THAY THẾ: khi captcha loại tk khỏi khung, giữ cỡ khung bằng cách xin 1 tk RẢNH từ kho chung
        // (khóa lease xuyên máy) → job không cạn khung phải chạy lại. null = không bù (giữ hành vi cũ).
        private readonly int _targetSize;
        private readonly Func<IReadOnlyCollection<string>, CancellationToken, Task<ShopeeAccount?>>? _acquireReplacement;
        private readonly Action<string>? _log;
        private readonly SemaphoreSlim _topUpGate = new(1, 1);
        private long _noReplUntilTick;   // Environment.TickCount64: backoff sau khi kho hết tk dư (khỏi dội Hub)

        public SessionAccountPool(string sheet, IEnumerable<ShopeeAccount> frame,
            Func<IReadOnlyCollection<string>, CancellationToken, Task<ShopeeAccount?>>? acquireReplacement = null,
            Action<string>? log = null)
        {
            _sheet = sheet;
            _frame = frame.ToList();
            _lru = _frame.Select(a => a.LastUsedTick).DefaultIfEmpty(0).Max();
            _targetSize = _frame.Count;   // cỡ khung ban đầu (đã trừ tk máy khác giữ) — mốc để bù cho đủ
            _acquireReplacement = acquireReplacement;
            _log = log;
        }

        public async Task<ScrapeAccountSpec?> BorrowAsync(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int usable, borrowedCount;
                lock (_lock)
                {
                    var now = DateTimeOffset.UtcNow;
                    var pick = _frame
                        .Where(a => !a.Disabled && !_dropped.Contains(a.Id) && !_borrowed.Contains(a.Id)
                                    && (!_cooldown.TryGetValue(a.Id, out var until) || until <= now))
                        .OrderBy(a => a.LastUsedTick)        // nghỉ lâu nhất trong khung trước → luân phiên nghỉ
                        .FirstOrDefault();
                    if (pick is not null)
                    {
                        _borrowed.Add(pick.Id);
                        _cooldown.Remove(pick.Id);
                        ShopeeAccountUsage.Shared.MarkInUse(pick.Id);
                        return ShopeeAccountSpecFactory.ToScrapeSpec(pick, _sheet);
                    }
                    usable = _frame.Count(a => !a.Disabled && !_dropped.Contains(a.Id));
                    borrowedCount = _borrowed.Count;
                }
                // Khung THIẾU so với cỡ ban đầu (captcha loại tk) → xin 1 tk THAY THẾ từ kho (khóa lease xuyên máy).
                // Có backoff sau khi kho hết tk dư để khỏi dội Hub liên tục.
                if (usable < _targetSize && Environment.TickCount64 >= Interlocked.Read(ref _noReplUntilTick))
                {
                    if (await TryTopUpAsync(ct).ConfigureAwait(false)) continue;   // đã bù → vòng lại mượn tk mới
                    Interlocked.Exchange(ref _noReplUntilTick, Environment.TickCount64 + 20_000);   // hết tk dư → nghỉ 20s
                }
                // Không có tk dùng được NGAY + không bù được: hết hẳn (mọi tk Disabled/loại) + không ai đang mượn
                // → null → worker dừng (hết tk → dừng job). Còn tk đang mượn/cooldown → chờ.
                if (usable == 0 && borrowedCount == 0) return null;
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }

        /// <summary>Xin 1 tk THAY THẾ từ kho chung (đã khóa lease) và thêm vào khung để giữ đủ cỡ. true = đã bù
        /// (hoặc khung đã đủ do worker khác vừa bù); false = kho hết tk dư. Nối tiếp 1-tại-1-thời-điểm.</summary>
        private async Task<bool> TryTopUpAsync(CancellationToken ct)
        {
            if (_acquireReplacement is null) return false;
            await _topUpGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                IReadOnlyCollection<string> frameIds;
                lock (_lock)
                {
                    if (_frame.Count(a => !a.Disabled && !_dropped.Contains(a.Id)) >= _targetSize)
                        return true;   // worker khác vừa bù đủ → coi như thành công, vòng lại mượn
                    frameIds = _frame.Select(a => a.Id).ToArray();
                }
                var repl = await _acquireReplacement(frameIds, ct).ConfigureAwait(false);
                if (repl is null) return false;
                lock (_lock) _frame.Add(repl);
                _log?.Invoke($"🔁 bù 1 tk thay thế \"{repl.DisplayName}\" vào khung (giữ đủ cỡ, khỏi cạn phải chạy lại).");
                return true;
            }
            finally { _topUpGate.Release(); }
        }

        public void Release(ScrapeAccountSpec spec)
        {
            lock (_lock)
            {
                if (Find(spec.Id) is { } a) a.LastUsedTick = ++_lru;
                _fail.Remove(spec.Id);
                _cooldown.Remove(spec.Id);
                _borrowed.Remove(spec.Id);
            }
            ShopeeAccountUsage.Shared.MarkReleased(spec.Id);
        }

        public AccountCooldown Cooldown(ScrapeAccountSpec spec)
        {
            int secs; bool setAside;
            lock (_lock)
            {
                var n = _fail[spec.Id] = _fail.GetValueOrDefault(spec.Id) + 1;
                setAside = n >= SetAsideAfter;
                secs = setAside ? LongCdSec : ShortCdSec;
                _cooldown[spec.Id] = DateTimeOffset.UtcNow.AddSeconds(secs);
                if (Find(spec.Id) is { } a) a.LastUsedTick = ++_lru;
                _borrowed.Remove(spec.Id);
            }
            ShopeeAccountUsage.Shared.MarkReleased(spec.Id);
            return new AccountCooldown(secs, setAside);
        }

        public void Quarantine(ScrapeAccountSpec spec)
        {
            // Captcha → LOẠI tk khỏi khung NGAY (bỏ grace). Khung thiếu → BorrowAsync tự bù tk thay thế
            // (TryTopUpAsync); hết tk dư → hết hẳn → BorrowAsync null → dừng job. Việc xóa profile do handler ngoài lo.
            lock (_lock)
            {
                _dropped.Add(spec.Id);
                _borrowed.Remove(spec.Id);
                _cooldown.Remove(spec.Id);
            }
            ShopeeAccountUsage.Shared.MarkReleased(spec.Id);
        }

        private ShopeeAccount? Find(string id) => _frame.FirstOrDefault(a => a.Id == id);
    }
}
