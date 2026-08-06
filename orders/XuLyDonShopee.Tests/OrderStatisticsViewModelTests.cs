using System.Diagnostics;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.App.Views;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Hồi quy cho bộ lọc ngày của thống kê đơn: VM phải dùng DateTime? khớp CalendarDatePicker
/// và xử lý rõ ràng khi người dùng xóa một mốc ngày.
/// </summary>
public class OrderStatisticsViewModelTests
{
    /// <summary>Vế CẢNH BÁO NGẮN mà VM ghép vào dòng nguồn số LOCAL khi màn ĐANG có thẻ số (kho máy đã dọn đơn kết
    /// thúc nên 3 thẻ ĐÃ GIAO / ĐÃ HỦY / DOANH THU bị hụt). Chép ĐÚNG chuỗi của VM: bỏ vế này đi là test đổ.
    /// Bản dài nằm ở ToolTip (<c>SourceToolTip</c>) — xem <see cref="CoDon_CanhBaoDaiNamOToolTip_DongLuonHienGiuNgan"/>.</summary>
    private const string CanhBaoHut = " ĐÃ GIAO / ĐÃ HỦY / DOANH THU bị HỤT — rê chuột để xem vì sao.";

    private const string NenDangHoi = "Số trên MÁY NÀY — đang hỏi Hub số chung…";
    private const string NenHubChet = "Số trên MÁY NÀY — Hub không phản hồi nên chưa gộp được số chung.";
    private const string NenDocLap = "Số trên MÁY NÀY (app chạy độc lập, chưa nối Hub).";

    private const string NguonDangHoi = NenDangHoi + CanhBaoHut;
    private const string NguonHubChet = NenHubChet + CanhBaoHut;
    private const string NguonDocLap = NenDocLap + CanhBaoHut;

    [Fact]
    public void Constructor_DefaultsToCurrentMonthThroughToday()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var before = DateTime.Today;

        var vm = new OrderStatisticsViewModel(services);

        var after = DateTime.Today;
        Assert.NotNull(vm.FromDate);
        Assert.NotNull(vm.ToDate);
        Assert.Equal(1, vm.FromDate!.Value.Day);
        Assert.Equal(vm.ToDate.Value.Year, vm.FromDate.Value.Year);
        Assert.Equal(vm.ToDate.Value.Month, vm.FromDate.Value.Month);
        Assert.InRange(vm.ToDate.Value.Date, before, after);
    }

    [Fact]
    public void ClearingEitherDate_DoesNotThrow_AndShowsValidationMessage()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var vm = new OrderStatisticsViewModel(services);

        var exception = Record.Exception(() => vm.FromDate = null);

        Assert.Null(exception);
        Assert.Null(vm.FromDate);
        Assert.False(vm.HasData);
        Assert.Equal("Hãy chọn đầy đủ Từ ngày và Đến ngày để xem thống kê.", vm.EmptyMessage);
        Assert.Equal(vm.EmptyMessage, vm.ScopeText);
        // Chưa lọc được gì thì KHÔNG có nguồn số nào để nói: để nguyên dòng cũ là header vừa bảo "hãy chọn ngày"
        // vừa khẳng định "Số chung toàn hệ thống (từ Hub)". Rỗng ⇒ XAML ẩn hẳn dòng (StringToVis).
        Assert.Equal(string.Empty, vm.SourceText);
        Assert.Null(vm.SourceToolTip);
    }

    // ===== Nguồn số đang xem phải nói RÕ (chống "hỏng im lặng": hub chết mà vẫn hiện số máy như số chung) =====

    [Fact]
    public void ChuaNoiHub_HienSoMayNay_VaNoiRoNguon()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1" } }, DateTime.UtcNow);

        var vm = new OrderStatisticsViewModel(services); // KHÔNG rót hook hub → app chạy độc lập

        Assert.Equal(NguonDocLap, vm.SourceText);
        Assert.Equal("1", vm.TotalOrdersText);
        Assert.True(vm.DangXemSoMay); // XAML dán ghi chú "chỉ đơn CÒN trên máy" lên 3 thẻ theo cờ này
    }

    /// <summary>Máy CHƯA cấu hình Hub: shell suite rót hook thống kê VÔ ĐIỀU KIỆN nên hook vẫn có mặt (và tự trả
    /// null), nhưng màn KHÔNG được tố "Hub không phản hồi" — chẳng có Hub nào để mà không phản hồi.</summary>
    [Fact]
    public void ChuaCauHinhHub_NoiLaChayDocLap_KhongToHubChet()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1" } }, DateTime.UtcNow);
        services.QueryOrderStatistics = (_, _, _, _) => Task.FromResult<SharedOrderStatistics?>(null);
        services.HubDaCauHinh = () => false; // CoordinationRuntime.Client is null

        var vm = new OrderStatisticsViewModel(services);

        Assert.Equal(NguonDocLap, vm.SourceText);
        Assert.Equal("1", vm.TotalOrdersText);
    }

    [Fact]
    public void HubKhongPhanHoi_GiuSoLocal_VaNoiRoNguon()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1" } }, DateTime.UtcNow);
        services.QueryOrderStatistics = (_, _, _, _) => Task.FromResult<SharedOrderStatistics?>(null); // hub lỗi/hub cũ

        var vm = new OrderStatisticsViewModel(services);

        Assert.Equal(NguonHubChet, vm.SourceText);
        Assert.Equal("1", vm.TotalOrdersText); // vẫn có số để nhìn, không rỗng
    }

    /// <summary>Lượt hỏi Hub CÒN ĐANG BAY thì chưa được kết luận "Hub không phản hồi" — chưa hỏi xong thì chưa
    /// biết hub sống hay chết (lỗi cũ: mỗi lần đổi ngày là hiện một dòng cáo buộc hub chết).</summary>
    [Fact]
    public void DangHoiHub_ChuaKetLuanHubChet()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1" } }, DateTime.UtcNow);
        var treo = new TaskCompletionSource<SharedOrderStatistics?>();
        services.QueryOrderStatistics = (_, _, _, _) => treo.Task; // lượt hỏi chưa về

        var vm = new OrderStatisticsViewModel(services);

        Assert.Equal(NguonDangHoi, vm.SourceText);
        Assert.Equal("1", vm.TotalOrdersText); // vẫn vẽ số local ngay, không chặn UI
    }

    /// <summary>Đang hiện số CHUNG mà kho đơn đổi (OrdersChanged sau mỗi lượt sync) → GIỮ số chung, KHÔNG vẽ đè số
    /// local rồi lại nhảy về số chung khi hub trả lời (triệu chứng "số nhảy" người dùng thấy mỗi lượt đồng bộ).</summary>
    [Fact]
    public void DangHienSoChung_VeLaiCungKhoangNgay_KhongVeDeSoLocal()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1" } }, DateTime.UtcNow);
        services.QueryOrderStatistics = (_, _, _, _) => Task.FromResult<SharedOrderStatistics?>(SoChung(7));

        var vm = new OrderStatisticsViewModel(services);
        Assert.Equal("Số chung toàn hệ thống (từ Hub).", vm.SourceText);
        Assert.Equal("7", vm.TotalOrdersText);
        Assert.False(vm.DangXemSoMay); // số chung đủ lịch sử → gỡ ghi chú "chỉ đơn CÒN trên máy"

        // Lượt vẽ lại CÙNG shop + CÙNG khoảng ngày, hub chưa kịp trả lời → lưới giữ nguyên số chung.
        // Bắn ĐÚNG đường thật (phiên sync ghi xong kho đơn), không gọi tay vm.Reload() — nút "Làm mới" nay là
        // đường KHÁC (ép vẽ số local), gọi nó ở đây sẽ đo nhầm cơ chế.
        var treo = new TaskCompletionSource<SharedOrderStatistics?>();
        services.QueryOrderStatistics = (_, _, _, _) => treo.Task;
        vm.DangHienTrenMan = true; // màn đang mở, không thì OrdersChanged bị gate bỏ qua và test đo hụt
        services.RaiseOrdersChanged();

        Assert.Equal("7", vm.TotalOrdersText);                       // KHÔNG tụt về số local ("1")
        Assert.Equal("Số chung toàn hệ thống (từ Hub).", vm.SourceText);
    }

    /// <summary>Nút "Làm mới" hứa "Đọc lại số liệu từ kho đơn trên máy này" → phải đọc THẬT, kể cả khi đang hiện
    /// số chung của Hub. Không có đường ép này thì nút vô nghĩa và Hub báo 0 đơn là màn kẹt rỗng dù máy có đơn.</summary>
    [Fact]
    public void BamLamMoi_VeLaiSoLocalNgay_DuDangHienSoHub()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1" } }, DateTime.UtcNow);
        services.QueryOrderStatistics = (_, _, _, _) => Task.FromResult<SharedOrderStatistics?>(SoChung(7));

        var vm = new OrderStatisticsViewModel(services);
        Assert.Equal("7", vm.TotalOrdersText); // đang hiện số chung

        var treo = new TaskCompletionSource<SharedOrderStatistics?>();
        services.QueryOrderStatistics = (_, _, _, _) => treo.Task; // lượt hỏi mới chưa về
        vm.LamMoiCommand.Execute(null);

        Assert.Equal("1", vm.TotalOrdersText); // số của kho đơn TRÊN MÁY, ngay lập tức
        Assert.True(vm.DangXemSoMay);
        Assert.Equal(NguonDangHoi, vm.SourceText);
    }

    /// <summary>Nhưng ĐỔI khoảng ngày thì phải vẽ lại số local ngay: số chung của khoảng CŨ không còn đúng.</summary>
    [Fact]
    public void DoiKhoangNgay_VeLaiSoLocal_KhongGiuSoChungCu()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1" } }, DateTime.UtcNow);
        services.QueryOrderStatistics = (_, _, _, _) => Task.FromResult<SharedOrderStatistics?>(SoChung(7));

        var vm = new OrderStatisticsViewModel(services);
        Assert.Equal("7", vm.TotalOrdersText);

        var treo = new TaskCompletionSource<SharedOrderStatistics?>();
        services.QueryOrderStatistics = (_, _, _, _) => treo.Task;
        vm.FromDate = DateTime.Today.AddDays(-40);   // khoảng KHÁC → số chung cũ không còn đúng

        Assert.Equal("1", vm.TotalOrdersText);
        Assert.Equal(NguonDangHoi, vm.SourceText);
    }

    /// <summary>Đang giữ số chung mà lượt hỏi mới trả null → KHÔNG lẳng lặng để nguyên dòng "Số chung (Hub)" như
    /// thể vừa cập nhật: nói rõ đây là số của lượt hỏi TRƯỚC.</summary>
    [Fact]
    public void DangHienSoChung_LuotHoiMoiThatBai_NoiRoLaSoCu()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1" } }, DateTime.UtcNow);
        services.QueryOrderStatistics = (_, _, _, _) => Task.FromResult<SharedOrderStatistics?>(SoChung(7));

        var vm = new OrderStatisticsViewModel(services);
        services.QueryOrderStatistics = (_, _, _, _) => Task.FromResult<SharedOrderStatistics?>(null);
        vm.DangHienTrenMan = true; // màn đang mở, không thì OrdersChanged bị gate bỏ qua và test đo hụt
        services.RaiseOrdersChanged(); // đường THẬT: phiên sync vừa ghi kho đơn (không phải nút "Làm mới")

        Assert.Equal("7", vm.TotalOrdersText); // số chung cũ vẫn có để nhìn
        Assert.Equal("Số chung (Hub) của lượt hỏi trước — lượt này Hub không phản hồi.", vm.SourceText);
    }

    /// <summary>Hub bảo khoảng này KHÔNG có đơn nào trong khi kho máy vừa đọc ra đơn → hai bên nói ngược nhau.
    /// Vẽ đè màn rỗng của Hub lên số máy đang có là hỏng im lặng (bấm "Làm mới" thấy số nháy một cái rồi trắng
    /// màn) → phải GIỮ số máy và nói thẳng lý do.</summary>
    [Fact]
    public void HubBao0Don_MaMayDangCoDon_GiuSoMayVaNoiRo()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1" } }, DateTime.UtcNow);
        services.QueryOrderStatistics = (_, _, _, _) => Task.FromResult<SharedOrderStatistics?>(SoChung(0));

        var vm = new OrderStatisticsViewModel(services);

        Assert.True(vm.HasData);                 // KHÔNG được tụt về màn rỗng
        Assert.Equal("1", vm.TotalOrdersText);   // vẫn là số của MÁY
        Assert.True(vm.DangXemSoMay);
        Assert.Contains("Hub báo 0 đơn", vm.SourceText, StringComparison.Ordinal);
    }

    /// <summary>Nhưng Hub báo 0 đơn mà máy CŨNG không có đơn nào thì đó là sự thật — hiện màn rỗng của HỆ THỐNG
    /// như cũ, không được lấy cớ "giữ số máy" để nuốt luôn trạng thái rỗng hợp lệ.</summary>
    [Fact]
    public void HubBao0Don_MaMayCungRong_VanHienManRongCuaHeThong()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.QueryOrderStatistics = (_, _, _, _) => Task.FromResult<SharedOrderStatistics?>(SoChung(0));

        var vm = new OrderStatisticsViewModel(services);

        Assert.False(vm.HasData);
        Assert.Equal("Số chung toàn hệ thống (từ Hub).", vm.SourceText);
        Assert.Contains("trên hệ thống", vm.EmptyMessage, StringComparison.Ordinal);
    }

    private static SharedOrderStatistics SoChung(int tongDon,
        IReadOnlyList<SharedStatBreakdown>? statusRows = null,
        IReadOnlyList<SharedShopStatRow>? shopRows = null) => new(
        tongDon, tongDon, 0, 0, 0, 0, 0, tongDon, 0, 0, null,
        statusRows ?? Array.Empty<SharedStatBreakdown>(), shopRows ?? Array.Empty<SharedShopStatRow>(),
        Array.Empty<SharedStatBreakdown>(), Array.Empty<SharedStatBreakdown>());

    // ══════════ Dòng nguồn: dài vừa đủ để đọc, chi tiết đẩy vào ToolTip, và CÂM khi không có thẻ nào ══════════

    /// <summary>Kho máy RỖNG thì màn chỉ có thẻ "chưa có dữ liệu" — dòng nguồn KHÔNG được dọa "ĐÃ GIAO / ĐÃ HỦY /
    /// DOANH THU bị HỤT" nữa: nó đang trỏ vào ba cái thẻ không tồn tại.</summary>
    [Fact]
    public void KhoRong_DongNguonKhongDoaBaTheKhongTonTai()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path); // kho đơn RỖNG

        var vm = new OrderStatisticsViewModel(services);

        Assert.False(vm.HasData);
        Assert.Equal(NenDocLap, vm.SourceText);
        Assert.DoesNotContain("HỤT", vm.SourceText, StringComparison.Ordinal);
        Assert.Null(vm.SourceToolTip);
    }

    /// <summary>Khoảng ngày KHÔNG hợp lệ cũng là ca "không có thẻ nào bên dưới" — và ở đây thì im hẳn.</summary>
    [Fact]
    public void KhoangNgayKhongHopLe_DongNguonCamHan()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1" } }, DateTime.UtcNow);
        var vm = new OrderStatisticsViewModel(services);
        Assert.Contains("HỤT", vm.SourceText, StringComparison.Ordinal); // đang có đơn ⇒ có cảnh báo

        vm.FromDate = vm.ToDate!.Value.AddDays(3); // Từ > Đến

        Assert.False(vm.HasData);
        Assert.Equal(string.Empty, vm.SourceText);
        Assert.Null(vm.SourceToolTip);
    }

    /// <summary>Có đơn thì vẫn phải cảnh báo, nhưng dòng LUÔN HIỆN giữ NGẮN (bản cũ 179 ký tự tràn 2 dòng và chạy
    /// sát ô "Từ ngày" ở 1366px) — phần giải thích dài dời vào ToolTip.</summary>
    [Fact]
    public void CoDon_CanhBaoDaiNamOToolTip_DongLuonHienGiuNgan()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1" } }, DateTime.UtcNow);

        var vm = new OrderStatisticsViewModel(services);

        Assert.Equal(NguonDocLap, vm.SourceText);
        Assert.True(vm.SourceText.Length <= 140, $"Dòng nguồn phải đủ ngắn để nằm 1 dòng: {vm.SourceText.Length} ký tự");
        Assert.NotNull(vm.SourceToolTip);
        Assert.Contains("bị dọn khỏi máy", vm.SourceToolTip!, StringComparison.Ordinal);
    }

    // ══════════ Danh sách shop phải GỘP shop chỉ còn sống trên Hub ══════════

    /// <summary>Kho máy dọn hết đơn kết thúc của một shop ⇒ <c>AllShopLogins()</c> không còn shop đó, nhưng Hub vẫn
    /// có số của nó. Không gộp lại thì shop biến mất khỏi ô lọc và bộ lọc âm thầm tụt về "Tất cả shop" ngay giữa
    /// lúc người dùng đang xem đúng shop đó bằng SỐ CHUNG.</summary>
    [Fact]
    public void ShopChiConTrenHub_VanNamTrongDanhSachVaGiuChon()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        // Kho máy chỉ còn shop A; Hub có cả A lẫn B.
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "A1" } }, DateTime.UtcNow,
            shopLogin: "a.store");
        services.QueryOrderStatistics = (_, _, _, _) => Task.FromResult<SharedOrderStatistics?>(
            SoChung(5, shopRows: new[]
            {
                new SharedShopStatRow("a.store", 3, 3, 0, 0, 0),
                new SharedShopStatRow("b.store", 2, 2, 0, 0, 0),
            }));

        var vm = new OrderStatisticsViewModel(services);

        Assert.Contains("b.store", vm.ShopOptions);
        vm.SelectedShop = "b.store";

        // Lượt sync ghi xong kho đơn → Reload dựng LẠI danh sách từ kho máy (không có b.store).
        vm.DangHienTrenMan = true;
        services.RaiseOrdersChanged();

        Assert.Contains("b.store", vm.ShopOptions);
        Assert.Equal("b.store", vm.SelectedShop); // KHÔNG được âm thầm tụt về "Tất cả shop"
    }

    /// <summary>Bẫy của bước 7: bổ sung mục vào <c>ShopOptions</c> mà quên cờ <c>_reloadingOptions</c> thì mỗi lần
    /// thêm shop lại kích <c>OnSelectedShopChanged</c> → <c>ApplyStatistics</c> → hỏi Hub → thêm shop… Ở WPF,
    /// ComboBox có thể NHẢ <c>SelectedItem</c> khi <c>ItemsSource</c> đổi — test mô phỏng đúng cú đó bằng
    /// <c>CollectionChanged</c>: một lượt vẽ phải là ĐÚNG MỘT lượt hỏi Hub, và lựa chọn của người dùng phải còn.</summary>
    [Fact]
    public void GopShopTuHub_KhongKichVongVeLai_VaGiuLuaChon()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "A1" } }, DateTime.UtcNow,
            shopLogin: "a.store");
        var dem = 0;
        var shopHub = new List<SharedShopStatRow> { new("a.store", 3, 3, 0, 0, 0) };
        services.QueryOrderStatistics = (_, _, _, _) =>
        {
            dem++;
            return Task.FromResult<SharedOrderStatistics?>(SoChung(5, shopRows: shopHub.ToList()));
        };

        var vm = new OrderStatisticsViewModel(services);
        vm.SelectedShop = "a.store";
        // Shop MỚI chỉ xuất hiện ở lượt hỏi Hub sắp tới ⇒ lượt vẽ dưới đây thật sự phải CHÈN mục vào ShopOptions
        // (không dựng sẵn từ trước thì đoạn chèn không bao giờ chạy và test rỗng).
        shopHub.Add(new SharedShopStatRow("b.store", 2, 2, 0, 0, 0));
        var truoc = dem;
        vm.ShopOptions.CollectionChanged += (_, _) => vm.SelectedShop = null; // ComboBox nhả lựa chọn

        vm.Reload();

        Assert.Contains("b.store", vm.ShopOptions); // đúng là có chèn thật
        Assert.Equal("a.store", vm.SelectedShop);
        Assert.Equal(truoc + 1, dem);
    }

    // ══════════ Local và Hub: cùng luật hiển thị + cùng thứ tự ══════════

    /// <summary>Hai nguồn số vẽ lên CÙNG một lưới thì phải cùng định dạng (0 đồng ⇒ ô "Ước tính" RỖNG, không phải
    /// "₫0") và cùng thứ tự (Hub sắp theo Ordinal — "Giao hàng" trước "Đã giao" — nên client phải sắp lại).</summary>
    [Fact]
    public void LuoiTrangThai_LocalVaHub_CungDinhDangVaCungThuTu()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[]
        {
            new SyncedOrder { OrderSn = "A1", Status = "Giao hàng" },
            new SyncedOrder { OrderSn = "A2", Status = "Giao hàng" },
            new SyncedOrder { OrderSn = "B1", Status = "Đã giao" },
            new SyncedOrder { OrderSn = "B2", Status = "Đã giao" },
        }, DateTime.UtcNow);

        var vm = new OrderStatisticsViewModel(services); // chưa nối Hub → số LOCAL
        var soMay = vm.StatusRows.Select(x => (x.Label, x.ValueText)).ToList();

        Assert.Equal(new[] { "Đã giao", "Giao hàng" }, soMay.Select(x => x.Label));
        Assert.All(soMay, x => Assert.Equal(string.Empty, x.ValueText)); // 0 đồng ⇒ RỖNG

        // Hub trả CÙNG dữ liệu nhưng theo thứ tự Ordinal.
        services.QueryOrderStatistics = (_, _, _, _) => Task.FromResult<SharedOrderStatistics?>(
            SoChung(4, statusRows: new[]
            {
                new SharedStatBreakdown("Giao hàng", 2, 0, 50),
                new SharedStatBreakdown("Đã giao", 2, 0, 50),
            }));
        vm.LamMoiCommand.Execute(null);

        Assert.Equal(soMay, vm.StatusRows.Select(x => (x.Label, x.ValueText)).ToList());
    }

    // ══════════ Màn ẩn thì đừng quét kho đơn + bắn HTTP ══════════

    /// <summary>VM sống suốt vòng đời app và <c>OrdersChanged</c> bắn sau MỖI shop của MỖI lượt sync — màn đang ẩn
    /// mà vẫn quét kho đơn trên luồng UI + hỏi Hub là đốt công cho một màn không ai nhìn.</summary>
    [Fact]
    public void ManAn_KhoDonDoi_KhongGoiHub()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1" } }, DateTime.UtcNow);
        var (vm, soLan) = VmDemLuotHoiHub(services);
        Assert.False(vm.DangHienTrenMan); // mặc định: màn chưa từng mở
        var truoc = soLan();

        services.RaiseOrdersChanged();

        Assert.Equal(truoc, soLan());
        Assert.True(vm.DangChoVeLai, "Bỏ qua lượt vẽ thì phải NHỚ là còn nợ một lượt");

        // Người dùng chọn lại màn: MainViewModel bật cờ rồi gọi Reload() (case 2) → số tươi trở lại.
        vm.DangHienTrenMan = true;
        vm.Reload();

        Assert.True(soLan() > truoc, "Mở lại màn thì phải vẽ lại từ kho đơn hiện tại");
        Assert.False(vm.DangChoVeLai);
    }

    /// <summary>Mặt kia của cửa: màn ĐANG hiện thì kho đơn đổi vẫn phải vẽ lại ngay (nếu không thì "gate" chỉ là
    /// cách viết hoa mỹ của "không bao giờ cập nhật").</summary>
    [Fact]
    public void ManDangHien_KhoDonDoi_VanVeLaiNgay()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1" } }, DateTime.UtcNow);
        var (vm, soLan) = VmDemLuotHoiHub(services);
        vm.DangHienTrenMan = true;
        var truoc = soLan();

        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN2" } }, DateTime.UtcNow);
        services.RaiseOrdersChanged();

        Assert.True(soLan() > truoc);
        Assert.Equal("2", vm.TotalOrdersText);
        Assert.False(vm.DangChoVeLai);
    }

    // ══════════ Lọc 1 shop thì bỏ khối "HIỆU QUẢ THEO SHOP" ══════════

    /// <summary>Lọc đúng một shop ⇒ lưới shop chỉ còn MỘT dòng lặp lại y các thẻ số phía trên → XAML ẩn khối đó và
    /// cho "PHÂN BỔ TRẠNG THÁI" chiếm hết chiều ngang. Cờ phải BÁO ĐỔI, không thì màn vẽ theo giá trị cũ.</summary>
    [Fact]
    public void LocMotShop_TatCoLuoiShop_VaBaoDoi()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "A1" } }, DateTime.UtcNow,
            shopLogin: "a.store");
        var vm = new OrderStatisticsViewModel(services);
        var baoDoi = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.HienLuoiShop)) baoDoi++;
        };

        Assert.True(vm.HienLuoiShop); // mặc định "Tất cả shop"

        vm.SelectedShop = "a.store";
        Assert.False(vm.HienLuoiShop);
        Assert.True(baoDoi > 0, "Đổi shop mà không báo đổi thì XAML không ẩn được khối shop");

        vm.SelectedShop = OrderStatisticsViewModel.AllShopsLabel;
        Assert.True(vm.HienLuoiShop);
    }

    /// <summary>Hồi quy lỗi "đơ tới 8 giây mỗi lần chỉnh ngày": số local phải vẽ NGAY, lời gọi Hub chạy nền.</summary>
    [Fact]
    public void HubChamKhongChanLuongUi_SoLocalHienNgay()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Orders.UpsertMany(1, new[] { new SyncedOrder { OrderSn = "SN1" } }, DateTime.UtcNow);
        services.QueryOrderStatistics = async (_, _, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct); // hub treo tới sát timeout
            return null;
        };

        var sw = Stopwatch.StartNew();
        var vm = new OrderStatisticsViewModel(services);
        vm.FromDate = DateTime.Today.AddDays(-3); // đổi ngày: mỗi lần đổi lại gọi hub
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"Vẽ thống kê không được chờ hub: {sw.ElapsedMilliseconds}ms");
        Assert.Equal("1", vm.TotalOrdersText);
    }

    // ══════════ Nhịp sang ngày mới (app chạy 24/7) — gọi THẲNG KiemTraSangNgay, không ngồi chờ 60s ══════════

    /// <summary>Dựng VM kèm bộ đếm số lượt hỏi Hub — mỗi lượt vẽ lại thống kê là một lượt hỏi, nên đếm được là
    /// biết màn có vẽ lại hay không mà không phải soi từng property.</summary>
    private static (OrderStatisticsViewModel Vm, Func<int> SoLanHoiHub) VmDemLuotHoiHub(AppServices services)
    {
        var dem = 0;
        services.QueryOrderStatistics = (_, _, _, _) =>
        {
            dem++;
            return Task.FromResult<SharedOrderStatistics?>(null);
        };
        return (new OrderStatisticsViewModel(services), () => dem);
    }

    [Fact]
    public void ChuaSangNgay_KhongVeLaiGi()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var (vm, soLan) = VmDemLuotHoiHub(services);
        var truoc = soLan();

        var daTinhLai = vm.KiemTraSangNgay(DateTime.Today);

        Assert.False(daTinhLai);
        Assert.Equal(truoc, soLan()); // đường nóng mỗi 60s: không được đụng gì khi ngày chưa đổi
    }

    /// <summary>Qua nửa đêm mà đang dùng CHIP: khoảng ngày phải trượt theo ngày mới (chip "Hôm nay" mà dữ liệu vẫn
    /// của hôm qua là số nói dối).</summary>
    [Fact]
    public void SangNgayMoi_DangDungChip_TinhLaiKhoangNgay()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var (vm, soLan) = VmDemLuotHoiHub(services);
        Assert.Equal(OrderStatisticsViewModel.PresetThangNay, vm.DatePreset); // mở màn = chip "Tháng này"
        var truoc = soLan();

        var mai = DateTime.Today.AddDays(1);

        var daTinhLai = vm.KiemTraSangNgay(mai);

        Assert.True(daTinhLai);
        // CỐT LÕI: khoảng ngày phải THẬT SỰ trượt sang ngày mới. Chỉ assert "đã vẽ lại" là test rỗng — vẽ lại
        // đúng khoảng ngày CŨ vẫn cho chip "Tháng này" trỏ vào dữ liệu hôm qua (và sang tháng thì lệch cả mốc đầu).
        Assert.Equal(mai, vm.ToDate);
        Assert.Equal(new DateTime(mai.Year, mai.Month, 1), vm.FromDate);
        Assert.True(soLan() > truoc, "Sang ngày mới mà đang dùng chip thì phải tính lại khoảng ngày");
        Assert.Equal(OrderStatisticsViewModel.PresetThangNay, vm.DatePreset); // chip vẫn là chip đó
    }

    /// <summary>Chip "Hôm nay" là ca đau nhất khi qua nửa đêm: CẢ HAI mốc phải nhảy sang ngày mới.</summary>
    [Fact]
    public void SangNgayMoi_ChipHomNay_CaHaiMocNhaySangNgayMoi()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var (vm, _) = VmDemLuotHoiHub(services);
        vm.ApplyDatePresetCommand.Execute(OrderStatisticsViewModel.PresetHomNay);
        var mai = DateTime.Today.AddDays(1);

        Assert.True(vm.KiemTraSangNgay(mai));

        Assert.Equal(mai, vm.FromDate);
        Assert.Equal(mai, vm.ToDate);
        Assert.True(vm.IsPresetHomNay);
    }

    /// <summary>Qua nửa đêm mà người dùng TỰ chọn khoảng ngày trên lịch (không còn chip) → TUYỆT ĐỐI không giật
    /// ngày khỏi tay họ.</summary>
    [Fact]
    public void SangNgayMoi_NguoiDungTuChonNgay_KhongGiatNgayKhoiTayHo()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var (vm, soLan) = VmDemLuotHoiHub(services);
        vm.FromDate = vm.FromDate!.Value.AddDays(-3); // tự chọn ngày → nhả hết chip
        Assert.Equal(string.Empty, vm.DatePreset);
        var from = vm.FromDate;
        var to = vm.ToDate;
        var truoc = soLan();

        var daTinhLai = vm.KiemTraSangNgay(DateTime.Today.AddDays(1));

        Assert.False(daTinhLai);
        Assert.Equal(from, vm.FromDate);
        Assert.Equal(to, vm.ToDate);
        Assert.Equal(truoc, soLan()); // không vẽ lại ⇒ cũng không phiền hub một lượt vô ích
    }

    // ══════════ Bánh xe chuột trên 4 lưới: chỉ trả về cho TRANG khi lưới đã hết chỗ cuộn ══════════

    /// <summary>
    /// Luật của <c>OrderStatisticsView.LuoiXemSo_PreviewMouseWheel</c>: còn chỗ cuộn theo chiều đang lăn thì lưới
    /// GIỮ bánh xe (lưới trạng thái dài vẫn phải cuộn được), hết chỗ / không có gì để cuộn thì nhả cho trang.
    /// <paramref name="delta"/> &gt; 0 = lăn LÊN, &lt; 0 = lăn XUỐNG.
    /// </summary>
    [Theory]
    [InlineData(0, 0, -120, false)]     // lưới ngắn, không có gì để cuộn → nhả ngay
    [InlineData(0, 0, 120, false)]
    [InlineData(0, 300, -120, true)]    // đang ở đỉnh, lăn xuống → còn cuộn được
    [InlineData(0, 300, 120, false)]    // đang ở đỉnh, lăn lên → hết chỗ, nhả cho trang
    [InlineData(300, 300, -120, false)] // đang ở đáy, lăn xuống → hết chỗ, nhả cho trang
    [InlineData(300, 300, 120, true)]   // đang ở đáy, lăn lên → còn cuộn được
    [InlineData(150, 300, -120, true)]  // đang ở giữa → cả hai chiều đều còn cuộn
    [InlineData(150, 300, 120, true)]
    public void LanChuotTrenLuoi_ChiNhaChoTrangKhiHetChoCuon(
        double viTri, double tamCuon, int delta, bool mongDoi)
        => Assert.Equal(mongDoi, OrderStatisticsView.ConChoCuonTheoChieuLan(viTri, tamCuon, delta));
}
