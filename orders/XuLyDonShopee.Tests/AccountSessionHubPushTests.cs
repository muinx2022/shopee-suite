using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Models;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Test hàm thuần <see cref="AccountSession.PushPendingToHubAsync"/> — lõi chia LÔ + đánh dấu của việc đẩy đơn
/// lên hub (tách khỏi <see cref="AccountSession"/> để test được, không cần Brave/Playwright thật; luồng nền +
/// hook thật kiểm ở smoke như các phần browser khác của dự án). Kiểm: hook nhận đúng lô ≤ batchSize theo thứ tự;
/// hook trả false → KHÔNG đánh dấu + dừng các lô sau; hook null / pending rỗng → không nổ, không đánh dấu; hủy → ném.
/// </summary>
public class AccountSessionHubPushTests
{
    private static List<SyncedOrder> MakeOrders(int n)
        => Enumerable.Range(1, n).Select(i => new SyncedOrder { OrderSn = "SN" + i }).ToList();

    [Fact]
    public async Task ChiaLo_HookNhanDungLo_TheoThuTu_DanhDauTatCa()
    {
        var pending = MakeOrders(250);
        var pushedBatches = new List<List<string>>();
        var markedBatches = new List<List<string>>();
        long? seenAccountId = null;

        var marked = await AccountSession.PushPendingToHubAsync(
            accountId: 42,
            pending: pending,
            push: (accId, batch, ct) =>
            {
                seenAccountId = accId;
                pushedBatches.Add(batch.Select(o => o.OrderSn).ToList());
                return Task.FromResult(true);
            },
            markSynced: sns => markedBatches.Add(sns.ToList()),
            batchSize: 200,
            ct: CancellationToken.None);

        Assert.Equal(250, marked);
        Assert.Equal(42, seenAccountId);                       // accountId truyền xuyên xuống hook
        Assert.Equal(new[] { 200, 50 }, pushedBatches.Select(b => b.Count)); // 2 lô: 200 + 50
        // Đánh dấu ĐÚNG các mã đơn của từng lô đã đẩy OK.
        Assert.Equal(new[] { 200, 50 }, markedBatches.Select(b => b.Count));
        Assert.Equal(pending.Select(o => o.OrderSn), markedBatches.SelectMany(b => b)); // đủ + đúng thứ tự
    }

    [Fact]
    public async Task LoNhoHonBatch_MotLoDuyNhat()
    {
        var pending = MakeOrders(3);
        var pushCount = 0;

        var marked = await AccountSession.PushPendingToHubAsync(
            accountId: 1,
            pending: pending,
            push: (accId, batch, ct) => { pushCount++; return Task.FromResult(true); },
            markSynced: _ => { },
            batchSize: 200,
            ct: CancellationToken.None);

        Assert.Equal(3, marked);
        Assert.Equal(1, pushCount); // 3 ≤ 200 → chỉ 1 lô
    }

    [Fact]
    public async Task HookTraFalse_KhongDanhDau_DungLuon()
    {
        var pending = MakeOrders(10);
        var markedAny = false;

        var marked = await AccountSession.PushPendingToHubAsync(
            accountId: 1,
            pending: pending,
            push: (accId, batch, ct) => Task.FromResult(false), // hub offline
            markSynced: _ => markedAny = true,
            batchSize: 200,
            ct: CancellationToken.None);

        Assert.Equal(0, marked);
        Assert.False(markedAny); // hook trả false → KHÔNG đánh dấu (đơn giữ để đẩy lại lượt sau)
    }

    [Fact]
    public async Task HookTraFalseLoThuHai_GiuLoDauDaDanhDau_DungLoSau()
    {
        var pending = MakeOrders(500); // 3 lô: 200 + 200 + 100
        var pushedBatches = new List<int>();
        var markedTotal = 0;

        var marked = await AccountSession.PushPendingToHubAsync(
            accountId: 1,
            pending: pending,
            push: (accId, batch, ct) =>
            {
                pushedBatches.Add(batch.Count);
                return Task.FromResult(pushedBatches.Count == 1); // lô 1 OK, lô 2 fail
            },
            markSynced: sns => markedTotal += sns.Count,
            batchSize: 200,
            ct: CancellationToken.None);

        Assert.Equal(200, marked);                     // chỉ lô 1 được đánh dấu
        Assert.Equal(200, markedTotal);
        Assert.Equal(new[] { 200, 200 }, pushedBatches); // đẩy lô 1 (OK) + lô 2 (fail) rồi DỪNG — KHÔNG đụng lô 3
    }

    [Fact]
    public async Task HookNull_KhongNo_KhongDanhDau()
    {
        var pending = MakeOrders(5);
        var markedAny = false;

        var marked = await AccountSession.PushPendingToHubAsync(
            accountId: 1,
            pending: pending,
            push: null,                       // hub tắt / hook chưa rót
            markSynced: _ => markedAny = true,
            batchSize: 200,
            ct: CancellationToken.None);

        Assert.Equal(0, marked);
        Assert.False(markedAny);
    }

    [Fact]
    public async Task PendingRong_HookKhongGoi_TraVe0()
    {
        var pushCalled = false;

        var marked = await AccountSession.PushPendingToHubAsync(
            accountId: 1,
            pending: new List<SyncedOrder>(),
            push: (accId, batch, ct) => { pushCalled = true; return Task.FromResult(true); },
            markSynced: _ => { },
            batchSize: 200,
            ct: CancellationToken.None);

        Assert.Equal(0, marked);
        Assert.False(pushCalled);
    }

    [Fact]
    public async Task HuyToken_NemOperationCanceled()
    {
        var pending = MakeOrders(10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AccountSession.PushPendingToHubAsync(
                accountId: 1,
                pending: pending,
                push: (accId, batch, ct) => Task.FromResult(true),
                markSynced: _ => { },
                batchSize: 200,
                ct: cts.Token));
    }
}
