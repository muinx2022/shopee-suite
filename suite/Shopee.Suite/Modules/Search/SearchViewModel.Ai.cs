using System.IO;
using CommunityToolkit.Mvvm.Input;
using Shopee.Core.Ai;
using Shopee.Suite.Services;
using ShopeeStatApp.Services;

namespace Shopee.Suite.Modules.Search;

/// <summary>Phần SearchViewModel: mảng TAB "Danh mục (AI)" — chọn file danh mục lá, nạp/làm mới lưới
/// danh mục, chạy gán danh mục bằng AI (CSDL hoặc file Excel) và kiểm điều kiện chạy AI.</summary>
public sealed partial class SearchViewModel
{
    // ── Tab Danh mục (AI) ───────────────────────────────────────────────────────
    [RelayCommand]
    private async Task BrowseDocxAsync()
    {
        var path = await FilePicker.OpenFileAsync("Chọn file danh mục lá Shopee", "Word/Excel danh mục|*.docx;*.xlsx|Tất cả|*.*");
        if (path is not null) CategoryDocxPath = path;
    }

    [RelayCommand]
    private void RefreshCategoryGrid()
    {
        CategoryRows.Clear();
        foreach (var c in Db.GetCategoryRows()) CategoryRows.Add(c);
        Status = $"{CategoryRows.Count} danh mục trong CSDL.";
    }

    /// <summary>Nạp danh mục lúc khởi động bằng MỘT lượt truy vấn cho cả lưới lẫn combo lọc
    /// (<c>GetCategoryRows</c> là JOIN <c>shop_products</c> — gọi riêng hai lượt là quét hai lần trên UI thread
    /// khi mở app), và CSDL hỏng không được làm chết ctor VM: lưới để trống, combo còn "(Tất cả)".</summary>
    private void NapDanhMucLucKhoiDong()
    {
        IReadOnlyList<SearchTaskStore.CategoryRow> rows;
        try { rows = Db.GetCategoryRows(); }
        catch (Exception ex)
        {
            Log("Không nạp được danh mục từ CSDL: " + ex.Message);
            rows = [];
        }
        CategoryRows.Clear();
        foreach (var c in rows) CategoryRows.Add(c);
        Status = $"{CategoryRows.Count} danh mục trong CSDL.";
        ReloadCategoryFilterFromDb(rows);
    }

    private bool CanRunAi() => AiIdle;

    [RelayCommand(CanExecute = nameof(CanRunAi))]
    private async Task UpdateCategoriesAiAsync()
    {
        if (await ValidateAiAsync() is not { } ai) return;
        AiBusy = true; _aiCts = new CancellationTokenSource();
        try
        {
            Log("🤖 Cập nhật danh mục (AI) cho TOÀN BỘ sản phẩm trong CSDL…");
            var n = await Db.UpdateCategoriesDbAsync(ai, CategoryDocxPath,
                (d, t) => OnUi(() => AiProgress = $"{d}/{t}"), _aiCts.Token);
            RefreshCategoryGrid();
            Status = $"Đã cập nhật danh mục {n} sản phẩm.";
            Log($"✔ AI: cập nhật danh mục {n} sản phẩm.");
        }
        catch (OperationCanceledException) { Status = "Đã dừng cập nhật danh mục."; }
        catch (Exception ex) { Warn("Lỗi cập nhật danh mục AI: " + ex.Message); }
        finally { AiBusy = false; AiProgress = ""; _aiCts?.Dispose(); _aiCts = null; }
    }

    [RelayCommand(CanExecute = nameof(CanRunAi))]
    private async Task UpdateCategoriesExcelAsync()
    {
        if (await ValidateAiAsync() is not { } ai) return;
        var excelPath = await FilePicker.OpenFileAsync("Chọn file Excel (cột tên sản phẩm) để gán danh mục", "Excel|*.xlsx");
        if (excelPath is null) return;

        AiBusy = true; _aiCts = new CancellationTokenSource();
        try
        {
            Log("🤖 Cập nhật danh mục (AI) cho file: " + Path.GetFileName(excelPath));
            var n = await Db.UpdateCategoriesExcelAsync(ai, CategoryDocxPath, excelPath,
                (d, t) => OnUi(() => AiProgress = $"{d}/{t}"), _aiCts.Token);
            Status = $"Đã ghi danh mục cho {n} dòng trong file.";
            Log($"✔ AI: ghi danh mục {n} dòng → {excelPath}");
        }
        catch (OperationCanceledException) { Status = "Đã dừng."; }
        catch (Exception ex) { Warn("Lỗi cập nhật danh mục Excel: " + ex.Message); }
        finally { AiBusy = false; AiProgress = ""; _aiCts?.Dispose(); _aiCts = null; }
    }

    [RelayCommand(CanExecute = nameof(AiBusy))]
    private void StopAi() => _aiCts?.Cancel();

    private async Task<AiConfig?> ValidateAiAsync()
    {
        var ai = await HubAiConfig.GetAsync();
        if (!ai.HasActiveKey) { Warn($"Chưa cấu hình API key cho {ai.Provider} (trang Cấu hình AI trên Hub)."); return null; }
        if (string.IsNullOrWhiteSpace(CategoryDocxPath) || !File.Exists(CategoryDocxPath))
        { Warn("Chọn file danh mục lá Shopee (.docx) hợp lệ trước."); return null; }
        return ai;
    }
}
