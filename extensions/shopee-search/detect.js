// Nhận diện trang: xác minh (/verify), lỗi mạng/proxy.
import { ctx } from './core.js';
import { getCurrentTabUrl } from './tabs.js';
import { isVerifyUrl, isNetworkErrorPage as detectNetworkErrorPage } from './shared/net-detect.js';

export async function isVerifyPage() {
  return isVerifyUrl(await getCurrentTabUrl());
}

// Marker list lives in shared/net-detect.js (union of the search + scrape lists).
export async function isNetworkErrorPage() {
  return detectNetworkErrorPage(ctx.searchTabId, { world: 'MAIN' });
}
