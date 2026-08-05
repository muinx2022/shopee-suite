// Bơm hàm vào trang (world MAIN): cài helper _na/_provCore lên window rồi chạy page-func.
// LƯU Ý sống còn: page-func được chrome.scripting serialize ĐỘC LẬP → phải TỰ CHỨA (xem page-funcs.js).

// Chạy một hàm trong trang (world MAIN), trả result[0].result.
// Cài helper _na/_provCore lên window của TRANG (world MAIN) — vì page-func chạy executeScript được serialize
// ĐỘC LẬP (không kèm helper ngoài), nên các hàm gọi bare `_na(...)`/`_provCore(...)` sẽ resolve về global window.*.
// PHẢI gọi TRƯỚC mỗi page-func dùng helper (idempotent, rẻ).
export function pageInstallHelpers() {
  window._na = function (s) {
    const nf = (s || "").replace(/\s+/g, " ").trim().toLowerCase().normalize("NFD");
    let out = "";
    for (const ch of nf) {
      const c = ch.charCodeAt(0);
      if (c >= 0x300 && c <= 0x36f) continue;
      out += ch === "đ" ? "d" : ch;
    }
    return out;
  };
  window._provCore = function (p) {
    let s = window._na(p);
    const prefixes = ["thanh pho ", "tinh ", "tp.", "tp "];
    for (const pre of prefixes) {
      if (s.indexOf(pre) === 0) { s = s.substring(pre.length).trim(); break; }
    }
    return s;
  };
}

export async function execInTab(tabId, func, args) {
  // Đảm bảo _na/_provCore có trên window trước khi chạy page-func (page-func gọi bare → global).
  try {
    await chrome.scripting.executeScript({ target: { tabId }, world: "MAIN", func: pageInstallHelpers });
  } catch (e) { /* bỏ qua — nếu func không dùng helper thì cũng không sao */ }
  const res = await chrome.scripting.executeScript({
    target: { tabId },
    world: "MAIN",
    func,
    args: args || [],
  });
  return res && res[0] ? res[0].result : null;
}

// Chờ tab load xong: waitForTabComplete ở shared/tab-wait.js (nghe sự kiện thay vì poll).

// Trusted input (chrome.debugger): ensureDbg/trustedClick ở shared/dbg-input.js.
