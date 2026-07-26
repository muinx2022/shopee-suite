using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test màn "Đơn toàn hệ thống" chạy headless (ViewModel + hook giả, KHÔNG dựng cửa sổ, KHÔNG cần Hub thật):
/// <list type="bullet">
/// <item>ba ca "không có dòng nào" (chưa kết nối Hub / Hub không phản hồi / Hub 0 đơn) PHẢI phân biệt được;</item>
/// <item>map dòng từ Hub ra lưới KHÔNG rỗng cột (kể cả tên shop tra từ id SỐ);</item>
/// <item>lọc + phân trang được gửi LÊN HUB (không tải hết về rồi lọc);</item>
/// <item><b>KHÔNG ghi một dòng nào vào CSDL local</b> — tiêu chí quan trọng nhất (chống lây nhiễm luồng đang chạy).</item>
/// </list>
/// </summary>
public class HubOrdersViewModelTests
{
    /// <summary>Một đơn "đầy đủ cột" như Hub trả về, để soi map DTO có rỗng cột không.</summary>
    private static HubOrderView FullOrder(string sn = "SN1", long shopId = 7) => new()
    {
        ShopId = shopId,
        OrderSn = sn,
        BuyerUsername = "buyer1",
        ItemCount = 3,
        ItemSummary = "Giày",
        Sku = "B02435",
        TotalPrice = 166500,
        FinalAmount = 150000,
        PaymentMethod = "COD",
        Status = "Chờ lấy hàng",
        Carrier = "SPX",
        TrackingNumber = "SPXVN123",
        SyncedAt = new DateTimeOffset(2026, 7, 26, 3, 30, 0, TimeSpan.Zero),
    };

    /// <summary>Rót hook giả: trả sẵn một trang kết quả + danh sách shop; ghi lại truy vấn cuối cùng.</summary>
    private static List<HubOrdersQuery> WireHooks(
        AppServices services,
        Func<HubOrdersQuery, HubOrdersResult?> respond,
        IReadOnlyList<(long Id, string Name)>? shops = null)
    {
        var seen = new List<HubOrdersQuery>();
        services.QueryHubOrders = (q, _) =>
        {
            seen.Add(q);
            return Task.FromResult(respond(q));
        };
        services.ListHubShops = _ => Task.FromResult(shops);
        return seen;
    }

    [Fact]
    public async Task HookChuaRot_BaoChuaKetNoiHub_KhongPhaiLuoiTrongCam()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);   // KHÔNG rót hook = app Đơn hàng chạy độc lập

        var vm = new HubOrdersViewModel(services);
        await vm.LoadAsync();

        Assert.Equal(HubOrdersState.NotConnected, vm.State);
        Assert.Equal("Máy này chưa kết nối Hub — không xem được đơn toàn hệ thống.", vm.EmptyMessage);
        Assert.True(vm.ShowMessage);
        Assert.False(vm.HasRows);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task HubKhongPhanHoi_BaoLoiHub_KhacHanCaChuaKetNoi()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        WireHooks(services, _ => null);   // hook CÓ rót nhưng hub trả null = không lấy được

        var vm = new HubOrdersViewModel(services);
        await vm.LoadAsync();

        Assert.Equal(HubOrdersState.HubError, vm.State);
        Assert.Equal("Không lấy được dữ liệu từ Hub (Hub không phản hồi). Thử Tải lại.", vm.EmptyMessage);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task HubTra0Don_BaoChuaCoDonNaoTrenHub()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        WireHooks(services, _ => new HubOrdersResult(Array.Empty<HubOrderView>(), 0, 1, 100));

        var vm = new HubOrdersViewModel(services);
        await vm.LoadAsync();

        Assert.Equal(HubOrdersState.Empty, vm.State);
        Assert.Equal("Chưa có đơn nào trên Hub.", vm.EmptyMessage);
    }

    [Fact]
    public async Task DangLocMaKhongRa_BaoKhacVoiHubHoanToanTrong()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        WireHooks(services, _ => new HubOrdersResult(Array.Empty<HubOrderView>(), 0, 1, 100));

        var vm = new HubOrdersViewModel(services);
        vm.SearchText = "khong-co-don-nao-nhu-vay";
        await vm.LoadAsync();

        Assert.Equal(HubOrdersState.FilteredEmpty, vm.State);
        Assert.NotEqual("Chưa có đơn nào trên Hub.", vm.EmptyMessage);   // 3 ca rỗng KHÔNG được trùng chữ
    }

    [Fact]
    public async Task BaThongBaoRong_KHAC_NHAU_TUNG_DOI_MOT()
    {
        var messages = new List<string>();
        foreach (var state in new[] { HubOrdersState.NotConnected, HubOrdersState.HubError, HubOrdersState.Empty })
        {
            using var temp = new TempDatabase();
            var services = new AppServices(temp.Path);
            if (state != HubOrdersState.NotConnected)
            {
                WireHooks(services, _ => state == HubOrdersState.HubError
                    ? null
                    : new HubOrdersResult(Array.Empty<HubOrderView>(), 0, 1, 100));
            }

            var vm = new HubOrdersViewModel(services);
            await vm.LoadAsync();
            Assert.Equal(state, vm.State);
            messages.Add(vm.EmptyMessage);
        }

        Assert.Equal(3, messages.Distinct().Count());
    }

    [Fact]
    public async Task MapDonTuHub_KhongRongCot_VaTraTenShopTuIdSo()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        WireHooks(services,
            _ => new HubOrdersResult(new[] { FullOrder() }, 1, 1, 100),
            shops: new[] { (7L, "alina99.store"), (8L, "shop-khac") });

        var vm = new HubOrdersViewModel(services);
        await vm.LoadAsync();

        Assert.Equal(HubOrdersState.Loaded, vm.State);
        var row = Assert.Single(vm.Rows);
        Assert.Equal("SN1", row.OrderSn);
        Assert.Equal("alina99.store", row.ShopLabel);      // shopId SỐ 7 → tên shop (tra 1 lần, không gọi mỗi dòng)
        Assert.Equal("buyer1", row.Buyer);
        Assert.Equal("Giày (+2)", row.Product);            // item_count 3 → "+2"
        Assert.Equal("B02435", row.Sku);
        Assert.Equal("₫166.500", row.Total);
        Assert.Equal("₫150.000", row.Estimate);            // cột "Ước tính" = final_amount
        Assert.Equal("Chờ lấy hàng", row.Status);
        Assert.Equal("SPX · SPXVN123", row.Shipping);
        Assert.False(string.IsNullOrWhiteSpace(row.SyncedAtDisplay));
        Assert.Equal("Đang hiển thị: 1/1 đơn (mọi máy)", vm.TotalText);
    }

    [Fact]
    public async Task KhongTraDuocTenShop_LuiVeShopId_ChuKhongDeTrong()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        WireHooks(services, _ => new HubOrdersResult(new[] { FullOrder(shopId: 42) }, 1, 1, 100), shops: null);

        var vm = new HubOrdersViewModel(services);
        await vm.LoadAsync();

        Assert.Equal("shop #42", Assert.Single(vm.Rows).ShopLabel);
    }

    [Fact]
    public async Task LocVaPhanTrang_DUOC_GUI_LEN_HUB_KhongLocTrongBoNho()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var seen = WireHooks(services,
            _ => new HubOrdersResult(new[] { FullOrder() }, 500, 1, 50),
            shops: new[] { (7L, "alina99.store") });

        var vm = new HubOrdersViewModel(services);
        await vm.LoadAsync();                                     // nạp danh sách shop + trang 1

        vm.SelectedShop = vm.ShopOptions.First(o => o.Id == 7);
        vm.SelectedStatus = "Chờ lấy hàng";
        vm.SearchText = "  SN1  ";
        vm.PageSize = 50;
        await vm.LoadAsync();

        var q = seen[^1];
        Assert.Equal(7, q.ShopId);
        Assert.Equal("Chờ lấy hàng", q.Status);
        Assert.Equal("SN1", q.Search);                            // đã trim
        Assert.Equal(50, q.PageSize);
        Assert.Equal(1, q.Page);                                  // đổi bộ lọc → về trang 1

        // Tổng do HUB đếm (500) chứ không phải đếm dòng đã tải (1) → phân trang đúng: 500/50 = 10 trang.
        Assert.Equal(500, vm.TotalCount);
        Assert.Equal(10, vm.TotalPages);

        await vm.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(2, seen[^1].Page);                           // sang trang 2 = HỎI LẠI HUB
    }

    [Fact]
    public async Task SentinelTatCa_KhongGuiBoLocLenHub()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var seen = WireHooks(services, _ => new HubOrdersResult(new[] { FullOrder() }, 1, 1, 100));

        var vm = new HubOrdersViewModel(services);
        await vm.LoadAsync();

        var q = Assert.Single(seen);
        Assert.Null(q.ShopId);      // "Tất cả shop"
        Assert.Null(q.Status);      // "Tất cả trạng thái"
        Assert.Null(q.Search);      // ô tìm trống
    }

    [Fact]
    public async Task DanhSachShop_ChiGoiMotLan_ChoNhieuLuotTai()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var shopCalls = 0;
        services.QueryHubOrders = (_, _) =>
            Task.FromResult<HubOrdersResult?>(new HubOrdersResult(new[] { FullOrder() }, 1, 1, 100));
        services.ListHubShops = _ =>
        {
            shopCalls++;
            return Task.FromResult<IReadOnlyList<(long Id, string Name)>?>(new[] { (7L, "alina99.store") });
        };

        var vm = new HubOrdersViewModel(services);
        await vm.LoadAsync();
        await vm.LoadAsync();
        await vm.LoadAsync();
        Assert.Equal(1, shopCalls);          // 3 lượt tải, 1 lần hỏi danh sách shop

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, shopCalls);          // bấm "Tải lại" → nạp lại shop (máy khác có thể vừa thêm shop)
    }

    [Fact]
    public async Task DangTaiLuotSau_GiuLuoiCu_KhongNhayVeKhoiThongBao()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var giu = new TaskCompletionSource<HubOrdersResult?>();
        var luot = 0;
        services.QueryHubOrders = (_, _) =>
            ++luot == 1
                ? Task.FromResult<HubOrdersResult?>(new HubOrdersResult(new[] { FullOrder() }, 1, 1, 100))
                : giu.Task;   // lượt 2 treo lại để soi trạng thái GIỮA CHỪNG

        var vm = new HubOrdersViewModel(services);
        await vm.LoadAsync();
        Assert.True(vm.HasRows);

        var dangTai = vm.LoadAsync();
        Assert.Equal(HubOrdersState.Loading, vm.State);
        Assert.True(vm.HasRows);          // lưới cũ VẪN hiện (không nháy sang khối thông báo)
        Assert.True(vm.IsLoading);

        giu.SetResult(new HubOrdersResult(new[] { FullOrder("SN2") }, 1, 1, 100));
        await dangTai;
        Assert.Equal(HubOrdersState.Loaded, vm.State);
        Assert.Equal("SN2", Assert.Single(vm.Rows).OrderSn);
    }

    [Fact]
    public async Task ChuaCoDongNaoMaDangTai_HienDangTai_KhongPhaiLuoiTrong()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var giu = new TaskCompletionSource<HubOrdersResult?>();
        services.QueryHubOrders = (_, _) => giu.Task;

        var vm = new HubOrdersViewModel(services);
        var dangTai = vm.LoadAsync();

        Assert.False(vm.HasRows);
        Assert.True(vm.ShowMessage);
        Assert.Equal("Đang tải đơn từ Hub…", vm.EmptyMessage);

        giu.SetResult(new HubOrdersResult(Array.Empty<HubOrderView>(), 0, 1, 100));
        await dangTai;
    }

    /// <summary>
    /// TIÊU CHÍ QUAN TRỌNG NHẤT: xem đơn toàn hệ thống KHÔNG được chép đơn về CSDL máy này. Đơn trên Hub thuộc
    /// shop của MÁY KHÁC (không có <c>account_id</c> local); chép vào bảng <c>orders</c> sẽ bị đẩy ngược lên Hub,
    /// ghi trùng dòng Google Sheet, bị vòng dọn "đơn kết thúc" xoá, bị vòng chờ đẩy nhặt nhầm.
    /// </summary>
    [Fact]
    public async Task XemDonToanHeThong_KHONG_GHI_GI_VAO_CSDL_LOCAL()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);

        // Nền: 1 đơn CỦA MÁY NÀY để chắc chắn đếm được sự thay đổi (0 → 0 cũng đúng nhưng yếu hơn).
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "LOCAL-1", Status = "Đã giao" } },
            DateTime.UtcNow, shopLogin: "shop-cua-may-nay");
        var truoc = services.Orders.Count();
        Assert.Equal(1, truoc);

        // Hub trả 3 đơn của máy khác (kể cả đơn TRÙNG mã với đơn local — bẫy upsert).
        WireHooks(services, _ => new HubOrdersResult(new[]
        {
            FullOrder("HUB-1"), FullOrder("HUB-2"), FullOrder("LOCAL-1"),
        }, 3, 1, 100), shops: new[] { (7L, "alina99.store") });

        var vm = new HubOrdersViewModel(services);
        await vm.LoadAsync();
        vm.SearchText = "HUB";
        await vm.LoadAsync();
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.Rows.Count);                       // màn CÓ hiện đơn của máy khác…
        Assert.Equal(truoc, services.Orders.Count());         // …nhưng CSDL local KHÔNG đổi số dòng
        var local = services.Orders.Query();
        Assert.Equal("LOCAL-1", Assert.Single(local).OrderSn);
        Assert.Equal("shop-cua-may-nay", Assert.Single(local).ShopLogin);   // dòng local KHÔNG bị đè bởi bản hub
    }
}
