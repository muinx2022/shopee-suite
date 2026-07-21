using Microsoft.AspNetCore.Components;

namespace Shopee.Hub.Web.Components;

/// <summary>Kho icon SVG dòng (Lucide-style) dùng chung cho sidebar + KPI — trước bị chép tay 3 nơi.</summary>
public static class HubIcons
{
    private static readonly Dictionary<string, string> Paths = new()
    {
        // sidebar (từ MainLayout.Icons)
        ["fleet"] = "<rect x='3' y='3' width='7' height='9' rx='1'/><rect x='14' y='3' width='7' height='5' rx='1'/><rect x='14' y='12' width='7' height='9' rx='1'/><rect x='3' y='16' width='7' height='5' rx='1'/>",
        ["stats"] = "<path d='M3 3v18h18'/><rect x='7' y='12' width='3' height='6'/><rect x='12' y='8' width='3' height='10'/><rect x='17' y='4' width='3' height='14'/>",
        ["machines"] = "<rect x='2' y='3' width='20' height='14' rx='2'/><path d='M8 21h8M12 17v4'/>",
        ["search"] = "<circle cx='11' cy='11' r='7'/><path d='M21 21l-4.3-4.3'/>",
        ["data"] = "<path d='M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z'/><path d='M3.27 6.96 12 12.01l8.73-5.05'/><path d='M12 22.08V12'/>",
        ["bigseller"] = "<path d='M12 2 2 7l10 5 10-5-10-5z'/><path d='M2 17l10 5 10-5'/><path d='M2 12l10 5 10-5'/>",
        ["users"] = "<path d='M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2'/><circle cx='9' cy='7' r='4'/><path d='M22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75'/>",
        ["alert"] = "<path d='M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z'/><path d='M12 9v4M12 17h.01'/>",
        ["ai"] = "<rect x='4' y='4' width='16' height='16' rx='2'/><rect x='9' y='9' width='6' height='6'/><path d='M9 2v2M15 2v2M9 20v2M15 20v2M20 9h2M20 14h2M2 9h2M2 14h2'/>",
        ["settings"] = "<circle cx='12' cy='12' r='3'/><path d='M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z'/>",
        ["files"] = "<path d='M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z'/>",
        ["logs"] = "<path d='M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z'/><path d='M14 2v6h6M8 13h8M8 17h8M8 9h2'/>",

        // KPI Fleet (từ Fleet.KpiIcons; "machines" dùng chung ở trên)
        ["run"] = "<polygon points='6 3 20 12 6 21 6 3'/>",
        ["clock"] = "<circle cx='12' cy='12' r='9'/><path d='M12 7v5l3 2'/>",
        ["cookie"] = "<path d='M12 2a10 10 0 1 0 10 10 4 4 0 0 1-5-5 4 4 0 0 1-5-5z'/><path d='M8.5 8.5h.01M15 9h.01M9.5 14.5h.01M14 15h.01M12 12h.01'/>",

        // KPI Stats (từ Stats.KpiIcons)
        ["shop"] = "<path d='M3 9l1.5-5h15L21 9'/><path d='M4 9v10a1 1 0 0 0 1 1h14a1 1 0 0 0 1-1V9'/><path d='M9 20v-6h6v6'/>",
        // Đơn hàng (sidebar nghiệp vụ đơn)
        ["orders"] = "<path d='M6 2h9l5 5v13a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1z'/><path d='M14 2v6h6M8 13h8M8 17h6'/>",
        ["scrape"] = "<polygon points='6 3 20 12 6 21 6 3'/>",
        ["import"] = "<path d='M12 3v13'/><path d='m7 11 5 5 5-5'/><path d='M5 21h14'/>",
        ["update"] = "<path d='M21 12a9 9 0 1 1-2.6-6.4L21 8'/><path d='M21 3v5h-5'/>",
        ["tag"] = "<path d='M20.6 11.4 12 2.8H4v8l8.6 8.6a2 2 0 0 0 2.8 0l5.2-5.2a2 2 0 0 0 0-2.8z'/><circle cx='7.5' cy='7.5' r='1.3'/>",

        // Nút hành động trên hàng lưới dữ liệu (emoji ✏ trên Windows mờ như nét gạch → SVG nét rõ, ăn currentColor)
        ["edit"] = "<path d='M17 3a2.85 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5L17 3z'/>",
        ["check"] = "<path d='M20 6 9 17l-5-5'/>",
        ["x"] = "<path d='M18 6 6 18M6 6l12 12'/>",
    };

    /// <summary>SVG inline stroke=currentColor. key lạ → svg rỗng (không vỡ trang).</summary>
    public static MarkupString Svg(string key, int size = 18, string strokeWidth = "1.8")
        => new($"<svg width='{size}' height='{size}' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='{strokeWidth}' stroke-linecap='round' stroke-linejoin='round'>{(Paths.TryGetValue(key, out var b) ? b : "")}</svg>");
}
