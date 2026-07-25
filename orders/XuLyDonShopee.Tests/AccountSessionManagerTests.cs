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

        public void MarkQueued()
        {
            // Giống AccountSession: đang hoạt động (Opening/Running/Stopping) → không hạ về hàng đợi.
            if (State is SessionState.Opening or SessionState.Running or SessionState.Stopping)
            {
                return;
            }
            State = SessionState.Queued;
            Changed?.Invoke();
        }

        public Task<bool> ProcessOrdersAsync() => Task.FromResult(false);

        public Task<bool> CheckOrdersAsync() => Task.FromResult(false);

        public Task<bool> SyncOrdersAsync() => Task.FromResult(false);

        public Task<bool> RedownloadSlipAsync(string orderSn) => Task.FromResult(false);

        public Task<bool> SyncFullAsync() => Task.FromResult(false);

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

    // ===================== Hàng đợi 1-phiên-cầu-nối-một-lúc (Lỗi 2) =====================

    [Fact]
    public void Start_MotAccount_ChayNgay_KhongDoiHanhVi()
    {
        var mgr = new AccountSessionManager(id => new StubSession(id));

        var s = (StubSession)mgr.Start(1);

        Assert.Equal(SessionState.Running, s.State);
        Assert.Equal(1, s.StartCalls);         // chạy ngay khi chỉ 1 account (không đổi hành vi cũ)
    }

    [Fact]
    public void Start_PhienThu2_KhiPhien1DangChay_VaoHangCho_KhongLaunch()
    {
        var mgr = new AccountSessionManager(id => new StubSession(id));

        var s1 = (StubSession)mgr.Start(1);
        var s2 = (StubSession)mgr.Start(2);

        Assert.Equal(SessionState.Running, s1.State);
        Assert.Equal(1, s1.StartCalls);
        Assert.Equal(SessionState.Queued, s2.State); // "Chờ đến lượt"
        Assert.Equal(0, s2.StartCalls);              // CHƯA mở trình duyệt (không bind cổng 47821 lần 2)
    }

    [Fact]
    public void Phien1DungHan_TuStartPhienKeTrongHang()
    {
        var mgr = new AccountSessionManager(id => new StubSession(id));

        var s1 = (StubSession)mgr.Start(1);
        var s2 = (StubSession)mgr.Start(2); // queued
        Assert.Equal(0, s2.StartCalls);

        mgr.Stop(1); // s1 → Stopped → OnSessionChanged: slot trống → dequeue s2 → StartAsync

        Assert.Equal(1, s2.StartCalls);
        Assert.Equal(SessionState.Running, s2.State);
    }

    [Fact]
    public void HangDoi_FIFO_TheoThuTuBam()
    {
        var mgr = new AccountSessionManager(id => new StubSession(id));

        var s1 = (StubSession)mgr.Start(1); // chạy
        var s2 = (StubSession)mgr.Start(2); // hàng: [2]
        var s3 = (StubSession)mgr.Start(3); // hàng: [2,3]
        Assert.Equal(SessionState.Queued, s2.State);
        Assert.Equal(SessionState.Queued, s3.State);

        mgr.Stop(1);
        Assert.Equal(SessionState.Running, s2.State); // 2 chạy trước (vào hàng trước)
        Assert.Equal(SessionState.Queued, s3.State);
        Assert.Equal(0, s3.StartCalls);

        mgr.Stop(2);
        Assert.Equal(SessionState.Running, s3.State); // rồi tới 3
    }

    [Fact]
    public void Stop_AccountDangXepHang_RutKhoiHang_KhongBaoGioLaunch()
    {
        var mgr = new AccountSessionManager(id => new StubSession(id));

        var s1 = (StubSession)mgr.Start(1); // chạy
        var s2 = (StubSession)mgr.Start(2); // queued
        Assert.Equal(SessionState.Queued, s2.State);

        mgr.Stop(2); // rút 2 khỏi hàng (chưa từng launch)

        Assert.Equal(0, s2.StartCalls);
        Assert.Equal(SessionState.Stopped, s2.State);
        Assert.Null(mgr.Get(2)); // đã gỡ khỏi dict (OnSessionChanged thấy Stopped)

        // s1 dừng → không còn ai trong hàng để start.
        mgr.Stop(1);
        Assert.Empty(mgr.Active);
    }

    [Fact]
    public void Start_LapLaiAccountDangCho_KhongNhanDoiHang()
    {
        var mgr = new AccountSessionManager(id => new StubSession(id));

        mgr.Start(1);                        // chạy
        var s2 = (StubSession)mgr.Start(2);  // queued
        mgr.Start(2);                        // bấm lại — vẫn queued, không nhân đôi
        Assert.Equal(SessionState.Queued, s2.State);
        Assert.Equal(0, s2.StartCalls);

        mgr.Stop(1);
        // Chỉ start MỘT lần (hàng không nhân đôi id 2).
        Assert.Equal(1, s2.StartCalls);
    }
}
