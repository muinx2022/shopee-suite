using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XuLyDonShopee.App.Services;
using XuLyDonShopee.Core.Services;

namespace XuLyDonShopee.App.ViewModels;

/// <summary>
/// <b>Tab "Kết quả"</b> của màn Tài khoản: lưới Shop | số đơn Chuẩn bị hàng theo NGÀY (số CỤC BỘ của máy, đè
/// bằng số CHUNG lấy từ Hub khi hỏi được), cột tiến độ (vòng quay / dấu tick theo shop phiên đang check) và
/// nhịp tự sang ngày lúc qua nửa đêm.
/// <para>Là phần <c>partial</c> của <see cref="AccountsViewModel"/> — property công khai vẫn nằm trên VM chính
/// (XAML bind thẳng <c>ResultRows</c>/<c>ResultDate</c>/<c>TongChuanBiHang</c>/<c>DangDungSoHub</c>).</para>
/// </summary>
public partial class AccountsViewModel
{
    /// <summary>Tab "Kết quả" (cột tiến độ): shop mà phiên của TỪNG tài khoản đang/vừa check — nhãn shop + có
    /// đang check hay không. Nhớ theo tài khoản (không phải một ô duy nhất) để chuyển qua lại giữa nhiều tài
    /// khoản đang chạy vẫn thấy đúng chấm của tài khoản đang mở. Chỉ ghi/đọc trên UI thread (xem
    /// <see cref="OnShopCheckChanged"/> đã marshal qua <see cref="RunOnUi"/>) nên không cần khóa.</summary>
    private readonly Dictionary<long, (string ShopLabel, bool IsChecking)> _shopCheck = new();

    /// <summary>Tab "Kết quả" (cột tiến độ): NHÃN các shop đã kiểm tra XONG trong LƯỢT CHẠY hiện tại của từng tài
    /// khoản — nguồn của dấu tick. Trạng thái của lượt chạy nên chỉ sống trong bộ nhớ (KHÔNG lưu DB) và bị xóa
    /// sạch khi lượt mới bắt đầu. So khớp nhãn không phân biệt hoa/thường. Chỉ ghi/đọc trên UI thread (xem
    /// <see cref="OnShopCheckChanged"/>/<see cref="OnShopListChanged"/> đã marshal qua <see cref="RunOnUi"/>) nên
    /// không cần khóa.</summary>
    private readonly Dictionary<long, HashSet<string>> _shopDaCheck = new();

    /// <summary>Tab "Kết quả": NGÀY đang lọc (mặc định hôm nay). Đổi ngày → <see cref="LoadResults"/> nạp lại số
    /// chuẩn bị hàng của ngày đó. DatePicker trả <c>DateTimeOffset?</c> — dùng kiểu KHÔNG null nên xóa trắng lịch
    /// KHÔNG ghi được về đây (giữ giá trị cũ).</summary>
    [ObservableProperty]
    private DateTimeOffset _resultDate = DateTimeOffset.Now;

    /// <summary>Ngày mà ô lọc "Kết quả" coi là HÔM NAY ở lần dò gần nhất. Dùng để phân biệt "người dùng đang xem
    /// hôm nay" (→ tự chuyển sang ngày mới lúc qua nửa đêm) với "người dùng chủ động chọn ngày cũ để xem lại"
    /// (→ TUYỆT ĐỐI không giật ngày khỏi tay họ). Chỉ đụng trên UI thread (xem <see cref="KiemTraSangNgay"/>).</summary>
    private DateTime _ngayCoiLaHomNay = DateTimeOffset.Now.Date;

    /// <summary>Nhịp dò "đã sang ngày mới chưa" — 60s: đủ nhạy để số của ngày mới hiện gần như tức thì mà gần
    /// như không tốn gì (chỉ so hai <c>DateTime</c>, không đụng DB/hub khi ngày chưa đổi).</summary>
    private static readonly TimeSpan NhipDoSangNgay = TimeSpan.FromSeconds(60);

    /// <summary>Đồng hồ dò sang ngày (chạy trên thread nền → callback marshal về UI thread). Dựng ở ctor, dọn ở
    /// <see cref="Dispose"/> (shell gọi khi thoát app — <c>OrdersModuleHost.StopAsync</c>).</summary>
    private readonly System.Threading.Timer _timerSangNgay;

    /// <summary>Tab "Kết quả": các dòng Shop | số Chuẩn bị hàng của NGÀY đang lọc — MỌI shop của tài khoản (kể cả
    /// shop 0 đơn). Dựng lại trong <see cref="LoadResults"/> khi đổi tài khoản chọn / đổi ngày.</summary>
    public ObservableCollection<ShopPrepareRow> ResultRows { get; } = new();

    /// <summary>Banner lỗi địa chỉ đang hiện (mỗi shop một dòng). Độc lập <see cref="ResultDate"/> — chỉ đổi khi
    /// chọn tài khoản / ghi alert / bấm X / kéo Hub.</summary>
    public ObservableCollection<PickupAlertRow> AddressAlertRows { get; } = new();

    /// <summary>TỔNG số đơn "Chuẩn bị hàng" của MỌI shop đang hiện ở tab Kết quả (ngày đang lọc). Bám ĐÚNG cột
    /// trong lưới: hub áp số của nó vào từng dòng thì tổng này cũng là số hub — không tính riêng một đường khác,
    /// kẻo tổng nói một đằng các dòng nói một nẻo.</summary>
    [ObservableProperty]
    private int _tongChuanBiHang;

    /// <summary>Cộng lại tổng từ chính <see cref="ResultRows"/>. Gọi ở MỌI chỗ vừa dựng lại dòng hoặc vừa gán lại
    /// <see cref="ShopPrepareRow.PreparedCount"/> — hai chỗ đó là toàn bộ đường số đi vào lưới.</summary>
    private void CapNhatTongChuanBiHang()
    {
        var tong = 0;
        foreach (var row in ResultRows)
        {
            tong += row.PreparedCount;
        }
        TongChuanBiHang = tong;
    }

    /// <summary>Tab "Kết quả": số đang hiện là số CHUNG TOÀN HỆ THỐNG lấy từ Hub (true) hay số CỤC BỘ của riêng máy
    /// này (false — chưa hỏi được Hub / bản chạy không có Hub). Ghi chú cạnh ô lọc ngày bám cờ này để người dùng
    /// biết mình đang xem con số nào. Bật ở <see cref="RefreshHubCountsAsync"/> khi hub trả lời được; hạ ở
    /// <see cref="ApplyHubCounts"/> khi không còn số hub nào áp được cho bối cảnh đang xem.</summary>
    [ObservableProperty]
    private bool _dangDungSoHub;

    /// <summary>Lượt hỏi Hub gần nhất của <see cref="RefreshHubCountsAsync"/> — lượt mới HỦY lượt cũ (khỏi tốn
    /// request cho ngày/tài khoản người dùng đã rời).</summary>
    private System.Threading.CancellationTokenSource? _hubCountsCts;

    /// <summary>Số thứ tự lượt hỏi Hub (chỉ đụng trên UI thread). Kết quả về mang số CŨ thì bỏ — chống lượt chậm
    /// ghi đè kết quả của lượt mới hơn.</summary>
    private int _hubCountsSeq;

    /// <summary>
    /// Số HUB lấy được ở lượt gần nhất (map <c>shop_login → số đơn</c>, khóa KHÔNG phân biệt hoa/thường) kèm bối
    /// cảnh của nó (<see cref="_hubCountsAccountId"/> + <see cref="_hubCountsDay"/>). null = chưa lấy được lần nào.
    /// <para><b>Vì sao phải nhớ:</b> <see cref="LoadResults"/> chạy lại sau MỖI đơn arrange xong
    /// (<see cref="OnPrepareCountChanged"/>) và dựng dòng bằng số CỤC BỘ. Không áp lại map này thì máy chạy SAU
    /// (cục bộ = 0, hub = 2) sẽ thấy số tụt về 0 giữa lượt rồi mới về 2 lúc xong shop — đúng triệu chứng cần sửa.</para>
    /// </summary>
    private IReadOnlyDictionary<string, int>? _hubCounts;

    /// <summary>Tài khoản mà <see cref="_hubCounts"/> thuộc về (chỉ áp khi trùng tài khoản đang mở).</summary>
    private long _hubCountsAccountId;

    /// <summary>Ngày (<c>yyyy-MM-dd</c>) mà <see cref="_hubCounts"/> thuộc về (chỉ áp khi trùng ngày đang lọc).</summary>
    private string? _hubCountsDay;

    /// <summary>Đổi NGÀY lọc ở tab "Kết quả" → nạp lại số chuẩn bị hàng của ngày mới (cục bộ trước, hub sau).</summary>
    partial void OnResultDateChanged(DateTimeOffset value)
    {
        LoadResults();
        _ = RefreshHubCountsAsync();
    }

    /// <summary>
    /// Quy tắc TỰ kéo ô ngày tab "Kết quả" sang ngày mới — hàm THUẦN (không đụng state, test được; đừng test
    /// bằng cách chờ timer). <paramref name="dangXem"/> = ngày ô lọc đang hiển thị, <paramref name="coiLaHomNay"/>
    /// = ngày mà lần dò TRƯỚC coi là hôm nay, <paramref name="homNay"/> = ngày của đồng hồ máy lúc dò.
    /// <list type="bullet">
    /// <item>Ngày chưa sang (<c>homNay == coiLaHomNay</c>) → không làm gì.</item>
    /// <item>Ngày đã sang và người dùng đang xem đúng "hôm nay (cũ)" → CHUYỂN sang <paramref name="homNay"/>.</item>
    /// <item>Ngày đã sang nhưng người dùng đang mở một ngày CŨ để đối chiếu → KHÔNG chuyển (giật ngày khỏi tay
    /// họ còn tệ hơn chính lỗi đang sửa). Mốc "coi là hôm nay" vẫn phải cập nhật ở bên gọi.</item>
    /// </list>
    /// Đồng hồ bị chỉnh LÙI (<c>homNay &lt; coiLaHomNay</c>) xử y hệt: đi theo đồng hồ máy, chuyển ĐÚNG MỘT lần
    /// rồi thôi — bên gọi cập nhật mốc ngay nên lần dò kế đã bằng nhau, không có vòng đổi qua đổi lại.
    /// </summary>
    public static (bool Chuyen, DateTime NgayMoi) QuyetDinhSangNgay(
        DateTime dangXem, DateTime coiLaHomNay, DateTime homNay)
    {
        var xem = dangXem.Date;
        var moc = coiLaHomNay.Date;
        var nay = homNay.Date;

        var chuyen = nay != moc && xem == moc;
        return (chuyen, chuyen ? nay : xem);
    }

    /// <summary>
    /// Dò sang ngày rồi áp kết quả của <see cref="QuyetDinhSangNgay"/>: kéo <see cref="ResultDate"/> sang
    /// <paramref name="homNay"/> khi người dùng đang xem "hôm nay", và LUÔN cập nhật mốc
    /// <see cref="_ngayCoiLaHomNay"/> (kể cả nhánh không chuyển — không thì hôm sau lần dò kế lại hiểu nhầm ngày
    /// người dùng chọn là "hôm nay").
    /// <para>Trả <c>true</c> khi ĐÃ đổi <see cref="ResultDate"/> — bên gọi biết lưới vừa được nạp lại rồi (setter
    /// kích <see cref="OnResultDateChanged"/> → <see cref="LoadResults"/> + <see cref="RefreshHubCountsAsync"/>)
    /// nên KHÔNG gọi lại tay, kẻo nạp hai lần và tốn thừa một lượt hỏi hub qua tunnel.</para>
    /// Nhận <paramref name="homNay"/> từ bên gọi (thay vì tự đọc đồng hồ) để test mô phỏng được lúc qua nửa đêm.
    /// Chỉ chạy trên UI thread (mọi bên gọi đã marshal qua <see cref="RunOnUi"/>).
    /// </summary>
    internal bool KiemTraSangNgay(DateTime homNay)
    {
        var nay = homNay.Date;
        if (nay == _ngayCoiLaHomNay)
        {
            return false; // chưa sang ngày → đường nóng, không đụng gì
        }

        var (chuyen, ngayMoi) = QuyetDinhSangNgay(ResultDate.Date, _ngayCoiLaHomNay, nay);

        // Cập nhật mốc TRƯỚC khi gán ResultDate: setter chạy ĐỒNG BỘ (LoadResults + có thể kéo theo sự kiện
        // khác), mọi đường chạy sau đó phải thấy mốc đã đúng ngày mới.
        _ngayCoiLaHomNay = nay;

        if (!chuyen)
        {
            return false; // người dùng đang mở ngày cũ để đối chiếu → KHÔNG giật ngày khỏi tay họ
        }

        // Gán mới KÍCH OnResultDateChanged (tự nạp lưới + hỏi hub) — cố ý không gọi LoadResults() ở đây.
        ResultDate = new DateTimeOffset(ngayMoi, DateTimeOffset.Now.Offset);
        return true;
    }

    /// <summary>Một nhịp của <see cref="_timerSangNgay"/> (THREAD NỀN) → marshal về UI thread rồi dò sang ngày.
    /// Nuốt lỗi: ngoại lệ lọt ra khỏi callback <see cref="System.Threading.Timer"/> sẽ GIẾT tiến trình, mà nhịp
    /// này chỉ là tiện ích (vd đang tắt app, không có dispatcher) — bỏ một nhịp thì nhịp sau dò lại.</summary>
    private void NhipSangNgay()
    {
        try
        {
            RunOnUi(() => KiemTraSangNgay(DateTimeOffset.Now.Date));
        }
        catch
        {
            // bỏ nhịp này
        }
    }

    /// <summary>
    /// Dựng lại <see cref="ResultRows"/> cho tab "Kết quả": MỌI shop của tài khoản đang chọn (từ
    /// <c>account_shops</c>) LEFT JOIN số đơn chuẩn bị hàng của <see cref="ResultDate"/> (từ <c>prepare_daily</c>) —
    /// shop không có đơn trong ngày → 0. Shop CÓ đơn trong ngày nhưng CHƯA có trong danh sách shop (lỡ đọc shop-list)
    /// → vẫn thêm để không sót. Chưa chọn tài khoản → lưới rỗng. Chạy trên UI thread (gọi từ setter/OnSelectedRowChanged).
    /// </summary>
    private void LoadResults()
    {
        // Bối cảnh vừa đổi (chọn tài khoản khác / đổi ngày lọc) → QUÊN số hub của bối cảnh cũ, kẻo áp nhầm.
        ClearHubCountsIfContextChanged();

        ResultRows.Clear();
        if (SelectedRow?.Id is not long accountId)
        {
            DangDungSoHub = false;
            CapNhatTongChuanBiHang();   // lưới rỗng → tổng phải về 0, đừng giữ số của tài khoản vừa xem
            LoadAddressAlertsFromLocal(); // clear banners khi bỏ chọn
            return;
        }

        var day = ResultDayKey;
        var shops = _services.Results.GetShops(accountId);
        var counts = _services.Results.GetPreparedByDay(accountId, day);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (shopLogin, shopName) in shops)
        {
            seen.Add(shopLogin);
            var name = string.IsNullOrWhiteSpace(shopName) ? shopLogin : shopName;
            ResultRows.Add(new ShopPrepareRow(name, shopLogin, counts.GetValueOrDefault(shopLogin, 0)));
        }

        // Shop có đơn nhưng chưa nằm trong account_shops → UNION thêm (nhãn = chính shop_login) để không sót đếm.
        foreach (var kv in counts)
        {
            if (!seen.Contains(kv.Key))
            {
                ResultRows.Add(new ShopPrepareRow(kv.Key, kv.Key, kv.Value));
            }
        }

        // Dòng vừa dựng lại là dòng MỚI (cờ tiến độ về mặc định) → áp lại tick/vòng quay. Bắt buộc: hàm này chạy
        // sau MỖI đơn chuẩn bị xong (PrepareCountChanged), thiếu bước này là tick nhấp nháy/biến mất khi đang chạy.
        ApplyShopCheckFlags();
        ApplyAddressErrorFlags();

        // Số vừa dựng ở trên là số CỤC BỘ → áp ĐÈ lại số HUB đã lấy được (nếu còn đúng bối cảnh). Bắt buộc vì
        // cùng lý do với ApplyShopCheckFlags: hàm này chạy sau MỖI đơn, thiếu bước này là số nhảy về số của máy.
        ApplyHubCounts();
    }

    /// <summary>
    /// Gắn <see cref="ShopPrepareRow.CoLoiDiaChi"/> theo banner lỗi địa chỉ còn active của tài khoản đang mở.
    /// Gọi sau LoadResults / sau nạp banner / sau dismiss X.
    /// </summary>
    private void ApplyAddressErrorFlags()
    {
        HashSet<string>? alertShops = null;
        if (SelectedRow?.Id is long accountId)
        {
            try
            {
                alertShops = new HashSet<string>(
                    _services.PickupAlerts.ListActive(accountId).Select(a => a.ShopLogin),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                // Best-effort: DB lỗi thì không gắn cờ X — banner vẫn hiện từ AddressAlertRows nếu đã nạp được.
            }
        }

        foreach (var row in ResultRows)
        {
            row.CoLoiDiaChi = alertShops is not null && alertShops.Contains(row.ShopLogin);
        }
    }

    /// <summary>Ngày đang lọc ở tab "Kết quả" dưới dạng KHÓA <c>yyyy-MM-dd</c> — dùng chung cho <c>prepare_daily</c>,
    /// câu hỏi gửi hub và bộ nhớ <see cref="_hubCountsDay"/> (một chỗ định dạng, không lệch nhau).</summary>
    private string ResultDayKey
        => ResultDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Áp map hub đã nhớ (<see cref="_hubCounts"/>) lên <see cref="ResultRows"/> khi nó còn ĐÚNG tài khoản đang mở
    /// + ĐÚNG ngày đang lọc: shop KHÔNG có trong map → <b>0</b> (hub là nguồn sự thật, không phải "không biết").
    /// Giữ nguyên <see cref="DangDungSoHub"/> ở nhánh áp được (cờ do <see cref="RefreshHubCountsAsync"/> quyết);
    /// không có map / lệch bối cảnh → để nguyên số cục bộ và hạ cờ. Chạy trên UI thread.
    /// </summary>
    private void ApplyHubCounts()
    {
        if (_hubCounts is not { } map
            || SelectedRow?.Id != _hubCountsAccountId
            || !string.Equals(_hubCountsDay, ResultDayKey, StringComparison.Ordinal))
        {
            DangDungSoHub = false;
            CapNhatTongChuanBiHang();   // giữ số cục bộ vừa dựng ở LoadResults → tổng phải khớp lại
            return;
        }

        foreach (var row in ResultRows)
        {
            row.PreparedCount = map.TryGetValue(row.ShopLogin.Trim(), out var soDon) ? soDon : 0;
        }
        CapNhatTongChuanBiHang();
    }

    /// <summary>Quên map hub đã nhớ khi bối cảnh KHÔNG còn khớp (đổi tài khoản / đổi ngày lọc / bỏ chọn). Chỉ dọn
    /// khi thật sự lệch — chọn lại ĐÚNG tài khoản đang xem thì giữ map, khỏi nháy về số cục bộ rồi mới về số hub.</summary>
    private void ClearHubCountsIfContextChanged()
    {
        if (_hubCounts is null)
        {
            return;
        }
        if (SelectedRow?.Id != _hubCountsAccountId
            || !string.Equals(_hubCountsDay, ResultDayKey, StringComparison.Ordinal))
        {
            _hubCounts = null;
            _hubCountsAccountId = 0;
            _hubCountsDay = null;
        }
    }

    /// <summary>
    /// Hỏi HUB số đơn "chuẩn bị hàng" CHUNG TOÀN HỆ THỐNG của ngày đang lọc rồi đổ vào lưới — hub đếm từ bảng đơn
    /// nên máy A chạy trước, máy B chạy sau vẫn thấy CÙNG một con số.
    /// <list type="bullet">
    /// <item>Có kết quả → NHỚ map (<see cref="_hubCounts"/>) rồi gán lại <see cref="ShopPrepareRow.PreparedCount"/>;
    /// shop KHÔNG có trong map → <b>0</b> (hub là nguồn sự thật, không phải "không biết").
    /// <see cref="DangDungSoHub"/> = true.</item>
    /// <item>Trả <c>null</c> (chưa kết nối / hub lỗi / hook chưa rót) → KHÔNG đụng lưới, chỉ hạ
    /// <see cref="DangDungSoHub"/>. Map đã nhớ được GIỮ (xem chú thích trong thân hàm).</item>
    /// </list>
    /// Gọi ở ĐÚNG 4 mốc (chọn tài khoản · đổi ngày lọc · phiên đọc xong danh sách shop · xong MỘT shop) — KHÔNG
    /// gọi theo từng đơn (<see cref="OnPrepareCountChanged"/>) kẻo spam hub. Chạy nền; kết quả marshal về UI thread
    /// và chỉ áp khi vẫn là lượt MỚI NHẤT (xem <see cref="_hubCountsSeq"/>) và bối cảnh chưa đổi.
    /// </summary>
    public async Task RefreshHubCountsAsync()
    {
        if (_services.QueryPrepareStats is not { } hook)
        {
            return; // bản chạy KHÔNG có hub (hook chưa rót) → giữ nguyên hành vi cũ, số của máy
        }
        if (SelectedRow?.Id is not long accountId)
        {
            return; // chưa chọn tài khoản → lưới rỗng, khỏi phiền hub
        }

        var day = ResultDayKey;

        // Lượt MỚI hủy lượt CŨ + tăng số thứ tự. Cả hai chỉ đụng ở đây (trước await) nên vẫn trên UI thread.
        var cts = new System.Threading.CancellationTokenSource();
        var truoc = _hubCountsCts;
        _hubCountsCts = cts;
        if (truoc is not null)
        {
            try { truoc.Cancel(); } catch { /* lượt cũ đã xong/đã dispose */ }
            truoc.Dispose();
        }
        var seq = ++_hubCountsSeq;

        IReadOnlyDictionary<string, int>? map;
        try
        {
            map = await hook(day, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // lượt sau đã đè lượt này → bỏ hẳn, KHÔNG đụng lưới
        }
        catch
        {
            map = null; // hub lỗi ngoài dự kiến → coi như "không hỏi được", giữ số cục bộ
        }

        RunOnUi(() =>
        {
            if (seq != _hubCountsSeq)
            {
                return; // đã có lượt mới hơn → không để kết quả cũ ghi đè kết quả mới
            }
            if (SelectedRow?.Id != accountId || !string.Equals(ResultDayKey, day, StringComparison.Ordinal))
            {
                return; // người dùng đã đổi tài khoản / đổi ngày trong lúc chờ → kết quả không còn đúng lưới
            }

            if (map is null)
            {
                // KHÔNG hỏi được hub → giữ số đang hiện + ghi chú cho người dùng biết. CỐ Ý không xoá
                // _hubCounts: hub chớp tắt giữa lượt mà quên số đã lấy thì lượt LoadResults kế lại kéo lưới
                // về số cục bộ (0 trên máy chạy sau) — đúng cái đang phải sửa.
                DangDungSoHub = false;
                return;
            }

            // Nhớ lại để LoadResults (chạy sau MỖI đơn) áp đè lên số cục bộ, khỏi nhảy số giữa lượt.
            // Khóa chuẩn hóa KHÔNG phân biệt hoa/thường ngay tại đây — không tin comparer của bên gọi.
            _hubCounts = ChuanHoaKhoaShop(map);
            _hubCountsAccountId = accountId;
            _hubCountsDay = day;
            DangDungSoHub = true;
            ApplyHubCounts();
        });
    }

    /// <summary>
    /// Dựng lại map hub thành từ điển khóa <see cref="StringComparer.OrdinalIgnoreCase"/> (khóa đã Trim). Nhãn shop
    /// giữa <c>account_shops</c> của máy và <c>shops.username</c> trên hub có thể lệch HOA/thường; tra theo
    /// <see cref="StringComparer.Ordinal"/> sẽ ra 0 một cách LẶNG (không lỗi, không log) — loại sai khó phát hiện
    /// nhất. Cùng quy tắc so khớp với <see cref="MatchesShopLabel"/>.
    /// <para>Khóa trùng nhau sau khi bỏ hoa/thường → CỘNG DỒN, không phải "bản sau thắng": hai dòng hub lệch
    /// HOA/thường là CÙNG một shop vật lý bị tách đôi, lấy mỗi bản sau là MẤT số của bản kia. Vẫn giữ phép cộng
    /// này kể cả khi hub đã gộp shop trùng — client có thể đang nói chuyện với hub chưa nâng cấp.</para>
    /// </summary>
    private static Dictionary<string, int> ChuanHoaKhoaShop(IReadOnlyDictionary<string, int> map)
    {
        var chuan = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in map)
        {
            if (!string.IsNullOrWhiteSpace(kv.Key))
            {
                var khoa = kv.Key.Trim();
                chuan.TryGetValue(khoa, out var dangCo);
                chuan[khoa] = dangCo + kv.Value;
            }
        }
        return chuan;
    }

    /// <summary>
    /// Áp cờ cột tiến độ (<see cref="ShopPrepareRow.IsChecking"/>/<see cref="ShopPrepareRow.DaKiemTra"/>) lên các
    /// dòng đang hiển thị theo shop mà phiên của TÀI KHOẢN ĐANG MỞ đang check + tập shop đã check xong lượt này.
    /// Không có tài khoản đang mở / tài khoản đó chưa chạy shop nào → xóa sạch cờ. Chạy trên UI thread.
    /// </summary>
    private void ApplyShopCheckFlags()
    {
        (string ShopLabel, bool IsChecking) state = default;
        HashSet<string>? daCheck = null;
        var hasState = false;
        if (SelectedRow?.Id is long accountId)
        {
            hasState = _shopCheck.TryGetValue(accountId, out state);
            _shopDaCheck.TryGetValue(accountId, out daCheck);
        }

        foreach (var row in ResultRows)
        {
            var current = hasState && MatchesShopLabel(row, state.ShopLabel);
            row.IsChecking = current && state.IsChecking;
            row.DaKiemTra = DaKiemTraShop(daCheck, row);
        }
    }

    /// <summary>
    /// Dòng lưới có nằm trong tập shop ĐÃ kiểm tra xong của lượt chạy (<paramref name="daCheck"/>) không. Phải
    /// DUYỆT tập rồi khớp bằng <see cref="MatchesShopLabel"/> (không tra khóa trực tiếp): nhãn phiên gửi về có thể
    /// lệch hoa/thường - khoảng trắng so với <c>account_shops</c>, và dòng lưới còn khớp được qua tên hiển thị.
    /// Tài khoản chưa có tập nào → false cho mọi dòng.
    /// </summary>
    private static bool DaKiemTraShop(HashSet<string>? daCheck, ShopPrepareRow row)
    {
        if (daCheck is null)
        {
            return false;
        }

        foreach (var label in daCheck)
        {
            if (MatchesShopLabel(row, label))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Dòng lưới có ứng với nhãn shop <paramref name="label"/> phiên báo về không. Nhãn phiên gửi là KHÓA shop
    /// (<c>LoginName</c>, rỗng thì <c>ShopName</c>) nên khớp <see cref="ShopPrepareRow.ShopLogin"/> là chính; vẫn
    /// nhận cả <see cref="ShopPrepareRow.ShopName"/> phòng dữ liệu cũ lưu lệch. So sánh bỏ khoảng trắng thừa +
    /// KHÔNG phân biệt hoa/thường (nhãn từ phiên và tên trong <c>account_shops</c> có thể lệch hoa/thường).
    /// </summary>
    private static bool MatchesShopLabel(ShopPrepareRow row, string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        return Same(row.ShopLogin, label) || Same(row.ShopName, label);

        static bool Same(string? a, string? b)
            => !string.IsNullOrWhiteSpace(a)
               && string.Equals(a.Trim(), b!.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Phiên vừa đọc được danh sách shop của một tài khoản → dựng lại lưới tab "Kết quả" nếu ĐÚNG tài khoản
    /// đang mở. KHÔNG lọc theo ngày (khác <see cref="OnPrepareCountChanged"/>): danh sách shop không phụ thuộc
    /// ngày, xem ngày cũ vẫn phải thấy đủ shop.
    /// <para>Cũng là mốc RESET dấu tick: đọc xong danh sách shop = phiên sắp lặp qua từng shop, tức LƯỢT CHẠY MỚI
    /// bắt đầu → xóa tập shop "đã kiểm tra" của tài khoản đó để lượt mới không kế thừa tick của lượt trước.</para>
    /// </summary>
    private void OnShopListChanged(long accountId) => RunOnUi(() =>
    {
        _shopDaCheck.Remove(accountId);

        if (SelectedRow is not null && SelectedRow.Id == accountId)
        {
            LoadResults();
            _ = RefreshHubCountsAsync(); // lượt chạy mới bắt đầu → xin số chung mới nhất từ hub
        }
    });

    /// <summary>
    /// Số "chuẩn bị hàng" của một tài khoản vừa tăng → nạp lại lưới tab "Kết quả" nếu ĐÚNG tài khoản đang mở.
    /// Tài khoản khác thì bỏ qua (đang chạy nhiều tk cùng lúc mà nạp hết là phí). Sự kiện đến từ THREAD NỀN của
    /// phiên → marshal về UI thread trước khi đụng <c>ResultRows</c>.
    /// <para>Dò sang ngày (<see cref="KiemTraSangNgay"/>) TRƯỚC mọi điều kiện: đơn vừa chuẩn bị luôn thuộc hôm
    /// nay, nên máy chạy xuyên đêm thì chính đơn ĐẦU TIÊN của ngày mới đã kéo ô ngày sang — không phải chờ hết
    /// một nhịp timer. Kéo được rồi thì thoát luôn: setter <see cref="ResultDate"/> đã nạp lưới + hỏi hub.</para>
    /// <para>Còn lại vẫn chỉ nạp khi ngày đang lọc là HÔM NAY: người dùng chủ động mở ngày cũ thì lưới của họ
    /// không đổi — nạp lại chỉ tổ nháy màn.</para>
    /// </summary>
    private void OnPrepareCountChanged(long accountId) => RunOnUi(() =>
    {
        if (KiemTraSangNgay(DateTimeOffset.Now.Date))
        {
            return; // ô ngày vừa sang ngày mới → OnResultDateChanged đã nạp lưới + hỏi hub, khỏi làm lại
        }

        if (SelectedRow is null || SelectedRow.Id != accountId)
        {
            return;
        }
        if (ResultDate.Date != DateTimeOffset.Now.Date)
        {
            return;
        }
        LoadResults();
    });

    /// <summary>
    /// Phiên vừa BẮT ĐẦU (<paramref name="checking"/> = true) hoặc XONG (false) việc check shop
    /// <paramref name="shopLabel"/> của tài khoản <paramref name="accountId"/> → cập nhật cột tiến độ tab "Kết quả".
    /// <list type="bullet">
    /// <item>bắt đầu → nhớ shop mới ⇒ vòng quay CHUYỂN sang shop đó ngay;</item>
    /// <item>xong → tắt vòng quay + ghi nhãn shop vào tập "đã kiểm tra" của lượt chạy ⇒ shop đó nhận dấu tick và
    /// GIỮ tick tới hết lượt (kể cả shop lỗi/captcha/bỏ qua — vẫn tính là đã kiểm tra qua).</item>
    /// </list>
    /// Nhớ cho MỌI tài khoản nhưng chỉ vẽ lại lưới khi đúng tài khoản đang mở. Sự kiện đến từ THREAD NỀN của
    /// phiên → marshal về UI thread trước khi đụng <c>ResultRows</c>.
    /// </summary>
    private void OnShopCheckChanged(long accountId, string shopLabel, bool checking) => RunOnUi(() =>
    {
        if (string.IsNullOrWhiteSpace(shopLabel))
        {
            return;
        }

        if (checking)
        {
            _shopCheck[accountId] = (shopLabel, true);
        }
        else if (_shopCheck.TryGetValue(accountId, out var prev))
        {
            // Xong shop: GIỮ nhãn đang nhớ, chỉ hạ cờ đang-check.
            _shopCheck[accountId] = (prev.ShopLabel, false);
        }
        else
        {
            _shopCheck[accountId] = (shopLabel, false);
        }

        if (!checking)
        {
            // Shop vừa xong → vào tập "đã kiểm tra" của lượt chạy (nguồn dấu tick), tạo tập nếu tài khoản chưa có.
            if (!_shopDaCheck.TryGetValue(accountId, out var daCheck))
            {
                daCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _shopDaCheck[accountId] = daCheck;
            }
            daCheck.Add(shopLabel);
        }

        if (SelectedRow?.Id == accountId)
        {
            ApplyShopCheckFlags();
            if (!checking)
            {
                // XONG một shop = đúng nhịp người dùng mong đợi thấy số mới → hỏi hub một lần. KHÔNG hỏi lúc
                // BẮT ĐẦU (số chưa đổi) và tuyệt đối không hỏi theo từng đơn (spam hub).
                _ = RefreshHubCountsAsync();
            }
        }
    });

    /// <summary>Phiên vừa ghi/đóng banner địa chỉ → nạp lại nếu đúng tài khoản đang mở.</summary>
    private void OnAddressAlertsChanged(long accountId) => RunOnUi(() =>
    {
        if (SelectedRow?.Id == accountId)
        {
            LoadAddressAlertsFromLocal();
        }
    });

    /// <summary>Đọc banner active từ SQLite local vào <see cref="AddressAlertRows"/> (không đụng ResultDate).</summary>
    private void LoadAddressAlertsFromLocal()
    {
        AddressAlertRows.Clear();
        if (SelectedRow?.Id is not long accountId)
        {
            return;
        }

        foreach (var a in _services.PickupAlerts.ListActive(accountId))
        {
            AddressAlertRows.Add(new PickupAlertRow(a.ShopLogin));
        }

        ApplyAddressErrorFlags();
    }

    /// <summary>
    /// Kéo Hub rồi merge: Hub dismiss → local dismiss; Hub active → local upsert. Sau đó nạp lại UI.
    /// Best-effort — Hub null/offline giữ local.
    /// </summary>
    private async Task SyncAddressAlertsFromHubAsync()
    {
        if (SelectedRow?.Id is not long accountId)
        {
            return;
        }

        var accountLogin = SelectedRow.Email?.Trim() ?? "";
        var fetch = _services.FetchPickupAlertsFromHub;
        if (fetch is null || accountLogin.Length == 0)
        {
            LoadAddressAlertsFromLocal();
            return;
        }

        IReadOnlyList<(string ShopLogin, string Province, bool Dismissed)>? hub;
        try
        {
            hub = await fetch(accountLogin, default).ConfigureAwait(false);
        }
        catch
        {
            hub = null;
        }

        RunOnUi(() =>
        {
            if (SelectedRow?.Id != accountId)
            {
                return;
            }

            if (hub is not null)
            {
                foreach (var (shop, province, dismissed) in hub)
                {
                    if (string.IsNullOrWhiteSpace(shop))
                    {
                        continue;
                    }

                    if (dismissed)
                    {
                        _services.PickupAlerts.Dismiss(accountId, shop);
                    }
                    else
                    {
                        _services.PickupAlerts.Upsert(accountId, shop, province);
                    }
                }
            }

            LoadAddressAlertsFromLocal();
        });
    }

    /// <summary>
    /// Login cố định cho tình huống TEST khi tài khoản chưa có shop nào trên lưới Kết quả.
    /// Shop đầu = lỗi địa chỉ; shop hai = vẫn chạy được (để thấy bỏ qua + tiếp tục).
    /// </summary>
    internal const string TestShopDauLoiDiaChi = "__test_dia_chi_1.store";
    internal const string TestShopHaiOk = "__test_dia_chi_2.store";

    /// <summary>Cờ TEST: vòng chạy kế tiếp ép shop đầu lỗi địa chỉ (một lần, tự tắt sau khi tiêu thụ).</summary>
    public bool EpShopDauLoiDiaChi
    {
        get => _services.Settings.GetForceFirstShopPickupFail();
        set
        {
            if (_services.Settings.GetForceFirstShopPickupFail() == value)
            {
                return;
            }

            _services.Settings.SetForceFirstShopPickupFail(value);
            OnPropertyChanged(nameof(EpShopDauLoiDiaChi));
        }
    }

    /// <summary>
    /// Giả lập NGAY trên UI (không cần chạy vòng Shopee): shop đầu lỗi địa chỉ → dừng shop đó (không in phiếu),
    /// hiện banner + dấu X đỏ; nếu có shop hai thì giả lập tiếp tục check shop đó.
    /// </summary>
    [RelayCommand]
    private async Task GiaLapLoiDiaChiShopDauAsync()
    {
        if (SelectedRow?.Id is not long accountId)
        {
            return;
        }

        DetailTabIndex = 1; // nhảy sang tab Kết quả để thấy banner + X

        // Chưa có shop trên lưới → dựng 2 shop TEST (đầu lỗi, hai ok).
        if (ResultRows.Count == 0)
        {
            _services.Results.UpsertShops(accountId, new[]
            {
                new ShopListItem("test-1", "TEST shop đầu (lỗi địa chỉ)", TestShopDauLoiDiaChi),
                new ShopListItem("test-2", "TEST shop hai (ok)", TestShopHaiOk),
            });
            LoadResults();
        }

        if (ResultRows.Count == 0)
        {
            return;
        }

        var shopDau = ResultRows[0].ShopLogin;
        var shopHai = ResultRows.Count > 1 ? ResultRows[1].ShopLogin : null;
        var province = string.IsNullOrWhiteSpace(SelectedRow.Account.PickupAddress)
            ? "Thanh Hóa"
            : SelectedRow.Account.PickupAddress!.Trim();
        var email = SelectedRow.Email ?? "";

        void Log(string m)
        {
            if (email.Length > 0)
            {
                _services.Log.Append(email, m);
            }
        }

        // 1) Đang check shop đầu → rồi xong (bỏ qua vì địa chỉ).
        _services.RaiseShopCheckChanged(accountId, shopDau, checking: true);
        await Task.Delay(450).ConfigureAwait(true);
        _services.RaiseShopCheckChanged(accountId, shopDau, checking: false);

        Log($"⛔ [TEST] Không đặt được địa chỉ lấy hàng ({province}) — BỎ QUA shop {shopDau}, "
            + "KHÔNG in phiếu; sang shop kế (nếu còn).");

        // 2) Cùng đường production: GhiBanner → AddressAlertsChanged → banner + dấu X.
        new OrderPersistPipeline(accountId, _services)
            .GhiBannerLoiDiaChi(shopDau, province, Log, CancellationToken.None);

        // 3) Shop hai vẫn chạy (nếu có) — chứng minh chỉ dừng shop lỗi, không dừng cả vòng.
        if (shopHai is not null)
        {
            await Task.Delay(350).ConfigureAwait(true);
            _services.RaiseShopCheckChanged(accountId, shopHai, checking: true);
            Log($"[TEST] Tiếp tục shop kế {shopHai}...");
            await Task.Delay(700).ConfigureAwait(true);
            _services.RaiseShopCheckChanged(accountId, shopHai, checking: false);
            Log($"[TEST] Xong giả lập: shop {shopDau} có banner + X đỏ; shop {shopHai} đã chạy tiếp.");
        }
        else
        {
            Log($"[TEST] Xong giả lập: shop {shopDau} có banner + X đỏ (không còn shop kế trên lưới).");
        }

        // Đồng bộ cờ checkbox (nếu user vừa bật ép vòng thật — không đụng ở đây).
        OnPropertyChanged(nameof(EpShopDauLoiDiaChi));
    }

    /// <summary>Bấm X trên một dòng banner — dismiss local + Hub; chỉ dòng đó biến mất.</summary>
    [RelayCommand]
    private void DismissAddressAlert(PickupAlertRow? row)
    {
        if (row is null || SelectedRow?.Id is not long accountId)
        {
            return;
        }

        _services.PickupAlerts.Dismiss(accountId, row.ShopLogin);
        AddressAlertRows.Remove(row);
        ApplyAddressErrorFlags();

        var accountLogin = SelectedRow.Email?.Trim() ?? "";
        var dismissHub = _services.DismissPickupAlertToHub;
        if (dismissHub is null || accountLogin.Length == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await dismissHub(accountLogin, row.ShopLogin, default).ConfigureAwait(false);
            }
            catch
            {
                // offline — local đã dismiss; máy khác sẽ lệch tới khi Hub nhận được lần sau
            }
        });
    }
}
