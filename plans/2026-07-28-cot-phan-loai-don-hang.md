# Plan: Thêm cột "Phân loại" cho đơn hàng (GSheet + app + hub)

- **Ngày:** 2026-07-28
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh — dữ liệu ĐÃ CÓ SẴN, chỉ chưa ai hiện ra

Người dùng muốn thêm cột **Phân loại** (vd `KEM,38/39`), và nghĩ phải đọc từ **trang chi tiết** đơn:

```html
<div class="product-meta">
  <div>Phân loại:&nbsp;KEM,38/39</div>
  <div>SKU phân loại: A449</div>
</div>
```

**Khảo sát cho thấy KHÔNG cần mở trang chi tiết.** Trang DANH SÁCH đã quét sẵn:
`extensions/shopee-orders/background.js:196-209` đọc `.item-description` → `variation` → nhét vào `items_json`
(`{name, variation, amount, image}`). Tra dữ liệu THẬT trên hub production:

```
260728T47N5KSS  sku: A322    variation: 'Nâu Be,39 [A322 A322]'
260727S2R0097C  sku: A141    variation: 'Kem,36 [A141 A141]'
260727S20VWQ0K  sku: B80482  variation: 'Trắng sữa,36 [B80482 B80482]'
```

Shopee gộp cả hai dòng (`Phân loại:` + `SKU phân loại:`) vào một ô, nên đuôi `[A322 A322]` là **SKU lặp lại**.
Người dùng chốt: **cắt bỏ đuôi đó** (SKU đã có cột riêng).

⇒ Không phải mở thêm tab chi tiết (vốn tốn ~20s/đơn và tăng rủi ro captcha), và **mọi đơn CŨ đều có sẵn dữ liệu**.

**Người dùng chốt nơi hiển thị:** cả **3 chỗ** — Google Sheet, màn Đơn hàng trên app, trang Đơn hàng trên hub.
Về sheet: *"tôi đã thêm 1 cột mới Phân loại, sau sku, bạn điền phần phân loại vào đó"* — cột đã có sẵn trong sheet,
việc của ta là **gửi thêm trường dữ liệu**; phần map trường → cột nằm ở Apps Script của người dùng (họ tự sửa,
Fable cấp sẵn đoạn code).

## 2. Phạm vi

**Làm:**
- Một hàm THUẦN tách "Phân loại" từ `items_json` (dùng chung cho cả 3 nơi — một nguồn sự thật).
- GSheet: gửi thêm trường `phanLoai` trong payload đẩy lên Apps Script.
- App: thêm cột **Phân loại** ngay sau cột SKU ở màn Đơn hàng.
- Hub: thêm cột **Phân loại** ở trang Đơn hàng (suy từ `items_json` hub đã có ⇒ **đơn cũ hiện được ngay**).

**Không làm:**
- KHÔNG đụng extension (dữ liệu đã quét đủ) — **tuyệt đối không thêm lượt mở trang chi tiết**.
- KHÔNG thêm cột DB mới ở client lẫn hub: giá trị suy từ `items_json` sẵn có ⇒ không migration, đơn cũ tự có.
- KHÔNG đụng cột SKU, cột Sản phẩm hiện tại.
- KHÔNG sửa Apps Script (nằm ngoài repo, người dùng tự dán).
- KHÔNG commit, KHÔNG deploy, KHÔNG release.

## 3. Các bước thực hiện

### Bước 1 — Hàm thuần tách phân loại (`orders/XuLyDonShopee.Core/Services/`)

File mới, ví dụ `PhanLoaiExtractor.cs`:

```csharp
/// <summary>Tách "Phân loại" từ items_json để hiện thành cột riêng. Shopee gộp cả "Phân loại:" lẫn
/// "SKU phân loại:" vào MỘT ô .item-description nên chuỗi thật có dạng "Nâu Be,39 [A322 A322]" — đuôi ngoặc
/// vuông là SKU lặp lại, CẮT BỎ (SKU đã có cột riêng). Đơn nhiều sản phẩm → nối bằng " · ".</summary>
public static string TuItemsJson(string? itemsJson);
```

Luật (viết rõ trong doc + phủ bằng test):
- `items_json` rỗng / `"[]"` / JSON hỏng → trả `""` (KHÔNG ném — dữ liệu từ web, phải chịu được rác).
- Mỗi item lấy `variation`; rỗng thì bỏ qua item đó.
- **Cắt đuôi ngoặc vuông ở CUỐI chuỗi**: `"Nâu Be,39 [A322 A322]"` → `"Nâu Be,39"`. Chỉ cắt ở cuối, và chỉ khi
  có cặp `[...]` — phân loại có thể chính đáng chứa dấu ngoặc ở giữa.
- Trim khoảng trắng + `&nbsp;` (` `) — HTML gốc dùng `&nbsp;` sau dấu hai chấm.
- Nếu chuỗi còn tiền tố `Phân loại:` / `Variation:` (tuỳ ngôn ngữ UI) → bóc nốt. Extension hiện chỉ bóc tiền tố
  **tiếng Anh** (`^Variation\s*:?\s*`), UI tiếng Việt sẽ để lại `Phân loại:` — **đọc lại `background.js:203-204`
  để xác nhận** rồi xử cho đủ cả hai.
- Nhiều item → nối `" · "`, bỏ trùng lặp liên tiếp giống nhau.

**LINK file này vào hub** (`server/Shopee.Hub.Web/Shopee.Hub.Web.csproj` đã có sẵn khuôn LINK vài file của
`XuLyDonShopee.Core` — thêm theo đúng khuôn đó) để hub và client dùng CHUNG một luật, không lệch nhau.

### Bước 2 — Test (`orders/XuLyDonShopee.Tests`)

Dùng chuỗi THẬT lấy từ production ở mục 1:
- `'Nâu Be,39 [A322 A322]'` → `Nâu Be,39`
- `'Kem,36 [A141 A141]'` → `Kem,36`
- `'Trắng sữa,36 [B80482 B80482]'` → `Trắng sữa,36`
- `'Đen 9p-form chuẩn,37 [B21318 B21318]'` → `Đen 9p-form chuẩn,37`
- Không có ngoặc → giữ nguyên.
- Có `Phân loại:` / `&nbsp;` đầu chuỗi → bóc sạch.
- 2 item → `A · B`; item thiếu `variation` → bỏ qua.
- `null` / `""` / `"[]"` / `"{"` (JSON hỏng) → `""`, không ném.

### Bước 3 — Google Sheet: gửi thêm trường

`orders/XuLyDonShopee.Core/Services/GoogleSheetSyncService.cs`:
- `GsheetOrderRow` thêm `string? PhanLoai` (đặt **ngay sau `Sku`** cho khớp thứ tự cột người dùng vừa thêm).
- Giữ nguyên quy ước sẵn có: **trường null bị BỎ khỏi JSON** (hợp đồng "chỉ điền ô trống") — phân loại rỗng thì
  đừng gửi chuỗi rỗng đè lên ô người dùng có thể đã tự điền.
- Chỗ dựng `GsheetOrderRow` từ `SyncedOrder`: điền bằng `PhanLoaiExtractor.TuItemsJson(o.ItemsJson)`, rỗng → `null`.

### Bước 4 — App: cột ở màn Đơn hàng

- `orders/XuLyDonShopee.App/Views/OrdersView.axaml:196` đang có
  `<DataGridTextColumn Header="SKU" Binding="{Binding Sku}" Width="80" />` → thêm cột **Phân loại** NGAY SAU nó.
- Nguồn: `OrderRowViewModel` thêm property chỉ-đọc gọi hàm thuần trên. **Tính một lần khi dựng dòng**, đừng tính
  lại mỗi lần binding đọc (lưới có thể vẽ lại liên tục).
- Bề rộng đủ đọc `Đen 9p-form chuẩn,37` mà không đẩy các cột khác ra khỏi màn — cân theo các cột đang có.

### Bước 5 — Hub: cột ở trang Đơn hàng

- `server/Shopee.Hub.Web/Data/HubDatabase.Orders.cs`: `OrderRecord` **chưa có** `ItemsJson` (câu SELECT không lấy
  cột này). Thêm `ItemsJson` vào SELECT + mapping (`ReadOrderRow`) — **cẩn thận chỉ số cột**, thêm vào CUỐI danh
  sách SELECT để không lệch mọi `rd.GetString(i)` đang có.
- `server/Shopee.Hub.Web/Components/Pages/Orders.razor`: thêm `<th>Phân loại</th>` ngay sau cột SKU + ô tương ứng,
  giá trị từ hàm thuần. Cột SKU đang có class `m-hide` (ẩn ở màn hẹp) — cột mới **theo cùng cách** để mobile không vỡ.
- Nhờ suy từ `items_json` mà hub đã có ⇒ **đơn cũ hiện được ngay sau khi deploy**, không chờ client release.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` + `dotnet build server/Shopee.Hub.Web` sạch, 0 warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` xanh kèm test mới ở Bước 2.
- [ ] Chạy hub local với bản sao dữ liệu thật (hoặc seed đúng chuỗi ở mục 1): trang Đơn hàng hiện cột **Phân loại**
      với giá trị `Nâu Be,39`, `Kem,36`… — **không** còn đuôi `[A322 A322]`.
- [ ] Payload GSheet có trường `phanLoai` (kiểm bằng serialize thật một `GsheetOrderRow`); phân loại rỗng → trường
      **vắng mặt** khỏi JSON, không phải chuỗi rỗng.
- [ ] Màn Đơn hàng trên app có cột Phân loại ngay sau SKU.
- [ ] 400px trên hub: không cuộn ngang toàn trang.
- [ ] Không thêm cột DB nào ở client lẫn hub; không đụng extension.

## 5. Rủi ro & lưu ý

- **Đừng mở thêm trang chi tiết.** Người dùng nghĩ phải đọc từ trang chi tiết, nhưng dữ liệu đã có ở danh sách —
  thêm lượt mở tab là tự chuốc captcha và làm chậm vòng, đổi lại đúng thứ đã có sẵn.
- **Chỉ cắt ngoặc vuông ở CUỐI chuỗi.** Cắt tham lam sẽ ăn mất phần phân loại có ngoặc ở giữa.
- Dữ liệu đến từ web nên phải chịu được rác: JSON hỏng, thiếu field, `&nbsp;`, chuỗi rỗng — trả `""`, KHÔNG ném.
- Hub và client **phải dùng chung một hàm** (LINK file), nếu không hai nơi sẽ hiện hai kiểu và không ai biết bên nào đúng.
- Thay đổi ở hub có hiệu lực ngay khi deploy; phần app + GSheet phải chờ release client kế tiếp.

---

## Báo cáo thực thi (Opus điền sau khi xong)
