using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test ĐƯỜNG THẬT của đồng bộ banner lỗi địa chỉ: <c>AccountsViewModel.MergeVaDayOutbox</c> + <c>DayLenHub</c>
/// (nối AppServices hook giả → SQLite thật → collection UI thật). Vùng này từng chứa 4 lỗi nặng mà test hàm
/// thuần không bắt được, vì lỗi nằm ở chỗ GHÉP các mảnh: thứ tự gọi, cờ outbox, và bẫy
/// <c>RunOnUi</c> = <c>BeginInvoke</c>.
/// <para>Trong test <c>Application.Current</c> là null nên <c>UiDispatch</c> chạy inline; các lệnh đẩy Hub vẫn
/// qua <c>Task.Run</c> thật nên phải chờ bằng <see cref="ChoDenKhi"/>.</para>
/// <para>Mỗi test dùng accountLogin RIÊNG: <c>PickupAlertHubGate</c> giữ chỗ trong dictionary static, xUnit
/// chạy các lớp test song song nên trùng khoá là tự chặn nhau.</para>
/// </summary>
public class PickupAlertSyncViewModelTests
{
    private const string Shop = "alina99.store";

    /// <summary>Chờ điều kiện thành true (mặc định 5s) — dùng cho việc đẩy Hub chạy trên Task.Run.</summary>
    private static bool ChoDenKhi(Func<bool> dieuKien, int msToiDa = 5000)
    {
        var het = Environment.TickCount64 + msToiDa;
        while (Environment.TickCount64 < het)
        {
            if (dieuKien()) return true;
            Thread.Sleep(15);
        }
        return dieuKien();
    }

    private static (AppServices Services, AccountsViewModel Vm, long AccountId) Dung(TempDatabase temp, string login)
    {
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = login, Password = "p" });
        var vm = new AccountsViewModel(services);
        var accountId = vm.Accounts.First().Id;
        services.Results.UpsertShops(accountId, [new ShopListItem("111", "Alina Store1", Shop)]);
        return (services, vm, accountId);
    }

    /// <summary>Ép một lượt sync mới (đổi tab về Kết quả kích <c>OnDetailTabIndexChanged</c>).</summary>
    private static void EpSync(AccountsViewModel vm)
    {
        vm.DetailTabIndex = 0;
        vm.DetailTabIndex = 1;
    }

    /// <summary>
    /// CA LÕI — lý do tồn tại của cờ outbox: Hub chết đúng lúc vòng shop phát hiện lỗi, trên Hub vẫn là
    /// tombstone của lần bấm X trước. Banner PHẢI còn (lỗi chưa sửa) và client phải đẩy lại lên Hub.
    /// Bản trước cờ outbox thì lượt sync kế nghe Hub và xoá mất cảnh báo.
    /// </summary>
    [Fact]
    public void HubTombstoneCu_KhongXoaDuocBannerDangChoDay()
    {
        using var temp = new TempDatabase();
        var (services, vm, accountId) = Dung(temp, "acc-tombstone");

        // Hub CHẾT lúc vòng shop chạy → máy này chưa từng nhận rev nào (hub_rev = 0).
        IReadOnlyList<PickupAlertHubDong>? tuHub = null;
        services.FetchPickupAlertsFromHub = (_, _) => Task.FromResult(tuHub);

        var soLanDay = 0;
        services.UpsertPickupAlertToHub = (_, _, _, _) =>
        {
            Interlocked.Increment(ref soLanDay);
            return Task.FromResult<long?>(null); // Hub vẫn chết → chưa nhận được
        };

        vm.SelectedRow = vm.Accounts.First();
        Assert.Empty(vm.AddressAlertRows);

        // Vòng shop phát hiện lỗi trong lúc Hub chết → banner + cờ chờ đẩy, hub_rev vẫn 0.
        new OrderPersistPipeline(accountId, services)
            .GhiBannerLoiDiaChi(Shop, "Thanh Hóa", _ => { }, CancellationToken.None);
        Assert.Single(vm.AddressAlertRows);
        Assert.Equal(0, services.PickupAlerts.ListAll(accountId).Single().HubRev);

        // Hub sống lại, MANG THEO tombstone cũ với rev MỚI HƠN mọi thứ máy này đang giữ.
        // Không có cờ outbox thì "rev Hub > rev đã nhận" ⇒ nghe Hub ⇒ banner của lỗi CHƯA SỬA bị xoá.
        tuHub = [new PickupAlertHubDong(Shop, "TH", Dismissed: true, Rev: 5)];
        EpSync(vm);

        Assert.Single(vm.AddressAlertRows);                              // banner CÒN
        Assert.True(vm.ResultRows.Single().CoLoiDiaChi);                 // dấu X đỏ CÒN
        Assert.True(ChoDenKhi(() => Volatile.Read(ref soLanDay) >= 1));  // và đã đẩy lại lên Hub
        Assert.Single(services.PickupAlerts.ListChoDay(accountId));      // cờ giữ vì Hub chưa nhận
    }

    /// <summary>
    /// KHÔNG HỒI QUY chức năng chính: máy khác bấm X (Hub tăng rev) → máy này gỡ banner + dấu X đỏ.
    /// </summary>
    [Fact]
    public void MayKhacBamX_RevMoiHon_MayNayGoBanner()
    {
        using var temp = new TempDatabase();
        var (services, vm, accountId) = Dung(temp, "acc-lan-dismiss");

        var hubDismissed = false;
        services.FetchPickupAlertsFromHub = (_, _) => Task.FromResult<IReadOnlyList<PickupAlertHubDong>?>(
            [new PickupAlertHubDong(Shop, "TH", hubDismissed, Rev: hubDismissed ? 7 : 6)]);
        services.UpsertPickupAlertToHub = (_, _, _, _) => Task.FromResult<long?>(6);

        vm.SelectedRow = vm.Accounts.First();       // Hub đang báo lỗi → banner lan sang máy này
        Assert.Single(vm.AddressAlertRows);
        Assert.True(vm.ResultRows.Single().CoLoiDiaChi);

        hubDismissed = true;                        // máy khác bấm X
        EpSync(vm);

        Assert.Empty(vm.AddressAlertRows);
        Assert.False(vm.ResultRows.Single().CoLoiDiaChi);
    }

    /// <summary>Đẩy được lên Hub → hạ cờ outbox và nhớ rev, không đẩy lại ở lượt sync sau.</summary>
    [Fact]
    public void DayDuocLenHub_HaCoOutbox_KhongDayLai()
    {
        using var temp = new TempDatabase();
        var (services, vm, accountId) = Dung(temp, "acc-ha-co");

        services.FetchPickupAlertsFromHub = (_, _) =>
            Task.FromResult<IReadOnlyList<PickupAlertHubDong>?>([]);

        var soLanDay = 0;
        services.UpsertPickupAlertToHub = (_, _, _, _) =>
        {
            Interlocked.Increment(ref soLanDay);
            return Task.FromResult<long?>(11);
        };

        vm.SelectedRow = vm.Accounts.First();
        new OrderPersistPipeline(accountId, services)
            .GhiBannerLoiDiaChi(Shop, "Thanh Hóa", _ => { }, CancellationToken.None);

        Assert.True(ChoDenKhi(() => services.PickupAlerts.ListChoDay(accountId).Count == 0));
        Assert.Equal(11, services.PickupAlerts.ListAll(accountId).Single().HubRev);

        var truoc = Volatile.Read(ref soLanDay);
        EpSync(vm);
        Assert.False(ChoDenKhi(() => Volatile.Read(ref soLanDay) > truoc, msToiDa: 400)); // không đẩy thừa
        Assert.Single(vm.AddressAlertRows);                                               // banner vẫn hiện
    }

    /// <summary>
    /// Dòng chờ đẩy mà Hub CHƯA hề có phải được đẩy: nó không nằm trong danh sách Hub trả về, bỏ sót là kẹt
    /// cờ vĩnh viễn. Đây là lý do vòng merge xét Hub ∪ outbox chứ không chỉ duyệt danh sách Hub.
    /// </summary>
    [Fact]
    public void DongChoDay_HubChuaCoDong_VanDuocDay()
    {
        using var temp = new TempDatabase();
        var (services, vm, accountId) = Dung(temp, "acc-outbox-drain");

        services.FetchPickupAlertsFromHub = (_, _) =>
            Task.FromResult<IReadOnlyList<PickupAlertHubDong>?>([]);   // Hub rỗng

        var hubChet = true;
        var soLanDay = 0;
        services.UpsertPickupAlertToHub = (_, _, _, _) =>
        {
            Interlocked.Increment(ref soLanDay);
            return Task.FromResult<long?>(hubChet ? null : 3);
        };

        vm.SelectedRow = vm.Accounts.First();
        new OrderPersistPipeline(accountId, services)
            .GhiBannerLoiDiaChi(Shop, "Thanh Hóa", _ => { }, CancellationToken.None);
        Assert.True(ChoDenKhi(() => Volatile.Read(ref soLanDay) >= 1));
        Assert.Single(services.PickupAlerts.ListChoDay(accountId));      // Hub chết → còn chờ đẩy

        hubChet = false;                 // Hub sống lại
        var truoc = Volatile.Read(ref soLanDay);
        EpSync(vm);                      // lượt sync phải TỰ rút hàng đợi dù Hub trả danh sách rỗng

        Assert.True(ChoDenKhi(() => Volatile.Read(ref soLanDay) > truoc));
        Assert.True(ChoDenKhi(() => services.PickupAlerts.ListChoDay(accountId).Count == 0));
    }

    /// <summary>
    /// Bấm X khi Hub chết: banner biến mất NGAY ở máy này, cờ outbox giữ lại, và lượt sync sau tự đẩy lại —
    /// lần bấm X không bao giờ mất.
    /// </summary>
    [Fact]
    public void BamXLucHubChet_GiuCoVaTuDayLaiSau()
    {
        using var temp = new TempDatabase();
        var (services, vm, accountId) = Dung(temp, "acc-bam-x");

        services.FetchPickupAlertsFromHub = (_, _) =>
            Task.FromResult<IReadOnlyList<PickupAlertHubDong>?>([]);
        services.UpsertPickupAlertToHub = (_, _, _, _) => Task.FromResult<long?>(1);

        var hubChet = true;
        var soLanDismiss = 0;
        services.DismissPickupAlertToHub = (_, _, _) =>
        {
            Interlocked.Increment(ref soLanDismiss);
            return Task.FromResult<long?>(hubChet ? null : 4);
        };

        vm.SelectedRow = vm.Accounts.First();
        new OrderPersistPipeline(accountId, services)
            .GhiBannerLoiDiaChi(Shop, "Thanh Hóa", _ => { }, CancellationToken.None);
        Assert.True(ChoDenKhi(() => services.PickupAlerts.ListChoDay(accountId).Count == 0));

        vm.DismissAddressAlertCommand.Execute(vm.AddressAlertRows.Single());

        Assert.Empty(vm.AddressAlertRows);                                   // biến mất ngay
        Assert.True(ChoDenKhi(() => Volatile.Read(ref soLanDismiss) >= 1));
        Assert.True(ChoDenKhi(() => services.PickupAlerts.ListChoDay(accountId).Count == 1)); // giữ cờ

        hubChet = false;
        var truoc = Volatile.Read(ref soLanDismiss);
        EpSync(vm);

        Assert.True(ChoDenKhi(() => Volatile.Read(ref soLanDismiss) > truoc));
        Assert.True(ChoDenKhi(() => services.PickupAlerts.ListChoDay(accountId).Count == 0));
        Assert.Empty(vm.AddressAlertRows);
    }

    /// <summary>Hub không nối (chế độ Shopee) → nhịp sync không ném, banner local vẫn hiện đúng.</summary>
    [Fact]
    public void KhongNoiHub_SyncKhongNem_BannerLocalVanHien()
    {
        using var temp = new TempDatabase();
        var (services, vm, accountId) = Dung(temp, "acc-khong-hub");

        services.FetchPickupAlertsFromHub = null;
        services.UpsertPickupAlertToHub = null;

        vm.SelectedRow = vm.Accounts.First();
        new OrderPersistPipeline(accountId, services)
            .GhiBannerLoiDiaChi(Shop, "Thanh Hóa", _ => { }, CancellationToken.None);

        EpSync(vm);
        Assert.Single(vm.AddressAlertRows);
    }
}
