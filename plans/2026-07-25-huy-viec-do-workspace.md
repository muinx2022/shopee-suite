# Plan: Khu "việc dở" — nêu rõ số việc + cho chọn Tiếp tục / Hủy

- **Ngày:** 2026-07-25
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh & mục tiêu

Ở màn Workspace, khi có việc chạy-tay còn dở (mở lại app / bị dừng giữa chừng) hiện chỉ có MỘT nút
"⏯ Tiếp tục việc dở (N)" ở góc phải — **không có nút Hủy**, và số việc dở chưa nêu rõ ràng. Người dùng
muốn: **nêu rõ có bao nhiêu việc dở**, và cho chọn **Tiếp tục** HOẶC **Hủy**.

**Hiện trạng code (đã khảo sát):** `suite/Shopee.Suite/Modules/Workspace/WorkspaceViewModel.cs`
- `_resumePending` (List<ResumeItem>) gom từ `ScrapeProgressStore.Shared.All()` (op "scrape") +
  `OpProgressStore.Shared.Snapshot()` (op "import"/"update") các mục status ∈ {running, stopped}, đã loại
  việc Hub quản + việc đang chạy thật. `ResumeItem(string Op, WorkspaceAccountViewModel Acct, BigSellerShop Shop)`.
- `HasResumePending`, `ResumePendingCount`, `ResumeButtonText`, `ResumeTooltip` (dòng ~299-308).
- `RecomputeResumePending()` (dòng ~333) dựng lại `_resumePending` + `OnPropertyChanged` + Notify các command.
- `ResumePendingWorkCommand` (dòng ~373) chạy lại tất cả.
- 2 store có sẵn hàm xoá tiến độ + tự bắn `Changed`:
  - `ScrapeProgressStore.Shared.Clear(accountId, sheet)` (Scrape/ScrapeProgressStore.cs:216)
  - `OpProgressStore.Shared.Clear(accountId, sheet, op)` (Progress/OpProgressStore.cs:148)
  - `Clear` chỉ xoá TIẾN ĐỘ (điểm resume), KHÔNG xoá dữ liệu SP đã lưu (workbook/Postgres).
- Định vị store từ ResumeItem: accountId = `item.Acct.Account.Id`, sheet = `item.Shop.ShopeeDataSheet ?? ""`, op = `item.Op`.
- Hộp xác nhận: `Dialogs.ConfirmAsync(text, caption, DialogIcon.Warning)` (đã dùng ở BigSeller/Accounts VM).
- View `WorkspaceView.axaml`: Grid `RowDefinitions="Auto,Auto,*"`; Row 1 là `<Border Grid.Row="1" Classes="card">`
  chứa trái = [↻ Tải lại] + hướng dẫn, phải = [Tiếp tục việc dở][Dừng tất cả] (dòng ~64-83).

## 2. Phạm vi

- **Làm:** thêm lệnh **Hủy việc dở** (xoá tiến độ dở qua `Clear`, có xác nhận) + thiết kế lại khu này thành
  **banner rõ ràng** (chỉ hiện khi có việc dở): nêu số việc + 2 nút Tiếp tục / Hủy.
- **KHÔNG làm:** không đụng nút "■ Dừng tất cả" (đó là dừng việc ĐANG chạy — giữ nguyên vị trí/hành vi);
  không đổi cơ chế resume; không đổi 2 store (chỉ GỌI `Clear` sẵn có); không đụng module/màn khác.

## 3. Các bước thực hiện

### Bước 1 — ViewModel: thêm lệnh Hủy (`WorkspaceViewModel.cs`)

Thêm command (đặt ngay dưới `ResumePendingWork`, dòng ~396):

```csharp
/// <summary>Nút "Hủy": bỏ TẤT CẢ việc chạy-tay còn dở khỏi hàng chờ — xoá tiến độ dở ở 2 store để
/// RecomputeResumePending không còn nhặt (status hết running/stopped). KHÔNG xoá dữ liệu SP đã lưu.</summary>
[RelayCommand(CanExecute = nameof(HasResumePending))]
private async Task DiscardPendingWork()
{
    var n = _resumePending.Count;
    if (!await Dialogs.ConfirmAsync(
            $"Hủy {n} việc còn dở? Các việc này sẽ KHÔNG tự chạy tiếp nữa (tiến độ dở bị xoá; dữ liệu sản phẩm đã lưu vẫn còn).",
            "Hủy việc dở", DialogIcon.Warning))
        return;
    foreach (var item in _resumePending.ToList())   // ToList: Clear bắn Changed → recompute làm rỗng _resumePending
    {
        var acc = item.Acct.Account.Id;
        var sheet = item.Shop.ShopeeDataSheet ?? "";
        if (item.Op == "scrape") ScrapeProgressStore.Shared.Clear(acc, sheet);
        else                     OpProgressStore.Shared.Clear(acc, sheet, item.Op);
    }
    RecomputeResumePending();
}
```

- Trong `RecomputeResumePending()` thêm 1 dòng cạnh các Notify khác:
  `DiscardPendingWorkCommand.NotifyCanExecuteChanged();`
- Thêm `using` cho `DialogIcon` nếu thiếu (xem namespace ở `suite/Shopee.Suite/Services/IDialogService.cs`).
- Kiểm tra `Dialogs` đã dùng được trong VM này chưa; nếu chưa `using` thì thêm (`Shopee.Suite` — `Dialogs.cs`).

### Bước 2 — View: banner "việc dở" (`WorkspaceView.axaml`)

- Bọc Row 1 thành `<StackPanel Grid.Row="1">` gồm: **(a)** banner mới (trên) + **(b)** Border toolbar cũ (dưới,
  BỎ thuộc tính `Grid.Row="1"` vì nay là con StackPanel; giữ nguyên nội dung NHƯNG **bỏ nút "Tiếp tục việc dở"**
  ở nhóm phải — chỉ còn "■ Dừng tất cả").
- Banner (chỉ hiện khi `HasResumePending`), nền cam nhạt + viền accent cho ra dáng "lời nhắc":

```xml
<Border Classes="card" Padding="14,10" Margin="0,0,0,12"
        Background="#FDEEE9" BorderBrush="{DynamicResource AccentBrush}"
        IsVisible="{Binding HasResumePending}">
    <Grid>
        <StackPanel Orientation="Horizontal" VerticalAlignment="Center" Spacing="10">
            <TextBlock Text="⏸" Classes="emoji" FontSize="16" VerticalAlignment="Center" />
            <TextBlock VerticalAlignment="Center" TextWrapping="Wrap"
                       Foreground="{DynamicResource TextPrimaryBrush}">
                <Run Text="Có" />
                <Run Text="{Binding ResumePendingCount}" FontWeight="Bold" />
                <Run Text="việc đang dở dang từ lần trước." />
            </TextBlock>
        </StackPanel>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Spacing="8" VerticalAlignment="Center">
            <Button Classes="primary" Content="▶ Tiếp tục tất cả"
                    Command="{Binding ResumePendingWorkCommand}" ToolTip.Tip="{Binding ResumeTooltip}" />
            <Button Classes="danger" Content="✕ Hủy bỏ"
                    Command="{Binding DiscardPendingWorkCommand}"
                    ToolTip.Tip="Bỏ các việc dở khỏi hàng chờ (không tự chạy tiếp nữa)." />
        </StackPanel>
    </Grid>
</Border>
```

- `ResumeButtonText` (VM) sau bước này KHÔNG còn ai bind → có thể để yên (vô hại) hoặc xoá; nếu xoá thì bỏ luôn
  dòng `OnPropertyChanged(nameof(ResumeButtonText))` trong RecomputeResumePending. Tùy người thực thi, miễn build sạch.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build` toàn solution 0 error.
- [ ] `dotnet test` (XuLyDonShopee.Tests) vẫn xanh (không hồi quy).
- [ ] Khi có việc dở: hiện banner nêu đúng SỐ việc (`ResumePendingCount`) + 2 nút Tiếp tục / Hủy; khi không có
      việc dở: banner ẩn, toolbar chỉ còn [↻ Tải lại] + hướng dẫn + [■ Dừng tất cả].
- [ ] Bấm "Hủy bỏ" → hiện hộp xác nhận; đồng ý → banner biến mất (HasResumePending=false), không lỗi.
- [ ] Không đụng file ngoài `WorkspaceViewModel.cs` + `WorkspaceView.axaml`.

## 5. Rủi ro & lưu ý

- Iterate `_resumePending.ToList()` (bản chụp) vì `Clear` bắn `Changed` → recompute làm rỗng list gốc giữa vòng lặp.
- "Hủy" xoá điểm resume (không xoá dữ liệu SP). Câu xác nhận phải nói rõ điều này (đã ghi trong code mẫu).
- Banner đặt TRONG Row 1 (bọc StackPanel) để KHÔNG phải đổi RowDefinitions / dịch Grid.Row của vùng nội dung Row 2.
- `DialogIcon.Warning` — xác nhận enum có giá trị này (xem IDialogService.cs); nếu tên khác thì dùng đúng tên.

---

## Báo cáo thực thi (Opus điền sau khi xong)

Hoàn thành đúng plan, chỉ 2 file (WorkspaceViewModel.cs + WorkspaceView.axaml). Build 0 error,
899 test xanh. `ResumeButtonText` giữ lại (vô hại, không còn bind). Đã commit 1fb84ac.
