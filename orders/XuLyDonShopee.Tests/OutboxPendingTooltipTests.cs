using System.Linq;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Tooltip của badge "⏳ Chờ đẩy" (H2.6): mỗi loại đích một DÒNG, và loại nào bằng 0 thì BỎ HẲN dòng đó — badge
/// chỉ hiện khi còn tồn, liệt kê cả mấy loại đã sạch chỉ làm loãng đúng con số đang kẹt.
/// </summary>
public class OutboxPendingTooltipTests
{
    private static string[] Dong(OutboxPending p) => MainViewModel.MoTaTonTooltip(p).Split('\n');

    [Fact]
    public void ChiLietKeLoaiKhac0()
    {
        // Orders = 0 CỐ Ý: dòng "đơn lên Hub" cũng phải biến mất, không chỉ mấy loại xếp sau.
        var dong = Dong(new OutboxPending(Orders: 0, Slips: 0, SheetRows: 5, SoldCounts: 0, ReturnCodes: 2));

        Assert.Contains(dong, d => d.Contains("5 dòng Google Sheet"));
        Assert.Contains(dong, d => d.Contains("2 mã trả hàng"));
        Assert.DoesNotContain(dong, d => d.Contains("đơn lên Hub"));
        Assert.DoesNotContain(dong, d => d.Contains("phiếu"));
        Assert.DoesNotContain(dong, d => d.Contains("Đã bán"));
    }

    [Fact]
    public void DuCa5Loai_ThiCoDu5Dong()
    {
        var dong = Dong(new OutboxPending(1, 2, 3, 4, 5));

        Assert.Equal(5, dong.Count(d => d.StartsWith("•")));
        Assert.Contains(dong, d => d.Contains("Hàng còn chờ đẩy: 15")); // tổng ở dòng đầu
    }

    [Fact]
    public void MoiLoaiMotDongRieng_KhongDonCucVaoMotDong()
    {
        var text = MainViewModel.MoTaTonTooltip(new OutboxPending(1, 1, 1, 1, 1));
        Assert.Equal(7, text.Split('\n').Length); // 1 dòng tổng + 5 dòng loại + 1 dòng nhắc nhịp
    }

    [Fact]
    public void HetTon_ChiConDongTongVaDongNhac()
    {
        var dong = Dong(default);
        Assert.Equal(2, dong.Length);
        Assert.Contains("0", dong[0]);
    }
}
