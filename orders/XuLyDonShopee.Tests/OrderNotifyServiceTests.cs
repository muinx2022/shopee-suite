using System.Text.Json;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test các hàm THUẦN (static) của <see cref="OrderNotifyService"/> — helper chung báo đơn mới 3 kênh:
/// <see cref="OrderNotifyService.NhanDienKenh"/> (nhận diện Slack/Discord/Telegram theo URL),
/// <see cref="OrderNotifyService.TaoNoiDungGui"/> (URL gửi + body JSON đúng từng kênh), và
/// <see cref="OrderNotifyService.TaoTinNhanDonMoi"/> (dựng text tin nhắn từ danh sách đơn mới).
/// </summary>
public class OrderNotifyServiceTests
{
    // ===================== TaoTinNhanDonMoi =====================

    private static SyncedOrder Full(string sn) => new()
    {
        OrderSn = sn,
        ItemSummary = "Giày",
        ItemCount = 1,
        Sku = "B02435",
        TotalPriceText = "₫166.500",
        TotalPrice = 166500,
        FinalAmountText = "₫160.000",
        PaymentMethod = "Thanh toán khi nhận hàng",
        Status = "Chờ lấy hàng",
        TrackingNumber = "SPXVN068067521447",
        BuyerUsername = "buyer1",
    };

    [Fact]
    public void TaoTinNhanDonMoi_MotDonDuTruong_DungThuTu_CoDayDu()
    {
        var luc = new DateTime(2026, 7, 20, 9, 5, 0);
        var text = OrderNotifyService.TaoTinNhanDonMoi("sully", new[] { Full("260716T6NPV58S") }, luc);

        var lines = text.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal("🛒 sully — 1 đơn MỚI (09:05 20/07)", lines[0]);
        Assert.Equal(
            "• 260716T6NPV58S — Giày — SKU B02435 — ₫166.500 — ước tính ₫160.000 — Thanh toán khi nhận hàng — Chờ lấy hàng — vận đơn SPXVN068067521447 — mua: buyer1",
            lines[1]);
    }

    [Fact]
    public void TaoTinNhanDonMoi_DonThieuTruong_KhongInNull_KhongThuaDauNgan()
    {
        // Chỉ có mã đơn — mọi trường khác null; ItemCount 0.
        var o = new SyncedOrder { OrderSn = "SN1" };
        var text = OrderNotifyService.TaoTinNhanDonMoi("shopA", new[] { o }, new DateTime(2026, 7, 20, 1, 2, 0));

        var lines = text.Split('\n');
        Assert.Equal("• SN1 — vận đơn chưa có", lines[1]);
        Assert.DoesNotContain("null", text);        // KHÔNG in chữ "null"
        Assert.DoesNotContain(" —  — ", text);      // KHÔNG thừa dấu " — " lặp
    }

    [Fact]
    public void TaoTinNhanDonMoi_NhieuSanPham_HienPlusN()
    {
        var o = Full("SN1");
        o.ItemCount = 3;                            // 1 tên hiển thị + 2 còn lại → "(+2)"
        var text = OrderNotifyService.TaoTinNhanDonMoi("shopA", new[] { o }, DateTime.Now);

        Assert.Contains("Giày (+2)", text);
    }

    [Fact]
    public void TaoTinNhanDonMoi_TotalPriceTextNull_DungTotalPriceDinhDang()
    {
        var o = Full("SN1");
        o.TotalPriceText = null;                   // không có nguyên văn → định dạng số
        o.TotalPrice = 166500;
        var text = OrderNotifyService.TaoTinNhanDonMoi("shopA", new[] { o }, DateTime.Now);

        Assert.Contains("166.500₫", text);          // nhóm nghìn bằng dấu '.'
    }

    [Fact]
    public void TaoTinNhanDonMoi_TrackingNull_HienChuaCo()
    {
        var o = Full("SN1");
        o.TrackingNumber = null;
        var text = OrderNotifyService.TaoTinNhanDonMoi("shopA", new[] { o }, DateTime.Now);

        Assert.Contains("vận đơn chưa có", text);
    }

    [Fact]
    public void TaoTinNhanDonMoi_Qua20Don_Chi20Dong_ThemDongConLai()
    {
        var donMoi = Enumerable.Range(1, 25).Select(i => Full($"SN{i}")).ToList();
        var text = OrderNotifyService.TaoTinNhanDonMoi("shopA", donMoi, new DateTime(2026, 7, 20, 9, 5, 0));

        var lines = text.Split('\n');
        Assert.Equal("🛒 shopA — 25 đơn MỚI (09:05 20/07)", lines[0]); // header vẫn ghi tổng N = 25
        var donLines = lines.Where(l => l.StartsWith("• ")).ToList();
        Assert.Equal(20, donLines.Count);                              // chỉ in 20 đơn đầu
        Assert.Contains("… và 5 đơn nữa.", text);                      // 25 - 20 = 5
    }

    // ===================== NhanDienKenh =====================

    [Theory]
    [InlineData("https://hooks.slack.com/services/T000/B000/xxxx", NotifyKenh.Slack)]
    [InlineData("https://discord.com/api/webhooks/123/abc", NotifyKenh.Discord)]
    [InlineData("https://discordapp.com/api/webhooks/123/abc", NotifyKenh.Discord)]
    [InlineData("https://api.telegram.org/bot123:ABC/sendMessage?chat_id=-100999", NotifyKenh.Telegram)]
    [InlineData("https://api.telegram.org/bot123:ABC/getUpdates", NotifyKenh.KhongBiet)] // getUpdates KHÔNG phải Telegram (thiếu /sendMessage)
    [InlineData("https://example.com/webhook", NotifyKenh.KhongBiet)]
    [InlineData("", NotifyKenh.KhongBiet)]
    [InlineData("   ", NotifyKenh.KhongBiet)]
    [InlineData(null, NotifyKenh.KhongBiet)]
    public void NhanDienKenh_TheoUrl(string? url, NotifyKenh mongDoi)
    {
        Assert.Equal(mongDoi, OrderNotifyService.NhanDienKenh(url));
    }

    // ===================== KiemTraUrl =====================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://hooks.slack.com/services/T000/B000/xxxx")]
    [InlineData("https://discord.com/api/webhooks/123/abc")]
    [InlineData("https://api.telegram.org/bot123:ABC/sendMessage?chat_id=-100999")]
    public void KiemTraUrl_HopLe_TraNull(string? url)
    {
        Assert.Null(OrderNotifyService.KiemTraUrl(url));
    }

    [Fact]
    public void KiemTraUrl_UrlLa_BaoLoiChung()
    {
        Assert.Equal("URL phải là webhook Slack / Discord / Telegram.",
            OrderNotifyService.KiemTraUrl("https://example.com/webhook"));
    }

    [Fact]
    public void KiemTraUrl_TelegramGetUpdates_NhacDungDangSendMessage()
    {
        // Dán nhầm URL getUpdates (URL docs bảo mở để lấy chat_id) → phải bị chặn với message nhắc /sendMessage.
        var loi = OrderNotifyService.KiemTraUrl("https://api.telegram.org/bot123:ABC/getUpdates");
        Assert.NotNull(loi);
        Assert.Contains("sendMessage", loi);
        Assert.Contains("getUpdates", loi); // nhắc rõ đừng dán getUpdates
    }

    [Theory]
    [InlineData("https://api.telegram.org/bot123:ABC/sendMessage")]                 // không có query
    [InlineData("https://api.telegram.org/bot123:ABC/sendMessage?parse_mode=HTML")] // có query nhưng thiếu chat_id
    public void KiemTraUrl_TelegramThieuChatId_BaoLoi(string url)
    {
        Assert.Equal("URL Telegram thiếu ?chat_id=...", OrderNotifyService.KiemTraUrl(url));
    }

    // ===================== TaoNoiDungGui =====================

    [Fact]
    public void TaoNoiDungGui_Slack_TextBody_GiuUrl()
    {
        const string url = "https://hooks.slack.com/services/T000/B000/xxxx";
        var (urlGui, body) = OrderNotifyService.TaoNoiDungGui(NotifyKenh.Slack, url, "xin chào");

        Assert.Equal(url, urlGui);                  // gửi nguyên URL
        using var doc = JsonDocument.Parse(body);   // JSON hợp lệ
        Assert.Equal("xin chào", doc.RootElement.GetProperty("text").GetString());
        Assert.False(doc.RootElement.TryGetProperty("content", out _));
    }

    [Fact]
    public void TaoNoiDungGui_Discord_ContentBody_GiuUrl()
    {
        const string url = "https://discord.com/api/webhooks/123/abc";
        var (urlGui, body) = OrderNotifyService.TaoNoiDungGui(NotifyKenh.Discord, url, "xin chào");

        Assert.Equal(url, urlGui);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("xin chào", doc.RootElement.GetProperty("content").GetString());
        Assert.False(doc.RootElement.TryGetProperty("text", out _));
    }

    [Fact]
    public void TaoNoiDungGui_Telegram_TachChatId_CatQuery_BodyDuTruong()
    {
        const string url = "https://api.telegram.org/bot123:ABC/sendMessage?chat_id=-100999";
        var (urlGui, body) = OrderNotifyService.TaoNoiDungGui(NotifyKenh.Telegram, url, "xin chào");

        Assert.Equal("https://api.telegram.org/bot123:ABC/sendMessage", urlGui); // cắt phần trước '?'
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("-100999", doc.RootElement.GetProperty("chat_id").GetString());
        Assert.Equal("xin chào", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void TaoNoiDungGui_Telegram_ChatIdCoThemThamSoKhac()
    {
        // chat_id không phải tham số đầu tiên trong query.
        const string url = "https://api.telegram.org/bot123:ABC/sendMessage?parse_mode=HTML&chat_id=42";
        var (urlGui, body) = OrderNotifyService.TaoNoiDungGui(NotifyKenh.Telegram, url, "hi");

        Assert.Equal("https://api.telegram.org/bot123:ABC/sendMessage", urlGui);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("42", doc.RootElement.GetProperty("chat_id").GetString());
    }

    [Fact]
    public void TaoNoiDungGui_KyTuDacBiet_JsonHopLe_RoundTrip()
    {
        const string url = "https://hooks.slack.com/services/x";
        var text = "Đơn \"mới\"\nDòng 2 — 100₫ & <b>";
        var (_, body) = OrderNotifyService.TaoNoiDungGui(NotifyKenh.Slack, url, text);

        using var doc = JsonDocument.Parse(body);   // ném nếu escape sai → JSON không hợp lệ
        Assert.Equal(text, doc.RootElement.GetProperty("text").GetString()); // round-trip nguyên văn
    }

    // ===================== ChiaTinTheoGioiHan =====================

    [Fact]
    public void ChiaTinTheoGioiHan_TextNgan_MotPhan_KhongTiepTuc()
    {
        var text = "🛒 shopA — 2 đơn MỚI\n• SN1 — vận đơn chưa có\n• SN2 — vận đơn chưa có";
        var parts = OrderNotifyService.ChiaTinTheoGioiHan(NotifyKenh.Discord, text);

        var only = Assert.Single(parts);
        Assert.Equal(text, only);                    // giữ nguyên, không đụng
        Assert.DoesNotContain("(tiếp) ", only);
    }

    [Fact]
    public void ChiaTinTheoGioiHan_Discord_NhieuDongVuot1900_ChiaPhan_KhongCatGiuaDong()
    {
        const int Limit = 1900;
        // 40 dòng, mỗi dòng có marker duy nhất "L###" + đệm → dài ~94 ký tự; tổng ~3800 > 1900 → phải chia.
        var lines = Enumerable.Range(0, 40).Select(i => $"L{i:D3}" + new string('x', 90)).ToList();
        var text = string.Join("\n", lines);

        var parts = OrderNotifyService.ChiaTinTheoGioiHan(NotifyKenh.Discord, text);

        Assert.True(parts.Count >= 2, "phải chia thành nhiều phần");
        Assert.All(parts, p => Assert.True(p.Length <= Limit, $"phần dài {p.Length} > {Limit}"));
        Assert.StartsWith("(tiếp) ", parts[1]);      // phần 2 trở đi mở đầu "(tiếp) "
        // KHÔNG cắt giữa dòng: MỖI dòng gốc phải xuất hiện NGUYÊN VẸN trong đúng một phần.
        foreach (var line in lines)
        {
            Assert.True(parts.Any(p => p.Contains(line)), $"dòng bị cắt: {line[..8]}…");
        }
    }

    [Fact]
    public void ChiaTinTheoGioiHan_Telegram_MotDongDai5000_CatCung_MoiPhanDuoi4000()
    {
        const int Limit = 4000;
        var line = new string('A', 5000);           // một dòng đơn lẻ dài hơn giới hạn → cắt CỨNG

        var parts = OrderNotifyService.ChiaTinTheoGioiHan(NotifyKenh.Telegram, line);

        Assert.True(parts.Count >= 2);
        Assert.All(parts, p => Assert.True(p.Length <= Limit, $"phần dài {p.Length} > {Limit}"));
        // Ghép lại (bỏ tiền tố "(tiếp) " của phần ≥ 2) phải bằng đúng dòng gốc — không mất/không thêm ký tự.
        var rebuilt = parts[0] + string.Concat(parts.Skip(1).Select(p => p.Substring("(tiếp) ".Length)));
        Assert.Equal(line, rebuilt);
    }
}
