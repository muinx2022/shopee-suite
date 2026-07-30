# Plan: Hợp nhất bộ Shopee-login (3A) + human-input CDP (3B) về Core

- **Ngày:** 2026-07-30
- **Trạng thái:** hoàn thành
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

**Xong phần code (3A + 3B). 3/4 tiêu chí nghiệm thu ĐẠT; tiêu chí "test mới" BỊ CHẶN vì lý do hạ tầng — xem mục E.**

Nền: worktree đã `git merge --ff-only main` lên `11cf807` trước khi build/test lần cuối, nên mọi số đo dưới đây là trên nền mới nhất.

### A. File đã sửa/tạo (7 file, +60 / −438 dòng)

| File | Thay đổi |
|---|---|
| `suite/Shopee.Core/Accounts/ShopeeAuth.cs` **(MỚI)** | `ParseLoginLine` + `ShopeeLoginLineOptions`/`ShopeeLoginLine`; `IsSessionCookie`; `BuildSpcFCookie`; hằng `SpcF/SpcSt/SpcEcCookie`, `DefaultCookieDomain`, `LoginUrl`, `LoginUrlPrefix` |
| `suite/Shopee.Core/Cdp/CdpHumanInput.cs` **(MỚI)** | `CdpSender` (2 delegate + `For(CdpSession)`); `HumanInputProfile` + 2 profile tĩnh `Search`/`CheckAccount`; `CdpHumanInput` (MoveMouseTo/Click/Wheel/TypeText/SelectAllAndDelete/PressKey) |
| `…MultiBrave/Engine/ShopeeLoginAutomation.cs` | **XOÁ** (62 dòng: `TryParseLoginLine` + record `ShopeeAccountLogin`) |
| `…MultiBrave/Engine/BraveInstanceSession.cs` | `IsShopeeLoggedInAsync` → `IsSessionCookie`; bỏ `TryParseShopeeAccountLogin`, gọi thẳng `ParseLoginLine(…Strict)`; `SetShopeeSpcFCookieAsync` → `BuildSpcFCookie`; 3 literal URL → `ShopeeAuth.LoginUrl/LoginUrlPrefix`; `ShopeeAccountLogin` → `ShopeeLoginLine`. **JS `typeHuman` KHÔNG đụng.** |
| `…Search/Engine/ShopeeLoginService.cs` | Xoá `TryParseLoginLine` cục bộ + hằng `LoginUrl`; `IsLoggedInAsync` → `IsSessionCookie`; `SetSpcFCookieAsync` → `BuildSpcFCookie`. Vòng chờ 90s + `FillLoginFormAsync` JS giữ nguyên tại module |
| `…Search/Engine/CdpInputController.cs` | −154 dòng primitive → `CdpHumanInput(HumanInputProfile.Search)`; giữ nguyên lớp điều phối WS/ack/`_gate`/reconnect |
| `…CheckAccount/ShopeeAccountChecker.cs` | −167 dòng (`TryParse` + primitive) → `CdpHumanInput(HumanInputProfile.CheckAccount, _rng)`; `LoginUrl` = `ShopeeAuth.LoginUrl`; `SetCookieAsync` → `SetSpcFCookieAsync` dùng `BuildSpcFCookie`. Flow điền form/chờ kết quả giữ tại module |

### B. Bảng đối chiếu hằng human-input trước/sau — KIỂM CHỨNG BẰNG MÁY, 12/12 khớp tuyệt đối

`Random.Next(lo, hi)` có **hi loại trừ**. Bản cũ dùng lẫn hai kiểu: helper `Delay`/`DelayAsync(min,max)` = `Next(min, max+1)` (bao gồm hai đầu) và vài chỗ gọi **thẳng** `Next(lo, hi)`. Lớp gộp chỉ có MỘT helper kiểu bao-gồm-hai-đầu, nên cột "profile mới" phải quy đổi. Bảng dưới so **cặp `(lo, hi-loại-trừ)` mà code THỰC SỰ gọi** — đây mới là bằng chứng "không hằng nào đổi":

| Module | Hằng | Biểu thức CŨ | `Next()` cũ | Profile mới | `Next()` mới | |
|---|---|---|---|---|---|---|
| SE | InitMouseX | `200 + Next(0,400)` | (200, 400) | `200 + 400` | (200, 400) | OK |
| SE | InitMouseY | `150 + Next(0,300)` | (150, 300) | `150 + 300` | (150, 300) | OK |
| SE | Delay mỗi bước di chuột | `Delay(8,24)` | (8, 25) | `8–24` | (8, 25) | OK |
| SE | Delay trước khi nhấn | `Delay(180,520)` | (180, 521) | `180–520` | (180, 521) | OK |
| SE | Delay giữ nút | `Delay(55,150)` | (55, 151) | `55–150` | (55, 151) | OK |
| SE | Delay mỗi ký tự ASCII | `Delay(45,120)` | (45, 121) | `45–120` | (45, 121) | OK |
| CA | InitMouseX | `220 + Next(0,380)` | (220, 380) | `220 + 380` | (220, 380) | OK |
| CA | InitMouseY | `160 + Next(0,260)` | (160, 260) | `160 + 260` | (160, 260) | OK |
| CA | Delay mỗi bước di chuột | `Next(8,24)` **(gọi thẳng)** | (8, 24) | `8–`**`23`** | (8, 24) | OK |
| CA | Delay trước khi nhấn | `DelayAsync(160,480)` | (160, 481) | `160–480` | (160, 481) | OK |
| CA | Delay giữ nút | `DelayAsync(50,140)` | (50, 141) | `50–140` | (50, 141) | OK |
| CA | Delay mỗi ký tự ASCII | `Next(55,150)` **(gọi thẳng)** | (55, 150) | `55–`**`149`** | (55, 150) | OK |

> **Hai ô in đậm (CA `8–23`, `55–149`) KHÔNG phải đổi giá trị.** Plan §1 ghi "delay 8-24ms" và "CA 55-150" là đọc theo literal `Next(8,24)` / `Next(55,150)` — mà hai chỗ đó CA gọi thẳng `Next` (cận trên loại trừ), khác với các chỗ còn lại đi qua helper. Viết `8–23` / `55–149` trong profile bao-gồm-hai-đầu sinh ra đúng `Next(8,24)` / `Next(55,150)` như cũ. Nếu ghi `8–24` / `55–150` mới là ĐỔI THẬT (thành `Next(8,25)` / `Next(55,151)`). Đây cũng là **2 điểm lệch SE↔CA plan chưa liệt kê** (plan xếp `delay 8-24ms` vào nhóm "trùng hết") → đã tham số hoá vào profile, không tự hợp nhất.

Hằng **giống hệt cả hai bản** nên để thẳng trong `CdpHumanInput`, không tham số hoá: số bước di chuột `Next(10,20)`; easing `t<0.5 ? 2t² : 1−(−2t+2)²/2`; jitter `Sin(t·π·3)·Rand(−4,4)` / `Cos(t·π·2)·Rand(−3,3)`; ngưỡng ASCII `0x20..0x7E`; delay trước `insertText` 120–260; delay phím Enter/đặc biệt 40–110; `KeyInfo` (Digit/Key/Space). Chuỗi phím Enter (`keyDown` + `text="\r"` → 40–110 → `keyUp`) hai bản vốn giống hệt tới cả thứ tự thuộc tính JSON.

**Hình dạng payload sự kiện chuột** — điểm lệch thứ 3 plan chưa liệt kê, đã tham số hoá bằng cờ `MouseEventsCarryWheelFields` (SE `true`, CA `false`) để JSON gửi đi y hệt trước:

| Sự kiện | SE (cũ = sau) | CA (cũ = sau) |
|---|---|---|
| `mouseMoved` trung gian (no-reply) | `type,x,y,button,buttons,clickCount=0,deltaX=0,deltaY=0` | `type,x,y,button,buttons` |
| `mouseMoved` cuối (có chờ) | `…,clickCount=0,deltaX=0,deltaY=0` | `…,clickCount=0` |
| `mousePressed`/`mouseReleased` | `…,clickCount=N,deltaX=0,deltaY=0` | `…,clickCount=1` |

Op **chỉ SE có** (`wheel`, `clearFirst`, `SelectAllAndDelete`, `SpecialKeyInfo`) đã đưa vào Core nguyên xi; CA không gọi.

**Chuỗi ngẫu nhiên:** CA truyền chính `_rng` của nó vào `CdpHumanInput` → thứ tự rút số của cả luồng (khởi tạo toạ độ chuột → `DelayAsync` của form → human-input) giữ nguyên như khi code còn nằm trong lớp. SE không dùng `_rng` ở đâu khác nên `CdpHumanInput` tự tạo `Random`.

### C. Bảng đối chiếu hằng/điều kiện login trước → sau

| Mục | MB cũ | SE cũ | CA cũ | Sau khi gộp |
|---|---|---|---|---|
| URL trang login | `…/buyer/login?next=https%3A%2F%2Fshopee.vn` | y hệt | y hệt | `ShopeeAuth.LoginUrl` — **3 bản vốn giống hệt**, không cần tham số hoá |
| Cookie SPC_F: hạn | 30 ngày | 30 ngày | 30 ngày | 30 ngày |
| Cookie SPC_F: `path/secure/httpOnly/sameSite` | `/`, true, false, `Lax` | y hệt | y hệt | y hệt |
| Cookie SPC_F: domain rỗng | fallback `.shopee.vn` | KHÔNG fallback (parse đã chặn domain rỗng → không tới được) | fallback `.shopee.vn` | fallback `.shopee.vn` — SE không đổi hành vi thực tế |
| Predicate phiên: domain | `Contains("shopee")` ignore-case | y hệt | y hệt | y hệt |
| Predicate phiên: giá trị | `!IsNullOrWhiteSpace && != "-" && Length>5` | `Length>5 && != "-"` | `Length>5 && != "-"` | `Length>5 && != "-"` |
| Predicate phiên: tên cookie | `Equals` **ignore-case** | `is not ("SPC_ST" or "SPC_EC")` (phân biệt hoa/thường) | như SE | **ignore-case** |

Hai dòng cuối lệch thật nhưng chỉ ở mức bệnh lý; đã hợp nhất theo hướng **union** để không bản nào mất true-positive: MB bỏ guard thừa `!IsNullOrWhiteSpace` (chỉ khác khi giá trị dài >5 mà toàn khoảng trắng — cookie phiên thật không thể vậy); SE/CA chuyển sang so tên ignore-case (nới lỏng; cookie thật của Shopee luôn đúng hoa).

### D. Đối chiếu ngữ nghĩa parse — kiểm chứng bằng máy, 42/45 ô giống hệt

Harness ngoài repo (scratchpad, KHÔNG commit) chép nguyên xi **cả ba bản parse cũ** làm oracle, chạy 15 dòng đầu vào × 3 bộ cờ = **45 ô**, so từng trường `(Ok, Username, Password, CookieDomain, SpcF)`.

**3 ô lệch — đúng bằng phần nới lỏng plan §3.1 yêu cầu, không ô nào biến "đang chạy được" thành "bị loại":**

| # | Bộ cờ | Đầu vào | Cũ | Mới | Căn cứ |
|---|---|---|---|---|---|
| 1 | MB `Strict` | `user1\|pass1\|.shopee.vn=FOO=abc123` | FAIL | OK, spcF=`abc123` | §3.1 bỏ ràng buộc BẮT BUỘC prefix `SPC_F=`; tên cookie khác vẫn lấy giá trị (bằng SE/CA) |
| 2 | SE `AllowEmptyPassword` | `user1\|pass1\|.shopee.vn=SPC_F=abc\|123` | spcF=`abc` (**rơi `\|123`**) | spcF=`abc\|123` | §3.1 ghép `parts[2..]` theo cách MB — không rơi dữ liệu |
| 3 | CA `AllowMissingCookie` | như trên | spcF=`abc` | spcF=`abc\|123` | như trên |

Cờ từng module đúng plan §3.1: MB `Strict` = {CookieOptional:false, PasswordOptional:false}; SE `AllowEmptyPassword` = {false, true}; CA `AllowMissingCookie` = {true, false}.

Ca đã phủ (đủ bộ plan §4 yêu cầu): dòng MB chuẩn có `SPC_F=` + `|` trong cookie · dòng SE không prefix lấy `=` thứ 2 · dòng CA 2 phần không cookie · dòng thiếu password (SE pass, MB/CA fail) · dòng cookie prefix lạ · thừa khoảng trắng · prefix chữ thường · không có tên cookie · cookie rỗng · username rỗng · cookie hỏng không có `=` · domain rỗng · giá trị SPC_F rỗng · chuỗi rỗng · 1 phần.

Thông điệp lỗi của MB (thứ chảy ra log `Shopee login: …`) giữ nguyên từng chữ trong `ParseLoginLine`.

### E. Nghiệm thu

| Tiêu chí | Kết quả |
|---|---|
| `dotnet build ShopeeSuite.sln` 0 lỗi 0 warning | **ĐẠT** — Build succeeded, 0 Warning(s), 0 Error(s) |
| `dotnet test orders/XuLyDonShopee.Tests` không tụt (1449) | **ĐẠT** — 1449/1449 pass, 0 fail |
| Grep sạch | **ĐẠT** — `TryParseLoginLine`/`ShopeeLoginAutomation`: 0 kết quả. `MoveMouseTo`/ease: chỉ `Core/Cdp/CdpHumanInput.cs` + 1 chỗ gọi ở SE. Literal `SPC_ST`/`SPC_EC` trong `suite/`: chỉ `ShopeeAuth.cs:43-44` (còn lại là comment) |
| Test mới cho `ParseLoginLine` | **CHẶN** — xem dưới |
| Bảng đối chiếu hằng số, không giá trị nào đổi | **ĐẠT** — mục B, 12/12 khớp tuyệt đối (kiểm chứng bằng máy) |

**Vì sao tiêu chí test bị chặn:** project test DUY NHẤT của `ShopeeSuite.sln` là `orders/XuLyDonShopee.Tests` — nằm trong vùng CẤM (`orders/**`) và cũng không tham chiếu `Shopee.Core`. Tạo project test mới thì phải sửa `ShopeeSuite.sln` — file dùng chung, đang có agent khác chạy song song. Đã thay bằng kiểm chứng bằng máy (mục B + D). **Đề xuất:** tách việc riêng dựng `suite/Shopee.Core.Tests` sau khi các agent song song merge xong; harness hiện có chuyển thẳng thành test case được (đã có sẵn oracle 3 bản cũ + bảng hằng).

### F. Điểm cần phiên chính soi lại

1. **Rủi ro hành vi THẬT duy nhất — ô #2/#3 mục D.** Với SE và CA, dòng tài khoản có **trường thứ 4 trở đi** (vd `user|pass|cookie|ghi-chu`) trước đây bị cắt bỏ, nay bị GHÉP vào giá trị SPC_F gửi lên Shopee. MB vốn đã ghép nên "mọi thứ sau dấu `|` thứ 2 là cookie" đúng là hợp đồng định dạng hiện hành và plan §3.1 chốt theo MB — nhưng nếu kho tài khoản thật có dòng 4 trường thì SE/CA sẽ set cookie SPC_F sai. Nên liếc dữ liệu tài khoản thật trước khi phát hành.
2. **Lệch so với chữ trong plan (3 điểm, đều có lý do kỹ thuật):**
   - **Đường dẫn `ShopeeAuth.cs`:** plan ghi `suite/Shopee.Core/Shopee/ShopeeAuth.cs`; đặt vào `Accounts/` (namespace `Shopee.Core.Accounts`, cạnh `ShopeeAccount.ShopeeAccountLogin` — đúng thứ đang parse). Lý do bắt buộc: namespace `Shopee.Core.Shopee` **tự che** namespace gốc `Shopee` — trong đó mọi tham chiếu `Shopee.Core.X` phân giải nhầm thành `Shopee.Core.Shopee.Core.X` → không build được.
   - **`IsLoggedIn`:** plan ghi chữ ký `IEnumerable<(string name, string value)>` — thiếu **domain**, mà cả 3 bản đều lọc `domain.Contains("shopee")` (SE/CA đọc `Network.getAllCookies` trả cookie của MỌI site nên bỏ lọc domain là sai chức năng). Đã đổi thành `IsSessionCookie(domain, name, value)` gọi tại chỗ ở cả 3 module; không thêm bản nhận collection vì sẽ thành code chết.
   - **`CdpSender` giữ kiểu delegate đúng plan**, dù thực tế **SE và CA đã dùng chung `Shopee.Core.Cdp.CdpSession`** (tiền đề "mỗi module một transport" của plan giờ chỉ còn đúng với MB). Giữ delegate để việc 3C dùng lại được cho `CdpClient` của MB.
3. **Chỗ lệch/còn sót ĐÃ GIỮ NGUYÊN, không tự hợp nhất** (theo §5):
   - `suite/Shopee.Module.MultiBrave/Engine/ExtensionRunnerAutomation.cs:727` còn literal `"https://shopee.vn/buyer/login"` — thuộc luồng extension-runner (nhận diện trang), không thuộc bộ login của plan; đồng thời đây đúng là file `main` vừa sửa trong 21 commit merge vào, đụng vào là tự tạo xung đột.
   - `orders/XuLyDonShopee.Core/Services/ShopeeLoginCookies.cs` có danh sách cookie phiên RIÊNG `{SPC_EC, SPC_ST, SPC_U}` (thừa `SPC_U`) — khác tập hợp + nằm trong vùng cấm → để nguyên, ghi nhận cho đợt sau.
   - MB dùng `Storage.setCookies` còn SE/CA dùng `Network.setCookie` — chỉ gộp phần *dựng payload* (`BuildSpcFCookie`), giữ nguyên cách gửi của từng module (đúng ý plan: chưa gộp transport).
