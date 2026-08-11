// Địa chỉ lấy hàng của shop: đặt về tỉnh cần gửi (trước khi xử đơn) và trả VỀ địa chỉ khác (sau khi xử xong).
// Thân hàm GIỮ NGUYÊN từ background.js (tách 2026-08-06).
import { send, orderTabIdStrict, LOI_MAT_TAB_SHOP } from "./core.js";
import { execInTab } from "./exec.js";
import { SHIPPING_SETTINGS_URL } from "./constants.js";
import {
  pageLocateByText, pageFindAddressEdit, pageFindOtherAddressEdit, pageModalHasTitle,
  pageFirstUncheckedBox, pageCheckboxCount, pageLocateInModal,
} from "./page-funcs.js";
import { dongModalChan } from "./flow-modal.js";
import { sleep } from "./shared/util.js";
import { waitForTabComplete } from "./shared/tab-wait.js";
import { trustedClick } from "./shared/dbg-input.js";

// Tiêu đề (chuẩn hoá KHÔNG dấu) của modal mà flow này TỰ mở và đang dùng. Mọi lượt dò "modal chắn" phải loại
// trừ nó, kẻo tự bấm đóng chính modal mình đang thao tác.
const MODAL_SUA_DIA_CHI = "^sua dia chi$";

// MỘT LƯỢT thử đặt địa chỉ: điều hướng về trang Cài đặt vận chuyển rồi đi hết các bước trên trang đó.
// KHÔNG gửi pickupDone (hàm gọi quyết định gửi đúng MỘT lần) — chỉ gửi progress, và gửi captcha ở nhánh /verify.
// Trả { ok, lyDo, captcha? }: `lyDo` để hàm gọi ghi nhật ký khi bỏ cuộc; `captcha:true` = đã gửi action captcha
// rồi, hàm gọi phải return NGAY và KHÔNG gửi pickupDone (giữ y hành vi cũ — C# xử qua _ch.CaptchaSeen).
// donModalTruoc=true: dọn modal chắn ngay khi trang vừa load (ca phổ biến nhất — modal có sẵn lúc vào trang —
// được xử ngay trong lượt 1, không tốn thêm lượt thử nào).
async function thuDatDiaChi(tabId, province, donModalTruoc) {
  try { await chrome.tabs.update(tabId, { url: SHIPPING_SETTINGS_URL }); } catch (e) {}
  await waitForTabComplete(tabId, 20000);
  let url = "";
  try { url = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
  if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return { ok: false, lyDo: "captcha", captcha: true }; }

  await sleep(1000);
  if (donModalTruoc) { await dongModalChan(tabId, MODAL_SUA_DIA_CHI); }

  // Tab "Địa Chỉ".
  let addrTab = null;
  const dl0 = Date.now() + 10000;
  while (Date.now() < dl0) {
    addrTab = await execInTab(tabId, pageLocateByText, [[".eds-tabs__nav-tab", "[role='tab']", "div", "span", "a"], "dia chi"]);
    if (addrTab) break;
    await sleep(500);
  }
  if (addrTab) { await trustedClick(tabId, addrTab.x, addrTab.y); await sleep(1200); }

  // Địa chỉ khớp tỉnh.
  let info = null;
  const dl = Date.now() + 15000;
  while (Date.now() < dl) {
    info = await execInTab(tabId, pageFindAddressEdit, [province]);
    if (info && info.found) break;
    await sleep(500);
  }
  if (!info || !info.found) {
    send({ action: "progress", message: "không thấy địa chỉ khớp tỉnh " + province + " — bỏ đặt địa chỉ lấy hàng." });
    return { ok: false, lyDo: "không thấy địa chỉ khớp tỉnh " + province };
  }
  // KHÔNG return sớm khi đã là pickup: vẫn mở Sửa để đảm bảo đủ 3 dấu tick (mặc định + lấy hàng + trả hàng).
  if (!info.hasEdit) {
    send({ action: "progress", message: info.hasTag ? ("địa chỉ " + province + " đã là địa chỉ lấy hàng (không có nút Sửa).") : ("không thấy nút Sửa của địa chỉ " + province + ".") });
    return { ok: info.hasTag, lyDo: "không thấy nút Sửa của địa chỉ " + province };
  }

  await trustedClick(tabId, info.x, info.y);

  // Modal "Sửa Địa chỉ".
  let hasModal = false;
  const dlm = Date.now() + 10000;
  while (Date.now() < dlm) { hasModal = await execInTab(tabId, pageModalHasTitle, [MODAL_SUA_DIA_CHI]); if (hasModal) break; await sleep(400); }
  if (!hasModal) {
    send({ action: "progress", message: "không mở được modal Sửa Địa chỉ." });
    return { ok: false, lyDo: "không mở được modal Sửa Địa chỉ" };
  }
  await sleep(800);

  // Tick TẤT CẢ checkbox cần (mặc định + lấy hàng + trả hàng) — bỏ qua cái đã tick / disabled. Lặp lấy-cái-đầu-chưa-tick → click.
  let cbGuard = 0;
  while (cbGuard < 8) {
    cbGuard++;
    const un = await execInTab(tabId, pageFirstUncheckedBox, [false]);
    if (!un) break;
    await trustedClick(tabId, un.x, un.y);
    await sleep(500);
  }
  const cnt = await execInTab(tabId, pageCheckboxCount, []);
  if (!cnt || cnt.total === 0) {
    send({ action: "progress", message: "không thấy checkbox trong modal Sửa Địa chỉ." });
    return { ok: false, lyDo: "không thấy checkbox trong modal Sửa Địa chỉ" };
  }
  send({ action: "progress", message: "đã đảm bảo " + cnt.done + "/" + cnt.total + " checkbox địa chỉ có dấu tick." });

  // Lưu.
  const save = await execInTab(tabId, pageLocateInModal, [MODAL_SUA_DIA_CHI, [".eds-modal__footer button", "button", "[role='button']"], "^luu$"]);
  if (!save) {
    send({ action: "progress", message: "không thấy nút Lưu." });
    return { ok: false, lyDo: "không thấy nút Lưu" };
  }
  await trustedClick(tabId, save.x, save.y);
  await sleep(1200);

  // Hộp xác nhận "Đồng ý" (không phải lúc nào cũng hiện) — modal HỢP LỆ của flow, KHÔNG phải modal chắn.
  const confirm = await execInTab(tabId, pageLocateByText, [[".eds-modal__footer button", "button", "[role='button']"], "^dong y$"]);
  if (confirm) { await trustedClick(tabId, confirm.x, confirm.y); await sleep(1000); }

  send({ action: "progress", message: "đã đặt địa chỉ lấy hàng = " + province + "." });
  return { ok: true, lyDo: "" };
}

// Phần B: đặt địa chỉ lấy hàng = province (port OpenShippingAddressSettingsAsync/SetPickupAddressAsync). → {action:"pickupDone", ok}.
// Điều phối: lượt 1 (có dọn modal chắn trước) → hỏng thì dọn modal rồi thử LẠI ĐÚNG MỘT lượt → gửi pickupDone.
// ⚠ BẤT BIẾN: mỗi lần gọi hàm này, C# Arm MỘT chặng pickup rồi chờ ĐÚNG MỘT pickupDone — mọi lối ra phải gửi
// đúng một cái, trừ nhánh captcha (đã gửi action captcha, KHÔNG gửi pickupDone).
export async function doSetPickupAddress(province) {
  const tabId = orderTabIdStrict();
  if (tabId == null) { send({ action: "error", message: "đặt địa chỉ lấy hàng: " + LOI_MAT_TAB_SHOP }); return; }

  let kq = await thuDatDiaChi(tabId, province, /*donModalTruoc*/ true);
  if (kq.captcha) { return; }            // thuDatDiaChi đã send captcha
  if (!kq.ok) {
    // Lượt 1 hỏng: modal có thể bật SAU khi trang load xong (Shopee bật trễ). Dọn rồi thử LẠI đúng 1 lượt, và
    // CHỈ khi thực sự đóng được modal — không đóng được gì mà vẫn thử lại là tốn 1 lượt vô ích mỗi shop.
    const daDong = await dongModalChan(tabId, MODAL_SUA_DIA_CHI);
    if (daDong > 0) {
      send({ action: "progress", message: "đã đóng " + daDong + " modal chắn — thử đặt địa chỉ lại." });
      // donModalTruoc=true cả ở lượt 2: thuDatDiaChi TẢI LẠI trang, nên modal nào Shopee bật ở MỖI lần load sẽ
      // hiện lại ngay sau lượt dọn vừa rồi. Bỏ lượt dọn này là lượt 2 chết đúng cái lỗi lượt 1 vừa chết.
      // Giá: một execInTab trả null (~vài trăm ms) khi trang sạch — rẻ hơn nhiều so với mất cả shop.
      kq = await thuDatDiaChi(tabId, province, /*donModalTruoc*/ true);
      if (kq.captcha) { return; }
    }
  }
  if (!kq.ok) { send({ action: "progress", message: "đặt địa chỉ vẫn hỏng: " + kq.lyDo }); }
  send({ action: "pickupDone", ok: kq.ok });
}

// Set địa chỉ lấy hàng VỀ ĐỊA CHỈ KHÁC (sau khi xử hết đơn) — port SetPickupAddressToOtherAsync. Tick CHỈ 2
// (mặc định + lấy hàng, skipReturn=true → GIỮ tag "trả hàng" ở địa chỉ mặc định). → {action:"pickupOtherDone", ok}.
export async function doSetPickupAddressToOther() {
  const tabId = orderTabIdStrict();
  if (tabId == null) { send({ action: "error", message: "trả địa chỉ về mặc định: " + LOI_MAT_TAB_SHOP }); return; }

  try { await chrome.tabs.update(tabId, { url: SHIPPING_SETTINGS_URL }); } catch (e) {}
  await waitForTabComplete(tabId, 20000);
  let url = "";
  try { url = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
  if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }

  await sleep(1000);
  await dongModalChan(tabId, MODAL_SUA_DIA_CHI); // modal thông báo của Shopee nuốt trusted click — dọn trước, không thử lại
  // Tab "Địa Chỉ".
  let addrTab = null;
  const dl0 = Date.now() + 10000;
  while (Date.now() < dl0) {
    addrTab = await execInTab(tabId, pageLocateByText, [[".eds-tabs__nav-tab", "[role='tab']", "div", "span", "a"], "dia chi"]);
    if (addrTab) break;
    await sleep(500);
  }
  if (addrTab) { await trustedClick(tabId, addrTab.x, addrTab.y); await sleep(1200); }

  // Địa chỉ KHÁC (không mang tag "lấy hàng").
  let info = null;
  const dl = Date.now() + 15000;
  while (Date.now() < dl) {
    info = await execInTab(tabId, pageFindOtherAddressEdit, []);
    if (info && info.found) break;
    await sleep(500);
  }
  if (!info || !info.found || !info.hasEdit) {
    send({ action: "progress", message: "không thấy địa chỉ khác (không mang tag lấy hàng) — bỏ qua set về địa chỉ khác." });
    send({ action: "pickupOtherDone", ok: false });
    return;
  }

  await trustedClick(tabId, info.x, info.y);

  // Modal "Sửa Địa chỉ".
  let hasModal = false;
  const dlm = Date.now() + 10000;
  while (Date.now() < dlm) { hasModal = await execInTab(tabId, pageModalHasTitle, [MODAL_SUA_DIA_CHI]); if (hasModal) break; await sleep(400); }
  if (!hasModal) { send({ action: "progress", message: "không mở được modal Sửa Địa chỉ (set khác)." }); send({ action: "pickupOtherDone", ok: false }); return; }
  await sleep(800);

  // Tick 2 (mặc định + lấy hàng) — skipReturn=true: GIỮ "trả hàng" ở địa chỉ mặc định.
  let cbGuard = 0;
  while (cbGuard < 8) {
    cbGuard++;
    const un = await execInTab(tabId, pageFirstUncheckedBox, [true]);
    if (!un) break;
    await trustedClick(tabId, un.x, un.y);
    await sleep(500);
  }

  // Lưu.
  const save = await execInTab(tabId, pageLocateInModal, [MODAL_SUA_DIA_CHI, [".eds-modal__footer button", "button", "[role='button']"], "^luu$"]);
  if (!save) { send({ action: "progress", message: "không thấy nút Lưu (set khác)." }); send({ action: "pickupOtherDone", ok: false }); return; }
  await trustedClick(tabId, save.x, save.y);
  await sleep(1200);

  // "Đồng ý" (nếu hiện).
  const confirm = await execInTab(tabId, pageLocateByText, [[".eds-modal__footer button", "button", "[role='button']"], "^dong y$"]);
  if (confirm) { await trustedClick(tabId, confirm.x, confirm.y); await sleep(1000); }

  send({ action: "progress", message: "đã set địa chỉ lấy hàng VỀ địa chỉ khác (giữ trả hàng ở địa chỉ mặc định)." });
  send({ action: "pickupOtherDone", ok: true });
}
