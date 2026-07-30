// Chờ tab (load xong / đổi URL) — dùng chung cho 3 extension.
//
// NGUỒN CHUẨN: sửa Ở ĐÂY rồi chạy `extensions/sync-shared.cmd` (hoặc .sh).
//
// waitForTabComplete lấy mẫu của shopee-scrape: NGHE sự kiện (onUpdated/onRemoved) thay vì poll,
// gỡ listener ở MỌI lối ra, và trả false NGAY khi tab bị đóng thay vì treo tới hết timeout.

import { sleep as defaultSleep } from "./util.js";

/** chrome.tabs.get không ném lỗi — null nếu tab đã biến mất. */
export async function getTabSafe(tabId) {
  if (!tabId) return null;
  try {
    return await chrome.tabs.get(tabId);
  } catch (_) {
    return null;
  }
}

/** Chờ tab load xong. true = đã "complete"; false = hết giờ hoặc tab đã đóng. */
export async function waitForTabComplete(tabId, timeoutMs = 30_000) {
  const tab = await getTabSafe(tabId);
  if (!tab) return false;
  if (tab.status === "complete") return true;

  return new Promise((resolve) => {
    const timeout = setTimeout(() => {
      chrome.tabs.onUpdated.removeListener(onUpdated);
      chrome.tabs.onRemoved.removeListener(onRemoved);
      resolve(false);
    }, timeoutMs);

    const cleanup = () => {
      clearTimeout(timeout);
      chrome.tabs.onUpdated.removeListener(onUpdated);
      chrome.tabs.onRemoved.removeListener(onRemoved);
    };

    const onRemoved = (removedTabId) => {
      if (removedTabId !== tabId) return;
      cleanup();
      resolve(false);
    };

    const onUpdated = (updatedTabId, changeInfo) => {
      if (updatedTabId !== tabId || changeInfo.status !== "complete") return;
      cleanup();
      resolve(true);
    };

    chrome.tabs.onUpdated.addListener(onUpdated);
    chrome.tabs.onRemoved.addListener(onRemoved);
  });
}

/** Chờ URL của tab CHỨA urlPart. true = thấy; false = hết giờ / tab biến mất. */
export async function waitForTabUrl(tabId, urlPart, opts = {}) {
  const { timeoutMs = 10_000, pollMs = 400, sleep = defaultSleep } = opts;
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const tab = await getTabSafe(tabId);
    if (!tab) return false;
    if (tab.url?.includes(urlPart)) return true;
    await sleep(pollMs);
  }
  return false;
}

/** Chờ URL của tab KHÁC previousUrl. true = đã đổi; false = hết giờ / tab biến mất. */
export async function waitForTabUrlChange(tabId, previousUrl, opts = {}) {
  const { timeoutMs = 10_000, pollMs = 350, sleep = defaultSleep } = opts;
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const tab = await getTabSafe(tabId);
    if (!tab) return false;
    if (tab.url && tab.url !== previousUrl) return true;
    await sleep(pollMs);
  }
  return false;
}
