using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test OrdersViewModel chạy headless (ObservableObject + repository trên DB tạm, không dựng cửa sổ):
/// nạp/map nhãn tài khoản, dựng chuỗi hiển thị (sản phẩm "(+n)", tổng tiền ₫), lọc theo tài
/// khoản/trạng thái/tìm kiếm, và map dòng đang hiển thị sang CSV.
/// </summary>
public class OrdersViewModelTests
{
    private static long SeedAccount(AppServices services, string email)
        => services.Accounts.Insert(new Account { Email = email, Password = "p" });

    [Fact]
    public void Reload_HienThiTatCaDon_MapNhanTaiKhoan_VaDinhDangHienThi()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var accId = SeedAccount(services, "shopA@mail.com");
        services.Orders.UpsertMany(accId, new[]
        {
            new SyncedOrder
            {
                OrderSn = "SN1", Status = "Đã hủy", ItemSummary = "Giày", ItemCount = 3,
                TotalPrice = 166500, BuyerUsername = "buyer1",
                CancelReason = "Giao dịch bất thường", Carrier = "SPX", TrackingNumber = "SPX1",
            }
        }, DateTime.UtcNow);

        var vm = new OrdersViewModel(services);

        var row = Assert.Single(vm.Rows);
        Assert.Equal("shopA@mail.com", row.AccountLabel);
        Assert.Equal("Giày (+2)", row.Product);           // item_count 3 → +2 sản phẩm còn lại
        Assert.Equal("₫166.500", row.Total);              // định dạng nhóm nghìn bằng dấu chấm
        Assert.Equal("Giao dịch bất thường", row.Note);   // ưu tiên lý do hủy
        Assert.Equal("Đang hiển thị: 1/1 đơn", vm.TotalText); // trang hiện tại / tổng khớp lọc
    }

    [Fact]
    public void ItemCount1_KhongThemHauTo()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var accId = SeedAccount(services, "a@mail.com");
        services.Orders.UpsertMany(accId, new[]
        {
            new SyncedOrder { OrderSn = "SN1", ItemSummary = "Áo", ItemCount = 1 }
        }, DateTime.UtcNow);

        var vm = new OrdersViewModel(services);
        Assert.Equal("Áo", Assert.Single(vm.Rows).Product);
    }

    [Fact]
    public void LocTheoTaiKhoan_ChiHienDonCuaTaiKhoanDo()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var a1 = SeedAccount(services, "a1@mail.com");
        var a2 = SeedAccount(services, "a2@mail.com");
        services.Orders.UpsertMany(a1, new[] { new SyncedOrder { OrderSn = "A1" } }, DateTime.UtcNow);
        services.Orders.UpsertMany(a2, new[]
        {
            new SyncedOrder { OrderSn = "B1" }, new SyncedOrder { OrderSn = "B2" }
        }, DateTime.UtcNow);

        var vm = new OrdersViewModel(services);
        Assert.Equal(3, vm.Rows.Count);

        vm.SelectedAccount = vm.AccountOptions.First(o => o.Id == a2);
        Assert.Equal(2, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.Equal("a2@mail.com", r.AccountLabel));
    }

    [Fact]
    public void LocTheoTrangThai_VaTimKiemTrucTiep()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var a1 = SeedAccount(services, "a1@mail.com");
        services.Orders.UpsertMany(a1, new[]
        {
            new SyncedOrder { OrderSn = "SN1", Status = "Đã hủy", BuyerUsername = "alpha", ItemSummary = "Áo" },
            new SyncedOrder { OrderSn = "SN2", Status = "Chờ lấy hàng", BuyerUsername = "beta", ItemSummary = "Quần" },
        }, DateTime.UtcNow);

        var vm = new OrdersViewModel(services);
        Assert.Equal(2, vm.Rows.Count);

        // ComboBox trạng thái nạp đủ 2 trạng thái + mục "tất cả".
        Assert.Contains("Đã hủy", vm.StatusOptions);
        Assert.Contains("Chờ lấy hàng", vm.StatusOptions);
        Assert.Equal(OrdersViewModel.AllStatusesLabel, vm.StatusOptions[0]);

        vm.SelectedStatus = "Đã hủy";
        Assert.Equal("SN1", Assert.Single(vm.Rows).OrderSn);

        vm.SelectedStatus = OrdersViewModel.AllStatusesLabel; // bỏ lọc trạng thái
        Assert.Equal(2, vm.Rows.Count);

        vm.SearchText = "beta"; // tìm theo người mua, áp dụng ngay
        Assert.Equal("SN2", Assert.Single(vm.Rows).OrderSn);
    }

    [Fact]
    public void Reload_GiuLuaChonTaiKhoan_KhiConTonTai()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var a1 = SeedAccount(services, "a1@mail.com");
        var a2 = SeedAccount(services, "a2@mail.com");
        services.Orders.UpsertMany(a2, new[] { new SyncedOrder { OrderSn = "B1" } }, DateTime.UtcNow);

        var vm = new OrdersViewModel(services);
        vm.SelectedAccount = vm.AccountOptions.First(o => o.Id == a2);

        vm.RefreshCommand.Execute(null); // như bấm "Làm mới"

        Assert.Equal(a2, vm.SelectedAccount!.Id); // vẫn giữ tài khoản đã chọn
        Assert.Equal("B1", Assert.Single(vm.Rows).OrderSn);
    }

    [Fact]
    public void ExportRows_DungDeDungCsvCoBom()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var a1 = SeedAccount(services, "a1@mail.com");
        services.Orders.UpsertMany(a1, new[]
        {
            new SyncedOrder { OrderSn = "SN1", ItemSummary = "Giày", ItemCount = 1, TotalPrice = 100000 }
        }, DateTime.UtcNow);

        var vm = new OrdersViewModel(services);
        var bytes = OrderCsvExporter.BuildCsvWithBom(vm.Rows.Select(r => r.ToExportRow()));

        Assert.Equal(0xEF, bytes[0]);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Contains("SN1", text);
        Assert.Contains("a1@mail.com", text);
        Assert.Contains("₫100.000", text);
    }

    [Fact]
    public void ComboBoxTrangThai_TheoTaiKhoanDangLoc()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var a1 = SeedAccount(services, "a1@mail.com");
        var a2 = SeedAccount(services, "a2@mail.com");
        services.Orders.UpsertMany(a1, new[] { new SyncedOrder { OrderSn = "A1", Status = "Đã hủy" } }, DateTime.UtcNow);
        services.Orders.UpsertMany(a2, new[] { new SyncedOrder { OrderSn = "B1", Status = "Chờ lấy hàng" } }, DateTime.UtcNow);

        var vm = new OrdersViewModel(services);
        // "Tất cả tài khoản" → thấy cả 2 trạng thái (sắp xếp: 'C' trước 'Đ').
        Assert.Equal(new[] { OrdersViewModel.AllStatusesLabel, "Chờ lấy hàng", "Đã hủy" }, vm.StatusOptions);

        vm.SelectedAccount = vm.AccountOptions.First(o => o.Id == a1);
        Assert.Equal(new[] { OrdersViewModel.AllStatusesLabel, "Đã hủy" }, vm.StatusOptions);

        vm.SelectedAccount = vm.AccountOptions.First(o => o.Id == a2);
        Assert.Equal(new[] { OrdersViewModel.AllStatusesLabel, "Chờ lấy hàng" }, vm.StatusOptions);
    }

    [Fact]
    public void DoiTaiKhoan_TrangThaiCuKhongCon_VeTatCa_VaHienDungBang()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var a1 = SeedAccount(services, "a1@mail.com");
        var a2 = SeedAccount(services, "a2@mail.com");
        services.Orders.UpsertMany(a1, new[] { new SyncedOrder { OrderSn = "A1", Status = "Đã hủy" } }, DateTime.UtcNow);
        services.Orders.UpsertMany(a2, new[] { new SyncedOrder { OrderSn = "B1", Status = "Chờ lấy hàng" } }, DateTime.UtcNow);

        var vm = new OrdersViewModel(services);
        vm.SelectedAccount = vm.AccountOptions.First(o => o.Id == a1);
        vm.SelectedStatus = "Đã hủy";
        Assert.Equal("A1", Assert.Single(vm.Rows).OrderSn);

        // Đổi sang a2 (không có trạng thái "Đã hủy") → về "Tất cả", bảng hiện đơn của a2 (không rỗng).
        vm.SelectedAccount = vm.AccountOptions.First(o => o.Id == a2);
        Assert.Equal(OrdersViewModel.AllStatusesLabel, vm.SelectedStatus);
        Assert.Equal("B1", Assert.Single(vm.Rows).OrderSn);
    }

    // ===== Link "In phiếu": SlipPath suy từ order_sn phải KHỚP nơi tải phiếu (CÙNG thư mục truyền vào + SanitizeFileName) =====
    [Theory]
    [InlineData("260715ABC", "260715ABC.pdf")]      // mã đơn sạch → giữ nguyên
    [InlineData("SN/1:2*3", "SN_1_2_3.pdf")]        // ký tự lạ (/ : *) → '_' đúng như SanitizeFileName lúc lưu
    [InlineData("  A B  ", "A_B.pdf")]              // khoảng trắng đầu/cuối cắt, giữa → '_'
    public void SlipPath_SuyTuOrderSn_KhopThuMucVaSanitize(string orderSn, string expectedFileName)
    {
        // Thư mục hóa đơn giờ TRUYỀN VÀO (OrdersViewModel đọc từ Cài đặt) — SlipPath dùng đúng thư mục đó + Sanitize.
        var invoiceDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xlds-slip-test");
        var row = new OrderRowViewModel(new OrderRow { OrderSn = orderSn }, "lbl", invoiceDir);
        var expected = System.IO.Path.Combine(invoiceDir, expectedFileName);
        Assert.Equal(expected, row.SlipPath);
    }

    // ===== Nhất quán 3 nơi: thư mục hóa đơn ở Cài đặt → SlipPath của dòng phải trỏ đúng thư mục đó =====
    [Fact]
    public void SlipPath_DungThuMucHoaDonTuCaiDat()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var accId = SeedAccount(services, "a@mail.com");
        services.Orders.UpsertMany(accId, new[]
        {
            new SyncedOrder { OrderSn = "SN-INV-1" }
        }, DateTime.UtcNow);

        // Đặt thư mục hóa đơn tùy chỉnh ở Cài đặt → SlipPath của dòng phải trỏ vào đúng thư mục đó (cùng nguồn xử lý đơn).
        var custom = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xlds-custom-invoices");
        services.Settings.SetInvoiceFolder(custom);

        var vm = new OrdersViewModel(services);
        var row = Assert.Single(vm.Rows);
        Assert.Equal(System.IO.Path.Combine(custom, "SN-INV-1.pdf"), row.SlipPath);
    }

    [Fact]
    public void PrintSlip_ThieuFile_BaoQuaNotify_KhongNem_KhongIn()
    {
        // order_sn duy nhất → file phiếu chắc chắn KHÔNG tồn tại → thoát trước PdfPrinter.TryPrint (không in thật trong test).
        var orderSn = "__no_slip_" + Guid.NewGuid().ToString("N");
        var invoiceDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xlds-slip-test");
        string? msg = null;
        var row = new OrderRowViewModel(new OrderRow { OrderSn = orderSn }, "lbl", invoiceDir, m => msg = m);

        row.PrintSlipCommand.Execute(null); // reference compile-time: chứng minh [RelayCommand] sinh PrintSlipCommand

        Assert.NotNull(msg);
        Assert.Contains("Chưa có file phiếu", msg!);
        Assert.Contains(orderSn, msg!);
    }

    // ===== C: CanPrintSlip — ẩn nút "In phiếu" khi trạng thái CHỨA "hủy" (chuẩn hóa hoa/thường + khoảng trắng) =====
    [Theory]
    [InlineData("Đã hủy", false)]
    [InlineData("Đã hủy một phần", false)]
    [InlineData("ĐÃ HỦY", false)]                 // hoa/thường không ảnh hưởng
    [InlineData("Chờ lấy hàng", true)]
    [InlineData("Hoàn thành", true)]
    [InlineData("Đang giao", true)]
    [InlineData("", true)]                         // rỗng → không phải hủy → vẫn hiện (không ẩn nhầm)
    public void CanPrintSlip_AnKhiTrangThaiChuaHuy(string status, bool expected)
    {
        var row = new OrderRowViewModel(new OrderRow { OrderSn = "SN", Status = status }, "lbl", "dir");
        Assert.Equal(expected, row.CanPrintSlip);
    }

    // ===== B: IsPendingPickup — nhận diện đơn "Chờ lấy hàng" để in hàng loạt (chuẩn hóa CHỨA) =====
    [Theory]
    [InlineData("Chờ lấy hàng", true)]
    [InlineData("chờ lấy hàng", true)]            // hoa/thường
    [InlineData("  Chờ   lấy   hàng ", true)]     // khoảng trắng thừa → chuẩn hóa vẫn khớp
    [InlineData("Chờ xác nhận", false)]           // "chờ" nhưng KHÔNG phải "chờ lấy hàng"
    [InlineData("Đã hủy", false)]
    [InlineData("Hoàn thành", false)]
    [InlineData("", false)]
    public void IsPendingPickup_ChiKhopChoLayHang(string status, bool expected)
    {
        var row = new OrderRowViewModel(new OrderRow { OrderSn = "SN", Status = status }, "lbl", "dir");
        Assert.Equal(expected, row.IsPendingPickup);
    }

    // ===== B: "In nhiều đơn" — CHỈ tính đơn "Chờ lấy hàng" đang hiển thị; thiếu file phiếu → đếm "thiếu file", KHÔNG in =====
    [Fact]
    public async Task PrintPendingSlips_ChiDonChoLayHang_ThieuFile_BaoThieu_KhongNem()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var accId = SeedAccount(services, "a@mail.com");
        services.Orders.UpsertMany(accId, new[]
        {
            new SyncedOrder { OrderSn = "P1", Status = "Chờ lấy hàng" },
            new SyncedOrder { OrderSn = "P2", Status = "Chờ lấy hàng" },
            new SyncedOrder { OrderSn = "C1", Status = "Đã hủy" },       // KHÔNG phải Chờ lấy hàng → bỏ qua
            new SyncedOrder { OrderSn = "D1", Status = "Hoàn thành" },   // KHÔNG phải Chờ lấy hàng → bỏ qua
        }, DateTime.UtcNow);

        // Thư mục hóa đơn RIÊNG + không tồn tại (unique) → file phiếu chắc chắn KHÔNG có → KHÔNG in gì (no Process.Start).
        var emptyDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xlds-print-" + Guid.NewGuid().ToString("N"));
        services.Settings.SetInvoiceFolder(emptyDir);

        var vm = new OrdersViewModel(services);
        Assert.Equal(4, vm.Rows.Count);

        await vm.PrintPendingSlipsCommand.ExecuteAsync(null);

        // 2 đơn Chờ lấy hàng, cả 2 thiếu file → in 0, thiếu 2; C1/D1 KHÔNG được tính.
        Assert.Equal("Đã gửi in 0 phiếu Chờ lấy hàng (thiếu file: 2).", vm.StatusMessage);
    }

    // ===== B: "In nhiều đơn" khi danh sách KHÔNG có đơn Chờ lấy hàng → báo rõ, KHÔNG in =====
    [Fact]
    public async Task PrintPendingSlips_KhongCoDonChoLayHang_BaoKhongCo()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var accId = SeedAccount(services, "a@mail.com");
        services.Orders.UpsertMany(accId, new[]
        {
            new SyncedOrder { OrderSn = "C1", Status = "Đã hủy" },
            new SyncedOrder { OrderSn = "D1", Status = "Hoàn thành" },
        }, DateTime.UtcNow);

        var vm = new OrdersViewModel(services);
        await vm.PrintPendingSlipsCommand.ExecuteAsync(null);

        Assert.Equal("Không có đơn Chờ lấy hàng nào trong danh sách để in.", vm.StatusMessage);
    }

    // ===== Lọc shop THEO CHỮ ĐANG GÕ: nguồn sự thật là AccountFilterText (gõ dở lọc hợp CHỨA, trống = tất cả) =====

    [Fact]
    public void GoDo_KhopNhieuShop_LocHopChuaTrongBoNho()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var mex1 = SeedAccount(services, "mexico1.store");
        var mex2 = SeedAccount(services, "mexstyle@mail.com");
        var other = SeedAccount(services, "alpha@mail.com");
        services.Orders.UpsertMany(mex1, new[] { new SyncedOrder { OrderSn = "M1" } }, DateTime.UtcNow);
        services.Orders.UpsertMany(mex2, new[] { new SyncedOrder { OrderSn = "M2" } }, DateTime.UtcNow);
        services.Orders.UpsertMany(other, new[] { new SyncedOrder { OrderSn = "O1" } }, DateTime.UtcNow);

        var vm = new OrdersViewModel(services);
        Assert.Equal(3, vm.Rows.Count);

        vm.AccountFilterText = "mex"; // gõ dở → 2 shop có Email chứa "mex"
        Assert.Equal(2, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.Contains("mex", r.AccountLabel));
        Assert.Equal("Đang hiển thị: 2/2 đơn", vm.TotalText);
    }

    [Fact]
    public void GoDo_KhongKhopShopNao_BangTrong()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var a1 = SeedAccount(services, "mexico1.store");
        var a2 = SeedAccount(services, "alpha@mail.com");
        services.Orders.UpsertMany(a1, new[] { new SyncedOrder { OrderSn = "M1" } }, DateTime.UtcNow);
        services.Orders.UpsertMany(a2, new[] { new SyncedOrder { OrderSn = "O1" } }, DateTime.UtcNow);

        var vm = new OrdersViewModel(services);

        vm.AccountFilterText = "zzz"; // không shop nào chứa → 0 dòng (phản hồi trung thực)
        Assert.Empty(vm.Rows);
        Assert.Equal("Đang hiển thị: 0/0 đơn", vm.TotalText);
    }

    [Fact]
    public void XoaTrong_QuayVeTatCa_SelectedAccountSentinel()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var mex1 = SeedAccount(services, "mexico1.store");
        var mex2 = SeedAccount(services, "mexstyle@mail.com");
        var other = SeedAccount(services, "alpha@mail.com");
        services.Orders.UpsertMany(mex1, new[] { new SyncedOrder { OrderSn = "M1" } }, DateTime.UtcNow);
        services.Orders.UpsertMany(mex2, new[] { new SyncedOrder { OrderSn = "M2" } }, DateTime.UtcNow);
        services.Orders.UpsertMany(other, new[] { new SyncedOrder { OrderSn = "O1" } }, DateTime.UtcNow);

        var vm = new OrdersViewModel(services);
        vm.AccountFilterText = "mex";
        Assert.Equal(2, vm.Rows.Count);

        vm.AccountFilterText = ""; // xóa trắng → về tất cả
        Assert.Equal(3, vm.Rows.Count);
        Assert.Null(vm.SelectedAccount!.Id); // sentinel "Tất cả tài khoản"
    }

    [Fact]
    public void KhopDungNhan_KhacHoaThuong_LocMotShop_StatusesTheoShop()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var mex1 = SeedAccount(services, "mexico1.store");
        var other = SeedAccount(services, "alpha@mail.com");
        services.Orders.UpsertMany(mex1, new[] { new SyncedOrder { OrderSn = "M1", Status = "Chờ lấy hàng" } }, DateTime.UtcNow);
        services.Orders.UpsertMany(other, new[] { new SyncedOrder { OrderSn = "O1", Status = "Đã hủy" } }, DateTime.UtcNow);

        var vm = new OrdersViewModel(services);
        vm.AccountFilterText = "MEXICO1.STORE"; // khớp ĐÚNG nhãn (khác hoa/thường) → như chọn gợi ý

        Assert.Equal("M1", Assert.Single(vm.Rows).OrderSn);
        Assert.Equal(mex1, vm.SelectedAccount!.Id);
        // StatusOptions nạp THEO shop đó (chỉ trạng thái của shop mex, không có "Đã hủy" của shop khác).
        Assert.Equal(new[] { OrdersViewModel.AllStatusesLabel, "Chờ lấy hàng" }, vm.StatusOptions);
    }

    [Fact]
    public void ClearAccountFilterCommand_VeTatCa()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var mex1 = SeedAccount(services, "mexico1.store");
        var mex2 = SeedAccount(services, "mexstyle@mail.com");
        var other = SeedAccount(services, "alpha@mail.com");
        services.Orders.UpsertMany(mex1, new[] { new SyncedOrder { OrderSn = "M1" } }, DateTime.UtcNow);
        services.Orders.UpsertMany(mex2, new[] { new SyncedOrder { OrderSn = "M2" } }, DateTime.UtcNow);
        services.Orders.UpsertMany(other, new[] { new SyncedOrder { OrderSn = "O1" } }, DateTime.UtcNow);

        var vm = new OrdersViewModel(services);
        vm.AccountFilterText = "mex";
        Assert.Equal(2, vm.Rows.Count);

        vm.ClearAccountFilterCommand.Execute(null);
        Assert.Equal(string.Empty, vm.AccountFilterText);
        Assert.Equal(3, vm.Rows.Count);
        Assert.Null(vm.SelectedAccount!.Id);
    }

    [Fact]
    public void Refresh_GiuBoLocDangGoDo_DonMoiHienThi_ShopKhacVanBiLoc()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var mex1 = SeedAccount(services, "mexico1.store");
        var other = SeedAccount(services, "alpha@mail.com");
        services.Orders.UpsertMany(mex1, new[] { new SyncedOrder { OrderSn = "M1" } }, DateTime.UtcNow);
        services.Orders.UpsertMany(other, new[] { new SyncedOrder { OrderSn = "O1" } }, DateTime.UtcNow);

        var vm = new OrdersViewModel(services);
        vm.AccountFilterText = "mex";
        Assert.Equal("M1", Assert.Single(vm.Rows).OrderSn);

        // Giả lập sync thêm đơn mới cho shop mex rồi bấm "Làm mới".
        services.Orders.UpsertMany(mex1, new[] { new SyncedOrder { OrderSn = "M2" } }, DateTime.UtcNow);
        vm.RefreshCommand.Execute(null);

        Assert.Equal("mex", vm.AccountFilterText);        // text đang gõ dở KHÔNG bị auto-refresh phá
        Assert.Equal(2, vm.Rows.Count);                    // M1 + M2 (đơn mới xuất hiện)
        Assert.DoesNotContain(vm.Rows, r => r.OrderSn == "O1"); // shop khác vẫn bị lọc
    }

    // ===================== Phân trang phía DB (LIMIT/OFFSET + COUNT) =====================

    /// <summary>Seed <paramref name="count"/> đơn cho 1 tài khoản, mã "SN1".."SNn".</summary>
    private static void SeedOrders(AppServices services, long accId, int count) =>
        services.Orders.UpsertMany(accId,
            Enumerable.Range(1, count).Select(i => new SyncedOrder { OrderSn = "SN" + i }).ToArray(),
            DateTime.UtcNow);

    [Fact]
    public void PhanTrang_TrangDauChiChuaMotTrang_TotalCountVaPageInfoDung()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var accId = SeedAccount(services, "a@mail.com");
        SeedOrders(services, accId, 5);

        var vm = new OrdersViewModel(services);
        vm.PageSize = 2; // 5 đơn / 2 = 3 trang

        Assert.Equal(2, vm.Rows.Count);        // trang 1 chỉ 1 trang dữ liệu = PageSize dòng
        Assert.Equal(5, vm.TotalCount);        // tổng khớp lọc trên MỌI trang
        Assert.Equal(3, vm.TotalPages);
        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal("Trang 1/3", vm.PageInfoText);
        Assert.Equal("Đang hiển thị: 2/5 đơn", vm.TotalText);
    }

    [Fact]
    public void PhanTrang_NextPrev_DoiTrang_ChanOBien()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var accId = SeedAccount(services, "a@mail.com");
        SeedOrders(services, accId, 5);

        var vm = new OrdersViewModel(services);
        vm.PageSize = 2; // 3 trang

        // Trang 1: Prev chặn, Next bật.
        Assert.False(vm.PrevPageCommand.CanExecute(null));
        Assert.True(vm.NextPageCommand.CanExecute(null));

        vm.NextPageCommand.Execute(null); // → trang 2
        Assert.Equal(2, vm.CurrentPage);
        Assert.Equal(2, vm.Rows.Count);
        Assert.True(vm.PrevPageCommand.CanExecute(null));

        vm.NextPageCommand.Execute(null); // → trang 3 (cuối, lẻ 1 đơn)
        Assert.Equal(3, vm.CurrentPage);
        Assert.Single(vm.Rows);
        Assert.False(vm.NextPageCommand.CanExecute(null)); // chặn ở trang cuối
        Assert.Equal("Trang 3/3", vm.PageInfoText);

        vm.PrevPageCommand.Execute(null); // ← trang 2
        Assert.Equal(2, vm.CurrentPage);
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public void DoiBoLoc_HoacCoTrang_VeTrang1()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var accId = SeedAccount(services, "a@mail.com");
        services.Orders.UpsertMany(accId,
            Enumerable.Range(1, 6).Select(i => new SyncedOrder
            {
                OrderSn = "SN" + i,
                Status = i % 2 == 0 ? "Đã hủy" : "Chờ lấy hàng",
                BuyerUsername = "buyer" + i,
            }).ToArray(),
            DateTime.UtcNow);

        var vm = new OrdersViewModel(services);
        vm.PageSize = 2; // 6 đơn → 3 trang

        vm.NextPageCommand.Execute(null);          // sang trang 2
        Assert.Equal(2, vm.CurrentPage);
        vm.SearchText = "buyer";                    // đổi từ khóa → về trang 1
        Assert.Equal(1, vm.CurrentPage);

        vm.NextPageCommand.Execute(null);          // sang trang 2 lần nữa
        Assert.Equal(2, vm.CurrentPage);
        vm.SelectedStatus = "Đã hủy";               // đổi trạng thái → về trang 1
        Assert.Equal(1, vm.CurrentPage);

        // Trước khi đổi cỡ trang: sang trang 2 (status "Đã hủy" có 3 đơn / 2 = 2 trang).
        vm.NextPageCommand.Execute(null);
        Assert.Equal(2, vm.CurrentPage);
        vm.PageSize = 100;                          // đổi cỡ trang → về trang 1
        Assert.Equal(1, vm.CurrentPage);
        Assert.Equal(1, vm.TotalPages);             // 3 đơn / 100 = 1 trang
    }

    [Fact]
    public void PhanTrang_ClampTrangCuoi_KhiTongGiam()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var accId = SeedAccount(services, "a@mail.com");
        SeedOrders(services, accId, 6); // 6 đơn

        var vm = new OrdersViewModel(services);
        vm.PageSize = 2;                 // 3 trang
        vm.NextPageCommand.Execute(null);
        vm.NextPageCommand.Execute(null); // đang ở trang 3 (cuối)
        Assert.Equal(3, vm.CurrentPage);

        // Giả lập tổng đơn giảm còn 2 (< số trang cũ) rồi Reload (như auto-refresh sau sync):
        // KHÔNG kéo về 1, chỉ clamp về trang cuối mới.
        using (var conn = services.Database.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM orders WHERE order_sn IN ('SN3','SN4','SN5','SN6');";
            cmd.ExecuteNonQuery();
        }
        vm.RefreshCommand.Execute(null);

        Assert.Equal(2, vm.TotalCount);
        Assert.Equal(1, vm.TotalPages);
        Assert.Equal(1, vm.CurrentPage); // clamp về trang cuối (=1 khi chỉ còn 1 trang)
    }

    [Fact]
    public void GoDo_PhanTrang_TongDungTrenMoiTrang_KhongLanShopKhac()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var mex1 = SeedAccount(services, "mexico1.store");
        var mex2 = SeedAccount(services, "mexstyle@mail.com");
        var other = SeedAccount(services, "alpha@mail.com");
        services.Orders.UpsertMany(mex1, Enumerable.Range(1, 3).Select(i => new SyncedOrder { OrderSn = "M1_" + i }).ToArray(), DateTime.UtcNow);
        services.Orders.UpsertMany(mex2, Enumerable.Range(1, 2).Select(i => new SyncedOrder { OrderSn = "M2_" + i }).ToArray(), DateTime.UtcNow);
        services.Orders.UpsertMany(other, Enumerable.Range(1, 4).Select(i => new SyncedOrder { OrderSn = "O_" + i }).ToArray(), DateTime.UtcNow);

        var vm = new OrdersViewModel(services);
        vm.PageSize = 2;
        vm.AccountFilterText = "mex"; // gõ dở → HỢP mex1 + mex2 = 5 đơn (lọc account_id IN (...) phía SQL)

        Assert.Equal(5, vm.TotalCount);   // tổng khớp lọc — KHÔNG phải 9 (không lẫn shop "other")
        Assert.Equal(3, vm.TotalPages);   // 5 / 2 = 3 trang
        Assert.Equal(2, vm.Rows.Count);   // trang 1 đầy 2 dòng

        // Duyệt hết 3 trang: gom đủ 5 đơn, KHÔNG đơn nào của shop "other".
        var seen = new List<string>(vm.Rows.Select(r => r.OrderSn));
        vm.NextPageCommand.Execute(null);
        seen.AddRange(vm.Rows.Select(r => r.OrderSn));
        vm.NextPageCommand.Execute(null);
        seen.AddRange(vm.Rows.Select(r => r.OrderSn));

        Assert.Equal(5, seen.Count);
        Assert.All(seen, sn => Assert.StartsWith("M", sn)); // toàn đơn của mex1/mex2
    }

    [Fact]
    public void XuatCsv_LayMoiTrang_KhongChiTrangHienTai()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var accId = SeedAccount(services, "a@mail.com");
        SeedOrders(services, accId, 5);

        var vm = new OrdersViewModel(services);
        vm.PageSize = 2;                 // Rows chỉ 2 dòng, nhưng CSV phải gồm cả 5 đơn
        Assert.Equal(2, vm.Rows.Count);

        // Dựng CSV theo ĐÚNG cách ExportCsvAsync: truy vấn KHÔNG phân trang cùng bộ lọc.
        var all = services.Orders.Query();
        Assert.Equal(5, all.Count);      // truy vấn không phân trang trả toàn bộ
    }
}
