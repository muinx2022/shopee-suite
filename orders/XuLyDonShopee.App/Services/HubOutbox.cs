using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.Services;

/// <summary>Kết quả một lượt đẩy — <see cref="HubOutboxWorker"/> dùng để quyết backoff (chỉ
/// <see cref="ThatBai"/> mới tính là "đích lỗi"). <see cref="KhongCanDay"/> = hook chưa rót / hàng đợi rỗng /
/// hủy chủ động → KHÔNG tính lỗi, cũng không tính thành công.</summary>
public enum KetQuaDay
{
    KhongCanDay,
    ThanhCong,
    ThatBai,
}

/// <summary>
/// <b>VÒNG CHỜ ĐẨY (outbox)</b> — 4 thân xử lý đẩy hàng tồn dùng CHUNG cho hai đường kích hoạt:
/// <list type="bullet">
/// <item><see cref="AccountSession"/> — đẩy KÉ ngay sau khi một shop sync xong (token của PHIÊN).</item>
/// <item><see cref="HubOutboxWorker"/> — đẩy nền định kỳ theo vòng đời APP (token vòng đời app), nhặt lại hàng
/// tồn khi đích sống lại lúc client đang nghỉ giữa 2 vòng, hoặc khi phiên bị dừng giữa lượt đẩy.</item>
/// </list>
/// Toàn bộ là <c>static</c>, nhận tham số tường minh (accountId + <see cref="AppServices"/> + log + token) —
/// KHÔNG giữ trạng thái phiên. Hàng đợi vẫn là các cờ DB (<c>hub_synced_at</c> / <c>hub_slip_synced_at</c> /
/// <c>gsheet_synced_at</c> / <c>sold_counted_at</c> NULL = còn chờ) nên hai đường kích hoạt tự khớp nhau; chống
/// chạy CHỒNG bằng <see cref="PushGate"/> (toàn tiến trình) ở phía caller.
/// <para>
/// Các thân hàm ở đây được TÁCH CƠ HỌC từ <see cref="AccountSession"/> — giữ NGUYÊN log, thứ tự, cách nuốt lỗi;
/// phần thêm duy nhất là giá trị trả về <see cref="KetQuaDay"/> (caller cũ bỏ qua) để worker biết đích có nhận không.
/// </para>
/// </summary>
public static class HubOutbox
{
    /// <summary>
    /// HÀM THUẦN (test được): đơn <paramref name="p"/> CÒN nghĩa vụ ghi Google Sheet không — tức lượt đẩy sheet
    /// có phải GỬI nó lên Apps Script không. Đây là <b>một nguồn duy nhất</b> cho hai chỗ: nhánh quyết định gửi
    /// trong <see cref="PushOrdersToGsheetAsync"/> và màn chẩn đoán "đơn kết thúc chưa dọn được"
    /// (<see cref="OrderPersistPipeline.ChanDoanDonKetThuc"/>) — hai nơi tự tính riêng là hai luật trôi lệch.
    /// <list type="bullet">
    /// <item><c>false</c> khi đơn HỦY mà CHƯA từng có vận đơn VÀ CHƯA từng ghi sheet: không thuộc sổ theo dõi →
    /// by design không ghi (và vì thế coi như đã xong nghĩa vụ).</item>
    /// <item><c>true</c> khi: chưa từng ghi dòng · có file phiếu để bổ sung link (<paramref name="coFileBoSung"/>,
    /// caller tính từ đĩa) · trạng thái hủy đổi so với lần đẩy trước · vận đơn / số ước tính / mã yêu cầu trả hàng
    /// VỪA xuất hiện so với lần đẩy trước.</item>
    /// </list>
    /// KHÔNG xét URL Web App: người dùng không dùng sheet thì caller tự coi MỌI đơn là đã settled.
    /// </summary>
    internal static bool ConNghiaVuGhiSheet(GsheetPendingOrder p, bool coFileBoSung)
    {
        var daHuy = ShopeeShippingNav.LaDonHuy(p.Status, p.StatusDescription, p.CancelReason);
        var coVanDon = !string.IsNullOrWhiteSpace(p.TrackingNumber);
        // DaTungGhiSheet (KHÔNG phải DaGhiSheet): nút "Đẩy lại" xoá gsheet_synced_at, nên hỏi DaGhiSheet thì một
        // đơn hủy ĐÃ CÓ DÒNG vừa được bấm "Đẩy lại" sẽ rơi vào lối tắt này → settled → bị dọn khỏi app, dòng cũ
        // trên sheet nằm TRẮNG vĩnh viễn vì không còn ai đẩy lượt tô đỏ.
        if (daHuy && !coVanDon && !p.DaTungGhiSheet)
        {
            return false; // đơn hủy trước khi vào pipeline giao → by design không ghi sheet
        }

        var huyDoi = p.GsheetDaHuy is null || daHuy != (p.GsheetDaHuy == 1);
        var vanDonMoi = coVanDon && p.GsheetDaCoVanDon != 1;
        var uocTinhMoi = p.FinalAmount is not null && p.GsheetDaCoUocTinh != 1;
        var donTraHangMoi = !string.IsNullOrWhiteSpace(p.ReturnRequestCode) && p.GsheetDaCoDonTraHang != 1;
        return !p.DaGhiSheet || coFileBoSung || huyDoi || vanDonMoi || uocTinhMoi || donTraHangMoi;
    }

    /// <summary>
    /// Đẩy các đơn CHƯA đẩy hub của tài khoản này lên HUB đơn hàng qua hook <see cref="AppServices.PushOrdersToHub"/>
    /// (do shell suite rót). <b>Không bao giờ ném</b> (sync DB đã xong — lỗi hub chỉ ghi log): hủy CHỦ ĐỘNG → thôi;
    /// lỗi khác → log "Hub: lỗi — ...". Hook null (app Đơn hàng chạy độc lập / hub chưa cấu hình) → return im lặng
    /// (không đổi hành vi cũ, KHÔNG đụng DB). Không có đơn chờ → return. Logic chia lô + đánh dấu tách sang hàm thuần
    /// <see cref="OrderPersistPipeline.PushPendingToHubAsync"/> (test được, không đụng trình duyệt).
    /// </summary>
    public static async Task<KetQuaDay> PushOrdersToHubAsync(
        long accountId, AppServices services, Action<string> log, CancellationToken ct)
    {
        var push = services.PushOrdersToHub;
        if (push is null)
        {
            return KetQuaDay.KhongCanDay; // hub tắt / app Đơn hàng chạy độc lập → im lặng, không đụng DB
        }

        try
        {
            var pending = services.Orders.GetForHubPush(accountId);
            if (pending.Count == 0)
            {
                return KetQuaDay.KhongCanDay;
            }

            var marked = await OrderPersistPipeline.PushPendingToHubAsync(
                accountId,
                pending,
                push,
                sns => services.Orders.MarkHubSynced(accountId, sns, DateTime.UtcNow),
                OrderPersistPipeline.HubPushBatchSize,
                ct).ConfigureAwait(false);

            if (marked > 0)
            {
                log($"Hub: đã đẩy {marked}/{pending.Count} đơn lên hub.");
                return KetQuaDay.ThanhCong;
            }

            // KHÔNG im lặng: hook trả false ngay lô đầu (hub offline/lỗi) trước đây không để lại dấu vết nào
            // → máy chạy cả buổi mà không đơn nào lên hub vẫn trông như bình thường.
            log($"Hub: đẩy 0/{pending.Count} đơn — hub không phản hồi, sẽ thử lại lượt sau.");
            return KetQuaDay.ThatBai;
        }
        catch (OperationCanceledException)
        {
            // Hủy chủ động (dừng phiên) — thôi.
            return KetQuaDay.KhongCanDay;
        }
        catch (Exception ex)
        {
            // Lỗi đẩy hub KHÔNG phá lượt sync (đã ghi DB) — chỉ log; đơn CHƯA đánh dấu → lượt sau đẩy lại.
            log("Hub: lỗi — " + ex.ToString());
            return KetQuaDay.ThatBai;
        }
    }

    /// <summary>
    /// Đẩy FILE PHIẾU của các đơn ĐÃ lên hub nhưng CHƯA đẩy phiếu (<see cref="OrdersRepository.GetForHubSlipPush"/>)
    /// lên HUB qua hook <see cref="AppServices.PushOrderSlipsToHub"/> (do shell suite rót). Với từng đơn: đọc file
    /// <c>&lt;invoiceDir&gt;/&lt;SanitizeFileName(sn)&gt;.pdf</c> qua kiểm magic sẵn có
    /// (<see cref="SlipFiles.TryReadSlipBase64"/>) — file THIẾU/hỏng → bỏ qua im lặng (khi file có, lượt sau tự
    /// đẩy). Chia lô ≤ <see cref="OrderPersistPipeline.HubSlipPushBatchSize"/>, gọi hook; danh sách <c>order_sn</c> hub báo
    /// ĐÃ LƯU → <see cref="OrdersRepository.MarkHubSlipSynced"/> đúng các đơn đó; hook trả null (hub lỗi cả lô) →
    /// DỪNG các lô sau (lượt sau thử lại). Log 1 dòng khi đẩy được ≥1 phiếu.
    /// <b>Không bao giờ ném</b>: hủy CHỦ ĐỘNG → thôi; lỗi khác → log. Hook null / không có đơn chờ → return im lặng.
    /// </summary>
    public static async Task<KetQuaDay> PushSlipsToHubAsync(
        long accountId, AppServices services, Action<string> log, CancellationToken ct)
    {
        var push = services.PushOrderSlipsToHub;
        if (push is null)
        {
            return KetQuaDay.KhongCanDay; // hub tắt / app Đơn hàng chạy độc lập → im lặng, không đụng DB
        }

        try
        {
            var pending = services.Orders.GetForHubSlipPush(accountId);
            if (pending.Count == 0)
            {
                return KetQuaDay.KhongCanDay;
            }

            // Đọc file phiếu local hợp lệ (tồn tại + ≤5MB + magic %PDF-) → (order_sn, base64). File thiếu → bỏ qua.
            var invoiceDir = services.Settings.GetInvoiceFolder();
            var ready = new List<(string OrderSn, string FileBase64)>();
            foreach (var (sn, _) in pending)
            {
                var path = Path.Combine(invoiceDir, ShopeeShippingNav.SanitizeFileName(sn) + ".pdf");
                if (SlipFiles.TryReadSlipBase64(path, log, out var b64) && b64 is not null)
                {
                    ready.Add((sn, b64));
                }
            }
            if (ready.Count == 0)
            {
                return KetQuaDay.KhongCanDay; // chưa có file phiếu local nào hợp lệ → lượt sau (khi tải-lại-phiếu xong) tự đẩy
            }

            var pushed = 0;
            for (var i = 0; i < ready.Count; i += OrderPersistPipeline.HubSlipPushBatchSize)
            {
                ct.ThrowIfCancellationRequested();

                var count = Math.Min(OrderPersistPipeline.HubSlipPushBatchSize, ready.Count - i);
                var batch = ready.GetRange(i, count);

                var saved = await push(accountId, batch, ct).ConfigureAwait(false);
                if (saved is null)
                {
                    break; // hub lỗi cả lô (offline / route chưa có) → dừng, lượt sync sau tự đẩy lại
                }
                if (saved.Count > 0)
                {
                    services.Orders.MarkHubSlipSynced(accountId, saved, DateTime.UtcNow);
                    pushed += saved.Count;
                }
            }

            if (pushed > 0)
            {
                log($"Hub phiếu: đã đẩy {pushed} file.");
                return KetQuaDay.ThanhCong;
            }
            return KetQuaDay.ThatBai; // có phiếu sẵn sàng mà không đẩy được cái nào → hub đang lỗi
        }
        catch (OperationCanceledException)
        {
            // Hủy chủ động (dừng phiên) — thôi.
            return KetQuaDay.KhongCanDay;
        }
        catch (Exception ex)
        {
            // Lỗi đẩy phiếu KHÔNG phá lượt sync (đã ghi DB) — chỉ log; đơn CHƯA đánh dấu → lượt sau đẩy lại.
            log("Hub phiếu: lỗi — " + ex.ToString());
            return KetQuaDay.ThatBai;
        }
    }

    /// <summary>
    /// +1 "Đã bán" theo SKU lên HUB qua hook <see cref="AppServices.IncrementSoldBySku"/> (do shell suite rót), rồi
    /// CHỈ đánh cờ <c>sold_counted_at</c> cho <paramref name="orderSns"/> khi hub +1 OK (ưu tiên KHÔNG mất đếm nếu
    /// hub lỗi). <b>Không bao giờ ném</b>: hủy CHỦ ĐỘNG → thôi; lỗi khác → log. Hook null (app Đơn hàng chạy độc
    /// lập / hub chưa cấu hình) → return im lặng (đơn CHƯA đánh cờ → lượt sync sau thử lại).
    /// </summary>
    public static async Task<KetQuaDay> IncrementSoldBySkuAsync(
        long accountId, AppServices services,
        IReadOnlyList<string> skus, IReadOnlyList<string> orderSns, Action<string> log, CancellationToken ct)
    {
        var inc = services.IncrementSoldBySku;
        if (inc is null)
        {
            return KetQuaDay.KhongCanDay; // hub tắt / app Đơn hàng chạy độc lập → im lặng, KHÔNG đánh cờ (lượt sau thử lại)
        }

        try
        {
            var ok = await inc(skus, ct).ConfigureAwait(false);
            if (ok)
            {
                // Hub +1 OK → đánh cờ để không +1 lại lượt sau. (Rủi ro hiếm: +1 xong mà đánh cờ lỗi/crash →
                // lượt sau đếm lại 1 lần — chấp nhận, ưu tiên không mất đếm.)
                services.Orders.MarkSoldCounted(accountId, orderSns, DateTime.UtcNow);
                var preview = string.Join(", ", skus.Take(20));
                log($"+{skus.Count} Đã bán theo SKU: {preview}{(skus.Count > 20 ? " …" : string.Empty)}");
                return KetQuaDay.ThanhCong;
            }

            log("Đã bán: hub chưa nhận (+1 hoãn) — lượt sync sau thử lại.");
            return KetQuaDay.ThatBai;
        }
        catch (OperationCanceledException)
        {
            // Hủy chủ động (dừng phiên) — thôi; đơn CHƯA đánh cờ, lượt sau thử lại.
            return KetQuaDay.KhongCanDay;
        }
        catch (Exception ex)
        {
            log("Đã bán: lỗi — " + ex.ToString());
            return KetQuaDay.ThatBai;
        }
    }

    /// <summary>
    /// Đẩy các đơn của tài khoản này (kèm file phiếu PDF base64) lên Google Sheet qua Apps Script Web App, RỒI
    /// DỌN đơn KẾT THÚC (Đã giao / Đã hủy) khỏi app (chính sách "app chỉ giữ đơn Chuẩn bị hàng"). Gọi CHẠY NỀN
    /// (qua <c>OrderPersistPipeline.StartGsheetPushInBackground</c> hoặc <see cref="HubOutboxWorker"/>) SAU khi Sync đã
    /// ghi đơn vào DB + tổng kết.
    /// <b>Không bao giờ ném</b> (sync DB đã xong — lỗi GSheet chỉ ghi log): hủy chủ động → thôi; lỗi khác → log.
    /// <para>
    /// <b>URL chưa cấu hình</b> KHÔNG return sớm: người dùng không dùng sheet thì coi như MỌI đơn đã "settled
    /// GSheet" nhưng vẫn phải DỌN đơn kết thúc. Chỉ đính kèm file khi phiếu tồn tại + đúng magic <c>%PDF-</c> và
    /// đơn chưa có link. Đơn đã ghi sheet mà không có gì mới → bỏ qua (không đẩy trùng) và coi là settled.
    /// </para>
    /// <para>
    /// <b>DỌN vòng đời:</b> đơn kết thúc chỉ bị XÓA khi (a) đã settled GSheet, (b) nếu Đã giao có SKU thì "Đã bán"
    /// đã đếm (<c>sold_counted_at</c>), (c) nếu hub bật thì đã đẩy hub (<c>hub_synced_at</c>) — xem
    /// <see cref="OrderPersistPipeline.NenXoaDonKetThuc"/>. Nghi ngờ thì GIỮ (đơn thừa vô hại, đơn mất là mất dữ liệu);
    /// lượt sync sau tự đẩy + dọn tiếp. Xóa xong phát <see cref="AppServices.RaiseOrdersChanged"/> để lưới Đơn hàng vẽ lại.
    /// </para>
    /// <paramref name="nenBaoThieuGsheetUrl"/>: cờ "được phép báo THIẾU URL Web App lần này" — phiên chỉ cho phép
    /// MỘT lần mỗi phiên (mỗi shop một lượt đẩy sheet sẽ rất ồn), worker luôn trả false (worker chỉ gọi khi URL đã
    /// có). <paramref name="imLangKhiKhongCoDonMoi"/>: bỏ dòng "không có đơn mới cần ghi" (worker chạy 2′/lượt →
    /// dòng này sẽ thành spam); phiên truyền <c>false</c> để giữ nguyên hành vi cũ.
    /// </summary>
    public static async Task<KetQuaDay> PushOrdersToGsheetAsync(
        long accountId, AppServices services, string? shopId, string? shopLogin,
        Func<bool> nenBaoThieuGsheetUrl, bool imLangKhiKhongCoDonMoi, Action<string> log, CancellationToken ct)
    {
        var ketQua = KetQuaDay.KhongCanDay;
        try
        {
            // Đọc pending TRƯỚC nhánh check URL — cần cho bước DỌN kể cả khi người dùng không dùng GSheet. Mô hình
            // nhiều-shop: lọc theo shopId (chỉ đơn của shop hiện tại) — null (chưa vào loop) → mọi đơn của account.
            var pending = services.Orders.GetForGsheetPush(accountId, shopId);
            if (pending.Count == 0)
            {
                return KetQuaDay.KhongCanDay; // không có đơn nào → không ghi, không dọn
            }

            var url = services.Settings.GetGsheetWebAppUrl();
            // Hub đơn hàng đang bật? (hook đã rót — CÙNG điều kiện PushOrdersToHubAsync dùng để quyết đẩy hub.)
            var hubHookActive = services.PushOrdersToHub is not null;

            // Cờ per-đơn: đơn đã "settled" với GSheet = đã ghi xong / không cần ghi / hủy-chưa-vận-đơn / URL trống.
            // Chỉ đơn settled mới đủ điều kiện dọn. Mã đơn so khớp Ordinal.
            var settled = new HashSet<string>(StringComparer.Ordinal);

            if (string.IsNullOrWhiteSpace(url))
            {
                // KHÔNG im lặng: máy chưa điền URL Web App thì cả buổi không ghi được dòng nào mà không ai hay
                // (sự cố thật). Chỉ log MỘT lần mỗi phiên — mỗi shop một dòng sẽ rất ồn.
                if (nenBaoThieuGsheetUrl())
                {
                    log($"GSheet: chưa cấu hình Web App URL — bỏ qua ghi sheet ({pending.Count} đơn chờ). Điền ở Cài đặt hoặc trên Hub.");
                }

                // Người dùng chưa dùng GSheet → coi MỌI đơn đã settled (không có nghĩa vụ ghi sheet); KHÔNG return,
                // vẫn xuống bước dọn đơn kết thúc.
                foreach (var p in pending)
                {
                    settled.Add(p.OrderSn);
                }
            }
            else
            {
                // Tên shop (cột F) = TÊN ĐĂNG NHẬP của SHOP, tính THEO TỪNG ĐƠN bên dưới (p.ShopLogin). Đây là giá
                // trị DÙNG CHUNG khi đơn chưa gắn shop (đơn cũ, shop_login NULL): shop hiện tại của vòng lặp, hoặc
                // Account.Email khi CHƯA vào loop — giữ nguyên hành vi cũ cho các đường không qua vòng lặp shop.
                var tenShopFallback = string.IsNullOrWhiteSpace(shopLogin)
                    ? services.Accounts.GetById(accountId)?.Email
                    : shopLogin;

                var invoiceDir = services.Settings.GetInvoiceFolder();
                // Đọc thời điểm MỘT LẦN cho cả lượt (ngày ghi cột + tab tự động theo tháng) — lượt vắt qua nửa
                // đêm cuối tháng vẫn nhất quán một tab.
                var now = DateTime.Now;
                var ngay = now.ToString("dd/MM/yyyy");

                // Tab đích của đơn MỚI: override ở Cài đặt (có giá trị) hoặc tự động "Tháng MM-yyyy" (override trống).
                // Đơn ĐÃ nhớ tab (p.GsheetTab) LUÔN về đúng tab cũ, bất kể override/tháng hiện tại.
                var overrideTab = services.Settings.GetGsheetTabName();     // "" = tự động
                var autoTab = GsheetTabName.ForMonth(now);
                var defaultTab = string.IsNullOrEmpty(overrideTab) ? autoTab : overrideTab;

                // File PHỤ (Google Sheet thứ hai): gửi ID đã BÓC (script khỏi parse lại). Chưa cấu hình / link rác
                // → "" = TẮT ghi file phụ; vẫn LUÔN gửi field để script phân biệt "tắt" với "client đời cũ".
                var sheet2 = GsheetConfigSync.BocIdSheet(services.Settings.GetGsheetSheet2());

                // Gộp rows theo tab đích (PushAsync nhận MỘT tab/lượt). Thứ tự đơn trong mỗi nhóm giữ nguyên
                // (List theo thứ tự duyệt pending). Thường 1–2 nhóm (tab tháng hiện tại + tab đã nhớ của đơn cũ).
                var rowsByTab = new Dictionary<string, List<GsheetOrderRow>>(StringComparer.Ordinal);
                // Nhớ trạng thái hủy + đã-có-vận-đơn + đã-có-ước-tính VỪA tính của từng đơn được gửi → dùng cho
                // MarkGsheetSynced (ghi cờ gsheet_da_huy / gsheet_da_co_van_don / gsheet_da_co_uoc_tinh).
                var daHuyByMaDon = new Dictionary<string, bool>(StringComparer.Ordinal);
                var coVanDonByMaDon = new Dictionary<string, bool>(StringComparer.Ordinal);
                var coUocTinhByMaDon = new Dictionary<string, bool>(StringComparer.Ordinal);
                var coDonTraHangByMaDon = new Dictionary<string, bool>(StringComparer.Ordinal);
                // Thế hệ dữ liệu ĐỌC ĐƯỢC lúc dựng lô — mang tới tận MarkGsheetSynced để lượt này không đóng cờ
                // mà cú bấm "Đẩy lại" (xảy ra trong lúc lô đang bay) vừa mở. Xem OrdersRepository.MarkGsheetSynced.
                var genByMaDon = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var p in pending)
                {
                    var daHuy = ShopeeShippingNav.LaDonHuy(p.Status, p.StatusDescription, p.CancelReason);
                    var coVanDon = !string.IsNullOrWhiteSpace(p.TrackingNumber);

                    // BỎ QUA đơn HỦY mà CHƯA từng có vận đơn VÀ CHƯA từng ghi sheet: đơn hủy trước khi vào pipeline
                    // giao không thuộc sổ theo dõi → không ghi (tránh spam dòng đỏ vô nghĩa). By design → coi là
                    // settled (được dọn). Đơn CHƯA hủy (đang chuẩn bị) vẫn ghi dù chưa có vận đơn (dòng TRẮNG),
                    // cột B tự điền sau. Đơn ĐÃ CÓ DÒNG trên sheet (DaGhiSheet) thì KHÔNG bỏ qua dù giờ mất vận đơn
                    // (Shopee hủy → danh sách không còn hiện mã): phải đi tiếp xuống phần quyết định gửi để huyDoi
                    // bật và Apps Script TÔ ĐỎ dòng cũ, kẻo dòng đó nằm trắng vĩnh viễn sau khi đơn bị dọn khỏi app.
                    // (Nhánh này là LỐI TẮT của ConNghiaVuGhiSheet — đặt TRƯỚC lượt đọc đĩa để khỏi mở file phiếu
                    //  vô ích. Hàm kia trả false cho đúng bộ điều kiện này; test canh hai bên không lệch nhau.)
                    // Hỏi DaTungGhiSheet chứ KHÔNG hỏi DaGhiSheet — xem ghi chú ở ConNghiaVuGhiSheet: nút "Đẩy lại"
                    // xoá gsheet_synced_at nên DaGhiSheet tụt về false, còn "đã từng có dòng" thì không được phép quên.
                    if (daHuy && !coVanDon && !p.DaTungGhiSheet)
                    {
                        settled.Add(p.OrderSn);
                        continue;
                    }

                    string? fileName = null;
                    string? fileBase64 = null;

                    // Chỉ đính kèm file khi đơn CHƯA có link (FileUrl trống) — tránh upload lại phiếu đã có.
                    if (string.IsNullOrEmpty(p.FileUrl))
                    {
                        var safeName = ShopeeShippingNav.SanitizeFileName(p.OrderSn);
                        var path = Path.Combine(invoiceDir, safeName + ".pdf");
                        if (SlipFiles.TryReadSlipBase64(path, log, out var b64))
                        {
                            fileName = safeName + ".pdf";
                            fileBase64 = b64;
                        }
                    }

                    // CHỌN GỬI khi thỏa ÍT NHẤT một điều kiện: (a) đơn mới với sheet; (b) có file phiếu để bổ sung
                    // link (fileBase64 chỉ set khi FileUrl null); (c) trạng thái hủy đổi so với lần đẩy trước (hoặc
                    // chưa từng đẩy) → sheet cần đổi màu; (d) vận đơn VỪA xuất hiện (đã ghi dòng lúc chưa có vận đơn,
                    // giờ có) → gửi lại để điền cột B; (e) số ước tính VỪA xuất hiện (đã ghi dòng lúc chưa mở trang
                    // chi tiết nên ô tiền còn TRỐNG, giờ có ước tính) → gửi lại để điền cột tiền; (f) mã yêu cầu
                    // TRẢ HÀNG vừa xuất hiện/vừa đổi (bước check cuối flow shop reset cờ khi mã đổi) → gửi lại để
                    // điền cột "Đơn trả hàng". Không thỏa → bỏ qua (đã ghi đủ, không đẩy trùng) → settled.
                    var coFileBoSung = fileBase64 is not null;
                    var coUocTinh = p.FinalAmount is not null;
                    var coDonTraHang = !string.IsNullOrWhiteSpace(p.ReturnRequestCode);
                    if (!ConNghiaVuGhiSheet(p, coFileBoSung))
                    {
                        settled.Add(p.OrderSn);
                        continue;
                    }

                    daHuyByMaDon[p.OrderSn] = daHuy;
                    coVanDonByMaDon[p.OrderSn] = coVanDon;
                    coUocTinhByMaDon[p.OrderSn] = coUocTinh;
                    coDonTraHangByMaDon[p.OrderSn] = coDonTraHang;
                    genByMaDon[p.OrderSn] = p.GsheetPushGen;

                    // Tab đích: tab đã nhớ của đơn (đẩy lại về đúng chỗ cũ) hoặc tab mặc định cho đơn mới.
                    var tab = string.IsNullOrEmpty(p.GsheetTab) ? defaultTab : p.GsheetTab;
                    if (!rowsByTab.TryGetValue(tab, out var tabRows))
                    {
                        tabRows = new List<GsheetOrderRow>();
                        rowsByTab[tab] = tabRows;
                    }
                    // J + K — trang CHI TIẾT đơn đã đọc được SKU/phân loại THẬT của TỪNG sản phẩm thì dựng cả hai
                    // cột từ CÙNG một danh sách (dòng thứ i của cột SKU khớp dòng thứ i của cột Phân loại; đơn 1
                    // sản phẩm ra chuỗi KHÔNG xuống dòng, y như trước). null = chưa có dữ liệu trang chi tiết
                    // (đơn cũ) → giữ NGUYÊN đường cũ, không gửi chuỗi rỗng đè ô đang có.
                    var cotSanPham = SanPhamDonParser.CotGsheet(p.ItemsJson);
                    var phanLoai = cotSanPham is null
                        ? PhanLoaiExtractor.TuItemsJson(p.ItemsJson)
                        : cotSanPham.PhanLoai;
                    var maSp = cotSanPham is null ? p.Sku : cotSanPham.Sku;

                    // F "Shop" — LẤY THEO ĐƠN. Đường đẩy BÙ của HubOutboxWorker gọi với shopId/shopLogin = null
                    // (gom MỌI shop của tài khoản trong một lượt) nên dùng một tên chung cho cả lô sẽ ghi EMAIL
                    // SUBACCOUNT vào cột Shop của mọi dòng. Đường đẩy theo shop không đổi: đơn đã lọc theo shopId
                    // nên p.ShopLogin chính là shopLogin truyền vào; đơn cũ chưa gắn shop mới rơi về fallback.
                    var tenShop = string.IsNullOrWhiteSpace(p.ShopLogin) ? tenShopFallback : p.ShopLogin;

                    // Thứ tự dưới đây xếp theo ĐÚNG thứ tự CỘT trong sheet (A→M) cho dễ đối chiếu — xem
                    // GsheetOrderRow. Tham số CÓ TÊN nên đổi thứ tự cột sau này không gây lệch âm thầm.
                    tabRows.Add(new GsheetOrderRow(
                        MaDon: p.OrderSn,                                     // A "Mã Đơn Gửi"
                        MaVanDon: p.TrackingNumber,                           // B
                        FileName: fileName,                                   // C
                        FileBase64: fileBase64,                               // C
                        // E — mã yêu cầu trả hàng bước check cuối flow shop lưu được; chưa có thì gửi NULL để
                        // field vắng khỏi JSON (hợp đồng "chỉ điền ô trống"), không đè ô đang có.
                        DonTraHang: coDonTraHang ? p.ReturnRequestCode : null,
                        // H "tiền bán" = "Ước tính" (số tiền cuối cùng đọc ở trang chi tiết); chưa có thì để TRỐNG
                        // (đơn hủy → tổng tiền, vì đơn hủy không bao giờ có ước tính) — xem GsheetMoney.Chon.
                        DoanhThu: GsheetMoney.Chon(p.FinalAmount, p.TotalPrice, daHuy),
                        Ngay: ngay,                                           // I
                        TenShop: tenShop,                                     // F "Shop"
                        // K — suy từ items_json đã quét; rỗng thì gửi NULL (cùng nếp "chỉ điền ô trống").
                        PhanLoai: string.IsNullOrWhiteSpace(phanLoai) ? null : phanLoai,
                        Sku: string.IsNullOrWhiteSpace(maSp) ? null : maSp,    // J "SKU"
                        DaHuy: daHuy));                                       // cờ tô màu, không phải cột
                }

                if (rowsByTab.Count == 0)
                {
                    if (!imLangKhiKhongCoDonMoi)
                    {
                        log("GSheet: không có đơn mới cần ghi.");
                    }
                }
                else
                {
                    // PushAsync có thể ném (lỗi mạng/lô) → đơn ĐỊNH-GỬI (trong rowsByTab) coi CHƯA settled → GIỮ
                    // lại, lượt sync sau tự đẩy lại. Đơn settled-by-design ở trên VẪN được dọn. OCE (hủy) cho xuyên.
                    // Đẩy LẦN LƯỢT từng tab (thường 1–2). Một nhóm ném lỗi → catch dưới log + DỪNG các nhóm sau
                    // (mạng đang hỏng); đơn các nhóm đã gửi trước đó vẫn settled, các nhóm sau giữ chưa settled.
                    try
                    {
                        int added = 0, updated = 0, withFile = 0, errors = 0, boChot = 0;
                        string? firstError = null;
                        foreach (var nhom in rowsByTab)
                        {
                            var tabName = nhom.Key;
                            var results = await services.GsheetSync.PushAsync(url, tabName, nhom.Value, log, ct, sheet2).ConfigureAwait(false);

                            foreach (var r in results)
                            {
                                if (r.Ok)
                                {
                                    var daHuy = daHuyByMaDon.TryGetValue(r.MaDon, out var dh) && dh;
                                    var coVanDon = coVanDonByMaDon.TryGetValue(r.MaDon, out var cv) && cv;
                                    var coUocTinh = coUocTinhByMaDon.TryGetValue(r.MaDon, out var cu) && cu;
                                    var coDonTraHang = coDonTraHangByMaDon.TryGetValue(r.MaDon, out var ct2) && ct2;
                                    // Không tra được thế hệ (đơn lạ trong kết quả trả về) → -1: KHÔNG khớp cột nào
                                    // ⇒ không đóng cờ. Thà đẩy lại thừa một lượt còn hơn nuốt mất một dòng.
                                    var gen = genByMaDon.TryGetValue(r.MaDon, out var g) ? g : -1L;
                                    var daDongCo = services.Orders.MarkGsheetSynced(accountId, r.MaDon, r.FileUrl, daHuy, coVanDon, coUocTinh, coDonTraHang, tabName, DateTime.UtcNow, gen);
                                    if (daDongCo > 0)
                                    {
                                        settled.Add(r.MaDon); // gửi thành công + cờ ĐÃ đóng → đủ điều kiện dọn nếu kết thúc
                                    }
                                    else
                                    {
                                        // Cờ KHÔNG đóng được (user bấm "Đẩy lại" giữa lượt này ⇒ thế hệ lệch).
                                        // TUYỆT ĐỐI không settled: `settled` chảy thẳng vào NenXoaDonKetThuc → DeleteOrders,
                                        // nên settled-mà-cờ-chưa-đóng = xoá đơn khỏi app với gsheet_synced_at còn NULL, cú bấm
                                        // của người dùng bốc hơi vĩnh viễn (không còn đơn để đẩy lại) — đúng thứ chốt thế hệ
                                        // sinh ra để chặn. Giữ đơn lại, lượt sau đẩy lại thật.
                                        boChot++;
                                    }
                                    if (r.Added) { added++; } else { updated++; }
                                    if (!string.IsNullOrEmpty(r.FileUrl)) { withFile++; }
                                }
                                else
                                {
                                    errors++;
                                    firstError ??= $"{r.MaDon}: {r.Error}";
                                }
                            }
                        }

                        var summary = $"GSheet: thêm {added} dòng mới, bổ sung {updated}, kèm {withFile} file phiếu.";
                        if (errors > 0)
                        {
                            summary += $" Lỗi {errors} đơn (vd {firstError}).";
                        }
                        if (boChot > 0)
                        {
                            // KHÔNG im lặng: đây là ca người dùng vừa bấm "Đẩy lại" nên nhìn vào log là hiểu ngay
                            // vì sao đơn còn nằm lại thay vì biến mất.
                            summary += $" Giữ lại {boChot} đơn vừa được bấm \"Đẩy lại\" giữa lượt — lượt sau gửi lại.";
                        }
                        log(summary);
                        ketQua = errors > 0 && added + updated == 0 ? KetQuaDay.ThatBai : KetQuaDay.ThanhCong;
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // hủy chủ động → bỏ qua cả bước dọn (lượt sau làm lại)
                    }
                    catch (Exception ex)
                    {
                        // Lỗi đẩy GSheet (mạng/lô) → đơn định-gửi giữ CHƯA settled; vẫn xuống dọn đơn settled-by-design.
                        log("GSheet: lỗi — " + ex.ToString());
                        ketQua = KetQuaDay.ThatBai;
                    }
                }
            }

            // ===== DỌN đơn KẾT THÚC (Đã giao / Đã hủy) đã hoàn tất mọi nghĩa vụ khỏi app =====
            // Thư mục phiếu để kiểm "còn phiếu local chưa đẩy hub" (giữ đơn tới khi phiếu lên hub). Đọc 1 lần.
            var slipDir = services.Settings.GetInvoiceFolder();
            // Kèm THẾ HỆ đã chụp lúc đọc `pending` (đầu lượt): giữa đó và đây có thể trôi vài phút, và mọi đường
            // ghi mở lại nghĩa vụ đều +1 thế hệ ⇒ đơn vừa được "Đẩy lại"/vừa có mã trả mới sẽ KHÔNG bị dọn theo
            // ảnh chụp cũ. Xem OrdersRepository.DeleteOrders.
            var deletable = new List<(string OrderSn, long GenChup)>();
            var terminalChuaXong = 0;
            foreach (var p in pending)
            {
                var terminal = ShopeeShippingNav.LaDonHuy(p.Status, p.StatusDescription, p.CancelReason)
                    || ShopeeShippingNav.LaDaGiaoDaBan(p.Status);
                if (!terminal)
                {
                    continue; // đơn trung gian (Chuẩn bị hàng / Đang giao / Chờ xác nhận…) → GIỮ, theo dõi tiếp
                }
                // Còn phiếu local HỢP LỆ chưa đẩy hub (hub bật) → GIỮ đơn để lượt sau đẩy phiếu xong mới dọn.
                var coPhieuLocalChuaDayHub = hubHookActive && !p.DaDayPhieuHub
                    && SlipFiles.SlipFileIsValidPdf(Path.Combine(slipDir, ShopeeShippingNav.SanitizeFileName(p.OrderSn) + ".pdf"));
                if (OrderPersistPipeline.NenXoaDonKetThuc(p, settled.Contains(p.OrderSn), hubHookActive, coPhieuLocalChuaDayHub))
                {
                    deletable.Add((p.OrderSn, p.HubPushGen));
                }
                else
                {
                    terminalChuaXong++;
                }
            }

            if (deletable.Count > 0)
            {
                var n = services.Orders.DeleteOrders(accountId, deletable);
                services.RaiseOrdersChanged(); // lưới Đơn hàng đang mở tự vẽ lại
                log($"Dọn: đã lưu sheet & xóa {n} đơn kết thúc (Đã giao/Đã hủy) khỏi app.");
                if (n < deletable.Count)
                {
                    // KHÔNG im lặng: đúng ca ai đó bấm "Đẩy lại" / mã trả hàng vừa đổi trong lúc lượt này đang bay
                    // — nhìn nhật ký là hiểu ngay vì sao đơn còn nằm lại thay vì biến mất. Câu chữ chừa đường cho
                    // ca thứ hai (một lượt chạy song song đã dọn trước — phiên vs worker) kẻo log kết luận oan.
                    log($"Dọn: {deletable.Count - n} đơn KHÔNG xóa lượt này (nghĩa vụ vừa mở lại giữa lượt, hoặc lượt chạy song song đã dọn trước) — lượt sau xử tiếp.");
                }
            }
            if (terminalChuaXong > 0)
            {
                log($"Dọn: {terminalChuaXong} đơn kết thúc chờ lượt sau (GSheet/hub/đếm chưa xong).");
            }

            return ketQua;
        }
        catch (OperationCanceledException)
        {
            // Hủy chủ động — thôi (sync DB đã xong; lượt sync sau tự đẩy + dọn lại nhờ cờ DB).
            return KetQuaDay.KhongCanDay;
        }
        catch (Exception ex)
        {
            // Lỗi bất ngờ KHÔNG phá lượt sync (đã báo thành công) — chỉ ghi log.
            log("GSheet: lỗi — " + ex.ToString());
            return KetQuaDay.ThatBai;
        }
    }

    /// <summary>
    /// <b>Lượt đẩy MÃ TRẢ HÀNG lên Google Sheet — đường ĐỘC LẬP với đường đẩy đơn.</b> Chạy SAU
    /// <see cref="PushOrdersToGsheetAsync"/> nhưng KHÔNG phụ thuộc nó: nguồn là bảng <c>return_codes</c>, mà bảng
    /// đó sống độc lập với vòng đời đơn. Đây chính là toàn bộ mục đích — Shopee cho trả hàng trong 15 ngày, còn
    /// app dọn đơn kết thúc sau một hai vòng, nên phần lớn mã trả hàng thuộc về đơn KHÔNG CÒN trong
    /// <c>orders</c>; đường đẩy đơn thường không bao giờ chạm tới chúng.
    /// <para>
    /// Payload là <see cref="GsheetReturnCodeRow"/>: chỉ <c>maDon</c> + <c>donTraHang</c> + <c>chiDienNeuCo</c>,
    /// <b>KHÔNG có <c>daHuy</c></b> (kèm <c>daHuy:false</c> là XOÁ nền đỏ của đơn đã hủy ở CẢ hai file, im lặng).
    /// </para>
    /// <para>
    /// Tab đích chỉ là ĐIỂM VÀO (script tra mã đơn trên MỌI tab): đơn còn trong app → tab đã nhớ; đơn đã dọn →
    /// tab theo tháng hiện tại / override ở Cài đặt.
    /// </para>
    /// <b>Không bao giờ ném</b>: hủy chủ động → thôi; lỗi khác → log, KHÔNG đánh dấu ⇒ lượt sau thử lại.
    /// URL Web App trống → không đẩy (nhưng vẫn DỌN bản ghi quá hạn để bảng không phình).
    /// </summary>
    /// <summary>Số mã QUÁ HẠN đã BÁO gần nhất theo tài khoản — chốt chỉ-log-khi-đổi của
    /// <see cref="NenBaoQuaHan"/>. Static vì thân đẩy là static (xem đầu lớp); sống theo vòng đời app là đúng ý:
    /// cùng một con số thì cả buổi chỉ cần một dòng.</summary>
    private static readonly ConcurrentDictionary<long, int> _quaHanDaBao = new();

    /// <summary>
    /// QUYẾT ĐỊNH THUẦN (nhận dict để test được): lượt này có LOG dòng "mã QUÁ HẠN" không, và tự cập nhật chốt.
    /// Luật: số &gt; 0 và KHÁC lần đã báo trước ⇒ log (lần đầu xuất hiện, hoặc tăng/giảm); y hệt lần trước ⇒ im;
    /// về 0 ⇒ xoá chốt im lặng (sự việc đã hết — số 0 không đáng một dòng, nhưng lần tăng lại SAU ĐÓ phải được
    /// báo như mới).
    /// </summary>
    internal static bool NenBaoQuaHan(ConcurrentDictionary<long, int> daBao, long accountId, int quaHan)
    {
        if (quaHan <= 0)
        {
            daBao.TryRemove(accountId, out _);
            return false;
        }
        if (daBao.TryGetValue(accountId, out var truoc) && truoc == quaHan)
        {
            return false;
        }
        daBao[accountId] = quaHan;
        return true;
    }

    public static async Task<KetQuaDay> PushReturnCodesToGsheetAsync(
        long accountId, AppServices services, Action<string> log, CancellationToken ct)
    {
        try
        {
            // Dọn theo TUỔI — KHÔNG theo vòng đời đơn (bất biến của bảng). Rẻ, chạy kèm mỗi lượt.
            services.ReturnCodes.DonDep(DateTime.UtcNow.AddDays(-ReturnCodesRepository.SoNgayGiuMac));

            // Mã quá hạn thử lại: app THÔI đẩy nhưng phải nói ra. Không có dòng log này thì nhóm đó biến khỏi cả
            // badge lẫn nhật ký đúng lúc bị bỏ — người dùng thấy ô "Mã đơn trả hàng" trống mà không biết vì sao.
            // Đếm TRƯỚC nhánh "hàng đợi rỗng": ca đáng lo nhất chính là hàng đợi rỗng vì mọi mã đều quá hạn.
            // CHỈ log khi SỐ ĐỔI (T10, review 11/08 — chốt theo mẫu `_tonDaBao` của HubOutboxWorker): worker gọi
            // lượt này mỗi 2', không chốt thì MỘT sự việc đứng yên rắc ~63.000 dòng y hệt vào nhật ký mỗi ngày,
            // chôn sạch kênh chẩn đoán.
            var quaHan = services.ReturnCodes.DemQuaHanThuLai(accountId);
            if (NenBaoQuaHan(_quaHanDaBao, accountId, quaHan))
            {
                log($"GSheet mã trả hàng: {quaHan} mã QUÁ HẠN {ReturnCodesRepository.SoNgayThuLaiSheet} ngày "
                    + "— THÔI thử lại (đơn chưa từng có dòng trên sheet / tiêu đề cột sai). Kiểm tra sheet nếu số này tăng.");
            }

            var pending = services.ReturnCodes.LayMaTraHangChuaDay(accountId);
            if (pending.Count == 0)
            {
                return KetQuaDay.KhongCanDay;
            }

            var url = services.Settings.GetGsheetWebAppUrl();
            if (string.IsNullOrWhiteSpace(url))
            {
                return KetQuaDay.KhongCanDay; // không dùng sheet → giữ nguyên hàng đợi (điền URL là đẩy được ngay)
            }

            var now = DateTime.Now;
            var overrideTab = services.Settings.GetGsheetTabName();      // "" = tự động theo tháng
            var defaultTab = string.IsNullOrEmpty(overrideTab) ? GsheetTabName.ForMonth(now) : overrideTab;
            var tabDaNho = services.Orders.GetGsheetTabs(accountId);     // đơn CÒN trong app mới có
            var sheet2 = GsheetConfigSync.BocIdSheet(services.Settings.GetGsheetSheet2());

            var rowsByTab = new Dictionary<string, List<GsheetReturnCodeRow>>(StringComparer.Ordinal);
            foreach (var (sn, code) in pending)
            {
                var tab = tabDaNho.TryGetValue(sn, out var t) && !string.IsNullOrEmpty(t) ? t : defaultTab;
                if (!rowsByTab.TryGetValue(tab, out var tabRows))
                {
                    tabRows = new List<GsheetReturnCodeRow>();
                    rowsByTab[tab] = tabRows;
                }
                tabRows.Add(new GsheetReturnCodeRow(MaDon: sn, DonTraHang: code));
            }

            var day = 0;
            var loi = 0;
            var boQua = 0;
            var lechHopDong = 0;
            string? loiDau = null;
            foreach (var nhom in rowsByTab)
            {
                var xong = new List<(string OrderSn, string Code)>();
                // MÃ ĐÃ GỬI của từng đơn trong CHÍNH lô này — nguồn duy nhất đúng để đánh dấu. Trong lúc lô bay
                // (mỗi nhóm tới 120s), bước check shop có thể ghi mã MỚI cho cùng đơn đó; đọc lại DB ở chỗ đánh
                // dấu là đóng cờ đè lên nghĩa vụ vừa mở. Xem ReturnCodesRepository.DanhDauDaDay.
                var maDaGui = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var row in nhom.Value)
                {
                    maDaGui[row.MaDon] = row.DonTraHang;
                }
                var results = await services.GsheetSync
                    .PushReturnCodesAsync(url, nhom.Key, nhom.Value, log, ct, sheet2).ConfigureAwait(false);
                foreach (var r in results)
                {
                    if (r.Ok && r.BoQua)
                    {
                        // Script trả ok:true nhưng KHÔNG ghi được gì (không tra thấy mã đơn trên sheet, hoặc
                        // thiếu tiêu đề cột). TUYỆT ĐỐI không đánh dấu đã-đẩy: dòng của đơn có thể lên sheet ở
                        // lượt sau, tiêu đề cột có thể được sửa — đánh dấu ở đây là vứt mã ÂM THẦM (lỗi cũ).
                        // Chặn thử-lại-vô-hạn nằm ở ReturnCodesRepository.SoNgayThuLaiSheet, không phải ở đây.
                        boQua++;
                    }
                    else if (r.Ok && maDaGui.TryGetValue(r.MaDon, out var maDaDay))
                    {
                        xong.Add((r.MaDon, maDaDay));
                    }
                    else if (r.Ok)
                    {
                        // Script trả về một mã đơn KHÔNG có trong lô vừa gửi (lệch hợp đồng) → không biết đóng cờ
                        // của mã nào ⇒ GIỮ lại, lượt sau đẩy lại. Thà thừa một lượt còn hơn nuốt mất một mã.
                        // ĐẾM RIÊNG, không gộp vào boQua: câu tóm tắt của boQua nói "chưa thấy dòng đơn / thiếu
                        // tiêu đề cột" — gộp vào là người dùng đi sửa tiêu đề cột trong khi thủ phạm là script
                        // trả sai mã đơn.
                        lechHopDong++;
                    }
                    else
                    {
                        loi++;
                        loiDau ??= $"{r.MaDon}: {r.Error}";
                    }
                }
                // Đánh dấu NGAY theo từng tab: nhóm sau ném lỗi thì nhóm này vẫn không bị đẩy lại.
                day += services.ReturnCodes.DanhDauDaDay(accountId, xong, DateTime.UtcNow);
            }

            var tomTat = $"GSheet mã trả hàng: đã đẩy {day}/{pending.Count} mã (đơn đã dọn vẫn điền được).";
            if (boQua > 0)
            {
                tomTat += $" {boQua} mã script BỎ QUA (chưa thấy dòng đơn trên sheet / thiếu tiêu đề cột) — GIỮ lại, thử lại lượt sau.";
            }
            if (lechHopDong > 0)
            {
                tomTat += $" {lechHopDong} phản hồi mang mã đơn KHÔNG có trong lô (script trả sai?) — GIỮ lại, thử lại lượt sau.";
            }
            if (loi > 0)
            {
                tomTat += $" Lỗi {loi} mã (vd {loiDau}).";
            }
            log(tomTat);
            // Cả lô bị script BỎ QUA (không đẩy được mã nào nhưng cũng KHÔNG có lỗi đích) → KhongCanDay: đích
            // đang khoẻ, backoff của HubOutboxWorker không có việc gì ở đây. Chỉ lỗi thật mới là ThatBai.
            return day > 0 ? KetQuaDay.ThanhCong : (loi > 0 ? KetQuaDay.ThatBai : KetQuaDay.KhongCanDay);
        }
        catch (OperationCanceledException)
        {
            return KetQuaDay.KhongCanDay; // hủy chủ động → chưa đánh dấu, lượt sau đẩy lại
        }
        catch (Exception ex)
        {
            log("GSheet mã trả hàng: lỗi — " + ex.ToString());
            return KetQuaDay.ThatBai;
        }
    }

    /// <summary>
    /// HÀM THUẦN (test được) — lọc HÀNG ĐỢI đếm "Đã bán" (<see cref="OrdersRepository.GetForSoldCountRetry"/>:
    /// đơn <c>sold_counted_at</c> NULL + có SKU) thành cặp danh sách <c>(Skus, OrderSns)</c> song song để +1 lên hub:
    /// chỉ giữ đơn ĐÃ GIAO (<see cref="ShopeeShippingNav.LaDaGiaoDaBan"/>) và KHÔNG phải đơn hủy
    /// (<see cref="ShopeeShippingNav.LaDonHuy"/> — SQL không biết luật này nên lọc ở C#). Mỗi đơn đóng góp ĐÚNG 1
    /// phần tử SKU (đơn trùng SKU → SKU lặp → hub +N), y hệt đường transition của phiên. Mã đơn trùng trong nguồn
    /// → chỉ xử 1 lần.
    /// </summary>
    internal static (List<string> Skus, List<string> OrderSns) LocHangDoiDemDaBan(
        IEnumerable<(string OrderSn, string Sku, string? Status, string? StatusDescription, string? CancelReason)> rows)
    {
        var skus = new List<string>();
        var orderSns = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.OrderSn) || !seen.Add(r.OrderSn))
            {
                continue;
            }
            if (!ShopeeShippingNav.LaDaGiaoDaBan(r.Status))
            {
                continue; // chưa giao xong (hoặc trạng thái giao-dở) → chưa tính "Đã bán"
            }
            if (ShopeeShippingNav.LaDonHuy(r.Status, r.StatusDescription, r.CancelReason))
            {
                continue; // đơn hủy KHÔNG đếm
            }

            var sku = r.Sku?.Trim();
            if (string.IsNullOrEmpty(sku))
            {
                continue; // không SKU thì không +1 được (đường phiên đã đánh cờ ngay cho nhóm này)
            }

            skus.Add(sku);
            orderSns.Add(r.OrderSn);
        }

        return (skus, orderSns);
    }
}
