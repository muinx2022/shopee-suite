using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Core.Services;

/// <summary>Kênh chat nhận diện được từ URL webhook (xem <see cref="OrderNotifyService.NhanDienKenh"/>).</summary>
public enum NotifyKenh
{
    /// <summary>URL trống / không khớp Slack / Discord / Telegram → không gửi được.</summary>
    KhongBiet,

    /// <summary>Slack Incoming Webhook (<c>hooks.slack.com</c>).</summary>
    Slack,

    /// <summary>Discord webhook (<c>discord.com/api/webhooks</c> hoặc <c>discordapp.com/api/webhooks</c>).</summary>
    Discord,

    /// <summary>Telegram Bot API (<c>api.telegram.org/bot&lt;token&gt;/sendMessage?chat_id=&lt;id&gt;</c>).</summary>
    Telegram,
}

/// <summary>
/// Helper CHUNG báo "đơn hàng MỚI" tới MỘT trong 3 kênh chat — <b>Slack / Discord / Telegram</b> — tự nhận
/// diện kênh theo URL người dùng dán vào (mỗi kênh chỉ là một HTTP POST JSON đơn giản; dùng
/// <see cref="HttpClient"/> thuần, KHÔNG thư viện ngoài để tránh DLL mới bị WDAC chặn). Người dùng chỉ cấu
/// hình MỘT URL webhook; app gọi <see cref="SendAsync"/> sau mỗi lượt Sync có đơn mới.
/// <list type="bullet">
/// <item><b>Slack</b> Incoming Webhook: POST <c>{"text": "..."}</c> tới nguyên URL.</item>
/// <item><b>Discord</b> webhook: POST <c>{"content": "..."}</c> tới nguyên URL.</item>
/// <item><b>Telegram</b> Bot API: đọc <c>chat_id</c> từ query string, POST <c>{"chat_id":"...","text":"..."}</c>
/// tới phần URL TRƯỚC dấu <c>?</c>.</item>
/// </list>
/// Các hàm dựng nội dung (<see cref="NhanDienKenh"/>, <see cref="TaoNoiDungGui"/>,
/// <see cref="TaoTinNhanDonMoi"/>) là static THUẦN để test được không cần mạng.
/// </summary>
public class OrderNotifyService
{
    /// <summary>Thời hạn một lần gửi (giây) — CTS liên kết với ct của caller.</summary>
    private const int TimeoutSec = 30;

    // HttpClient DÙNG CHUNG (tránh cạn socket). Timeout INFINITE ở đây; thời hạn từng lần gửi kiểm bằng CTS
    // liên kết (giống GoogleSheetSyncService).
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    // camelCase không cần thiết (các key đã đúng tên: text/content/chat_id) → giữ mặc định (PascalName giữ
    // nguyên). UnsafeRelaxedJsonEscaping để tiếng Việt + emoji giữ NGUYÊN VĂN (đọc được), '"'/'\'/xuống dòng
    // vẫn được escape đúng (là ký tự cấu trúc/điều khiển) → JSON luôn hợp lệ.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Định dạng số tiền VND fallback (khi không có nguyên văn): nhóm hàng nghìn bằng dấu '.', không thập phân
    // → "166.500". CỐ ĐỊNH (không phụ thuộc culture máy) để tin nhắn/ test nhất quán.
    private static readonly NumberFormatInfo VndFormat = new()
    {
        NumberGroupSeparator = ".",
        NumberGroupSizes = new[] { 3 },
        NumberDecimalDigits = 0,
    };

    /// <summary>
    /// Nhận diện kênh theo URL: chứa <c>hooks.slack.com</c> → <see cref="NotifyKenh.Slack"/>; chứa
    /// <c>discord.com/api/webhooks</c> hoặc <c>discordapp.com/api/webhooks</c> → <see cref="NotifyKenh.Discord"/>;
    /// chứa CẢ <c>api.telegram.org/bot</c> VÀ <c>/sendMessage</c> → <see cref="NotifyKenh.Telegram"/>; còn lại
    /// (trống / lạ) → <see cref="NotifyKenh.KhongBiet"/>. So khớp KHÔNG phân biệt hoa/thường (host web).
    /// <para>
    /// Telegram siết CHẶT ở <c>/sendMessage</c> để CHỐNG "thành công giả": nếu chỉ nhận theo host, người dùng
    /// dán nhầm URL <c>.../getUpdates</c> (chính URL docs bảo mở để LẤY chat_id) vẫn qua validate, POST tới
    /// getUpdates được Telegram trả 200 <c>{"ok":true}</c> → tưởng đã gửi trong khi KHÔNG có tin nào tới chat.
    /// </para>
    /// <c>public</c> vì màn Cài đặt (SettingsViewModel) dùng để validate URL, đồng thời test được không cần mạng.
    /// </summary>
    public static NotifyKenh NhanDienKenh(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return NotifyKenh.KhongBiet;
        }

        var u = url.Trim();
        if (Chua(u, "hooks.slack.com"))
        {
            return NotifyKenh.Slack;
        }
        if (Chua(u, "discord.com/api/webhooks") || Chua(u, "discordapp.com/api/webhooks"))
        {
            return NotifyKenh.Discord;
        }
        if (Chua(u, "api.telegram.org/bot") && Chua(u, "/sendmessage"))
        {
            return NotifyKenh.Telegram;
        }
        return NotifyKenh.KhongBiet;
    }

    /// <summary>
    /// Kiểm tra URL webhook người dùng nhập, trả <c>null</c> nếu HỢP LỆ (hoặc trống = "tắt tính năng", caller
    /// tự diễn giải); ngược lại trả THÔNG ĐIỆP LỖI tiếng Việt cụ thể để màn Cài đặt hiện đúng:
    /// <list type="bullet">
    /// <item>Trống → <c>null</c> (tắt).</item>
    /// <item>Không phải Slack/Discord/Telegram → "URL phải là webhook Slack / Discord / Telegram."</item>
    /// <item>Trông giống Telegram (host <c>api.telegram.org</c>) nhưng KHÔNG đúng dạng
    /// <c>/bot&lt;token&gt;/sendMessage</c> (vd dán nhầm getUpdates) → nhắc đúng dạng.</item>
    /// <item>Đúng dạng Telegram nhưng THIẾU <c>chat_id</c> trong query → "URL Telegram thiếu ?chat_id=..."</item>
    /// </list>
    /// </summary>
    public static string? KiemTraUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null; // trống → tắt tính năng (caller xử lý)
        }

        var u = url.Trim();

        // Trông giống Telegram (host) → kiểm CHI TIẾT để báo lỗi đúng (chống dán nhầm getUpdates / thiếu chat_id).
        if (Chua(u, "api.telegram.org"))
        {
            if (!(Chua(u, "api.telegram.org/bot") && Chua(u, "/sendmessage")))
            {
                return "URL Telegram phải đúng dạng https://api.telegram.org/bot<token>/sendMessage?chat_id=<id> (đừng dán URL getUpdates).";
            }
            var (_, chatId) = TachTelegram(u);
            if (string.IsNullOrWhiteSpace(chatId))
            {
                return "URL Telegram thiếu ?chat_id=...";
            }
            return null; // Telegram hợp lệ
        }

        // Slack / Discord dựa vào NhanDienKenh.
        return NhanDienKenh(u) == NotifyKenh.KhongBiet
            ? "URL phải là webhook Slack / Discord / Telegram."
            : null;
    }

    /// <summary>Chứa chuỗi con, KHÔNG phân biệt hoa/thường (Ordinal).</summary>
    private static bool Chua(string haystack, string needle)
        => haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Tạo (URL gửi, JSON body) đúng định dạng từng kênh (System.Text.Json — KHÔNG tự nối chuỗi JSON):
    /// <list type="bullet">
    /// <item><see cref="NotifyKenh.Slack"/> → body <c>{"text":…}</c>, gửi NGUYÊN <paramref name="url"/>.</item>
    /// <item><see cref="NotifyKenh.Discord"/> → body <c>{"content":…}</c>, gửi NGUYÊN <paramref name="url"/>.</item>
    /// <item><see cref="NotifyKenh.Telegram"/> → tách <c>chat_id</c> từ query của URL, body
    /// <c>{"chat_id":…,"text":…}</c>, URL gửi = phần TRƯỚC dấu <c>?</c>.</item>
    /// </list>
    /// <see cref="NotifyKenh.KhongBiet"/> là lỗi lập trình (caller đã chặn) → ném
    /// <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    internal static (string UrlGui, string JsonBody) TaoNoiDungGui(NotifyKenh kenh, string url, string text)
    {
        switch (kenh)
        {
            case NotifyKenh.Slack:
                return (url, JsonSerializer.Serialize(new { text }, JsonOpts));
            case NotifyKenh.Discord:
                return (url, JsonSerializer.Serialize(new { content = text }, JsonOpts));
            case NotifyKenh.Telegram:
                var (urlGui, chatId) = TachTelegram(url);
                return (urlGui, JsonSerializer.Serialize(new { chat_id = chatId, text }, JsonOpts));
            default:
                throw new ArgumentOutOfRangeException(nameof(kenh), kenh, "Kênh không gửi được.");
        }
    }

    /// <summary>
    /// Tách URL Telegram <c>…/sendMessage?chat_id=&lt;id&gt;&amp;…</c> thành (URL trước dấu <c>?</c>, chat_id).
    /// Không có <c>?</c> / không thấy <c>chat_id</c> → chat_id rỗng (server Telegram sẽ báo lỗi, ta chỉ log).
    /// </summary>
    private static (string UrlGui, string ChatId) TachTelegram(string url)
    {
        var q = url.IndexOf('?');
        if (q < 0)
        {
            return (url, string.Empty);
        }

        var urlGui = url.Substring(0, q);
        var query = url.Substring(q + 1);
        var chatId = string.Empty;
        foreach (var pair in query.Split('&'))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair.Substring(0, eq);
            if (string.Equals(key, "chat_id", StringComparison.OrdinalIgnoreCase))
            {
                chatId = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair.Substring(eq + 1));
                break;
            }
        }
        return (urlGui, chatId);
    }

    /// <summary>Giới hạn ký tự AN TOÀN mỗi tin theo kênh (dưới trần thật để chừa chỗ escape/định dạng): Slack
    /// 39000 (trần ~40000), Discord 1900 (trần content 2000), Telegram 4000 (trần text 4096).</summary>
    private static int GioiHanKyTu(NotifyKenh kenh) => kenh switch
    {
        NotifyKenh.Slack => 39000,
        NotifyKenh.Discord => 1900,
        NotifyKenh.Telegram => 4000,
        _ => 1900, // an toàn nhất
    };

    /// <summary>Tiền tố cho các phần thứ 2 trở đi (dễ đọc khi tin bị chia).</summary>
    private const string TiepTucPrefix = "(tiếp) ";

    /// <summary>
    /// Chia <paramref name="text"/> thành nhiều phần để mỗi phần KHÔNG vượt giới hạn ký tự của
    /// <paramref name="kenh"/> (<see cref="GioiHanKyTu"/>) — lưới AN TOÀN chống HTTP 400 khi tin quá dài (vd
    /// sync LẦN ĐẦU sau cấu hình → toàn đơn mới → 20 dòng có thể vượt trần Discord 2000 / Telegram 4096). Cắt
    /// theo DÒNG <c>'\n'</c> (KHÔNG bao giờ cắt GIỮA một dòng), gom dần các dòng vào từng phần tới sát giới hạn.
    /// Một dòng đơn lẻ DÀI HƠN giới hạn → cắt CỨNG chính dòng đó. Phần thứ 2 trở đi mở đầu bằng
    /// <see cref="TiepTucPrefix"/> (đã tính vào giới hạn). Text rỗng → trả 1 phần rỗng. Tách static để test.
    /// </summary>
    internal static IReadOnlyList<string> ChiaTinTheoGioiHan(NotifyKenh kenh, string text)
    {
        var gioiHan = GioiHanKyTu(kenh);
        var parts = new List<string>();
        var body = new StringBuilder();      // NỘI DUNG phần đang gom (CHƯA gồm tiền tố)

        // Ngân sách cho phần đang gom = giới hạn trừ độ dài tiền tố (phần 0 không tiền tố).
        int NganSach() => gioiHan - (parts.Count == 0 ? 0 : TiepTucPrefix.Length);

        void Flush()
        {
            if (body.Length == 0)
            {
                return;
            }
            var prefix = parts.Count == 0 ? string.Empty : TiepTucPrefix;
            parts.Add(prefix + body.ToString());
            body.Clear();
        }

        foreach (var line in text.Split('\n'))
        {
            var addCost = body.Length == 0 ? line.Length : 1 + line.Length; // +1 cho '\n' nối
            if (body.Length + addCost <= NganSach())
            {
                if (body.Length > 0) { body.Append('\n'); }
                body.Append(line);
                continue;
            }

            // Không vừa phần hiện tại → chốt phần đang gom (nếu có) rồi thử đặt dòng vào phần mới.
            Flush();

            if (line.Length <= NganSach())
            {
                body.Append(line);
                continue;
            }

            // Dòng ĐƠN LẺ dài hơn giới hạn → cắt CỨNG thành các phần (mỗi phần một mảnh, có tiền tố nếu ≥ phần 2).
            var rest = line;
            while (rest.Length > 0)
            {
                var budget = NganSach();
                var take = Math.Min(budget, rest.Length);
                var prefix = parts.Count == 0 ? string.Empty : TiepTucPrefix;
                parts.Add(prefix + rest.Substring(0, take));
                rest = rest.Substring(take);
            }
        }

        Flush();
        if (parts.Count == 0)
        {
            parts.Add(string.Empty); // text rỗng → 1 phần rỗng (không để danh sách trống)
        }
        return parts;
    }

    /// <summary>
    /// Gửi <paramref name="text"/> tới <paramref name="webhookUrl"/>: nhận diện kênh; KHÔNG nhận diện được →
    /// log 1 dòng + trả <c>false</c>. Chia tin theo giới hạn ký tự từng kênh (<see cref="ChiaTinTheoGioiHan"/>)
    /// rồi POST TỪNG phần JSON đúng kênh, mỗi phần thời hạn <see cref="TimeoutSec"/> giây (CTS liên kết
    /// <paramref name="ct"/>). MỌI phần HTTP 2xx → <c>true</c>. Một phần lỗi mạng / HTTP ≠ 2xx / timeout → log
    /// rõ "phần i/k" + <c>false</c> và DỪNG các phần sau (KHÔNG ném — không phá lượt sync). Hủy CHỦ ĐỘNG qua
    /// <paramref name="ct"/> → ném <see cref="OperationCanceledException"/> xuyên để caller dừng sạch.
    /// </summary>
    public async Task<bool> SendAsync(string webhookUrl, string text, Action<string> log, CancellationToken ct)
    {
        var kenh = NhanDienKenh(webhookUrl);
        if (kenh == NotifyKenh.KhongBiet)
        {
            log("Notify: URL không nhận diện được kênh (Slack/Discord/Telegram)");
            return false;
        }

        var url = webhookUrl.Trim();
        var phanList = ChiaTinTheoGioiHan(kenh, text);
        for (int i = 0; i < phanList.Count; i++)
        {
            var (urlGui, jsonBody) = TaoNoiDungGui(kenh, url, phanList[i]);
            var moTaPhan = phanList.Count > 1 ? $" phần {i + 1}/{phanList.Count}" : string.Empty;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSec));
            try
            {
                using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                using var resp = await Http.PostAsync(urlGui, content, cts.Token).ConfigureAwait(false);
                var status = (int)resp.StatusCode;
                if (status < 200 || status >= 300)
                {
                    var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    log($"Notify: lỗi gửi ({kenh}){moTaPhan} — HTTP {status}: {Truncate(body, 200)}");
                    return false; // dừng các phần sau (mạng/định dạng đang hỏng)
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct); // hủy chủ động → ném xuyên
            }
            catch (OperationCanceledException)
            {
                // cts hết giờ (KHÔNG do ct) → timeout.
                log($"Notify: lỗi gửi ({kenh}){moTaPhan} — quá {TimeoutSec} giây (mạng chậm).");
                return false;
            }
            catch (Exception ex)
            {
                log($"Notify: lỗi gửi ({kenh}){moTaPhan} — {ex.Message}");
                return false;
            }
        }

        return true; // mọi phần 2xx
    }

    /// <summary>
    /// Dựng tin nhắn text THUẦN (cả 3 kênh render được) cho <paramref name="donMoi"/> đơn MỚI của
    /// <paramref name="tenShop"/> lúc <paramref name="luc"/>:
    /// <list type="bullet">
    /// <item>Dòng đầu: <c>🛒 {tenShop} — {N} đơn MỚI ({luc:HH:mm dd/MM})</c>.</item>
    /// <item>Mỗi đơn một dòng bắt đầu <c>•</c>, gồm các trường ĐÃ SYNC — trường null/rỗng BỎ QUA (không in
    /// "null", không thừa dấu " — "): mã đơn, tên sản phẩm (kèm <c>(+n)</c> nếu nhiều sản phẩm), SKU, tổng
    /// tiền (nguyên văn, thiếu thì định dạng số), ước tính (số tiền cuối cùng nguyên văn), thanh toán, trạng
    /// thái, mã vận đơn (<c>chưa có</c> nếu trống), người mua.</item>
    /// <item>Quá 20 đơn → in 20 đơn đầu + dòng cuối <c>… và {N-20} đơn nữa.</c> (chống tin nhắn quá dài).</item>
    /// </list>
    /// <c>public</c> vì <c>AccountSession</c> (tầng App) dựng tin nhắn qua đây; đồng thời test được (thuần).
    /// </summary>
    public static string TaoTinNhanDonMoi(string tenShop, IReadOnlyList<SyncedOrder> donMoi, DateTime luc)
    {
        const int MaxDong = 20;
        var n = donMoi?.Count ?? 0;
        var sb = new StringBuilder();
        sb.Append($"🛒 {tenShop} — {n} đơn MỚI ({luc.ToString("HH:mm dd/MM", CultureInfo.InvariantCulture)})");

        var hienThi = Math.Min(n, MaxDong);
        for (int i = 0; i < hienThi; i++)
        {
            sb.Append('\n');
            sb.Append(DongDon(donMoi![i]));
        }

        if (n > MaxDong)
        {
            sb.Append($"\n… và {n - MaxDong} đơn nữa.");
        }

        return sb.ToString();
    }

    /// <summary>Một dòng "• …" cho một đơn: ghép các phần KHÔNG rỗng bằng " — " (bỏ trường null, không in "null").</summary>
    private static string DongDon(SyncedOrder o)
    {
        var parts = new List<string>();

        // Mã đơn — luôn có (khóa).
        parts.Add(o.OrderSn);

        // Tên sản phẩm đầu + "(+n)" nếu nhiều sản phẩm (n = số sản phẩm còn lại).
        if (!string.IsNullOrWhiteSpace(o.ItemSummary))
        {
            parts.Add(o.ItemCount > 1 ? $"{o.ItemSummary} (+{o.ItemCount - 1})" : o.ItemSummary!);
        }

        if (!string.IsNullOrWhiteSpace(o.Sku))
        {
            parts.Add($"SKU {o.Sku}");
        }

        // Tổng tiền: ưu tiên nguyên văn (vd "₫166.500"); thiếu thì định dạng số ("166.500₫").
        if (!string.IsNullOrWhiteSpace(o.TotalPriceText))
        {
            parts.Add(o.TotalPriceText!);
        }
        else if (o.TotalPrice is long tp)
        {
            parts.Add($"{tp.ToString("N0", VndFormat)}₫");
        }

        if (!string.IsNullOrWhiteSpace(o.FinalAmountText))
        {
            parts.Add($"ước tính {o.FinalAmountText}");
        }

        if (!string.IsNullOrWhiteSpace(o.PaymentMethod))
        {
            parts.Add(o.PaymentMethod!);
        }

        if (!string.IsNullOrWhiteSpace(o.Status))
        {
            parts.Add(o.Status!);
        }

        // Vận đơn — luôn có (trống → "chưa có").
        parts.Add($"vận đơn {(string.IsNullOrWhiteSpace(o.TrackingNumber) ? "chưa có" : o.TrackingNumber!)}");

        if (!string.IsNullOrWhiteSpace(o.BuyerUsername))
        {
            parts.Add($"mua: {o.BuyerUsername}");
        }

        return "• " + string.Join(" — ", parts);
    }

    /// <summary>Cắt chuỗi tối đa <paramref name="max"/> ký tự (cho thông báo lỗi chẩn đoán).</summary>
    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max));
}
