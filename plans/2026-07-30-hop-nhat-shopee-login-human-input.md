# Plan: Hợp nhất bộ Shopee-login (3A) + human-input CDP (3B) về Core

- **Ngày:** 2026-07-30
- **Trạng thái:** đang làm
- **Người lập:** Fable · **Người thực thi:** Opus

## 1. Bối cảnh & mục tiêu

Repo có **3 bản** logic đăng nhập Shopee đang LỆCH ngữ nghĩa và **2 bản** human-input CDP gần trùng (kiểm chứng 30/07):

| Bản | Vị trí | Ngữ nghĩa parse dòng tài khoản (`user\|pass\|cookie...`) |
|---|---|---|
| MB (MultiBrave) | `suite/Shopee.Module.MultiBrave/Engine/ShopeeLoginAutomation.cs:5-55` (TryParseLoginLine) + `BraveInstanceSession.cs:~1463-1791` (IsShopeeLoggedInAsync ~1466, EnsureShopeeLoggedInAsync ~1497, SetSpcF ~1666, FillShopeeLoginFormAsync ~1698 JS typeHuman) | Split('\|', TrimEntries); đòi ≥3 phần; cookiePart = JOIN lại parts[2..] bằng '\|' (chấp nhận '\|' trong giá trị); đòi user+pass+cookie non-empty; BẮT BUỘC prefix `SPC_F=` (OrdinalIgnoreCase); trả error message |
| SE (Search) | `suite/Shopee.Module.Search/Engine/ShopeeLoginService.cs` (221 dòng, bản đầy đủ độc lập: EnsureLoggedInAsync, IsLoggedInAsync SPC_ST/SPC_EC, SetSpcFCookieAsync, FillLoginFormAsync JS, vòng chờ 90s, TryParseLoginLine :188-215; SearchSession.cs:177 dùng) | Split('\|') KHÔNG TrimEntries; đòi ≥3 phần nhưng CHỈ lấy parts[2] (rơi phần sau '\|' thứ 3); KHÔNG check prefix SPC_F (lấy mọi thứ sau dấu '=' thứ 2 bất kể tên); KHÔNG đòi password non-empty |
| CA (CheckAccount) | `suite/Shopee.Module.CheckAccount/ShopeeAccountChecker.cs` (573 dòng: CheckAsync ~50, EnsureLoggedInAsync ~124, WaitOutcomeAsync ~283, IsLoggedInAsync ~314, TryParse ~545-572, human-input ~452-538) | Chấp nhận ≥2 phần (cookie TUỲ CHỌN — 'user\|pass' đủ); đòi user+pass non-empty; chỉ lấy parts[2]; không check prefix; cookiePart hỏng (eqIdx≤0) vẫn trả true không lỗi |

Human-input (3B): `SE/Engine/CdpInputController.cs:140-277` vs `CA/ShopeeAccountChecker.cs:~452-538` — TRÙNG hết logic (MoveMouseTo steps rng 10-20, ease `t<0.5?2t²…`, jitter Sin(t·π·3)·Rand(−4,4) / Cos(t·π·2)·Rand(−3,3), SendNoReply, delay 8-24ms; KeyInfo Digit/Key/Space; Enter '\r' delay 40-110) nhưng **LỆCH hằng**: click SE 180-520ms & 55-150ms vs CA 160-480ms & 50-140ms; gõ ascii SE 45-120ms vs CA 55-150ms; toạ độ khởi tạo chuột SE 200+400/150+300 vs CA 220+380/160+260; SE có thêm op wheel/clearFirst/SelectAllAndDelete/SpecialK.

Mục tiêu: mỗi logic chỉ còn **một bản trong `suite/Shopee.Core`**, hành vi từng module GIỮ NGUYÊN TỪNG HẰNG SỐ (code anti-bot — đổi delay/easing là dính captcha).

## 2. Phạm vi

- **Làm:** như dưới; chỉ đụng `suite/Shopee.Core/**` (file MỚI + đăng ký), `suite/Shopee.Module.MultiBrave/**`, `suite/Shopee.Module.Search/**`, `suite/Shopee.Module.CheckAccount/**`.
- **Không làm:** KHÔNG sửa `suite/Shopee.Core/Coordination/**`, `suite/Shopee.Suite/**`, `orders/**`, `server/**`, `extensions/**` (agent khác đang làm song song); KHÔNG hợp nhất transport CDP (việc 3C sau); KHÔNG đụng JS typeHuman của MB (giữ nguyên cơ chế fill bằng JS của MB); KHÔNG "cải thiện" delay/easing.

## 3. Các bước thực hiện

1. **Core `ShopeeAuth` (pure logic)** — file mới `suite/Shopee.Core/Shopee/ShopeeAuth.cs`:
   - Hằng: tên cookie `SPC_ST`, `SPC_EC`, `SPC_F`, domain cookie, URL trang login (đối chiếu 3 bản — nếu URL lệch nhau thì tham số hoá, ghi rõ trong báo cáo).
   - `ParseLoginLine(string line, ShopeeLoginLineOptions opts)` → record kết quả (Username, Password, SpcF, Error). Ngữ nghĩa HỢP NHẤT kiểu superset an toàn (không tài khoản nào đang chạy được bị loại):
     - Split('|') + TrimEntries; đòi ≥3 phần, hoặc ≥2 khi `opts.CookieOptional` (CA).
     - Password: bắt buộc non-empty trừ khi `opts.PasswordOptional` (SE).
     - CookiePart = JOIN parts[2..] bằng '|' (cách MB — không rơi dữ liệu). Nếu có prefix `SPC_F=` (ignore-case) → lấy phần sau; nếu KHÔNG có prefix nhưng có dấu '=' → lấy phần sau dấu '=' đầu tiên (tương thích SE/CA hiện tại) + không lỗi; không có '=' → lỗi khi cookie bắt buộc.
     - Cờ gọi từng module: MB `{CookieOptional:false, PasswordOptional:false}`; SE `{false, true}`; CA `{true, false}`.
   - `IsLoggedIn(IEnumerable<(string name, string value)> cookies)` — predicate SPC_ST/SPC_EC dùng chung (đối chiếu 3 bản trước khi viết: bản nào check gì thì hợp nhất theo bản CHẶT hơn chỉ khi 3 bản thực sự tương đương; nếu lệch thật thì tham số hoá và ghi rõ).
   - JS tìm nút login / selector form: chỉ đưa vào Core phần 3 bản GIỐNG HỆT; phần lệch giữ tại module.
2. **Core `HumanInputProfile` + `CdpHumanInput`** — file mới `suite/Shopee.Core/Cdp/CdpHumanInput.cs`:
   - `HumanInputProfile` chứa TOÀN BỘ hằng lệch: ClickDelayRange1/2, AsciiKeyDelayRange, EnterKeyDelayRange, InitMouseX/Y (base+rand)… Hai profile tĩnh: `SearchProfile` (180-520, 55-150, 45-120, 200+400/150+300), `CheckAccountProfile` (160-480, 50-140, 55-150, 220+380/160+260) — đúng từng số hiện tại.
   - `CdpHumanInput` nhận delegate gửi CDP (`Func<string method, object params, Task>` SendNoReply + Send) để không phụ thuộc transport từng module; port nguyên xi MoveMouseTo/Click/TypeText/PressEnter (giữ ease + jitter từng ký tự); các op chỉ SE có (wheel, clearFirst, SelectAllAndDelete, SpecialK) đưa vào luôn, CA không gọi.
   - SE `CdpInputController` và CA `ShopeeAccountChecker` chuyển sang gọi bản Core với profile tương ứng; xoá code trùng tại chỗ.
3. **Ba module chuyển sang `ShopeeAuth.ParseLoginLine`** (+ giữ message lỗi hiện có nơi UI đang hiển thị); xoá `TryParseLoginLine`/`TryParse` cục bộ.
4. **SE `ShopeeLoginService` + CA `EnsureLoggedInAsync`/`IsLoggedInAsync` + MB `IsShopeeLoggedInAsync`/`SetSpcF`**: thay phần predicate cookie + set-SPC_F + hằng URL bằng bản Core (qua delegate CDP hiện có của từng module). Vòng chờ login + flow điền form GIỮ TẠI MODULE (chỉ gọi helper Core cho phần chung). MB FillShopeeLoginFormAsync (JS typeHuman) KHÔNG đụng.
5. **Bảng đối chiếu hằng số trước/sau** trong báo cáo: từng delay/toạ độ/điều kiện của SE, CA, MB — chứng minh không hằng nào đổi giá trị theo đường gọi của module đó.

## 4. Tiêu chí nghiệm thu

- [ ] `dotnet build ShopeeSuite.sln` 0 lỗi 0 warning; `dotnet test orders/XuLyDonShopee.Tests` không tụt (1449) — orders không đụng nhưng build chung solution.
- [ ] Grep: `TryParseLoginLine|TryParse.*LoginLine` chỉ còn bản Core; ba module không còn định nghĩa MoveMouseTo/ease cục bộ (SE/CA); `SPC_ST`/`SPC_EC` literal chỉ còn trong Core (+ chỗ khác ngoài phạm vi nếu có — liệt kê).
- [ ] Test mới cho `ShopeeAuth.ParseLoginLine`: bộ case phủ cả 3 ngữ nghĩa cũ — dòng MB chuẩn (SPC_F= + '|' trong cookie), dòng SE (không prefix, '=' thứ 2), dòng CA 2 phần không cookie, dòng thiếu password (SE pass, MB/CA fail), dòng cookie prefix lạ.
- [ ] Bảng đối chiếu hằng số trong báo cáo — không giá trị nào đổi.

## 5. Rủi ro & lưu ý

- ANTI-BOT: cấm thay đổi giá trị delay/easing/jitter/toạ độ — chỉ DI CHUYỂN. Mọi chỗ nghi lệch mà plan chưa liệt kê → giữ nguyên tại module + ghi vào báo cáo, KHÔNG tự hợp nhất.
- Bạn làm trong worktree riêng: mọi đường dẫn quy về thư mục làm việc của bạn; không đọc/ghi cây chính. Agent khác đang sửa `suite/Shopee.Core/Coordination/**` + `suite/Shopee.Suite/**` + `orders/**` + `server/**` + `extensions/**` — tránh tuyệt đối các khu đó.
- KHÔNG commit; xong điền "Báo cáo thực thi" vào file plan này (trong worktree) rồi báo cáo tóm tắt.

---

## Báo cáo thực thi (Opus điền sau khi xong)

(chưa)
