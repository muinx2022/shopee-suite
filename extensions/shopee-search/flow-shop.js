// â”€â”€ Shop-from-link flow â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
// Open a product link → its shop → "All products" → sort "Top sales" → crawl product
// pages. The crawl/pagination part is shared with the keyword flow.
import { ctx, log, send, sleep, stopSearch, cdpClickAt, reportNetworkError } from './core.js';
import { closeApiTabs, closeOtherTabs, resolveSearchTab, waitForTabLoad } from './tabs.js';
import { isVerifyPage, isNetworkErrorPage, isProductNotFoundPage } from './detect.js';
import { crawlPagesForCurrentState } from './crawl.js';

export async function startShopFromLink(msg) {
  stopSearch();
  const link = msg.link || '';
  const state = {
    keyword: link, link, resumeCategoryIndex: 1,
    stopped: false, networkErrorDetected: false, captchaDetected: false,
    mode: 'shopFromLink',
  };
  ctx.searchState = state;
  const dead = () => state !== ctx.searchState || state.stopped || state.networkErrorDetected;

  await closeApiTabs();
  if (dead()) return;

  ctx.searchTabId = await resolveSearchTab();
  if (dead()) return;
  if (!ctx.searchTabId) {
    const t = await chrome.tabs.create({ url: 'https://shopee.vn/', active: true });
    ctx.searchTabId = t.id;
    await waitForTabLoad(ctx.searchTabId);
    if (dead()) return;
  }
  await closeOtherTabs(ctx.searchTabId);
  if (dead()) return;

  log('Mở link sản phẩm: ' + link);
  await chrome.tabs.update(ctx.searchTabId, { url: link });
  await waitForTabLoad(ctx.searchTabId);
  await sleep(2500 + Math.random() * 1500);
  if (dead()) return;
  if (await isNetworkErrorPage()) { reportNetworkError('Không tải được trang sản phẩm.'); return; }
  if (await isVerifyPage()) { state.captchaDetected = true; send({ action: 'captcha' }); return; }
  // Link chết (SP không tồn tại/đã xoá): báo TERMINAL để coordinator đánh dấu link và sang link
  // kế — KHÔNG networkError (sẽ đổi account + mở lại đúng link đó vô hạn → "máy mở đi mở lại").
  if (await isProductNotFoundPage()) { send({ action: 'error', message: 'Sản phẩm không tồn tại — bỏ qua link.' }); return; }

  if (dead()) return;
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
  await waitForTabLoad(ctx.searchTabId);
  await sleep(2500 + Math.random() * 1500);
  if (dead()) return;
  if (await isVerifyPage()) { state.captchaDetected = true; send({ action: 'captcha' }); return; }

  const shopName = await readShopName();
  if (shopName) { state.shopName = shopName; }

  if (dead()) return;
  log('Bấm "Tất cả sản phẩm"...');
  const okAll = await clickAllProducts();
  if (dead()) return;
  if (!okAll) log('Không thấy menu "Tất cả sản phẩm", tiếp tục với trang hiện tại.');
  await waitForTabLoad(ctx.searchTabId);
  await sleep(2500 + Math.random() * 1500);
  if (dead()) return;

  if (dead()) return;
  log('Sắp xếp theo "Bán chạy"...');
  await clickTopSalesShop();
  if (dead()) return;
  await waitForTabLoad(ctx.searchTabId);
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

// Resolve + click an anchor (view shop / all products); fall back to navigating its
// href if the trusted click didn't change the URL.
async function clickResolvedAnchor(pt) {
  if (!pt.ok) return false;
  try {
    await cdpClickAt(pt.x, pt.y);
    await sleep(900 + Math.random() * 700);
    if (pt.href) {
      const [res] = await chrome.scripting.executeScript({
        target: { tabId: ctx.searchTabId }, world: 'MAIN', args: [pt.beforeUrl],
        func: (before) => location.href === before,
      });
      if (res?.result === true) await chrome.tabs.update(ctx.searchTabId, { url: pt.href });
    }
    return true;
  } catch (e) {
    log('Anchor click via CDP failed, navigate href: ' + e.message);
    if (pt.href) { await chrome.tabs.update(ctx.searchTabId, { url: pt.href }); return true; }
    return false;
  }
}

async function resolveViewShopPoint() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: ctx.searchTabId }, world: 'MAIN',
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
      target: { tabId: ctx.searchTabId }, world: 'MAIN',
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
      target: { tabId: ctx.searchTabId }, world: 'MAIN',
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
      target: { tabId: ctx.searchTabId }, world: 'MAIN',
      func: () => {
        const bar = document.querySelector('fieldset.shopee-sort-bar');
        if (!bar) return { ok: false };
        const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
        const opts = Array.from(bar.querySelectorAll('.sort-by-options__option'));
        // Text match first; positional index[2] only as a last-resort fallback.
        let usedIndexFallback = false;
        let b = opts.find(x => /bán chạy|top sales/.test(norm(x.textContent)));
        if (!b && opts.length >= 3) { b = opts[2]; usedIndexFallback = true; }
        if (!b) return { ok: false };
        b.scrollIntoView({ block: 'center' });
        const r = b.getBoundingClientRect();
        return { ok: r.width > 0 && r.height > 0, usedIndexFallback, already: b.getAttribute('aria-pressed') === 'true', x: r.left + r.width / 2, y: r.top + r.height / 2, dpr: window.devicePixelRatio };
      },
    });
    const out = res?.result ?? { ok: false };
    if (out.usedIndexFallback) log('Nút "Bán chạy" (shop): không khớp text, dùng fallback vị trí thứ 3.');
    return out;
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
      target: { tabId: ctx.searchTabId }, world: 'MAIN', args: [pt.x, pt.y],
      func: (x, y) => { const el = document.elementFromPoint(x, y); if (el) el.click(); },
    });
    return true;
  }
}
