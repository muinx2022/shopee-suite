using System;
using System.Diagnostics;
using System.Threading.Tasks;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.App.ViewModels;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test phần thuần của <see cref="AccountRowViewModel"/>: <see cref="AccountRowViewModel.SyncFromSession"/>
/// đổ trạng thái phiên vào dòng (RunState / IsRunning / "Chờ lấy: N"). Dùng stub <see cref="IAccountSession"/>
/// (không cần Brave).
/// </summary>
public class AccountRowViewModelTests
{
    /// <summary>Stub phiên chỉ trả về State + ToShipCount cố định (đủ cho SyncFromSession).</summary>
    private sealed class StubSession : IAccountSession
    {
        public StubSession(SessionState state, int? toShip)
        {
            State = state;
            ToShipCount = toShip;
        }

        public long AccountId => 1;
        public SessionState State { get; }
        public string? StatusText => null;
        public int? ToShipCount { get; }
        public bool ReadyForActions => false; // không dùng trong test SyncFromSession
        public string? LastError => null;
        public Process? BraveProcess => null;

        public event Action? Changed;
        public event Action<long>? CookieSaved;

        public Task StartAsync()
        {
            Changed?.Invoke();
            CookieSaved?.Invoke(AccountId);
            return Task.CompletedTask;
        }

        public Task StopAsync() => Task.CompletedTask;

        public Task<bool> ProcessOrdersAsync() => Task.FromResult(false);

        public Task<bool> CheckOrdersAsync() => Task.FromResult(false);

        public Task<bool> SyncOrdersAsync() => Task.FromResult(false);

        public Task<bool> SyncFullAsync() => Task.FromResult(false);
    }

    private static AccountRowViewModel NewRow()
        => new(new Account { Id = 1, Email = "a@mail.com", Password = "p" });

    // ===== Không có phiên (null) → không chạy, không có dòng "Chờ lấy" =====
    [Fact]
    public void SyncFromSession_Null_KhongChay_KhongToShip()
    {
        var row = NewRow();

        row.SyncFromSession(null);

        Assert.Null(row.RunState);
        Assert.False(row.IsRunning);
        Assert.Null(row.ToShipText);
    }

    // ===== Đang chạy + đọc được số đơn → "Chờ lấy: 3" =====
    [Fact]
    public void SyncFromSession_RunningCoSoDon_HienChoLay()
    {
        var row = NewRow();

        row.SyncFromSession(new StubSession(SessionState.Running, 3));

        Assert.Equal(SessionState.Running, row.RunState);
        Assert.True(row.IsRunning);
        Assert.Equal("Chờ lấy: 3", row.ToShipText);
    }

    // ===== Đang chạy nhưng CHƯA đọc được số → không hiện dòng "Chờ lấy" =====
    [Fact]
    public void SyncFromSession_RunningChuaCoSo_ToShipNull()
    {
        var row = NewRow();

        row.SyncFromSession(new StubSession(SessionState.Running, null));

        Assert.True(row.IsRunning);
        Assert.Null(row.ToShipText);
    }

    // ===== Đang chuẩn bị (Opening) cũng coi là đang chạy; có số thì vẫn hiện "Chờ lấy: N" =====
    [Fact]
    public void SyncFromSession_Opening_DangChay_CoSoVanHien()
    {
        var row = NewRow();

        row.SyncFromSession(new StubSession(SessionState.Opening, 5));

        Assert.Equal(SessionState.Opening, row.RunState);
        Assert.True(row.IsRunning);
        Assert.Equal("Chờ lấy: 5", row.ToShipText);
    }

    // ===== Đã dừng → không chạy, xóa dòng "Chờ lấy" =====
    [Fact]
    public void SyncFromSession_Stopped_KhongChay()
    {
        var row = NewRow();
        row.SyncFromSession(new StubSession(SessionState.Running, 3)); // đang chạy trước

        row.SyncFromSession(new StubSession(SessionState.Stopped, 3)); // rồi dừng

        Assert.Equal(SessionState.Stopped, row.RunState);
        Assert.False(row.IsRunning);
        Assert.Null(row.ToShipText);
    }

    // ===== Lỗi (Error) không phải "đang chạy" =====
    [Fact]
    public void SyncFromSession_Error_KhongChay()
    {
        var row = NewRow();

        row.SyncFromSession(new StubSession(SessionState.Error, null));

        Assert.Equal(SessionState.Error, row.RunState);
        Assert.False(row.IsRunning);
        Assert.Null(row.ToShipText);
    }

    // ===== IsRunning phát PropertyChanged khi RunState đổi (để UI cập nhật chấm chạy) =====
    [Fact]
    public void RunStateDoi_PhatIsRunningChanged()
    {
        var row = NewRow();
        var isRunningRaised = false;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AccountRowViewModel.IsRunning))
            {
                isRunningRaised = true;
            }
        };

        row.SyncFromSession(new StubSession(SessionState.Running, null));

        Assert.True(isRunningRaised);
    }
}
