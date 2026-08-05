# Plan: Đợt F — Cải thiện UI hub web (8 mục)

- **Ngày:** 2026-08-06
- **Trạng thái:** chờ làm (song song được với đợt G — khác project, khác solution)
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

<chưa có>
