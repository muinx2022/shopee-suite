using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// TỰ TẢI LẠI PHIẾU THIẾU trong vòng check shop (2026-08-06). Phiếu chỉ được lưu ĐÚNG MỘT LẦN lúc chuẩn bị hàng;
/// lưu hỏng / đơn arrange ở vòng-máy khác ⇒ trước đây không có đường thử lại. Ba nhóm test:
/// <list type="bullet">
/// <item>hàm THUẦN <see cref="DonThieuPhieu.ChonDonThieuPhieu"/> (App) — CHỌN đơn nào, xếp mới nhất trước;</item>
/// <item>hàm THUẦN <see cref="ShopFlowRunner.ChiaTheoTranTaiLaiPhieu"/> (Core) — cắt theo TRẦN, đếm phần để lại;</item>
/// <item>bước chạy thật qua cầu nối giả (<see cref="BridgeTestRig"/>) — hai cái phanh (trần + KHÔNG thử lại trong
/// cùng vòng) và các ca BỎ HẲN bước (chưa rót callback / thiếu thư mục phiếu / đã captcha).</item>
/// </list>
/// </summary>
public class TaiLaiPhieuThieuTests
{
    private const string Tinh = "Thanh Hóa";

    /// <summary>Runner tối giản cho các test ở đây: chỉ rót callback lấy danh sách + thư mục phiếu.</summary>
    private static ShopFlowRunner Runner(
        BridgeTestRig rig,
        string? invoiceDir,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? layDonThieuPhieu,
        Func<string, string, IReadOnlyList<SyncedOrder>, CancellationToken, Task>? syncCallback = null)
        => new(rig.Channel, rig.Log, invoiceDir, Tinh, syncCallback, finalDoneSns: null,
            onOrderPrepared: null, returnCountLast: null, saveReturnCount: null, saveReturnCodes: null,
            layDonThieuPhieu: layDonThieuPhieu);

    private static string ThuMucTam() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xlds_taila_" + Guid.NewGuid().ToString("N"));

    private static string Base64Pdf() => Convert.ToBase64String(Encoding.UTF8.GetBytes("%PDF-1.4 giả lập"));

    /// <summary>Nhận đúng một lệnh <c>redownloadSlip</c> và trả về mã đơn của lệnh đó.</summary>
    private static async Task<string?> NhanLenhTaiLaiAsync(BridgeTestRig rig, TimeSpan? timeout = null)
    {
        using var lenh = await rig.NhanLenhAsync(timeout);
        Assert.Equal("redownloadSlip", lenh.RootElement.GetProperty("action").GetString());
        return lenh.RootElement.GetProperty("orderSn").GetString();
    }

    // ===== 1. Hàm thuần phía App: CHỌN đơn thiếu phiếu =====

    [Fact]
    public void ChonDonThieuPhieu_BoDonChuaCoVanDon_VaDonDaCoFile()
    {
        var nguon = new[]
        {
            new DonUngVienTaiLaiPhieu("SN-CO-FILE", "TRK1", CoFilePhieu: true, Moc: 10),   // đã có phiếu → bỏ
            new DonUngVienTaiLaiPhieu("SN-CHUA-ARRANGE", null, CoFilePhieu: false, Moc: 9), // chưa có vận đơn → bỏ
            new DonUngVienTaiLaiPhieu("SN-TRK-RONG", "  ", CoFilePhieu: false, Moc: 8),     // vận đơn rỗng → bỏ
            new DonUngVienTaiLaiPhieu("SN-THIEU", "TRK2", CoFilePhieu: false, Moc: 7),      // GIỮ
        };

        Assert.Equal(new[] { "SN-THIEU" }, DonThieuPhieu.ChonDonThieuPhieu(nguon));
    }

    /// <summary>
    /// Đơn ĐÃ HỦY phải bị loại khỏi danh sách tự tải lại. Đơn hủy vẫn có mã vận đơn + vẫn thiếu file phiếu, mà
    /// Seller Centre KHÔNG còn nút "In phiếu giao" cho nó ⇒ lượt tải lại luôn trượt. Không loại thì mấy đơn này
    /// chiếm suất trần 20 của MỌI vòng, VĨNH VIỄN — "xếp mới nhất trước" chỉ đẩy chúng xuống đáy, không cứu được.
    /// </summary>
    [Fact]
    public void ChonDonThieuPhieu_BoDonDaHuy_KhongDeNgonSuatTranMoiVong()
    {
        var nguon = new[]
        {
            new DonUngVienTaiLaiPhieu("SN-HUY-MOI", "TRK1", CoFilePhieu: false, Moc: 99, DaHuy: true),
            new DonUngVienTaiLaiPhieu("SN-CON-SONG", "TRK2", CoFilePhieu: false, Moc: 5, DaHuy: false),
        };

        // Đơn hủy tuy MỚI hơn (Moc 99) vẫn không được chen lên trước — nó phải biến mất hẳn.
        Assert.Equal(new[] { "SN-CON-SONG" }, DonThieuPhieu.ChonDonThieuPhieu(nguon));
    }

    [Fact]
    public void ChonDonThieuPhieu_XepMoiNhatTruoc()
    {
        // Đầu vào CỐ Ý xáo trộn: luật "ưu tiên đơn mới nhất" phải nằm trong hàm, không phụ thuộc thứ tự caller.
        var nguon = new[]
        {
            new DonUngVienTaiLaiPhieu("SN-CU", "TRK", CoFilePhieu: false, Moc: 1),
            new DonUngVienTaiLaiPhieu("SN-MOI-NHAT", "TRK", CoFilePhieu: false, Moc: 99),
            new DonUngVienTaiLaiPhieu("SN-GIUA", "TRK", CoFilePhieu: false, Moc: 50),
        };

        Assert.Equal(new[] { "SN-MOI-NHAT", "SN-GIUA", "SN-CU" }, DonThieuPhieu.ChonDonThieuPhieu(nguon));
    }

    [Fact]
    public void ChonDonThieuPhieu_BoMaRong_GopMaTrung_GiuBanMoiNhat()
    {
        var nguon = new[]
        {
            new DonUngVienTaiLaiPhieu("  ", "TRK", CoFilePhieu: false, Moc: 100),
            new DonUngVienTaiLaiPhieu(null, "TRK", CoFilePhieu: false, Moc: 100),
            new DonUngVienTaiLaiPhieu("SN-A", "TRK", CoFilePhieu: false, Moc: 5),
            new DonUngVienTaiLaiPhieu("SN-A", "TRK", CoFilePhieu: false, Moc: 60),
            new DonUngVienTaiLaiPhieu("SN-B", "TRK", CoFilePhieu: false, Moc: 30),
        };

        // SN-A gộp thành 1 và đứng trước SN-B (bản Moc=60 thắng).
        Assert.Equal(new[] { "SN-A", "SN-B" }, DonThieuPhieu.ChonDonThieuPhieu(nguon));
    }

    [Fact]
    public void ChonDonThieuPhieu_NguonRongHoacNull_TraRong()
    {
        Assert.Empty(DonThieuPhieu.ChonDonThieuPhieu(null));
        Assert.Empty(DonThieuPhieu.ChonDonThieuPhieu(Array.Empty<DonUngVienTaiLaiPhieu>()));
    }

    // ===== 2. Hàm thuần phía Core: cắt theo TRẦN =====

    [Fact]
    public void ChiaTheoTran_LayDungSoTranODAU_PhanConLaiDemDung()
    {
        var ds = Enumerable.Range(1, 25).Select(i => "SN" + i).ToList();

        var (canTai, conLai) = ShopFlowRunner.ChiaTheoTranTaiLaiPhieu(ds, 20);

        Assert.Equal(20, canTai.Count);
        Assert.Equal("SN1", canTai[0]);     // giữ nguyên thứ tự caller đưa (mới nhất trước)
        Assert.Equal("SN20", canTai[19]);
        Assert.Equal(5, conLai);            // 5 đơn để vòng sau — phải LOG, không im lặng cắt
    }

    [Fact]
    public void ChiaTheoTran_ItHonTran_ThiKhongConLai()
    {
        var (canTai, conLai) = ShopFlowRunner.ChiaTheoTranTaiLaiPhieu(new[] { "A", "B" }, 20);
        Assert.Equal(new[] { "A", "B" }, canTai);
        Assert.Equal(0, conLai);
    }

    [Fact]
    public void ChiaTheoTran_BoMaRongVaTrung_TruocKhiCat()
    {
        var (canTai, conLai) = ShopFlowRunner.ChiaTheoTranTaiLaiPhieu(
            new[] { "A", "", "A", "  ", "B", "A" }, 2);

        Assert.Equal(new[] { "A", "B" }, canTai);   // mã trùng KHÔNG được ăn mất suất của trần
        Assert.Equal(0, conLai);
    }

    [Fact]
    public void ChiaTheoTran_TranKhongDuong_ThiKhongTaiGiNhung_VanDemDuConLai()
    {
        var (canTai, conLai) = ShopFlowRunner.ChiaTheoTranTaiLaiPhieu(new[] { "A", "B" }, 0);
        Assert.Empty(canTai);
        Assert.Equal(2, conLai);
    }

    [Fact]
    public void ChiaTheoTran_DanhSachRongHoacNull_TraRong()
    {
        Assert.Empty(ShopFlowRunner.ChiaTheoTranTaiLaiPhieu(null, 20).CanTai);
        Assert.Equal(0, ShopFlowRunner.ChiaTheoTranTaiLaiPhieu(Array.Empty<string>(), 20).ConLai);
    }

    // ===== 3. Bước chạy thật qua cầu nối giả =====

    [Fact]
    public async Task TaiLaiPhieuThieu_TaiTungDon_LuuFile_VaLogTongKet()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, dir, (_, _) =>
                Task.FromResult<IReadOnlyList<string>>(new[] { "SN-1", "SN-2" }));

            var chay = flow.TaiLaiPhieuThieuAsync("shop1", CancellationToken.None);

            Assert.Equal("SN-1", await NhanLenhTaiLaiAsync(rig));
            await rig.GuiAsync(new { action = "slipRedownloaded", slipBase64 = Base64Pdf() });
            Assert.Equal("SN-2", await NhanLenhTaiLaiAsync(rig));
            await rig.GuiAsync(new { action = "slipRedownloaded", slipBase64 = Base64Pdf() });
            await chay;

            Assert.True(File.Exists(Path.Combine(dir, "SN-1.pdf")));
            Assert.True(File.Exists(Path.Combine(dir, "SN-2.pdf")));
            Assert.True(rig.CoLog("Tải lại phiếu thiếu shop shop1: 2/2 thành công (còn 0 đơn chưa thử"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    [Fact]
    public async Task TaiLaiPhieuThieu_ChanTheoTran20_VaLogSoBoLai()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            // 23 đơn thiếu phiếu (ca "lần đầu bật tính năng, tồn đọng nhiều"): chỉ được tải 20 đơn ĐẦU.
            var ds = Enumerable.Range(1, 23).Select(i => "SN" + i).ToArray();
            var flow = Runner(rig, dir, (_, _) => Task.FromResult<IReadOnlyList<string>>(ds));

            var chay = flow.TaiLaiPhieuThieuAsync("shop1", CancellationToken.None);

            var daGui = new List<string?>();
            for (var i = 0; i < 20; i++)
            {
                daGui.Add(await NhanLenhTaiLaiAsync(rig));
                await rig.GuiAsync(new { action = "slipRedownloaded", slipBase64 = Base64Pdf() });
            }
            await chay;

            Assert.Equal(ds.Take(20), daGui);
            // Đơn thứ 21 KHÔNG được gửi (nếu có, NhanLenhAsync nhận được thay vì hết giờ).
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
            Assert.True(rig.CoLog("để lại 3 đơn cho vòng sau (trần 20)"));
            Assert.True(rig.CoLog("20/20 thành công (còn 3 đơn chưa thử"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    [Fact]
    public async Task TaiLaiPhieuThieu_ExtensionTraRong_KhongThuLaiTrongCungVong()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var flow = Runner(rig, dir, (_, _) =>
                Task.FromResult<IReadOnlyList<string>>(new[] { "SN-CU", "SN-MOI" }));

            var chay = flow.TaiLaiPhieuThieuAsync("shop1", CancellationToken.None);

            // Đơn quá cũ đã rơi khỏi danh sách "Tất cả" → extension trả base64 rỗng.
            Assert.Equal("SN-CU", await NhanLenhTaiLaiAsync(rig));
            await rig.GuiAsync(new { action = "slipRedownloaded", slipBase64 = "" });

            // Lệnh KẾ TIẾP phải là đơn KHÁC (không gửi lại SN-CU) — đó là luật "không retry trong cùng vòng".
            Assert.Equal("SN-MOI", await NhanLenhTaiLaiAsync(rig));
            await rig.GuiAsync(new { action = "slipRedownloaded", slipBase64 = Base64Pdf() });
            await chay;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
            Assert.True(rig.CoLog("Không nhận được phiếu đơn SN-CU"));
            Assert.True(rig.CoLog("1/2 thành công"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }

    [Fact]
    public async Task TaiLaiPhieuThieu_ChuaCauHinhThuMucPhieu_BoHanBuoc_KhongGuiLenh()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var goi = 0;
        var flow = Runner(rig, invoiceDir: null, layDonThieuPhieu: (_, _) =>
        {
            goi++;
            return Task.FromResult<IReadOnlyList<string>>(new[] { "SN-1" });
        });

        await flow.TaiLaiPhieuThieuAsync("shop1", CancellationToken.None);

        Assert.Equal(0, goi);   // không cả hỏi DB
        Assert.True(rig.CoLog("chưa cấu hình thư mục lưu phiếu"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task TaiLaiPhieuThieu_DaDinhCaptcha_BoHanBuoc_KhongGuiLenh()
    {
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        var goi = 0;
        var flow = Runner(rig, dir, (_, _) =>
        {
            goi++;
            return Task.FromResult<IReadOnlyList<string>>(new[] { "SN-1" });
        });
        rig.Channel.CaptchaSeen = true;   // bước trước vừa dính captcha

        await flow.TaiLaiPhieuThieuAsync("shop1", CancellationToken.None);

        Assert.Equal(0, goi);
        Assert.True(rig.CoLog("đã dính captcha từ bước trước — bỏ bước này (để vòng sau)"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task TaiLaiPhieuThieu_ChuaRotCallback_BoHanBuoc()
    {
        // Đường "Chạy thử" (RunSliceCoreAsync): phiên dựng KHÔNG rót callback nào → bước này phải tắt hẳn.
        await using var rig = await BridgeTestRig.StartAsync();
        var flow = Runner(rig, ThuMucTam(), layDonThieuPhieu: null);

        await flow.TaiLaiPhieuThieuAsync("shop1", CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.NhanLenhAsync(TimeSpan.FromMilliseconds(300)));
        // TẮT HẲN, không phải "gọi rồi nuốt lỗi": không một dòng nhật ký nào của bước này được sinh ra.
        Assert.False(rig.CoLog("Tải lại phiếu thiếu"));
    }

    // ===== 4. Vị trí trong flow shop: SAU khi _syncCallback đã lưu đơn =====

    [Fact]
    public async Task RunShopOrders_TaiLaiPhieu_ChayNGAYSAU_syncCallback()
    {
        // BẪY của plan: chạy TRƯỚC _syncCallback thì danh sách "thiếu phiếu" tính trên DB CŨ ⇒ sót đúng những đơn
        // vừa arrange trong lượt này. Test ghi lại THỨ TỰ hai callback.
        await using var rig = await BridgeTestRig.StartAsync();
        var dir = ThuMucTam();
        try
        {
            var thuTu = new List<string>();
            var flow = Runner(rig, dir,
                layDonThieuPhieu: (shop, _) =>
                {
                    thuTu.Add("layDanhSach:" + shop);
                    return Task.FromResult<IReadOnlyList<string>>(new[] { "SN-THIEU" });
                },
                syncCallback: (_, _, _, _) =>
                {
                    thuTu.Add("luuDon");
                    return Task.CompletedTask;
                });

            // toShip = 0 → bỏ Phần B; callback trả hàng null → bỏ bước cuối. Chỉ còn đọc đơn → lưu → tải lại phiếu.
            var chay = flow.RunShopOrdersAsync("shop-id-1", "shop1", toShip: 0, CancellationToken.None);
            using (var lenh = await rig.NhanLenhAsync())
            {
                Assert.Equal("syncOrders", lenh.RootElement.GetProperty("action").GetString());
            }
            await rig.GuiJsonAsync(JsonSerializer.Serialize(new
            {
                action = "pageData",
                kind = "orders",
                data = new[] { new { orderSn = "SN-THIEU", status = "Chờ lấy hàng", buyer = "nguoimua" } },
            }));

            Assert.Equal("SN-THIEU", await NhanLenhTaiLaiAsync(rig));
            await rig.GuiAsync(new { action = "slipRedownloaded", slipBase64 = Base64Pdf() });
            await chay;

            Assert.Equal(new[] { "luuDon", "layDanhSach:shop1" }, thuTu);
            Assert.True(File.Exists(Path.Combine(dir, "SN-THIEU.pdf")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* thư mục tạm */ }
        }
    }
}
