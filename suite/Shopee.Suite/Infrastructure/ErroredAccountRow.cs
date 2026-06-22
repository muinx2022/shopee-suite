using CommunityToolkit.Mvvm.ComponentModel;

namespace Shopee.Suite.Infrastructure;

/// <summary>Một tài khoản bị lỗi khi chạy (captcha/verify, proxy hết hạn…) — hiển thị cho người dùng.</summary>
public sealed partial class ErroredAccountRow : ObservableObject
{
    public ErroredAccountRow(string id, string account, string reason, string time)
    {
        Id = id;
        Account = account;
        _reason = reason;
        _time = time;
    }

    public string Id { get; }
    public string Account { get; }
    [ObservableProperty] private string _reason;
    [ObservableProperty] private string _time;
}
