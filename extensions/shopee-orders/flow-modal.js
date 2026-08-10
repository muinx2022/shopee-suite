// Dọn MODAL CHẮN TRANG — dùng chung cho mọi bước của vòng shop.
// Tách khỏi flow-address.js (10/08/2026): modal chắn không chỉ hại bước đặt địa chỉ. Lượt chạy 10/08 cho thấy
// bước check đơn trả hàng cũng chết vì nó — cú trusted click vào tab "Trả hàng/Hoàn tiền/Hủy" bị mask nuốt,
// trang đứng nguyên ở /portal/sale/order rồi hết 20s chờ ô tổng.
import { send } from "./core.js";
import { execInTab } from "./exec.js";
import { pageLocateBlockingModalButton, pageLocateBlockingModalCheckbox, pageConLopPhuChan } from "./page-funcs.js";
import { sleep } from "./shared/util.js";
import { trustedClick } from "./shared/dbg-input.js";

// Trần chờ lớp phủ (mask) tan sau khi bấm đóng modal. EDS gỡ hộp modal trước, mask mờ dần ~300ms rồi mới mất;
// máy chậm thì lâu hơn. Chờ tới khi trang thật sự thông thoáng, KHÔNG ngủ một số cứng rồi bấm mù.
const TRAN_CHO_MASK_MS = 3000;

// Chờ trang hết bị lớp phủ chắn. Trả chuỗi mô tả lớp phủ còn sót (rỗng = đã thông thoáng, bấm được).
export async function choHetLopPhu(tabId, x, y) {
  const dl = Date.now() + TRAN_CHO_MASK_MS;
  let con = "";
  while (Date.now() < dl) {
    try { con = (await execInTab(tabId, pageConLopPhuChan, [x ?? null, y ?? null])) || ""; } catch (e) { con = ""; }
    if (!con) return "";
    await sleep(200);
  }
  return con;
}

// Số lần bấm đóng modal chắn tối đa trong MỘT lượt dọn — để không quay vô tận khi Shopee bật modal mới liên tục.
// 3 → 6 (10/08/2026): tour hướng dẫn `.on-boarding` đi NHIỀU BƯỚC, mỗi bước một nút "Đã hiểu"; trần 3 là bỏ dở
// giữa tour và trang vẫn bị chắn. Trần này chỉ tốn thời gian khi THẬT SỰ còn khối chắn — trang sạch thì vòng
// đầu đã `break`.
const TRAN_MODAL_CHAN = 6;

// Số ô tick tối đa xử trong MỘT modal chắn. Modal điều khoản hiện chỉ có 1 ô, để 3 cho modal nhiều ô; đây là
// chốt chặn để cú tick trượt (không đổi được trạng thái) không thành vòng lặp vô tận.
const TRAN_O_TICK_MODAL = 3;

// Dọn các modal thông báo đang CHẮN trang (không tính modal `giuTitle` mà flow đang chờ/đang dùng). Shopee bật
// thông báo đổi chính sách/tính năng đè lên trang; mask của nó nuốt mọi trusted click nên bước sau fail OAN.
// Trả về SỐ modal đã đóng được. `giuTitle` = regex tiêu đề (chuẩn hoá KHÔNG dấu) của modal mà flow đang dùng;
// flow nào không tự mở modal thì truyền "".
export async function dongModalChan(tabId, giuTitle) {
  const ten = (m) => (m && m.title) || "(không tiêu đề)";
  let daDong = 0;
  // ⚠ BỌC KÍN cả thân: execInTab (exec.js KHÔNG bọc executeScript) và trustedClick (dbgSend REJECT khi
  // chrome.runtime.lastError, vd "Detached while handling command" lúc trang đang điều hướng) đều NÉM được.
  // Để exception thoát ra là background.js gửi {action:"error"} → C# fault chặng đang chờ → ném khỏi vòng shop
  // → CẢ VÒNG dừng, KHÔNG ghi banner, KHÔNG cảnh báo. Tức một lượt dọn hỏng làm hỏng luôn các shop còn lại,
  // tệ hơn hẳn hành vi cũ (bỏ qua đúng 1 shop). Dọn modal là việc BEST-EFFORT: hỏng thì trả số đã đóng được.
  try {
    for (let i = 0; i < TRAN_MODAL_CHAN; i++) {
      // TICK TRƯỚC, BẤM SAU. Modal "Điều khoản - Điều kiện" khoá nút "Đồng ý" (disabled) cho tới khi tick ô
      // "Tôi xác nhận đã đọc..." và KHÔNG có nút ✕ → không tick thì pageLocateBlockingModalButton trả null,
      // dongModalChan tưởng trang sạch rồi bỏ đi, modal nằm lì nuốt mọi click ⇒ "Lỗi địa chỉ" oan.
      let daTick = 0;
      let oTick = null;
      for (let k = 0; k < TRAN_O_TICK_MODAL; k++) {
        const cb = await execInTab(tabId, pageLocateBlockingModalCheckbox, [giuTitle || ""]);
        if (!cb) break;
        oTick = cb;
        await trustedClick(tabId, cb.x, cb.y);
        await sleep(400);
        daTick++;
      }
      if (daTick > 0) {
        // Một dòng cho cả cụm (không phải mỗi ô một dòng): tick trượt thì chạm trần 3 lượt, log sẽ ngập.
        send({ action: "progress", message: 'modal chắn "' + ten(oTick) + '": đã tick ' + daTick + ' ô xác nhận để mở khoá nút.' });
      }

      const m = await execInTab(tabId, pageLocateBlockingModalButton, [giuTitle || ""]);
      if (!m) break;
      await trustedClick(tabId, m.x, m.y);
      // Chờ MASK TAN chứ không ngủ một số cứng: hộp modal biến mất trước, mask còn nằm đó thêm một nhịp và
      // nuốt gọn cú bấm kế tiếp của flow (ca 10/08/2026 — đóng được modal xong vẫn trượt 2 cú bấm liền).
      const conPhu = await choHetLopPhu(tabId);
      if (conPhu) {
        send({ action: "progress", message: "sau khi đóng modal vẫn còn lớp phủ chắn trang: " + conPhu });
      }

      // ĐẾM THEO MODAL ĐÃ BIẾN MẤT, không theo số cú bấm: cú bấm có thể trượt (rơi vào mask của modal khác,
      // hoặc nút dò được không thật sự đóng gì). Đếm theo cú bấm thì daDong luôn > 0 ⇒ chốt "chỉ thử lại khi
      // đóng được modal" ở doSetPickupAddress thành vô nghĩa, và MỌI shop tốn thêm nguyên một lượt thử.
      const con = await execInTab(tabId, pageLocateBlockingModalButton, [giuTitle || ""]);
      if (con && con.title === m.title) {
        // Bấm rồi mà đúng modal đó vẫn còn → không đóng được, bấm thêm 2 lượt nữa cũng vậy. Dừng, KHÔNG đếm.
        send({ action: "progress", message: 'modal chắn "' + ten(m) + '" KHÔNG đóng được bằng nút "' + m.label + '" — bỏ cuộc.' });
        break;
      }
      daDong++;
      send({ action: "progress", message: 'đã đóng modal chắn "' + ten(m) + '" bằng nút "' + m.label + '".' });
      if (!con) break; // sạch trang rồi, khỏi dò thêm
      // Còn modal KHÁC → vòng sau xử. Chạm trần thì báo ra, CẤM im lặng bỏ cuộc.
      if (i === TRAN_MODAL_CHAN - 1) {
        send({ action: "progress", message: "còn modal chắn sau " + TRAN_MODAL_CHAN + " lượt đóng — bỏ cuộc." });
      }
    }
  } catch (e) {
    send({ action: "progress", message: "dọn modal chắn lỗi (bỏ qua, không phá vòng): " + String((e && e.message) || e) });
  }
  return daDong;
}
