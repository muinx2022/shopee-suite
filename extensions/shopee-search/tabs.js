// Quản lý tab của lane search: chọn/dọn tab, đọc URL, và các hàm chờ (bọc shared/tab-wait.js).
import { ctx, sleep } from './core.js';
import { waitForTabComplete, waitForTabUrl, waitForTabUrlChange } from './shared/tab-wait.js';

export async function resolveSearchTab() {
  if (ctx.initialTabId) {
    const tab = await chrome.tabs.get(ctx.initialTabId).catch(() => null);
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

export async function closeApiTabs() {
  const tabs = await chrome.tabs.query({ url: 'https://shopee.vn/api/*' }).catch(() => []);
  for (const tab of tabs) {
    if (tab.id) chrome.tabs.remove(tab.id).catch(() => {});
  }
}

export async function closeOtherTabs(keepTabId) {
  const tabs = await chrome.tabs.query({}).catch(() => []);
  const ids = tabs
    .map(t => t.id)
    .filter(id => id && id !== keepTabId);
  if (ids.length) await chrome.tabs.remove(ids).catch(() => {});
}

export async function getCurrentTabUrl() {
  const tab = await chrome.tabs.get(ctx.searchTabId).catch(() => null);
  return tab?.url || '';
}

// Chờ tab/URL: shared/tab-wait.js — ở đây chỉ bọc lại cho gọn tham số của lane search.
export async function waitForTabLoad(tabId, timeoutMs = 15000) {
  return waitForTabComplete(tabId, timeoutMs);
}

export async function waitForUrlChange(previousUrl, timeoutMs = 10000) {
  return waitForTabUrlChange(ctx.searchTabId, previousUrl, { timeoutMs, pollMs: 350, sleep });
}

export async function waitForUrl(urlPart, timeoutMs = 10000) {
  return waitForTabUrl(ctx.searchTabId, urlPart, { timeoutMs, pollMs: 400, sleep });
}
