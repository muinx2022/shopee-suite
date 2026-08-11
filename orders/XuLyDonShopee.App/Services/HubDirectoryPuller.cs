using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using XuLyDonShopee.Core.Models;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.Services;

/// <summary>
/// "Kéo TK từ Hub" — hỏi Hub DANH BẠ sub-acc Đơn hàng rồi tạo sẵn bản ghi cục bộ cho các login máy CHƯA có.
/// Tách khỏi <c>AccountsViewModel</c> vì đây là việc hỏi-hub + ghi-DB thuần, không đụng danh sách/lựa chọn/form;
/// màn hình chỉ rót 3 callback (ghi nhật ký · dòng trạng thái · nạp lại danh sách) nên THỨ TỰ tác động lên UI
/// giữ y hệt bản cũ.
/// </summary>
internal sealed class HubDirectoryPuller
{
    private readonly AppServices _services;

    public HubDirectoryPuller(AppServices services) => _services = services;

    /// <summary>
    /// Máy MỚI hỏi Hub DANH BẠ sub-acc Đơn hàng (login + shop + 3 ô đăng nhập) rồi:
    /// <list type="number">
    /// <item>TẠO bản ghi cục bộ cho các login máy CHƯA có, điền luôn 3 ô đăng nhập Hub biết;</item>
    /// <item><b>VÁ Ô TRỐNG</b> cho các login máy ĐÃ có: ô nào đang trống thì lấy của Hub, ô nào đã có chữ thì
    /// TUYỆT ĐỐI không đụng (xem <see cref="VaOTrong"/>). Cookie/ghi chú/trạng thái không bao giờ bị đè.</item>
    /// </list>
    /// Ô Hub cũng rỗng (chưa máy nào nhập) → người dùng vẫn phải tự nhập rồi bấm Chạy.
    /// Hook chưa rót / Hub offline / hub cũ → báo rõ, không tạo gì.
    /// </summary>
    /// <param name="nhatKy">Ghi một dòng vào panel nhật ký (nguồn cấp-BATCH).</param>
    /// <param name="trangThai">Đặt dòng trạng thái hiển thị trên màn.</param>
    /// <param name="napLai">Nạp lại danh sách tài khoản của màn (giữ lựa chọn/form/tick).</param>
    internal async Task KeoAsync(Action<string> nhatKy, Action<string> trangThai, Action napLai)
    {
        if (_services.QueryOrdersDirectory is not { } hook)
        {
            nhatKy("Hub chưa kết nối — không kéo được danh bạ tài khoản.");
            trangThai("Hub chưa kết nối.");
            return;
        }

        IReadOnlyList<OrdersDirectoryItem>? dir;
        try
        {
            dir = await hook(System.Threading.CancellationToken.None);
        }
        catch (Exception ex)
        {
            nhatKy("Kéo danh bạ từ Hub lỗi: " + ex.ToString());
            trangThai("Kéo danh bạ từ Hub lỗi.");
            return;
        }

        if (dir is null)
        {
            nhatKy("Không kéo được danh bạ từ Hub (Hub offline / bản Hub cũ).");
            trangThai("Không kéo được danh bạ từ Hub.");
            return;
        }
        if (dir.Count == 0)
        {
            nhatKy("Hub chưa có tài khoản nào.");
            trangThai("Hub chưa có tài khoản nào.");
            return;
        }

        var toAdd = TinhLoginCanThem(dir.Select(d => d.Login), _services.Accounts.GetAll().Select(a => a.Email));

        // Map login (ignore-case) → mục danh bạ, dùng cho cả seed shop lẫn vá 3 ô đăng nhập.
        var theoLogin = new Dictionary<string, OrdersDirectoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dir)
        {
            if (!string.IsNullOrWhiteSpace(d.Login))
            {
                theoLogin[d.Login.Trim()] = d;
            }
        }

        // VÁ Ô TRỐNG cho tài khoản ĐÃ CÓ trên máy — chạy TRƯỚC nhánh "không có tài khoản mới" thoát sớm, vì
        // đây mới là việc chính khi máy đã đủ login mà vài ô còn trống.
        var soOVa = 0;
        var soAccVa = 0;
        foreach (var acc in _services.Accounts.GetAll())
        {
            var email = acc.Email?.Trim() ?? "";
            if (email.Length == 0 || !theoLogin.TryGetValue(email, out var tuHub))
            {
                continue;
            }
            var va = VaOTrong(acc, tuHub.Password, tuHub.VerifyEmail, tuHub.VerifyEmailPassword);
            if (va == 0)
            {
                continue;
            }
            _services.Accounts.Update(acc);
            soOVa += va;
            soAccVa++;
        }

        if (toAdd.Count == 0)
        {
            var them = soOVa > 0 ? $" Đã vá {soOVa} ô đăng nhập còn trống của {soAccVa} tài khoản." : "";
            nhatKy("Không có tài khoản mới (máy đã có đủ tài khoản Hub biết)." + them);
            trangThai("Không có tài khoản mới." + them);
            napLai();
            return;
        }

        foreach (var login in toAdd)
        {
            var tuHub = theoLogin.TryGetValue(login, out var d0) ? d0 : null;
            var matKhau = tuHub?.Password ?? "";
            var acc = new Account
            {
                Email = login,
                Password = matKhau,
                VerifyEmail = tuHub?.VerifyEmail ?? "",
                VerifyEmailPassword = tuHub?.VerifyEmailPassword ?? "",
                Status = AccountStatus.ChuaKiemTra,
                Note = matKhau.Length > 0 ? "Kéo từ Hub" : "Kéo từ Hub — cần nhập mật khẩu",
            };
            _services.Accounts.Insert(acc);

            // Seed shop (tùy chọn, best-effort): hiện shop ngay ở tab "Shops"; lỗi KHÔNG chặn việc tạo tài khoản.
            var shops = tuHub?.Shops ?? new List<(string Login, string Name)>();
            if (shops.Count > 0)
            {
                try
                {
                    _services.Results.UpsertShops(acc.Id, shops
                        .Where(s => !string.IsNullOrWhiteSpace(s.Login))
                        .Select(s => new ShopListItem(string.Empty, s.Name ?? string.Empty, s.Login)));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("[AccountsViewModel] Seed shop khi kéo từ Hub lỗi: " + ex.ToString());
                }
            }
        }

        napLai();
        // Chỉ nhắc "nhập mật khẩu" khi thật sự còn tài khoản thiếu mật khẩu — Hub giờ có thể đã gửi sẵn.
        var conThieuMatKhau = toAdd.Count(l =>
            string.IsNullOrWhiteSpace(theoLogin.TryGetValue(l, out var d1) ? d1.Password : null));
        var nhac = conThieuMatKhau > 0
            ? $" — còn {conThieuMatKhau} tài khoản chưa có mật khẩu, hãy mở ra nhập rồi bấm Chạy."
            : " — đã có sẵn mật khẩu từ Hub, bấm Chạy được ngay.";
        var vaThem = soOVa > 0 ? $" Vá thêm {soOVa} ô đăng nhập còn trống của {soAccVa} tài khoản cũ." : "";
        nhatKy($"Đã kéo {toAdd.Count} tài khoản mới từ Hub{nhac}{vaThem}");
        trangThai($"Đã kéo {toAdd.Count} tài khoản mới từ Hub{nhac}");
    }

    /// <summary>
    /// (THUẦN, test được) <b>Vá ô trống</b> cho một bản ghi tài khoản cục bộ bằng giá trị Hub gửi về: ô nào đang
    /// TRỐNG thì điền, ô nào ĐÃ CÓ CHỮ thì tuyệt đối không đụng — kể cả khi Hub có giá trị khác. Đây là chốt
    /// "Hub không bao giờ đè thứ người dùng đã gõ trên máy này". Giá trị Hub rỗng cũng không xoá gì.
    /// Trả về SỐ Ô vừa vá (0 = không đụng gì ⇒ khỏi ghi DB).
    /// </summary>
    internal static int VaOTrong(Account local, string? password, string? verifyEmail, string? verifyEmailPassword)
    {
        var va = 0;
        if (string.IsNullOrWhiteSpace(local.Password) && !string.IsNullOrWhiteSpace(password))
        {
            local.Password = password;
            va++;
        }
        if (string.IsNullOrWhiteSpace(local.VerifyEmail) && !string.IsNullOrWhiteSpace(verifyEmail))
        {
            local.VerifyEmail = verifyEmail;
            va++;
        }
        if (string.IsNullOrWhiteSpace(local.VerifyEmailPassword) && !string.IsNullOrWhiteSpace(verifyEmailPassword))
        {
            local.VerifyEmailPassword = verifyEmailPassword;
            va++;
        }
        return va;
    }

    /// <summary>
    /// (THUẦN, test được) Tính danh sách login CẦN THÊM = các login Hub trả về mà máy CHƯA có. Distinct
    /// ignore-case, bỏ rỗng/space, GIỮ thứ tự gặp đầu tiên trong <paramref name="hubLogins"/>. So khớp với
    /// <paramref name="localEmails"/> KHÔNG phân biệt hoa/thường (đã Trim). Đây là chốt "không đè dữ liệu local".
    /// </summary>
    internal static List<string> TinhLoginCanThem(IEnumerable<string> hubLogins, IEnumerable<string> localEmails)
    {
        var local = new HashSet<string>(
            (localEmails ?? Enumerable.Empty<string>())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in hubLogins ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            var login = raw.Trim();
            if (local.Contains(login) || !seen.Add(login))
            {
                continue;
            }
            result.Add(login);
        }
        return result;
    }
}
