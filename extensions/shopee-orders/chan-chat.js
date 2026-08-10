// CHẶN SDK CHAT của Seller Centre qua CDP (Network.setBlockedURLs) — THÍ NGHIỆM chữa BỆNH GỐC của cầu nối.
//
// Bệnh (đo trong nhật ký Chromium ngày 10/08/2026): network service của trình duyệt bị dựng lại đều đặn ~240s/lần
// (15 lần/giờ, jitter ±0,2s), mỗi lần kéo sập WebSocket cầu nối:
//     ERROR:network_service_instance_impl.cc:618] Network service crashed or was terminated, restarting service.
//     "WebSocket connection to 'ws://localhost:47821/' failed"
// Giả thuyết (CHƯA chứng minh): 198 kết nối WebRTC + 197 data channel trong 60 phút đến từ SDK chat của Seller
// Centre. WebRTC chạy qua ĐÚNG network service đang chết. Chặn SDK chat đi thì có thể hết.
//
// ⚠ TẮT NHANH khi thí nghiệm hỏng: đặt CHAN_SDK_CHAT = false ở dưới rồi cho app phóng lại trình duyệt (extension
// được chép sang hồ sơ mỗi lần mở). Tắt là trở về hành vi trước 10/08/2026, KHÔNG còn lệnh CDP nào được gửi.
// Dấu hiệu phải tắt: trang Seller Centre không render nổi danh sách đơn hoặc trang trả hàng.
//
// KHÔNG đặt phần này trong shared/dbg-input.js: file đó là NGUỒN CHUẨN dùng chung 3 extension (sửa là phải chạy
// extensions/sync-shared.cmd, và release-suite.cmd có bước --check sẽ chặn nếu lệch). Chuyện chat của Seller
// Centre là chuyện RIÊNG của shopee-orders.

import { ensureDbg, dbgSend } from "./shared/dbg-input.js";
import { send } from "./core.js";

/** Công tắc của cả thí nghiệm. false ⇒ không gửi lệnh CDP nào, hành vi y hệt trước 10/08/2026. */
export const CHAN_SDK_CHAT = false;

// Mẫu URL chặn — lấy từ các request THẬT thấy trong nhật ký:
//   https://banhang.shopee.vn/chateasy/minichat.js?_=…
//   https://banhang.shopee.vn/webchat/api/coreapi/v1.2/mini/login/sc?…
// Cố ý HẸP (chỉ ba nhánh đường dẫn của chat), không chặn theo host: chặn rộng là giết luôn API đơn hàng.
export const MAU_URL_CHAN_CHAT = ["*/chateasy/*", "*minichat*", "*/webchat/*"];

// Tab ĐÃ thử áp lệnh chặn trong lần attach hiện tại (thành công hay không đều nhớ, để khỏi spam nhật ký mỗi lệnh).
// Reset khi debugger rời tab đó — Network.setBlockedURLs sống theo TARGET của phiên debugger, detach là mất sạch.
let _tabDaThu = null;

/**
 * Áp lệnh chặn lên tab đang được debugger attach. Giả định debugger ĐÃ attach (gọi qua ensureDbgChanChat).
 * NUỐT MỌI LỖI: chặn không được thì ghi nhật ký rồi chạy tiếp — thí nghiệm này tuyệt đối không được phá vòng chạy.
 * Trả true nếu lệnh chặn đã áp được trong lượt này.
 */
export async function chanSdkChat(tabId) {
  if (!CHAN_SDK_CHAT || tabId == null) { return false; }
  if (_tabDaThu === tabId) { return false; } // đã áp (hoặc đã thất bại) cho lần attach này
  _tabDaThu = tabId;
  try {
    // Bật domain Network là ĐIỀU KIỆN của setBlockedURLs. Kẹp hai bộ đệm xuống mức tối thiểu: ta chỉ cần lệnh
    // chặn, không bao giờ đọc lại thân phản hồi, mà Seller Centre tải rất nặng — để mặc định là ôm cả chục MB
    // thân phản hồi trong tiến trình trình duyệt suốt một vòng 12 shop, đúng chỗ đang nghi bị quá tải.
    await dbgSend({ tabId }, "Network.enable", { maxTotalBufferSize: 1024, maxResourceBufferSize: 1024 });
    await dbgSend({ tabId }, "Network.setBlockedURLs", { urls: MAU_URL_CHAN_CHAT });
    send({ action: "progress", message: "Đã CHẶN SDK chat trên tab " + tabId + " (" + MAU_URL_CHAN_CHAT.join(" ") + ")" });
    return true;
  } catch (e) {
    send({
      action: "progress",
      message: "KHÔNG chặn được SDK chat trên tab " + tabId + ": " + String((e && e.message) || e) + " — chạy tiếp như thường.",
    });
    return false;
  }
}

/** ensureDbg + áp lệnh chặn. Dùng THAY ensureDbg ở mọi chỗ của shopee-orders: CDP theo TARGET, nên đổi tab
 *  (hoặc bị detach rồi attach lại) là phải áp lại từ đầu. */
export async function ensureDbgChanChat(tabId) {
  await ensureDbg(tabId);
  await chanSdkChat(tabId);
}

// Debugger rời tab (điều hướng, tab đóng, user bấm "Huỷ" trên banner, ensureDbg đổi sang tab khác) → lệnh chặn
// của tab đó mất theo. Quên reset là lần attach sau tưởng đã chặn rồi và SDK chat vào lại im lặng.
try {
  chrome.debugger.onDetach.addListener((source) => {
    if (_tabDaThu != null && source && source.tabId === _tabDaThu) { _tabDaThu = null; }
  });
} catch (e) { /* không đăng ký được → cùng lắm là chặn hụt sau một lượt detach, không phá vòng */ }
