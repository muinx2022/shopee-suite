using Microsoft.Data.Sqlite;
using Shopee.Core.Coordination;
using Shopee.Hub;

namespace Shopee.Hub.Web.Tests;

/// <summary>
/// Mã trả hàng đến từ <c>app-alert kind=don_tra</c> (đơn ĐÃ bị app dọn nên không còn về qua <c>orders/push</c>,
/// nhưng hub VẪN GIỮ dòng đơn). Không ghi được lớp này thì cột "Đơn trả hàng" ở /orders trống và dòng
/// "mã trả hàng mới hôm nay" của tin tổng kết đếm hụt gần hết — đúng lỗi phản biện H2 chỉ ra.
/// </summary>
public sealed class ReturnCodeFromAppAlertTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "hub-retcode-test-" + Guid.NewGuid().ToString("N"));

    private static void ThemDon(HubDatabase db, string sn) =>
        db.UpsertOrders(db.GetOrCreateShopByUsername("shop-1", "Shop 1"),
            [new OrderPushItem { OrderSn = sn, Status = "Đã giao" }]);

    /// <summary>Đơn đã dọn khỏi app → mã trả vẫn vào được bảng orders + có mốc để tin tổng kết đếm.</summary>
    [Fact]
    public void DonDaDonKhoiApp_GhiDuocMaTra_VaDemVaoTinTongKet()
    {
        using var db = new HubDatabase(_dataDir);
        ThemDon(db, "SN-1");

        var ghi = db.ApplyReturnCodesFromAppAlert([("SN-1", "RR-111")]);

        Assert.Equal(1, ghi);
        var truoc = DateTimeOffset.UtcNow.AddMinutes(-5);
        var sau = DateTimeOffset.UtcNow.AddMinutes(5);
        Assert.Equal(1, db.CountReturnCodesInRange(truoc, sau));
    }

    /// <summary>Gửi LẠI cùng lô (client đẩy bù / lượt quét sau) KHÔNG được cộng thêm số vào tin tổng kết.</summary>
    [Fact]
    public void GuiLaiCungMa_KhongCongThemSo()
    {
        using var db = new HubDatabase(_dataDir);
        ThemDon(db, "SN-1");

        Assert.Equal(1, db.ApplyReturnCodesFromAppAlert([("SN-1", "RR-111")]));
        Assert.Equal(0, db.ApplyReturnCodesFromAppAlert([("SN-1", "RR-111")]));      // y hệt
        Assert.Equal(0, db.ApplyReturnCodesFromAppAlert([("SN-1", "  RR-111  ")]));  // khác mỗi khoảng trắng

        Assert.Equal(1, db.CountReturnCodesInRange(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    /// <summary>Mã ĐỔI (Shopee cấp mã khác cho cùng đơn) → ghi lại và tính là mã mới.</summary>
    [Fact]
    public void MaDoi_GhiLai_VaTinhLaMoi()
    {
        using var db = new HubDatabase(_dataDir);
        ThemDon(db, "SN-1");

        db.ApplyReturnCodesFromAppAlert([("SN-1", "RR-111")]);
        Assert.Equal(1, db.ApplyReturnCodesFromAppAlert([("SN-1", "RR-222")]));
    }

    /// <summary>Mã của đơn KHÔNG có trên hub (hub chưa từng nhận đơn đó) → không ghi, không ném.</summary>
    [Fact]
    public void DonKhongCoTrenHub_KhongGhi_KhongNem()
    {
        using var db = new HubDatabase(_dataDir);
        ThemDon(db, "SN-1");

        Assert.Equal(0, db.ApplyReturnCodesFromAppAlert([("SN-KHONG-CO", "RR-999")]));
    }

    /// <summary>Cặp rỗng/thiếu vế bị bỏ qua (không ghi mã rỗng đè lên mã đang có).</summary>
    [Fact]
    public void CapThieuVe_BiBoQua()
    {
        using var db = new HubDatabase(_dataDir);
        ThemDon(db, "SN-1");
        db.ApplyReturnCodesFromAppAlert([("SN-1", "RR-111")]);

        Assert.Equal(0, db.ApplyReturnCodesFromAppAlert([("SN-1", "   "), ("", "RR-333")]));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}
