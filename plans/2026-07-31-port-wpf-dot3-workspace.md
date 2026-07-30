# Plan: Port WPF — Đợt 3: WorkspaceView (màn nặng nhất) + ScrapeStatsWindow (nhánh `only-windows`)

- **Ngày:** 2026-07-31
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

> **ĐỌC TRƯỚC:** `plans/2026-07-31-port-wpf-ke-hoach-tong.md` (20 quyết định kỹ thuật) + mục "Báo cáo thực
> thi" của plan đợt 1 và đợt 2 (quy ước đã hình thành + các bẫy đã gặp). Việc chạy trong WORKTREE
> `d:\Projects\shopee-suite-onlywin` (nhánh `only-windows`); TUYỆT ĐỐI không đọc/ghi
> `d:\Projects\shopee-suite`.

## 1. Bối cảnh & mục tiêu

Đợt 1–2 xong: shell + Accounts/Data/BigSeller đã là WPF thật, build 0 warning, binding log 0. Đợt 3 port màn
lớn nhất của app:

| Port sang | Nguồn Avalonia (`git show d6bb696:<path>`) | Ghi chú |
|---|---|---|
| `suite/Shopee.Suite/Modules/Workspace/WorkspaceView.xaml` (+.cs) | `.../Workspace/WorkspaceView.axaml` (**924 dòng**, code-behind 27 dòng) | TabControl nhiều tab con, ≥2 DataGrid, ô log, 16 chỗ pseudo-class, `$parent`, code-behind dùng `FindAncestorOfType<DataGridRow>` + Tapped |
| `suite/Shopee.Suite/Modules/Scrape/ScrapeStatsWindow.xaml` (+.cs) | `.../Scrape/ScrapeStatsWindow.axaml` (54 dòng, code-behind 21 dòng) | Cửa sổ thống kê scrape — thay placeholder C# `Modules/Scrape/ScrapeStatsWindow.cs` hiện tại (GIỮ nguyên tên class/chữ ký nơi gọi) |

Các VM liên quan (WorkspaceViewModel/WorkspaceShopViewModel/WorkspaceStatsViewModel/Scrape*) đã port từ đợt 1
(brush qua `AppBrushes`) — đợt này CHỈ làm view.

## 2. Phạm vi

- **Làm:** 2 view trên; thay DataTemplate placeholder của WorkspaceViewModel trong `App.xaml` bằng view thật;
  xoá placeholder `ScrapeStatsWindow.cs`; port nốt các style Theme.xaml đang đánh dấu `<!-- đợt N -->` mà
  WorkspaceView cần (đối chiếu selector Workspace trong Theme.axaml cũ qua `git show d6bb696:`).
- **Không làm:** Search/CheckAccount/Fleet/Settings/orders (đợt 4–5); không sửa ViewModel trừ lỗi compile
  thật sự do port (ghi rõ báo cáo); không commit.

## 3. Các bước thực hiện

1. Đọc nguồn `WorkspaceView.axaml` cũ TRỌN VẸN trước khi viết (924 dòng — chia theo tab con mà port, đừng
   dịch trộn); đọc `Theme.xaml` hiện tại (bảng quy ước đầu file) + báo cáo đợt 2 (bẫy watermark: đã fix bằng
   `TemplateBinding`, KHÔNG quay lại kiểu `{Binding (b:…)}`).
2. Port `WorkspaceView.xaml`: đúng cấu trúc tab con + lưới + panel như bản cũ; `Classes.xxx` động →
   DataTrigger; `$parent[...]` → RelativeSource FindAncestor; code-behind Tapped → `PreviewMouseLeftButtonUp`
   + helper leo `VisualTreeHelper` (đợt 2 đã có mẫu trong `DataView.xaml.cs` — tái dùng cùng idiom, nếu trùng
   logic thì cân nhắc đưa helper chung vào `Infrastructure/` thay vì copy).
3. Port `ScrapeStatsWindow.xaml` + code-behind; giữ đúng cách `WindowHost`/VM đang mở nó.
4. Build + test: `dotnet build ShopeeSuite.sln` 0 error 0 warning; 2 project test xanh.
5. Chạy thử cách ly (quy ước như đợt 2: `data-dir.txt` cạnh exe trỏ thư mục tạm — XOÁ sau; `--mode
   workspace`; KHÔNG bấm nút chạy scrape/import/update thật, không phóng Brave; chỉ đóng đúng PID mình mở).
   Màn Workspace là màn mặc định của tab Workspace. Dùng lại rig UIAutomation ở
   `C:\Users\NGXUAN~1\AppData\Local\Temp\claude\d--Projects-shopee-suite\86f7fb17-b280-49ad-87e5-94d7a1e7b273\scratchpad\`
   (`verify-dot2.ps1`, `verify-modals2.ps1` — bản modals2 đã sửa 3 lỗi rig: chọn tab trước, dò cửa sổ
   ShowDialog bằng EnumWindows + FromHandle, gate nút theo hub-client.json/hub-server.json giả). Chụp từng
   tab con của Workspace + ScrapeStatsWindow (nếu mở được từ UI với dữ liệu rỗng; bị gate thì ghi rõ),
   `SHOPEESUITE_BINDING_LOG` = 0 dòng.
6. Điền "Báo cáo thực thi" plan này (bản trong worktree).

## 4. Tiêu chí nghiệm thu

- [ ] Build 0 error 0 warning; test 1459 + 61 xanh.
- [ ] Màn Workspace hiện view thật, đủ các tab con/lưới/log theo bản cũ; ScrapeStatsWindow mở-đóng đúng.
- [ ] Binding log 0 dòng khi duyệt hết các tab con Workspace.
- [ ] Không sót `data-dir.txt`/file tạm/process con.

## 5. Rủi ro & lưu ý

- Đây là view NẶNG nhất — nếu quá nửa thời gian mà chưa xong phần port XAML, ưu tiên HOÀN CHỈNH từng tab con
  (build được, chạy được) thay vì dịch dở cả file; tab nào chưa xong để placeholder cục bộ + ghi rõ báo cáo.
- DataGrid trong Workspace có style/row template riêng ở Theme cũ — dịch theo nghĩa sang WPF (Trigger/
  RowStyle), đừng bịa part Avalonia (`BackgroundRectangle` không tồn tại ở WPF).
- Brush theo dòng từ VM đã Freeze sẵn — bind thẳng, không tạo brush trong converter mới.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<để trống>
