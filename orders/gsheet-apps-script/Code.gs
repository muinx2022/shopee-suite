// ═══════════════════════════════════════════════════════════════════════════════════════════════════
//  Apps Script của Google Sheet "quản lý đơn" — nhận POST từ app Xử lý đơn Shopee.
//  Dán vào Tiện ích mở rộng → Apps Script, rồi Triển khai → Ứng dụng web.
//  BẢN SAO ĐỂ THAM CHIẾU: file thật nằm trên Google, sửa ở đó thì nhớ cập nhật lại file này.
//
//  Ghi ĐƠN vào TAB ĐÍCH (body.tab, mặc định "tháng 4") + lưu phiếu PDF vào Drive người triển khai.
//  Chống trùng theo mã đơn (cột A) trên MỌI tab. KHÔNG bao giờ ghi đè ô đã có (xem ghiNeuTrong) —
//  chỉ ĐIỀN vào ô đang trống, nên dữ liệu người dùng gõ tay luôn an toàn.
//
//  ─── SỬA 28/07/2026: vì sao phải viết lại ─────────────────────────────────────────────────────────
//  Bản cũ ghi bằng SỐ CỘT CỨNG (5, 7, 8, 9). Khi cột "Mã đơn trả hàng" được chèn vào E, mọi cột từ E
//  trở đi dịch sang phải một ô — dòng CŨ trôi theo nên vẫn đúng, nhưng script vẫn ghi vào cột cũ ⇒
//  MỌI DÒNG THÊM MỚI bị lệch trái đúng 1 cột, ÂM THẦM, không có lỗi nào báo ra.
//  Bản này tra cột THEO TÊN TIÊU ĐỀ (dòng 1) nên chèn/đổi chỗ cột bao nhiêu lần cũng không vỡ; tiêu đề
//  nào không tìm thấy thì BỎ TRỐNG + báo về trong `canhBao` chứ KHÔNG đoán cột (ghi sai cột còn tệ hơn
//  để trống). Đồng thời nhánh "đơn đã có" nay điền bổ sung ĐỦ mọi cột app quản lý, không chỉ B và C.
//
//  ─── THÊM 28/07/2026: ghi song song sang FILE PHỤ ─────────────────────────────────────────────────
//  Sau khi xong cả lô ở file chính, script ghi thêm A/B/C/E sang file "Quản Lý Đơn 2" (ID_FILE_PHU)
//  bằng openById — MỘT script, MỘT bản luật, dùng chung hàm với file chính (chép luật ra bản thứ hai
//  chính là cái đã gây lỗi lệch cột ở trên). Lỗi ở file phụ chỉ vào `filePhu.loi`, KHÔNG phá file chính.
//
//  ─── THÊM 29/07/2026: cờ `chiDienNeuCo` + MÀU CHỮ đánh dấu trả hàng ───────────────────────────────
//  Shopee cho trả hàng trong 15 ngày, mà app dọn đơn kết thúc ngay khi ghi sheet xong — nên mã trả hàng
//  thường về TỚI đây khi đơn đã biến mất khỏi máy. App nay giữ mã ở một bảng riêng và đẩy lên bằng
//  payload CHỈ-CÓ-MÃ-TRẢ: {maDon, donTraHang, chiDienNeuCo:true} — KHÔNG có `daHuy`.
//    · `chiDienNeuCo:true` → đơn không tra thấy mã đơn ở BẤT KỲ tab nào thì BỎ QUA, không append (cả hai
//      file). Thiếu cờ này thì nhánh append đẻ một dòng gần-như-rỗng cho mọi đơn chưa từng ghi sheet.
//    · KHÔNG có `daHuy` là CỐ Ý: `daHuy === false` ở dưới sẽ XOÁ nền đỏ — đẩy mã trả cho một đơn ĐÃ HỦY
//      mà kèm `daHuy:false` là xoá sạch dấu hủy ở cả hai file, im lặng.
//  Đồng thời: dòng VỪA nhận mã trả hàng được đánh dấu bằng MÀU CHỮ (MAU_CHU_TRA_HANG), một trục riêng
//  hoàn toàn với NỀN đỏ đơn hủy — dòng vừa hủy vừa có mã trả đọc được cả hai trạng thái.
//
//  ─── SỬA 09/08/2026: trạng thái ghi mã trả hàng báo THEO TỪNG DÒNG ────────────────────────────────
//  Trước bản này, client chỉ suy được "ghi được hay không" từ `canhBao` cấp PHẢN HỒI. Mà `thieuCot` gom
//  chung cho MỌI tab LẪN FILE PHỤ ⇒ file phụ thiếu tiêu đề "Mã đơn trả hàng" là cả lô mã ĐÃ ghi xong ở
//  file chính vẫn bị client coi là chưa đẩy, rồi đẩy lại mỗi chu kỳ suốt 14 ngày (đốt quota Apps Script,
//  badge "Chờ đẩy" báo sai). Nay mỗi dòng kết quả có thể mang `chuaGhiMaTra:true` + `lyDoChuaGhi`
//  ('thieucot' | 'congthuc'); ô ĐÃ đúng mã sẵn thì KHÔNG báo (coi như xong, kẻo đẩy lại vô tận).
//  FILE PHỤ cố ý KHÔNG bao giờ đặt cờ này: nó là bản sao, thiếu cột ở đó không có nghĩa file chính hỏng.
//
//  ─── SỬA 30/07/2026: mã trả hàng ĐỔI thì GHI ĐÈ ───────────────────────────────────────────────────
//  App reset cờ đã-đẩy khi Shopee tạo LẠI yêu cầu với mã khác — nhưng ghiNeuTrong chỉ ghi ô TRỐNG nên ô
//  còn mã CŨ không bao giờ nhận được mã mới, mà lượt đẩy vẫn trả ok:true ⇒ app đánh dấu "đã đẩy" và mã
//  mới mất IM LẶNG. Nay riêng cột "Mã đơn trả hàng" của payload chỉ-mã-trả (`chiDienNeuCo === true`)
//  dùng ghiDeNeuKhac: ghi đè khi giá trị KHÁC, bỏ qua ô có CÔNG THỨC. Cột này do máy ghi, người dùng
//  không gõ tay. Mọi cột khác + payload đơn thường GIỮ NGUYÊN luật "chỉ điền ô trống".
//  ⚠ TRIỂN KHAI: file này chỉ là BẢN SAO tham chiếu — phải tự dán lên script.google.com rồi Triển khai
//  lại Web App TRƯỚC khi phát hành bản client mới, nếu không mã đổi vẫn kẹt ở bản script cũ.
//
//  ─── SỬA 29/07/2026: file phụ cũng theo trạng thái HỦY ────────────────────────────────────────────
//  Bản trước ghi sang file phụ mọi đơn `ok`, không xét hủy — mà file phụ cố ý không tô màu nên đơn hủy
//  nằm đó trông y hệt đơn còn sống. Nay hai file CÙNG một trạng thái: hủy trước khi vào file phụ →
//  KHÔNG tạo dòng; đã có dòng rồi mà file chính báo hủy → TÔ ĐỎ dòng đó (không xóa dòng).
//
//  ─── THÊM 11/08/2026: ĐƯỜNG THỨ HAI — người dùng GÕ TAY D/E ở file chính → file phụ ───────────────
//  Tới đây script chỉ có MỘT đường vào là `doPost` (app đẩy lô đơn lên). Mà D "mã đơn đặt" thì app KHÔNG
//  bao giờ có (người dùng tự gõ), còn E "mã đơn trả hàng" người dùng cũng sửa tay ⇒ hai cột đó ở file phụ
//  vĩnh viễn trống/lệch. Nay thêm đường thứ hai: trigger "khi chỉnh sửa" (onEditDongBoFilePhu) chép ô vừa
//  gõ sang file phụ, KHỚP DÒNG THEO CỘT A (mã đơn) — xem khối ĐỒNG BỘ TAY ở cuối file.
//  ⚠ Phải là trigger CÀI ĐẶT, không phải `onEdit` đơn giản: simple trigger chạy KHÔNG có quyền uỷ nhiệm
//  nên `openById` ném lỗi, không mở nổi file phụ. Cài bằng menu "Đồng bộ file 2 → Bật đồng bộ".
// ═══════════════════════════════════════════════════════════════════════════════════════════════════

const TEN_THU_MUC_PHIEU = 'Phieu-don-Shopee';
const TEN_TAB_MAC_DINH = 'tháng 4';
const TEN_TAB_MAU = 'tháng 4';   // tab dùng làm MẪU cấu trúc khi tự tạo tab tháng mới
const SO_DONG_TIEU_DE = 1;       // dòng tiêu đề ở đầu tab (giữ khi nhân bản; cũng là dòng để tra tên cột)
const MAU_DO_HUY = '#ea9998'; // nền đỏ đơn hủy — CỐ Ý lệch 1 so với "light red 2" (#ea9999) của bảng màu
                              // chuẩn để không xóa nhầm nền đỏ người dùng tự tô
// MÀU CHỮ (font color) đánh dấu dòng CÓ MÃ TRẢ HÀNG — trục HOÀN TOÀN KHÁC với NỀN đỏ của đơn hủy ở trên.
// Hai trục tách nhau nên không giẫm chân: dòng vừa hủy vừa có mã trả đọc được CẢ HAI trạng thái cùng lúc.
// MỘT CHIỀU: chỉ SET khi vừa ghi được mã, KHÔNG bao giờ trả màu chữ về mặc định (mã trả hàng không biến mất,
// mà đặt-lại là đụng vào thứ người dùng có thể đã tự chỉnh).
const MAU_CHU_TRA_HANG = '#C05621';
const SO_COT_TO_MAU = 11;     // tô nền từ cột A tới K (đã kẹp theo lưới thật của tab)
const SO_COT_TO_MAU_PHU = 5;  // file phụ CHỈ có A–E, không phải 11 như file chính. Dùng nhầm SO_COT_TO_MAU
                              // ở đây thì getRange vượt lưới sẽ NÉM, mà cả khối phụ nằm trong MỘT
                              // try/catch ⇒ một đơn hủy nuốt luôn các đơn còn lại của cả lô.

// Các cột đã được người dùng chốt theo VỊ TRÍ, không phụ thuộc chữ ở dòng tiêu đề.
const COT_MA_DON = 1;
const COT_TEN_SHOP = 6;   // F — Shop
const COT_SKU = 10;       // J — SKU
const COT_PHAN_LOAI = 11; // K — Phân Loại Đơn Hàng

// ─── BẢN ĐỒ TRƯỜNG → TÊN TIÊU ĐỀ (chữ thường) ────────────────────────────────────────────────────
// ĐÂY LÀ CHỖ DUY NHẤT cần sửa khi muốn đổi cột đích. Tên viết thường, khoảng trắng gộp lại — so khớp
// qua chuanHoa() nên hoa/thường và khoảng trắng thừa trong tiêu đề đều không ảnh hưởng.
//
// Shop (F), SKU (J) và Phân Loại Đơn Hàng (K) dùng số cột cố định ở trên, nên không nằm trong bản đồ này.
const COT = {
  maVanDon:   'mã vận đơn gửi',
  fileUrl:    'ảnh mã vận đơn gửi',
  donTraHang: 'mã đơn trả hàng',
  doanhThu:   'tiền bán',
  ngay:       'ngày đặt',
};

// Tiêu đề dựng cho tab mới khi KHÔNG tìm thấy tab mẫu — đúng layout A–K hiện tại.
const TIEU_DE_MAC_DINH = [
  'Mã Đơn Gửi', 'mã vận đơn gửi', 'ảnh mã vận đơn gửi', 'mã đơn đặt', 'Mã Đơn Trả Hàng', 'Shop',
  'tiền đặt', 'tiền bán', 'ngày đặt', 'SKU', 'Phân Loại Đơn Hàng'
];

// ─── FILE PHỤ "Quản Lý Đơn 2" (chỉ cột A–E) ──────────────────────────────────────────────────────
// Ghi song song 4 trường A/B/C/E; D "mã đơn đặt" người dùng tự điền nên script KHÔNG đụng tới.
// NGUỒN THẬT của ID là CẤU HÌNH PHÍA APP: client gửi lên `body.sheet2` (URL đầy đủ hoặc ID trần).
// Hằng dưới đây chỉ là DỰ PHÒNG khi client chưa gửi `sheet2` (bản app cũ). `sheet2` là chuỗi RỖNG
// tường minh = TẮT ghi file phụ.
// ⚠ Tài khoản triển khai Web App phải có QUYỀN SỬA file phụ, nếu không openById ném lỗi và mọi lượt
// đẩy rơi vào `filePhu.loi` (file chính vẫn ghi đủ).
const ID_FILE_PHU = '1CK-mu-rtLw0QnGDZ2cuEIkRelEnZkNWuB7Ir_ZuRLhk';
const TEN_TAB_MAU_PHU = 'Trang tính1';   // tab mẫu của file phụ (chỉ có dòng tiêu đề)
const TIEU_DE_MAC_DINH_PHU = [
  'Mã Đơn Hàng gửi', 'mã vận đơn gửi', 'ảnh mã vận đơn gửi', 'mã đơn đặt', 'Mã đơn trả hàng'
];

// ─── ĐỒNG BỘ TAY (11/08/2026): cột nào người dùng gõ ở file chính thì chép sang file phụ ─────────
// Tra THEO TIÊU ĐỀ chứ KHÔNG theo số cột D/E — chèn một cột vào giữa là mọi số cột cứng lệch ÂM THẦM,
// đúng cái đã mất mấy ngày dữ liệu hồi 28/07 (xem đầu file). Hôm nay hai tiêu đề này nằm ở D và E.
// Tên viết ở dạng ĐÃ CHUẨN HÓA (thường, một khoảng trắng) vì so khớp với đầu ra của chuanHoa().
// Muốn đồng bộ thêm cột nào thì thêm TÊN TIÊU ĐỀ vào đây — cột đó phải có mặt ở CẢ HAI file.
const COT_DONG_BO_TAY = ['mã đơn đặt', 'mã đơn trả hàng'];
// Trần số ô xét trong MỘT lượt sửa (dán cả cột thì e.range có thể trải hàng nghìn dòng). Vượt trần thì
// phần dư được ĐẾM và BÁO RA toast — cắt lặng thì người dùng tưởng đã sang đủ.
const TRAN_O_DONG_BO = 500;
// Trần giờ cho lượt chạy lại toàn bộ: Apps Script cắt ở ~6 phút, dừng sớm để còn kịp BÁO đã tới đâu.
const HAN_CHAY_LAI_MS = 4.5 * 60 * 1000;
// ID file phụ mà doPost dùng gần nhất — trigger đọc lại để ghi ĐÚNG file app đang ghi (app đổi cấu hình
// `sheet2` thì trigger đi theo, không kẹt ở hằng ID_FILE_PHU rồi ghi nhầm sang file cũ).
const KHOA_ID_FILE_PHU = 'ID_FILE_PHU_DANG_DUNG';
const TEN_HAM_TRIGGER_DONG_BO = 'onEditDongBoFilePhu';

function doPost(e) {
  const lock = LockService.getScriptLock();
  lock.waitLock(30000); // nhiều tài khoản sync cùng lúc → xếp hàng, tránh 2 lượt cùng chèn 1 dòng
  try {
    const body = JSON.parse(e.postData.contents);
    const ss = SpreadsheetApp.getActiveSpreadsheet();
    const tenTabGoc = body.tab || TEN_TAB_MAC_DINH;
    // Tab tháng chưa có → TỰ TẠO theo cấu trúc tab mẫu. Được LockService bọc nên 2 lượt POST cùng
    // tháng không tạo trùng.
    const tabDich = timTab(ss, tenTabGoc) || taoTabTheoThang(ss, tenTabGoc);

    // Bản đồ mã đơn (cột A) -> {tab, dòng} trên MỌI tab — ưu tiên tab đích trước để điền bổ sung tại đó.
    const viTri = banDoMaDon(ss, tabDich);

    // Bản đồ tiêu đề → số cột, nhớ theo từng tab (một đơn có thể nằm ở tab tháng khác, tiêu đề tab đó
    // có thể khác tab đích) — đọc một lần mỗi tab cho đỡ tốn lượt gọi API.
    const layMap = taoLayMap();

    let folder = null;
    const results = [];
    const thieuCot = [];   // tiêu đề KHÔNG tìm thấy → báo về cho người gọi biết mà sửa sheet
    let boQuaKhongThay = 0; // đơn có cờ chiDienNeuCo mà không tra thấy dòng nào → BỎ, không append (xem dưới)
    for (const don of (body.orders || [])) {
      const r = { maDon: don.maDon, ok: false, added: false, fileUrl: null, error: null };
      try {
        const key = String(don.maDon).trim();
        let cho = viTri[key] || null;
        let fileUrl = null;
        if (don.fileBase64) {
          // Chỉ upload khi ô "ảnh mã vận đơn gửi" chưa có gì. Ô đó có thể chứa CÔNG THỨC hiển thị rỗng
          // (vd =IMAGE(...) → getValue()='') → vẫn coi là ĐÃ có link, không upload đè.
          let daCoLink = false;
          if (cho) {
            const cotAnh = layMap(cho.sh)[COT.fileUrl];
            if (cotAnh) {
              const oAnh = cho.sh.getRange(cho.row, cotAnh);
              daCoLink = String(oAnh.getValue()).trim() !== '' || oAnh.getFormula() !== '';
            }
          }
          if (!daCoLink) {
            if (!folder) folder = layThuMucPhieu();
            const blob = Utilities.newBlob(
              Utilities.base64Decode(don.fileBase64), 'application/pdf',
              don.fileName || (key + '.pdf'));
            const f = folder.createFile(blob);
            f.setSharing(DriveApp.Access.ANYONE_WITH_LINK, DriveApp.Permission.VIEW);
            fileUrl = 'https://drive.google.com/file/d/' + f.getId() + '/view?usp=sharing';
          }
        }
        // Payload CHỈ-CÓ-MÃ-TRẢ (chiDienNeuCo): mã yêu cầu trả hàng của đơn app ĐÃ DỌN khỏi máy. Dòng của đơn
        // đó thường vẫn còn trên sheet nên chỉ cần ĐIỀN vào ô "Mã đơn trả hàng" đang trống. KHÔNG tra thấy mã
        // đơn ở bất kỳ tab nào ⇒ BỎ QUA, TUYỆT ĐỐI không append: nhánh dưới sẽ đẻ một dòng gần như rỗng (chỉ mã
        // đơn + mã trả) cho mọi đơn chưa từng ghi sheet — vd đơn hủy-trước-khi-vào-pipeline mà app cố ý không ghi.
        // Trả ok:true (không phải lỗi — chỉ là "không có gì để làm") để client đánh dấu đã đẩy, khỏi thử lại vô
        // hạn mỗi lượt; số lần bỏ qua nằm ở `chiDienNeuCo.boQuaKhongThay` của phản hồi để còn soi.
        if (!cho && don.chiDienNeuCo === true) {
          boQuaKhongThay++;
          r.ok = true;
          r.boQua = true;
          results.push(r);
          continue;
        }
        if (!cho) {
          cho = { sh: tabDich, row: tabDich.getLastRow() + 1 };
          r.added = true;
          viTri[key] = cho;
        }
        // Lưới hết dòng (sheet mặc định 1000 dòng) → chèn thêm 100 dòng để ghi được.
        if (cho.row > cho.sh.getMaxRows()) {
          cho.sh.insertRowsAfter(cho.sh.getMaxRows(), 100);
        }

        // MỘT đường ghi DUY NHẤT cho cả đơn mới lẫn đơn đã có. Trước đây đơn đã có chỉ được điền B và C,
        // nên "Ước tính" về muộn / phân loại / mã trả hàng KHÔNG BAO GIỜ tới sheet. Gộp lại được vì
        // ghiNeuTrong chỉ ghi vào ô TRỐNG — dòng cũ không hề bị sửa.
        const map = layMap(cho.sh);
        ghiNeuTrong(cho.sh, cho.row, COT_MA_DON, don.maDon);          // A — Mã Đơn Gửi
        ghiTruong(cho.sh, map, cho.row, 'maVanDon',   don.maVanDon,   thieuCot);
        ghiTruong(cho.sh, map, cho.row, 'fileUrl',    fileUrl,        thieuCot);
        // Cột "Mã đơn trả hàng" do MÁY ghi (người dùng không gõ tay) → payload chỉ-mã-trả được GHI ĐÈ khi mã
        // KHÁC: yêu cầu bị tạo lại mang mã mới, app đã reset cờ để đẩy lại, ô còn mã cũ mà chỉ-ghi-ô-trống thì
        // mã mới không bao giờ tới nơi (mà lượt đẩy vẫn ok ⇒ hỏng IM LẶNG). Payload đơn thường giữ nguyên luật cũ.
        // Payload CHỈ-MÃ-TRẢ đi đường RIÊNG (ghiMaTraHang) để trả về trạng thái THEO TỪNG DÒNG. Trước 09/08 nó
        // dùng chung ghiTruong, mà ghiTruong chỉ trả true/false không phân biệt được "thiếu cột" với "ô đã đúng
        // mã rồi" — client buộc phải suy từ `canhBao` cấp PHẢN HỒI, tức cả lô. Mà `thieuCot` gom chung cho MỌI
        // tab LẪN file phụ ⇒ file phụ thiếu tiêu đề là cả lô mã đã ghi xong ở file chính vẫn bị coi là chưa đẩy
        // rồi đẩy lại mỗi chu kỳ suốt 14 ngày. Nay trạng thái nằm ở từng dòng, file phụ không làm ô nhiễm nữa.
        let vuaGhiMaTra = false;
        if (don.chiDienNeuCo === true) {
          const ketMaTra = ghiMaTraHang(cho.sh, map, cho.row, don.donTraHang, thieuCot);
          vuaGhiMaTra = ketMaTra === 'ghi';
          // 'trung' = ô ĐÃ đúng mã ⇒ coi như xong, KHÔNG báo chưa ghi (báo thì client đẩy lại vô tận).
          if (ketMaTra === 'thieucot' || ketMaTra === 'congthuc') {
            r.chuaGhiMaTra = true;
            r.lyDoChuaGhi = ketMaTra;
          }
        } else {
          vuaGhiMaTra = ghiTruong(cho.sh, map, cho.row, 'donTraHang', don.donTraHang, thieuCot);
        }
        ghiNeuTrong(cho.sh, cho.row, COT_TEN_SHOP, don.tenShop);      // F — Shop
        ghiTruong(cho.sh, map, cho.row, 'doanhThu',   don.doanhThu,   thieuCot);
        ghiTruong(cho.sh, map, cho.row, 'ngay',       don.ngay,       thieuCot);
        ghiNeuTrong(cho.sh, cho.row, COT_SKU, don.sku);               // J — SKU
        ghiNeuTrong(cho.sh, cho.row, COT_PHAN_LOAI, don.phanLoai);    // K — Phân Loại Đơn Hàng

        // Màu trạng thái: hủy → nền đỏ cả dòng; hết hủy (daHuy === false TƯỜNG MINH) → CHỈ xóa đúng màu
        // đỏ script đã tô (không đụng màu người dùng tự tô; thiếu field daHuy → không đụng màu).
        const vungDong = cho.sh.getRange(cho.row, 1, 1, Math.min(SO_COT_TO_MAU, cho.sh.getMaxColumns()));
        if (don.daHuy === true) {
          vungDong.setBackground(MAU_DO_HUY);
        } else if (don.daHuy === false && String(cho.sh.getRange(cho.row, 1).getBackground()).toLowerCase() === MAU_DO_HUY) {
          vungDong.setBackground(null);
        }

        // Dòng VỪA nhận mã trả hàng → đổi MÀU CHỮ cả dòng. Trục riêng, KHÔNG đụng setBackground: nền là của đơn
        // hủy / của người dùng. Chỉ khi THỰC SỰ vừa ghi được mã (ô đang trống + có giá trị), không phải mỗi lượt
        // đẩy cho mọi dòng. Dùng lại đúng dải A..min(SO_COT_TO_MAU, số cột thật) — bẫy vượt lưới vẫn còn nguyên.
        if (vuaGhiMaTra) {
          vungDong.setFontColor(MAU_CHU_TRA_HANG);
        }

        const cotAnh = map[COT.fileUrl];
        r.fileUrl = cotAnh ? (String(cho.sh.getRange(cho.row, cotAnh).getValue()).trim() || null) : null;
        r.ok = true;
      } catch (err) {
        r.error = String(err);
      }
      results.push(r);
    }

    // ─── GHI SONG SONG SANG FILE PHỤ ────────────────────────────────────────────────────────────
    // Chạy SAU khi cả lô đã xong ở file chính. Bọc try/catch RIÊNG: openById có thể ném (thiếu quyền
    // / ID sai / file bị xóa) và chuyện đó KHÔNG được phá kết quả file chính — results[] đã chốt.
    // ID lấy từ cấu hình app (body.sheet2), vắng field thì lùi về hằng, chuỗi rỗng = tắt.
    let idPhu = ID_FILE_PHU;
    let loiCauHinhPhu = null;
    if (typeof body.sheet2 === 'string') {
      const cauHinh = body.sheet2.trim();
      idPhu = cauHinh === '' ? '' : bocIdSheet(cauHinh);
      if (cauHinh !== '' && !idPhu) loiCauHinhPhu = 'Không đọc được ID sheet phụ từ cấu hình: ' + cauHinh;
    }
    // Ghi nhớ ID vừa chốt để TRIGGER ĐỒNG BỘ TAY dùng lại đúng file này (nó không có body.sheet2 để mà đọc).
    // Chuỗi rỗng = app đã TẮT file phụ ⇒ nhớ luôn cái rỗng, để trigger tắt theo. Lỗi cấu hình thì KHÔNG nhớ:
    // '' của nhánh lỗi mang nghĩa "không đọc nổi", khác hẳn "cố ý tắt" — nhớ nhầm là tắt luôn đồng bộ tay.
    if (!loiCauHinhPhu) nhoIdFilePhu(idPhu);
    const filePhu = { ghi: 0, them: 0, boQuaHuy: 0, boQuaKhongThay: 0, loi: loiCauHinhPhu };
    if (idPhu) {
      try {
        const ssPhu = SpreadsheetApp.openById(idPhu);   // MỘT lần cho cả lô, không phải mỗi đơn một lần
        const tabPhu = timTab(ssPhu, tenTabGoc)
          || taoTabTheoThang(ssPhu, tenTabGoc, TEN_TAB_MAU_PHU, TIEU_DE_MAC_DINH_PHU);
        // Chống trùng phải dò trên CHÍNH file phụ — hai file có tập dòng khác nhau, dùng lại `viTri`
        // của file chính là ghi nhầm dòng.
        const viTriPhu = banDoMaDon(ssPhu, tabPhu);
        const layMapPhu = taoLayMap();
        const dsDon = body.orders || [];
        for (let i = 0; i < dsDon.length; i++) {
          const don = dsDon[i];
          const r = results[i];
          if (!r || !r.ok) continue;   // đơn hỏng ở file chính → không đẩy sang file phụ
          const key = String(don.maDon).trim();
          let cho = viTriPhu[key] || null;
          if (!cho) {
            // Payload CHỈ-CÓ-MÃ-TRẢ không tra thấy dòng → BỎ ở ĐÂY NỮA. Guard này BẮT BUỘC phải có: ở file chính
            // đơn đó được trả ok:true, mà vòng dưới chỉ lọc theo `r.ok` — thiếu guard thì file phụ vẫn đẻ đúng
            // cái dòng gần-như-rỗng mà file chính vừa từ chối tạo.
            if (don.chiDienNeuCo === true) { filePhu.boQuaKhongThay++; continue; }
            // Đơn ĐÃ HỦY mà chưa từng có dòng ở file phụ → KHÔNG tạo dòng (y như file chính bỏ hẳn đơn
            // hủy chưa vào sổ). Phải xét TRƯỚC nhánh tạo dòng, xét sau là đã lỡ đẻ dòng rồi.
            // `daHuy` vắng (client đời cũ) → không chặn, giữ nguyên hành vi cũ.
            if (don.daHuy === true) { filePhu.boQuaHuy++; continue; }
            cho = { sh: tabPhu, row: tabPhu.getLastRow() + 1 };
            viTriPhu[key] = cho;
            filePhu.them++;
          }
          if (cho.row > cho.sh.getMaxRows()) {
            cho.sh.insertRowsAfter(cho.sh.getMaxRows(), 100);
          }
          // Đúng 4 trường; D "mã đơn đặt" của người dùng và màu dòng KHÔNG đụng tới. Vẫn là ghiNeuTrong
          // nên ô nào người dùng đã gõ thì giữ nguyên.
          // ⚠ C lấy từ r.fileUrl (link phiếu file chính vừa ghi) — TUYỆT ĐỐI không gọi lại nhánh
          // don.fileBase64 ở đây, kẻo Drive đẻ hai bản PDF mỗi đơn và link hai file trỏ hai nơi.
          const map = layMapPhu(cho.sh);
          ghiNeuTrong(cho.sh, cho.row, COT_MA_DON, don.maDon);              // A
          ghiTruong(cho.sh, map, cho.row, 'maVanDon',   don.maVanDon,   thieuCot);   // B
          ghiTruong(cho.sh, map, cho.row, 'fileUrl',    r.fileUrl,      thieuCot);   // C — DÙNG LẠI link
          const vuaGhiMaTraPhu = ghiTruong(                                          // E
            cho.sh, map, cho.row, 'donTraHang', don.donTraHang, thieuCot, don.chiDienNeuCo === true);

          // Đơn ĐÃ có dòng ở file phụ mà file chính đánh dấu hủy → file phụ đánh dấu hủy theo (TÔ ĐỎ,
          // KHÔNG xóa dòng: xóa làm lệch mọi công thức tham chiếu theo số dòng và mất cột D người dùng
          // gõ tay). Cùng một luật với file chính, chỉ khác số cột (A–E).
          const vungDongPhu = cho.sh.getRange(cho.row, 1, 1, Math.min(SO_COT_TO_MAU_PHU, cho.sh.getMaxColumns()));
          if (don.daHuy === true) {
            vungDongPhu.setBackground(MAU_DO_HUY);
          } else if (don.daHuy === false && String(cho.sh.getRange(cho.row, 1).getBackground()).toLowerCase() === MAU_DO_HUY) {
            vungDongPhu.setBackground(null);
          }
          // MÀU CHỮ trả hàng — cùng luật file chính, chỉ khác SỐ CỘT (A–E). Dùng SO_COT_TO_MAU (11) ở đây thì
          // getRange vượt lưới sẽ NÉM, mà cả khối phụ nằm trong MỘT try/catch ⇒ nuốt luôn các đơn còn lại của lô.
          if (vuaGhiMaTraPhu) {
            vungDongPhu.setFontColor(MAU_CHU_TRA_HANG);
          }
          filePhu.ghi++;
        }
      } catch (err) {
        filePhu.loi = String(err);
      }
    }

    const traVe = { results: results };
    if (idPhu || loiCauHinhPhu) traVe.filePhu = filePhu;
    // Số đơn CHỈ-CÓ-MÃ-TRẢ bị bỏ vì không tra thấy dòng nào — chỉ để soi (client không đọc). Số này lớn bất
    // thường = dòng của đơn chưa từng được ghi lên sheet, không phải script hỏng.
    if (boQuaKhongThay) traVe.chiDienNeuCo = { boQuaKhongThay: boQuaKhongThay };
    if (thieuCot.length) {
      // KHÔNG tìm thấy tiêu đề ⇒ giá trị đã bị BỎ, không ghi bừa vào cột khác. Báo ra để còn biết mà sửa
      // tiêu đề sheet — chính vì bản cũ hỏng ÂM THẦM mà không ai phát hiện suốt nhiều ngày.
      traVe.canhBao = 'Không tìm thấy cột theo tiêu đề: ' + thieuCot.join(', ')
        + '. Các giá trị này KHÔNG được ghi. Kiểm tra dòng tiêu đề của tab.';
    }
    return traJson(traVe);
  } finally {
    lock.releaseLock();
  }
}

// Tìm tab theo tên trong MỘT file: khớp đúng trước, rồi nới lỏng "tên tab CHỨA chuỗi cần tìm"
// (vd "Tháng 4 - 2026"). Không thấy → null (người gọi tự quyết định tạo mới hay không).
// Dùng CHUNG cho file chính lẫn file phụ — một luật tìm tab duy nhất.
function timTab(ss, tenTabGoc) {
  const tenTab = chuanHoa(tenTabGoc);
  for (const sh of ss.getSheets()) {
    if (chuanHoa(sh.getName()) === tenTab) return sh;
  }
  for (const sh of ss.getSheets()) {
    if (chuanHoa(sh.getName()).indexOf(tenTab) !== -1) return sh;
  }
  return null;
}

// Bản đồ mã đơn (cột A) -> {tab, dòng} trên MỌI tab của MỘT file — tab đích xếp trước để đơn trùng
// được điền bổ sung ngay tại tab đích. Quét một lần cho cả lô (mỗi lượt đọc là một lệnh gọi API).
// `tabDich` = null ⇒ không ưu tiên tab nào, quét theo thứ tự tab sẵn có (đường ĐỒNG BỘ TAY dùng khi file
// phụ chưa có tab trùng tên với tab đang sửa — vẫn phải dò được mã đơn ở các tab tháng khác).
function banDoMaDon(ss, tabDich) {
  const cacTab = tabDich
    ? [tabDich].concat(ss.getSheets().filter(function (s) { return s.getSheetId() !== tabDich.getSheetId(); }))
    : ss.getSheets();
  const viTri = {};
  for (const sh of cacTab) {
    const n = sh.getLastRow();
    if (n < 1) continue;
    const colA = sh.getRange(1, 1, n, 1).getValues();
    for (let i = 0; i < n; i++) {
      const v = String(colA[i][0]).trim();
      if (v && !viTri[v]) viTri[v] = { sh: sh, row: i + 1 };
    }
  }
  return viTri;
}

// Tạo hàm tra bản đồ tiêu đề→cột, có nhớ theo từng tab. Mỗi file dùng một cái riêng (số cột của tab
// bên file phụ khác hẳn file chính, mà getSheetId() hai file có thể trùng nhau).
function taoLayMap() {
  const mapTheoTab = {};
  return function (sh) {
    const id = sh.getSheetId();
    if (!(id in mapTheoTab)) mapTheoTab[id] = mapCot(sh);
    return mapTheoTab[id];
  };
}

// Bóc ID spreadsheet từ chuỗi cấu hình: URL đầy đủ (…/spreadsheets/d/<ID>/edit) hoặc ID trần.
// Không nhận ra dạng nào → '' (báo lỗi cấu hình chứ KHÔNG đoán bừa rồi ghi nhầm file).
function bocIdSheet(s) {
  const chuoi = String(s || '').trim();
  const m = chuoi.match(/\/spreadsheets\/d\/([a-zA-Z0-9_-]+)/);
  if (m) return m[1];
  return /^[a-zA-Z0-9_-]+$/.test(chuoi) ? chuoi : '';
}

// Bản đồ TÊN TIÊU ĐỀ (đã chuẩn hóa) → SỐ CỘT, đọc từ dòng 1 của tab. Tiêu đề rỗng (vd cột A) bị bỏ qua.
// Hai cột trùng tên → giữ cột TRÁI NHẤT (đầu tiên gặp).
function mapCot(sheet) {
  const map = {};
  const n = sheet.getLastColumn();
  if (n < 1) return map;
  const hdr = sheet.getRange(SO_DONG_TIEU_DE, 1, 1, n).getValues()[0];
  for (let i = 0; i < hdr.length; i++) {
    const ten = chuanHoa(hdr[i]);
    if (ten && !(ten in map)) map[ten] = i + 1;
  }
  return map;
}

// Ghi một TRƯỜNG vào cột tra theo tiêu đề. Không tìm thấy tiêu đề → KHÔNG ghi (thà để trống còn hơn ghi
// nhầm cột) và gom tên tiêu đề vào `thieu` để báo ngược về người gọi.
// TRẢ VỀ true khi THỰC SỰ ghi được (ô đổi giá trị) — nhánh mã trả hàng dùng để biết có nên đổi màu chữ dòng
// hay không, khỏi tô lại mỗi lượt đẩy cho mọi dòng.
// `choGhiDe` = true → dùng ghiDeNeuKhac (ô đã có giá trị KHÁC vẫn ghi đè). CHỈ dùng cho cột "Mã đơn trả hàng"
// của payload chỉ-mã-trả — xem ghiDeNeuKhac.
function ghiTruong(sheet, map, row, khoa, giaTri, thieu, choGhiDe) {
  if (giaTri === null || giaTri === undefined || giaTri === '') return false;
  const ten = COT[khoa];
  const col = ten ? map[ten] : null;
  if (!col) {
    if (ten && thieu.indexOf(ten) === -1) thieu.push(ten);
    return false;
  }
  return choGhiDe === true
    ? ghiDeNeuKhac(sheet, row, col, giaTri)
    : ghiNeuTrong(sheet, row, col, giaTri);
}

// Tạo tab tháng MỚI theo cấu trúc tab mẫu: NHÂN BẢN tab mẫu (giữ dòng tiêu đề + định dạng +
// độ rộng cột + freeze), rồi XÓA dữ liệu từ dòng (SO_DONG_TIEU_DE + 1) trở xuống (cả nền đỏ hủy cũ).
// ⚠ Tab mẫu phải có ĐỦ tiêu đề mới (Mã đơn trả hàng, Phân loại), nếu không tab tháng mới sẽ thiếu cột và
// script sẽ báo trong `canhBao` chứ không ghi bừa.
// `tenMau`/`tieuDeMacDinh` bỏ trống = tab mẫu của FILE CHÍNH; file phụ truyền TEN_TAB_MAU_PHU vào.
function taoTabTheoThang(ss, tenMoi, tenMau, tieuDeMacDinh) {
  const mauChuan = chuanHoa(tenMau || TEN_TAB_MAU);
  let mau = null;
  for (const sh of ss.getSheets()) { if (chuanHoa(sh.getName()) === mauChuan) { mau = sh; break; } }
  if (!mau) { // nới lỏng: tên tab mẫu CHỨA chuỗi
    for (const sh of ss.getSheets()) { if (chuanHoa(sh.getName()).indexOf(mauChuan) !== -1) { mau = sh; break; } }
  }

  if (mau) {
    const tab = mau.copyTo(ss);          // copyTo đặt bản sao ở CUỐI, tên "Bản sao của ..."
    tab.setName(tenMoi);
    // Xóa dữ liệu (nội dung + nền) từ dòng sau tiêu đề — GIỮ dòng tiêu đề + mọi định dạng/độ rộng cột.
    const maxR = tab.getMaxRows();
    if (maxR > SO_DONG_TIEU_DE) {
      tab.getRange(SO_DONG_TIEU_DE + 1, 1, maxR - SO_DONG_TIEU_DE, tab.getMaxColumns())
        .clearContent().setBackground(null);
    }
    return tab;
  }

  // Không có tab mẫu → sheet trống + dòng tiêu đề ĐẦY ĐỦ (khớp COT ở trên).
  const tieuDe = tieuDeMacDinh || TIEU_DE_MAC_DINH;
  const tab = ss.insertSheet(tenMoi);
  tab.getRange(1, 1, 1, tieuDe.length).setValues([tieuDe]);
  tab.setFrozenRows(1);
  return tab;
}

// Chỉ ghi khi ô đang trống — bảo vệ dữ liệu người dùng điền tay (mã đơn đặt, tiền đặt, tk đặt, ghi chú,
// CÔNG THỨC =IMAGE…). Ô CÓ dữ liệu khi giá trị hiển thị khác rỗng HOẶC chứa công thức (công thức có thể
// hiển thị rỗng, vd =IMAGE(...) → getValue() trả '' → KHÔNG được coi là trống).
// Trả về true khi đã ghi thật (ô trống + có giá trị), false khi bỏ qua — xem ghiTruong.
function ghiNeuTrong(sheet, row, col, giaTri) {
  if (giaTri === null || giaTri === undefined || giaTri === '') return false;
  const o = sheet.getRange(row, col);
  if (String(o.getValue()).trim() === '' && o.getFormula() === '') {
    o.setValue(giaTri);
    return true;
  }
  return false;
}

// Ghi ĐÈ khi giá trị KHÁC (ô trống cũng ghi) — CHỈ dùng cho cột "Mã đơn trả hàng" ở payload chỉ-mã-trả.
// Vì sao phải có: app RESET cờ đã-đẩy khi Shopee tạo LẠI yêu cầu với mã khác, để đẩy mã mới lên. Nếu ở đây
// vẫn là ghiNeuTrong thì ô đã có mã CŨ nên không bao giờ ghi được, mà lượt đẩy vẫn trả ok:true ⇒ app đánh dấu
// "đã đẩy" và mã mới mất im lặng.
// Ô có CÔNG THỨC thì KHÔNG đụng (người dùng có thể tự dựng =…) — cùng lằn ranh với ghiNeuTrong.
// Trả true khi thực sự đổi giá trị ô.
// Ghi cột "Mã đơn trả hàng" cho payload CHỈ-MÃ-TRẢ và nói RÕ kết cục của ĐÚNG dòng này:
//   'ghi'      — vừa ghi giá trị mới vào ô (⇒ đổi màu chữ dòng đó)
//   'trung'    — ô đã đúng mã sẵn ⇒ KHÔNG có gì để làm, coi như XONG (client đừng đẩy lại)
//   'thieucot' — tab này không có tiêu đề "Mã đơn trả hàng" ⇒ giá trị BỊ BỎ, client phải giữ lại thử sau
//   'congthuc' — ô đang chứa CÔNG THỨC nên script không đụng ⇒ cũng là chưa ghi được
// Vì sao tách khỏi ghiTruong/ghiDeNeuKhac: hai hàm đó trả true/false, mà 'trung' và 'thieucot' cùng ra false —
// gộp lại thì client không tài nào biết nên đánh dấu đã-đẩy hay giữ lại, và đó đúng là chỗ mã trả hàng bị nuốt.
function ghiMaTraHang(sheet, map, row, giaTri, thieu) {
  const ten = COT.donTraHang;
  const col = map[ten];
  if (!col) {
    if (thieu.indexOf(ten) === -1) thieu.push(ten);
    return 'thieucot';
  }
  const o = sheet.getRange(row, col);
  if (o.getFormula() !== '') return 'congthuc';
  if (String(o.getValue()).trim() === String(giaTri).trim()) return 'trung';
  o.setValue(giaTri);
  return 'ghi';
}

function ghiDeNeuKhac(sheet, row, col, giaTri) {
  if (giaTri === null || giaTri === undefined || giaTri === '') return false;
  const o = sheet.getRange(row, col);
  if (o.getFormula() !== '') return false;
  if (String(o.getValue()).trim() === String(giaTri).trim()) return false;
  o.setValue(giaTri);
  return true;
}

function chuanHoa(s) {
  return String(s || '').toLowerCase().replace(/\s+/g, ' ').trim();
}

function traJson(obj) {
  return ContentService.createTextOutput(JSON.stringify(obj)).setMimeType(ContentService.MimeType.JSON);
}

function layThuMucPhieu() {
  const it = DriveApp.getFoldersByName(TEN_THU_MUC_PHIEU);
  return it.hasNext() ? it.next() : DriveApp.createFolder(TEN_THU_MUC_PHIEU);
}

// ═══════════════════════════════════════════════════════════════════════════════════════════════════
//  ĐỒNG BỘ TAY (11/08/2026) — người dùng GÕ ô ở FILE CHÍNH → chép sang FILE PHỤ, khớp theo CỘT A
// ═══════════════════════════════════════════════════════════════════════════════════════════════════
//  Đường này HOÀN TOÀN TÁCH khỏi doPost: doPost là "app đẩy lô đơn lên", còn đây là "người vừa gõ một ô".
//  Chỉ dùng chung các hàm nền (chuanHoa, timTab, banDoMaDon, taoLayMap) — KHÔNG chép luật ra bản thứ hai.
//
//  Luật ghi ở đây KHÁC doPost một điểm CỐ Ý: doPost "chỉ điền ô trống" (bảo vệ thứ người dùng gõ tay), còn
//  đây CHÍNH LÀ người dùng đang gõ ⇒ GHI ĐÈ khi giá trị khác. Không đè thì sửa lại D ở file 1 mà file 2 giữ
//  giá trị cũ = lệch IM LẶNG, đúng lớp lỗi mà ghiDeNeuKhac đã phải sinh ra để vá hồi 30/07.
//
//  Bốn ranh giới không được vượt:
//   1. KHÔNG tra thấy mã đơn ở file phụ ⇒ BỎ QUA, TUYỆT ĐỐI không tạo dòng. Đơn ĐÃ HỦY cố ý không có dòng
//      bên đó (filePhu.boQuaHuy); đẻ dòng ở đây là phá đúng luật ấy và tạo dòng gần-như-rỗng.
//   2. Ô đích chứa CÔNG THỨC thì không đụng — cùng lằn ranh với ghiNeuTrong/ghiDeNeuKhac.
//   3. Cột tra THEO TIÊU ĐỀ ở CẢ HAI file, không theo số cột D/E (xem COT_DONG_BO_TAY).
//   4. Mọi ca "không làm được" (không thấy mã / thiếu cột / vướng công thức / vượt trần) phải BÁO RA toast.
//      Nuốt lặng thì người dùng đinh ninh file 2 đã khớp — mà đó mới là kiểu hỏng đắt nhất ở repo này.

// Menu tay. `onOpen` là simple trigger nên KHÔNG cần cài đặt gì, chạy được ngay sau khi dán code.
function onOpen() {
  try {
    SpreadsheetApp.getUi()
      .createMenu('Đồng bộ file 2')
      .addItem('Bật đồng bộ (cài trigger)', 'caiTriggerDongBo')
      .addItem('Đồng bộ lại toàn bộ', 'dongBoLaiToanBo')
      .addItem('Kiểm tra cấu hình', 'kiemTraDongBo')
      .addToUi();
  } catch (err) {
    // Không có UI (chạy từ trigger/API) — menu chỉ là tiện ích, không được để nó phá gì.
  }
}

// Cài trigger "khi chỉnh sửa" dạng CÀI ĐẶT cho chính file này.
// ⚠ Vì sao không dùng `onEdit(e)` simple trigger: simple trigger chạy KHÔNG có quyền uỷ nhiệm nên
// SpreadsheetApp.openById() ném lỗi ⇒ không mở nổi file phụ. Trigger cài đặt chạy dưới quyền NGƯỜI BẤM CÀI,
// nên người đó bắt buộc phải có quyền SỬA file phụ.
// Xoá trigger cũ cùng tên trước khi tạo: bấm menu hai lần mà không xoá thì mỗi lần sửa ô chạy đồng bộ 2 lượt.
function caiTriggerDongBo() {
  let daXoa = 0;
  for (const t of ScriptApp.getProjectTriggers()) {
    if (t.getHandlerFunction() === TEN_HAM_TRIGGER_DONG_BO) { ScriptApp.deleteTrigger(t); daXoa++; }
  }
  ScriptApp.newTrigger(TEN_HAM_TRIGGER_DONG_BO)
    .forSpreadsheet(SpreadsheetApp.getActiveSpreadsheet())
    .onEdit()
    .create();
  baoNguoiDung('Đã bật đồng bộ sang file 2.\n\n'
    + 'Từ giờ gõ vào cột ' + COT_DONG_BO_TAY.join(' / ') + ' ở file này là tự chép sang file 2 '
    + '(khớp dòng theo mã đơn ở cột A).\n\n'
    + (daXoa ? 'Đã gỡ ' + daXoa + ' trigger cũ trùng tên.\n\n' : '')
    + 'Dữ liệu đã gõ TRƯỚC lúc này không tự sang — chạy "Đồng bộ lại toàn bộ" một lần.');
}

// Trigger chính. Google gọi hàm này SAU MỖI lần người dùng sửa ô ở file chính (kể cả dán khối, kể cả xoá).
function onEditDongBoFilePhu(e) {
  if (!e || !e.range) {
    baoNguoiDung('Hàm này do trigger "khi chỉnh sửa" gọi — chạy tay từ trình soạn thảo thì không có ô nào '
      + 'để đồng bộ. Muốn chạy tay: menu "Đồng bộ file 2 → Đồng bộ lại toàn bộ".');
    return;
  }
  const sh = e.range.getSheet();
  const dongDau = e.range.getRow();
  const dongCuoi = dongDau + e.range.getNumRows() - 1;
  const cotDau = e.range.getColumn();
  const cotCuoi = cotDau + e.range.getNumColumns() - 1;

  // Cắt bỏ phần đè lên dòng tiêu đề (dán từ dòng 1 xuống là chuyện thường).
  const dongBatDau = Math.max(dongDau, SO_DONG_TIEU_DE + 1);
  if (dongBatDau > dongCuoi) return;

  // Vùng vừa sửa có chạm cột nào thuộc danh sách đồng bộ không? Tra bằng TIÊU ĐỀ của chính tab này —
  // đây cũng là bộ lọc rẻ nhất: sửa ô ở tab khác / cột khác thì thoát ngay, chưa đụng tới file phụ.
  const soCotThat = sh.getLastColumn();
  if (soCotThat < 1) return;
  const hdr = sh.getRange(SO_DONG_TIEU_DE, 1, 1, soCotThat).getValues()[0];
  const cotCanDongBo = [];
  for (let c = cotDau; c <= cotCuoi && c <= soCotThat; c++) {
    const ten = chuanHoa(hdr[c - 1]);
    if (ten && COT_DONG_BO_TAY.indexOf(ten) !== -1) cotCanDongBo.push({ col: c, ten: ten });
  }
  if (!cotCanDongBo.length) return;

  // Xếp hàng sau doPost: app có thể đang chèn dòng ở file phụ ngay lúc này, dò dòng giữa chừng là ghi nhầm.
  // Không giành được khoá thì BÁO, không âm thầm bỏ — người dùng còn biết đường gõ lại.
  const lock = LockService.getScriptLock();
  if (!lock.tryLock(30000)) {
    baoNhanh('Sheet đang bận (app đang đẩy dữ liệu lên) — ô vừa sửa CHƯA sang file 2. '
      + 'Gõ lại ô đó, hoặc chạy menu "Đồng bộ file 2 → Đồng bộ lại toàn bộ".');
    return;
  }
  try {
    const idPhu = layIdFilePhu();
    if (!idPhu) return;   // file phụ đang TẮT (app gửi sheet2 rỗng) — không phải lỗi, không làm phiền

    const soDong = dongCuoi - dongBatDau + 1;
    const khoa = sh.getRange(dongBatDau, COT_MA_DON, soDong, 1).getValues();

    const ssPhu = SpreadsheetApp.openById(idPhu);
    // Tab cùng tên được ưu tiên, nhưng mã đơn vẫn dò trên MỌI tab của file phụ: đơn tháng trước nằm ở tab
    // tháng trước, mà người dùng hoàn toàn có thể mở tab cũ ra gõ bổ sung.
    const viTriPhu = banDoMaDon(ssPhu, timTab(ssPhu, sh.getName()));
    const layMapPhu = taoLayMap();

    const dem = { ghi: 0, xoa: 0, trung: 0, congThuc: 0, khongThay: 0, thieuCot: 0, khongKhoa: 0, vuotTran: 0 };
    const maKhongThay = [];
    let daXet = 0;
    for (const ct of cotCanDongBo) {
      const giaTri = sh.getRange(dongBatDau, ct.col, soDong, 1).getValues();
      for (let i = 0; i < soDong; i++) {
        const key = String(khoa[i][0]).trim();
        if (!key) {
          // Gõ D/E vào dòng chưa có mã đơn ở cột A ⇒ không có gì để khớp. Chỉ tính khi ô thực sự có chữ,
          // kẻo dán một khối rỗng cũng kêu.
          if (String(giaTri[i][0]).trim() !== '') dem.khongKhoa++;
          continue;
        }
        if (daXet >= TRAN_O_DONG_BO) { dem.vuotTran++; continue; }
        daXet++;
        const cho = viTriPhu[key];
        if (!cho) {
          dem.khongThay++;
          if (maKhongThay.length < 3 && maKhongThay.indexOf(key) === -1) maKhongThay.push(key);
          continue;
        }
        const col = layMapPhu(cho.sh)[ct.ten];
        if (!col) { dem.thieuCot++; continue; }
        dem[datOFilePhu(cho.sh, cho.row, col, giaTri[i][0])]++;
      }
    }
    // Có vấn đề thì để toast lâu cho đọc kịp; chạy trót lọt thì nháy nhanh rồi biến — người nhập liệu gõ
    // liên tục hàng chục dòng, toast 10 giây mỗi ô là tra tấn.
    const coVanDe = dem.khongThay || dem.thieuCot || dem.congThuc || dem.khongKhoa || dem.vuotTran;
    baoNhanh(tomTatDongBo(dem, maKhongThay), coVanDe ? 10 : 4);
  } catch (err) {
    // openById ném (mất quyền / ID sai / file bị xoá) là ca người dùng PHẢI biết — im lặng ở đây thì file 2
    // lệch dần mà không ai hay.
    baoNhanh('LỖI đồng bộ sang file 2: ' + err);
  } finally {
    lock.releaseLock();
  }
}

// Đặt một ô ở FILE PHỤ theo luật ĐỒNG BỘ TAY. Trả về đúng tên bộ đếm trong `dem`:
//   'ghi'      — vừa ghi giá trị mới
//   'xoa'      — nguồn bị xoá trắng ⇒ xoá theo (xem ghi chú dưới)
//   'trung'    — đã đúng giá trị, không đụng
//   'congThuc' — ô đích đang có CÔNG THỨC ⇒ không đụng, coi như CHƯA đồng bộ được
// Vì sao XOÁ THEO: đồng bộ nửa vời (chỉ ghi, không xoá) đúng là kiểu lệch im lặng mà repo này bị nhiều lần —
// người dùng xoá nhầm ở file 1 rồi Ctrl+Z thì trigger bắn lại lượt nữa và file 2 tự lành, còn lệch thì không.
// (Lượt "Đồng bộ lại toàn bộ" thì NGƯỢC LẠI: quét hàng nghìn dòng cũ, ô trống ở đó gần như luôn là "chưa từng
// điền" chứ không phải "vừa cố ý xoá" — nên nó bỏ qua ô nguồn trống. Xem dongBoLaiToanBo.)
function datOFilePhu(sheet, row, col, giaTri) {
  const o = sheet.getRange(row, col);
  if (o.getFormula() !== '') return 'congThuc';
  const moi = (giaTri === null || giaTri === undefined) ? '' : String(giaTri).trim();
  if (String(o.getValue()).trim() === moi) return 'trung';
  if (moi === '') { o.clearContent(); return 'xoa'; }
  o.setValue(giaTri);
  return 'ghi';
}

// Quét NGƯỢC toàn bộ file chính, đẩy các cột đồng bộ sang file phụ. Dùng cho dữ liệu đã gõ TRƯỚC khi có
// trigger (trigger chỉ bắn lúc sửa, không hồi tố), và để vá lại sau những lượt sửa lỡ rơi vào ca "sheet bận".
// KHÔNG xoá ô nào ở file phụ: ô nguồn trống ở đây nghĩa là "chưa từng điền", không phải "vừa cố ý xoá".
function dongBoLaiToanBo() {
  const idPhu = layIdFilePhu();
  if (!idPhu) {
    baoNguoiDung('Đang TẮT ghi file 2 (cấu hình sheet phụ của app để trống) — không có gì để đồng bộ.');
    return;
  }
  const lock = LockService.getScriptLock();
  if (!lock.tryLock(60000)) {
    baoNguoiDung('Sheet đang bận (app đang đẩy dữ liệu lên). Đợi lượt đẩy xong rồi chạy lại.');
    return;
  }
  try {
    const ss = SpreadsheetApp.getActiveSpreadsheet();
    const ssPhu = SpreadsheetApp.openById(idPhu);
    const viTriPhu = banDoMaDon(ssPhu, null);
    const layMapPhu = taoLayMap();
    const dem = { ghi: 0, xoa: 0, trung: 0, congThuc: 0, khongThay: 0, thieuCot: 0, khongKhoa: 0, vuotTran: 0 };
    const maKhongThay = [];
    const batDau = new Date().getTime();
    let hetGio = false;

    for (const sh of ss.getSheets()) {
      if (hetGio) break;
      const soCotThat = sh.getLastColumn();
      const soDongThat = sh.getLastRow();
      if (soCotThat < 1 || soDongThat <= SO_DONG_TIEU_DE) continue;
      const hdr = sh.getRange(SO_DONG_TIEU_DE, 1, 1, soCotThat).getValues()[0];
      const cotCan = [];
      for (let c = 1; c <= soCotThat; c++) {
        const ten = chuanHoa(hdr[c - 1]);
        if (ten && COT_DONG_BO_TAY.indexOf(ten) !== -1) cotCan.push({ col: c, ten: ten });
      }
      if (!cotCan.length) continue;   // tab không phải bảng đơn (hoặc thiếu tiêu đề) → bỏ qua

      // Đọc cả vùng MỘT lần: mỗi getValues là một lệnh gọi API, đọc từng dòng thì hết hạn mức trước khi xong.
      const vung = sh.getRange(SO_DONG_TIEU_DE + 1, 1, soDongThat - SO_DONG_TIEU_DE, soCotThat).getValues();
      for (let i = 0; i < vung.length; i++) {
        if (new Date().getTime() - batDau > HAN_CHAY_LAI_MS) { hetGio = true; break; }
        const key = String(vung[i][COT_MA_DON - 1]).trim();
        if (!key) continue;
        const cho = viTriPhu[key];
        if (!cho) {
          dem.khongThay++;
          if (maKhongThay.length < 3 && maKhongThay.indexOf(key) === -1) maKhongThay.push(key);
          continue;
        }
        for (const ct of cotCan) {
          const giaTri = vung[i][ct.col - 1];
          if (giaTri === null || giaTri === undefined || String(giaTri).trim() === '') continue;
          const col = layMapPhu(cho.sh)[ct.ten];
          if (!col) { dem.thieuCot++; continue; }
          dem[datOFilePhu(cho.sh, cho.row, col, giaTri)]++;
        }
      }
    }
    // 'dòng' chứ không phải 'ô': ở đây khongThay đếm theo DÒNG (một dòng hụt là hụt cả D lẫn E), khác lượt
    // sửa tay đếm theo ô. Báo sai đơn vị là báo cáo nói dối, dù chỉ nói dối một chữ.
    baoNguoiDung('Đồng bộ lại toàn bộ — xong.\n\n' + tomTatDongBo(dem, maKhongThay, 'dòng')
      + (hetGio ? '\n\n⚠ DỪNG SỚM vì chạm hạn giờ của Apps Script. CHẠY LẠI lần nữa để làm nốt '
        + '(lượt sau bỏ qua các ô đã khớp nên nhanh hơn nhiều).' : ''));
  } catch (err) {
    baoNguoiDung('LỖI khi đồng bộ lại toàn bộ: ' + err);
  } finally {
    lock.releaseLock();
  }
}

// Soi nhanh khi "gõ mà không thấy sang": ID file 2 đang dùng, trigger đã cài chưa, hai file có đủ cột không.
function kiemTraDongBo() {
  const dong = [];
  const idPhu = layIdFilePhu();
  dong.push('File 2 đang dùng: ' + (idPhu || '(TRỐNG — đang tắt ghi file 2)'));
  try {
    let soTrigger = 0;
    for (const t of ScriptApp.getProjectTriggers()) {
      if (t.getHandlerFunction() === TEN_HAM_TRIGGER_DONG_BO) soTrigger++;
    }
    dong.push('Trigger "khi chỉnh sửa": '
      + (soTrigger ? 'ĐÃ CÀI (' + soTrigger + ')' : 'CHƯA CÀI — bấm menu "Bật đồng bộ (cài trigger)"'));
  } catch (err) {
    dong.push('Trigger: không đọc được (' + err + ')');
  }

  const sh = SpreadsheetApp.getActiveSheet();
  const coOChinh = timCotDongBo(sh);
  dong.push('');
  dong.push('Tab đang mở ở file 1 — "' + sh.getName() + '":');
  dong.push('  ' + (coOChinh.length ? 'có cột ' + coOChinh.join(', ') : 'KHÔNG có cột nào để đồng bộ '
    + '(kiểm tra dòng tiêu đề: cần đúng chữ ' + COT_DONG_BO_TAY.join(' / ') + ')'));

  if (idPhu) {
    try {
      const ssPhu = SpreadsheetApp.openById(idPhu);
      const tabPhu = timTab(ssPhu, sh.getName());
      dong.push('');
      dong.push('File 2 — "' + ssPhu.getName() + '":');
      if (!tabPhu) {
        dong.push('  chưa có tab nào khớp tên "' + sh.getName() + '" (vẫn dò mã đơn ở mọi tab khác)');
      } else {
        const coOPhu = timCotDongBo(tabPhu);
        dong.push('  tab "' + tabPhu.getName() + '": '
          + (coOPhu.length ? 'có cột ' + coOPhu.join(', ') : 'KHÔNG có cột nào khớp — sửa dòng tiêu đề'));
      }
    } catch (err) {
      dong.push('');
      dong.push('KHÔNG mở được file 2: ' + err);
      dong.push('(tài khoản đang dùng phải có quyền SỬA file đó)');
    }
  }
  baoNguoiDung(dong.join('\n'));
}

// Các tiêu đề thuộc danh sách đồng bộ có mặt ở tab này, kèm chữ cái cột cho dễ đối chiếu bằng mắt.
function timCotDongBo(sheet) {
  const n = sheet.getLastColumn();
  if (n < 1) return [];
  const hdr = sheet.getRange(SO_DONG_TIEU_DE, 1, 1, n).getValues()[0];
  const ra = [];
  for (let c = 1; c <= n; c++) {
    const ten = chuanHoa(hdr[c - 1]);
    if (ten && COT_DONG_BO_TAY.indexOf(ten) !== -1) ra.push(chuCaiCot(c) + ' "' + ten + '"');
  }
  return ra;
}

function chuCaiCot(n) {
  let s = '';
  let x = n;
  while (x > 0) { const d = (x - 1) % 26; s = String.fromCharCode(65 + d) + s; x = (x - d - 1) / 26; }
  return s;
}

// Một câu tóm tắt cho toast/hộp thoại. Mọi ca KHÔNG làm được đều phải lộ ra đây — xem ranh giới #4 ở đầu khối.
// `donViKhongThay`: lượt sửa tay đếm theo Ô, lượt chạy lại toàn bộ đếm theo DÒNG.
function tomTatDongBo(dem, maKhongThay, donViKhongThay) {
  const xong = [];
  if (dem.ghi) xong.push(dem.ghi + ' ô đã ghi');
  if (dem.xoa) xong.push(dem.xoa + ' ô đã xoá theo');
  if (dem.trung) xong.push(dem.trung + ' ô đã khớp sẵn');
  const van = [];
  if (dem.khongThay) {
    van.push(dem.khongThay + ' ' + (donViKhongThay || 'ô') + ' không thấy mã đơn bên file 2'
      + (maKhongThay && maKhongThay.length ? ' (vd ' + maKhongThay.join(', ') + ')' : '')
      + ' — file 2 KHÔNG tự tạo dòng mới');
  }
  if (dem.thieuCot) van.push(dem.thieuCot + ' ô không có cột tương ứng ở file 2 (kiểm tra dòng tiêu đề)');
  if (dem.congThuc) van.push(dem.congThuc + ' ô bên file 2 đang là CÔNG THỨC nên giữ nguyên');
  if (dem.khongKhoa) van.push(dem.khongKhoa + ' ô nằm ở dòng chưa có mã đơn tại cột A');
  if (dem.vuotTran) van.push(dem.vuotTran + ' ô CHƯA xét vì vượt trần ' + TRAN_O_DONG_BO
    + ' ô/lượt — chạy "Đồng bộ lại toàn bộ" để làm nốt');
  if (!xong.length && !van.length) return '';
  return 'Sang file 2: ' + (xong.length ? xong.join(', ') : 'không có gì đổi')
    + (van.length ? '.\n⚠ ' + van.join('.\n⚠ ') + '.' : '.');
}

// Toast góc màn hình — dùng trong trigger (trigger KHÔNG mở được hộp thoại). Chuỗi rỗng thì im, khỏi phiền.
function baoNhanh(msg, giay) {
  if (!msg) return;
  try {
    SpreadsheetApp.getActiveSpreadsheet().toast(msg, 'Đồng bộ file 2', giay || 10);
  } catch (err) {
    Logger.log(msg);
  }
}

// Hộp thoại cho các lệnh chạy từ MENU (người dùng đang ngồi đó chờ đọc). Không có UI thì lùi về toast/log.
function baoNguoiDung(msg) {
  try {
    SpreadsheetApp.getUi().alert(msg);
    return;
  } catch (err) { /* không có UI — thử toast */ }
  try {
    SpreadsheetApp.getActiveSpreadsheet().toast(msg, 'Đồng bộ file 2', 20);
    return;
  } catch (err) { /* cũng không có spreadsheet đang mở */ }
  Logger.log(msg);
}

// ID file phụ cho đường ĐỒNG BỘ TAY. Ưu tiên cái doPost vừa dùng (app là NGUỒN THẬT của cấu hình `sheet2`),
// chưa có thì lùi về hằng. Phân biệt CHƯA-CÓ (null) với CỐ Ý TẮT (chuỗi rỗng) — nhập nhèm hai cái này thì
// hoặc là ghi nhầm file, hoặc là tắt oan.
function layIdFilePhu() {
  try {
    const p = PropertiesService.getScriptProperties().getProperty(KHOA_ID_FILE_PHU);
    if (p !== null && p !== undefined) return p;
  } catch (err) { /* không đọc được Properties — lùi về hằng */ }
  return ID_FILE_PHU;
}

// Chỉ ghi khi ĐỔI: doPost chạy mỗi lượt đẩy, ghi đi ghi lại một giá trị y hệt là phí lệnh gọi.
function nhoIdFilePhu(id) {
  try {
    const props = PropertiesService.getScriptProperties();
    const moi = String(id === null || id === undefined ? '' : id);
    if (props.getProperty(KHOA_ID_FILE_PHU) !== moi) props.setProperty(KHOA_ID_FILE_PHU, moi);
  } catch (err) {
    // Không nhớ được thì đường đồng bộ tay lùi về hằng ID_FILE_PHU — không được phá lượt đẩy của app.
  }
}
