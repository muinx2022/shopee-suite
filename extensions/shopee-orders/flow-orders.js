// Lệnh cấp ĐƠN trên tab shop: quét đơn (phân trang) → "Số tiền cuối cùng" + sản phẩm → Chuẩn bị hàng +
// in phiếu → tải lại phiếu một đơn. Thân hàm GIỮ NGUYÊN từ background.js (tách 2026-08-06).
import { send, orderTabIdStrict, LOI_MAT_TAB_SHOP } from "./core.js";
import { execInTab } from "./exec.js";
import { ORDERS_URL, ORDER_DETAIL_PREFIX, MAX_ORDER_PAGES, MAX_ORDER_PRODUCTS } from "./constants.js";
import {
  pageOrderCount, pageListSignature, pageLocateByText, pageScanOrders,
  pageReadFinalAmount, pageChanDoanUocTinh, pageReadOrderProducts, pageFindPrepareOrder,
  pageModalHasTitle, pageAnyModalVisible, pageDumpClickables, pageLocateInModal,
  pageReadModalTracking, pagePrintButton, pageFetchSlipBase64, pageFindPrintInCardBySn,
} from "./page-funcs.js";
import { timNutTrangSau, trangThaiTruocBamLai, SO_LAN_BAM_LAI_PAGER } from "./pager.js";
import { sleep } from "./shared/util.js";
import { waitForTabComplete } from "./shared/tab-wait.js";
import { ensureDbg, trustedClick } from "./shared/dbg-input.js";

// ===== GĐ3 — lệnh đọc + xử đơn (chạy trên tab shop = shopTabId) =====

// Chờ số card đơn ỔN ĐỊNH (đứng yên 2 lượt nếu >0, hoặc 3 lượt nếu 0 = shop rỗng) hoặc hết giờ.
async function waitOrdersStable(tabId, timeoutMs) {
  const dl = Date.now() + timeoutMs;
  let last = -1, stable = 0;
  while (Date.now() < dl) {
    let c = 0;
    try { c = (await execInTab(tabId, pageOrderCount, [])) || 0; } catch (e) { c = 0; }
    if (c === last) { stable++; if (stable >= 2 && c > 0) return; if (stable >= 3) return; }
    else { stable = 0; last = c; }
    await sleep(500);
  }
}

// Chờ danh sách ĐỔI so với ký hiệu 'before' (port WaitListChangedAsync): bỏ trạng thái đang tải ("0|...").
async function waitOrdersChanged(tabId, before, timeoutMs) {
  const dl = Date.now() + timeoutMs;
  while (Date.now() < dl) {
    await sleep(300);
    let now = "";
    try { now = (await execInTab(tabId, pageListSignature, [])) || ""; } catch (e) {}
    if (now.indexOf("0|") === 0) continue;
    if (now && now !== before) return true;
  }
  return false;
}

// Phần A: đọc đơn tab "Tất cả" (phân trang). Trả {action:"pageData", kind:"orders", data:<json gộp>}.
export async function doSyncOrders() {
  const tabId = orderTabIdStrict();
  if (tabId == null) { send({ action: "error", message: "đọc đơn: " + LOI_MAT_TAB_SHOP }); return; }

  try { await chrome.tabs.update(tabId, { url: ORDERS_URL }); } catch (e) {}
  await waitForTabComplete(tabId, 20000);
  let url = "";
  try { url = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
  if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }

  await waitOrdersStable(tabId, 15000);

  // Sang tab "Tất cả" (best-effort: không thấy thì quét tab hiện tại).
  const allTab = await execInTab(tabId, pageLocateByText, [["[role='tab']", ".eds-tabs__nav-tab", "a", "div", "span"], "^tat ca$"]);
  if (allTab) { await trustedClick(tabId, allTab.x, allTab.y); await sleep(1200); await waitOrdersStable(tabId, 10000); }

  const all = [];
  const seen = {};
  let pageNo = 0;
  while (pageNo < MAX_ORDER_PAGES) {
    pageNo++;
    await waitOrdersStable(tabId, 15000);
    let json = "[]";
    try { json = (await execInTab(tabId, pageScanOrders, [])) || "[]"; } catch (e) { json = "[]"; }
    let arr = [];
    try { arr = JSON.parse(json) || []; } catch (e) { arr = []; }
    for (const o of arr) {
      const sn = o && o.orderSn ? o.orderSn : "";
      if (sn && !seen[sn]) { seen[sn] = 1; all.push(o); }
    }

    let sigBefore = "";
    try { sigBefore = (await execInTab(tabId, pageListSignature, [])) || ""; } catch (e) {}
    const next = await timNutTrangSau(tabId);
    if (!next) break;
    if (pageNo >= MAX_ORDER_PAGES) break;
    await trustedClick(tabId, next.x, next.y);
    let changed = await waitOrdersChanged(tabId, sigBefore, 10000);
    // Trượt thì thử LẠI đúng một lượt (đo lại toạ độ từ đầu — layout có thể vừa nhảy). Vẫn trượt ⇒ BÁO RA rồi
    // mới dừng: lật trang là đường đi THƯỜNG ở đây (~94 đơn/shop), dừng câm là mất đơn không ai hay.
    // ⚠ TRƯỚC cú bấm lại PHẢI hỏi chữ ký (trangThaiTruocBamLai): fetch chậm >10s làm cú bấm ĐÃ ăn trông y hệt
    // bấm trượt — bấm nữa là nhảy 2 trang, trang ở giữa không được quét (mất đơn im lặng).
    for (let lai = 0; !changed && lai < SO_LAN_BAM_LAI_PAGER; lai++) {
      const tt = await trangThaiTruocBamLai(tabId, pageListSignature, sigBefore);
      if (tt === "doi") { changed = true; break; }                                // cú bấm đầu đã ăn, chỉ là đổi muộn
      if (tt === "dangTai") { changed = await waitOrdersChanged(tabId, sigBefore, 10000); break; } // đang vẽ → chờ, KHÔNG bấm
      const lanHai = await timNutTrangSau(tabId);
      if (!lanHai) break;
      await trustedClick(tabId, lanHai.x, lanHai.y);
      changed = await waitOrdersChanged(tabId, sigBefore, 10000);
    }
    if (!changed) {
      send({
        action: "progress",
        message: "lật sang trang " + (pageNo + 1) + " trượt — dừng ở " + pageNo + " trang, đọc THIẾU đơn.",
      });
      break;
    }
    await sleep(1000);
  }

  send({ action: "pageData", kind: "orders", data: JSON.stringify(all) });
}

// Lấy "Số tiền cuối cùng" (cột Ước tính) cho các đơn C# rót sang (port FetchFinalAmountsForPageAsync của Playwright):
// với mỗi đơn {orderSn, shopeeOrderId} → mở tab CHI TIẾT (active:false) → pageReadFinalAmount (nguồn CHÍNH nay là
// BẢNG DOANH THU trang chính, thẻ [type='FinalAmount'] lùi làm dự phòng) → trả text. Best-effort per-đơn (1 đơn
// lỗi/timeout → finalText rỗng, KHÔNG phá lượt); LUÔN đóng tab chi tiết; dò /verify → gom cờ captcha + dừng lượt.
// Chốt chặn 30 đơn/lượt. KHÔNG đổi active sang tab chi tiết (giữ tab thao tác).
// ĐỌC KÈM danh sách SẢN PHẨM (pageReadOrderProducts) NGAY TRONG tab chi tiết đó — miễn phí, KHÔNG thêm lượt mở
// trang nào (chrome.tabs.create của hàm này vẫn là chỗ DUY NHẤT mở tab trong cả extension đơn hàng).
// Trả {action:"pageData", kind:"finals", data:<json mảng {orderSn, finalText, sanPham:[…], nguon}>}.
export async function doSyncOrderFinals(orders) {
  const MAX_FINALS = 30;
  let list = Array.isArray(orders) ? orders : [];
  if (list.length > MAX_FINALS) {
    send({ action: "progress", message: "syncOrderFinals: " + list.length + " đơn → cắt còn " + MAX_FINALS + "/lượt." });
    list = list.slice(0, MAX_FINALS);
  }
  const out = [];
  for (const o of list) {
    const orderSn = o && o.orderSn ? String(o.orderSn) : "";
    const shopeeOrderId = o && o.shopeeOrderId ? String(o.shopeeOrderId).replace(/[^0-9]/g, "") : "";
    if (!orderSn || !shopeeOrderId) continue;
    let tabId = null;
    let finalText = "";
    let sanPham = [];
    let nguon = ""; // nguồn đọc được / lý do hụt — chỉ để C# log (xem pageChanDoanUocTinh)
    try {
      const t = await chrome.tabs.create({ url: ORDER_DETAIL_PREFIX + shopeeOrderId, active: false });
      tabId = t.id;
      await waitForTabComplete(tabId, 20000);
      let url = "";
      try { url = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
      if (/\/verify/i.test(url)) {
        try { await chrome.tabs.remove(tabId); } catch (e) {}
        tabId = null;
        send({ action: "captcha", message: url });
        return;
      }
      // POLL ≤15s (500ms) tới khi đọc được số. GIỮ NGUYÊN trần 15s — nguồn CHÍNH nay là bảng doanh thu của trang
      // chính (render cùng nhịp .product-list) nên thường có ngay từ lần poll đầu, chờ thêm là vô ích.
      const dl = Date.now() + 15000;
      while (Date.now() < dl) {
        try { finalText = (await execInTab(tabId, pageReadFinalAmount, [])) || ""; } catch (e) { finalText = ""; }
        if (finalText) break;
        await sleep(500);
      }
      // CHẨN ĐOÁN cho log phía C#: đọc SAU khi poll xong nên KHÔNG ảnh hưởng thời gian chờ.
      //   "chi-bang"   = lấy được TRONG KHI thẻ remote chưa có số ⇒ đúng số đơn mà bảng doanh thu CỨU được
      //   "ca-hai"     = lấy được và thẻ remote cũng đã có số (đường cũ cũng chạy được)
      //   "dang-tai"   = KHÔNG lấy được, thẻ có mặt nhưng .amount còn rỗng (remote chưa về, hết giờ)
      //   "khong-thay" = KHÔNG lấy được, chưa thấy thẻ nào trong DOM
      try {
        const cd = await execInTab(tabId, pageChanDoanUocTinh, []);
        if (finalText) nguon = cd && cd.theCoSo ? "ca-hai" : "chi-bang";
        else nguon = cd && cd.coThe ? "dang-tai" : "khong-thay";
      } catch (e) { nguon = ""; }
      // Đọc THÊM sản phẩm trong CHÍNH tab này (kể cả khi finalText rỗng). Bọc try RIÊNG: sản phẩm là phần THÊM,
      // lỗi ở đây KHÔNG được làm mất finalText vừa đọc được.
      try {
        const raw = (await execInTab(tabId, pageReadOrderProducts, [MAX_ORDER_PRODUCTS])) || "[]";
        const arr = JSON.parse(raw);
        if (Array.isArray(arr)) sanPham = arr;
      } catch (e) { sanPham = []; }
    } catch (e) {
      finalText = ""; // 1 đơn lỗi → bỏ, tiếp đơn kế
    } finally {
      if (tabId != null) { try { await chrome.tabs.remove(tabId); } catch (e) {} } // LUÔN đóng tab chi tiết
    }
    out.push({ orderSn: orderSn, finalText: finalText, sanPham: sanPham, nguon: nguon });
  }
  send({ action: "pageData", kind: "finals", data: JSON.stringify(out) });
}

// Phần B: xử ĐƠN ĐẦU cần "Chuẩn bị hàng" (port ProcessFirstOrderAsync). → {action:"orderPrepared",...} hoặc {action:"noOrder"}.
export async function doPrepareNextOrder() {
  const tabId = orderTabIdStrict();
  if (tabId == null) { send({ action: "error", message: "chuẩn bị hàng: " + LOI_MAT_TAB_SHOP }); return; }

  try { await chrome.tabs.update(tabId, { url: ORDERS_URL }); } catch (e) {}
  await waitForTabComplete(tabId, 20000);
  let url = "";
  try { url = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
  if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }

  await waitOrdersStable(tabId, 15000);
  await ensureDbg(tabId); // attach TRƯỚC khi đọc toạ độ → banner đứng yên, toạ độ + click cùng trạng thái (click không trượt).
  // Tab "Chờ lấy hàng".
  const toShipTab = await execInTab(tabId, pageLocateByText, [["[role='tab']", ".eds-tabs__nav-tab", "a", "div", "span"], "^cho lay hang"]);
  if (toShipTab) { await trustedClick(tabId, toShipTab.x, toShipTab.y); await sleep(1200); await waitOrdersStable(tabId, 10000); }

  // Đơn đầu có "Chuẩn bị hàng" VÀ đọc được mã. Kết quả { thieuMa } (có nút mà không card nào đọc được mã) chỉ
  // được CHỐT sau khi hết cửa sổ poll — card đang render dở có thể thiếu .order-sn một nhịp, chờ thêm là có.
  let prep = null;
  const dl = Date.now() + 12000;
  while (Date.now() < dl) {
    prep = await execInTab(tabId, pageFindPrepareOrder, []);
    if (prep && !prep.thieuMa) break;
    await sleep(500);
  }
  if (!prep) { send({ action: "noOrder" }); return; }
  // KHÔNG thao tác mù trên đơn thật (T3, review 11/08): card có nút "Chuẩn bị hàng" mà không đọc được mã đơn
  // (.order-sn đổi markup?) thì arrange + in phiếu vẫn CHẠY THẬT trên Shopee trong khi app không biết đơn nào —
  // phiếu ghi đè lẫn nhau vào "phieu.pdf", không MarkPrepared, mã vận đơn bắt được bị vứt. pageFindPrepareOrder
  // đã tự BỎ QUA card hỏng mã để thử card kế; tới đây vẫn thieuMa nghĩa là KHÔNG card nào dùng được → báo
  // `prepareBlocked` (KHÔNG phải noOrder — C# phải log được "selector hỏng" chứ không phải "hết đơn" y shop
  // khỏe), để vòng sau (sau khi sửa selector) xử tiếp. Arrange mù thì không rút lại được.
  if (prep.thieuMa) {
    send({
      action: "progress",
      message: "⚠ card có nút 'Chuẩn bị hàng' nhưng KHÔNG đọc được mã đơn (.order-sn đổi markup?) — DỪNG bước "
        + "chuẩn bị hàng của shop này, không thao tác trên đơn thật.",
    });
    send({ action: "prepareBlocked" });
    return;
  }
  const orderCode = prep.orderCode || "";
  if (!orderCode) { send({ action: "prepareBlocked" }); return; } // lưới cuối — pageFindPrepareOrder đã chặn ở trên

  const beforeTabs = (await chrome.tabs.query({})).map((t) => t.id); // mọi tab (tab phiếu awbprint có thể khác domain).

  // Modal "Giao Đơn Hàng" đôi khi dựng chậm hoặc cú click đầu bị hụt khi UI đang repaint.
  // Cho phép bấm lại vài nhịp trước khi kết luận là không mở được.
  let hasShip = false;
  let shipAttempts = 0;
  const shipDeadline = Date.now() + 18000;
  while (!hasShip && Date.now() < shipDeadline && shipAttempts < 4) {
    // TRƯỚC mỗi cú bấm (nhất là cú bấm LẠI): modal đúng tiêu đề đã mở → xong; còn BẤT KỲ modal nào đang hiện
    // → CHỜ, tuyệt đối không bấm. prep.y nằm giữa viewport (pageFindPrepareOrder cuộn về center) nên cú bấm mù
    // lúc modal đang dựng rơi trúng lớp mask (modal đóng lại → lặp mở/đóng → báo "không mở được modal" OAN)
    // hoặc trúng nút TRONG modal (đơn bị arrange với phương thức giao MẶC ĐỊNH, sai).
    hasShip = await execInTab(tabId, pageModalHasTitle, ["^giao don hang$"]);
    if (hasShip) break;
    let coModal = false;
    try { coModal = await execInTab(tabId, pageAnyModalVisible, []); } catch (e) { coModal = false; }
    if (coModal) { await sleep(500); continue; } // modal khác đang dựng/đang đóng → chờ, không tiêu một lượt bấm

    shipAttempts++;
    await trustedClick(tabId, prep.x, prep.y);
    // ≥10s mỗi lượt (bằng bản trước khi có retry): máy chậm dựng modal ~5s, probe ngắn là bấm chồng lên modal.
    const probeDeadline = Date.now() + 10000;
    while (Date.now() < probeDeadline) {
      hasShip = await execInTab(tabId, pageModalHasTitle, ["^giao don hang$"]);
      if (hasShip) break;
      await sleep(250);
    }
    if (!hasShip) await sleep(500);
  }
  if (!hasShip) {
    let diag = "";
    try { diag = await execInTab(tabId, pageDumpClickables, []); } catch (e) { diag = ""; }
    send({ action: "error", message: "không mở được modal Giao Đơn Hàng (đơn " + orderCode + ")" + (diag ? " — clickable: " + diag : "") });
    return;
  }
  await sleep(800);

  // Chọn "tự mang hàng tới Bưu cục" (nếu chưa selected).
  const drop = await execInTab(tabId, pageLocateInModal, ["^giao don hang$", ["div", "label", "[role='radio']", "span"], "tu mang hang toi buu cuc"]);
  if (drop && !drop.selected) { await trustedClick(tabId, drop.x, drop.y); await sleep(600); }

  // "Xác nhận".
  const conf = await execInTab(tabId, pageLocateInModal, ["^giao don hang$", [".eds-modal__footer button", "button", "[role='button']"], "^xac nhan$"]);
  if (!conf) { send({ action: "error", message: "không thấy nút Xác nhận (đơn " + orderCode + ")" }); return; }
  await trustedClick(tabId, conf.x, conf.y);

  // Modal "Thông Tin Chi Tiết".
  let hasDetail = false;
  const dl2 = Date.now() + 15000;
  while (Date.now() < dl2) { hasDetail = await execInTab(tabId, pageModalHasTitle, ["^thong tin chi tiet$"]); if (hasDetail) break; await sleep(500); }
  if (!hasDetail) { send({ action: "error", message: "không mở được modal Thông Tin Chi Tiết (đơn " + orderCode + ")" }); return; }
  await sleep(1000);

  // Đọc MÃ VẬN ĐƠN NGAY tại modal "Thông Tin Chi Tiết" (Shopee tạo vận đơn sau vài giây: "...đang được tạo" → mã thật).
  // Poll tới khi có mã (tối đa 90s; modal vẫn mở — cũng là lúc chờ Shopee tạo vận đơn để nút In phiếu hoạt động). Không
  // lấy được → để rỗng (list sẽ bù chu kỳ sau). Bắt tại đây = app + GSheet + hub có mã NGAY, khỏi chờ chu kỳ sync kế.
  let tracking = "";
  { const trd = Date.now() + 90000;
    while (Date.now() < trd) {
      try { tracking = (await execInTab(tabId, pageReadModalTracking, [])) || ""; } catch (e) { tracking = ""; }
      if (tracking) break;
      await sleep(1000);
    } }
  if (tracking) send({ action: "progress", message: "Mã vận đơn (đơn " + orderCode + "): " + tracking });

  // "In phiếu giao" → bắt tab phiếu (window.open → awbprint). Bấm lặp tới khi có tab (Shopee tạo vận đơn có thể muộn, ~2').
  send({ action: "progress", message: "modal Chi Tiết mở — tìm + bấm 'In phiếu giao'..." });
  let slipTab = null;
  let printTries = 0;
  const printDeadline = Date.now() + 120000;
  while (!slipTab && Date.now() < printDeadline) {
    const tabs = await chrome.tabs.query({});
    const cand = tabs.find((t) => beforeTabs.indexOf(t.id) === -1 && t.id !== tabId);
    if (cand) { slipTab = cand; break; }
    await ensureDbg(tabId);
    const pbtn = await execInTab(tabId, pagePrintButton, []);
    printTries++;
    if (pbtn) {
      await trustedClick(tabId, pbtn.x, pbtn.y);
      if (printTries <= 2) send({ action: "progress", message: "đã bấm 'In phiếu giao' tại (" + pbtn.x + "," + pbtn.y + ") — chờ tab phiếu..." });
    } else {
      if (printTries <= 2) send({ action: "progress", message: "CHƯA thấy nút 'In phiếu giao' (data-testid=print-button) — thử lại..." });
    }
    const wt = Date.now() + 8000;
    while (!slipTab && Date.now() < wt) {
      const tt = await chrome.tabs.query({});
      const cc = tt.find((t) => beforeTabs.indexOf(t.id) === -1 && t.id !== tabId);
      if (cc) { slipTab = cc; break; }
      await sleep(400);
    }
    if (!slipTab) await sleep(800);
  }
  if (!slipTab) { send({ action: "error", message: "không mở được tab phiếu giao sau " + printTries + " lần bấm (đơn " + orderCode + ")" }); return; }
  send({ action: "progress", message: "đã bắt được tab phiếu (sau " + printTries + " lần bấm)." });

  // Chờ tab phiếu điều hướng tới awbprint.
  const slipId = slipTab.id;
  let slipUrl = slipTab.url || "";
  const ud = Date.now() + 10000;
  while (Date.now() < ud) {
    try { const t = await chrome.tabs.get(slipId); slipUrl = t.url || slipUrl; if (slipUrl.indexOf("awbprint") >= 0) break; } catch (e) { break; }
    await sleep(300);
  }

  // Tải PDF phiếu NGAY TRONG TAB PHIẾU (có cookie, same-origin blob) → base64. Poll ~25s để khung nhúng PDF kịp render.
  await waitForTabComplete(slipId, 15000);
  let slipB64 = "";
  const fd = Date.now() + 25000;
  while (Date.now() < fd) {
    try { slipB64 = (await execInTab(slipId, pageFetchSlipBase64, [])) || ""; } catch (e) { slipB64 = ""; }
    if (slipB64) break;
    await sleep(800);
  }

  // Đóng tab phiếu sau khi lấy xong (user yêu cầu: lưu xong thì close tab phiếu).
  try { await chrome.tabs.remove(slipId); } catch (e) {}

  send({ action: "orderPrepared", orderCode: orderCode, slipTabUrl: slipUrl, slipBase64: slipB64, tracking: tracking });
}

// Tải LẠI phiếu MỘT đơn (nút "Tải phiếu" màn Đơn hàng): về danh sách "Tất cả" của shop đang mở → duyệt trang tìm
// card mã = orderSn → bấm "In phiếu giao" NGAY trong card → bắt tab awbprint → tải PDF base64 → trả về C#.
// KHÔNG arrange lại (đơn đã Chuẩn bị hàng). LUÔN trả {action:"slipRedownloaded", orderSn, slipBase64}: base64 rỗng =
// không thấy đơn / chưa có nút In phiếu / không lấy được (C# coi là thất bại). /verify → gửi captcha rồi thôi.
export async function doRedownloadSlip(orderSn) {
  const tabId = orderTabIdStrict();
  // Mất tab shop ⇒ BÁO LỖI chứ không trả "không lấy được": trả rỗng là đổ oan cho đơn (C# ghi nhận đơn này không
  // tải được phiếu) trong khi thủ phạm là service worker vừa chết. Thiếu MÃ ĐƠN thì vẫn theo lối cũ.
  if (tabId == null) { send({ action: "error", message: "tải lại phiếu: " + LOI_MAT_TAB_SHOP }); return; }
  if (!orderSn) { send({ action: "slipRedownloaded", orderSn: orderSn, slipBase64: "" }); return; }

  try { await chrome.tabs.update(tabId, { url: ORDERS_URL }); } catch (e) {}
  await waitForTabComplete(tabId, 20000);
  let url = "";
  try { url = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
  if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }

  await waitOrdersStable(tabId, 15000);
  await ensureDbg(tabId); // attach TRƯỚC khi đọc toạ độ + click (banner đứng yên → click không trượt).

  // Sang tab "Tất cả" (đơn Chuẩn bị hàng chắc chắn nằm trong "Tất cả").
  const allTab = await execInTab(tabId, pageLocateByText, [["[role='tab']", ".eds-tabs__nav-tab", "a", "div", "span"], "^tat ca$"]);
  if (allTab) { await trustedClick(tabId, allTab.x, allTab.y); await sleep(1200); await waitOrdersStable(tabId, 10000); }

  // Duyệt trang tìm card orderSn (có nút In phiếu).
  let loc = { found: false, hasPrint: false };
  let pageNo = 0;
  while (pageNo < MAX_ORDER_PAGES) {
    pageNo++;
    await waitOrdersStable(tabId, 15000);
    await ensureDbg(tabId);
    try { loc = (await execInTab(tabId, pageFindPrintInCardBySn, [orderSn])) || { found: false, hasPrint: false }; } catch (e) { loc = { found: false, hasPrint: false }; }
    if (loc && loc.found) break;

    // Sang trang sau (reuse signature + timNutTrangSau + waitOrdersChanged như doSyncOrders).
    let sigBefore = "";
    try { sigBefore = (await execInTab(tabId, pageListSignature, [])) || ""; } catch (e) {}
    const next = await timNutTrangSau(tabId);
    if (!next) break;
    if (pageNo >= MAX_ORDER_PAGES) break;
    await trustedClick(tabId, next.x, next.y);
    let changed = await waitOrdersChanged(tabId, sigBefore, 10000);
    // Cùng chốt chặn chữ ký như doSyncOrders — bấm lại khi trang thật ra ĐÃ đổi là nhảy 2 trang, đơn cần tìm
    // có thể nằm đúng trang bị nhảy qua.
    for (let lai = 0; !changed && lai < SO_LAN_BAM_LAI_PAGER; lai++) {
      const tt = await trangThaiTruocBamLai(tabId, pageListSignature, sigBefore);
      if (tt === "doi") { changed = true; break; }
      if (tt === "dangTai") { changed = await waitOrdersChanged(tabId, sigBefore, 10000); break; }
      const lanHai = await timNutTrangSau(tabId);
      if (!lanHai) break;
      await trustedClick(tabId, lanHai.x, lanHai.y);
      changed = await waitOrdersChanged(tabId, sigBefore, 10000);
    }
    if (!changed) {
      // Dừng câm ở đây là báo SAI thủ phạm ("không thấy đơn trong danh sách") cho một đơn thật ra nằm ở trang sau.
      send({
        action: "progress",
        message: "Tải lại phiếu: lật sang trang " + (pageNo + 1) + " trượt — chỉ duyệt được " + pageNo
          + " trang, đơn " + orderSn + " có thể nằm sâu hơn.",
      });
      break;
    }
    await sleep(1000);
  }

  if (!loc || !loc.found) {
    send({ action: "progress", message: "Tải lại phiếu: không thấy đơn " + orderSn + " trong danh sách (shop khác / quá cũ)." });
    send({ action: "slipRedownloaded", orderSn: orderSn, slipBase64: "" });
    return;
  }
  if (!loc.hasPrint) {
    send({ action: "progress", message: "Tải lại phiếu: đơn " + orderSn + " chưa có nút In phiếu (chưa chuẩn bị hàng?)." });
    send({ action: "slipRedownloaded", orderSn: orderSn, slipBase64: "" });
    return;
  }

  // Bấm "In phiếu giao" → bắt tab phiếu (awbprint).
  const beforeTabs = (await chrome.tabs.query({})).map((t) => t.id);
  await ensureDbg(tabId);
  await trustedClick(tabId, loc.x, loc.y);

  let slipTab = null;
  const printDeadline = Date.now() + 30000;
  while (!slipTab && Date.now() < printDeadline) {
    const tabs = await chrome.tabs.query({});
    const cand = tabs.find((t) => beforeTabs.indexOf(t.id) === -1 && t.id !== tabId);
    if (cand) { slipTab = cand; break; }
    await sleep(400);
  }
  if (!slipTab) {
    send({ action: "progress", message: "Tải lại phiếu: không mở được tab phiếu (đơn " + orderSn + ")." });
    send({ action: "slipRedownloaded", orderSn: orderSn, slipBase64: "" });
    return;
  }

  // Chờ tab phiếu tới awbprint rồi tải PDF NGAY TRONG TAB (có cookie, same-origin blob) → base64.
  const slipId = slipTab.id;
  let slipUrl = slipTab.url || "";
  const ud = Date.now() + 10000;
  while (Date.now() < ud) {
    try { const t = await chrome.tabs.get(slipId); slipUrl = t.url || slipUrl; if (slipUrl.indexOf("awbprint") >= 0) break; } catch (e) { break; }
    await sleep(300);
  }
  await waitForTabComplete(slipId, 15000);
  let slipB64 = "";
  const fd = Date.now() + 25000;
  while (Date.now() < fd) {
    try { slipB64 = (await execInTab(slipId, pageFetchSlipBase64, [])) || ""; } catch (e) { slipB64 = ""; }
    if (slipB64) break;
    await sleep(800);
  }
  try { await chrome.tabs.remove(slipId); } catch (e) {}

  send({ action: "slipRedownloaded", orderSn: orderSn, slipBase64: slipB64 });
}
