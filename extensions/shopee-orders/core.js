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

export const bridge = createWsBridge({
  defaultPort: DEFAULT_PORT,
  reconnectDelayMs: 1200,
  onOpen: (b) => b.send({ action: "ready" }),
  onMessage: (cmd) => {
    commandHandler(cmd).catch((e) => send({ action: "error", message: String((e && e.message) || e) }));
  },
});

export function send(obj) {
  bridge.send(obj);
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
