// Page-func của trang "Trả hàng/Hoàn tiền/Hủy" — tách riêng vì đây là mảng hay phải vá lại nhất khi Shopee
// đổi giao diện, và nó là bước CUỐI, độc lập với vòng đơn. Thân hàm GIỮ NGUYÊN từ background.js (2026-08-06).
// Cùng luật tự chứa như page-funcs.js: `_na(...)` bên trong thân hàm resolve về window._na TRONG TAB
// (pageInstallHelpers cài), KHÔNG phải binding import dưới đây — import chỉ để định danh có nguồn rõ ràng.
import { _na } from "./page-funcs.js";

// ===== Bước CUỐI flow shop — trang "Trả hàng/Hoàn tiền/Hủy" (/portal/sale/returnrefundcancel) =====

// Toạ độ TÂM tab mở trang trả hàng. Dùng data-testid (ổn định) — ĐỪNG dò theo text.
export function pageLocateReturnTab() {
  const el = document.querySelector("[data-testid='l1-tab-return_refund_cancel']");
  if (!el) return null;
  const r0 = el.getBoundingClientRect();
  if (!(r0.width > 0 && r0.height > 0)) return null;
  try { el.scrollIntoView({ block: "center" }); } catch (e) {}
  const r = el.getBoundingClientRect();
  return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) };
}

// Tab "Đơn Trả hàng Hoàn tiền" trên tab-strip của trang trả hàng (khớp reSrc = text đã chuẩn hoá KHÔNG dấu).
// PHẠM VI CỐ Ý thu hẹp vào ".return-case-tab-wrapper": thanh điều hướng TRÁI có mục tên gần giống ("Đơn Trả
// hàng/Hoàn tiền hoặc Đơn hủy") — dò text trên cả trang là bấm nhầm sang đó rồi lạc trang.
// Trả { daDung: true } nếu tab khớp ĐANG chọn (class "active") ⇒ caller KHÔNG bấm: bấm lại rồi ngồi chờ danh
// sách vẽ lại (mà nó không vẽ lại) là đốt thời gian mỗi shop. Khớp mà chưa active → toạ độ TÂM để trustedClick.
// null = không có tab-strip / không tab nào khớp ⇒ caller vẫn đi tiếp với tab hiện tại (kèm cảnh báo).
export function pageLocateReturnCaseTab(reSrc) {
  const re = new RegExp(reSrc);
  for (const wrap of document.querySelectorAll(".return-case-tab-wrapper")) {
    for (const tab of wrap.querySelectorAll(".eds-tabs__nav-tab")) {
      if (!re.test(_na(tab.textContent))) continue;
      const cls = " " + (tab.getAttribute("class") || "") + " ";
      if (cls.indexOf(" active ") >= 0) return { daDung: true };
      const r0 = tab.getBoundingClientRect();
      if (!(r0.width > 0 && r0.height > 0)) continue;
      try { tab.scrollIntoView({ block: "center" }); } catch (e) {}
      const r = tab.getBoundingClientRect();
      return { daDung: false, x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) };
    }
  }
  return null;
}

// Text ô tổng ".return-list-summary-title" (vd "7 Yêu cầu"). "" nếu chưa render — C# lo parse ra SỐ.
export function pageReturnSummaryText() {
  const el = document.querySelector(".return-list-summary-title");
  return el ? (el.textContent || "").replace(/\s+/g, " ").trim() : "";
}

// Toạ độ nút mở dropdown sắp xếp (.sort-button). null nếu chưa có.
export function pageLocateSortButton() {
  for (const el of document.querySelectorAll(".sort-button")) {
    const r0 = el.getBoundingClientRect();
    if (!(r0.width > 0 && r0.height > 0)) continue;
    try { el.scrollIntoView({ block: "center" }); } catch (e) {}
    const r = el.getBoundingClientRect();
    return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) };
  }
  return null;
}

// Mục sắp xếp khớp reSrc (text đã chuẩn hoá KHÔNG dấu) trong .eds-dropdown-menu đang mở → toạ độ. null nếu không thấy.
export function pageLocateSortOption(reSrc) {
  const re = new RegExp(reSrc);
  for (const menu of document.querySelectorAll(".eds-dropdown-menu")) {
    const mr = menu.getBoundingClientRect();
    if (!(mr.width > 0 && mr.height > 0)) continue;
    for (const li of menu.querySelectorAll("li.eds-dropdown-item, li, [role='menuitem']")) {
      if (!re.test(_na(li.textContent))) continue;
      const r0 = li.getBoundingClientRect();
      if (!(r0.width > 0 && r0.height > 0)) continue;
      try { li.scrollIntoView({ block: "center" }); } catch (e) {}
      const r = li.getBoundingClientRect();
      return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) };
    }
  }
  return null;
}

// Số dòng yêu cầu đang render (chờ danh sách vẽ lại sau khi đổi sắp xếp).
export function pageReturnRowCount() {
  return document.querySelectorAll(".return-table-content a.return-row-item").length;
}

// Ký hiệu danh sách trả hàng hiện tại: "<số dòng>|<href dòng đầu>" — để biết trang ĐÃ ĐỔI sau khi bấm "trang
// sau". CỐ Ý dùng href (mang returnId, duy nhất) chứ không dùng mỗi số dòng: hai trang liên tiếp gần như LUÔN
// cùng số dòng, nhìn vào số dòng thì tưởng chưa đổi rồi dừng lật sớm.
// (Đối xứng pageListSignature của trang đơn — hai trang khác selector nên không dùng chung được một hàm.)
export function pageReturnListSignature() {
  const rows = document.querySelectorAll(".return-table-content a.return-row-item");
  return rows.length + "|" + (rows.length ? (rows[0].getAttribute("href") || "") : "");
}

// CHẨN ĐOÁN khi KHÔNG tìm được nút "trang sau" (pageFindNextPage trả null): gửi về HTML rút gọn của khối phân
// trang để lượt chạy THẬT lộ markup thật — selector pager đang dùng chung với trang đơn (bộ EDS pager), chưa
// xác nhận trên trang trả hàng. `null` = trang không có khối phân trang nào (một trang là hết, chuyện thường).
export function pageChanDoanPagerTraHang(maxHtml) {
  for (const el of document.querySelectorAll("[class*='pager'], [class*='pagination']")) {
    const r = el.getBoundingClientRect();
    if (!(r.width > 0 && r.height > 0)) continue;
    let html = "";
    try {
      const clone = el.cloneNode(true);
      for (const rac of clone.querySelectorAll("img, svg")) rac.remove();
      html = clone.outerHTML;
    } catch (e) { html = el.outerHTML; }
    return html.length > maxHtml ? html.substring(0, maxHtml) : html;
  }
  return null;
}

// Quét các dòng yêu cầu (trang ĐẦU, không phân trang) → JSON [{shopeeOrderId, laTraHang, headHtml}].
// CỐ Ý KHÔNG phân loại mã ở đây: luật nhận diện (class order-id/return-id → nhãn → vị trí) nằm ở C#
// (TraHangParser) — test được, và dòng nào trượt luật thì C# log NGUYÊN VĂN html để lộ cấu trúc thật.
// shopeeOrderId THƯỜNG RỖNG: href dòng trả hàng là /portal/sale/return/<returnId> chứ không phải
// /portal/sale/order/<id>. Không sao — C# ghép cặp chỉ bằng headHtml; đừng đổi regex sang bắt /return/(\d+)
// rồi nhét return-id vào field tên "orderId" (sai ngữ nghĩa).
//
// laTraHang = href trỏ /portal/sale/return/… ⇒ dòng TRẢ HÀNG thật. Dòng ĐƠN HỦY trỏ /portal/sale/order/… và
// KHÔNG có khối mã yêu cầu. Đây là chốt chặn THỨ HAI, độc lập với việc chọn được tab hay không (tab-strip nhận
// theo TEXT nên vẫn có thể trượt). CỐ Ý VẪN GỬI dòng đơn hủy kèm cờ false thay vì lọc câm ở đây — C# đếm và
// log được "bỏ k dòng vì là đơn hủy"; lọc ở JS thì con số đó biến mất khỏi nhật ký.
export function pageScanReturnRows(maxRows, maxHtml) {
  const rows = document.querySelectorAll(".return-table-content a.return-row-item");
  const out = [];
  for (const row of rows) {
    if (out.length >= maxRows) break;
    try {
      let shopeeOrderId = "";
      const href = row.getAttribute("href") || "";
      const hm = href.match(/\/portal\/sale\/order\/(\d+)/);
      if (hm) shopeeOrderId = hm[1];
      const laTraHang = /\/portal\/sale\/return\//.test(href);
      const head = row.querySelector(".return-row-item-head");
      const src = head || row; // không có head → gửi cả dòng (C# vẫn tách được)
      // Bỏ <img>/<svg> — avatar người mua có khi là data URI base64 (>1000 ký tự) và 2 icon copy mỗi cái ~450 ký
      // tự path: chúng đẩy head chạm trần maxHtml và cắt mất khối return-id nằm CUỐI head, mất mã yêu cầu ÂM
      // THẦM. Phải cloneNode TRƯỚC khi xoá: xoá trên DOM thật sẽ làm mất ảnh trên màn hình người dùng đang xem.
      let html = "";
      try {
        const clone = src.cloneNode(true);
        for (const rac of clone.querySelectorAll("img, svg")) rac.remove();
        html = clone.outerHTML;
      } catch (e) { html = src.outerHTML; } // clone lỗi (node lạ) → lùi về bản gốc, đừng phá cả lượt quét
      if (html.length > maxHtml) html = html.substring(0, maxHtml);
      out.push({ shopeeOrderId: shopeeOrderId, laTraHang: laTraHang, headHtml: html });
    } catch (e) { /* dòng lạ — bỏ qua, không phá cả lượt */ }
  }
  return JSON.stringify(out);
}

// CHẨN ĐOÁN khi hết giờ chờ ô tổng: 4 dấu hiệu phân biệt DỨT ĐIỂM "hết giờ thật" / "đọc nhầm tab" / "sai
// selector" — trước đây chỉ có một dòng "chưa render sau 20s", nhìn vào không biết nới thời gian có ích không.
//   · coOTong=false            → selector .return-list-summary-title KHÔNG còn (Shopee đổi giao diện).
//   · coOTong=true, textRong   → ô có mà rỗng ⇒ hết giờ THẬT (Vue chưa rót số) → nới thời gian mới có nghĩa.
//   · coTabWrapper=false       → chưa vào đúng trang trả hàng (lạc trang / còn ở trang đơn).
//   · soDong>0 mà không có ô   → danh sách đã vẽ nhưng ô tổng đổi chỗ ⇒ sai selector, không phải hết giờ.
export function pageChanDoanTraHang() {
  const el = document.querySelector(".return-list-summary-title");
  return {
    url: location.href,
    title: document.title || "",
    coOTong: !!el,
    textOTong: el ? (el.textContent || "").replace(/\s+/g, " ").trim() : "",
    soDong: document.querySelectorAll("a.return-row-item").length,
    coTabWrapper: !!document.querySelector(".return-case-tab-wrapper"),
  };
}
