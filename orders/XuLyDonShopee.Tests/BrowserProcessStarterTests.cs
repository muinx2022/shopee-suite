using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

public class BrowserProcessStarterTests
{
    [Fact]
    public void JoinArguments_Rong_TraChuoiRong()
    {
        Assert.Equal(string.Empty, BrowserProcessStarter.JoinArguments(Array.Empty<string>()));
        Assert.Equal(string.Empty, BrowserProcessStarter.JoinArguments(new List<string>()));
    }

    [Fact]
    public void JoinArguments_TokenThuong_KhongQuote()
    {
        var joined = BrowserProcessStarter.JoinArguments(new[]
        {
            "--profile-directory=Default",
            "--new-window",
            "--lang=vi-VN",
        });
        Assert.Equal("--profile-directory=Default --new-window --lang=vi-VN", joined);
    }

    [Fact]
    public void JoinArguments_PathCoKhoangTrang_DuocQuote()
    {
        var dir = @"C:\Users\Ng Xuan Mui\AppData\Roaming\XuLyDonShopee\profiles\30-brave";
        var joined = BrowserProcessStarter.JoinArguments(new[]
        {
            $"--user-data-dir={dir}",
            "--profile-directory=Default",
        });
        Assert.Equal(
            $"\"--user-data-dir={dir}\" --profile-directory=Default",
            joined);
    }

    [Fact]
    public void JoinArguments_TokenCoNhayKep_Escape()
    {
        var joined = BrowserProcessStarter.JoinArguments(new[] { @"say ""hi""" });
        Assert.Equal("\"say \\\"hi\\\"\"", joined);
    }
}
