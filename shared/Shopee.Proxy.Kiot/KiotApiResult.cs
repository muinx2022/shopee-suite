namespace Shopee.Proxy.Kiot;

/// <summary>
/// Kết quả một lần gọi API KiotProxy. Client dùng chung KHÔNG BAO GIỜ ném vì lỗi
/// HTTP/parse/timeout: mọi lỗi được biểu diễn bằng <see cref="Success"/> = <c>false</c>
/// kèm <see cref="Message"/> (và <see cref="HttpStatus"/> nếu có phản hồi HTTP).
/// Adapter của từng module tự quyết định nuốt lỗi (orders → trả null) hay ném theo
/// hợp đồng thông điệp (suite, phase 2b).
/// </summary>
/// <param name="Success">
/// <c>true</c> khi phản hồi hợp lệ về mặt giao thức: HTTP 2xx, JSON đọc được,
/// <c>success</c> KHÔNG bằng false và <c>status</c> KHÔNG bằng "FAIL".
/// </param>
/// <param name="Message">Thông điệp lỗi (từ <c>message</c>/<c>error</c>) hoặc lý do thất bại; null nếu không có.</param>
/// <param name="Data">Dữ liệu proxy đã bóc; null khi thất bại.</param>
/// <param name="HttpStatus">Mã HTTP của phản hồi; null khi lỗi mạng/timeout (chưa có phản hồi).</param>
public sealed record KiotApiResult(bool Success, string? Message, KiotProxyInfo? Data, int? HttpStatus);
