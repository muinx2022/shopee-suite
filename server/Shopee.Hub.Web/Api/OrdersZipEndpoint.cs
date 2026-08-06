using System.IO.Compression;
using Shopee.Hub;
using Shopee.Hub.Web.Services;

namespace Shopee.Hub.Web.Api;

/// <summary>
/// Nút "⬇ ZIP phiếu" của trang /orders (H2.4): tải MỘT file zip gồm mọi phiếu PDF mà hub đang giữ của các đơn
/// khớp BỘ LỌC hiện tại (shop / trạng thái / tìm / có mã trả) — mọi trang, không chỉ trang đang xem.
/// <para>
/// Nguồn file là kho <c>&lt;DataDir&gt;/slips/&lt;shopId&gt;/&lt;order_sn đã sanitize&gt;.pdf</c> — ĐÚNG chỗ
/// <c>POST /api/orders/slip</c> ghi và <c>GET /slips/{shopId}/{orderSn}</c> phục vụ. Khoá cookie admin
/// (<c>RequireAuthorization("Web")</c>) y như hai đường kia.
/// </para>
/// <para>
/// Ghi THẲNG <see cref="ZipArchive"/> lên response stream với <see cref="CompressionLevel.NoCompression"/> (PDF
/// đã nén sẵn): không dựng file tạm, không gom byte vào RAM — 500 phiếu ~150MB vẫn chỉ tốn một buffer copy.
/// </para>
/// </summary>
public static class OrdersZip
{
    /// <summary>Đường dẫn endpoint — hằng dùng CHUNG với trang /orders để link và route không lệch chuỗi.</summary>
    public const string Route = "/orders/zip";

    /// <summary>Trần số phiếu MỘT lượt tải. Quá trần → 400 kèm lời nhắc thu hẹp bộ lọc (không cắt bớt âm thầm:
    /// người tải sẽ tưởng đã có đủ phiếu).</summary>
    public const int MaxSlips = 500;

    /// <summary>Tên file tải về — mốc theo giờ Việt Nam (người tải ở VN, tên file là để họ đọc).</summary>
    internal static string TenFileZip(DateTimeOffset now) =>
        "phieu-" + GioVietNam.Doi(now).ToString("yyyyMMdd-HHmm") + ".zip";

    /// <summary>Đoạn tên thư mục AN TOÀN trong zip cho một shop: giữ <c>[A-Za-z0-9._-]</c>, còn lại thành
    /// <c>_</c> (tên shop là chuỗi tự do, có thể chứa <c>/</c> hoặc ký tự cấm của Windows). Rỗng sau lọc →
    /// <c>shop-{id}</c> để entry không bao giờ mất đoạn thư mục.</summary>
    internal static string TenThuMucShop(string? shopName, long shopId)
    {
        var sb = new System.Text.StringBuilder((shopName ?? "").Length);
        foreach (var ch in (shopName ?? "").Trim())
            sb.Append(ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.'
                ? ch : '_');
        var s = sb.ToString().Trim('.', '_');
        return s.Length == 0 ? "shop-" + shopId : s;
    }

    public static void MapOrdersZip(this WebApplication app, HubDatabase db)
    {
        app.MapGet(Route, (long? shop, string? status, string? q, string? comatra, HubOptions opts, HttpContext ctx) =>
        {
            var coMaTra = comatra == "1";
            // Lấy TRẦN+1 dòng: đủ để biết "quá trần" mà không phải đếm cả bảng.
            var rows = db.OrdersWithSlip(
                shop is > 0 ? shop : null,
                string.IsNullOrWhiteSpace(status) ? null : status,
                string.IsNullOrWhiteSpace(q) ? null : q,
                coMaTra,
                MaxSlips + 1);

            if (rows.Count > MaxSlips)
                return Results.Text(
                    $"Bộ lọc hiện tại khớp hơn {MaxSlips} phiếu — hãy thu hẹp bộ lọc (chọn shop, trạng thái, "
                    + "hoặc từ khoá tìm) rồi tải lại.", "text/plain; charset=utf-8", statusCode: 400);

            if (rows.Count == 0)
                return Results.Text("Không có phiếu nào trên hub khớp bộ lọc hiện tại.",
                    "text/plain; charset=utf-8", statusCode: 404);

            // Tên shop đọc MỘT lần (entry name = <shop>/<order_sn>.pdf).
            var ten = db.ListShops().ToDictionary(
                s => s.Id, s => string.IsNullOrWhiteSpace(s.Username) ? s.Name : s.Username!);
            var slipsRoot = Path.Combine(opts.DataDir, "slips");

            // BẮT BUỘC: ZipArchive.Dispose ghi "central directory" bằng lệnh GHI ĐỒNG BỘ, mà Kestrel mặc định
            // CẤM ghi đồng bộ lên response stream → toàn bộ gói zip 500 lỗi ở đúng byte cuối (đã dính khi chạy
            // thật). Chỉ nới cho ĐÚNG request này (feature theo request, không phải cấu hình toàn server).
            var bodyControl = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
            if (bodyControl is not null) bodyControl.AllowSynchronousIO = true;

            return Results.Stream(async output =>
            {
                // leaveOpen: ASP.NET Core sở hữu response stream — ZipArchive không được đóng nó.
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                var daDung = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (shopId, orderSn) in rows)
                {
                    var safe = HubDatabase.SanitizeSlipFileName(orderSn);
                    if (safe is null) continue;
                    var path = Path.Combine(slipsRoot, shopId.ToString(), safe + ".pdf");
                    // slip_at có thể trỏ tới file đã bị xoá tay trên đĩa → bỏ qua, KHÔNG làm hỏng cả gói.
                    if (!File.Exists(path)) continue;

                    // Hai shop khác id nhưng tên sanitize ra giống nhau → chèn id để không đè entry.
                    var thuMuc = TenThuMucShop(ten.GetValueOrDefault(shopId), shopId);
                    var name = thuMuc + "/" + safe + ".pdf";
                    if (!daDung.Add(name))
                    {
                        name = thuMuc + "-" + shopId + "/" + safe + ".pdf";
                        if (!daDung.Add(name)) continue;
                    }

                    var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                    await using var es = entry.Open();
                    await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await fs.CopyToAsync(es);
                }
            }, "application/zip", TenFileZip(DateTimeOffset.UtcNow));
        }).RequireAuthorization("Web");
    }
}
