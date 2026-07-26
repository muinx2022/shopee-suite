using XuLyDonShopee.App.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test <see cref="PushGate"/> — chốt chống chồng TOÀN TIẾN TRÌNH của các lượt đẩy. Đây là chốt duy nhất ngăn
/// phiên (<see cref="AccountSession"/>) và vòng chờ (<see cref="HubOutboxWorker"/>) cùng đẩy MỘT tài khoản; sai
/// ở đây thì đếm "Đã bán" bị +2 cho một đơn (sai số liệu kho, không tự phát hiện được).
/// <para>Mỗi test dùng <c>accountId</c> RIÊNG (gate là static toàn tiến trình, xunit chạy song song các class).</para>
/// </summary>
public class PushGateTests
{
    [Fact]
    public async Task NhieuLuong_CungKhoa_ChiMotVaoDuoc()
    {
        const long acc = 900_101;
        const int soLuong = 32;
        using var start = new ManualResetEventSlim(false);
        var vaoDuoc = 0;

        var tasks = Enumerable.Range(0, soLuong).Select(_ => Task.Run(() =>
        {
            start.Wait();
            if (PushGate.TryEnter(acc, PushKind.SoldCount))
            {
                Interlocked.Increment(ref vaoDuoc);
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(1, vaoDuoc);                              // đúng MỘT luồng chiếm được chỗ
        Assert.True(PushGate.DangBan(acc, PushKind.SoldCount)); // chỗ vẫn đang bị chiếm (chưa Exit)

        PushGate.Exit(acc, PushKind.SoldCount);
        Assert.False(PushGate.DangBan(acc, PushKind.SoldCount));
    }

    [Fact]
    public void SauExit_LuotSauVaoDuoc()
    {
        const long acc = 900_102;

        Assert.True(PushGate.TryEnter(acc, PushKind.Hub));
        Assert.False(PushGate.TryEnter(acc, PushKind.Hub)); // lượt 2 bị chặn khi lượt 1 còn chạy

        PushGate.Exit(acc, PushKind.Hub);

        Assert.True(PushGate.TryEnter(acc, PushKind.Hub));  // nhả xong thì vào được
        PushGate.Exit(acc, PushKind.Hub);
    }

    [Fact]
    public void CacLoaiKhacNhau_DocLap()
    {
        const long acc = 900_103;

        Assert.True(PushGate.TryEnter(acc, PushKind.Hub));
        Assert.True(PushGate.TryEnter(acc, PushKind.HubSlip));   // loại khác → chỗ khác
        Assert.True(PushGate.TryEnter(acc, PushKind.Gsheet));
        Assert.True(PushGate.TryEnter(acc, PushKind.SoldCount));

        PushGate.Exit(acc, PushKind.Hub);
        PushGate.Exit(acc, PushKind.HubSlip);
        PushGate.Exit(acc, PushKind.Gsheet);
        PushGate.Exit(acc, PushKind.SoldCount);
    }

    [Fact]
    public void CacTaiKhoanKhacNhau_DocLap()
    {
        Assert.True(PushGate.TryEnter(900_104, PushKind.SoldCount));
        Assert.True(PushGate.TryEnter(900_105, PushKind.SoldCount)); // tài khoản khác → không chặn nhau

        PushGate.Exit(900_104, PushKind.SoldCount);
        PushGate.Exit(900_105, PushKind.SoldCount);
    }

    [Fact]
    public void ExitThua_KhongNo()
    {
        PushGate.Exit(900_106, PushKind.Gsheet); // chưa từng TryEnter → vô hại
        Assert.False(PushGate.DangBan(900_106, PushKind.Gsheet));
    }
}
