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

// LƯỚI THỨ NĂM (thêm 10/08/2026) — CANH SOCKET "CHẾT CÂM", xem `imLangToiDaMs`.
//  Bốn điểm trên chỉ lo lượt đứt mà trình duyệt CÓ báo (`onclose`). Còn một đường không báo gì: phía C# mất
//  socket (vòng nhận bị huỷ I/O) mà đầu này vẫn thấy `readyState === OPEN`. Khi đó `onclose` KHÔNG bắn ⇒
//  `scheduleReconnect` không chạy, mà `connect()` cũng bị chính guard (2) chặn vì "đã có socket sống". Cầu nối
//  nằm chết cứng tới khi có gì khác đánh thức. Đo được ở vòng 22:08–22:24 ngày 10/08/2026: mỗi cú đứt mất 29
//  giây mới nối lại (đúng nhịp `chrome.alarms` 0.5 phút) dù `reconnectDelayMs` chỉ 1200ms — tức đường nhanh
//  KHÔNG hề chạy, thứ cứu là alarm.
//  BẰNG CHỨNG là zombie chứ không phải "quên hẹn": ở shopee-orders, `content.js` đã gọi `bridge.connect()`
//  đều đặn MỖI 20 GIÂY (nhịp 'wake' + cổng bền "od-keepalive"). Socket mà đóng thật thì chậm nhất 20s là nối
//  lại xong. Mất tới 29s chỉ có một lời giải: guard (2) thấy `readyState === OPEN` nên quay ra ngay — cái nó
//  thấy "còn sống" chính là socket đã chết ở đầu kia.

/**
 * @param {object} options
 * @param {number} options.defaultPort      Cổng mặc định khi chưa biết cổng của lane.
 * @param {number} [options.reconnectDelayMs=3000] Nghỉ bao lâu trước khi nối lại.
 * @param {number} [options.imLangToiDaMs=0] Bao lâu KHÔNG nhận được gói nào thì coi socket đang mở là ĐÃ CHẾT
 *                                          và thay bằng socket mới. `0` = TẮT (mặc định) — chỉ bật cho lane
 *                                          nào phía C# có bắn nhịp đều đặn, không thì mọi kỳ nghỉ bình thường
 *                                          đều bị hiểu nhầm là đứt.
 * @param {(bridge)=>void} [options.onOpen]     Socket mở xong (thường gửi {action:'ready'}; cũng là chỗ xả
 *                                              hàng đợi gói chưa gửi được của lượt đứt trước).
 * @param {(msg, bridge)=>void} [options.onMessage] Nhận 1 gói ĐÃ JSON.parse (gói hỏng bị bỏ im).
 * @param {(bridge)=>void} [options.onClose]    Socket đứt — nơi dọn dẹp phía gọi (vd. reject lệnh treo).
 * @param {(port:number)=>void} [options.onPortChange] Cổng vừa được chốt (nơi lưu lại cho SW khởi động lại).
 */
export function createWsBridge(options) {
  const {
    defaultPort,
    reconnectDelayMs = 3000,
    imLangToiDaMs = 0,
    onOpen,
    onMessage,
    onClose,
    onPortChange,
  } = options || {};

  let ws = null;
  let port = defaultPort;
  let reconnectTimer = null;
  let mocGoiCuoi = 0;      // Date.now() của lần mở / lần nhận gói gần nhất — gốc đo cho lưới canh chết câm.
  let canhTimer = null;

  const isOpen = () => !!ws && ws.readyState === WebSocket.OPEN;

  /**
   * Gửi một gói. Trả về `true` khi gói ĐÃ ĐƯỢC GIAO cho socket, `false` khi socket không mở hoặc `ws.send`
   * ném — nhờ đó phía gọi biết gói nào RỚT mà giữ lại gửi lại (shopee-orders/core.js dùng để xếp hàng đợi
   * câu trả lời). Trước đây hàm này nuốt trắng cả hai đường ⇒ mỗi cú đứt cầu nối là một câu trả lời ĐÃ TÍNH
   * XONG bị vứt lặng lẽ.
   * ⚠ `true` chỉ có nghĩa "đã đưa vào socket", KHÔNG bảo đảm C# nhận được: socket đứt ngay sau đó thì phần
   *   đệm chưa kịp đẩy đi vẫn mất. Muốn chắc chắn tới nơi thì phải có ACK từ C#, hiện chưa có.
   * @returns {boolean}
   */
  function send(obj) {
    try {
      if (!isOpen()) return false;
      ws.send(JSON.stringify(obj));
      return true;
    } catch (_) { return false; }
  }

  function scheduleReconnect() {
    if (reconnectTimer) clearTimeout(reconnectTimer);
    reconnectTimer = setTimeout(() => { reconnectTimer = null; connect(port); }, reconnectDelayMs);
  }

  /// Nhịp soi của lưới canh chết câm: 1/3 ngưỡng (tối thiểu 1s) — đủ dày để phát hiện trong vòng một nhịp,
  /// đủ thưa để không tốn gì. Đặt tên thay vì rắc số trần vào chỗ dùng.
  function nhipCanhMs() { return Math.max(1000, Math.floor(imLangToiDaMs / 3)); }

  function batCanhImLang() {
    if (!imLangToiDaMs || canhTimer) return;
    canhTimer = setInterval(soiImLang, nhipCanhMs());
  }

  /// Socket vẫn khai OPEN mà im quá lâu ⇒ coi như đã chết ở đầu kia (xem khối "LƯỚI THỨ NĂM" đầu file). Thay
  /// nó CÓ CHỦ ĐÍCH theo đúng khuôn điểm (3): gỡ handler trước rồi mới đóng, để cái sắp chết không hẹn thêm
  /// một lượt reconnect nữa chồng lên lượt mình đang gọi.
  function soiImLang() {
    if (!imLangToiDaMs || !ws) return;
    if (ws.readyState !== WebSocket.OPEN) return;          // CONNECTING/CLOSING: onclose lo, đừng chen
    if (Date.now() - mocGoiCuoi <= imLangToiDaMs) return;
    const chet = ws;
    chet.onopen = null;
    chet.onmessage = null;
    chet.onclose = null;
    chet.onerror = null;
    ws = null;
    try { chet.close(); } catch (_) {}
    if (onClose) { try { onClose(bridge); } catch (_) {} }
    connect(port);                                          // nối lại NGAY, không đợi hết reconnectDelayMs
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
    mocGoiCuoi = Date.now();   // gốc đo mới: socket vừa dựng, chưa im giây nào
    batCanhImLang();
    // (4) Mọi handler tự kiểm tra còn là socket hiện hành.
    sock.onopen = () => {
      if (ws !== sock) return;
      mocGoiCuoi = Date.now();
      if (onOpen) { try { onOpen(bridge); } catch (_) {} }
    };
    sock.onmessage = (evt) => {
      if (ws !== sock) return;
      mocGoiCuoi = Date.now();   // đặt TRƯỚC cả lượt parse: gói hỏng vẫn là bằng chứng đường truyền còn sống
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
