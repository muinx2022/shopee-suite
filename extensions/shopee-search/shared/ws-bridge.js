// Cầu WebSocket extension ↔ app C# (kênh lệnh/dữ liệu của shopee-search và shopee-orders).
//
// NGUỒN CHUẨN: sửa Ở ĐÂY rồi chạy `extensions/sync-shared.cmd` (hoặc .sh).
//
// Mẫu reconnect lấy từ shopee-search (bản ĐÃ FIX) — 4 điểm bắt buộc giữ:
//  1. Vào connect() là HUỶ timer reconnect đang treo, kẻo timer cũ bắn muộn và thay mất socket vừa mở.
//  2. Guard socket-sống CÙNG CỔNG → không mở socket thứ hai. Không có guard này thì một lần reload tab
//     (onUpdated) hay một lượt reconnect muộn sẽ dựng socket thứ hai, onopen gửi lại 'ready' → C# gửi
//     lại 'start' → việc ĐANG chạy bị dừng rồi chạy lại từ đầu.
//  3. Đổi cổng thì THAY socket có chủ đích: gỡ onclose/onerror của socket cũ TRƯỚC khi close, để nó
//     không hẹn thêm một lượt reconnect nữa.
//  4. Mọi handler kiểm tra "còn là socket hiện hành" (ws !== sock → thôi) — socket cũ đến muộn không
//     được phép động vào trạng thái của socket mới.

/**
 * @param {object} options
 * @param {number} options.defaultPort      Cổng mặc định khi chưa biết cổng của lane.
 * @param {number} [options.reconnectDelayMs=3000] Nghỉ bao lâu trước khi nối lại.
 * @param {(bridge)=>void} [options.onOpen]     Socket mở xong (thường gửi {action:'ready'}).
 * @param {(msg, bridge)=>void} [options.onMessage] Nhận 1 gói ĐÃ JSON.parse (gói hỏng bị bỏ im).
 * @param {(bridge)=>void} [options.onClose]    Socket đứt — nơi dọn dẹp phía gọi (vd. reject lệnh treo).
 * @param {(port:number)=>void} [options.onPortChange] Cổng vừa được chốt (nơi lưu lại cho SW khởi động lại).
 */
export function createWsBridge(options) {
  const {
    defaultPort,
    reconnectDelayMs = 3000,
    onOpen,
    onMessage,
    onClose,
    onPortChange,
  } = options || {};

  let ws = null;
  let port = defaultPort;
  let reconnectTimer = null;

  const isOpen = () => !!ws && ws.readyState === WebSocket.OPEN;

  function send(obj) {
    try {
      if (isOpen()) ws.send(JSON.stringify(obj));
    } catch (_) {}
  }

  function scheduleReconnect() {
    if (reconnectTimer) clearTimeout(reconnectTimer);
    reconnectTimer = setTimeout(() => { reconnectTimer = null; connect(port); }, reconnectDelayMs);
  }

  function connect(nextPort) {
    const targetPort = nextPort || port || defaultPort;
    // (1) Đang nối lại NGAY BÂY GIỜ → huỷ timer đang treo.
    if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
    // (2) Đã có socket sống tới ĐÚNG cổng đó → khỏi làm gì. Cổng khác thì lọt guard này để thay socket.
    if (ws && targetPort === port &&
        (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
    port = targetPort;
    if (onPortChange) { try { onPortChange(port); } catch (_) {} }
    if (ws) {
      // (3) Thay có chủ đích (đổi cổng): gỡ handler của socket cũ trước khi đóng.
      const old = ws;
      old.onclose = null;
      old.onerror = null;
      try { old.close(); } catch (_) {}
      ws = null;
    }
    let sock;
    try {
      sock = new WebSocket(`ws://localhost:${port}`);
    } catch (_) {
      scheduleReconnect();
      return;
    }
    ws = sock;
    // (4) Mọi handler tự kiểm tra còn là socket hiện hành.
    sock.onopen = () => {
      if (ws !== sock) return;
      if (onOpen) { try { onOpen(bridge); } catch (_) {} }
    };
    sock.onmessage = (evt) => {
      if (ws !== sock) return;
      let msg;
      try { msg = JSON.parse(evt.data); } catch (_) { return; }   // gói hỏng → bỏ im
      if (onMessage) { try { onMessage(msg, bridge); } catch (_) {} }
    };
    sock.onclose = () => {
      if (ws !== sock) return;
      ws = null;
      if (onClose) { try { onClose(bridge); } catch (_) {} }
      scheduleReconnect();
    };
    sock.onerror = () => {};   // onclose lo dọn + hẹn lại
  }

  const bridge = {
    connect,
    send,
    isOpen,
    get port() { return port; },
  };
  return bridge;
}
