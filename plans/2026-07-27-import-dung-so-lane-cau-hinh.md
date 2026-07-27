# Plan: Import LUÔN chạy 1 lane — khoá cứng, không cho cấu hình

- **Ngày:** 2026-07-27
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus (`opus-dev`)

## 1. Bối cảnh — lỗi người dùng báo

> "Phần import… tôi yêu cầu chỉ chạy 1 worker, nhưng khi tôi import từ hub, muinx-nuc chạy nhiều worker."
>
> Làm rõ sau đó: **"import chỉ chạy 1 worker, không có chỗ nào set được cái thay đổi đó."**

⇒ Import là **1 lane, luôn luôn**. KHÔNG thêm ô cấu hình, KHÔNG cho hub ghi đè. Đây là hằng số của hệ, không phải
tuỳ chọn.

Truy vết vì sao hiện đang chạy nhiều lane:

1. `suite/Shopee.Core/BigSeller/BigSellerRunConfig.cs:14-15` — `Processes` (mặc định **2**) ghi rõ *"áp dụng MỌI op
   (scrape/import/update)"*. Đây là con số duy nhất của kiến trúc Workspace/hub.
2. `suite/Shopee.Suite/Modules/UpdateProduct/UpdateProductViewModel.cs:360` — `var lanes = processes ?? t.UpdateWorkers;`
   rồi dòng ~371 truyền `lanes, lanes` vào `UpdateProductContext` ⇒ **Import và Update dùng CHUNG số lane**.
   Hub giao việc còn ghi đè bằng ô "Số process" của trang Giao việc → import chạy 2+ lane.
3. `suite/Shopee.Module.UpdateProduct/Engine/ShopConfig.cs:36` — `BigSellerImportMaxProcess` (mặc định 1) chỉ thuộc
   đường cấu hình CŨ của module (`BigSellerContextFactory`); `grep BigSellerImportMaxProcess suite/Shopee.Suite/`
   → **0 kết quả** ⇒ đường Workspace/hub không đọc tới.

Vì sao import phải 1 lane: nhiều lane import đụng nhau ở **Material Center** và tab **"Đã nhận"** của BigSeller
(cùng kho tài nguyên + cùng danh sách chờ) — xem `MaterialCenterCleaner` và cờ `ImportFromClaimedTab`.

## 2. Phạm vi

**Làm:**
- Khoá cứng **import = 1 lane** ở đường Workspace/hub, bất kể cấu hình tài khoản hay tham số hub giao.
- `AssignmentWorker.RequiredBraves`: việc `import` chỉ tính **1** khung Brave (quỹ khớp thực tế).
- Trang Giao việc: nói rõ ô "Số process" **không áp cho Import**.
- Test chốt hành vi để không ai vô tình mở lại thành nhiều lane.

**Không làm:**
- KHÔNG thêm field cấu hình mới (người dùng nói rõ: không có chỗ nào set được).
- KHÔNG đổi lane của scrape/update.
- KHÔNG đụng đường legacy `ShopConfig.BigSellerImportMaxProcess` + `BigSellerContextFactory` (lối chạy cũ, ngoài
  phạm vi) — chỉ ghi nhận trong báo cáo là nó không còn ảnh hưởng tới đường Workspace/hub.
- KHÔNG commit, KHÔNG deploy, KHÔNG release.

## 3. Các bước thực hiện

### Bước 1 — Hằng số + khoá lane ở `UpdateProductViewModel`

`suite/Shopee.Suite/Modules/UpdateProduct/UpdateProductViewModel.cs`:

```csharp
/// <summary>Import LUÔN 1 lane — nhiều lane đụng nhau ở Material Center + tab "Đã nhận" của BigSeller (cùng kho
/// tài nguyên, cùng danh sách chờ). Đây là HẰNG SỐ của hệ, KHÔNG cấu hình được: cấu hình tài khoản lẫn tham số
/// "Số process" của hub giao việc đều KHÔNG ghi đè được. Đổi số này = mở lại đúng lỗi 2026-07-27.</summary>
private const int ImportLanes = 1;
```

Trong `BuildContext` (hiện dòng ~360 và ~371):
- `var lanes = processes is int p && p > 0 ? p : t.UpdateWorkers;` — giữ nguyên, đây là lane của **Update**.
- Chỗ truyền `lanes, lanes` vào `UpdateProductContext` đổi thành `ImportLanes, lanes` (tham số thứ nhất là
  `ImportMaxProcess`, thứ hai là `UpdateMaxProcess` — **kiểm lại thứ tự trong khai báo `UpdateProductContext`**,
  đừng tin trí nhớ).
- Comment cũ `// Import & Update dùng CHUNG số lane (RunConfig.Processes); Hub giao có thể ghi đè` phải sửa cho
  đúng sự thật mới.

### Bước 2 — Quỹ Brave: `AssignmentWorker.RequiredBraves`

`suite/Shopee.Suite/Infrastructure/AssignmentWorker.cs:213` hiện trả `a.Processes > 0 ? a.Processes :
(RunConfig?.Processes ?? 2)` cho cả scrape/import/update. Thêm nhánh: `if (a.Op == "import") return 1;` (đặt ngay
sau nhánh `rewrite`/`search` trả 0), kèm comment trỏ tới hằng số `ImportLanes`.

Không sửa nhánh scrape/update. Không sửa `RequeueOrFailAsync`.

### Bước 3 — Trang Giao việc nói đúng sự thật

`server/Shopee.Hub.Web/Components/Pages/Dispatch.razor`, panel tham số: ô `Số process` đổi nhãn thành
`Số process (không áp cho Import — Import luôn 1 lane)` hoặc thêm `title` nói điều đó. KHÔNG đổi cơ chế truyền
(assignment vẫn mang `Processes`; client tự bỏ qua với op import).

### Bước 4 — Test chốt hành vi

Đặt ở `orders/XuLyDonShopee.Tests` theo khuôn LINK file nguồn đang dùng (xem `MachineSlots.cs` / `DispatchBalancer`
trước đây), hoặc chỗ nào phù hợp hơn nếu tìm được:
- `RequiredBraves(op="import", Processes=0)` → **1**.
- `RequiredBraves(op="import", Processes=5)` → **1** (hub ghi đè KHÔNG thắng).
- `RequiredBraves(op="update", Processes=0)` → giữ nguyên hành vi cũ (theo `RunConfig.Processes`).
- `RequiredBraves(op="scrape", Processes=3)` → 3 (không hồi quy).

Nếu `RequiredBraves` là `private static` thì đổi thành `internal static` + `InternalsVisibleTo` (project đã dùng
khuôn này) — KHÔNG nới thành `public`.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` + `dotnet build server/Shopee.Hub.Web` sạch, 0 warning mới.
- [ ] `dotnet test orders/XuLyDonShopee.Tests` xanh, kèm 4 test mới ở Bước 4.
- [ ] **Chạy thật trên máy này (muinx-nuc)**: hub giao **Import** cho một shop → log client báo import **1 lane**
      (dòng `▶ Import SONG SONG 1 lane …` hoặc tương đương) và **chỉ 1 cửa sổ Brave** mở. Đây là tiêu chí quan
      trọng nhất — đúng lỗi người dùng báo.
- [ ] Chạy thật: hub giao Import với ô "Số process" = **5** → vẫn **1 lane** (ghi đè không thắng).
- [ ] Chạy thật: hub giao **Update** với Số process = 0 → vẫn dùng `RunConfig.Processes` như cũ (không hồi quy).
- [ ] `grep -rn "lanes, lanes" suite/` → không còn kết quả.

## 5. Rủi ro & lưu ý

- **Kiểm thứ tự tham số `UpdateProductContext`** trước khi đổi — đặt nhầm chỗ là hoán vị lane Import/Update, lỗi
  im lặng và rất khó thấy.
- Người dùng đang chạy production: import từ nay chậm hơn (1 lane thay vì 2) — **đó đúng ý họ**, nêu rõ trong báo cáo.
- Đừng "tối ưu" bằng cách cho phép cấu hình lại: người dùng đã nói rõ không muốn có chỗ set.
- Đường legacy (`BigSellerContextFactory` đọc `BigSellerImportMaxProcess`) để nguyên; nếu phát hiện đường đó vẫn
  được Workspace/hub gọi tới thì DỪNG và báo lại — nghĩa là chẩn đoán ở mục 1 thiếu một nhánh.

---

## Báo cáo thực thi (Opus điền sau khi xong)
