// Nhận diện trang: xác minh (/verify), sản phẩm không tồn tại, lỗi mạng/proxy.
import { ctx } from './core.js';
import { getCurrentTabUrl } from './tabs.js';
import { isVerifyUrl, isNetworkErrorPage as detectNetworkErrorPage } from './shared/net-detect.js';

export async function isVerifyPage() {
  return isVerifyUrl(await getCurrentTabUrl());
}

// Sản phẩm không tồn tại / đã bị xoá: trang Shopee trả 200 nhưng là trang "không tìm thấy".
// Phân biệt với lỗi mạng/proxy (isNetworkErrorPage) và verify (isVerifyPage) để báo TERMINAL
// (bỏ qua link, sang link kế) thay vì retry/đổi account vô hạn trên một link chết.
export async function isProductNotFoundPage() {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: ctx.searchTabId },
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

// Marker list lives in shared/net-detect.js (union of the search + scrape lists).
export async function isNetworkErrorPage() {
  return detectNetworkErrorPage(ctx.searchTabId, { world: 'MAIN' });
}
