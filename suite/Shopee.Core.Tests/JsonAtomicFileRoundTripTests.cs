using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shopee.Core.Accounts;
using Shopee.Core.BigSeller;
using Shopee.Core.Infrastructure;
using Shopee.Core.Progress;

namespace Shopee.Core.Tests;

/// <summary>
/// Đối chiếu ĐỊNH DẠNG TRÊN ĐĨA của <see cref="JsonAtomicFile"/> với khuôn cũ tự-viết mà 13 store dùng
/// trước khi gom về helper. Cách kiểm: ghi "file mẫu" bằng ĐÚNG khuôn cũ → đọc lại qua helper → ghi lại
/// qua helper → hai file phải BẰNG NHAU TỪNG BYTE (kể cả BOM). Sai chỗ này là mọi file cấu hình trên máy
/// người dùng đổi byte / đọc không ra.
/// Dùng chính các kiểu thật của store; KHÔNG chạm singleton <c>*.Shared</c> (chúng trỏ vào %AppData% thật).
/// </summary>
public sealed class JsonAtomicFileRoundTripTests : IDisposable
{
    /// <summary>Options mà cả 13 store dùng để GHI.</summary>
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shopee-core-tests", Guid.NewGuid().ToString("N"));

    public JsonAtomicFileRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Khuôn GHI CŨ, chép nguyên từ các store trước refactor — mốc so sánh của mọi test dưới đây.</summary>
    private static void GhiKieuCu(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }

    private void BangByteVoiKhuonCu<T>(
        string ten, T value, JsonSerializerOptions writeOpts, JsonSerializerOptions? readOpts = null)
    {
        var mau = Path.Combine(_dir, ten + ".mau.json");
        GhiKieuCu(mau, JsonSerializer.Serialize(value, writeOpts));

        var doc = JsonAtomicFile.TryLoad<T>(mau, readOpts);
        Assert.NotNull(doc);

        var lai = Path.Combine(_dir, ten + ".lai.json");
        Assert.True(JsonAtomicFile.Save(lai, doc!, writeOpts));

        Assert.Equal(File.ReadAllBytes(mau), File.ReadAllBytes(lai));
    }

    [Fact]
    public void AccountStore_RoundTrip_BangByte()
    {
        var accounts = new List<ShopeeAccount>
        {
            new()
            {
                Id = "a1b2c3d4e5f60718293a4b5c6d7e8f90",
                Label = "Máy chị Bảy — tài khoản #1",
                ShopeeAccountLogin = "user01|mật khẩu có dấu|.shopee.vn=SPC_F=abc",
                KiotProxyKey = "kiot-key-01",
                ProfileRelativePath = "profiles/a1b2c3d4e5f60718293a4b5c6d7e8f90",
                LastUsedTick = 1234567890,
                LastError = "Lỗi: \"captcha\" ở trang chủ\nDòng 2",
                CaptchaUrl = "https://shopee.vn/verify/traffic?next=%2F",
                HubOwned = true,
            },
            new()
            {
                Id = "0000000000000000000000000000ffff",
                Disabled = true,
                LastError = null,
            },
        };

        BangByteVoiKhuonCu("accounts", accounts, Indented);
    }

    [Fact]
    public void BigSellerStore_RoundTrip_BangByte()
    {
        var accounts = new List<BigSellerAccount>
        {
            new()
            {
                Id = "bs000000000000000000000000000001",
                Label = "Shop Bảo Ngọc",
                Email = "a@b.com",
                Password = "p@ss word",
                WorkbookPath = @"C:\Users\Ng Xuan Mui\AppData\Roaming\ShopeeSuite\shared\sp.xlsx",
                CookieFile = @"C:\đường dẫn\cookie.json",
                DataSource = "hub",
                UpdateRunSelected = true,
                RunConfig = new BigSellerRunConfig { StartRow = 5, EndRow = 900, Processes = 3 },
                Shops =
                [
                    new BigSellerShop
                    {
                        Id = "shop0000000000000000000000000001",
                        Name = "Shop 1",
                        ShopeeDataSheet = "Sheet Ảnh",
                        ColumnMap = new BigSellerColumnMap { LinkColumn = 2, SkuColumn = 9 },
                    },
                ],
            },
            new() { Id = "bs000000000000000000000000000002" },
        };

        BangByteVoiKhuonCu("bigseller", accounts, Indented);
    }

    [Fact]
    public void OpProgressStore_RoundTrip_BangByte()
    {
        var items = new List<OpProgress>
        {
            new()
            {
                AccountId = "bs000000000000000000000000000001",
                Sheet = "Sheet Ảnh",
                Op = "update",
                AccountName = "Shop Bảo Ngọc",
                Status = "running",
                LastRunAt = new DateTimeOffset(2026, 7, 30, 21, 45, 12, TimeSpan.FromHours(7)),
                Done = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["27412345678"] = "Áo thun nam cổ tròn — hàng mới",
                    ["27498765432"] = null,
                },
            },
            new() { AccountId = "bs000000000000000000000000000002", Op = "import" },
        };

        BangByteVoiKhuonCu("op-progress", items, Indented);
    }

    /// <summary>Bản sao DTO của <c>AppModeStore</c> (kiểu thật là private) — store DUY NHẤT dùng options
    /// ĐỌC khác options GHI, nên phải chắc helper nhận đúng cả hai.</summary>
    private sealed class AppModeDto
    {
        [JsonPropertyName("mode")] public string? Mode { get; set; }
    }

    [Fact]
    public void AppModeStore_RoundTrip_BangByte_VoiOptionsDocRieng()
    {
        var readOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        BangByteVoiKhuonCu("app-mode", new AppModeDto { Mode = "Workspace" }, Indented, readOpts);
    }
}
