using System;
using System.IO;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// FILE PHIẾU GIAO (PDF) trên đĩa — đọc/kiểm tính hợp lệ. Tách khỏi <see cref="AccountSession"/> (đợt dọn
/// 2026-07-30): mấy hàm này thuần IO + hàm PURE, không dính vòng đời phiên, mà lại được gọi từ ba chỗ khác nhau
/// (<see cref="HubOutbox"/> khi đẩy hub/GSheet, <c>OrderRowViewModel</c> khi vẽ nút "Tải phiếu", luồng dọn đơn).
/// <para><c>internal</c>: chỉ dùng trong module Đơn hàng (test unit thấy được qua <c>InternalsVisibleTo</c>).</para>
/// </summary>
internal static class SlipFiles
{
    /// <summary>Giới hạn kích thước file phiếu đính kèm (5MB) — PDF phiếu giao thường ~100–300KB.</summary>
    internal const long MaxSlipBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Đọc file phiếu <paramref name="path"/> thành base64 nếu HỢP LỆ: tồn tại, ≤ 5MB, và 5 byte đầu là
    /// <c>%PDF-</c> (kiểm magic — bài học cũ: đừng tin đuôi file, GET lại phiếu có thể ra HTML 200-OK). File
    /// quá lớn → log 1 dòng + bỏ qua. Mọi lỗi đọc → false. Trả true + base64 khi hợp lệ.
    /// </summary>
    internal static bool TryReadSlipBase64(string path, Action<string> log, out string? base64)
    {
        base64 = null;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var info = new FileInfo(path);
            if (info.Length > MaxSlipBytes)
            {
                log($"GSheet: file phiếu quá lớn (>{MaxSlipBytes / (1024 * 1024)}MB), bỏ qua: {Path.GetFileName(path)}");
                return false;
            }

            var bytes = File.ReadAllBytes(path);
            if (!SlipMagic.LooksPdf(bytes))
            {
                return false; // không phải PDF thật → không gửi rác
            }

            base64 = Convert.ToBase64String(bytes);
            return true;
        }
        catch
        {
            return false; // lỗi đọc file → bỏ qua, không phá luồng
        }
    }

    /// <summary>
    /// True nếu file phiếu <paramref name="path"/> TỒN TẠI và là PDF thật (5 byte đầu <c>%PDF-</c>). Đọc TỐI ĐA
    /// 5 byte đầu (nhẹ, gọi được cho mỗi dòng lưới) — KHÔNG áp trần dung lượng (chỉ kiểm tồn tại + magic, đúng
    /// định nghĩa "có phiếu"). Mọi lỗi IO → <c>false</c>. Dùng cho <c>OrderRowViewModel.HasSlipFile</c> (nút
    /// "Tải phiếu") và <c>HubOutbox</c> (giữ đơn tới khi phiếu lên hub).
    /// </summary>
    internal static bool SlipFileIsValidPdf(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[5];
            var n = fs.Read(head);
            return SlipMagic.LooksPdf(head[..Math.Max(0, n)]);
        }
        catch
        {
            return false;
        }
    }
}
