// POC kiểm chứng GĐ0: trong trình duyệt THƯỜNG (không CDP), thử mở shop từ trang
// /portal/shop bằng extension để xem có né được captcha "Chi tiết" không.
// Thêm 2 nút test nổi góc phải-dưới (chỉ có nghĩa ở trang danh sách shop).
(function () {
  const TAG = '[EXT-TEST]';
  function log(m) { console.log(TAG, m); }

  // Tìm nút "Chi tiết" của DÒNG shop đầu tiên (bảng /portal/shop: tr[data-row-key]).
  function findFirstDetail() {
    const rows = document.querySelectorAll('tr[data-row-key]');
    for (const row of rows) {
      const cands = row.querySelectorAll('button, a, [role="button"]');
      for (const b of cands) {
        const t = (b.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
        if (t.includes('chi tiết') || t.includes('chi tiet') || t === 'detail') {
          return { row, btn: b, key: row.getAttribute('data-row-key') };
        }
      }
    }
    return null;
  }

  // Click "thật hơn": dispatch chuỗi sự kiện chuột ở tâm phần tử (vẫn synthetic, isTrusted=false).
  function mouseClick(el) {
    const r = el.getBoundingClientRect();
    const x = r.left + r.width / 2, y = r.top + r.height / 2;
    const base = { bubbles: true, cancelable: true, view: window, clientX: x, clientY: y, button: 0 };
    try { el.scrollIntoView({ block: 'center' }); } catch (_) {}
    for (const type of ['mousemove', 'mouseover', 'mousedown', 'mouseup', 'click']) {
      el.dispatchEvent(new MouseEvent(type, base));
    }
  }

  function run(kind) {
    const f = findFirstDetail();
    if (!f) { alert('Không thấy nút "Chi tiết" — hãy đứng ở trang danh sách shop (/portal/shop).'); return; }
    log(`Mở shop key=${f.key} bằng: ${kind}`);
    if (kind === 'click') f.btn.click();
    else mouseClick(f.btn);
    log('Đã bấm — theo dõi tab mới: vào shop được hay ra captcha "Lỗi tải".');
  }

  function addPanel() {
    if (document.getElementById('__ext_test_panel')) return;
    const box = document.createElement('div');
    box.id = '__ext_test_panel';
    box.style.cssText =
      'position:fixed;right:16px;bottom:16px;z-index:2147483647;display:flex;flex-direction:column;gap:8px;font-family:Arial,sans-serif';
    const mk = (label, fn) => {
      const b = document.createElement('button');
      b.textContent = label;
      b.style.cssText =
        'padding:10px 14px;background:#ee4d2d;color:#fff;border:0;border-radius:8px;cursor:pointer;font-size:13px;box-shadow:0 2px 8px rgba(0,0,0,.25)';
      b.addEventListener('click', fn);
      return b;
    };
    box.appendChild(mk('🧪 Mở shop đầu — .click()', () => run('click')));
    box.appendChild(mk('🧪 Mở shop đầu — mouse events', () => run('mouse')));
    document.documentElement.appendChild(box);
  }

  addPanel();
  // SPA vẽ lại DOM → thêm lại nút nếu bị mất.
  try {
    new MutationObserver(() => addPanel()).observe(document.documentElement, { childList: true, subtree: true });
  } catch (_) {}
})();
