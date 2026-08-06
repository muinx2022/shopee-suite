using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Chip chọn nhanh khoảng ngày ở màn Thống kê (đợt G): mỗi chip set ĐÚNG cặp Từ/Đến, chip đang chọn được tô,
/// và người dùng tự sửa ngày trên lịch thì cả 3 chip nhả ra.
/// </summary>
public class OrderStatisticsDatePresetTests
{
    private static OrderStatisticsViewModel NewVm(TempDatabase temp)
        => new(new AppServices(temp.Path));

    [Fact]
    public void MoMan_MacDinhLaChipThangNay()
    {
        using var temp = new TempDatabase();

        var vm = NewVm(temp);

        Assert.Equal(OrderStatisticsViewModel.PresetThangNay, vm.DatePreset);
        Assert.True(vm.IsPresetThangNay);
        Assert.False(vm.IsPresetHomNay);
        Assert.False(vm.IsPresetBayNgay);
    }

    [Fact]
    public void ChipHomNay_DatCaHaiMocVeHomNay()
    {
        using var temp = new TempDatabase();
        var vm = NewVm(temp);
        var today = DateTime.Today;

        vm.ApplyDatePresetCommand.Execute(OrderStatisticsViewModel.PresetHomNay);

        Assert.Equal(today, vm.FromDate);
        Assert.Equal(today, vm.ToDate);
        Assert.True(vm.IsPresetHomNay);
        Assert.False(vm.IsPresetThangNay);
    }

    [Fact]
    public void Chip7Ngay_TinhCaHomNay_Nen_Lui6Ngay()
    {
        using var temp = new TempDatabase();
        var vm = NewVm(temp);
        var today = DateTime.Today;

        vm.ApplyDatePresetCommand.Execute(OrderStatisticsViewModel.PresetBayNgay);

        Assert.Equal(today.AddDays(-6), vm.FromDate);
        Assert.Equal(today, vm.ToDate);
        Assert.True(vm.IsPresetBayNgay);
    }

    [Fact]
    public void ChipThangNay_TuNgay1DenHomNay()
    {
        using var temp = new TempDatabase();
        var vm = NewVm(temp);
        vm.ApplyDatePresetCommand.Execute(OrderStatisticsViewModel.PresetHomNay);
        var today = DateTime.Today;

        vm.ApplyDatePresetCommand.Execute(OrderStatisticsViewModel.PresetThangNay);

        Assert.Equal(new DateTime(today.Year, today.Month, 1), vm.FromDate);
        Assert.Equal(today, vm.ToDate);
        Assert.True(vm.IsPresetThangNay);
    }

    /// <summary>
    /// Gán mốc mới bằng cách LÙI từ chính giá trị đang có, KHÔNG dùng <c>DateTime.Today.AddDays(-3)</c>: mặc định
    /// <c>FromDate</c> là NGÀY 1 của tháng, nên chạy vào MÙNG 4 thì <c>Today.AddDays(-3)</c> trùng đúng giá trị cũ
    /// ⇒ setter không bắn ⇒ chip không nhả ⇒ test đỏ mỗi tháng một ngày (bom hẹn giờ).
    /// </summary>
    [Fact]
    public void TuChonNgayTrenLich_NhaHetChip()
    {
        using var temp = new TempDatabase();
        var vm = NewVm(temp);

        vm.FromDate = vm.FromDate!.Value.AddDays(-3);

        Assert.Equal(string.Empty, vm.DatePreset);
        Assert.False(vm.IsPresetHomNay);
        Assert.False(vm.IsPresetBayNgay);
        Assert.False(vm.IsPresetThangNay);
    }

    /// <summary>Chốt hành vi đứng SAU bom hẹn giờ ở trên: gán lại ĐÚNG giá trị đang có thì setter của
    /// <c>CommunityToolkit.Mvvm</c> KHÔNG bắn (SetProperty so bằng trước) ⇒ màn không coi đó là "người dùng tự
    /// chọn ngày" ⇒ chip GIỮ nguyên. Test viết ra để lần sau ai dựa vào mốc ngày tuyệt đối thì biết vì sao hỏng.</summary>
    [Fact]
    public void GanLaiDungGiaTriCu_KhongCoiLaNguoiDungDoiNgay()
    {
        using var temp = new TempDatabase();
        var vm = NewVm(temp);

        vm.FromDate = vm.FromDate; // gán TRÙNG giá trị cũ

        Assert.Equal(OrderStatisticsViewModel.PresetThangNay, vm.DatePreset);
        Assert.True(vm.IsPresetThangNay);
    }

    /// <summary>Chip KHÔNG được đi ngang qua trạng thái "khoảng ngày không hợp lệ" (Từ &gt; Đến) khi đặt lần
    /// lượt 2 mốc. Ca dễ vỡ: người dùng tự kéo "Đến ngày" về quá khứ rồi bấm chip "Hôm nay" — đặt Từ = hôm nay
    /// TRƯỚC khi kịp đặt Đến sẽ tạo khoảng Từ &gt; Đến, và nếu vẽ lại ngay sau MỖI mốc thì màn chớp một lượt
    /// báo lỗi + gọi phí một lượt hỏi Hub.</summary>
    [Fact]
    public void DoiChip_KhongLotQuaKhoangNgayKhongHopLe()
    {
        using var temp = new TempDatabase();
        var vm = NewVm(temp);
        vm.FromDate = DateTime.Today.AddDays(-10);
        vm.ToDate = DateTime.Today.AddDays(-5);
        var loi = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.EmptyMessage)) loi.Add(vm.EmptyMessage);
        };

        vm.ApplyDatePresetCommand.Execute(OrderStatisticsViewModel.PresetHomNay);

        Assert.DoesNotContain(loi, x => x.Contains("không hợp lệ", StringComparison.Ordinal));
        Assert.Equal(DateTime.Today, vm.FromDate);
        Assert.Equal(DateTime.Today, vm.ToDate);
    }

    [Fact]
    public void ThamSoLa_KhongDoiKhoangDangXem()
    {
        using var temp = new TempDatabase();
        var vm = NewVm(temp);
        var from = vm.FromDate;
        var to = vm.ToDate;

        vm.ApplyDatePresetCommand.Execute("khong-co-that");

        Assert.Equal(from, vm.FromDate);
        Assert.Equal(to, vm.ToDate);
        Assert.Equal(OrderStatisticsViewModel.PresetThangNay, vm.DatePreset);
    }
}
