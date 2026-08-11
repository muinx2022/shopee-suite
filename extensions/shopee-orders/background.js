// Service worker — CẦU NỐI extension↔C# cho module Đơn hàng (Seller Centre slice).
//
// PIVOT GĐ2: ĐĂNG NHẬP subaccount + SSO nay do C# làm bằng Playwright/CDP (subaccount + /portal/shop KHÔNG bị
// captcha nên an toàn). Extension chỉ còn lo phần Seller Centre né captcha: đọc danh sách shop → mở "Chi tiết"
// bằng TRUSTED CLICK (chrome.debugger) → đọc "Chờ Lấy Hàng". Trình duyệt sạch mở thẳng /portal/shop (đã đăng
// nhập nhờ hồ sơ). KHÔNG mở --remote-debugging-port.
//
// Kênh lệnh/dữ liệu: WebSocket tới ws://localhost:<port> (C# chạy OrdersWebSocketServer). Cổng: hash #_od_ws=<port>
// nếu còn, không thì DEFAULT_PORT cố định; content.js gửi 'wake' đánh thức SW + nối cầu.
//
// Protocol:
//   C#  -> ext:  {action:"readShopList"} | {action:"openShopDetail", shopId} | {action:"readToShip"}
//                {action:"redownloadSlip", orderSn}                               (tải lại phiếu 1 đơn)
//                {action:"readReturnRequests"}                                    (bước CUỐI: trang trả hàng, TRANG ĐẦU)
//                {action:"readReturnRequestsMore", maxPages}                       (lật thêm trang trên trang đang mở)
//   ext -> C#:   {action:"ready"}
//                {action:"shopOpened"}                                            (openShopDetail xong)
//                {action:"pageData", kind:"shopList", data:<json-array-string>}
//                {action:"pageData", kind:"toShip",   data:<raw-item-title-string>}
//                {action:"pageData", kind:"returns",  data:<json {soYeuCauText,sortApplied,tabTraHang,list}>}
//                {action:"slipRedownloaded", orderSn, slipBase64}                 (base64 "" = không lấy được)
//                {action:"progress", message}                                     (chỉ để log)
//                {action:"captcha",  message}                                     (rơi vào trang /verify)
//                {action:"error",    message}
//
// Sau đợt tách 2026-08-06 file này CHỈ còn wiring: import module + đăng ký listener + dispatch handleCommand.
//   core.js               state chung (ctx) + cầu WS + ensureListTab/orderTabIdStrict
//   constants.js          URL Seller Centre / trần quét / regex nhãn
//   exec.js               execInTab + pageInstallHelpers (bơm hàm vào tab, world MAIN)
//   page-funcs.js         hàm page* thuần DOM (picker shop, đơn, modal, phiếu, địa chỉ) + _na/_provCore
//   page-funcs-returns.js hàm page* của trang Trả hàng/Hoàn tiền/Hủy
//   flow-shop.js          gotoSellerCentre / readShopList / openShopDetail / readToShip / closeShopTab
//   flow-orders.js        syncOrders / syncOrderFinals / prepareNextOrder / redownloadSlip
//   flow-address.js       setPickupAddress / setPickupAddressToOther
//   flow-returns.js       readReturnRequests

import { bridge, ctx, setCommandHandler } from "./core.js";
import { gotoSellerCentre, doReadShopList, openShopDetail, doReadToShip, doCloseShopTab } from "./flow-shop.js";
import { doSyncOrders, doSyncOrderFinals, doPrepareNextOrder, doRedownloadSlip } from "./flow-orders.js";
import { doSetPickupAddress, doSetPickupAddressToOther } from "./flow-address.js";
import { doReadReturnRequests, doReadReturnRequestsMore } from "./flow-returns.js";

// ---- Xử lý lệnh từ C# -----------------------------------------------------

async function handleCommand(cmd) {
  if (!cmd || !cmd.action) return;

  switch (cmd.action) {
    case "gotoSellerCentre": await gotoSellerCentre(); return;
    case "readShopList":     await doReadShopList(); return;
    case "openShopDetail":   await openShopDetail(String(cmd.shopId || "").replace(/'/g, "")); return;
    case "readToShip":       await doReadToShip(); return;
    case "syncOrders":       await doSyncOrders(); return;
    case "syncOrderFinals":  await doSyncOrderFinals(cmd.orders || []); return;
    case "setPickupAddress": await doSetPickupAddress(String(cmd.province || "")); return;
    case "setPickupAddressToOther": await doSetPickupAddressToOther(); return;
    case "prepareNextOrder": await doPrepareNextOrder(); return;
    case "redownloadSlip":   await doRedownloadSlip(String(cmd.orderSn || "")); return;
    case "readReturnRequests": await doReadReturnRequests(); return;
    case "readReturnRequestsMore": await doReadReturnRequestsMore(Number(cmd.maxPages) || 0); return;
    case "closeShopTab":     await doCloseShopTab(); return;
  }
}

// Nối handler TRƯỚC khi mở cầu WS (core gọi ngược qua đây thay vì import module flow).
setCommandHandler(handleCommand);

// content.js gửi 'wake' (mọi trang khớp) → đánh thức SW + nối cầu. Không còn phụ thuộc hash sống sót:
// mất hash thì content.js gửi DEFAULT_PORT. listTabId gán tab đầu tiên khớp; ensureListTab(BANHANG_HOSTS) tự
// phân giải lại tab /portal/shop khi chạy lát cắt (login đã do C#/Playwright lo, extension chỉ ở Seller Centre).
// ⚠ content.js gửi 'wake' theo NHỊP 20s (keepalive giữ SW sống qua kỳ nghỉ 3–5' giữa hai shop), nên handler này
// chạy liên tục chứ không chỉ lúc load trang. Vì vậy CHỈ gói `dauTien` (lượt đầu của mỗi lần load trang) mới
// được nhận vai listTabId — để nhịp lặp cũng giành thì tab shop có thể cướp vai tab picker giữa chừng, và mọi
// lệnh cần picker sau đó bắn nhầm tab. Gói không có `dauTien` (bản content.js cũ) vẫn được nhận như trước.
chrome.runtime.onMessage.addListener((msg, sender) => {
  if (msg && msg.type === "wake") {
    const nhanTab = msg.dauTien === undefined || msg.dauTien === true;
    if (nhanTab && ctx.listTabId == null && sender.tab && sender.tab.id != null) ctx.listTabId = sender.tab.id;
    bridge.connect(msg.wsPort);
    try { chrome.storage.session.set({ wsPort: bridge.port, listTabId: ctx.listTabId }); } catch (e) {}
  }
});

// CỔNG BỀN từ content.js ("od-keepalive"): trình duyệt giữ service worker sống chừng nào còn cổng mở, và mỗi
// gói trên cổng lại gia hạn đồng hồ idle. Đây là lưới CHÍNH chống "SW ngủ trong kỳ nghỉ 3–5' giữa hai shop";
// nhịp sendMessage 20s và chrome.alarms là hai lưới sau. Mỗi gói cũng nối lại cầu WS luôn (connect bỏ qua khi
// socket còn sống) — SW vừa được dựng dậy thì đây là đường nối lại nhanh nhất.
try {
  chrome.runtime.onConnect.addListener((port) => {
    if (!port || port.name !== "od-keepalive") { return; }
    port.onMessage.addListener((m) => { bridge.connect(m && m.wsPort ? m.wsPort : undefined); });
    port.onDisconnect.addListener(() => { /* tab đóng/điều hướng — content script bên kia tự mở lại */ });
    bridge.connect();
  });
} catch (e) { /* không dựng được cổng bền → vẫn còn nhịp sendMessage + alarms */ }

// Khôi phục ctx.shopTabId sau khi service worker bị giết — nhưng CHỈ khi tab đó còn sống VÀ còn đứng ở Seller
// Centre. Không validate thì một id cũ (tab đã đóng, hoặc trình duyệt cấp lại id đó cho tab khác) sẽ dẫn các
// lệnh cấp đơn — gồm cả thao tác THẬT như chuẩn bị hàng / đổi địa chỉ lấy hàng — vào một tab lạ. Không hợp lệ
// thì để null: `orderTabIdStrict` sẽ bỏ lượt có báo lỗi, C# cho shop chạy lại từ `openShopDetail`.
//
// Hàm này chạy ASYNC từ storage (lúc SW dựng dậy + mỗi nhịp alarm 30s) nên có thể hoàn tất SAU một ghi chủ
// động vừa chen vào quanh `await chrome.tabs.get`: `doCloseShopTab` vừa ghi null (hoàn tất muộn là HỒI SINH
// id đã đóng) hoặc `openShopDetail` vừa ghi id mới (validate id CŨ chết xong lại ĐÈ null lên id mới). Chốt
// bằng THẾ HỆ: `nhoTabShop` tăng `ctx.theHeTabShop` mỗi lần ghi chủ động — sau `await` mà thế hệ đã đổi thì
// bỏ kết quả, bên ghi chủ động thắng.
async function khoiPhucTabShop(id) {
  if (ctx.shopTabId != null) return; // SW đang sống + đã có ngữ cảnh — ctx là nguồn chân lý, không đè từ storage
  const theHe = ctx.theHeTabShop;
  let hopLe = false;
  try {
    const t = await chrome.tabs.get(id);
    hopLe = !!(t && (t.url || t.pendingUrl || "").indexOf("banhang.shopee.vn") >= 0);
  } catch (e) {}
  if (theHe !== ctx.theHeTabShop) return;
  if (hopLe) { ctx.shopTabId = id; return; }
  ctx.shopTabId = null;
  try { chrome.storage.session.set({ shopTabId: null }); } catch (e) {}
}

// Service worker khởi động lại (MV3 có thể ngủ) → nạp cổng đã lưu (hoặc mặc định) rồi nối lại.
// ⚠ Hàm này còn chạy MỖI NHỊP alarm 30s (xem dưới) chứ không riêng lúc SW dựng dậy, nên với cả hai tab id đều
// theo một kỷ luật: ctx đang có giá trị ⇒ ctx là nguồn chân lý, KHÔNG đè từ storage. Với listTabId: storage chỉ
// được đồng bộ theo gói wake 20s, còn `ensureListTab`/`gotoSellerCentre` đổi ctx.listTabId mà không ghi storage
// — thiếu chốt `== null` thì nhịp alarm kéo listTabId LÙI về tab cũ đã chết, và `doCloseShopTab` dùng thẳng id
// đó: đốt 20s chờ tab ma rồi báo "picker không sẵn sàng" oan (đúng chốt của gói wake `dauTien`, background.js
// phần trên; phản biện 11/08/2026).
function noiLaiTuStorage() {
  try {
    chrome.storage.session.get(["wsPort", "listTabId", "shopTabId"], (v) => {
      if (v && v.listTabId != null && ctx.listTabId == null) ctx.listTabId = v.listTabId;
      if (v && v.shopTabId != null) { khoiPhucTabShop(v.shopTabId).catch(() => {}); }
      bridge.connect(v && v.wsPort ? v.wsPort : undefined);
    });
  } catch (e) { bridge.connect(); }
}
noiLaiTuStorage();

// HỒI SINH service worker bằng chrome.alarms — KHÔNG phải "giữ" cho nó sống, mà DỰNG NÓ DẬY sau khi bị giết.
// Vì sao cần (ca thật 10/08/2026): giữa hai shop vòng chạy NGHỈ 3–4 phút. Trình duyệt giết service worker MV3
// sau ~30s không hoạt động; chết rồi thì `scheduleReconnect` của ws-bridge chết theo, và KHÔNG có sự kiện nào
// đánh thức (content.js chỉ gửi 'wake' lúc trang load, mà lúc nghỉ không trang nào load). Kết quả: lệnh đầu
// tiên của shop kế ném "Cầu nối extension chưa kết nối".
// Gói ping 20s mà C# bắn xuống KHÔNG cứu được ca này — đã thử ở vòng 06:50 và vẫn chết, nên đừng tin vào nó.
// chrome.alarms sống ĐỘC LẬP với service worker: tới hẹn thì trình duyệt dựng service worker dậy để chạy
// handler, và chính lúc dựng dậy thì đoạn top-level trên đã nối lại cầu rồi. Handler chỉ cần gọi thêm một lần
// nữa cho chắc — `bridge.connect` tự bỏ qua khi socket còn sống nên gọi thừa vô hại.
// 0.5 phút: extension nạp bằng --load-extension (unpacked) nên được phép dưới 1 phút; bị trình duyệt kẹp lên
// 1 phút cũng vẫn đủ, vì phía C# chờ nối lại tới CauNoiLai trước khi báo lỗi.
const ALARM_HOI_SINH = "hoi-sinh-cau-noi";
try {
  chrome.alarms.create(ALARM_HOI_SINH, { periodInMinutes: 0.5 });
  chrome.alarms.onAlarm.addListener((a) => {
    if (a && a.name === ALARM_HOI_SINH) { noiLaiTuStorage(); }
  });
} catch (e) { /* không có quyền alarms → mất đường hồi sinh, cầu nối vẫn chạy như cũ */ }
