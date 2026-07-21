using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hàm thuần <see cref="HumanTyping.NextCharDelayMs"/>: delay luôn trong biên [80,800], đa số nhịp
/// nhanh (&lt; 250ms), và có "ngập ngừng" (&gt; 300ms) rải rác. Dùng <see cref="Random"/> có seed.
/// </summary>
public class HumanTypingTests
{
    [Fact]
    public void MoiMau_TrongBien_80_Den_800()
    {
        var rng = new Random(2024);
        for (var i = 0; i < 2000; i++)
        {
            var d = HumanTyping.NextCharDelayMs(rng);
            Assert.InRange(d, 80, 800);
        }
    }

    [Fact]
    public void DaSoMau_DuoiNguong_250ms()
    {
        var rng = new Random(7);
        const int total = 2000;
        var fast = 0;
        for (var i = 0; i < total; i++)
        {
            if (HumanTyping.NextCharDelayMs(rng) < 250)
            {
                fast++;
            }
        }

        Assert.True(fast > total * 0.7, $"đa số nhịp phải < 250ms; thực tế {fast}/{total}");
    }

    [Fact]
    public void CoNgapNgung_MotSoMau_Tren_300ms()
    {
        var rng = new Random(11);
        var hesitations = 0;
        for (var i = 0; i < 500; i++)
        {
            if (HumanTyping.NextCharDelayMs(rng) > 300)
            {
                hesitations++;
            }
        }

        Assert.True(hesitations >= 5, $"phải có vài mẫu ngập ngừng > 300ms; thực tế {hesitations}");
    }
}
