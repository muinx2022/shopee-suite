# Plan: Gộp hai nút cập nhật vào MỘT nút trên ribbon (Kiểm tra cập nhật ⇄ Cập nhật)

- **Ngày:** 2026-08-08
- **Trạng thái:** hoàn thành
- **Người lập:** Opus 5 (phiên chính) · **Người thực thi:** Opus (`opus-executor`)

## 1. Bối cảnh & mục tiêu

### Yêu cầu người dùng (08/08/2026, nguyên văn)

> "cái nút ở ribbon sẽ là nút Kiểm tra cập nhật, khi click vào đó thì ra tab Phiên bản và cập nhật, thực hiện
> việc kiểm tra luôn. Nếu có version mới thì dùng ribbon đó để cập nhật luôn (đổi text thành Cập nhật), bỏ 2
> cái nút ở dưới"

Kèm ảnh chụp màn Cài đặt (v1.8.2): khung XANH = nút ribbon "Cập nhật & khởi động lại"; khung ĐỎ = hai nút
"Kiểm tra bản mới" + "Cập nhật & khởi động lại" nằm trong tab "Phiên bản & cập nhật".

### Diễn giải chốt

Một nút DUY NHẤT trên ribbon (nhóm "Hành động" của tab Cài đặt), đổi nhãn theo trạng thái:

| Trạng thái | Nhãn nút | Bấm vào thì |
|---|---|---|
| Chưa có bản mới (mặc định) | **Kiểm tra cập nhật** | Chuyển sang tab "Phiên bản & cập nhật" **rồi kiểm tra ngay** |
| Đã tải xong bản mới (`UpdateReady`) | **Cập nhật** | Áp dụng bản đã tải + khởi động lại |

Hai nút trong tab bị **bỏ hẳn**. Tab vẫn giữ dòng phiên bản, dòng trạng thái, và dòng nhắc "không hỗ trợ tự
cập nhật".

### Hiện trạng mã nguồn (đã khảo sát — đường dẫn + số dòng thật)

**`suite/Shopee.Suite/ViewModels/RibbonModels.cs:105-136` — `RibbonActionItem`**

```csharp
public sealed class RibbonActionItem
{
    public RibbonActionItem(string title, string iconKey, ICommand command, string? toolTip = null)
    public string Title { get; }          // ← GET-ONLY, gán 1 lần ở ctor
    public Geometry? Icon { get; }        // tra từ Application.Resources NGAY lúc dựng (LookupIcon)
    public ICommand Command { get; }
    public string? ToolTip { get; }       // ← GET-ONLY
}
```

Lớp này **KHÔNG có INotifyPropertyChanged** ⇒ hiện KHÔNG đổi nhãn lúc chạy được. Đây là thay đổi hạ tầng bắt
buộc của việc này.

**`suite/Shopee.Suite/MainWindow.xaml:116-125` — DataTemplate của action item**

```xml
<DataTemplate DataType="{x:Type vm:RibbonActionItem}">
    <Button Style="{StaticResource ribbon}" Margin="2,0"
            Command="{Binding Command}" ToolTip="{Binding ToolTip}">
        ... <TextBlock Style="{StaticResource ribbonLabel}" Text="{Binding Title}" />
</DataTemplate>
```

`Text="{Binding Title}"` + `ToolTip="{Binding ToolTip}"` là binding thường ⇒ **chỉ cần lớp phát INPC là nhãn
tự đổi**, KHÔNG phải sửa XAML này.

**`suite/Shopee.Suite/ViewModels/ShellViewModel.cs:279-287` — dựng ribbon tab Cài đặt**

```csharp
var settingsTab = new RibbonTab("Cài đặt", new List<RibbonGroup>
{
    new RibbonGroup("Màn hình", new object[] { setScreen }),
    new RibbonGroup("Hành động", new object[]
    {
        new RibbonActionItem("Cập nhật & khởi động lại", "IconUpgrade", settings.ApplyUpdateCommand,
            "Áp dụng bản đã tải + mở lại app (chỉ khả dụng khi đã tải xong bản mới)"),
    }),
});
```

Có sẵn **tiền lệ nghe PropertyChanged để cập nhật ribbon** ở ngay file này, dòng 262-273
(`SyncOrdersActionGroups`) — bắt chước đúng mẫu đó.

**`suite/Shopee.Suite/Modules/Settings/SettingsViewModel.cs:96-133`**

- `AppVersionText` (99), `UpdateStatus` (102), `UpdateSupported` (105-107), `UpdateNotSupported` (110),
  `UpdateReady` (113-115), `OnUpdateChanged()` (117-122) — bơm từ `UpdateService.Shared.Changed`.
- `CheckUpdateCommand` (125-126) → `UpdateService.Shared.CheckAsync()`
- `ApplyUpdateCommand` (132-133, `CanExecute = nameof(UpdateReady)`) → `UpdateService.Shared.ApplyAfterPrepareAsync()`

**`suite/Shopee.Suite/Modules/Settings/UnifiedSettingsViewModel.cs:54`** — `public SettingsViewModel Suite { get; }`
⇒ trong XAML mọi binding đi qua tiền tố `Suite.`.

**`suite/Shopee.Suite/Modules/Settings/UnifiedSettingsView.xaml`**

- Dòng 50: `<TabControl Grid.Row="1" Style="{StaticResource subtabs}">` — **KHÔNG có binding SelectedIndex/SelectedItem**.
- Dòng 461: `<TabItem Header="Phiên bản &amp; cập nhật">` (tab thứ 4).
- Dòng 472-491: khối `<StackPanel Orientation="Horizontal" Margin="0,12,0,0">` chứa ĐÚNG hai nút cần bỏ.
- Dòng 493-495: dòng nhắc `UpdateNotSupported` — **GIỮ**.

⚠ **Cạm bẫy đã ghi sẵn trong file, dòng 126-129:** tab 2 (`Đơn hàng`) và tab 3 (`Hiệu năng & Đồng bộ`) bị **ẩn
theo chế độ ứng dụng** (`ShowsWorkspaceSettings`). Vì vậy **CHỈ SỐ của tab "Phiên bản & cập nhật" KHÔNG cố
định** — dùng `SelectedIndex` là sai ở chế độ Shopee. Phải chọn tab bằng `IsSelected` trên chính TabItem đó.

### Tra cứu: sau thay đổi, hai lệnh cũ KHÔNG còn ai dùng

`grep` toàn repo cho `ApplyUpdateCommand` / `CheckUpdateCommand` ra đúng 4 chỗ, và cả 4 đều bị việc này thay:
`SettingsViewModel.cs:114` (thuộc tính `NotifyCanExecuteChangedFor`), `UnifiedSettingsView.xaml:476` và `:484`
(hai nút bị bỏ), `ShellViewModel.cs:284` (nút ribbon). ⇒ Gộp về MỘT lệnh, không để lại lệnh chết.

## 2. Phạm vi

**Làm:**

- **A.** `RibbonActionItem` đổi nhãn/tooltip được lúc chạy (thêm INPC).
- **B.** `SettingsViewModel`: một lệnh `KiemTraHoacCapNhat` + cờ chọn tab + nhãn nút; bỏ hai lệnh cũ.
- **C.** `UnifiedSettingsView.xaml`: bỏ hai nút; gắn `IsSelected` cho TabItem "Phiên bản & cập nhật".
- **D.** `ShellViewModel`: nút ribbon dùng lệnh mới + tự đổi nhãn theo `UpdateReady`.
- **E.** Test cho phần thuần (nhãn nút theo trạng thái).
- **F.** Bump `version.txt` + `CHANGELOG.md`.

**Không làm:**

- KHÔNG đụng `UpdateService.cs` — luật kiểm tra/tải/áp dụng giữ NGUYÊN.
- KHÔNG đụng đường update do Hub giao (`RemoteUpdateService`, `HttpCoordinationHub.UpdateRequested`).
- KHÔNG đổi DataTemplate ribbon ở `MainWindow.xaml` (binding sẵn đã đủ).
- KHÔNG đổi các `RibbonActionItem` khác (Dừng jobs, Chọn tất cả, Chạy đã chọn…).
- KHÔNG đụng `orders/`, `server/`, `extensions/`.
- KHÔNG phát hành (người dùng tự quyết).

## 3. Các bước thực hiện

### Bước A — `suite/Shopee.Suite/ViewModels/RibbonModels.cs`: cho `RibbonActionItem` đổi nhãn được

- Đổi `public sealed class RibbonActionItem` → `public sealed partial class RibbonActionItem : ObservableObject`
  (`partial` là bắt buộc để `[ObservableProperty]` sinh mã).
- `Title` và `ToolTip` → `[ObservableProperty] private string _title;` / `private string? _toolTip;`
  Ctor gán qua field sinh ra (`_title = title; _toolTip = toolTip;`).
- `Icon` và `Command` **GIỮ get-only** — không có nhu cầu đổi, đổi thêm là mở rộng phạm vi vô ích.
- **GIỮ NGUYÊN chữ ký ctor** `(string title, string iconKey, ICommand command, string? toolTip = null)` để 6 chỗ
  gọi còn lại không phải sửa.
- Thêm `using CommunityToolkit.Mvvm.ComponentModel;` nếu chưa có.
- Ghi xmldoc ngắn nói RÕ vì sao Title phải quan sát được: nút cập nhật trên ribbon đổi nhãn
  "Kiểm tra cập nhật" ⇄ "Cập nhật" theo trạng thái.

⚠ Kiểm: `RibbonScreenItem` / `RibbonToggleItem` trong cùng file có thể đã kế thừa `ObservableObject` — đọc
trước, theo đúng lối đang có của file, đừng trộn hai kiểu INPC khác nhau.

### Bước B — `suite/Shopee.Suite/Modules/Settings/SettingsViewModel.cs`

**B1. Cờ chọn tab** (đặt cạnh nhóm phiên bản/cập nhật):

```csharp
/// <summary>Bật lên để View CHỌN tab "Phiên bản & cập nhật". Bind HAI CHIỀU vào TabItem.IsSelected — KHÔNG
/// dùng SelectedIndex vì tab "Đơn hàng"/"Hiệu năng & Đồng bộ" bị ẩn theo chế độ ⇒ chỉ số tab không cố định
/// (xem chú thích ở UnifiedSettingsView.xaml dòng 126-129).</summary>
[ObservableProperty] private bool _chonTabPhienBan;
```

**B2. Nhãn nút ribbon** — hàm THUẦN + property:

```csharp
/// <summary>HÀM THUẦN (test được): nhãn nút cập nhật trên ribbon theo trạng thái.</summary>
internal static string NhanNutCapNhat(bool updateReady) => updateReady ? "Cập nhật" : "Kiểm tra cập nhật";

/// <summary>HÀM THUẦN (test được): tooltip nút cập nhật trên ribbon theo trạng thái.</summary>
internal static string TipNutCapNhat(bool updateReady) => updateReady
    ? "Đã tải xong bản mới — áp dụng + khởi động lại app ngay"
    : "Mở tab \"Phiên bản & cập nhật\" và kiểm tra bản mới ngay";
```

**B3. Gộp hai lệnh thành một:**

- **BỎ** `[RelayCommand] private async Task CheckUpdate()` (125-126) và
  `[RelayCommand(CanExecute = nameof(UpdateReady))] private async Task ApplyUpdate()` (132-133).
- **BỎ** dòng 114 `[NotifyCanExecuteChangedFor(nameof(ApplyUpdateCommand))]` trên `UpdateReady`
  (lệnh đó không còn) — **KHÔNG thay bằng gì cả**: nhãn nút do `ShellViewModel` nghe `PropertyChanged` của
  `UpdateReady` mà đẩy sang `RibbonActionItem`, không đi qua property của VM này.
- **THÊM**:

```csharp
/// <summary>
/// Nút cập nhật DUY NHẤT trên ribbon (người dùng chốt 08/08/2026 — bỏ hai nút trong tab).
/// LUÔN mở tab "Phiên bản & cập nhật" trước để người dùng thấy dòng trạng thái, rồi:
/// đã tải xong bản mới → áp dụng + khởi động lại; chưa → kiểm tra (và tải nền) ngay.
/// <para>Hai nhánh đi ĐÚNG hai hàm cũ của <see cref="UpdateService"/>, không đổi luật cập nhật.</para>
/// </summary>
[RelayCommand]
private async Task KiemTraHoacCapNhat()
{
    ChonTabPhienBan = true;
    if (UpdateReady)
    {
        await UpdateService.Shared.ApplyAfterPrepareAsync();
        return;
    }
    await UpdateService.Shared.CheckAsync();
}
```

⚠ **KHÔNG đặt `CanExecute`.** Máy chạy bản dev (`UpdateSupported == false`) bấm vào vẫn phải MỞ ĐƯỢC tab để
đọc dòng nhắc "chỉ tự cập nhật khi cài qua Velopack". Khoá nút ở đó là người dùng bấm mãi không hiểu vì sao.

### Bước C — `suite/Shopee.Suite/Modules/Settings/UnifiedSettingsView.xaml`

**C1.** Dòng 461, gắn cờ chọn tab:

```xml
<TabItem Header="Phiên bản &amp; cập nhật"
         IsSelected="{Binding Suite.ChonTabPhienBan, Mode=TwoWay}">
```

**C2.** XOÁ trọn khối hai nút (dòng 472-491) — từ `<StackPanel Orientation="Horizontal" Margin="0,12,0,0">`
đến `</StackPanel>` đóng của nó. **GIỮ**: `AppVersionText` (466-467), `UpdateStatus` (469-471), và dòng nhắc
`UpdateNotSupported` (493-495).

**C3.** Sau khi bỏ nút, `UpdateSupported` không còn chỗ dùng trong XAML (chỉ `UpdateNotSupported` còn). Đó là
bình thường — **GIỮ property `UpdateSupported`** vì `UpdateNotSupported` tính từ nó.

**C4.** Thêm một câu hướng dẫn ngay dưới dòng trạng thái để tab không bị trống trơn khó hiểu:

```xml
<TextBlock Style="{StaticResource caption}" Margin="0,10,0,0"
           Text="Dùng nút &quot;Kiểm tra cập nhật&quot; trên thanh ribbon phía trên. Có bản mới thì nút đổi thành &quot;Cập nhật&quot;." />
```

### Bước D — `suite/Shopee.Suite/ViewModels/ShellViewModel.cs`

Thay khối dòng 279-287. Bắt chước ĐÚNG mẫu `SyncOrdersActionGroups` (262-273):

```csharp
// Nút cập nhật DUY NHẤT: nhãn đổi theo trạng thái (Kiểm tra cập nhật ⇄ Cập nhật) — người dùng chốt
// 08/08/2026, hai nút trong tab đã bỏ. RibbonActionItem nay phát INPC nên chỉ cần gán lại Title/ToolTip.
var nutCapNhat = new RibbonActionItem(
    SettingsViewModel.NhanNutCapNhat(settings.UpdateReady), "IconUpgrade",
    settings.KiemTraHoacCapNhatCommand,
    SettingsViewModel.TipNutCapNhat(settings.UpdateReady));

void DongBoNutCapNhat()
{
    nutCapNhat.Title = SettingsViewModel.NhanNutCapNhat(settings.UpdateReady);
    nutCapNhat.ToolTip = SettingsViewModel.TipNutCapNhat(settings.UpdateReady);
}
settings.PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(SettingsViewModel.UpdateReady)) DongBoNutCapNhat();
};

var settingsTab = new RibbonTab("Cài đặt", new List<RibbonGroup>
{
    new RibbonGroup("Màn hình", new object[] { setScreen }),
    new RibbonGroup("Hành động", new object[] { nutCapNhat }),
});
```

⚠ `settings` là VM **singleton** sống suốt vòng đời app (xem chú thích `SettingsViewModel.cs:93` — "VM là
singleton (tạo 1 lần) → không rò event"), và `ShellViewModel` cũng vậy ⇒ đăng ký `PropertyChanged` ở đây
KHÔNG rò. Ghi câu đó vào comment để người sau khỏi lo.

⚠ `OnUpdateChanged` của `SettingsViewModel` chạy qua `UiThread.Post` (dòng 117) ⇒ `PropertyChanged` bắn trên
UI thread ⇒ gán `Title` an toàn cho binding.

### Bước E — Test

`suite/Shopee.Core.Tests/` hoặc project test của suite (kiểm xem `SettingsViewModel` có nằm trong assembly
được test không — nếu KHÔNG có project test cho `Shopee.Suite` thì **báo lại, đừng tự tạo project mới**).

Nếu test được, thêm cho hai hàm thuần:

- `NhanNutCapNhat(false) == "Kiểm tra cập nhật"`, `NhanNutCapNhat(true) == "Cập nhật"`.
- `TipNutCapNhat` khác nhau ở hai trạng thái và đều không rỗng.

⚠ Test viết xong phải **thử phá** (đảo điều kiện `updateReady ? ... : ...`) rồi chạy lại xem có đổ không.

### Bước F — Phát hành

- `version.txt`: `1.8.4` → `1.8.5`.
- `CHANGELOG.md`: mục v1.8.5, tiếng Việt, theo khuôn các mục có sẵn.
- **KHÔNG chạy `release-suite.cmd`.**

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln -t:Rebuild` — **0 warning, 0 error**.
- [ ] `dotnet test` các project test có sẵn — toàn bộ xanh; ghi rõ số test cũ → mới.
- [ ] `grep -rn "ApplyUpdateCommand\|CheckUpdateCommand" --include=*.cs --include=*.xaml suite/ orders/`
      (bỏ `bin`/`obj`) → **không còn kết quả nào**. Không để lệnh chết.
- [ ] `grep -n "Kiểm tra bản mới" suite/Shopee.Suite/Modules/Settings/UnifiedSettingsView.xaml` → **rỗng**.
- [ ] TabItem "Phiên bản & cập nhật" có `IsSelected="{Binding Suite.ChonTabPhienBan, Mode=TwoWay}"`;
      **KHÔNG có** `SelectedIndex` nào được thêm vào TabControl (chỉ số tab không cố định — xem mục 5).
- [ ] `RibbonActionItem` là `partial` và kế thừa `ObservableObject`; ctor giữ NGUYÊN chữ ký; 6 chỗ gọi còn lại
      không phải sửa dòng nào — chứng minh bằng `git diff` (chỉ `ShellViewModel.cs:284` đổi).
- [ ] `KiemTraHoacCapNhatCommand` **không có** `CanExecute` (bản dev vẫn bấm được để đọc dòng nhắc).
- [ ] `git diff` không đụng file ngoài danh sách mục 2.

## 5. Rủi ro & lưu ý

1. **`SelectedIndex` là bẫy.** Tab "Đơn hàng" và "Hiệu năng & Đồng bộ" ẩn theo chế độ ứng dụng ⇒ chỉ số tab
   "Phiên bản & cập nhật" đổi theo chế độ. Bắt buộc dùng `IsSelected` trên chính TabItem.
2. **`IsSelected` TwoWay và chuyện bấm lần thứ hai.** Người dùng bấm nút → `ChonTabPhienBan = true` → tab được
   chọn. Người dùng bấm sang tab khác → TwoWay ghi ngược `false`. Bấm nút lần nữa → `true` → chọn lại: chạy
   đúng. NHƯNG nếu vì lý do nào đó binding KHÔNG ghi ngược được `false`, lần bấm thứ hai sẽ **không** kéo được
   tab về (gán `true` khi đang `true` không phát PropertyChanged). Người thực thi **phải tự kiểm ca này** và
   nếu thấy không chắc thì đặt `ChonTabPhienBan = false;` ngay trước khi gán `true` trong lệnh — rẻ và kín.
3. **`RibbonActionItem` thành `ObservableObject`** đụng lớp DÙNG CHUNG cho 7 nút ribbon. Rủi ro thấp (chỉ thêm
   INPC) nhưng phải build lại cả solution và mắt thường xác nhận các nút khác còn nguyên nhãn.
4. **Nhánh `UpdateReady` đóng app.** `ApplyAfterPrepareAsync` dừng job rồi khởi động lại — gán `ChonTabPhienBan`
   trước đó là vô hại (app sắp đóng), giữ vì một đường mã dễ đọc hơn hai.
5. **Không được nuốt lỗi im lặng.** `CheckAsync`/`ApplyAfterPrepareAsync` tự bơm câu trạng thái qua
   `UpdateService.Changed` → `UpdateStatus`. Đừng bọc `try/catch` rỗng quanh chúng.
6. **Đừng đổi luật cập nhật.** Việc này thuần UI: gộp hai nút thành một và chọn tab. Mọi thứ bên trong
   `UpdateService` giữ nguyên.

---

## Báo cáo thực thi

**Xong 08/08/2026.** `opus-executor` triển khai → phiên chính đối chiếu diff, sửa một regression, tự kiểm chứng.

### Kiểm chứng thật (phiên chính tự chạy)

| Lệnh | Kết quả |
|---|---|
| `dotnet build ShopeeSuite.sln -t:Rebuild` | 0 Warning, 0 Error |
| `dotnet test ShopeeSuite.sln` | `Shopee.Core.Tests` 111/111 · `XuLyDonShopee.Tests` 1658/1658 |
| grep `ApplyUpdateCommand\|CheckUpdateCommand` | **rỗng** — không còn lệnh chết |
| grep `"Kiểm tra bản mới"` trong XAML | **rỗng** |
| grep `SelectedIndex` trong XAML | chỉ 2 dòng COMMENT, không có thuộc tính nào |

### Regression phiên chính phát hiện và ĐÃ sửa

`ChonTabPhienBan` sống trên `SettingsViewModel` **singleton**, còn `UnifiedSettingsView` bị **dựng lại** mỗi lần
quay về màn Cài đặt (shell bind `ContentControl` vào ViewModel + DataTemplate, `RibbonScreenItem.ScreenVm` giữ
VM chứ không giữ view). Hệ quả: sau MỘT lần bấm "Kiểm tra cập nhật", **mọi lần mở Cài đặt sau đó đều nhảy
thẳng vào tab "Phiên bản & cập nhật"** thay vì tab đầu — người dùng không hề yêu cầu.

Sửa: hạ cờ ở `Unloaded` trong `UnifiedSettingsView.xaml.cs`. Cố ý KHÔNG hạ trong lệnh: lúc còn ở trên màn mà
gán `false` thì binding TwoWay đẩy ngược `IsSelected = false` ⇒ TabControl rơi về `SelectedIndex = -1`.

### Điểm executor làm khác plan (đều đúng, đã nhận)

1. **Không thêm `ChonTabPhienBan = false` trước khi gán `true`** (mục Rủi ro #2 của plan gợi ý "không chắc thì
   thêm"). Executor dựng một app WPF thật để ĐO: WPF **có** ghi ngược `false` về source khi người dùng bấm
   sang tab khác ⇒ lần bấm thứ hai vẫn kéo được tab về; còn thêm bước reset thì gây `SelectedIndex = -1` nhấp
   nháy khi tab đang được chọn. Điều kiện "không chắc" không còn đúng ⇒ bỏ gợi ý đó.
2. **Plan đếm sai số chỗ gọi `RibbonActionItem`**: 7 chứ không phải 6 (sót `"Dừng tất cả"`,
   `ShellViewModel.cs:242`). Không ảnh hưởng kết quả — không chỗ nào phải sửa, chữ ký ctor giữ nguyên.
3. **Xmldoc `ChonTabPhienBan` trỏ "khối tab Đơn hàng"** thay vì số dòng, vì chính plan này đẩy số dòng trôi đi.

### Bước E (test) — KHÔNG làm được, không phải bỏ sót

Solution chỉ có 2 project test: `suite/Shopee.Core.Tests` (`net8.0`, chỉ ref `Shopee.Core`) và
`orders/XuLyDonShopee.Tests` (`net8.0-windows`, ref `XuLyDonShopee.*`). **Không project nào ref `Shopee.Suite`**,
và `Shopee.Suite` là `WinExe`/WPF nên `Shopee.Core.Tests` (`net8.0`) không ref được nếu không đổi TFM — nằm
ngoài phạm vi. Executor dừng đúng, không tự tạo project mới.

Bù lại: đo trên `ShopeeSuite.dll` đã build bằng probe phản chiếu (ngoài repo) — 12 mục đạt, và **thử phá**
(đảo ternary trong `NhanNutCapNhat`) → probe đổ đúng 2 mục, hoàn tác → đạt lại.

**Nợ:** hai hàm thuần `NhanNutCapNhat` / `TipNutCapNhat` chưa có test tự động. Muốn có thì phải mở plan riêng
tạo `Shopee.Suite.Tests`.

### Chuyện "test chập chờn" — chốt lại nguyên nhân

Executor gặp 1/8 lượt `XuLyDonShopee.Tests` đỏ, giống lượt đỏ hôm trước. Phiên chính truy tiếp:

- 10 lượt liên tiếp có `--logger trx`, chạy sạch → **0 lỗi**, không bắt được gì.
- Ép chạy 6 lượt test SONG SONG với vòng `dotnet build -t:Rebuild` → đỏ ngay, nhưng đỏ **457 test** với
  `System.IO.FileNotFoundException: Could not load file or assembly 'Microsoft.Data.Sqlite'`.

⇒ **Nguyên nhân là chạy build song song với test trên CÙNG thư mục output**: `-t:Rebuild` xoá/thay file trong
`bin/Debug/net8.0-windows` trong lúc test đang nạp assembly. Cả hai lượt đỏ trước đó đều xảy ra đúng lúc có
build/agent khác đang chạy. **KHÔNG phải lỗi của bộ test, cũng không phải race cổng loopback** như phiên chính
đoán ở plan trước — bản vá `BridgeTestRig` hôm đó vẫn đúng và đáng giữ (race có thật, đọc ra được từ mã), chỉ
là nó không phải thủ phạm của lượt đỏ kia.

**Bài học vận hành: đừng chạy `dotnet build` song song với `dotnet test` trên cùng project.**
