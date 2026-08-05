// Bước CUỐI vòng shop: trang "Trả hàng/Hoàn tiền/Hủy" — chọn tab, đổi sắp xếp, đọc ô tổng + các dòng.
// Thân hàm GIỮ NGUYÊN từ background.js (tách 2026-08-06).
import { send, orderTabId } from "./core.js";
import { execInTab } from "./exec.js";
import { RETURNS_URL, RETURN_TAB_RE, SORT_NEWEST_RE, MAX_RETURN_ROWS, MAX_RETURN_HEAD_HTML } from "./constants.js";
import {
  pageLocateReturnTab, pageReturnSummaryText, pageChanDoanTraHang, pageLocateReturnCaseTab,
  pageReturnRowCount, pageLocateSortButton, pageLocateSortOption, pageScanReturnRows,
} from "./page-funcs-returns.js";
import { sleep } from "./shared/util.js";
import { waitForTabComplete } from "./shared/tab-wait.js";
import { ensureDbg, trustedClick } from "./shared/dbg-input.js";

// Bước CUỐI flow shop (bước PHỤ): mở trang "Trả hàng/Hoàn tiền/Hủy" của shop đang mở → ĐỔI SẮP XẾP sang
// "Ngày yêu cầu (Mới - Cũ)" (mặc định trang là "Ngày đến hạn" — không đổi thì luật "N dòng đầu" của C# bỏ sót
// ÂM THẦM) → đọc text ô tổng + HTML đầu từng dòng → trả MỘT lượt {soYeuCauText, sortApplied, tabTraHang, list}.
// MỘT NHỊP: gửi cả số LẪN các dòng, C# tự cắt còn k dòng đầu — trang chỉ mở/đọc đúng một lần, không tốn thêm
// vòng WS (đọc DOM thừa vài chục dòng rẻ hơn nhiều so với một lượt chờ-trả-lời nữa).
// LUÔN gửi pageData (kể cả khi không đọc được gì) để C# không phải ngồi chờ hết timeout; /verify → captcha.
export async function doReadReturnRequests() {
  const tabId = orderTabId();
  if (tabId == null) { send({ action: "error", message: "chưa có tab shop để check đơn trả hàng" }); return; }

  const traVe = (summary, sortApplied, tabTraHang, list, chanDoan) => send({
    action: "pageData",
    kind: "returns",
    data: JSON.stringify({
      soYeuCauText: summary || "",
      sortApplied: !!sortApplied,
      tabTraHang: !!tabTraHang,
      list: list || [],
      // Chỉ có mặt ở lượt BỎ (không đọc được ô tổng) — xem pageChanDoanTraHang.
      chanDoan: chanDoan || null,
    }),
  });

  // 1) Mở trang trả hàng: ưu tiên BẤM TAB (trusted click, data-testid ổn định); không thấy tab → điều hướng thẳng.
  await ensureDbg(tabId);
  let tab = null;
  const tdl = Date.now() + 8000;
  while (Date.now() < tdl) {
    try { tab = await execInTab(tabId, pageLocateReturnTab, []); } catch (e) { tab = null; }
    if (tab) break;
    await sleep(500);
  }
  if (tab) {
    await trustedClick(tabId, tab.x, tab.y);
  } else {
    send({ action: "progress", message: "không thấy tab 'Trả hàng/Hoàn tiền/Hủy' — điều hướng thẳng tới trang." });
    try { await chrome.tabs.update(tabId, { url: RETURNS_URL }); } catch (e) {}
    await waitForTabComplete(tabId, 20000);
  }

  // 2) Chờ ô tổng render (trần 20s). Rơi /verify → báo captcha rồi thôi (C# coi là bỏ bước, đi tiếp).
  let summary = "";
  const dl = Date.now() + 20000;
  while (Date.now() < dl) {
    let url = "";
    try { url = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
    if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }
    try { summary = (await execInTab(tabId, pageReturnSummaryText, [])) || ""; } catch (e) { summary = ""; }
    if (summary) break;
    await sleep(600);
  }
  if (!summary) {
    // CỐ Ý KHÔNG nới 20s ở đây: chưa có dữ liệu thì nới là đoán. Thu 4 dấu hiệu rồi gửi kèm để C# log — lượt
    // chạy thật sau sẽ nói rõ hết giờ THẬT hay đọc nhầm tab hay sai selector (xem pageChanDoanTraHang).
    let cd = null;
    try { cd = await execInTab(tabId, pageChanDoanTraHang, []); } catch (e) { cd = null; }
    send({ action: "progress", message: "trang trả hàng chưa render ô tổng sau 20s — bỏ lượt check này." });
    traVe("", false, false, [], cd);
    return;
  }

  // 3) CHỌN TAB "Đơn Trả hàng Hoàn tiền" — PHẢI làm TRƯỚC bước đổi sắp xếp và trước khi đọc số. Không thấy tab
  // nào khớp / bấm mà tab KHÔNG lên "active" → tabTraHang=false, C# BỎ LƯỢT (mốc giữ nguyên): số của tab "Tất cả"
  // ghi vào mốc là nuốt vĩnh viễn mọi yêu cầu mới, xem QuyetDinhLuotTraHang bên C#.
  let tabTraHang = false;
  try {
    await ensureDbg(tabId);
    const ct = await execInTab(tabId, pageLocateReturnCaseTab, [RETURN_TAB_RE]);
    if (ct && ct.daDung) {
      tabTraHang = true; // đã đúng tab → KHÔNG bấm (bấm lại = một vòng chờ vô ích, nhân với mọi shop mỗi lượt)
    } else if (ct) {
      const soTruoc = summary;
      let dongTruoc = 0;
      try { dongTruoc = (await execInTab(tabId, pageReturnRowCount, [])) || 0; } catch (e) { dongTruoc = 0; }
      await trustedClick(tabId, ct.x, ct.y);
      // Đổi tab → ô tổng VÀ danh sách vẽ lại. Chờ một trong hai đổi (trần 8s) rồi mới sang bước sắp xếp/đọc số.
      // Thoát sớm khi tab-strip đã báo "active": hai tab CÙNG số + cùng số dòng là chuyện thường, chờ trọn 8s
      // mỗi shop chỉ để chắc là phí.
      const cdl = Date.now() + 8000;
      while (Date.now() < cdl) {
        await sleep(500);
        let s2 = "", n2 = dongTruoc;
        try { s2 = (await execInTab(tabId, pageReturnSummaryText, [])) || ""; } catch (e) { s2 = ""; }
        try { n2 = (await execInTab(tabId, pageReturnRowCount, [])) || 0; } catch (e) { n2 = dongTruoc; }
        if ((s2 && s2 !== soTruoc) || n2 !== dongTruoc) { summary = s2 || summary; break; }
        let ct2 = null;
        try { ct2 = await execInTab(tabId, pageLocateReturnCaseTab, [RETURN_TAB_RE]); } catch (e) { ct2 = null; }
        if (ct2 && ct2.daDung) { summary = s2 || summary; break; }
      }
      // XÁC NHẬN bằng chính tab-strip, KHÔNG đặt cờ mù sau cú click: click TRƯỢT và "hai tab cùng số" trông y
      // hệt nhau qua ô tổng/số dòng, mà một bên là dữ liệu SAI tab.
      let ctSau = null;
      try { ctSau = await execInTab(tabId, pageLocateReturnCaseTab, [RETURN_TAB_RE]); } catch (e) { ctSau = null; }
      tabTraHang = !!(ctSau && ctSau.daDung);
    }
  } catch (e) { /* chọn tab lỗi → tabTraHang=false ⇒ C# bỏ lượt */ }
  if (!tabTraHang) {
    send({ action: "progress", message: "KHÔNG xác nhận được tab 'Đơn Trả hàng Hoàn tiền' đang chọn — C# sẽ bỏ lượt check (mốc giữ nguyên)." });
  }

  // 4) Đổi sắp xếp — áp SAU khi đổi tab (đổi tab nhiều khả năng reset sắp xếp về mặc định "Ngày đến hạn").
  // Không thấy nút/mục → VẪN đọc tiếp nhưng sortApplied=false để C# log cảnh báo.
  let sortApplied = false;
  try {
    await ensureDbg(tabId);
    const btn = await execInTab(tabId, pageLocateSortButton, []);
    if (btn) {
      await trustedClick(tabId, btn.x, btn.y);
      await sleep(700);
      let opt = null;
      const odl = Date.now() + 5000;
      while (Date.now() < odl) {
        opt = await execInTab(tabId, pageLocateSortOption, [SORT_NEWEST_RE]);
        if (opt) break;
        await sleep(400);
      }
      if (opt) {
        await trustedClick(tabId, opt.x, opt.y);
        sortApplied = true;
        await sleep(1500); // danh sách vẽ lại theo thứ tự mới
      }
    }
  } catch (e) { /* đổi sắp xếp lỗi → đọc theo thứ tự đang có (sortApplied=false) */ }
  if (!sortApplied) {
    send({ action: "progress", message: "KHÔNG đổi được sắp xếp 'Ngày yêu cầu (Mới - Cũ)' — đọc theo thứ tự đang có." });
  }

  // 5) Đọc lại ô tổng (sau khi danh sách vẽ lại) + quét dòng.
  try { summary = (await execInTab(tabId, pageReturnSummaryText, [])) || summary; } catch (e) {}
  const rdl = Date.now() + 8000;
  while (Date.now() < rdl) {
    let n = 0;
    try { n = (await execInTab(tabId, pageReturnRowCount, [])) || 0; } catch (e) { n = 0; }
    if (n > 0) break;
    await sleep(500);
  }
  let rows = "[]";
  try { rows = (await execInTab(tabId, pageScanReturnRows, [MAX_RETURN_ROWS, MAX_RETURN_HEAD_HTML])) || "[]"; } catch (e) { rows = "[]"; }
  let list = [];
  try { list = JSON.parse(rows) || []; } catch (e) { list = []; }

  traVe(summary, sortApplied, tabTraHang, list);
}
