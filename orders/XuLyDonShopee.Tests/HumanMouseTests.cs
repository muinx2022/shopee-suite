using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hàm thuần <see cref="HumanMouse.GeneratePath"/>: số điểm đúng, điểm đầu/cuối đúng, đường đi
/// <b>cong</b> (không thẳng hàng), và các trường hợp suy biến không ném/không NaN. Dùng <see cref="Random"/>
/// có seed để tất định.
/// </summary>
public class HumanMouseTests
{
    /// <summary>Khoảng cách vuông góc từ điểm (px,py) tới đường thẳng qua (x0,y0)-(x1,y1).</summary>
    private static double PerpDistance(double x0, double y0, double x1, double y1, double px, double py)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9)
        {
            return Math.Sqrt((px - x0) * (px - x0) + (py - y0) * (py - y0));
        }
        return Math.Abs(dx * (py - y0) - dy * (px - x0)) / len;
    }

    [Fact]
    public void SoDiem_BangSteps()
    {
        var path = HumanMouse.GeneratePath(0, 0, 400, 300, 40, new Random(12345));
        Assert.Equal(40, path.Count);
    }

    [Fact]
    public void DiemDau_DungGiaTri_DiemCuoi_EpDung()
    {
        var path = HumanMouse.GeneratePath(10, 20, 500, 400, 30, new Random(1));

        // Điểm đầu đúng bằng (x0,y0).
        Assert.Equal(10, path[0].X, 6);
        Assert.Equal(20, path[0].Y, 6);
        // Điểm cuối ÉP đúng bằng (x1,y1).
        Assert.Equal(500, path[^1].X, 6);
        Assert.Equal(400, path[^1].Y, 6);
    }

    [Fact]
    public void DuongDi_Cong_KhongThangHang()
    {
        // Đường ngang (0,0)->(600,0): độ lệch vuông góc = độ "phồng" của đường cong.
        var path = HumanMouse.GeneratePath(0, 0, 600, 0, 50, new Random(999));
        var maxDev = path.Max(p => PerpDistance(0, 0, 600, 0, p.X, p.Y));

        Assert.True(maxDev > 5, $"độ lệch tối đa {maxDev:0.0} phải > 5 (đường phải cong, không thẳng)");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(101)]
    [InlineData(20260714)]
    public void NhieuSeed_LuonCong(int seed)
    {
        // Bất kể seed, đường luôn có điểm lệch vuông góc (cùng phía → không thể triệt tiêu về thẳng).
        var path = HumanMouse.GeneratePath(100, 100, 700, 500, 40, new Random(seed));
        var maxDev = path.Max(p => PerpDistance(100, 100, 700, 500, p.X, p.Y));

        Assert.True(maxDev > 1, $"seed {seed}: độ lệch tối đa {maxDev:0.0} phải > 1 (đường cong)");
    }

    [Fact]
    public void StartTrungEnd_KhongNem_TraDiemLapLai()
    {
        var path = HumanMouse.GeneratePath(50, 50, 50, 50, 10, new Random(3));

        Assert.Equal(10, path.Count);
        Assert.All(path, p =>
        {
            Assert.Equal(50, p.X);
            Assert.Equal(50, p.Y);
        });
    }

    [Fact]
    public void StepsMotHoacKhong_TraDiemCuoi_KhongNem()
    {
        var p1 = HumanMouse.GeneratePath(0, 0, 100, 100, 1, new Random(4));
        Assert.Single(p1);
        Assert.Equal(100, p1[0].X);
        Assert.Equal(100, p1[0].Y);

        var p0 = HumanMouse.GeneratePath(0, 0, 100, 100, 0, new Random(4));
        Assert.Single(p0); // count = max(steps,1) = 1
        Assert.Equal(100, p0[0].X);
        Assert.Equal(100, p0[0].Y);
    }

    [Fact]
    public void KhongCoNaN_KeCaToaDoAm()
    {
        var path = HumanMouse.GeneratePath(-30, 15, 220, -90, 25, new Random(77));

        Assert.All(path, p =>
        {
            Assert.False(double.IsNaN(p.X));
            Assert.False(double.IsNaN(p.Y));
        });
    }
}
