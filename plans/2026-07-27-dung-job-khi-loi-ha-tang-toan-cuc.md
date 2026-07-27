# Plan: Dừng job khi lỗi hạ tầng TOÀN CỤC (key proxy hết hạn) thay vì bỏ qua dòng

- **Ngày:** 2026-07-27
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh — sự cố thật vừa xảy ra

Người dùng giao Scrape từ hub cho máy `muinx-nuc` (shop `babysak.store`, acc `albencherbij@hotmail.com`) và báo
"máy không chạy gì cả". Truy vết thật (máy dev chính là `muinx-nuc` nên đọc được log client):

```
14:31:36 ⏯ Tiếp tục ... 998 dòng cần chạy · 2 cửa sổ · KHUNG 10 tk Shopee
14:31:46 ✘ KiotProxy new 400: Key proxy đã hết hạn, vui lòng gia hạn để tiếp tục sử dụng | KEY_EXPIRED
         ⏸ xp1snwl3az: ... → cho tk nghỉ 90s, vá phần dở bằng tk khác (lỗi 2 lần — nghỉ dài 90s).
         ⛔ Dòng 54 kẹt 3 lần liên tiếp (... KEY_EXPIRED) → BỎ QUA dòng 54.
```

Thực tế: **200 lượt xin proxy, 0 lượt thành công**; cả 10 tk Shopee trong khung đều `KEY_EXPIRED`; **17 dòng bị BỎ
QUA vĩnh viễn** trong ~6 phút và vẫn tăng; chưa mở nổi một cửa sổ Brave nào (nên nhìn ngoài tưởng máy đứng im).
Trên hub, assignment vẫn `running` và ô vẫn `⏳ đang chạy` — hub chỉ nhận đúng 1 dòng log `▶ Nhận Scrape` rồi im.

**Nguyên nhân gốc:** key KiotProxy hết hạn. Toàn bộ **156 tk Shopee dùng CHUNG một key**
(`config/accounts.json` trên hub) ⇒ đây là lỗi **toàn cục**, không phải lỗi của một tài khoản.

**Lỗi thiết kế cần sửa:** `ScrapeRunner.cs` (khoảng dòng 275-300) xếp mọi lỗi không-phải-captcha vào nhánh
"lỗi proxy/đứt/tạm" → `pool.Cooldown(spec)` cho tk nghỉ 15s/90s rồi **vá bằng tk khác**, và sau 3 lần kẹt cùng một
dòng thì **BỎ QUA dòng** (`ScrapeRunner.cs:160`). Cách xử lý đó ĐÚNG cho lỗi ngẫu nhiên của một tk/một proxy,
nhưng SAI hoàn toàn khi key hết hạn: đổi bao nhiêu tk cũng vô ích vì tất cả dùng chung một key — kết quả là job
chạy hết 998 dòng rồi báo "✓ Xong" trong khi dữ liệu thủng lỗ chỗ. **Hỏng im lặng.**

## 2. Phạm vi

**Làm:**
- Phân loại lỗi **TOÀN CỤC** (key/tài khoản proxy chết) tách khỏi lỗi tạm thời của một tk.
- Gặp lỗi toàn cục → **DỪNG cả job ngay**, không cooldown, không vá bằng tk khác, **không bỏ qua dòng nào**.
- Báo `failed` lên hub kèm lý do người-đọc-được → ô ở /dispatch thành ✕ Lỗi + việc vào danh sách gián đoạn.
- Ghi log rõ một dòng ở mức job (không spam mỗi tk một dòng).

**Không làm:**
- KHÔNG đụng UI hub (`Dispatch.razor`, `app.css`) — plan song song `2026-07-27-kpi-bam-duoc-tren-dispatch.md`
  đang sửa file đó. Nếu thấy cần đổi hiển thị thì BÁO LẠI, đừng sửa.
- KHÔNG đổi logic captcha (`pool.Quarantine`) và KHÔNG đổi ngưỡng bỏ-qua-dòng cho lỗi thường.
- KHÔNG tự gia hạn/đổi key proxy.
- KHÔNG commit, KHÔNG deploy, KHÔNG release.

## 3. Các bước thực hiện

### Bước 1 — Bộ phân loại lỗi toàn cục (`suite/Shopee.Core/Proxy/`)

Thêm hàm thuần (đặt cạnh `KiotProxyClient`, hoặc file mới `ProxyFailure.cs`):

```csharp
/// <summary>Lỗi HẠ TẦNG TOÀN CỤC: key/tài khoản proxy chết ⇒ MỌI tk Shopee đều hỏng như nhau, đổi tk vô ích.
/// Khác hẳn lỗi proxy TẠM THỜI của một tk (rớt mạng, IP xấu) vốn xử lý bằng cooldown + vá bằng tk khác.</summary>
public static bool IsFleetWideProxyFailure(string? reason);
```
Khớp (không phân biệt hoa/thường): `KEY_EXPIRED`, `KEY_NOT_FOUND`, `Key proxy đã hết hạn`, `vui lòng gia hạn`,
`hết hạn`/`het han` **CHỈ khi** đi kèm chuỗi `key` (tránh bắt nhầm "phiên hết hạn" của Shopee).

**KHÔNG đụng** `BraveInstanceSession.IsProxyExpiredError` (dòng ~1307) — hàm đó phục vụ việc *đổi proxy rồi khởi
động lại*, mục đích khác; sửa nó là đổi hành vi ngoài phạm vi.

Kèm test đơn vị cho hàm này (chuỗi lỗi thật lấy từ log ở mục 1, cùng vài chuỗi KHÔNG được khớp).

### Bước 2 — `ScrapeRunner` dừng job khi gặp lỗi toàn cục

Tại nhánh xử lý lỗi (`ScrapeRunner.cs` ~275-300, chỗ `pool.Cooldown(spec)`):
- Nếu `ProxyFailure.IsFleetWideProxyFailure(res.Reason)` → **KHÔNG** cooldown, **KHÔNG** vá bằng tk khác:
  nhả tk về kho, đặt một cờ dừng ở mức job, thoát vòng lặp phân phối khối, và kết thúc runner với trạng thái
  **lỗi** kèm `Reason`.
- Phần dòng đã cào xong vẫn phải được báo qua `RowsCompleted` như hiện tại (không mất tiến độ đã có).
- Ghi **một** dòng log mức job: `⛔ DỪNG JOB: key proxy hết hạn — mọi tk Shopee dùng chung key này. Gia hạn key rồi chạy lại.`
- Bảo đảm các khối đang chạy song song (P1/P2…) cũng dừng, không để một luồng tiếp tục cào.

Runner hiện báo kết quả ra ngoài thế nào thì theo đúng đường đó (soi `ScrapeRunner` + `ScrapeViewModel.RunSingleAsync`),
**đừng đẻ kênh mới**. Nếu runner chưa có đường trả "lỗi job" thì thêm tối thiểu (vd một `JobFatal?.Invoke(reason)`
song song với các event sẵn có) và ghi rõ trong báo cáo.

### Bước 3 — Báo `failed` lên hub kèm lý do

Việc hub-giao chạy qua `AssignmentWorker` (`suite/Shopee.Suite/Infrastructure/AssignmentWorker.cs`) — đã có
`hub.ReportAssignmentAsync(id, "failed", lý_do)` (xem `RequeueOrFailAsync`). Cần: khi job kết thúc vì lỗi toàn cục,
assignment tương ứng chuyển **`failed`** với `last_error` = lý do gọn (vd `key proxy hết hạn (KEY_EXPIRED)`), KHÔNG
phải `requeue` (requeue là dành cho lỗi tạm thời — key hết hạn thì thử lại 6 lần cũng vô ích, chỉ tổ đốt thêm dòng).

Truy vết đường job-kết-thúc → assignment-status hiện có (`_inflight`, `ReconcileInflightAsync`) rồi cắm vào đúng
chỗ; **không** dựng đường báo trạng thái thứ hai.

Việc chạy TAY (không có assignment) thì chỉ dừng + log, không báo hub.

### Bước 4 — Kiểm chứng

Không có key hết hạn thật để thử, nên bắt buộc dựng đường giả lập:
- Test đơn vị cho `IsFleetWideProxyFailure` (Bước 1).
- Test cho nhánh quyết định trong `ScrapeRunner`: cùng một `res.Reason`, khẳng định lỗi toàn cục → **không** gọi
  `pool.Cooldown`, **không** sinh việc vá, và job kết thúc ở trạng thái lỗi; còn lỗi thường → giữ nguyên hành vi cũ
  (cooldown + vá). Nếu `ScrapeRunner` khó test do phụ thuộc, **tách riêng hàm quyết định** rồi test hàm đó — đừng
  bỏ test.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` sạch, 0 warning mới; `dotnet test` xanh, có test mới.
- [ ] Chuỗi lỗi THẬT từ log (`KiotProxy new 400: Key proxy đã hết hạn, vui lòng gia hạn để tiếp tục sử dụng | KEY_EXPIRED`)
      → `IsFleetWideProxyFailure` trả **true**.
- [ ] Các chuỗi KHÔNG được khớp: lỗi captcha, `PROXY_NOT_FOUND_BY_KEY` (đây là proxy lẻ, vẫn cooldown được),
      "phiên đăng nhập hết hạn" → trả **false**.
- [ ] Test nhánh: lỗi toàn cục → không cooldown, không vá, không BỎ QUA dòng nào, job kết thúc trạng thái lỗi.
- [ ] Test hồi quy: lỗi proxy thường vẫn cooldown 15s/90s + vá bằng tk khác **y như cũ**.
- [ ] Assignment nhận `failed` (KHÔNG phải `requeue`) kèm `last_error` chứa lý do đọc được.
- [ ] `Dispatch.razor` và `app.css` **không bị sửa** (`git diff --stat` không có 2 file này).

## 5. Rủi ro & lưu ý

- **Đừng bắt nhầm lỗi tạm thành lỗi toàn cục** — bắt nhầm thì một trục trặc proxy lẻ sẽ giết cả job đang chạy tốt.
  Danh sách khớp phải hẹp, có test cho cả ca âm tính.
- Có plan khác đang chạy **song song trong worktree khác** sửa `Dispatch.razor` + `app.css`. Tuyệt đối không đụng
  hai file đó.
- Không đổi hành vi bỏ-qua-dòng cho lỗi thường: cơ chế đó đang cứu job khỏi kẹt vì một dòng hỏng.
- Lý do báo lên hub phải **ngắn và người đọc hiểu ngay** — nó hiện thẳng trên ô ở trang Giao việc.

---

## Báo cáo thực thi (Opus điền sau khi xong)
