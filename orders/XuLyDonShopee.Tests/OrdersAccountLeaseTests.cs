using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.App.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// KHÓA CHẠY TÀI KHOẢN xuyên máy của module Đơn hàng (chống hai máy cùng chạy một subaccount → tranh đơn "chuẩn bị
/// hàng" + đăng nhập song song một tài khoản Shopee). Test phần PHÍA MODULE: quyết định chạy/bỏ qua theo kết quả
/// hook + vòng đời nhả khóa. Dùng hook STUB nên không cần hub, không cần trình duyệt.
/// <para>Phần chuẩn hóa khóa (<c>"orders:" + login</c> đã trim + hạ chữ) nằm ở <c>OrdersModuleHost</c> phía shell
/// suite — KHÔNG test được ở đây vì project test này chỉ tham chiếu module Đơn hàng.</para>
/// </summary>
public class OrdersAccountLeaseTests
{
    private const string Login = "alina99.store";

    /// <summary>Phiên thật (không mở trình duyệt — chỉ gọi 2 hàm khóa) + bộ dịch vụ trên SQLite tạm.</summary>
    private static (AppServices Services, AccountSession Session) NewSession(TempDatabase temp)
    {
        var services = new AppServices(temp.Path);
        return (services, new AccountSession(1, services));
    }

    // ===== 1. Câu báo bị từ chối: có tên máy thì nói TÊN, không biết máy nào thì "máy khác" =====
    [Fact]
    public void CauBiTuChoiKhoa_CoTenMay_NoiTenMay()
    {
        var msg = AccountSession.CauBiTuChoiKhoa("MAY-KHO-1");
        Assert.Contains("MAY-KHO-1", msg);
        Assert.Contains("bỏ qua lượt này", msg);
    }

    [Fact]
    public void CauBiTuChoiKhoa_KhongBietMayNao_NoiMayKhac()
    {
        var msg = AccountSession.CauBiTuChoiKhoa(null);
        Assert.Contains("máy khác", msg);

        // Hub trả chuỗi rỗng/trắng cũng phải rơi về "máy khác" (không dựng câu cụt "ở máy   —").
        Assert.Equal(msg, AccountSession.CauBiTuChoiKhoa("   "));
    }

    // ===== 2. Hook từ chối + có tên máy ⇒ KHÔNG chạy, log/StatusText mang tên máy =====
    [Fact]
    public async Task HookTuChoi_CoTenMay_KhongChay_LogCoTenMay()
    {
        using var temp = new TempDatabase();
        var (services, session) = NewSession(temp);
        var logs = new List<string>();
        services.AcquireAccountLease = (login, ct) => Task.FromResult(new OrdersLeaseResult(false, "MAY-KHO-1"));

        var duocChay = await session.XinKhoaChayAsync(Login, logs.Add, CancellationToken.None);

        Assert.False(duocChay);
        Assert.Contains(logs, m => m.Contains("MAY-KHO-1"));
        Assert.Contains("MAY-KHO-1", session.StatusText ?? "");
    }

    // ===== 3. Hook từ chối + không biết máy nào ⇒ câu log dạng "máy khác" =====
    [Fact]
    public async Task HookTuChoi_KhongBietMay_LogDangMayKhac()
    {
        using var temp = new TempDatabase();
        var (services, session) = NewSession(temp);
        var logs = new List<string>();
        services.AcquireAccountLease = (login, ct) => Task.FromResult(new OrdersLeaseResult(false, null));

        var duocChay = await session.XinKhoaChayAsync(Login, logs.Add, CancellationToken.None);

        Assert.False(duocChay);
        Assert.Contains(logs, m => m.Contains("máy khác"));
    }

    // ===== 4. Hook cấp khóa ⇒ chạy bình thường, hook nhận ĐÚNG login THÔ =====
    [Fact]
    public async Task HookCapKhoa_DuocChay_HookNhanLoginTho()
    {
        using var temp = new TempDatabase();
        var (services, session) = NewSession(temp);
        var logs = new List<string>();
        string? seenLogin = null;
        services.AcquireAccountLease = (login, ct) =>
        {
            seenLogin = login;
            return Task.FromResult(new OrdersLeaseResult(true, null));
        };

        var duocChay = await session.XinKhoaChayAsync("  Alina99.Store ", logs.Add, CancellationToken.None);

        Assert.True(duocChay);
        Assert.Equal("  Alina99.Store ", seenLogin); // module KHÔNG chuẩn hóa — quy ước khóa do phía suite lo
        Assert.Empty(logs);                          // được chạy → không làm bẩn nhật ký
    }

    // ===== 5. Hook chưa rót (app chạy độc lập / chưa có hub) ⇒ chạy bình thường, không ném =====
    [Fact]
    public async Task HookChuaRot_DuocChay_KhongNem()
    {
        using var temp = new TempDatabase();
        var (_, session) = NewSession(temp);
        var logs = new List<string>();

        var duocChay = await session.XinKhoaChayAsync(Login, logs.Add, CancellationToken.None);

        Assert.True(duocChay);
        Assert.Empty(logs);
    }

    // ===== 6a. Nhả khóa ĐÚNG MỘT lần dù đường dọn dẹp gọi nhiều lần =====
    [Fact]
    public async Task NhaKhoa_GoiDungMotLan()
    {
        using var temp = new TempDatabase();
        var (services, session) = NewSession(temp);
        var released = new List<string>();
        services.AcquireAccountLease = (login, ct) => Task.FromResult(new OrdersLeaseResult(true, null));
        services.ReleaseAccountLease = login => { released.Add(login); return Task.CompletedTask; };

        Assert.True(await session.XinKhoaChayAsync(Login, _ => { }, CancellationToken.None));
        await session.NhaKhoaChayAsync(Login);
        await session.NhaKhoaChayAsync(Login); // lối ra thứ hai (vd Stop chồng lên finally)

        Assert.Equal(new[] { Login }, released);
    }

    // ===== 6b. CHƯA từng giành được khóa (bị từ chối / hook acquire chưa rót) ⇒ KHÔNG nhả =====
    [Fact]
    public async Task ChuaGianhDuocKhoa_KhongNha()
    {
        using var temp = new TempDatabase();
        var (services, session) = NewSession(temp);
        var released = new List<string>();
        services.AcquireAccountLease = (login, ct) => Task.FromResult(new OrdersLeaseResult(false, "MAY-KHO-1"));
        services.ReleaseAccountLease = login => { released.Add(login); return Task.CompletedTask; };

        Assert.False(await session.XinKhoaChayAsync(Login, _ => { }, CancellationToken.None));
        await session.NhaKhoaChayAsync(Login);

        Assert.Empty(released); // nhả khóa của MÁY KHÁC = cướp lượt chạy của họ
    }

    [Fact]
    public async Task KhongXinKhoa_KhongNha()
    {
        using var temp = new TempDatabase();
        var (services, session) = NewSession(temp);
        var released = new List<string>();
        services.ReleaseAccountLease = login => { released.Add(login); return Task.CompletedTask; };

        await session.NhaKhoaChayAsync(Login); // vòng chạy thoát trước cả bước xin khóa

        Assert.Empty(released);
    }

    // ===== 7. Hook nhả lỗi ⇒ KHÔNG ném ngược vào đường dọn dẹp của phiên =====
    [Fact]
    public async Task HookNhaLoi_KhongNem()
    {
        using var temp = new TempDatabase();
        var (services, session) = NewSession(temp);
        services.AcquireAccountLease = (login, ct) => Task.FromResult(new OrdersLeaseResult(true, null));
        services.ReleaseAccountLease = login => throw new InvalidOperationException("hub sập");

        Assert.True(await session.XinKhoaChayAsync(Login, _ => { }, CancellationToken.None));
        await session.NhaKhoaChayAsync(Login); // không ném là đạt
    }
}
