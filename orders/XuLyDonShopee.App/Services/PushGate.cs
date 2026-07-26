using System.Collections.Concurrent;

namespace XuLyDonShopee.App.Services;

/// <summary>LOẠI lượt đẩy — mỗi loại một chỗ đứng riêng trong <see cref="PushGate"/> (khóa theo cặp
/// <c>(accountId, kind)</c>): đẩy đơn lên hub / đẩy file phiếu lên hub / ghi Google Sheet / +1 "Đã bán".</summary>
public enum PushKind
{
    Hub,
    HubSlip,
    Gsheet,
    SoldCount,
}

/// <summary>
/// CHỐT CHỐNG CHỒNG <b>TOÀN TIẾN TRÌNH</b> cho các lượt đẩy: một cặp <c>(accountId, kind)</c> chỉ có ĐÚNG MỘT
/// lượt chạy tại một thời điểm, bất kể lượt đó do <see cref="AccountSession"/> (phiên) hay
/// <see cref="HubOutboxWorker"/> (vòng chờ) kích hoạt.
/// <para>
/// <b>Vì sao phải toàn tiến trình:</b> trước đây mỗi phiên có cờ <c>Interlocked</c> RIÊNG (theo instance) — đủ
/// khi chỉ phiên đẩy. Từ khi có worker đẩy nền theo vòng đời app, hai luồng KHÁC instance có thể cùng đẩy MỘT
/// tài khoản. Nguy hiểm nhất là <see cref="PushKind.SoldCount"/>: hai luồng cùng +1 một đơn = <b>+2</b> cho kho
/// hub, sai số liệu và KHÔNG tự phát hiện được.
/// </para>
/// Dùng <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> làm phép "chiếm chỗ" nguyên tử: có mặt trong dict
/// = đang chạy. <b>Bắt buộc</b> gọi <see cref="Exit"/> trong <c>finally</c> của lượt đã <see cref="TryEnter"/> OK.
/// </summary>
public static class PushGate
{
    /// <summary>Các lượt ĐANG chạy (khóa <c>(accountId, kind)</c>). Giá trị không dùng — chỉ cần sự hiện diện.</summary>
    private static readonly ConcurrentDictionary<(long AccountId, PushKind Kind), byte> DangChay = new();

    /// <summary>
    /// Thử CHIẾM chỗ cho lượt đẩy <paramref name="kind"/> của tài khoản <paramref name="accountId"/>. Trả
    /// <c>true</c> = chiếm được (caller PHẢI gọi <see cref="Exit"/> khi xong, trong <c>finally</c>);
    /// <c>false</c> = đã có lượt khác đang chạy → caller BỎ QUA lượt này (hàng còn trong hàng đợi DB nên lượt sau
    /// tự đẩy bù).
    /// </summary>
    public static bool TryEnter(long accountId, PushKind kind)
        => DangChay.TryAdd((accountId, kind), 0);

    /// <summary>NHẢ chỗ đã chiếm bằng <see cref="TryEnter"/>. Gọi thừa (chưa từng chiếm) là vô hại.</summary>
    public static void Exit(long accountId, PushKind kind)
        => DangChay.TryRemove((accountId, kind), out _);

    /// <summary>True nếu lượt <paramref name="kind"/> của tài khoản này ĐANG chạy (chỉ để chẩn đoán/test).</summary>
    public static bool DangBan(long accountId, PushKind kind)
        => DangChay.ContainsKey((accountId, kind));
}
