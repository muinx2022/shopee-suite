// Service worker — CẦU NỐI extension↔C# cho module Đơn hàng (GĐ1 + GĐ2).
//
// Kênh lệnh/dữ liệu: WebSocket tới ws://localhost:<port> (C# chạy OrdersWebSocketServer). Cổng lấy từ hash
// #_od_ws=<port> mà C# nhúng vào URL trang đầu (subaccount.shopee.com ở GĐ2 / /portal/shop ở GĐ1); content.js
// đọc rồi gửi sang đây ('hello'). Kênh input "thật": chrome.debugger (Input.dispatchMouseEvent / dispatchKeyEvent)
// — trusted click + trusted type, đã chứng minh né captcha ở GĐ0. KHÔNG mở --remote-debugging-port.
//
// Protocol:
//   C#  -> ext:  GĐ1: {action:"readShopList"} | {action:"openShopDetail", shopId} | {action:"readToShip"}
//                GĐ2: {action:"login", user, pass} | {action:"checkLogin"} | {action:"gotoSellerCentre"}
//   ext -> C#:   {action:"ready"}
//                {action:"loginStatus", state:"loggedIn"|"needCode"|"pending"}   (đáp checkLogin)
//                {action:"atSellerCentre"}                                        (gotoSellerCentre xong)
//                {action:"shopOpened"}                                            (openShopDetail xong)
//                {action:"pageData", kind:"shopList", data:<json-array-string>}
//                {action:"pageData", kind:"toShip",   data:<raw-item-title-string>}
//                {action:"progress", message}                                     (chỉ để log)
//                {action:"captcha",  message}                                     (rơi vào trang /verify)
//                {action:"error",    message}

let ws = null;
let wsPort = null;
let listTabId = null; // tab đang thao tác (subaccount → sau SSO là tab banhang /portal/shop)
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
    handleCommand(cmd).catch((e) => send({ action: "error", message: String((e && e.message) || e) }));
  };
}

// content.js gửi cổng ws (kèm tabId của trang đầu = tab thao tác ban đầu).
chrome.runtime.onMessage.addListener((msg, sender) => {
  if (msg && msg.type === "hello" && msg.wsPort) {
    wsPort = msg.wsPort;
    if (sender.tab && sender.tab.id != null) listTabId = sender.tab.id;
    try { chrome.storage.session.set({ wsPort, listTabId }); } catch (e) {}
    connect();
  }
});

// Service worker khởi động lại (MV3 có thể ngủ) → nạp lại cổng đã lưu rồi nối lại.
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

// Đọc toạ độ TÂM ô user/pass + nút "Đăng nhập" của form subaccount (đúng SubUserSelectors/SubPassSelectors/
// SubSubmitSelectors + SignInRegex phía C#). KHÔNG cuộn (form gọn, mọi phần tử đã hiển thị) — tránh lệch toạ độ.
function pageLocateLoginForm() {
  function firstVisible(sels) {
    for (const sel of sels) {
      const els = document.querySelectorAll(sel);
      for (const el of els) {
        const r = el.getBoundingClientRect();
        if (r.width > 0 && r.height > 0) return el;
      }
    }
    return null;
  }
  function center(el) {
    if (!el) return null;
    const r = el.getBoundingClientRect();
    return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) };
  }
  const userEl = firstVisible([".login-card input[type='text']", "input[placeholder*='Tên đăng nhập']", "input[placeholder*='SĐT']", "input[type='text']"]);
  const passEl = firstVisible([".login-card input[type='password']", "input[type='password']"]);
  let submitEl = null;
  const cands = document.querySelectorAll(".login-card button.shopee-button--primary, button.shopee-button--primary, button, [role='button']");
  const re = /sign\s*in|đăng nhập|dang nhap/i;
  for (const b of cands) {
    const t = (b.textContent || "").replace(/\s+/g, " ").trim();
    if (re.test(t)) {
      const r = b.getBoundingClientRect();
      if (r.width > 0 && r.height > 0) { submitEl = b; break; }
    }
  }
  return { user: center(userEl), pass: center(passEl), submit: center(submitEl) };
}

// Trạng thái đăng nhập subaccount: 'loggedIn' (nav "Tài khoản của tôi") / 'needCode' (trang/ô nhập mã) / 'pending'.
function pageLoginStatus() {
  function na(s) {
    const nf = (s || "").replace(/\s+/g, " ").trim().toLowerCase().normalize("NFD");
    let out = "";
    for (const ch of nf) {
      const c = ch.charCodeAt(0);
      if (c >= 0x300 && c <= 0x36f) continue; // bỏ dấu thanh (combining marks)
      out += ch === "đ" ? "d" : ch;
    }
    return out;
  }
  const myRe = /tai khoan cua toi|my account/;
  const els = document.querySelectorAll("li, a, div, span, [role='menuitem']");
  for (const el of els) {
    const t = na(el.textContent);
    if (t && myRe.test(t)) {
      const r = el.getBoundingClientRect();
      if (r.width > 0 && r.height > 0) return "loggedIn";
    }
  }
  const url = (location.href || "").toLowerCase();
  if (url.indexOf("verify") >= 0 || url.indexOf("/otp") >= 0) return "needCode";
  const inputs = document.querySelectorAll("input");
  const codeRe = /ma xac|verification code|nhap ma|otp|ma xac minh|ma xac thuc|enter code/;
  for (const inp of inputs) {
    const ph = na((inp.getAttribute("placeholder") || "") + " " + (inp.getAttribute("aria-label") || ""));
    if (ph && codeRe.test(ph)) {
      const r = inp.getBoundingClientRect();
      if (r.width > 0 && r.height > 0) return "needCode";
    }
  }
  return "pending";
}

// Tìm phần tử khớp text (chuẩn hoá KHÔNG dấu) trong danh sách selector → toạ độ TÂM (đã cuộn vào giữa). null nếu không thấy.
function pageLocateByText(selectors, reSrc) {
  function na(s) {
    const nf = (s || "").replace(/\s+/g, " ").trim().toLowerCase().normalize("NFD");
    let out = "";
    for (const ch of nf) {
      const c = ch.charCodeAt(0);
      if (c >= 0x300 && c <= 0x36f) continue; // bỏ dấu thanh (combining marks)
      out += ch === "đ" ? "d" : ch;
    }
    return out;
  }
  const re = new RegExp(reSrc);
  for (const sel of selectors) {
    const els = document.querySelectorAll(sel);
    for (const el of els) {
      const t = na(el.textContent);
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

// code/vk cho ký tự ASCII (port KeyInfo của CdpInputController).
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

// Cú click trusted self-contained (tự attach/detach) — dùng cho openShopDetail + SSO click.
async function trustedClick(tabId, x, y) {
  await withDebugger(tabId, async (target) => { await dbgClick(target, x, y); });
}

// ---- Xử lý lệnh từ C# -----------------------------------------------------

async function handleCommand(cmd) {
  if (!cmd || !cmd.action) return;

  switch (cmd.action) {
    case "login":            await doLogin(cmd); return;
    case "checkLogin":       await doCheckLogin(); return;
    case "gotoSellerCentre": await gotoSellerCentre(); return;
    case "readShopList":     await doReadShopList(); return;
    case "openShopDetail":   await openShopDetail(String(cmd.shopId || "").replace(/'/g, "")); return;
    case "readToShip":       await doReadToShip(); return;
  }
}

// GĐ2: điền form đăng nhập subaccount + submit (trusted).
async function doLogin(cmd) {
  if (listTabId == null) { send({ action: "error", message: "chưa biết tab subaccount" }); return; }
  const loc = await execInTab(listTabId, pageLocateLoginForm, []);
  if (!loc || !loc.user || !loc.pass) {
    send({ action: "error", message: "không thấy ô đăng nhập subaccount" });
    return;
  }
  await withDebugger(listTabId, async (target) => {
    await dbgClick(target, loc.user.x, loc.user.y);
    await sleep(150);
    await dbgType(target, String(cmd.user || ""));
    await sleep(200);
    await dbgClick(target, loc.pass.x, loc.pass.y);
    await sleep(150);
    await dbgType(target, String(cmd.pass || ""));
    await sleep(200);
    if (loc.submit) { await dbgClick(target, loc.submit.x, loc.submit.y); }
    else { await dbgEnter(target); }
  });
  send({ action: "progress", message: "đã điền + gửi form đăng nhập subaccount" });
}

// GĐ2: một lần đọc trạng thái đăng nhập (C# poll lặp lại).
async function doCheckLogin() {
  if (listTabId == null) { send({ action: "loginStatus", state: "pending" }); return; }
  let st = "pending";
  try { st = (await execInTab(listTabId, pageLoginStatus, [])) || "pending"; } catch (e) { st = "pending"; }
  send({ action: "loginStatus", state: st });
}

// GĐ2: SSO "Tài khoản của tôi" → "Kênh Người bán" → tab banhang → /portal/shop (port TryLoginSubaccountAsync 6-8).
async function gotoSellerCentre() {
  if (listTabId == null) { send({ action: "error", message: "chưa biết tab subaccount" }); return; }

  const acc = await execInTab(listTabId, pageLocateByText, [["li", "a", "div", "span", "[role='menuitem']"], "tai khoan cua toi|my account"]);
  if (!acc) { send({ action: "error", message: "không thấy 'Tài khoản của tôi'" }); return; }
  await trustedClick(listTabId, acc.x, acc.y);
  await sleep(2200);

  const before = (await chrome.tabs.query({ url: "https://banhang.shopee.vn/*" })).map((t) => t.id);

  const seller = await execInTab(listTabId, pageLocateByText, [["span.entry-text", ".entry", "span", "div", "[role='button']", "a"], "kenh nguoi ban|seller\\s*cent(re|er)|seller\\s*channel"]);
  if (!seller) { send({ action: "error", message: "không thấy 'Kênh Người bán'" }); return; }
  await trustedClick(listTabId, seller.x, seller.y);

  // Chờ tab banhang: (a) tab MỚI, hoặc (b) tab subaccount tự điều hướng sang banhang.
  const deadline = Date.now() + 90000;
  let found = null;
  const subTabId = listTabId;
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

  // Chuyển "tab thao tác" sang banhang; đóng tab subaccount nếu là tab riêng.
  listTabId = found.id;
  shopTabId = null;
  if (subTabId !== listTabId) { try { await chrome.tabs.remove(subTabId); } catch (e) {} }

  await waitTabComplete(listTabId, 15000);
  let url = "";
  try { url = (await chrome.tabs.get(listTabId)).url || ""; } catch (e) {}
  if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }

  // Về bảng shop (/portal/shop) để lệnh readShopList đọc được tr[data-row-key].
  if (url.indexOf("/portal/shop") < 0) {
    try { await chrome.tabs.update(listTabId, { url: "https://banhang.shopee.vn/portal/shop" }); } catch (e) {}
    await waitTabComplete(listTabId, 20000);
    try { url = (await chrome.tabs.get(listTabId)).url || ""; } catch (e) {}
    if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }
  }

  send({ action: "atSellerCentre" });
}

// GĐ1: đọc danh sách shop.
async function doReadShopList() {
  if (listTabId == null) { send({ action: "error", message: "chưa biết tab /portal/shop" }); return; }
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
  if (listTabId == null) { send({ action: "error", message: "chưa biết tab /portal/shop" }); return; }

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
