(() => {
  if (window.__shopee27052026ScrapeClickerInjected) {
    return;
  }
  window.__shopee27052026ScrapeClickerInjected = true;

  const MAX_FIND_MS = 30_000;     // chờ nút scrape xuất hiện
  // Chờ kết quả THEO TRẠNG THÁI BigSeller, KHÔNG đặt giờ cứng: còn "scraping" thì chờ tới khi xong;
  // chỉ "failed"/"ok" mới làm tiếp. Bỏ chờ DUY NHẤT khi BigSeller im lặng (không còn báo đang crawl,
  // cũng chưa có kết quả) lâu hơn IDLE_RESULT_MS — chốt an toàn phòng trang hỏng. Watchdog (8') là chặn cuối.
  const IDLE_RESULT_MS = 90_000;
  const STEP_MS = 500;

  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

  const isVisible = (element) => {
    if (!element) return false;
    const style = window.getComputedStyle(element);
    return (
      style &&
      style.display !== "none" &&
      style.visibility !== "hidden" &&
      style.opacity !== "0"
    );
  };

  const matchesScrape = (element) => {
    const label = [
      element.getAttribute("aria-label"),
      element.getAttribute("title"),
      element.textContent,
      element.value,
      element.dataset?.tooltip,
      element.dataset?.testid,
    ]
      .filter(Boolean)
      .join(" ")
      .toLowerCase();
    return label.includes("scrape");
  };

  // BigSeller gắn class .scraped vào wrapper nút khi sản phẩm ĐÃ được cào. Nếu mở lại link đã scrape
  // (vd resume), click lại sẽ reload trang → vòng lặp "scrape → reload → scrape". Phát hiện trạng thái
  // này để BỎ QUA (coi như xong) thay vì click.
  const isAlreadyScraped = (root) => {
    if (root.querySelector(".crawl_trigger.crawl_btn_wrapper.big_crawl.scraped")) return true;
    for (const host of root.querySelectorAll("*")) {
      if (host.shadowRoot && isAlreadyScraped(host.shadowRoot)) return true;
    }
    return false;
  };

  const searchInRoot = (root) => {
    const selectors = [
      ".crawl_trigger.crawl_btn_wrapper.big_crawl button.btn_01.crawl_text.detail",
      ".crawl_trigger.crawl_btn_wrapper.big_crawl button",
      "#scrapeBtn",
      ".bigseller-scrape",
      "[data-testid*='scrape']",
      "[aria-label*='scrape' i]",
      "[title*='scrape' i]",
    ];

    for (const selector of selectors) {
      const el = root.querySelector(selector);
      if (el && typeof el.click === "function" && isVisible(el)) return el;
    }

    const candidates = root.querySelectorAll(
      "button, [role='button'], input[type='button'], input[type='submit'], a"
    );
    for (const el of candidates) {
      if (typeof el.click !== "function" || !isVisible(el)) continue;
      if (matchesScrape(el)) return el;
    }

    for (const host of root.querySelectorAll("*")) {
      if (host.shadowRoot) {
        const found = searchInRoot(host.shadowRoot);
        if (found) return found;
      }
    }

    return null;
  };

  const findVariantStockCheckbox = (root) => {
    const label =
      root.querySelector('label[for="crawl_inventory"]') ||
      Array.from(root.querySelectorAll("label")).find((el) =>
        (el.textContent || "").trim().toLowerCase().includes("scrape variant stock")
      );
    const input =
      root.querySelector("#crawl_inventory") ||
      root.querySelector('input[type="checkbox"][id="crawl_inventory"]') ||
      (label?.htmlFor ? root.getElementById?.(label.htmlFor) : null);

    if (input || label) return { input, label };

    for (const host of root.querySelectorAll("*")) {
      if (host.shadowRoot) {
        const found = findVariantStockCheckbox(host.shadowRoot);
        if (found) return found;
      }
    }

    return null;
  };

  // Người dùng KHÔNG muốn cào tồn kho biến thể ("Scrape Variant Stock"). Đảm bảo Ô NÀY BỎ TICK trước khi
  // bấm Scrape — BigSeller có thể nhớ trạng thái tick từ lần trước nên phải chủ động bỏ nếu đang được tick.
  const disableVariantStockScrape = async () => {
    const target = findVariantStockCheckbox(document);
    if (!target) return false;

    const { input, label } = target;
    if (!input?.checked) return true; // đã không tick → không làm gì

    const clickable = label && isVisible(label) ? label : input;
    if (clickable && typeof clickable.click === "function") {
      clickable.click();
      await sleep(150);
    }

    return !input?.checked;
  };

  // Đọc kết quả BigSeller báo qua toast (.content_wrap > .icon.success|failed|waiting + h3). Tìm cả trong
  // shadow DOM. Trả về { state, text } của toast NHẬN DẠNG ĐƯỢC mới nhất; null nếu chưa có toast.
  const readBigSellerResult = (root) => {
    // DOM BigSeller: <div class="content_wrap"><span class="icon success|failed|waiting"></span><h3>…</h3></div>
    //  • success: "Successfully Scraped"  • failed: "Failed, log in BigSeller first"  • waiting: "Scrapping the product..."
    // ƯU TIÊN kết quả THẬT (success/failed) hơn toast "đang scrape" còn sót lại (tránh che mất kết quả).
    let terminal = null;
    let waiting = null;
    const wraps = root.querySelectorAll?.(".content_wrap") || [];
    for (const w of wraps) {
      const icon = w.querySelector(".icon");
      if (!icon) continue;
      const text = (w.querySelector("h3")?.textContent || "").trim();
      if (icon.classList.contains("success")) terminal = { state: "success", text };
      else if (icon.classList.contains("failed")) terminal = { state: "failed", text };
      else if (icon.classList.contains("waiting")) waiting = { state: "waiting", text };
    }
    if (terminal) return terminal;
    if (waiting) return waiting;
    for (const host of root.querySelectorAll?.("*") || []) {
      if (host.shadowRoot) {
        const found = readBigSellerResult(host.shadowRoot);
        if (found) return found;
      }
    }
    return null;
  };

  const notify = async (detail) => {
    try {
      await chrome.runtime.sendMessage({ type: "SCRAPE_RESULT", detail });
    } catch (_) {}
  };

  const run = async () => {
    // Đã scrape trước đó → coi như xong, bỏ qua (tránh click lại gây reload lặp).
    if (isAlreadyScraped(document)) {
      await notify({ ok: true, alreadyScraped: true, message: "Link đã scrape trước đó — bỏ qua, sang link kế." });
      return;
    }

    // 1) Chờ nút scrape xuất hiện rồi click.
    const findDeadline = Date.now() + MAX_FIND_MS;
    let button = null;
    while (Date.now() < findDeadline) {
      if (isAlreadyScraped(document)) {
        await notify({ ok: true, alreadyScraped: true, message: "Link đã scrape trước đó — bỏ qua, sang link kế." });
        return;
      }
      button = searchInRoot(document);
      if (button) break;
      await sleep(STEP_MS);
    }
    if (!button) {
      await notify({ ok: false, message: "Không tìm thấy nút scrape." });
      return;
    }

    await disableVariantStockScrape();
    button.click();

    // 2) Chờ BigSeller báo KẾT QUẢ THẬT. CHỜ TỚI KHI CRAWL XONG: nếu BigSeller còn đang crawl
    //    (state="waiting") thì GIA HẠN cửa sổ chờ → KHÔNG bỏ giữa chừng (tránh background tưởng fail
    //    rồi reload + click lại → vòng "scrape lâu → reload → scrape" khi sản phẩm crawl chậm).
    //    Chỉ bỏ khi: (a) im lặng quá IDLE_RESULT_MS (không thấy crawl), hoặc (b) quá trần tổng.
    let idleDeadline = Date.now() + IDLE_RESULT_MS;
    while (Date.now() < idleDeadline) {
      const r = readBigSellerResult(document);
      if (r) {
        if (r.state === "success") {
          await notify({ ok: true, message: "BigSeller: " + (r.text || "Successfully Scraped") });
          return;
        }
        if (r.state === "failed") {
          const low = (r.text || "").toLowerCase();
          const needLogin = low.includes("log in") || low.includes("login") || low.includes("đăng nhập");
          await notify({ ok: false, needLogin, message: "BigSeller: " + (r.text || "Failed") });
          return;
        }
        // state === "waiting" → BigSeller ĐANG crawl → gia hạn, KIÊN NHẪN chờ tới khi xong (không bỏ
        // giữa chừng → không để background tưởng fail rồi reload + click lại).
        if (r.state === "waiting") idleDeadline = Date.now() + IDLE_RESULT_MS;
      }
      // Có thể chuyển sang trạng thái đã-scraped mà không kịp thấy toast.
      if (isAlreadyScraped(document)) {
        await notify({ ok: true, alreadyScraped: true, message: "Đã scrape (phát hiện trạng thái scraped)." });
        return;
      }
      await sleep(STEP_MS);
    }
    await notify({ ok: false, message: "BigSeller im lặng quá lâu (không thấy đang crawl) — bỏ qua dòng này." });
  };

  run().catch(async (error) => {
    await notify({ ok: false, message: error.message || String(error) });
  });
})();
