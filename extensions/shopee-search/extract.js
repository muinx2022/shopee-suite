// â”€â”€ Extract product data from loaded search results page â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
import { ctx, log, reportNetworkError } from './core.js';

export async function extractPageData(keyword, categoryName = '') {
  try {
    const [res] = await chrome.scripting.executeScript({
      target: { tabId: ctx.searchTabId },
      world: 'MAIN',
      args: [categoryName],
      func: (categoryName) => {
        const result = { links: [], items: [], source: 'none' };

        // API price is in micro-VND (price × 100000), e.g. 122999đ → 12,299,900,000.
        // Shop pages sometimes store the price already in VND. Only divide when the value
        // is clearly micro (large), so "122.999" → 122999 either way (never 1.22).
        const priceToVnd = p => {
          const n = Number(p) || 0;
          return n > 1e7 ? Math.round(n / 100000) : n;
        };

        // â”€â”€ Try 1: __NEXT_DATA__ (Next.js SSR) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        try {
          const nd = document.getElementById('__NEXT_DATA__');
          if (nd) {
            const parsed = JSON.parse(nd.textContent);
            const itemList =
              parsed?.props?.pageProps?.initialData?.data?.item_card_list ||
              parsed?.props?.pageProps?.data?.item_card_list ||
              parsed?.props?.pageProps?.searchResult?.item_card_list;
            if (itemList?.length) {
              result.source = '__NEXT_DATA__';
              result.items = itemList.map(it => {
                const b = it.item_basic || it;
                return {
                  name:     b.name,
                  itemid:   b.itemid,
                  shopid:   b.shopid,
                  price:    priceToVnd(b.price),
                  sold:     b.sold,
                  rating:   b.rating_star,
                  location: b.shop_location,
                  category: categoryName || '',
                  image:    b.image,
                  link:     `https://shopee.vn/product/${b.shopid}/${b.itemid}`,
                };
              });
              result.links = result.items.map(i => i.link);
              return result;
            }
          }
        } catch (_) {}

        // â”€â”€ Try 2: window.__SC_DATA__ or other globals â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        try {
          const sc = window.__SC_DATA__ || window.__SHOPEE_INITIAL_STATE__;
          const itemList = sc?.search?.item_card_list || sc?.search?.items;
          if (itemList?.length) {
            result.source = '__SC_DATA__';
            result.items = itemList.map(it => {
              const b = it.item_basic || it;
              return {
                name: b.name, itemid: b.itemid, shopid: b.shopid,
                price: priceToVnd(b.price), sold: b.sold,
                rating: b.rating_star,
                // Thiếu location là app LOẠI SẠCH sản phẩm ở bộ lọc khu vực (rỗng = loại) mà vẫn báo "xong".
                location: b.shop_location,
                category: categoryName || '',
                image: b.image,
                link: `https://shopee.vn/product/${b.shopid}/${b.itemid}`,
              };
            });
            result.links = result.items.map(i => i.link);
            return result;
          }
        } catch (_) {}

        // â”€â”€ Try 3: DOM â€” find all product <a> links â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        try {
          const pattern = /shopee\.vn\/[^?#]*-i\.(\d+)\.(\d+)/;
          const seen = new Set();
          const normalize = s => (s || '').replace(/\s+/g, ' ').trim();
          const toNumber = raw => {
            const digits = String(raw || '').replace(/[^\d]/g, '');
            return digits ? Number(digits) : 0;
          };
          const parsePrice = text => {
            const prices = [];
            for (const m of normalize(text).matchAll(/(?:₫|đ|â‚«|Ä‘)\s*([\d.,]+)|([\d.,]+)\s*(?:₫|đ|â‚«|Ä‘)/gi)) {
              const n = toNumber(m[1] || m[2]);
              if (n >= 1000) prices.push(n);
            }
            if (prices.length) return Math.min(...prices);
            const m = normalize(text).match(/(?:^|[^\d])(\d{1,3}(?:[.,]\d{3})+)(?=\D|$)/);
            return m ? toNumber(m[1]) : 0;
          };
          const parseSold = text => {
            const source = normalize(text);
            const lower = source.toLowerCase();
            const markers = ['đã bán', 'da ban', 'sold', 'Ä‘Ã£ bÃ¡n'];
            const markerIndex = markers
              .map(x => lower.indexOf(x))
              .filter(x => x >= 0)
              .sort((a, b) => a - b)[0];
            const soldText = markerIndex >= 0
              ? source.slice(Math.max(0, markerIndex - 36), markerIndex + 96)
              : source;
            const unitWords = 'k|nghìn|nghin|ngan|nghÃ¬n|tr|triệu|trieu|triá»‡u';
            const afterMarker = new RegExp(`(?:đã\\s*bán|da\\s*ban|Ä‘Ã£\\s*bÃ¡n|sold(?:\\s*(?:/|per)?\\s*(?:month|monthly))?)\\s*[>≥~]?\\s*([\\d.,]+)\\s*(${unitWords})?\\+?`, 'i');
            const beforeMarker = new RegExp(`([\\d.,]+)\\s*(${unitWords})?\\+?\\s*(?:đã\\s*bán|da\\s*ban|Ä‘Ã£\\s*bÃ¡n|sold(?:\\s*(?:/|per)?\\s*(?:month|monthly))?)`, 'i');
            const m = soldText.match(afterMarker) || soldText.match(beforeMarker);
            if (!m) return 0;
            let value = Number((m[1] || '').replace(',', '.').replace(/[^\d.]/g, ''));
            const unit = (m[2] || '').toLowerCase();
            if (!Number.isFinite(value)) return 0;
            if (unit === 'k' || unit === 'nghìn' || unit === 'nghin' || unit === 'nghÃ¬n' || unit === 'ngan') value *= 1000;
            if (unit === 'tr' || unit === 'triệu' || unit === 'triá»‡u' || unit === 'trieu') value *= 1000000;
            return Math.round(value);
          };
          const parseRating = text => {
            const m = normalize(text).match(/(?:^|\s)([1-5](?:[.,]\d)?)(?:\s|$)/);
            return m ? Number(m[1].replace(',', '.')) : 0;
          };
          const findCard = a => {
            const item = a.closest('li.shopee-search-item-result__item, .shop-search-result-view__item');
            if (item) return item;
            let el = a;
            for (let i = 0; el && i < 8; i++, el = el.parentElement) {
              const rect = el.getBoundingClientRect?.();
              const text = normalize(el.innerText || el.textContent || '');
              if (rect && rect.width >= 120 && rect.height >= 180 && text.length > 20) return el;
            }
            return a;
          };
          const getName = (card, a) => {
            const alt = [...card.querySelectorAll('img')]
              .map(img => normalize(img.alt))
              .find(x => x && !/custom-overlay|flag-label|voucher|rating|location/i.test(x));
            if (alt) return alt;
            const title = normalize(a.title || a.getAttribute('aria-label'));
            if (title) return title;
            const bad = /đã bán|Ä‘Ã£ bÃ¡n|da ban|sold|₫|đ|â‚«|Ä‘|%|voucher|location|rating|mua để nhận|mua Ä‘á»ƒ nháº­n|mua de nhan/i;
            return normalize(card.innerText || card.textContent || '')
              .split('\n')
              .map(normalize)
              .filter(x => x.length >= 8 && !bad.test(x))
              .sort((a, b) => b.length - a.length)[0] || '';
          };
          const getLocation = card => {
            const aria = [...card.querySelectorAll('[aria-label]')]
              .map(el => normalize(el.getAttribute('aria-label')))
              .find(x => /^location-/i.test(x));
            if (aria) return aria.replace(/^location-/i, '');
            const rx = /(Hà Nội|HÃ  Ná»™i|Ha Noi|Thành phố Hồ Chí Minh|Hồ Chí Minh|Há»“ ChÃ­ Minh|Ho Chi Minh|Đà Nẵng|ÄÃ  Náºµng|Da Nang|Hải Phòng|Háº£i PhÃ²ng|Hai Phong|Cần Thơ|Cáº§n ThÆ¡|Can Tho|Bình Dương|BÃ¬nh DÆ°Æ¡ng|Đồng Nai|Äá»“ng Nai|Dong Nai|Nước ngoài|NÆ°á»›c ngoÃ i|Quốc tế|Quá»‘c táº¿)/i;
            return normalize(card.innerText || card.textContent || '').split('\n').map(normalize).find(x => rx.test(x)) || '';
          };
          const getImage = card => {
            const img = [...card.querySelectorAll('img')]
              .find(x => x.src && /susercontent/.test(x.src) && !/custom-overlay|flag-label|voucher|rating|location/i.test(x.alt || ''));
            return img?.currentSrc || img?.src || '';
          };
          // On the keyword search page, restrict to the result section to avoid
          // recommendation widgets. On a shop "All products" page there is no such
          // section, so accept any product link (find_similar links are excluded below).
          const searchSection = document.querySelector('section.shopee-search-item-result');
          const isSearchResultCard = a => {
            if (!searchSection) return true;
            return !!a.closest('section.shopee-search-item-result');
          };
          const cardItems = [];
          const scanRoot = container => {
            container.querySelectorAll('a[href]').forEach(a => {
              const m = a.href.match(pattern);
              if (!m || seen.has(a.href)) return;
              if (a.href.includes('/find_similar_products')) return;
              if (!isSearchResultCard(a)) return;
              seen.add(a.href);
              const card = findCard(a);
              const text = normalize(card.innerText || card.textContent || '');
              cardItems.push({
                shopid: Number(m[1]),
                itemid: Number(m[2]),
                link: a.href,
                name: getName(card, a),
                price: parsePrice(text),
                sold: parseSold(text),
                rating: parseRating(text),
                category: categoryName || '',
                location: getLocation(card),
                image: getImage(card),
              });
            });
          };

          const root = searchSection
            || document.querySelector('.shop-search-result-view')
            || document.querySelector('[class*="shop-search-result"]')
            || document.querySelector('.shop-page')
            || document;
          scanRoot(root);
          // Shop layouts vary; if the preferred container yielded nothing, scan the whole page.
          if (cardItems.length === 0 && root !== document) scanRoot(document);

          const richItems = cardItems.filter(x => x.name || x.price > 0 || x.sold > 0);
          if (richItems.length) {
            result.source = 'DOM_cards';
            result.items = richItems;
            result.links = richItems.map(i => i.link);
            return result;
          }

          result.links = cardItems.map(i => i.link);
          if (result.links.length) result.source = 'DOM_links';
        } catch (_) {}

        // â”€â”€ Try 4: inline <script> tags with JSON data â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        if (!result.items.length) {
          try {
            for (const s of document.querySelectorAll('script:not([src])')) {
              const txt = s.textContent;
              if (txt.includes('item_card_list') || txt.includes('"itemid"')) {
                const m = txt.match(/\{[^;]+item_card_list[^;]+\}/s);
                if (m) {
                  const obj = JSON.parse(m[0]);
                  const list = obj.item_card_list || obj.items;
                  if (list?.length) {
                    result.source = 'script_tag';
                    result.items = list.slice(0, 60).map(it => {
                      const b = it.item_basic || it;
                      return {
                        name: b.name, itemid: b.itemid, shopid: b.shopid,
                        price: priceToVnd(b.price), sold: b.sold,
                        category: categoryName || '',
                        link: `https://shopee.vn/product/${b.shopid}/${b.itemid}`,
                      };
                    });
                    break;
                  }
                }
              }
            }
          } catch (_) {}
        }

        return result;
      },
    });
    return res?.result ?? null;
  } catch (e) {
    log('extractPageData error: ' + e.message);
    if (/error page|timed out|proxy|ERR_/i.test(e.message || '')) {
      reportNetworkError('Không lấy DOM được vì tab đang ở trang lỗi/proxy.');
    }
    return null;
  }
}
