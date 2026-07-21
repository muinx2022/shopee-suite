# Plan: Gộp phase 2a — project chung Shopee.Proxy.Kiot + chuyển module đơn hàng sang dùng

- **Ngày:** 2026-07-21
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Nơi làm:** cây làm việc CHÍNH `D:\Projects\shopee-suite`, nhánh `feature/gop-don-hang`.
  KHÔNG đụng `main`. Có một agent khác đang làm trong worktree riêng (`../shopee-suite-wt-profile`)
  — không liên quan, không đọc/ghi sang đó.

## 1. Bối cảnh & mục tiêu

Phase 2 roadmap = hợp nhất lớp proxy KiotProxy. Khảo sát (2026-07-21) kết luận:
- Hai bên gọi CÙNG API kiotproxy.com (`/api/v1/proxies/new?key=…&region=…`,
  `/api/v1/proxies/current?key=…`) nhưng bằng 3 bộ code khác nhau:
  - suite "KiotResult" (`suite/Shopee.Core/Proxy/KiotProxyClient.cs:10-91`): static HttpClient,
    nuốt lỗi trả `Error`, region /new hard-code `random`, dùng `nextRequestAt`.
  - suite "Dictionary" (cùng file, dòng 101-213): NÉM InvalidOperationException với message
    làm HỢP ĐỒNG cho `BraveInstanceSession.IsProxyExpiredError` (MultiBrave); trả Dictionary
    thô cho fingerprint.
  - orders (`orders/XuLyDonShopee.Core/Services/KiotProxyClient.cs`): instance, inject được
    HttpClient + baseUrl (testable), nuốt lỗi → null, `/current` kiểm `expirationAt` còn sống,
    map ra `ProxyEntry` qua `ProxyParser`.
- Ngữ nghĩa POOL 3 bộ trái ngược (suite ProxyPool: IP mới + gate nextRequestAt; suite
  KiotProxyPoolStore: ánh xạ vị trí, đồng bộ Hub; orders: key/phiên suốt đời + IP sticky +
  watchdog) → GIỮ NGUYÊN cả 3, KHÔNG hợp nhất pool.

**Phase 2a (plan này):** tạo project chung `shared/Shopee.Proxy.Kiot` chứa HTTP client + parser
duy nhất, rồi chuyển `KiotProxyClient` của orders thành adapter mỏng trên nó — orders có 720
test bảo vệ nên làm trước. **Phase 2b (sau, plan riêng):** chuyển 2 dạng gọi của suite thành
adapter — cần người dùng nghiệm thu runtime (MultiBrave/Search/CheckAccount không có test).

## 2. Phạm vi

- **Làm:**
  - Tạo `shared/Shopee.Proxy.Kiot/` (project mới) + đưa vào `ShopeeSuite.sln` (solution folder `shared`).
  - Viết lại RUỘT `orders/XuLyDonShopee.Core/Services/KiotProxyClient.cs` thành adapter gọi
    client chung — BỀ MẶT PUBLIC GIỮ NGUYÊN.
  - `orders/XuLyDonShopee.Core/XuLyDonShopee.Core.csproj`: thêm ProjectReference → shared.
  - Test mới cho client chung (đặt trong `orders/XuLyDonShopee.Tests/`, tên
    `KiotApiClientTests.cs` — ProjectReference transitive qua Core, không cần sửa csproj Tests).
- **Không làm:**
  - KHÔNG sửa bất kỳ file nào dưới `suite/` (kể cả `Shopee.Core/Proxy/*`) — đó là phase 2b.
  - KHÔNG sửa test hiện có của orders (chúng là tiêu chí hành-vi-không-đổi). Ngoại lệ duy nhất:
    nếu test chọc internal member đã đổi — phải nêu rõ trong báo cáo từng chỗ và lý do.
  - KHÔNG đổi ngữ nghĩa pool/selector/watchdog của orders (`KiotKeyPool`, `ProxySelector`,
    `ProxyWatchdog`, `AccountSession*` giữ nguyên).
  - KHÔNG commit.

## 3. Các bước thực hiện

1. **Project `shared/Shopee.Proxy.Kiot`** — net8.0, `Nullable=enable`, KHÔNG package ngoài
   (System.Text.Json có sẵn trong net8). API public đề xuất (được phép tinh chỉnh chi tiết,
   nhưng giữ các nguyên tắc đánh dấu ✱):
   ```csharp
   namespace Shopee.Proxy.Kiot;

   public sealed record KiotProxyInfo(
       string? Host, int? HttpPort, int? Socks5Port,
       string? Http, string? Socks5,            // chuỗi "host:port" như API trả
       string? RealIpAddress,
       long? NextRequestAtMs, long? ExpirationAtMs,
       IReadOnlyDictionary<string, object?> Raw); // ✱ giữ dict thô — phase 2b cần cho fingerprint MultiBrave

   public sealed record KiotApiResult(bool Success, string? Message, KiotProxyInfo? Data, int? HttpStatus);

   public sealed class KiotApiClient
   {
       public KiotApiClient(HttpClient http, string? baseUrl = null); // ✱ inject được cả hai — testable
       public Task<KiotApiResult> GetNewAsync(string key, string? region, CancellationToken ct = default);
       public Task<KiotApiResult> GetCurrentAsync(string key, CancellationToken ct = default); // ✱ /current KHÔNG gửi region
   }
   ```
   - ✱ KHÔNG BAO GIỜ ném vì lỗi HTTP/parse/timeout: trả `Success=false + Message (+HttpStatus)`.
     Adapter mỗi bên tự quyết nuốt (orders) hay ném theo hợp đồng message (suite, 2b).
   - ✱ Parser đọc đủ: `success/message/status`, `data.{host,httpPort,socks5Port,http,socks5,
     realIpAddress,nextRequestAt,expirationAt,ttl,ttc}` — thiếu field nào để null, và luôn nhét
     toàn bộ `data` vào `Raw`.
   - Mặc định `baseUrl` lấy đúng giá trị orders đang dùng (xem `_baseUrl` hiện tại trong
     `orders/.../KiotProxyClient.cs:15`).
2. **Đưa vào sln:** solution folder `shared` (GUID mới) + project (GUID mới), đủ
   ProjectConfigurationPlatforms + NestedProjects, format giống các mục sẵn có.
3. **Adapter orders:** viết lại ruột `orders/XuLyDonShopee.Core/Services/KiotProxyClient.cs`:
   - Giữ nguyên: tên class/namespace, `IKiotProxyClient`, chữ ký ctor hiện tại (kể cả tham số
     HttpClient/baseUrl inject — chuyển tiếp xuống `KiotApiClient`), `SelectAcrossKeysAsync`
     xoay key, hành vi `/current`-trước-`/new`-sau ở tầng gọi.
   - Chuyển phần gọi HTTP + bóc JSON sang `KiotApiClient`; phần map → `ProxyEntry` (danh sách
     field ứng viên `http, proxyHttp, proxy, proxyAddress, address, socks5, proxySocks5, https`
     + `ProxyParser.Parse`) và kiểm hết hạn `ParseProxyIfAlive` (expirationAt ≤ now → null)
     GIỮ TRONG ADAPTER, đọc từ `KiotProxyInfo.Raw`/`ExpirationAtMs`.
   - Hành vi giữ nguyên từng li: `!IsSuccessStatusCode` → null im lặng; `success==false` /
     `status=="FAIL"` → null; pool key rỗng → null không gọi HTTP.
4. **Test mới `KiotApiClientTests.cs`:** stub HttpClient (theo mẫu KiotProxyClientTests hiện
   có): parse thành công đủ field; thiếu field → null; success=false → Message; HTTP 500 →
   Success=false + HttpStatus; timeout/exception → Success=false không ném; /current không
   chứa `region` trong URL; /new có region khi truyền.
5. **Build + test:** `dotnet build ShopeeSuite.sln -c Release` 0 lỗi 0 warning mới;
   `dotnet test orders/XuLyDonShopee.Tests/... -c Release` → 720 test CŨ pass nguyên vẹn +
   test mới pass. Fail đồng loạt 0x800711C7 = WDAC máy → báo cáo.

## 4. Tiêu chí nghiệm thu

- [ ] `shared/Shopee.Proxy.Kiot` build độc lập, không package ngoài, không tham chiếu
      suite/orders (một chiều: orders → shared).
- [ ] 720 test cũ pass mà KHÔNG sửa file test cũ nào (trừ ngoại lệ đã khai báo); test mới pass.
- [ ] `git status` chỉ gồm: sln + `shared/**` + `orders/XuLyDonShopee.Core/Services/KiotProxyClient.cs`
      + csproj Core + `orders/XuLyDonShopee.Tests/KiotApiClientTests.cs` + file plan này.
- [ ] Không file nào dưới `suite/` bị sửa.
- [ ] Grep xác nhận orders `KiotProxyClient.cs` không còn tự gọi HttpClient/parse JSON API Kiot
      (đã ủy quyền cho shared).

## 5. Rủi ro & lưu ý

- KiotProxyClientTests hiện stub HttpClient + baseUrl — adapter phải chuyển tiếp 2 tham số này
  xuống KiotApiClient sao cho stub cũ hoạt động NGUYÊN VẸN. Nếu một test cũ fail, coi đó là
  tín hiệu adapter làm sai hành vi — sửa adapter, KHÔNG sửa test.
- Thiết kế API client chung phải nhìn trước phase 2b (suite cần: Raw dict cho fingerprint,
  Message + HttpStatus để dựng lại đúng chuỗi lỗi `"KiotProxy current {status}: …"` — hợp đồng
  `IsProxyExpiredError` của MultiBrave). KHÔNG cần viết adapter suite bây giờ, chỉ cần đừng
  thiết kế bít đường.
- Agent khác đang sửa `BrowserLocator/BrowserProfilePaths/AccountSession` trong worktree riêng —
  các file đó KHÔNG thuộc phạm vi plan này, tuyệt đối không đụng để merge không xung đột.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
