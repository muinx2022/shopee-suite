# Plan: Ô cấu hình "Google Sheet thứ hai" (hub + client), gửi kèm payload

- **Ngày:** 2026-07-28
- **Trạng thái:** hoàn thành — đã gộp worktree về main, 1357 test xanh. Đã kiểm LIÊN THÔNG C# ↔ Apps Script: body do `TaoJsonBody` sinh thật nạp thẳng vào `doPost` thật → bật thì mở đúng ID 1 lần + ghi A/B/E vào tab tháng tự tạo, tắt thì không đụng file phụ. Chờ deploy hub + release client.
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`) — **chạy trong worktree**

## 1. Bối cảnh

Việc ghi song song sang file Google Sheet thứ hai đang được làm ở phía Apps Script
(`plans/2026-07-28-ghi-song-song-sheet-thu-hai.md`). Script đọc ID file phụ từ **payload** (`body.sheet2`), có
hằng dự phòng. Plan này lo **phần còn lại**: chỗ nhập cấu hình + đẩy giá trị đó lên trong payload.

**Người dùng chốt:** *"cần phải có chỗ config cho url thứ 2 chứ"* — không được chôn ID vào code.

### Ai đẩy lên Google Sheet? — CLIENT, không phải hub

Đã kiểm: `orders/XuLyDonShopee.App/Services/HubOutbox.cs:430` là **chỗ DUY NHẤT** trong repo gọi
`GsheetSync.PushAsync`. Tìm `gsheet` trong file `.cs` của `server/Shopee.Hub.Web/` ra **rỗng** — hub không đẩy gì.

| | Việc |
|---|---|
| **Hub** | GIỮ cấu hình (`config/orders.json` → `OrdersSharedConfig`) + trang `/config/orders`, phát xuống mọi máy |
| **Client** | Nhận cấu hình, lưu SQLite `settings`, rồi **tự POST** lên Apps Script Web App |

⇒ Phải sửa **cả hai**, nhưng phần việc khác nhau: hub thêm ô nhập (gõ một lần cho cả fleet), client nhận về +
gửi kèm payload.

### Khuôn đã có, BÁM THEO — đừng đẻ luật đồng bộ mới

`suite/Shopee.Core/Coordination/OrdersSharedConfig.cs` (2 field) + hàm thuần
`XuLyDonShopee.Core.Services.GsheetConfigSync.QuyetDinhApBanHub` (đã có unit test) giữ **bất biến**:

> URL trống = **CÔNG TẮC TẮT** đồng bộ GSheet ⇒ **Hub trống thì TUYỆT ĐỐI không đè client** (kẻo cả fleet âm
> thầm tắt ghi sheet). Hub có URL ⇒ khối GSheet là **MỘT đơn vị đã cấu hình** ⇒ áp CẢ hai field.

Field mới phải nằm **trong cùng khối một-đơn-vị đó**, không tự tạo luật riêng.

## 2. Phạm vi

**Làm:**
- Thêm field `GsheetSheet2` vào `OrdersSharedConfig` + trang hub `/config/orders` + màn Cài đặt client.
- Đồng bộ theo đúng khối GSheet sẵn có.
- Gửi kèm `sheet2` trong payload POST lên Apps Script.

**Không làm:**
- KHÔNG đụng `orders/gsheet-apps-script/Code.gs` (việc song song đang sửa file đó — **sẽ xung đột**).
- KHÔNG đổi hành vi ô URL Web App / ô Tab hiện có.
- KHÔNG đẩy từ hub — hub chỉ giữ cấu hình.
- KHÔNG commit, KHÔNG deploy, KHÔNG release. KHÔNG đụng `%LOCALAPPDATA%\Programs\ShopeeSuite`.

## 3. ⚠ Ba cái bẫy

1. **Giá trị này KHÔNG phải URL Web App.** Ô cũ là `https://script.google.com/…/exec`; ô mới là **URL bảng tính**
   `https://docs.google.com/spreadsheets/d/<ID>/edit…` hoặc **ID trần**. **Đừng dùng lại `GsheetConfigSync.KiemTraUrl`**
   (nó ép tiền tố `script.google.com` ⇒ sẽ báo lỗi oan). Viết hàm kiểm riêng.
2. **Hub rỗng không được đè client** — nhưng field mới nằm TRONG khối GSheet, nên khi hub CÓ URL Web App thì
   `GsheetSheet2` của hub được áp **kể cả khi nó trống** (trống lúc đó mang nghĩa "tắt ghi file phụ", giống hệt
   cách tab trống nghĩa là "tự động theo tháng"). Ghi rõ điều này trong doc + test.
3. **Trống phải là công tắc TẮT tường minh.** Payload gửi `sheet2: ""` khi người dùng để trống — để script phân
   biệt "tắt" với "client đời cũ chưa biết field này" (field vắng → script lùi về hằng dự phòng của nó).
   ⇒ Field `Sheet2` trong `GsheetOrderRow`/body **KHÔNG** được bỏ khi rỗng như các field khác.

## 4. Các bước

### Bước 1 — DTO dùng chung

`suite/Shopee.Core/Coordination/OrdersSharedConfig.cs`: thêm

```csharp
/// <summary>URL bảng tính (hoặc ID) của FILE PHỤ nhận thêm cột A–E. Trống = KHÔNG ghi file phụ.
/// KHÁC GsheetWebAppUrl: đây là link docs.google.com/spreadsheets/…, không phải Web App /exec.</summary>
public string? GsheetSheet2 { get; set; }
```

### Bước 2 — Hàm thuần: kiểm + bóc ID

`orders/XuLyDonShopee.Core/Services/GsheetConfigSync.cs`, thêm cạnh `KiemTraUrl`:

- `public static string? KiemTraSheet2(string? s)` — trống → hợp lệ (tắt). Khác trống: chấp nhận URL
  `docs.google.com/spreadsheets/d/<ID>` **hoặc** ID trần (`[A-Za-z0-9_-]{20,}`). Không khớp → thông điệp lỗi
  tiếng Việt cho người dùng (mẫu `KiemTraUrl`).
- `public static string BocIdSheet(string? s)` — trả ID hoặc chuỗi rỗng. Luật khớp **đúng như** hàm `bocIdSheet`
  phía Apps Script để hai bên không lệch: regex `/spreadsheets/d/([A-Za-z0-9_-]+)` trước; không khớp thì nhận cả
  chuỗi nếu chỉ gồm `[A-Za-z0-9_-]`; còn lại rỗng.

Test: URL đầy đủ có `?usp=sharing` / có `#gid=` / ID trần / chuỗi rác / rỗng / null.

### Bước 3 — Luật đồng bộ: field mới vào cùng khối

`GsheetConfigSync.QuyetDinhApBanHub`: **đọc code trước**, mở rộng để mang theo `GsheetSheet2` đúng cùng nhánh
quyết định với `GsheetTabName`. Không thêm nhánh mới, không thêm điều kiện riêng cho field này.

Test bổ sung (giữ nguyên mọi test cũ):
- [ ] Hub URL trống → **không áp gì**, `GsheetSheet2` local giữ nguyên (bất biến #1).
- [ ] Hub có URL, `GsheetSheet2` hub trống, local có → **áp** (⇒ local thành trống = tắt ghi file phụ). Đây là
      hành vi CỐ Ý, phải có test khoá lại.
- [ ] Hub có URL, `GsheetSheet2` khác local → áp.
- [ ] Ba field y hệt local → `Ap = false` (khỏi ghi SQLite mỗi nhịp poll).

### Bước 4 — Lưu phía client

`orders/XuLyDonShopee.Core/Data/SettingsRepository.cs`: khóa `gsheet_sheet2`, cặp `GetGsheetSheet2` /
`SetGsheetSheet2` theo đúng khuôn `GetGsheetWebAppUrl` (trim, rỗng → null).

### Bước 5 — Màn Cài đặt client

`orders/XuLyDonShopee.App/ViewModels/SettingsViewModel.cs` + view tương ứng: thêm ô **"Link Google Sheet 2 (tuỳ chọn)"**
ngay dưới ô tab hiện có. Nạp/lưu/validate theo đúng khuôn hai ô sẵn có, dùng `KiemTraSheet2`. Placeholder:
`https://docs.google.com/spreadsheets/d/…` kèm chú thích ngắn *"để trống = không ghi file phụ"*.

### Bước 6 — Trang hub `/config/orders`

`server/Shopee.Hub.Web/Components/Pages/ConfigOrders.razor`: thêm ô thứ ba, validate bằng **cùng** `KiemTraSheet2`
(trang này đã gọi `GsheetConfigSync.KiemTraUrl` cho ô cũ — bám đúng khuôn đó). Trim khi lưu.

### Bước 7 — Gửi kèm payload

`orders/XuLyDonShopee.Core/Services/GoogleSheetSyncService.cs`:
- `TaoJsonBody` thêm `sheet2` ở cấp **body** (cạnh `tab`), KHÔNG phải trong từng đơn — nó là thuộc tính của cả lô.
- **LUÔN gửi** kể cả chuỗi rỗng (xem bẫy #3) — khác quy ước "bỏ field null" của các field trong đơn.
- `PushAsync` nhận thêm tham số `sheet2` (mặc định `""` để call site cũ/test không vỡ).

`orders/XuLyDonShopee.App/Services/HubOutbox.cs` (quanh dòng 430): đọc `services.Settings.GetGsheetSheet2()`,
bóc ID bằng `BocIdSheet`, truyền xuống `PushAsync`.

Test: body có `"sheet2":"<ID>"`; cấu hình trống → `"sheet2":""` **có mặt**; URL đầy đủ → gửi ID đã bóc.

## 5. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` + `dotnet build server/Shopee.Hub.Web` sạch, 0 warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` xanh, **không sửa kỳ vọng test cũ nào**.
- [ ] Serialize thật một body: có `sheet2` đúng ID; cấu hình trống → `"sheet2":""` vẫn có mặt.
- [ ] Mọi test cũ của `GsheetConfigSync` còn nguyên và còn xanh (bất biến "hub rỗng không đè").

## 6. Rủi ro & lưu ý

- **Đừng đụng `orders/gsheet-apps-script/Code.gs`** — có việc khác đang sửa file đó, sẽ xung đột khi gộp.
- Ô mới **không** dùng chung validator với ô Web App (bẫy #1) — dùng nhầm là báo lỗi oan mọi link bảng tính.
- Bóc ID ở **client** (không gửi URL thô) để script khỏi phải parse lại — nhưng luật bóc phải khớp hai bên,
  vì script vẫn tự bóc phòng client đời cũ gửi URL thô.
- Thay đổi ở client ⇒ chỉ có hiệu lực sau release; ở hub ⇒ hiệu lực ngay khi deploy. **Deploy hub TRƯỚC**, để khi
  client mới lên thì cấu hình đã sẵn ở hub (đúng nếp `hub-remote-update-command`).

---

## Báo cáo thực thi (Opus điền sau khi xong)
