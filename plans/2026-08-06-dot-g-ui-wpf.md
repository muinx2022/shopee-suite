# Plan: Đợt G — Cải thiện UI 2 app WPF (suite + Đơn hàng)

- **Ngày:** 2026-08-06
- **Trạng thái:** chờ làm (song song được với đợt F — khác project/solution)
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

Các điểm UI WPF từ đợt rà soát 05/08, bám định hướng UI phẳng Win11 đã chốt (bo 4/6, không gradient/bóng, header tối đặc, active kiểu Win11 nhẹ; 2 bộ token riêng suite vs Đơn hàng). Toàn bộ là XAML + ít code-behind/VM; không đổi nghiệp vụ.

## 2. Phạm vi

- **Làm:** 9 mục phần 3 (suite: G1–G5, orders app: G6–G9).
- **Không làm:** không đổi hành vi nghiệp vụ; không đụng server; không thêm màn mới (đợt H); không đổi 2 bộ token màu.

## 3. Các bước thực hiện

### Suite (`suite/Shopee.Suite`)

**G1. Style chip trạng thái header dùng chung.** 4 màn (SearchView ~19–29, BigSellerView ~15–25, CheckAccountView ~17–27, AccountsView ~13–32) copy khối header chip CHỒNG LỚP (Grid 1 ô, chip đè cùng ô tiêu đề → status dài/cửa sổ hẹp là chồng chữ). Màn Cài đặt đã sửa đúng mẫu (UnifiedSettingsView ~37–55: 2 cột + StringToVis + MaxWidth 420). Rút mẫu đó thành style/ControlTemplate dùng chung trong Theme.xaml (vd key `headerStatusChip` + layout 2 cột chuẩn) rồi áp cho cả 4 màn (AccountsView có thêm nút Check Acc trong header — giữ nút, vẫn theo layout 2 cột). Workspace header hiển thị Status chữ trần (~:257) — đổi sang cùng chip cho nhất quán nếu không phá layout, không ép được thì ghi lại.

**G2. Chuẩn hóa bo góc về 4/6.** Hiện rải 3–8: nút 5 (Theme.xaml ~:186), popup ComboBox 8 (~:265), khung subtabs 7 (~:932), khung lưới shop 7 (WorkspaceView ~:504), pill op 3 (WorkspaceView ~:328), chip 4, thẻ 6. Quy ước: control nhỏ (nút, input, chip, pill) = 4; panel/khung/popup/thẻ = 6. Sửa toàn bộ CornerRadius trong Theme.xaml + các view suite về 2 nấc này (grep `CornerRadius` toàn suite/Shopee.Suite).

**G3. Nút "Dừng việc shop này" thôi overlay.** WorkspaceView (~:70–72): nút đang đè góc trên-phải ngoài TabControl, khay tab chừa lề phải cứng 210px. Đưa nút vào cùng hàng với khay subtab (DockPanel/Grid cột Auto bên phải), bỏ margin cứng — tự co giãn.

**G4. Tham số hóa 4 cột op lưới shop.** WorkspaceView ~:527/551/588/624 — 4 khối DataGridTemplateColumn ~121 dòng gần copy nguyên xi (Grid > Button wsAction + doneBadge + PathIcon). Gộp về 1 DataTemplate dùng chung + attached property/Tag chứa (op, icon, tooltip, command); GIỮ khác biệt có chủ đích: 2 cột IMPORT/UPDATE có ContextMenu xóa tiến độ resume mà SCRAPE/TÊN SP không có.

**G5. Chữ gợi ý phím tắt theo số tab thật.** MainWindow (~:82) ghi cứng "Ctrl + 1…4" trong khi chế độ Shopee chỉ 2 tab, Workspace 3. Bind theo `Tabs.Count` (vd "Ctrl + 1…{n}") hoặc đổi chữ chung chung.

### App Đơn hàng (`orders/XuLyDonShopee.App`)

**G6. Bảng đơn: ghim cột + ẩn cột phụ.** OrdersView (~:196–253, 15 cột ~1600px): đặt `FrozenColumnCount="2"` (ghim Shop + Mã đơn — kiểm tra thứ tự cột thực tế, ghim 2 cột đầu dùng đối chiếu); thêm ContextMenu trên header cho ẩn/hiện các cột phụ ít dùng (Ước tính, Thanh toán, …) — lưu lựa chọn vào settings local của app (SettingsRepository nếu có khuôn, không thì session-only, ghi rõ chọn gì).

**G7. Nút đóng banner địa chỉ dễ bấm, hết trùng ngôn ngữ.** AccountsView (~:694–700): X đỏ 11px trùng với X đỏ "shop lỗi" ở cột tiến độ ngay dưới (~:722–726). Đổi thành nút chữ "Đóng" (kèm ✕ nhỏ), vùng bấm ≥ 28px, style theo token. GIỮ nguyên hành vi đóng (luật rev/tombstone — CHỈ đổi hình thức nút, không đụng logic).

**G8. Chip preset ngày ở màn Thống kê.** OrderStatisticsView (~:79–94, 2 DatePicker Từ/Đến): thêm hàng chip "Hôm nay · 7 ngày · Tháng này" (kiểu segmented Win11, tái dùng style subtab/tabBadge của theme orders) — bấm chip là set 2 DatePicker tương ứng (theo giờ VN) và refresh.

**G9. Vặt:** (a) AccountsView cột trái Width=360 đo tay từng dính cắt chữ (~:248–251) → MinWidth + Auto cho hàng nút; (b) badge "TK chưa xác nhận" hardcode `#FDECEA/#F5C6C0` (~:361–366) → token họ WarnSoft*/Danger* trong Colors.xaml; (c) hợp nhất style trùng tên lệch định nghĩa phía suite: 2 bản `fieldLabel` (WorkspaceView ~:218–224 vs RowEditWindow ~:18–20) + `sectionLabel` (UnifiedSettingsView ~:22–27) chồng vai `section` (Theme ~:156–160) → 1 bản trong Theme, override margin tại chỗ.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` 0 error 0 warning (XAML compile qua BAML là lưới bắt lỗi cú pháp chính).
- [ ] 3 bộ test xanh (test orders có test dựng control WPF thật — phải xanh).
- [ ] Grep `CornerRadius` suite: chỉ còn giá trị 4 hoặc 6 (liệt kê ngoại lệ có lý do nếu giữ).
- [ ] G4: 4 cột op còn 1 template; diff chứng minh ContextMenu Import/Update được giữ.
- [ ] App khởi động được: chạy `dotnet run`/exe build của Shopee.Suite ở chế độ hiện tại, cửa sổ mở không crash, mở lần lượt các màn đã sửa (Search/BigSeller/CheckAccount/Accounts/Workspace/Đơn hàng/Thống kê). Nếu môi trường không cho tương tác UI thì tối thiểu app phải khởi động + không có binding error trong Output (bật `PresentationTraceSources`/soi stderr), ghi rõ đã kiểm tới đâu.
- [ ] Báo cáo kèm ảnh chụp màn hình từng màn đã sửa nếu chụp được (memory `verify-wpf-ui-tren-may-nay`: dùng UIA, chú ý DPI); không chụp được thì ghi rõ.

## 5. Rủi ro & lưu ý

- Binding error WPF chết câm — sau khi sửa XAML phải soi trace binding lúc app chạy, đừng chỉ tin build xanh.
- G6 FrozenColumnCount đổi hành vi cuộn ngang — kiểm tra không phá cột template (nút/hyperlink) ở 2 cột bị ghim.
- G1/G9c đụng ResourceDictionary scope: style cùng key ở scope khác nhau resolve khác — sau hợp nhất phải mở TỪNG màn dùng style đó.
- App đang được user dùng làm việc thật — KHÔNG để app tự khởi động chiếm foreground lâu; mở kiểm tra rồi đóng.
- KHÔNG commit.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<chưa có>
