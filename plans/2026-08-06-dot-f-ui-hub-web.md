# Plan: Đợt F — Cải thiện UI hub web (8 mục)

- **Ngày:** 2026-08-06
- **Trạng thái:** hoàn thành (code + nghiệm thu tĩnh; còn checklist bấm tay sau deploy — xem cuối file)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

8 điểm UI hub web từ đợt rà soát 05/08: 2 điểm an toàn (lộ token, tự khóa mật khẩu), 1 điểm nhất quán nền tảng (window.confirm), 5 điểm tiện dụng. Hub là Blazor Server tại `server/Shopee.Hub.Web/`, token/nguyên tắc đã có: mọi view-state vào URL (pattern UrlState), title trang qua MainLayout.UpdateTitle, css token trong `wwwroot/app.css` (nhớ bump `app.css?v=N` trong `Components/App.razor` nếu sửa css), pattern modal sẵn ở RowMapModal (Fleet.razor).

## 2. Phạm vi

- **Làm:** 8 mục phần 3, trong `server/Shopee.Hub.Web/` (razor + css).
- **Không làm:** KHÔNG deploy VM (phiên chính lo sau, gộp với đợt tính năng); không đổi API/DB; không đụng suite/orders; không làm dashboard (đợt H1).

## 3. Các bước thực hiện

### F1. /settings — che token API
`Settings.razor` (~:15): input readonly đang hiện token trần. Mặc định hiện `••••••••` (không đưa token thật vào DOM khi đang che); nút 👁 toggle hiện thật; nút 📋 Copy luôn copy giá trị thật (JS interop clipboard — copy được cả khi đang che).

### F2. /settings — đổi mật khẩu admin an toàn
(~:58) Thêm ô "Nhập lại mật khẩu" + chỉ enable nút Lưu khi 2 ô khớp và không rỗng; hiện dòng lỗi nhỏ khi lệch. (Không cần nút 👁 nếu đã có ô nhập lại.)

### F3. Modal confirm dùng chung thay `window.confirm`
Làm component `Shared/ConfirmDialog.razor` theo token hiện có (khuôn modal RowMapModal): tiêu đề, mô tả hệ quả, nút hủy + nút hành động (variant nguy hiểm = đỏ), Esc = hủy, focus vào nút hủy khi mở. Thay TOÀN BỘ `window.confirm`/`JS.InvokeAsync<bool>("confirm", …)` hiện có: Machines.razor (~:159/167/207/216), Shops.razor (~:126), Logs.razor (~:114) — grep `confirm(` toàn Components/ để không sót chỗ khác. Nội dung xác nhận viết tiếng Việt, nêu rõ hệ quả (vd "Xóa máy X và CHẶN nó đăng ký lại. Mọi việc đang giữ sẽ bị thu hồi.").

### F4. /machines — gom nút phụ vào menu ⋯
(~:69–77) Mỗi dòng đang có 3–4 nút chữ dài ngang hàng, nút phá hoại "🗑 Xoá & chặn" sát nút thường. Giữ nút chính (▶ Tiếp tục / ⬆ Cập nhật) hiện trực tiếp; gom "⟳ Reset việc" + "🗑 Xoá & chặn" vào menu ⋯ per-dòng (dropdown thuần Blazor + css, đóng khi click ngoài/Esc; không kéo lib ngoài — CSP chặn). Mục nguy hiểm trong menu tô đỏ.

### F5. /orders — toggle ẩn/hiện cột, lưu URL
(~:49–51, bảng 13 cột.) Thêm nút "Cột ▾" mở danh sách checkbox cột phụ (Cuối cùng, Phân loại, Sync, và các cột ít dùng khác — giữ Shop/Mã đơn/Trạng thái luôn hiện). Trạng thái ẩn/hiện vào URL query (vd `?cols=...`) theo đúng pattern UrlState của trang; F5/share giữ nguyên lựa chọn.

### F6. /logs-view — tìm text + tạm dừng
(~:10–27.) Thêm ô tìm chuỗi lọc client-side trên `_logs` (mã đơn/tên shop/chuỗi bất kỳ, không phân biệt hoa thường) + nút ⏸/▶ ngắt-nối vòng `PollAsync` (đang refresh 2s làm mất vị trí khi soi). Ô tìm + trạng thái pause vào URL nếu rẻ (pause thì không cần persist).

### F7. /shops — sửa inline
(~:16–32.) Form sửa hiện chèn phía trên bảng, đẩy bảng xuống, mất ngữ cảnh dòng đang sửa. Đổi thành sửa inline: bấm ✎ thì ô Tên/Ghi chú của CHÍNH DÒNG ĐÓ thành input + nút ✓/✕; bảng đứng yên. Enter=lưu, Esc=hủy.

### F8. /dispatch — rút nhãn field
(~:108–113.) Nhãn đang nhét chú thích dài làm gãy dòng. Rút nhãn ngắn ("Số process", "Tk/khung", …) + chuyển chú thích vào `title` tooltip (pattern title dùng khắp trang); GIỮ dòng ghi chú chung "0 = dùng cấu hình client".

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build server/ShopeeHub.sln` 0 error 0 warning; `dotnet test server/Shopee.Hub.Web.Tests` xanh.
- [ ] `rg 'confirm\(' server/Shopee.Hub.Web/Components` = 0 hit (ngoài ConfirmDialog nếu có tên trùng).
- [ ] F1: xem source DOM khi đang che KHÔNG chứa token thật (kiểm bằng render logic — token chỉ bind khi 👁 bật); Copy hoạt động cả khi che.
- [ ] F5: URL thay đổi khi toggle cột; mở URL đó ở tab mới → đúng bộ cột.
- [ ] Nếu sửa `app.css`: đã bump `app.css?v=N` trong App.razor.
- [ ] Chạy hub local (`dotnet run --project server/Shopee.Hub.Web` hoặc theo README server) + duyệt nhanh 6 trang đã sửa bằng trình duyệt (curl/HttpClient chỉ bắt lỗi render 500; điểm tương tác ghi lại để phiên chính duyệt bằng browser).

## 5. Rủi ro & lưu ý

- Blazor Server: dropdown/menu ⋯ + modal tự viết phải xử lý đóng-khi-click-ngoài bằng cách Blazor-friendly (overlay bắt click), đừng gắn event JS toàn cục dễ rò handler qua circuit.
- F1 đừng để token lọt vào attribute ẩn/`data-*` khi đang che — "che" phải là không render, không phải css.
- Mobile: các trang đã responsive (đợt 13/07) — menu ⋯ và bảng cột phải kiểm tra lại ở bề ngang hẹp (class m-hide hiện có).
- KHÔNG commit, KHÔNG deploy.

---

## Báo cáo thực thi (Opus điền sau khi xong)

**Trạng thái: 8/8 mục ĐÃ LÀM.** Không commit, không deploy. Chỉ sửa trong `server/Shopee.Hub.Web/`.

### File tạo mới
- `server/Shopee.Hub.Web/Components/Shared/ConfirmDialog.razor` (mới)

### File sửa
`Components/App.razor` (bump `app.css?v=39` → `?v=40`) · `wwwroot/app.css` (+33 dòng) ·
`Components/Pages/Settings.razor` · `Machines.razor` · `Shops.razor` · `Logs.razor` · `Orders.razor` ·
`Dispatch.razor` · `Files.razor` · `Search.razor` · `AllData.razor` ·
`Components/Shared/ImportExcelWizard.razor` · `Components/Shared/ProductGridPanel.razor`.

### Từng mục
- **F1 (che token)** — ô token render `_showToken ? _token : "••••••••••••"`; nút `👁 Hiện / 🙈 Ẩn`; nút
  `📋 Copy` vẫn đọc `_token` thật (không đọc từ ô). `onfocus="this.select()"` CHỈ gắn khi đang hiện (khi che
  attribute bị bỏ hẳn) để bôi-đen không copy nhầm dấu chấm. Đổi token xong tự che lại.
- **F2 (đổi mật khẩu)** — thêm `_newPass2` + ô "Nhập lại mật khẩu" (`@bind:event="oninput"`), dòng
  `.fielderr` khi lệch, nút Lưu `disabled` tới khi 2 ô khớp & khác rỗng. `SavePass` vẫn kiểm tra lại
  (khớp → ≥ 6 ký tự) chứ không tin mỗi UI.
- **F3 (ConfirmDialog)** — component dùng chung, API `AskAsync(title, message, okText, cancelText, danger)`
  trả `Task<bool>` qua `TaskCompletionSource` (RunContinuationsAsynchronously). Overlay bắt click = huỷ,
  `@onkeydown` Esc = huỷ, `ElementReference.FocusAsync()` đưa focus vào nút Huỷ khi mở, `Dispose` trả false
  để chỗ `await` không treo khi rời trang. Thay **11 chỗ** `JS.InvokeAsync<bool>("confirm", …)`:
  Machines ×4, Shops, Logs, Settings, Files, Search, ImportExcelWizard, và 2 chỗ tiêm vào `ProductGridEngine`
  (AllData + ProductGridPanel — qua `ConfirmDialog.AskEngineAsync`, câu hỏi có chữ "xoá" thì nút đỏ).
  Gỡ luôn `@inject IJSRuntime JS` ở 8 file không còn dùng (Settings vẫn giữ — clipboard).
- **F4 (menu ⋯ /machines)** — giữ `▶ Tiếp tục` + `⬆ Cập nhật` ngoài; `⟳ Reset việc` + `🗑 Xoá & chặn` vào
  menu `⋯` per-dòng (`_menuFor` = machineId). Dropdown thuần Blazor: `.menu-backdrop` (nút trong suốt
  `position:fixed`) bắt click ngoài, `@onkeydown` Esc, mục nguy hiểm `.menu-item.danger` đỏ. Handler
  Reset/Delete đóng menu TRƯỚC khi `await` hộp xác nhận.
- **F5 (chọn cột /orders)** — nút `🧩 Cột ▾ (ẩn N)` mở menu 10 checkbox + "↺ Hiện tất cả". Shop / Mã đơn /
  Trạng thái luôn hiện. **Khác plan ở tên key:** dùng `?hide=` (danh sách cột ĐANG ẨN) thay vì `?cols=` —
  `cols=` mà giá trị lại là cột-bị-ẩn thì sai nghĩa, còn liệt kê cột-đang-hiện thì mặc định phải nhét cả 10
  key vào URL. Mặc định (hiện đủ) = key vắng mặt, đúng quy ước UrlState. Key lạ trong URL bị bỏ qua. Khối
  "Chi tiết" mobile cũng bám theo cột đang ẩn; ẩn hết cột m-hide thì bỏ luôn nút xổ.
- **F6 (/logs-view)** — ô "Tìm" lọc client-side trên `_logs` (khớp cả `Text` lẫn `Hostname`,
  OrdinalIgnoreCase), vào URL `?q=`; nút `⏸ Tạm dừng / ▶ Chạy lại` cho `PollAsync` bỏ lượt nạp (giữ nhịp
  timer, không persist vào URL). Dòng đếm đổi thành `N/M dòng` khi đang lọc.
- **F7 (/shops sửa inline)** — bỏ form `editcard` phía trên bảng; ô Tên/Ghi chú của ĐÚNG dòng đang sửa
  thành input + nút `✓ Lưu` / `✕`. Enter = lưu, Esc = huỷ. Cột Ghi chú vốn `m-hide`: dòng đang sửa được
  BỎ class đó để trên điện thoại vẫn sửa được ghi chú. Xoá đúng shop đang sửa thì tự thoát chế độ sửa.
- **F8 (/dispatch nhãn)** — nhãn rút còn "Số process / Tk / khung / Reload (giây) / Từ tab "Đã nhận"", chú
  thích "op nào đọc field này" chuyển vào `title`; thêm title cho Từ dòng / Đến dòng; GIỮ dòng
  "0 = dùng cấu hình của máy client."

### Kết quả kiểm chứng (chạy thật)
1. `dotnet build server/ShopeeHub.sln --no-incremental` → **Build succeeded. 0 Warning(s), 0 Error(s)**
   (lần cuối 00:00:04.70).
2. `dotnet test server/Shopee.Hub.Web.Tests` → **Passed! Failed: 0, Passed: 53, Skipped: 0, Total: 53**.
3. `rg 'confirm\(' server/Shopee.Hub.Web/Components` → **0 hit**. Grep rộng hơn
   `InvokeAsync<bool>\("confirm"|window\.confirm` → chỉ còn 1 hit là **dòng chú thích** trong
   ConfirmDialog.razor.
4. Đã bump `app.css?v=40` (App.razor:15); trang trả về đúng `href="app.css?v=40"`, `/app.css?v=40` = 200,
   50.200 B, có đủ `.confirm-modal/.menu-backdrop/.inline-edit/.fielderr`.
5. **Chạy hub local**: `dotnet run --project server/Shopee.Hub.Web` với `HUB_DATA_DIR` trỏ vào thư mục tạm
   (DB throwaway, đã xoá sau khi xong — KHÔNG đụng `server/Shopee.Hub.Web/hub-data`). Lưu ý: `dotnet run`
   luôn nghe **127.0.0.1:8088** theo `Properties/launchSettings.json`, biến `ASPNETCORE_URLS` bị ghi đè.
   Đăng nhập bằng cookie rồi GET (Blazor prerender ⇒ HTML trả về là render THẬT của server):

   | Trang | HTTP |
   |---|---|
   | `/` `/machines` `/shops` `/logs-view` `/orders` `/settings` `/files` `/search` `/data` `/dispatch?mach=…` | 200 (không trang nào 500) |

   Log server sau toàn bộ lượt duyệt: 0 exception.

   Đã kiểm bằng dữ liệu bơm qua API client (1 máy heartbeat, 1 shop, 3 dòng log):
   - **F1 đạt tiêu chí:** HTML `/settings` chứa **0** lần chuỗi token thật, ô token render 12 dấu `•`,
     attribute `onfocus` biến mất khi đang che.
   - **F2:** hai ô password render, nút Đổi mật khẩu có thuộc tính `disabled` lúc trống.
   - **F5 đạt tiêu chí:** `/orders?hide=sku,loai,phieu,sync,badkey` → thead còn 9 `<th>` (đúng 13−4),
     `colspan="9"`, nút hiện "🧩 Cột ▾ (ẩn 4)", key rác `badkey` bị bỏ. Kết hợp
     `?status=all&q=abc&hide=sku,sync` → cả bộ lọc lẫn cột đều khôi phục đúng (11 `<th>`, ô Tìm = "abc").
   - **F6:** `/logs-view?q=BETA` → khôi phục ô tìm, lọc đúng 2/3 dòng (khớp cả text "Beta" lẫn host
     "PC-BETA"), stat hiện "2/3 dòng", nút ⏸ render.
   - **F4:** dòng máy render nút `⋯` trong `.menuwrap`. **F7:** dòng shop render `✎ Sửa` / `🗑 Xoá`.
   - **F8:** 6 tooltip decode ra đúng câu tiếng Việt, nhãn ngắn, dòng "0 = dùng cấu hình của máy client."
     còn nguyên.

### Bẫy đã gặp & xử lý (đáng ghi lại)
- **Blazor HTML-encode MỌI giá trị attribute.** Viết `&amp;` / `&quot;` trong `title=` sẽ hiện ra đúng chữ
  `&amp;` / `&quot;` trên tooltip (bắt được lúc soi HTML thật, không phải lúc build). Đã sửa: dùng ký tự
  `&` thẳng, và bọc attribute bằng nháy đơn khi cần dấu `"`. Trong NỘI DUNG thẻ thì `&amp;` vẫn đúng
  (markup tĩnh đi thẳng) — nên `🗑 Xoá &amp; chặn` ở nhãn nút giữ nguyên.
- **`.tablewrap` xén dropdown**: `overflow-x:auto` làm `overflow-y` tính ra `auto` ⇒ menu ⋯ xổ ra bị cắt.
  Xử lý bằng `.tablewrap:has(.menu) { overflow: visible; }` — chỉ nhả kẹp ĐÚNG LÚC menu mở (`.menu` chỉ tồn
  tại khi mở), đóng menu là trở lại cuộn ngang. `:has()` đã được dùng sẵn nhiều chỗ trong app.css.

### Điểm TƯƠNG TÁC cần phiên chính duyệt bằng browser thật (curl không kiểm được)
1. `/settings`: bấm 👁 → ô hiện token thật; bấm 🙈 → che lại. Bấm 📋 Copy **khi đang che** → clipboard phải
   ra token thật. Gõ lệch 2 ô mật khẩu → hiện dòng đỏ + nút Lưu vẫn xám.
2. `/machines`: bấm `⋯` → menu xổ **không bị bảng cắt** (thử cả dòng CUỐI bảng); bấm ra ngoài / Esc → đóng;
   bấm `🗑 Xoá & chặn` → hộp xác nhận đỏ, focus ở nút Huỷ, Esc = huỷ, bấm nền = huỷ.
3. `/orders`: mở `🧩 Cột ▾`, tick/bỏ tick vài cột → URL đổi ngay (`?hide=…`), menu **không tự đóng** khi
   tick; F5 và mở URL ở tab mới → đúng bộ cột. "↺ Hiện tất cả" xoá `hide` khỏi URL.
4. `/logs-view`: gõ vào ô Tìm (lọc tức thì + URL `?q=`), bấm ⏸ → khung log đứng yên khi có log mới chảy về,
   bấm ▶ → bắt kịp.
5. `/shops`: bấm ✎ → input hiện ngay trên dòng đó, bảng KHÔNG xê dịch; Enter lưu, Esc huỷ; thử ở bề ngang
   hẹp (< 920px) xem ô Ghi chú của dòng đang sửa có hiện không.
6. `/data` + tab 📋 Dữ liệu của Fleet: bấm "Xoá dòng đã chọn" / "Sinh SKU mới" → hộp xác nhận mới hiện đúng
   (đường tiêm qua `ProductGridEngine`); wizard ⬆ Nhập Excel chế độ Ghi đè → hộp xác nhận phải nằm **trên**
   modal wizard.
7. Kiểm nền tối (nút 🌙) cho hộp xác nhận + menu, và bề ngang điện thoại cho menu ⋯ / Cột ▾.

### Chưa làm / lưu ý
- KHÔNG commit, KHÔNG deploy (đúng yêu cầu).
- Không dùng browser thật để bấm thử: các bước đó cần đăng nhập bằng mật khẩu vào form → để phiên chính làm
  (đã liệt kê checklist ở trên). Mọi thứ kiểm được không cần bấm thì đã kiểm bằng HTML render thật.
- Build/test chạy trong lúc agent đợt G đang sửa `suite/` + `orders/`; hub link vài file nguồn của
  `orders/XuLyDonShopee.Core` nên có ăn theo thay đổi của họ — vẫn 0 warning / 53 test xanh.

---

## Nghiệm thu (Fable tổng hợp sau phản biện, 2026-08-06)

`nghiem-thu` chấm **ĐẠT CÓ ĐIỀU KIỆN** — mọi tiêu chí đo được đều đạt và nó tự kiểm bằng HTML render thật
(hub local + dữ liệu bơm qua API): token che thật (0 lần chuỗi token trong DOM, onfocus biến mất khi che),
?hide= hoạt động đúng mọi ca biên (key rác, ẩn hết, mobile). Vòng đời ConfirmDialog soi kỹ: không rò, không
treo, không double-set. Quyết định `?hide=` thay `?cols=`: ĐỒNG Ý giữ. Sai số tài liệu: 12 chỗ confirm được
thay (executor ghi 11).

Điều kiện = các điểm CHỈ xác nhận được bằng bấm tay (phiên chính đã thử tự bấm qua browser nhưng DB trắng
đòi tạo admin + đăng nhập — ngoài quyền của agent) → chuyển thành CHECKLIST SAU DEPLOY, quan trọng nhất trước:
1. **(M1)** /machines cửa sổ ~1100–1300px: cuộn bảng sang phải rồi bấm ⋯ — bảng có NHẢY về trái / trang có
   mọc thanh cuộn ngang / thead sticky có giật không. (Nguyên nhân: `.tablewrap:has(.menu){overflow:visible}`
   nhả cả overflow-x. Nếu giật → sửa menu sang position:fixed tính tọa độ từ nút.)
2. **(M2)** Menu ⋯ đang mở, bấm chỗ khác = chỉ đóng menu (phải bấm 2 lần) — chủ ý, xác nhận chấp nhận được.
3. **(L4)** Hộp xác nhận mở ra, bấm Enter ngay = HỦY (đổi ngữ nghĩa so window.confirm vốn Enter=OK — an toàn
   hơn nhưng khác thói quen, nhất là nút "Cập nhật app cho N máy").
4. **(L3)** Bấm ⋯ rồi Esc không di chuột: Safari/máy không focus nút có thể không đóng (backdrop vẫn đóng được).
5. /data + tab 📋 Fleet: sửa dòng đổi SKU trùng → hộp xác nhận phải nằm TRÊN modal.
6. Copy token khi đang che vẫn copy giá trị thật; thử cả nền tối.
7. Menu ⋯ ở DÒNG CUỐI bảng không bị cắt.

Ghi nhận không sửa đợt này: `:has()` cần Chrome/Edge hiện đại hoặc Safari ≥15.4 (fleet dùng browser mới — chấp
nhận); ô "Đổi token" vẫn hiện token MỚI trần khi bấm 🎲 (đó là ô nhập, user cần thấy — ngoài phạm vi); ô Tìm
logs không debounce (không đụng DB, chỉ chatty); nút đỏ ConfirmDialog chọn theo chữ "xoá" trong message (SKU
user gõ chứa "xoá" sẽ đỏ oan — cosmetic).
