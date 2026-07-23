// Content script (ISOLATED, run_at document_start): "đánh thức" service worker của extension + báo cổng
// WebSocket để background nối cầu tới C#. Cổng ưu tiên đọc từ hash #_od_ws=<port> (nếu C# nhúng và còn),
// KHÔNG có/rụng (do Shopee redirect trang đăng nhập) → dùng CỔNG CỐ ĐỊNH mặc định (khớp C# OrdersBridgeSession).
// Vì mỗi trang khớp (subaccount / accounts / banhang) đều gửi 'wake' → SW luôn được đánh thức để nối lại, không
// còn phụ thuộc hash sống sót. Thao tác DOM khác do background làm qua chrome.scripting / chrome.debugger.
(function () {
  const DEFAULT_PORT = 47821; // PHẢI khớp OrdersBridgeSession.BridgePort phía C#.
  let port = DEFAULT_PORT;
  try {
    const m = (location.hash || "").match(/_od_ws=(\d+)/);
    if (m) port = parseInt(m[1], 10);
  } catch (e) { /* bỏ qua */ }

  try { chrome.runtime.sendMessage({ type: "wake", wsPort: port, href: location.href }); } catch (e) {}

  // Badge chẩn đoán (tạm thời trong lúc kiểm chứng cầu nối) — xác nhận content.js CHẠY + host + cổng đang dùng.
  try {
    const draw = () => {
      if (!document.body || document.getElementById("__od_badge")) return;
      const b = document.createElement("div");
      b.id = "__od_badge";
      b.textContent = "OD bridge · port=" + port + " · " + location.host;
      b.style.cssText =
        "position:fixed;left:8px;bottom:8px;z-index:2147483647;background:#ee4d2d;color:#fff;" +
        "font:12px/1.4 Arial,sans-serif;padding:4px 9px;border-radius:6px;opacity:.9;pointer-events:none";
      document.body.appendChild(b);
    };
    draw();
    new MutationObserver(draw).observe(document.documentElement, { childList: true, subtree: true });
  } catch (e) { /* bỏ qua */ }
})();
