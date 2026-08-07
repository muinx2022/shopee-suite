using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test MỘT LƯỢT của vòng chờ đẩy chạy trên CSDL thật (file tạm) + hook hub GIẢ — kiểm đúng các tiêu chí sống còn:
/// đẩy bù khi đích sống lại, KHÔNG đẩy đôi (gate), đếm "Đã bán" đúng MỘT lần, và số tồn hiển thị.
/// Không đụng trình duyệt/hub thật; lượt được gọi thẳng qua <see cref="HubOutboxWorker.MotLuotAsync"/> nên không
/// phải chờ đồng hồ của vòng lặp nền.
/// </summary>
public class HubOutboxWorkerRoundTests
{
    /// <summary>Id tài khoản RIÊNG của lớp này — xem <see cref="TempDatabase.ThemTaiKhoanIdRieng{TLopTest}"/> (lấy id 1
    /// mặc định thì lớp này và <c>NotifyDonTraKhoMaTests</c> giành <c>PushGate(1, Gsheet)</c> của nhau).
    /// <b>Chép lớp test này đi nơi khác thì PHẢI đổi số này</b> — trùng id là dựng lại đúng cuộc đua đó.</summary>
    private const long AccId = 4201;

    /// <summary>Dựng bộ dịch vụ trên DB tạm + 1 tài khoản, trả (services, accountId).</summary>
    private static (AppServices Services, long AccountId) Dung(TempDatabase temp)
    {
        var services = new AppServices(temp.Path);
        return (services, TempDatabase.ThemTaiKhoanIdRieng<HubOutboxWorkerRoundTests>(services.Database, AccId));
    }

    private static SyncedOrder Order(string sn, string? status, string? sku = null)
        => new() { OrderSn = sn, Status = status, Sku = sku };

    [Fact]
    public async Task HubSongLai_DayBuDonTon_KhongCanShopNaoSync()
    {
        using var temp = new TempDatabase();
        var (services, accId) = Dung(temp);
        services.Orders.UpsertMany(accId, new[]
        {
            Order("SN1", "Chờ lấy hàng"),
            Order("SN2", "Chờ lấy hàng"),
        }, DateTime.UtcNow);

        var nhan = new List<string>();
        var hubSong = false;
        services.PushOrdersToHub = (id, orders, ct) =>
        {
            if (!hubSong) return Task.FromResult(false);   // hub đang chết
            nhan.AddRange(orders.Select(o => o.OrderSn));
            return Task.FromResult(true);
        };

        var worker = new HubOutboxWorker(services);

        // Lượt 1: hub chết → không đơn nào lên, đơn giữ nguyên trong hàng đợi, badge = 2.
        Assert.False(await worker.MotLuotAsync(CancellationToken.None));
        Assert.Empty(nhan);
        Assert.Equal(2, services.Orders.CountForHubPush(accId));
        Assert.Equal(2, services.PendingOutbox.Orders);

        // Lượt 2: hub sống lại (client đang NGHỈ giữa 2 vòng, không shop nào sync) → worker tự đẩy hết.
        hubSong = true;
        Assert.True(await worker.MotLuotAsync(CancellationToken.None));
        Assert.Equal(new[] { "SN1", "SN2" }, nhan);
        Assert.Equal(0, services.Orders.CountForHubPush(accId));
        Assert.Equal(0, services.PendingOutbox.Tong);   // badge tắt
    }

    [Fact]
    public async Task DemDaBan_DonDaGiao_ChiCong1Lan_DuChayHaiLuot()
    {
        using var temp = new TempDatabase();
        var (services, accId) = Dung(temp);

        // Đơn ĐÃ GIAO có SKU, chưa đếm — mô phỏng ca hub lỗi ở lượt của phiên (cờ sold_counted_at còn NULL
        // nhưng DB đã lưu trạng thái đã-giao) → DetectNewlyDelivered không còn thấy transition.
        services.Orders.UpsertMany(accId, new[] { Order("SN1", "Đã giao", "B00001") },
            DateTime.UtcNow - TimeSpan.FromMinutes(10));   // đã NGUỘI (qua chốt chống đếm đôi)

        var congSkus = new List<string>();
        services.IncrementSoldBySku = (skus, ct) => { congSkus.AddRange(skus); return Task.FromResult(true); };

        var worker = new HubOutboxWorker(services);

        Assert.True(await worker.MotLuotAsync(CancellationToken.None));
        Assert.Equal(new[] { "B00001" }, congSkus);     // đếm BÙ được (không mất đếm)

        // Lượt sau: cờ sold_counted_at đã set → KHÔNG đếm lại.
        await worker.MotLuotAsync(CancellationToken.None);
        Assert.Equal(new[] { "B00001" }, congSkus);
        Assert.Equal(0, services.PendingOutbox.SoldCounts);
    }

    [Fact]
    public async Task DemDaBan_HubLoi_KhongDanhCo_LuotSauDemLai()
    {
        using var temp = new TempDatabase();
        var (services, accId) = Dung(temp);
        services.Orders.UpsertMany(accId, new[] { Order("SN1", "Đã giao", "B00001") },
            DateTime.UtcNow - TimeSpan.FromMinutes(10));

        var soLanGoi = 0;
        var hubSong = false;
        services.IncrementSoldBySku = (skus, ct) => { soLanGoi++; return Task.FromResult(hubSong); };

        var worker = new HubOutboxWorker(services);

        Assert.False(await worker.MotLuotAsync(CancellationToken.None));
        Assert.Equal(1, soLanGoi);
        Assert.Single(services.Orders.GetForSoldCountRetry(accId));   // CHƯA đánh cờ → còn trong hàng đợi

        hubSong = true;
        Assert.True(await worker.MotLuotAsync(CancellationToken.None));
        Assert.Equal(2, soLanGoi);
        Assert.Empty(services.Orders.GetForSoldCountRetry(accId));    // hub OK → đánh cờ, hàng đợi sạch
    }

    [Fact]
    public async Task DemDaBan_DonVUA_GHI_ChuaNguoi_WorkerKhongDem()
    {
        using var temp = new TempDatabase();
        var (services, accId) = Dung(temp);
        // Đơn vừa được phiên ghi XONG (updated_at = bây giờ) — phiên có thể đang đánh cờ grandfather cho chính nó.
        services.Orders.UpsertMany(accId, new[] { Order("SN1", "Đã giao", "B00001") }, DateTime.UtcNow);

        var goi = 0;
        services.IncrementSoldBySku = (skus, ct) => { goi++; return Task.FromResult(true); };

        var worker = new HubOutboxWorker(services);
        await worker.MotLuotAsync(CancellationToken.None);

        Assert.Equal(0, goi);   // chốt chống ĐẾM ĐÔI: chưa nguội thì worker không đụng vào
    }

    [Fact]
    public async Task PhienDangDay_WorkerBoQua_KhongDayDoi()
    {
        using var temp = new TempDatabase();
        var (services, accId) = Dung(temp);
        services.Orders.UpsertMany(accId, new[] { Order("SN1", "Đã giao", "B00001") },
            DateTime.UtcNow - TimeSpan.FromMinutes(10));

        var goi = 0;
        services.IncrementSoldBySku = (skus, ct) => { goi++; return Task.FromResult(true); };

        var worker = new HubOutboxWorker(services);

        // Mô phỏng PHIÊN đang giữ chỗ (lượt +1 của phiên đang chạy).
        Assert.True(PushGate.TryEnter(accId, PushKind.SoldCount));
        try
        {
            await worker.MotLuotAsync(CancellationToken.None);
            Assert.Equal(0, goi);   // worker KHÔNG được +1 chồng lên phiên
        }
        finally
        {
            PushGate.Exit(accId, PushKind.SoldCount);
        }

        // Phiên nhả chỗ → lượt sau worker đếm bù được.
        await worker.MotLuotAsync(CancellationToken.None);
        Assert.Equal(1, goi);
    }

    [Fact]
    public async Task KhongCoHub_KhongCoSheet_LuotChayEm_BadgeVeKhong()
    {
        using var temp = new TempDatabase();
        var (services, accId) = Dung(temp);
        services.Orders.UpsertMany(accId, new[] { Order("SN1", "Chờ lấy hàng") }, DateTime.UtcNow);
        // Không rót hook nào + không cấu hình URL Web App = app Đơn hàng chạy độc lập.

        var worker = new HubOutboxWorker(services);

        Assert.Null(await worker.MotLuotAsync(CancellationToken.None));  // không có gì để đẩy → không tính lỗi
        Assert.Equal(0, services.PendingOutbox.Tong);                    // badge KHÔNG kẹt số
    }

    [Fact]
    public async Task DayDon_RoiMoiDayPhieu_DungThuTu()
    {
        using var temp = new TempDatabase();
        var (services, accId) = Dung(temp);
        services.Orders.UpsertMany(accId, new[]
        {
            new SyncedOrder { OrderSn = "SN1", Status = "Chờ lấy hàng", TrackingNumber = "VD1" },
        }, DateTime.UtcNow);

        var thuTu = new List<string>();
        services.PushOrdersToHub = (id, orders, ct) => { thuTu.Add("don"); return Task.FromResult(true); };
        services.PushOrderSlipsToHub = (id, slips, ct) =>
        {
            thuTu.Add("phieu");
            return Task.FromResult<IReadOnlyList<string>?>(slips.Select(s => s.OrderSn).ToList());
        };

        var worker = new HubOutboxWorker(services);

        // Lượt 1: chỉ có đơn chờ (phiếu chưa đủ điều kiện — đơn chưa lên hub lúc bắt đầu lượt).
        await worker.MotLuotAsync(CancellationToken.None);
        Assert.Equal(new[] { "don" }, thuTu);
        Assert.Equal(1, services.Orders.CountForHubSlipPush(accId));   // giờ mới tới lượt phiếu

        // Lượt 2: đơn đã lên hub → tới phiếu. (File phiếu local không tồn tại nên hook không được gọi —
        // đúng hành vi "file thiếu thì bỏ qua, lượt sau đẩy".)
        await worker.MotLuotAsync(CancellationToken.None);
        Assert.Equal(new[] { "don" }, thuTu);
    }
}
