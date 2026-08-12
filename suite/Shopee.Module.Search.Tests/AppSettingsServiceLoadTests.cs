using ShopeeStatApp.Models;
using ShopeeStatApp.Services;

namespace Shopee.Module.Search.Tests;

/// <summary>
/// <c>settings.json</c> phải được ĐỌC ngay khi dựng service. Trước 2026-08-12 <c>SearchRunner</c> chỉ
/// <c>new AppSettingsService()</c> mà không <c>Load()</c>: cấu hình của user không những bị bỏ qua, mà lần
/// <c>SaveSettings()</c> đầu tiên trong lượt chạy (BraveManager/ShopeeLoginService gọi) còn ghi ĐÈ nguyên
/// file bằng object mặc định — chạy Search một lượt là mất sạch đường dẫn Brave, danh sách file, proxy…
/// </summary>
public sealed class AppSettingsServiceLoadTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ss-settings-tests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private void GhiFileCuaUser() =>
        File.WriteAllText(SettingsPath, """
            {
              "bravePath": "D:\\Brave\\brave.exe",
              "maxParallelLanes": 9,
              "lastFilePaths": [ "D:\\links\\a.xlsx" ]
            }
            """);

    [Fact]
    public void Load_DocDungFileDaCo()
    {
        Directory.CreateDirectory(_dir);
        GhiFileCuaUser();

        var svc = new AppSettingsService(SettingsPath);
        svc.Load();

        Assert.Equal("D:\\Brave\\brave.exe", svc.Settings.BravePath);
        Assert.Equal(9, svc.Settings.MaxParallelLanes);
        Assert.Equal(["D:\\links\\a.xlsx"], svc.Settings.LastFilePaths);
    }

    /// <summary>Đây mới là thiệt hại thật: KHÔNG Load rồi Save là xoá trắng cấu hình của user.</summary>
    [Fact]
    public void CoLoad_RoiSave_GiuNguyenCauHinhCuaUser()
    {
        Directory.CreateDirectory(_dir);
        GhiFileCuaUser();

        var svc = new AppSettingsService(SettingsPath);
        svc.Load();
        svc.SaveSettings();   // y như BraveManager/ShopeeLoginService ghi lại lúc chạy

        var doLai = new AppSettingsService(SettingsPath);
        doLai.Load();
        Assert.Equal("D:\\Brave\\brave.exe", doLai.Settings.BravePath);
        Assert.Equal(9, doLai.Settings.MaxParallelLanes);
    }

    [Fact]
    public void ChuaCoFile_VanChayDuocVoiCauHinhMacDinh()
    {
        var svc = new AppSettingsService(SettingsPath);
        svc.Load();

        Assert.Equal("", svc.Settings.BravePath);
        Assert.Equal(new LauncherSettings().MaxParallelLanes, svc.Settings.MaxParallelLanes);
    }

    /// <summary>File TỒN TẠI nhưng đọc/parse hỏng (đang bị ghi dở / AV khoá / JSON nát): Settings về mặc định
    /// để chạy tiếp, nhưng SaveSettings phải BỊ CẤM — ghi lúc này là đè object mặc định lên file của user,
    /// đúng cái lỗi "mất sạch cấu hình" mà cả nhóm test này canh.</summary>
    [Fact]
    public void FileHong_LoadVeMacDinh_NhungSaveBiCam()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ json nát");

        var svc = new AppSettingsService(SettingsPath);
        svc.Load();
        Assert.Equal("", svc.Settings.BravePath);   // chạy tiếp được với mặc định
        Assert.True(svc.LoadFailed);                // cờ phơi ra để UI cảnh báo

        svc.Settings.BravePath = "C:\\khac\\brave.exe";
        svc.SaveSettings();                          // phải bị từ chối im lặng

        Assert.Equal("{ json nát", File.ReadAllText(SettingsPath));   // file user còn nguyên
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));               // không để rác tmp
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
