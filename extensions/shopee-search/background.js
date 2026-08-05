// Entry service worker (MV3) của Shopee Search: đăng ký listener ở TOP-LEVEL rồi chạy flow category.
// Thân code nằm ở các module: core (state/WS/CDP), tabs, detect, crawl, extract, page-funcs, flow-category.
import { ctx, bridge, connectWs, log, resolveGesture, setAppMessageHandler, stopSearch, DEFAULT_WS_PORT } from './core.js';
import { startCategoryFromLink } from './flow-category.js';

// â”€â”€ Service worker keep-alive â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
chrome.alarms.create('keepAlive', { periodInMinutes: 0.4 });
chrome.alarms.onAlarm.addListener(() => chrome.storage.local.get('_'));

// â”€â”€ Message handler â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
function handleMessage(msg) {
  if (msg.kind === 'cdpInputAck') { resolveGesture(msg); return; }
  console.log('[SS] recv:', msg.action);
  switch (msg.action) {
    // Phía C# chỉ tạo SearchConfig với Mode="categoryFromLink" (FileRunCoordinator) — flow duy nhất còn sống.
    case 'start':  startCategoryFromLink(msg); break;
    case 'stop':   stopSearch();     break;
  }
}
setAppMessageHandler(handleMessage);

// â”€â”€ Detect initial tab (from Brave launch URL) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (changeInfo.status !== 'complete') return;
  if (!tab.url?.includes('shopee.vn')) return;
  if (tab.url.includes('shopee.vn/api/')) {
    chrome.tabs.remove(tabId).catch(() => {});
    return;
  }
  const match = tab.url.match(/#.*_ss_ws=(\d+)/);
  if (match) {
    const port = parseInt(match[1]);
    ctx.initialTabId = tabId;
    log(`Port ${port}, tabId=${tabId}`);
    // Reconnect if the port changed OR the socket isn't actually open — after a service
    // worker restart the bridge port may have reset to 9111 while no fresh 'complete' fires.
    if (port && (port !== bridge.port || !bridge.isOpen())) connectWs(port);
  }
});

// Restore the lane's WS port across service-worker restarts. A plain connectWs(DEFAULT)
// here would pin the SW to 9111; if the lane's shopee tab already finished loading there's
// no new 'complete' event to re-point it to the lane port → permanent "waiting for extension".
chrome.storage.local.get('_wsPort', (data) => {
  const p = data && data._wsPort ? parseInt(data._wsPort) : DEFAULT_WS_PORT;
  connectWs(p || DEFAULT_WS_PORT);
});
