namespace Shopee.Core.Coordination;

/// <summary>
/// Luật SỐ LANE (cửa sổ Brave chạy song song) theo loại việc. Dùng chung giữa nơi CHẠY THẬT
/// (<c>UpdateProductViewModel.BuildContext</c> dựng <c>UpdateProductContext</c>) và nơi TÍNH QUỸ Brave
/// (<c>AssignmentWorker.RequiredBraves</c>) — hai chỗ này lệch nhau là quỹ sai (đặt chỗ thừa/thiếu cửa sổ).
/// Thuần BCL để test LINK được (khuôn của <see cref="MachineSlots"/>).
/// </summary>
public static class OpLanes
{
    /// <summary>Import LUÔN 1 lane — nhiều lane đụng nhau ở Material Center + tab "Đã nhận" của BigSeller
    /// (cùng kho tài nguyên, cùng danh sách chờ). Đây là HẰNG SỐ của hệ, KHÔNG cấu hình được: cấu hình tài
    /// khoản (<c>RunConfig.Processes</c>) lẫn tham số "Số process" của Hub giao việc đều KHÔNG ghi đè được.
    /// Đổi số này = mở lại đúng lỗi 2026-07-27 (hub giao import → client chạy nhiều worker).</summary>
    public const int Import = 1;

    /// <summary>Số lane dùng khi tài khoản chưa có <c>RunConfig</c> — khớp mặc định của
    /// <c>BigSellerRunConfig.Processes</c>.</summary>
    public const int DefaultProcesses = 2;

    /// <summary>Số cửa sổ Brave một việc CẦN để tính quỹ. rewrite/search KHÔNG mở trình duyệt → 0. import →
    /// LUÔN <see cref="Import"/> (Hub ghi đè KHÔNG thắng). scrape/update: ưu tiên <paramref name="assignedProcesses"/>
    /// Hub đặt cho lượt này, else <paramref name="clientProcesses"/> (cấu hình client). Clamp theo trần engine:
    /// update 1..10, scrape 1..64.
    /// <para>Op ở đây CỐ Ý để literal thay vì dùng <c>AssignmentOps</c>: file này được LINK vào
    /// <c>orders\XuLyDonShopee.Tests</c> (khuôn "thuần BCL, không kéo theo file khác" — xem chú thích lớp) nên
    /// tham chiếu <c>AssignmentConsts.cs</c> sẽ vỡ build project đó. Giá trị PHẢI khớp <c>AssignmentOps</c>.</para></summary>
    public static int RequiredBraves(string? op, int assignedProcesses, int clientProcesses)
    {
        if (op is "rewrite" or "search") return 0;       // = AssignmentOps.Rewrite / .Search
        if (op == "import") return Import;               // = AssignmentOps.Import
        var n = assignedProcesses > 0 ? assignedProcesses : clientProcesses;
        return op == "scrape" ? Math.Clamp(n, 1, 64) : Math.Clamp(n, 1, 10);   // "scrape" = AssignmentOps.Scrape
    }
}
