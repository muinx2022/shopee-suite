// POC kiểm chứng: so sánh 3 cách mở shop từ /portal/shop —
//   1) .click()      (synthetic, isTrusted=false)  → dự kiến captcha
//   2) mouse events  (synthetic, isTrusted=false)  → dự kiến captcha
//   3) TRUSTED (chrome.debugger Input) isTrusted=true → dự kiến VÀO ĐƯỢC (giống người bấm)
(function () {
  const TAG = '[EXT-TEST]';
  function log(m) { console.log(TAG, m); }

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

  function centerOf(el) {
    const r = el.getBoundingClientRect();
    return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) };
  }

  function synthClick(el) {
    const c = centerOf(el);
    const base = { bubbles: true, cancelable: true, view: window, clientX: c.x, clientY: c.y, button: 0 };
    for (const type of ['mousemove', 'mouseover', 'mousedown', 'mouseup', 'click']) {
      el.dispatchEvent(new MouseEvent(type, base));
    }
  }

  function runSynthetic(kind) {
    const f = findFirstDetail();
    if (!f) { alert('Không thấy "Chi tiết" — hãy ở trang /portal/shop.'); return; }
    log(`[${kind}] mở shop key=${f.key}`);
    if (kind === 'click') f.btn.click(); else synthClick(f.btn);
    log('Đã bấm (synthetic) — theo dõi tab mới.');
  }

  function runTrusted() {
    const f = findFirstDetail();
    if (!f) { alert('Không thấy "Chi tiết" — hãy ở trang /portal/shop.'); return; }
    try { f.btn.scrollIntoView({ block: 'center' }); } catch (_) {}
    // Đọc toạ độ SAU khi cuộn (đổi vị trí), rồi nhờ background bắn click trusted qua chrome.debugger.
    setTimeout(() => {
      const c = centerOf(f.btn);
      log(`[trusted] mở shop key=${f.key} tại ${c.x},${c.y}`);
      chrome.runtime.sendMessage({ cmd: 'trustedClick', x: c.x, y: c.y }, resp => {
        log('[trusted] kết quả: ' + JSON.stringify(resp));
        if (resp && !resp.ok) alert('Trusted click lỗi: ' + resp.error);
      });
    }, 350);
  }

  function addPanel() {
    if (document.getElementById('__ext_test_panel')) return;
    const box = document.createElement('div');
    box.id = '__ext_test_panel';
    box.style.cssText =
      'position:fixed;right:16px;bottom:16px;z-index:2147483647;display:flex;flex-direction:column;gap:8px;font-family:Arial,sans-serif';
    const mk = (label, bg, fn) => {
      const b = document.createElement('button');
      b.textContent = label;
      b.style.cssText =
        'padding:10px 14px;background:' + bg + ';color:#fff;border:0;border-radius:8px;cursor:pointer;font-size:13px;box-shadow:0 2px 8px rgba(0,0,0,.25)';
      b.addEventListener('click', fn);
      return b;
    };
    box.appendChild(mk('🧪 .click() (synthetic)', '#888', () => runSynthetic('click')));
    box.appendChild(mk('🧪 mouse events (synthetic)', '#888', () => runSynthetic('mouse')));
    box.appendChild(mk('✅ TRUSTED (chrome.debugger)', '#ee4d2d', () => runTrusted()));
    document.documentElement.appendChild(box);
  }

  addPanel();
  try {
    new MutationObserver(() => addPanel()).observe(document.documentElement, { childList: true, subtree: true });
  } catch (_) {}
})();
