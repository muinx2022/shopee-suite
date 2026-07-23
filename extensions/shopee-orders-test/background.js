// Service worker: bắn TRUSTED click qua chrome.debugger (Input.dispatchMouseEvent) — event có
// isTrusted=true như người bấm thật. KHÔNG enable Runtime/Page (chỉ gửi Input) → tránh dấu vết
// "console side-channel" của Playwright. Nhận toạ độ từ content script, click tại đó, ack lại.

function sendCmd(target, method, params) {
  return new Promise((resolve, reject) => {
    chrome.debugger.sendCommand(target, method, params || {}, () => {
      const e = chrome.runtime.lastError;
      if (e) reject(new Error(e.message)); else resolve();
    });
  });
}
function attach(target) {
  return new Promise((resolve, reject) => {
    chrome.debugger.attach(target, '1.3', () => {
      const e = chrome.runtime.lastError;
      if (e) reject(new Error(e.message)); else resolve();
    });
  });
}
function detach(target) {
  return new Promise((resolve) => { chrome.debugger.detach(target, () => resolve()); });
}
const sleep = ms => new Promise(r => setTimeout(r, ms));

async function trustedClick(tabId, x, y) {
  const target = { tabId };
  await attach(target);
  try {
    const base = { x, y, button: 'left' };
    // move → press → release (mô phỏng một cú click chuột thật, tất cả isTrusted=true).
    await sendCmd(target, 'Input.dispatchMouseEvent', { type: 'mouseMoved', x, y, buttons: 0 });
    await sleep(70);
    await sendCmd(target, 'Input.dispatchMouseEvent', { type: 'mousePressed', ...base, buttons: 1, clickCount: 1 });
    await sleep(50);
    await sendCmd(target, 'Input.dispatchMouseEvent', { type: 'mouseReleased', ...base, buttons: 0, clickCount: 1 });
    return { ok: true };
  } finally {
    // Nhả debugger sau ~1.5s (đủ để cú click kịp mở tab/điều hướng trước khi banner biến mất).
    await sleep(1500);
    await detach(target);
  }
}

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg && msg.cmd === 'trustedClick' && sender.tab && sender.tab.id != null) {
    trustedClick(sender.tab.id, msg.x, msg.y)
      .then(r => sendResponse(r))
      .catch(e => sendResponse({ ok: false, error: String(e) }));
    return true; // async response
  }
});
