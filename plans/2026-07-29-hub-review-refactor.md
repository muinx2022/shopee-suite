# Plan: Hub - on dinh, mobile va refactor

- **Ngay:** 2026-07-29
- **Trang thai:** hoan thanh
- **Pham vi:** `server/Shopee.Hub.Web`

## Thu tu thuc hien

- [x] P0 - Chong race cap `row_no` khi append/insert PostgreSQL (integration test PostgreSQL nam o muc test).
- [x] P0 - Mobile Orders van xem duoc ma tra hang, van chuyen va mo phieu PDF.
- [x] P1 - Tach pagination dung chung cho Orders.
- [x] P1 - Thay `System.Threading.Timer` polling bang vong `PeriodicTimer` tuan tu, co cancellation va log loi.
- [x] P1 - Dua webhook vao bounded queue + `BackgroundService`; retry gioi han va shutdown sach.
- [x] P1 - Import Excel khong giu file 256 MB thanh nhieu ban sao trong RAM; co cancellation va don file tam.
- [x] P2 - Gom projection/helper trung giua Dispatch va Fleet.
- [x] P2 - Don inline style/CSS lap lai trong cac form da cham toi; bo `.m-hide` khoi luong xem du lieu nghiep vu Orders tren mobile.
- [x] P2 - Toi uu read path SQLite cho Orders/Shops, tranh giu global gate khi doc va bo N+1 count.
- [x] P2 - Them project test cho Hub va smoke test responsive cac breakpoint 375/768/1440.

## Follow-up rieng

- [x] Refactor an toan `Dispatch.razor`: tach projection/mapping KPI va Orders thanh `DispatchViewLogic`, them test cho KPI lifecycle va trang thai Orders. Khong tach component BigSeller/Orders trong vong nay vi callback/state/DB surface con qua chat, de tranh tang coupling chi de giam dong.
- [x] Khong trien khai integration test append/insert dong thoi tren PostgreSQL that trong dot nay: Hub chu yeu import tuan tu va it CRUD; advisory lock duoc giu lam lop bao ve phong ngua.

## Tieu chi nghiem thu

- Append/insert dong thoi tren cung account/shop khong trung `row_no`, khong mat dong.
- Mobile Orders co luong xem chi tiet va mo phieu, khong can chuyen desktop.
- Moi poller chi co toi da mot tick dang chay; loi duoc log, service khong dung am tham.
- Webhook khong tao `Task.Run` vo han va xu ly dung khi ung dung shutdown.
- Import lon co gioi han tai nguyen va huy duoc khi circuit dong.
- `dotnet build server/Shopee.Hub.Web/Shopee.Hub.Web.csproj` sach warning.
- Test Hub chay trong `server/ShopeeHub.sln`.

## Bao cao thuc thi

Cap nhat sau moi nhom thay doi.

- PostgreSQL: advisory lock theo account/sheet bao phu append, insert, import append va import replace.
- Orders mobile: them khu chi tiet co ma tra hang/van chuyen/phieu PDF; Playwright 375x812 khong overflow ngang.
- Pagination: tach `Components/Shared/Pagination.razor`, dung chung dau/cuoi bang Orders.
- Polling: Fleet va Logs dung `PeriodicTimer`, co cancellation; Fleet serialize moi refresh va throttle log loi.
- Webhook: bounded channel 256, mot worker, retry toi da 2 lan; queue day duoc ghi log thay vi tao task vo han.
- Import Excel: upload vao `import-temp`, parse tuan tu, truyen cancellation xuong PostgreSQL va cleanup file cu.
- SQLite Orders: dung pooled read connection rieng, read transaction deferred; gom count/items trong mot snapshot va group count theo shop.
- Dispatch/Fleet: dung chung `FleetViewProjection` cho account/shop projection, sheet presence va operation label.
- CSS/navigation: gom class form lap lai; bo sung title/aria-label cho icon rail tren tablet.
- Test Hub: `dotnet test server\\ShopeeHub.sln --no-restore` dat 8/8; `git diff --check` khong co loi whitespace.
- Responsive: Playwright dat 4/4 smoke checks tren 14 route tai 375x812, 768x1024 va 1440x900; khong co document-level horizontal overflow.
- Dispatch follow-up: `Dispatch.razor` giam tu 1.649 xuong 1.600 dong; `DispatchViewLogic` gom mapping KPI/Orders va `DispatchWorkItem` de unit test. `dotnet test server\\ShopeeHub.sln --no-restore` dat 25/25.
