using System;
using System.Threading.Tasks;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Nhịp GIỮ SỐNG service worker của cầu nối.
/// <para>
/// Bối cảnh (lỗi thật 10/08/2026): service worker MV3 bị trình duyệt giết sau ~30s không hoạt động. Vòng chạy
/// NGHỈ ~3 phút giữa hai shop ⇒ shop 1 xong, nghỉ, tới shop 2 thì <c>SendAsync</c> ném "Cầu nối extension chưa
/// kết nối (WebSocket chưa mở)" và CẢ VÒNG dừng. Service worker chết rồi thì tự nó không nối lại được.
/// Thuốc: C# bắn gói <c>ping</c> mỗi <see cref="OrdersBridgeChannel.NhipGiuSong"/> — Chromium tính hoạt động
/// WebSocket là hoạt động của service worker.
/// </para>
/// </summary>
public class CauNoiGiuSongTests
{
    /// <summary>Nhịp rút gọn cho test — production là 20s, chờ ngần ấy trong test là vô nghĩa.</summary>
    private static readonly TimeSpan NhipTest = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task GiuSong_BanPingDeuDan_KhiKhongAiGuiLenh()
    {
        await using var rig = await BridgeTestRig.StartAsync(NhipTest);

        // Không gửi lệnh nào — đúng cảnh "đang nghỉ giữa hai shop". Phải nhận được ping liên tiếp.
        for (var i = 0; i < 2; i++)
        {
            using var doc = await rig.NhanLenhAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("ping", doc.RootElement.GetProperty("action").GetString());
        }
    }

    [Fact]
    public async Task GiuSong_KhongDungToiChangDangCho()
    {
        await using var rig = await BridgeTestRig.StartAsync(NhipTest);

        // Arm một chặng rồi để ping bắn qua vài nhịp: chặng PHẢI còn treo (ping không hoàn tất/không fault nó).
        var tcs = rig.Channel.ArmReturns();
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        Assert.False(tcs.Task.IsCompleted);

        // Và chặng vẫn nhận đúng dữ liệu thật sau đó.
        await rig.GuiAsync(new { action = "pageData", kind = "returns", data = "{\"list\":[]}" });
        var json = await rig.Channel.AwaitAsync(tcs, TimeSpan.FromSeconds(5), default);
        Assert.Contains("\"list\"", json);
    }

    [Fact]
    public async Task SendAsync_ChoExtensionNoiLai_ThayViNemNgay()
    {
        await using var rig = await BridgeTestRig.StartAsync(NhipTest);

        // Extension "chết" (đóng socket) — đúng cảnh service worker MV3 bị giết lúc vòng chạy nghỉ.
        await rig.NgatKetNoiAsync();

        // Gửi lệnh NGAY lúc đang đứt: KHÔNG được ném liền, phải chờ. Cho extension nối lại sau một nhịp.
        var gui = rig.Channel.SendAsync(new { action = "readShopList" }, TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        Assert.False(gui.IsCompleted); // còn đang chờ, chưa bỏ cuộc

        await rig.NoiLaiAsync();
        await gui; // nối lại được thì lệnh đi bình thường

        using var doc = await rig.NhanLenhAsync(TimeSpan.FromSeconds(5), boQuaPing: true);
        Assert.Equal("readShopList", doc.RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public async Task SendAsync_HetHanChoNoiLai_ThiVanNem()
    {
        await using var rig = await BridgeTestRig.StartAsync(NhipTest);
        await rig.NgatKetNoiAsync();

        // Không ai nối lại → hết hạn phải NÉM đúng lỗi cũ, không được nuốt im lặng rồi coi như gửi xong.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Channel.SendAsync(new { action = "readShopList" }, TimeSpan.FromMilliseconds(600)));
        Assert.Contains("chưa kết nối", ex.Message, StringComparison.Ordinal);
    }

    // CỐ Ý KHÔNG có test "Dispose tắt nhịp giữ sống": không quan sát được một cách tất định. Dispose đóng luôn
    // WebSocketServer nên client nhận lỗi socket dù timer còn hay đã tắt ⇒ assert luôn xanh (test trang trí);
    // mà đo bằng "im lặng trong N giây" thì thua cuộc đua với gói vừa bay trước lúc Dispose — đã thấy đỏ ngẫu
    // nhiên khi chạy song song. Thứ tự dispose (timer TRƯỚC server) giữ bằng comment tại chỗ trong Dispose().
}
