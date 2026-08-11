// ---- Hàm chạy trong trang (world MAIN) ------------------------------------
// (Port từ ScanShopListJs / OpenShopDetailAsync / FindToShipTitleAsync / SubUserSelectors... phía C#.)
// LƯU Ý: mỗi hàm world:MAIN được serialize độc lập → PHẢI tự chứa, không tham chiếu helper ngoài.
// (Tách khỏi background.js 2026-08-06 — thân hàm GIỮ NGUYÊN TỪNG KÝ TỰ, chỉ thêm `export`.)
// _na/_provCore ở đây KHÔNG chạy trong service worker: chúng chỉ là bản gốc để đọc: khi page-func được bơm
// vào tab, lời gọi bare `_na(...)`/`_provCore(...)` resolve về window._na/window._provCore do
// exec.js/pageInstallHelpers cài trước mỗi lượt. ĐỪNG gọi chúng ở SW, và ĐỪNG đổi tên.

// Quét bảng shop → JSON mảng [{rowKey,name,login}] (đúng khuôn ScanShopListJs).
export function pageScanShopList() {
  const norm = (s) => (s || "").replace(/\s+/g, " ").trim();
  const rows = document.querySelectorAll("tr[data-row-key]");
  const out = [];
  for (const row of rows) {
    try {
      const rowKey = row.getAttribute("data-row-key") || "";
      const nameEl = row.querySelector("span[class*='shop-name-text']");
      const name = nameEl ? norm(nameEl.textContent) : "";
      let login = "";
      const tds = row.querySelectorAll("td");
      if (tds.length >= 2) {
        const span = tds[1].querySelector("span");
        login = norm(span ? span.textContent : tds[1].textContent);
      }
      out.push({ rowKey: rowKey, name: name, login: login });
    } catch (e) { /* dòng lạ — bỏ qua */ }
  }
  return JSON.stringify(out);
}

// Cuộn nút "Chi tiết" của shop vào giữa màn (để toạ độ click ổn định).
export function pageScrollDetailIntoView(shopId) {
  const row = document.querySelector("tr[data-row-key='" + shopId + "']");
  if (!row) return false;
  const cands = row.querySelectorAll("button, a, [role='button']");
  for (const b of cands) {
    const t = (b.textContent || "").replace(/\s+/g, " ").trim().toLowerCase();
    if (t.includes("chi tiết") || t.includes("chi tiet") || t === "detail") {
      try { b.scrollIntoView({ block: "center" }); } catch (e) {}
      return true;
    }
  }
  return false;
}

// Đọc toạ độ TÂM nút "Chi tiết" (sau khi đã cuộn) → {x,y} hoặc null.
export function pageLocateDetailRect(shopId) {
  const row = document.querySelector("tr[data-row-key='" + shopId + "']");
  if (!row) return null;
  const cands = row.querySelectorAll("button, a, [role='button']");
  for (const b of cands) {
    const t = (b.textContent || "").replace(/\s+/g, " ").trim().toLowerCase();
    if (t.includes("chi tiết") || t.includes("chi tiet") || t === "detail") {
      const r = b.getBoundingClientRect();
      return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) };
    }
  }
  return null;
}

// Đọc số "Chờ Lấy Hàng" từ to-do box → text ô .item-title (đúng khuôn FindToShipTitleAsync). null nếu chưa có.
export function pageReadToShip() {
  const norm = (s) => (s || "").replace(/\s+/g, " ").trim();
  const items = document.querySelectorAll(".to-do-box-item");
  for (const item of items) {
    const desc = item.querySelector(".item-desc");
    if (!desc) continue;
    if (norm(desc.textContent).toLowerCase() === "chờ lấy hàng") {
      const title = item.querySelector(".item-title");
      if (title) return norm(title.textContent);
    }
  }
  const fb = document.querySelector("a[href*='type=toship'][href*='to_process'] .item-title");
  if (fb) return norm(fb.textContent);
  return null;
}

// ===== GĐ3 — page-func đọc + xử đơn (port ScanOrdersJs / ShopeeShippingNav; tự chứa world MAIN) =====

// Chuẩn hoá text KHÔNG dấu (mirror ShopeeShippingNav.NormalizeUiText + bỏ dấu) — helper tái dùng trong nhiều page-func.
export function _na(s) {
  const nf = (s || "").replace(/\s+/g, " ").trim().toLowerCase().normalize("NFD");
  let out = "";
  for (const ch of nf) {
    const c = ch.charCodeAt(0);
    if (c >= 0x300 && c <= 0x36f) continue; // bỏ dấu thanh (combining marks)
    out += ch === "đ" ? "d" : ch;
  }
  return out;
}

// Quét MỌI card đơn của trang hiện tại → JSON.stringify(mảng đơn). Port NGUYÊN ScanOrdersJs (ShopeeLoginService.cs).
export function pageScanOrders() {
  const norm = (s) => (s || "").replace(/\s+/g, " ").trim();
  const cards = document.querySelectorAll("a[data-testid='order-item']");
  const out = [];
  for (const card of cards) {
    try {
      const snEl = card.querySelector(".order-sn");
      const snRaw = snEl ? norm(snEl.textContent) : "";
      const snTokens = snRaw.split(" ");
      const orderSn = snTokens.length ? snTokens[snTokens.length - 1] : "";

      let shopeeOrderId = "";
      const href = card.getAttribute("href") || "";
      const hm = href.match(/\/portal\/sale\/order\/(\d+)/);
      if (hm) shopeeOrderId = hm[1];

      const buyerEl = card.querySelector(".buyer-username");
      const buyer = buyerEl ? norm(buyerEl.textContent) : "";

      const items = [];
      for (const it of card.querySelectorAll(".item")) {
        try {
          const nameEl = it.querySelector(".item-name");
          const descEl = it.querySelector(".item-description");
          const amtEl = it.querySelector(".item-amount");
          const imgEl = it.querySelector(".item-image");
          const name = nameEl ? norm(nameEl.textContent) : "";
          let variation = descEl ? norm(descEl.textContent) : "";
          variation = variation.replace(/^Variation\s*:?\s*/i, "").trim();
          let amount = amtEl ? norm(amtEl.textContent) : "";
          amount = amount.replace(/^[x×]\s*/i, "").trim();
          let image = "";
          if (imgEl) image = imgEl.getAttribute("src") || imgEl.getAttribute("data-src") || "";
          items.push({ name, variation, amount, image });
        } catch (e) { /* item lạ — bỏ qua */ }
      }

      const totalEl = card.querySelector(".total-price");
      const totalText = totalEl ? norm(totalEl.textContent) : "";
      const payEl = card.querySelector(".payment-method");
      const payment = payEl ? norm(payEl.textContent) : "";

      const statusColEl = card.querySelector(".status-info-col");
      let status = "";
      if (statusColEl) {
        let stEl = statusColEl.querySelector(".status");
        if (!stEl) {
          for (const c of statusColEl.querySelectorAll("[class*=status]")) {
            const cls = typeof c.className === "string" ? c.className : "";
            if (cls.indexOf("status-description") >= 0 || cls.indexOf("status-info-col") >= 0) continue;
            if (norm(c.textContent)) { stEl = c; break; }
          }
        }
        status = stEl ? norm(stEl.textContent) : "";
        if (!status) {
          for (const ch of statusColEl.children) {
            const t = norm(ch.textContent);
            if (t) { status = t; break; }
          }
        }
      }

      const sdescEl = card.querySelector(".status-description");
      const statusDesc = sdescEl ? norm(sdescEl.textContent) : "";

      let cancelReason = "";
      const statusCol = card.querySelector(".status-info-col") || card;
      for (const pop of statusCol.querySelectorAll(".eds-popover__content")) {
        const raw = pop.textContent || "";
        if (raw.indexOf("Lý do hủy") >= 0) {
          cancelReason = norm(raw).replace(/^.*?Lý do hủy\s*:?\s*/, "").trim();
          break;
        }
      }

      const channelEl = card.querySelector(".maksed-channel-name");
      const channel = channelEl ? norm(channelEl.textContent) : "";
      const carrierEl = card.querySelector(".fulfilment-channel-name");
      const carrier = carrierEl ? norm(carrierEl.textContent) : "";
      const trackEl = card.querySelector(".tracking-number");
      const tracking = trackEl ? norm(trackEl.textContent) : "";

      out.push({ orderSn, shopeeOrderId, buyer, items, totalText, payment, status, statusDesc, cancelReason, channel, carrier, tracking });
    } catch (e) { /* card lạ — bỏ qua, không phá cả trang */ }
  }
  return JSON.stringify(out);
}

// Số card đơn hiện có (để chờ danh sách render/ổn định).
export function pageOrderCount() {
  return document.querySelectorAll("a[data-testid='order-item']").length;
}

// Đọc text "Số tiền cuối cùng" trên TRANG CHI TIẾT đơn. Tự chứa, world MAIN. Trả chuỗi text (rỗng nếu chưa thấy).
// THỨ TỰ ƯU TIÊN (người dùng chốt 28/07 — trước đây thẻ remote đứng đầu và hụt 1/3 số đơn):
//  1) BẢNG DOANH THU (.income-item) của TRANG CHÍNH — render CÙNG NHỊP .product-list nên đọc được NGAY, không
//     phải chờ. Bằng chứng 28/07: cùng một lượt mở tab, sản phẩm về 4/4 mà thẻ remote chỉ về 3/4.
//  2) card [type='FinalAmount'] > .amount — port NGUYÊN FinalAmountJs (ShopeeLoginService.cs). Thẻ này nằm trong
//     <div class="remote-component">, tải BẤT ĐỒNG BỘ và có hẳn nhánh fail="…renderFail…" ⇒ hụt ~1/3 số đơn.
//     GIỮ làm dự phòng (phòng khối doanh thu bị GẬP / bố cục khác).
//  3) fallback tìm phần tử text ĐÚNG "Số tiền cuối cùng" rồi lần ≤4 cấp cha tìm .amount (tránh vơ nhầm .amount đầu trang).
// Đường 2 và 3 GIỮ NGUYÊN nội dung, chỉ lùi thứ tự — chúng đang phục vụ ~2/3 số đơn, đừng gỡ.
// ⚠ BẪY của bảng doanh thu: BA dòng cùng chứa chữ "ước tính" ("Tổng phí vận chuyển ước tính", "Phí vận chuyển
// ước tính", "Doanh thu đơn hàng ước tính") ⇒ phải khớp CẢ "doanh thu" LẪN "uoc tinh", kẻo ghi nhầm PHÍ VẬN
// CHUYỂN lên Google Sheet (số sai còn tệ hơn để trống). Nhãn còn dính tooltip: textContent thô ra
// "Doanh thu đơn hàng ước tính .cls-1{fill-rule:evenodd;}question" ⇒ CLONE rồi xoá svg/i/.eds-popover mới lấy text
// (clone để KHÔNG đụng DOM thật — người dùng đang nhìn trang đó).
export function pageReadFinalAmount() {
  const norm = (s) => (s || "").replace(/\s+/g, " ").trim();

  // Đường 1: bảng doanh thu trang chính (.payment-info-details → .income-container → .income-item).
  // Bỏ dấu tại chỗ theo ĐÚNG cách của _na (mã ký tự, không regex): hàm này CỐ Ý tự chứa — nó là đường đọc chính,
  // không được phụ thuộc pageInstallHelpers có chạy được hay không.
  const boDau = (s) => {
    const nf = norm(s).toLowerCase().normalize("NFD");
    let out = "";
    for (const ch of nf) {
      const c = ch.charCodeAt(0);
      if (c >= 0x300 && c <= 0x36f) continue; // bỏ dấu thanh (combining marks)
      out += ch === "đ" ? "d" : ch;
    }
    return out;
  };
  let theoBang = "";
  for (const item of document.querySelectorAll(".income-item")) {
    const labEl = item.querySelector(".income-label-text");
    const valEl = item.querySelector(".income-value");
    if (!labEl || !valEl) continue;
    let nhan;
    try {
      const clone = labEl.cloneNode(true);
      for (const rac of clone.querySelectorAll("svg, i, .eds-popover")) rac.remove();
      nhan = boDau(clone.textContent);
    } catch (e) { nhan = boDau(labEl.textContent); }
    if (nhan.indexOf("doanh thu") < 0 || nhan.indexOf("uoc tinh") < 0) continue; // ⚠ phải có CẢ HAI
    const giaTri = norm(valEl.textContent);
    if (!giaTri) continue;
    if (item.classList && item.classList.contains("highlighted")) return giaTri; // dòng chốt (tô đậm) → lấy ngay
    if (!theoBang) theoBang = giaTri;
  }
  if (theoBang) return theoBang;

  // Đường 2 (dự phòng): thẻ remote.
  const card = document.querySelector("[type='FinalAmount']");
  if (card) {
    const amt = card.querySelector(".amount");
    if (amt) return norm(amt.textContent);
  }
  // Đường 3 (dự phòng): neo theo chữ "Số tiền cuối cùng".
  const nodes = document.querySelectorAll("div, span, p");
  for (const t of nodes) {
    if (norm(t.textContent) === "Số tiền cuối cùng") {
      let p = t.parentElement;
      for (let up = 0; up < 4 && p; up++, p = p.parentElement) {
        const amt = p.querySelector(".amount");
        if (amt) return norm(amt.textContent);
      }
    }
  }
  return "";
}

// CHẨN ĐOÁN cho log phía C# (đơn nào hụt vì lý do gì / bảng doanh thu đang cứu được bao nhiêu đơn). Gọi ĐÚNG MỘT
// LẦN sau khi vòng poll đã kết thúc — KHÔNG dự phần vào việc quyết định chờ (trần poll vẫn là 15s như cũ).
// Trả {coThe, theCoSo} = thẻ [type='FinalAmount'] có mặt chưa / .amount của nó đã có nội dung chưa.
export function pageChanDoanUocTinh() {
  const card = document.querySelector("[type='FinalAmount']");
  const amt = card ? card.querySelector(".amount") : null;
  return {
    coThe: !!card,
    theCoSo: !!(amt && (amt.textContent || "").replace(/\s+/g, " ").trim()),
  };
}

// Đọc DANH SÁCH SẢN PHẨM trên TRANG CHI TIẾT đơn. Chạy trong CHÍNH tab chi tiết doSyncOrderFinals đã mở sẵn để
// lấy "Số tiền cuối cùng" ⇒ KHÔNG thêm lượt mở trang nào. Trả JSON mảng
// [{stt, ten, phanLoai, sku, donGia, soLuong, thanhTien, anh, metaLa}]; không có .product-list → "[]" (KHÔNG ném).
// BA CÁI BẪY của HTML này — đừng gỡ nếu chưa có HTML mới:
//  1) Dòng TIÊU ĐỀ mang CẢ class product-list-item (class="product-list-item product-list-head") ⇒ phải loại
//     .product-list-head, kẻo có một "sản phẩm" tên "Sản phẩm" giá "Đơn Giá".
//  2) Nhãn "SKU phân loại" CHỨA chuỗi "phân loại" ⇒ xét nhãn SKU TRƯỚC, không thì SKU chui vào ô phân loại.
//  3) Tên SP ở thuộc tính title (text bên trong dính <!----> của Vue); ảnh là background-image trong style,
//     KHÔNG phải <img src>.
// donGia/soLuong/thanhTien trả TEXT THÔ — C# parse số (test được, đúng nếp soYeuCauText của bước trả hàng).
// Vượt trần maxItems → cắt + gắn cờ bicat trên dòng CUỐI để C# log (đừng im lặng nuốt sản phẩm).
export function pageReadOrderProducts(maxItems) {
  const norm = (s) => (s || "").replace(/\u00A0/g, " ").replace(/\s+/g, " ").trim();
  const tran = maxItems > 0 ? maxItems : 20;
  const rows = document.querySelectorAll(".product-list .product-list-item:not(.product-list-head)");
  const out = [];
  for (const row of rows) {
    if (out.length >= tran) {
      if (out.length) out[out.length - 1].bicat = true;
      break;
    }
    try {
      const txt = (sel) => { const el = row.querySelector(sel); return el ? norm(el.textContent) : ""; };

      const nameEl = row.querySelector(".product-name");
      let ten = nameEl ? norm(nameEl.getAttribute("title")) : "";
      if (!ten && nameEl) ten = norm(nameEl.textContent);

      let phanLoai = "", sku = "";
      const metaLa = [];
      for (const d of row.querySelectorAll(".product-meta > div")) {
        const raw = norm(d.textContent);
        if (!raw) continue;
        const ci = raw.indexOf(":");
        const nhan = (ci >= 0 ? raw.substring(0, ci) : raw).toLowerCase();
        const giaTri = ci >= 0 ? raw.substring(ci + 1).trim() : "";
        if (nhan.indexOf("sku") >= 0) { if (!sku) sku = giaTri; }                          // BẪY 2: SKU xét TRƯỚC
        else if (nhan.indexOf("phân loại") >= 0 || nhan.indexOf("phan loai") >= 0 || nhan.indexOf("variation") >= 0) {
          if (!phanLoai) phanLoai = giaTri;
        }
        else metaLa.push(raw); // nhãn lạ → gửi NGUYÊN VĂN để C# log, đừng đoán bừa
      }

      let anh = "";
      const imgEl = row.querySelector(".product-image");
      if (imgEl) {
        const bg = (imgEl.style && imgEl.style.backgroundImage) || "";
        const m = bg.match(/url\((['"]?)(.*?)\1\)/);
        if (m) anh = m[2];
      }

      out.push({
        stt: txt(".no"), ten: ten, phanLoai: phanLoai, sku: sku,
        donGia: txt(".price"), soLuong: txt(".qty"), thanhTien: txt(".subtotal"),
        anh: anh, metaLa: metaLa,
      });
    } catch (e) { /* dòng lạ — bỏ qua, không phá cả đơn */ }
  }
  return JSON.stringify(out);
}

// Ký hiệu danh sách hiện tại: "<số card>|<mã đơn card đầu>" — phát hiện trang ĐỔI sau khi bấm trang sau.
export function pageListSignature() {
  const cards = document.querySelectorAll("a[data-testid='order-item']");
  let first = "";
  if (cards.length) {
    const sn = cards[0].querySelector(".order-sn");
    first = sn ? (sn.textContent || "").replace(/\s+/g, " ").trim() : "";
  }
  return cards.length + "|" + first;
}

// Nút "trang sau" còn DÙNG ĐƯỢC (có box, không disabled) → toạ độ; port FindNextPageButtonAsync + IsUsableNextButtonAsync.
// ⚠ behavior "instant": mặc định ("auto") theo CSS `scroll-behavior`, mà trang cuộn MƯỢT thì rect đo NGAY sau
// scrollIntoView vẫn là toạ độ CŨ ⇒ cú bấm rơi xuống chỗ trống. Đúng thủ phạm đã kết luận cho nút sắp xếp
// (pageLocateSortButton), mà pager còn dính nặng hơn: nó LUÔN nằm đáy trang nên lần nào cũng phải cuộn. Triệu
// chứng nhìn y hệt "hết trang" ⇒ mất sạch đơn từ trang 2 trở đi, không một dòng log.
// Chỗ gọi CÒN đo lại lần hai sau một nhịp nghỉ — xem `timNutTrangSau` trong pager.js.
export function pageFindNextPage() {
  const sels = [
    ".eds-pager button.eds-pager__button-next",
    "li.eds-pager__next button",
    "button[class*='next']",
    "[class*='pager'] button:last-of-type",
  ];
  for (const sel of sels) {
    let els;
    try { els = document.querySelectorAll(sel); } catch (e) { continue; }
    for (const el of els) {
      const r0 = el.getBoundingClientRect();
      if (!(r0.width > 0 && r0.height > 0)) continue;
      if (el.disabled) continue;
      if (el.getAttribute("aria-disabled") === "true") continue;
      const cls = (el.getAttribute("class") || "").toLowerCase();
      if (cls.split(/\s+/).some((c) => c.indexOf("disabled") >= 0)) continue;
      try { el.scrollIntoView({ block: "center", behavior: "instant" }); }
      catch (e) { try { el.scrollIntoView({ block: "center" }); } catch (e2) {} }
      const r = el.getBoundingClientRect();
      return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) };
    }
  }
  return null;
}

// Tìm phần tử khớp text (chuẩn hoá KHÔNG dấu) trong danh sách selector → toạ độ TÂM (đã cuộn vào giữa). null nếu không thấy.
export function pageLocateByText(selectors, reSrc) {
  const re = new RegExp(reSrc);
  for (const sel of selectors) {
    let els;
    try { els = document.querySelectorAll(sel); } catch (e) { continue; }
    for (const el of els) {
      const t = _na(el.textContent);
      if (t && re.test(t)) {
        const r0 = el.getBoundingClientRect();
        if (r0.width > 0 && r0.height > 0) {
          try { el.scrollIntoView({ block: "center" }); } catch (e) {}
          const r = el.getBoundingClientRect();
          return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) };
        }
      }
    }
  }
  return null;
}

// Chẩn đoán: liệt kê text các phần tử bấm được (visible, ≤40 ký tự) — để soi nhãn thật khi không khớp. Tự chứa.
export function pageDumpClickables() {
  const norm = (s) => (s || "").replace(/\s+/g, " ").trim();
  const out = [];
  const seen = new Set();
  const els = document.querySelectorAll("a, button, [role='button'], [role='menuitem'], span.entry-text, .entry, li, .nav-item");
  for (const el of els) {
    const r = el.getBoundingClientRect();
    if (r.width <= 0 || r.height <= 0) continue;
    const t = norm(el.textContent);
    if (!t || t.length > 40 || seen.has(t)) continue;
    seen.add(t);
    out.push(t);
    if (out.length >= 40) break;
  }
  return JSON.stringify(out);
}

// Đơn ĐẦU có nút "Chuẩn bị hàng" (IsPrepareOrderButtonText) → {x,y,orderCode}. null nếu không còn.
export function pageFindPrepareOrder() {
  const cards = document.querySelectorAll("a[data-testid='order-item']");
  for (const card of cards) {
    for (const b of card.querySelectorAll("button, [role='button'], a")) {
      if (_na(b.textContent) === "chuan bi hang") {
        const r0 = b.getBoundingClientRect();
        if (!(r0.width > 0 && r0.height > 0)) continue;
        const snEl = card.querySelector(".order-sn");
        const snRaw = snEl ? (snEl.textContent || "").replace(/\s+/g, " ").trim() : "";
        const toks = snRaw.split(" ");
        const orderCode = toks.length ? toks[toks.length - 1] : "";
        try { b.scrollIntoView({ block: "center" }); } catch (e) {}
        const r = b.getBoundingClientRect();
        return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2), orderCode: orderCode };
      }
    }
  }
  return null;
}

// Định vị nút "In phiếu giao" NGAY trong card của đơn có mã = orderSn (đơn đã Chuẩn bị hàng thường có sẵn nút này).
// → {found, hasPrint, x, y}. found=false nghĩa card không ở trang này (caller tìm trang khác). found=true & hasPrint=false
// nghĩa thấy card nhưng chưa có nút In phiếu (chưa chuẩn bị hàng). Tự chứa world MAIN (dùng _na global qua execInTab).
export function pageFindPrintInCardBySn(orderSn) {
  const norm = (s) => (s || "").replace(/\s+/g, " ").trim();
  const cards = document.querySelectorAll("a[data-testid='order-item']");
  for (const card of cards) {
    const snEl = card.querySelector(".order-sn");
    const snRaw = snEl ? norm(snEl.textContent) : "";
    const toks = snRaw.split(" ");
    const sn = toks.length ? toks[toks.length - 1] : "";
    if (sn !== orderSn) continue;
    for (const b of card.querySelectorAll("button, [role='button'], a")) {
      if (_na(b.textContent) === "in phieu giao") {
        const r0 = b.getBoundingClientRect();
        if (!(r0.width > 0 && r0.height > 0)) continue;
        try { b.scrollIntoView({ block: "center" }); } catch (e) {}
        const r = b.getBoundingClientRect();
        return { found: true, hasPrint: true, x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) };
      }
    }
    return { found: true, hasPrint: false, x: 0, y: 0 }; // thấy card nhưng không có nút In phiếu
  }
  return { found: false, hasPrint: false, x: 0, y: 0 }; // card không ở trang này
}

// True nếu có modal (.eds-modal__box) hiển thị với .title khớp reSrc (chuẩn hoá không dấu).
export function pageModalHasTitle(reSrc) {
  const re = new RegExp(reSrc);
  for (const box of document.querySelectorAll(".eds-modal__box")) {
    const r = box.getBoundingClientRect();
    if (!(r.width > 0 && r.height > 0)) continue;
    // Tiêu đề modal CHUẨN là .eds-modal__title (KHÔNG phải .title — .title đầu tiên thường là order-sn/logo).
    const title = box.querySelector(".eds-modal__title") || box.querySelector(".title");
    if (title && re.test(_na(title.textContent))) return true;
  }
  return false;
}

// True nếu có BẤT KỲ modal (.eds-modal__box) nào đang hiển thị — không quan tâm tiêu đề. Dùng làm chốt chặn
// TRƯỚC khi bấm lại một toạ độ đã đọc từ trước: modal đang mở thì cú bấm đó rơi vào mask/nút trong modal.
export function pageAnyModalVisible() {
  for (const box of document.querySelectorAll(".eds-modal__box")) {
    const r = box.getBoundingClientRect();
    if (r.width > 0 && r.height > 0) return true;
  }
  return false;
}

// Tìm nút ĐÓNG của modal CHẮN — modal đang hiển thị mà KHÔNG phải modal ta đang chờ (exceptTitleReSrc).
// Shopee hay bật thông báo (đổi chính sách/tính năng mới) đè lên trang Cài đặt vận chuyển; mask của nó nuốt mọi
// trusted click nên bước đặt địa chỉ fail OAN. Trả {x,y,title,label} của nút bấm được, hoặc null.
// Ưu tiên nút CHỮ ở footer (Đồng ý/OK/Xác nhận/Đã hiểu/Bỏ qua/Để sau/Đóng), sau đó mới tới nút ✕
// (.eds-modal__close) — bấm nút chữ là ý người dùng thật, ✕ chỉ là đường lui.
// ⚠ Modal "Sửa Địa chỉ" của chính flow CŨNG là .eds-modal__box: caller PHẢI truyền exceptTitleReSrc
// "^sua dia chi$", nếu không hàm này sẽ bấm đóng đúng cái modal flow đang dùng.
// ⚠ KHỐI CHẮN KHÔNG CHỈ LÀ MODAL: tour hướng dẫn của Shopee (`.on-boarding` + `.eds-popover`, nút "Đã hiểu")
// phủ nguyên trang bằng lớp highlight và nuốt click y hệt, nhưng KHÔNG có `.eds-modal__box` nào. Ca thật
// 10/08/2026: trang trả hàng bật tour "Đơn Trả hàng/Hoàn tiền, Đơn Hủy và Đơn Giao không thành công" ngay sau
// khi đóng modal điều khoản ⇒ cú bấm chọn tab và cú đổi sắp xếp trượt im lặng ⇒ bỏ lượt check. Nên phải quét
// CẢ hai loại khối.
// TỰ CHỨA (world MAIN serialize ĐỘC LẬP): mọi hằng/regex khai báo TRONG hàm, chỉ gọi bare _na.
export function pageLocateBlockingModalButton(exceptTitleReSrc) {
  const NHAN_DONG = /^(dong y|ok|xac nhan|da hieu|toi da hieu|toi biet roi|da biet|bo qua|de sau|dong|tiep tuc|hoan tat)$/;
  const SEL_NUT = [".eds-modal__footer button", ".eds-modal__footer [role='button']", "button", "[role='button']", "a"];
  // HẸP có chủ đích: [class*='close'] trần từng ở đây là quá rộng — querySelector trả phần tử ĐẦU theo thứ tự
  // DOM (KHÔNG theo thứ tự selector) nên một <div class="closed-…"> trang trí cũng trúng, và ta bấm mù vào tâm
  // nó. Flow chạy TIẾP trên chính trang đó, nên một cú click lạc gây điều hướng sẽ đẻ ra đúng cái "lỗi địa chỉ
  // oan" mà hàm này sinh ra để dập.
  const SEL_X = ".eds-modal__close, .eds-icon-close, [aria-label='Close'], [class*='eds-modal__close']";

  let except = null;
  if (exceptTitleReSrc) { try { except = new RegExp(exceptTitleReSrc); } catch (e) { except = null; } }

  const toaDo = (el, t) => {
    try { el.scrollIntoView({ block: "center" }); } catch (e) {}
    const r = el.getBoundingClientRect();
    return {
      x: Math.round(r.left + r.width / 2),
      y: Math.round(r.top + r.height / 2),
      title: t || "",
      // Cắt 40: nhãn chỉ để ghi nhật ký. Dò trúng phần tử bọc thì textContent là CẢ modal — đẩy nguyên khối
      // đó qua WebSocket vào ô nhật ký là rác.
      label: (_na(el.textContent) || "x").slice(0, 40),
    };
  };

  for (const box of document.querySelectorAll(".eds-modal__box, .on-boarding")) {
    const clsBox = typeof box.className === "string" ? box.className : "";
    const laTour = clsBox.indexOf("on-boarding") >= 0;
    const rb = box.getBoundingClientRect();
    // Khung `.on-boarding` thường RỖNG về kích thước (mọi con định vị absolute) → rect 0×0. Lấy nó làm điều
    // kiện hiển thị là loại nhầm cả tour. Với tour, thứ "thấy được" là cái nút bên trong — vòng dưới đã kiểm
    // rect của TỪNG nút rồi nên không mất chốt chặn nào.
    if (!laTour && !(rb.width > 0 && rb.height > 0)) continue;
    const titleEl = box.querySelector(".eds-modal__title") || box.querySelector(".title");
    const t = titleEl ? _na(titleEl.textContent) : "";
    if (except && except.test(t)) continue; // modal flow đang chờ — TUYỆT ĐỐI không đóng

    // 1) Nút CHỮ (footer trước, rồi cả hộp).
    for (const sel of SEL_NUT) {
      let els;
      try { els = box.querySelectorAll(sel); } catch (e) { continue; }
      for (const el of els) {
        if (el.disabled) continue;
        if (!NHAN_DONG.test(_na(el.textContent))) continue;
        const r0 = el.getBoundingClientRect();
        if (!(r0.width > 0 && r0.height > 0)) continue;
        return toaDo(el, t);
      }
    }

    // 2) Nút ✕ — đường lui khi modal không có nút chữ nào.
    let nutX = null;
    try { nutX = box.querySelector(SEL_X); } catch (e) { nutX = null; }
    if (nutX) {
      const r0 = nutX.getBoundingClientRect();
      if (r0.width > 0 && r0.height > 0) return toaDo(nutX, t);
    }
  }
  return null;
}

// Tìm Ô TICK BẮT BUỘC của modal CHẮN — ô phải tick thì nút xác nhận mới hết khoá.
// Ca thật (10/08/2026): modal "Điều khoản - Điều kiện" (TosModal của Shopee) có
// `<button class="... disabled" disabled>Đồng ý</button>` và một `label.eds-checkbox`
// "Tôi xác nhận đã đọc, hiểu và đồng ý...". pageLocateBlockingModalButton BỎ QUA nút disabled, modal lại KHÔNG
// có nút ✕ → hàm đó trả null ⇒ dongModalChan tưởng "không có modal nào" ⇒ modal nằm lì chắn cả trang, mọi
// trusted click sau đó rơi vào mask ⇒ đặt địa chỉ fail OAN (đúng triệu chứng "Lỗi địa chỉ" của người dùng).
// CHỈ trả ô tick khi modal đó CÓ nút xác nhận ĐANG BỊ KHOÁ — cố ý hẹp: modal nào nút đã bấm được thì không
// đụng tới ô tick nào của nó (không tự tick hộ những ô không cần thiết, vd "đăng ký nhận tin").
// Toạ độ trả về là TÂM CỦA LABEL chứ không phải input: EDS giấu input thật, phần bấm được là label/indicator
// (cùng cách pageFirstUncheckedBox đang làm ở modal Sửa Địa chỉ).
// TỰ CHỨA (world MAIN serialize ĐỘC LẬP): mọi hằng/regex khai báo TRONG hàm, chỉ gọi bare _na.
export function pageLocateBlockingModalCheckbox(exceptTitleReSrc) {
  const NHAN_DONG = /^(dong y|ok|xac nhan|da hieu|toi da hieu|toi biet roi|da biet|bo qua|de sau|dong|tiep tuc|hoan tat)$/;
  const SEL_NUT = [".eds-modal__footer button", ".eds-modal__footer [role='button']", "button", "[role='button']"];

  let except = null;
  if (exceptTitleReSrc) { try { except = new RegExp(exceptTitleReSrc); } catch (e) { except = null; } }

  const biKhoa = (el) => {
    if (el.disabled === true) return true;
    const cls = typeof el.className === "string" ? el.className : "";
    return cls.split(/\s+/).indexOf("disabled") >= 0 || el.getAttribute("aria-disabled") === "true";
  };

  for (const box of document.querySelectorAll(".eds-modal__box")) {
    const rb = box.getBoundingClientRect();
    if (!(rb.width > 0 && rb.height > 0)) continue;
    const titleEl = box.querySelector(".eds-modal__title") || box.querySelector(".title");
    const t = titleEl ? _na(titleEl.textContent) : "";
    if (except && except.test(t)) continue; // modal flow đang chờ — TUYỆT ĐỐI không đụng

    // Có nút xác nhận nào ĐANG BẤM ĐƯỢC không? Có → modal này không bị khoá, khỏi tick.
    let coNutKhoa = false;
    let coNutMo = false;
    for (const sel of SEL_NUT) {
      let els;
      try { els = box.querySelectorAll(sel); } catch (e) { continue; }
      for (const el of els) {
        if (!NHAN_DONG.test(_na(el.textContent))) continue;
        const r0 = el.getBoundingClientRect();
        if (!(r0.width > 0 && r0.height > 0)) continue;
        if (biKhoa(el)) { coNutKhoa = true; } else { coNutMo = true; }
      }
    }
    if (coNutMo || !coNutKhoa) continue;

    for (const lbl of box.querySelectorAll("label.eds-checkbox")) {
      const cls = typeof lbl.className === "string" ? lbl.className : "";
      if (cls.indexOf("disabled") >= 0) continue;
      const inp = lbl.querySelector("input.eds-checkbox__input") || lbl.querySelector("input[type='checkbox']");
      if (inp && (inp.checked === true || inp.disabled === true)) continue;
      const b0 = lbl.getBoundingClientRect();
      if (!(b0.width > 0 && b0.height > 0)) continue;
      try { lbl.scrollIntoView({ block: "center" }); } catch (e) {}
      const b = lbl.getBoundingClientRect();
      return {
        x: Math.round(b.left + b.width / 2),
        y: Math.round(b.top + b.height / 2),
        title: t || "",
        label: (_na(lbl.textContent) || "x").slice(0, 40),
      };
    }
  }
  return null;
}

// Trang CÒN BỊ LỚP PHỦ CHẮN không → chuỗi mô tả lớp phủ (rỗng = trang thông thoáng, bấm được).
// Vì sao cần: EDS gỡ .eds-modal__box TRƯỚC, còn lớp mask mờ dần rồi mới biến mất. Bấm trong khoảng đó là cú
// trusted click rơi vào mask — DOM vẫn "thấy" nút, toạ độ vẫn đúng, mà không có gì xảy ra. Đó đúng là ca
// 10/08/2026: modal điều khoản đóng lúc 06:24:57, hai cú bấm ngay sau (chọn tab trả hàng, đổi sắp xếp) đều
// trượt im lặng.
// Kiểm tại ĐIỂM SẮP BẤM (nếu truyền x,y) và tại tâm khung nhìn — leo cây cha để bắt cả mask bọc ngoài.
// TỰ CHỨA (world MAIN serialize ĐỘC LẬP).
export function pageConLopPhuChan(x, y) {
  const diem = [];
  if (typeof x === "number" && typeof y === "number") { diem.push([x, y]); }
  diem.push([Math.round(window.innerWidth / 2), Math.round(window.innerHeight / 2)]);

  for (const d of diem) {
    let el = null;
    try { el = document.elementFromPoint(d[0], d[1]); } catch (e) { el = null; }
    let sau = 0;
    while (el && sau < 12) {
      const cls = typeof el.className === "string" ? el.className : "";
      if (/eds-modal|on-boarding|onboarding|mask|overlay|backdrop/i.test(cls)) {
        return (el.tagName || "?").toLowerCase() + "." + cls.split(/\s+/).slice(0, 3).join(".");
      }
      el = el.parentElement;
      sau++;
    }
  }
  return "";
}

// Đọc MÃ VẬN ĐƠN trong modal "Thông Tin Chi Tiết" (ô data-testid=shipping-detail-tracking-number, class .tracking-number).
// Chuẩn hoá BỎ HẾT khoảng trắng ("SPX VN0 626 215 188 57" → "SPXVN062621518857"). Chưa tạo xong ("...đang được tạo")
// hoặc không phải code (còn ký tự tiếng Việt / <6 ký tự) → "" (chưa sẵn sàng).
export function pageReadModalTracking() {
  for (const box of document.querySelectorAll(".eds-modal__box")) {
    const r = box.getBoundingClientRect();
    if (!(r.width > 0 && r.height > 0)) continue;
    const title = box.querySelector(".eds-modal__title") || box.querySelector(".title");
    if (!(title && /thong tin chi tiet/.test(_na(title.textContent)))) continue;
    const el = box.querySelector("[data-testid='shipping-detail-tracking-number']")
            || box.querySelector(".pickup-tn-and-short-code .tracking-number");
    if (!el) return "";
    const raw = (el.textContent || "").trim();
    if (!raw || /đang được tạo|đang tạo|creating|generat/i.test(raw)) return ""; // Shopee chưa tạo xong vận đơn
    const compact = raw.replace(/\s+/g, "");
    if (compact.length < 6 || /[^A-Za-z0-9]/.test(compact)) return ""; // không giống code → bỏ
    return compact;
  }
  return "";
}

// Trong modal có .title khớp titleReSrc, tìm phần tử (theo selectors) có text khớp textReSrc → {x,y,selected}.
export function pageLocateInModal(titleReSrc, selectors, textReSrc) {
  const tre = new RegExp(titleReSrc);
  const re = new RegExp(textReSrc);
  for (const box of document.querySelectorAll(".eds-modal__box")) {
    const r = box.getBoundingClientRect();
    if (!(r.width > 0 && r.height > 0)) continue;
    const title = box.querySelector(".eds-modal__title") || box.querySelector(".title");
    if (!title || !tre.test(_na(title.textContent))) continue;
    for (const sel of selectors) {
      let els;
      try { els = box.querySelectorAll(sel); } catch (e) { continue; }
      for (const el of els) {
        if (re.test(_na(el.textContent))) {
          const b0 = el.getBoundingClientRect();
          if (!(b0.width > 0 && b0.height > 0)) continue;
          try { el.scrollIntoView({ block: "center" }); } catch (e) {}
          const b = el.getBoundingClientRect();
          const cls = typeof el.className === "string" ? el.className.toLowerCase() : "";
          return { x: Math.round(b.left + b.width / 2), y: Math.round(b.top + b.height / 2), selected: cls.indexOf("selected") >= 0 };
        }
      }
    }
  }
  return null;
}

// Nút "In phiếu giao" TRONG MODAL "Thông Tin Chi Tiết" (KHÔNG lấy nút "In phiếu giao" ở DÒNG order list phía sau —
// bug cũ: vớ nhầm link cột Thao tác bên phải). Ưu tiên button[data-testid='print-button'], fallback text. → {x,y}.
export function pagePrintButton() {
  const pick = (el) => {
    if (!el) return null;
    const r0 = el.getBoundingClientRect();
    if (!(r0.width > 0 && r0.height > 0)) return null;
    try { el.scrollIntoView({ block: "center" }); } catch (e) {}
    const r = el.getBoundingClientRect();
    return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) };
  };
  // CHỈ tìm trong modal đang hiển thị (.eds-modal__box) — nút In phiếu của modal Chi Tiết, không phải của order list.
  for (const box of document.querySelectorAll(".eds-modal__box")) {
    const rb = box.getBoundingClientRect();
    if (!(rb.width > 0 && rb.height > 0)) continue;
    const byId = pick(box.querySelector("button[data-testid='print-button']"));
    if (byId) return byId;
    for (const btn of box.querySelectorAll("button")) {
      if (_na(btn.textContent) === "in phieu giao") {
        const p = pick(btn);
        if (p) return p;
      }
    }
  }
  return null;
}

// Tải PDF phiếu NGAY TRONG TRANG awbprint (có cookie + same-origin cho blob) → base64. Port e0/e1 của SaveSlipAsync:
// PDF nhúng trong iframe/embed/object dạng blob: (gốc, ưu tiên) hoặc http(s). Tự chứa (world MAIN, async). "" nếu chưa có.
export async function pageFetchSlipBase64() {
  const srcs = [];
  for (const e of document.querySelectorAll("iframe")) { if (e.src) srcs.push(e.src); }
  for (const e of document.querySelectorAll("embed")) { if (e.src) srcs.push(e.src); }
  for (const e of document.querySelectorAll("object")) { if (e.data) srcs.push(e.data); }
  let url = srcs.find((s) => s.indexOf("blob:") === 0) || srcs.find((s) => s.indexOf("http") === 0) || "";
  if (!url) return "";
  const h = url.indexOf("#"); if (h >= 0) url = url.substring(0, h); // bỏ #toolbar=0...
  try {
    const resp = await fetch(url);
    const buf = await resp.arrayBuffer();
    const bytes = new Uint8Array(buf);
    let bin = ""; const chunk = 0x8000;
    for (let i = 0; i < bytes.length; i += chunk) bin += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
    return btoa(bin);
  } catch (e) { return ""; }
}

// Rút tên lõi tỉnh từ chuỗi tỉnh (mirror ShopeeShippingNav.ProvinceCoreName trên text đã bỏ dấu).
export function _provCore(p) {
  let s = _na(p);
  const prefixes = ["thanh pho ", "tinh ", "tp.", "tp "];
  for (const pre of prefixes) {
    if (s.indexOf(pre) === 0) { s = s.substring(pre.length).trim(); break; }
  }
  return s;
}

// Địa chỉ (.address-list .address-item-container) khớp tỉnh → {found,hasTag,hasEdit,x,y (của nút "Sửa")}. null nếu không có.
export function pageFindAddressEdit(province) {
  const core = _provCore(province);
  if (!core) return null;
  const items = document.querySelectorAll(".address-list .address-item-container");
  for (const it of items) {
    let detail = "";
    for (const grid of it.querySelectorAll("div.grid")) {
      const label = grid.querySelector("span.label");
      if (label && _na(label.textContent) === "dia chi") {
        const d = grid.querySelector(".detail");
        if (d) detail = d.textContent || "";
        break;
      }
    }
    let last = "";
    for (const line of detail.split("\n")) { if (line.trim()) last = line; }
    const target = _na(last || detail);
    if (!target || target.indexOf(core) < 0) continue;

    let hasTag = false;
    for (const tag of it.querySelectorAll(".address-label")) {
      if (_na(tag.textContent) === "dia chi lay hang") { hasTag = true; break; }
    }
    let edit = null;
    for (const b of it.querySelectorAll("button, [role='button'], a")) {
      if (_na(b.textContent) === "sua") {
        const r = b.getBoundingClientRect();
        if (r.width > 0 && r.height > 0) { edit = b; break; }
      }
    }
    let ex = 0, ey = 0;
    if (edit) {
      try { edit.scrollIntoView({ block: "center" }); } catch (e) {}
      const r = edit.getBoundingClientRect();
      ex = Math.round(r.left + r.width / 2);
      ey = Math.round(r.top + r.height / 2);
    }
    return { found: true, hasTag: hasTag, hasEdit: !!edit, x: ex, y: ey };
  }
  return null;
}

// Địa chỉ ĐẦU TIÊN KHÔNG mang tag "Địa chỉ lấy hàng" (để "set về địa chỉ khác") → {found,hasEdit,x,y (nút Sửa)}.
export function pageFindOtherAddressEdit() {
  const items = document.querySelectorAll(".address-list .address-item-container");
  for (const it of items) {
    let hasTag = false;
    for (const tag of it.querySelectorAll(".address-label")) {
      if (_na(tag.textContent) === "dia chi lay hang") { hasTag = true; break; }
    }
    if (hasTag) continue; // đang là địa chỉ lấy hàng → tìm địa chỉ KHÁC
    let edit = null;
    for (const b of it.querySelectorAll("button, [role='button'], a")) {
      if (_na(b.textContent) === "sua") {
        const r = b.getBoundingClientRect();
        if (r.width > 0 && r.height > 0) { edit = b; break; }
      }
    }
    if (!edit) continue;
    try { edit.scrollIntoView({ block: "center" }); } catch (e) {}
    const r = edit.getBoundingClientRect();
    return { found: true, hasEdit: true, x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) };
  }
  return { found: false, hasEdit: false, x: 0, y: 0 };
}

// Checkbox ĐẦU TIÊN cần tick trong modal "Sửa Địa chỉ" (đã cuộn vào giữa) → {x,y}; null nếu không còn.
// BỎ QUA: đã tick, DISABLED (vd "lấy hàng" đang là địa chỉ hiện tại — không đổi được), và (nếu skipReturn) "trả hàng".
// User: set địa chỉ LẤY HÀNG → tick cả 3; set VỀ địa chỉ khác → skipReturn=true (chỉ mặc định + lấy hàng, giữ trả hàng ở địa chỉ mặc định).
export function pageFirstUncheckedBox(skipReturn) {
  for (const box of document.querySelectorAll(".eds-modal__box")) {
    const r = box.getBoundingClientRect();
    if (!(r.width > 0 && r.height > 0)) continue;
    const title = box.querySelector(".eds-modal__title") || box.querySelector(".title");
    if (!title || _na(title.textContent) !== "sua dia chi") continue;
    for (const lbl of box.querySelectorAll("label.eds-checkbox")) {
      const cls = typeof lbl.className === "string" ? lbl.className : "";
      if (cls.indexOf("disabled") >= 0) continue; // disabled → không tick được (thường là đã set)
      const inp = lbl.querySelector("input.eds-checkbox__input");
      if (inp && (inp.checked === true || inp.disabled === true)) continue;
      if (skipReturn && _na(lbl.textContent).indexOf("tra hang") >= 0) continue; // giữ trả hàng ở địa chỉ mặc định
      const b0 = lbl.getBoundingClientRect();
      if (!(b0.width > 0 && b0.height > 0)) continue;
      try { lbl.scrollIntoView({ block: "center" }); } catch (e) {}
      const b = lbl.getBoundingClientRect();
      return { x: Math.round(b.left + b.width / 2), y: Math.round(b.top + b.height / 2) };
    }
    return null; // modal Sửa Địa chỉ đã thấy nhưng không còn checkbox cần tick
  }
  return null;
}

// Đếm checkbox trong modal "Sửa Địa chỉ" → {total, done}. done = số checkbox đã tick HOẶC disabled (đã set). null nếu chưa mở.
export function pageCheckboxCount() {
  for (const box of document.querySelectorAll(".eds-modal__box")) {
    const r = box.getBoundingClientRect();
    if (!(r.width > 0 && r.height > 0)) continue;
    const title = box.querySelector(".eds-modal__title") || box.querySelector(".title");
    if (!title || _na(title.textContent) !== "sua dia chi") continue;
    let total = 0, done = 0;
    for (const lbl of box.querySelectorAll("label.eds-checkbox")) {
      const b = lbl.getBoundingClientRect();
      if (!(b.width > 0 && b.height > 0)) continue;
      total++;
      const cls = typeof lbl.className === "string" ? lbl.className : "";
      const inp = lbl.querySelector("input.eds-checkbox__input");
      const disabled = cls.indexOf("disabled") >= 0 || (inp && inp.disabled === true);
      if ((inp && inp.checked === true) || disabled) done++;
    }
    return { total: total, done: done };
  }
  return null;
}

// Số dòng shop trong picker (tr[data-row-key]) — chờ trang chọn shop render sau SSO.
export function pageShopRowCount() {
  return document.querySelectorAll("tr[data-row-key]").length;
}

// True nếu đang ở FORM ĐĂNG NHẬP subaccount: có ô mật khẩu HIỂN THỊ (SubPassSelectors). Bản sạch KHÔNG tự login.
export function pageIsLoginForm() {
  const sels = [".login-card input[type='password']", "input[type='password']"];
  for (const sel of sels) {
    for (const el of document.querySelectorAll(sel)) {
      const r = el.getBoundingClientRect();
      if (r.width > 0 && r.height > 0) return true;
    }
  }
  return false;
}
