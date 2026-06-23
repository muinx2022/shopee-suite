(() => {
  if (window.__shopee27052026ScrapeClickerInjected) {
    return;
  }
  window.__shopee27052026ScrapeClickerInjected = true;

  const MAX_FIND_MS = 30_000;     // chờ nút scrape xuất hiện
  const MAX_RESULT_MS = 60_000;   // chờ BigSeller báo kết quả sau khi click (scrape variant stock có thể lâu)
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

  const enableVariantStockScrape = async () => {
    const target = findVariantStockCheckbox(document);
    if (!target) return false;

    const { input, label } = target;
    if (input?.checked) return true;

    const clickable = label && isVisible(label) ? label : input;
    if (clickable && typeof clickable.click === "function") {
      clickable.click();
      await sleep(150);
    }

    return Boolean(input?.checked);
  };

  // Đọc kết quả BigSeller báo qua toast (.content_wrap > .icon.success|failed|waiting + h3). Tìm cả trong
  // shadow DOM. Trả về { state, text } của toast NHẬN DẠNG ĐƯỢC mới nhất; null nếu chưa có toast.
  const readBigSellerResult = (root) => {
    let result = null;
    const wraps = root.querySelectorAll?.(".content_wrap") || [];
    for (const w of wraps) {
      const icon = w.querySelector(".icon");
      if (!icon) continue;
      const text = (w.querySelector("h3")?.textContent || "").trim();
      if (icon.classList.contains("success")) result = { state: "success", text };
      else if (icon.classList.contains("failed")) result = { state: "failed", text };
      else if (icon.classList.contains("waiting")) result = { state: "waiting", text };
    }
    if (result) return result;
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

    await enableVariantStockScrape();
    button.click();

    // 2) Chờ BigSeller báo KẾT QUẢ THẬT (không báo xong trước khi scrape xong nữa).
    const resultDeadline = Date.now() + MAX_RESULT_MS;
    while (Date.now() < resultDeadline) {
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
        // state === "waiting" → đang scrape, chờ tiếp.
      }
      // Có thể chuyển sang trạng thái đã-scraped mà không kịp thấy toast.
      if (isAlreadyScraped(document)) {
        await notify({ ok: true, alreadyScraped: true, message: "Đã scrape (phát hiện trạng thái scraped)." });
        return;
      }
      await sleep(STEP_MS);
    }
    await notify({ ok: false, message: "Hết giờ chờ kết quả BigSeller (có thể vẫn đang scrape)." });
  };

  run().catch(async (error) => {
    await notify({ ok: false, message: error.message || String(error) });
  });
})();
