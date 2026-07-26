using System.Globalization;
using System.Linq;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.Core.Data;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Số đơn "chuẩn bị hàng" CHUNG toàn hệ thống: client đánh dấu <c>prepared_at</c> cho ĐÚNG đơn vừa arrange rồi đẩy
/// lên hub; tab "Kết quả" lấy số từ hub, mất hub thì lùi về số cục bộ + ghi chú.
/// <list type="bullet">
/// <item>Phần kho (<see cref="OrdersRepository.MarkPrepared"/>): ghi LẦN ĐẦU, reset cờ đẩy hub, mã đơn lạ không phá gì.</item>
/// <item>Phần ViewModel: <b>null KHÁC map rỗng</b> — null = "không hỏi được hub" (giữ số máy), rỗng = "hub bảo 0".</item>
/// </list>
/// VM chạy trên thread test (CheckAccess()==true) → <c>RunOnUi</c> chạy đồng bộ, giống lúc phiên bắn về UI thread.
/// </summary>
public class PrepareHubCountTests
{
    private const string LoginA = "alina99.store";
    private const string LoginB = "shop9x.store";

    private static SyncedOrder Sample(string sn) => new()
    {
        OrderSn = sn,
        ItemsJson = "[]",
        Status = "Chờ lấy hàng",
    };

    // ===== 1. Ghi LẦN ĐẦU; gọi lại KHÔNG dời thời điểm; reset hub_synced_at để đẩy lại =====
    [Fact]
    public void MarkPrepared_GhiLanDau_GoiLaiKhongDoi_VaMoLaiHangDoiHub()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);
        repo.MarkHubSynced(1, new[] { "SN1" }, DateTime.UtcNow);
        Assert.Empty(repo.GetForHubPush(1)); // đã đẩy hub → không còn trong hàng đợi

        var lanDau = new DateTime(2026, 7, 27, 3, 0, 0, DateTimeKind.Utc);
        repo.MarkPrepared(1, "SN1", lanDau);

        // hub_synced_at về NULL → đơn quay lại hàng đợi để lượt đẩy kế mang prepared_at lên hub.
        var cho = repo.GetForHubPush(1);
        Assert.Equal(lanDau, Assert.Single(cho).PreparedAt!.Value);

        // Arrange lại / chạy lại ngày hôm sau → GIỮ mốc lần đầu (không dời số sang ngày khác).
        repo.MarkPrepared(1, "SN1", lanDau.AddDays(1));
        Assert.Equal(lanDau, Assert.Single(repo.GetForHubPush(1)).PreparedAt!.Value);
    }

    // ===== 2. Mã đơn không tồn tại / rỗng → không ném, KHÔNG đổi dòng nào =====
    [Fact]
    public void MarkPrepared_MaDonLa_KhongNem_KhongDungDongNao()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Sample("SN1") }, DateTime.UtcNow);
        repo.MarkHubSynced(1, new[] { "SN1" }, DateTime.UtcNow);

        repo.MarkPrepared(1, "SN-KHONG-CO", DateTime.UtcNow);
        repo.MarkPrepared(1, "   ", DateTime.UtcNow);

        // Không dòng nào bị đụng: cờ hub của SN1 còn nguyên (nếu bị reset thì đơn đã nhảy vào hàng đợi).
        Assert.Empty(repo.GetForHubPush(1));
    }

    // ===== 3. GetForHubPush trả ĐÚNG prepared_at đã ghi (đơn chưa đánh dấu → null) =====
    [Fact]
    public void GetForHubPush_TraPreparedAtDaGhi_DonChuaDanhDauLaNull()
    {
        using var temp = new TempDatabase();
        var repo = new OrdersRepository(temp.Open());
        repo.UpsertMany(1, new[] { Sample("SN1"), Sample("SN2") }, DateTime.UtcNow);

        var at = new DateTime(2026, 7, 27, 10, 30, 15, DateTimeKind.Utc);
        repo.MarkPrepared(1, "SN1", at);

        var cho = repo.GetForHubPush(1);
        Assert.Equal(at, cho.Single(o => o.OrderSn == "SN1").PreparedAt!.Value);
        Assert.Null(cho.Single(o => o.OrderSn == "SN2").PreparedAt);
    }

    // ===== Dựng VM đang mở MỘT tài khoản có 2 shop, số CỤC BỘ: A = 2, B = 1 =====
    private static (AppServices Services, AccountsViewModel Vm) NewVmVoiSoCucBo(TempDatabase temp)
    {
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "p" });

        var vm = new AccountsViewModel(services);
        var accountId = vm.Accounts.First().Id;
        services.Results.UpsertShops(accountId, new[]
        {
            new ShopListItem("111", "Alina Store1", LoginA),
            new ShopListItem("222", "Shop 9X", LoginB),
        });

        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        services.Results.IncrementPrepared(accountId, LoginA, today);
        services.Results.IncrementPrepared(accountId, LoginA, today);
        services.Results.IncrementPrepared(accountId, LoginB, today);

        vm.SelectedRow = vm.Accounts.First(); // nạp lưới kết quả (số cục bộ)
        Assert.Equal(2, vm.ResultRows.Count);
        Assert.Equal(2, Row(vm, LoginA).PreparedCount);
        Assert.Equal(1, Row(vm, LoginB).PreparedCount);
        Assert.False(vm.DangDungSoHub); // chưa hỏi hub lần nào
        return (services, vm);
    }

    private static ShopPrepareRow Row(AccountsViewModel vm, string login)
        => vm.ResultRows.Single(r => r.ShopLogin == login);

    // ===== 4. Hub trả map → số hub ĐÈ số máy; shop không có trong map → 0 (hub là nguồn sự thật) =====
    [Fact]
    public async Task HubTraMap_SoHubDeSoMay_ShopKhongCoTrongMapVe0()
    {
        using var temp = new TempDatabase();
        var (services, vm) = NewVmVoiSoCucBo(temp);

        string? ngayHoi = null;
        services.QueryPrepareStats = (day, _) =>
        {
            ngayHoi = day;
            return Task.FromResult<IReadOnlyDictionary<string, int>?>(
                new Dictionary<string, int>(StringComparer.Ordinal) { [LoginA] = 5 });
        };

        await vm.RefreshHubCountsAsync();

        Assert.Equal(vm.ResultDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ngayHoi);
        Assert.Equal(5, Row(vm, LoginA).PreparedCount); // hub ĐÈ (không cộng dồn với số máy)
        Assert.Equal(0, Row(vm, LoginB).PreparedCount); // hub không kể tên → 0, KHÔNG giữ số máy
        Assert.True(vm.DangDungSoHub);
    }

    // ===== 5. Hub trả NULL (không hỏi được) → GIỮ số cục bộ, cờ tắt =====
    [Fact]
    public async Task HubTraNull_GiuSoCucBo_CoBaoDangXemSoMay()
    {
        using var temp = new TempDatabase();
        var (services, vm) = NewVmVoiSoCucBo(temp);

        services.QueryPrepareStats = (_, _) => Task.FromResult<IReadOnlyDictionary<string, int>?>(null);

        await vm.RefreshHubCountsAsync();

        Assert.Equal(2, Row(vm, LoginA).PreparedCount);
        Assert.Equal(1, Row(vm, LoginB).PreparedCount);
        Assert.False(vm.DangDungSoHub);
    }

    // ===== BẪY CHÍNH: map RỖNG KHÁC null — "hub bảo 0 đơn" thì lưới PHẢI về 0 =====
    [Fact]
    public async Task HubTraMapRong_MoiDongVe0_VanLaSoHub()
    {
        using var temp = new TempDatabase();
        var (services, vm) = NewVmVoiSoCucBo(temp);

        services.QueryPrepareStats = (_, _) => Task.FromResult<IReadOnlyDictionary<string, int>?>(
            new Dictionary<string, int>(StringComparer.Ordinal));

        await vm.RefreshHubCountsAsync();

        Assert.All(vm.ResultRows, r => Assert.Equal(0, r.PreparedCount));
        Assert.True(vm.DangDungSoHub);
    }

    // ===== BẪY NẶNG: mỗi đơn arrange xong LoadResults dựng lại dòng bằng số CỤC BỘ — số hub KHÔNG được tụt =====
    // Máy chạy SAU (Hoàng): cục bộ 0, hub 2 → giữa lượt số phải GIỮ 2, không nhảy về 0 rồi mới về 2 lúc xong shop.
    [Fact]
    public async Task DonMoiArrangeXong_LoadResultsChayLai_KhongKeoSoVeCucBo()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "p" });
        var vm = new AccountsViewModel(services);
        var accountId = vm.Accounts.First().Id;
        services.Results.UpsertShops(accountId, new[]
        {
            new ShopListItem("111", "Alina Store1", LoginA),
            new ShopListItem("222", "Shop 9X", LoginB),
        });
        vm.SelectedRow = vm.Accounts.First();
        Assert.Equal(0, Row(vm, LoginA).PreparedCount); // máy này chưa chuẩn bị đơn nào (cục bộ = 0)

        services.QueryPrepareStats = (_, _) => Task.FromResult<IReadOnlyDictionary<string, int>?>(
            new Dictionary<string, int>(StringComparer.Ordinal) { [LoginA] = 2 });
        await vm.RefreshHubCountsAsync();
        Assert.Equal(2, Row(vm, LoginA).PreparedCount);

        // Máy này arrange xong 1 đơn của shop B → +1 cục bộ + phát PrepareCountChanged → LoadResults dựng lại.
        services.Results.IncrementPrepared(accountId, LoginB, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        services.RaisePrepareCountChanged(accountId);

        Assert.Equal(2, Row(vm, LoginA).PreparedCount); // GIỮ số hub, KHÔNG tụt về 0
        Assert.Equal(0, Row(vm, LoginB).PreparedCount); // hub chưa kể B → 0 (số hub, không phải số cục bộ 1)
        Assert.True(vm.DangDungSoHub);
    }

    // ===== Đổi NGÀY lọc → quên map hub của ngày cũ, KHÔNG áp nhầm sang ngày mới =====
    [Fact]
    public async Task DoiNgayLoc_KhongApNhamMapCuaNgayCu()
    {
        using var temp = new TempDatabase();
        var (services, vm) = NewVmVoiSoCucBo(temp);

        services.QueryPrepareStats = (_, _) => Task.FromResult<IReadOnlyDictionary<string, int>?>(
            new Dictionary<string, int>(StringComparer.Ordinal) { [LoginA] = 9 });
        await vm.RefreshHubCountsAsync();
        Assert.Equal(9, Row(vm, LoginA).PreparedCount);

        // Đổi sang ngày khác: LoadResults dựng số cục bộ của ngày đó (0) và KHÔNG được đội lốt số hub ngày cũ.
        services.QueryPrepareStats = null; // hub không trả lời cho ngày mới
        vm.ResultDate = vm.ResultDate.AddDays(-1);

        Assert.Equal(0, Row(vm, LoginA).PreparedCount);
        Assert.False(vm.DangDungSoHub);
    }

    // ===== BẪY LẶNG: shop_login lệch HOA/thường giữa hub và account_shops → vẫn phải ra đúng số =====
    [Fact]
    public async Task NhanShopLechHoaThuong_VanKhopDungSo()
    {
        using var temp = new TempDatabase();
        var (services, vm) = NewVmVoiSoCucBo(temp);
        Assert.Equal(LoginA, Row(vm, LoginA).ShopLogin); // dòng lưới: "alina99.store"

        // Hub trả nhãn viết hoa khác (và dư khoảng trắng) — map Ordinal của bên gọi cũng phải khớp được.
        services.QueryPrepareStats = (_, _) => Task.FromResult<IReadOnlyDictionary<string, int>?>(
            new Dictionary<string, int>(StringComparer.Ordinal) { ["  Alina99.Store "] = 7 });

        await vm.RefreshHubCountsAsync();

        Assert.Equal(7, Row(vm, LoginA).PreparedCount);
        Assert.Equal(0, Row(vm, LoginB).PreparedCount);
        Assert.True(vm.DangDungSoHub);
    }

    // ===== 6. Hook chưa rót (bản chạy KHÔNG có hub) → y như cũ, không ném =====
    [Fact]
    public async Task HookChuaRot_HanhViYNhuCu_KhongNem()
    {
        using var temp = new TempDatabase();
        var (services, vm) = NewVmVoiSoCucBo(temp);
        Assert.Null(services.QueryPrepareStats);

        await vm.RefreshHubCountsAsync();

        Assert.Equal(2, Row(vm, LoginA).PreparedCount);
        Assert.Equal(1, Row(vm, LoginB).PreparedCount);
        Assert.False(vm.DangDungSoHub);
    }
}
