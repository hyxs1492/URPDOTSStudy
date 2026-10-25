using DOTSUtils.Pathfinding;
using Unity.Mathematics;

namespace DOTSUtils.Samples.Pathfinding
{
    /// <summary>
    /// 鼠标地面落点与网格的对齐：把落点夹进网格的 xz 范围，再吸附到最近的可走格中心，
    /// 使写进 <see cref="AStarDemoCursorTarget"/> 的目标永远在网格内且可走。
    /// </summary>
    public static class AStarDemoCursor
    {
        /// <summary>
        /// 网格资产的便捷重载：取资产的参数与掩码。
        /// </summary>
        public static bool TrySnapToWalkableCell(float3 point, AStarGridAsset asset, out float3 snapped)
        {
            snapped = default;
            if (asset == null)
            {
                return false;
            }

            var bounds = new AABB
            {
                Center = asset.BoundsCenter,
                Extents = asset.BoundsExtents,
            };
            return TrySnapToWalkableCell(point, bounds, asset.CellSize, asset.Mask, asset.Size, out snapped);
        }

        /// <summary>
        /// 把 <paramref name="point"/> 的 xz 夹进 <paramref name="bounds"/> 覆盖的格网，
        /// 再从该格出发向外一圈圈找最近的可走格，输出它的格中心（y 取 <c>bounds.Center.y</c>）。
        /// 网格里一格可走都没有时返回 <c>false</c>。
        /// </summary>
        public static bool TrySnapToWalkableCell(float3 point, in AABB bounds, float cellSize,
                                                 byte[] mask, int2 size, out float3 snapped)
        {
            snapped = default;
            if (size.x <= 0 || size.y <= 0)
            {
                return false;
            }

            var step = math.max(cellSize, 1e-3f);
            var originX = bounds.Center.x - bounds.Extents.x;
            var originZ = bounds.Center.z - bounds.Extents.z;

            // 先夹到范围里再取整：格号一定落在 [0, size - 1]。
            var localX = math.clamp((point.x - originX) / step, 0f, size.x - 1e-4f);
            var localZ = math.clamp((point.z - originZ) / step, 0f, size.y - 1e-4f);
            var cell = new int2((int)localX, (int)localZ);

            if (!TryFindNearestWalkable(cell, mask, size, out var walkable))
            {
                return false;
            }

            snapped = new float3(
                originX + (walkable.x + 0.5f) * step,
                bounds.Center.y,
                originZ + (walkable.y + 0.5f) * step);
            return true;
        }

        /// <summary>
        /// 从 <paramref name="start"/> 出发按切比雪夫距离一圈圈向外找：环内取欧氏距离最近的可走格。
        /// 环数上限取网格长边，保证整片网格都会被搜到。
        /// </summary>
        private static bool TryFindNearestWalkable(int2 start, byte[] mask, int2 size, out int2 cell)
        {
            if (IsWalkable(start, mask, size))
            {
                cell = start;
                return true;
            }

            var maxRing = math.max(size.x, size.y);
            for (var ring = 1; ring <= maxRing; ring++)
            {
                var found = false;
                var bestCell = default(int2);
                var bestDistance = float.MaxValue;

                for (var dz = -ring; dz <= ring; dz++)
                {
                    for (var dx = -ring; dx <= ring; dx++)
                    {
                        if (math.max(math.abs(dx), math.abs(dz)) != ring)
                        {
                            continue;   // 只走本环
                        }

                        var candidate = start + new int2(dx, dz);
                        if (!IsWalkable(candidate, mask, size))
                        {
                            continue;
                        }

                        var distance = dx * dx + dz * dz;
                        if (found && distance >= bestDistance)
                        {
                            continue;
                        }

                        found = true;
                        bestDistance = distance;
                        bestCell = candidate;
                    }
                }

                if (found)
                {
                    cell = bestCell;
                    return true;
                }
            }

            cell = start;
            return false;
        }

        /// <summary>该格是否在网格内且可走；掩码为 <c>null</c> 时全部可走。</summary>
        private static bool IsWalkable(int2 cell, byte[] mask, int2 size)
        {
            if (cell.x < 0 || cell.y < 0 || cell.x >= size.x || cell.y >= size.y)
            {
                return false;
            }
            if (mask == null)
            {
                return true;
            }

            var index = cell.y * size.x + cell.x;
            return index < mask.Length && mask[index] != 0;
        }
    }
}
