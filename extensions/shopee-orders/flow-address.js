// Địa chỉ lấy hàng của shop: đặt về tỉnh cần gửi (trước khi xử đơn) và trả VỀ địa chỉ khác (sau khi xử xong).
// Thân hàm GIỮ NGUYÊN từ background.js (tách 2026-08-06).
import { send, orderTabId } from "./core.js";
import { execInTab } from "./exec.js";
import { SHIPPING_SETTINGS_URL } from "./constants.js";
import {
  pageLocateByText, pageFindAddressEdit, pageFindOtherAddressEdit, pageModalHasTitle,
  pageFirstUncheckedBox, pageCheckboxCount, pageLocateInModal,
} from "./page-funcs.js";
import { sleep } from "./shared/util.js";
import { waitForTabComplete } from "./shared/tab-wait.js";
import { trustedClick } from "./shared/dbg-input.js";

// Phần B: đặt địa chỉ lấy hàng = province (port OpenShippingAddressSettingsAsync/SetPickupAddressAsync). → {action:"pickupDone", ok}.
export async function doSetPickupAddress(province) {
  const tabId = orderTabId();
  if (tabId == null) { send({ action: "error", message: "chưa có tab shop để đặt địa chỉ" }); return; }

  try { await chrome.tabs.update(tabId, { url: SHIPPING_SETTINGS_URL }); } catch (e) {}
  await waitForTabComplete(tabId, 20000);
  let url = "";
  try { url = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
  if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }

  await sleep(1000);
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
  if (!info || !info.found) { send({ action: "progress", message: "không thấy địa chỉ khớp tỉnh " + province + " — bỏ đặt địa chỉ lấy hàng." }); send({ action: "pickupDone", ok: false }); return; }
  // KHÔNG return sớm khi đã là pickup: vẫn mở Sửa để đảm bảo đủ 3 dấu tick (mặc định + lấy hàng + trả hàng).
  if (!info.hasEdit) {
    send({ action: "progress", message: info.hasTag ? ("địa chỉ " + province + " đã là địa chỉ lấy hàng (không có nút Sửa).") : ("không thấy nút Sửa của địa chỉ " + province + ".") });
    send({ action: "pickupDone", ok: info.hasTag });
    return;
  }

  await trustedClick(tabId, info.x, info.y);

  // Modal "Sửa Địa chỉ".
  let hasModal = false;
  const dlm = Date.now() + 10000;
  while (Date.now() < dlm) { hasModal = await execInTab(tabId, pageModalHasTitle, ["^sua dia chi$"]); if (hasModal) break; await sleep(400); }
  if (!hasModal) { send({ action: "progress", message: "không mở được modal Sửa Địa chỉ." }); send({ action: "pickupDone", ok: false }); return; }
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
  if (!cnt || cnt.total === 0) { send({ action: "progress", message: "không thấy checkbox trong modal Sửa Địa chỉ." }); send({ action: "pickupDone", ok: false }); return; }
  send({ action: "progress", message: "đã đảm bảo " + cnt.done + "/" + cnt.total + " checkbox địa chỉ có dấu tick." });

  // Lưu.
  const save = await execInTab(tabId, pageLocateInModal, ["^sua dia chi$", [".eds-modal__footer button", "button", "[role='button']"], "^luu$"]);
  if (!save) { send({ action: "progress", message: "không thấy nút Lưu." }); send({ action: "pickupDone", ok: false }); return; }
  await trustedClick(tabId, save.x, save.y);
  await sleep(1200);

  // Hộp xác nhận "Đồng ý" (không phải lúc nào cũng hiện).
  const confirm = await execInTab(tabId, pageLocateByText, [[".eds-modal__footer button", "button", "[role='button']"], "^dong y$"]);
  if (confirm) { await trustedClick(tabId, confirm.x, confirm.y); await sleep(1000); }

  send({ action: "progress", message: "đã đặt địa chỉ lấy hàng = " + province + "." });
  send({ action: "pickupDone", ok: true });
}

// Set địa chỉ lấy hàng VỀ ĐỊA CHỈ KHÁC (sau khi xử hết đơn) — port SetPickupAddressToOtherAsync. Tick CHỈ 2
// (mặc định + lấy hàng, skipReturn=true → GIỮ tag "trả hàng" ở địa chỉ mặc định). → {action:"pickupOtherDone", ok}.
export async function doSetPickupAddressToOther() {
  const tabId = orderTabId();
  if (tabId == null) { send({ action: "error", message: "chưa có tab shop để set địa chỉ khác" }); return; }

  try { await chrome.tabs.update(tabId, { url: SHIPPING_SETTINGS_URL }); } catch (e) {}
  await waitForTabComplete(tabId, 20000);
  let url = "";
  try { url = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
  if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }

  await sleep(1000);
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
  while (Date.now() < dlm) { hasModal = await execInTab(tabId, pageModalHasTitle, ["^sua dia chi$"]); if (hasModal) break; await sleep(400); }
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
  const save = await execInTab(tabId, pageLocateInModal, ["^sua dia chi$", [".eds-modal__footer button", "button", "[role='button']"], "^luu$"]);
  if (!save) { send({ action: "progress", message: "không thấy nút Lưu (set khác)." }); send({ action: "pickupOtherDone", ok: false }); return; }
  await trustedClick(tabId, save.x, save.y);
  await sleep(1200);

  // "Đồng ý" (nếu hiện).
  const confirm = await execInTab(tabId, pageLocateByText, [[".eds-modal__footer button", "button", "[role='button']"], "^dong y$"]);
  if (confirm) { await trustedClick(tabId, confirm.x, confirm.y); await sleep(1000); }

  send({ action: "progress", message: "đã set địa chỉ lấy hàng VỀ địa chỉ khác (giữ trả hàng ở địa chỉ mặc định)." });
  send({ action: "pickupOtherDone", ok: true });
}
