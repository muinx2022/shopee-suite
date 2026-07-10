using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace UpdateProduct;

/// <summary>
/// NƠI DUY NHẤT nhận diện "Lưu &amp; Publish thành công" trên trang edit BigSeller (selector + bộ text +
/// tín hiệu URL/tab). BigSeller đang chuyển DOM từ ant-design sang Vue riêng và hay TỰ đóng dialog / TỰ
/// chuyển trang / TỰ đóng tab rất nhanh sau khi lưu → bám cứng 1 selector modal là hụt tín hiệu (bug prod:
/// SP publish thật nhưng runner trả false → không bắn RowsDone → Hub nhận 0 dòng). DOM đổi lần nữa thì CHỈ
/// sửa file này; runner/sự kiện phía trên không phải đụng.
/// </summary>
internal static class BigSellerSaveSuccessHelper
{
    // Trang edit = URL dạng /edit/{id}.htm — rời khỏi URL này (về listing / "create next product") = đã lưu xong.
    private static readonly Regex EditUrlRegex = new(@"/edit/(\d+)\.htm", RegexOptions.IgnoreCase);

    // Dialog "thành công" đọc theo THỨ TỰ ƯU TIÊN, gộp cả ant-design (cũ) lẫn el-dialog/vxe-modal (Vue mới) +
    // bắt rộng [class*='dialog'/'modal'] để đón bản DOM kế. Chỉ đọc phần tử ĐANG HIỆN (:visible) — trang edit
    // có nhiều modal ẩn dựng sẵn, đọc cả sẽ vớ nhầm text rỗng → tưởng chưa xong.
    private static readonly string[] SuccessDialogSelectors =
    {
        "div.ant-modal:visible", ".el-dialog:visible", ".vxe-modal--box:visible",
        "div[class*='dialog']:visible", "div[class*='modal']:visible",
    };

    // Dump chẩn đoán khi TIMEOUT: BigSeller đổi DOM lần nữa thì log này cho ngay selector/text mới để sửa TRONG
    // helper, không cần ngồi canh dialog thật trên máy client.
    private const string DumpVisibleDialogsJs = @"() => {
  const nodes = document.querySelectorAll("".ant-modal, .ant-modal-wrap, .el-dialog, .vxe-modal--box, [class*='dialog'], [class*='modal'], [class*='popup']"");
  const out = [];
  for (const el of nodes) {
    if (el.getClientRects().length === 0) continue;   // chỉ phần tử đang hiện
    const t = (el.innerText || '').replace(/\n/g, ' · ').slice(0, 120);
    out.push(el.tagName + '.' + el.className + ' ' + t);
    if (out.length >= 6) break;
  }
  return out;
}";

    /// <summary>
    /// Gọi SAU khi save đã submit và KHÔNG phát hiện lỗi. Poll ~250ms tới <paramref name="timeoutMs"/> để xác nhận
    /// đã lưu qua 1 trong 3 tín hiệu (tab tự đóng / URL rời trang edit / modal thành công), đóng tab (nếu còn) rồi
    /// gọi <paramref name="onSuccess"/> ĐÚNG lúc đó. Trả true = đã lưu chắc chắn (onSuccess được gọi TỐI ĐA 1 lần);
    /// trả false = không xác nhận được (KHÔNG gọi onSuccess) — tuyệt đối không báo thành công oan, vì báo oan thì
    /// dòng sheet bị đánh dấu xong trên Hub trong khi SP chưa update → auto-dispatch bỏ sót SP.
    /// </summary>
    public static async Task<bool> ConfirmSavedThenCloseAsync(
        IPage editPage, IBrowserContext context, int timeoutMs,
        Action<string> log, Func<int, CancellationToken, Task> delay,
        Func<Task>? onSuccess, CancellationToken ct)
    {
        var deadline = timeoutMs;
        const int step = 250;
        while (deadline > 0)
        {
            // (1) Tab tự đóng sau save — BigSeller đóng tab edit ngay khi publish xong. CHỈ báo thành công khi
            //     browser context CÒN SỐNG; context chết (Brave sập giữa chừng) = không chắc đã lưu → false.
            if (editPage.IsClosed)
            {
                if (!IsContextAlive(context)) return false;
                log("✅ xác nhận lưu thành công (tab tự đóng)");
                if (onSuccess != null) await onSuccess();
                return true;
            }

            try
            {
                // (2) URL rời trang edit → BigSeller đã điều hướng đi (listing / "create next product") sau lưu.
                var url = editPage.Url ?? "";
                if (!EditUrlRegex.IsMatch(url))
                {
                    log($"✅ xác nhận lưu thành công (URL đổi: {url})");
                    await CloseAcceptingDialogAsync(editPage);
                    if (onSuccess != null) await onSuccess();
                    return true;
                }

                // (3) Modal thành công đang hiện (bộ text song ngữ giữ nguyên từ WaitForSaveSuccessAsync cũ).
                if (await HasSuccessDialogAsync(editPage))
                {
                    log("✅ xác nhận lưu thành công (modal)");
                    await CloseAcceptingDialogAsync(editPage);
                    if (onSuccess != null) await onSuccess();
                    return true;
                }
            }
            catch (Exception ex) when ((ex.Message ?? "").Contains("closed", StringComparison.OrdinalIgnoreCase)
                                       || (ex.Message ?? "").Contains("Target", StringComparison.OrdinalIgnoreCase))
            {
                // Tab/target đóng ngay giữa lúc đọc → KHÔNG nuốt-lặp: vòng kế IsClosed sẽ chốt thành công/thất bại.
            }

            await delay(step, ct);
            deadline -= step;
        }

        await DumpVisibleDialogsAsync(editPage, log);
        return false;
    }

    // Brave sập → phải trả false (KHÔNG báo thành công oan). Dùng Browser.IsConnected (trạng thái kết nối CDP,
    // cập nhật tức thời) thay vì context.Pages — Pages là cache phía client, browser chết vẫn đọc được bình
    // thường nên không phân biệt nổi "BigSeller tự đóng tab sau lưu OK" với "Brave rớt cả cụm".
    private static bool IsContextAlive(IBrowserContext context)
    {
        try { return context.Browser?.IsConnected ?? true; }
        catch { return false; }
    }

    private static async Task<bool> HasSuccessDialogAsync(IPage page)
    {
        var sb = new StringBuilder();
        foreach (var sel in SuccessDialogSelectors)
        {
            // try/catch RIÊNG mỗi selector: 1 selector lỗi (DOM chưa có, target đóng) không chặn các selector còn lại.
            try
            {
                foreach (var t in await page.Locator(sel).AllInnerTextsAsync())
                    sb.Append(t).Append(" \n ");
            }
            catch { }
        }
        var m = Normalize(sb.ToString());
        return m.Contains("thao tac thanh cong") || m.Contains("successfully") ||
               m.Contains("de trinh") || m.Contains("pending by shopee") ||
               Regex.IsMatch(m, @"publishing\s*/\s*failed\s*/\s*active") ||
               m.Contains("close this page") || m.Contains("dong trang nay") ||
               m.Contains("create next product") || m.Contains("tao san pham tiep");
    }

    private static async Task DumpVisibleDialogsAsync(IPage page, Action<string> log)
    {
        try
        {
            var items = await page.EvaluateAsync<string[]>(DumpVisibleDialogsJs);
            foreach (var it in items)
                log("⚠ SaveSuccess TIMEOUT — dialog đang hiện: " + it);
        }
        catch { }
    }

    // Đóng tab + tự Accept dialog "rời trang?" (RunBeforeUnload). Sao từ ClosePageAcceptingDialogAsync ở runner:
    // nuốt lỗi + fallback CloseAsync trần; tab đã đóng sẵn thì bỏ qua êm.
    private static async Task CloseAcceptingDialogAsync(IPage page)
    {
        if (page.IsClosed) return;
        try
        {
            page.Dialog += async (_, d) => { try { await d.AcceptAsync(); } catch { } };
            await page.CloseAsync(new() { RunBeforeUnload = true });
        }
        catch { try { await page.CloseAsync(); } catch { } }
    }

    /// <summary>Bỏ dấu tiếng Việt + hạ chữ thường + gộp khoảng trắng (đ→d, bỏ dấu tổ hợp) để match text song ngữ.</summary>
    internal static string Normalize(string? s)
    {
        s ??= "";
        s = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (c == 'đ' || c == 'Đ') { sb.Append('d'); continue; }
            sb.Append(char.ToLowerInvariant(c));
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
