// Trusted input qua chrome.debugger (mouse) — mẫu lấy từ shopee-orders (bản đang chạy thật).
//
// NGUỒN CHUẨN: sửa Ở ĐÂY rồi chạy `extensions/sync-shared.cmd` (hoặc .sh).
//
// Hiện chỉ shopee-orders dùng: shopee-search bắn cử chỉ qua WebSocket cho C# thực thi bằng CDP
// (không phải chrome.debugger), shopee-scrape không cần trusted input.
//
// Cần quyền "debugger" trong manifest của extension nào import module này.

import { sleep } from "./util.js";

export function dbgSend(target, method, params) {
  return new Promise((resolve, reject) => {
    chrome.debugger.sendCommand(target, method, params || {}, () => {
      const e = chrome.runtime.lastError;
      if (e) reject(new Error(e.message)); else resolve();
    });
  });
}

export function dbgAttach(target) {
  return new Promise((resolve, reject) => {
    chrome.debugger.attach(target, "1.3", () => {
      const e = chrome.runtime.lastError;
      if (e) reject(new Error(e.message)); else resolve();
    });
  });
}

export function dbgDetach(target) {
  return new Promise((resolve) => { chrome.debugger.detach(target, () => resolve()); });
}

/** Cú click chuột trusted (giả định debugger ĐÃ attach). */
export async function dbgClick(target, x, y, opts = {}) {
  const { moveDelayMs = 70, pressDelayMs = 50 } = opts;
  await dbgSend(target, "Input.dispatchMouseEvent", { type: "mouseMoved", x, y, buttons: 0 });
  await sleep(moveDelayMs);
  await dbgSend(target, "Input.dispatchMouseEvent", { type: "mousePressed", x, y, button: "left", buttons: 1, clickCount: 1 });
  await sleep(pressDelayMs);
  await dbgSend(target, "Input.dispatchMouseEvent", { type: "mouseReleased", x, y, button: "left", buttons: 0, clickCount: 1 });
}

// Giữ chrome.debugger ATTACH XUYÊN SUỐT (KHÔNG attach/detach từng cú, cũng KHÔNG detach ở cuối mỗi lệnh) — vì:
// (1) banner "đang gỡ lỗi" hết nhấp nháy; (2) mọi toạ độ (đọc qua executeScript) + cú click ở CÙNG trạng thái
// banner → click không trượt. Gọi ensureDbg(tab) đầu mỗi lệnh nhiều-click (TRƯỚC khi đọc toạ độ); chỉ detach
// khi ĐỔI sang tab khác (ngay trong ensureDbg) hoặc khi Chrome tự detach.
let _dbgTab = null;

export async function ensureDbg(tabId) {
  if (_dbgTab === tabId) return;
  if (_dbgTab != null) { try { await dbgDetach({ tabId: _dbgTab }); } catch (e) {} _dbgTab = null; }
  try { await dbgAttach({ tabId }); } catch (e) { /* có thể đã attach sẵn — coi như ok */ }
  _dbgTab = tabId;
}

// Debugger tự detach (điều hướng trang / user bấm "Huỷ" trên banner) → reset để cú click sau tự attach lại.
try {
  chrome.debugger.onDetach.addListener((source) => {
    if (_dbgTab != null && source && source.tabId === _dbgTab) _dbgTab = null;
  });
} catch (e) {}

/** Cú click trusted — tự bảo đảm debugger đã attach, KHÔNG detach sau mỗi cú. */
export async function trustedClick(tabId, x, y) {
  await ensureDbg(tabId);
  await dbgClick({ tabId }, x, y);
}
