namespace ShopeeStatApp.Models;

public sealed class LauncherSettings
{
    public List<InstanceConfig> Instances { get; set; } = [];
    public string BravePath { get; set; } = "";
    public string ExtensionPath { get; set; } = "";
    /// <summary>KHÔNG còn được đọc ở đâu: mỗi lane tự xin cổng động qua PortAllocator.Reserve()
    /// (xem SearchSession) rồi nhét vào URL "#_ss_ws={port}". Giữ lại để settings.json cũ vẫn nạp được;
    /// 9111 chỉ là cổng mặc định phía extension (extensions/shopee-search/core.js) và Toolkit WebSocketServer.</summary>
    public int WsPort { get; set; } = 9111;
    public string OutputDirectory { get; set; } = "";

    /// <summary>Số lane (cửa sổ Brave + account) chạy song song khi bấm "Tự động".</summary>
    public int MaxParallelLanes { get; set; } = 6;

    /// <summary>Danh sách file .xlsx đã chọn lần trước ở tab "Tìm theo file" — để mở app nạp lại + resume.</summary>
    public List<string> LastFilePaths { get; set; } = [];

    /// <summary>Số Process (lane) lần trước ở tab "Tìm theo file" — giữ lại để không phải set lại mỗi lần chạy.</summary>
    public int LastFileProcessCount { get; set; } = 1;

    /// <summary>
    /// Id tài khoản được dùng sau cùng. Lượt chạy kế tiếp bắt đầu từ account NGAY SAU id này
    /// (xoay vòng), thay vì luôn chọn lại từ đầu danh sách — tránh dồn traffic lên vài account đầu
    /// và bị Shopee đánh dấu "traffic error" sau mỗi lần dừng/chạy lại.
    /// </summary>
    public string LastUsedAccountId { get; set; } = "";
}

