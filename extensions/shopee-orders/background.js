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

// Chạy một hàm trong trang (world MAIN), trả result[0].result.
async function execInTab(tabId, func, args) {
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

// Cú click trusted self-contained (tự attach/detach) — dùng cho openShopDetail.
async function trustedClick(tabId, x, y) {
  await withDebugger(tabId, async (target) => { await dbgClick(target, x, y); });
}

// ---- Xử lý lệnh từ C# -----------------------------------------------------

async function handleCommand(cmd) {
  if (!cmd || !cmd.action) return;

  switch (cmd.action) {
    case "readShopList":     await doReadShopList(); return;
    case "openShopDetail":   await openShopDetail(String(cmd.shopId || "").replace(/'/g, "")); return;
    case "readToShip":       await doReadToShip(); return;
  }
}

// Đọc danh sách shop.
async function doReadShopList() {
  if (!(await ensureListTab(BANHANG_HOSTS))) { send({ action: "error", message: "chưa thấy tab /portal/shop" }); return; }
  const json = await execInTab(listTabId, pageScanShopList, []);
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
  if (!(await ensureListTab(BANHANG_HOSTS))) { send({ action: "error", message: "chưa thấy tab /portal/shop" }); return; }

  const before = (await chrome.tabs.query({ url: "https://banhang.shopee.vn/*" })).map((t) => t.id);

  const scrolled = await execInTab(listTabId, pageScrollDetailIntoView, [shopId]);
  if (!scrolled) { send({ action: "error", message: "không thấy nút Chi tiết của shop " + shopId }); return; }
  await sleep(350);
  const c = await execInTab(listTabId, pageLocateDetailRect, [shopId]);
  if (!c) { send({ action: "error", message: "không đọc được toạ độ nút Chi tiết" }); return; }

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
