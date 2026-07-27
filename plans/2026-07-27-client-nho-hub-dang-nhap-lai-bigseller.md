# Plan: Client gặp verify BigSeller → nhờ HUB đăng nhập lại → kéo cookie về, không phải gõ mã ở client

- **Ngày:** 2026-07-27
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

> Người dùng: *"khi BigSeller yêu cầu verify code, khi đó cần yêu cầu hub đăng nhập lại, sau đó sync cookie xuống
> cho client để không phải verify code ở client."*

Khảo sát: **gần như mọi mảnh đã có sẵn, thiếu đúng một mắt xích nối.**

| Mảnh | Nơi | Tình trạng |
|---|---|---|
| Login BigSeller trên hub (Playwright headless, OpenAI giải captcha, **tự đọc mã OTP từ Hotmail**, nạp lại device-trust trước khi login để login chỉ captcha) | `server/Shopee.Hub.Web/Services/BigSellerLoginService.cs` | ✅ có. `Start(acctId, email, password, emailPassword)` fire-and-forget, trả `false` nếu đang chạy; `GetState(acctId)` → `LoginState{Status: idle\|running\|needsOtp\|success\|failed, Message}`; xong thì ghi TOÀN BỘ cookie (gồm device-trust) vào kho `cookies/{acctId}.json` |
| UI admin gõ OTP khi hub không tự đọc được | `Components/Shared/AccountConfigPanel.razor` | ✅ có |
| Re-login định kỳ ~7 ngày/acc, rải 1 acc/giờ (device-trust không hết hạn) | `Services/BigSellerReloginScheduler.cs` | ✅ có |
| Kéo cookie mới từ hub về client | `HubConfigSync.PullCookiesIfNewerAsync()` | ✅ có |
| Client nhận ra "BigSeller mất phiên" | `ScrapeRunner.BigSellerNeedLogin` → `ScrapeViewModel.cs:742` | ✅ có, nhưng **chỉ ghi log** *"Hãy ĐĂNG NHẬP LẠI BigSeller rồi chạy lại"* |
| **Đường để client NHỜ hub login lại NGAY** | — | ❌ **THIẾU** — `HubRoutes` không có route login nào; `BigSellerLoginService` hiện chỉ được scheduler + UI hub gọi |

⇒ Hôm nay: client đang chạy mà BigSeller đòi mã thì job chết tại chỗ, người dùng phải ra tận máy đăng nhập tay.

**Mục tiêu:** client tự nhờ hub login lại, hub login (tự giải captcha + tự đọc OTP), cookie mới về kho hub, client
kéo về rồi chạy tiếp — **không ai phải gõ mã ở client**.

## 2. Phạm vi

**Làm:**
- Hub: endpoint `POST /bigseller/relogin` + `GET /bigseller/relogin?accountId=` (đọc trạng thái).
- Client: `BigSellerReloginCoordinator` — nhờ hub login, theo dõi trạng thái, kéo cookie về khi xong.
- Client: nối `BigSellerNeedLogin` (scrape) và đường login-fail của import/update vào coordinator.
- `AssignmentWorker`: trong lúc một acc đang được hub login lại → **không claim việc mới của acc đó** và trả việc
  đang dở về hàng chờ kèm lý do đọc được, thay vì đốt hết số lần thử.

**Không làm:**
- KHÔNG đụng `BigSellerLoginService` phần lõi login (đang chạy tốt, có OTP Hotmail).
- KHÔNG đụng `BigSellerReloginScheduler` (lịch định kỳ) — chỉ dùng lại khoá chống-chạy-chồng của `Start`.
- KHÔNG thêm UI mới trên hub (UI gõ OTP đã có ở `AccountConfigPanel`).
- KHÔNG commit, KHÔNG deploy, KHÔNG release.

## 3. Các bước thực hiện

### Bước 1 — DTO + route (`suite/Shopee.Core/Coordination/`)

`HubRoutes`: `public const string BigSellerRelogin = "/bigseller/relogin";`

`HubDtos.cs`:
```csharp
/// <summary>Client nhờ Hub đăng nhập lại 1 acc BigSeller (gặp verify/mất phiên). MachineId để Hub ghi log biết
/// máy nào xin.</summary>
public sealed record BigSellerReloginRequest(string AccountId, string MachineId);

/// <summary>Trạng thái phiên login trên Hub. <see cref="Status"/> = idle|running|needsOtp|success|failed
/// (nguyên văn LoginState.Status). <see cref="Accepted"/> = Hub vừa BẮT ĐẦU phiên mới cho lượt xin này
/// (false = đã có phiên đang chạy → client cứ chờ phiên đó).</summary>
public sealed record BigSellerReloginResponse(bool Accepted, string Status, string Message);
```

`HubClient`: `PostAsync`/`GetFromJsonAsync` tương ứng, khuôn y các method sẵn có.

### Bước 2 — Hub: endpoint (`Api/ClientApiEndpoints.cs`)

```csharp
api.MapPost(HubRoutes.BigSellerRelogin, (BigSellerReloginRequest? r, BigSellerLoginService login, FileStoreConfigService cfg) => …);
api.MapGet(HubRoutes.BigSellerRelogin, (string? accountId, BigSellerLoginService login) => …);
```
- POST: tra acc trong `cfg.BigSellerAccounts()` theo `AccountId` → lấy `Email` / `Password` / `EmailPassword` →
  gọi `login.Start(...)`. `Start` trả false (đang chạy) → vẫn trả 200 với `Accepted=false` + trạng thái hiện tại
  (KHÔNG coi là lỗi — client chỉ cần biết "đang có người lo").
- Acc không tồn tại / thiếu mật khẩu → `Accepted=false`, `Status="failed"`, `Message` nói rõ thiếu gì.
- Ghi một dòng log hub (`db.AppendLog`) như các endpoint khác: máy nào xin login lại acc nào.
- GET: trả `GetState(accountId)`; chưa có phiên → `Status="idle"`.

### Bước 3 — Client: `BigSellerReloginCoordinator` (`suite/Shopee.Core/Coordination/`)

Một singleton nhỏ, thuần logic + gọi `HubClient` (KHÔNG đụng UI):

- `bool IsRelogging(string accountId)` — acc đang chờ hub login lại.
- `void Request(string accountId, string reason)` — nếu acc chưa trong danh sách: đánh dấu + POST relogin
  (fire-and-forget, nuốt lỗi mạng) + bắn sự kiện log.
- Vòng theo dõi (timer ~15s, chỉ chạy khi danh sách khác rỗng): GET trạng thái từng acc đang chờ:
  - `success` → gọi `HubConfigSync.PullCookiesIfNewerAsync()` **một lần**, bỏ acc khỏi danh sách, bắn log
    `✅ Hub đã đăng nhập lại <acc> — đã kéo cookie mới về, việc sẽ chạy lại.`
  - `failed` → bỏ khỏi danh sách, log `⛔ Hub đăng nhập lại <acc> KHÔNG được: <message> — cần xử lý tay trên hub.`
  - `needsOtp` → GIỮ trong danh sách, log **một lần** `⏳ Hub đang chờ mã OTP cho <acc> — vào hub nhập mã.`
  - `running` → giữ, không log lại.
- **Trần chờ**: quá 10 phút chưa xong → bỏ khỏi danh sách + log, để không kẹt vĩnh viễn (`BigSellerLoginService`
  giữ browser chờ OTP tối đa 5' nên 10' là dư).
- Sự kiện log bắn ra ngoài (`Action<string,string>? Log` — accountId, dòng chữ) để ViewModel client hiển thị; lớp
  này KHÔNG tự biết UI.

### Bước 4 — Nối vào chỗ phát hiện mất phiên

1. **Scrape** — `suite/Shopee.Suite/Modules/Scrape/ScrapeViewModel.cs:742` (`runner.BigSellerNeedLogin`): giữ dòng
   log hiện có nhưng đổi nội dung, và gọi `coordinator.Request(account.Id, reason)`. Dòng log mới phải nói đúng
   việc đang xảy ra: *"⛔ BigSeller mất đăng nhập — đã nhờ Hub đăng nhập lại, cookie mới sẽ tự về, việc sẽ chạy lại."*
2. **Import/Update** — tìm đường thất bại do `requiresBigSellerLogin: true` trong
   `UpdateProductViewModel.RunOneWorkflowAsync` (và/hoặc nơi engine báo "log in first") rồi nối tương tự.
   **Nếu không tìm được điểm phát hiện rõ ràng cho import/update thì CHỈ làm cho scrape** và ghi rõ trong báo cáo —
   đừng đoán chỗ nối.

### Bước 5 — `AssignmentWorker`: đừng đốt số lần thử trong lúc chờ login

`suite/Shopee.Suite/Infrastructure/AssignmentWorker.cs`:
- Trước khi claim: bỏ qua việc của acc đang `IsRelogging` (đừng nhận rồi lại chết ngay).
- Việc đang dở của acc vừa bị mất phiên → `ReportAssignmentAsync(id, "requeue", "BigSeller mất phiên — Hub đang đăng nhập lại")`.
  **Dùng `requeue`, KHÔNG `failed`**: đây là lỗi tạm, cookie về là chạy tiếp được.
- **Quan trọng:** `RequeueOrFailAsync` có trần `MaxLaunchAttempts` (6 lần) → login mất vài phút sẽ đốt hết trần rồi
  báo `failed` oan. Nên nhánh này phải gọi thẳng `ReportAssignmentAsync(..., "requeue", …)` **không tăng bộ đếm**
  (đã có tiền lệ: nhánh "chờ quỹ Brave" ở dòng ~199 làm đúng vậy — theo khuôn đó).

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` + `dotnet build server/Shopee.Hub.Web` sạch, 0 warning mới; `dotnet test` xanh.
- [ ] Test cho `BigSellerReloginCoordinator` (thuần logic, LINK vào test project theo khuôn `MachineSlots`/`OpLanes`):
      `Request` hai lần liên tiếp cùng acc → chỉ POST **một** lần; `success` → gọi pull cookie đúng 1 lần rồi bỏ khỏi
      danh sách; `failed`/quá 10' → bỏ khỏi danh sách; `needsOtp` → giữ và chỉ log một lần.
- [ ] Hub: `POST /bigseller/relogin` với acc có thật → `Accepted=true`, `GetState` chuyển `running`; gọi lại ngay lần
      hai → `Accepted=false` + `Status="running"` (không dựng phiên thứ hai).
- [ ] Hub: acc không tồn tại → `Accepted=false` + `Message` nói rõ, KHÔNG ném exception.
- [ ] Hub: client cũ (không biết route này) không bị ảnh hưởng.
- [ ] `AssignmentWorker`: acc đang relogin → không claim việc mới của acc đó; việc đang dở về `queued` với lý do
      đọc được trên hub, và **số lần thử không tăng** (kiểm bằng cách để trạng thái relogin kéo dài > 6 nhịp).

## 5. Rủi ro & lưu ý

- **Đừng để hai phiên login cùng lúc cho một acc** — `Start` đã chặn, nhưng client cũng phải chặn ở phía mình
  (`IsRelogging`) để không spam request qua tunnel.
- **Nhiều máy cùng gặp mất phiên một lúc** (đúng sự cố 2026-07-11): tất cả sẽ cùng POST relogin cho cùng acc → hub
  chỉ chạy một phiên (tốt), nhưng client phải chịu được `Accepted=false` mà không coi là lỗi.
- **Đừng đụng nhịp rải của `BigSellerReloginScheduler`**: nó cố tình 1 acc/giờ để BigSeller không siết cả cụm.
  Login theo yêu cầu là đường KHÁC (sự cố, hiếm) — nhưng nếu thấy dễ bùng (nhiều acc mất phiên cùng lúc) thì thêm
  chặn tối thiểu ở hub: không quá 1 phiên login đồng thời TOÀN HUB. **Kiểm xem `BigSellerLoginService` đã chặn
  toàn cục chưa** (đọc code, đừng đoán); chưa có thì thêm, và ghi rõ trong báo cáo.
- Mật khẩu acc + mật khẩu hòm thư nằm trên hub — đường này KHÔNG gửi mật khẩu qua mạng (client chỉ gửi `AccountId`),
  giữ đúng như vậy.
- Cookie về client qua `PullCookiesIfNewerAsync` vốn có luật "mới hơn thắng theo `iat` của JWT" — đừng viết đường
  ghi cookie thứ hai.

---

## Báo cáo thực thi (Opus điền sau khi xong)
