using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Canh bước "dọn hồ sơ trước khi phóng trình duyệt" — bước quyết định để không dính process-singleton handoff
/// (triệu chứng: "Trình duyệt thoát ngay khi khởi động", cả vòng chết ở bước đăng nhập).
/// </summary>
public class BrowserProfileGuardTests
{
    private const string Profile = @"C:\Users\Ng Xuan Mui\AppData\Roaming\XuLyDonShopee\profiles\30-brave";

    [Fact]
    public void BuildProcessFilter_LocDungBaTrinhDuyet_VaChuaDuongDanHoSo()
    {
        var filter = BrowserProfileGuard.BuildProcessFilter(Profile, alsoMatchBridgeExtension: false);

        Assert.Contains("$_.Name -in 'brave.exe','chrome.exe','msedge.exe'", filter);
        Assert.Contains("$_.CommandLine -like '*" + Profile + "*'", filter);
    }

    [Fact]
    public void BuildProcessFilter_KhongBatExtension_ThiKhongDungCuaSoPhienKhac()
    {
        // Đường đăng nhập Playwright + dọn cuối vòng chỉ được đụng ĐÚNG hồ sơ của mình. Lọt 'shopee-orders' vào
        // đây là giết luôn trình duyệt cầu nối của tài khoản khác đang chạy.
        var filter = BrowserProfileGuard.BuildProcessFilter(Profile, alsoMatchBridgeExtension: false);

        Assert.DoesNotContain("shopee-orders", filter);
    }

    [Fact]
    public void BuildProcessFilter_BatExtension_GiuNguyenHanhViCuaCauNoi()
    {
        // Đường cầu nối (trình duyệt sạch) PHẢI giết cả bản đang nạp extension dù khác hồ sơ: chúng tranh cổng WS
        // cố định 47821 và có thể còn --remote-debugging-port (bản sạch bị nhồi vào đó ⇒ Chi tiết dính captcha).
        // Chuỗi dưới đây là NGUYÊN VĂN mệnh đề của bản trước khi tách lớp — refactor không được đổi hành vi.
        var filter = BrowserProfileGuard.BuildProcessFilter(Profile, alsoMatchBridgeExtension: true);

        Assert.Equal(
            "$_.Name -in 'brave.exe','chrome.exe','msedge.exe' -and " +
            "($_.CommandLine -like '*" + Profile + "*' -or $_.CommandLine -like '*shopee-orders*')",
            filter);
    }

    [Fact]
    public void BuildProcessFilter_DuongDanCoNhayDon_DuocEscape()
    {
        // Nháy đơn không escape → PowerShell đứt chuỗi ⇒ lệnh lọc hỏng ⇒ KHÔNG giết được gì mà cũng không ai báo lỗi.
        var filter = BrowserProfileGuard.BuildProcessFilter(@"C:\hs\Mui's\30-brave", alsoMatchBridgeExtension: false);

        Assert.Contains(@"*C:\hs\Mui''s\30-brave*", filter);
        Assert.DoesNotContain(@"Mui's", filter);
    }

    [Fact]
    public void EscapeLikePattern_NgoacVuong_DuocEscape_ViLaKyTuDaiDienCuaLike()
    {
        // '[' hợp lệ trong tên thư mục Windows nhưng MỞ character class của -like → lọt vào là mẫu lọc hỏng câm.
        // ']' đứng một mình (sau khi '[' đã bị escape) là ký tự thường nên KHÔNG cần escape.
        Assert.Equal(@"C:\hs\`[m]\30", BrowserProfileGuard.EscapeLikePattern(@"C:\hs\[m]\30"));
    }

    [Fact]
    public void EscapeLikePattern_DauHuyenNgangEscapeTruoc_KhongAnNhauVoiNgoacVuong()
    {
        // Phải nhân đôi '`' TRƯỚC khi thêm '`' cho '[', kẻo dấu '`' vốn có trong tên lại nuốt ký tự đứng sau.
        Assert.Equal("a``b`[c", BrowserProfileGuard.EscapeLikePattern("a`b[c"));
    }

    [Theory]
    [InlineData("killed=3;conlai=0", 3, 0)]
    [InlineData("  killed=0;conlai=2  ", 0, 2)]
    public void DocKetQuaDon_DungDinhDang_TachDuocSo(string stdout, int giet, int con)
    {
        Assert.Equal((giet, con), BrowserProfileGuard.DocKetQuaDon(stdout));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Get-CimInstance : Access denied")]
    [InlineData("killed=x;conlai=y")]
    public void DocKetQuaDon_SaiDinhDang_TraNull(string? stdout)
    {
        Assert.Equal((null, null), BrowserProfileGuard.DocKetQuaDon(stdout));
    }

    [Fact]
    public void BaoCaoKetQuaDon_HoSoVonDaRanh_ThiImLang()
    {
        // Vòng chạy 24/7, mỗi vòng dọn 3 lần — báo "không có gì để dọn" là rác nhật ký.
        var dong = new List<string>();
        BrowserProfileGuard.BaoCaoKetQuaDon("killed=0;conlai=0", dong.Add);
        Assert.Empty(dong);
    }

    [Fact]
    public void BaoCaoKetQuaDon_CoGietCuaSo_ThiBao()
    {
        var dong = new List<string>();
        BrowserProfileGuard.BaoCaoKetQuaDon("killed=2;conlai=0", dong.Add);
        Assert.Single(dong);
        Assert.Contains("2", dong[0]);
    }

    [Fact]
    public void BaoCaoKetQuaDon_ConSotSauKhiDon_ThiKeu()
    {
        // Đây là tín hiệu "bước dọn THẤT BẠI" — im lặng ở đây là lặp lại đúng cái mù đã đẻ ra lớp này.
        var dong = new List<string>();
        BrowserProfileGuard.BaoCaoKetQuaDon("killed=1;conlai=1", dong.Add);
        Assert.Single(dong);
        Assert.Contains("⚠", dong[0]);
    }

    [Fact]
    public void BaoCaoKetQuaDon_KhongDocDuocKetQua_ThiKeu()
    {
        var dong = new List<string>();
        BrowserProfileGuard.BaoCaoKetQuaDon("Access denied", dong.Add);
        Assert.Single(dong);
        Assert.Contains("⚠", dong[0]);
    }

    [Fact]
    public void ClearProfileSessionAndLocks_XoaSessionVaSingleton_GiuCookies()
    {
        var root = Path.Combine(Path.GetTempPath(), "hsguard-" + Guid.NewGuid().ToString("N"));
        var def = Path.Combine(root, "Default");
        Directory.CreateDirectory(Path.Combine(def, "Sessions"));
        foreach (var f in new[] { "Current Session", "Current Tabs", "Last Session", "Last Tabs", "Cookies" })
        {
            File.WriteAllText(Path.Combine(def, f), "x");
        }
        File.WriteAllText(Path.Combine(def, "Sessions", "Session_1"), "x");
        foreach (var s in new[] { "SingletonLock", "SingletonCookie", "SingletonSocket" })
        {
            File.WriteAllText(Path.Combine(root, s), "x");
        }

        try
        {
            BrowserProfileGuard.ClearProfileSessionAndLocks(root);

            foreach (var f in new[] { "Current Session", "Current Tabs", "Last Session", "Last Tabs" })
            {
                Assert.False(File.Exists(Path.Combine(def, f)), f + " phải bị xóa");
            }
            Assert.False(Directory.Exists(Path.Combine(def, "Sessions")), "thư mục Sessions phải bị xóa");
            foreach (var s in new[] { "SingletonLock", "SingletonCookie", "SingletonSocket" })
            {
                Assert.False(File.Exists(Path.Combine(root, s)), s + " phải bị xóa");
            }
            // Xóa Cookies = mất đăng nhập ⇒ vòng sau phải login lại từ đầu (và ăn thêm mã verify).
            Assert.True(File.Exists(Path.Combine(def, "Cookies")), "Cookies phải được GIỮ");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* bỏ qua */ }
        }
    }

    [Fact]
    public void ClearProfileSessionAndLocks_HoSoRongHoacKhongTonTai_KhongNem()
    {
        BrowserProfileGuard.ClearProfileSessionAndLocks(string.Empty);
        BrowserProfileGuard.ClearProfileSessionAndLocks("   ");
        BrowserProfileGuard.ClearProfileSessionAndLocks(
            Path.Combine(Path.GetTempPath(), "khong-ton-tai-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void FreeProfile_ChayTHATTrenWindows_HoSoKhongAiGiu_ThiImLang()
    {
        // Test TÍCH HỢP có chủ đích: chạy đúng chuỗi lệnh PowerShell thật. Hồ sơ không tồn tại nên không giết ai
        // → script phải in "killed=0;conlai=0" → BaoCaoKetQuaDon im lặng. Nếu chuỗi lệnh hỏng (sai nháy, sai cú
        // pháp, PS không chạy được) thì không đọc được kết quả ⇒ có dòng ⚠ ⇒ test đỏ. Đường dẫn cố tình có
        // khoảng trắng + nháy đơn + ngoặc vuông để soi luôn phần escape.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dong = new List<string>();
        var hoSo = Path.Combine(Path.GetTempPath(), "hs guard's [x]-" + Guid.NewGuid().ToString("N"));

        BrowserProfileGuard.FreeProfile(hoSo, alsoMatchBridgeExtension: false, dong.Add);

        Assert.Empty(dong);
    }

    /// <summary>Bước dọn hồ sơ PHẢI xoá luôn model AI on-device Chrome tự tải về gốc hồ sơ (3,98 GB/hồ sơ, đo
    /// 07/08/2026) — chốt phần NỐI DÂY: chỉ có hàm dọn mà không ai gọi thì ổ vẫn đầy. Chạy Windows-only vì
    /// FreeProfile gọi PowerShell (bước kill), phần xoá thư mục thì thuần BCL.</summary>
    [Fact]
    public void FreeProfile_XoaLuonModelAiOnDevice_VaBaoNhatKy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dong = new List<string>();
        // Đường dẫn phải có segment "profiles" như hồ sơ THẬT (BrowserProfilePaths.ForAccount) — bước dọn có
        // sanity check chặn mọi đường dẫn ngoài 'profiles'.
        var goc = Path.Combine(Path.GetTempPath(), "hs-optguide-" + Guid.NewGuid().ToString("N"));
        var hoSo = Path.Combine(goc, "profiles", "12-chrome");
        var model = Path.Combine(hoSo, "OptGuideOnDeviceModel", "2025.8.8.1141");
        try
        {
            Directory.CreateDirectory(model);
            File.WriteAllBytes(Path.Combine(model, "weights.bin"), new byte[2 * 1024 * 1024]);

            BrowserProfileGuard.FreeProfile(hoSo, alsoMatchBridgeExtension: false, dong.Add);

            Assert.False(Directory.Exists(Path.Combine(hoSo, "OptGuideOnDeviceModel")));
            Assert.Contains(dong, d => d.Contains("model AI on-device"));
        }
        finally
        {
            try { Directory.Delete(goc, true); } catch { /* bỏ qua */ }
        }
    }

    [Fact]
    public void MoTaThoatSom_NeuRoTenTrinhDuyet_MaThoat_VaHoSo()
    {
        var msg = LoginBrowserBootstrap.MoTaThoatSom(@"C:\Program Files\Google\Chrome\Application\chrome.exe",
            Profile, exitCode: 0, lan: 2);

        Assert.Contains("chrome.exe", msg);
        Assert.Contains("mã thoát 0", msg);
        Assert.Contains(Profile, msg);
        // Lần thử thứ mấy: đọc log là biết bước dọn + thử-lại đã chạy hay chưa (plan §3 bước 3).
        Assert.Contains("lần thử 2/2", msg);
    }

    [Fact]
    public void MoTaThoatSom_KhongDocDuocMaThoat_ThiGhiDauHoi()
    {
        var msg = LoginBrowserBootstrap.MoTaThoatSom(@"C:\brave.exe", Profile, exitCode: null, lan: 1);

        Assert.Contains("mã thoát ?", msg);
        Assert.Contains("lần thử 1/2", msg);
    }
}
