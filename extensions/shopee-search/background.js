// â”€â”€ Config â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
const DEFAULT_WS_PORT = 9111;
const DELAY_MS        = 3000;

// â”€â”€ State â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
let ws          = null;
let wsPort      = DEFAULT_WS_PORT;
let searchTabId = null;
let initialTabId= null;
let searchState = null;

// â”€â”€ Service worker keep-alive â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
chrome.alarms.create('keepAlive', { periodInMinutes: 0.4 });
chrome.alarms.onAlarm.addListener(() => chrome.storage.local.get('_'));

// â”€â”€ WebSocket â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
function connectWs(port) {
  wsPort = port || DEFAULT_WS_PORT;
  // Persist this lane's port so a service-worker restart (MV3 kills the SW after ~30s)
  // can restore it instead of resetting to DEFAULT_WS_PORT (9111) and hanging.
  try { chrome.storage.local.set({ _wsPort: wsPort }); } catch (_) {}
  if (ws) {
    // Intentional replacement: detach onclose first so the old socket's close
    // handler doesn't schedule a duplicate reconnect 3s later.
    const old = ws;
    old.onclose = null;
    old.onerror = null;
    try { old.close(); } catch (_) {}
  }
  const sock = new WebSocket(`ws://localhost:${wsPort}`);
  ws = sock;
  sock.onopen  = () => { if (ws !== sock) return; console.log('[SS] WS connected'); send({ action: 'ready' }); };
  sock.onmessage = evt => { if (ws !== sock) return; try { handleMessage(JSON.parse(evt.data)); } catch (_) {} };
  sock.onclose = () => { if (ws !== sock) return; ws = null; setTimeout(() => connectWs(wsPort), 3000); };
  sock.onerror = () => {};
}
function send(obj) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
}
function log(msg) {
  console.log('[SS]', msg);
  send({ action: 'progress', message: msg });
}

function reportNetworkError(message) {
  if (searchState) {
    if (searchState.networkErrorDetected) return;
    searchState.networkErrorDetected = true;
  }
  send({ action: 'networkError', message });
}

// â”€â”€ CDP trusted-input gesture channel â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
// Send one gesture (moveTo/click/wheel/type/pressKey) to the C# app, which executes
// it as a TRUSTED event via CDP, and await the ack. A nack/timeout rejects so the
// caller can fall back to synthetic JS dispatch.
let _gid = 0;
const _gestPending = new Map();

function cdpGesture(op) {
  return new Promise((resolve, reject) => {
    if (!ws || ws.readyState !== WebSocket.OPEN) { reject(new Error('ws not open')); return; }
    const id = ++_gid;
    const timer = setTimeout(() => {
      _gestPending.delete(id);
      reject(new Error('cdpGesture timeout: ' + op.op));
    }, 30000);
    _gestPending.set(id, { resolve, reject, timer });
    send({ kind: 'cdpInput', id, ...op });
  });
}

function resolveGesture(msg) {
  const entry = _gestPending.get(msg.id);
  if (!entry) return;
  clearTimeout(entry.timer);
  _gestPending.delete(msg.id);
  if (msg.ok) entry.resolve(true);
  else entry.reject(new Error(msg.error || 'cdp nack'));
}

// Click an element via CDP given its center coordinates; resolves true on success.
// Coordinates come from the page (getBoundingClientRect), already viewport CSS px.
async function cdpClickAt(x, y) {
  await cdpGesture({ op: 'click', x, y, button: 'left', clickCount: 1 });
  return true;
}

// â”€â”€ Message handler â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
function handleMessage(msg) {
  if (msg.kind === 'cdpInputAck') { resolveGesture(msg); return; }
  console.log('[SS] recv:', msg.action);
  switch (msg.action) {
    case 'start':
      if (msg.mode === 'shopFromLink') startShopFromLink(msg);
      else if (msg.mode === 'categoryFromLink') startCategoryFromLink(msg);
      else startSearch(msg);
      break;
    case 'stop':   stopSearch();     break;
    case 'pause':  if (searchState) searchState.paused = true;  break;
    case 'resume': if (searchState) searchState.paused = false; break;
  }
}

// Block at a safe point while paused, without killing the run. Returns when resumed,
// or immediately if the run was stopped/errored/replaced meanwhile.
async function waitWhilePaused(state) {
  if (!state.paused) return;
  log('Đã tạm dừng. Chờ tiếp tục...');
  while (state.paused && state === searchState && !state.stopped && !state.networkErrorDetected) {
    await sleep(400);
  }
  if (state === searchState && !state.stopped && !state.networkErrorDetected) {
    log('Tiếp tục chạy.');
  }
}

// â”€â”€ Search â€” type keyword + Enter, collect DOM data â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
async function startSearch(msg) {
  stopSearch();
  const { keyword, filters } = msg;
  const resumeCategoryIndex = Math.max(1, Number(msg.resumeCategoryIndex || 1));
  // Page to resume at WITHIN the resumed category (account swap continues here, not page 1).
  const resumePage = Math.max(1, Number(msg.resumePage || 1));
  // Capture a local run state and bind it as the global current run.
  // `dead()` is true once this run is no longer the active one (a newer
  // startSearch replaced it), was stopped, or hit a network error — every
  // await below re-checks it so a stale/zombie run exits instead of fighting
  // a newer one over the same tab.
  const state = {
    keyword, filters, resumeCategoryIndex,
    stopped: false, paused: false, networkErrorDetected: false, captchaDetected: false,
  };
  searchState = state;
  const dead = () => state !== searchState || state.stopped || state.networkErrorDetected;

  await closeApiTabs();
  if (dead()) return;

  // Use initial warm tab or create one
  searchTabId = await resolveSearchTab();
  if (dead()) return;
  if (!searchTabId) {
    const t = await chrome.tabs.create({ url: 'https://shopee.vn/', active: true });
    searchTabId = t.id;
    await waitForTabLoad(searchTabId);
    if (dead()) return;
  }
  await closeOtherTabs(searchTabId);
  if (dead()) return;

  // â”€â”€ Step 1: navigate to Shopee homepage â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  log('Mở trang chủ Shopee...');
  await chrome.tabs.update(searchTabId, { url: 'https://shopee.vn/' });
  await waitForTabLoad(searchTabId);
  await sleep(1400 + Math.random() * 1300);
  if (dead()) return;

  // Step 2: wait 5-7s before typing
  const waitMs = 5000 + Math.floor(Math.random() * 2000);
  log(`Chờ ${(waitMs/1000).toFixed(1)}s trước khi nhập...`);
  await sleep(waitMs);
  if (dead()) return;

  log(`Nhập từ khóa: "${keyword}"`);
  const typed = await typeAndSearch(keyword);
  if (dead()) return;
  if (!typed) {
    log('Không tìm thấy ô search - fallback navigate URL');
    await chrome.tabs.update(searchTabId, {
      url: `https://shopee.vn/search?keyword=${encodeURIComponent(keyword)}&by=sales&order=desc`,
    });
    if (dead()) return;
  }

  // â”€â”€ Step 3: wait for search results page â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  log('Chờ trang kết quả tải...');
  await sleep(1000);
  if (dead()) return;
  if (await isNetworkErrorPage()) {
    reportNetworkError('Shopee không tải được, có thể proxy timeout.');
    return;
  }
  if (await isVerifyPage()) {
    if (dead()) return;
    state.captchaDetected = true;
    send({ action: 'captcha' });
    return;
  }
  const loaded = await waitForUrl('/search', 10000);
  if (dead()) return;
  if (!loaded) {
    log('Enter did not navigate to search; opening search URL fallback...');
    await chrome.tabs.update(searchTabId, { url: buildSearchUrl(keyword) });
  }
  await waitForTabLoad(searchTabId);
  await sleep(3000); // let React render products
  if (dead()) return;
  if (await isNetworkErrorPage()) {
    reportNetworkError('Shopee không tải được sau khi search.');
    return;
  }
  if (await isVerifyPage()) {
    if (dead()) return;
    state.captchaDetected = true;
    send({ action: 'captcha' });
    return;
  }

  log('Prepare Shopee filters: sort by best-selling, scroll...');
  const prepResult = await prepareBestSelling();
  if (dead()) return;
  if (prepResult) {
    log(`Prepare done: clickedBestSelling=${prepResult.clickedBestSelling}, setPrice=${prepResult.setPrice}, fallbackNavigate=${prepResult.fallbackNavigate}, firstScrollSteps=${prepResult.firstScrollSteps}`);
  }
  await waitForTabLoad(searchTabId);
  await sleep(3000);
  if (dead()) return;

  const maxPages = 9;
  const baseSearchUrl = await getCurrentTabUrl();
  log('Collecting search categories...');
  const categories = await collectSearchCategories();
  if (dead()) return;
  log(`Found ${categories.length} categories.`);

  if (!categories.length) {
    await crawlPagesForCurrentState(state, keyword, '', 1, 1, maxPages, true, resumePage);
    if (dead()) return;
    send({ action: 'done' });
    return;
  }

  const startCategoryIndex = Math.min(categories.length, resumeCategoryIndex);
  if (startCategoryIndex > 1) {
    log(`Resume mode: skipping categories 1-${startCategoryIndex - 1}, start at category ${startCategoryIndex}.`);
  }

  for (let i = startCategoryIndex - 1; i < categories.length; i++) {
    await waitWhilePaused(state);
    if (dead()) return;
    const category = categories[i];
    log(`Category ${i + 1}/${categories.length}: ${category.name}`);
    await chrome.tabs.update(searchTabId, { url: baseSearchUrl });
    await waitForTabLoad(searchTabId);
    await sleep(2200 + Math.random() * 1300);
    if (dead()) return;
    if (await isNetworkErrorPage()) {
      reportNetworkError('Shopee không tải được khi mở lại category base URL.');
      return;
    }
    if (await isVerifyPage()) {
      if (dead()) return;
      state.captchaDetected = true;
      send({ action: 'captcha' });
      return;
    }

    const selected = await selectSearchCategory(category.value, category.name);
    if (dead()) return;
    log(`Category selected=${selected}: ${category.name}`);
    await waitForTabLoad(searchTabId);
    await sleep(3000 + Math.random() * 1800);
    if (dead()) return;
    if (await isNetworkErrorPage()) {
      reportNetworkError('Shopee không tải được sau khi chọn category.');
      return;
    }
    if (await isVerifyPage()) {
      if (dead()) return;
      state.captchaDetected = true;
      send({ action: 'captcha' });
      return;
    }

    // Only the first resumed category continues at resumePage; later categories start at page 1.
    const startPage = i === startCategoryIndex - 1 ? resumePage : 1;
    await crawlPagesForCurrentState(state, keyword, category.name, i + 1, categories.length, maxPages, i === categories.length - 1, startPage);
    if (dead() || state.captchaDetected) return;
  }

  if (dead()) return;
  send({ action: 'done' });
}

function stopSearch() {
  if (searchState) searchState.stopped = true;
  searchState = null;
}

// â”€â”€ Shop-from-link flow â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
// Open a product link → its shop → "All products" → sort "Top sales" → crawl product
// pages. The crawl/pagination part is shared with the keyword flow.
async function startShopFromLink(msg) {
  stopSearch();
  const link = msg.link || '';
  const state = {
    keyword: link, link, filters: msg.filters, resumeCategoryIndex: 1,
    stopped: false, paused: false, networkErrorDetected: false, captchaDetected: false,
    mode: 'shopFromLink',
  };
  searchState = state;
  const dead = () => state !== searchState || state.stopped || state.networkErrorDetected;

  await closeApiTabs();
  if (dead()) return;

  searchTabId = await resolveSearchTab();
  if (dead()) return;
  if (!searchTabId) {
    const t = await chrome.tabs.create({ url: 'https://shopee.vn/', active: true });
    searchTabId = t.id;
    await waitForTabLoad(searchTabId);
    if (dead()) return;
  }
  await closeOtherTabs(searchTabId);
  if (dead()) return;

  log('Mở link sản phẩm: ' + link);
  await chrome.tabs.update(searchTabId, { url: link });
  await waitForTabLoad(searchTabId);
  await sleep(2500 + Math.random() * 1500);
  if (dead()) return;
  if (await isNetworkErrorPage()) { reportNetworkError('Không tải được trang sản phẩm.'); return; }
  if (await isVerifyPage()) { state.captchaDetected = true; send({ action: 'captcha' }); return; }
  // Link chết (SP không tồn tại/đã xoá): báo TERMINAL để coordinator đánh dấu link và sang link
  // kế — KHÔNG networkError (sẽ đổi account + mở lại đúng link đó vô hạn → "máy mở đi mở lại").
  if (await isProductNotFoundPage()) { send({ action: 'error', message: 'Sản phẩm không tồn tại — bỏ qua link.' }); return; }

  await waitWhilePaused(state); if (dead()) return;
  log('Tìm và bấm "Xem shop"...');
  const okShop = await clickViewShop();
  if (dead()) return;
  if (!okShop) {
    // Trang đã tải xong, không phải verify/lỗi mạng, nhưng không có khối shop → gần như chắc chắn
    // SP không tồn tại. Chờ thêm 1 nhịp rồi kiểm tra lại để loại trừ trang load chậm; vẫn không có
    // thì báo TERMINAL (bỏ qua link) thay vì networkError (đổi account + lặp lại link chết).
    await sleep(1800);
    if (dead()) return;
    if (await isVerifyPage()) { state.captchaDetected = true; send({ action: 'captcha' }); return; }
    if (await isNetworkErrorPage()) { reportNetworkError('Không tải được trang sản phẩm.'); return; }
    if (await clickViewShop()) {
      // nhịp chờ thêm đã giúp tìm thấy nút — đi tiếp bình thường.
    } else {
      send({ action: 'error', message: 'Không mở được shop (sản phẩm có thể đã bị xoá) — bỏ qua link.' });
      return;
    }
  }
  await waitForTabLoad(searchTabId);
  await sleep(2500 + Math.random() * 1500);
  if (dead()) return;
  if (await isVerifyPage()) { state.captchaDetected = true; send({ action: 'captcha' }); return; }

  const shopName = await readShopName();
  if (shopName) { state.shopName = shopName; send({ action: 'shopInfo', name: shopName }); }

  await waitWhilePaused(state); if (dead()) return;
  log('Bấm "Tất cả sản phẩm"...');
  const okAll = await clickAllProducts();
  if (dead()) return;
  if (!okAll) log('Không thấy menu "Tất cả sản phẩm", tiếp tục với trang hiện tại.');
  await waitForTabLoad(searchTabId);
  await sleep(2500 + Math.random() * 1500);
  if (dead()) return;

  await waitWhilePaused(state); if (dead()) return;
  log('Sắp xếp theo "Bán chạy"...');
  await clickTopSalesShop();
  if (dead()) return;
  await waitForTabLoad(searchTabId);
  await sleep(3000 + Math.random() * 1800);
  if (dead()) return;
  if (await isVerifyPage()) { state.captchaDetected = true; send({ action: 'captcha' }); return; }

  // Quét toàn bộ "Tất cả sản phẩm" theo TRANG (cách cũ) — KHÔNG click danh mục shop nữa
  // (cây "Danh Mục" của shop là bộ sưu tập do shop tự đặt, không phải danh mục thật của Shopee).
  const maxPages = 50;
  await crawlPagesForCurrentState(state, link, '', 1, 1, maxPages, true);
  if (dead() || state.captchaDetected) return;
  send({ action: 'done' });
}

// ── Category-from-link flow ─────────────────────────────────────────────────
// Mở link CATEGORY → lặp qua MỌI danh mục con (sub-category) → mỗi sub: lọc "Nơi Bán"
// khớp khu vực + sắp "Bán chạy" → cào sản phẩm (tái dùng crawlPagesForCurrentState).
async function startCategoryFromLink(msg) {
  stopSearch();
  const link = msg.link || '';
  const region = (msg.region || '').trim();
  const state = {
    keyword: link, link, region, filters: msg.filters, resumeCategoryIndex: 1,
    stopped: false, paused: false, networkErrorDetected: false, captchaDetected: false,
    mode: 'categoryFromLink',
  };
  searchState = state;
  const dead = () => state !== searchState || state.stopped || state.networkErrorDetected;

  await closeApiTabs();
  if (dead()) return;
  searchTabId = await resolveSearchTab();
  if (dead()) return;
  if (!searchTabId) {
    const t = await chrome.tabs.create({ url: 'https://shopee.vn/', active: true });
    searchTabId = t.id;
    await waitForTabLoad(searchTabId);
    if (dead()) return;
  }
  await closeOtherTabs(searchTabId);
  if (dead()) return;

  // Mở TRANG CHỦ rồi CLICK danh mục (theo cat id trong link). Điều hướng SPA bằng click MỚI có rail
  // bộ lọc; mở URL category trực tiếp thì Shopee trả trang KHÔNG có bộ lọc.
  const catId = parseCatId(link);
  log('Mở trang chủ Shopee…');
  await chrome.tabs.update(searchTabId, { url: 'https://shopee.vn/' });
  await waitForTabLoad(searchTabId);
  await sleep(2500 + Math.random() * 1500);
  if (dead()) return;
  if (await isNetworkErrorPage()) { reportNetworkError('Không tải được trang chủ Shopee.'); return; }
  if (await isVerifyPage()) { state.captchaDetected = true; send({ action: 'captcha' }); return; }

  // Tắt popup quảng cáo (nếu có) trước khi click danh mục.
  await dismissHomePopups();
  await sleep(800 + Math.random() * 600);
  await dismissHomePopups();
  if (dead()) return;

  log(`Click danh mục (cat ${catId}) trên trang chủ…`);
  let clicked = false;
  for (let attempt = 0; attempt < 6 && !clicked; attempt++) {
    if (dead()) return;
    await dismissHomePopups();   // popup có thể hiện trễ
    const r = await clickHomeCategory(catId);
    if (r.ok) { clicked = true; log('Đã click danh mục: ' + (r.href || catId)); break; }
    await sleep(1200 + Math.random() * 800);
  }
  if (!clicked) {
    log('Không thấy danh mục trên trang chủ — mở link trực tiếp (có thể thiếu bộ lọc).');
    await chrome.tabs.update(searchTabId, { url: link });
  }
  // Click SPA không kích hoạt tab onComplete → chờ URL đổi sang trang category.
  await waitForUrl('-cat.', 12000);
  await sleep(2500 + Math.random() * 1500);
  if (dead()) return;
  if (await isVerifyPage()) { state.captchaDetected = true; send({ action: 'captcha' }); return; }
  if (await isNetworkErrorPage()) { reportNetworkError('Không tải được trang danh mục.'); return; }

  const subs = await collectSubCategories();
  if (dead()) return;

  if (!subs.length) {
    // Không thấy danh mục con → cào THẲNG trang danh mục hiện tại (lọc Nơi Bán + Bán chạy).
    log('Không thấy danh mục con — cào thẳng trang danh mục.');
    await applyLocationFilter(state, region); if (dead()) return;
    if (await isVerifyPage()) { state.captchaDetected = true; send({ action: 'captcha' }); return; }
    await waitWhilePaused(state); if (dead()) return;
    log('Sắp xếp "Bán chạy"...');
    await prepareBestSelling(); if (dead()) return;
    await waitForTabLoad(searchTabId);
    await sleep(2500 + Math.random() * 1500);
    if (dead()) return;
    await crawlPagesForCurrentState(state, link, '', 1, 1, 50, true);
    if (dead() || state.captchaDetected) return;
    send({ action: 'done' });
    return;
  }

  log(`Tìm thấy ${subs.length} danh mục con — lần lượt cào từng cái.`);
  for (let i = 0; i < subs.length; i++) {
    await waitWhilePaused(state); if (dead()) return;
    const sub = subs[i];
    log(`Danh mục con ${i + 1}/${subs.length}: ${sub.name}`);
    // CLICK danh mục con trong rail (SPA → giữ bộ lọc), KHÔNG nav URL trực tiếp.
    const subCatId = parseCatId(sub.href);
    const sc = await clickSubCategory(sub.href);
    if (!sc.ok) {
      log('Không click được danh mục con (rail) — mở URL trực tiếp.');
      await chrome.tabs.update(searchTabId, { url: sub.href });
    }
    await waitForUrl(subCatId || '-cat.', 12000);
    await sleep(2500 + Math.random() * 1500);
    if (dead()) return;
    if (await isVerifyPage()) { state.captchaDetected = true; send({ action: 'captcha' }); return; }
    if (await isNetworkErrorPage()) { reportNetworkError('Không tải được danh mục con: ' + sub.name); return; }

    // Lọc "Nơi Bán" khớp khu vực (nếu có nhập).
    await applyLocationFilter(state, region); if (dead()) return;
    if (await isVerifyPage()) { state.captchaDetected = true; send({ action: 'captcha' }); return; }

    // Sắp "Bán chạy" rồi cào.
    await waitWhilePaused(state); if (dead()) return;
    log('Sắp xếp "Bán chạy"...');
    await prepareBestSelling(); if (dead()) return;
    await waitForTabLoad(searchTabId);
    await sleep(2500 + Math.random() * 1500);
    if (dead()) return;
    if (await isVerifyPage()) { state.captchaDetected = true; send({ action: 'captcha' }); return; }

    await crawlPagesForCurrentState(state, sub.href, sub.name, i + 1, subs.length, 50, i === subs.length - 1);
    if (dead() || state.captchaDetected) return;
  }
  send({ action: 'done' });
}

// Thu thập danh mục con của category đang mở (mở rộng "Thêm" để lấy đủ).
async function collectSubCategories() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId }, world: 'MAIN',
      func: async () => {
        const sleep = ms => new Promise(r => setTimeout(r, ms));
        const norm = s => (s || '').replace(/\s+/g, ' ').trim();
        const list = document.querySelector('.shopee-category-list__sub-category-list');
        if (!list) return [];
        list.scrollIntoView({ block: 'center' });
        await sleep(500);
        // Mở rộng "Thêm" để lộ các danh mục con bị ẩn.
        const toggle = list.querySelector('.shopee-category-list__toggle-btn');
        if (toggle) { try { toggle.click(); await sleep(800); } catch (_) {} }
        const seen = new Set();
        return Array.from(list.querySelectorAll('a.shopee-category-list__sub-category'))
          .map(a => ({ name: norm(a.textContent || ''), href: a.href || '' }))
          .filter(x => x.name && x.href && !seen.has(x.href) && seen.add(x.href));
      },
    });
    return Array.isArray(res?.result) ? res.result : [];
  } catch (e) { log('collectSubCategories error: ' + e.message); return []; }
}

// Lấy chuỗi cat id trong link (vd .../-cat.11035567 → "11035567"; .../-cat.11035567.11035568 → "11035567.11035568").
function parseCatId(link) {
  const m = /-cat\.([\d.]+)/i.exec(link || '');
  return m ? m[1] : '';
}

// Tắt popup quảng cáo trên trang chủ Shopee (best-effort): click nút đóng đã biết + ESC.
async function dismissHomePopups() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId }, world: 'MAIN',
      func: () => {
        const sels = [
          '.shopee-popup__close-btn',
          '.home-popup__close-area',
          '.home-popup__close-btn',
          '.shopee-drawer__close-button',
          'div[role="dialog"] button[aria-label="close" i]',
          'div[role="dialog"] [class*="close" i]',
          '[class*="home-popup" i] [class*="close" i]',
          '[class*="popup" i] [class*="close" i]',
        ];
        let n = 0;
        for (const s of sels) {
          let els;
          try { els = document.querySelectorAll(s); } catch (_) { continue; }
          els.forEach(el => {
            try {
              const r = el.getBoundingClientRect();
              if (r.width > 0 && r.height > 0) { el.click(); n++; }
            } catch (_) {}
          });
        }
        try { document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, which: 27, bubbles: true })); } catch (_) {}
        return n;
      },
    });
    const n = res?.result || 0;
    if (n > 0) log(`Đã tắt ${n} popup.`);
  } catch (e) { log('dismissHomePopups error: ' + e.message); }
}

// Click danh mục trên trang chủ theo cat id (điều hướng SPA → có rail bộ lọc).
async function clickHomeCategory(catId) {
  try {
    const topId = String(catId || '').split('.')[0];
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId }, world: 'MAIN', args: [topId],
      func: (topId) => {
        const anchors = Array.from(document.querySelectorAll('a.home-category-list__category-grid, .home-category-list a[href*="-cat."]'));
        const idOf = a => { const m = /-cat\.(\d+)/.exec(a.getAttribute('href') || ''); return m ? m[1] : ''; };
        const a = anchors.find(x => idOf(x) === topId);
        if (!a) return { ok: false };
        try { a.scrollIntoView({ block: 'center', inline: 'center' }); } catch (_) {}
        a.click();   // SPA nav — giữ bộ lọc
        return { ok: true, href: a.href || '' };
      },
    });
    return res?.result ?? { ok: false };
  } catch (e) { log('clickHomeCategory error: ' + e.message); return { ok: false }; }
}

// Click 1 danh mục con trong rail trái (SPA → giữ bộ lọc), khớp theo href tuyệt đối.
async function clickSubCategory(href) {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId }, world: 'MAIN', args: [href],
      func: (href) => {
        const list = document.querySelector('.shopee-category-list__sub-category-list');
        if (list) {
          const toggle = list.querySelector('.shopee-category-list__toggle-btn');
          if (toggle) { try { toggle.click(); } catch (_) {} }
        }
        const anchors = Array.from(document.querySelectorAll('a.shopee-category-list__sub-category'));
        const a = anchors.find(x => x.href === href)
          || anchors.find(x => { const h = x.getAttribute('href') || ''; return h && href.endsWith(h); });
        if (!a) return { ok: false };
        try { a.scrollIntoView({ block: 'center' }); } catch (_) {}
        a.click();
        return { ok: true };
      },
    });
    return res?.result ?? { ok: false };
  } catch (e) { log('clickSubCategory error: ' + e.message); return { ok: false }; }
}

// Tìm fieldset "Nơi Bán", mở rộng "Thêm" nếu cần, trả về điểm cần click (toggle).
async function resolveLocationToggle() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId }, world: 'MAIN',
      func: () => {
        const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
        const fs = document.querySelector('fieldset.shopee-location-filter')
          || Array.from(document.querySelectorAll('fieldset.shopee-filter-group'))
            .find(g => /nơi bán|noi ban|location/i.test(norm(g.querySelector('legend, .shopee-filter-group__header')?.textContent || '')));
        if (!fs) return { ok: false };
        fs.scrollIntoView({ block: 'center' });
        const toggle = fs.querySelector('.shopee-filter-group__toggle-btn');
        if (!toggle || toggle.getAttribute('aria-expanded') === 'true') return { ok: true, needsExpand: false };
        const r = toggle.getBoundingClientRect();
        return { ok: true, needsExpand: true, x: r.left + r.width / 2, y: r.top + r.height / 2, dpr: window.devicePixelRatio };
      },
    });
    return res?.result ?? { ok: false };
  } catch (e) { log('resolveLocationToggle error: ' + e.message); return { ok: false }; }
}

// Tìm checkbox "Nơi Bán" khớp khu vực (so khớp kiểu contains, bỏ "Thành phố/Tỉnh/TP", EN/VN).
async function resolveLocationCheckboxPoint(region) {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId }, world: 'MAIN', args: [String(region || '')],
      func: (region) => {
        // Bỏ DẤU tiếng Việt + tiền tố "Thành phố/Tỉnh/City/Province" → "Hà Nội" / "Ha Noi" /
        // "Thành phố Hà Nội" / "Ha Noi City" đều về "ha noi" (khớp cả giao diện VN lẫn EN).
        const strip = s => (s || '')
          .normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/đ/g, 'd').replace(/Đ/g, 'D')
          .replace(/\s+/g, ' ').trim().toLowerCase()
          .replace(/^(thanh pho|tinh|tp\.?|city|province)\s+/i, '')
          .replace(/\s+(city|province)$/i, '').trim();
        const want = strip(region);
        if (!want) return { ok: false };
        const fs = document.querySelector('fieldset.shopee-location-filter')
          || Array.from(document.querySelectorAll('fieldset.shopee-filter-group'))
            .find(g => /nơi bán|noi ban|location/i.test((g.querySelector('legend, .shopee-filter-group__header')?.textContent || '').toLowerCase()));
        if (!fs) return { ok: false };
        const labels = Array.from(fs.querySelectorAll('.shopee-checkbox-filter label.shopee-checkbox, label.shopee-checkbox'));
        let best = null;
        for (const label of labels) {
          const input = label.querySelector('input[type="checkbox"]');
          const text = strip(label.querySelector('.shopee-checkbox__label')?.textContent || input?.value || label.textContent || '');
          if (!text) continue;
          if (text === want || text.includes(want) || want.includes(text)) { best = { label, text }; break; }
        }
        if (!best) return { ok: false };
        best.label.scrollIntoView({ block: 'center' });
        const r = best.label.getBoundingClientRect();
        return { ok: r.width > 0 && r.height > 0, label: best.text, x: r.left + (0.3 + Math.random() * 0.4) * r.width, y: r.top + (0.3 + Math.random() * 0.4) * r.height, dpr: window.devicePixelRatio };
      },
    });
    return res?.result ?? { ok: false };
  } catch (e) { log('resolveLocationCheckboxPoint error: ' + e.message); return { ok: false }; }
}

// Tick "Nơi Bán" khớp khu vực (CDP trusted click). Trống region → bỏ qua. Trả về true nếu đã tick.
async function applyLocationFilter(state, region) {
  if (!region) { log('Không nhập khu vực — bỏ qua lọc "Nơi Bán".'); return false; }
  const dead = () => state !== searchState || state.stopped || state.networkErrorDetected;
  try {
    const tog = await resolveLocationToggle();
    if (dead()) return false;
    if (tog.ok && tog.needsExpand) { await cdpClickAt(tog.x, tog.y); await sleep(800 + Math.random() * 400); }
    const pt = await resolveLocationCheckboxPoint(region);
    if (dead()) return false;
    if (!pt.ok) { log('Không thấy "Nơi Bán" khớp khu vực: ' + region); return false; }
    await sleep(400 + Math.random() * 300);
    await cdpClickAt(pt.x, pt.y);
    log('Đã tick "Nơi Bán": ' + (pt.label || region));
    await waitForTabLoad(searchTabId);
    await sleep(2500 + Math.random() * 1500);
    return true;
  } catch (e) { log('applyLocationFilter error: ' + e.message); return false; }
}

// Resolve + click an anchor (view shop / all products); fall back to navigating its
// href if the trusted click didn't change the URL.
async function clickResolvedAnchor(pt) {
  if (!pt.ok) return false;
  try {
    await cdpClickAt(pt.x, pt.y);
    await sleep(900 + Math.random() * 700);
    if (pt.href) {
      const [res] = await chrome.scripting.executeScript({
        target: { tabId: searchTabId }, world: 'MAIN', args: [pt.beforeUrl],
        func: (before) => location.href === before,
      });
      if (res?.result === true) await chrome.tabs.update(searchTabId, { url: pt.href });
    }
    return true;
  } catch (e) {
    log('Anchor click via CDP failed, navigate href: ' + e.message);
    if (pt.href) { await chrome.tabs.update(searchTabId, { url: pt.href }); return true; }
    return false;
  }
}

async function resolveViewShopPoint() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId }, world: 'MAIN',
      func: () => {
        const sec = document.querySelector('#sll2-pdp-product-shop') || document.querySelector('.page-product__shop');
        if (!sec) return { ok: false };
        sec.scrollIntoView({ block: 'center' });
        const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
        const anchors = Array.from(sec.querySelectorAll('a[href]'));
        let a = anchors.find(x => /xem shop|view shop/.test(norm(x.textContent)));
        if (!a) a = anchors.find(x => x.getAttribute('href'));
        if (!a) return { ok: false };
        const r = a.getBoundingClientRect();
        return { ok: r.width > 0 && r.height > 0, x: r.left + r.width / 2, y: r.top + r.height / 2, href: a.href || '', beforeUrl: location.href, dpr: window.devicePixelRatio };
      },
    });
    return res?.result ?? { ok: false };
  } catch (e) { log('resolveViewShopPoint error: ' + e.message); return { ok: false }; }
}

async function clickViewShop() {
  return clickResolvedAnchor(await resolveViewShopPoint());
}

// Read the shop name from the shop overview header (MAIN world).
async function readShopName() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId }, world: 'MAIN',
      func: () => {
        const el = document.querySelector('.section-seller-overview-horizontal__portrait-name')
          || document.querySelector('.fV3TIn');
        return el ? (el.textContent || '').replace(/\s+/g, ' ').trim() : '';
      },
    });
    return res?.result || '';
  } catch (e) { log('readShopName error: ' + e.message); return ''; }
}

async function resolveAllProductsPoint() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId }, world: 'MAIN',
      func: () => {
        const menu = document.querySelector('.shop-page-menu');
        if (!menu) return { ok: false };
        const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
        const items = Array.from(menu.querySelectorAll('a.navbar-with-more-menu__item, a[href]'));
        let a = items.find(x => /all products|tất cả sản phẩm/.test(norm(x.textContent)));
        if (!a) a = items.find(x => (x.getAttribute('href') || '').includes('#product_list'));
        if (!a) return { ok: false };
        a.scrollIntoView({ block: 'center' });
        const r = a.getBoundingClientRect();
        return { ok: r.width > 0 && r.height > 0, x: r.left + r.width / 2, y: r.top + r.height / 2, href: a.href || '', beforeUrl: location.href, dpr: window.devicePixelRatio };
      },
    });
    return res?.result ?? { ok: false };
  } catch (e) { log('resolveAllProductsPoint error: ' + e.message); return { ok: false }; }
}

async function clickAllProducts() {
  return clickResolvedAnchor(await resolveAllProductsPoint());
}

async function resolveTopSalesShopPoint() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId }, world: 'MAIN',
      func: () => {
        const bar = document.querySelector('fieldset.shopee-sort-bar');
        if (!bar) return { ok: false };
        const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
        const opts = Array.from(bar.querySelectorAll('.sort-by-options__option'));
        let b = opts.find(x => /bán chạy|top sales/.test(norm(x.textContent)));
        if (!b && opts.length >= 3) b = opts[2];
        if (!b) return { ok: false };
        b.scrollIntoView({ block: 'center' });
        const r = b.getBoundingClientRect();
        return { ok: r.width > 0 && r.height > 0, already: b.getAttribute('aria-pressed') === 'true', x: r.left + r.width / 2, y: r.top + r.height / 2, dpr: window.devicePixelRatio };
      },
    });
    return res?.result ?? { ok: false };
  } catch (e) { log('resolveTopSalesShopPoint error: ' + e.message); return { ok: false }; }
}

async function clickTopSalesShop() {
  const pt = await resolveTopSalesShopPoint();
  if (!pt.ok) { log('Không thấy nút "Bán chạy" trên shop.'); return false; }
  if (pt.already) return true;
  try {
    await cdpClickAt(pt.x, pt.y);
    return true;
  } catch (e) {
    log('CDP top-sales click failed, synthetic: ' + e.message);
    await chrome.scripting.executeScript({
      target: { tabId: searchTabId }, world: 'MAIN', args: [pt.x, pt.y],
      func: (x, y) => { const el = document.elementFromPoint(x, y); if (el) el.click(); },
    });
    return true;
  }
}

async function resolveSearchTab() {
  if (initialTabId) {
    const tab = await chrome.tabs.get(initialTabId).catch(() => null);
    if (isUsableShopeeTab(tab)) return tab.id;
  }

  const tabs = await chrome.tabs.query({ url: 'https://shopee.vn/*' }).catch(() => []);
  const usableTabs = tabs.filter(isUsableShopeeTab);
  const activeTab = usableTabs.find(t => t.active);
  return (activeTab || usableTabs[0])?.id || null;
}

function isUsableShopeeTab(tab) {
  return !!tab?.id
    && !!tab.url
    && tab.url.includes('shopee.vn')
    && !tab.url.includes('shopee.vn/api/');
}

async function closeApiTabs() {
  const tabs = await chrome.tabs.query({ url: 'https://shopee.vn/api/*' }).catch(() => []);
  for (const tab of tabs) {
    if (tab.id) chrome.tabs.remove(tab.id).catch(() => {});
  }
}

async function closeOtherTabs(keepTabId) {
  const tabs = await chrome.tabs.query({}).catch(() => []);
  const ids = tabs
    .map(t => t.id)
    .filter(id => id && id !== keepTabId);
  if (ids.length) await chrome.tabs.remove(ids).catch(() => {});
}

function buildSearchUrl(keyword) {
  const params = new URLSearchParams({
    keyword: keyword || '',
    by: 'sales',
    order: 'desc',
  });
  return `https://shopee.vn/search?${params.toString()}`;
}

async function getCurrentTabUrl() {
  const tab = await chrome.tabs.get(searchTabId).catch(() => null);
  return tab?.url || '';
}

async function isVerifyPage() {
  const url = await getCurrentTabUrl();
  return /\/verify\//i.test(url || '');
}

// Sản phẩm không tồn tại / đã bị xoá: trang Shopee trả 200 nhưng là trang "không tìm thấy".
// Phân biệt với lỗi mạng/proxy (isNetworkErrorPage) và verify (isVerifyPage) để báo TERMINAL
// (bỏ qua link, sang link kế) thay vì retry/đổi account vô hạn trên một link chết.
async function isProductNotFoundPage() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId },
      world: 'MAIN',
      func: () => {
        const body = (document.body?.innerText || '').toLowerCase();
        const title = (document.title || '').toLowerCase();
        const markers = [
          'trang bạn muốn xem không tồn tại',
          'không tìm thấy trang',
          'trang không tồn tại',
          'sản phẩm không tồn tại',
          'sản phẩm bạn đang tìm',
          'this page is currently unavailable',
          'page not found',
          "the product you're looking for",
          'the product you are looking for',
          'oops! the page you',
        ];
        const hit = markers.some(m => body.includes(m) || title.includes(m));
        // PDP thật luôn có khối shop "#sll2-pdp-product-shop" hoặc ".page-product__shop".
        const hasPdpShop = !!(document.querySelector('#sll2-pdp-product-shop')
          || document.querySelector('.page-product__shop'));
        return hit && !hasPdpShop;
      },
    });
    return res?.result === true;
  } catch (_) {
    return false;
  }
}

async function isNetworkErrorPage() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId },
      world: 'MAIN',
      func: () => {
        const bodyText = (document.body?.innerText || '').toLowerCase();
        const title = (document.title || '').toLowerCase();
        return title.includes('site can') ||
          bodyText.includes('this site can') ||
          bodyText.includes('err_timed_out') ||
          bodyText.includes('err_proxy') ||
          bodyText.includes('err_proxy_connection_failed') ||
          bodyText.includes('no internet') ||
          bodyText.includes('something wrong with the proxy server') ||
          bodyText.includes('checking the proxy address') ||
          bodyText.includes('took too long to respond') ||
          bodyText.includes('checking the proxy');
      },
    });
    return res?.result === true;
  } catch (_) {
    return false;
  }
}

async function getPageHtml() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId },
      world: 'MAIN',
      func: () => ({
        title: document.title,
        url: location.href,
        html: document.documentElement.outerHTML,
      }),
    });
    return res?.result ?? null;
  } catch (e) {
    log('getPageHtml error: ' + e.message);
    return null;
  }
}

// â”€â”€ Type keyword into Shopee search box and submit (human-like) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
// Read scroll state + visible product links from the page (MAIN world).
async function readScrollState() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId },
      world: 'MAIN',
      func: () => {
        const pattern = /shopee\.vn\/[^?#]*-i\.(\d+)\.(\d+)/;
        const root = document.querySelector('section.shopee-search-item-result') || document;
        const links = [];
        const cards = [];
        root.querySelectorAll('a[href]').forEach(a => {
          const h = a.href || '';
          if (h.includes('/find_similar_products')) return;
          if (!pattern.test(h)) return;
          links.push(h);
          // Tâm các thẻ SP đang HIỆN trong viewport → dùng làm điểm rê chuột/hover khi cuộn.
          const r = a.getBoundingClientRect();
          if (r.width > 40 && r.height > 40 && r.top > 80 && r.bottom < window.innerHeight - 20)
            cards.push({ x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) });
        });
        return {
          scrollY: Math.round(window.scrollY),
          height: document.documentElement.scrollHeight,
          vw: window.innerWidth,
          vh: window.innerHeight,
          links: [...new Set(links)],
          cards: cards.slice(0, 6),
        };
      },
    });
    return res?.result ?? null;
  } catch (e) {
    log('readScrollState error: ' + e.message);
    return null;
  }
}

// Trusted scroll via CDP mouse-wheel events (the browser actually scrolls), with the
// loop driven here. Falls back to synthetic WheelEvent dispatch if CDP is unavailable.
async function humanScrollPage() {
  try {
    const first = await readScrollState();
    if (!first) return humanScrollPageSynthetic();

    const linkSet = new Set(first.links);
    let steps = 0, bottomHits = 0, lastHeight = first.height, lastScrollY = first.scrollY;
    let vw = first.vw, vh = first.vh;
    let roomLeft = Math.max(0, first.height - (first.scrollY + vh));   // px còn lại để cuộn xuống
    let cards = first.cards || [];
    // Điểm quan tâm = vị trí con trỏ. Bắt đầu ở giữa, sau bám theo thẻ SP đang "đọc"
    // → đường CDP trusted KHÔNG còn cảnh "wheel mà con trỏ đứng im giữa màn hình" (tell mạnh nhất).
    let poiX = vw / 2, poiY = vh / 2;
    const notch = Math.random() < 0.5 ? 100 : 120;   // "nấc" cuộn của 1 con chuột thật (cố định/phiên)
    let prevDelta = notch * 4;

    // Thoát khi: quá nhiều bước, HOẶC chạm đáy mà chiều cao không tăng vài lần (hết nội dung).
    while (steps < 60 && bottomHits < 3) {
      const atBottom = roomLeft <= 8;

      // ~38% bước: rê chuột tới 1 thẻ SP rồi dừng "đọc" (chỉ khi CÒN chỗ cuộn).
      if (cards.length && !atBottom && Math.random() < 0.38) {
        const c = cards[Math.floor(Math.random() * cards.length)];
        poiX = clamp(c.x + rand(-12, 12), 10, vw - 10);
        poiY = clamp(c.y + rand(-10, 10), 10, vh - 10);
        try { await cdpGesture({ op: 'moveTo', x: Math.round(poiX), y: Math.round(poiY) }); } catch (_) {}
        await sleep(Math.random() < 0.1 ? 1500 + Math.random() * 1000 : 250 + Math.random() * 850);
      }

      // Hướng + độ lớn cuộn.
      let delta;
      if (atBottom) {
        // ĐÁY: KHÔNG bắn wheel xuống nữa — đây chính là nguồn "giật giật" (overscroll). Chỉ dừng đọc 1 nhịp.
        delta = 0;
      } else {
        const down = !(Math.random() < 0.14 && lastScrollY > vh);
        if (down) {
          // Delta theo "nấc" + quán tính (momentum), nhiễu nhỏ — thay vì uniform i.i.d. phẳng.
          const target = notch * randInt(3, 7);
          delta = Math.round(0.7 * prevDelta + 0.3 * target) + Math.round(rand(-8, 8));
          if (delta < notch) delta = notch + Math.round(rand(0, notch * 2));
          // KHÔNG cuộn vượt phần còn lại → tới gần đáy là dừng êm, tránh nảy.
          delta = Math.min(delta, roomLeft + Math.round(notch * 0.4));
          prevDelta = Math.max(notch, delta);
        } else {
          delta = -Math.round(140 + Math.random() * 280);
        }
      }

      if (delta !== 0) {
        // Điểm phát wheel bám quanh con trỏ + nhiễu trục X (chuột thật không cuộn dọc hoàn hảo).
        const wx = clamp(poiX + rand(-20, 20), 10, vw - 10);
        const wy = clamp(poiY + rand(-18, 18), 10, vh - 10);
        try {
          await cdpGesture({ op: 'wheel', x: Math.round(wx), y: Math.round(wy), deltaY: delta, deltaX: Math.round(rand(-6, 6)) });
        } catch (e) {
          if (steps === 0) {
            log('CDP scroll unavailable, fallback synthetic: ' + e.message);
            return humanScrollPageSynthetic();
          }
          break;
        }
      }
      steps++;
      // Nghỉ: ở đáy/đọc → lâu; lướt mạnh → ngắn (sàn ~240ms để lazy-load kịp).
      const big = Math.abs(delta) > notch * 5;
      await sleep(delta === 0 ? 500 + Math.random() * 900 : (big ? 240 + Math.random() * 360 : 700 + Math.random() * 1400));

      const st = await readScrollState();
      if (!st) break;
      st.links.forEach(l => linkSet.add(l));
      if (st.cards && st.cards.length) cards = st.cards;
      roomLeft = Math.max(0, st.height - (st.scrollY + st.vh));
      // Đếm lần chạm đáy mà chiều cao KHÔNG tăng (hết nội dung) → đủ 3 thì dừng; tăng lại → reset.
      if (roomLeft <= 8 && Math.abs(st.height - lastHeight) < 40) bottomHits++;
      else bottomHits = 0;
      lastHeight = st.height; lastScrollY = st.scrollY; vw = st.vw; vh = st.vh;
    }

    return { steps, links: [...linkSet], y: lastScrollY, height: lastHeight };
  } catch (e) {
    log('humanScrollPage error: ' + e.message);
    return humanScrollPageSynthetic();
  }
}

async function humanScrollPageSynthetic() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId },
      world: 'MAIN',
      func: async () => {
        const sleep = ms => new Promise(r => setTimeout(r, ms));
        const rand = (min, max) => min + Math.random() * (max - min);
        const links = new Set();
        let mouse = {
          x: Math.floor(rand(120, Math.max(180, window.innerWidth - 160))),
          y: Math.floor(rand(140, Math.max(220, window.innerHeight - 180))),
        };

        function collectLinks() {
          const pattern = /shopee\.vn\/[^?#]*-i\.(\d+)\.(\d+)/;
          const root = document.querySelector('section.shopee-search-item-result') || document;
          root.querySelectorAll('a[href]').forEach(a => {
            const href = a.href || '';
            if (href.includes('/find_similar_products')) return;
            if (pattern.test(href)) links.add(href);
          });
        }

        function elementAt(x, y) {
          return document.elementFromPoint(
            Math.max(1, Math.min(window.innerWidth - 2, x)),
            Math.max(1, Math.min(window.innerHeight - 2, y)));
        }

        function mouseEvent(type, x, y) {
          const target = elementAt(x, y) || document.body;
          target.dispatchEvent(new MouseEvent(type, {
            bubbles: true, cancelable: true, clientX: x, clientY: y,
            screenX: x + window.screenX, screenY: y + window.screenY, view: window,
          }));
        }

        async function moveMouseTo(tx, ty) {
          const sx = mouse.x;
          const sy = mouse.y;
          const steps = Math.floor(rand(18, 42));
          for (let i = 1; i <= steps; i++) {
            const t = i / steps;
            const ease = t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
            const x = Math.round(sx + (tx - sx) * ease + Math.sin(t * Math.PI * 3) * rand(-4, 4));
            const y = Math.round(sy + (ty - sy) * ease + Math.cos(t * Math.PI * 2) * rand(-3, 3));
            mouseEvent('mousemove', x, y);
            await sleep(rand(8, 28));
          }
          mouse.x = Math.round(tx);
          mouse.y = Math.round(ty);
          mouseEvent('mouseover', mouse.x, mouse.y);
        }

        async function hoverProductMaybe() {
          const cards = Array.from(document.querySelectorAll('a[href*="-i."]'))
            .map(a => a.getBoundingClientRect())
            .filter(r => r.width > 40 && r.height > 40 && r.top > 80 && r.bottom < window.innerHeight - 20);
          if (!cards.length || Math.random() > 0.55) return;
          const r = cards[Math.floor(rand(0, cards.length))];
          await moveMouseTo(r.left + rand(20, Math.max(25, r.width - 20)), r.top + rand(20, Math.max(25, r.height - 20)));
          await sleep(rand(250, 900));
        }

        async function wheel(deltaY) {
          const x = mouse.x + rand(-25, 25);
          const y = mouse.y + rand(-20, 20);
          mouseEvent('mousemove', x, y);
          (elementAt(x, y) || document).dispatchEvent(new WheelEvent('wheel', {
            bubbles: true, cancelable: true, deltaY, deltaX: rand(-8, 8),
            deltaMode: 0, clientX: x, clientY: y, view: window,
          }));
          window.scrollBy({ top: deltaY, left: 0, behavior: 'smooth' });
        }

        collectLinks();
        await moveMouseTo(mouse.x + rand(-80, 120), mouse.y + rand(-60, 90));

        let steps = 0;
        let stableBottomCount = 0;
        let lastHeight = document.documentElement.scrollHeight;
        while (steps < 55 && stableBottomCount < 5) {
          await hoverProductMaybe();
          const direction = Math.random() < 0.14 && window.scrollY > window.innerHeight ? -1 : 1;
          await wheel(direction > 0 ? rand(420, 900) : -rand(140, 420));
          steps++;
          await sleep(rand(650, 1800));
          collectLinks();

          const height = document.documentElement.scrollHeight;
          const nearBottom = window.scrollY + window.innerHeight >= height - rand(240, 520);
          stableBottomCount = nearBottom && Math.abs(height - lastHeight) < 40 ? stableBottomCount + 1 : 0;
          lastHeight = height;
        }

        await moveMouseTo(rand(120, window.innerWidth - 120), rand(120, Math.min(window.innerHeight - 80, 420)));
        collectLinks();
        return {
          steps,
          links: Array.from(links),
          y: Math.round(window.scrollY),
          height: document.documentElement.scrollHeight,
        };
      },
    });
    return res?.result ?? null;
  } catch (e) {
    log('humanScrollPage error: ' + e.message);
    if (/error page|timed out|proxy|ERR_/i.test(e.message || '')) {
      reportNetworkError('Không cuộn được vì tab đang ở trang lỗi/proxy.');
    }
    return null;
  }
}

async function hasNextSearchPage() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId },
      world: 'MAIN',
      func: () => {
        const next = document.querySelector('.shopee-page-controller .shopee-icon-button--right:not(.shopee-icon-button--disabled), .shopee-mini-page-controller__next-btn:not(.shopee-button-outline--disabled)');
        if (!next) return false;
        if (next.getAttribute('aria-disabled') === 'true' || next.disabled) return false;
        // Anchor pagers (search page) have href; button pagers (shop mini-controller) don't.
        return next.tagName === 'BUTTON' || !!next.href;
      },
    });
    return res?.result === true;
  } catch (e) {
    log('hasNextSearchPage error: ' + e.message);
    return false;
  }
}

// Read the total number of result pages from the pager, if shown (0 = unknown).
async function getTotalPages() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId }, world: 'MAIN',
      func: () => {
        const totalEl = document.querySelector('.shopee-mini-page-controller__total');
        if (totalEl) {
          const n = parseInt((totalEl.textContent || '').replace(/[^\d]/g, ''), 10);
          if (n > 0) return n;
        }
        const nums = Array.from(document.querySelectorAll('.shopee-page-controller button'))
          .map(b => parseInt((b.textContent || '').trim(), 10))
          .filter(n => Number.isFinite(n));
        return nums.length ? Math.max(...nums) : 0;
      },
    });
    return res?.result ?? 0;
  } catch { return 0; }
}

async function crawlPagesForCurrentState(state, keyword, categoryName, categoryIndex, categoryTotal, maxPages, finalCategory, startPage = 1) {
  const dead = () => state !== searchState || state.stopped || state.networkErrorDetected;
  const seenItems = new Set();

  // Resume within this category at a specific page (account swap): jump straight to it via the
  // URL page param (Shopee's is 0-based) instead of re-crawling pages 1..startPage-1.
  startPage = Math.max(1, startPage || 1);
  if (startPage > 1) {
    try {
      const cur = await getCurrentTabUrl();
      const u = new URL(cur);
      u.searchParams.set('page', String(startPage - 1));
      log(`${categoryName ? 'Category ' + categoryName + ': ' : ''}tiếp tục tại trang ${startPage}.`);
      await chrome.tabs.update(searchTabId, { url: u.toString() });
      await waitForTabLoad(searchTabId);
      await sleep(2500 + Math.random() * 1300);
    } catch (e) {
      log('Không nhảy được tới trang resume, quét từ trang 1: ' + e.message);
      startPage = 1;
    }
    if (dead()) return;
  }

  // Cap to the shop/search's real page count when the pager exposes it.
  const totalPages = await getTotalPages();
  if (dead()) return;
  const pageCap = totalPages > 0 ? Math.min(maxPages, totalPages) : maxPages;
  if (totalPages > 0) log(`${categoryName ? 'Category ' + categoryName + ': ' : ''}phát hiện ${totalPages} trang, sẽ quét tối đa ${pageCap}.`);

  for (let pageNo = startPage; pageNo <= pageCap; pageNo++) {
    await waitWhilePaused(state);
    if (dead()) return;
    const prefix = categoryName ? `Category ${categoryIndex}/${categoryTotal} "${categoryName}", page ${pageNo}/${pageCap}` : `Page ${pageNo}/${pageCap}`;
    if (await isVerifyPage()) {
      if (dead()) return;
      state.captchaDetected = true;
      send({ action: 'captcha' });
      return;
    }
    if (await isNetworkErrorPage()) {
      reportNetworkError(`${prefix}: Shopee không tải được, có thể proxy timeout.`);
      return;
    }
    log(`${prefix}: human-like scrolling to load lazy products...`);
    const scrollResult = await humanScrollPage();
    if (dead()) return;
    if (scrollResult) {
      log(`${prefix}: scroll done, steps=${scrollResult.steps}, linksSeen=${scrollResult.links?.length ?? 0}, height=${scrollResult.height}`);
    }
    await sleep(800 + Math.random() * 900);
    if (dead()) return;

    const pageUrl = await getCurrentTabUrl();
    if (await isNetworkErrorPage()) {
      reportNetworkError(`${prefix}: trang lỗi mạng/proxy.`);
      return;
    }
    if (/\/verify\//i.test(pageUrl || '')) {
      if (dead()) return;
      state.captchaDetected = true;
      send({ action: 'captcha' });
      return;
    }
    log(`${prefix}: current URL: ${pageUrl}`);
    log(`${prefix}: collecting data from rendered DOM...`);
    const pageData = await extractPageData(keyword, categoryName);
    if (dead()) return;

    if (!pageData) {
      reportNetworkError(`${prefix}: không đọc được DOM, có thể tab đang ở trang lỗi.`);
      return;
    }

    if ((pageData.items?.length ?? 0) === 0 && (pageData.links?.length ?? 0) === 0) {
      log(`${prefix}: empty product page, stop current category.`);
      return;
    }

    // Stop if this page brought no new products (we've looped to already-seen content,
    // e.g. clicking "next" on the last page just re-shows it). Robust end-of-crawl signal.
    const ids = (pageData.items || []).map(it => `${it.shopid}.${it.itemid}`)
      .concat((pageData.links || []));
    const newCount = ids.filter(id => !seenItems.has(id)).length;
    ids.forEach(id => seenItems.add(id));
    if (pageNo > 1 && newCount === 0) {
      log(`${prefix}: không có sản phẩm mới (đã hết trang), dừng.`);
      // Still send this page's data (harmless duplicates dedup on the app side) then stop.
      pageData.page = pageNo;
      pageData.category = categoryName || '';
      pageData.categoryIndex = categoryIndex;
      pageData.categoryTotal = categoryTotal;
      pageData.isFinal = finalCategory;
      if (!dead()) send({ action: 'pageData', keyword, data: pageData });
      return;
    }

    const nextAvailable = pageNo < pageCap && await hasNextSearchPage();
    if (dead()) return;
    let clickedNext = false;
    if (nextAvailable) {
      log(`${prefix}: clicking next page...`);
      clickedNext = await clickNextSearchPage();
      if (dead()) return;
    }
    pageData.page = pageNo;
    pageData.category = categoryName || '';
    pageData.categoryIndex = categoryIndex;
    pageData.categoryTotal = categoryTotal;
    pageData.isFinal = finalCategory && !clickedNext;
    log(`${prefix}: found ${pageData.links?.length ?? 0} links, ${pageData.items?.length ?? 0} items with data`);
    if (dead()) return;
    send({ action: 'pageData', keyword, data: pageData });

    if (!clickedNext) break;

    await waitForUrlChange(pageUrl, 10000);
    await waitForTabLoad(searchTabId, 8000);
    await sleep(3500 + Math.random() * 1800);
    if (dead()) return;
    if (await isNetworkErrorPage()) {
      reportNetworkError(`${prefix}: lỗi mạng/proxy sau khi chuyển trang.`);
      return;
    }
    if (await isVerifyPage()) {
      if (dead()) return;
      state.captchaDetected = true;
      send({ action: 'captcha' });
      return;
    }
  }
}

async function collectSearchCategories() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId },
      world: 'MAIN',
      func: async () => {
        const sleep = ms => new Promise(r => setTimeout(r, ms));
        const normalize = s => (s || '').replace(/\s+/g, ' ').trim();
        const findCategoryFieldset = () => {
          const groups = Array.from(document.querySelectorAll('fieldset.shopee-facet-filter, fieldset.shopee-filter-group'));
          return groups.find(fs => {
            const header = normalize(fs.querySelector('legend, .shopee-filter-group__header')?.textContent || '');
            return /category|danh\s*mục|danh m/i.test(header);
          }) || document.querySelector('fieldset.shopee-facet-filter');
        };

        const fs = findCategoryFieldset();
        if (!fs) return [];
        fs.scrollIntoView({ block: 'center', behavior: 'smooth' });
        await sleep(700);

        const toggle = fs.querySelector('.shopee-filter-group__toggle-btn');
        if (toggle && toggle.getAttribute('aria-expanded') !== 'true') {
          toggle.click();
          await sleep(900);
        }

        const seen = new Set();
        return Array.from(fs.querySelectorAll('.shopee-checkbox-filter label, label.shopee-checkbox'))
          .map((label, index) => {
            const input = label.querySelector('input[type="checkbox"]');
            const name = normalize(label.querySelector('.shopee-checkbox__label')?.textContent || label.textContent || '');
            const value = input?.value || '';
            return { name, value, index };
          })
          .filter(x => x.name && x.value && !seen.has(x.value) && seen.add(x.value))
          .slice(0, 20);
      },
    });
    return Array.isArray(res?.result) ? res.result : [];
  } catch (e) {
    log('collectSearchCategories error: ' + e.message);
    return [];
  }
}

// Expand the category fieldset (if collapsed) and resolve the toggle button's
// center point, or signal that no expand is needed (MAIN world).
async function resolveCategoryToggle() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId },
      world: 'MAIN',
      func: () => {
        const normalize = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
        const fs = Array.from(document.querySelectorAll('fieldset.shopee-facet-filter, fieldset.shopee-filter-group'))
          .find(group => /category|danh\s*mục|danh m/i.test(normalize(group.querySelector('legend, .shopee-filter-group__header')?.textContent || '')))
          || document.querySelector('fieldset.shopee-facet-filter');
        if (!fs) return { ok: false };
        const toggle = fs.querySelector('.shopee-filter-group__toggle-btn');
        if (!toggle || toggle.getAttribute('aria-expanded') === 'true') return { ok: true, needsExpand: false };
        toggle.scrollIntoView({ block: 'center' });
        const r = toggle.getBoundingClientRect();
        return { ok: true, needsExpand: true, x: r.left + r.width / 2, y: r.top + r.height / 2, dpr: window.devicePixelRatio };
      },
    });
    return res?.result ?? { ok: false };
  } catch (e) {
    log('resolveCategoryToggle error: ' + e.message);
    return { ok: false };
  }
}

// Resolve the center point of the category checkbox label (MAIN world).
async function resolveCategoryLabelPoint(value, name) {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId },
      world: 'MAIN',
      args: [String(value || ''), String(name || '')],
      func: (value, name) => {
        const normalize = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
        const fs = Array.from(document.querySelectorAll('fieldset.shopee-facet-filter, fieldset.shopee-filter-group'))
          .find(group => /category|danh\s*mục|danh m/i.test(normalize(group.querySelector('legend, .shopee-filter-group__header')?.textContent || '')))
          || document.querySelector('fieldset.shopee-facet-filter');
        if (!fs) return { ok: false };
        const input = fs.querySelector(`input[type="checkbox"][value="${CSS.escape(value)}"]`);
        const label = input?.closest('label') || Array.from(fs.querySelectorAll('label'))
          .find(l => normalize(l.textContent || '') === normalize(name));
        if (!label) return { ok: false };
        label.scrollIntoView({ block: 'center' });
        const r = label.getBoundingClientRect();
        return { ok: r.width > 0 && r.height > 0, x: r.left + (0.3 + Math.random() * 0.4) * r.width, y: r.top + (0.3 + Math.random() * 0.4) * r.height, dpr: window.devicePixelRatio };
      },
    });
    return res?.result ?? { ok: false };
  } catch (e) {
    log('resolveCategoryLabelPoint error: ' + e.message);
    return { ok: false };
  }
}

async function selectSearchCategory(value, name) {
  // Trusted CDP path: expand the category group (if needed), then click the checkbox.
  try {
    const toggle = await resolveCategoryToggle();
    if (toggle.ok) {
      if (toggle.needsExpand) {
        await cdpClickAt(toggle.x, toggle.y);
        await sleep(800 + Math.random() * 400);
      }
      const label = await resolveCategoryLabelPoint(value, name);
      if (label.ok) {
        await sleep(400 + Math.random() * 300);
        await cdpClickAt(label.x, label.y);
        return true;
      }
    }
    log('Category point not resolved for CDP path; using synthetic fallback.');
  } catch (e) {
    log('CDP selectSearchCategory failed, fallback synthetic: ' + e.message);
  }
  return selectSearchCategorySynthetic(value, name);
}

async function selectSearchCategorySynthetic(value, name) {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId },
      world: 'MAIN',
      args: [String(value || ''), String(name || '')],
      func: async (value, name) => {
        const sleep = ms => new Promise(r => setTimeout(r, ms));
        const rand = (min, max) => min + Math.random() * (max - min);
        const normalize = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
        let mouse = {
          x: Math.floor(rand(120, Math.max(180, window.innerWidth - 140))),
          y: Math.floor(rand(120, Math.max(180, window.innerHeight - 140))),
        };
        function elementAt(x, y) {
          return document.elementFromPoint(
            Math.max(1, Math.min(window.innerWidth - 2, x)),
            Math.max(1, Math.min(window.innerHeight - 2, y)));
        }
        function mouseEvent(type, x, y) {
          const target = elementAt(x, y) || document.body;
          target.dispatchEvent(new MouseEvent(type, {
            bubbles: true, cancelable: true, clientX: x, clientY: y,
            screenX: x + window.screenX, screenY: y + window.screenY, view: window,
          }));
        }
        async function moveMouseTo(tx, ty) {
          const sx = mouse.x;
          const sy = mouse.y;
          const steps = Math.floor(rand(18, 42));
          for (let i = 1; i <= steps; i++) {
            const t = i / steps;
            const ease = t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
            const x = Math.round(sx + (tx - sx) * ease + Math.sin(t * Math.PI * 3) * rand(-4, 4));
            const y = Math.round(sy + (ty - sy) * ease + Math.cos(t * Math.PI * 2) * rand(-3, 3));
            mouseEvent('mousemove', x, y);
            await sleep(rand(8, 28));
          }
          mouse.x = Math.round(tx);
          mouse.y = Math.round(ty);
          mouseEvent('mouseover', mouse.x, mouse.y);
        }
        async function clickElement(el) {
          const r = el.getBoundingClientRect();
          const x = r.left + rand(Math.min(8, r.width / 4), Math.max(10, r.width - 8));
          const y = r.top + rand(Math.min(6, r.height / 4), Math.max(8, r.height - 6));
          await moveMouseTo(x, y);
          await sleep(rand(180, 520));
          mouseEvent('mousedown', mouse.x, mouse.y);
          await sleep(rand(55, 150));
          mouseEvent('mouseup', mouse.x, mouse.y);
          el.click();
        }
        const fs = Array.from(document.querySelectorAll('fieldset.shopee-facet-filter, fieldset.shopee-filter-group'))
          .find(group => /category|danh\s*mục|danh m/i.test(normalize(group.querySelector('legend, .shopee-filter-group__header')?.textContent || '')))
          || document.querySelector('fieldset.shopee-facet-filter');
        if (!fs) return false;

        const toggle = fs.querySelector('.shopee-filter-group__toggle-btn');
        if (toggle && toggle.getAttribute('aria-expanded') !== 'true') {
          toggle.scrollIntoView({ block: 'center', behavior: 'smooth' });
          await sleep(600);
          await clickElement(toggle);
          await sleep(800);
        }

        const input = fs.querySelector(`input[type="checkbox"][value="${CSS.escape(value)}"]`);
        const label = input?.closest('label') || Array.from(fs.querySelectorAll('label'))
          .find(l => normalize(l.textContent || '') === normalize(name));
        if (!label) return false;

        label.scrollIntoView({ block: 'center', behavior: 'smooth' });
        await sleep(700);
        await clickElement(label);
        return true;
      },
    });
    return res?.result === true;
  } catch (e) {
    log('selectSearchCategory error: ' + e.message);
    if (/error page|timed out|proxy|ERR_/i.test(e.message || '')) {
      reportNetworkError('Không thao tác được category vì tab đang ở trang lỗi/proxy.');
    }
    return false;
  }
}

// Scroll to the pager and resolve the next-page button's center + href (MAIN world).
async function resolveNextPagePoint() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId },
      world: 'MAIN',
      func: async () => {
        const sleep = ms => new Promise(r => setTimeout(r, ms));
        window.scrollTo({ top: document.documentElement.scrollHeight, behavior: 'smooth' });
        await sleep(900 + Math.random() * 700);
        const next = document.querySelector('.shopee-page-controller .shopee-icon-button--right:not(.shopee-icon-button--disabled), .shopee-mini-page-controller__next-btn:not(.shopee-button-outline--disabled)');
        if (!next || next.getAttribute('aria-disabled') === 'true') return { ok: false };
        next.scrollIntoView({ block: 'center' });
        const r = next.getBoundingClientRect();
        return {
          ok: r.width > 0 && r.height > 0,
          x: r.left + (0.3 + Math.random() * 0.4) * r.width,
          y: r.top + (0.3 + Math.random() * 0.4) * r.height,
          href: next.href || '',
          beforeUrl: location.href,
          dpr: window.devicePixelRatio,
        };
      },
    });
    return res?.result ?? { ok: false };
  } catch (e) {
    log('resolveNextPagePoint error: ' + e.message);
    return { ok: false };
  }
}

async function clickNextSearchPage() {
  // Trusted CDP path: click the resolved next-page button; fall back to navigating
  // to its href if the click didn't change the URL (Shopee sometimes routes via JS).
  try {
    const pt = await resolveNextPagePoint();
    if (pt.ok) {
      await cdpClickAt(pt.x, pt.y);
      await sleep(900 + Math.random() * 700);
      if (pt.href) {
        await chrome.scripting.executeScript({
          target: { tabId: searchTabId }, world: 'MAIN',
          args: [pt.href, pt.beforeUrl],
          func: (href, before) => { if (location.href === before) location.href = href; },
        });
      }
      return true;
    }
    return false;
  } catch (e) {
    log('CDP clickNextSearchPage failed, fallback synthetic: ' + e.message);
    return clickNextSearchPageSynthetic();
  }
}

async function clickNextSearchPageSynthetic() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId },
      world: 'MAIN',
      func: async () => {
        const sleep = ms => new Promise(r => setTimeout(r, ms));
        const rand = (min, max) => min + Math.random() * (max - min);

        window.scrollTo({ top: document.documentElement.scrollHeight, behavior: 'smooth' });
        await sleep(900 + Math.random() * 700);

        const next = document.querySelector('.shopee-page-controller .shopee-icon-button--right:not(.shopee-icon-button--disabled), .shopee-mini-page-controller__next-btn:not(.shopee-button-outline--disabled)');
        if (!next || next.getAttribute('aria-disabled') === 'true') return false;
        const beforeUrl = location.href;
        const nextHref = next.href;

        const rect = next.getBoundingClientRect();
        let x = rand(100, Math.max(120, window.innerWidth - 120));
        let y = rand(120, Math.max(140, window.innerHeight - 120));
        const tx = rect.left + rect.width / 2;
        const ty = rect.top + rect.height / 2;

        for (let i = 1; i <= 28; i++) {
          const t = i / 28;
          const ease = t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
          const cx = Math.round(x + (tx - x) * ease + Math.sin(t * Math.PI * 4) * rand(-3, 3));
          const cy = Math.round(y + (ty - y) * ease + Math.cos(t * Math.PI * 3) * rand(-3, 3));
          next.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: cx, clientY: cy, view: window }));
          await sleep(rand(10, 28));
        }

        next.dispatchEvent(new MouseEvent('mouseover', { bubbles: true, clientX: tx, clientY: ty, view: window }));
        await sleep(rand(180, 520));
        next.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, clientX: tx, clientY: ty, view: window }));
        await sleep(rand(60, 160));
        next.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, clientX: tx, clientY: ty, view: window }));
        next.click();
        await sleep(rand(900, 1600));
        if (nextHref && location.href === beforeUrl) {
          location.href = nextHref;
        }
        return true;
      },
    });
    return res?.result === true;
  } catch (e) {
    log('clickNextSearchPage error: ' + e.message);
    return false;
  }
}

// Resolve the "best selling" sort button (MAIN world).
async function resolveBestSellingPoint() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId }, world: 'MAIN',
      func: () => {
        const sortGroup = document.querySelector('.shopee-sort-by-options__option-group');
        const sortButtons = sortGroup ? Array.from(sortGroup.querySelectorAll('button')) : [];
        let btn = sortButtons.length >= 3 ? sortButtons[2] : null;
        if (!btn) {
          btn = Array.from(document.querySelectorAll('.shopee-sort-by-options button, button'))
            .find(b => /top\s*sales|best\s*selling|b[aá]n\s*ch/i.test((b.textContent || '').trim()));
        }
        if (!btn) return { ok: false };
        btn.scrollIntoView({ block: 'center' });
        const r = btn.getBoundingClientRect();
        return {
          ok: r.width > 0 && r.height > 0,
          alreadyPressed: btn.getAttribute('aria-pressed') === 'true',
          x: r.left + (0.3 + Math.random() * 0.4) * r.width, y: r.top + (0.3 + Math.random() * 0.4) * r.height, dpr: window.devicePixelRatio,
        };
      },
    });
    return res?.result ?? { ok: false };
  } catch (e) { log('resolveBestSellingPoint error: ' + e.message); return { ok: false }; }
}

// If sort didn't take via the UI, navigate to sales-sorted search URL (MAIN world).
async function applySalesSortFallbackIfNeeded() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId }, world: 'MAIN',
      func: () => {
        try {
          const url = new URL(window.location.href);
          if (url.searchParams.get('sortBy') !== 'sales') {
            url.pathname = '/search';
            url.searchParams.set('sortBy', 'sales');
            window.location.href = url.toString();
            return true;
          }
        } catch (_) {}
        return false;
      },
    });
    return res?.result === true;
  } catch (e) { log('applySalesSortFallbackIfNeeded error: ' + e.message); return false; }
}

// Trusted CDP scroll: load lazy products then return to the top, driven from here.
async function cdpScrollToLoadThenTop(maxSteps = 24) {
  let st = await readScrollState();
  if (!st) return;
  let vw = st.vw, vh = st.vh, stable = 0, lastH = st.height, steps = 0;
  while (steps < maxSteps && stable < 4) {
    await cdpGesture({ op: 'wheel', x: vw / 2 + (Math.random() * 40 - 20), y: vh / 2 + (Math.random() * 30 - 15), deltaY: Math.round(440 + Math.random() * 460) });
    steps++;
    await sleep(550 + Math.random() * 950);
    st = await readScrollState();
    if (!st) return;
    const near = st.scrollY + st.vh >= st.height - (260 + Math.random() * 300);
    stable = near && Math.abs(st.height - lastH) < 40 ? stable + 1 : 0;
    lastH = st.height; vw = st.vw; vh = st.vh;
  }
  let guard = 0;
  while (guard++ < 30) {
    st = await readScrollState();
    if (!st || st.scrollY <= 120) break;
    await cdpGesture({ op: 'wheel', x: st.vw / 2, y: st.vh / 2, deltaY: -Math.round(500 + Math.random() * 450) });
    await sleep(450 + Math.random() * 750);
  }
}

async function prepareBestSelling() {
  // Trusted CDP path: click sort, scroll to load — all real events.
  // The URL fallback at the end is the safety net if the UI click didn't take.
  try {
    const bs = await resolveBestSellingPoint();
    if (!bs.ok) {
      log('Best-selling button not found for CDP path; using synthetic fallback.');
      return prepareBestSellingSynthetic();
    }
    let clickedBestSelling = false;
    if (!bs.alreadyPressed) {
      await cdpClickAt(bs.x, bs.y);
      await sleep(3000 + Math.random() * 1800);
    }
    clickedBestSelling = true;

    await cdpScrollToLoadThenTop();

    const fallbackNavigate = await applySalesSortFallbackIfNeeded();
    return { clickedBestSelling, setPrice: false, firstScrollSteps: 0, fallbackNavigate };
  } catch (e) {
    log('CDP prepareBestSelling failed, fallback synthetic: ' + e.message);
    return prepareBestSellingSynthetic();
  }
}

async function prepareBestSellingSynthetic() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId },
      world: 'MAIN',
      func: async () => {
        const sleep = ms => new Promise(r => setTimeout(r, ms));
        const rand = (min, max) => min + Math.random() * (max - min);
        let mouse = {
          x: Math.floor(rand(140, Math.max(180, window.innerWidth - 180))),
          y: Math.floor(rand(140, Math.max(220, window.innerHeight - 220))),
        };

        function elementAt(x, y) {
          return document.elementFromPoint(
            Math.max(1, Math.min(window.innerWidth - 2, x)),
            Math.max(1, Math.min(window.innerHeight - 2, y)));
        }

        function mouseEvent(type, x, y) {
          const target = elementAt(x, y) || document.body;
          target.dispatchEvent(new MouseEvent(type, {
            bubbles: true, cancelable: true, clientX: x, clientY: y,
            screenX: x + window.screenX, screenY: y + window.screenY, view: window,
          }));
        }

        async function moveMouseTo(tx, ty) {
          const sx = mouse.x;
          const sy = mouse.y;
          const steps = Math.floor(rand(22, 48));
          for (let i = 1; i <= steps; i++) {
            const t = i / steps;
            const ease = t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
            const x = Math.round(sx + (tx - sx) * ease + Math.sin(t * Math.PI * 2.7) * rand(-5, 5));
            const y = Math.round(sy + (ty - sy) * ease + Math.cos(t * Math.PI * 2.3) * rand(-4, 4));
            mouseEvent('mousemove', x, y);
            await sleep(rand(9, 30));
          }
          mouse.x = Math.round(tx);
          mouse.y = Math.round(ty);
          mouseEvent('mouseover', mouse.x, mouse.y);
        }

        async function clickElement(el) {
          const r = el.getBoundingClientRect();
          const x = r.left + rand(Math.min(10, r.width / 4), Math.max(12, r.width - 10));
          const y = r.top + rand(Math.min(8, r.height / 4), Math.max(10, r.height - 8));
          await moveMouseTo(x, y);
          await sleep(rand(180, 550));
          mouseEvent('mousedown', mouse.x, mouse.y);
          await sleep(rand(60, 160));
          mouseEvent('mouseup', mouse.x, mouse.y);
          el.click();
        }

        async function wheel(deltaY) {
          const x = mouse.x + rand(-20, 20);
          const y = mouse.y + rand(-16, 16);
          mouseEvent('mousemove', x, y);
          (elementAt(x, y) || document).dispatchEvent(new WheelEvent('wheel', {
            bubbles: true, cancelable: true, deltaY, deltaX: rand(-6, 6),
            deltaMode: 0, clientX: x, clientY: y, view: window,
          }));
          window.scrollBy({ top: deltaY, left: 0, behavior: 'smooth' });
        }

        await moveMouseTo(rand(160, window.innerWidth - 180), rand(180, Math.min(window.innerHeight - 120, 420)));
        await wheel(rand(260, 520));
        await sleep(rand(700, 1400));

        function findBestSellingButton() {
          const sortGroup = document.querySelector('.shopee-sort-by-options__option-group');
          const sortButtons = sortGroup ? Array.from(sortGroup.querySelectorAll('button')) : [];
          if (sortButtons.length >= 3) return sortButtons[2];

          return Array.from(document.querySelectorAll('.shopee-sort-by-options button, button'))
            .find(b => /top\s*sales|best\s*selling|b[aá]n\s*ch/i.test((b.textContent || '').trim()));
        }

        const bestSellingButton = findBestSellingButton();
        let clickedBestSelling = false;
        if (bestSellingButton) {
          bestSellingButton.scrollIntoView({ block: 'center', behavior: 'smooth' });
          await sleep(rand(650, 1300));
          const beforePressed = bestSellingButton.getAttribute('aria-pressed');
          if (beforePressed !== 'true') await clickElement(bestSellingButton);
          clickedBestSelling = bestSellingButton.getAttribute('aria-pressed') === 'true' || beforePressed !== 'true';
          await sleep(rand(3000, 4800));
        }

        let firstScrollSteps = 0;
        let stableBottomCount = 0;
        let lastHeight = document.documentElement.scrollHeight;
        while (firstScrollSteps < 38 && stableBottomCount < 4) {
          const direction = Math.random() < 0.16 && window.scrollY > window.innerHeight ? -1 : 1;
          await wheel(direction > 0 ? rand(440, 900) : -rand(120, 360));
          firstScrollSteps++;
          await sleep(rand(550, 1500));
          const height = document.documentElement.scrollHeight;
          const nearBottom = window.scrollY + window.innerHeight >= height - rand(260, 560);
          stableBottomCount = nearBottom && Math.abs(height - lastHeight) < 40 ? stableBottomCount + 1 : 0;
          lastHeight = height;
        }

        while (window.scrollY > 120) {
          await wheel(-rand(500, 950));
          await sleep(rand(450, 1200));
          if (Math.random() < 0.18) {
            await wheel(rand(90, 220));
            await sleep(rand(250, 700));
          }
        }

        await sleep(rand(900, 1700));

        let fallbackNavigate = false;
        try {
          const url = new URL(window.location.href);
          if (url.searchParams.get('sortBy') !== 'sales') {
            url.pathname = '/search';
            url.searchParams.set('sortBy', 'sales');
            window.location.href = url.toString();
            fallbackNavigate = true;
          }
        } catch (_) {}

        return { clickedBestSelling, setPrice: false, firstScrollSteps, fallbackNavigate };
      },
    });
    return res?.result ?? null;
  } catch (e) {
    log('prepareBestSelling error: ' + e.message);
    return null;
  }
}

// Resolve a click point for the visible search input (after scrollIntoView).
async function resolveSearchInputPoint() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId },
      world: 'MAIN',
      func: () => {
        const selectors = [
          'input.shopee-searchbar-input__input',
          'input[name="keyword"]',
          'input[type="search"]',
          'input[placeholder]',
        ];
        let inp = null;
        for (const sel of selectors) {
          for (const el of document.querySelectorAll(sel)) {
            if (el.offsetParent !== null) { inp = el; break; }
          }
          if (inp) break;
        }
        if (!inp) return { ok: false };
        inp.scrollIntoView({ block: 'center' });
        const r = inp.getBoundingClientRect();
        const rand = (a, b) => a + Math.random() * (b - a);
        const x = r.left + rand(r.width * 0.3, r.width * 0.7);
        const y = r.top + rand(r.height * 0.3, r.height * 0.7);   // jitter cả trục y → không trúng tâm hình học

        // Banner/popup quảng cáo có thể phủ lên ô tìm kiếm — kiểm tra phần tử
        // thực sự nằm tại điểm click; nếu là overlay thì tìm nút đóng của nó.
        const cover = document.elementFromPoint(x, y);
        const occluded = !!(cover && cover !== inp && !inp.contains(cover));
        let close = null;
        if (occluded && cover) {
          let root = cover;
          while (root.parentElement && root.parentElement !== document.body) root = root.parentElement;
          const btn = root.querySelector(
            '.shopee-popup__close-btn, .home-popup__close-area, ' +
            '[aria-label*="close" i], [aria-label*="đóng" i], [class*="close"]');
          const br = btn?.getBoundingClientRect();
          if (br && br.width > 0 && br.height > 0) {
            close = { x: br.left + br.width / 2, y: br.top + br.height / 2 };
          }
        }

        return {
          ok: r.width > 0 && r.height > 0,
          x, y,
          value: inp.value || '',   // để bỏ Ctrl+A+Delete khi ô đang RỖNG (tell typing)
          dpr: window.devicePixelRatio,
          occluded,
          close,
        };
      },
    });
    return res?.result ?? { ok: false };
  } catch (e) {
    log('resolveSearchInputPoint error: ' + e.message);
    return { ok: false };
  }
}

async function typeAndSearch(keyword) {
  // Trusted CDP path: focus the input, type, press Enter — all as real events.
  try {
    let pt = await resolveSearchInputPoint();
    // Banner quảng cáo che ô tìm kiếm: click nút đóng (trusted) hoặc nhấn
    // Escape, rồi resolve lại — tối đa 3 lần.
    for (let i = 0; i < 3 && pt.ok && pt.occluded; i++) {
      log('Popup/banner che ô tìm kiếm — đang đóng...');
      if (pt.close) await cdpGesture({ op: 'click', x: pt.close.x, y: pt.close.y });
      else await cdpGesture({ op: 'pressKey', key: 'Escape' });
      await sleep(600 + Math.random() * 400);
      pt = await resolveSearchInputPoint();
    }
    if (pt.ok && pt.occluded) {
      log('Không đóng được popup che ô tìm kiếm; dùng synthetic fallback.');
      return typeAndSearchSynthetic(keyword);
    }
    if (pt.ok) {
      await cdpGesture({ op: 'click', x: pt.x, y: pt.y, dpr: pt.dpr });
      // "Nghĩ" trước khi gõ — tỉ lệ độ dài từ khóa (người không gõ tức thì sau khi bấm vào ô).
      await sleep(220 + keyword.length * (25 + Math.random() * 35) + Math.random() * 300);
      // Chỉ clear khi ô KHÔNG rỗng (homepage thường rỗng → tránh Ctrl+A+Delete vô cớ = tell).
      await cdpGesture({ op: 'type', text: keyword, clearFirst: (pt.value || '').length > 0 });
      // Verify the keyword actually landed in the input (a popup/overlay can
      // swallow the click); if not, bail to the synthetic path instead of
      // pressing Enter into nowhere.
      const [chk] = await chrome.scripting.executeScript({
        target: { tabId: searchTabId }, world: 'MAIN',
        func: () => {
          const inp = document.querySelector('input.shopee-searchbar-input__input, input[name="keyword"]');
          return inp ? inp.value : null;
        },
      });
      if ((chk?.result ?? '') !== keyword) {
        throw new Error('typed value mismatch: "' + (chk?.result ?? '') + '"');
      }
      // Dừng "đọc gợi ý autocomplete" trước khi Enter — log-normal đuôi dài (đôi khi >2s) thay uniform hẹp.
      await sleep(Math.round(380 * Math.exp((Math.random() + Math.random() + Math.random() - 1.5) * 0.85)));
      await cdpGesture({ op: 'pressKey', key: 'Enter' });
      // Fallback submit if Enter didn't navigate off the homepage (giữ để đảm bảo submit chắc chắn).
      await sleep(400);
      await chrome.scripting.executeScript({
        target: { tabId: searchTabId }, world: 'MAIN',
        func: () => {
          if (window.location.pathname === '/') {
            const inp = document.querySelector('input.shopee-searchbar-input__input, input[name="keyword"]');
            const form = inp?.closest('form');
            if (form) form.submit();
          }
        },
      });
      return true;
    }
    log('Search input not found for CDP path; using synthetic fallback.');
  } catch (e) {
    log('CDP typeAndSearch failed, fallback synthetic: ' + e.message);
  }
  return typeAndSearchSynthetic(keyword);
}

async function typeAndSearchSynthetic(keyword) {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId },
      world: 'MAIN',
      func: async (kw) => {
        // â”€â”€ find visible search input â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        const selectors = [
          'input.shopee-searchbar-input__input',
          'input[name="keyword"]',
          'input[type="search"]',
          'input[placeholder]',
        ];
        let inp = null;
        for (const sel of selectors) {
          for (const el of document.querySelectorAll(sel)) {
            if (el.offsetParent !== null) { inp = el; break; }
          }
          if (inp) break;
        }
        if (!inp) return false;

        // â”€â”€ click & focus â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        inp.click();
        inp.focus();
        await new Promise(r => setTimeout(r, 300 + Math.random() * 200));

        // â”€â”€ clear existing value â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        const nativeSetter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
        nativeSetter.call(inp, '');
        inp.dispatchEvent(new Event('input', { bubbles: true }));
        await new Promise(r => setTimeout(r, 100));

        // â”€â”€ type each character with random delay (human-like) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        const delay = () => new Promise(r => setTimeout(r, 40 + Math.random() * 80));

        for (const char of kw) {
          inp.dispatchEvent(new KeyboardEvent('keydown',  { key: char, code: 'Key' + char.toUpperCase(), bubbles: true }));
          inp.dispatchEvent(new KeyboardEvent('keypress', { key: char, charCode: char.charCodeAt(0), bubbles: true }));

          // insert char at cursor position
          const start = inp.selectionStart ?? inp.value.length;
          const end   = inp.selectionEnd   ?? inp.value.length;
          const newVal = inp.value.slice(0, start) + char + inp.value.slice(end);
          nativeSetter.call(inp, newVal);
          inp.setSelectionRange(start + 1, start + 1);

          inp.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: char }));
          inp.dispatchEvent(new KeyboardEvent('keyup', { key: char, bubbles: true }));

          await delay();
        }

        // â”€â”€ small pause before hitting Enter â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        await new Promise(r => setTimeout(r, 300 + Math.random() * 300));

        inp.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', keyCode: 13, code: 'Enter', bubbles: true }));
        inp.dispatchEvent(new KeyboardEvent('keyup',   { key: 'Enter', keyCode: 13, code: 'Enter', bubbles: true }));

        // fallback: submit form if navigation hasn't started
        await new Promise(r => setTimeout(r, 200));
        const form = inp.closest('form');
        if (form && window.location.pathname === '/') form.submit();

        return true;
      },
      args: [keyword],
    });
    return res?.result === true;
  } catch (e) {
    log('typeAndSearch error: ' + e.message);
    return false;
  }
}

// â”€â”€ Extract product data from loaded search results page â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
async function extractPageData(keyword, categoryName = '') {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: searchTabId },
      world: 'MAIN',
      args: [categoryName],
      func: (categoryName) => {
        const result = { links: [], items: [], source: 'none' };

        // API price is in micro-VND (price × 100000), e.g. 122999đ → 12,299,900,000.
        // Shop pages sometimes store the price already in VND. Only divide when the value
        // is clearly micro (large), so "122.999" → 122999 either way (never 1.22).
        const priceToVnd = p => {
          const n = Number(p) || 0;
          return n > 1e7 ? Math.round(n / 100000) : n;
        };

        // â”€â”€ Try 1: __NEXT_DATA__ (Next.js SSR) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        try {
          const nd = document.getElementById('__NEXT_DATA__');
          if (nd) {
            const parsed = JSON.parse(nd.textContent);
            const itemList =
              parsed?.props?.pageProps?.initialData?.data?.item_card_list ||
              parsed?.props?.pageProps?.data?.item_card_list ||
              parsed?.props?.pageProps?.searchResult?.item_card_list;
            if (itemList?.length) {
              result.source = '__NEXT_DATA__';
              result.items = itemList.map(it => {
                const b = it.item_basic || it;
                return {
                  name:     b.name,
                  itemid:   b.itemid,
                  shopid:   b.shopid,
                  price:    priceToVnd(b.price),
                  sold:     b.sold,
                  rating:   b.rating_star,
                  location: b.shop_location,
                  category: categoryName || '',
                  image:    b.image,
                  link:     `https://shopee.vn/product/${b.shopid}/${b.itemid}`,
                };
              });
              result.links = result.items.map(i => i.link);
              return result;
            }
          }
        } catch (_) {}

        // â”€â”€ Try 2: window.__SC_DATA__ or other globals â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        try {
          const sc = window.__SC_DATA__ || window.__SHOPEE_INITIAL_STATE__;
          const itemList = sc?.search?.item_card_list || sc?.search?.items;
          if (itemList?.length) {
            result.source = '__SC_DATA__';
            result.items = itemList.map(it => {
              const b = it.item_basic || it;
              return {
                name: b.name, itemid: b.itemid, shopid: b.shopid,
                price: priceToVnd(b.price), sold: b.sold,
                category: categoryName || '',
                link: `https://shopee.vn/product/${b.shopid}/${b.itemid}`,
              };
            });
            result.links = result.items.map(i => i.link);
            return result;
          }
        } catch (_) {}

        // â”€â”€ Try 3: DOM â€” find all product <a> links â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        try {
          const pattern = /shopee\.vn\/[^?#]*-i\.(\d+)\.(\d+)/;
          const seen = new Set();
          const normalize = s => (s || '').replace(/\s+/g, ' ').trim();
          const toNumber = raw => {
            const digits = String(raw || '').replace(/[^\d]/g, '');
            return digits ? Number(digits) : 0;
          };
          const parsePrice = text => {
            const prices = [];
            for (const m of normalize(text).matchAll(/(?:₫|đ|â‚«|Ä‘)\s*([\d.,]+)|([\d.,]+)\s*(?:₫|đ|â‚«|Ä‘)/gi)) {
              const n = toNumber(m[1] || m[2]);
              if (n >= 1000) prices.push(n);
            }
            if (prices.length) return Math.min(...prices);
            const m = normalize(text).match(/(?:^|[^\d])(\d{1,3}(?:[.,]\d{3})+)(?=\D|$)/);
            return m ? toNumber(m[1]) : 0;
          };
          const parseSold = text => {
            const source = normalize(text);
            const lower = source.toLowerCase();
            const markers = ['đã bán', 'da ban', 'sold', 'Ä‘Ã£ bÃ¡n'];
            const markerIndex = markers
              .map(x => lower.indexOf(x))
              .filter(x => x >= 0)
              .sort((a, b) => a - b)[0];
            const soldText = markerIndex >= 0
              ? source.slice(Math.max(0, markerIndex - 36), markerIndex + 96)
              : source;
            const unitWords = 'k|nghìn|nghin|ngan|nghÃ¬n|tr|triệu|trieu|triá»‡u';
            const afterMarker = new RegExp(`(?:đã\\s*bán|da\\s*ban|Ä‘Ã£\\s*bÃ¡n|sold(?:\\s*(?:/|per)?\\s*(?:month|monthly))?)\\s*[>≥~]?\\s*([\\d.,]+)\\s*(${unitWords})?\\+?`, 'i');
            const beforeMarker = new RegExp(`([\\d.,]+)\\s*(${unitWords})?\\+?\\s*(?:đã\\s*bán|da\\s*ban|Ä‘Ã£\\s*bÃ¡n|sold(?:\\s*(?:/|per)?\\s*(?:month|monthly))?)`, 'i');
            const m = soldText.match(afterMarker) || soldText.match(beforeMarker);
            if (!m) return 0;
            let value = Number((m[1] || '').replace(',', '.').replace(/[^\d.]/g, ''));
            const unit = (m[2] || '').toLowerCase();
            if (!Number.isFinite(value)) return 0;
            if (unit === 'k' || unit === 'nghìn' || unit === 'nghin' || unit === 'nghÃ¬n' || unit === 'ngan') value *= 1000;
            if (unit === 'tr' || unit === 'triệu' || unit === 'triá»‡u' || unit === 'trieu') value *= 1000000;
            return Math.round(value);
          };
          const parseRating = text => {
            const m = normalize(text).match(/(?:^|\s)([1-5](?:[.,]\d)?)(?:\s|$)/);
            return m ? Number(m[1].replace(',', '.')) : 0;
          };
          const findCard = a => {
            const item = a.closest('li.shopee-search-item-result__item, .shop-search-result-view__item');
            if (item) return item;
            let el = a;
            for (let i = 0; el && i < 8; i++, el = el.parentElement) {
              const rect = el.getBoundingClientRect?.();
              const text = normalize(el.innerText || el.textContent || '');
              if (rect && rect.width >= 120 && rect.height >= 180 && text.length > 20) return el;
            }
            return a;
          };
          const getName = (card, a) => {
            const alt = [...card.querySelectorAll('img')]
              .map(img => normalize(img.alt))
              .find(x => x && !/custom-overlay|flag-label|voucher|rating|location/i.test(x));
            if (alt) return alt;
            const title = normalize(a.title || a.getAttribute('aria-label'));
            if (title) return title;
            const bad = /đã bán|Ä‘Ã£ bÃ¡n|da ban|sold|₫|đ|â‚«|Ä‘|%|voucher|location|rating|mua để nhận|mua Ä‘á»ƒ nháº­n|mua de nhan/i;
            return normalize(card.innerText || card.textContent || '')
              .split('\n')
              .map(normalize)
              .filter(x => x.length >= 8 && !bad.test(x))
              .sort((a, b) => b.length - a.length)[0] || '';
          };
          const getLocation = card => {
            const aria = [...card.querySelectorAll('[aria-label]')]
              .map(el => normalize(el.getAttribute('aria-label')))
              .find(x => /^location-/i.test(x));
            if (aria) return aria.replace(/^location-/i, '');
            const rx = /(Hà Nội|HÃ  Ná»™i|Ha Noi|Thành phố Hồ Chí Minh|Hồ Chí Minh|Há»“ ChÃ­ Minh|Ho Chi Minh|Đà Nẵng|ÄÃ  Náºµng|Da Nang|Hải Phòng|Háº£i PhÃ²ng|Hai Phong|Cần Thơ|Cáº§n ThÆ¡|Can Tho|Bình Dương|BÃ¬nh DÆ°Æ¡ng|Đồng Nai|Äá»“ng Nai|Dong Nai|Nước ngoài|NÆ°á»›c ngoÃ i|Quốc tế|Quá»‘c táº¿)/i;
            return normalize(card.innerText || card.textContent || '').split('\n').map(normalize).find(x => rx.test(x)) || '';
          };
          const getImage = card => {
            const img = [...card.querySelectorAll('img')]
              .find(x => x.src && /susercontent/.test(x.src) && !/custom-overlay|flag-label|voucher|rating|location/i.test(x.alt || ''));
            return img?.currentSrc || img?.src || '';
          };
          // On the keyword search page, restrict to the result section to avoid
          // recommendation widgets. On a shop "All products" page there is no such
          // section, so accept any product link (find_similar links are excluded below).
          const searchSection = document.querySelector('section.shopee-search-item-result');
          const isSearchResultCard = a => {
            if (!searchSection) return true;
            return !!a.closest('section.shopee-search-item-result');
          };
          const cardItems = [];
          const scanRoot = container => {
            container.querySelectorAll('a[href]').forEach(a => {
              const m = a.href.match(pattern);
              if (!m || seen.has(a.href)) return;
              if (a.href.includes('/find_similar_products')) return;
              if (!isSearchResultCard(a)) return;
              seen.add(a.href);
              const card = findCard(a);
              const text = normalize(card.innerText || card.textContent || '');
              cardItems.push({
                shopid: Number(m[1]),
                itemid: Number(m[2]),
                link: a.href,
                name: getName(card, a),
                price: parsePrice(text),
                sold: parseSold(text),
                rating: parseRating(text),
                category: categoryName || '',
                location: getLocation(card),
                image: getImage(card),
              });
            });
          };

          const root = searchSection
            || document.querySelector('.shop-search-result-view')
            || document.querySelector('[class*="shop-search-result"]')
            || document.querySelector('.shop-page')
            || document;
          scanRoot(root);
          // Shop layouts vary; if the preferred container yielded nothing, scan the whole page.
          if (cardItems.length === 0 && root !== document) scanRoot(document);

          const richItems = cardItems.filter(x => x.name || x.price > 0 || x.sold > 0);
          if (richItems.length) {
            result.source = 'DOM_cards';
            result.items = richItems;
            result.links = richItems.map(i => i.link);
            return result;
          }

          result.links = cardItems.map(i => i.link);
          if (result.links.length) result.source = 'DOM_links';
        } catch (_) {}

        // â”€â”€ Try 4: inline <script> tags with JSON data â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        if (!result.items.length) {
          try {
            for (const s of document.querySelectorAll('script:not([src])')) {
              const txt = s.textContent;
              if (txt.includes('item_card_list') || txt.includes('"itemid"')) {
                const m = txt.match(/\{[^;]+item_card_list[^;]+\}/s);
                if (m) {
                  const obj = JSON.parse(m[0]);
                  const list = obj.item_card_list || obj.items;
                  if (list?.length) {
                    result.source = 'script_tag';
                    result.items = list.slice(0, 60).map(it => {
                      const b = it.item_basic || it;
                      return {
                        name: b.name, itemid: b.itemid, shopid: b.shopid,
                        price: priceToVnd(b.price), sold: b.sold,
                        category: categoryName || '',
                        link: `https://shopee.vn/product/${b.shopid}/${b.itemid}`,
                      };
                    });
                    break;
                  }
                }
              }
            }
          } catch (_) {}
        }

        return result;
      },
    });
    return res?.result ?? null;
  } catch (e) {
    log('extractPageData error: ' + e.message);
    if (/error page|timed out|proxy|ERR_/i.test(e.message || '')) {
      reportNetworkError('Không lấy DOM được vì tab đang ở trang lỗi/proxy.');
    }
    return null;
  }
}

// â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
// Hệ số tốc-độ-phiên: mỗi lần SW khởi động chọn 1 nhịp riêng (≈0.8-1.5×) → mọi quãng chờ HÀNH VI
// co giãn nhất quán theo phiên, diệt tính bất biến cross-session/account. KHÔNG áp cho timeout chờ load.
let sessionPace = 0.8 + Math.random() * 0.7;
{ const h = new Date().getHours(); if (h >= 23 || h < 6) sessionPace *= 1.15; }   // đêm chậm hơn chút
const rand    = (min, max) => min + Math.random() * (max - min);
const randInt = (min, max) => Math.floor(min + Math.random() * (max - min + 1));
const clamp   = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
// Quãng chờ hành vi: nhân sessionPace (KHÔNG dùng cho timeout hệ thống — dùng rawSleep cho cái đó).
const sleep = ms => new Promise(r => setTimeout(r, Math.round(ms * sessionPace)));
const rawSleep = ms => new Promise(r => setTimeout(r, ms));

async function waitForTabLoad(tabId, timeoutMs = 15000) {
  return new Promise(resolve => {
    let done = false;
    const finish = () => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      chrome.tabs.onUpdated.removeListener(fn);
      resolve();
    };
    const timer = setTimeout(finish, timeoutMs);
    const fn = (id, info) => {
      if (id === tabId && info.status === 'complete') {
        finish();
      }
    };

    chrome.tabs.get(tabId, tab => {
      if (done) return;
      if (tab?.status === 'complete') { finish(); return; }
      chrome.tabs.onUpdated.addListener(fn);
    });
  });
}

async function waitForUrlChange(previousUrl, timeoutMs = 10000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const tab = await chrome.tabs.get(searchTabId).catch(() => null);
    if (!tab) return false;
    if (tab.url && tab.url !== previousUrl) return true;
    await sleep(350);
  }
  return false;
}

async function waitForUrl(urlPart, timeoutMs = 10000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const tab = await chrome.tabs.get(searchTabId).catch(() => null);
    if (!tab) return false;
    if (tab.url?.includes(urlPart)) return true;
    await sleep(400);
  }
  return false;
}

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
    initialTabId = tabId;
    log(`Port ${port}, tabId=${tabId}`);
    // Reconnect if the port changed OR the socket isn't actually open — after a service
    // worker restart wsPort may have reset to 9111 while no fresh 'complete' fires.
    if (port && (port !== wsPort || !ws || ws.readyState !== WebSocket.OPEN)) connectWs(port);
  }
});

// Restore the lane's WS port across service-worker restarts. A plain connectWs(DEFAULT)
// here would pin the SW to 9111; if the lane's shopee tab already finished loading there's
// no new 'complete' event to re-point it to the lane port → permanent "waiting for extension".
chrome.storage.local.get('_wsPort', (data) => {
  const p = data && data._wsPort ? parseInt(data._wsPort) : DEFAULT_WS_PORT;
  connectWs(p || DEFAULT_WS_PORT);
});
