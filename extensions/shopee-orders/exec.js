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

// Trần chờ tab hồi sinh sau khi bị trình duyệt DISCARD (nạp lại từ đầu) — xem `hoiSinhTabNeuBiVutBoNho`.
const CHO_TAB_HOI_SINH_MS = 20000;

/// Tab bị trình duyệt VỨT KHỎI BỘ NHỚ (discarded) thì nạp lại rồi chờ xong. Trả true nếu ĐÃ hồi sinh.
// Vì sao cần (ca thật 10/08/2026, vòng 08:48): giữa hai shop vòng chạy nghỉ 3–4 phút; Brave/Chrome discard tab
// nền để tiết kiệm RAM. Tab discarded GIỮ NGUYÊN url (nên chrome.tabs.get vẫn đọc ra /portal/shop) nhưng KHÔNG
// còn renderer, nên chrome.scripting.executeScript ném "Cannot access contents of the page. Extension manifest
// must request permission to access the respective host." — câu báo SAI THỦ PHẠM, host thì có quyền hẳn hoi.
async function hoiSinhTabNeuBiVutBoNho(tabId) {
  let t = null;
  try { t = await chrome.tabs.get(tabId); } catch (e) { return false; }
  // BẮT CẢ HAI trạng thái. Ca 10/08/2026 vòng 09:07 báo `status=unloaded discarded=false`: Brave UNLOAD tab
  // (renderer bị bỏ, tab vẫn còn trong danh sách) — hệ quả giống hệt discard mà cờ `discarded` KHÔNG bật.
  // Chỉ kiểm `discarded` là lưới không nổ, đúng lỗi của bản trước.
  if (!t || (t.discarded !== true && t.status !== "unloaded")) { return false; }
  try { await chrome.tabs.reload(tabId); } catch (e) { return false; }
  const dl = Date.now() + CHO_TAB_HOI_SINH_MS;
  while (Date.now() < dl) {
    await new Promise((r) => setTimeout(r, 400));
    try {
      const t2 = await chrome.tabs.get(tabId);
      if (t2 && t2.discarded !== true && t2.status === "complete") { return true; }
      if (t2 && t2.status === "unloaded") { try { await chrome.tabs.reload(tabId); } catch (e3) { return false; } }
    } catch (e) { return false; }
  }
  return false;
}

export async function execInTab(tabId, func, args) {
  // Đảm bảo _na/_provCore có trên window trước khi chạy page-func (page-func gọi bare → global).
  try {
    await chrome.scripting.executeScript({ target: { tabId }, world: "MAIN", func: pageInstallHelpers });
  } catch (e) { /* bỏ qua — nếu func không dùng helper thì cũng không sao */ }
  try {
    const res = await chrome.scripting.executeScript({
      target: { tabId },
      world: "MAIN",
      func,
      args: args || [],
    });
    return res && res[0] ? res[0].result : null;
  } catch (e) {
    // Ưu tiên số 1: tab bị DISCARD thì nạp lại rồi THỬ LẠI ĐÚNG MỘT LẦN — đây là ca thường gặp sau lúc nghỉ,
    // và để nó ném ra ngoài là background.js gửi {action:"error"} ⇒ C# fault chặng ⇒ CẢ VÒNG chết vì một tab
    // ngủ đông. Chỉ thử lại một lần: hỏng tiếp là hỏng thật, đừng quay vòng.
    if (await hoiSinhTabNeuBiVutBoNho(tabId)) {
      try {
        await chrome.scripting.executeScript({ target: { tabId }, world: "MAIN", func: pageInstallHelpers });
      } catch (e2) { /* như trên */ }
      const res2 = await chrome.scripting.executeScript({ target: { tabId }, world: "MAIN", func, args: args || [] });
      return res2 && res2[0] ? res2[0].result : null;
    }

    // KÈM TAB + URL + trạng thái vào lỗi. Câu gốc của Chrome nói "thiếu host permission" kể cả khi host CÓ trong
    // manifest (ca tab discarded), nên không có mấy dữ kiện này là đi tìm sai hướng — đã mất một vòng vì thế.
    let mota = "(không đọc được tab " + tabId + ")";
    try {
      const t = await chrome.tabs.get(tabId);
      mota = "url=" + (t.url || "(rỗng)") + " status=" + (t.status || "?") + " discarded=" + (t.discarded === true);
    } catch (e2) { /* giữ mota mặc định */ }
    const goc = String((e && e.message) || e);
    throw new Error(goc + " · tab=" + tabId + " " + mota + " · func=" + ((func && func.name) || "?"));
  }
}

// Chờ tab load xong: waitForTabComplete ở shared/tab-wait.js (nghe sự kiện thay vì poll).

// Trusted input (chrome.debugger): ensureDbg/trustedClick ở shared/dbg-input.js.
