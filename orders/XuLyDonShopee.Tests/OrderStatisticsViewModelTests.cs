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
    /// <summary>Vế CẢNH BÁO mà VM ghép vào MỌI dòng nguồn số LOCAL (kho máy đã dọn đơn kết thúc nên 3 thẻ
    /// ĐÃ GIAO / ĐÃ HỦY / DOANH THU bị hụt). Chép ĐÚNG chuỗi của VM: bỏ vế này đi là test đổ.</summary>
    private const string CanhBaoHut =
        " Kho máy chỉ giữ đơn CHƯA kết thúc (đơn Đã giao/Đã hủy đã dọn sau khi đẩy Hub & Google Sheet)" +
        " — ĐÃ GIAO / ĐÃ HỦY / DOANH THU bên dưới bị HỤT.";

    private const string NguonDangHoi = "Số trên MÁY NÀY — đang hỏi Hub số chung…" + CanhBaoHut;
    private const string NguonHubChet = "Số trên MÁY NÀY — Hub không phản hồi nên chưa gộp được số chung." + CanhBaoHut;
    private const string NguonDocLap = "Số trên MÁY NÀY (app chạy độc lập, chưa nối Hub)." + CanhBaoHut;

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

    private static SharedOrderStatistics SoChung(int tongDon) => new(
        tongDon, tongDon, 0, 0, 0, 0, 0, tongDon, 0, 0, null,
        Array.Empty<SharedStatBreakdown>(), Array.Empty<SharedShopStatRow>(),
        Array.Empty<SharedStatBreakdown>(), Array.Empty<SharedStatBreakdown>());

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
