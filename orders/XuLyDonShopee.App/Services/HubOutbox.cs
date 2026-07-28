using System;
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
    /// Đẩy các đơn CHƯA đẩy hub của tài khoản này lên HUB đơn hàng qua hook <see cref="AppServices.PushOrdersToHub"/>
    /// (do shell suite rót). <b>Không bao giờ ném</b> (sync DB đã xong — lỗi hub chỉ ghi log): hủy CHỦ ĐỘNG → thôi;
    /// lỗi khác → log "Hub: lỗi — ...". Hook null (app Đơn hàng chạy độc lập / hub chưa cấu hình) → return im lặng
    /// (không đổi hành vi cũ, KHÔNG đụng DB). Không có đơn chờ → return. Logic chia lô + đánh dấu tách sang hàm thuần
    /// <see cref="AccountSession.PushPendingToHubAsync"/> (test được, không đụng trình duyệt).
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

            var marked = await AccountSession.PushPendingToHubAsync(
                accountId,
                pending,
                push,
                sns => services.Orders.MarkHubSynced(accountId, sns, DateTime.UtcNow),
                AccountSession.HubPushBatchSize,
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
            log("Hub: lỗi — " + ex.Message);
            return KetQuaDay.ThatBai;
        }
    }

    /// <summary>
    /// Đẩy FILE PHIẾU của các đơn ĐÃ lên hub nhưng CHƯA đẩy phiếu (<see cref="OrdersRepository.GetForHubSlipPush"/>)
    /// lên HUB qua hook <see cref="AppServices.PushOrderSlipsToHub"/> (do shell suite rót). Với từng đơn: đọc file
    /// <c>&lt;invoiceDir&gt;/&lt;SanitizeFileName(sn)&gt;.pdf</c> qua kiểm magic sẵn có
    /// (<see cref="AccountSession.TryReadSlipBase64"/>) — file THIẾU/hỏng → bỏ qua im lặng (khi file có, lượt sau tự
    /// đẩy). Chia lô ≤ <see cref="AccountSession.HubSlipPushBatchSize"/>, gọi hook; danh sách <c>order_sn</c> hub báo
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
                if (AccountSession.TryReadSlipBase64(path, log, out var b64) && b64 is not null)
                {
                    ready.Add((sn, b64));
                }
            }
            if (ready.Count == 0)
            {
                return KetQuaDay.KhongCanDay; // chưa có file phiếu local nào hợp lệ → lượt sau (khi tải-lại-phiếu xong) tự đẩy
            }

            var pushed = 0;
            for (var i = 0; i < ready.Count; i += AccountSession.HubSlipPushBatchSize)
            {
                ct.ThrowIfCancellationRequested();

                var count = Math.Min(AccountSession.HubSlipPushBatchSize, ready.Count - i);
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
            log("Hub phiếu: lỗi — " + ex.Message);
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
            log("Đã bán: lỗi — " + ex.Message);
            return KetQuaDay.ThatBai;
        }
    }

    /// <summary>
    /// Đẩy các đơn của tài khoản này (kèm file phiếu PDF base64) lên Google Sheet qua Apps Script Web App, RỒI
    /// DỌN đơn KẾT THÚC (Đã giao / Đã hủy) khỏi app (chính sách "app chỉ giữ đơn Chuẩn bị hàng"). Gọi CHẠY NỀN
    /// (qua <c>AccountSession.StartGsheetPushInBackground</c> hoặc <see cref="HubOutboxWorker"/>) SAU khi Sync đã
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
    /// <see cref="AccountSession.NenXoaDonKetThuc"/>. Nghi ngờ thì GIỮ (đơn thừa vô hại, đơn mất là mất dữ liệu);
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
                // Tên shop (cột E) = TÊN ĐĂNG NHẬP của SHOP hiện tại (mô hình nhiều-shop: vd "alina99.store"), lấy từ
                // bảng /portal/shop khi vào loop. Fallback về Account.Email khi CHƯA vào loop (shopLogin null/rỗng —
                // giữ hành vi cũ cho các đường không qua vòng lặp shop).
                var tenShop = string.IsNullOrWhiteSpace(shopLogin)
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

                // Gộp rows theo tab đích (PushAsync nhận MỘT tab/lượt). Thứ tự đơn trong mỗi nhóm giữ nguyên
                // (List theo thứ tự duyệt pending). Thường 1–2 nhóm (tab tháng hiện tại + tab đã nhớ của đơn cũ).
                var rowsByTab = new Dictionary<string, List<GsheetOrderRow>>(StringComparer.Ordinal);
                // Nhớ trạng thái hủy + đã-có-vận-đơn + đã-có-ước-tính VỪA tính của từng đơn được gửi → dùng cho
                // MarkGsheetSynced (ghi cờ gsheet_da_huy / gsheet_da_co_van_don / gsheet_da_co_uoc_tinh).
                var daHuyByMaDon = new Dictionary<string, bool>(StringComparer.Ordinal);
                var coVanDonByMaDon = new Dictionary<string, bool>(StringComparer.Ordinal);
                var coUocTinhByMaDon = new Dictionary<string, bool>(StringComparer.Ordinal);
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
                    if (daHuy && !coVanDon && !p.DaGhiSheet)
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
                        if (AccountSession.TryReadSlipBase64(path, log, out var b64))
                        {
                            fileName = safeName + ".pdf";
                            fileBase64 = b64;
                        }
                    }

                    // CHỌN GỬI khi thỏa ÍT NHẤT một điều kiện: (a) đơn mới với sheet; (b) có file phiếu để bổ sung
                    // link (fileBase64 chỉ set khi FileUrl null); (c) trạng thái hủy đổi so với lần đẩy trước (hoặc
                    // chưa từng đẩy) → sheet cần đổi màu; (d) vận đơn VỪA xuất hiện (đã ghi dòng lúc chưa có vận đơn,
                    // giờ có) → gửi lại để điền cột B; (e) số ước tính VỪA xuất hiện (đã ghi dòng lúc chưa mở trang
                    // chi tiết nên ô tiền còn TRỐNG, giờ có ước tính) → gửi lại để điền cột tiền.
                    // Không thỏa → bỏ qua (đã ghi đủ, không đẩy trùng) → settled.
                    var coFileBoSung = fileBase64 is not null;
                    var huyDoi = p.GsheetDaHuy is null || daHuy != (p.GsheetDaHuy == 1);
                    var vanDonMoi = coVanDon && p.GsheetDaCoVanDon != 1;
                    var coUocTinh = p.FinalAmount is not null;
                    var uocTinhMoi = coUocTinh && p.GsheetDaCoUocTinh != 1;
                    if (!(!p.DaGhiSheet || coFileBoSung || huyDoi || vanDonMoi || uocTinhMoi))
                    {
                        settled.Add(p.OrderSn);
                        continue;
                    }

                    daHuyByMaDon[p.OrderSn] = daHuy;
                    coVanDonByMaDon[p.OrderSn] = coVanDon;
                    coUocTinhByMaDon[p.OrderSn] = coUocTinh;

                    // Tab đích: tab đã nhớ của đơn (đẩy lại về đúng chỗ cũ) hoặc tab mặc định cho đơn mới.
                    var tab = string.IsNullOrEmpty(p.GsheetTab) ? defaultTab : p.GsheetTab;
                    if (!rowsByTab.TryGetValue(tab, out var tabRows))
                    {
                        tabRows = new List<GsheetOrderRow>();
                        rowsByTab[tab] = tabRows;
                    }
                    tabRows.Add(new GsheetOrderRow(
                        MaDon: p.OrderSn,
                        MaVanDon: p.TrackingNumber,
                        TenShop: tenShop,
                        // Tiền bán = "Ước tính" (số tiền cuối cùng đọc ở trang chi tiết); chưa có thì để TRỐNG
                        // (đơn hủy → tổng tiền, vì đơn hủy không bao giờ có ước tính) — xem GsheetMoney.Chon.
                        DoanhThu: GsheetMoney.Chon(p.FinalAmount, p.TotalPrice, daHuy),
                        Ngay: ngay,
                        Sku: p.Sku,
                        // Cột "Phân loại" (ngay sau SKU): suy từ items_json đã quét — rỗng thì gửi NULL để field
                        // vắng khỏi JSON (hợp đồng "chỉ điền ô trống"), không đè chuỗi rỗng lên ô đã có.
                        PhanLoai: PhanLoaiExtractor.TuItemsJson(p.ItemsJson) is { Length: > 0 } phanLoai ? phanLoai : null,
                        FileName: fileName,
                        FileBase64: fileBase64,
                        DaHuy: daHuy));
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
                        int added = 0, updated = 0, withFile = 0, errors = 0;
                        string? firstError = null;
                        foreach (var nhom in rowsByTab)
                        {
                            var tabName = nhom.Key;
                            var results = await services.GsheetSync.PushAsync(url, tabName, nhom.Value, log, ct).ConfigureAwait(false);

                            foreach (var r in results)
                            {
                                if (r.Ok)
                                {
                                    var daHuy = daHuyByMaDon.TryGetValue(r.MaDon, out var dh) && dh;
                                    var coVanDon = coVanDonByMaDon.TryGetValue(r.MaDon, out var cv) && cv;
                                    var coUocTinh = coUocTinhByMaDon.TryGetValue(r.MaDon, out var cu) && cu;
                                    services.Orders.MarkGsheetSynced(accountId, r.MaDon, r.FileUrl, daHuy, coVanDon, coUocTinh, tabName, DateTime.UtcNow);
                                    settled.Add(r.MaDon); // gửi thành công → settled (đủ điều kiện dọn nếu kết thúc)
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
                        log("GSheet: lỗi — " + ex.Message);
                        ketQua = KetQuaDay.ThatBai;
                    }
                }
            }

            // ===== DỌN đơn KẾT THÚC (Đã giao / Đã hủy) đã hoàn tất mọi nghĩa vụ khỏi app =====
            // Thư mục phiếu để kiểm "còn phiếu local chưa đẩy hub" (giữ đơn tới khi phiếu lên hub). Đọc 1 lần.
            var slipDir = services.Settings.GetInvoiceFolder();
            var deletable = new List<string>();
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
                    && AccountSession.SlipFileIsValidPdf(Path.Combine(slipDir, ShopeeShippingNav.SanitizeFileName(p.OrderSn) + ".pdf"));
                if (AccountSession.NenXoaDonKetThuc(p, settled.Contains(p.OrderSn), hubHookActive, coPhieuLocalChuaDayHub))
                {
                    deletable.Add(p.OrderSn);
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
            log("GSheet: lỗi — " + ex.Message);
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
