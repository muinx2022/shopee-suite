// Hàm bơm vào trang cho bước CHUẨN BỊ trang tìm kiếm: sắp "Bán chạy" + gõ từ khoá.
// LƯU Ý: hàm truyền cho chrome.scripting.executeScript({func}) bị serialize ĐỘC LẬP — nó không
// thấy import của module, nên mọi helper (sleep/rand/mouseEvent…) PHẢI nằm trong thân hàm.
import { ctx, log, sleep, cdpGesture, cdpClickAt } from './core.js';
import { cdpScrollToLoadThenTop } from './crawl.js';

// Resolve the "best selling" sort button (MAIN world).
async function resolveBestSellingPoint() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: ctx.searchTabId }, world: 'MAIN',
      func: () => {
        const rx = /top\s*sales|best\s*selling|b[aá]n\s*ch/i;
        const sortGroup = document.querySelector('.shopee-sort-by-options__option-group')
          || document.querySelector('.shopee-sort-by-options');
        // 3-tier priority (avoid clicking a look-alike button elsewhere on the page):
        //  1) text match SCOPED to the sort bar container;
        //  2) text match page-wide (only if no container / no in-bar match);
        //  3) positional index[2] in the sort group as a last resort (+ flag/log).
        let usedIndexFallback = false;
        let btn = sortGroup
          ? Array.from(sortGroup.querySelectorAll('button')).find(b => rx.test((b.textContent || '').trim()))
          : null;
        if (!btn) {
          btn = Array.from(document.querySelectorAll('.shopee-sort-by-options button, button'))
            .find(b => rx.test((b.textContent || '').trim()));
        }
        if (!btn) {
          const sortButtons = sortGroup ? Array.from(sortGroup.querySelectorAll('button')) : [];
          if (sortButtons.length >= 3) { btn = sortButtons[2]; usedIndexFallback = true; }
        }
        if (!btn) return { ok: false };
        btn.scrollIntoView({ block: 'center' });
        const r = btn.getBoundingClientRect();
        return {
          ok: r.width > 0 && r.height > 0,
          usedIndexFallback,
          alreadyPressed: btn.getAttribute('aria-pressed') === 'true',
          x: r.left + (0.3 + Math.random() * 0.4) * r.width, y: r.top + (0.3 + Math.random() * 0.4) * r.height, dpr: window.devicePixelRatio,
        };
      },
    });
    const out = res?.result ?? { ok: false };
    if (out.usedIndexFallback) log('Nút "Bán chạy": không khớp text, dùng fallback vị trí thứ 3.');
    return out;
  } catch (e) { log('resolveBestSellingPoint error: ' + e.message); return { ok: false }; }
}

// If sort didn't take via the UI, navigate to sales-sorted search URL (MAIN world).
async function applySalesSortFallbackIfNeeded() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: ctx.searchTabId }, world: 'MAIN',
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

export async function prepareBestSelling() {
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
      target: { tabId: ctx.searchTabId },
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

        let bestSellingIndexFallback = false;
        function findBestSellingButton() {
          const rx = /top\s*sales|best\s*selling|b[aá]n\s*ch/i;
          const sortGroup = document.querySelector('.shopee-sort-by-options__option-group')
            || document.querySelector('.shopee-sort-by-options');
          // 3-tier: (1) text scoped to sort bar; (2) text page-wide; (3) index[2] in sort group.
          const inBar = sortGroup
            ? Array.from(sortGroup.querySelectorAll('button')).find(b => rx.test((b.textContent || '').trim()))
            : null;
          if (inBar) return inBar;
          const byText = Array.from(document.querySelectorAll('.shopee-sort-by-options button, button'))
            .find(b => rx.test((b.textContent || '').trim()));
          if (byText) return byText;
          const sortButtons = sortGroup ? Array.from(sortGroup.querySelectorAll('button')) : [];
          if (sortButtons.length >= 3) { bestSellingIndexFallback = true; return sortButtons[2]; }
          return null;
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

        return { clickedBestSelling, setPrice: false, firstScrollSteps, fallbackNavigate, bestSellingIndexFallback };
      },
    });
    const out = res?.result ?? null;
    if (out && out.bestSellingIndexFallback) log('Nút "Bán chạy" (synthetic): không khớp text, dùng fallback vị trí thứ 3.');
    return out;
  } catch (e) {
    log('prepareBestSelling error: ' + e.message);
    return null;
  }
}

// â”€â”€ Type keyword into Shopee search box and submit (human-like) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
// Resolve a click point for the visible search input (after scrollIntoView).
async function resolveSearchInputPoint() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: ctx.searchTabId },
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

export async function typeAndSearch(keyword) {
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
        target: { tabId: ctx.searchTabId }, world: 'MAIN',
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
        target: { tabId: ctx.searchTabId }, world: 'MAIN',
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
      target: { tabId: ctx.searchTabId },
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
