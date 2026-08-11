using System.Collections.Concurrent;
using XuLyDonShopee.App.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// <b>T10 (review 11/08): dòng "mã QUÁ HẠN 14 ngày" chỉ được log khi SỐ ĐỔI.</b> Worker outbox gọi lượt sheet
/// mỗi 2' — không chốt thì MỘT sự việc đứng yên rắc ~63.000 dòng y hệt/ngày vào nhật ký, chôn kênh chẩn đoán.
/// Chốt theo mẫu <c>_tonDaBao</c> của HubOutboxWorker.
/// </summary>
public class HubOutboxQuaHanLatchTests
{
    [Fact]
    public void LanDauBao_LapLaiIm_DoiSoBaoLai_Ve0XoaChot_TangLaiBaoNhuMoi()
    {
        var daBao = new ConcurrentDictionary<long, int>();

        Assert.True(HubOutbox.NenBaoQuaHan(daBao, 1, 5));   // lần đầu xuất hiện → báo
        Assert.False(HubOutbox.NenBaoQuaHan(daBao, 1, 5));  // y hệt lần trước → im
        Assert.False(HubOutbox.NenBaoQuaHan(daBao, 1, 5));  // vẫn im (2'/lượt cả ngày)
        Assert.True(HubOutbox.NenBaoQuaHan(daBao, 1, 7));   // số ĐỔI (tăng) → báo lại
        Assert.True(HubOutbox.NenBaoQuaHan(daBao, 1, 3));   // số ĐỔI (giảm) → cũng báo
        Assert.False(HubOutbox.NenBaoQuaHan(daBao, 1, 0));  // hết sự việc → im + xoá chốt
        Assert.True(HubOutbox.NenBaoQuaHan(daBao, 1, 3));   // tăng lại SAU khi hết → báo như mới
    }

    [Fact]
    public void HaiTaiKhoan_ChotDocLap()
    {
        var daBao = new ConcurrentDictionary<long, int>();

        Assert.True(HubOutbox.NenBaoQuaHan(daBao, 1, 5));
        Assert.True(HubOutbox.NenBaoQuaHan(daBao, 2, 5));   // tài khoản khác — chốt riêng
        Assert.False(HubOutbox.NenBaoQuaHan(daBao, 1, 5));
        Assert.False(HubOutbox.NenBaoQuaHan(daBao, 2, 5));
    }
}
