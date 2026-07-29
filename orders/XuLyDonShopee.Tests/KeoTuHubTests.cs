using System.Linq;
using XuLyDonShopee.App.ViewModels;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hàm thuần <see cref="AccountsViewModel.TinhLoginCanThem"/> — chốt "kéo TK từ Hub KHÔNG đè dữ liệu local":
/// chỉ trả về login Hub biết mà máy CHƯA có, distinct ignore-case, giữ thứ tự, bỏ rỗng.
/// </summary>
public class KeoTuHubTests
{
    [Fact]
    public void ChiThemLoginChuaCoOMay()
    {
        var hub = new[] { "a@x", "b@x", "c@x" };
        var local = new[] { "a@x" };

        var toAdd = AccountsViewModel.TinhLoginCanThem(hub, local);

        Assert.Equal(new[] { "b@x", "c@x" }, toAdd);
    }

    [Fact]
    public void TrungHoaThuong_KhongThem()
    {
        var toAdd = AccountsViewModel.TinhLoginCanThem(new[] { "B@X" }, new[] { "b@x" });
        Assert.Empty(toAdd);
    }

    [Fact]
    public void Distinct_GiuThuTuGapDauTien()
    {
        var toAdd = AccountsViewModel.TinhLoginCanThem(new[] { "a@x", "A@X", "b@x" }, System.Array.Empty<string>());
        Assert.Equal(new[] { "a@x", "b@x" }, toAdd);
    }

    [Fact]
    public void BoRong_VaSpace()
    {
        var toAdd = AccountsViewModel.TinhLoginCanThem(new[] { "", "  ", " a@x " }, System.Array.Empty<string>());
        Assert.Equal(new[] { "a@x" }, toAdd);
    }

    [Fact]
    public void HubRong_TraRong()
    {
        Assert.Empty(AccountsViewModel.TinhLoginCanThem(System.Array.Empty<string>(), new[] { "a@x" }));
    }

    [Fact]
    public void LocalCoSpace_VanKhopIgnoreCase()
    {
        var toAdd = AccountsViewModel.TinhLoginCanThem(new[] { "a@x", "b@x" }, new[] { "  A@X  " });
        Assert.Equal(new[] { "b@x" }, toAdd);
    }
}
