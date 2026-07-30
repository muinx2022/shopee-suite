# Plan: Port WPF — Đợt 2: AccountsView + DataView + BigSellerView (nhánh `only-windows`)

- **Ngày:** 2026-07-31
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

> **ĐỌC TRƯỚC:** `plans/2026-07-31-port-wpf-ke-hoach-tong.md` (20 quyết định kỹ thuật, QĐ n) và mục
> "Báo cáo thực thi" của `plans/2026-07-31-port-wpf-dot1-ha-tang-shell.md` (quy ước đã hình thành ở đợt 1:
> bảng dịch `Classes`→`x:Key` trong đầu `Theme.xaml`, `cardButton`, `ribbonNav`, `WatermarkAssist`,
> `AppBrushes`, hook log binding `SHOPEESUITE_BINDING_LOG`). Việc chạy trong WORKTREE
> `d:\Projects\shopee-suite-onlywin` (nhánh `only-windows`) — TUYỆT ĐỐI không đọc/ghi
> `d:\Projects\shopee-suite`.

## 1. Bối cảnh & mục tiêu

Đợt 1 đã xong: solution là WPF thuần, shell chạy, mọi màn module đang placeholder. Đợt 2 port 3 màn đầu
tiên + 2 cửa sổ con của chúng. **Nguồn đối chiếu**: bản Avalonia cũ đọc bằng
`git show d6bb696:<đường-dẫn>.axaml` (d6bb696 = commit gốc trước khi xoá; KHÔNG còn trên đĩa):

| Port sang | Nguồn Avalonia (qua `git show d6bb696:`) | Ghi chú |
|---|---|---|
| `suite/Shopee.Suite/Modules/Accounts/AccountsView.xaml` (+.cs) | `.../Accounts/AccountsView.axaml` (249 dòng, code-behind 20 dòng) | DataGrid danh sách tài khoản; double-click dòng mở Brave (code-behind) |
| `suite/Shopee.Suite/Modules/Accounts/ImportAccountsWindow.xaml` (+.cs) | `.../Accounts/ImportAccountsWindow.axaml` (34 dòng) | Modal 2 TextBox; `Close(true/false)` → `DialogResult` (QĐ 13) |
| `suite/Shopee.Suite/Modules/Data/DataView.xaml` (+.cs) | `.../Data/DataView.axaml` (217 dòng, code-behind 33 dòng) | DataGrid + Watermark + phân trang; code-behind `Gestures.TappedEvent` bubble |
| `suite/Shopee.Suite/Modules/Data/RowEditWindow.xaml` (+.cs) | `.../Data/RowEditWindow.axaml` (106 dòng, code-behind 32 dòng) | Modal sửa dòng |
| `suite/Shopee.Suite/Modules/BigSeller/BigSellerView.xaml` (+.cs) | `.../BigSeller/BigSellerView.axaml` (351 dòng) | DataGrid + form shop/account |

## 2. Phạm vi

- **Làm:** 5 file XAML trên + code-behind; thay 3 DataTemplate placeholder trong `App.xaml`
  (AccountsViewModel/DataViewModel/BigSellerViewModel → view thật); XOÁ 2 placeholder C# tương ứng
  (`Modules/Accounts/ImportAccountsWindow.cs`, `Modules/Data/RowEditWindow.cs` — thay bằng Window XAML thật,
  GIỮ NGUYÊN tên class + chữ ký mà ViewModel/WindowHost đang gọi); bổ sung style DataGrid/control còn thiếu
  vào `Theme.xaml` (đối chiếu selector cũ có comment `<!-- đợt N -->`).
- **Không làm:** các view khác (Workspace/Search/CheckAccount/Fleet/Settings/orders) vẫn placeholder;
  không đổi ViewModel (trừ khi phát hiện lỗi compile thật sự do port — ghi rõ báo cáo); không commit.

## 3. Các bước thực hiện

1. Đọc kỹ 5 file nguồn qua `git show d6bb696:` + code-behind; đọc `Theme.xaml` hiện có (bảng quy ước đầu
   file) trước khi viết XAML.
2. Port từng view theo đúng QĐ 5/6/15/16 (IsVisible→Visibility, Classes→StaticResource, Watermark→
   WatermarkAssist, cú pháp Grid/Spacing/ToolTip). Code-behind:
   - AccountsView: double-click DataGrid → `MouseDoubleClick` (lọc đúng dòng dữ liệu, bỏ qua header/blank).
   - DataView: `Gestures.TappedEvent` + `GetVisualParent` → `PreviewMouseLeftButtonUp` +
     `VisualTreeHelper` đi lên tìm ancestor (giữ nguyên hành vi chọn ô/dòng cũ).
   - RowEditWindow/ImportAccountsWindow: `ShowDialog<bool?>` cũ → `DialogResult` + property kết quả,
     khớp đúng cách `WindowHost`/VM đang await.
3. Style DataGrid dùng chung đặt ở `Theme.xaml` (header/row/hover/selected đã có từ đợt 1 — mở rộng nếu 3
   view này cần thêm: cột readonly, wrap text, row màu theo binding brush của VM…). KHÔNG nhét style dùng
   chung vào từng view.
4. Build + test + chạy: `dotnet build ShopeeSuite.sln` 0 error 0 warning; `dotnet test` 2 project xanh.
   Chạy app **`--mode workspace`** với data-dir cách ly như đợt 1 đã làm (tạo `data-dir.txt` trỏ thư mục
   tạm — XOÁ file này sau khi xong; tuyệt đối không đụng `%APPDATA%` thật, không bấm nút nào phóng
   Brave/launcher thật vì máy đang chạy bản production). Mở lần lượt 3 màn, chụp màn hình từng màn, mở thử
   RowEditWindow/ImportAccountsWindow nếu mở được bằng dữ liệu tạm; bật `SHOPEESUITE_BINDING_LOG` xác nhận
   **0 dòng lỗi binding**.
5. Điền "Báo cáo thực thi" vào plan này (bản trong worktree): file đã tạo/sửa/xoá, số liệu build/test,
   mô tả + đường dẫn ảnh chụp, điểm trệch plan.

## 4. Tiêu chí nghiệm thu

- [ ] Build 0 error 0 warning; test 1459 + 61 xanh.
- [ ] 3 màn hiện view thật (không còn placeholder), bố cục khớp bản Avalonia cũ (so theo nguồn
      `git show d6bb696:`), icon/nút/tooltip đủ.
- [ ] 2 cửa sổ con mở-đóng đúng chu trình modal (`DialogResult` trả về đúng cho VM).
- [ ] `SHOPEESUITE_BINDING_LOG`: 0 dòng khi mở 3 màn.
- [ ] Không sót `data-dir.txt`/dữ liệu tạm trong worktree.

## 5. Rủi ro & lưu ý

- Brush theo dòng của VM (AccountItemViewModel/DataRowItem) đã qua `AppBrushes` (Freeze) từ đợt 1 — nếu
  view bind brush đó vào DataGrid row thì chỉ việc bind, đừng tạo brush mới trong XAML converter.
- DataGrid WPF: sự kiện/part khác Avalonia (xem plan tổng mục 5) — hành vi lấy theo nghĩa, không dịch chữ.
- Placeholder `CheckAccountWindow`/`ScrapeStatsWindow` GIỮ NGUYÊN (đợt 3–4).
- Máy đang chạy bản production: mọi lượt chạy thử đều qua data-dir tạm; không giết process Brave nào.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<để trống>
