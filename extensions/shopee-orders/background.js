// Service worker — CẦU NỐI extension↔C# cho module Đơn hàng (GĐ1).
//
// Kênh lệnh/dữ liệu: WebSocket tới ws://localhost:<port> (C# chạy OrdersWebSocketServer). Cổng lấy từ hash
// #_od_ws=<port> mà C# nhúng vào URL /portal/shop đầu tiên; content.js đọc rồi gửi sang đây ('hello').
// Kênh input "thật": chrome.debugger (Input.dispatchMouseEvent) — trusted click, đã chứng minh né captcha ở GĐ0.
// KHÔNG mở --remote-debugging-port (không có kênh CDP để anti-bot soi / Playwright attach).
//
// Protocol:
//   C#  -> ext:  {action:"readShopList"} | {action:"openShopDetail", shopId} | {action:"readToShip"}
//   ext -> C#:   {action:"ready"}
//                {action:"pageData", kind:"shopList", data:<json-array-string>}
//                {action:"pageData", kind:"toShip",   data:<raw-item-title-string>}
//                {action:"progress", message}          (chỉ gửi khi mở Chi tiết xong)
//                {action:"captcha",  message}          (rơi vào trang /verify)
//                {action:"error",    message}

let ws = null;
let wsPort = null;
let listTabId = null; // tab /portal/shop (bảng danh sách shop)
let shopTabId = null; // tab shop mở ra sau khi bấm "Chi tiết"

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---- WebSocket ------------------------------------------------------------

function send(obj) {
  try {
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
  } catch (e) {
    /* bỏ qua */
  }
}

function connect() {
  if (!wsPort) return;
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
  try {
    ws = new WebSocket("ws://localhost:" + wsPort);
  } catch (e) {
    return;
  }
  ws.onopen = () => send({ action: "ready" });
  ws.onclose = () => { ws = null; };
  ws.onerror = () => { /* onclose sẽ dọn */ };
  ws.onmessage = (ev) => {
    let cmd;
    try { cmd = JSON.parse(ev.data); } catch (e) { return; }
    handleCommand(cmd).catch((e) => send({ action: "error", message: String(e && e.message || e) }));
  };
}

// content.js gửi cổng ws (kèm tabId của trang /portal/shop).
chrome.runtime.onMessage.addListener((msg, sender) => {
  if (msg && msg.type === "hello" && msg.wsPort) {
    wsPort = msg.wsPort;
    if (sender.tab && sender.tab.id != null) listTabId = sender.tab.id;
    try { chrome.storage.session.set({ wsPort, listTabId }); } catch (e) {}
    connect();
  }
});

// Khi service worker khởi động lại (MV3 có thể ngủ) — nạp lại cổng đã lưu rồi nối lại.
try {
  chrome.storage.session.get(["wsPort", "listTabId"], (v) => {
    if (v && v.wsPort) {
      wsPort = v.wsPort;
      if (v.listTabId != null) listTabId = v.listTabId;
      connect();
    }
  });
} catch (e) {}

// ---- Hàm chạy trong trang (world MAIN) ------------------------------------
// (Port từ ScanShopListJs / OpenShopDetailAsync / FindToShipTitleAsync phía C#.)

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

// ---- Trusted click qua chrome.debugger (từ POC shopee-orders-test) --------

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

async function trustedClick(tabId, x, y) {
  const target = { tabId };
  await dbgAttach(target);
  try {
    const base = { x, y, button: "left" };
    await dbgSend(target, "Input.dispatchMouseEvent", { type: "mouseMoved", x, y, buttons: 0 });
    await sleep(70);
    await dbgSend(target, "Input.dispatchMouseEvent", { type: "mousePressed", ...base, buttons: 1, clickCount: 1 });
    await sleep(50);
    await dbgSend(target, "Input.dispatchMouseEvent", { type: "mouseReleased", ...base, buttons: 0, clickCount: 1 });
  } finally {
    // Nhả debugger sau ~1.5s (đủ để cú click kịp mở tab/điều hướng).
    await sleep(1500);
    await dbgDetach(target);
  }
}

// ---- Xử lý lệnh từ C# -----------------------------------------------------

async function handleCommand(cmd) {
  if (!cmd || !cmd.action) return;

  if (cmd.action === "readShopList") {
    if (listTabId == null) { send({ action: "error", message: "chưa biết tab /portal/shop" }); return; }
    const json = await execInTab(listTabId, pageScanShopList, []);
    send({ action: "pageData", kind: "shopList", data: json });
    return;
  }

  if (cmd.action === "openShopDetail") {
    await openShopDetail(String(cmd.shopId || "").replace(/'/g, ""));
    return;
  }

  if (cmd.action === "readToShip") {
    const tabId = shopTabId != null ? shopTabId : listTabId;
    if (tabId == null) { send({ action: "error", message: "chưa có tab shop để đọc" }); return; }
    // Poll ~8s để to-do box kịp render.
    const deadline = Date.now() + 8000;
    let raw = null;
    while (Date.now() < deadline) {
      try { raw = await execInTab(tabId, pageReadToShip, []); } catch (e) { raw = null; }
      if (raw != null) break;
      await sleep(400);
    }
    send({ action: "pageData", kind: "toShip", data: raw });
    return;
  }
}

async function openShopDetail(shopId) {
  if (listTabId == null) { send({ action: "error", message: "chưa biết tab /portal/shop" }); return; }

  // Danh sách tab banhang TRƯỚC click (để phát hiện tab mới mở ra).
  const before = (await chrome.tabs.query({ url: "https://banhang.shopee.vn/*" })).map((t) => t.id);

  // Cuộn nút vào giữa rồi đọc toạ độ (2 bước như POC: cuộn xong đợi một nhịp mới đo).
  const scrolled = await execInTab(listTabId, pageScrollDetailIntoView, [shopId]);
  if (!scrolled) { send({ action: "error", message: "không thấy nút Chi tiết của shop " + shopId }); return; }
  await sleep(350);
  const c = await execInTab(listTabId, pageLocateDetailRect, [shopId]);
  if (!c) { send({ action: "error", message: "không đọc được toạ độ nút Chi tiết" }); return; }

  await trustedClick(listTabId, c.x, c.y);

  // Chờ tab shop: (a) tab banhang MỚI, hoặc (b) tab danh sách tự điều hướng khỏi /portal/shop.
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

  // Chờ tab load xong (best-effort ~15s) rồi đọc URL để bắt trang verify/captcha.
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

  if (/\/verify/i.test(url)) {
    send({ action: "captcha", message: url });
    return;
  }
  send({ action: "progress", message: "đã mở shop " + shopId });
}
