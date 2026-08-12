// ── Category-from-link flow ─────────────────────────────────────────────────
// Mở link CATEGORY → lặp qua MỌI danh mục con (sub-category) → mỗi sub: lọc "Nơi Bán"
// khớp khu vực + sắp "Bán chạy" → cào sản phẩm (tái dùng crawlPagesForCurrentState).
import { ctx, log, send, sleep, stopSearch, cdpClickAt, reportNetworkError } from './core.js';
import { closeApiTabs, closeOtherTabs, resolveSearchTab, waitForTabLoad, waitForUrl } from './tabs.js';
import { isVerifyPage, isNetworkErrorPage } from './detect.js';
import { prepareBestSelling } from './page-funcs.js';
import { crawlPagesForCurrentState } from './crawl.js';

export async function startCategoryFromLink(msg) {
  stopSearch();
  const link = msg.link || '';
  const region = (msg.region || '').trim();
  // Điểm tiếp tục do app gửi (đổi account / nối lại WS / bấm Tiếp tục). Bỏ qua hai field này là mỗi lần
  // nối lại đều cào lại từ danh mục #1 và checkpoint bị đẩy LÙI về 1 — link không bao giờ chạy hết.
  const resumeIdx = Math.max(1, (msg.resumeCategoryIndex | 0) || 1);
  const resumePage = Math.max(1, (msg.resumePage | 0) || 1);
  const state = {
    keyword: link, link, region, resumeCategoryIndex: resumeIdx,
    stopped: false, networkErrorDetected: false, captchaDetected: false,
    mode: 'categoryFromLink',
  };
  if (resumeIdx > 1 || resumePage > 1) log(`⏯ Tiếp tục từ danh mục #${resumeIdx}, trang ${resumePage}.`);
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

  // Mở TRANG CHỦ rồi CLICK danh mục (theo cat id trong link). Điều hướng SPA bằng click MỚI có rail
  // bộ lọc; mở URL category trực tiếp thì Shopee trả trang KHÔNG có bộ lọc.
  const catId = parseCatId(link);
  log('Mở trang chủ Shopee…');
  await chrome.tabs.update(ctx.searchTabId, { url: 'https://shopee.vn/' });
  await waitForTabLoad(ctx.searchTabId);
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
    await chrome.tabs.update(ctx.searchTabId, { url: link });
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
    if (dead()) return;
    log('Sắp xếp "Bán chạy"...');
    await prepareBestSelling(); if (dead()) return;
    await waitForTabLoad(ctx.searchTabId);
    await sleep(2500 + Math.random() * 1500);
    if (dead()) return;
    await crawlPagesForCurrentState(state, link, '', 1, 1, 50, true, resumePage);
    if (dead() || state.captchaDetected) return;
    send({ action: 'done' });
    return;
  }

  log(`Tìm thấy ${subs.length} danh mục con — lần lượt cào từng cái.`);
  // Danh sách danh mục con có thể đã đổi so với lúc lưu checkpoint → mốc cũ trỏ ra ngoài danh sách:
  // cào lại từ đầu (kèm cảnh báo) còn hơn bỏ trắng cả link.
  let startIdx = resumeIdx - 1;
  let startPage = resumePage;
  if (startIdx >= subs.length) {
    log(`⚠ Danh mục #${resumeIdx} vượt quá ${subs.length} danh mục con (danh sách đã đổi) — cào lại từ danh mục #1.`);
    startIdx = 0;
    startPage = 1;
  }
  for (let i = startIdx; i < subs.length; i++) {
    if (dead()) return;
    const sub = subs[i];
    log(`Danh mục con ${i + 1}/${subs.length}: ${sub.name}`);
    // CLICK danh mục con trong rail (SPA → giữ bộ lọc), KHÔNG nav URL trực tiếp.
    const subCatId = parseCatId(sub.href);
    const sc = await clickSubCategory(sub.href);
    if (!sc.ok) {
      log('Không click được danh mục con (rail) — mở URL trực tiếp.');
      await chrome.tabs.update(ctx.searchTabId, { url: sub.href });
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
    if (dead()) return;
    log('Sắp xếp "Bán chạy"...');
    await prepareBestSelling(); if (dead()) return;
    await waitForTabLoad(ctx.searchTabId);
    await sleep(2500 + Math.random() * 1500);
    if (dead()) return;
    if (await isVerifyPage()) { state.captchaDetected = true; send({ action: 'captcha' }); return; }

    // Chỉ danh mục ĐẦU TIÊN của lượt tiếp tục mới nhảy vào giữa chừng; các danh mục sau cào từ trang 1.
    await crawlPagesForCurrentState(state, sub.href, sub.name, i + 1, subs.length, 50, i === subs.length - 1,
      i === startIdx ? startPage : 1);
    if (dead() || state.captchaDetected) return;
  }
  send({ action: 'done' });
}

// Thu thập danh mục con của category đang mở (mở rộng "Thêm" để lấy đủ).
async function collectSubCategories() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: ctx.searchTabId }, world: 'MAIN',
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
      target: { tabId: ctx.searchTabId }, world: 'MAIN',
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
      target: { tabId: ctx.searchTabId }, world: 'MAIN', args: [topId],
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
      target: { tabId: ctx.searchTabId }, world: 'MAIN', args: [href],
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
      target: { tabId: ctx.searchTabId }, world: 'MAIN',
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
      target: { tabId: ctx.searchTabId }, world: 'MAIN', args: [String(region || '')],
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
  const dead = () => state !== ctx.searchState || state.stopped || state.networkErrorDetected;
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
    await waitForTabLoad(ctx.searchTabId);
    await sleep(2500 + Math.random() * 1500);
    return true;
  } catch (e) { log('applyLocationFilter error: ' + e.message); return false; }
}
