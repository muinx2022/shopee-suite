// Content script (ISOLATED): nhiệm vụ DUY NHẤT ở GĐ1 là đọc cổng WebSocket mà C# nhúng vào hash URL
// (#_od_ws=<port>) của trang /portal/shop lần đầu, rồi chuyển cho background để nối cầu. Mọi thao tác DOM
// khác (đọc bảng shop, đọc to-do box, bắn trusted click) do background làm qua chrome.scripting/chrome.debugger.
// Trang không có hash (vd tab shop chi tiết) → không gửi gì (background giữ cổng đã biết).
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
