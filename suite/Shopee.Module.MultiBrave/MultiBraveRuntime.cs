using OpenMultiBraveLauncherV3;

namespace Shopee.Modules.MultiBrave;

/// <summary>Khởi tạo/dọn dẹp runtime của engine v31 (cấp port block, thư mục session). Gọi 1 lần
/// lúc app khởi động và lúc thoát.</summary>
public static class MultiBraveRuntime
{
    public static void Initialize() => AppSession.Initialize();
    public static void Cleanup() => AppSession.Cleanup();
}
