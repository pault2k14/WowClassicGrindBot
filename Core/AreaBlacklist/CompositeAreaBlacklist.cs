using System.Numerics;

namespace Core.AreaBlacklist;

/// <summary>
/// Combines two <see cref="IAreaBlacklist"/> instances — a static route blacklist
/// and a dynamic one built from runtime-recorded stuck rects — into a single
/// <see cref="IAreaBlacklist"/> that satisfies all Navigation query methods.
///
/// Either inner list may be null (treated as empty).
/// </summary>
public sealed class CompositeAreaBlacklist : IAreaBlacklist
{
    private readonly IAreaBlacklist? _static;
    private readonly IAreaBlacklist? _dynamic;

    public CompositeAreaBlacklist(IAreaBlacklist? staticList, IAreaBlacklist? dynamicList)
    {
        _static = staticList;
        _dynamic = dynamicList;
    }

    public bool ContainsWorld(Vector3 worldPos)
        => (_static?.ContainsWorld(worldPos) == true) ||
           (_dynamic?.ContainsWorld(worldPos) == true);

    public bool TryGetContainingRect(Vector3 worldPos, out BlacklistRect rect)
    {
        if (_static?.TryGetContainingRect(worldPos, out rect) == true) return true;
        if (_dynamic?.TryGetContainingRect(worldPos, out rect) == true) return true;
        rect = default;
        return false;
    }

    public bool TryGetContainingRectInflated(Vector3 worldPos, float inflateBy, out BlacklistRect rect)
    {
        if (_static?.TryGetContainingRectInflated(worldPos, inflateBy, out rect) == true) return true;
        if (_dynamic?.TryGetContainingRectInflated(worldPos, inflateBy, out rect) == true) return true;
        rect = default;
        return false;
    }

    public bool TryGetBlockingRect(Vector3 aWorld, Vector3 bWorld, out BlacklistRect blockingRect)
    {
        if (_static?.TryGetBlockingRect(aWorld, bWorld, out blockingRect) == true) return true;
        if (_dynamic?.TryGetBlockingRect(aWorld, bWorld, out blockingRect) == true) return true;
        blockingRect = default;
        return false;
    }

    public bool TryGetBlockingRectExcluding(
        Vector3 aWorld, Vector3 bWorld,
        in BlacklistRect ignoreRect,
        out BlacklistRect blockingRect)
    {
        if (_static?.TryGetBlockingRectExcluding(aWorld, bWorld, ignoreRect, out blockingRect) == true) return true;
        if (_dynamic?.TryGetBlockingRectExcluding(aWorld, bWorld, ignoreRect, out blockingRect) == true) return true;
        blockingRect = default;
        return false;
    }
}
