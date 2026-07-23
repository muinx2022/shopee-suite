using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test logic Start/Stop/StopAll/IsRunning của <see cref="AccountSessionManager"/> bằng stub
/// <see cref="IAccountSession"/> (không cần Brave/Playwright thật). Đây là phần logic thuần của engine
/// đa phiên; luồng browser thật được kiểm ở smoke test (như các phần browser khác của dự án).
/// </summary>
public class AccountSessionManagerTests
{
    /// <summary>Stub phiên: chỉ đổi State khi Start/Stop, đếm số lần gọi, phát Changed như phiên thật.</summary>
    private sealed class StubSession : IAccountSession
    {
        public long AccountId { get; }
        public SessionState State { get; private set; } = SessionState.Stopped;
        public string? StatusText => null;
        public int? ToShipCount => null;
        public bool ReadyForActions { get; set; } // stub cho phép set khi cần (mặc định false)
        public bool IsShopLoopRunning { get; set; } // stub cho phép set khi cần (mặc định false)
        public string? LastError => null;
        public Process? BraveProcess => null;

        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public event Action? Changed;
        public event Action<long>? CookieSaved;

        public StubSession(long id) => AccountId = id;

        public Task StartAsync()
        {
            StartCalls++;
            State = SessionState.Running;
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCalls++;
            State = SessionState.Stopped;
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public Task<bool> ProcessOrdersAsync() => Task.FromResult(false);

        public Task<bool> CheckOrdersAsync() => Task.FromResult(false);

        public Task<bool> SyncOrdersAsync() => Task.FromResult(false);

        public Task<bool> RedownloadSlipAsync(string orderSn) => Task.FromResult(false);

        public Task<bool> SyncFullAsync() => Task.FromResult(false);

        public Task<ShopeePageState?> ProbePageStateAsync() => Task.FromResult<ShopeePageState?>(null);

        /// <summary>Mô phỏng phiên phát lại sự kiện Changed (vd event Stopped TRỄ) mà không đổi State.</summary>
        public void RaiseChanged() => Changed?.Invoke();

        // Không dùng trong test nhưng cần để tránh cảnh báo "event không được dùng".
        internal void RaiseCookieSaved() => CookieSaved?.Invoke(AccountId);
    }

    [Fact]
    public void Start_HaiLanCungId_ChiMotSession_KhongMoTrung()
    {
        var factoryCalls = 0;
        var mgr = new AccountSessionManager(id => { factoryCalls++; return new StubSession(id); });

        var s1 = mgr.Start(5);
        var s2 = mgr.Start(5);

        Assert.Same(s1, s2);          // cùng một phiên, không tạo phiên thứ hai
        Assert.Equal(1, factoryCalls); // factory chỉ được gọi 1 lần cho id 5
        Assert.Single(mgr.Active);
    }

    [Fact]
    public void IsRunning_DungTheoTungTaiKhoan()
    {
        var mgr = new AccountSessionManager(id => new StubSession(id));

        Assert.False(mgr.IsRunning(1)); // chưa mở

        mgr.Start(1);

        Assert.True(mgr.IsRunning(1));
        Assert.False(mgr.IsRunning(2)); // mở tài khoản 1 KHÔNG khiến tài khoản 2 "đang chạy"
    }

    [Fact]
    public void Stop_GoKhoiActive_VaIsRunningFalse()
    {
        var mgr = new AccountSessionManager(id => new StubSession(id));
        mgr.Start(7);
        Assert.True(mgr.IsRunning(7));

        mgr.Stop(7);

        Assert.False(mgr.IsRunning(7));
        Assert.Empty(mgr.Active);
        Assert.Null(mgr.Get(7));
    }

    [Fact]
    public async Task StopAll_DungHetVaActiveRong()
    {
        var mgr = new AccountSessionManager(id => new StubSession(id));
        mgr.Start(1);
        mgr.Start(2);
        mgr.Start(3);
        Assert.Equal(3, mgr.Active.Count);

        await mgr.StopAllAsync();

        Assert.Empty(mgr.Active);
        Assert.False(mgr.IsRunning(1));
        Assert.False(mgr.IsRunning(2));
        Assert.False(mgr.IsRunning(3));
    }

    [Fact]
    public void Get_TraVePhienDangChay_HoacNull()
    {
        var mgr = new AccountSessionManager(id => new StubSession(id));

        Assert.Null(mgr.Get(9));

        var s = mgr.Start(9);
        Assert.Same(s, mgr.Get(9));
    }

    // ===== Lỗi 1 (concurrency): event Stopped TRỄ của phiên cũ KHÔNG được xóa nhầm phiên mới cùng id =====
    // Kịch bản: id 5 mở phiên A → Dừng (A bị gỡ) → Start lại 5 tạo phiên B đang chạy → event Stopped TRỄ
    // của A chạy sau. Gỡ theo KEY sẽ xóa nhầm B (B mồ côi); gỡ theo VALUE thì thấy dict[5]=B≠A → giữ B.
    [Fact]
    public void StoppedTre_CuaPhienCu_KhongXoaNhamPhienMoiCungId()
    {
        var mgr = new AccountSessionManager(id => new StubSession(id));

        var a = (StubSession)mgr.Start(5);   // phiên A
        mgr.Stop(5);                         // A.StopAsync → State=Stopped → OnSessionChanged gỡ A
        Assert.False(mgr.IsRunning(5));
        Assert.Null(mgr.Get(5));

        var b = (StubSession)mgr.Start(5);   // phiên MỚI B (khác instance), đang chạy
        Assert.NotSame(a, b);
        Assert.Same(b, mgr.Get(5));
        Assert.True(mgr.IsRunning(5));

        // Event Stopped TRỄ của A (A vẫn còn subscribe, State đang Stopped) chạy sau khi B đã vào dict.
        a.RaiseChanged();

        // B KHÔNG bị xóa nhầm.
        Assert.Same(b, mgr.Get(5));
        Assert.True(mgr.IsRunning(5));
    }

    // ===================== Bộ cấp phát KiotProxy key theo POOL (Acquire/Release) =====================

    /// <summary>Tạo manager stub với pool KiotProxy cố định (không cần Brave) để test cấp phát key.</summary>
    private static AccountSessionManager MakeMgrWithPool(params string[] pool)
        => new AccountSessionManager(id => new StubSession(id), () => pool);

    [Fact]
    public void AcquireKiotKey_PoolRong_TraNull()
    {
        // Ctor test không cấp pool → mặc định rỗng.
        var mgr = new AccountSessionManager(id => new StubSession(id));
        Assert.Null(mgr.AcquireKiotKey(1));

        // Pool rỗng tường minh cũng null.
        Assert.Null(MakeMgrWithPool().AcquireKiotKey(1));
    }

    [Fact]
    public void AcquireKiotKey_DuKey_ChiaDeuKeyRanhTruoc_MoiAccountKeyKhac()
    {
        var mgr = MakeMgrWithPool("k1", "k2", "k3");

        var a = mgr.AcquireKiotKey(1);
        var b = mgr.AcquireKiotKey(2);
        var c = mgr.AcquireKiotKey(3);

        // 3 account, 3 key → mỗi account nhận key RẢNH khác nhau (chia đều trước khi phải chia sẻ).
        Assert.Equal(new[] { "k1", "k2", "k3" }, new[] { a, b, c });
        Assert.Equal(3, new HashSet<string?> { a, b, c }.Count);
    }

    [Fact]
    public void AcquireKiotKey_AccountNhieuHonKey_ChiaSe_MoiKeyKhongVuotCeil()
    {
        var mgr = MakeMgrWithPool("k1", "k2");

        // 5 account, 2 key → chia sẻ luân phiên key ít bận nhất: k1,k2,k1,k2,k1.
        var got = Enumerable.Range(1, 5).Select(id => mgr.AcquireKiotKey(id)).ToList();

        Assert.Equal(new[] { "k1", "k2", "k1", "k2", "k1" }, got);
        // Không có lỗi khi hết key rảnh; mỗi key ≤ ceil(5/2) = 3 phiên.
        Assert.True(got.Count(k => k == "k1") <= 3);
        Assert.True(got.Count(k => k == "k2") <= 3);
    }

    [Fact]
    public void AcquireKiotKey_CungAccountGoiLai_TraCungKey_KhongReAcquire()
    {
        var mgr = MakeMgrWithPool("k1", "k2");

        var first = mgr.AcquireKiotKey(7);
        var again = mgr.AcquireKiotKey(7);

        // Idempotent theo phiên: account đã giữ key còn trong pool → cùng key (relaunch không đổi key).
        Assert.Equal(first, again);
    }

    [Fact]
    public void ReleaseKiotKey_KeyRanhLai_ChoLanAcquireSau()
    {
        var mgr = MakeMgrWithPool("k1", "k2");

        var a1 = mgr.AcquireKiotKey(1); // k1
        var a2 = mgr.AcquireKiotKey(2); // k2 (k1 đã bận)
        Assert.NotEqual(a1, a2);

        mgr.ReleaseKiotKey(1);          // nhả k1 → rảnh lại

        var a3 = mgr.AcquireKiotKey(3); // account mới nhận LẠI k1 (rảnh) chứ không phải k2 (đang bận)
        Assert.Equal(a1, a3);
    }
}
