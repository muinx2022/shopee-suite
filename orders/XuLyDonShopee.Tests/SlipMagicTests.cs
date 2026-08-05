using System.Text;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Luật magic phiếu PDF (<see cref="SlipMagic.LooksPdf"/>): đủ 5 byte <c>%PDF-</c> mới là phiếu thật.
/// Canh việc SIẾT từ 4 → 5 byte ở đợt hợp nhất 06/08: trước đây phía GHI chỉ kiểm <c>%PDF</c> nên file
/// "4 byte đúng, byte 5 sai" được lưu rồi phía ĐỌC mới từ chối — giờ phải chặn ngay từ đầu.
/// </summary>
public class SlipMagicTests
{
    [Theory]
    [InlineData("%PDF-1.4 noi dung", true)]
    [InlineData("%PDF-", true)]          // vừa đúng 5 byte
    [InlineData("%PDFX1.4", false)]      // 4 byte đúng, byte 5 sai — luật siết phải chặn
    [InlineData("%PDF", false)]          // thiếu byte thứ 5
    [InlineData("<html>loi</html>", false)]
    [InlineData("", false)]
    public void LooksPdf_DungLuat5Byte(string text, bool expected)
        => Assert.Equal(expected, SlipMagic.LooksPdf(Encoding.ASCII.GetBytes(text)));
}
