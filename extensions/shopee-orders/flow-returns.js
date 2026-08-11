// Bước CUỐI vòng shop: trang "Trả hàng/Hoàn tiền/Hủy" — chọn tab, đổi sắp xếp, đọc ô tổng + các dòng.
// Thân hàm GIỮ NGUYÊN từ background.js (tách 2026-08-06).
import { send, orderTabIdStrict, LOI_MAT_TAB_SHOP } from "./core.js";
import { execInTab } from "./exec.js";
import {
  RETURNS_URL, RETURN_TAB_RE, SORT_NEWEST_RE, SORT_MENU_MARK_RE,
  MAX_RETURN_ROWS, MAX_RETURN_PAGES, MAX_RETURN_HEAD_HTML,
} from "./constants.js";
import {
  pageLocateReturnTab, pageReturnSummaryText, pageChanDoanTraHang, pageLocateReturnCaseTab,
  pageReturnRowCount, pageLocateSortButton, pageLocateSortOption, pageScanReturnRows,
  pageReturnListSignature, pageChanDoanPagerTraHang, pageChanDoanTabTraHang, pageChanDoanSapXep, pageDemMenuHien,
  pageChanDoanDiemBam,
} from "./page-funcs-returns.js";
import { pageFindNextPage } from "./page-funcs.js";
import { timNutTrangSau, trangThaiTruocBamLai, SO_LAN_BAM_LAI_PAGER } from "./pager.js";
import { dongModalChan, choHetLopPhu } from "./flow-modal.js";
import { sleep } from "./shared/util.js";
import { waitForTabComplete } from "./shared/tab-wait.js";
import { ensureDbg, trustedClick } from "./shared/dbg-input.js";

// Hạn chờ dải tab loại đơn ("Đơn Trả hàng/Hoàn tiền" · "Đơn Hủy" · "Đơn Giao không thành công") xuất hiện sau
// khi trang trả hàng render. 6s: đủ cho một nhịp vẽ lại của Vue, và vẫn nằm gọn trong hạn chặng Returns 90s
// kể cả khi lượt dọn modal trước đó tốn kha khá.
const CHO_TAB_LOAI_DON_MS = 6000;

// Mở dropdown "Sắp xếp theo": bấm tối đa 3 LƯỢT THẬT, mỗi lượt chờ menu 4s.
// Vì sao nhiều lượt (bằng chứng vòng 09:44/10:07): bấm xong mà chẩn đoán ra "menu tong=2 hien=0" — menu có trong
// DOM nhưng KHÔNG cái nào mở. Ba nguyên nhân đều có cơ tự khỏi ở lượt sau: (a) lượt đầu bấm trượt vì nút vừa bị
// cuộn/vẽ lại, (b) lượt đầu mở rồi bị chính cú nhả chuột đóng lại, (c) tour/modal chắn ở lượt đầu, dọn xong thì
// hết. Trần 3 lượt để bước phụ này không ngốn hạn chặng Returns 90s của cả trang.
// ⚠ "LƯỢT" ở đây là LƯỢT BẤM, KHÔNG phải "ứng viên .sort-button thứ n". Bản trước lấy `lan` làm thẳng chỉ số ứng
// viên rồi `break` khi hết ứng viên — mà trang thật LUÔN chỉ có 1 nút ⇒ lượt 2 thoát ngay ⇒ vòng "3 lượt" chưa
// bao giờ chạy quá 1 lượt. Bằng chứng đồng hồ: bản 2-lượt trước tốn 9–11s mỗi lần hỏng, bản đó chỉ 4–5s.
const SO_LAN_MO_SAP_XEP = 3;
const CHO_MENU_SAP_XEP_MS = 4000;
// Nghỉ giữa hai lượt bấm sắp xếp. Đủ để popper EDS của lượt trước đóng hẳn và layout lắng lại — bấm dồn thì cú
// sau rơi vào lúc menu đang thu, tức tự tay tái tạo đúng ca "mở rồi tự đóng" đang muốn thoát ra.
const NGHI_GIUA_HAI_LUOT_SAP_XEP_MS = 700;
// Trần thời gian cho CẢ bước đổi sắp xếp. Đổi sắp xếp là bước PHỤ (hỏng thì đọc theo thứ tự đang có), còn chặng
// Returns phía C# chỉ có 90s cho cả trang — mở trang + chờ ô tổng (tới 20s) + chọn tab (tới 8s) đã ăn kha khá.
// Nay mỗi lượt bấm còn kèm dọn tour + hai lượt chờ lớp phủ tan, nên ca xấu nhất (tour bật, cả 3 lượt trượt) đủ
// sức đẩy chặng quá hạn ⇒ mất TRỌN lượt check (mốc giữ nguyên) — tệ hơn hẳn cái giá "đọc sai thứ tự". Hết trần
// thì thôi thử, phần đọc số + quét dòng vẫn kịp chạy.
const TRAN_THOI_GIAN_SAP_XEP_MS = 35000;

// Chờ danh sách trả hàng ĐỔI so với ký hiệu 'before' sau khi bấm "trang sau" (đối xứng waitOrdersChanged của
// trang đơn). Bỏ trạng thái đang tải ("0|"): giữa hai trang Vue xoá sạch bảng một nhịp rồi mới vẽ trang mới.
async function waitReturnsChanged(tabId, before, timeoutMs) {
  const dl = Date.now() + timeoutMs;
  while (Date.now() < dl) {
    await sleep(300);
    let now = "";
    try { now = (await execInTab(tabId, pageReturnListSignature, [])) || ""; } catch (e) {}
    if (now.indexOf("0|") === 0) continue;
    if (now && now !== before) return true;
  }
  return false;
}

// Quét dòng của TRANG HIỆN TẠI, cắt theo phần còn lại của trần tổng.
async function quetTrangHienTai(tabId, daCo) {
  const con = MAX_RETURN_ROWS - daCo;
  if (con <= 0) return [];
  let rows = "[]";
  try { rows = (await execInTab(tabId, pageScanReturnRows, [con, MAX_RETURN_HEAD_HTML])) || "[]"; } catch (e) { rows = "[]"; }
  try { return JSON.parse(rows) || []; } catch (e) { return []; }
}

// Có nút "trang sau" DÙNG ĐƯỢC không (dùng chung pageFindNextPage với trang đơn — cùng bộ EDS pager).
async function coTrangSau(tabId) {
  try { return !!(await execInTab(tabId, pageFindNextPage, [])); } catch (e) { return false; }
}

// LẬT TRANG: từ trang đang mở, lật tối đa `soTrang` lượt, gom dòng vào `list` (đã có sẵn dòng trang hiện tại).
// Trả { soTrangLat, coTrangSau, pagerChanDoan } — dừng khi: hết trang / danh sách không đổi (bấm trượt) /
// chạm trần dòng. KHÔNG bao giờ ném: lật trang là phần MỞ RỘNG, hỏng thì lượt vẫn có dữ liệu trang đầu.
async function latTrang(tabId, list, soTrang) {
  let soTrangLat = 0;
  let pagerChanDoan = null;
  const tran = Math.min(Math.max(0, soTrang), MAX_RETURN_PAGES);
  while (soTrangLat < tran && list.length < MAX_RETURN_ROWS) {
    let sigTruoc = "";
    try { sigTruoc = (await execInTab(tabId, pageReturnListSignature, [])) || ""; } catch (e) {}
    const next = await timNutTrangSau(tabId);
    if (!next) {
      // CHỈ chẩn đoán khi chưa lật nổi trang NÀO trong lượt này: đó mới là ca "selector pager của trang trả hàng
      // khác trang đơn" cần lộ ra. Lật được rồi mới hết nút là đường THÀNH CÔNG (đã tới trang cuối) — bắn chẩn
      // đoán kèm 4000 ký tự HTML ở đó là báo động giả, mỗi shop mỗi vòng một lần.
      if (soTrangLat === 0) {
        try { pagerChanDoan = await execInTab(tabId, pageChanDoanPagerTraHang, [MAX_RETURN_HEAD_HTML]); } catch (e) {}
      }
      break;
    }
    await ensureDbg(tabId);
    await trustedClick(tabId, next.x, next.y);
    let doi = await waitReturnsChanged(tabId, sigTruoc, 10000);
    // Trượt thì đo lại toạ độ + bấm LẠI đúng một lượt (layout vừa nhảy / danh sách vừa vẽ lại). Vẫn trượt ⇒ nói
    // thẳng là đọc THIẾU: dừng câm ở đây là C# tưởng đã tới đáy rồi chốt mốc, tức nuốt phần đuôi vĩnh viễn.
    // ⚠ TRƯỚC cú bấm lại PHẢI hỏi chữ ký (trangThaiTruocBamLai): fetch chậm >10s làm cú bấm ĐÃ ăn trông y hệt
    // bấm trượt — bấm nữa là lật sang trang NỮA, trang vừa mở không ai quét (mất im lặng, còn tự chốt mốc).
    for (let lai = 0; !doi && lai < SO_LAN_BAM_LAI_PAGER; lai++) {
      const tt = await trangThaiTruocBamLai(tabId, pageReturnListSignature, sigTruoc);
      if (tt === "doi") { doi = true; break; }                                   // cú bấm đầu đã ăn, chỉ là đổi muộn
      if (tt === "dangTai") { doi = await waitReturnsChanged(tabId, sigTruoc, 10000); break; } // đang vẽ → chờ, KHÔNG bấm
      const lanHai = await timNutTrangSau(tabId);
      if (!lanHai) break;
      await trustedClick(tabId, lanHai.x, lanHai.y);
      doi = await waitReturnsChanged(tabId, sigTruoc, 10000);
    }
    if (!doi) {
      // KHÔNG đánh số trang tuyệt đối ở đây: mỗi lệnh `readReturnRequestsMore` chỉ lật 1 trang nên số đếm trong
      // hàm này là số CỤC BỘ — C# mới biết đang ở trang thứ mấy của cả lượt và tự log số đó.
      send({
        action: "progress",
        message: "Trả hàng: bấm 'trang sau' 2 lượt mà danh sách KHÔNG đổi — dừng lật, lượt này đọc THIẾU.",
      });
      break;
    }
    soTrangLat++;
    const them = await quetTrangHienTai(tabId, list.length);
    if (them.length === 0) break;
    for (const d of them) list.push(d);
  }
  return { soTrangLat: soTrangLat, coTrangSau: await coTrangSau(tabId), pagerChanDoan: pagerChanDoan };
}

// Bước CUỐI flow shop (bước PHỤ): mở trang "Trả hàng/Hoàn tiền/Hủy" của shop đang mở → ĐỔI SẮP XẾP sang
// "Ngày yêu cầu (Mới - Cũ)" (mặc định trang là "Ngày đến hạn" — không đổi thì luật "N dòng đầu" của C# bỏ sót
// ÂM THẦM) → đọc text ô tổng + HTML đầu từng dòng → trả MỘT lượt {soYeuCauText, sortApplied, tabTraHang, list}.
// MỘT NHỊP: gửi cả số LẪN các dòng, C# tự cắt còn k dòng đầu — trang chỉ mở/đọc đúng một lần, không tốn thêm
// vòng WS (đọc DOM thừa vài chục dòng rẻ hơn nhiều so với một lượt chờ-trả-lời nữa).
// LUÔN gửi pageData (kể cả khi không đọc được gì) để C# không phải ngồi chờ hết timeout; /verify → captcha.
export async function doReadReturnRequests() {
  const tabId = orderTabIdStrict();
  if (tabId == null) { send({ action: "error", message: "check đơn trả hàng: " + LOI_MAT_TAB_SHOP }); return; }

  const traVe = (summary, sortApplied, tabTraHang, list, chanDoan, them) => send({
    action: "pageData",
    kind: "returns",
    data: JSON.stringify(Object.assign({
      soYeuCauText: summary || "",
      sortApplied: !!sortApplied,
      tabTraHang: !!tabTraHang,
      list: list || [],
      // Chỉ có mặt ở lượt BỎ (không đọc được ô tổng) — xem pageChanDoanTraHang.
      chanDoan: chanDoan || null,
    }, them || {})),
  });

  // 0) Dọn modal chắn TRƯỚC. Bước này toàn trusted click, mà mask của modal nuốt sạch. Ca thật 10/08/2026:
  // modal "Điều khoản - Điều kiện" nuốt cú bấm tab, trang đứng nguyên ở /portal/sale/order rồi hết 20s chờ ô
  // tổng → bỏ lượt check. Flow này KHÔNG tự mở modal nào nên giữ lại "" (không loại trừ tiêu đề nào).
  await ensureDbg(tabId);
  await dongModalChan(tabId, "");

  // 1) Mở trang trả hàng: ưu tiên BẤM TAB (trusted click, data-testid ổn định); không thấy tab → điều hướng thẳng.
  let tab = null;
  const tdl = Date.now() + 8000;
  while (Date.now() < tdl) {
    try { tab = await execInTab(tabId, pageLocateReturnTab, []); } catch (e) { tab = null; }
    if (tab) break;
    await sleep(500);
  }
  let daDieuHuong = false;
  if (tab) {
    await trustedClick(tabId, tab.x, tab.y);
    // ĐƯỜNG LUI khi cú bấm bị nuốt (modal bật SAU lượt dọn, hoặc toạ độ tab đã cũ): chờ URL đổi sang trang trả
    // hàng, không đổi thì điều hướng thẳng như nhánh "không thấy tab". Trước đây bấm xong là tin luôn — bấm
    // trượt thì ngồi hết 20s chờ ô tổng trên ĐÚNG trang đơn rồi bỏ lượt (mốc giữ nguyên, mất cả lượt check).
    const udl = Date.now() + 6000;
    let daSang = false;
    while (Date.now() < udl) {
      let u = "";
      try { u = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
      if (/returnrefundcancel/i.test(u)) { daSang = true; break; }
      await sleep(400);
    }
    if (!daSang) {
      send({ action: "progress", message: "bấm tab 'Trả hàng/Hoàn tiền/Hủy' không ăn (modal chắn?) — điều hướng thẳng tới trang." });
      daDieuHuong = true;
    }
  } else {
    send({ action: "progress", message: "không thấy tab 'Trả hàng/Hoàn tiền/Hủy' — điều hướng thẳng tới trang." });
    daDieuHuong = true;
  }
  if (daDieuHuong) {
    try { await chrome.tabs.update(tabId, { url: RETURNS_URL }); } catch (e) {}
    await waitForTabComplete(tabId, 20000);
    // Trang mới load → modal của Shopee bật lại theo mỗi lần load, dọn lần nữa kẻo bước chọn tab/sắp xếp chết.
    await dongModalChan(tabId, "");
  }

  // 2) Chờ ô tổng render (trần 20s). Rơi /verify → báo captcha rồi thôi (C# coi là bỏ bước, đi tiếp).
  let summary = "";
  const dl = Date.now() + 20000;
  while (Date.now() < dl) {
    let url = "";
    try { url = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
    if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }
    try { summary = (await execInTab(tabId, pageReturnSummaryText, [])) || ""; } catch (e) { summary = ""; }
    if (summary) break;
    await sleep(600);
  }
  if (!summary) {
    // CỐ Ý KHÔNG nới 20s ở đây: chưa có dữ liệu thì nới là đoán. Thu 4 dấu hiệu rồi gửi kèm để C# log — lượt
    // chạy thật sau sẽ nói rõ hết giờ THẬT hay đọc nhầm tab hay sai selector (xem pageChanDoanTraHang).
    let cd = null;
    try { cd = await execInTab(tabId, pageChanDoanTraHang, []); } catch (e) { cd = null; }
    send({ action: "progress", message: "trang trả hàng chưa render ô tổng sau 20s — bỏ lượt check này." });
    traVe("", false, false, [], cd);
    return;
  }

  // 3) CHỌN TAB "Đơn Trả hàng Hoàn tiền" — PHẢI làm TRƯỚC bước đổi sắp xếp và trước khi đọc số. Không thấy tab
  // nào khớp / bấm mà tab KHÔNG lên "active" → tabTraHang=false, C# BỎ LƯỢT (mốc giữ nguyên): số của tab "Tất cả"
  // ghi vào mốc là nuốt vĩnh viễn mọi yêu cầu mới, xem QuyetDinhLuotTraHang bên C#.
  let tabTraHang = false;
  let dangChon = "";
  try {
    await ensureDbg(tabId);
    // CHỜ dải tab loại đơn vẽ xong rồi mới kết luận. Trước đây dò ĐÚNG MỘT LẦN: ca 10/08/2026 modal đóng lúc
    // :42 thì :43 đã kết luận "không thấy tab" — Vue vẽ lại dải tab sau khi trang hết bị chắn, ta nhìn quá sớm.
    let ct = null;
    const ctdl = Date.now() + CHO_TAB_LOAI_DON_MS;
    while (Date.now() < ctdl) {
      try { ct = await execInTab(tabId, pageLocateReturnCaseTab, [RETURN_TAB_RE]); } catch (e) { ct = null; }
      if (ct) break;
      await sleep(500);
    }
    if (ct && ct.daDung) {
      tabTraHang = true; // đã đúng tab → KHÔNG bấm (bấm lại = một vòng chờ vô ích, nhân với mọi shop mỗi lượt)
      dangChon = ct.dangChon || ""; // dòng đối chứng bên dưới phải in được NHÃN THẬT ở ĐÚNG nhánh này — chính
                                    // nhánh "đã đúng tab" đã cho dương tính giả 07:08 ngày 10/08/2026.
    } else if (ct) {
      const soTruoc = summary;
      let dongTruoc = 0;
      try { dongTruoc = (await execInTab(tabId, pageReturnRowCount, [])) || 0; } catch (e) { dongTruoc = 0; }
      // Chắc chắn ĐIỂM SẮP BẤM không bị lớp phủ nào đè. Đây là cú bấm quyết định cả lượt check (bấm trượt ⇒
      // đọc nhầm số của tab "Tất cả" ⇒ C# bỏ lượt), nên phải biết chắc thay vì bấm mù rồi đoán.
      const phuTruocKhiBam = await choHetLopPhu(tabId, ct.x, ct.y);
      if (phuTruocKhiBam) {
        send({ action: "progress", message: "điểm bấm tab 'Đơn Trả hàng Hoàn tiền' đang bị che bởi: " + phuTruocKhiBam });
      }
      await trustedClick(tabId, ct.x, ct.y);
      // Đổi tab → ô tổng VÀ danh sách vẽ lại. Chờ một trong hai đổi (trần 8s) rồi mới sang bước sắp xếp/đọc số.
      // Thoát sớm khi tab-strip đã báo "active": hai tab CÙNG số + cùng số dòng là chuyện thường, chờ trọn 8s
      // mỗi shop chỉ để chắc là phí.
      const cdl = Date.now() + 8000;
      while (Date.now() < cdl) {
        await sleep(500);
        let s2 = "", n2 = dongTruoc;
        try { s2 = (await execInTab(tabId, pageReturnSummaryText, [])) || ""; } catch (e) { s2 = ""; }
        try { n2 = (await execInTab(tabId, pageReturnRowCount, [])) || 0; } catch (e) { n2 = dongTruoc; }
        if ((s2 && s2 !== soTruoc) || n2 !== dongTruoc) { summary = s2 || summary; break; }
        let ct2 = null;
        try { ct2 = await execInTab(tabId, pageLocateReturnCaseTab, [RETURN_TAB_RE]); } catch (e) { ct2 = null; }
        if (ct2 && ct2.daDung) { summary = s2 || summary; break; }
      }
      // XÁC NHẬN bằng chính tab-strip, KHÔNG đặt cờ mù sau cú click: click TRƯỢT và "hai tab cùng số" trông y
      // hệt nhau qua ô tổng/số dòng, mà một bên là dữ liệu SAI tab.
      let ctSau = null;
      try { ctSau = await execInTab(tabId, pageLocateReturnCaseTab, [RETURN_TAB_RE]); } catch (e) { ctSau = null; }
      tabTraHang = !!(ctSau && ctSau.daDung);
      if (tabTraHang) { dangChon = ctSau.dangChon || ""; }
    }
  } catch (e) { /* chọn tab lỗi → tabTraHang=false ⇒ C# bỏ lượt */ }
  if (tabTraHang) {
    // ĐỐI CHỨNG một dòng: nói rõ nó tưởng đang đứng ở tab nào. Vòng 07:08 ngày 10/08/2026 báo "đã đúng tab" mà
    // thật ra đang ở "Tất cả" (đọc 33 dòng toàn ĐƠN HỦY) — không có dòng này thì dương tính giả đi lọt im lặng.
    send({ action: "progress", message: 'tab loại đơn đang chọn: "' + (dangChon || "(không đọc được nhãn)") + '".' });
  } else {
    // Kèm CHẨN ĐOÁN: bỏ lượt check là mất dữ liệu thật (mốc giữ nguyên, mã mới không được ghi), nên câu báo
    // phải nói được VÌ SAO — Shopee đổi nhãn tab hay đổi khung, hay tab không render. Không có nó thì mỗi lần
    // Shopee đổi giao diện là một vòng đoán mò như đợt 10/08/2026.
    let cdTab = "";
    try { cdTab = (await execInTab(tabId, pageChanDoanTabTraHang, [MAX_RETURN_HEAD_HTML])) || ""; } catch (e) { cdTab = ""; }
    send({
      action: "progress",
      message: "KHÔNG xác nhận được tab 'Đơn Trả hàng Hoàn tiền' đang chọn — C# sẽ bỏ lượt check (mốc giữ nguyên)."
        + (cdTab ? " Tab thấy trên trang: " + cdTab : ""),
    });
  }

  // 4) Đổi sắp xếp — áp SAU khi đổi tab (đổi tab nhiều khả năng reset sắp xếp về mặc định "Ngày đến hạn").
  // Không thấy nút/mục → VẪN đọc tiếp nhưng sortApplied=false để C# log cảnh báo.
  let sortApplied = false;
  // Có LÚC NÀO menu bung ra không (dù mục cần bấm không khớp / bung rồi tự đóng) — tách hẳn hai ca "cú bấm
  // không mở được dropdown" với "dropdown mở được nhưng không tìm ra mục", mà chẩn đoán hậu-kỳ không tách nổi.
  let menuTungMo = false;
  // Lỗi NÉM RA giữa chừng, nếu có. PHẢI lộ vào câu báo: ca thật 10/08/2026 có 6/17 lượt "hỏng sắp xếp" mà thân
  // try ném ngay từ dòng đầu (execInTab lỗi vì tab bị vứt khỏi bộ nhớ) — CHƯA HỀ bấm phát nào, mà nhật ký in ra
  // y hệt lượt bấm thật ⇒ cả ngày đi soi DOM trong khi thủ phạm nằm ở cầu nối.
  let loiSapXep = "";
  const nhatKyBam = [];       // "k/N tai(x,y)" của TỪNG lượt bấm THẬT — để lần sau đếm được bằng nhật ký.
  const chanDoanDiemBam = []; // ảnh chụp điểm bấm NGAY TRƯỚC mỗi cú bấm; chỉ gửi đi khi lượt sắp xếp HỎNG.
  let hetTranSapXep = false;  // dừng vì chạm TRAN_THOI_GIAN_SAP_XEP_MS chứ không phải vì hết lượt — phải phân biệt.
  try {
    await ensureDbg(tabId);
    // Lượt 1 CHỈ để cuộn nút vào giữa màn; đo toạ độ thật ở lượt 2 sau khi layout đã lắng. Đo ngay sau khi cuộn
    // là cách chắc chắn nhất để bấm trượt (trang cuộn mượt / danh sách vẽ lại đẩy nút đi chỗ khác).
    await execInTab(tabId, pageLocateSortButton, [0]);
    await sleep(400);
    // Trang thật chỉ có 1 ứng viên .sort-button, nhưng vẫn giữ đường thử ứng viên khác khi có nhiều nút (ưu tiên
    // nút trong ô tổng ".return-list-summary"): KẸP chỉ số vào [0, tong-1] để lượt thử lại luôn bấm được một nút
    // THẬT thay vì thoát non như bản trước.
    let tongUngVien = 1;
    const hanSapXep = Date.now() + TRAN_THOI_GIAN_SAP_XEP_MS;
    for (let lan = 0; lan < SO_LAN_MO_SAP_XEP && !sortApplied; lan++) {
      if (Date.now() >= hanSapXep) { hetTranSapXep = true; break; }
      if (lan > 0) { await sleep(NGHI_GIUA_HAI_LUOT_SAP_XEP_MS); }
      // Menu SẮP XẾP đang mở sẵn thì ĐỪNG bấm nữa — cú bấm lúc này là tự tay đóng đúng cái vừa mở (dropdown là
      // nút bật/tắt). Bẫy do chính lượt thử lại đẻ ra, phải chặn ngay tại đây. Chỉ đếm menu sắp xếp: popper KHÁC
      // đang mở không liên quan gì tới cú bấm này (xem pageDemMenuHien).
      let dem = null;
      try { dem = await execInTab(tabId, pageDemMenuHien, [SORT_MENU_MARK_RE]); } catch (e) { dem = null; }
      if (!dem || (dem.sapXep || 0) === 0) {
        const viTri = Math.min(lan, Math.max(0, tongUngVien - 1));
        let btn = await execInTab(tabId, pageLocateSortButton, [viTri]);
        if (!btn) break; // trang KHÔNG còn nút .sort-button nào hiện hình → bấm lại cũng vô ích (pageChanDoanSapXep sẽ nói rõ)
        tongUngVien = btn.tong || tongUngVien;

        // ĐIỂM SẮP BẤM có bị lớp phủ đè không — DÙNG LẠI đúng cơ chế của cú bấm tab (choHetLopPhu), đừng đẻ
        // đường mới. Ca 19:21 ngày 10/08/2026: tour `.on-boarding` bật giữa lượt (lượt dọn modal đầu vòng đã ném
        // lỗi vì tab mất truy cập, nên tour ở lại suốt lượt) — dải tour đẩy nút sắp xếp xuống đúng 40px và lớp
        // `.on-boarding-highlight` nuốt cú bấm. Che thì DỌN LẠI rồi bấm tiếp, KHÔNG bỏ cuộc.
        let phu = await choHetLopPhu(tabId, btn.x, btn.y);
        if (phu) {
          send({ action: "progress", message: "điểm bấm nút sắp xếp đang bị che bởi: " + phu + " — dọn lại modal/tour rồi bấm tiếp." });
          // dongModalChan xử luôn tour `.on-boarding`: nhãn "Đã hiểu" nằm sẵn trong bộ NHAN_DONG của
          // pageLocateBlockingModalButton, và nó lặp tới 6 lượt cho tour nhiều bước. Hàm này KHÔNG ném.
          await dongModalChan(tabId, "");
          await ensureDbg(tabId); // trang vẽ lại sau khi tour tắt có thể làm debugger tự detach
          // ĐO LẠI toạ độ: gỡ dải tour xong là nội dung dịch lên, toạ độ vừa đo đã cũ (đúng 40px lệch đã đo được).
          const btn2 = await execInTab(tabId, pageLocateSortButton, [Math.min(lan, Math.max(0, tongUngVien - 1))]);
          if (btn2) { btn = btn2; tongUngVien = btn2.tong || tongUngVien; }
          phu = await choHetLopPhu(tabId, btn.x, btn.y);
          if (phu) { send({ action: "progress", message: "vẫn còn lớp phủ ở điểm bấm nút sắp xếp sau lượt dọn: " + phu }); }
        }

        // Ảnh chụp điểm bấm NGAY TRƯỚC cú bấm — thứ duy nhất phân biệt được "bị che" / "lệch hệ toạ độ" / "nút
        // bị khoá". Gom lại chứ KHÔNG gửi ngay: lượt thành công thì chẳng ai cần, mà gửi mỗi shop mỗi lượt là
        // ngập nhật ký. Chỉ đổ ra ở câu báo hỏng bên dưới (cùng lối với mọi chẩn đoán khác trong file này).
        let cdBam = "";
        try { cdBam = (await execInTab(tabId, pageChanDoanDiemBam, [btn.x, btn.y, Math.min(lan, Math.max(0, tongUngVien - 1))])) || ""; } catch (e) { cdBam = ""; }
        if (cdBam) { chanDoanDiemBam.push("lượt " + (lan + 1) + " → " + cdBam); }

        await trustedClick(tabId, btn.x, btn.y);
        nhatKyBam.push((lan + 1) + "/" + SO_LAN_MO_SAP_XEP + " tai(" + btn.x + "," + btn.y + ")");
      }
      let opt = null;
      const odl = Date.now() + CHO_MENU_SAP_XEP_MS;
      // Dò DÀY ngay sau cú bấm (150ms) rồi thưa dần: menu có thể mở rồi tự đóng, mà nhịp 400ms như cũ thì bỏ
      // lọt hẳn khoảng mở đó và chẩn đoán hậu-kỳ chỉ thấy "không có menu nào hiện" — đúng ca đã đánh lừa hai vòng.
      let cho = 150;
      while (Date.now() < odl) {
        await sleep(cho);
        cho = Math.min(400, cho + 100);
        try {
          const d2 = await execInTab(tabId, pageDemMenuHien, [SORT_MENU_MARK_RE]);
          if (d2 && (d2.sapXep || 0) > 0) { menuTungMo = true; }
        } catch (e) {}
        opt = await execInTab(tabId, pageLocateSortOption, [SORT_NEWEST_RE]);
        if (opt) break;
      }
      if (opt) {
        await trustedClick(tabId, opt.x, opt.y);
        sortApplied = true;
        await sleep(1500); // danh sách vẽ lại theo thứ tự mới
      }
    }
  } catch (e) {
    // KHÔNG nuốt câm nữa — xem chú thích của `loiSapXep`. Vẫn đi tiếp (đọc theo thứ tự đang có, sortApplied=false).
    loiSapXep = String((e && e.message) || e);
  }
  if (!sortApplied) {
    // Regex SORT_NEWEST_RE đã kiểm khớp ĐÚNG nhãn thật "Ngày yêu cầu (Mới - Cũ)" (10/08/2026) ⇒ lỗi KHÔNG ở
    // luật khớp text. Còn lại hai khả năng: bấm nút mà dropdown không mở, hoặc menu nằm chỗ khác. Chẩn đoán
    // phân biệt được hai cái đó ngay ở lượt chạy kế, khỏi đoán thêm vòng nào nữa.
    let cdSort = "";
    try { cdSort = (await execInTab(tabId, pageChanDoanSapXep, [])) || ""; } catch (e) { cdSort = ""; }
    send({
      action: "progress",
      message: "KHÔNG đổi được sắp xếp 'Ngày yêu cầu (Mới - Cũ)' — đọc theo thứ tự đang có."
        + " menu-tung-mo=" + (menuTungMo ? "CO" : "KHONG")
        // ĐẾM ĐƯỢC BẰNG NHẬT KÝ: có bấm hay không, bấm mấy lượt, bấm vào đâu. Không có dòng này thì lượt "ném
        // ngay dòng đầu" và lượt "bấm 3 phát đều trượt" in ra y hệt nhau.
        + " · lượt bấm: " + (nhatKyBam.length ? nhatKyBam.join(", ") : "KHONG bam duoc lan nao")
        + (hetTranSapXep ? " · DỪNG vì chạm trần " + TRAN_THOI_GIAN_SAP_XEP_MS + "ms của bước sắp xếp" : "")
        + (loiSapXep ? " · LỖI: " + loiSapXep : "")
        + (chanDoanDiemBam.length ? " · Điểm bấm: " + chanDoanDiemBam.join(" | ") : "")
        + (cdSort ? " · Chẩn đoán: " + cdSort : ""),
    });
  }

  // 5) Đọc lại ô tổng (sau khi danh sách vẽ lại) + quét dòng TRANG ĐẦU.
  try { summary = (await execInTab(tabId, pageReturnSummaryText, [])) || summary; } catch (e) {}
  const rdl = Date.now() + 8000;
  while (Date.now() < rdl) {
    let n = 0;
    try { n = (await execInTab(tabId, pageReturnRowCount, [])) || 0; } catch (e) { n = 0; }
    if (n > 0) break;
    await sleep(500);
  }
  const list = await quetTrangHienTai(tabId, 0);

  // Ô TỔNG nói CÓ mà quét ra 0 DÒNG = hỏng THẬT (selector dòng / khối đầu dòng đổi), không phải shop sạch. Thu
  // chẩn đoán trang gửi kèm để C# in ra ngay — CHỈ ở đúng ca này, lượt bình thường không tốn thêm execInTab nào.
  let cdTrong = null;
  const soOTong = parseInt((String(summary).match(/[\d.,]+/) || ["0"])[0].replace(/[.,\s]/g, ""), 10) || 0;
  if (list.length === 0 && soOTong > 0) {
    try { cdTrong = await execInTab(tabId, pageChanDoanTraHang, []); } catch (e) { cdTrong = null; }
  }

  // Lượt này CHỈ trang đầu. C# đọc ô tổng rồi so với mốc mới biết cần lật mấy trang (luật SoTrangCanDoc) — nên
  // phần sâu đi bằng lệnh THỨ HAI `readReturnRequestsMore` trên chính trang đang mở, KHÔNG mở lại trang lần nữa.
  // Cố ý không đoán trước độ sâu ở đây: đoán thừa thì mọi shop mỗi vòng đều lật trang vô ích.
  const coTrangSauKq = await coTrangSau(tabId);

  // Chẩn đoán pager phải bắn Ở ĐÂY chứ không chỉ trong latTrang (T11, review 11/08): selector nút "trang sau"
  // hỏng ⇒ coTrangSau=false ⇒ C# không bao giờ gửi nhịp 2 ⇒ khối chẩn đoán trong latTrang không bao giờ chạy —
  // đúng ca nó sinh ra để phục vụ. Dấu hiệu: ô tổng nói NHIỀU hơn số dòng trang đầu mà lại "hết trang". Shop
  // một-trang (ô tổng ≤ số dòng đọc được) không nổ, không tốn execInTab nào thêm ở lượt thường.
  if (list.length > 0 && soOTong > list.length && !coTrangSauKq) {
    let cdPager = "";
    try { cdPager = (await execInTab(tabId, pageChanDoanPagerTraHang, [MAX_RETURN_HEAD_HTML])) || ""; } catch (e) { cdPager = ""; }
    send({
      action: "progress",
      message: "⚠ Trả hàng: ô tổng nói " + soOTong + " yêu cầu mà trang đầu chỉ đọc được " + list.length
        + " dòng và KHÔNG thấy nút 'trang sau' — selector phân trang có thể đã đổi, phần sâu sẽ bị bỏ sót."
        + (cdPager ? " Khối phân trang: " + cdPager : ""),
    });
  }

  traVe(summary, sortApplied, tabTraHang, list, cdTrong, {
    soTrangDaDoc: 1,
    coTrangSau: coTrangSauKq,
  });
}

// Lượt ĐỌC THÊM (bước 2 của check trả hàng): C# đã biết số yêu cầu + mốc cũ nên tự tính được cần lật mấy trang,
// gửi `readReturnRequestsMore {maxPages}`. Trang trả hàng ĐANG MỞ sẵn (đúng tab, đúng sắp xếp) từ lượt trước —
// hàm này KHÔNG điều hướng, KHÔNG chọn lại tab, KHÔNG đổi lại sắp xếp: chỉ lật trang và quét.
// Trả về CÙNG khuôn `pageData kind:"returns"` (C# chỉ dùng phần `list`).
export async function doReadReturnRequestsMore(maxPages) {
  const tabId = orderTabIdStrict();
  const traVe = (list, them) => send({
    action: "pageData",
    kind: "returns",
    data: JSON.stringify(Object.assign({
      soYeuCauText: "", sortApplied: true, tabTraHang: true, list: list || [], chanDoan: null,
    }, them || {})),
  });
  // Mất tab shop giữa chừng ⇒ BÁO LỖI, đừng trả danh sách RỖNG: rỗng nhìn y hệt "lật trượt", mà lượt này chưa
  // hề đọc gì trên tab shop nào cả.
  if (tabId == null) { send({ action: "error", message: "đọc thêm trang trả hàng: " + LOI_MAT_TAB_SHOP }); return; }

  // Rơi /verify giữa chừng → báo captcha rồi thôi (C# coi như bỏ phần đọc thêm, phần trang đầu vẫn giữ).
  let url = "";
  try { url = (await chrome.tabs.get(tabId)).url || ""; } catch (e) {}
  if (/\/verify/i.test(url)) { send({ action: "captcha", message: url }); return; }

  const list = [];
  const kq = await latTrang(tabId, list, maxPages);
  if (kq.pagerChanDoan) {
    send({ action: "progress", message: "Trả hàng: không thấy nút 'trang sau' — khối phân trang: " + kq.pagerChanDoan });
  }
  traVe(list, { soTrangDaDoc: kq.soTrangLat, coTrangSau: kq.coTrangSau });
}
