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
// PHẠM VI: ưu tiên ".return-case-tab-wrapper", KHÔNG còn bắt buộc (10/08/2026 — xem hai vòng dưới).
// Chốt chặn "không bấm nhầm mục tên gần giống" nay nằm ở CHÍNH regex chứ không ở phạm vi: `RETURN_TAB_RE` bắt
// buộc bắt đầu bằng "don tra hang…", nên tab L1 "Trả hàng/Hoàn tiền/Hủy" (_na → "tra hang/hoan tien/huy") và
// mục điều hướng trái đều không lọt. Đã kiểm trên markup L1 THẬT do người dùng gửi 10/08.
// Trả { daDung: true } nếu tab khớp ĐANG chọn (class "active") ⇒ caller KHÔNG bấm: bấm lại rồi ngồi chờ danh
// sách vẽ lại (mà nó không vẽ lại) là đốt thời gian mỗi shop. Khớp mà chưa active → toạ độ TÂM để trustedClick.
// null = không có tab-strip / không tab nào khớp ⇒ caller vẫn đi tiếp với tab hiện tại (kèm cảnh báo).
export function pageLocateReturnCaseTab(reSrc) {
  const re = new RegExp(reSrc);
  // HAI VÒNG, ưu tiên khung cũ. Vòng 1 = `.return-case-tab-wrapper` (giữ nguyên hành vi đã kiểm chứng). Vòng 2
  // = quét TOÀN TRANG khi khung đó không còn — Shopee đang đổi giao diện khu này (10/08/2026, trang tự bật tour
  // hướng dẫn về đúng mấy mục đó) và đòi ĐỦ CẢ khung LẪN text là hỏng cả hai đường một lúc.
  // Quét toàn trang KHÔNG lỏng tay: vẫn phải khớp `re` (bắt đầu bằng "don tra hang…"), nên tab điều hướng trái
  // "Trả hàng/Hoàn tiền/Hủy" (_na → "tra hang/hoan tien/huy", không có "don") không thể lọt.
  const KHUNG = [".return-case-tab-wrapper", ""];
  const SEL_TAB = ".eds-tabs__nav-tab, [role='tab']";

  const doc = (tab) => {
    const cls = " " + (tab.getAttribute("class") || "") + " ";
    // "active" là class của EDS; aria-selected là đường chuẩn của [role='tab'] — nhận cả hai.
    // `dangChon` = text của CHÍNH phần tử được coi là đang chọn, để C# ghi nhật ký ĐỐI CHỨNG: nhìn log là biết
    // ngay nó tưởng đang ở tab nào, khỏi phải đoán khi số liệu trông lạ.
    if (cls.indexOf(" active ") >= 0 || tab.getAttribute("aria-selected") === "true") {
      return { daDung: true, dangChon: (_na(tab.textContent) || "").slice(0, 40) };
    }
    const r0 = tab.getBoundingClientRect();
    if (!(r0.width > 0 && r0.height > 0)) return null;
    try { tab.scrollIntoView({ block: "center" }); } catch (e) {}
    const r = tab.getBoundingClientRect();
    return { daDung: false, x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) };
  };

  for (const k of KHUNG) {
    let goc;
    try { goc = k ? document.querySelectorAll(k) : [document]; } catch (e) { continue; }
    for (const wrap of goc) {
      let tabs;
      try { tabs = wrap.querySelectorAll(SEL_TAB); } catch (e) { continue; }
      for (const tab of tabs) {
        if (!re.test(_na(tab.textContent))) continue;
        const kq = doc(tab);
        if (kq) return kq;
      }
    }
  }

  // VÒNG 3 — chỉ TRONG khung `.return-case-tab-wrapper`, bỏ hẳn ràng buộc class tab.
  // Ca thật 10/08/2026: chẩn đoán báo `wrapper=co` mà KHÔNG có phần tử nào là .eds-tabs__nav-tab/[role=tab]
  // ⇒ Shopee đổi markup bên trong khung. Khung vẫn là ranh giới an toàn (chỉ chứa đúng mấy tab loại đơn), nên
  // dò theo TEXT trong đó không có nguy cơ bấm lạc như dò toàn trang.
  // Lấy phần tử KHỚP SÂU NHẤT (không có con nào cũng khớp) — tránh bấm vào cả dải bọc ngoài.
  for (const wrap of document.querySelectorAll(".return-case-tab-wrapper")) {
    let ung = null;
    let els;
    try { els = wrap.querySelectorAll("*"); } catch (e) { continue; }
    for (const el of els) {
      if (!re.test(_na(el.textContent))) continue;
      let coConKhop = false;
      for (const con of el.children) {
        if (re.test(_na(con.textContent))) { coConKhop = true; break; }
      }
      if (coConKhop) continue;
      ung = el;
      break;
    }
    if (!ung) continue;
    // "Đang chọn" có thể nằm ở chính nó HOẶC ở thẻ bọc Ô TAB (dải EDS gắn active lên thẻ bọc).
    // ⚠ CHỐT SỐNG CÒN — chỉ leo khi cha VẪN CHỈ CHỨA ĐÚNG TAB NÀY (text không đổi).
    // Lỗi 10/08/2026 vòng 07:08: leo mù 4 cấp thì chạm phải DẢI BỌC CẢ 4 TAB, mà dải đó cũng mang class
    // "active" ⇒ tab nào cũng bị coi là "đang chọn" ⇒ KHÔNG bấm, KHÔNG cảnh báo, rồi đọc số 33 của tab
    // "Tất cả" và GHI THẲNG VÀO MỐC. Dương tính giả kiểu này hại hơn trượt hẳn: trượt thì còn bỏ lượt giữ mốc,
    // còn cái này ghi số sai vào mốc, nuốt vĩnh viễn mọi yêu cầu trả hàng mới.
    const textTab = _na(ung.textContent);
    let leo = ung;
    for (let i = 0; i < 4 && leo && leo !== wrap; i++) {
      if (_na(leo.textContent) !== textTab) break; // đã trèo ra khỏi ô tab (ôm cả tab khác) → dừng
      const cls = " " + (leo.getAttribute("class") || "") + " ";
      if (cls.indexOf(" active ") >= 0 || leo.getAttribute("aria-selected") === "true") {
        return { daDung: true, dangChon: textTab.slice(0, 40) };
      }
      leo = leo.parentElement;
    }
    const kq = doc(ung);
    if (kq) return kq;
  }
  return null;
}

// CHẨN ĐOÁN khi pageLocateReturnCaseTab trả null: liệt kê MỌI phần tử trông như tab trên trang (text đã chuẩn
// hoá + class rút gọn) để lượt chạy thật lộ markup THẬT, thay vì ngồi đoán Shopee đổi gì.
// Trả chuỗi gọn; trần 12 mục để không đẩy nguyên trang qua WebSocket vào ô nhật ký.
export function pageChanDoanTabTraHang(maxHtml) {
  const phan = [];
  const khung = document.querySelector(".return-case-tab-wrapper");
  phan.push("wrapper=" + (khung ? "co" : "KHONG"));
  let dem = 0;
  for (const tab of document.querySelectorAll(".eds-tabs__nav-tab, [role='tab']")) {
    const r = tab.getBoundingClientRect();
    if (!(r.width > 0 && r.height > 0)) continue;
    if (dem++ >= 12) { phan.push("…"); break; }
    const cls = (tab.getAttribute("class") || "").split(/\s+/).slice(0, 2).join(".");
    phan.push('"' + (_na(tab.textContent) || "").slice(0, 40) + '" [' + cls + "]");
  }
  if (dem === 0) { phan.push("KHONG co phan tu nao trong nhu tab"); }

  // Khung CÓ mà không tab nào khớp ⇒ Shopee đổi markup bên trong. Đổ HTML rút gọn của ĐÚNG khung đó ra để lượt
  // sau biết chính xác phải dò theo gì, thay vì lại một vòng đoán nữa.
  if (khung) {
    let html = "";
    try {
      const clone = khung.cloneNode(true);
      for (const rac of clone.querySelectorAll("img, svg")) rac.remove();
      html = clone.outerHTML || "";
    } catch (e) { html = khung.outerHTML || ""; }
    const tran = maxHtml || 2000;
    phan.push("HTML khung: " + (html.length > tran ? html.substring(0, tran) : html));
  }
  return phan.join(" · ");
}

// Text ô tổng ".return-list-summary-title" (vd "7 Yêu cầu"). "" nếu chưa render — C# lo parse ra SỐ.
export function pageReturnSummaryText() {
  const el = document.querySelector(".return-list-summary-title");
  return el ? (el.textContent || "").replace(/\s+/g, " ").trim() : "";
}

// Toạ độ nút mở dropdown sắp xếp (.sort-button). null nếu chưa có.
// ⚠ behavior "instant": mặc định ("auto") theo CSS `scroll-behavior`, mà trang cuộn MƯỢT thì rect đo NGAY sau
// scrollIntoView vẫn là toạ độ CŨ — cú bấm rơi xuống chỗ trống, dropdown không mở, và triệu chứng nhìn y hệt
// "sai selector". Đúng ca đã bắt được 10/08/2026: nút tab (nằm sát đỉnh trang, không phải cuộn) thì bấm được,
// còn nút sắp xếp (phải cuộn xuống) thì không. Chỗ gọi CÒN đo lại lần hai sau một nhịp nghỉ, xem flow-returns.
// Toạ độ nút mở dropdown sắp xếp thứ <idx> (0 = ứng viên tốt nhất). null nếu không có ứng viên nào ở vị trí đó.
// Ứng viên xếp ƯU TIÊN: nút nằm trong ô tổng ".return-list-summary" (đúng chỗ trên trang thật) trước, rồi mới
// tới các .sort-button khác; chỉ lấy nút có kích thước thật. Trả kèm `tong` = số ứng viên, để chỗ gọi biết còn
// nút nào để thử nữa không.
//
// ⚠⚠ HÀM NÀY BỊ BƠM VÀO TRANG (chrome.scripting.executeScript, world MAIN) nên phải TỰ CHỨA: chỉ được dùng
// biến toàn cục do pageInstallHelpers bơm sẵn (_na / _provCore), TUYỆT ĐỐI không gọi hàm cấp module trong file
// này — hàm bơm đi chỉ mang theo THÂN của chính nó, phạm vi module ở lại đây. Đã vấp đúng 10/08/2026: tách phần
// gom ứng viên ra hàm `_nutSapXep()` cho gọn ⇒ mỗi lượt gọi ném "Uncaught ReferenceError: _nutSapXep is not
// defined" (chỉ thấy trong console của TRANG, không lộ ra nhật ký app) ⇒ bước sắp xếp chết câm ở 5/5 shop.
//
// ⚠ behavior "instant": mặc định ("auto") theo CSS `scroll-behavior`, mà trang cuộn MƯỢT thì rect đo NGAY sau
// scrollIntoView vẫn là toạ độ CŨ — cú bấm rơi xuống chỗ trống, dropdown không mở, và triệu chứng nhìn y hệt
// "sai selector". Chỗ gọi CÒN đo lại lần hai sau một nhịp nghỉ, xem flow-returns.
export function pageLocateSortButton(idx) {
  const trongO = [];
  const conLai = [];
  for (const el of document.querySelectorAll(".sort-button")) {
    const r0 = el.getBoundingClientRect();
    if (!(r0.width > 0 && r0.height > 0)) continue;
    (el.closest(".return-list-summary") ? trongO : conLai).push(el);
  }
  const ds = trongO.concat(conLai);
  const el = ds[idx || 0];
  if (!el) return null;
  try { el.scrollIntoView({ block: "center", behavior: "instant" }); }
  catch (e) { try { el.scrollIntoView({ block: "center" }); } catch (e2) {} }
  const r = el.getBoundingClientRect();
  return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2), tong: ds.length };
}

// Số .eds-dropdown-menu ĐANG HIỆN (rect > 0). Dùng để biết cú bấm có mở được dropdown hay không — và để lượt
// bấm thứ hai KHÔNG bấm khi menu đang mở sẵn (bấm lúc đó là tự tay đóng lại đúng cái vừa mở ra).
export function pageDemMenuHien() {
  let hien = 0;
  for (const m of document.querySelectorAll(".eds-dropdown-menu")) {
    const r = m.getBoundingClientRect();
    if (r.width > 0 && r.height > 0) { hien++; }
  }
  return hien;
}

// CHẨN ĐOÁN khi KHÔNG đổi được sắp xếp: nút có không, có mấy .eds-dropdown-menu (hiện/tổng), và text các mục
// trong menu ĐANG HIỆN. Regex SORT_NEWEST_RE đã được kiểm là khớp đúng "Ngày yêu cầu (Mới - Cũ)" trên nhãn
// THẬT (10/08/2026), nên lỗi nằm ở "dropdown không mở" hoặc "không thấy menu" — hai thứ chỉ lượt chạy thật mới
// phân biệt được. Trần 10 mục cho gọn nhật ký.
export function pageChanDoanSapXep() {
  const phan = [];
  const tatCa = document.querySelectorAll(".sort-button");
  phan.push("sort-button tong=" + tatCa.length);
  // Liệt kê TỪNG nút kèm kích thước + có nằm trong ô tổng không: nếu trang có nhiều .sort-button thì cú bấm rất
  // dễ rơi vào nút của khối khác (trông "bấm rồi mà chẳng có gì mở"). Trần 4 nút cho gọn.
  let i = 0;
  for (const el of tatCa) {
    if (i++ >= 4) { phan.push("…"); break; }
    const r = el.getBoundingClientRect();
    phan.push("nut#" + i + " " + Math.round(r.width) + "x" + Math.round(r.height)
      + " tai(" + Math.round(r.left) + "," + Math.round(r.top) + ")"
      + " trong-o-tong=" + (el.closest(".return-list-summary") ? "CO" : "khong"));
  }

  const menus = document.querySelectorAll(".eds-dropdown-menu");
  let hien = 0;
  const muc = [];
  for (const m of menus) {
    const r = m.getBoundingClientRect();
    if (!(r.width > 0 && r.height > 0)) continue;
    hien++;
    for (const li of m.querySelectorAll("li, [role='menuitem']")) {
      if (muc.length >= 10) break;
      muc.push('"' + (_na(li.textContent) || "").slice(0, 40) + '"');
    }
  }
  phan.push("menu tong=" + menus.length + " hien=" + hien);
  phan.push(muc.length ? "muc: " + muc.join(", ") : "KHONG co muc nao trong menu dang hien");

  // Vòng 09:44 ra đúng "menu tong=2 hien=0": menu CÓ trong DOM nhưng không cái nào mở. Để lượt sau khỏi phải
  // đoán tiếp "menu nào là menu sắp xếp, nó bị ẩn bằng gì", mô tả từng menu ẨN: cách bị ẩn (display/visibility/
  // opacity), lớp của chính nó và của cha, và nó CÓ chứa mục cần bấm hay không.
  let soMenuAn = 0;
  for (const m of menus) {
    const r = m.getBoundingClientRect();
    if (r.width > 0 && r.height > 0) continue;
    soMenuAn++;
    let cs = null;
    try { cs = getComputedStyle(m); } catch (e) { cs = null; }
    const cha = m.parentElement;
    phan.push("menu-an#" + soMenuAn
      + " display=" + (cs ? cs.display : "?")
      + " visibility=" + (cs ? cs.visibility : "?")
      + " opacity=" + (cs ? cs.opacity : "?")
      + " class=[" + ((m.getAttribute("class") || "").slice(0, 60)) + "]"
      + " cha=[" + (cha ? (cha.getAttribute("class") || cha.tagName || "").slice(0, 60) : "khong co") + "]"
      + " co-muc-ngay-yeu-cau=" + (/ngay yeu cau/.test(_na(m.textContent)) ? "CO" : "khong"));
  }
  return phan.join(" · ");
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
