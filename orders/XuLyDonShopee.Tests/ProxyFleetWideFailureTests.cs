using Shopee.Core.Proxy;
using Shopee.Modules.MultiBrave;

namespace XuLyDonShopee.Tests;

/// <summary>
/// Lỗi HẠ TẦNG TOÀN CỤC (key KiotProxy chết) vs lỗi proxy TẠM THỜI của một tk.
/// <list type="bullet">
/// <item>Sự cố thật 2026-07-27: key hết hạn → cả 156 tk Shopee (dùng CHUNG một key) đều fail, nhưng runner
/// coi là lỗi tạm → cho tk nghỉ, vá bằng tk khác, rồi BỎ QUA dòng: 17 dòng thủng trong 6 phút mà job vẫn
/// báo "đang chạy".</item>
/// <item><b>BẤT BIẾN</b>: danh sách khớp phải HẸP. Bắt nhầm một trục trặc proxy lẻ thành lỗi toàn cục sẽ
/// giết oan job đang chạy tốt — nguy hiểm hơn chính cái bug đang chữa ⇒ ca ÂM TÍNH quan trọng ngang ca dương.</item>
/// </list>
/// Thuần hàm tĩnh — không cần proxy, không cần Brave.
/// </summary>
public class ProxyFleetWideFailureTests
{
    /// <summary>Chuỗi NGUYÊN VĂN từ log sự cố (KiotProxyClient.GetNewProxyAsync ném ra, ScrapeRunner bắt).</summary>
    private const string LogThat =
        "KiotProxy new 400: Key proxy đã hết hạn, vui lòng gia hạn để tiếp tục sử dụng | KEY_EXPIRED";

    // ===== 1. DƯƠNG TÍNH: key/tài khoản proxy chết =====
    [Fact]
    public void ChuoiLoiThatTuLog_LaLoiToanCuc()
    {
        Assert.True(ProxyFailure.IsFleetWideProxyFailure(LogThat));
    }

    [Theory]
    [InlineData("KEY_EXPIRED")]
    [InlineData("key_expired")]                                   // không phân biệt hoa/thường
    [InlineData("KiotProxy current 400: ... | KEY_NOT_FOUND")]
    [InlineData("Key proxy đã hết hạn")]
    [InlineData("Vui lòng gia hạn để tiếp tục sử dụng")]
    public void MaLoiVaCauThongBaoCuaKiotProxy_LaLoiToanCuc(string reason)
    {
        Assert.True(ProxyFailure.IsFleetWideProxyFailure(reason));
    }

    /// <summary>"hết hạn"/"het han" chỉ tính khi câu lỗi có nhắc tới "key" (key proxy).</summary>
    [Theory]
    [InlineData("KiotProxy: key da het han")]
    [InlineData("Key proxy hết hạn")]
    public void HetHan_DiKemChuKey_LaLoiToanCuc(string reason)
    {
        Assert.True(ProxyFailure.IsFleetWideProxyFailure(reason));
    }

    // ===== 2. ÂM TÍNH: lỗi lẻ/tạm thời — KHÔNG được giết job =====
    /// <summary>Proxy LẺ không tìm thấy: câu này CÓ chữ "key" nhưng là lỗi của một IP, cooldown + đổi tk là chữa
    /// được. Đây là ca dễ bắt nhầm nhất.</summary>
    [Theory]
    [InlineData("KiotProxy current 400: Could not find the proxy being used by key | PROXY_NOT_FOUND_BY_KEY")]
    [InlineData("PROXY_NOT_FOUND_BY_KEY")]
    public void ProxyLeKhongTimThay_KHONG_PhaiLoiToanCuc(string reason)
    {
        Assert.False(ProxyFailure.IsFleetWideProxyFailure(reason));
    }

    /// <summary>"hết hạn" của phiên đăng nhập (Shopee/BigSeller) KHÔNG dính dáng tới key proxy.</summary>
    [Theory]
    [InlineData("Phiên đăng nhập hết hạn, vui lòng đăng nhập lại")]
    [InlineData("Phien dang nhap het han")]
    [InlineData("Session expired")]
    public void PhienDangNhapHetHan_KHONG_PhaiLoiToanCuc(string reason)
    {
        Assert.False(ProxyFailure.IsFleetWideProxyFailure(reason));
    }

    [Theory]
    [InlineData("Captcha/verify")]
    [InlineData("Lỗi proxy")]
    [InlineData("KiotProxy vẫn trả về proxy cũ đang lỗi: http://1.2.3.4:8080")]
    [InlineData("ERR_PROXY_CONNECTION_FAILED")]
    [InlineData("Log in BigSeller first")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void LoiThuongVaChuoiRong_KHONG_PhaiLoiToanCuc(string? reason)
    {
        Assert.False(ProxyFailure.IsFleetWideProxyFailure(reason));
    }

    // ===== 3. Luật quyết định của ScrapeRunner =====
    /// <summary>Lỗi toàn cục → DỪNG JOB: KHÔNG cooldown, KHÔNG sinh việc vá, KHÔNG bỏ qua dòng nào.</summary>
    [Fact]
    public void Decide_LoiToanCuc_DungJob()
    {
        Assert.Equal(ScrapeFailureAction.StopJob,
            ScrapeFailurePolicy.Decide(errored: true, isCaptcha: false, reason: LogThat));
    }

    /// <summary>Hồi quy: lỗi proxy THƯỜNG vẫn cooldown (15s/90s) + vá bằng tk khác y như cũ.</summary>
    [Theory]
    [InlineData("Lỗi proxy")]
    [InlineData("KiotProxy current 400: Could not find the proxy being used by key | PROXY_NOT_FOUND_BY_KEY")]
    [InlineData("Không lấy được proxy.")]
    public void Decide_LoiProxyThuong_VanCooldown(string reason)
    {
        Assert.Equal(ScrapeFailureAction.Cooldown,
            ScrapeFailurePolicy.Decide(errored: true, isCaptcha: false, reason: reason));
    }

    /// <summary>Captcha giữ nguyên đường cũ (quarantine) — kể cả khi thông điệp có lẫn chữ key hết hạn.</summary>
    [Fact]
    public void Decide_Captcha_VanQuarantine()
    {
        Assert.Equal(ScrapeFailureAction.Quarantine,
            ScrapeFailurePolicy.Decide(errored: true, isCaptcha: true, reason: "Captcha/verify"));
        Assert.Equal(ScrapeFailureAction.Quarantine,
            ScrapeFailurePolicy.Decide(errored: true, isCaptcha: true, reason: LogThat));
    }

    [Fact]
    public void Decide_KhongLoi_TraTkVeKho()
    {
        Assert.Equal(ScrapeFailureAction.None,
            ScrapeFailurePolicy.Decide(errored: false, isCaptcha: false, reason: ""));
        // Chunk KHÔNG lỗi thì chuỗi lỗi cũ (nếu có) không được biến thành dừng job.
        Assert.Equal(ScrapeFailureAction.None,
            ScrapeFailurePolicy.Decide(errored: false, isCaptcha: false, reason: LogThat));
    }

    // ===== 4. Lý do báo lên Hub: NGẮN + người đọc hiểu ngay (hiện thẳng trên ô trang Giao việc) =====
    [Fact]
    public void FleetWideReason_KeyHetHan_NeuRoMaLoi()
    {
        var reason = ScrapeFailurePolicy.FleetWideReason(LogThat);
        Assert.Contains("key proxy hết hạn", reason);
        Assert.Contains("KEY_EXPIRED", reason);
        Assert.True(reason.Length <= 80, $"lý do phải NGẮN để hiện vừa ô: {reason}");
    }

    [Fact]
    public void FleetWideReason_KeyKhongTonTai_NoiDungKhac()
    {
        var reason = ScrapeFailurePolicy.FleetWideReason("KiotProxy new 400: ... | KEY_NOT_FOUND");
        Assert.Contains("KEY_NOT_FOUND", reason);
        Assert.DoesNotContain("hết hạn", reason);
    }

    [Fact]
    public void FleetWideReason_KhongRoMaLoi_VanCoLyDoDocDuoc()
    {
        var reason = ScrapeFailurePolicy.FleetWideReason("Key proxy đã hết hạn");
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Contains("key proxy", reason, StringComparison.OrdinalIgnoreCase);
    }
}
