using Shopee.Core.Coordination;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Luật SỐ LANE theo op — chốt hành vi để không ai vô tình mở lại Import thành nhiều lane.
/// <list type="bullet">
/// <item><b>BẤT BIẾN</b>: Import LUÔN 1 lane. Cấu hình client (RunConfig.Processes) lẫn ô "Số process" của
/// trang Giao việc trên Hub đều KHÔNG ghi đè được (lỗi 2026-07-27: hub giao import → client chạy nhiều worker,
/// đụng nhau ở Material Center + tab "Đã nhận").</item>
/// <item>scrape/update KHÔNG đổi: Hub đặt &gt; 0 thì thắng, else cấu hình client.</item>
/// </list>
/// Thuần hàm tĩnh — không cần hub, không cần Brave.
/// </summary>
public class OpLanesTests
{
    // ===== 1. Import: 1 lane, mọi đường đều không ghi đè được =====

    /// <summary>Hub không đặt (0) → 1 lane, KHÔNG rơi về cấu hình client (dù client để 4).</summary>
    [Fact]
    public void Import_HubKhongDat_VanMotLane()
    {
        Assert.Equal(1, OpLanes.RequiredBraves("import", assignedProcesses: 0, clientProcesses: 4));
    }

    /// <summary>Hub đặt "Số process" = 5 → VẪN 1 lane (ghi đè KHÔNG thắng) — đúng lỗi người dùng báo.</summary>
    [Fact]
    public void Import_HubDatNamProcess_VanMotLane()
    {
        Assert.Equal(1, OpLanes.RequiredBraves("import", assignedProcesses: 5, clientProcesses: 2));
        Assert.Equal(OpLanes.Import, OpLanes.RequiredBraves("import", 99, 99));
    }

    // ===== 2. Không hồi quy: update/scrape/rewrite/search giữ nguyên luật cũ =====

    /// <summary>Update, Hub không đặt → theo cấu hình client (RunConfig.Processes).</summary>
    [Fact]
    public void Update_HubKhongDat_TheoCauHinhClient()
    {
        Assert.Equal(2, OpLanes.RequiredBraves("update", assignedProcesses: 0, clientProcesses: 2));
        Assert.Equal(4, OpLanes.RequiredBraves("update", assignedProcesses: 0, clientProcesses: 4));
        // Hub đặt > 0 thì thắng cấu hình client; trần engine của update là 10.
        Assert.Equal(3, OpLanes.RequiredBraves("update", assignedProcesses: 3, clientProcesses: 2));
        Assert.Equal(10, OpLanes.RequiredBraves("update", assignedProcesses: 50, clientProcesses: 2));
    }

    /// <summary>Scrape giữ nguyên: Hub đặt 3 → 3 (trần 64, cao hơn update).</summary>
    [Fact]
    public void Scrape_TheoThamSoHub_TranSauMuoiBon()
    {
        Assert.Equal(3, OpLanes.RequiredBraves("scrape", assignedProcesses: 3, clientProcesses: 2));
        Assert.Equal(64, OpLanes.RequiredBraves("scrape", assignedProcesses: 100, clientProcesses: 2));
        Assert.Equal(1, OpLanes.RequiredBraves("scrape", assignedProcesses: 0, clientProcesses: 0));   // cấu hình rác → 1
    }

    /// <summary>rewrite/search KHÔNG mở trình duyệt → 0 (không trừ quỹ Brave).</summary>
    [Fact]
    public void RewriteVaSearch_KhongTonQuyBrave()
    {
        Assert.Equal(0, OpLanes.RequiredBraves("rewrite", 5, 5));
        Assert.Equal(0, OpLanes.RequiredBraves("search", 5, 5));
    }
}
