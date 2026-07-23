// Content script (ISOLATED, run_at document_start): nhiệm vụ DUY NHẤT là đọc cổng WebSocket mà C# nhúng vào
// hash URL (#_od_ws=<port>) của trang đầu (subaccount.shopee.com ở GĐ2, hoặc /portal/shop ở GĐ1) rồi chuyển
// cho background để nối cầu. Đọc SỚM (document_start) để không rụng cổng khi Shopee redirect. Trang không có
// hash (tab đã redirect / tab shop) → không gửi gì; background giữ cổng đã biết (đã lưu chrome.storage.session).
// Mọi thao tác DOM khác do background làm qua chrome.scripting / chrome.debugger.
(function () {
  try {
    const m = location.hash.match(/_od_ws=(\d+)/);
    if (m) {
      chrome.runtime.sendMessage({ type: "hello", wsPort: parseInt(m[1], 10) });
    }
  } catch (e) {
    /* bỏ qua — không có cổng thì thôi */
  }
})();
