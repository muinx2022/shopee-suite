/**
 * Shopee Data Runner — thin executor for Multi Brave Manager.
 * Launcher điều khiển vòng lặp dòng / log / start-stop; extension chỉ:
 * - mở tab + hiện overlay thông báo
 * - click scrape và trả kết quả
 */

// shared/ là BẢN COPY của extensions/shared/ — sửa ở nguồn chuẩn rồi chạy extensions/sync-shared.cmd.
import { sleep } from "./shared/util.js";
import { getTabSafe, waitForTabComplete } from "./shared/tab-wait.js";
import { isVerifyUrl, isNetworkErrorPage } from "./shared/net-detect.js";

const SCRAPE_SCRIPT = "content.js";
const OVERLAY_SCRIPT = "overlay.js";
const TAB_LOAD_TIMEOUT_MS = 30_000;
// content.js nay chờ kết quả THEO TRẠNG THÁI BigSeller (còn "scraping" thì chờ tới khi xong, không
// đặt giờ cứng). Waiter background CHỈ là chốt chặn rất rộng để không cắt content.js giữa chừng (cắt
// sớm → tưởng fail → reload + click lại). Để 9' (> watchdog 8' — watchdog mới là cơ chế xử lý kẹt thật).
const SCRAPE_WAIT_TIMEOUT_MS = 540_000;
const MAX_SCRAPE_RETRIES = 1;

let scrapeWaiters = [];
let scrapeTokenSeq = 0;
let abortRequested = false;

// Token duy nhất cho mỗi lượt chờ kết quả scrape. Content script echo lại token này trong
// SCRAPE_RESULT; background chỉ resolve waiter có token khớp → kết quả cũ (đến muộn sau khi đã
// re-inject) không còn resolve nhầm waiter mới.
const nextScrapeToken = (rowNumber) => `${Number(rowNumber) || 0}-${++scrapeTokenSeq}`;

const readState = async () => {
  const data = await chrome.storage.local.get("runnerState");
  return data.runnerState || { running: false };
};

const writeState = async (patch) => {
  const current = await readState();
  const next = { ...current, ...patch };
  await chrome.storage.local.set({ runnerState: next });
  syncKeepAlive(next);
  return next;
};

const broadcastState = async () => {
  try {
    const state = await readState();
    await chrome.runtime.sendMessage({ type: "RUNNER_STATE", state });
  } catch (_) {}
};

// ── Keep-alive cho service worker (MV3) ──────────────────────────────────────
// SW bị Chromium evict sau ~30s idle. Khi scrape có khoảng nghỉ 2–4 phút giữa các link,
// SW chết giữa chừng → launcher mất kết nối (SW_NO_RESPONSE / mất hook), dù trước đó đã
// kết nối ngon. Giữ SW sống bằng ping API mỗi 20s (mỗi lời gọi chrome.* reset bộ đếm idle
// 30s) + alarm dự phòng để dựng/rearm lại nếu SW vẫn bị recycle. CHỈ bật khi đang chạy để
// không giữ SW sống vô ích lúc rảnh.
let __keepAliveTimer = null;
const KEEPALIVE_PING_MS = 20_000;

const startKeepAlive = () => {
  if (__keepAliveTimer === null) {
    __keepAliveTimer = setInterval(() => {
      try { chrome.runtime.getPlatformInfo(() => {}); } catch (_) {}
    }, KEEPALIVE_PING_MS);
  }
  try { chrome.alarms.create("sw-keepalive", { periodInMinutes: 0.4 }); } catch (_) {}
};

const stopKeepAlive = () => {
  if (__keepAliveTimer !== null) {
    clearInterval(__keepAliveTimer);
    __keepAliveTimer = null;
  }
  try { chrome.alarms.clear("sw-keepalive"); } catch (_) {}
};

const syncKeepAlive = (state) => {
  if (state && state.running) startKeepAlive();
  else stopKeepAlive();
};

// SW vừa bị recycle giữa lúc đang chạy → alarm đánh thức rồi tự bật lại keepalive.
try {
  chrome.alarms.onAlarm.addListener((alarm) => {
    if (alarm && alarm.name === "sw-keepalive") {
      readState().then(syncKeepAlive).catch(() => {});
    }
  });
} catch (_) {}

// SW (re)spawn khi run vẫn đang chạy → bật lại keepalive ngay khi load.
readState().then((s) => { if (s && s.running) startKeepAlive(); }).catch(() => {});

// Fix #3: ghi tiến độ (dòng vừa scrape xong) vào chrome.storage NGAY khi scrape OK — TRƯỚC khi trang
// reload xong / trước khi trả kết quả về launcher. Nếu SW/CDP rớt do reload làm kết quả không tới được
// launcher, lần relaunch sau ExtensionProgressReader (C#) vẫn đọc được lastCompletedRow ở đây → resume
// ĐÚNG dòng kế, KHÔNG scrape lại dòng đã xong (gãy vòng lặp "scrape → reload → scrape lại").
const persistCompletedRow = async (rowNumber) => {
  const row = Number(rowNumber);
  if (!(row > 0)) return;
  try {
    await writeState({ lastCompletedRow: row, currentRow: row });
  } catch (_) {}
};

const normalizeLink = (value) => {
  if (typeof value !== "string") return "";
  const trimmed = value.trim();
  if (!trimmed) return "";
  if (/^https?:\/\//i.test(trimmed)) return trimmed;
  if (trimmed.startsWith("www.")) return `https://${trimmed}`;
  return trimmed;
};

const isScrapeWorkTabUrl = (url) => {
  if (!url || typeof url !== "string") return false;
  if (url.startsWith("chrome-extension://")) return false;
  if (url.startsWith("chrome://") || url.startsWith("brave://")) return false;
  try {
    const parsed = new URL(url);
    if (!/(^|\.)shopee\./i.test(parsed.hostname)) return false;
    if (/\/buyer\/login/i.test(parsed.pathname)) return false;
    return true;
  } catch (_) {
    return /shopee/i.test(url) && !/buyer\/login/i.test(url);
  }
};

/** Tìm tab Shopee đang dùng để scrape — tránh chrome.tabs.create khi tab cũ vẫn còn. */
const findReuseableScrapeTab = async (preferTabId = null) => {
  if (preferTabId) {
    const preferred = await getTabSafe(preferTabId);
    if (preferred?.id && isScrapeWorkTabUrl(preferred.url)) return preferred.id;
  }

  const tabs = await chrome.tabs.query({});
  const candidates = tabs.filter((t) => t.id && isScrapeWorkTabUrl(t.url || ""));
  if (candidates.length === 0) return null;

  const active = candidates.find((t) => t.active);
  return active?.id ?? candidates[candidates.length - 1].id;
};

const openLinkInTab = async (tabId, url) => {
  const existing = await getTabSafe(tabId);
  if (existing?.id) {
    try {
      await chrome.tabs.update(existing.id, { url, active: true });
      return existing.id;
    } catch (_) {
      // tab biến mất giữa get và update
    }
  }

  const reuseId = await findReuseableScrapeTab(tabId);
  if (reuseId) {
    try {
      await chrome.tabs.update(reuseId, { url, active: true });
      return reuseId;
    } catch (_) {
      // reuse tab cũng không update được
    }
  }

  const created = await chrome.tabs.create({ url, active: true });
  return created.id;
};

const getCurrentTabUrl = async (tabId, fallback = "") => {
  const tab = await getTabSafe(tabId);
  return tab?.url || fallback || "";
};

const isShopeeProductUrl = (url) => {
  try {
    const parsed = new URL(url);
    const host = parsed.hostname.toLowerCase();
    if (!/(^|\.)shopee\./i.test(host)) return false;
    if (isVerifyUrl(url)) return false;
    // Nhận cả 3 dạng link sản phẩm Shopee:
    //  - SEO:   /<ten>-i.<shopid>.<itemid>
    //  - /product/<shopid>/<itemid>   (dạng Shopee hay redirect tới — TRƯỚC ĐÂY BỊ BỎ SÓT → kẹt "không sang link kế")
    //  - query: ?itemid=...&shopid=...
    return /-i\.\d+\.\d+/i.test(parsed.pathname) ||
           /\/product\/\d+\/\d+/i.test(parsed.pathname) ||
           /[?&](itemid|shopid)=/i.test(parsed.search);
  } catch (_) {
    return false;
  }
};

const waitForUrlStable = async (tabId, settleMs = 2500) => {
  let lastUrl = await getCurrentTabUrl(tabId);
  const deadline = Date.now() + settleMs;
  while (Date.now() < deadline) {
    await sleep(400);
    const current = await getCurrentTabUrl(tabId, lastUrl);
    if (current !== lastUrl) {
      lastUrl = current;
      continue;
    }
  }
  return lastUrl;
};

const injectOverlayManager = async (tabId) => {
  if (!(await getTabSafe(tabId))) return false;
  try {
    await chrome.scripting.executeScript({ target: { tabId }, files: [OVERLAY_SCRIPT] });
    return true;
  } catch (_) {
    return false;
  }
};

const rearmScrapeClicker = async (tabId) => {
  // Gỡ cờ guard injected cũ để lần chèn kế clicker chạy lại. Cần khi tab GIỮ NGUYÊN document
  // (vd. sau khi giải captcha mà trang không reload) — cờ __...ScrapeClickerInjected vẫn = true
  // sẽ khiến content script return ngay đầu, KHÔNG tìm/click nút scrape.
  await chrome.scripting.executeScript({
    target: { tabId },
    func: () => { window.__shopee27052026ScrapeClickerInjected = false; },
  }).catch(() => {});
};

const injectScrapeClicker = async (tabId, token) => {
  if (!(await getTabSafe(tabId))) return false;
  try {
    // Đặt token vào page (world ISOLATED, cùng world với content.js file-inject) TRƯỚC khi chèn
    // clicker, để content.js đọc và echo lại trong SCRAPE_RESULT.
    await chrome.scripting.executeScript({
      target: { tabId },
      func: (t) => { window.__shopeeScrapeResultToken = t; },
      args: [token ?? null],
    });
    await chrome.scripting.executeScript({ target: { tabId }, files: [SCRAPE_SCRIPT] });
    return true;
  } catch (_) {
    return false;
  }
};

const showOverlay = async (tabId, text) => {
  try {
    await chrome.tabs.sendMessage(tabId, { type: "SHOW_NEXT_LINK_MESSAGE", text });
  } catch (_) {}
};

const hideOverlay = async (tabId) => {
  try {
    await chrome.tabs.sendMessage(tabId, { type: "HIDE_NEXT_LINK_MESSAGE" });
  } catch (_) {}
};

const rejectAllScrapeWaiters = (message) => {
  while (scrapeWaiters.length) {
    const waiter = scrapeWaiters.shift();
    if (waiter?.resolve) waiter.resolve({ ok: false, message });
  }
};

const waitForScrapeResult = async (token, timeoutMs = SCRAPE_WAIT_TIMEOUT_MS) => {
  return new Promise((resolve) => {
    const waiter = { token, resolve: null };
    const timeout = setTimeout(() => {
      const index = scrapeWaiters.indexOf(waiter);
      if (index >= 0) scrapeWaiters.splice(index, 1);
      resolve({ ok: false, message: "Hết thời gian chờ kết quả scrape." });
    }, timeoutMs);
    waiter.resolve = (value) => {
      clearTimeout(timeout);
      resolve(value);
    };
    scrapeWaiters.push(waiter);
  });
};

// Trang lỗi mạng/proxy: dùng bộ dấu hiệu GỘP ở shared/net-detect.js. Giữ world ISOLATED (mặc định cũ) và
// coi "không inject được" là lỗi trang — y như checkPageLoadSuccess trước đây.
const isProxyErrorPage = (tabId) =>
  isNetworkErrorPage(tabId, { world: "ISOLATED", onInjectError: true });

const detectCaptcha = async (tabId) => {
  // URL của tab đọc qua chrome.tabs (KHÔNG cần inject). Trang /verify/captcha của Shopee có thể CHẶN
  // inject content script → executeScript ném lỗi → nếu chỉ dựa vào inject sẽ BỎ SÓT captcha. Vì vậy
  // bắt captcha theo URL ở ngay tầng background (kể cả khi inject lỗi).
  let tabUrl = "";
  try { tabUrl = await getCurrentTabUrl(tabId); } catch (_) {}
  const urlIsVerify = isVerifyUrl(tabUrl);
  try {
    const results = await chrome.scripting.executeScript({
      target: { tabId },
      args: [tabUrl],
      func: (currentTabUrl) => {
        const isVisible = (element) => {
          if (!element) return false;
          const style = window.getComputedStyle(element);
          const rect = element.getBoundingClientRect();
          return (
            style &&
            style.display !== "none" &&
            style.visibility !== "hidden" &&
            style.opacity !== "0" &&
            rect.width > 0 &&
            rect.height > 0
          );
        };
        const matchesScrape = (element) => {
          const label = [
            element.getAttribute("aria-label"),
            element.getAttribute("title"),
            element.textContent,
            element.value,
            element.dataset?.tooltip,
            element.dataset?.testid,
          ]
            .filter(Boolean)
            .join(" ")
            .toLowerCase();
          return label.includes("scrape");
        };
        const hasVisibleScrapeButton = () => {
          const selectors = [
            ".crawl_trigger.crawl_btn_wrapper.big_crawl.scraped button.btn_01.crawl_text.detail",
            ".crawl_trigger.crawl_btn_wrapper.big_crawl button.btn_01.crawl_text.detail",
            ".crawl_trigger.crawl_btn_wrapper.big_crawl button",
            "#scrapeBtn",
            ".bigseller-scrape",
            "[data-testid*='scrape']",
            "[aria-label*='scrape' i]",
            "[title*='scrape' i]",
          ];
          for (const selector of selectors) {
            const el = document.querySelector(selector);
            if (el && isVisible(el)) return true;
          }
          for (const el of document.querySelectorAll("button, [role='button'], input[type='button'], input[type='submit'], a")) {
            if (isVisible(el) && matchesScrape(el)) return true;
          }
          return false;
        };
        const bodyClone = document.body?.cloneNode(true);
        bodyClone?.querySelector("#shopee27052026-next-link-overlay")?.remove();
        const text = (bodyClone?.innerText || "").toLowerCase();
        const url = currentTabUrl || location.href || "";
        const scrapeReady = hasVisibleScrapeButton();
        let verifyPath = /^\/verify(?:\/|$)/i.test(location.pathname);
        try {
          verifyPath = verifyPath || /^\/verify(?:\/|$)/i.test(new URL(url).pathname);
        } catch (_) {}
        const captchaText =
          // English
          text.includes("verify to continue") ||
          text.includes("complete the puzzle") ||   // "please slide to complete the puzzle" + biến thể
          text.includes("slide to verify") ||
          text.includes("drag the slider") ||
          text.includes("press & hold") || text.includes("press and hold") ||
          text.includes("verify it's you") || text.includes("verify its you") ||
          // Tiếng Việt (Shopee hiển thị captcha theo ngôn ngữ trang)
          text.includes("trượt để") ||              // "Vui lòng trượt để hoàn thành câu đố"
          text.includes("hoàn thành câu đố") ||
          text.includes("xác minh để tiếp tục") ||
          text.includes("vui lòng xác minh") ||
          text.includes("xác nhận bạn không phải") || // "...không phải người máy/robot"
          text.includes("nhấn giữ");                // "Nhấn giữ để xác minh"
        // Phòng captcha KHÔNG khớp text (vd nằm trong iframe/khung riêng): phát hiện khung captcha hiển thị.
        let captchaEl = false;
        try {
          for (const el of document.querySelectorAll(
            "iframe[src*='captcha' i], iframe[src*='verify' i], .secsdk-captcha-drag-icon, " +
            "[class*='captcha_verify' i], [class*='geetest' i], [class*='captcha-slide' i]")) {
            if (isVisible(el)) { captchaEl = true; break; }
          }
        } catch (_) {}
        const captchaVisible = captchaText || captchaEl;
        return { detected: captchaVisible || verifyPath, scrapeReady, captchaVisible, title: document.title || "", url };
      },
    });
    const r = results?.[0]?.result ?? { detected: false, title: "", url: tabUrl };
    return { ...r, detected: r.detected || urlIsVerify, captchaVisible: r.captchaVisible || urlIsVerify };
  } catch (_) {
    // Inject lỗi (trang /verify chặn content script) → vẫn báo captcha dựa trên URL /verify của tab.
    return { detected: urlIsVerify, scrapeReady: false, captchaVisible: urlIsVerify, title: "", url: tabUrl };
  }
};

const markCaptchaTab = async (tabId, label) => {
  try {
    await chrome.scripting.executeScript({
      target: { tabId },
      args: [label],
      func: (titlePrefix) => {
        if (!window.__launcherOriginalTitle) {
          window.__launcherOriginalTitle = document.title || "";
        }
        document.title = `${titlePrefix} | ${window.__launcherOriginalTitle}`.slice(0, 120);
      },
    });
  } catch (_) {}
};

// Chờ giải captcha bằng TAY tối đa 3': hiện overlay nhắc giải, poll mỗi vài giây. Giải xong
// (captcha biến mất) → chạy tiếp (waited:true), KHÔNG đánh dấu. Quá 3' vẫn còn → mới báo captcha
// để launcher đánh dấu lỗi. (Trước đây hàm này thấy captcha là bỏ NGAY → đánh dấu oan dù người
// dùng có thể giải tay được.)
const CAPTCHA_MANUAL_WAIT_MS = 3 * 60_000;
const CAPTCHA_POLL_MS = 3_000;

const waitForCaptchaToClear = async (tabId, context) => {
  const firstCheck = await detectCaptcha(tabId);
  if (!firstCheck.detected) return { ok: true, waited: false };

  const instanceName = String(context?.instanceName || "Profile").trim();
  const rowNumber = Number(context?.rowNumber) || 0;
  const sku = String(context?.sku || "").trim();
  const rowText = rowNumber > 0 ? `dong ${rowNumber}` : "dong hien tai";
  const skuText = sku ? `\nSKU: ${sku}` : "";
  const message =
    `CAPTCHA - ${instanceName}\n${rowText}${skuText}\nGiai tay giup — tu dong chay tiep khi xong (toi da 3').`;

  await injectOverlayManager(tabId);
  await showOverlay(tabId, message);
  await markCaptchaTab(tabId, `CAPTCHA ${instanceName} ${rowText}`);

  const deadline = Date.now() + CAPTCHA_MANUAL_WAIT_MS;
  while (Date.now() < deadline) {
    if (abortRequested)
      return { ok: false, captcha: true, aborted: true, message: `Da huy khi cho giai captcha - ${instanceName}, ${rowText}.` };
    await sleep(CAPTCHA_POLL_MS);
    const check = await detectCaptcha(tabId);
    if (!check.detected)
      return { ok: true, waited: true }; // đã giải tay xong → chạy tiếp, không đánh dấu
  }

  // Quá 3' vẫn còn captcha → để launcher đánh dấu captcha/lỗi.
  return {
    ok: false,
    captcha: true,
    message: `Dung vi captcha (qua 3' khong giai tay) - ${instanceName}, ${rowText}.`,
  };
};

const checkCurrentLinkBeforeNext = async (tabId, context) => {
  const deadline = Date.now() + 30_000;
  let currentUrl = "";
  let stableProductSince = 0;

  while (Date.now() < deadline) {
    currentUrl = await getCurrentTabUrl(tabId, currentUrl);
    const captchaCheck = await detectCaptcha(tabId);
    if (isVerifyUrl(currentUrl) || captchaCheck.detected) {
      break;
    }

    if (isShopeeProductUrl(currentUrl)) {
      stableProductSince ||= Date.now();
      if (Date.now() - stableProductSince >= 3000) {
        return { ok: true, waited: false, pageUrl: currentUrl };
      }
    } else {
      stableProductSince = 0;
    }

    await sleep(700);
  }

  currentUrl = await getCurrentTabUrl(tabId, currentUrl);
  const captchaCheck = await detectCaptcha(tabId);
  if (isVerifyUrl(currentUrl) || captchaCheck.detected) {
    const captchaWait = await waitForCaptchaToClear(tabId, context);
    if (!captchaWait.ok) {
      return {
        ok: false,
        captcha: true,
        aborted: Boolean(captchaWait.aborted),
        message: captchaWait.message || `Dang dung vi captcha - ${context?.instanceName || "Profile"}, dong ${context?.rowNumber || ""}.`,
        pageUrl: await getCurrentTabUrl(tabId, currentUrl),
      };
    }
    return {
      ok: true,
      waited: true,
      pageUrl: await getCurrentTabUrl(tabId, currentUrl),
    };
  }

  if (!isShopeeProductUrl(currentUrl)) {
    return {
      ok: false,
      captcha: false,
      message: `URL hien tai khong phai link san pham Shopee: ${currentUrl || "(trong)"}`,
      pageUrl: currentUrl,
    };
  }

  return { ok: true, waited: false, pageUrl: currentUrl };
};

/** Một bước: mở link → overlay → click scrape. Launcher xử lý video / log / vòng lặp. */
globalThis.__launcherExecuteScrapeStep = async (payload) => {
  try {
    abortRequested = false;
    startKeepAlive(); // đang scrape → chắc chắn giữ SW sống dù launcher chưa kịp set running.
    const link = normalizeLink(payload?.link);
    const rowNumber = Number(payload?.rowNumber) || 0;
    const statusText =
      payload?.statusText ||
      (rowNumber > 0 ? `Đang xử lý dòng ${rowNumber}…` : "Đang xử lý…");
    const instanceName = String(payload?.instanceName || "Profile").trim();
    const sku = String(payload?.sku || "").trim();

    if (!link) {
      return { ok: false, scrapeOk: false, message: "Thiếu link.", tabId: null, pageUrl: "" };
    }

    let tabId = payload?.tabId ? Number(payload.tabId) : null;
    if (tabId && !(await getTabSafe(tabId))) {
      tabId = null;
    }

    tabId = await openLinkInTab(tabId, link);

    let loaded = await waitForTabComplete(tabId, TAB_LOAD_TIMEOUT_MS);
    if (!loaded) {
      const stillThere = await getTabSafe(tabId);
      if (stillThere?.id) {
        await sleep(1000);
        loaded = await waitForTabComplete(tabId, TAB_LOAD_TIMEOUT_MS);
        if (!loaded) {
          try {
            await chrome.tabs.update(tabId, { url: link, active: true });
            loaded = await waitForTabComplete(tabId, TAB_LOAD_TIMEOUT_MS);
          } catch (_) {
            tabId = await openLinkInTab(await findReuseableScrapeTab(), link);
            loaded = await waitForTabComplete(tabId, TAB_LOAD_TIMEOUT_MS);
          }
        }
      } else {
        tabId = await openLinkInTab(await findReuseableScrapeTab(), link);
        loaded = await waitForTabComplete(tabId, TAB_LOAD_TIMEOUT_MS);
      }

      if (!loaded) {
        const surviving = await getTabSafe(tabId);
        return {
          ok: false,
          scrapeOk: false,
          message: "Tab đã đóng hoặc không tải được trang.",
          tabId: surviving?.id ?? tabId,
          pageUrl: link,
        };
      }
    }

    if (abortRequested) {
      return { ok: false, scrapeOk: false, message: "Đã hủy.", tabId, pageUrl: link, aborted: true };
    }

    let currentPageUrl = await waitForUrlStable(tabId);
    if (isVerifyUrl(currentPageUrl)) {
      await injectOverlayManager(tabId);
      const verifyWait = await waitForCaptchaToClear(tabId, { instanceName, rowNumber, sku });
      if (!verifyWait.ok) {
        return {
          ok: false,
          scrapeOk: false,
          captcha: true,
          aborted: Boolean(verifyWait.aborted),
          message: verifyWait.message || `Dang dung vi captcha - ${instanceName}, dong ${rowNumber}.`,
          tabId,
          pageUrl: currentPageUrl || link,
        };
      }
      currentPageUrl = await waitForUrlStable(tabId);
    }

    if (await isProxyErrorPage(tabId)) {
      return {
        ok: false,
        scrapeOk: false,
        proxyError: true,
        message: `Lỗi proxy / không tải được trang (dòng ${rowNumber}).`,
        tabId,
        pageUrl: currentPageUrl || link,
      };
    }

    await injectOverlayManager(tabId);
    await showOverlay(tabId, statusText);

    let captchaWait = await waitForCaptchaToClear(tabId, { instanceName, rowNumber, sku });
    if (!captchaWait.ok) {
      return {
        ok: false,
        scrapeOk: false,
        captcha: true,
        aborted: Boolean(captchaWait.aborted),
        message: captchaWait.message || `Dang dung vi captcha - ${instanceName}, dong ${rowNumber}.`,
        tabId,
        pageUrl: await getCurrentTabUrl(tabId, currentPageUrl || link),
      };
    }

    let scrapeToken = nextScrapeToken(rowNumber);
    let scrapeWaiterPromise = waitForScrapeResult(scrapeToken);
    // Re-arm CHỈ ở lần chèn đầu của mỗi bước, để ca resume sau captcha (document không reload) vẫn click.
    // Các lần re-inject sau (retry/post-captcha/beforeNext) tự reset guard riêng nên không cần ở đây —
    // tránh gỡ guard vô tội vạ gây click-trùng → reload lặp.
    await rearmScrapeClicker(tabId);
    const injected = await injectScrapeClicker(tabId, scrapeToken);
    if (!injected) {
      captchaWait = await waitForCaptchaToClear(tabId, { instanceName, rowNumber, sku });
      if (captchaWait.ok) {
        scrapeToken = nextScrapeToken(rowNumber);
        scrapeWaiterPromise = waitForScrapeResult(scrapeToken);
        const reinjected = await injectScrapeClicker(tabId, scrapeToken);
        if (reinjected) {
          const retryResult = await scrapeWaiterPromise;
          if (retryResult?.ok) {
            let retryPageUrl = link;
            const retryTab = await getTabSafe(tabId);
            if (retryTab?.url) retryPageUrl = retryTab.url;
            return {
              ok: true,
              scrapeOk: true,
              captcha: Boolean(captchaWait.waited),
              message: retryResult.message || "Da click scrape sau captcha.",
              tabId,
              pageUrl: retryPageUrl,
            };
          }
        }
      }
      const survivingTab = await getTabSafe(tabId);
      return {
        ok: false,
        scrapeOk: false,
        captcha: !captchaWait.ok || Boolean(captchaWait.waited),
        message: "Không inject được scrape clicker (tab có thể đã đóng).",
        tabId: survivingTab?.id ?? tabId,
        pageUrl: link,
      };
    }

    let scrapeResult = await scrapeWaiterPromise;
    if (scrapeResult?.ok) {
      await persistCompletedRow(rowNumber); // Fix #3: ghi tiến độ ngay (bền với reload/SW chết)
      await waitForUrlStable(tabId, 1800);
    }
    let postScrapeCaptcha = await detectCaptcha(tabId);
    if (scrapeResult?.ok && postScrapeCaptcha.detected) {
      captchaWait = await waitForCaptchaToClear(tabId, { instanceName, rowNumber, sku });
      if (!captchaWait.ok) {
        return {
          ok: false,
          scrapeOk: false,
          captcha: true,
          aborted: Boolean(captchaWait.aborted),
          message: captchaWait.message || `Dang dung vi captcha - ${instanceName}, dong ${rowNumber}.`,
          tabId,
          pageUrl: await getCurrentTabUrl(tabId, link),
        };
      }
      scrapeToken = nextScrapeToken(rowNumber);
      scrapeWaiterPromise = waitForScrapeResult(scrapeToken);
      await chrome.scripting.executeScript({
        target: { tabId },
        func: () => { window.__shopee27052026ScrapeClickerInjected = false; },
      }).catch(() => {});
      await injectScrapeClicker(tabId, scrapeToken);
      scrapeResult = await scrapeWaiterPromise;
    }

    // BigSeller báo "Failed, log in BigSeller first" → token tk này đã chết. RETRY/RELOAD là VÔ NGHĨA
    // (mọi dòng sẽ fail y hệt) và chính là thủ phạm vòng "scrape → reload → scrape" ở 1 tk khi 3 tk kia
    // chạy ngon. DỪNG NGAY, báo needLogin để launcher dừng job tk đó + yêu cầu đăng nhập lại — KHÔNG reload.
    if (scrapeResult?.needLogin) {
      return {
        ok: false,
        scrapeOk: false,
        needLogin: true,
        message: scrapeResult.message || "BigSeller chưa đăng nhập — cần đăng nhập lại tài khoản BigSeller.",
        tabId,
        pageUrl: await getCurrentTabUrl(tabId, link),
      };
    }

    for (let retry = 1; retry <= MAX_SCRAPE_RETRIES && !scrapeResult?.ok && !scrapeResult?.needLogin && !abortRequested; retry++) {
      if (!(await getTabSafe(tabId))) {
        tabId = await openLinkInTab(await findReuseableScrapeTab(), link);
        await waitForTabComplete(tabId, TAB_LOAD_TIMEOUT_MS);
        await injectOverlayManager(tabId);
      }
      await showOverlay(tabId, `Thử lại scrape (lần ${retry}) — dòng ${rowNumber}…`);
      await chrome.scripting.executeScript({
        target: { tabId },
        func: () => { window.__shopee27052026ScrapeClickerInjected = false; },
      }).catch(() => {});
      scrapeToken = nextScrapeToken(rowNumber);
      scrapeWaiterPromise = waitForScrapeResult(scrapeToken);
      await injectScrapeClicker(tabId, scrapeToken);
      scrapeResult = await scrapeWaiterPromise;
      if (!scrapeResult?.ok) {
        const retryCaptcha = await waitForCaptchaToClear(tabId, { instanceName, rowNumber, sku });
        if (retryCaptcha.ok && retryCaptcha.waited) {
          scrapeToken = nextScrapeToken(rowNumber);
          scrapeWaiterPromise = waitForScrapeResult(scrapeToken);
          await chrome.scripting.executeScript({
            target: { tabId },
            func: () => { window.__shopee27052026ScrapeClickerInjected = false; },
          }).catch(() => {});
          await injectScrapeClicker(tabId, scrapeToken);
          scrapeResult = await scrapeWaiterPromise;
        } else if (!retryCaptcha.ok) {
          return {
            ok: false,
            scrapeOk: false,
            captcha: true,
            aborted: Boolean(retryCaptcha.aborted),
            message: retryCaptcha.message || `Dang dung vi captcha - ${instanceName}, dong ${rowNumber}.`,
            tabId,
            pageUrl: link,
          };
        }
      }
    }

    if (abortRequested) {
      return { ok: false, scrapeOk: false, message: "Đã hủy.", tabId, pageUrl: link, aborted: true };
    }

    // Fix #2: KHÔNG chờ checkCurrentLinkBeforeNext trong step nữa. Trước đây nó nán tới ~30s SAU click,
    // giữ kết nối SW mong manh mở suốt lúc trang reload (do nút crawl BigSeller làm reload) → dễ rớt SW →
    // launcher relaunch → lặp. Trả kết quả scrape NGAY; phần kiểm tra captcha/ổn-định-URL trước link kế
    // do launcher lo riêng qua CheckBeforeNextLinkAsync (chạy trong lúc nghỉ giữa 2 link).
    if (scrapeResult?.ok) await persistCompletedRow(rowNumber);
    const pageUrl = await getCurrentTabUrl(tabId, link);

    return {
      ok: Boolean(scrapeResult?.ok),
      scrapeOk: Boolean(scrapeResult?.ok),
      captcha: Boolean(captchaWait.waited),
      message: scrapeResult?.message || (scrapeResult?.ok ? "Đã click scrape." : "Không tìm thấy nút scrape."),
      tabId,
      pageUrl,
    };
  } catch (error) {
    return {
      ok: false,
      scrapeOk: false,
      message: error?.message || String(error),
      tabId: null,
      pageUrl: payload?.link || "",
    };
  }
};

globalThis.__launcherCheckBeforeNextLink = async (payload) => {
  const tabId = payload?.tabId ? Number(payload.tabId) : null;
  if (!tabId || !(await getTabSafe(tabId))) {
    return { ok: true, waited: false, tabId: null, pageUrl: "" };
  }

  const context = {
    instanceName: String(payload?.instanceName || "Profile").trim(),
    rowNumber: Number(payload?.rowNumber) || 0,
    sku: String(payload?.sku || "").trim(),
  };
  const result = await checkCurrentLinkBeforeNext(tabId, context);
  return { ...result, tabId, pageUrl: result.pageUrl || await getCurrentTabUrl(tabId, "") };
};

globalThis.__launcherShowOverlay = async ({ tabId, text }) => {
  if (!tabId || !(await getTabSafe(tabId))) return { ok: false };
  try {
    await injectOverlayManager(tabId);
    await showOverlay(tabId, text || "");
    return { ok: true };
  } catch (_) {
    return { ok: false };
  }
};

globalThis.__launcherHideOverlay = async ({ tabId }) => {
  if (!tabId || !(await getTabSafe(tabId))) return { ok: false };
  try {
    await hideOverlay(tabId);
    return { ok: true };
  } catch (_) {
    return { ok: false };
  }
};

globalThis.__launcherAbortStep = async () => {
  abortRequested = true;
  rejectAllScrapeWaiters("Đã hủy từ launcher.");
  return { ok: true };
};

/** Launcher ghi trạng thái hiển thị cho popup (read-only). */
globalThis.__launcherSetDisplayState = async (state) => {
  await chrome.storage.local.set({ runnerState: state || { running: false } });
  syncKeepAlive(state || { running: false });
  await broadcastState();
  return { ok: true };
};

globalThis.__launcherGetRunnerState = async () => {
  const data = await chrome.storage.local.get(["runnerState", "lastRunConfig"]);
  return {
    runnerState: data.runnerState || { running: false },
    lastRunConfig: data.lastRunConfig || {},
  };
};

globalThis.__launcherApplyFormConfig = async (config) => {
  const sheetName = String(config?.sheetName || "").trim();
  const startRow = Number(config?.startRow);
  const endRow = Number(config?.endRow);
  const lastRunConfig = {
    sheetName,
    startRow: Number.isInteger(startRow) && startRow > 0 ? startRow : "",
    endRow: Number.isInteger(endRow) && endRow > 0 ? endRow : "",
  };
  await chrome.storage.local.set({ lastRunConfig });

  const state = await readState();
  const nextSheet = sheetName || state.sheetName;
  const nextStart = lastRunConfig.startRow || state.startRow;
  const nextEnd = lastRunConfig.endRow || state.endRow;

  // ĐỔI KHỐI (bộ sheet/từ dòng/đến dòng khác bộ đang lưu) → tiến độ trong runnerState là RÁC của khối
  // TRƯỚC: profile Brave dùng lại theo tk Shopee nên Local Extension Settings không bị dọn, giữ nguyên
  // lastCompletedRow của lượt cũ (vd 5000 hôm qua) → launcher đọc lên rồi coi khối 2–12 "đã cào xong".
  // TRÙNG cả 3 → GIỮ NGUYÊN: watchdog relaunch giữa chừng chạy lại đúng khối này và resume nhờ chính
  // lastCompletedRow đó (SuggestedResumeRow) — reset vô điều kiện là bắt cào lại từ đầu khối.
  const same = (a, b) => String(a ?? "") === String(b ?? "");
  const sameBlock =
    same(state.sheetName, nextSheet) && same(state.startRow, nextStart) && same(state.endRow, nextEnd);

  const patch = { sheetName: nextSheet, startRow: nextStart, endRow: nextEnd };
  if (!sameBlock) {
    // 2 mốc tiến độ THẬT của runnerState (extension này không lưu stoppedAtRow — C# tự suy ra từ 2 mốc này).
    patch.lastCompletedRow = null;
    patch.currentRow = null;
  }
  await writeState(patch);
  await broadcastState();
  return { ok: true, lastRunConfig, blockChanged: !sameBlock };
};

/** Giữ tương thích cũ — chỉ hủy bước scrape đang chạy. */
globalThis.__launcherStopRun = async () => {
  await globalThis.__launcherAbortStep();
  const state = await readState();
  const next = await writeState({
    running: false,
    phase: "stopped",
    lastMessage: "Đã dừng từ Multi Brave Manager.",
  });
  await broadcastState();
  return {
    ok: true,
    lastCompletedRow: next.lastCompletedRow ?? null,
    currentRow: next.currentRow ?? null,
    sheetName: next.sheetName || next.lastSheetName || "",
    phase: next.phase,
  };
};

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message || message.type !== "LAUNCHER_INVOKE") return undefined;

  const invoke = async () => {
    if (message.method === "probe") {
      return {
        ok: true,
        result: {
          // "sẵn sàng" = có ĐỦ các hàm launcher mới (kể cả applyFormConfig) → SW cũ bị cache sẽ báo chưa sẵn.
          hasScrapeStep:
            typeof globalThis.__launcherExecuteScrapeStep === "function" &&
            typeof globalThis.__launcherApplyFormConfig === "function",
        },
      };
    }

    const handlers = {
      executeScrapeStep: globalThis.__launcherExecuteScrapeStep,
      setDisplayState: globalThis.__launcherSetDisplayState,
      getRunnerState: globalThis.__launcherGetRunnerState,
      applyFormConfig: globalThis.__launcherApplyFormConfig,
      showOverlay: globalThis.__launcherShowOverlay,
      hideOverlay: globalThis.__launcherHideOverlay,
      abortStep: globalThis.__launcherAbortStep,
      stopRun: globalThis.__launcherStopRun,
      notifyRunnerUi: async () => {
        await broadcastState();
        return { ok: true };
      },
    };

    const fn = handlers[message.method];
    if (typeof fn !== "function") {
      return { ok: false, error: `Extension chưa hỗ trợ ${message.method}.` };
    }

    const result = await fn(message.payload);
    return { ok: true, result };
  };

  invoke()
    .then((response) => sendResponse(response))
    .catch((err) => sendResponse({ ok: false, error: String(err?.message || err) }));
  return true;
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message || message.type !== "SCRAPE_RESULT") return undefined;

  // Chỉ resolve waiter có token khớp. Kết quả token lạ (dòng cũ đến muộn sau khi đã re-inject) bị bỏ
  // để không resolve nhầm waiter đang chờ của lượt mới.
  const token = message.token;
  const index = scrapeWaiters.findIndex((w) => w.token === token);
  if (index >= 0) {
    const waiter = scrapeWaiters.splice(index, 1)[0];
    waiter.resolve(message.detail || { ok: false, message: "Không có kết quả scrape." });
  } else {
    console.log("[scrape] SCRAPE_RESULT token lạ, bỏ qua:", token);
  }
  sendResponse({ ok: true });
  return false;
});
