using System;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Thứ tự <b>"bản sạch TRƯỚC, Playwright chỉ khi cần"</b> (user chốt 2026-08-07): hồ sơ còn cookie thì vào thẳng
/// trang chọn shop, khỏi một lượt mở/đóng trình duyệt điều khiển mỗi vòng. Hai mảnh được canh ở đây:
/// <list type="bullet">
/// <item><see cref="OrdersBridgeSession.QuyetDinhSauThuBanSach"/> — bảng quyết định THUẦN (đăng nhập lại hay không).</item>
/// <item><see cref="OrdersBridgeSession.SsoQuaCauNoiAsync"/> — phân loại Ok/Captcha/Lỗi từ phản hồi THẬT của
/// extension, chạy qua cầu nối thật (<see cref="BridgeTestRig"/>), KHÔNG mở trình duyệt.</item>
/// </list>
/// </summary>
public class OrdersBridgeSsoTests
{
    // ===== 1. Bảng quyết định thuần =====

    // Ma trận đủ 6 ca trong MỘT Fact (không tách [Theory]: enum là internal nên không lên được chữ ký
    // public mà xUnit đòi ở test method).
    [Fact]
    public void QuyetDinhSauThuBanSach_MaTran()
    {
        // Vào được picker → chạy tiếp, bất kể đã đăng nhập lại hay chưa.
        Assert.Equal(HanhDongSauThuSach.ChayTiep, OrdersBridgeSession.QuyetDinhSauThuBanSach(KetQuaSso.Ok, daFallback: false));
        Assert.Equal(HanhDongSauThuSach.ChayTiep, OrdersBridgeSession.QuyetDinhSauThuBanSach(KetQuaSso.Ok, daFallback: true));

        // Captcha → nghỉ vòng. KHÔNG đăng nhập lại: đẩy Playwright vào lúc Shopee đang nghi ngờ là tự khai bot.
        Assert.Equal(HanhDongSauThuSach.DungVongCaptcha, OrdersBridgeSession.QuyetDinhSauThuBanSach(KetQuaSso.Captcha, daFallback: false));
        Assert.Equal(HanhDongSauThuSach.DungVongCaptcha, OrdersBridgeSession.QuyetDinhSauThuBanSach(KetQuaSso.Captcha, daFallback: true));

        // Lỗi ở lượt ĐẦU (chưa đăng nhập lại) → đăng nhập lại rồi thử nốt một lượt.
        Assert.Equal(HanhDongSauThuSach.DangNhapLai, OrdersBridgeSession.QuyetDinhSauThuBanSach(KetQuaSso.Loi, daFallback: false));

        // Lỗi ở lượt HAI (đã đăng nhập lại) → hết đường lui, báo lỗi và chờ vòng sau.
        Assert.Equal(HanhDongSauThuSach.BaoLoi, OrdersBridgeSession.QuyetDinhSauThuBanSach(KetQuaSso.Loi, daFallback: true));

        // TREO (không phản hồi) → nghỉ vòng, KHÔNG đăng nhập lại: hết giờ có thể là captcha mà extension chưa
        // kịp báo (hạn phía extension ~145s > hạn chặng AtSeller 120s). Đăng nhập lại lúc đó là tự khai bot.
        Assert.Equal(HanhDongSauThuSach.BaoLoi, OrdersBridgeSession.QuyetDinhSauThuBanSach(KetQuaSso.Treo, daFallback: false));
        Assert.Equal(HanhDongSauThuSach.BaoLoi, OrdersBridgeSession.QuyetDinhSauThuBanSach(KetQuaSso.Treo, daFallback: true));
    }

    // ===== 2. Phân loại phản hồi extension (cầu nối thật, không trình duyệt) =====

    /// <summary>Nhận lệnh <c>gotoSellerCentre</c> mà C# vừa gửi (đóng vai extension).</summary>
    private static async Task NhanLenhSsoAsync(BridgeTestRig rig)
    {
        using var lenh = await rig.NhanLenhAsync();
        Assert.Equal("gotoSellerCentre", lenh.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public async Task SsoQuaCauNoi_VeDuocPicker_TraOk()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var chay = OrdersBridgeSession.SsoQuaCauNoiAsync(rig.Channel, rig.Log, CancellationToken.None);

        await NhanLenhSsoAsync(rig);
        await rig.GuiAsync(new { action = "atSellerCentre" });

        var kq = await chay;
        Assert.Equal(KetQuaSso.Ok, kq.Ket);
        Assert.Null(kq.LyDo);
        Assert.False(rig.Channel.CaptchaSeen);
    }

    [Fact]
    public async Task SsoQuaCauNoi_Captcha_TraCaptcha_KhongPhaiLoi()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var chay = OrdersBridgeSession.SsoQuaCauNoiAsync(rig.Channel, rig.Log, CancellationToken.None);

        await NhanLenhSsoAsync(rig);
        await rig.GuiAsync(new { action = "captcha", message = "https://banhang.shopee.vn/verify" });

        var kq = await chay;
        // Captcha PHẢI khác Lỗi: đường Lỗi sẽ đi đăng nhập lại bằng Playwright — đúng thứ không được làm lúc này.
        Assert.Equal(KetQuaSso.Captcha, kq.Ket);
        Assert.True(rig.Channel.CaptchaSeen);
        Assert.Equal(HanhDongSauThuSach.DungVongCaptcha,
            OrdersBridgeSession.QuyetDinhSauThuBanSach(kq.Ket, daFallback: false));
    }

    [Fact]
    public async Task SsoQuaCauNoi_ExtensionBaoTrangDangNhap_TraLoiKemLyDo()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var chay = OrdersBridgeSession.SsoQuaCauNoiAsync(rig.Channel, rig.Log, CancellationToken.None);

        await NhanLenhSsoAsync(rig);
        // Câu THẬT của extension khi /account ra form đăng nhập (flow-shop.js) — ca chính của nhánh đăng nhập lại.
        await rig.GuiAsync(new
        {
            action = "error",
            message = "bản sạch gặp trang đăng nhập subaccount (cookie hết hạn) — cần đăng nhập lại",
        });

        var kq = await chay;
        Assert.Equal(KetQuaSso.Loi, kq.Ket);
        Assert.Contains("cookie hết hạn", kq.LyDo);
        Assert.False(rig.Channel.CaptchaSeen);
        Assert.Equal(HanhDongSauThuSach.DangNhapLai,
            OrdersBridgeSession.QuyetDinhSauThuBanSach(kq.Ket, daFallback: false));
    }

    [Fact]
    public async Task SsoQuaCauNoi_ExtensionBaoLoiKhac_VanLaLoi_DeDangNhapLai()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var chay = OrdersBridgeSession.SsoQuaCauNoiAsync(rig.Channel, rig.Log, CancellationToken.None);

        await NhanLenhSsoAsync(rig);
        // Lỗi SSO KHÔNG nhắc gì tới đăng nhập: vẫn phải đi đường đăng nhập lại (không so khớp câu chữ tiếng Việt).
        await rig.GuiAsync(new { action = "error", message = "không thấy 'Kênh Người bán' trên https://subaccount.shopee.com/account" });

        var kq = await chay;
        Assert.Equal(KetQuaSso.Loi, kq.Ket);
        Assert.Equal(HanhDongSauThuSach.DangNhapLai,
            OrdersBridgeSession.QuyetDinhSauThuBanSach(kq.Ket, daFallback: false));
    }

    [Fact]
    public async Task SsoQuaCauNoi_KhongAiTraLoi_TraTreo_KhongDangNhapLai()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        // Hạn ngắn để canh được đường TREO mà không phải chờ thật 120s.
        var chay = OrdersBridgeSession.SsoQuaCauNoiAsync(rig.Channel, rig.Log, CancellationToken.None,
            hanReady: TimeSpan.FromSeconds(5), hanAtSeller: TimeSpan.FromMilliseconds(300));

        await NhanLenhSsoAsync(rig); // extension nhận lệnh rồi CÂM (trang treo / chưa kịp báo captcha)

        var kq = await chay;
        Assert.Equal(KetQuaSso.Treo, kq.Ket);
        // Điểm cốt tử: treo KHÔNG được dẫn tới đăng nhập lại — hạn phía extension (~145s) dài hơn hạn chặng
        // AtSeller (120s), nên hết giờ có thể chính là lúc Shopee đang bày trang verify.
        Assert.Equal(HanhDongSauThuSach.BaoLoi,
            OrdersBridgeSession.QuyetDinhSauThuBanSach(kq.Ket, daFallback: false));
    }

    [Fact]
    public async Task SsoQuaCauNoi_CoCaptchaNhungChangHetGio_VanLaCaptcha()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var chay = OrdersBridgeSession.SsoQuaCauNoiAsync(rig.Channel, rig.Log, CancellationToken.None,
            hanReady: TimeSpan.FromSeconds(5), hanAtSeller: TimeSpan.FromSeconds(2));

        await NhanLenhSsoAsync(rig);
        // Đặt THẲNG cờ (không đi qua message "captcha"): test này canh BẤT BIẾN "cờ captcha thắng đường hết giờ",
        // không mô phỏng luồng tới trạng thái đó. Ca đời thật là hạn phía extension (~145s) dài hơn hạn chặng
        // AtSeller (120s) nên C# hết giờ trước khi extension kịp gửi captcha.
        rig.Channel.CaptchaSeen = true;

        var kq = await chay;
        // Cờ captcha phải THẮNG đường hết giờ, nếu không sẽ đi đăng nhập lại ngay giữa lúc bị nghi ngờ.
        Assert.Equal(KetQuaSso.Captcha, kq.Ket);
        Assert.Equal(HanhDongSauThuSach.DungVongCaptcha,
            OrdersBridgeSession.QuyetDinhSauThuBanSach(kq.Ket, daFallback: false));
    }

    [Fact]
    public async Task SsoQuaCauNoi_ChoReady_TruocKhiGuiLenh()
    {
        // Vì sao có test này: BridgeTestRig bắt tay sẵn (đã gửi ready) nên các test khác chạy qua chặng Ready mà
        // KHÔNG canh được nó — xoá dòng chờ ready khỏi SsoQuaCauNoiAsync thì chúng vẫn xanh. Mà chính dòng đó giữ
        // cho lượt 2 (sau khi đăng nhập lại) không bắn lệnh vào socket của trình duyệt lượt 1 vừa bị kill.
        await using var rig = await BridgeTestRig.StartAsync();
        rig.Channel.ResetStages(); // giả lập lượt trình duyệt MỚI: chặng ready thay mới, chưa ai báo

        var chay = OrdersBridgeSession.SsoQuaCauNoiAsync(rig.Channel, rig.Log, CancellationToken.None,
            hanReady: TimeSpan.FromSeconds(10), hanAtSeller: TimeSpan.FromSeconds(10));

        // Hứng lệnh bằng task NỀN (không đặt hạn ngắn rồi hủy: hủy một thao tác WebSocket làm abort cả socket,
        // các bước sau của test sẽ hỏng vì lý do lạc đề).
        // Hạn 30s chỉ là lưới an toàn (không phải thứ đang canh): hết hạn sẽ HỦY ReceiveAsync → abort socket →
        // test đỏ vì lý do lạc đề. Để rộng cho máy chậm.
        var nhanLenh = rig.NhanLenhAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(500);

        // Chưa ai báo ready → KHÔNG được gửi gotoSellerCentre. Bỏ dòng chờ ready khỏi SsoQuaCauNoiAsync là dòng
        // này đỏ ngay (lệnh sẽ tới trước).
        Assert.False(nhanLenh.IsCompleted);

        await rig.GuiAsync(new { action = "ready" }); // extension của lượt mới nối cầu
        using (var lenh = await nhanLenh)            // giờ mới được gửi lệnh
        {
            Assert.Equal("gotoSellerCentre", lenh.RootElement.GetProperty("action").GetString());
        }
        await rig.GuiAsync(new { action = "atSellerCentre" });

        Assert.Equal(KetQuaSso.Ok, (await chay).Ket);
    }

    [Fact]
    public void CongCauNoi_MoLanHai_Nem_DoLaLyDoPhaiGacStarted()
    {
        // Canh LÝ DO tồn tại của guard `if (!_channel.Started)` trong StartBridgeAndLaunch: một vòng có thể phóng
        // trình duyệt sạch hai lần (thử trước bằng cookie, rồi mở lại sau khi đăng nhập). Gọi Start lần hai là
        // dựng HttpListener MỚI trên cổng mình ĐANG giữ → ném, và retry trong Start không cứu được.
        var channel = new OrdersBridgeChannel();
        try
        {
            var port = CongTrong();
            channel.Start(port);
            Assert.True(channel.Started);
            // Lần hai NÉM thật (HttpListener: "conflicts with an existing registration") — sau khi retry
            // 5×400ms trong Start cũng không cứu được, vì kẻ đang giữ cổng chính là ta.
            Assert.Throws<System.Net.HttpListenerException>(() => channel.Start(port));
        }
        finally
        {
            channel.Dispose();
        }
    }

    /// <summary>Cổng loopback TRỐNG (bind port 0 rồi nhả) — không đụng cổng cầu nối thật 47821.</summary>
    private static int CongTrong()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task SsoQuaCauNoi_NguoiDungDung_NemHuy_KhongBienThanhLoi()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        using var cts = new CancellationTokenSource();
        var chay = OrdersBridgeSession.SsoQuaCauNoiAsync(rig.Channel, rig.Log, cts.Token);

        await NhanLenhSsoAsync(rig);
        cts.Cancel(); // user bấm Dừng giữa lúc chờ SSO

        // Nuốt thành Lỗi là đi mở trình duyệt đăng nhập NGAY SAU khi người dùng vừa bấm Dừng.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chay);
    }
}
