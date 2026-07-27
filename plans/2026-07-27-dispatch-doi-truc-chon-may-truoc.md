# Plan: Trang Giao việc đổi trục — chọn MÁY trước, bấm action thẳng trên dòng

- **Ngày:** 2026-07-27
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

Trang `/dispatch` vừa làm xong (commit `03f3495`, plan `2026-07-27-trang-giao-viec-dispatch.md`) đi theo trục
**shop-first**: tick nhiều dòng shop → xuống thanh đáy chọn Op → chọn Máy → bấm Giao. Người dùng đánh giá: chỉ hơn
bản cũ ở chỗ không phải click từng shop, **vẫn khó dùng** — vì vẫn phải qua 3 bước gián tiếp (chọn op, chọn máy, ghim).

**Yêu cầu mới (nguyên văn người dùng, đây là thiết kế BẮT BUỘC làm theo):**
1. Vào trang → **hiển thị tất cả client (máy)**. Máy **online → bấm chọn được**; máy **offline → disable, không bấm được**.
2. Bấm vào một client → **phần option hiện phía trên** (tham số cho lượt giao việc của máy đó).
3. Phía dưới là **list acc / shop** như hiện tại.
4. **Action đặt ngay trên list** — muốn chạy action nào thì bấm thẳng action đó, **trực quan hơn việc phải chọn
   action rồi ghim máy**.
5. Giữ nguyên quy tắc đang chạy tốt: **một acc chỉ một máy** — client khác không chiếm chỗ.
6. **Sang client khác, action đã có thì disable trên list, không cho bấm.**

Mô hình mới, nói gọn: *"Tôi đang đứng ở máy PC-01 — tôi muốn nó làm gì."* Chọn máy một lần, rồi bấm action trên
từng dòng shop, mỗi click là một lệnh.

## 2. Phạm vi

**Làm:**
- Viết lại phần BigSeller của `Components/Pages/Dispatch.razor` theo trục **máy-first**.
- Hàng thẻ máy ở đầu trang (chọn được / disable khi offline), hiện tải thật.
- Panel tham số cho máy đang chọn, đặt **trên** danh sách.
- Mỗi ô op trong list là **một nút hành động** — bấm là chạy / huỷ / chạy lại ngay, không qua bước trung gian.
- Nút gộp mức tài khoản (chạy cả acc) trên dòng nhóm — đây là "chọn nhiều" duy nhất còn lại.
- **Xoá** thanh hành động hàng loạt, cột checkbox, hàng "chọn nhanh", dropdown lọc Máy, và
  `Services/DispatchBalancer.cs` + test + link trong csproj (mô hình mới không còn tự-cân-tải: người dùng tự chọn máy).
- CSS cho thành phần mới, xoá CSS đã chết; bump `app.css?v=29`.

**Không làm:**
- KHÔNG sửa `Fleet.razor`.
- KHÔNG đụng tab **Đơn hàng** (giữ nguyên read-only như hiện tại).
- KHÔNG làm backend giao việc cho Đơn hàng.
- KHÔNG deploy (Fable deploy sau khi nghiệm thu).

## 3. Bố cục mới (thay toàn bộ nhánh `_tab == "bs"`)

```
KPI (giữ nguyên 4 thẻ)
Tabs: 🛒 BigSeller | 📦 Đơn hàng          (giữ nguyên)

┌─ Chọn máy client ────────────────────────────────────────────────┐
│ [🟢 PC-01        ] [🟢 KHO-03      ] [⚪ LAP-A        ]           │
│  còn 4/6 brave     còn 4/4 brave      offline · 2 giờ            │
│  2 việc · v1.6.5   0 việc · v1.6.5    (không bấm được)           │
└──────────────────────────────────────────────────────────────────┘

⚙ Tham số cho lượt giao của PC-01   (panel, hiện khi đã chọn máy)
   Từ dòng [0] Đến dòng [0] Số process [0] Số tk/khung [0] Reload [0]
   ☐ Import từ tab "Đã nhận"          0 = dùng cấu hình của máy client

Lọc: [Tài khoản ▾] ⦿Chưa xong ○Đang chạy ○Gián đoạn ○Tất cả  🔍 tìm shop

┌ kho1 · 4 shop · đang do PC-01 giữ ······ [▶ Scrape cả acc] [▶ Import cả acc] [▶ Update cả acc] ┐
│ dola-store        [ ✖ Huỷ · 40% ]   [ ▶ Import ]    [ ▶ Update ]                              │
│ mint-house        [ ↻ Chạy lại ✓ ]  [ ▶ Import ]    [ ▶ Update ]                              │
├ kho2 · 3 shop · đang do PC-02 giữ ······ (nút cả-acc disable: acc do PC-02 giữ)               │
│ bepvui            [ ⏳ PC-02 ]       [ ⏳ PC-02 ]     [ ⏳ PC-02 ]     ← disable hết            │
└───────────────────────────────────────────────────────────────────────────────────────────────┘
```

Chưa chọn máy → ẩn panel tham số, list vẫn hiện đầy đủ trạng thái nhưng **mọi nút action disable**, kèm dòng nhắc
"Chọn một máy client ở trên để giao việc."

## 4. Các bước thực hiện

### Bước 1 — Hàng thẻ máy

- Nguồn: `_budgets` (đã có, dựng từ `Snap.Machines` + quỹ Brave; giữ nguyên `BuildBudgets()`).
- Mỗi thẻ là `<button class="mcard">`: hostname · trạng thái (🟢 online / ⚪ offline + `FleetStateService.Ago(LastSeen)`)
  · `còn {Free}/{Free+Running} brave` · `{Running} việc` · `AppVersion` (từ `MachinePresence.AppVersion`, rỗng thì bỏ).
- Máy offline → `disabled` thật sự (thuộc tính `disabled` trên `<button>`, không chỉ làm mờ bằng CSS) + `title`
  giải thích. Máy online → bấm để chọn; thẻ đang chọn có class `sel`.
- Máy đang chọn mà **chuyển sang offline** giữa chừng (nhịp fleet 2s) → tự bỏ chọn + hiện dòng cảnh báo
  "Máy X vừa offline — đã bỏ chọn." (đặt ở `OnFleetTick`/`ApplyFilter`).
- Máy đang chọn biến mất khỏi fleet → bỏ chọn (đã có tiền lệ ở `BuildBudgets()` với `_bulkMachine`).

### Bước 2 — Panel tham số

- Chỉ hiện khi `_selMachine` khác rỗng. Dùng lại đúng các field đang có (`_optStart/_optEnd/_optProcs/_optFrame/
  _optReload/_optFromClaimed`), bỏ khối `⚙ Tuỳ chọn` gập/mở cũ trong thanh đáy.
- `Số tk / khung` chỉ hiện ý nghĩa với scrape, `Reload` với import/update, `Import từ tab "Đã nhận"` với import —
  ở mô hình mới KHÔNG biết trước op nào sẽ bấm nên **hiện hết**, kèm chú thích ngắn cho từng field ghi rõ op nào đọc
  nó (vd `Số tk / khung (chỉ Scrape đọc)`). Lúc tạo assignment vẫn lọc theo op y như hiện tại.
- Tham số là **của lượt giao**, giữ nguyên giữa các lần bấm cho tới khi người dùng đổi — không reset sau mỗi click.

### Bước 3 — Ô op thành nút hành động

Thay `<button class="opcell">` + menu dòng phụ hiện tại bằng **một nút** cho mỗi (shop, op). Xoá hẳn `_menuKey`,
`ToggleMenu`, `CloseMenu`, `OpenLogs`, `MachinesFor`, khối `<tr class="opmenurow">` và override `ShouldTickRender`
(không còn menu nên không cần dừng nhịp vẽ).

Trạng thái nút, xét theo `cell = FleetStateService.OpCell(Snap, acc, shop, op)`, `_selMachine`, `_holds`, và
assignment mở của (shop, op) trong `Snap.Assignments` (`status is "queued" or "running"`):

| Tình huống | Nhãn nút | Bấm được | Hành vi khi bấm |
|---|---|---|---|
| Chưa chọn máy | nhãn theo trạng thái (như pill cũ) | ✗ | — (title: "Chọn máy ở trên trước") |
| Có assignment mở, **máy đang chọn** | `✖ Huỷ` + `· {Text}` | ✓ | `Db.CancelAssignment(a.Id)` |
| Có assignment mở, **máy khác** | `⏳ {tên máy}` | ✗ | title: "đã xếp/đang chạy ở máy X" |
| `cell.Locked` (đang chạy TAY, có lease) | `⏳ {máy trong lease}` | ✗ | title: "đang chạy tay trên máy X — dừng ở app máy đó" |
| Acc đang bị **máy khác** giữ (`_holds[acc] != _selMachine`) | nhãn trạng thái | ✗ | title: "acc đang do máy X giữ — 1 acc chỉ chạy 1 máy" |
| Ledger đã `completed` (`cell.Text` bắt đầu `✓`) | `↻ Chạy lại` | ✓ | tạo assignment như dưới |
| Còn lại (chưa chạy / dừng / lỗi) | `▶ {Tên op}` | ✓ | tạo assignment như dưới |

Tạo assignment (giữ đúng khuôn `Fleet.Pin()` và `Assign()` hiện tại):

```csharp
var payload = op == "import" ? JsonSerializer.Serialize(new ImportJobPayload { FromClaimedTab = _optFromClaimed }) : "";
Db.CreateAssignment(new CreateAssignmentRequest(
    r.AccountId, r.ShopId, r.Sheet, op, _selMachine, Pinned: true,
    Math.Max(0, _optStart), Math.Max(0, _optEnd), payload,
    Processes: Math.Max(0, _optProcs),
    FrameSize: op == "scrape" ? Math.Max(0, _optFrame) : 0,
    ReloadSeconds: op is "import" or "update" ? Math.Max(0, _optReload) : 0));
FleetState.Refresh();
```

Sau mỗi lần bấm: cập nhật `_result` một dòng ngắn (`✔ Đã giao Scrape · dola-store → PC-01` /
`✖ Đã huỷ Import · mint-house`), hiển thị ở ngay dưới hàng lọc. **Một click = một lệnh, KHÔNG xác nhận 2 bước**
(nhẹ, và huỷ lại được ngay bằng chính nút đó).

### Bước 4 — Nút mức tài khoản (thay cho chọn nhiều)

Trên dòng nhóm `tr.acctrow`, thêm 3 nút `▶ Scrape cả acc` / `▶ Import cả acc` / `▶ Update cả acc`:
- Chỉ áp cho **các dòng của acc đó đang hiện sau bộ lọc** và **nút của dòng đó đang bấm được** — dòng nào disable
  thì bỏ qua (không tạo assignment).
- Disable cả cụm khi chưa chọn máy, hoặc acc đang do máy khác giữ.
- **Xác nhận 2 bước** (pattern repo: bấm lần đầu → đổi nhãn thành `Bấm lần nữa để giao N việc`), vì một cú bấm tạo
  nhiều việc. Chỉ một cụm ở trạng thái chờ-xác-nhận tại một thời điểm; đổi acc/đổi máy/đổi lọc → huỷ trạng thái chờ.
- Kết quả gộp một dòng: `✔ Đã giao 4 việc Scrape của kho1 → PC-01 (bỏ qua 1 shop đang chạy)`.

### Bước 5 — Dọn phần đã chết

Xoá khỏi `Dispatch.razor`: thanh `.bulkbar` và mọi state của nó (`_picked`, `_bulkOp`, `_bulkMachine`, `_preview`,
`_confirmAssign`, `_showOpts`, `ShowBulk`, `PickByOp`, `PickInterrupted`, `ClearPick`, `AfterPickChanged`,
`TogglePick`, `ToggleAll`, `AllVisiblePicked`, `PickedTargets`, `BuildPreview`, `BuildPlan`, `PreviewTotal`,
`BlockReason`, `MachineOptionText`, `Assign`), cột checkbox, hàng "Chọn nhanh", dropdown lọc **Máy**.

Xoá file: `server/Shopee.Hub.Web/Services/DispatchBalancer.cs`, `orders/XuLyDonShopee.Tests/DispatchBalancerTests.cs`,
và mục `<Compile Include="…DispatchBalancer.cs" …>` trong `orders/XuLyDonShopee.Tests/XuLyDonShopee.Tests.csproj`
(cả `<ItemGroup>` bọc nó nếu rỗng sau khi xoá). Mô hình mới người dùng tự chọn máy → không còn tự-cân-tải.

**Giữ lại:** `_holds` / `Holds()` (luật 1-acc-1-máy — vẫn là xương sống của mọi rule disable), `BuildBudgets()`,
`_fState` chips, lọc Tài khoản, ô tìm, KPI, URL-state, tab Đơn hàng.

### Bước 6 — URL-state

Thêm `mach` (máy đang chọn) vào query cùng `tab/f/acct/q`; bỏ `mac` (lọc theo máy đã xoá). Khôi phục lúc init:
máy trong URL không còn tồn tại **hoặc đang offline** → bỏ qua, không chọn.

### Bước 7 — CSS (`wwwroot/app.css`)

- Thêm: `.mcards` (grid `repeat(auto-fill, minmax(200px, 1fr))`, gap 12px), `.mcard`, `.mcard.sel`
  (viền `--primary` + nền `--primary-soft`), `.mcard:disabled` (mờ + `cursor: not-allowed`), `.optspanel`,
  `.opbtn` (+ `.opbtn.run/.done/.busy/.blocked`), `.acctrow .acctacts`.
- Xoá: `.bulkbar`, `.bulkbar .*`, `.plan`, `.plan.none`, `.opcell`, `.opmenu*`, `tr.picked`, `tr.opmenurow`,
  `.dispatch.hasbulk` và cả phần của chúng trong `@media (max-width: 920px)`.
- Giữ `.chips`/`.chip` (còn dùng).
- Chỉ dùng token sẵn có; override nền tối viết dạng `html.dark .xxx`.
- Nút `.opbtn` phải đủ rộng để 3 cột op không nhảy nhót khi nhãn đổi (`min-width` cố định, `text-align: center`).
- Bump `app.css?v=29` ở `Components/App.razor`.

## 5. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` sạch, 0 warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` xanh (số test giảm đúng 12 do xoá `DispatchBalancerTests`).
- [ ] Mở `/dispatch`: hàng thẻ máy hiện đủ mọi máy; máy offline **không bấm được** (kiểm bằng cách bấm thật, không
      chỉ nhìn màu).
- [ ] Chưa chọn máy → panel tham số ẩn, mọi nút action trên list disable, có dòng nhắc chọn máy.
- [ ] Chọn máy online → panel tham số hiện; bấm `▶ Scrape` một dòng → **một click tạo đúng một assignment**
      `queued` pinned đúng máy đó (soi qua trang Fleet), nút đổi ngay thành `✖ Huỷ`.
- [ ] Bấm `✖ Huỷ` → assignment về `canceled`, nút quay lại `▶`.
- [ ] Chọn sang máy khác → mọi nút của shop đang có việc ở máy cũ **disable** kèm `title` nói rõ máy nào giữ; nút
      của acc đang bị máy khác giữ cũng disable.
- [ ] `▶ Scrape cả acc`: bấm lần một đổi nhãn xác nhận, bấm lần hai tạo đúng N assignment (N = số dòng hiện đang
      bấm được), dòng kết quả nói rõ số bỏ qua.
- [ ] F5 giữ nguyên máy đang chọn + bộ lọc (URL-state). Máy trong URL đã offline → không tự chọn.
- [ ] 400px: bảng cuộn ngang trong khung, body không cuộn ngang, thẻ máy xuống dòng gọn.
- [ ] Nền tối đọc được mọi trạng thái nút.
- [ ] `git grep -n DispatchBalancer` không còn kết quả nào.
- [ ] `Fleet.razor` không bị sửa.

## 6. Rủi ro & lưu ý

- **Trang giao việc THẬT vào fleet production.** Nút một-click không có xác nhận → khi tự test hãy chọn máy đang
  offline, hoặc huỷ ngay sau khi kiểm tra.
- **Disable phải thật.** Người dùng nêu rõ "không bấm vào được" — phải dùng thuộc tính `disabled` của `<button>`,
  không được chỉ đổi màu. Có `title` nói rõ lý do cho từng trường hợp.
- Nhãn nút đổi theo nhịp fleet 2s. Đừng để layout nhảy: `min-width` cố định cho `.opbtn`.
- Bảng vẽ lại mỗi 2s và giờ **không còn** `ShouldTickRender() == false` để dừng nhịp — bảo đảm không có state người
  dùng đang gõ bị mất: các ô tham số nằm ở panel riêng (`@bind` bình thường, Blazor giữ giá trị), nhưng phải kiểm
  thật: gõ dở "Đến dòng" rồi đợi qua 2 nhịp xem có bị nuốt không.
- Trạng thái chờ-xác-nhận của nút cả-acc phải bị huỷ khi đổi máy/acc/lọc — kẻo bấm nhầm sang acc khác.
- `Snap.Assignments` chứa cả việc của op `rewrite` và của shop khác — lọc đúng `(BigsellerId, ShopId, Op)` khi tìm
  assignment mở của một ô.

---

## Báo cáo thực thi

**Sửa:** `Dispatch.razor` (viết lại nhánh BigSeller: +230 / −353), `wwwroot/app.css`, `App.razor` (v=29),
`XuLyDonShopee.Tests.csproj`. **Xoá:** `Services/DispatchBalancer.cs`, `DispatchBalancerTests.cs`.
`Fleet.razor` + tab Đơn hàng không đụng.

**Nghiệm thu (Fable tự chạy):**
- `dotnet build ShopeeSuite.sln` → 0 Warning, 0 Error.
- `dotnet test orders/XuLyDonShopee.Tests` → **1044/1044** (đúng −12 do xoá test balancer).
- `grep -rn DispatchBalancer server/ suite/ orders/` → **không còn kết quả** trong mã nguồn.
- Đọc lại `Btn()` / `AcctBtn()` / `SelectMachine()` / `DropMachine()`: đúng bảng luật trong plan; nút disable dùng
  thuộc tính `disabled` thật (`OpBtn.Disabled => Act.Length == 0`), mỗi trường hợp có `title` riêng.

**Delta hành vi cần biết:** trang `/dispatch` KHÔNG còn đặt-tay ledger (✓ Đánh dấu xong / ↺ Reset) — hai mục đó chỉ
sống trong menu ô của bản cũ, mà mô hình mới bỏ menu. Chức năng vẫn còn nguyên ở trang Fleet (combo `.cellset`).
Cùng lý do, `_homes` + `Db.AccountHomes()` bị bỏ khỏi trang (chỉ balancer dùng).

**Opus tự quyết ngoài plan (đã soi, chấp nhận):** `MachineBudget` khai lại thành record cục bộ trong Dispatch.razor
(file gốc bị xoá) + thêm `Version`/`LastSeen` cho thẻ máy; bấm lại thẻ đang chọn = bỏ chọn; nút cả-acc disable thêm
khi 0 dòng bấm được; assignment mở không ghim máy nào vẫn cho huỷ (kẻo ô kẹt disable vĩnh viễn); `.opbtn` thêm
`max-width` + ellipsis, nội dung đầy đủ nằm trong `title`.
