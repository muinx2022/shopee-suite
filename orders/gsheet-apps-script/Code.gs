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
// ═══════════════════════════════════════════════════════════════════════════════════════════════════

const TEN_THU_MUC_PHIEU = 'Phieu-don-Shopee';
const TEN_TAB_MAC_DINH = 'tháng 4';
const TEN_TAB_MAU = 'tháng 4';   // tab dùng làm MẪU cấu trúc khi tự tạo tab tháng mới
const SO_DONG_TIEU_DE = 1;       // dòng tiêu đề ở đầu tab (giữ khi nhân bản; cũng là dòng để tra tên cột)
const MAU_DO_HUY = '#ea9998'; // nền đỏ đơn hủy — CỐ Ý lệch 1 so với "light red 2" (#ea9999) của bảng màu
                              // chuẩn để không xóa nhầm nền đỏ người dùng tự tô
const SO_COT_TO_MAU = 13;     // tô nền từ cột A tới M (đã kẹp theo lưới thật của tab)

// Cột A KHÔNG có tiêu đề (ô trống) nên không tra được theo tên → chỉ chỗ này là số cứng.
const COT_MA_DON = 1;

// ─── BẢN ĐỒ TRƯỜNG → TÊN TIÊU ĐỀ (chữ thường) ────────────────────────────────────────────────────
// ĐÂY LÀ CHỖ DUY NHẤT cần sửa khi muốn đổi cột đích. Tên viết thường, khoảng trắng gộp lại — so khớp
// qua chuanHoa() nên hoa/thường và khoảng trắng thừa trong tiêu đề đều không ảnh hưởng.
//
// LƯU Ý hai chỗ trông "lệch tên" nhưng CỐ Ý giữ nguyên theo hiện trạng sheet:
//   · tenShop → cột "Note"  (tên shop vẫn nằm ở Note từ trước tới nay)
//   · sku     → cột "shop"  (SKU vẫn nằm ở cột tên "shop")
// Muốn dọn lại cho đúng nghĩa thì đổi thành 'shop' và 'mã sp' — NHƯNG nhớ kéo dữ liệu các dòng CŨ
// sang cột mới trước, kẻo nửa sheet một kiểu.
const COT = {
  maVanDon:   'mã vận đơn gửi',
  fileUrl:    'ảnh mã vận đơn gửi',
  donTraHang: 'mã đơn trả hàng',
  tenShop:    'note',
  doanhThu:   'tiền bán',
  ngay:       'ngày đặt',
  sku:        'shop',
  phanLoai:   'phân loại',
};

// Tiêu đề dựng cho tab mới khi KHÔNG tìm thấy tab mẫu (A để trống đúng như sheet thật đang dùng).
const TIEU_DE_MAC_DINH = [
  '', 'mã vận đơn gửi', 'ảnh mã vận đơn gửi', 'mã đơn đặt', 'Mã đơn trả hàng', 'Note',
  'tiền đặt', 'tiền bán', 'ngày đặt', 'shop', 'Phân loại', 'tk đặt', 'Mã Sp'
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
        ghiNeuTrong(cho.sh, cho.row, COT_MA_DON, don.maDon);   // A — tiêu đề trống nên dùng số cứng
        ghiTruong(cho.sh, map, cho.row, 'maVanDon',   don.maVanDon,   thieuCot);
        ghiTruong(cho.sh, map, cho.row, 'fileUrl',    fileUrl,        thieuCot);
        ghiTruong(cho.sh, map, cho.row, 'donTraHang', don.donTraHang, thieuCot);
        ghiTruong(cho.sh, map, cho.row, 'tenShop',    don.tenShop,    thieuCot);
        ghiTruong(cho.sh, map, cho.row, 'doanhThu',   don.doanhThu,   thieuCot);
        ghiTruong(cho.sh, map, cho.row, 'ngay',       don.ngay,       thieuCot);
        ghiTruong(cho.sh, map, cho.row, 'sku',        don.sku,        thieuCot);
        ghiTruong(cho.sh, map, cho.row, 'phanLoai',   don.phanLoai,   thieuCot);

        // Màu trạng thái: hủy → nền đỏ cả dòng; hết hủy (daHuy === false TƯỜNG MINH) → CHỈ xóa đúng màu
        // đỏ script đã tô (không đụng màu người dùng tự tô; thiếu field daHuy → không đụng màu).
        const vungDong = cho.sh.getRange(cho.row, 1, 1, Math.min(SO_COT_TO_MAU, cho.sh.getMaxColumns()));
        if (don.daHuy === true) {
          vungDong.setBackground(MAU_DO_HUY);
        } else if (don.daHuy === false && String(cho.sh.getRange(cho.row, 1).getBackground()).toLowerCase() === MAU_DO_HUY) {
          vungDong.setBackground(null);
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
    const filePhu = { ghi: 0, them: 0, loi: loiCauHinhPhu };
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
          ghiTruong(cho.sh, map, cho.row, 'donTraHang', don.donTraHang, thieuCot);   // E
          filePhu.ghi++;
        }
      } catch (err) {
        filePhu.loi = String(err);
      }
    }

    const traVe = { results: results };
    if (idPhu || loiCauHinhPhu) traVe.filePhu = filePhu;
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
function banDoMaDon(ss, tabDich) {
  const cacTab = [tabDich].concat(ss.getSheets().filter(function (s) { return s.getSheetId() !== tabDich.getSheetId(); }));
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
function ghiTruong(sheet, map, row, khoa, giaTri, thieu) {
  if (giaTri === null || giaTri === undefined || giaTri === '') return;
  const ten = COT[khoa];
  const col = ten ? map[ten] : null;
  if (!col) {
    if (ten && thieu.indexOf(ten) === -1) thieu.push(ten);
    return;
  }
  ghiNeuTrong(sheet, row, col, giaTri);
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
function ghiNeuTrong(sheet, row, col, giaTri) {
  if (giaTri === null || giaTri === undefined || giaTri === '') return;
  const o = sheet.getRange(row, col);
  if (String(o.getValue()).trim() === '' && o.getFormula() === '') o.setValue(giaTri);
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
