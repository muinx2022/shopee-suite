// Cào theo trang: đọc trạng thái cuộn, cuộn kiểu người, đọc/bấm phân trang, vòng lặp trang.
import { ctx, log, send, sleep, cdpGesture, cdpClickAt, reportNetworkError } from './core.js';
import { rand, randInt, clamp } from './shared/util.js';
import { getCurrentTabUrl, waitForTabLoad, waitForUrlChange } from './tabs.js';
import { isVerifyPage, isNetworkErrorPage } from './detect.js';
import { extractPageData } from './extract.js';

// Read scroll state + visible product links from the page (MAIN world).
async function readScrollState() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: ctx.searchTabId },
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
      target: { tabId: ctx.searchTabId },
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

// Trusted CDP scroll: load lazy products then return to the top, driven from here.
export async function cdpScrollToLoadThenTop(maxSteps = 24) {
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

async function hasNextSearchPage() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: ctx.searchTabId },
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
      target: { tabId: ctx.searchTabId }, world: 'MAIN',
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

// Scroll to the pager and resolve the next-page button's center + href (MAIN world).
async function resolveNextPagePoint() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: ctx.searchTabId },
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
          target: { tabId: ctx.searchTabId }, world: 'MAIN',
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
      target: { tabId: ctx.searchTabId },
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

export async function crawlPagesForCurrentState(state, keyword, categoryName, categoryIndex, categoryTotal, maxPages, finalCategory, startPage = 1) {
  const dead = () => state !== ctx.searchState || state.stopped || state.networkErrorDetected;
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
      await chrome.tabs.update(ctx.searchTabId, { url: u.toString() });
      await waitForTabLoad(ctx.searchTabId);
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
    await waitForTabLoad(ctx.searchTabId, 8000);
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
