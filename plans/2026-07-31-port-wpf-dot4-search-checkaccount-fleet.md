# Plan: Port WPF — Đợt 4: SearchView + CheckAccountView/Window + FleetView (nhánh `only-windows`)

- **Ngày:** 2026-07-31
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-executor`)

> **ĐỌC TRƯỚC:** `plans/2026-07-31-port-wpf-ke-hoach-tong.md` (20 quyết định) + mục "Báo cáo thực thi" của
> plan đợt 1/2/3 (quy ước + bẫy: TemplateBinding cho attached property trong template; `<Run Text="{Binding…}">`
> PHẢI `Mode=OneWay` khi property chỉ-đọc — bẫy làm chết app ở đợt 3; TabItem giữ ContentAlignment=Stretch;
> helper `Infrastructure/VisualTreeSearch.cs` dùng chung). Việc chạy trong WORKTREE
> `d:\Projects\shopee-suite-onlywin` (nhánh `only-windows`); TUYỆT ĐỐI không đọc/ghi `d:\Projects\shopee-suite`.

## 1. Bối cảnh & mục tiêu

Đợt 1–3 xong (shell + Accounts/Data/BigSeller + Workspace/ScrapeStats). Đợt 4 port 3 màn còn lại của suite:

| Port sang | Nguồn Avalonia (`git show d6bb696:<path>`) | Ghi chú |
|---|---|---|
| `suite/Shopee.Suite/Modules/Search/SearchView.xaml` (+.cs) | `.../Search/SearchView.axaml` (363 dòng, code-behind 8 dòng) | TabControl 2 tab con + DataGrid + log |
| `suite/Shopee.Suite/Modules/CheckAccount/CheckAccountView.xaml` (+.cs) | `.../CheckAccount/CheckAccountView.axaml` (149 dòng, code-behind 21 dòng) | TabControl + DataGrid "TK OK"; code-behind lọc SelectionChanged bong bóng |
| `suite/Shopee.Suite/Modules/CheckAccount/CheckAccountWindow.xaml` (+.cs) | `.../CheckAccount/CheckAccountWindow.axaml` (10 dòng, code-behind 14 dòng) | Vỏ Window bọc CheckAccountView — thay placeholder C# `Modules/CheckAccount/CheckAccountWindow.cs` (GIỮ tên class/chữ ký nơi gọi) |
| `suite/Shopee.Suite/Modules/Fleet/FleetView.xaml` (+.cs) | `.../Fleet/FleetView.axaml` (198 dòng, code-behind 8 dòng) | TabControl + DataGrid đa máy + tab Log |

VM liên quan đã port từ đợt 1 (FleetViewModel có brush qua `AppBrushes`). Đợt này CHỈ view.

## 2. Phạm vi

- **Làm:** 4 file trên + code-behind; App.xaml: SearchViewModel/FleetViewModel (và CheckAccount VM nếu có
  DataTemplate) → view thật; xoá placeholder `CheckAccountWindow.cs`; bổ sung style Theme còn thiếu mà 3 màn
  này cần (đối chiếu Theme.axaml cũ, phần đánh dấu `<!-- đợt N -->`).
- **Không làm:** Settings + toàn bộ orders (đợt 5); không sửa VM trừ lỗi compile do port (ghi rõ); không commit.

## 3. Các bước thực hiện

1. Đọc trọn nguồn 4 file qua `git show d6bb696:`; đọc Theme.xaml hiện tại + báo cáo 3 đợt trước.
2. Port từng view theo QĐ 5/6/15/16; áp ngay các bài học: `<Run>` bind → `Mode=OneWay`; TabItem không đặt
   VerticalContentAlignment=Center; leo cây dùng `VisualTreeSearch`; watermark qua attached property có sẵn.
3. Build + test: 0 error 0 warning; 1459 + 61 xanh.
4. Chạy thử cách ly đúng quy ước các đợt trước (data-dir tạm — xoá sau; `--mode workspace`; KHÔNG bấm nút
   chạy check/search thật, không phóng Brave; đóng đúng PID). Search + Trạng thái (Fleet) nằm trong ribbon
   tab Workspace; CheckAccount mở từ màn "Tài khoản & Proxy" (nút "Check Acc" mở CheckAccountWindow — mở thử,
   chụp, đóng; lệnh bị gate thì ghi rõ). Seed dữ liệu tạm nếu giúp lưới có dòng (đợt 3 đã làm mẫu). Chụp từng
   màn/tab; `SHOPEESUITE_BINDING_LOG` = 0 dòng sau từng bước.
5. Điền "Báo cáo thực thi" plan này (bản trong worktree).

## 4. Tiêu chí nghiệm thu

- [ ] Build 0 error 0 warning; test 1459 + 61 xanh.
- [ ] 3 màn + CheckAccountWindow hiện view thật đúng bố cục bản cũ; sau đợt này suite KHÔNG còn màn
      placeholder nào ngoài Cài đặt (UnifiedSettings) và màn thuộc orders.
- [ ] Binding log 0 dòng khi duyệt hết các tab con của 3 màn.
- [ ] Không sót `data-dir.txt`/file tạm/process con.

## 5. Rủi ro & lưu ý

- CheckAccountView code-behind cũ lọc `SelectionChanged` bong bóng từ DataGrid con — WPF sự kiện cũng bubble
  qua `RoutedEventArgs.OriginalSource`: giữ đúng hành vi lọc, đừng bỏ.
- FleetView bind brush trạng thái máy từ VM (đã Freeze) — bind thẳng.
- SearchView có 2 tab con: soát kỹ phần kết quả theo máy (lưới) — đối chiếu memory luồng Search nếu cần
  nhưng KHÔNG đổi nghiệp vụ.

---

## Báo cáo thực thi (Opus điền sau khi xong)

<để trống>
