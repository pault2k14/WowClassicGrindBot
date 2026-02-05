using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharedLib;

namespace Core.AreaBlacklist;

public static class BlacklistConversion
{
    public static RectBlacklist BuildWorldBlacklistFromMapRects(
        IEnumerable<BlacklistRect> mapRects,
        WorldMapArea wma)
    {
        var worldRects = mapRects.Select(r =>
        {
            var rn = r.Normalized();

            var p1m = new Vector3(rn.MinX, rn.MinY, 0);
            var p2m = new Vector3(rn.MaxX, rn.MaxY, 0);

            var p1w = WorldMapAreaDB.ToWorld_FlipXY(p1m, wma);
            var p2w = WorldMapAreaDB.ToWorld_FlipXY(p2m, wma);

            return new BlacklistRect(p1w.X, p1w.Y, p2w.X, p2w.Y).Normalized();
        });

        return new RectBlacklist(worldRects);
    }
}
