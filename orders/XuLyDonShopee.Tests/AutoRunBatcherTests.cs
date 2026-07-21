using System.Collections.Generic;
using System.Linq;
using XuLyDonShopee.App.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hàm thuần chia lô <see cref="AutoRunBatcher.Split{T}"/>: đúng số lô, đúng phần dư, giữ thứ tự, và
/// các biên (rỗng, batchSize ≤ 0, batch lớn hơn danh sách).
/// </summary>
public class AutoRunBatcherTests
{
    [Fact]
    public void Split_7Phan_Lo3_ThanhBaLo_3_3_1()
    {
        var items = new[] { 1, 2, 3, 4, 5, 6, 7 };

        var batches = AutoRunBatcher.Split(items, 3);

        Assert.Equal(3, batches.Count);
        Assert.Equal(new[] { 1, 2, 3 }, batches[0]);
        Assert.Equal(new[] { 4, 5, 6 }, batches[1]);
        Assert.Equal(new[] { 7 }, batches[2]); // phần dư
    }

    [Fact]
    public void Split_ChiaChan_KhongLoDu()
    {
        var items = new[] { 1, 2, 3, 4, 5, 6 };

        var batches = AutoRunBatcher.Split(items, 3);

        Assert.Equal(2, batches.Count);
        Assert.Equal(new[] { 1, 2, 3 }, batches[0]);
        Assert.Equal(new[] { 4, 5, 6 }, batches[1]);
    }

    [Fact]
    public void Split_BatchLonHonDanhSach_MotLoDuyNhat()
    {
        var items = new[] { 1, 2 };

        var batches = AutoRunBatcher.Split(items, 5);

        Assert.Single(batches);
        Assert.Equal(new[] { 1, 2 }, batches[0]);
    }

    [Fact]
    public void Split_DanhSachRong_TraVeRong()
    {
        var batches = AutoRunBatcher.Split(new int[0], 3);

        Assert.Empty(batches);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Split_BatchSizeKhongHopLe_EpVe1_MoiPhanMotLo(int badSize)
    {
        var items = new[] { 10, 20, 30 };

        var batches = AutoRunBatcher.Split(items, badSize);

        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.Single(b));
        Assert.Equal(new[] { 10, 20, 30 }, batches.Select(b => b[0]));
    }

    [Fact]
    public void Split_GiuNguyenThuTu_VaBaoToanTatCaPhanTu()
    {
        var items = Enumerable.Range(1, 10).ToList();

        var batches = AutoRunBatcher.Split(items, 4);

        Assert.Equal(3, batches.Count); // 4 + 4 + 2
        var flat = batches.SelectMany(b => b).ToList();
        Assert.Equal(items, flat);
    }
}
