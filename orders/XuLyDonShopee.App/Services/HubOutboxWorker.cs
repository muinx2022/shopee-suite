using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// <b>VÒNG CHỜ ĐẨY — luồng đẩy ĐỘC LẬP theo vòng đời APP</b> (không theo phiên). Cứ ~2 phút duyệt MỌI tài khoản,
/// đếm hàng còn tồn (đơn hub / phiếu hub / dòng Google Sheet / lượt đếm "Đã bán") và đẩy phần tồn đó lên đích.
/// <para>
/// <b>Vì sao cần:</b> trước đây việc đẩy chỉ chạy KÉ sau khi một shop sync xong và gắn TOKEN CỦA PHIÊN — nên
/// (a) đích sống lại trong lúc client đang nghỉ giữa 2 vòng thì hàng tồn nằm im tới shop kế tiếp, (b) dừng phiên
/// ngay sau khi shop xong thì lượt đẩy bị hủy giữa chừng. Worker này dùng token vòng đời APP nên không dính cả hai.
/// </para>
/// <para>
/// <b>KHÔNG đẩy đôi:</b> mọi lượt đẩy đi qua <see cref="PushGate"/> (chốt toàn tiến trình theo
/// <c>(accountId, loại)</c>) — phiên đang đẩy thì worker bỏ qua và ngược lại. Riêng đếm "Đã bán" còn thêm chốt
/// thời gian (chỉ nhặt đơn đã NGUỘI — xem <see cref="OnDinhTruocKhiDemBu"/>).
/// </para>
/// Toàn bộ nuốt lỗi — vòng lặp KHÔNG bao giờ ném ra ngoài.
/// </summary>
public sealed class HubOutboxWorker : IDisposable
{
    /// <summary>Chu kỳ thường (mọi thứ khỏe).</summary>
    public static readonly TimeSpan ChuKyThuong = TimeSpan.FromMinutes(2);

    /// <summary>Chu kỳ sau 1 lượt lỗi liên tiếp.</summary>
    public static readonly TimeSpan ChuKyLoi1 = TimeSpan.FromMinutes(5);

    /// <summary>Chu kỳ TRẦN (từ 2 lượt lỗi liên tiếp trở đi).</summary>
    public static readonly TimeSpan ChuKyLoiTran = TimeSpan.FromMinutes(10);

    /// <summary>Hoãn lượt ĐẦU TIÊN sau khi mở app: đủ để CSDL + cầu nối hub lên xong, vẫn coi như "chạy ngay".</summary>
    public static readonly TimeSpan HoanLuotDau = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Đơn phải "nguội" ít nhất bằng đây (theo <c>updated_at</c>) mới được worker đếm bù "Đã bán". Chốt chống
    /// ĐẾM ĐÔI với luồng phiên: phiên ghi trạng thái đã-giao (UpsertMany) rồi mới đánh cờ cho nhóm grandfather
    /// ngay sau đó — worker đọc trúng khe hở ấy sẽ +1 nhầm. Xem <c>OrdersRepository.GetForSoldCountRetry</c>.
    /// </summary>
    public static readonly TimeSpan OnDinhTruocKhiDemBu = TimeSpan.FromMinutes(1);

    /// <summary>Nhãn nguồn cho các dòng log CẤP VÒNG (không thuộc tài khoản nào).</summary>
    private const string LogLabel = "Vòng chờ";

    private readonly AppServices _services;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Đã báo "chưa cấu hình Web App URL" cho tài khoản nào rồi (chống spam — worker chạy cả ngày).
    /// Hiện worker KHÔNG gọi lượt sheet khi URL trống nên tập này chỉ là chốt phòng hờ.</summary>
    private readonly ConcurrentDictionary<long, byte> _daBaoThieuGsheetUrl = new();

    /// <summary>Số tồn ĐÃ BÁO lần gần nhất của từng tài khoản — chỉ log dòng "Vòng chờ: đẩy …" khi số ĐỔI
    /// (đích chết cả buổi thì mỗi 10′ lại một dòng y hệt = spam; người dùng đã biết từ dòng đầu).</summary>
    private readonly ConcurrentDictionary<long, OutboxPending> _tonDaBao = new();

    public HubOutboxWorker(AppServices services) => _services = services;

    /// <summary>
    /// CHU KỲ chờ trước lượt kế theo số lượt LỖI LIÊN TIẾP: 0 → 2′, 1 → 5′, ≥2 → 10′ (trần). Hàm THUẦN để test được.
    /// </summary>
    public static TimeSpan ChuKyTiepTheo(int soLuotLoiLienTiep) => soLuotLoiLienTiep switch
    {
        <= 0 => ChuKyThuong,
        1 => ChuKyLoi1,
        _ => ChuKyLoiTran,
    };

    /// <summary>Khởi động vòng lặp nền (idempotent — gọi lần 2 không tạo thêm vòng).</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Vòng lặp chính: hoãn ngắn cho app ổn định → chạy lượt đầu → nghỉ theo <see cref="ChuKyTiepTheo"/> → lặp.
    /// Chỉ log khi <b>kết quả ĐỔI</b> (OK ↔ lỗi) để không làm ngập nhật ký.
    /// </summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            try { await Task.Delay(HoanLuotDau, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var loiLienTiep = 0;
            bool? luotTruocOk = null;

            while (!ct.IsCancellationRequested)
            {
                var ok = await MotLuotAsync(ct).ConfigureAwait(false);

                if (ok is not null)
                {
                    // Chỉ đổi trạng thái/backoff khi lượt này THỰC SỰ có đẩy (null = không có gì để đẩy).
                    loiLienTiep = ok.Value ? 0 : loiLienTiep + 1;

                    if (luotTruocOk != ok.Value)
                    {
                        _services.Log.Append(LogLabel, ok.Value
                            ? "Đã đẩy xong hàng tồn."
                            : $"Đích chưa nhận — sẽ thử lại sau {ChuKyTiepTheo(loiLienTiep).TotalMinutes:0}′.");
                        luotTruocOk = ok.Value;
                    }
                }

                try { await Task.Delay(ChuKyTiepTheo(loiLienTiep), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (Exception)
        {
            // Chốt chặn cuối: vòng chờ đẩy KHÔNG bao giờ được ném ra ngoài (task nền unobserved). Lỗi từng lượt
            // đã bị nuốt + ghi log ở MotLuotAsync; tới đây chỉ còn ca thoát app (token hủy giữa chừng).
        }
    }

    /// <summary>
    /// MỘT lượt: duyệt mọi tài khoản, đếm tồn, đẩy phần tồn qua <see cref="PushGate"/>, rồi cập nhật số tồn hiển
    /// thị (<see cref="AppServices.SetPendingOutbox"/>). Trả <c>null</c> = KHÔNG có gì để đẩy (không tính vào
    /// backoff); <c>true</c> = mọi lượt đẩy đích đều nhận; <c>false</c> = có ít nhất một lượt đích không nhận.
    /// <b>Không bao giờ ném.</b>
    /// <para><c>internal</c> để test chạy được MỘT lượt mà không phải chờ đồng hồ của vòng lặp nền.</para>
    /// </summary>
    internal async Task<bool?> MotLuotAsync(CancellationToken ct)
    {
        try
        {
            var tong = new OutboxPending();
            bool? ketQua = null;

            foreach (var acc in _services.Accounts.GetAll())
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var (ton, ok) = await MotTaiKhoanAsync(acc.Id, TenTaiKhoan(acc.Email, acc.Id), ct).ConfigureAwait(false);
                tong = tong.Cong(ton);
                if (ok is not null)
                {
                    ketQua = (ketQua ?? true) && ok.Value;
                }
            }

            _services.SetPendingOutbox(tong);
            return ketQua;
        }
        catch (Exception ex)
        {
            // Lỗi bất ngờ (đọc DB hỏng…) KHÔNG được giết vòng lặp — lượt sau thử lại.
            _services.Log.Append(LogLabel, "Lỗi lượt quét: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Xử MỘT tài khoản trong một lượt: đếm 4 loại tồn → có tồn thì đẩy (thứ tự ĐƠN → PHIẾU giữ nguyên như phiên,
    /// vì phiếu chỉ đẩy được cho đơn ĐÃ lên hub) → đếm LẠI để báo số CÒN tồn. Trả (số tồn còn lại, kết quả đẩy) —
    /// kết quả <c>null</c> = không có gì để đẩy. Mọi lỗi bị nuốt (các hàm <see cref="HubOutbox"/> tự nuốt).
    /// </summary>
    private async Task<(OutboxPending Ton, bool? Ok)> MotTaiKhoanAsync(long accountId, string tenTk, CancellationToken ct)
    {
        var log = (Action<string>)(m => _services.Log.Append(tenTk, m));

        var (ton, skus, orderSns) = DemTon(accountId);
        if (ton.Tong == 0)
        {
            _tonDaBao.TryRemove(accountId, out _);
            return (ton, null); // tài khoản này sạch → không đẩy, không log
        }

        // Chỉ báo khi số tồn ĐỔI so với lần báo trước (lần đầu backlog xuất hiện, hoặc vơi/dày thêm).
        if (!_tonDaBao.TryGetValue(accountId, out var daBao) || !daBao.Equals(ton))
        {
            _tonDaBao[accountId] = ton;
            log($"Vòng chờ: đẩy {ton.Orders} đơn / {ton.Slips} phiếu / {ton.SheetRows} dòng sheet / {ton.SoldCounts} lượt đếm Đã bán còn tồn.");
        }

        bool? ok = null;
        // 1) ĐƠN lên hub (phải trước phiếu — phiếu chỉ đẩy cho đơn đã có hub_synced_at).
        if (ton.Orders > 0)
        {
            ok = Gop(ok, await ChayQuaGateAsync(accountId, PushKind.Hub,
                () => HubOutbox.PushOrdersToHubAsync(accountId, _services, log, ct)).ConfigureAwait(false));
        }
        // 2) PHIẾU lên hub.
        if (ton.Slips > 0)
        {
            ok = Gop(ok, await ChayQuaGateAsync(accountId, PushKind.HubSlip,
                () => HubOutbox.PushSlipsToHubAsync(accountId, _services, log, ct)).ConfigureAwait(false));
        }
        // 3) Dòng Google Sheet (KHÔNG cần hub — đi thẳng Apps Script). shopId null = mọi đơn của tài khoản.
        if (ton.SheetRows > 0)
        {
            ok = Gop(ok, await ChayQuaGateAsync(accountId, PushKind.Gsheet,
                () => HubOutbox.PushOrdersToGsheetAsync(
                    accountId, _services, shopId: null, shopLogin: null,
                    nenBaoThieuGsheetUrl: () => _daBaoThieuGsheetUrl.TryAdd(accountId, 0),
                    imLangKhiKhongCoDonMoi: true, log, ct)).ConfigureAwait(false));
        }
        // 4) +1 "Đã bán" theo SKU (đường đếm BÙ khi lượt của phiên lỗi — xem GetForSoldCountRetry).
        if (skus.Count > 0)
        {
            ok = Gop(ok, await ChayQuaGateAsync(accountId, PushKind.SoldCount,
                () => HubOutbox.IncrementSoldBySkuAsync(accountId, _services, skus, orderSns, log, ct)).ConfigureAwait(false));
        }

        // Đếm LẠI sau khi đẩy → badge "⏳ Chờ đẩy" hiện số CÒN tồn thật, không phải số đầu lượt (đẩy sạch mà badge
        // còn treo 2 phút thì người dùng tưởng vẫn kẹt). Truy vấn COUNT nhẹ, chỉ chạy cho tài khoản CÓ đẩy.
        var (conLai, _, _) = DemTon(accountId);
        return (conLai, ok);
    }

    /// <summary>
    /// Đếm hàng còn tồn của MỘT tài khoản + dựng sẵn danh sách (SKU, mã đơn) cho lượt đếm "Đã bán".
    /// <para>
    /// <b>CHỈ đếm loại nào đang có ĐÍCH thật</b> (hook hub đã rót / URL Web App đã điền), kẻo badge kẹt số vĩnh
    /// viễn: máy chạy độc lập không có hub thì <c>hub_synced_at</c> NULL mãi mãi, không dùng sheet thì
    /// <c>gsheet_synced_at</c> cũng vậy — đó không phải "hàng chờ đẩy".
    /// </para>
    /// Đếm bằng SQL <c>COUNT</c> cho 3 loại đầu (worker quét mọi tài khoản mỗi 2′ nên phải nhẹ); riêng "Đã bán"
    /// phải nạp danh sách hẹp rồi lọc bằng C# (SQL không biết luật "đã giao"/"đơn hủy").
    /// </summary>
    private (OutboxPending Ton, List<string> Skus, List<string> OrderSns) DemTon(long accountId)
    {
        var coHub = _services.PushOrdersToHub is not null;
        var coHubPhieu = _services.PushOrderSlipsToHub is not null;
        var coSheet = !string.IsNullOrWhiteSpace(_services.Settings.GetGsheetWebAppUrl());
        var coDemDaBan = _services.IncrementSoldBySku is not null;

        var tonDon = coHub ? _services.Orders.CountForHubPush(accountId) : 0;
        var tonPhieu = coHubPhieu ? _services.Orders.CountForHubSlipPush(accountId) : 0;
        var tonSheet = coSheet ? _services.Orders.CountForGsheetPush(accountId) : 0;

        var skus = new List<string>();
        var orderSns = new List<string>();
        if (coDemDaBan)
        {
            var hangDoi = _services.Orders.GetForSoldCountRetry(accountId, DateTime.UtcNow - OnDinhTruocKhiDemBu);
            (skus, orderSns) = HubOutbox.LocHangDoiDemDaBan(hangDoi);
        }

        return (new OutboxPending(tonDon, tonPhieu, tonSheet, skus.Count), skus, orderSns);
    }

    /// <summary>
    /// Chạy một lượt đẩy SAU KHI chiếm được <see cref="PushGate"/>; không chiếm được (phiên hoặc lượt worker trước
    /// còn đang chạy) → trả <c>null</c> = "chưa biết", KHÔNG tính lỗi (lượt sau tự đẩy bù). Nhả gate ở <c>finally</c>.
    /// </summary>
    private static async Task<bool?> ChayQuaGateAsync(long accountId, PushKind kind, Func<Task<KetQuaDay>> day)
    {
        if (!PushGate.TryEnter(accountId, kind))
        {
            return null; // đang có lượt khác chạy (phiên) → bỏ qua, KHÔNG đẩy đôi
        }

        try
        {
            var kq = await day().ConfigureAwait(false);
            return kq switch
            {
                KetQuaDay.ThanhCong => true,
                KetQuaDay.ThatBai => false,
                _ => null,
            };
        }
        finally
        {
            PushGate.Exit(accountId, kind);
        }
    }

    /// <summary>Gộp kết quả nhiều lượt đẩy: chỉ cần MỘT lượt thất bại là cả tài khoản coi như thất bại;
    /// <c>null</c> (không rõ / bỏ qua) không làm đổi kết quả.</summary>
    private static bool? Gop(bool? hienTai, bool? them)
        => them is null ? hienTai : (hienTai ?? true) && them.Value;

    /// <summary>Nhãn log của tài khoản = email (tên đăng nhập) như <c>AccountSession</c>; trống → "TK {id}".</summary>
    private static string TenTaiKhoan(string? email, long accountId)
        => string.IsNullOrWhiteSpace(email) ? $"TK {accountId}" : email!;

    /// <summary>Dừng vòng chờ khi thoát app. CỐ Ý chỉ <c>Cancel</c>, KHÔNG <c>Dispose</c> nguồn token: vòng lặp
    /// có thể còn đang tháo dỡ và dùng token đó (đăng ký vào <c>Task.Delay</c>) — dispose sớm sẽ ném
    /// <c>ObjectDisposedException</c> trong task nền. Vật thể nhỏ, app đang thoát nên không cần thu hồi.</summary>
    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* bỏ qua khi thoát */ }
    }
}
