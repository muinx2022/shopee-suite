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
    /// Máy MỚI hỏi Hub DANH BẠ sub-acc Đơn hàng (login + shop) rồi TẠO SẴN bản ghi cục bộ cho
    /// các login CHƯA có (mật khẩu TRỐNG, trạng thái Chưa kiểm tra, ghi chú nhắc nhập mật khẩu). Login đã có ở
    /// máy → GIỮ NGUYÊN (KHÔNG đè mật khẩu/cookie/ghi chú). Hub KHÔNG giữ mật khẩu nên người dùng phải tự mở
    /// từng tài khoản nhập mật khẩu rồi bấm Chạy. Hook chưa rót / Hub offline / hub cũ → báo rõ, không tạo gì.
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
            nhatKy("Kéo danh bạ từ Hub lỗi: " + ex.Message);
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
        if (toAdd.Count == 0)
        {
            nhatKy("Không có tài khoản mới (máy đã có đủ tài khoản Hub biết).");
            trangThai("Không có tài khoản mới.");
            napLai();
            return;
        }

        // Map login (ignore-case) → shops để seed sau khi Insert (khỏi phải đợi đăng nhập mới thấy shop).
        var shopsByLogin = new Dictionary<string, IReadOnlyList<(string Login, string Name)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dir)
        {
            if (!string.IsNullOrWhiteSpace(d.Login))
            {
                shopsByLogin[d.Login.Trim()] = d.Shops ?? new List<(string, string)>();
            }
        }

        foreach (var login in toAdd)
        {
            var acc = new Account
            {
                Email = login,
                Password = string.Empty,
                Status = AccountStatus.ChuaKiemTra,
                Note = "Kéo từ Hub — cần nhập mật khẩu",
            };
            _services.Accounts.Insert(acc);

            // Seed shop (tùy chọn, best-effort): hiện shop ngay ở tab "Kết quả"; lỗi KHÔNG chặn việc tạo tài khoản.
            if (shopsByLogin.TryGetValue(login, out var shops) && shops.Count > 0)
            {
                try
                {
                    _services.Results.UpsertShops(acc.Id, shops
                        .Where(s => !string.IsNullOrWhiteSpace(s.Login))
                        .Select(s => new ShopListItem(string.Empty, s.Name ?? string.Empty, s.Login)));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("[AccountsViewModel] Seed shop khi kéo từ Hub lỗi: " + ex.Message);
                }
            }
        }

        napLai();
        nhatKy($"Đã kéo {toAdd.Count} tài khoản mới từ Hub — hãy mở từng tài khoản nhập mật khẩu rồi bấm Chạy.");
        trangThai($"Đã kéo {toAdd.Count} tài khoản mới từ Hub — nhập mật khẩu rồi Chạy.");
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
