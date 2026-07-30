// Lõi service worker Shopee Search: trạng thái dùng chung, cầu WS tới app C#, kênh cử chỉ CDP,
// nhịp nghỉ theo phiên. Mọi module khác import từ đây; core KHÔNG import ngược module nào.
// shared/ là BẢN COPY của extensions/shared/ — sửa ở nguồn chuẩn rồi chạy extensions/sync-shared.cmd.
import { createWsBridge } from './shared/ws-bridge.js';
import { pacedSleep } from './shared/util.js';

export const DEFAULT_WS_PORT = 9111;

// Trạng thái dùng chung của lane search. Gom vào MỘT object vì binding import không gán lại được
// từ module khác — mọi nơi đọc/ghi qua ctx.* là cùng một ô nhớ.
export const ctx = {
  searchTabId: null,
  initialTabId: null,
  searchState: null,
};

// â”€â”€ WebSocket â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
// Reconnect model lives in shared/ws-bridge.js (guard against a second socket, cancel stale
// reconnect timers, per-socket identity checks). Only the search-specific bits stay here.

// Gói tin từ app do entry (background.js) phân nhánh — đăng ký qua đây để core khỏi import ngược flow.
let appMessageHandler = () => {};
export function setAppMessageHandler(fn) { appMessageHandler = fn; }

export const bridge = createWsBridge({
  defaultPort: DEFAULT_WS_PORT,
  reconnectDelayMs: 3000,
  onOpen: b => { console.log('[SS] WS connected'); b.send({ action: 'ready' }); },
  onMessage: msg => appMessageHandler(msg),
  onClose: () => {
    // Reject every in-flight CDP gesture so its caller falls through to the synthetic
    // fallback immediately, instead of each one hanging the full 30s cdpGesture timeout.
    _gestPending.forEach(entry => {
      try { clearTimeout(entry.timer); } catch (_) {}
      try { entry.reject(new Error('ws closed')); } catch (_) {}
    });
    _gestPending.clear();
  },
  // Persist this lane's port so a service-worker restart (MV3 kills the SW after ~30s)
  // can restore it instead of resetting to DEFAULT_WS_PORT (9111) and hanging.
  onPortChange: port => { try { chrome.storage.local.set({ _wsPort: port }); } catch (_) {} },
});

export function connectWs(port) {
  bridge.connect(port);
}
export function send(obj) {
  bridge.send(obj);
}
export function log(msg) {
  console.log('[SS]', msg);
  send({ action: 'progress', message: msg });
}

export function reportNetworkError(message) {
  if (ctx.searchState) {
    if (ctx.searchState.networkErrorDetected) return;
    ctx.searchState.networkErrorDetected = true;
  }
  send({ action: 'networkError', message });
}

// â”€â”€ CDP trusted-input gesture channel â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
// Send one gesture (moveTo/click/wheel/type/pressKey) to the C# app, which executes
// it as a TRUSTED event via CDP, and await the ack. A nack/timeout rejects so the
// caller can fall back to synthetic JS dispatch.
let _gid = 0;
const _gestPending = new Map();

export function cdpGesture(op) {
  return new Promise((resolve, reject) => {
    if (!bridge.isOpen()) { reject(new Error('ws not open')); return; }
    const id = ++_gid;
    const timer = setTimeout(() => {
      _gestPending.delete(id);
      reject(new Error('cdpGesture timeout: ' + op.op));
    }, 30000);
    _gestPending.set(id, { resolve, reject, timer });
    send({ kind: 'cdpInput', id, ...op });
  });
}

export function resolveGesture(msg) {
  const entry = _gestPending.get(msg.id);
  if (!entry) return;
  clearTimeout(entry.timer);
  _gestPending.delete(msg.id);
  if (msg.ok) entry.resolve(true);
  else entry.reject(new Error(msg.error || 'cdp nack'));
}

// Click an element via CDP given its center coordinates; resolves true on success.
// Coordinates come from the page (getBoundingClientRect), already viewport CSS px.
export async function cdpClickAt(x, y) {
  await cdpGesture({ op: 'click', x, y, button: 'left', clickCount: 1 });
  return true;
}

export function stopSearch() {
  if (ctx.searchState) ctx.searchState.stopped = true;
  ctx.searchState = null;
}

// Hệ số tốc-độ-phiên: mỗi lần SW khởi động chọn 1 nhịp riêng (≈0.8-1.5×) → mọi quãng chờ HÀNH VI
// co giãn nhất quán theo phiên, diệt tính bất biến cross-session/account. KHÔNG áp cho timeout chờ load.
let sessionPace = 0.8 + Math.random() * 0.7;
{ const h = new Date().getHours(); if (h >= 23 || h < 6) sessionPace *= 1.15; }   // đêm chậm hơn chút
// Quãng chờ hành vi: nhân sessionPace (KHÔNG dùng cho timeout hệ thống). rand/randInt/clamp: shared/util.js.
export const sleep = pacedSleep(() => sessionPace);
