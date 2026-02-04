using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Core.Goals;

/// <summary>
/// Blacklist is enforced in WORLD XY.
/// </summary>
public interface IAreaBlacklist
{
    bool ContainsWorld(Vector3 worldPos);

    /// <summary>Returns true if pos is inside any rect; outputs that rect.</summary>
    bool TryGetContainingRect(Vector3 worldPos, out BlacklistRect rect);

    /// <summary>Returns true if segment intersects any rect (standard rule).</summary>
    bool TryGetBlockingRect(Vector3 aWorld, Vector3 bWorld, out BlacklistRect blockingRect);

    /// <summary>
    /// Same as TryGetBlockingRect but ignores a specific rect (used for "allow leaving only").
    /// </summary>
    bool TryGetBlockingRectExcluding(Vector3 aWorld, Vector3 bWorld, in BlacklistRect ignoreRect, out BlacklistRect blockingRect);
}

/// <summary>Axis-aligned rectangle in XY (used for both map and world depending on context).</summary>
public readonly record struct BlacklistRect(float MinX, float MinY, float MaxX, float MaxY)
{
    public BlacklistRect Normalized()
    {
        float minX = MathF.Min(MinX, MaxX);
        float maxX = MathF.Max(MinX, MaxX);
        float minY = MathF.Min(MinY, MaxY);
        float maxY = MathF.Max(MinY, MaxY);
        return new BlacklistRect(minX, minY, maxX, maxY);
    }

    public BlacklistRect Inflate(float amount)
    {
        return new BlacklistRect(
            MinX - amount, MinY - amount,
            MaxX + amount, MaxY + amount
        );
    }

    public float MidX => (MinX + MaxX) * 0.5f;
    public float MidY => (MinY + MaxY) * 0.5f;

    public bool Contains(Vector2 p) =>
        p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

    public bool IntersectsSegment(Vector2 a, Vector2 b)
    {
        // Fast AABB reject using segment bbox
        float segMinX = MathF.Min(a.X, b.X);
        float segMaxX = MathF.Max(a.X, b.X);
        float segMinY = MathF.Min(a.Y, b.Y);
        float segMaxY = MathF.Max(a.Y, b.Y);
        if (segMaxX < MinX || segMinX > MaxX || segMaxY < MinY || segMinY > MaxY)
            return false;

        // Endpoint inside => intersects
        if (Contains(a) || Contains(b))
            return true;

        // Check intersection with rectangle edges
        var r1 = new Vector2(MinX, MinY);
        var r2 = new Vector2(MaxX, MinY);
        var r3 = new Vector2(MaxX, MaxY);
        var r4 = new Vector2(MinX, MaxY);

        return SegmentsIntersect(a, b, r1, r2) ||
               SegmentsIntersect(a, b, r2, r3) ||
               SegmentsIntersect(a, b, r3, r4) ||
               SegmentsIntersect(a, b, r4, r1);
    }

    private static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
    {
        static float Cross(Vector2 a, Vector2 b, Vector2 c)
            => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

        static bool OnSegment(Vector2 a, Vector2 b, Vector2 p)
            => p.X >= MathF.Min(a.X, b.X) && p.X <= MathF.Max(a.X, b.X) &&
               p.Y >= MathF.Min(a.Y, b.Y) && p.Y <= MathF.Max(a.Y, b.Y);

        float d1 = Cross(p1, p2, q1);
        float d2 = Cross(p1, p2, q2);
        float d3 = Cross(q1, q2, p1);
        float d4 = Cross(q1, q2, p2);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
            ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;

        const float eps = 1e-6f;
        if (MathF.Abs(d1) < eps && OnSegment(p1, p2, q1)) return true;
        if (MathF.Abs(d2) < eps && OnSegment(p1, p2, q2)) return true;
        if (MathF.Abs(d3) < eps && OnSegment(q1, q2, p1)) return true;
        if (MathF.Abs(d4) < eps && OnSegment(q1, q2, p2)) return true;

        return false;
    }
}

public sealed class RectBlacklist : IAreaBlacklist
{
    private readonly BlacklistRect[] rects;

    public RectBlacklist(IEnumerable<BlacklistRect> rects)
    {
        this.rects = rects.Select(r => r.Normalized()).ToArray();
    }

    public bool ContainsWorld(Vector3 worldPos)
        => TryGetContainingRect(worldPos, out _);

    public bool TryGetContainingRect(Vector3 worldPos, out BlacklistRect rect)
    {
        var p = new Vector2(worldPos.X, worldPos.Y);
        foreach (var r in rects)
        {
            if (r.Contains(p))
            {
                rect = r;
                return true;
            }
        }
        rect = default;
        return false;
    }

    public bool TryGetBlockingRect(Vector3 aWorld, Vector3 bWorld, out BlacklistRect blockingRect)
        => TryGetBlockingRectExcluding(aWorld, bWorld, default, out blockingRect);

    public bool TryGetBlockingRectExcluding(Vector3 aWorld, Vector3 bWorld, in BlacklistRect ignoreRect, out BlacklistRect blockingRect)
    {
        var a = new Vector2(aWorld.X, aWorld.Y);
        var b = new Vector2(bWorld.X, bWorld.Y);

        foreach (var r in rects)
        {
            // IgnoreRect is only meaningful when the caller passes a real rect.
            // If ignoreRect is default (0,0,0,0) it still works; intersection is harmless.
            if (r.Equals(ignoreRect))
                continue;

            if (r.IntersectsSegment(a, b))
            {
                blockingRect = r;
                return true;
            }
        }

        blockingRect = default;
        return false;
    }
}
