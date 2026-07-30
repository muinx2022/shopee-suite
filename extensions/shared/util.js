// Tiện ích nhỏ dùng chung cho 3 extension.
//
// NGUỒN CHUẨN: sửa Ở ĐÂY rồi chạy `extensions/sync-shared.cmd` (hoặc .sh) để chép sang
// `shopee-*/shared/`. Extension nạp thẳng từ thư mục của nó (--load-extension) nên KHÔNG
// import ngược ra ngoài thư mục extension được — mỗi extension phải giữ một bản copy.
//
// Không đụng chrome.* → dùng được ở mọi ngữ cảnh (service worker, popup…).

/** Chờ ms mili-giây (KHÔNG co giãn). */
export const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** Số thực ngẫu nhiên trong [min, max). */
export const rand = (min, max) => min + Math.random() * (max - min);

/** Số nguyên ngẫu nhiên trong [min, max] (hai đầu đều lấy). */
export const randInt = (min, max) => Math.floor(min + Math.random() * (max - min + 1));

/** Kẹp v vào đoạn [lo, hi]. */
export const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

/**
 * Dựng một hàm sleep có HỆ SỐ NHỊP: mọi quãng chờ HÀNH VI co giãn theo nhịp của phiên
 * (shopee-search dùng để diệt tính bất biến cross-session/account). paceGetter được đọc ở
 * MỖI lần gọi nên đổi nhịp giữa phiên vẫn ăn ngay.
 * KHÔNG dùng cho timeout hệ thống (chờ tab load…) — chỗ đó phải là sleep thường.
 */
export const pacedSleep = (paceGetter) => (ms) =>
  new Promise((resolve) => setTimeout(resolve, Math.round(ms * (paceGetter() || 1))));
