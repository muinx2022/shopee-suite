using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// <see cref="OrderPersistPipeline"/> — phần HẬU XỬ LÝ tách khỏi phiên (ghi DB → đẩy GSheet/hub → notify). Chạy
/// được với SQLite tạm + hook để trống (không hub, không sheet, không trình duyệt), nên đây là chỗ chốt các luật
/// dễ vỡ nhất khi cầu nối gọi lại nhiều lần:
/// <list type="bullet">
/// <item>lưu 2 lần cùng lô đơn → CẬP NHẬT, KHÔNG đẻ dòng trùng;</item>
/// <item>đơn MỚI không ở trạng thái "chuẩn bị hàng" → BỎ QUA (chặn vòng lặp ghi-xóa với đơn đã bị dọn);</item>
/// <item>đơn ĐÃ theo dõi thì luôn cập nhật, kể cả khi đã rời trạng thái chuẩn bị hàng;</item>
/// <item>shop-context rót trước lượt lưu được gắn vào đơn (cột Shop / Tên Shop trên sheet).</item>
/// </list>
/// </summary>
public class OrderPersistPipelineTests
{
    private const long AccId = 4001;

    private static SyncedOrder Don(string sn, string status) => new()
    {
        OrderSn = sn,
        BuyerUsername = "nguoimua",
        ItemsJson = "[]",
        ItemCount = 0,
        Status = status,
    };

    private static (AppServices Services, OrderPersistPipeline Pipeline) Moi(TempDatabase temp)
    {
        var services = new AppServices(temp.Path);
        return (services, new OrderPersistPipeline(AccId, services));
    }

    private static Task<OrderPersistPipeline.PersistOrdersResult> Luu(
        OrderPersistPipeline p, params SyncedOrder[] orders)
        => p.PersistSyncedOrdersAsync("shop-id-1", orders, _ => { }, CancellationToken.None);

    [Fact]
    public async Task Luu2Lan_CungLoDon_KhongDeDongTrung()
    {
        using var temp = new TempDatabase();
        var (services, pipeline) = Moi(temp);

        var lan1 = await Luu(pipeline, Don("SN1", "Chờ lấy hàng"), Don("SN2", "Chờ lấy hàng"));
        var lan2 = await Luu(pipeline, Don("SN1", "Chờ lấy hàng"), Don("SN2", "Chờ lấy hàng"));

        Assert.Equal(2, lan1.Inserted);
        Assert.Equal(0, lan1.Updated);
        Assert.Equal(0, lan2.Inserted);   // lượt hai KHÔNG thêm mới
        Assert.Equal(2, lan2.Updated);
        Assert.Equal(2, services.Orders.CountByAccount(AccId)); // vẫn đúng 2 dòng trong kho
    }

    [Fact]
    public async Task DonMoi_KhongPhaiChuanBiHang_BiBoQua_KhongGhiVaoKho()
    {
        using var temp = new TempDatabase();
        var (services, pipeline) = Moi(temp);

        // Đơn chưa từng theo dõi + đã rời trạng thái chuẩn bị hàng (vd đơn ĐÃ GIAO vừa bị dọn khỏi app) → BỎ QUA:
        // nhận vào là lặp vòng ghi-xóa mỗi chu kỳ sync.
        var kq = await Luu(pipeline, Don("SN-CU", "Đã giao"));

        Assert.Equal(0, kq.Inserted);
        Assert.Equal(1, kq.BoQua);
        Assert.Equal(0, services.Orders.CountByAccount(AccId));
    }

    [Fact]
    public async Task DonDaTheoDoi_VanCapNhat_DuDaRoiTrangThaiChuanBi()
    {
        using var temp = new TempDatabase();
        var (services, pipeline) = Moi(temp);

        await Luu(pipeline, Don("SN1", "Chờ lấy hàng"));
        var kq = await Luu(pipeline, Don("SN1", "Đang giao"));

        Assert.Equal(0, kq.BoQua);
        Assert.Equal(1, kq.Updated);
        var trongKho = services.Orders.Query(AccId).Single();
        Assert.Equal("Đang giao", trongKho.Status);
    }

    [Fact]
    public async Task ShopContext_DuocGanVaoDon_ChoCotShop()
    {
        using var temp = new TempDatabase();
        var (services, pipeline) = Moi(temp);

        pipeline.SetShopContext("shop-id-1", "shop-mot");
        Assert.Equal("shop-mot", pipeline.CurrentShopLogin);
        await Luu(pipeline, Don("SN1", "Chờ lấy hàng"));

        Assert.Equal("shop-mot", services.Orders.Query(AccId).Single().ShopLogin);

        pipeline.SetShopContext(null, "   ");
        Assert.Null(pipeline.CurrentShopLogin); // nhãn rỗng → null (không ghi chuỗi trắng vào cột Shop)
    }
}
