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

### Bổ sung sau khi agent trước bị dừng giữa chừng: sửa lỗi binding watermark (Error 40)

> Phần này CHỈ nói về hạng mục sửa bug watermark + chạy lại nghiệm thu. 5 file view/window của đợt 2 do
> lượt thực thi trước viết (agent đó bị dừng trước khi điền báo cáo) — lượt này KHÔNG soát lại nội dung port
> của chúng, chỉ dùng chúng để nghiệm thu.

**Hiện tượng**: bật `SHOPEESUITE_BINDING_LOG`, mở màn Tài khoản & Proxy + cửa sổ Import tài khoản → 3 dòng
`System.Windows.Data Error: 40 : … '(b:WatermarkAssist.Watermark)' property not found on 'object' 'TextBox'`.

**Root cause (đã kiểm chứng bằng đo thật, KHÔNG phải "modal vs cửa sổ chính")**: template TextBox trong
`Theme.xaml` bind watermark bằng `Path` dạng CHUỖI có prefix — `{Binding (b:WatermarkAssist.Watermark),
RelativeSource={RelativeSource TemplatedParent}}`. WPF giữ nguyên chuỗi đó và phân giải prefix `b:` **lúc
chạy, theo phạm vi xmlns của file XAML khai ra ô nhập** (tức view đang dùng template), chứ KHÔNG theo
`Theme.xaml` — nơi câu binding được viết. Hệ quả: view nào tình cờ có khai
`xmlns:b="clr-namespace:Shopee.Suite.Behaviors"` thì phân giải được; view nào không khai thì Error 40.
Đối chiếu thực tế khớp 100%:

| XAML | khai `xmlns:b` | ô nhập được dựng lúc đo | số lỗi (bản CHƯA sửa) |
|---|---|---|---|
| `DataView.xaml` | có | 4 | 0 |
| `BigSellerView.xaml` | có | nhiều | 0 |
| `RowEditWindow.xaml` | có | 6 | 0 |
| `AccountsView.xaml` | **không** | 1 (ô kho KiotProxy) | **1** |
| `ImportAccountsWindow.xaml` | **không** | 2 (`LoginsBox`, `KeysBox`) | **2** |

Cửa sổ modal `RowEditWindow` (có khai prefix) sạch lỗi trong khi màn `AccountsView` nằm NGAY TRONG cửa sổ
chính lại lỗi → yếu tố quyết định là phạm vi xmlns của file XAML, không phải modal hay không. MultiTrigger
`Condition Property="b:WatermarkAssist.HasText"` không bao giờ lỗi vì `Property` được phân giải lúc biên
dịch (DependencyPropertyConverter chạy trong xmlns của chính `Theme.xaml`).

**Đã sửa** (1 file): `suite/Shopee.Suite/Themes/Theme.xaml` (template TextBox mặc định, ~dòng 398–410) —
đổi sang `Text="{TemplateBinding b:WatermarkAssist.Watermark}"`. `TemplateBinding` phân giải attached
property **lúc biên dịch BAML** theo xmlns của `Theme.xaml` → không còn PropertyPath chuỗi, mọi view dùng
được mà KHÔNG cần khai `xmlns:b`. Kèm comment giải thích để đợt port sau không lặp lại lối `{Binding (b:…)}`.
Không đụng `WatermarkAssist.cs` (cơ chế `HasText` + trigger ẩn/hiện giữ nguyên) và không thêm `xmlns:b` vào
view nào (không cần nữa).

**Build / test**

| Lệnh | Kết quả |
|---|---|
| `dotnet build ShopeeSuite.sln` | Build succeeded — 0 Warning, 0 Error |
| `dotnet test suite/Shopee.Core.Tests` | Passed 61 / Failed 0 |
| `dotnet test orders/XuLyDonShopee.Tests` | Passed 1459 / Failed 0 |

**Nghiệm thu chạy thật** (bản dev, `data-dir.txt` trỏ thư mục tạm trong scratchpad, `--mode workspace`,
không bấm nút phóng Brave; bản production trên máy không bị đụng):

| Lượt | Kịch bản | Binding log |
|---|---|---|
| trước khi sửa | Tài khoản & Proxy → Import → Dữ liệu → Thêm dòng → Cấu hình BigSeller | **3 dòng** (1 + 2 như bảng trên) |
| sau khi sửa | y hệt | **0 dòng**, ExitCode 0 |
| sau khi sửa | script 3 màn chính (`verify-dot2.ps1`, data-dir chưa cấu hình Hub) | **0 dòng**, ExitCode 0 |

Ảnh chụp (scratchpad `…86f7fb17…\scratchpad\`): `modal-import-fix2.png` (Import tài khoản),
`modal-rowedit-fix2.png` (Dòng dữ liệu — watermark `B#####` hiện đúng ở ô SKU),
`modal-rowedit-typed-fix2.png` (gõ `B12345` vào ô SKU → watermark biến mất: trigger `HasText` còn nguyên),
`modal-data-fix2.png` (watermark `SKU…` / `Giá từ` / `Giá đến` vẫn hiện), `modal-accounts-fix2.png`,
`dot2-0..3-*.png`.

**Ghi chú cho lần nghiệm thu sau** (script `verify-modals2.ps1` trong scratchpad đã xử lý sẵn):
1. Ribbon: màn "Dữ liệu"/"Tài khoản & Proxy" nằm trong tab **Workspace** → phải bấm tab Workspace trước.
2. Cửa sổ modal `ShowDialog` **không** liệt kê được qua `AutomationElement.RootElement.FindAll(Children)`;
   phải dò bằng `EnumWindows` (Win32) rồi `AutomationElement.FromHandle`. Đây là lý do script cũ báo
   "KHONG mo duoc modal" dù modal đã mở thật.
3. Nút "Thêm dòng" chỉ chạy khi `DataViewModel._engine` khác null → cần `hub-client.json` có cấu hình (lượt
   đo trỏ `http://127.0.0.1:59999` là cổng chết, an toàn); nút còn bị khoá (`IsBusy`) trong lúc truy vấn đầu
   tiên → phải chờ `IsEnabled` rồi mới Invoke. Muốn thấy nút "Import…" thì thêm `hub-server.json` bật
   `Enabled` (nếu không, `IsReadOnlyMode`=true sẽ ẩn cả nút này lẫn thẻ kho KiotProxy).
