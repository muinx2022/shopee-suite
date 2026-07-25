# Plan: Đợt 1 — Sửa bug hành vi Core + Suite desktop

- **Ngày:** 2026-07-25
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)
- **Plan cha:** `plans/2026-07-25-ke-hoach-refactor-toan-app.md` (mục 1A)

## 1. Bối cảnh & mục tiêu

Đợt review 2026-07-25 phát hiện các bug hành vi trong `suite/Shopee.Core` và `suite/Shopee.Suite` + `suite/Shopee.Module.MultiBrave`. Mỗi mục dưới là bug độc lập, sửa nhỏ gọn, KHÔNG refactor kèm (refactor có plan riêng sau). Mọi đường dẫn tương đối từ gốc repo.

## 2. Phạm vi

- **Làm:** 7 hạng mục dưới, trong `suite/`.
- **Không làm:** dọn code chết, hợp nhất trùng lặp, tách class (các plan sau); KHÔNG đụng `orders/`, `server/`, `extensions/`.

## 3. Các bước thực hiện

### Bước 1 — `CoordinationRuntime.Reconnect` rò poller (bug: máy "Ngắt kết nối" vẫn heartbeat)

`suite/Shopee.Core/Coordination/CoordinationRuntime.cs:345-353`: gán `Client/Hub/ConfigSync = null` rồi dựng lại nhưng không `Dispose()` `HttpCoordinationHub` cũ → `Timer _poller` 12s của instance cũ sống mãi (heartbeat tiếp sau khi ngắt; đổi URL/token thì 2 poller song song). Sửa: giữ tham chiếu cũ, gọi `old?.Dispose()` trước khi dựng mới (cả đường Disconnect nếu có nhánh tương tự). Kiểm tra `HttpCoordinationHub.Dispose` dừng timer đúng; cân nhắc cho `HubClient` implement `IDisposable` để dispose 2 HttpClient bên trong (nếu ít lan toả).

### Bước 2 — Ghi cookie không atomic ở `HubConfigSync` (vi phạm bất biến WriteAtomic)

`suite/Shopee.Core/Coordination/HubConfigSync.cs:104` (PullAccountsAsync) và `:270` (PullCookiesIfNewerAsync) ghi file cookie bằng `File.WriteAllBytesAsync` — không atomic. Bất biến đã ghi tại `suite/Shopee.Core/BigSeller/BigSellerCookieEngine.cs:219-220`: MỌI nơi ghi file cookie phải qua `WriteAtomic` (torn-read từng gây hỏng cookie lan đa máy). Sửa: thêm method public `BigSellerCookieEngine.TryWriteCookieFileBytes(string path, byte[] bytes)` (hoặc overload tương đương) đi qua đúng cơ chế tmp-unique → Move retry của `WriteAtomic` hiện có, rồi dùng ở cả 2 chỗ trên.

### Bước 3 — Timer async-void không lưới đỡ (nguy cơ sập process)

`suite/Shopee.Module.MultiBrave/Engine/BraveInstanceSession.cs:129-133`: `_monitorTimer.Elapsed += async (_, _) => { await CheckRunnerStallAndRecoverAsync(); await CheckProxyAndRestartIfNeededAsync(); }` — exception lọt khỏi 2 hàm (vd lỗi IO khi ghi log) trên async-void = unhandled exception = sập app. Sửa: bọc toàn thân lambda trong `try { … } catch (Exception ex) { /* log qua _log, nuốt */ }`.

### Bước 4 — Guard `ResumeContinueAsync` không nguyên tử (2 vòng runner cùng profile)

`BraveInstanceSession.cs:322-341`: guard `if (_runnerLoopActive)` đọc cờ chỉ được set BÊN TRONG `Task.Run` (dòng 383) → 2 lời gọi sát nhau (user bấm + watchdog cùng lúc) đều lọt. Sửa: guard bằng `Interlocked.CompareExchange` trên cờ `int` (mẫu `_syncBusy` trong cùng file); `_runnerLoopRequested` cũng đọc/ghi đa luồng — chuyển sang cùng cơ chế hoặc `volatile`.

### Bước 5 — Race "dừng êm để update" từ Hub

`suite/Shopee.Suite/Services/RemoteUpdateService.cs:54` chạy `HandleAsync` trên `Task.Run` (thread-pool) → chuỗi gọi tới closure `ShellViewModel.cs:174-182` (`StopAllSingle`, `scrape.StopCommand.Execute`, `worker.PrepareForShutdownAsync`) chạy trên thread-pool, trong khi `AssignmentWorker._inflight` là `Dictionary` thường (`suite/Shopee.Suite/Infrastructure/AssignmentWorker.cs:27`) được Tick (UI thread) đọc/ghi song song; `PrepareForShutdownAsync` (83-107) enumerate + Remove trực tiếp. Sửa (chọn 1, ưu tiên đơn giản): (a) đổi `_inflight` sang `ConcurrentDictionary` + rà các chỗ enumerate cho an toàn; hoặc (b) marshal phần đụng state về `UiThread.InvokeAsync` (giữ phần `Task.Delay` poll ngoài UI thread). Đường nút bấm tay (UI thread) không được đổi hành vi.

### Bước 6 — `CdpSession` rò `_pending` + HttpClient per-call

`suite/Shopee.Core/Cdp/CdpSession.cs:169-192` (`SendAsync`): timeout/cancel gọi `tcs.TrySetCanceled()` nhưng KHÔNG remove entry khỏi `_pending` → session sống lâu (PortCdpHub) rò dictionary khi Brave lặng thinh. Sửa: `linked.Token.Register(() => { _pending.TryRemove(id, out _); tcs.TrySetCanceled(); })` (hoặc remove trong nhánh timeout). Kèm: `CdpSession.cs:99-108` (`IsBrowserAliveAsync`) và các chỗ `:73`, `:114` tạo `new HttpClient` mỗi lần gọi — bị `BigSellerLoginRunner.RunLoginAsync:159` poll mỗi 3s suốt phiên login → đổi sang `AppServices.DirectHttp` (đã có sẵn, no-proxy; timeout riêng qua CTS).

### Bước 7 — Lưới lỗi cho Tick + fire-and-forget + `LoginAsync`

1. `suite/Shopee.Suite/Infrastructure/AssignmentWorker.cs:120`: `catch { }` trong vòng Tick 10s → thay bằng `catch (Exception ex)` ghi log qua `HubLog.Warn` (hoặc kênh log sẵn có) CÓ THROTTLE (mẫu throttle DiagLog trong `HttpCoordinationHub`) — tránh spam khi lỗi lặp.
2. Thêm helper nhỏ `TaskExt.FireAndForget(Task task, string tag)` (đặt trong `Shopee.Core.Infrastructure`) ghi log khi task lỗi; thay cho khuôn `try { _ = XxxAsync(); } catch { }` (catch hiện không bắt được gì vì fire-and-forget) tại: `suite/Shopee.Suite/Modules/Scrape/ScrapeViewModel.cs:428` (SetLedgerStatusAsync khi Reset — lỗi mạng đang chìm im lặng đúng ca fold-poisoning), `AssignmentWorker.cs:403` (ConfigSync PushAsync), `suite/Shopee.Suite/Infrastructure/AccountLeaseScope.cs:130` (heartbeat lease).
3. `suite/Shopee.Suite/Modules/BigSeller/BigSellerViewModel.cs` `LoginAsync` (245-318): try/finally KHÔNG có catch trong AsyncRelayCommand → lỗi chìm trong ExecutionTask, Status kẹt. Thêm `catch (OperationCanceledException) {}` + `catch (Exception ex) { Status = "✘ …"; Log(...); }` cho khớp `LoginAllAsync`/`CleanMediasAsync` cùng file.

### Bước 8 — Build

`dotnet build ShopeeSuite.sln` sạch. Không có test tự động cho suite — nghiệm thu bằng đọc diff + build.

## 4. Tiêu chí nghiệm thu

- [ ] Build solution sạch, 0 warning mới.
- [ ] Grep xác nhận: không còn `File.WriteAllBytesAsync` ghi file cookie trong `HubConfigSync`; không còn `new HttpClient` trong `CdpSession`; `Reconnect` có dispose hub cũ.
- [ ] Timer lambda trong `BraveInstanceSession` ctor được bọc try/catch toàn thân; guard resume dùng Interlocked.
- [ ] `_inflight` an toàn đa luồng (ConcurrentDictionary hoặc mọi truy cập đã marshal UI thread) — ghi rõ trong báo cáo chọn phương án nào và vì sao.
- [ ] Các chỗ fire-and-forget liệt kê ở bước 7 dùng helper mới; Tick có log lỗi throttle.
- [ ] Không đổi hành vi nghiệp vụ nào khác (diff chỉ gồm các điểm nêu trên).

## 5. Rủi ro & lưu ý

- Đây toàn là code chạy nền/đa luồng — sửa TỐI THIỂU, không "tiện tay" refactor.
- Bước 2: giữ nguyên semantics WriteAtomic hiện có (tmp unique + Move retry), đừng viết cơ chế mới.
- Bước 5: nếu chọn ConcurrentDictionary, chú ý các đoạn enumerate-rồi-Remove trong `PrepareForShutdownAsync` phải snapshot trước.
- Bước 6: `AppServices.DirectHttp` là HttpClient chia sẻ — timeout per-call phải qua CTS, không đổi `Timeout` của client chung.

---

## Báo cáo thực thi (Opus điền sau khi xong)
