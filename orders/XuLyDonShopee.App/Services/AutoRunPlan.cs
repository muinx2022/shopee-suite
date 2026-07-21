using System.Collections.Generic;

namespace XuLyDonShopee.App.Services;

/// <summary>Một hành động autorun chạy trên phiên của một tài khoản trong lô.</summary>
public enum AutoRunActionKind
{
    /// <summary>Xử lý đơn (arrange địa chỉ + in phiếu — GHI lên Shopee). Chỉ chạy khi bật cờ.</summary>
    Process,

    /// <summary>Sync đơn hàng về DB. Chỉ chạy khi bật cờ.</summary>
    Sync,

    /// <summary>Kiểm tra đơn (đọc số "Chờ Lấy Hàng"). LUÔN chạy.</summary>
    Check
}

/// <summary>
/// Quyết định THỨ TỰ hành động cho một tài khoản theo các cờ cấu hình. Hàm THUẦN — tách khỏi scheduler để
/// test được. Thứ tự chốt: <b>Xử lý đơn → Sync → Kiểm tra</b> (arrange đơn trước, rồi lưu snapshot đơn sau
/// xử lý, rồi đọc số cuối). Kiểm tra LUÔN đứng cuối và LUÔN có mặt.
/// </summary>
public static class AutoRunPlan
{
    /// <summary>
    /// Danh sách hành động theo thứ tự cho một tài khoản: <see cref="AutoRunActionKind.Process"/> (nếu
    /// <paramref name="doProcess"/>), rồi <see cref="AutoRunActionKind.Sync"/> (nếu <paramref name="doSync"/>),
    /// rồi LUÔN <see cref="AutoRunActionKind.Check"/>.
    /// </summary>
    public static IReadOnlyList<AutoRunActionKind> ActionsFor(bool doProcess, bool doSync)
    {
        var list = new List<AutoRunActionKind>(3);
        if (doProcess)
        {
            list.Add(AutoRunActionKind.Process);
        }

        if (doSync)
        {
            list.Add(AutoRunActionKind.Sync);
        }

        list.Add(AutoRunActionKind.Check);
        return list;
    }
}
