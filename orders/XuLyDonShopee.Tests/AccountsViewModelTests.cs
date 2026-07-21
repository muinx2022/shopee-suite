using System.Linq;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hồi quy cho AccountsViewModel. ViewModel không cần Avalonia runtime để chạy logic
/// (chỉ dùng ObservableObject + repository trên DB tạm), nên test được bằng xunit thuần.
/// Lưu ý: đường dẫn xóa/hủy dùng DialogService (Avalonia Window) nên KHÔNG test ở đây.
/// </summary>
public class AccountsViewModelTests
{
    // ===== Lỗi 1: lưu tài khoản mới khi đang có bộ lọc tìm kiếm =====
    [Fact]
    public void Save_TaoMoiKhiDangLoc_ChonDungBanGhi_VaKhongTaoTrung()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        // Có sẵn 1 tài khoản để bộ lọc "abc" có ý nghĩa.
        services.Accounts.Insert(new Account { Email = "abc@mail.com", Password = "p" });

        var vm = new AccountsViewModel(services);
        vm.SearchText = "abc";
        Assert.Single(vm.Accounts); // chỉ hiển thị abc@

        vm.AddCommand.Execute(null);
        vm.EditEmail = "xyz@mail.com"; // email KHÔNG chứa "abc"
        vm.EditPassword = "123";
        vm.SaveCommand.Execute(null);

        // Sau khi lưu: form về trạng thái sửa bình thường, bản ghi mới được chọn và hiển thị.
        Assert.False(vm.IsNew);
        Assert.NotNull(vm.SelectedRow);
        Assert.Equal("xyz@mail.com", vm.SelectedRow!.Email);
        Assert.Contains(vm.Accounts, a => a.Email == "xyz@mail.com");
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(2, services.Accounts.GetAll().Count);

        // Lưu lần 2 (không đổi gì): KHÔNG tạo bản ghi trùng, KHÔNG báo lỗi "email đã tồn tại".
        vm.SaveCommand.Execute(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(2, services.Accounts.GetAll().Count);
    }

    // ===== Phần 1: cho phép nhập user KHÔNG phải email (bỏ ràng buộc định dạng email) =====
    [Fact]
    public void Save_UserKhongPhaiEmail_VanLuuDuoc()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);

        var vm = new AccountsViewModel(services);
        vm.AddCommand.Execute(null);
        vm.EditEmail = "shopee_user01"; // KHÔNG có '@'
        vm.EditPassword = "123";
        vm.SaveCommand.Execute(null);

        Assert.Null(vm.ErrorMessage);
        Assert.Single(services.Accounts.GetAll());
        Assert.Equal("shopee_user01", services.Accounts.GetAll().First().Email);
        Assert.NotNull(vm.SelectedRow);
        Assert.Equal("shopee_user01", vm.SelectedRow!.Email);
    }

    // ===== Phần 1: user rỗng vẫn phải báo lỗi và KHÔNG ghi DB =====
    [Fact]
    public void Save_UserRong_BaoLoi()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);

        var vm = new AccountsViewModel(services);
        vm.AddCommand.Execute(null);
        vm.EditEmail = "";
        vm.EditPassword = "123";
        vm.SaveCommand.Execute(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(services.Accounts.GetAll());
    }

    // ===== Lỗi A: proxy thủ công phải round-robin BỀN qua các lần mở (không reset về [0]) =====
    [Fact]
    public void NextManualProxy_XoayVongBenQuaCacLanGoi()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        var vm = new AccountsViewModel(services);

        var manual = new List<ProxyEntry>
        {
            new() { Host = "P0", Port = 8080 },
            new() { Host = "P1", Port = 8080 },
            new() { Host = "P2", Port = 8080 },
        };

        var hosts = new List<string?>();
        for (var i = 0; i < 4; i++)
        {
            hosts.Add(vm.NextManualProxy(manual)?.Host);
        }

        // 4 lần gọi trên list 3 proxy → P0,P1,P2,P0 (chỉ số bền, không reset về 0 mỗi lần).
        Assert.Equal(new[] { "P0", "P1", "P2", "P0" }, hosts);

        // List rỗng → null.
        Assert.Null(vm.NextManualProxy(new List<ProxyEntry>()));
    }

    // ===== Lỗi 2: làm mới danh sách khi đang sửa dở không được mất thay đổi chưa lưu =====
    // Trigger thật trong app là gõ ô tìm kiếm (ListBox tự đẩy SelectedItem về null khi ItemsSource
    // bị Clear). Cơ chế lỗi gốc — "thao tác chọn lại tài khoản làm nạp đè form" — được tái hiện ở
    // đây qua Reload() (re-fetch sinh instance mới, khiến code cũ kích hoạt nạp đè).
    [Fact]
    public void Reload_KhiDangSuaDo_GiuNguyenThayDoiChuaLuu()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "keep@mail.com", Password = "orig" });

        var vm = new AccountsViewModel(services);
        vm.SelectedRow = vm.Accounts.First(a => a.Email == "keep@mail.com");
        Assert.Equal("orig", vm.EditPassword);

        vm.EditPassword = "changed"; // sửa dở, chưa lưu

        vm.Reload(); // giống lúc gõ tìm kiếm / quay lại tab

        Assert.Equal("changed", vm.EditPassword); // KHÔNG bị nạp lại về "orig"
        Assert.True(vm.IsEditing);
        Assert.NotNull(vm.SelectedRow);
        Assert.Equal("keep@mail.com", vm.SelectedRow!.Email);
    }

    // ===== Chọn sang tài khoản KHÁC vẫn phải nạp form của tài khoản đó (không hồi quy) =====
    [Fact]
    public void ChonTaiKhoanKhac_NapFormCuaTaiKhoanDo()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "pa" });
        services.Accounts.Insert(new Account { Email = "b@mail.com", Password = "pb" });

        var vm = new AccountsViewModel(services);
        vm.SelectedRow = vm.Accounts.First(a => a.Email == "a@mail.com");
        Assert.Equal("pa", vm.EditPassword);

        vm.SelectedRow = vm.Accounts.First(a => a.Email == "b@mail.com");
        Assert.Equal("b@mail.com", vm.EditEmail);
        Assert.Equal("pb", vm.EditPassword);
    }

    // ===== Cập nhật tài khoản hiện có (không lọc) vẫn ghi đúng DB =====
    [Fact]
    public void Save_CapNhatTaiKhoanHienCo_GhiDungDb()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "c@mail.com", Password = "old" });

        var vm = new AccountsViewModel(services);
        vm.SelectedRow = vm.Accounts.First();
        vm.EditPassword = "new";
        vm.EditStatus = AccountStatus.BiKhoa;
        vm.SaveCommand.Execute(null);

        Assert.Null(vm.ErrorMessage);
        Assert.Single(services.Accounts.GetAll());
        var saved = services.Accounts.GetAll().First();
        Assert.Equal("new", saved.Password);
        Assert.Equal(AccountStatus.BiKhoa, saved.Status);
    }

    // Chuỗi cookie JSON hợp lệ (không rỗng) để mô phỏng phiên đã đăng nhập.
    private static string SampleCookieJson()
        => CookieJson.Serialize(new[]
        {
            new StoredCookie("SPC_EC", "abc123", ".shopee.vn", "/", 0, true, true, "Lax")
        });

    // ===== Lỗi A: đổi chọn tài khoản giữa chừng → cookie phải ghi vào ĐÚNG targetId, không sang tài khoản khác =====
    [Fact]
    public void LuuCookie_DoiChonGiuaChung_GhiVaoTargetId_KhongDeSangTaiKhoanDangXem()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "pa" });
        services.Accounts.Insert(new Account { Email = "b@mail.com", Password = "pb" });

        var vm = new AccountsViewModel(services);
        var a = vm.Accounts.First(x => x.Email == "a@mail.com");
        var b = vm.Accounts.First(x => x.Email == "b@mail.com");
        long aId = a.Id, bId = b.Id;

        // Bắt đầu luồng cho A (targetId = aId), rồi người dùng đổi chọn sang B giữa chừng.
        vm.SelectedRow = a;
        vm.SelectedRow = b; // _editingId giờ = bId

        var json = SampleCookieJson();
        var result = vm.SaveCapturedCookie(aId, json);

        Assert.Equal(AccountsViewModel.SaveCookieResult.Saved, result);
        // Cookie ghi vào ĐÚNG A, KHÔNG ghi vào B.
        Assert.Equal(json, services.Accounts.GetById(aId)!.Cookie);
        Assert.Null(services.Accounts.GetById(bId)!.Cookie);
        // Form đang xem B → KHÔNG bị đè cookie của A, vẫn hiển thị B.
        Assert.NotEqual(json, vm.EditCookie);
        Assert.Equal("b@mail.com", vm.EditEmail);
    }

    // ===== Lỗi A: bấm "+ Thêm" giữa chừng (_editingId = null) → không ném, vẫn ghi đúng targetId =====
    [Fact]
    public void LuuCookie_KhiEditingIdNull_KhongNem_VanGhiTargetId()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "pa" });

        var vm = new AccountsViewModel(services);
        long aId = vm.Accounts.First().Id;
        vm.SelectedRow = vm.Accounts.First();

        // Người dùng bấm "+ Thêm" giữa chừng → _editingId = null.
        vm.AddCommand.Execute(null);

        var json = SampleCookieJson();
        var ex = Record.Exception(() => vm.SaveCapturedCookie(aId, json));

        Assert.Null(ex); // không ném dù _editingId đã thành null
        Assert.Equal(json, services.Accounts.GetById(aId)!.Cookie); // vẫn ghi vào A
        Assert.True(vm.IsNew); // vẫn ở form tạo mới, không bị kéo về A
    }

    // ===== Lỗi B: sau khi lưu cookie, chọn lại tài khoản phải thấy cookie; Save không xóa mất cookie =====
    [Fact]
    public void LuuCookie_ChonLaiTaiKhoan_FormHienDungCookie_SaveKhongMatCookie()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "pa" });
        services.Accounts.Insert(new Account { Email = "b@mail.com", Password = "pb" });

        var vm = new AccountsViewModel(services);
        long aId = vm.Accounts.First(x => x.Email == "a@mail.com").Id;
        vm.SelectedRow = vm.Accounts.First(x => x.Email == "a@mail.com"); // đang mở A

        var json = SampleCookieJson();
        var result = vm.SaveCapturedCookie(aId, json);
        Assert.Equal(AccountsViewModel.SaveCookieResult.Saved, result);

        // Chọn sang B rồi quay lại A từ danh sách hiển thị (instance trong Accounts).
        vm.SelectedRow = vm.Accounts.First(x => x.Email == "b@mail.com");
        vm.SelectedRow = vm.Accounts.First(x => x.Email == "a@mail.com");

        // Form hiển thị đúng cookie vừa lưu (Accounts đã được dựng lại với instance mới có cookie).
        Assert.Equal(json, vm.EditCookie);

        // Bấm Lưu KHÔNG được xóa cookie (trước đây instance cũ rỗng → Save ghi null).
        vm.SaveCommand.Execute(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(json, services.Accounts.GetById(aId)!.Cookie);
    }

    // ===== Lỗi B (biến thể): người dùng đang mở A, lưu cookie xong không cần chọn lại vẫn có cookie trên form =====
    [Fact]
    public void LuuCookie_DangMoDungTaiKhoan_FormCapNhatCookieNgay()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "pa" });

        var vm = new AccountsViewModel(services);
        long aId = vm.Accounts.First().Id;
        vm.SelectedRow = vm.Accounts.First();

        var json = SampleCookieJson();
        vm.SaveCapturedCookie(aId, json);

        // Form cập nhật cookie ngay và instance trong danh sách cũng có cookie.
        Assert.Equal(json, vm.EditCookie);
        Assert.Equal(json, vm.Accounts.First(x => x.Id == aId).Account.Cookie);
    }

    // ===== Plan B: "Chọn toàn bộ" tick/bỏ hết theo danh sách đang hiển thị (toggle) =====
    [Fact]
    public void SelectAll_TickHetRoiBoHet_Toggle()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "p" });
        services.Accounts.Insert(new Account { Email = "b@mail.com", Password = "p" });
        services.Accounts.Insert(new Account { Email = "c@mail.com", Password = "p" });

        var vm = new AccountsViewModel(services);
        Assert.Equal(3, vm.Accounts.Count);
        Assert.All(vm.Accounts, r => Assert.False(r.IsSelected));

        // Lần 1: chưa tick hết → tick hết.
        vm.SelectAllCommand.Execute(null);
        Assert.All(vm.Accounts, r => Assert.True(r.IsSelected));

        // Lần 2: đã tick hết → bỏ tick hết.
        vm.SelectAllCommand.Execute(null);
        Assert.All(vm.Accounts, r => Assert.False(r.IsSelected));
    }

    // ===== "Chọn toàn bộ" khi mới tick một phần → phải tick HẾT (chưa đủ mới coi là "đã hết") =====
    [Fact]
    public void SelectAll_TickMotPhan_ThiTickHet()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "p" });
        services.Accounts.Insert(new Account { Email = "b@mail.com", Password = "p" });

        var vm = new AccountsViewModel(services);
        vm.Accounts.First().IsSelected = true; // mới tick 1 dòng

        vm.SelectAllCommand.Execute(null);

        Assert.All(vm.Accounts, r => Assert.True(r.IsSelected));
    }

    // ===== Plan B: bấm 1 tài khoản → nạp đúng form; KHÔNG đổi thứ tự danh sách (đã BỎ "nổi lên đầu"). =====
    [Fact]
    public void ChonRow_NapDungForm_KhongDoiThuTuList()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "pa" });
        services.Accounts.Insert(new Account { Email = "b@mail.com", Password = "pb" });
        services.Accounts.Insert(new Account { Email = "c@mail.com", Password = "pc" });

        var vm = new AccountsViewModel(services);

        // Chọn dòng CUỐI (c) → nạp form của c, và danh sách GIỮ NGUYÊN thứ tự (c KHÔNG nhảy lên đầu).
        var third = vm.Accounts.First(r => r.Email == "c@mail.com");
        vm.SelectedRow = third;

        Assert.Same(third, vm.SelectedRow);
        Assert.Equal("c@mail.com", vm.EditEmail);    // form nạp đúng tài khoản vừa chọn
        Assert.Equal("pc", vm.EditPassword);
        // KHÔNG đổi thứ tự: a vẫn ở đầu, c vẫn ở cuối.
        Assert.Equal("a@mail.com", vm.Accounts[0].Email);
        Assert.Equal("c@mail.com", vm.Accounts[2].Email);
    }

    // ===== "Nổi lên đầu" khi đang sửa dở đúng tài khoản đó KHÔNG nạp đè form (giữ dữ liệu chưa lưu) =====
    [Fact]
    public void ChonLaiRowDangSuaDo_KhongNapDeForm()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "pa" });
        services.Accounts.Insert(new Account { Email = "b@mail.com", Password = "pb" });

        var vm = new AccountsViewModel(services);
        var rowB = vm.Accounts.First(r => r.Email == "b@mail.com");
        vm.SelectedRow = rowB;
        vm.EditPassword = "changed"; // sửa dở, chưa lưu

        // Chọn lại CHÍNH dòng đang sửa (cùng Id) → không nạp đè, giữ "changed".
        vm.SelectedRow = vm.Accounts.First(r => r.Id == rowB.Id);

        Assert.Equal("changed", vm.EditPassword);
    }

    // ===== "Chọn toàn bộ" chỉ áp trên danh sách ĐANG LỌC (dòng bị ẩn không bị tick) =====
    [Fact]
    public void SelectAll_ChiApTrenDanhSachDangLoc()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "abc@mail.com", Password = "p" });
        services.Accounts.Insert(new Account { Email = "xyz@mail.com", Password = "p" });

        var vm = new AccountsViewModel(services);
        vm.SearchText = "abc"; // chỉ còn abc hiển thị
        Assert.Single(vm.Accounts);

        vm.SelectAllCommand.Execute(null);
        Assert.True(vm.Accounts.Single().IsSelected);

        // Bỏ lọc → xyz xuất hiện nhưng KHÔNG bị tick (chỉ tick dòng đang hiển thị lúc nãy).
        vm.SearchText = string.Empty;
        Assert.False(vm.Accounts.First(r => r.Email == "xyz@mail.com").IsSelected);
    }

    // ===== LỖI 1: phiên lưu cookie (dựng lại danh sách) KHÔNG được xóa tick "đã chọn" khi đang chạy =====
    [Fact]
    public void LuuCookie_KhiDangChay_KhongMatTickChon()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "pa" });
        services.Accounts.Insert(new Account { Email = "b@mail.com", Password = "pb" });

        var vm = new AccountsViewModel(services);
        long aId = vm.Accounts.First(r => r.Email == "a@mail.com").Id;

        // Tick cả 2 (mô phỏng "Chạy đã chọn" 2 phiên).
        foreach (var r in vm.Accounts)
        {
            r.IsSelected = true;
        }

        // Một phiên lưu cookie xong → dựng lại danh sách (mô phỏng luồng CookieSaved gây rebuild).
        var result = vm.SaveCapturedCookie(aId, SampleCookieJson());
        Assert.Equal(AccountsViewModel.SaveCookieResult.Saved, result);

        // Tick KHÔNG bị mất → "Dừng đã chọn" vẫn thấy đủ 2 dòng.
        Assert.All(vm.Accounts, r => Assert.True(r.IsSelected));
        Assert.Equal(2, vm.Accounts.Count(r => r.IsSelected));
    }

    // ===== SỬA B: tick còn nguyên khi lọc ẩn dòng rồi bỏ lọc (dòng ẩn vẫn giữ tick) =====
    [Fact]
    public void TimKiem_RoiXoa_GiuTickChon()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "abc@mail.com", Password = "p" });
        services.Accounts.Insert(new Account { Email = "xyz@mail.com", Password = "p" });

        var vm = new AccountsViewModel(services);
        vm.Accounts.First(r => r.Email == "abc@mail.com").IsSelected = true;

        // Lọc ẩn abc (chỉ còn xyz), rồi xóa từ khóa.
        vm.SearchText = "xyz";
        Assert.DoesNotContain(vm.Accounts, r => r.Email == "abc@mail.com");
        vm.SearchText = string.Empty;

        // abc xuất hiện lại và VẪN được tick.
        Assert.True(vm.Accounts.First(r => r.Email == "abc@mail.com").IsSelected);
    }

    // ===== Địa chỉ lấy hàng: tạo mới không đụng gì → DB lưu mặc định "Thanh Hóa" =====
    [Fact]
    public void Save_TaoMoi_MacDinhPickupAddressThanhHoa()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);

        var vm = new AccountsViewModel(services);
        vm.AddCommand.Execute(null);
        Assert.Equal(AccountsViewModel.DefaultPickupAddress, vm.EditPickupAddress); // form mặc định
        vm.EditEmail = "new@mail.com";
        vm.EditPassword = "123";
        vm.SaveCommand.Execute(null);

        Assert.Null(vm.ErrorMessage);
        Assert.Equal("Thanh Hóa", services.Accounts.GetAll().Single().PickupAddress);
    }

    // ===== Địa chỉ lấy hàng: tài khoản có "Hà Nội" → LoadIntoForm hiện "Hà Nội" =====
    [Fact]
    public void LoadIntoForm_TaiKhoanCoHaNoi_FormHienHaNoi()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "p", PickupAddress = "Hà Nội" });

        var vm = new AccountsViewModel(services);
        vm.SelectedRow = vm.Accounts.First();

        Assert.Equal("Hà Nội", vm.EditPickupAddress);
    }

    // ===== Địa chỉ lấy hàng: bản ghi cũ (null) → form hiện mặc định "Thanh Hóa" =====
    [Fact]
    public void LoadIntoForm_PickupAddressNull_FormHienThanhHoa()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "p" }); // PickupAddress = null

        var vm = new AccountsViewModel(services);
        vm.SelectedRow = vm.Accounts.First();

        Assert.Equal("Thanh Hóa", vm.EditPickupAddress);
    }

    // ===== Địa chỉ lấy hàng: đổi sang "TP Hồ Chí Minh" → Save → DB đúng =====
    [Fact]
    public void Save_DoiPickupAddress_GhiDungDb()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "p", PickupAddress = "Hà Nội" });

        var vm = new AccountsViewModel(services);
        vm.SelectedRow = vm.Accounts.First();
        vm.EditPickupAddress = "TP Hồ Chí Minh";
        vm.SaveCommand.Execute(null);

        Assert.Null(vm.ErrorMessage);
        Assert.Equal("TP Hồ Chí Minh", services.Accounts.GetAll().Single().PickupAddress);
    }

    // ===== Nút Sync/Kiểm tra LUÔN bật khi đang xem tài khoản ĐÃ LƯU (không phụ thuộc phiên chạy) =====
    [Fact]
    public void CanSyncCheck_ChonTaiKhoanDaLuu_Bat_DuChuaMoPhien()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "p" });

        var vm = new AccountsViewModel(services);
        // Chưa chọn gì → nút Sync tắt (chưa có tài khoản để thao tác).
        Assert.False(vm.CanSyncOrders);

        vm.SelectedRow = vm.Accounts.First();

        // Đang xem tài khoản đã lưu, KHÔNG mở phiên nào → nút Sync VẪN bật (bấm sẽ tự mở phiên).
        Assert.False(services.Sessions.IsRunning(vm.Accounts.First().Id));
        Assert.True(vm.CanSyncOrders);
    }

    // ===== Tài khoản MỚI chưa lưu (IsNew) → 2 nút tắt =====
    [Fact]
    public void CanSyncCheck_TaiKhoanMoiChuaLuu_Tat()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);

        var vm = new AccountsViewModel(services);
        vm.AddCommand.Execute(null); // form tạo mới (IsNew = true), chưa có Id

        Assert.False(vm.CanSyncOrders);
    }

    // ===== Sau khi LƯU tài khoản mới → có Id, hết IsNew → 2 nút bật =====
    [Fact]
    public void CanSyncCheck_SauKhiLuuTaiKhoanMoi_Bat()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);

        var vm = new AccountsViewModel(services);
        vm.AddCommand.Execute(null);
        Assert.False(vm.CanSyncOrders); // đang tạo mới → tắt

        vm.EditEmail = "new@mail.com";
        vm.EditPassword = "123";
        vm.SaveCommand.Execute(null);

        // Đã lưu (có Id, IsNew=false, vẫn đang xem) → bật.
        Assert.False(vm.IsNew);
        Assert.True(vm.CanSyncOrders);
    }

    // ===== Bỏ chọn (SelectedRow=null) → 2 nút tắt lại =====
    [Fact]
    public void CanSyncCheck_BoChon_Tat()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "p" });

        var vm = new AccountsViewModel(services);
        vm.SelectedRow = vm.Accounts.First();
        Assert.True(vm.CanSyncOrders);

        vm.SelectedRow = null; // rời khỏi tài khoản (không ở chế độ tạo mới)

        Assert.False(vm.CanSyncOrders);
    }

    // ===== Nút Sync/Kiểm tra phát PropertyChanged khi đổi chọn (để binding IsEnabled cập nhật) =====
    [Fact]
    public void CanSyncCheck_PhatPropertyChanged_KhiDoiChon()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "p" });

        var vm = new AccountsViewModel(services);
        var raised = new HashSet<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is not null) raised.Add(e.PropertyName); };

        vm.SelectedRow = vm.Accounts.First();

        Assert.Contains(nameof(vm.CanSyncOrders), raised);
    }

    // ===== Hàm thuần quyết định "phiên sẵn sàng thao tác" theo CỜ TƯỜNG MINH ReadyForActions =====
    // (KHÔNG suy từ ToShipCount != null — số đơn cũ còn sót khi relaunch sẽ gây "sẵn sàng ảo" lúc đang login.)
    [Theory]
    [InlineData(SessionState.Running, true, true)]    // Running + cờ bật → sẵn sàng (đăng nhập xong + đọc số lần đầu)
    [InlineData(SessionState.Running, false, false)]  // Running nhưng cờ CHƯA bật (đang login / số đơn cũ) → CHƯA sẵn sàng
    [InlineData(SessionState.Opening, true, false)]   // đang mở → chưa sẵn sàng (dù cờ lỡ còn sót → chốt bằng state)
    [InlineData(SessionState.Stopped, true, false)]
    [InlineData(SessionState.Error, true, false)]
    public void IsSessionReadyForActions_DungTheoCoTuongMinh_VaState(
        SessionState state, bool ready, bool expected)
    {
        Assert.Equal(expected, AccountsViewModel.IsSessionReadyForActions(state, ready));
    }

    // ===== HÀNG LOẠT: không tick tài khoản nào → command thoát ngay, KHÔNG mở phiên nào =====
    // (đường "chụp danh sách rỗng → thôi" của RunSelectedBatchAsync; không cần Brave.)
    [Fact]
    public async Task SyncSelected_KhongTick_KhongMoPhien()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "p" });

        var vm = new AccountsViewModel(services);
        await vm.SyncSelectedCommand.ExecuteAsync(null);

        Assert.Empty(services.Sessions.Active);
    }

    // ===== Đồng bộ tick ↔ chọn dòng: ToggleRowTick (giữ tick nhiều) =====
    [Fact]
    public void ToggleRowTick_DoiTrangThaiTick()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "p" });

        var vm = new AccountsViewModel(services);
        var row = vm.Accounts.First();

        Assert.False(row.IsSelected);
        vm.ToggleRowTick(row);
        Assert.True(row.IsSelected);
        vm.ToggleRowTick(row);
        Assert.False(row.IsSelected);
    }

    [Fact]
    public void ToggleRowTick_ChiDoiDungRow_GiuTickCacRowKhac()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);
        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "p" });
        services.Accounts.Insert(new Account { Email = "b@mail.com", Password = "p" });

        var vm = new AccountsViewModel(services);
        var a = vm.Accounts.First();
        var b = vm.Accounts.First(r => r.Id != a.Id);
        a.IsSelected = true;
        b.IsSelected = true;

        vm.ToggleRowTick(a); // chỉ đổi a; b giữ nguyên → vẫn tick nhiều được
        Assert.False(a.IsSelected);
        Assert.True(b.IsSelected);
    }

}
