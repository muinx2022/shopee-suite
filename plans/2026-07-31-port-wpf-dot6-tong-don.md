# Plan: Port WPF — Đợt 6: tổng dọn + chuẩn bị phát hành (nhánh `only-windows`)

- **Ngày:** 2026-07-31
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

> **ĐỌC TRƯỚC:** `plans/2026-07-31-port-wpf-ke-hoach-tong.md` + "Báo cáo thực thi" plan đợt 1–5 (mục "Đề nghị
> đợt 6" của đợt 3/4/5 chính là đầu vào của plan này). WORKTREE `d:\Projects\shopee-suite-onlywin` (nhánh
> `only-windows`); TUYỆT ĐỐI không đọc/ghi `d:\Projects\shopee-suite`. Phần nhánh/merge về main do người điều
> phối làm SAU — KHÔNG thuộc plan này.

## 1. Bối cảnh & mục tiêu

Đợt 1–5 xong: toàn bộ UI hai module đã là WPF, build 0 warning, binding log 0. Đợt 6 dọn các món đã ghi nợ
qua 5 đợt + làm nhánh sẵn sàng merge về main.

## 2. Phạm vi

- **Làm:** các mục ở bước 3 dưới. **Không làm:** không đổi nghiệp vụ/VM; không bump `version.txt` (điều phối
  bump khi phát hành); không tách nhánh/merge; không commit.

## 3. Các bước thực hiện

1. **Quét `<Run Text="{Binding…}">` toàn repo** (suite + orders): mọi chỗ bind thiếu `Mode=OneWay` → thêm
   (bẫy đợt 3: mặc định TwoWay, gặp property chỉ-đọc là chết app lúc dựng cây). Grep cả `Run Text=` trong
   *.xaml, liệt kê từng chỗ trong báo cáo (đợt 3 ghi nhận DataView còn ~5 chỗ).
2. **Sửa các lỗi hiển thị đã ghi nhận:**
   - AccountsView orders: nhãn nút "Thêm tài khoản" bị cắt ("Thêm tài khoả…") — nới bố cục cột trái/чиều
     rộng nút cho đủ chữ (giữ tinh thần layout cũ, không thiết kế lại).
   - OrdersView: pill "Trả hàng/Hoàn tiền" cắt ở cột 140px — nới cột hoặc cho pill wrap/thu chữ, chọn phương
     án nhìn ổn nhất, chụp ảnh trước/sau.
   - SearchView tab "Tìm kiếm": hàng cao cố định `190` ép hàng `*` về 0 khi cửa sổ thấp → đổi sang
     `Auto`/`MaxHeight` như đề nghị đợt 4, kiểm ở cửa sổ 1000×700.
   - Template ComboBox phẳng (đợt 4 hoãn): đưa ComboBox về đúng hướng phẳng Win11 của theme (bo 4–6, viền ấm,
     focus cam) — một style chung trong `Theme.xaml`, orders dùng bản module nếu token khác.
3. **Dọn mã:** xoá `suite/Shopee.Suite/Views/PortingWindow.cs` (0 lớp con còn lại — xác minh bằng grep trước
   khi xoá); rà `grep -i avalonia` trong comment/xmldoc của 3 project UI → sửa các câu đã lỗi thời (vd ghi chú
   R2R/WDAC trong `orders/XuLyDonShopee.App.csproj` nói về DLL Avalonia — cập nhật thành ghi chú WPF: ràng
   buộc cũ nhiều khả năng hết áp dụng, cần kiểm lại trên máy WDAC khi phát hành thật).
4. **Xoá đường build Linux khỏi nhánh này:** `release-suite.sh`, `publish-suite.sh`, `install-linux.sh`
   (bản Linux sống ở nhánh `avalonia` sẽ tách sau). `release-suite.cmd` GIỮ NGUYÊN channel `win`. Grep các
   tham chiếu tới 3 file vừa xoá (docs/cmd khác) để khỏi gãy.
5. **CHANGELOG.md:** trong mục `## Chưa phát hành` (đã có entry làm lại Cài đặt), thêm phần port WPF: UI
   chuyển toàn bộ Avalonia → WPF (bản Windows-only), điểm người dùng nhận thấy (chữ ClearType sắc hơn, giao
   diện giữ nguyên bố cục), ghi rõ bản Linux/Ubuntu từ nay phát hành từ nhánh `avalonia`.
6. **Regression toàn cục:** build `--no-incremental` 0 error 0 warning; test 1459 + 61 xanh; chạy lại rig
   nghiệm thu các đợt (2/3/4/5 — cách ly như cũ, đặc biệt đợt 5: redirect USERPROFILE, cấm --mode full, không
   phóng trình duyệt) → binding log 0 dòng ở mọi lượt; chụp ảnh các chỗ sửa ở bước 2.
7. Điền "Báo cáo thực thi" plan này trong worktree (kèm bảng: từng món nợ đợt 3/4/5 → đã xử/why not).

## 4. Tiêu chí nghiệm thu

- [ ] Grep `<Run Text="{Binding` không còn chỗ nào thiếu `Mode=OneWay` (trừ chỗ property có setter VÀ cố ý
      two-way — nếu có, liệt kê + lý do).
- [ ] 4 lỗi hiển thị bước 2 có ảnh trước/sau.
- [ ] Không còn `PortingWindow.cs`; không còn comment Avalonia lỗi thời trong 3 project UI; không còn 3 script
      Linux trên nhánh.
- [ ] Build 0/0; test xanh; mọi rig binding log 0; không sót file tạm/process; production không bị đụng.

## 5. Rủi ро & lưu ý

- Đây là đợt "đánh bóng" — KHÔNG mở rộng: thấy món gì ngoài danh sách thì ghi vào báo cáo, đừng tự xử.
- Mọi lượt chạy thử tuân thủ nguyên xi quy trình cách ly đợt 5 (USERPROFILE giả, không --mode full).
- Smoke-test chế độ Full với máy thật (tắt bản production) KHÔNG thuộc đợt này — điều phối sẽ thu xếp với
  người dùng trước khi phát hành 1.7.0.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<để trống>
