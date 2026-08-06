using System;
using System.Linq;
using System.Threading.Tasks;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Màn chẩn đoán "đơn kết thúc chưa dọn được" (H2.5) chạy HEADLESS: quét đúng những đơn ĐANG kẹt và nói đúng
/// lý do. Đơn còn chạy (chưa kết thúc) và đơn kết thúc đã xong nghĩa vụ đều KHÔNG được lên màn — lên là báo động
/// giả, người trực sẽ bấm "Đẩy lại" vô ích.
/// </summary>
public class ChanDoanDonViewModelTests
{
    private static (AppServices Services, long AccId) Moi(TempDatabase temp, string email = "sub@shopee")
    {
        var services = new AppServices(temp.Path);
        var accId = services.Accounts.Insert(new Account { Email = email, Password = "p" });
        return (services, accId);
    }

    private static SyncedOrder Don(string sn, string status, string? sku = null) => new()
    {
        OrderSn = sn,
        Status = status,
        Sku = sku,
        ItemsJson = "[]",
    };

    [Fact]
    public void DonConChay_KhongLenManChanDoan()
    {
        using var temp = new TempDatabase();
        var (services, acc) = Moi(temp);
        services.Orders.UpsertMany(acc, new[] { Don("SN1", "Chờ lấy hàng") }, DateTime.UtcNow, shopLogin: "shopA");

        var vm = new ChanDoanDonViewModel(services);
        Assert.Empty(vm.Rows);
        Assert.Contains("Không có đơn kết thúc nào đang kẹt", vm.SummaryText);
    }

    [Fact]
    public void DonKetThuc_HetNghiaVu_KhongLenManChanDoan()
    {
        // Không dùng GSheet (URL trống) + không có hub + Đã giao KHÔNG SKU → chẳng nợ gì, lượt sau sẽ dọn.
        using var temp = new TempDatabase();
        var (services, acc) = Moi(temp);
        services.Orders.UpsertMany(acc, new[] { Don("SN1", "Đã giao") }, DateTime.UtcNow, shopLogin: "shopA");

        var vm = new ChanDoanDonViewModel(services);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void DaGiao_CoSku_ChuaDem_LenMan_KemLyDo()
    {
        using var temp = new TempDatabase();
        var (services, acc) = Moi(temp);
        services.Orders.UpsertMany(acc, new[] { Don("SN1", "Đã giao", sku: "B02435") }, DateTime.UtcNow,
            shopLogin: "shopA");

        var vm = new ChanDoanDonViewModel(services);

        var row = Assert.Single(vm.Rows);
        Assert.Equal("SN1", row.OrderSn);
        Assert.Equal("shopA", row.ShopLabel);
        Assert.Equal("sub@shopee", row.TenTaiKhoan);
        Assert.Contains("Đã bán", row.ThieuText);
        Assert.Contains("1 đơn kết thúc chưa dọn được", vm.SummaryText);
    }

    [Fact]
    public void HubBat_ChuaDayHub_LenMan_KemLyDoHub()
    {
        using var temp = new TempDatabase();
        var (services, acc) = Moi(temp);
        // Hook hub đã rót ⇒ "chưa đẩy hub" là nghĩa vụ thật.
        services.PushOrdersToHub = (_, _, _) => Task.FromResult(true);
        services.Orders.UpsertMany(acc, new[] { Don("SN1", "Đã hủy") }, DateTime.UtcNow, shopLogin: "shopA");

        var vm = new ChanDoanDonViewModel(services);

        var row = Assert.Single(vm.Rows);
        Assert.Contains("Hub", row.ThieuText);
    }

    [Fact]
    public void CoUrlSheet_ChuaGhiSheet_LenMan_KemLyDoSheet()
    {
        using var temp = new TempDatabase();
        var (services, acc) = Moi(temp);
        services.Settings.SetGsheetWebAppUrl("https://script.google.com/macros/s/abc/exec");
        // Đơn Đã giao KHÔNG SKU (khỏi vướng nghĩa vụ đếm) nhưng chưa từng ghi dòng sheet.
        services.Orders.UpsertMany(acc, new[] { Don("SN1", "Đã giao") }, DateTime.UtcNow, shopLogin: "shopA");

        var vm = new ChanDoanDonViewModel(services);

        var row = Assert.Single(vm.Rows);
        Assert.Contains("Google Sheet", row.ThieuText);
    }

    [Fact]
    public void KhongCoUrlSheet_ThiKhongCoiLaNoSheet()
    {
        // URL Web App trống = không dùng Google Sheet → không được coi là "chưa ghi sheet" (cùng quy ước với
        // lượt đẩy sheet, nếu không thì MỌI đơn kết thúc của máy không dùng sheet đều hiện lên như đang kẹt).
        using var temp = new TempDatabase();
        var (services, acc) = Moi(temp);
        services.Orders.UpsertMany(acc, new[] { Don("SN1", "Đã giao") }, DateTime.UtcNow, shopLogin: "shopA");

        Assert.Empty(new ChanDoanDonViewModel(services).Rows);
    }

    // Nút "Làm mới" quét ở LUỒNG NỀN (ReloadCommand là AsyncRelayCommand) → phải ExecuteAsync + await, gọi
    // Execute() rồi assert ngay là đọc Rows trước khi quét xong.
    [Fact]
    public async Task DayLai_RoiReload_DonHetKetKhiNghiaVuDaXong()
    {
        // Không đi qua nút (hộp thoại xác nhận cần cửa sổ) — kiểm chính vòng: reset cờ → quét lại thấy đổi.
        using var temp = new TempDatabase();
        var (services, acc) = Moi(temp);
        services.Settings.SetGsheetWebAppUrl("https://script.google.com/macros/s/abc/exec");
        services.Orders.UpsertMany(acc, new[] { Don("SN1", "Đã giao") }, DateTime.UtcNow, shopLogin: "shopA");

        var vm = new ChanDoanDonViewModel(services);
        Assert.Single(vm.Rows);   // đang kẹt vì chưa ghi sheet

        // Ghi sheet xong → hết nghĩa vụ → quét lại phải sạch.
        services.Orders.MarkGsheetSynced(acc, "SN1", null, daHuy: false, coVanDon: false, coUocTinh: false,
            coDonTraHang: false, tab: "Tháng 08-2026", at: DateTime.UtcNow);
        await vm.ReloadCommand.ExecuteAsync(null);
        Assert.Empty(vm.Rows);

        // Bấm "Đẩy lại" (qua repository — đúng cái nút gọi) → đơn quay lại hàng chờ ⇒ lại hiện là đang kẹt.
        Assert.Equal(1, services.Orders.DatLaiCoDayLai(acc, "SN1"));
        await vm.ReloadCommand.ExecuteAsync(null);
        Assert.Single(vm.Rows);
    }

    [Fact]
    public void QuetMoiTaiKhoan_KhongChiTaiKhoanDauTien()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var acc1 = services.Accounts.Insert(new Account { Email = "a@shopee", Password = "p" });
        var acc2 = services.Accounts.Insert(new Account { Email = "b@shopee", Password = "p" });
        services.Orders.UpsertMany(acc1, new[] { Don("SN1", "Đã giao", sku: "B1") }, DateTime.UtcNow, shopLogin: "shopA");
        services.Orders.UpsertMany(acc2, new[] { Don("SN2", "Đã giao", sku: "B2") }, DateTime.UtcNow, shopLogin: "shopB");

        var vm = new ChanDoanDonViewModel(services);
        Assert.Equal(new[] { "SN1", "SN2" }, vm.Rows.Select(r => r.OrderSn).OrderBy(s => s).ToArray());
    }
}
