// Nhận diện trang xác minh (/verify) và trang lỗi mạng/proxy — dùng chung cho shopee-search + shopee-scrape.
//
// NGUỒN CHUẨN: sửa Ở ĐÂY rồi chạy `extensions/sync-shared.cmd` (hoặc .sh).

/** Đường dẫn trang xác minh của Shopee: "/verify" hoặc "/verify/...". */
export const VERIFY_PATH_RE = /^\/verify(?:\/|$)/i;

/**
 * URL có phải trang xác minh không — xét trên PATHNAME (mẫu của shopee-scrape) để không dính
 * nhầm các URL chỉ chứa chữ "verify" ở query (vd. .../buyer/login?next=%2Fverify...).
 */
export function isVerifyUrl(url) {
  if (!url || typeof url !== "string") return false;
  try {
    return VERIFY_PATH_RE.test(new URL(url).pathname);
  } catch (_) {
    return /\/verify(?:\/|$)/i.test(url);   // URL không parse được → xét thô trên chuỗi
  }
}

// Dấu hiệu trang lỗi mạng/proxy. GỘP hai danh sách cũ (search + scrape) vì chúng bổ khuyết nhau;
// so trên innerText VÀ title, tất cả đã lowercase. Nguồn từng dấu hiệu:
//
//   dấu hiệu                                | nguồn  | ghi chú
//   ----------------------------------------|--------|------------------------------------------------
//   site can                                | search | "This site can't be reached" (search xét ở title)
//   err_timed_out                           | search |
//   err_proxy                               | search | bao luôn ERR_PROXY_CONNECTION_FAILED (cả 2 bản)
//                                           |        | và ERR_PROXY_AUTH_UNSUPPORTED (scrape)
//   err_tunnel_connection_failed             | scrape |
//   err_socks_connection_failed              | scrape |
//   no internet                             | cả hai | scrape so title === "No internet"
//   something wrong with the proxy server   | search |
//   checking the proxy                      | search | bao luôn "checking the proxy address"
//   took too long to respond                | search |
export const NETWORK_ERROR_MARKERS = [
  "site can",
  "err_timed_out",
  "err_proxy",
  "err_tunnel_connection_failed",
  "err_socks_connection_failed",
  "no internet",
  "something wrong with the proxy server",
  "checking the proxy",
  "took too long to respond",
];

/**
 * Tab đang đứng ở trang lỗi mạng/proxy?
 * @param {number} tabId
 * @param {object} [opts]
 * @param {string[]} [opts.markers=NETWORK_ERROR_MARKERS]
 * @param {'MAIN'|'ISOLATED'} [opts.world='MAIN'] World để chạy hàm dò (giữ đúng world bản gốc từng extension).
 * @param {boolean} [opts.onInjectError=false] Trả gì khi KHÔNG inject được (trang lỗi hay chặn inject).
 */
export async function isNetworkErrorPage(tabId, opts = {}) {
  const { markers = NETWORK_ERROR_MARKERS, world = "MAIN", onInjectError = false } = opts;
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId },
      world,
      args: [markers],
      // Hàm này được serialize sang trang → PHẢI tự chứa, danh sách dấu hiệu truyền qua args.
      func: (list) => {
        const body = (document.body?.innerText || "").toLowerCase();
        const title = (document.title || "").toLowerCase();
        return list.some((m) => body.includes(m) || title.includes(m));
      },
    });
    return res?.result === true;
  } catch (_) {
    return onInjectError;
  }
}
