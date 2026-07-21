using System.Linq;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test cho ProxiesViewModel.SaveKeys: chỉ lưu danh sách API key KiotProxy vào pool chung (settings),
/// KHÔNG còn gán cố định key vào từng tài khoản — app tự chia key cho tài khoản khi chạy.
/// ViewModel chạy được bằng xunit thuần (ObservableObject + repository trên DB tạm).
/// </summary>
public class ProxiesViewModelTests
{
    // ===== Lưu N key → chỉ vào pool settings; ProxyKey của mọi tài khoản GIỮ NGUYÊN =====
    [Fact]
    public void SaveKeys_LuuKeyVaoPool_KhongDungProxyKeyTaiKhoan()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);

        // 10 tài khoản; một vài tài khoản có sẵn ProxyKey cũ để kiểm SaveKeys KHÔNG đụng tới.
        for (var i = 1; i <= 10; i++)
        {
            services.Accounts.Insert(new Account
            {
                Email = $"acc{i:D2}@mail.com",
                Password = "p",
                ProxyKey = i % 3 == 0 ? "cu-cua-toi" : null
            });
        }

        var vm = new ProxiesViewModel(services);
        vm.Keys = "k1\nk2\nk3\nk4";
        vm.SaveKeysCommand.Execute(null);

        // Pool settings lưu đúng 4 key.
        Assert.Equal(new[] { "k1", "k2", "k3", "k4" }, services.Settings.GetKiotProxyKeys());

        // ProxyKey của MỌI tài khoản giữ nguyên như trước khi lưu (cũ vẫn "cu-cua-toi", null vẫn null).
        var saved = services.Accounts.GetAll();
        Assert.Equal(10, saved.Count);
        Assert.Equal(
            Enumerable.Range(1, 10).Select(i => i % 3 == 0 ? "cu-cua-toi" : null),
            saved.Select(a => a.ProxyKey));

        // Thông báo phản ánh cơ chế pool.
        Assert.Contains("vào pool", vm.SavedKeysMessage);
        Assert.Contains("4 key", vm.SavedKeysMessage);
    }

    // ===== Ô trống bấm Lưu → pool rỗng; không đụng ProxyKey tài khoản; thông báo "chưa có key" =====
    [Fact]
    public void SaveKeys_ORong_ThongBaoChuaCoKey()
    {
        using var temp = new TempDatabase();
        var services = new AppServices(temp.Path);

        services.Accounts.Insert(new Account { Email = "a@mail.com", Password = "p", ProxyKey = "giu-nguyen-a" });
        services.Accounts.Insert(new Account { Email = "b@mail.com", Password = "p", ProxyKey = null });

        var vm = new ProxiesViewModel(services);
        vm.Keys = "";
        vm.SaveKeysCommand.Execute(null);

        var saved = services.Accounts.GetAll();
        Assert.Equal("giu-nguyen-a", saved[0].ProxyKey);
        Assert.Null(saved[1].ProxyKey);

        Assert.Equal("Đã lưu (chưa có key — sẽ dùng IP máy).", vm.SavedKeysMessage);
        Assert.Empty(services.Settings.GetKiotProxyKeys());
    }
}
