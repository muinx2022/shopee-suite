using System;
using System.IO;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test cho <see cref="ProfileJanitor.TryResetDirectory"/> — hàm thuần BCL nên chạy trực tiếp trên thư mục tạm.
/// Mọi thư mục tạm đều nằm dưới một segment "profiles" để qua được sanity check (thao tác phá hủy).
/// </summary>
public class ProfileJanitorTests
{
    /// <summary>Cấp một thư mục tạm dạng &lt;tmp&gt;/xlds_pj_&lt;guid&gt;/profiles/&lt;name&gt; (tự dọn khi Dispose).</summary>
    private sealed class TempProfileDir : IDisposable
    {
        public string Root { get; }
        public string Dir { get; }

        public TempProfileDir(string name = "12-chrome")
        {
            Root = Path.Combine(Path.GetTempPath(), $"xlds_pj_{Guid.NewGuid():N}");
            Dir = Path.Combine(Root, "profiles", name);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // Bỏ qua lỗi dọn thư mục tạm.
            }
        }
    }

    [Fact]
    public void TryResetDirectory_ThuMucCoFileCon_ResetXongRongVaTonTai()
    {
        using var temp = new TempProfileDir();
        Directory.CreateDirectory(temp.Dir);
        File.WriteAllText(Path.Combine(temp.Dir, "cookie.txt"), "abc");
        Directory.CreateDirectory(Path.Combine(temp.Dir, "Cache"));
        File.WriteAllText(Path.Combine(temp.Dir, "Cache", "x.bin"), "y");

        var ok = ProfileJanitor.TryResetDirectory(temp.Dir);

        Assert.True(ok);
        Assert.True(Directory.Exists(temp.Dir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Dir)); // đã sạch
    }

    [Fact]
    public void TryResetDirectory_ThuMucChuaTonTai_TaoMoiTraTrue()
    {
        using var temp = new TempProfileDir();
        Assert.False(Directory.Exists(temp.Dir));

        var ok = ProfileJanitor.TryResetDirectory(temp.Dir);

        Assert.True(ok);
        Assert.True(Directory.Exists(temp.Dir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Dir));
    }

    [Fact]
    public void TryResetDirectory_FileDangKhoa_TraFalse_KhongNem()
    {
        using var temp = new TempProfileDir();
        Directory.CreateDirectory(temp.Dir);
        var locked = Path.Combine(temp.Dir, "LOCK");

        // Giữ khóa độc quyền (FileShare.None) → Directory.Delete recursive không xóa nổi → retry rồi trả false.
        using var fs = new FileStream(locked, FileMode.Create, FileAccess.Write, FileShare.None);

        var ex = Record.Exception(() =>
        {
            var ok = ProfileJanitor.TryResetDirectory(temp.Dir, attempts: 2, delayMs: 10);
            Assert.False(ok);
        });

        Assert.Null(ex); // không ném dù bị khóa
        Assert.True(File.Exists(locked)); // file khóa vẫn còn (không bị xóa)
    }

    [Fact]
    public void TryResetDirectory_DuongDanKhongCoSegmentProfiles_TraFalse_KhongXoa()
    {
        // Thư mục KHÔNG nằm dưới "profiles" → sanity check chặn, KHÔNG đụng đĩa.
        var root = Path.Combine(Path.GetTempPath(), $"xlds_pj_{Guid.NewGuid():N}");
        var dir = Path.Combine(root, "data", "12-chrome");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "keep.txt"), "quan trọng");

            var ok = ProfileJanitor.TryResetDirectory(dir);

            Assert.False(ok);
            Assert.True(File.Exists(Path.Combine(dir, "keep.txt"))); // KHÔNG bị xóa
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResetDirectory_DuongDanRong_TraFalse(string dir)
    {
        Assert.False(ProfileJanitor.TryResetDirectory(dir));
    }

    /// <summary>Chrome tự tải model AI on-device về GỐC hồ sơ — 3,98 GB/hồ sơ (đo 07/08/2026). Bước dọn phải xoá
    /// ĐÚNG thư mục đó và KHÔNG đụng gì khác trong hồ sơ (nhất là cookie: mất là phải đăng nhập lại).</summary>
    [Fact]
    public void XoaModelAiOnDevice_XoaDungThuMucModel_GiuNguyenPhanConLai()
    {
        using var temp = new TempProfileDir();
        var model = Path.Combine(temp.Dir, "OptGuideOnDeviceModel", "2025.8.8.1141");
        Directory.CreateDirectory(model);
        File.WriteAllBytes(Path.Combine(model, "weights.bin"), new byte[2048]);
        var cookies = Path.Combine(temp.Dir, "Default", "Network");
        Directory.CreateDirectory(cookies);
        File.WriteAllText(Path.Combine(cookies, "Cookies"), "giu-nguyen");

        var freed = ProfileJanitor.XoaModelAiOnDevice(temp.Dir);

        Assert.False(Directory.Exists(Path.Combine(temp.Dir, "OptGuideOnDeviceModel")));
        Assert.True(freed >= 2048, $"phải tính được dung lượng đã giải phóng, nhận {freed}");
        Assert.Equal("giu-nguyen", File.ReadAllText(Path.Combine(cookies, "Cookies")));
    }

    [Fact]
    public void XoaModelAiOnDevice_KhongCoThuMucModel_TraKhongVaKhongNem()
    {
        using var temp = new TempProfileDir();
        Directory.CreateDirectory(temp.Dir);

        Assert.Equal(0, ProfileJanitor.XoaModelAiOnDevice(temp.Dir));
        Assert.Equal(0, ProfileJanitor.XoaModelAiOnDevice(string.Empty));
        Assert.True(Directory.Exists(temp.Dir));
    }

    /// <summary>Sanity check phòng thủ giống <c>TryResetDirectory</c>: đường dẫn KHÔNG có segment "profiles" thì
    /// tuyệt đối không đụng đĩa. Hồ sơ thật luôn là &lt;baseDir&gt;/profiles/&lt;id&gt;-&lt;kind&gt; nên luật này không cắt mất
    /// ca hợp lệ nào — nó chặn đường dẫn rỗng/tương đối/trỏ nhầm xoá đệ quy nhầm chỗ.</summary>
    [Fact]
    public void XoaModelAiOnDevice_DuongDanNgoaiProfiles_KhongXoaGi()
    {
        var goc = Path.Combine(Path.GetTempPath(), $"xlds_noprof_{Guid.NewGuid():N}");
        var model = Path.Combine(goc, "OptGuideOnDeviceModel", "2025.8.8.1141");
        try
        {
            Directory.CreateDirectory(model);
            File.WriteAllBytes(Path.Combine(model, "weights.bin"), new byte[2048]);

            Assert.Equal(0, ProfileJanitor.XoaModelAiOnDevice(goc));

            Assert.True(Directory.Exists(model), "đường dẫn ngoài 'profiles' phải được bỏ qua, không xoá");
        }
        finally
        {
            try { Directory.Delete(goc, recursive: true); } catch { /* bỏ qua */ }
        }
    }
}
