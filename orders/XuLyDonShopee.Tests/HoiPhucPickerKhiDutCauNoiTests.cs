using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// HỒI PHỤC TRANG CHỌN SHOP PHẢI CHỊU ĐƯỢC CÚ ĐỨT CẦU NỐI.
/// <para>
/// Bối cảnh đo thật 10/08/2026: network service của trình duyệt bị dựng lại đều đặn ~240s/lần, mỗi lần kéo sập
/// WebSocket, extension nối lại sau 0–30s. Vòng 15:23→16:17 đi được 11/12 shop rồi mất shop cuối vì lượt hồi phục
/// thứ hai rơi ĐÚNG vào cú đứt lúc 16:17:15 và chết tức khắc — nhật ký không hề có dòng "lần 2/2", mà vòng vẫn
/// tuyên bố "Không quay lại được trang chọn shop" (đổ oan cho picker) rồi bỏ nốt shop 12.
/// </para>
/// <para>
/// Thuốc: CHỜ cầu nối sống lại trước mỗi lượt thử (<see cref="OrdersBridgeChannel.ChoNoiLaiAsync"/>), nới lên
/// <see cref="ShopFlowRunner.SoLanThuDongTabShop"/> lượt, và TÁCH ĐÔI hai nguyên nhân
/// (<see cref="KetQuaVePicker.CauNoiChet"/> vs <see cref="KetQuaVePicker.PickerHong"/>) để người trực không đi
/// chữa nhầm bệnh.
/// </para>
/// </summary>
public class HoiPhucPickerKhiDutCauNoiTests
{
    /// <summary>Hạn chờ cầu nối rót cho test — production là 45s, chờ ngần ấy trong test là vô nghĩa.</summary>
    private static readonly TimeSpan ChoCauNoiTest = TimeSpan.FromMilliseconds(150);

    /// <summary>Hạn "rộng rãi" cho ca cầu nối CÓ nối lại: phải đủ dài để lượt nối lại kịp về.</summary>
    private static readonly TimeSpan ChoCauNoiRong = TimeSpan.FromSeconds(10);

    private static ShopFlowRunner TaoFlow(BridgeTestRig rig)
        => new(rig.Channel, rig.Log, invoiceDir: null, province: "Thanh Hóa",
            syncCallback: null, finalDoneSns: null, onOrderPrepared: null,
            returnCountLast: null, saveReturnCount: null, saveReturnCodes: null);

    private static async Task NhanCloseRoiTraLoiAsync(BridgeTestRig rig, bool ok)
    {
        using var lenh = await rig.NhanLenhAsync(TimeSpan.FromSeconds(15), boQuaPing: true);
        Assert.Equal("closeShopTab", lenh.RootElement.GetProperty("action").GetString());
        await rig.GuiAsync(new { action = "shopTabClosed", ok });
    }

    // ===== ChoNoiLaiAsync =====

    [Fact]
    public async Task ChoNoiLai_DangNoi_ThiTraNgay()
    {
        await using var rig = await BridgeTestRig.StartAsync();

        var dongHo = Stopwatch.StartNew();
        Assert.True(await rig.Channel.ChoNoiLaiAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
        dongHo.Stop();

        Assert.True(dongHo.Elapsed < TimeSpan.FromSeconds(2),
            $"Đang nối mà vẫn ngồi chờ — {dongHo.Elapsed.TotalSeconds:0.0}s.");
    }

    [Fact]
    public async Task ChoNoiLai_DutRoiNoiLai_ThiTraTrue()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        await rig.NgatKetNoiAsync();

        var cho = rig.Channel.ChoNoiLaiAsync(ChoCauNoiRong, CancellationToken.None);
        await rig.NoiLaiAsync();

        Assert.True(await cho);
    }

    [Fact]
    public async Task ChoNoiLai_KhongBaoGioNoiLai_ThiTraFalseSauHan()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        await rig.NgatKetNoiAsync();

        Assert.False(await rig.Channel.ChoNoiLaiAsync(ChoCauNoiTest, CancellationToken.None));
    }

    // ===== DongTabShopAsync: ba ca bắt buộc =====

    [Fact]
    public async Task DongTabShop_CauNoiDutRoiNoiLaiKip_ThiVanVeDuocPicker()
    {
        // Ca (a) — đúng cảnh 16:15:15: đứt rồi extension quay lại. Lượt hồi phục KHÔNG được chết theo cú đứt đó.
        await using var rig = await BridgeTestRig.StartAsync();
        var flow = TaoFlow(rig);

        await rig.NgatKetNoiAsync();
        var chay = flow.DongTabShopAsync(CancellationToken.None, ChoCauNoiRong);
        await rig.NoiLaiAsync();
        await NhanCloseRoiTraLoiAsync(rig, ok: true);

        Assert.Equal(KetQuaVePicker.VeDuoc, await chay);
    }

    [Fact]
    public async Task DongTabShop_CauNoiKhongNoiLai_ThiBaoLyDoCauNoiChuKhongPhaiPicker()
    {
        // Ca (b) — bệnh nằm ở WebSocket/trình duyệt. Ghi thành "picker hỏng" là lần sửa sau đi nhầm chỗ.
        await using var rig = await BridgeTestRig.StartAsync();
        var flow = TaoFlow(rig);

        await rig.NgatKetNoiAsync();
        Assert.Equal(KetQuaVePicker.CauNoiChet,
            await flow.DongTabShopAsync(CancellationToken.None, ChoCauNoiTest));

        Assert.True(rig.CoLog("cầu nối chưa sống lại"));
        // KHÔNG được có dòng nào nói lệnh đã quá hạn: chưa hề gửi đi lệnh nào.
        Assert.False(rig.CoLog("closeShopTab quá hạn"));
    }

    [Fact]
    public async Task DongTabShop_ExtensionTraOkFalse_ThiBaoLyDoPicker()
    {
        // Ca (c) — cầu nối sống nguyên, extension trả lời đàng hoàng mà vẫn không về được trang chọn shop.
        await using var rig = await BridgeTestRig.StartAsync();
        var flow = TaoFlow(rig);

        var chay = flow.DongTabShopAsync(CancellationToken.None, ChoCauNoiRong);
        for (var lan = 0; lan < ShopFlowRunner.SoLanThuDongTabShop; lan++)
        {
            await NhanCloseRoiTraLoiAsync(rig, ok: false);
        }

        Assert.Equal(KetQuaVePicker.PickerHong, await chay);
    }

    [Fact]
    public async Task DongTabShop_LuotDauTruotVaLuotSauOk_ThiVeDuocPicker()
    {
        // Chính cơ chế hồi phục vốn có: lượt hai gửi lại closeShopTab thì extension điều hướng THẲNG listTabId về
        // /portal/shop. Bài này cố định rằng việc nới số lượt không làm hỏng đường đó.
        await using var rig = await BridgeTestRig.StartAsync();
        var flow = TaoFlow(rig);

        var chay = flow.DongTabShopAsync(CancellationToken.None, ChoCauNoiRong);
        await NhanCloseRoiTraLoiAsync(rig, ok: false);
        await NhanCloseRoiTraLoiAsync(rig, ok: true);

        Assert.Equal(KetQuaVePicker.VeDuoc, await chay);
    }

    // ===== PURE: câu lý do dừng vòng =====

    [Fact]
    public void LyDoDungVong_ViCauNoi_ThiNoiCauNoiChuKhongDoOanPicker()
    {
        var lyDo = OrdersBridgeSession.LyDoDungVongKhongVePicker("bizly.store", shopVuaHong: true, viCauNoi: true);
        Assert.Contains("Cầu nối extension KHÔNG sống lại", lyDo, StringComparison.Ordinal);
        Assert.Contains("bị lỗi", lyDo, StringComparison.Ordinal);
        Assert.DoesNotContain("Không quay lại được trang chọn shop", lyDo, StringComparison.Ordinal);
    }

    [Fact]
    public void LyDoDungVong_ViCauNoi_ShopKhongHong_ThiKhongCoVeBiLoi()
    {
        var lyDo = OrdersBridgeSession.LyDoDungVongKhongVePicker("shopA", shopVuaHong: false, viCauNoi: true);
        Assert.Contains("Cầu nối extension KHÔNG sống lại", lyDo, StringComparison.Ordinal);
        Assert.Contains("sau shop shopA —", lyDo, StringComparison.Ordinal);
        Assert.DoesNotContain("bị lỗi", lyDo, StringComparison.Ordinal);
    }

    [Fact]
    public void LyDoDungVong_ViPicker_ShopHong_ThiGiuNguyenCauCu()
    {
        var lyDo = OrdersBridgeSession.LyDoDungVongKhongVePicker("bizly.store", shopVuaHong: true, viCauNoi: false);
        Assert.Contains("Không quay lại được trang chọn shop sau shop bizly.store bị lỗi", lyDo, StringComparison.Ordinal);
    }

    [Fact]
    public void LyDoDungVong_ViPicker_ShopKhongHong_ThiGiuNguyenCauCu()
    {
        var lyDo = OrdersBridgeSession.LyDoDungVongKhongVePicker("shopA", shopVuaHong: false, viCauNoi: false);
        Assert.Contains("Không quay lại được trang chọn shop sau shop shopA —", lyDo, StringComparison.Ordinal);
    }

    // ===== PURE: mở lại trình duyệt hay dừng vòng =====

    [Fact]
    public void MatPicker_ConShopChuaChayVaChuaChamTran_ThiMoLaiTrinhDuyet()
    {
        Assert.Equal(KhiMatPicker.MoLaiTrinhDuyet,
            OrdersBridgeSession.QuyetDinhKhiMatPicker(conShopChuaChay: true, soLanDaMoLai: 0, captcha: false));
        // Ngay SÁT trần vẫn phải mở lại — trần là mốc dừng, không phải mốc cảnh báo.
        Assert.Equal(KhiMatPicker.MoLaiTrinhDuyet,
            OrdersBridgeSession.QuyetDinhKhiMatPicker(
                conShopChuaChay: true,
                soLanDaMoLai: OrdersBridgeSession.TranMoLaiTrinhDuyet - 1,
                captcha: false));
    }

    [Fact]
    public void MatPicker_ChamTranMoLai_ThiDungVong()
    {
        // Không có trần là mời một vòng lặp mở-đóng vô tận khi trình duyệt hỏng hẳn.
        Assert.Equal(KhiMatPicker.DungVong,
            OrdersBridgeSession.QuyetDinhKhiMatPicker(
                conShopChuaChay: true,
                soLanDaMoLai: OrdersBridgeSession.TranMoLaiTrinhDuyet,
                captcha: false));
        Assert.Equal(KhiMatPicker.DungVong,
            OrdersBridgeSession.QuyetDinhKhiMatPicker(
                conShopChuaChay: true,
                soLanDaMoLai: OrdersBridgeSession.TranMoLaiTrinhDuyet + 5,
                captcha: false));
    }

    [Fact]
    public void MatPicker_HetShopChuaChay_ThiKetThucBinhThuong()
    {
        // Luật CŨ giữ nguyên: picker hỏng sau shop CUỐI không hại ai — vòng đằng nào cũng kết thúc.
        Assert.Equal(KhiMatPicker.KetThucBinhThuong,
            OrdersBridgeSession.QuyetDinhKhiMatPicker(conShopChuaChay: false, soLanDaMoLai: 0, captcha: false));
    }

    [Fact]
    public void MatPicker_Captcha_ThiDungVongDuConShopVaChuaChamTran()
    {
        // Đẩy thêm một lượt mở trình duyệt vào đúng lúc Shopee đang nghi ngờ là tự khai bot — captcha xét TRƯỚC.
        Assert.Equal(KhiMatPicker.DungVong,
            OrdersBridgeSession.QuyetDinhKhiMatPicker(conShopChuaChay: true, soLanDaMoLai: 0, captcha: true));
    }

    // ===== PURE: ghép phần shop chưa chạy sau khi đọc lại picker =====

    private static ShopListItem Shop(string id) => new(id, $"Ten {id}", $"login{id}");

    private static IReadOnlySet<string> DaChay(params string[] ids)
        => new HashSet<string>(ids, StringComparer.Ordinal);

    [Fact]
    public void GhepShopConLai_GiuDungPhanConLai_KhongLapShopDaXong()
    {
        // RÀNG BUỘC QUAN TRỌNG NHẤT: chạy lại shop đã xong là đếm trùng, in phiếu trùng, đụng đơn THẬT.
        var moi = new[] { Shop("1"), Shop("2"), Shop("3"), Shop("4") };
        var ghep = OrdersBridgeSession.GhepShopConLai(moi, chuaChayCu: new[] { Shop("3"), Shop("4") },
            daChayShopId: DaChay("1", "2"));

        Assert.Equal(new[] { "3", "4" }, ghep.ConLai.Select(s => s.ShopId));
        Assert.Empty(ghep.BienMat);
    }

    [Fact]
    public void GhepShopConLai_DanhSachMoiDoiThuTu_ThiVanDungPhanConLai()
    {
        // Picker đọc lại có thể trả thứ tự khác → tin chỉ số `i` của danh sách cũ là chạy lại shop đã xong.
        var moi = new[] { Shop("4"), Shop("1"), Shop("3"), Shop("2") };
        var ghep = OrdersBridgeSession.GhepShopConLai(moi, chuaChayCu: new[] { Shop("3"), Shop("4") },
            daChayShopId: DaChay("1", "2"));

        Assert.Equal(new[] { "4", "3" }, ghep.ConLai.Select(s => s.ShopId)); // theo thứ tự danh sách MỚI
        Assert.Empty(ghep.BienMat);
    }

    [Fact]
    public void GhepShopConLai_ShopBienMatKhoiDanhSachMoi_ThiBoQuaVaNeuTen()
    {
        var moi = new[] { Shop("1"), Shop("3") };
        var ghep = OrdersBridgeSession.GhepShopConLai(moi, chuaChayCu: new[] { Shop("3"), Shop("4") },
            daChayShopId: DaChay("1", "2"));

        Assert.Equal(new[] { "3" }, ghep.ConLai.Select(s => s.ShopId));
        Assert.Equal(new[] { "login4" }, ghep.BienMat); // phải NÊU TÊN, cấm im lặng nuốt
    }

    [Fact]
    public void GhepShopConLai_ShopMoiXuatHien_ThiVanDuocChay()
    {
        // Shop mới chưa từng bị đụng trong vòng này → chạy được, không có rủi ro trùng.
        var moi = new[] { Shop("1"), Shop("2"), Shop("3"), Shop("9") };
        var ghep = OrdersBridgeSession.GhepShopConLai(moi, chuaChayCu: new[] { Shop("3") },
            daChayShopId: DaChay("1", "2"));

        Assert.Equal(new[] { "3", "9" }, ghep.ConLai.Select(s => s.ShopId));
        Assert.Empty(ghep.BienMat);
    }

    [Fact]
    public void GhepShopConLai_DaChayHetSachTrongDanhSachMoi_ThiConLaiRong()
    {
        var moi = new[] { Shop("1"), Shop("2") };
        var ghep = OrdersBridgeSession.GhepShopConLai(moi, chuaChayCu: Array.Empty<ShopListItem>(),
            daChayShopId: DaChay("1", "2"));

        Assert.Empty(ghep.ConLai);
        Assert.Empty(ghep.BienMat);
    }
}
