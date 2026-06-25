# Tối ưu hóa app khi chạy (chống treo / ì máy)

Hướng dẫn chạy Shopee Suite ổn định khi cào nhiều Brave (Scrape/Search) — áp dụng cho **bất kỳ máy nào**.

> Bối cảnh: chạy nhiều cửa sổ Brave nhiều giờ dễ làm máy **ì/treo dù RAM còn dư** — thủ phạm thường là
> (1) bùng số tiến trình Brave, (2) Windows Defender quét realtime đống file Brave/video tạo ra liên tục.
> File này gom mọi bước cần làm để tránh điều đó.

---

## TL;DR — máy mới cần làm 5 việc
1. Cài **Brave** + extension **"Product Copy" của BigSeller** cho Brave (Scrape bắt buộc có).
2. **Build bản mới** (`publish-suite.cmd`).
3. **Thêm Windows Defender exclusions** (PowerShell Admin) — quan trọng nhất, chống ì.
4. **Đặt số cửa sổ song song** hợp với RAM máy.
5. Chạy `publish\ShopeeSuite\ShopeeSuite.exe`.

---

## 1. Build / cập nhật bản chạy
- **Đóng app + Brave trước khi build** (publish ghi đè đúng folder đang chạy, app đang mở sẽ khoá file).
- Chạy `publish-suite.cmd` ở gốc repo, hoặc lệnh tương đương:
  ```
  dotnet publish suite\Shopee.Suite\Shopee.Suite.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o "publish\ShopeeSuite"
  ```
- Exe ra ở `publish\ShopeeSuite\ShopeeSuite.exe`.
- Kiểm tra đang chạy ĐÚNG bản mới (PowerShell):
  ```powershell
  $s = Get-Process ShopeeSuite -ErrorAction SilentlyContinue
  $s.Path; (Get-Item $s.Path).LastWriteTime    # ngày build phải mới
  ```

---

## 2. Windows Defender exclusions — BẮT BUỘC nên làm
**Vì sao:** app + Brave ghi/xóa LIÊN TỤC profile-cache + file video → Defender quét realtime đống file đó
→ nghẽn đĩa/CPU → máy **ì dù RAM còn dư**. Loại các thư mục dữ liệu app + 2 process khỏi quét realtime sẽ
cắt nguồn ì này. (Chỉ loại dữ liệu app tin cậy — an toàn, và gỡ lại được bất cứ lúc nào.)

Mở **PowerShell (Run as administrator)** → dán (sửa path video + publish cho đúng máy):
```powershell
Add-MpPreference -ExclusionProcess "brave.exe"
Add-MpPreference -ExclusionProcess "ShopeeSuite.exe"
Add-MpPreference -ExclusionPath "$env:APPDATA\ShopeeSuite"
Add-MpPreference -ExclusionPath "D:\videos"                                       # ĐỔI thành thư mục video thật
Add-MpPreference -ExclusionPath "D:\Projects\shopee-suite\publish\ShopeeSuite"    # ĐỔI thành nơi đặt publish
```
Kiểm tra đã vào chưa:
```powershell
Get-MpPreference | Select -ExpandProperty ExclusionProcess
Get-MpPreference | Select -ExpandProperty ExclusionPath
```
Gỡ sau này (nếu muốn): thay `Add-MpPreference` → `Remove-MpPreference` với cùng tham số.

> `$env:APPDATA\ShopeeSuite` đã bao trùm persistent-data / profiles / shared / search (nơi Brave ghi cache nhiều nhất).

---

## 3. Số cửa sổ Brave song song theo RAM
- **Tổng cửa sổ Brave = (số job BigSeller chạy cùng lúc) × MaxProcess** mỗi job. Mỗi cửa sổ ≈ 5–8 tiến trình, ~1–1.5GB.
- App tự chặn tổng ở trần cửa sổ — mặc định lấy ràng buộc CHẶT hơn giữa **RAM/2** và **số_nhân_CPU/2** (vì mỗi cửa sổ ngốn ~0.6 nhân; chạy quá tay làm máy ì dù RAM dư). Vd 32GB + 12 nhân → **6**.
- **Chỉnh trong app: Cài đặt → tab Hiệu năng** — hiện CPU/RAM máy + nhập **ngân sách**: *số nhân CPU dùng* (mỗi cửa sổ ~1 nhân) + *RAM dùng (GB)* (mỗi cửa sổ ~2GB) → app tính **tối đa = min(CPU, RAM÷2)** (hiện live). Mặc định = nửa số nhân + toàn bộ RAM. Đặt thấp hơn để chừa máy cho việc khác; cao hơn = nhanh hơn nhưng dễ ì. Lưu xong có hiệu lực từ lượt chạy kế tiếp.
- "Dòng/lần" (Scrape) mặc định **60** — Brave xoay vòng ít hơn → đỡ cold-start extension liên tục (nguồn giật CPU). Tk đã lưu cấu hình cũ vẫn giữ số cũ, chỉnh tay trong UI nếu muốn.
- Gợi ý đặt MaxProcess + số job sao cho TỔNG không vượt:

  | RAM máy | Tổng cửa sổ Brave nên giữ |
  |---|---|
  | 16 GB | ≤ 6–8 |
  | 32 GB | ≤ 12–14 |
  | 64 GB | ≤ ~30 |

- Đặt quá tay thì app **tự cho chờ** (không tràn), nhưng nên đặt vừa để mượt + đỡ bị Shopee soi.

---

## 4. Cơ chế chống treo đã tích hợp sẵn (tự động — không cần thao tác)
- **Phanh tổng cửa sổ** + chờ khi RAM trống thấp.
- **Trần CỨNG số tiến trình ở Job Object** → OS tự ép, kể cả khi app treo; kèm `KILL_ON_JOB_CLOSE` → đóng app là Brave chết theo.
- **Quét dọn Brave mồ côi** lúc khởi động + định kỳ (4 phút) + GC/trả RAM về OS (chạy luồng nền, không phụ thuộc UI).
- **Scrape & Search không giành cùng 1 account** (sổ giữ chỗ chung).

---

## 5. Khi máy bắt đầu ì — cứu nhanh (KHÔNG cần hard reset)
1. Mở **Task Manager → Run new task** (hoặc Win+R) → gõ:
   ```
   taskkill /F /IM ShopeeSuite.exe
   ```
   → Job Object kéo theo **toàn bộ Brave con chết theo** → RAM/đĩa nhả ngay, máy hồi lại.
2. Mở lại app → nó **tự quét dọn Brave sót** của lần trước.
3. Nếu kẹt nặng không vào nổi terminal → reboot cũng an toàn (tiến độ lưu sau mỗi chunk, không mất dữ liệu).

---

## 6. Theo dõi / chẩn đoán khi nghi ì (tùy chọn)
Dùng lệnh **nhẹ, KHÔNG đụng WMI** (Get-Process là .NET, nhanh kể cả khi máy bận):
```powershell
(Get-Process brave).Count                                   # số tiến trình brave (bò lên = mồ côi tích tụ)
[math]::Round((Get-Process brave | Measure-Object WorkingSet64 -Sum).Sum/1GB,2)  # RAM brave (GB)
(Get-Process brave | Measure-Object HandleCount -Sum).Sum   # tổng handle (leo đều = rò handle)
$s=Get-Process ShopeeSuite; "$($s.HandleCount) handle / $($s.Threads.Count) thread"
```
- **TRÁNH** `Get-CimInstance Win32_PageFileUsage` và các query WMI khác khi máy đang bận — chúng hay **treo/timeout** và làm nặng thêm.
- Đối chiếu xu hướng: handle/thread leo → rò handle; số brave tăng → mồ côi; brave-RAM creep → renderer phình.

---

## Lưu ý quan trọng
- Phải cài **extension "Product Copy" của BigSeller cho Brave** trước khi chạy Scrape (thiếu là Scrape dừng báo lỗi).
- Profile login Shopee/BigSeller lưu ở `%AppData%\ShopeeSuite\persistent-data` — **bền qua các lần build** (không bị xoá).
- Đừng tắt media (ảnh/video) của Shopee để "cho nhẹ" — Shopee coi là bất thường, dễ dính captcha; và luồng tải video cần thẻ `<video>` nạp metadata.
