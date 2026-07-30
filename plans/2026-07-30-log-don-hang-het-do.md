# Plan: Nhật ký module Đơn hàng hết đơ khi chạy nhiều tài khoản

- **Ngày:** 2026-07-30
- **Trạng thái:** hoàn thành
- **Người lập:** Fable · **Người thực thi:** opus-dev (trong worktree)

## 1. Bối cảnh & mục tiêu

User báo: tab **Shopee → Tài khoản**, khi chạy nhiều tài khoản cùng lúc thì panel **Nhật ký** làm app
"đơ đơ". Yêu cầu của user: *"cắt bớt log đi, chỉ hiển thị log mới thôi"*.

### Nguyên nhân đã xác định (khảo sát code, không đoán)

| # | Nguyên nhân | Vị trí |
|---|---|---|
| A | **Ghi file đồng bộ từng dòng** — `File.AppendAllText` mở/ghi/đóng file cho mỗi dòng, dưới `lock (_fileLock)` chung. Nhiều điểm gọi `Append` nằm trên **luồng UI** → UI đứng chờ I/O, hoặc chờ khóa mà luồng nền đang giữ | `App/Services/ActivityLog.cs:69-79` |
| B | **Dựng lại toàn bộ chuỗi mỗi dòng** — `LogText => string.Join("\n", 500 phần tử)` rồi gán `TextBox.Text` mới → Avalonia đo lại layout cả khối text có `TextWrapping="Wrap"` | `App/ViewModels/AccountsViewModel.cs:61, 119` |
| C | **Đầy hạn mức thì nhân đôi việc** — mỗi `Append` bắn `Add` rồi `Remove` ⇒ 2 lần dựng chuỗi + 2 lần gán `Text` cho MỘT dòng log. `FilteredLogEntries.Remove` còn quét O(n) | `ActivityLog.cs:84-88`, `AccountsViewModel.cs:879-892` |
| D | **Không gom nhóm** — mỗi dòng 1 lượt `Dispatcher.UIThread.Post` | `ActivityLog.cs:47, 82` |
| E | **Tự cuộn thêm 1 lượt dispatcher nữa mỗi dòng**, đọc `box.Text.Length` (chuỗi 40-60KB) | `App/Views/AccountsView.axaml.cs:105-128` |
| F | **Hạn mức 500 dùng CHUNG toàn app** (không phải riêng từng tài khoản) → chạy 5 tk thì mỗi tk còn ~100 dòng; tk chạy ồn đẩy văng log của tk đang xem | `ActivityLog.cs:44` |
| G | **File log không xoay vòng**, phình vô hạn → mở/đóng file lớn liên tục càng nặng | `ActivityLog.cs:55, 73` |

### Đối chiếu: bên suite ĐÃ giải đúng bài này

`suite/Shopee.Suite/Infrastructure/LogBuffer.cs` — hạn mức 500, **ghi file qua `ConcurrentQueue` +
`Timer` flush 1 giây/lần ở luồng nền**, **xoay file khi > 8MB**. Kèm
`suite/Shopee.Suite/Infrastructure/AccountLogRegistry.cs` — buffer **riêng từng tài khoản**.

Module Đơn hàng **không dùng** (grep `LogBuffer|AccountLogRegistry` trong `orders/` = 0 kết quả), tự
viết `ActivityLog` sao chép mỗi ý tưởng hạn mức 500 mà bỏ phần quan trọng nhất.

**KHÔNG tái sử dụng trực tiếp `LogBuffer` của suite lần này**: nó nằm trong project `Shopee.Suite`, mà
`Shopee.Suite` → tham chiếu → `XuLyDonShopee.App` (một chiều). Muốn dùng chung phải chuyển lớp xuống
project chia sẻ — đúng lúc phiên khác đang sửa dở đúng vùng tham chiếu đó. Lần này **áp dụng cùng kỹ
thuật ngay trong `ActivityLog`**, để dành việc hợp nhất cho đợt dọn trùng lặp sau.

### Mục tiêu

1. Chạy nhiều tài khoản, log dồn dập → **UI không giật**.
2. Panel chỉ hiển thị **log mới** (đúng yêu cầu user) — nhưng **file log trên đĩa vẫn đủ**, không mất
   lịch sử.
3. Không dòng log nào bị mất vì tài khoản khác chạy ồn.
4. **Không đổi chữ ký `Append(source, message)`** — mọi nơi gọi log giữ nguyên, không sửa lan man.

## 2. Phạm vi

### Làm — chỉ 3 file

- `orders/XuLyDonShopee.App/Services/ActivityLog.cs` — viết lại phần ruột.
- `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs` — phần log (không đụng phần khác).
- `orders/XuLyDonShopee.App/Views/AccountsView.axaml.cs` — phần tự cuộn.

### Không làm

- **KHÔNG sửa** `AppServices.cs`, `AccountSession.cs`, `OrdersViewModel.cs`, `OrderStatisticsViewModel.cs`,
  `OrdersRepository.cs`, `OrdersViewModelTests.cs`, `server/**`, `suite/Shopee.Core/Coordination/**`,
  `suite/Shopee.Suite/Infrastructure/OrdersModuleHost.cs` — **phiên khác đang sửa dở đúng các file này**.
  Giữ nguyên chữ ký public của `ActivityLog` chính là để không phải đụng vào chúng.
- Không chuyển `LogBuffer` xuống project chung (để đợt refactor sau).
- Không đổi giao diện panel Nhật ký (vẫn `TextBox` + nút Copy/Xóa).
- Không bump version, không commit.

## 3. Các bước thực hiện

### Bước 1 — `ActivityLog`: buffer riêng từng nguồn, có hạn mức

Thay `ObservableCollection<LogEntry> Entries` (một rổ chung, cap 500 toàn cục) bằng **buffer riêng theo
`source`**:

- `Dictionary<string, Queue<LogEntry>>` (hoặc ring buffer) — key là `source`, **so sánh không phân biệt
  hoa thường** (`StringComparer.OrdinalIgnoreCase`) vì nguồn là email.
- Hạn mức **200 dòng cho MỖI nguồn** (hằng số đặt tên rõ, vd `MaxLinesPerSource = 200`). Đây là phần
  "chỉ hiển thị log mới" user yêu cầu — 200 dòng gần nhất của tài khoản đang xem.
- Mọi truy cập bọc `lock` riêng (đừng dùng chung khóa với khóa ghi file).
- Cấp API cho VM lấy log của một nguồn: `IReadOnlyList<LogEntry> Snapshot(string source)` — trả **bản
  sao** dưới lock (không trả tham chiếu ra ngoài, tránh đọc trong lúc luồng nền đang ghi).
- Giữ nguyên `Append(string source, string message)` và `CurrentLogPath`. `Clear()` cũng giữ (VM đang
  dùng cho nút Xóa) nhưng đổi ngữ nghĩa: xóa buffer của nguồn đang chọn — nếu chữ ký hiện tại không có
  tham số thì thêm **overload** mới, đừng phá cái cũ.

### Bước 2 — `ActivityLog`: ghi file gom nhóm, chạy nền

Bê đúng cách của `LogBuffer.cs:24, 38, 56-84` (đọc file đó trước rồi làm theo):

- `ConcurrentQueue<string>` chứa dòng chờ ghi; `Append` chỉ **enqueue** rồi trả về ngay — **không I/O
  trên luồng gọi** (đây là fix quan trọng nhất, giết nguyên nhân A).
- `Timer` nền flush **1 giây/lần**: rút hết hàng đợi, ghi **một lần** bằng `File.AppendAllLines`.
- **Xoay file khi > 8MB** (`RollIfNeeded` như `LogBuffer.cs:73-84`).
- Nuốt lỗi I/O như hiện tại (không được để log làm sập app).
- `Dispose`/tắt app: flush nốt hàng đợi (tìm xem `AppServices` có chỗ shutdown không; **nếu không có
  thì thôi**, đừng sửa `AppServices.cs` — ghi vào báo cáo là hàng đợi ≤1 giây có thể mất khi tắt app).

### Bước 3 — `ActivityLog`: gom nhóm đẩy lên UI

- Bỏ `Dispatcher.UIThread.Post` mỗi dòng.
- Thay bằng **một sự kiện gộp**: `event Action<string>? SourceUpdated` (tham số là `source` vừa có log
  mới), bắn qua `Dispatcher.UIThread.Post` **tối đa 1 lần mỗi ~250ms cho mỗi nguồn** (cờ "đã hẹn bắn"
  + timer; dòng log tới trong lúc chờ chỉ gộp vào, không hẹn thêm).
- 250ms: mắt người vẫn thấy log chạy realtime, mà số lần dựng lại chuỗi giảm hàng chục lần khi log dội.

### Bước 4 — `AccountsViewModel`: bỏ `FilteredLogEntries`, dựng chuỗi 1 lần mỗi nhịp

- Bỏ hẳn `FilteredLogEntries` (`AccountsViewModel.cs:114`) và `OnLogEntriesChanged`
  (`AccountsViewModel.cs:861-896`) — cùng nguyên nhân B và C.
- Đăng ký `ActivityLog.SourceUpdated`; khi nguồn báo về **trùng** `SelectedRow.Email` thì:
  `LogText = string.Join("\n", Log.Snapshot(email).Select(e => e.Display))` — gán vào **field backing**
  rồi `OnPropertyChanged`, **không** để `LogText` là property tính toán như hiện nay.
- Đổi `SelectedRow` → dựng lại `LogText` một lần từ `Snapshot` (thay `RebuildFilteredLog`).
- **Nhớ gỡ đăng ký sự kiện** khi VM bị hủy, kẻo rò bộ nhớ (`ActivityLog` sống suốt vòng đời app).
- Nút Copy (`AccountsView.axaml.cs:81-103`) đang tự join lại từ `FilteredLogEntries` → đổi sang dùng
  thẳng `LogText`.
- Sửa luôn lỗi vặt: `LogPath` (`AccountsViewModel.cs:122`) không bao giờ báo đổi ⇒ qua nửa đêm UI vẫn
  hiện file hôm qua. Cho `LogPath` báo đổi mỗi lần dựng lại `LogText` là đủ (rẻ).

### Bước 5 — `AccountsView.axaml.cs`: tự cuộn 1 lần mỗi nhịp

- Bỏ đăng ký `FilteredLogEntries.CollectionChanged` (`:105-128`) — collection đó không còn nữa.
- Thay bằng: nghe `PropertyChanged` của VM, khi `LogText` đổi thì `Post` **một** lần đặt
  `CaretIndex = Text.Length`. Vì `LogText` giờ chỉ đổi mỗi ~250ms nên tự cuộn cũng chỉ chạy ngần ấy.
- Xóa comment lỗi thời `AccountsView.axaml.cs:79-80` ("log nằm trong ListBox" — giờ là `TextBox`).

## 4. Kiểm chứng

### Môi trường — BẮT BUỘC làm trong worktree

Cây làm việc chính **đang hỏng build** vì phiên khác sửa dở (`orders/XuLyDonShopee.App` thiếu tham
chiếu `Shopee.Core`/`SharedOrderStatistics`). Vì vậy: làm **trong worktree tách từ HEAD**, tuyệt đối
không đọc/ghi file của cây chính.

### Build

```text
dotnet build orders/XuLyDonShopee.App/XuLyDonShopee.App.csproj -c Debug
dotnet test  orders/XuLyDonShopee.Tests
```

Test hiện có **774 test** phải còn xanh (số này từ ghi chép cũ — cứ chạy rồi báo số thật). Nếu có test
nào chạm `ActivityLog`, sửa test cho khớp API mới là được, nhưng **không** nới lỏng test đang kiểm hành
vi khác.

### Đo hiệu năng — bắt buộc có số, không nói suông

Viết một test (hoặc chương trình con dùng một lần) bơm **5000 dòng log từ 5 luồng song song** vào
`ActivityLog` rồi đo:

1. **Thời gian `Append` trung bình và tệ nhất** — phải ở mức micro-giây (chỉ enqueue). Trước khi sửa nó
   là một lần mở/ghi/đóng file. Báo cả 2 con số trước/sau nếu đo được bản cũ.
2. **Số lần `SourceUpdated` bắn ra** — với 5000 dòng dồn dập phải là **hàng chục**, không phải 5000.
3. Log không mất: sau khi flush xong, **đếm số dòng trong file** phải đúng 5000.
4. Buffer mỗi nguồn đúng 200 dòng và là **200 dòng MỚI NHẤT** (kiểm nội dung dòng đầu/cuối).

### Kiểm bằng mắt

Chạy app (**cách ly dữ liệu bằng marker `data-dir.txt`, tuyệt đối không dùng `%AppData%\XuLyDonShopee`
thật của user**), vào tab Shopee → Tài khoản:

1. Panel Nhật ký hiện log, tự cuộn xuống dòng mới nhất.
2. Đổi chọn giữa các tài khoản → log đổi theo đúng tài khoản, không lẫn.
3. Nút **Copy** và **Xóa** vẫn chạy đúng.
4. Đường dẫn file log dưới panel vẫn hiện đúng.

## 5. Tiêu chí nghiệm thu

- [ ] Build xanh, test xanh (báo số test thật).
- [ ] Có **số đo** cho 4 mục "Đo hiệu năng" ở trên.
- [ ] `Append` không còn chạm đĩa trên luồng gọi (chỉ ra dòng code chứng minh).
- [ ] Hạn mức 200 dòng là **riêng từng tài khoản**, tk chạy ồn không đẩy văng log tk khác.
- [ ] File log trên đĩa vẫn đủ dòng (cắt hiển thị, KHÔNG cắt file) + xoay vòng ở 8MB.
- [ ] Chỉ 3 file trong phạm vi bị đổi; `git status` trong worktree không lòi file lạ.
- [ ] Ảnh chụp panel Nhật ký chạy được sau khi sửa.

## 6. Rủi ro & lưu ý

- **Phiên khác đang chạy song song trên cây chính** — đó là lý do bắt buộc dùng worktree. Danh sách file
  cấm đụng ghi ở mục Phạm vi; đọc kỹ trước khi sửa.
- **Giữ nguyên chữ ký `Append(source, message)`** — đây là ràng buộc cứng để không phải sửa
  `OrdersModuleHost.cs`/`AccountSession.cs`/`OrdersViewModel.cs` (đang bị phiên khác giữ).
- **Nhãn nguồn không khớp:** log phát trước `AccountSession.cs:987` dùng nhãn `"TK {id}"`, sau đó mới đổi
  sang email ⇒ mấy dòng đầu vào buffer khác. **Đừng sửa `AccountSession.cs`** (file bị cấm). Chỉ ghi nhận
  vào báo cáo để xử ở việc sau.
- **Đừng ham gom thêm việc:** không đụng nguồn `"Đơn hàng"`, `"Cấu hình"`, `"Hàng loạt"` — chúng cứ có
  buffer riêng theo cơ chế mới là đủ.
- Nếu thấy chỗ nào trong plan sai với code thật (số dòng lệch, tên thuộc tính khác), **báo lại rồi mới
  làm**, đừng tự suy diễn.

---

## Báo cáo thực thi

**Ngày:** 2026-07-30 · **Người thực thi:** opus-dev (worktree `agent-ab1fc2db54e1eb5f2`) · **Chưa commit.**

Plan đối chiếu với code thật: **khớp toàn bộ** (7 vị trí `file:dòng` ở mục 1 đều đúng). Không có điểm nào
phải hỏi lại trước khi làm.

### File đã sửa (4 — 3 theo phạm vi + 1 file test được plan §4 cho phép)

| File | Thay đổi |
|---|---|
| `orders/XuLyDonShopee.App/Services/ActivityLog.cs` | Viết lại ruột. Bỏ `ObservableCollection Entries` (rổ chung cap 500) → `Dictionary<string, Queue<LogEntry>>` khóa `OrdinalIgnoreCase`, trần `MaxLinesPerSource = 200` **cho mỗi nguồn**. `Append` chỉ `_pending.Enqueue(...)` + nhét buffer + hẹn báo → **không I/O trên luồng gọi**. `Timer` nền flush 1s/lần bằng `File.AppendAllLines` + `RollIfNeeded` 8MB (bê từ `LogBuffer.cs`). Thêm `event Action<string>? SourceUpdated` gom nhóm 250ms (cờ `_notifyScheduled` + timer một-nhịp), `Snapshot(source)` trả bản sao dưới lock, `Flush()` public, `IDisposable`. Giữ nguyên `Append(string,string)`, `CurrentLogPath`, `FormatLine`, `Clear()`, `Clear(string)`. |
| `orders/XuLyDonShopee.App/ViewModels/AccountsViewModel.cs` | Bỏ `LogEntries`, `FilteredLogEntries`, `OnLogEntriesChanged`, `RebuildFilteredLog`. `LogText` từ property tính toán → `[ObservableProperty]` gán 1 lần/nhịp trong `RebuildLogText()`. Thêm `OnLogSourceUpdated(string)` (chỉ dựng lại khi nguồn == `SelectedRow.Email`). `LogPath` được `OnPropertyChanged` mỗi lần rebuild (sửa lỗi đứng ở file hôm qua sau nửa đêm). `Dispose` gỡ `SourceUpdated`. |
| `orders/XuLyDonShopee.App/Views/AccountsView.axaml.cs` | Bỏ nghe `FilteredLogEntries.CollectionChanged` → nghe `vm.PropertyChanged` lọc `nameof(LogText)`, mỗi lần đổi `Post` **một** lượt đặt `CaretIndex`. Nút Copy dùng thẳng `vm.LogText`. Xóa comment lỗi thời "log nằm trong ListBox". |
| `orders/XuLyDonShopee.Tests/ActivityLogTests.cs` | Cập nhật theo API mới + thêm test cap-riêng-từng-nguồn, `Snapshot` (hoa/thường, bản sao, rỗng), file-vẫn-đủ-dòng-khi-hiển-thị-bị-cắt, `Clear` báo ngay, và test đo hiệu năng 5000 dòng/5 luồng. |

### Đo hiệu năng (số THẬT — test `Append_5000DongTu5Luong_NhanhKhongChamDia_BaoUIGomNhom_FileDuDong`)

5000 dòng, 5 luồng song song, 5 nguồn:

| # | Chỉ số | Kết quả |
|---|---|---|
| 1 | Thời gian `Append` | **trung bình 6,99 µs · p50 0,60 µs · p99 6,60 µs · tệ nhất 4428,7 µs**. Mốc bản CŨ (`File.AppendAllText` 1 dòng/lần, chính việc `Append` cũ làm): **85,0 µs/dòng** → p50 nhanh hơn ~**140×**. Cả 5000 dòng xong trong **10,1 ms**. |
| 2 | Số lần `SourceUpdated` bắn | **5** (không gom nhóm sẽ là 5000) → giảm **1000×**. |
| 3 | Log không mất | file có **đúng 5000 dòng** sau `Flush()`. |
| 4 | Buffer mỗi nguồn | **200 dòng**, đầu = `dong 800`, cuối = `dong 999` → đúng 200 dòng MỚI NHẤT. |

Ghi chú số "tệ nhất 4,4 ms": xảy ra ở lượt #657 giữa vòng lặp (GC/điều phối luồng), **không phải chạm đĩa** —
p99 chỉ 6,6 µs. Bản thân phép đo `Stopwatch.GetTimestamp()` quanh mỗi lượt cũng cộng ~0,1 µs vào con số.

### Build & test

- `dotnet build ShopeeSuite.sln -c Debug` → **Build succeeded, 0 Warning, 0 Error**.
- `dotnet test orders/XuLyDonShopee.Tests` → **Passed 1445 / Failed 0 / Skipped 0**. (Con số 774 trong plan là
  ghi chép cũ; số thật hiện tại là 1445.)
- `git status` trong worktree: **đúng 4 file M**, không có file lạ.

### Kiểm bằng mắt (cách ly tuyệt đối)

**Cảnh báo phải ghi lại:** ở commit này `data-dir.txt` **KHÔNG** cách ly được module Đơn hàng — nó chỉ đổi
`SuitePaths.Root` (phía suite), còn orders đi đường `OrdersModuleHost` → `new AppServices()` →
`Database.DefaultPath()` = `%AppData%\XuLyDonShopee\app.db` cứng, không đọc marker cũng không đọc `--data-dir`.
Chạy app suite thật sẽ đụng dữ liệu thật của user.

Nên thay vì chạy app suite, dựng **bệ thử Avalonia riêng NGOÀI repo** (trong scratchpad) nạp đúng
`AccountsView` + `AccountsViewModel` thật, với `new AppServices(<db trong scratchpad>)` — đường duy nhất
truyền được thư mục dữ liệu. Đã xác nhận sau khi chạy: `%APPDATA%\XuLyDonShopee` có `LastWriteTime` =
**29/07**, không sinh file nào của hôm nay.

Ảnh chụp (`RenderTargetBitmap` của chính cửa sổ), 3 tấm:
`…\scratchpad\shots\01-dang-xem-log-cua-tk-A.png`, `02-doi-sang-tk-B.png`, `03-sau-khi-bam-xoa.png`
(scratchpad phiên `68d1c245-ea0b-42cf-b067-209262af4e2d`; mã bệ thử ở `…\scratchpad\LogPanelHarness\`).

Kịch bản: 2 tài khoản, tk A bơm 400 dòng (6ms/dòng) trong lúc tk B "chạy ồn" bơm liên tục (2ms/dòng).

1. Panel hiện log tk A, **tự cuộn** xuống dòng mới nhất, không lẫn một dòng nào của tk B — dù tk B đang dội. ✔
2. Đổi chọn sang tk B → panel đổi hẳn sang log tk B, vẫn tự cuộn. ✔
3. Nút **Xóa** dọn sạch panel của tk đang chọn; đường dẫn file log dưới panel vẫn hiện đúng. ✔
4. File log trên đĩa: **866 dòng** = 400 (tk A) + 466 (tk B) — cắt hiển thị 200, KHÔNG cắt file. ✔

### Điểm cần phiên chính soi lại

1. **Hàng đợi ≤1 giây có thể mất khi tắt app** — `ActivityLog.Dispose()`/`Flush()` đã có nhưng **không ai gọi**:
   nơi duy nhất giữ nó là `AppServices` (file bị cấm sửa). Đúng như plan Bước 2 dặn "nếu không có thì thôi".
   Việc sau: gọi `Log.Dispose()` trong `OrdersModuleHost.StopAsync`.
2. **So khớp nguồn đổi từ `Ordinal` sang `OrdinalIgnoreCase`** (cả buffer lẫn `OnLogSourceUpdated`) cho khớp
   cách gom buffer theo email. Nới lỏng chứ không siết, nhưng là khác biệt hành vi có chủ ý.
3. **Nhãn `"TK {id}"`** — xác nhận đúng như plan cảnh báo (`AccountSession.cs:91` đặt, `:985` mới đổi sang
   email). Các dòng phát trước đó vào buffer `"TK 1"` nên không hiện trên panel. **Không phải hồi quy** (bản cũ
   lọc `Source == email` cũng không hiện). Chưa sửa vì file bị cấm — để việc sau.
4. **Dòng log xếp hàng trước nửa đêm mà flush sau nửa đêm sẽ rơi vào file NGÀY MỚI** (đường dẫn tính lúc
   flush). Lệch tối đa 1 giây, `LogBuffer` của suite cũng cùng dạng — không xử để khỏi phình phạm vi.
5. **Test đo hiệu năng có yếu tố thời gian** (`Thread.Sleep(600)` chờ nhịp báo cuối). Ngưỡng assert để rất
   rộng (trung bình < 50 µs, số lần báo < 500) nên khó nhấp nháy, nhưng vẫn là test chạm đồng hồ — nếu phiên
   chính không muốn giữ trong bộ test thường thì cứ bỏ hẳn test đó, 4 số đo đã có ở trên.
6. **`ActivityLogTests.cs` bị sửa** — nằm ngoài danh sách 3 file, nhưng plan §4 cho phép ("sửa test cho khớp
   API mới"); file này không nằm trong danh sách cấm.
