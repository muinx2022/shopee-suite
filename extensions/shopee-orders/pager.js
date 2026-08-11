// Tìm nút "trang sau" theo HAI NHỊP — dùng CHUNG cho trang đơn (flow-orders) và trang trả hàng (flow-returns),
// vì cả hai chạy trên cùng bộ pager EDS và cùng dính đúng một cái bẫy.
//
// Vì sao hai nhịp: `pageFindNextPage` phải CUỘN nút vào giữa màn rồi mới đo được toạ độ, mà pager LUÔN nằm đáy
// trang nên lần nào cũng phải cuộn thật. Cuộn xong đo NGAY thì toạ độ có thể còn là của khung hình cũ (trang
// cuộn mượt / danh sách vẽ lại đẩy nút đi chỗ khác) ⇒ cú bấm rơi xuống chỗ trống, danh sách không đổi, caller
// tưởng "hết trang" và dừng — mất sạch đơn từ trang 2 trở đi mà không một dòng log. Đây là ĐÚNG khuôn đã kiểm
// chứng cho nút sắp xếp trang trả hàng (nhịp 1 cuộn → nghỉ → nhịp 2 lấy toạ độ thật).
import { execInTab } from "./exec.js";
import { pageFindNextPage } from "./page-funcs.js";
import { sleep } from "./shared/util.js";

/// Nghỉ giữa NHỊP 1 (cuộn) và NHỊP 2 (đo toạ độ thật). 400ms = đúng con số đang dùng cho nút sắp xếp
/// (flow-returns): đủ cho một nhịp cuộn mượt + một nhịp vẽ lại của Vue, mà nhân với ≤10 trang vẫn không đáng kể
/// so với hạn chặng.
export const NGHI_DO_LAI_PAGER_MS = 400;

/// Số lượt BẤM LẠI khi lật trang trượt (danh sách không đổi sau cú bấm đầu). Đúng MỘT: cú thứ hai cứu được ca
/// "toạ độ vừa cũ đi vì layout nhảy", còn bấm mãi thì hoặc là thật sự hết trang, hoặc trang đang hỏng — cả hai
/// đều phải BÁO ra ngoài chứ không phải thử tiếp.
export const SO_LAN_BAM_LAI_PAGER = 1;

/// Trạng thái chữ ký danh sách NGAY TRƯỚC cú bấm lại — chốt chặn bắt buộc trước khi được phép bấm nữa:
///   "doi"     = chữ ký đã KHÁC bản chụp trước cú bấm đầu ⇒ cú bấm đầu ĐÃ ăn, trang đổi xong SAU hạn chờ
///               (fetch chậm >10s trông y hệt bấm trượt). Bấm nữa lúc này là lật THÊM một trang: trang vừa
///               mở không ai quét — đổi "mất phần đuôi" lấy "mất một trang giữa", còn tệ hơn vì caller
///               tưởng đã lật đúng một trang rồi chốt mốc/tắt cờ còn-sót.
///   "dangTai" = "0|…" (khung xương đang tải) hoặc không đọc được chữ ký ⇒ nhiều khả năng cú bấm đầu đã ăn
///               và danh sách ĐANG vẽ. Cấm bấm; caller chỉ nên CHỜ thêm một lượt rồi kết luận.
///   "chuaDoi" = vẫn y bản chụp cũ ⇒ cú bấm đầu trượt thật, được phép bấm lại.
/// KHÔNG bao giờ ném. `hamChuKy` là hàm page* đọc chữ ký của đúng danh sách đó (đơn: pageListSignature,
/// trả hàng: pageReturnListSignature) — truyền vào để ba caller không chép lệch luật này.
export async function trangThaiTruocBamLai(tabId, hamChuKy, sigTruoc) {
  let sig = "";
  try { sig = (await execInTab(tabId, hamChuKy, [])) || ""; } catch (e) { sig = ""; }
  if (!sig || sig.indexOf("0|") === 0) return "dangTai";
  return sig !== sigTruoc ? "doi" : "chuaDoi";
}

/// Toạ độ nút "trang sau" DÙNG ĐƯỢC, đo ở nhịp 2. null = không có nút (hết trang thật / pager đổi selector).
/// KHÔNG bao giờ ném — lật trang là phần mở rộng, hỏng thì caller vẫn còn dữ liệu trang hiện tại.
export async function timNutTrangSau(tabId) {
  let nhip1 = null;
  try { nhip1 = await execInTab(tabId, pageFindNextPage, []); } catch (e) { nhip1 = null; }
  if (!nhip1) return null;            // không có nút thì chẳng có gì để đo lại
  await sleep(NGHI_DO_LAI_PAGER_MS);
  let nhip2 = null;
  try { nhip2 = await execInTab(tabId, pageFindNextPage, []); } catch (e) { nhip2 = null; }
  // Nhịp 2 hụt (trang vừa vẽ lại, execInTab lỗi) → dùng toạ độ nhịp 1: bấm bằng toạ độ cũ vẫn còn cơ trúng, chắc
  // chắn hơn là bỏ luôn cả trang sau.
  return nhip2 || nhip1;
}
