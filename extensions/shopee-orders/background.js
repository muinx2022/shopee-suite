// Service worker — CẦU NỐI extension↔C# cho module Đơn hàng (Seller Centre slice).
//
// PIVOT GĐ2: ĐĂNG NHẬP subaccount + SSO nay do C# làm bằng Playwright/CDP (subaccount + /portal/shop KHÔNG bị
// captcha nên an toàn). Extension chỉ còn lo phần Seller Centre né captcha: đọc danh sách shop → mở "Chi tiết"
// bằng TRUSTED CLICK (chrome.debugger) → đọc "Chờ Lấy Hàng". Trình duyệt sạch mở thẳng /portal/shop (đã đăng
// nhập nhờ hồ sơ). KHÔNG mở --remote-debugging-port.
//
// Kênh lệnh/dữ liệu: WebSocket tới ws://localhost:<port> (C# chạy OrdersWebSocketServer). Cổng: hash #_od_ws=<port>
// nếu còn, không thì DEFAULT_PORT cố định; content.js gửi 'wake'/'hello' đánh thức SW + nối cầu.
//
// Protocol:
//   C#  -> ext:  {action:"readShopList"} | {action:"openShopDetail", shopId} | {action:"readToShip"}
//   ext -> C#:   {action:"ready"}
//                {action:"shopOpened"}                                            (openShopDetail xong)
//                {action:"pageData", kind:"shopList", data:<json-array-string>}
//                {action:"pageData", kind:"toShip",   data:<raw-item-title-string>}
//                {action:"progress", message}                                     (chỉ để log)
//                {action:"captcha",  message}                                     (rơi vào trang /verify)
//                {action:"error",    message}

const DEFAULT_PORT = 47821; // PHẢI khớp OrdersBridgeSession.BridgePort phía C# (khi hash rụng).

let ws = null;
let wsPort = DEFAULT_PORT;
let listTabId = null; // tab đang thao tác (subaccount → sau SSO là tab banhang /portal/shop)
let shopTabId = null; // tab shop mở ra sau khi bấm "Chi tiết"
let reconnectTimer = null;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---- WebSocket ------------------------------------------------------------

function send(obj) {
  try {
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
  } catch (e) {
    /* bỏ qua */
  }
}

function scheduleReconnect() {
  if (reconnectTimer) return;
  reconnectTimer = setTimeout(() => { reconnectTimer = null; connect(); }, 1200);
}

function connect() {
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
  const port = wsPort || DEFAULT_PORT;
  try {
    ws = new WebSocket("ws://localhost:" + port);
  } catch (e) {
    scheduleReconnect();
    return;
  }
  ws.onopen = () => send({ action: "ready" });
  ws.onclose = () => { ws = null; scheduleReconnect(); }; // C# chưa lên / rớt → thử lại (browser bị kill thì SW chết theo).
  ws.onerror = () => { /* onclose sẽ dọn + hẹn lại */ };
  ws.onmessage = (ev) => {
    let cmd;
    try { cmd = JSON.parse(ev.data); } catch (e) { return; }
    handleCommand(cmd).catch((e) => send({ action: "error", message: String((e && e.message) || e) }));
  };
}

// content.js gửi 'wake' (mọi trang khớp) hoặc 'hello' → đánh thức SW + nối cầu. Không còn phụ thuộc hash sống sót:
// mất hash thì content.js gửi DEFAULT_PORT. listTabId gán tab đầu tiên khớp; ensureListTab(BANHANG_HOSTS) tự
// phân giải lại tab /portal/shop khi chạy lát cắt (login đã do C#/Playwright lo, extension chỉ ở Seller Centre).
chrome.runtime.onMessage.addListener((msg, sender) => {
  if (msg && (msg.type === "wake" || msg.type === "hello")) {
    if (msg.wsPort) wsPort = msg.wsPort;
    if (listTabId == null && sender.tab && sender.tab.id != null) listTabId = sender.tab.id;
    try { chrome.storage.session.set({ wsPort, listTabId }); } catch (e) {}
    connect();
  }
});

// Service worker khởi động lại (MV3 có thể ngủ) → nạp cổng đã lưu (hoặc mặc định) rồi nối lại.
try {
  chrome.storage.session.get(["wsPort", "listTabId"], (v) => {
    if (v && v.wsPort) wsPort = v.wsPort;
    if (v && v.listTabId != null) listTabId = v.listTabId;
    connect();
  });
} catch (e) { connect(); }

// ---- Hàm chạy trong trang (world MAIN) ------------------------------------
// (Port từ ScanShopListJs / OpenShopDetailAsync / FindToShipTitleAsync / SubUserSelectors... phía C#.)
// LƯU Ý: mỗi hàm world:MAIN được serialize độc lập → PHẢI tự chứa, không tham chiếu helper ngoài.

// Quét bảng shop → JSON mảng [{rowKey,name,login}] (đúng khuôn ScanShopListJs).
function pageScanShopList() {
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
function pageScrollDetailIntoView(shopId) {
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
function pageLocateDetailRect(shopId) {
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
function pageReadToShip() {
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
function _na(s) {
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
function pageScanOrders() {
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
function pageOrderCount() {
  return document.querySelectorAll("a[data-testid='order-item']").length;
}

// Ký hiệu danh sách hiện tại: "<số card>|<mã đơn card đầu>" — phát hiện trang ĐỔI sau khi bấm trang sau.
function pageListSignature() {
  const cards = document.querySelectorAll("a[data-testid='order-item']");
  let first = "";
  if (cards.length) {
    const sn = cards[0].querySelector(".order-sn");
    first = sn ? (sn.textContent || "").replace(/\s+/g, " ").trim() : "";
  }
  return cards.length + "|" + first;
}

// Nút "trang sau" còn DÙNG ĐƯỢC (có box, không disabled) → toạ độ; port FindNextPageButtonAsync + IsUsableNextButtonAsync.
function pageFindNextPage() {
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
      try { el.scrollIntoView({ block: "center" }); } catch (e) {}
      const r = el.getBoundingClientRect();
      return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) };
    }
  }
  return null;
}

// Tìm phần tử khớp text (chuẩn hoá KHÔNG dấu) trong danh sách selector → toạ độ TÂM (đã cuộn vào giữa). null nếu không thấy.
function pageLocateByText(selectors, reSrc) {
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
function pageDumpClickables() {
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
function pageFindPrepareOrder() {
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

// True nếu có modal (.eds-modal__box) hiển thị với .title khớp reSrc (chuẩn hoá không dấu).
function pageModalHasTitle(reSrc) {
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

// Trong modal có .title khớp titleReSrc, tìm phần tử (theo selectors) có text khớp textReSrc → {x,y,selected}.
function pageLocateInModal(titleReSrc, selectors, textReSrc) {
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
function pagePrintButton() {
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
async function pageFetchSlipBase64() {
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
function _provCore(p) {
  let s = _na(p);
  const prefixes = ["thanh pho ", "tinh ", "tp.", "tp "];
  for (const pre of prefixes) {
    if (s.indexOf(pre) === 0) { s = s.substring(pre.length).trim(); break; }
  }
  return s;
}

// Địa chỉ (.address-list .address-item-container) khớp tỉnh → {found,hasTag,hasEdit,x,y (của nút "Sửa")}. null nếu không có.
function pageFindAddressEdit(province) {
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
function pageFindOtherAddressEdit() {
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
function pageFirstUncheckedBox(skipReturn) {
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
function pageCheckboxCount() {
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
function pageShopRowCount() {
  return document.querySelectorAll("tr[data-row-key]").length;
}

// True nếu đang ở FORM ĐĂNG NHẬP subaccount: có ô mật khẩu HIỂN THỊ (SubPassSelectors). Bản sạch KHÔNG tự login.
function pageIsLoginForm() {
  const sels = [".login-card input[type='password']", "input[type='password']"];
  for (const sel of sels) {
    for (const el of document.querySelectorAll(sel)) {
      const r = el.getBoundingClientRect();
      if (r.width > 0 && r.height > 0) return true;
    }
  }
  return false;
}

let lastTabUrls = []; // chẩn đoán: các URL tab lần query gần nhất (đưa vào thông báo lỗi khi không thấy tab).

// Phân giải "tab thao tác" một cách CHẮC CHẮN: nếu listTabId còn sống VÀ khớp host cần → giữ; ngược lại query
// TẤT CẢ tab rồi khớp theo CHUỖI URL (bỏ match-pattern cho khỏi vướng quyền), lấy tab MỚI NHẤT khớp. null nếu
// không thấy (kèm lastTabUrls để báo chẩn đoán).
async function ensureListTab(preferSubstrings) {
  const subs = preferSubstrings || ["subaccount.shopee.com", "accounts.shopee.vn", "banhang.shopee.vn"];
  const matches = (url) => url && subs.some((s) => url.indexOf(s) >= 0);

  if (listTabId != null) {
    try { const t = await chrome.tabs.get(listTabId); if (t && matches(t.url)) return listTabId; } catch (e) {}
    listTabId = null;
  }
  const all = await chrome.tabs.query({});
  lastTabUrls = all.map((t) => t.url || t.pendingUrl || "").filter(Boolean);
  for (const s of subs) {
    const hit = all.filter((t) => (t.url || t.pendingUrl || "").indexOf(s) >= 0);
    if (hit.length) { listTabId = hit[hit.length - 1].id; return listTabId; }
  }
  return null;
}

const BANHANG_HOSTS = ["banhang.shopee.vn"];
const SUBACCOUNT_HOSTS = ["subaccount.shopee.com", "accounts.shopee.vn"];
const SHOP_LIST_URL = "https://banhang.shopee.vn/portal/shop";
const ORDERS_URL = "https://banhang.shopee.vn/portal/sale/order";
const SHIPPING_SETTINGS_URL = "https://banhang.shopee.vn/portal/all-settings/shipping";
const MAX_ORDER_PAGES = 10; // chốt chặn số trang quét (khớp MaxSyncPages phía C#).

// Chạy một hàm trong trang (world MAIN), trả result[0].result.
// Cài helper _na/_provCore lên window của TRANG (world MAIN) — vì page-func chạy executeScript được serialize
// ĐỘC LẬP (không kèm helper ngoài), nên các hàm gọi bare `_na(...)`/`_provCore(...)` sẽ resolve về global window.*.
// PHẢI gọi TRƯỚC mỗi page-func dùng helper (idempotent, rẻ).
function pageInstallHelpers() {
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

async function execInTab(tabId, func, args) {
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

async function waitTabComplete(tabId, ms) {
  const dl = Date.now() + ms;
  while (Date.now() < dl) {
    try {
      const t = await chrome.tabs.get(tabId);
      if (t.status === "complete") return;
    } catch (e) { return; }
    await sleep(400);
  }
}

// ---- Trusted input qua chrome.debugger (mouse: từ POC; key: port CdpInputController.TypeAsync) ----

function dbgSend(target, method, params) {
  return new Promise((resolve, reject) => {
    chrome.debugger.sendCommand(target, method, params || {}, () => {
      const e = chrome.runtime.lastError;
      if (e) reject(new Error(e.message)); else resolve();
    });
  });
}
function dbgAttach(target) {
  return new Promise((resolve, reject) => {
    chrome.debugger.attach(target, "1.3", () => {
      const e = chrome.runtime.lastError;
      if (e) reject(new Error(e.message)); else resolve();
    });
  });
}
function dbgDetach(target) {
  return new Promise((resolve) => { chrome.debugger.detach(target, () => resolve()); });
}

// Attach 1 lần → chạy chuỗi thao tác → detach (đỡ nhấp nháy banner debug so với attach/detach từng cú).
async function withDebugger(tabId, fn) {
  const target = { tabId };
  await dbgAttach(target);
  try { await fn(target); }
  finally { await sleep(500); await dbgDetach(target); }
}

// Cú click chuột trusted (giả định debugger ĐÃ attach).
async function dbgClick(target, x, y) {
  await dbgSend(target, "Input.dispatchMouseEvent", { type: "mouseMoved", x, y, buttons: 0 });
  await sleep(70);
  await dbgSend(target, "Input.dispatchMouseEvent", { type: "mousePressed", x, y, button: "left", buttons: 1, clickCount: 1 });
  await sleep(50);
  await dbgSend(target, "Input.dispatchMouseEvent", { type: "mouseReleased", x, y, button: "left", buttons: 0, clickCount: 1 });
}

// code/vk cho ký tự ASCII (port KeyInfo của CdpInputController) — cho gõ địa chỉ ở GĐ3 (nếu cần).
function keyInfo(ch) {
  const c = ch.charCodeAt(0);
  if (ch >= "0" && ch <= "9") return { code: "Digit" + ch, vk: c };
  if (ch >= "a" && ch <= "z") return { code: "Key" + ch.toUpperCase(), vk: ch.toUpperCase().charCodeAt(0) };
  if (ch >= "A" && ch <= "Z") return { code: "Key" + ch, vk: c };
  if (ch === " ") return { code: "Space", vk: 32 };
  return { code: "", vk: 0 };
}

// Gõ trusted (port TypeAsync): ASCII in-được → keyDown/keyUp từng ký tự; có unicode → insertText cả chuỗi.
async function dbgType(target, text) {
  if (!text) return;
  let isAscii = true;
  for (const ch of text) { const c = ch.charCodeAt(0); if (c < 0x20 || c > 0x7e) { isAscii = false; break; } }
  if (!isAscii) {
    await sleep(150);
    await dbgSend(target, "Input.insertText", { text });
    return;
  }
  for (const ch of text) {
    const ki = keyInfo(ch);
    await dbgSend(target, "Input.dispatchKeyEvent", { type: "keyDown", text: ch, key: ch, code: ki.code, windowsVirtualKeyCode: ki.vk });
    await dbgSend(target, "Input.dispatchKeyEvent", { type: "keyUp", key: ch, code: ki.code, windowsVirtualKeyCode: ki.vk });
    await sleep(50 + Math.floor(Math.random() * 70));
  }
}

// Enter: keyDown kèm text "\r" (renderer cần keypress để submit form) + keyUp (port PressKeyAsync).
async function dbgEnter(target) {
  await dbgSend(target, "Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, text: "\r" });
  await sleep(50);
  await dbgSend(target, "Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
}

// Giữ chrome.debugger ATTACH XUYÊN SUỐT một lệnh (KHÔNG attach/detach từng cú) — vì: (1) banner "đang gỡ lỗi"
// hết nhấp nháy; (2) mọi toạ độ (execInTab) + cú click ở CÙNG trạng thái banner → click không trượt. Gọi
// ensureDbg(tab) đầu mỗi lệnh nhiều-click (trước khi đọc toạ độ), releaseDbg() ở cuối.
let _dbgTab = null;
async function ensureDbg(tabId) {
  if (_dbgTab === tabId) return;
  if (_dbgTab != null) { try { await dbgDetach({ tabId: _dbgTab }); } catch (e) {} _dbgTab = null; }
  try { await dbgAttach({ tabId }); } catch (e) { /* có thể đã attach sẵn — coi như ok */ }
  _dbgTab = tabId;
}
async function releaseDbg() {
  if (_dbgTab == null) return;
  const t = _dbgTab; _dbgTab = null;
  try { await sleep(300); await dbgDetach({ tabId: t }); } catch (e) {}
}
// Debugger tự detach (điều hướng trang / user bấm "Huỷ" trên banner) → reset để cú click sau tự attach lại.
try { chrome.debugger.onDetach.addListener((source) => { if (_dbgTab != null && source && source.tabId === _dbgTab) _dbgTab = null; }); } catch (e) {}

// Cú click trusted — dùng debugger đã attach (ensureDbg trước đó), KHÔNG detach sau mỗi cú.
async function trustedClick(tabId, x, y) {
  await ensureDbg(tabId);
  await dbgClick({ tabId }, x, y);
}

// ---- Xử lý lệnh từ C# -----------------------------------------------------

async function handleCommand(cmd) {
  if (!cmd || !cmd.action) return;

  switch (cmd.action) {
    case "gotoSellerCentre": await gotoSellerCentre(); return;
    case "readShopList":     await doReadShopList(); return;
    case "openShopDetail":   await openShopDetail(String(cmd.shopId || "").replace(/'/g, "")); return;
    case "readToShip":       await doReadToShip(); return;
    case "syncOrders":       await doSyncOrders(); return;
    case "setPickupAddress": await doSetPickupAddress(String(cmd.province || "")); return;
    case "setPickupAddressToOther": await doSetPickupAddressToOther(); return;
    case "prepareNextOrder": await doPrepareNextOrder(); return;
    case "closeShopTab":     await doCloseShopTab(); return;
  }
}

// GĐ4: đóng tab shop hiện tại rồi VỀ picker /portal/shop (giữa các shop). Shop thường mở ở TAB RIÊNG (shopTabId
// khác listTabId) → chỉ đóng tab đó, picker (listTabId) còn nguyên. Nếu shop mở CÙNG tab picker → điều hướng
// listTabId về /portal/shop. Cuối cùng poll tr[data-row-key] để chắc chắn picker sẵn sàng cho shop kế.
async function doCloseShopTab() {
  if (shopTabId != null && shopTabId !== listTabId) {
    try { await chrome.tabs.remove(shopTabId); } catch (e) {}
    shopTabId = null;
  } else if (listTabId != null) {
    // Shop mở cùng tab picker (hoặc không rõ) → đưa picker về /portal/shop.
    try { await chrome.tabs.update(listTabId, { url: SHOP_LIST_URL }); } catch (e) {}
    await waitTabComplete(listTabId, 20000);
    shopTabId = null;
  }
  if (listTabId == null) { send({ action: "shopTabClosed", ok: false }); return; }
  const st = await ensureShopPicker(listTabId); // "ok" | "verify" | "stuck"
  if (st === "verify") {
    let u = "";
    try { u = (await chrome.tabs.get(listTabId)).url || ""; } catch (e) {}
    send({ action: "captcha", message: u });
    return;
  }
  send({ action: "shopTabClosed", ok: st === "ok" });
}

// SSO từ trang tài khoản subaccount (/account, đã đăng nhập nhờ cookie) → "Kênh Người bán" → tab banhang →
// trang CHỌN SHOP (/portal/shop, picker tất-cả-shop — né sticky-shop server-side khi mở thẳng /portal/shop).
async function gotoSellerCentre() {
  if (!(await ensureListTab(SUBACCOUNT_HOSTS))) {
    send({ action: "error", message: "chưa thấy tab subaccount/account để mở Kênh Người bán — SW thấy: [" + lastTabUrls.join(" | ") + "]" });
    return;
  }

  // Trang /account có thể ra FORM LOGIN nếu cookie hết hạn — bản sạch KHÔNG tự điền (đã bỏ khi pivot GĐ2).
  let isLogin = false;
  try { isLogin = await execInTab(listTabId, pageIsLoginForm, []); } catch (e) {}
  if (isLogin) {
    send({ action: "error", message: "bản sạch gặp trang đăng nhập subaccount (cookie hết hạn) — cần đăng nhập lại" });
    return;
  }

  // Trên /account (đã đăng nhập) → tìm "Kênh Người bán". POLL ~10s (SPA render trễ); mỗi vòng thử thêm click
  // "Tài khoản của tôi" (có thể là menu xổ ra chứa entry). Không thấy → DUMP các mục bấm được để biết nhãn thật.
  const sellerSel = ["span.entry-text", ".entry", "a", "span", "div", "[role='button']", "[role='menuitem']", "li"];
  const sellerRe = "kenh nguoi ban|seller\\s*cent(re|er)|seller\\s*channel|nguoi ban";
  let seller = null;
  let triedAcc = false;
  const sdl = Date.now() + 10000;
  while (Date.now() < sdl) {
    try { seller = await execInTab(listTabId, pageLocateByText, [sellerSel, sellerRe]); } catch (e) { seller = null; }
    if (seller) break;
    if (!triedAcc) {
      const acc = await execInTab(listTabId, pageLocateByText, [["li", "a", "div", "span", "[role='menuitem']"], "tai khoan cua toi|my account"]);
      if (acc) { await trustedClick(listTabId, acc.x, acc.y); await sleep(1500); }
      triedAcc = true;
    }
    await sleep(600);
  }
  if (!seller) {
    let dump = "[]"; try { dump = await execInTab(listTabId, pageDumpClickables, []); } catch (e) {}
    let u = ""; try { u = (await chrome.tabs.get(listTabId)).url || ""; } catch (e) {}
    send({ action: "error", message: "không thấy 'Kênh Người bán' trên " + u + " — các mục bấm được: " + dump });
    return;
  }

  const before = (await chrome.tabs.query({ url: "https://banhang.shopee.vn/*" })).map((t) => t.id);
  const subTabId = listTabId;
  await trustedClick(listTabId, seller.x, seller.y);

  // Theo tab banhang MỚI hoặc tab subaccount tự điều hướng sang banhang.
  const deadline = Date.now() + 90000;
  let found = null;
  while (Date.now() < deadline) {
    const tabs = await chrome.tabs.query({ url: "https://banhang.shopee.vn/*" });
    const cand = tabs.find((t) => before.indexOf(t.id) === -1);
    if (cand) { found = cand; break; }
    try {
      const lt = await chrome.tabs.get(subTabId);
      if (lt && lt.url && lt.url.indexOf("banhang.shopee.vn") >= 0) { found = lt; break; }
    } catch (e) {}
    await sleep(600);
  }
  if (!found) { send({ action: "error", message: "bấm 'Kênh Người bán' xong chờ 90s chưa thấy Seller Centre" }); return; }

  listTabId = found.id;
  shopTabId = null;
  await waitTabComplete(listTabId, 15000);
  let url = "";
  try { url = (await chrome.tabs.get(listTabId)).url || ""; } catch (e) {}
  if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }

  // Đảm bảo VỀ ĐƯỢC picker /portal/shop (poll tr[data-row-key]); có thể vẫn bị sticky-redirect → điều hướng lại 1 lần.
  const st = await ensureShopPicker(listTabId);
  if (st === "verify") {
    let u = "";
    try { u = (await chrome.tabs.get(listTabId)).url || ""; } catch (e) {}
    send({ action: "captcha", message: u });
    return;
  }
  if (st !== "ok") {
    send({ action: "error", message: "không về được trang chọn shop (/portal/shop) — có thể vẫn dính shop cũ (sticky) hoặc bảng chưa render" });
    return;
  }

  // Picker OK → đóng tab subaccount (nếu là tab riêng) rồi báo sẵn sàng.
  if (subTabId !== listTabId) { try { await chrome.tabs.remove(subTabId); } catch (e) {} }
  send({ action: "atSellerCentre" });
}

// Về/chờ trang chọn shop: poll tr[data-row-key] tới ~30s; nếu chưa ở /portal/shop thì điều hướng lại MỘT lần.
// Trả "ok" (thấy bảng shop) | "verify" (rơi trang xác minh) | "stuck" (hết giờ, có thể vẫn dính shop cũ).
async function ensureShopPicker(tabId) {
  const overall = Date.now() + 30000;
  let navigated = false;
  while (Date.now() < overall) {
    let u = "";
    try { u = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
    if (/\/verify/i.test(u)) return "verify";
    let n = 0;
    try { n = (await execInTab(tabId, pageShopRowCount, [])) || 0; } catch (e) { n = 0; }
    if (n > 0) return "ok";
    if (u.indexOf("/portal/shop") < 0 && !navigated) {
      navigated = true;
      try { await chrome.tabs.update(tabId, { url: SHOP_LIST_URL }); } catch (e) {}
      await waitTabComplete(tabId, 20000);
      continue;
    }
    await sleep(700);
  }
  return "stuck";
}

// Đọc danh sách shop.
async function doReadShopList() {
  if (!(await ensureListTab(BANHANG_HOSTS))) { send({ action: "error", message: "chưa thấy tab /portal/shop — SW thấy các tab: [" + lastTabUrls.join(" | ") + "]" }); return; }
  // Poll chờ bảng shop render (tr[data-row-key]) — production chờ tới ~20s; ở đây 15s. Đọc một phát dễ trúng lúc
  // bảng CHƯA render → 0 shop.
  const deadline = Date.now() + 15000;
  let json = "[]";
  while (Date.now() < deadline) {
    try { json = (await execInTab(listTabId, pageScanShopList, [])) || "[]"; } catch (e) { json = "[]"; }
    let n = 0; try { n = JSON.parse(json).length; } catch (e) {}
    if (n > 0) break;
    await sleep(500);
  }
  send({ action: "pageData", kind: "shopList", data: json });
}

// GĐ1: đọc số "Chờ Lấy Hàng".
async function doReadToShip() {
  const tabId = shopTabId != null ? shopTabId : listTabId;
  if (tabId == null) { send({ action: "error", message: "chưa có tab shop để đọc" }); return; }
  const deadline = Date.now() + 8000;
  let raw = null;
  while (Date.now() < deadline) {
    try { raw = await execInTab(tabId, pageReadToShip, []); } catch (e) { raw = null; }
    if (raw != null) break;
    await sleep(400);
  }
  send({ action: "pageData", kind: "toShip", data: raw });
}

// GĐ1: mở "Chi tiết" shop đầu bằng trusted click, theo tab shop mới.
async function openShopDetail(shopId) {
  if (!(await ensureListTab(BANHANG_HOSTS))) { send({ action: "error", message: "chưa thấy tab /portal/shop — SW thấy các tab: [" + lastTabUrls.join(" | ") + "]" }); return; }

  // Sau thời gian NGHỈ giữa shop (3–5'), picker có thể đã drift: sticky-redirect về trang đơn của shop trước,
  // tự refresh, hoặc bảng chưa render lại. ĐẢM BẢO về /portal/shop + bảng shop có dòng TRƯỚC khi tìm dòng shopId.
  const pk = await ensureShopPicker(listTabId);
  if (pk === "verify") { send({ action: "captcha", message: "rơi trang verify khi mở lại picker shop" }); return; }

  // POLL chờ ĐÚNG dòng shopId render (KHÔNG đọc 1 phát — dòng có thể chưa render / picker vừa nav lại sau nghỉ).
  let scrolled = false;
  const rowDeadline = Date.now() + 15000;
  while (Date.now() < rowDeadline) {
    scrolled = await execInTab(listTabId, pageScrollDetailIntoView, [shopId]);
    if (scrolled) break;
    await sleep(500);
  }
  if (!scrolled) { send({ action: "error", message: "không thấy nút Chi tiết của shop " + shopId }); return; }
  await sleep(350);
  const c = await execInTab(listTabId, pageLocateDetailRect, [shopId]);
  if (!c) { send({ action: "error", message: "không đọc được toạ độ nút Chi tiết" }); return; }

  // Chụp tập tab banhang NGAY trước khi click (sau ensureShopPicker) để phát hiện tab shop MỚI mở.
  const before = (await chrome.tabs.query({ url: "https://banhang.shopee.vn/*" })).map((t) => t.id);
  await trustedClick(listTabId, c.x, c.y);

  const deadline = Date.now() + 30000;
  let found = null;
  while (Date.now() < deadline) {
    const tabs = await chrome.tabs.query({ url: "https://banhang.shopee.vn/*" });
    const cand = tabs.find((t) => before.indexOf(t.id) === -1);
    if (cand) { found = cand; break; }
    try {
      const lt = await chrome.tabs.get(listTabId);
      if (lt && lt.url && lt.url.indexOf("/portal/shop") === -1) { found = lt; break; }
    } catch (e) {}
    await sleep(500);
  }
  if (!found) { send({ action: "error", message: "chờ 30s chưa thấy tab shop mở" }); return; }
  shopTabId = found.id;

  const loadDeadline = Date.now() + 15000;
  let url = found.url || "";
  while (Date.now() < loadDeadline) {
    try {
      const t = await chrome.tabs.get(shopTabId);
      url = t.url || url;
      if (t.status === "complete") break;
    } catch (e) { break; }
    await sleep(400);
  }

  if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }
  send({ action: "shopOpened" });
}

// ===== GĐ3 — lệnh đọc + xử đơn (chạy trên tab shop = shopTabId) =====

function orderTabId() {
  return shopTabId != null ? shopTabId : listTabId;
}

// Chờ số card đơn ỔN ĐỊNH (đứng yên 2 lượt nếu >0, hoặc 3 lượt nếu 0 = shop rỗng) hoặc hết giờ.
async function waitOrdersStable(tabId, timeoutMs) {
  const dl = Date.now() + timeoutMs;
  let last = -1, stable = 0;
  while (Date.now() < dl) {
    let c = 0;
    try { c = (await execInTab(tabId, pageOrderCount, [])) || 0; } catch (e) { c = 0; }
    if (c === last) { stable++; if (stable >= 2 && c > 0) return; if (stable >= 3) return; }
    else { stable = 0; last = c; }
    await sleep(500);
  }
}

// Chờ danh sách ĐỔI so với ký hiệu 'before' (port WaitListChangedAsync): bỏ trạng thái đang tải ("0|...").
async function waitOrdersChanged(tabId, before, timeoutMs) {
  const dl = Date.now() + timeoutMs;
  while (Date.now() < dl) {
    await sleep(300);
    let now = "";
    try { now = (await execInTab(tabId, pageListSignature, [])) || ""; } catch (e) {}
    if (now.indexOf("0|") === 0) continue;
    if (now && now !== before) return true;
  }
  return false;
}

// Phần A: đọc đơn tab "Tất cả" (phân trang). Trả {action:"pageData", kind:"orders", data:<json gộp>}.
async function doSyncOrders() {
  const tabId = orderTabId();
  if (tabId == null) { send({ action: "error", message: "chưa có tab shop để đọc đơn" }); return; }

  try { await chrome.tabs.update(tabId, { url: ORDERS_URL }); } catch (e) {}
  await waitTabComplete(tabId, 20000);
  let url = "";
  try { url = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
  if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }

  await waitOrdersStable(tabId, 15000);

  // Sang tab "Tất cả" (best-effort: không thấy thì quét tab hiện tại).
  const allTab = await execInTab(tabId, pageLocateByText, [["[role='tab']", ".eds-tabs__nav-tab", "a", "div", "span"], "^tat ca$"]);
  if (allTab) { await trustedClick(tabId, allTab.x, allTab.y); await sleep(1200); await waitOrdersStable(tabId, 10000); }

  const all = [];
  const seen = {};
  let pageNo = 0;
  while (pageNo < MAX_ORDER_PAGES) {
    pageNo++;
    await waitOrdersStable(tabId, 15000);
    let json = "[]";
    try { json = (await execInTab(tabId, pageScanOrders, [])) || "[]"; } catch (e) { json = "[]"; }
    let arr = [];
    try { arr = JSON.parse(json) || []; } catch (e) { arr = []; }
    for (const o of arr) {
      const sn = o && o.orderSn ? o.orderSn : "";
      if (sn && !seen[sn]) { seen[sn] = 1; all.push(o); }
    }

    let sigBefore = "";
    try { sigBefore = (await execInTab(tabId, pageListSignature, [])) || ""; } catch (e) {}
    const next = await execInTab(tabId, pageFindNextPage, []);
    if (!next) break;
    if (pageNo >= MAX_ORDER_PAGES) break;
    await trustedClick(tabId, next.x, next.y);
    const changed = await waitOrdersChanged(tabId, sigBefore, 10000);
    if (!changed) break;
    await sleep(1000);
  }

  send({ action: "pageData", kind: "orders", data: JSON.stringify(all) });
}

// Phần B: đặt địa chỉ lấy hàng = province (port OpenShippingAddressSettingsAsync/SetPickupAddressAsync). → {action:"pickupDone", ok}.
async function doSetPickupAddress(province) {
  const tabId = orderTabId();
  if (tabId == null) { send({ action: "error", message: "chưa có tab shop để đặt địa chỉ" }); return; }

  try { await chrome.tabs.update(tabId, { url: SHIPPING_SETTINGS_URL }); } catch (e) {}
  await waitTabComplete(tabId, 20000);
  let url = "";
  try { url = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
  if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }

  await sleep(1000);
  // Tab "Địa Chỉ".
  let addrTab = null;
  const dl0 = Date.now() + 10000;
  while (Date.now() < dl0) {
    addrTab = await execInTab(tabId, pageLocateByText, [[".eds-tabs__nav-tab", "[role='tab']", "div", "span", "a"], "dia chi"]);
    if (addrTab) break;
    await sleep(500);
  }
  if (addrTab) { await trustedClick(tabId, addrTab.x, addrTab.y); await sleep(1200); }

  // Địa chỉ khớp tỉnh.
  let info = null;
  const dl = Date.now() + 15000;
  while (Date.now() < dl) {
    info = await execInTab(tabId, pageFindAddressEdit, [province]);
    if (info && info.found) break;
    await sleep(500);
  }
  if (!info || !info.found) { send({ action: "progress", message: "không thấy địa chỉ khớp tỉnh " + province + " — bỏ đặt địa chỉ lấy hàng." }); send({ action: "pickupDone", ok: false }); return; }
  // KHÔNG return sớm khi đã là pickup: vẫn mở Sửa để đảm bảo đủ 3 dấu tick (mặc định + lấy hàng + trả hàng).
  if (!info.hasEdit) {
    send({ action: "progress", message: info.hasTag ? ("địa chỉ " + province + " đã là địa chỉ lấy hàng (không có nút Sửa).") : ("không thấy nút Sửa của địa chỉ " + province + ".") });
    send({ action: "pickupDone", ok: info.hasTag });
    return;
  }

  await trustedClick(tabId, info.x, info.y);

  // Modal "Sửa Địa chỉ".
  let hasModal = false;
  const dlm = Date.now() + 10000;
  while (Date.now() < dlm) { hasModal = await execInTab(tabId, pageModalHasTitle, ["^sua dia chi$"]); if (hasModal) break; await sleep(400); }
  if (!hasModal) { send({ action: "progress", message: "không mở được modal Sửa Địa chỉ." }); send({ action: "pickupDone", ok: false }); return; }
  await sleep(800);

  // Tick TẤT CẢ checkbox cần (mặc định + lấy hàng + trả hàng) — bỏ qua cái đã tick / disabled. Lặp lấy-cái-đầu-chưa-tick → click.
  let cbGuard = 0;
  while (cbGuard < 8) {
    cbGuard++;
    const un = await execInTab(tabId, pageFirstUncheckedBox, [false]);
    if (!un) break;
    await trustedClick(tabId, un.x, un.y);
    await sleep(500);
  }
  const cnt = await execInTab(tabId, pageCheckboxCount, []);
  if (!cnt || cnt.total === 0) { send({ action: "progress", message: "không thấy checkbox trong modal Sửa Địa chỉ." }); send({ action: "pickupDone", ok: false }); return; }
  send({ action: "progress", message: "đã đảm bảo " + cnt.done + "/" + cnt.total + " checkbox địa chỉ có dấu tick." });

  // Lưu.
  const save = await execInTab(tabId, pageLocateInModal, ["^sua dia chi$", [".eds-modal__footer button", "button", "[role='button']"], "^luu$"]);
  if (!save) { send({ action: "progress", message: "không thấy nút Lưu." }); send({ action: "pickupDone", ok: false }); return; }
  await trustedClick(tabId, save.x, save.y);
  await sleep(1200);

  // Hộp xác nhận "Đồng ý" (không phải lúc nào cũng hiện).
  const confirm = await execInTab(tabId, pageLocateByText, [[".eds-modal__footer button", "button", "[role='button']"], "^dong y$"]);
  if (confirm) { await trustedClick(tabId, confirm.x, confirm.y); await sleep(1000); }

  send({ action: "progress", message: "đã đặt địa chỉ lấy hàng = " + province + "." });
  send({ action: "pickupDone", ok: true });
}

// Set địa chỉ lấy hàng VỀ ĐỊA CHỈ KHÁC (sau khi xử hết đơn) — port SetPickupAddressToOtherAsync. Tick CHỈ 2
// (mặc định + lấy hàng, skipReturn=true → GIỮ tag "trả hàng" ở địa chỉ mặc định). → {action:"pickupOtherDone", ok}.
async function doSetPickupAddressToOther() {
  const tabId = orderTabId();
  if (tabId == null) { send({ action: "error", message: "chưa có tab shop để set địa chỉ khác" }); return; }

  try { await chrome.tabs.update(tabId, { url: SHIPPING_SETTINGS_URL }); } catch (e) {}
  await waitTabComplete(tabId, 20000);
  let url = "";
  try { url = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
  if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }

  await sleep(1000);
  // Tab "Địa Chỉ".
  let addrTab = null;
  const dl0 = Date.now() + 10000;
  while (Date.now() < dl0) {
    addrTab = await execInTab(tabId, pageLocateByText, [[".eds-tabs__nav-tab", "[role='tab']", "div", "span", "a"], "dia chi"]);
    if (addrTab) break;
    await sleep(500);
  }
  if (addrTab) { await trustedClick(tabId, addrTab.x, addrTab.y); await sleep(1200); }

  // Địa chỉ KHÁC (không mang tag "lấy hàng").
  let info = null;
  const dl = Date.now() + 15000;
  while (Date.now() < dl) {
    info = await execInTab(tabId, pageFindOtherAddressEdit, []);
    if (info && info.found) break;
    await sleep(500);
  }
  if (!info || !info.found || !info.hasEdit) {
    send({ action: "progress", message: "không thấy địa chỉ khác (không mang tag lấy hàng) — bỏ qua set về địa chỉ khác." });
    send({ action: "pickupOtherDone", ok: false });
    return;
  }

  await trustedClick(tabId, info.x, info.y);

  // Modal "Sửa Địa chỉ".
  let hasModal = false;
  const dlm = Date.now() + 10000;
  while (Date.now() < dlm) { hasModal = await execInTab(tabId, pageModalHasTitle, ["^sua dia chi$"]); if (hasModal) break; await sleep(400); }
  if (!hasModal) { send({ action: "progress", message: "không mở được modal Sửa Địa chỉ (set khác)." }); send({ action: "pickupOtherDone", ok: false }); return; }
  await sleep(800);

  // Tick 2 (mặc định + lấy hàng) — skipReturn=true: GIỮ "trả hàng" ở địa chỉ mặc định.
  let cbGuard = 0;
  while (cbGuard < 8) {
    cbGuard++;
    const un = await execInTab(tabId, pageFirstUncheckedBox, [true]);
    if (!un) break;
    await trustedClick(tabId, un.x, un.y);
    await sleep(500);
  }

  // Lưu.
  const save = await execInTab(tabId, pageLocateInModal, ["^sua dia chi$", [".eds-modal__footer button", "button", "[role='button']"], "^luu$"]);
  if (!save) { send({ action: "progress", message: "không thấy nút Lưu (set khác)." }); send({ action: "pickupOtherDone", ok: false }); return; }
  await trustedClick(tabId, save.x, save.y);
  await sleep(1200);

  // "Đồng ý" (nếu hiện).
  const confirm = await execInTab(tabId, pageLocateByText, [[".eds-modal__footer button", "button", "[role='button']"], "^dong y$"]);
  if (confirm) { await trustedClick(tabId, confirm.x, confirm.y); await sleep(1000); }

  send({ action: "progress", message: "đã set địa chỉ lấy hàng VỀ địa chỉ khác (giữ trả hàng ở địa chỉ mặc định)." });
  send({ action: "pickupOtherDone", ok: true });
}

// Phần B: xử ĐƠN ĐẦU cần "Chuẩn bị hàng" (port ProcessFirstOrderAsync). → {action:"orderPrepared",...} hoặc {action:"noOrder"}.
async function doPrepareNextOrder() {
  const tabId = orderTabId();
  if (tabId == null) { send({ action: "error", message: "chưa có tab shop để xử đơn" }); return; }

  try { await chrome.tabs.update(tabId, { url: ORDERS_URL }); } catch (e) {}
  await waitTabComplete(tabId, 20000);
  let url = "";
  try { url = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
  if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }

  await waitOrdersStable(tabId, 15000);
  await ensureDbg(tabId); // attach TRƯỚC khi đọc toạ độ → banner đứng yên, toạ độ + click cùng trạng thái (click không trượt).
  // Tab "Chờ lấy hàng".
  const toShipTab = await execInTab(tabId, pageLocateByText, [["[role='tab']", ".eds-tabs__nav-tab", "a", "div", "span"], "^cho lay hang"]);
  if (toShipTab) { await trustedClick(tabId, toShipTab.x, toShipTab.y); await sleep(1200); await waitOrdersStable(tabId, 10000); }

  // Đơn đầu có "Chuẩn bị hàng".
  let prep = null;
  const dl = Date.now() + 12000;
  while (Date.now() < dl) { prep = await execInTab(tabId, pageFindPrepareOrder, []); if (prep) break; await sleep(500); }
  if (!prep) { send({ action: "noOrder" }); return; }
  const orderCode = prep.orderCode || "";

  const beforeTabs = (await chrome.tabs.query({})).map((t) => t.id); // mọi tab (tab phiếu awbprint có thể khác domain).
  await trustedClick(tabId, prep.x, prep.y);

  // Modal "Giao Đơn Hàng".
  let hasShip = false;
  const dl1 = Date.now() + 10000;
  while (Date.now() < dl1) { hasShip = await execInTab(tabId, pageModalHasTitle, ["^giao don hang$"]); if (hasShip) break; await sleep(400); }
  if (!hasShip) { send({ action: "error", message: "không mở được modal Giao Đơn Hàng (đơn " + orderCode + ")" }); return; }
  await sleep(1000);

  // Chọn "tự mang hàng tới Bưu cục" (nếu chưa selected).
  const drop = await execInTab(tabId, pageLocateInModal, ["^giao don hang$", ["div", "label", "[role='radio']", "span"], "tu mang hang toi buu cuc"]);
  if (drop && !drop.selected) { await trustedClick(tabId, drop.x, drop.y); await sleep(600); }

  // "Xác nhận".
  const conf = await execInTab(tabId, pageLocateInModal, ["^giao don hang$", [".eds-modal__footer button", "button", "[role='button']"], "^xac nhan$"]);
  if (!conf) { send({ action: "error", message: "không thấy nút Xác nhận (đơn " + orderCode + ")" }); return; }
  await trustedClick(tabId, conf.x, conf.y);

  // Modal "Thông Tin Chi Tiết".
  let hasDetail = false;
  const dl2 = Date.now() + 15000;
  while (Date.now() < dl2) { hasDetail = await execInTab(tabId, pageModalHasTitle, ["^thong tin chi tiet$"]); if (hasDetail) break; await sleep(500); }
  if (!hasDetail) { send({ action: "error", message: "không mở được modal Thông Tin Chi Tiết (đơn " + orderCode + ")" }); return; }
  await sleep(1000);

  // "In phiếu giao" → bắt tab phiếu (window.open → awbprint). Bấm lặp tới khi có tab (Shopee tạo vận đơn có thể muộn, ~2').
  send({ action: "progress", message: "modal Chi Tiết mở — tìm + bấm 'In phiếu giao'..." });
  let slipTab = null;
  let printTries = 0;
  const printDeadline = Date.now() + 120000;
  while (!slipTab && Date.now() < printDeadline) {
    const tabs = await chrome.tabs.query({});
    const cand = tabs.find((t) => beforeTabs.indexOf(t.id) === -1 && t.id !== tabId);
    if (cand) { slipTab = cand; break; }
    await ensureDbg(tabId);
    const pbtn = await execInTab(tabId, pagePrintButton, []);
    printTries++;
    if (pbtn) {
      await trustedClick(tabId, pbtn.x, pbtn.y);
      if (printTries <= 2) send({ action: "progress", message: "đã bấm 'In phiếu giao' tại (" + pbtn.x + "," + pbtn.y + ") — chờ tab phiếu..." });
    } else {
      if (printTries <= 2) send({ action: "progress", message: "CHƯA thấy nút 'In phiếu giao' (data-testid=print-button) — thử lại..." });
    }
    const wt = Date.now() + 8000;
    while (!slipTab && Date.now() < wt) {
      const tt = await chrome.tabs.query({});
      const cc = tt.find((t) => beforeTabs.indexOf(t.id) === -1 && t.id !== tabId);
      if (cc) { slipTab = cc; break; }
      await sleep(400);
    }
    if (!slipTab) await sleep(800);
  }
  if (!slipTab) { send({ action: "error", message: "không mở được tab phiếu giao sau " + printTries + " lần bấm (đơn " + orderCode + ")" }); return; }
  send({ action: "progress", message: "đã bắt được tab phiếu (sau " + printTries + " lần bấm)." });

  // Chờ tab phiếu điều hướng tới awbprint.
  const slipId = slipTab.id;
  let slipUrl = slipTab.url || "";
  const ud = Date.now() + 10000;
  while (Date.now() < ud) {
    try { const t = await chrome.tabs.get(slipId); slipUrl = t.url || slipUrl; if (slipUrl.indexOf("awbprint") >= 0) break; } catch (e) { break; }
    await sleep(300);
  }

  // Tải PDF phiếu NGAY TRONG TAB PHIẾU (có cookie, same-origin blob) → base64. Poll ~25s để khung nhúng PDF kịp render.
  await waitTabComplete(slipId, 15000);
  let slipB64 = "";
  const fd = Date.now() + 25000;
  while (Date.now() < fd) {
    try { slipB64 = (await execInTab(slipId, pageFetchSlipBase64, [])) || ""; } catch (e) { slipB64 = ""; }
    if (slipB64) break;
    await sleep(800);
  }

  // Đóng tab phiếu sau khi lấy xong (user yêu cầu: lưu xong thì close tab phiếu).
  try { await chrome.tabs.remove(slipId); } catch (e) {}

  send({ action: "orderPrepared", orderCode: orderCode, slipTabUrl: slipUrl, slipBase64: slipB64 });
}
