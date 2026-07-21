using System.Collections.Generic;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hàm THUẦN <see cref="KiotKeyPool.PickLeastUsed"/>: chọn key KiotProxy ít bận nhất từ pool chung
/// (ưu tiên key rảnh; hòa → key trước; key ngoài pool trong usage bị bỏ qua; pool rỗng → null).
/// </summary>
public class KiotKeyPoolTests
{
    [Fact]
    public void PoolRong_TraNull()
    {
        var chon = KiotKeyPool.PickLeastUsed(new List<string>(), new Dictionary<string, int>());
        Assert.Null(chon);
    }

    [Fact]
    public void MotKey_TraKeyDo_DuUsageCao()
    {
        var pool = new List<string> { "k1" };
        var usage = new Dictionary<string, int> { ["k1"] = 5 };

        Assert.Equal("k1", KiotKeyPool.PickLeastUsed(pool, usage));
    }

    [Fact]
    public void KeyChuaCoTrongUsage_CoiNhu0_UuTienRanh()
    {
        // k1 bận 2 phiên, k2 CHƯA có trong usage (coi như 0) → chọn k2 (rảnh).
        var pool = new List<string> { "k1", "k2" };
        var usage = new Dictionary<string, int> { ["k1"] = 2 };

        Assert.Equal("k2", KiotKeyPool.PickLeastUsed(pool, usage));
    }

    [Fact]
    public void NhieuKey_ChonItBanNhat()
    {
        var pool = new List<string> { "k1", "k2", "k3" };
        var usage = new Dictionary<string, int> { ["k1"] = 3, ["k2"] = 1, ["k3"] = 2 };

        Assert.Equal("k2", KiotKeyPool.PickLeastUsed(pool, usage));
    }

    [Fact]
    public void Hoa_ChonKeyDungTruocTrongPool()
    {
        // k1 và k2 cùng bận 2 → chọn k1 (đứng trước trong pool). Đảo thứ tự pool → k2 thắng.
        var usage = new Dictionary<string, int> { ["k1"] = 2, ["k2"] = 2 };

        Assert.Equal("k1", KiotKeyPool.PickLeastUsed(new List<string> { "k1", "k2" }, usage));
        Assert.Equal("k2", KiotKeyPool.PickLeastUsed(new List<string> { "k2", "k1" }, usage));
    }

    [Fact]
    public void KeyNgoaiPool_TrongUsage_BiBoQua()
    {
        // "ngoai" bận rất nhiều nhưng KHÔNG thuộc pool → không ảnh hưởng; trong pool k1 (0) < k2 (1) → k1.
        var pool = new List<string> { "k1", "k2" };
        var usage = new Dictionary<string, int> { ["ngoai"] = 99, ["k2"] = 1 };

        Assert.Equal("k1", KiotKeyPool.PickLeastUsed(pool, usage));
    }

    [Fact]
    public void UsageRong_TatCaRanh_ChonKeyDauPool()
    {
        var pool = new List<string> { "a", "b", "c" };
        Assert.Equal("a", KiotKeyPool.PickLeastUsed(pool, new Dictionary<string, int>()));
    }
}
