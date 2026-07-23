using System.Globalization;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test <see cref="GsheetTabName.ForMonth"/>: định dạng "Tháng MM-yyyy" (tháng 2 chữ số, số 0 đứng đầu) và
/// KHÔNG lệ thuộc culture (lịch/chữ số khác locale không làm sai tháng/năm).
/// </summary>
public class GsheetTabNameTests
{
    [Fact]
    public void ForMonth_Thang1_CoSo0DungDau()
    {
        Assert.Equal("Tháng 01-2026", GsheetTabName.ForMonth(new DateTime(2026, 1, 5)));
    }

    [Fact]
    public void ForMonth_Thang12()
    {
        Assert.Equal("Tháng 12-2026", GsheetTabName.ForMonth(new DateTime(2026, 12, 31)));
    }

    [Fact]
    public void ForMonth_KhongPhuThuocCulture()
    {
        var goc = CultureInfo.CurrentCulture;
        try
        {
            // th-TH mặc định lịch Phật (năm 2569), ar-SA lịch Hồi + chữ số Ả-Rập → NẾU không dùng InvariantCulture
            // sẽ ra tháng/năm/chữ số sai. Phải luôn "Tháng 07-2026".
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            Assert.Equal("Tháng 07-2026", GsheetTabName.ForMonth(new DateTime(2026, 7, 15)));

            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal("Tháng 07-2026", GsheetTabName.ForMonth(new DateTime(2026, 7, 15)));
        }
        finally
        {
            CultureInfo.CurrentCulture = goc;
        }
    }
}
