// Content script (ISOLATED, run_at document_start): "đánh thức" service worker của extension + báo cổng
// WebSocket để background nối cầu tới C#. Cổng ưu tiên đọc từ hash #_od_ws=<port> (nếu C# nhúng và còn),
// KHÔNG có/rụng (do Shopee redirect trang đăng nhập) → dùng CỔNG CỐ ĐỊNH mặc định (khớp C# OrdersBridgeSession).
// Vì mỗi trang khớp (subaccount / accounts / banhang) đều gửi 'wake' → SW luôn được đánh thức để nối lại, không
// còn phụ thuộc hash sống sót. Thao tác DOM khác do background làm qua chrome.scripting / chrome.debugger.
//
// NHỊP ĐÁNH THỨC 20s (thêm 10/08/2026) — đây là dây cứu sinh CHÍNH của cầu nối, không phải tối ưu vặt:
// giữa hai shop vòng chạy NGHỈ 3–5 phút, không trang nào load, service worker MV3 bị giết sau ~30s im lặng.
// Hai đường đã thử và ĐỀU KHÔNG cứu được (có bằng chứng nhật ký):
//   · gói ping 20s từ C# — vòng 06:50 vẫn chết (traffic WebSocket không gia hạn tuổi thọ SW);
//   · chrome.alarms 30s trong background.js — vòng 09:11 chết ở shop 5, vòng 09:44 chết ở shop 6, cả hai lần
//     SW không dậy nổi trong 90s C# chờ.
// Message từ content script thì KHÁC HẲN: nó vừa GIA HẠN đồng hồ idle của SW đang sống, vừa DỰNG DẬY SW đã
// chết — và tab picker /portal/shop vẫn mở suốt kỳ nghỉ nên luôn có người bắn nhịp. 20s < 30s cho dư biên.
// Tab bị vứt khỏi bộ nhớ thì interval chết theo; đó là lý do vẫn GIỮ chrome.alarms làm lưới thứ hai.
(function () {
  const DEFAULT_PORT = 47821; // PHẢI khớp OrdersBridgeSession.BridgePort phía C#.
  const NHIP_DANH_THUC_MS = 20000;
  let port = DEFAULT_PORT;
  try {
    const m = (location.hash || "").match(/_od_ws=(\d+)/);
    if (m) port = parseInt(m[1], 10);
  } catch (e) { /* bỏ qua */ }

  // dauTien: CHỈ lần đầu của mỗi lượt load trang mới được nhận làm tab picker (xem background.js). Nhịp lặp
  // là keepalive thuần — để nó cũng giành quyền đặt listTabId thì tab shop có thể cướp vai picker giữa chừng.
  function danhThuc(dauTien) {
    try {
      const p = chrome.runtime.sendMessage({ type: "wake", wsPort: port, href: location.href, dauTien: !!dauTien });
      // MV3 trả Promise; SW đang dựng dậy có thể reject ("Receiving end does not exist") — nuốt, nhịp sau lo.
      if (p && typeof p.catch === "function") { p.catch(() => {}); }
    } catch (e) { /* extension vừa reload → context cũ chết, bỏ qua */ }
  }

  danhThuc(true);
  setInterval(() => danhThuc(false), NHIP_DANH_THUC_MS);

  // CỔNG BỀN (chrome.runtime.connect) — lưới mạnh hơn nhịp sendMessage ở trên, thêm cùng ngày sau khi vòng
  // 10:24 vẫn chết ở shop 6: cổng đang mở thì trình duyệt GIỮ service worker sống, và quan trọng hơn — lúc SW
  // bị giết thì `onDisconnect` bắn NGAY tại content script, nối lại tức thì dựng SW dậy trong khoảng một giây,
  // thay vì ngồi đợi hết nhịp 20s. Cổng chết vì extension reload thì thôi (context cũ đã hỏng, không nối nữa).
  const NHIP_CONG_MS = 20000;
  let cong = null;
  let hetCua = false;
  function moCong() {
    if (hetCua) { return; }
    try {
      cong = chrome.runtime.connect({ name: "od-keepalive" });
      cong.onDisconnect.addListener(() => {
        cong = null;
        // chrome.runtime biến mất = extension đã reload/gỡ → dừng hẳn, đừng quay vòng vô ích.
        if (!chrome.runtime || !chrome.runtime.id) { hetCua = true; return; }
        setTimeout(moCong, 500);
      });
      try { cong.postMessage({ type: "keepalive", wsPort: port }); } catch (e) {}
    } catch (e) { hetCua = !chrome.runtime || !chrome.runtime.id; cong = null; }
  }
  moCong();
  setInterval(() => {
    if (!cong) { moCong(); return; }
    try { cong.postMessage({ type: "keepalive", wsPort: port }); }
    catch (e) { cong = null; moCong(); }
  }, NHIP_CONG_MS);
})();
