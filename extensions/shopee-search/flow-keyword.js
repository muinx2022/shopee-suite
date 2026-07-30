// â”€â”€ Search â€” type keyword + Enter, collect DOM data â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
import { ctx, log, send, sleep, stopSearch, cdpClickAt, reportNetworkError } from './core.js';
import { closeApiTabs, closeOtherTabs, resolveSearchTab, getCurrentTabUrl, waitForTabLoad, waitForUrl } from './tabs.js';
import { isVerifyPage, isNetworkErrorPage } from './detect.js';
import { prepareBestSelling, typeAndSearch } from './page-funcs.js';
import { crawlPagesForCurrentState } from './crawl.js';

export async function startSearch(msg) {
  stopSearch();
  const { keyword } = msg;
  const resumeCategoryIndex = Math.max(1, Number(msg.resumeCategoryIndex || 1));
  // Page to resume at WITHIN the resumed category (account swap continues here, not page 1).
  const resumePage = Math.max(1, Number(msg.resumePage || 1));
  // Capture a local run state and bind it as the global current run.
  // `dead()` is true once this run is no longer the active one (a newer
  // startSearch replaced it), was stopped, or hit a network error — every
  // await below re-checks it so a stale/zombie run exits instead of fighting
  // a newer one over the same tab.
  const state = {
    keyword, resumeCategoryIndex,
    stopped: false, networkErrorDetected: false, captchaDetected: false,
  };
  ctx.searchState = state;
  const dead = () => state !== ctx.searchState || state.stopped || state.networkErrorDetected;

  await closeApiTabs();
  if (dead()) return;

  // Use initial warm tab or create one
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

  // â”€â”€ Step 1: navigate to Shopee homepage â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
  log('Mở trang chủ Shopee...');
  await chrome.tabs.update(ctx.searchTabId, { url: 'https://shopee.vn/' });
  await waitForTabLoad(ctx.searchTabId);
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
    await chrome.tabs.update(ctx.searchTabId, {
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
    await chrome.tabs.update(ctx.searchTabId, { url: buildSearchUrl(keyword) });
  }
  await waitForTabLoad(ctx.searchTabId);
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
  await waitForTabLoad(ctx.searchTabId);
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
    if (dead()) return;
    const category = categories[i];
    log(`Category ${i + 1}/${categories.length}: ${category.name}`);
    await chrome.tabs.update(ctx.searchTabId, { url: baseSearchUrl });
    await waitForTabLoad(ctx.searchTabId);
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
    await waitForTabLoad(ctx.searchTabId);
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

function buildSearchUrl(keyword) {
  const params = new URLSearchParams({
    keyword: keyword || '',
    by: 'sales',
    order: 'desc',
  });
  return `https://shopee.vn/search?${params.toString()}`;
}

async function collectSearchCategories() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: ctx.searchTabId },
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
      target: { tabId: ctx.searchTabId },
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
      target: { tabId: ctx.searchTabId },
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
      target: { tabId: ctx.searchTabId },
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
