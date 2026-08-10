// Lõi service worker Đơn hàng: state dùng chung (ctx), cầu WS tới app C#, phân giải tab thao tác.
// Mọi module khác import từ đây; core KHÔNG import ngược module flow nào (entry đăng ký handler qua
// setCommandHandler) — giữ đồ thị import một chiều để service worker không chết vì import vòng.

// shared/ là BẢN COPY của extensions/shared/ — sửa ở nguồn chuẩn rồi chạy extensions/sync-shared.cmd.
import { createWsBridge } from "./shared/ws-bridge.js";

export const DEFAULT_PORT = 47821; // PHẢI khớp OrdersBridgeSession.BridgePort phía C# (khi hash rụng).

// State dùng chung của phiên cầu nối. Gom vào MỘT object vì binding import KHÔNG gán lại được từ module
// khác — mọi nơi đọc/ghi qua ctx.* là cùng một ô nhớ (trước đây là 3 biến `let` ở module scope).
export const ctx = {
  listTabId: null, // tab đang thao tác (subaccount → sau SSO là tab banhang /portal/shop)
  shopTabId: null, // tab shop mở ra sau khi bấm "Chi tiết"
  lastTabUrls: [], // chẩn đoán: các URL tab lần query gần nhất (đưa vào thông báo lỗi khi không thấy tab).
};

// ---- WebSocket ------------------------------------------------------------
// Mẫu reconnect ở shared/ws-bridge.js (bản đã fix của shopee-search); nhịp nối lại 1200ms giữ như cũ.
// C# chưa lên / rớt → tự thử lại (browser bị kill thì SW chết theo).

// Lệnh từ C# do entry (background.js) phân nhánh — đăng ký qua đây để core khỏi import ngược flow.
// ⚠ BẤT BIẾN: bridge.connect() chỉ được gọi SAU setCommandHandler (hiện chỉ background.js gọi, sau dòng 64).
// Gọi connect từ trong core.js (trước khi entry chạy xong) là lệnh C# bị handler rỗng NUỐT IM LẶNG.
let commandHandler = async () => {};
export function setCommandHandler(fn) { commandHandler = fn; }

/// Ngưỡng IM LẶNG coi socket là "chết câm" (xem `imLangToiDaMs` trong shared/ws-bridge.js). Bật được ở lane
/// này vì phía C# bắn `ping` đều đặn mỗi 20s (`OrdersBridgeChannel.NhipGiuSong`) suốt phiên, kể cả trong kỳ
/// nghỉ giữa hai shop — nên "im quá 65s" chỉ có thể là đường truyền đã chết, không phải đang rảnh.
/// 65s = lỡ 3 nhịp ping: rộng hơn hẳn một nhịp để máy nghẽn/GC không bị hiểu nhầm là đứt, mà vẫn NGẮN HƠN
/// `ChoNoiLai` (90s) phía C# — tức cầu nối kịp sống lại TRƯỚC khi C# bỏ chặng đang chờ. Đó là lý do chọn 65
/// chứ không phải 80 hay 90.
/// ⚠ Đây là lưới CUỐI, không phải đường chính. Đường chính là `onclose` → nối lại sau 1200ms; phía C# nay đã
/// Abort+Dispose socket khi vòng nhận kết thúc nên `onclose` phải bắn. Lưới này chỉ chặn phần đuôi: C# bị
/// đóng băng/giết mà TCP không kịp tiễn, tức những ca trước đây treo tới khi có thứ khác đánh thức.
export const TRAN_IM_LANG_MS = 65_000;

export const bridge = createWsBridge({
  defaultPort: DEFAULT_PORT,
  reconnectDelayMs: 1200,
  imLangToiDaMs: TRAN_IM_LANG_MS,
  // 'ready' đi TRƯỚC (giữ nguyên bắt tay cũ), rồi mới xả các câu trả lời tồn của lượt đứt trước.
  onOpen: (b) => { b.send({ action: "ready" }); xaHangDoiTraLoi(); },
  onMessage: (cmd) => {
    commandHandler(cmd).catch((e) => send({ action: "error", message: String((e && e.message) || e) }));
  },
});

// ---- Hàng đợi CÂU TRẢ LỜI chưa gửi được -------------------------------------
//
// Vì sao có: cầu nối đứt đều đặn 240s/lần (cả ngày 10/08/2026), extension nối lại sau 1–10s. Mỗi cú đứt rơi
// đúng vào lúc một flow VỪA TÍNH XONG kết quả mà CHƯA kịp gửi thì kết quả đó bị vứt lặng lẽ, và phía C# ngồi
// chờ tới hết hạn chặng rồi ném CauNoiRotGiuaChangException — đó chính là đường đánh rơi shop.
//
// ⚠⚠ RÀNG BUỘC SỐNG CÒN: hàng đợi này CHỈ giữ CÂU TRẢ LỜI (dữ liệu đã tính xong), TUYỆT ĐỐI KHÔNG giữ/chạy
// lại LỆNH. Gửi lại một kết quả đã có là vô hại; chạy lại `prepareNextOrder` là chuẩn bị hàng + in phiếu
// TRÙNG trên đơn thật của người dùng. Lệnh đi theo chiều C# → extension và không bao giờ đi qua đây.
//
// ⚠ Hàng đợi nằm trong BỘ NHỚ service worker: service worker MV3 bị giết thì mất. Chấp nhận được vì ca cần
// chữa là "socket đứt, SW vẫn sống, nối lại sau 1–10s" (scheduleReconnect chạy trong chính SW đó).

/// Trần SỐ GÓI giữ lại. Bình thường mỗi chặng chỉ có tối đa MỘT câu trả lời đang bay, nên 8 là rất rộng;
/// trần tồn tại để một chuỗi đứt dài không phình bộ nhớ SW (gói `orderPrepared` mang phiếu base64, nặng).
/// Vượt trần thì bỏ gói CŨ NHẤT — câu trả lời mới luôn đáng giá hơn.
export const TRAN_GOI_TON = 8;

/// Trần TUỔI của một gói tồn (ms). Phải NHỎ HƠN HẲN `OrdersBridgeChannel.ChoNoiLai` (90s) phía C#: khi socket
/// đứt, C# rút hạn chặng đang chờ xuống còn 90s rồi mới BỎ chặng. Gói già hơn ngưỡng đó không những vô ích mà
/// còn NGUY HIỂM — chặng cũ đã bị bỏ, chặng MỚI cùng loại có thể đang chờ, và câu trả lời cũ sẽ hoàn tất NHẦM
/// chặng mới (vd `orderPrepared` của đơn trước rơi vào lượt chuẩn bị đơn sau).
/// 45s chứ không phải 60–90s vì hai đồng hồ KHÔNG cùng gốc: tuổi gói đếm từ lúc xếp hàng, còn hạn 90s của C#
/// đếm từ lúc nó thấy socket đứt. Biên gấp đôi (45 vs 90) là chỗ hở cho lệch gốc đó. Lượt nối lại THẬT chỉ mất
/// 1–10s nên trần này không hề chật.
export const TRAN_TUOI_GOI_MS = 45_000;

/// Action KHÔNG xếp hàng: `progress` chỉ để ghi nhật ký, mất một dòng log không hỏng chặng nào, mà nó lại là
/// loại gói đông nhất — xếp hàng nó thì gói chở kết quả thật bị đẩy khỏi trần.
const ACTION_KHONG_XEP_HANG = ["progress"];

/// Gói tồn: `{ goi, luc }` (luc = Date.now lúc xếp hàng). Cũ → mới.
let goiTon = [];

/// Xả hàng đợi khi cầu nối vừa nối lại: bỏ gói quá tuổi, gửi lại phần còn lại ĐÚNG THỨ TỰ cũ, gói nào vẫn
/// không gửi được thì giữ tiếp cho lượt sau.
function xaHangDoiTraLoi() {
  if (!goiTon.length) return;
  const cho = goiTon;
  goiTon = [];
  const nguong = Date.now() - TRAN_TUOI_GOI_MS;
  let daGui = 0, daBoVaQuaCu = 0;
  for (const m of cho) {
    if (m.luc < nguong) { daBoVaQuaCu++; continue; }
    if (bridge.send(m.goi)) daGui++;
    else goiTon.push(m);
  }
  if (daGui || daBoVaQuaCu) {
    bridge.send({
      action: "progress",
      message: "Cầu nối nối lại: gửi lại " + daGui + " câu trả lời tồn" +
               (daBoVaQuaCu ? (", bỏ " + daBoVaQuaCu + " gói quá " + (TRAN_TUOI_GOI_MS / 1000) + "s") : "") + ".",
    });
  }
}

/// Gửi một gói lên C#. Gửi được thì thôi; RỚT (socket chưa/không còn mở, hoặc `ws.send` ném) thì GIỮ LẠI để
/// `xaHangDoiTraLoi` bắn lại ngay khi nối lại — xem khối chú thích hàng đợi ở trên.
export function send(obj) {
  if (bridge.send(obj)) return;
  if (!obj || ACTION_KHONG_XEP_HANG.indexOf(obj.action) >= 0) return;
  goiTon.push({ goi: obj, luc: Date.now() });
  if (goiTon.length > TRAN_GOI_TON) goiTon.shift();
}

// Phân giải "tab thao tác" một cách CHẮC CHẮN: nếu listTabId còn sống VÀ khớp host cần → giữ; ngược lại query
// TẤT CẢ tab rồi khớp theo CHUỖI URL (bỏ match-pattern cho khỏi vướng quyền), lấy tab MỚI NHẤT khớp. null nếu
// không thấy (kèm lastTabUrls để báo chẩn đoán).
export async function ensureListTab(preferSubstrings) {
  const subs = preferSubstrings || ["subaccount.shopee.com", "accounts.shopee.vn", "banhang.shopee.vn"];
  const matches = (url) => url && subs.some((s) => url.indexOf(s) >= 0);

  if (ctx.listTabId != null) {
    try { const t = await chrome.tabs.get(ctx.listTabId); if (t && matches(t.url)) return ctx.listTabId; } catch (e) {}
    ctx.listTabId = null;
  }
  const all = await chrome.tabs.query({});
  ctx.lastTabUrls = all.map((t) => t.url || t.pendingUrl || "").filter(Boolean);
  for (const s of subs) {
    const hit = all.filter((t) => (t.url || t.pendingUrl || "").indexOf(s) >= 0);
    if (hit.length) { ctx.listTabId = hit[hit.length - 1].id; return ctx.listTabId; }
  }
  return null;
}

// Tab dùng cho các lệnh cấp ĐƠN: ưu tiên tab shop (mở sau khi bấm "Chi tiết"), không có thì tab thao tác.
export function orderTabId() {
  return ctx.shopTabId != null ? ctx.shopTabId : ctx.listTabId;
}
