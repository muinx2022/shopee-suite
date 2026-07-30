using XuLyDonShopee.App.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test chốt chặn GỬI TRÙNG tin "đơn trả hàng" (<see cref="OrderPersistPipeline.CoNenGuiNotifyLocal"/>): máy ĐÃ nối Hub
/// thì Hub bắn tin sau <c>orders/push</c> — client gửi nữa là người trực nhận hai tin. Máy chạy ĐỘC LẬP (chưa nối
/// Hub) vẫn phải tự gửi, kẻo mất hẳn thông báo.
/// </summary>
public class NotifyDonTraChanTrungTests
{
    private const string Url = "https://hooks.slack.com/services/x/y/z";

    [Fact]
    public void DaNoiHub_ThiKhongGuiLocal_DuCoUrl()
    {
        Assert.False(OrderPersistPipeline.CoNenGuiNotifyLocal(daNoiHub: true, Url, soMuc: 3));
    }

    [Fact]
    public void ChayDocLap_CoUrl_ThiGui()
    {
        // Máy không nối Hub: không ai gửi hộ → phải tự gửi.
        Assert.True(OrderPersistPipeline.CoNenGuiNotifyLocal(daNoiHub: false, Url, soMuc: 1));
    }

    [Fact]
    public void UrlTrong_ThiKhongGui_KhongNem()
    {
        Assert.False(OrderPersistPipeline.CoNenGuiNotifyLocal(daNoiHub: false, null, soMuc: 3));
        Assert.False(OrderPersistPipeline.CoNenGuiNotifyLocal(daNoiHub: false, "", soMuc: 3));
        Assert.False(OrderPersistPipeline.CoNenGuiNotifyLocal(daNoiHub: false, "   ", soMuc: 3));
    }

    [Fact]
    public void KhongCoMucNao_ThiKhongGui()
    {
        Assert.False(OrderPersistPipeline.CoNenGuiNotifyLocal(daNoiHub: false, Url, soMuc: 0));
    }
}
