using System.Collections.ObjectModel;

namespace Shopee.Suite.Infrastructure;

/// <summary>
/// Gói idiom lặp ở nhiều ViewModel: "kho (Store) đổi → dựng lại một <see cref="ObservableCollection{T}"/>
/// từ danh sách model, GIỮ selection theo Id". Chiếu <typeparamref name="TModel"/> (model trong store)
/// thành <typeparamref name="TVm"/> (item VM) vào <c>target</c> theo THỨ TỰ nguồn, rồi đặt lại phần tử
/// đang chọn theo Id cũ (mất → phần tử đầu, danh sách rỗng → null).
///
/// <para><see cref="Rebuild"/> dựng lại KHÔNG điều kiện (như <c>Reload()</c> cũ). <see cref="ReloadIfChanged"/>
/// thêm guard "id-set không đổi thì bỏ qua rebuild" (tránh mất focus lúc gõ + tránh vòng Save→Changed→Reload
/// vô ích) và trả false để caller tự chạy fast-path cập-nhật-tại-chỗ nếu cần.</para>
///
/// <para>CHỈ lo chiếu + selection: KHÔNG marshal về UI thread (caller gọi trong UI thread như code cũ) và
/// KHÔNG tự đăng ký <c>Store.Changed</c> — mỗi VM giữ handler riêng vì thường kèm việc khác (đặt Status,
/// kéo ảnh Hub, chặn khi đang chạy…).</para>
/// </summary>
internal sealed class ObservableProjection<TModel, TVm>
    where TVm : class
{
    private readonly ObservableCollection<TVm> _target;
    private readonly Func<IEnumerable<TModel>> _source;
    private readonly Func<TModel, TVm> _factory;
    private readonly Func<TVm, string> _vmId;
    private readonly Func<TModel, string> _modelId;
    private readonly Func<TVm?> _getSelected;
    private readonly Action<TVm?> _setSelected;

    public ObservableProjection(
        ObservableCollection<TVm> target,
        Func<IEnumerable<TModel>> source,
        Func<TModel, TVm> factory,
        Func<TVm, string> vmId,
        Func<TModel, string> modelId,
        Func<TVm?> getSelected,
        Action<TVm?> setSelected)
    {
        _target = target;
        _source = source;
        _factory = factory;
        _vmId = vmId;
        _modelId = modelId;
        _getSelected = getSelected;
        _setSelected = setSelected;
    }

    /// <summary>Dựng lại KHÔNG điều kiện: nhớ Id đang chọn → xoá sạch → chiếu từng model theo thứ tự nguồn →
    /// chọn lại phần tử có Id trùng (không còn → phần tử đầu, rỗng → null).</summary>
    public void Rebuild()
    {
        var sel = _getSelected();
        var prevId = sel is null ? null : _vmId(sel);
        _target.Clear();
        foreach (var m in _source())
            _target.Add(_factory(m));
        _setSelected(_target.FirstOrDefault(v => _vmId(v) == prevId) ?? _target.FirstOrDefault());
    }

    /// <summary>Chỉ dựng lại khi TẬP Id (nguồn) khác tập Id hiện có trong <c>target</c>. true = đã rebuild;
    /// false = tập Id y nguyên (caller tự chạy fast-path cập nhật tại chỗ nếu cần).</summary>
    public bool ReloadIfChanged()
    {
        var sourceIds = new HashSet<string>(_source().Select(_modelId));
        var targetIds = new HashSet<string>(_target.Select(_vmId));
        if (sourceIds.SetEquals(targetIds)) return false;
        Rebuild();
        return true;
    }
}
