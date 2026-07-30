using Shopee.Core.Accounts;
using Shopee.Modules.MultiBrave;
using Shopee.Suite.Infrastructure;

namespace Shopee.Suite.Modules.Scrape;

// Partial của ScrapeViewModel: phiên chạy (kho tk chung + sổ job + cấp KHUNG) và handle từng job — pure move.
public sealed partial class ScrapeViewModel
{
    // ── Phiên chạy + handle từng job (để chạy/dừng RIÊNG từng tk khi đang run) ──
    private sealed class RunSession
    {
        public required string SourceUserData;
        public readonly List<ShopeeAccount> Available = [];        // kho tk Shopee CÒN LẠI (reserve) — đã trừ các khung
        public readonly object AllocLock = new();
        public readonly CancellationTokenSource MasterCts = new(); // huỷ TOÀN phiên (Stop)
        // Sổ job theo tk (key = BigSeller Account.Id). Tự lo lock + dedup + chốt (Finalizing cũ = _sealed nội bộ).
        public readonly PerAccountJobRegistry<JobHandle> Jobs = new();
        public int JobSeq;          // tăng dần — namespace key lưới UI (thay jobIndex cũ)
        public long LruTick;        // bộ đếm vòng-LRU cấp phát tk Shopee (seed cho ClaimFrame)

        // ── Cấp KHUNG tk Shopee cho 1 job BigSeller (đóng khung): lấy (và GỠ khỏi kho chung) tối đa n tk —
        // ưu tiên id đã lưu (resume giữ khung cũ), rồi bù bằng tk nghỉ lâu nhất. Khung các job RỜI nhau
        // (mỗi tk chỉ thuộc 1 khung) → mỗi tk BigSeller chỉ phơi ngần ấy thiết bị. Mỗi tk Shopee có
        // profile bền RIÊNG nên tái dùng trong khung = import BigSeller 1 lần rồi giữ token sống. ──
        // AFFINITY: mineIds = tk "nhà" ở máy NÀY (đã có profile trusted) → ưu tiên sau preferIds; blockedIds =
        // tk đang thuộc máy KHÁC còn online → LOẠI khỏi MỌI vòng (nhường, khỏi tranh trust). Rỗng cả hai khi
        // không có Hub → thứ tự về đúng hành vi cũ (preferIds → ngẫu nhiên).
        public List<ShopeeAccount> ClaimFrame(int n, IReadOnlyList<string>? preferIds,
            IReadOnlyCollection<string> mineIds, IReadOnlyCollection<string> blockedIds)
        {
            lock (AllocLock)
            {
                var frame = new List<ShopeeAccount>();
                // CHỈ lấy tk GIÀNH ĐƯỢC quyền (TryReserve) → KHÔNG đụng tk module khác (Search) đang giữ →
                // 2 module không bao giờ mở cùng 1 tk Shopee. Khung được NHẢ khi job kết thúc (RunOneJobAsync finally).
                // (a) preferIds (resume — giữ khung cũ), LOẠI blocked.
                if (preferIds is not null)
                    foreach (var id in preferIds)
                    {
                        // Né tk module khác đang giữ lease Hub trên máy này (IsHubLeased) — khỏi cướp lease chéo-module.
                        var a = Available.FirstOrDefault(x => x.Id == id && !x.Disabled
                            && !blockedIds.Contains(x.Id) && !ShopeeAccountUsage.Shared.IsHubLeased(x.Id));
                        if (a is not null && ShopeeAccountUsage.Shared.TryReserve(a.Id)) { frame.Add(a); Available.Remove(a); }
                    }
                // (b) mineIds (tk máy này đã "nhà" → tái dùng profile trusted), LOẠI blocked; chỉ lấp khi chưa đủ n.
                if (frame.Count < n)
                    foreach (var id in mineIds)
                    {
                        if (frame.Count >= n) break;
                        var a = Available.FirstOrDefault(x => x.Id == id && !x.Disabled
                            && !blockedIds.Contains(x.Id) && !ShopeeAccountUsage.Shared.IsHubLeased(x.Id));
                        if (a is not null && ShopeeAccountUsage.Shared.TryReserve(a.Id)) { frame.Add(a); Available.Remove(a); }
                    }
                // (c) Lấp đủ số: chọn NGẪU NHIÊN trong kho (tk còn bật + chưa module khác giữ chỗ/giữ lease Hub +
                //     KHÔNG thuộc máy khác) cho đủ n — đây là tk mồ côi / nhà-offline.
                while (frame.Count < n)
                {
                    var candidates = Available.Where(x => !x.Disabled
                        && !blockedIds.Contains(x.Id)
                        && !ShopeeAccountUsage.Shared.IsReserved(x.Id)
                        && !ShopeeAccountUsage.Shared.IsHubLeased(x.Id)).ToList();
                    if (candidates.Count == 0) break;
                    var a = candidates[Random.Shared.Next(candidates.Count)];
                    // Module khác vừa giành mất giữa lúc lọc → bỏ tk này khỏi kho, thử tk khác (không kẹt vì kho co dần).
                    if (!ShopeeAccountUsage.Shared.TryReserve(a.Id)) { Available.Remove(a); continue; }
                    frame.Add(a); Available.Remove(a);
                }
                return frame;
            }
        }
    }

    private sealed class JobHandle
    {
        public required ScrapeTargetViewModel Target;
        public required int Seq;
        public required CancellationTokenSource Cts;   // linked tới MasterCts → dừng RIÊNG 1 tk
        public ScrapeRunner? Runner;
        public Task Task = Task.CompletedTask;
        public bool Force;   // Chạy đè: bỏ qua khoá hub của máy khác
    }
}
