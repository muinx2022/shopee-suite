using Shopee.Core.Coordination;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Điều phối "client nhờ Hub đăng nhập lại BigSeller" — chốt các hành vi dễ làm hỏng đường này:
/// <list type="bullet">
/// <item>Nhiều lane/nhiều lần phát hiện mất phiên cùng 1 acc → CHỈ MỘT lời nhờ gửi lên hub (đừng spam qua tunnel).</item>
/// <item>Hub báo xong → kéo cookie ĐÚNG MỘT LẦN rồi mới buông acc (buông sớm = việc claim lại chạy bằng cookie chết).</item>
/// <item>Hỏng / quá trần chờ → buông, không kẹt "đang đăng nhập lại" vĩnh viễn (việc của acc sẽ không bao giờ chạy lại).</item>
/// <item>Hub bận acc khác / chưa hỏi được (mất mạng, hub cũ chưa có route) → XIN LẠI nhịp sau, KHÔNG coi là lỗi.</item>
/// </list>
/// Thuần logic — hub/đĩa/mạng đều là hàm tiêm vào; đồng hồ tiêm để kiểm trần chờ mà không phải đợi thật.
/// </summary>
public class BigSellerReloginCoordinatorTests
{
    private const string Acct = "bs-acc-1";

    /// <summary>Giàn thử: đếm số lần hỏi hub / kéo cookie + tự đặt câu trả lời của hub và mốc thời gian.</summary>
    private sealed class Rig
    {
        public int Asks, Polls, Pulls;
        public HubReloginState? AskReply = new(true, BigSellerReloginCoordinator.StatusRunning, "");
        public HubReloginState? PollReply = new(false, BigSellerReloginCoordinator.StatusRunning, "");
        public DateTimeOffset Now = new(2026, 7, 27, 8, 0, 0, TimeSpan.Zero);
        public readonly List<string> Lines = [];
        public readonly BigSellerReloginCoordinator Co;

        public Rig()
        {
            Co = new BigSellerReloginCoordinator(
                (_, _) => { Asks++; return Task.FromResult<HubReloginState?>(AskReply); },
                (_, _) => { Polls++; return Task.FromResult<HubReloginState?>(PollReply); },
                _ => { Pulls++; return Task.CompletedTask; },
                () => Now);
            Co.Log = (_, line) => Lines.Add(line);
        }

        public int LinesContaining(string needle) => Lines.Count(l => l.Contains(needle, StringComparison.Ordinal));
    }

    // ===== 1. Dedup lời nhờ =====

    /// <summary>Request 2 lần liên tiếp cùng acc → CHỈ 1 request lên hub, acc nằm trong danh sách chờ.</summary>
    [Fact]
    public void RequestHaiLan_ChiHoiHubMotLan()
    {
        var r = new Rig();
        r.Co.Request(Acct, "log in first");
        r.Co.Request(Acct, "log in first lần 2");

        Assert.Equal(1, r.Asks);
        Assert.Equal(1, r.Co.PendingCount);
        Assert.True(r.Co.IsRelogging(Acct));
    }

    /// <summary>Acc khác KHÔNG bị chặn theo (dedup theo từng acc).</summary>
    [Fact]
    public void AccKhac_VanHoiHubRieng()
    {
        var r = new Rig();
        r.Co.Request(Acct, "x");
        r.Co.Request("bs-acc-2", "x");

        Assert.Equal(2, r.Asks);
        Assert.True(r.Co.IsRelogging("bs-acc-2"));
        Assert.False(r.Co.IsRelogging("bs-acc-3"));
    }

    // ===== 2. Hub xong → kéo cookie 1 lần rồi buông =====

    /// <summary>success → kéo cookie ĐÚNG 1 lần + bỏ acc khỏi danh sách; nhịp sau không hỏi/kéo lại nữa.</summary>
    [Fact]
    public async Task Success_KeoCookieMotLanRoiBuong()
    {
        var r = new Rig();
        r.Co.Request(Acct, "log in first");
        r.PollReply = new HubReloginState(false, BigSellerReloginCoordinator.StatusSuccess, "");

        await r.Co.TickAsync();

        Assert.Equal(1, r.Pulls);
        Assert.False(r.Co.IsRelogging(Acct));
        Assert.Equal(0, r.Co.PendingCount);
        Assert.Equal(1, r.LinesContaining("đã kéo cookie mới về"));

        var polls = r.Polls;
        await r.Co.TickAsync();
        Assert.Equal(1, r.Pulls);
        Assert.Equal(polls, r.Polls);
    }

    /// <summary>Kéo cookie lỗi (mất mạng giữa chừng) vẫn phải buông acc — không kẹt danh sách chờ.</summary>
    [Fact]
    public async Task Success_KeoCookieLoi_VanBuongAcc()
    {
        var now = new DateTimeOffset(2026, 7, 27, 8, 0, 0, TimeSpan.Zero);
        var co = new BigSellerReloginCoordinator(
            (_, _) => Task.FromResult<HubReloginState?>(new HubReloginState(true, BigSellerReloginCoordinator.StatusRunning, "")),
            (_, _) => Task.FromResult<HubReloginState?>(new HubReloginState(false, BigSellerReloginCoordinator.StatusSuccess, "")),
            _ => throw new InvalidOperationException("mất mạng"),
            () => now);
        co.Request(Acct, "log in first");

        await co.TickAsync();

        Assert.False(co.IsRelogging(Acct));
    }

    // ===== 3. Hỏng / quá trần chờ → buông =====

    /// <summary>failed → buông + KHÔNG kéo cookie, log nói rõ phải xử lý tay trên hub.</summary>
    [Fact]
    public async Task Failed_BuongVaKhongKeoCookie()
    {
        var r = new Rig();
        r.Co.Request(Acct, "log in first");
        r.PollReply = new HubReloginState(false, BigSellerReloginCoordinator.StatusFailed, "captcha sai 5 lần");

        await r.Co.TickAsync();

        Assert.Equal(0, r.Pulls);
        Assert.False(r.Co.IsRelogging(Acct));
        Assert.Equal(1, r.LinesContaining("captcha sai 5 lần"));
    }

    /// <summary>Quá trần chờ (10') mà hub chưa xong → buông, KHÔNG hỏi trạng thái nữa.</summary>
    [Fact]
    public async Task QuaTranCho_Buong()
    {
        var r = new Rig();
        r.Co.Request(Acct, "log in first");
        await r.Co.TickAsync();                       // còn trong hạn → vẫn theo dõi
        Assert.True(r.Co.IsRelogging(Acct));
        var polls = r.Polls;

        r.Now += BigSellerReloginCoordinator.MaxWait + TimeSpan.FromMinutes(1);
        await r.Co.TickAsync();

        Assert.False(r.Co.IsRelogging(Acct));
        Assert.Equal(polls, r.Polls);                 // buông trước khi hỏi
        Assert.Equal(1, r.LinesContaining("Quá 10 phút"));
    }

    /// <summary>Hub từ chối hẳn ngay lúc xin (acc không có trên hub / thiếu mật khẩu) → buông NGAY, khỏi chờ 10'.</summary>
    [Fact]
    public void HubTuChoiNgay_BuongLuon()
    {
        var r = new Rig
        {
            AskReply = new HubReloginState(false, BigSellerReloginCoordinator.StatusFailed, "Hub không có acc BigSeller id 'bs-acc-1'."),
        };
        r.Co.Request(Acct, "log in first");

        Assert.False(r.Co.IsRelogging(Acct));
        Assert.Equal(1, r.LinesContaining("Hub không có acc BigSeller"));
    }

    // ===== 4. needsOtp: giữ acc, chỉ nhắc MỘT lần =====

    /// <summary>needsOtp → GIỮ trong danh sách chờ (việc của acc vẫn nằm hàng đợi) và chỉ log nhắc 1 lần.</summary>
    [Fact]
    public async Task NeedsOtp_GiuAccVaChiLogMotLan()
    {
        var r = new Rig();
        r.Co.Request(Acct, "log in first");
        r.PollReply = new HubReloginState(false, BigSellerReloginCoordinator.StatusNeedsOtp, "");

        await r.Co.TickAsync();
        await r.Co.TickAsync();
        await r.Co.TickAsync();

        Assert.True(r.Co.IsRelogging(Acct));
        Assert.Equal(1, r.LinesContaining("chờ mã OTP"));
    }

    // ===== 5. Chưa nhận được lời nhờ → xin lại, KHÔNG coi là lỗi =====

    /// <summary>Hub đang bận acc khác (Accepted=false, chưa có phiên cho acc này) → nhịp sau XIN LẠI, chưa hỏi
    /// trạng thái; nhắc "chờ tới lượt" đúng 1 lần. Nhận rồi thì chuyển sang theo dõi trạng thái.</summary>
    [Fact]
    public async Task HubBanAccKhac_XinLaiNhipSau()
    {
        var r = new Rig
        {
            AskReply = new HubReloginState(false, BigSellerReloginCoordinator.StatusIdle, "Hub đang đăng nhập một acc khác — chờ tới lượt."),
        };
        r.Co.Request(Acct, "log in first");
        await r.Co.TickAsync();

        Assert.Equal(2, r.Asks);                      // 1 lần lúc Request + 1 lần nhịp theo dõi
        Assert.Equal(0, r.Polls);                     // chưa có phiên → chưa hỏi trạng thái
        Assert.True(r.Co.IsRelogging(Acct));
        Assert.Equal(1, r.LinesContaining("chờ tới lượt"));

        r.AskReply = new HubReloginState(true, BigSellerReloginCoordinator.StatusRunning, "");
        await r.Co.TickAsync();                       // xin lại → hub nhận
        await r.Co.TickAsync();                       // từ đây mới theo dõi trạng thái
        Assert.Equal(3, r.Asks);
        Assert.Equal(1, r.Polls);
    }

    /// <summary>Không hỏi được hub (mất mạng / hub CŨ chưa có route → null) → giữ acc + xin lại, KHÔNG buông,
    /// KHÔNG kéo cookie.</summary>
    [Fact]
    public async Task KhongHoiDuocHub_GiuVaXinLai()
    {
        var r = new Rig { AskReply = null };
        r.Co.Request(Acct, "log in first");
        await r.Co.TickAsync();

        Assert.True(r.Co.IsRelogging(Acct));
        Assert.Equal(2, r.Asks);
        Assert.Equal(0, r.Pulls);
    }

    /// <summary>Đã có phiên chạy sẵn cho acc (máy khác vừa xin) → Accepted=false nhưng KHÔNG phải lỗi: theo dõi
    /// luôn phiên đó, không xin lại.</summary>
    [Fact]
    public async Task DaCoPhienDangChay_TheoDoiPhienDo()
    {
        var r = new Rig
        {
            AskReply = new HubReloginState(false, BigSellerReloginCoordinator.StatusRunning, "Khởi động Chromium…"),
        };
        r.Co.Request(Acct, "log in first");
        await r.Co.TickAsync();

        Assert.Equal(1, r.Asks);                      // không xin lại
        Assert.Equal(1, r.Polls);                     // đã chuyển sang theo dõi
        Assert.True(r.Co.IsRelogging(Acct));
    }

    /// <summary>Hub mất phiên giữa chừng (GET trả "idle" — hub restart) → xin lại thay vì chờ vô ích.</summary>
    [Fact]
    public async Task HubMatPhienGiuaChung_XinLai()
    {
        var r = new Rig();
        r.Co.Request(Acct, "log in first");
        r.PollReply = new HubReloginState(false, BigSellerReloginCoordinator.StatusIdle, "");

        await r.Co.TickAsync();                       // thấy idle → đánh dấu phải xin lại
        Assert.Equal(1, r.Asks);
        await r.Co.TickAsync();                       // nhịp sau xin lại
        Assert.Equal(2, r.Asks);
        Assert.True(r.Co.IsRelogging(Acct));
    }

    /// <summary>Xong 1 vòng rồi mất phiên lần nữa → Request mới lại hỏi hub (không bị dedup vĩnh viễn).</summary>
    [Fact]
    public async Task SauKhiXong_RequestMoiVanHoiLai()
    {
        var r = new Rig();
        r.Co.Request(Acct, "lần 1");
        r.PollReply = new HubReloginState(false, BigSellerReloginCoordinator.StatusSuccess, "");
        await r.Co.TickAsync();

        r.Co.Request(Acct, "lần 2");
        Assert.Equal(2, r.Asks);
        Assert.True(r.Co.IsRelogging(Acct));
    }
}
