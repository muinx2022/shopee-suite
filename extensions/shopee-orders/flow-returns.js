// Bước CUỐI vòng shop: trang "Trả hàng/Hoàn tiền/Hủy" — chọn tab, đổi sắp xếp, đọc ô tổng + các dòng.
// Thân hàm GIỮ NGUYÊN từ background.js (tách 2026-08-06).
import { send, orderTabId } from "./core.js";
import { execInTab } from "./exec.js";
import {
  RETURNS_URL, RETURN_TAB_RE, SORT_NEWEST_RE, MAX_RETURN_ROWS, MAX_RETURN_PAGES, MAX_RETURN_HEAD_HTML,
} from "./constants.js";
import {
  pageLocateReturnTab, pageReturnSummaryText, pageChanDoanTraHang, pageLocateReturnCaseTab,
  pageReturnRowCount, pageLocateSortButton, pageLocateSortOption, pageScanReturnRows,
  pageReturnListSignature, pageChanDoanPagerTraHang,
} from "./page-funcs-returns.js";
import { pageFindNextPage } from "./page-funcs.js";
import { sleep } from "./shared/util.js";
import { waitForTabComplete } from "./shared/tab-wait.js";
import { ensureDbg, trustedClick } from "./shared/dbg-input.js";

// Chờ danh sách trả hàng ĐỔI so với ký hiệu 'before' sau khi bấm "trang sau" (đối xứng waitOrdersChanged của
// trang đơn). Bỏ trạng thái đang tải ("0|"): giữa hai trang Vue xoá sạch bảng một nhịp rồi mới vẽ trang mới.
async function waitReturnsChanged(tabId, before, timeoutMs) {
  const dl = Date.now() + timeoutMs;
  while (Date.now() < dl) {
    await sleep(300);
    let now = "";
    try { now = (await execInTab(tabId, pageReturnListSignature, [])) || ""; } catch (e) {}
    if (now.indexOf("0|") === 0) continue;
    if (now && now !== before) return true;
  }
  return false;
}

// Quét dòng của TRANG HIỆN TẠI, cắt theo phần còn lại của trần tổng.
async function quetTrangHienTai(tabId, daCo) {
  const con = MAX_RETURN_ROWS - daCo;
  if (con <= 0) return [];
  let rows = "[]";
  try { rows = (await execInTab(tabId, pageScanReturnRows, [con, MAX_RETURN_HEAD_HTML])) || "[]"; } catch (e) { rows = "[]"; }
  try { return JSON.parse(rows) || []; } catch (e) { return []; }
}

// Có nút "trang sau" DÙNG ĐƯỢC không (dùng chung pageFindNextPage với trang đơn — cùng bộ EDS pager).
async function coTrangSau(tabId) {
  try { return !!(await execInTab(tabId, pageFindNextPage, [])); } catch (e) { return false; }
}

// LẬT TRANG: từ trang đang mở, lật tối đa `soTrang` lượt, gom dòng vào `list` (đã có sẵn dòng trang hiện tại).
// Trả { soTrangLat, coTrangSau, pagerChanDoan } — dừng khi: hết trang / danh sách không đổi (bấm trượt) /
// chạm trần dòng. KHÔNG bao giờ ném: lật trang là phần MỞ RỘNG, hỏng thì lượt vẫn có dữ liệu trang đầu.
async function latTrang(tabId, list, soTrang) {
  let soTrangLat = 0;
  let pagerChanDoan = null;
  const tran = Math.min(Math.max(0, soTrang), MAX_RETURN_PAGES);
  while (soTrangLat < tran && list.length < MAX_RETURN_ROWS) {
    let sigTruoc = "";
    try { sigTruoc = (await execInTab(tabId, pageReturnListSignature, [])) || ""; } catch (e) {}
    let next = null;
    try { next = await execInTab(tabId, pageFindNextPage, []); } catch (e) { next = null; }
    if (!next) {
      // CHỈ chẩn đoán khi chưa lật nổi trang NÀO trong lượt này: đó mới là ca "selector pager của trang trả hàng
      // khác trang đơn" cần lộ ra. Lật được rồi mới hết nút là đường THÀNH CÔNG (đã tới trang cuối) — bắn chẩn
      // đoán kèm 4000 ký tự HTML ở đó là báo động giả, mỗi shop mỗi vòng một lần.
      if (soTrangLat === 0) {
        try { pagerChanDoan = await execInTab(tabId, pageChanDoanPagerTraHang, [MAX_RETURN_HEAD_HTML]); } catch (e) {}
      }
      break;
    }
    await ensureDbg(tabId);
    await trustedClick(tabId, next.x, next.y);
    if (!(await waitReturnsChanged(tabId, sigTruoc, 10000))) break;
    soTrangLat++;
    const them = await quetTrangHienTai(tabId, list.length);
    if (them.length === 0) break;
    for (const d of them) list.push(d);
  }
  return { soTrangLat: soTrangLat, coTrangSau: await coTrangSau(tabId), pagerChanDoan: pagerChanDoan };
}

// Bước CUỐI flow shop (bước PHỤ): mở trang "Trả hàng/Hoàn tiền/Hủy" của shop đang mở → ĐỔI SẮP XẾP sang
// "Ngày yêu cầu (Mới - Cũ)" (mặc định trang là "Ngày đến hạn" — không đổi thì luật "N dòng đầu" của C# bỏ sót
// ÂM THẦM) → đọc text ô tổng + HTML đầu từng dòng → trả MỘT lượt {soYeuCauText, sortApplied, tabTraHang, list}.
// MỘT NHỊP: gửi cả số LẪN các dòng, C# tự cắt còn k dòng đầu — trang chỉ mở/đọc đúng một lần, không tốn thêm
// vòng WS (đọc DOM thừa vài chục dòng rẻ hơn nhiều so với một lượt chờ-trả-lời nữa).
// LUÔN gửi pageData (kể cả khi không đọc được gì) để C# không phải ngồi chờ hết timeout; /verify → captcha.
export async function doReadReturnRequests() {
  const tabId = orderTabId();
  if (tabId == null) { send({ action: "error", message: "chưa có tab shop để check đơn trả hàng" }); return; }

  const traVe = (summary, sortApplied, tabTraHang, list, chanDoan, them) => send({
    action: "pageData",
    kind: "returns",
    data: JSON.stringify(Object.assign({
      soYeuCauText: summary || "",
      sortApplied: !!sortApplied,
      tabTraHang: !!tabTraHang,
      list: list || [],
      // Chỉ có mặt ở lượt BỎ (không đọc được ô tổng) — xem pageChanDoanTraHang.
      chanDoan: chanDoan || null,
    }, them || {})));

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

  // 5) Đọc lại ô tổng (sau khi danh sách vẽ lại) + quét dòng TRANG ĐẦU.
  try { summary = (await execInTab(tabId, pageReturnSummaryText, [])) || summary; } catch (e) {}
  const rdl = Date.now() + 8000;
  while (Date.now() < rdl) {
    let n = 0;
    try { n = (await execInTab(tabId, pageReturnRowCount, [])) || 0; } catch (e) { n = 0; }
    if (n > 0) break;
    await sleep(500);
  }
  const list = await quetTrangHienTai(tabId, 0);

  // Lượt này CHỈ trang đầu. C# đọc ô tổng rồi so với mốc mới biết cần lật mấy trang (luật SoTrangCanDoc) — nên
  // phần sâu đi bằng lệnh THỨ HAI `readReturnRequestsMore` trên chính trang đang mở, KHÔNG mở lại trang lần nữa.
  // Cố ý không đoán trước độ sâu ở đây: đoán thừa thì mọi shop mỗi vòng đều lật trang vô ích.
  traVe(summary, sortApplied, tabTraHang, list, null, {
    soTrangDaDoc: 1,
    coTrangSau: await coTrangSau(tabId),
  });
}

// Lượt ĐỌC THÊM (bước 2 của check trả hàng): C# đã biết số yêu cầu + mốc cũ nên tự tính được cần lật mấy trang,
// gửi `readReturnRequestsMore {maxPages}`. Trang trả hàng ĐANG MỞ sẵn (đúng tab, đúng sắp xếp) từ lượt trước —
// hàm này KHÔNG điều hướng, KHÔNG chọn lại tab, KHÔNG đổi lại sắp xếp: chỉ lật trang và quét.
// Trả về CÙNG khuôn `pageData kind:"returns"` (C# chỉ dùng phần `list`).
export async function doReadReturnRequestsMore(maxPages) {
  const tabId = orderTabId();
  const traVe = (list, them) => send({
    action: "pageData",
    kind: "returns",
    data: JSON.stringify(Object.assign({
      soYeuCauText: "", sortApplied: true, tabTraHang: true, list: list || [], chanDoan: null,
    }, them || {})),
  });
  if (tabId == null) { traVe([], { soTrangDaDoc: 0, coTrangSau: false }); return; }

  // Rơi /verify giữa chừng → báo captcha rồi thôi (C# coi như bỏ phần đọc thêm, phần trang đầu vẫn giữ).
  let url = "";
  try { url = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
  if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }

  const list = [];
  const kq = await latTrang(tabId, list, maxPages);
  if (kq.pagerChanDoan) {
    send({ action: "progress", message: "Trả hàng: không thấy nút 'trang sau' — khối phân trang: " + kq.pagerChanDoan });
  }
  traVe(list, { soTrangDaDoc: kq.soTrangLat, coTrangSau: kq.coTrangSau });
}
