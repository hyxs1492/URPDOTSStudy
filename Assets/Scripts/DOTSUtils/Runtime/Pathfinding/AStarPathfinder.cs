using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace DOTSUtils.Pathfinding
{
    /// <summary>
    /// 寻路的复用工作区：每格的标记 / g 值 / 来源，以及二叉堆。
    ///
    /// 「标记」用**世代值**（<c>openMark</c> / <c>closedMark</c>）区分，所以每次查询不需要清零整张表 ——
    /// 只把世代自增一次。工作区由调用方持有并复用（job 内用 <see cref="Allocator.Temp"/>）。
    /// </summary>
    public struct AStarScratch
    {
        private UnsafeList<int> m_Marks;
        private UnsafeList<float> m_G;
        private UnsafeList<int> m_CameFrom;
        private UnsafeList<int> m_HeapIndex;
        private UnsafeList<float> m_HeapF;
        private int m_Capacity;
        private int m_QueryId;
        private int m_OpenMark;
        private int m_ClosedMark;

        /// <summary>已分配容量（格数）。</summary>
        public int Capacity => m_Capacity;

        /// <summary>本次查询访问过的格数。</summary>
        public int VisitedCount { get; private set; }

        /// <summary>把工作区容量扩到至少 <paramref name="cells"/> 格。已够则不做事。</summary>
        public void EnsureCapacity(int cells, Allocator allocator)
        {
            if (m_Capacity >= cells && m_Marks.IsCreated)
            {
                return;
            }

            Dispose();
            m_Capacity = math.max(cells, 1);
            m_Marks = new UnsafeList<int>(m_Capacity, allocator);
            m_Marks.Resize(m_Capacity, NativeArrayOptions.ClearMemory);
            m_G = new UnsafeList<float>(m_Capacity, allocator);
            m_G.Resize(m_Capacity, NativeArrayOptions.ClearMemory);
            m_CameFrom = new UnsafeList<int>(m_Capacity, allocator);
            m_CameFrom.Resize(m_Capacity, NativeArrayOptions.ClearMemory);
            m_HeapIndex = new UnsafeList<int>(math.max(16, m_Capacity / 4), allocator);
            m_HeapF = new UnsafeList<float>(math.max(16, m_Capacity / 4), allocator);
            m_QueryId = 0;
        }

        /// <summary>开始一次新查询：推进世代、清空堆。</summary>
        public void BeginQuery()
        {
            // 世代用「两个连续值」表示 open / closed；快溢出时把标记表清零重来。
            if (m_QueryId == 0 || m_QueryId >= int.MaxValue / 2 - 2)
            {
                for (var i = 0; i < m_Marks.Length; i++)
                {
                    m_Marks[i] = 0;
                }
                m_QueryId = 1;
            }
            else
            {
                m_QueryId++;
            }

            m_OpenMark = m_QueryId * 2;
            m_ClosedMark = m_OpenMark + 1;
            m_HeapIndex.Clear();
            m_HeapF.Clear();
            VisitedCount = 0;
        }

        /// <summary>释放内存。</summary>
        public void Dispose()
        {
            if (m_Marks.IsCreated)
            {
                m_Marks.Dispose();
                m_G.Dispose();
                m_CameFrom.Dispose();
                m_HeapIndex.Dispose();
                m_HeapF.Dispose();
            }
            m_Marks = default;
            m_G = default;
            m_CameFrom = default;
            m_HeapIndex = default;
            m_HeapF = default;
            m_Capacity = 0;
        }

        // ------------------------------------------------------------------
        //  状态读写（0 = 未访问，1 = open，2 = closed）
        // ------------------------------------------------------------------

        internal int State(int index)
        {
            var mark = m_Marks[index];
            if (mark == m_OpenMark)
            {
                return 1;
            }
            return mark == m_ClosedMark ? 2 : 0;
        }

        internal void MarkOpen(int index) => m_Marks[index] = m_OpenMark;

        internal void MarkClosed(int index) => m_Marks[index] = m_ClosedMark;

        internal float G(int index) => m_G[index];

        internal void SetG(int index, float value) => m_G[index] = value;

        internal int CameFrom(int index) => m_CameFrom[index];

        internal void SetCameFrom(int index, int value) => m_CameFrom[index] = value;

        internal void CountVisited() => VisitedCount++;

        // ------------------------------------------------------------------
        //  二叉最小堆（按 f 值；存格的一维下标）
        // ------------------------------------------------------------------

        internal bool HeapIsEmpty => m_HeapIndex.Length == 0;

        internal void HeapPush(int index, float f)
        {
            m_HeapIndex.Add(index);
            m_HeapF.Add(f);
            var child = m_HeapIndex.Length - 1;
            while (child > 0)
            {
                var parent = (child - 1) >> 1;
                if (m_HeapF[parent] <= m_HeapF[child])
                {
                    break;
                }
                Swap(parent, child);
                child = parent;
            }
        }

        internal int HeapPop()
        {
            var top = m_HeapIndex[0];
            var last = m_HeapIndex.Length - 1;
            m_HeapIndex[0] = m_HeapIndex[last];
            m_HeapF[0] = m_HeapF[last];
            m_HeapIndex.Length = last;
            m_HeapF.Length = last;

            var count = m_HeapIndex.Length;
            var parent = 0;
            while (true)
            {
                var left = parent * 2 + 1;
                if (left >= count)
                {
                    break;
                }
                var right = left + 1;
                var small = right < count && m_HeapF[right] < m_HeapF[left] ? right : left;
                if (m_HeapF[parent] <= m_HeapF[small])
                {
                    break;
                }
                Swap(parent, small);
                parent = small;
            }
            return top;
        }

        private void Swap(int a, int b)
        {
            var index = m_HeapIndex[a];
            m_HeapIndex[a] = m_HeapIndex[b];
            m_HeapIndex[b] = index;
            var f = m_HeapF[a];
            m_HeapF[a] = m_HeapF[b];
            m_HeapF[b] = f;
        }
    }

    /// <summary>
    /// 网格路径求解器：在 <see cref="AStarGrid"/> 上找一条从起点到终点的格子路径，
    /// 输出为一串格中心的世界坐标（含起点与终点）。可用在 Burst job 里。
    ///
    /// <see cref="AStarConfig.Algorithm"/> 决定用哪种算法，两者共用下面这套最优优先搜索内核：
    /// <see cref="PathfindingAlgorithm.AStar"/> 用 f = g + h × <see cref="AStarConfig.HeuristicWeight"/>，
    /// <see cref="PathfindingAlgorithm.Dijkstra"/> 的启发式恒为 0（f = g），因此 <see cref="AStarConfig.HeuristicWeight"/>
    /// 对 Dijkstra 不参与计算。
    /// </summary>
    public static class AStarPathfinder
    {
        private const float k_Sqrt2 = 1.41421356f;

        /// <summary>
        /// 求路径，结果**追加**到 <paramref name="path"/>（调用方负责清空与复用）。
        /// 返回 <see cref="AStarStatus.Found"/> 时 <paramref name="path"/> 里是一条从起点到终点的折线。
        /// </summary>
        public static AStarStatus FindPath(in AStarGrid grid, float3 start, float3 end, in AStarConfig config,
                                          ref AStarScratch scratch, ref UnsafeList<float3> path)
        {
            if (!grid.IsCreated)
            {
                return AStarStatus.NoGrid;
            }

            var startCell = grid.WorldToCell(start);
            var endCell = grid.WorldToCell(end);
            if (!grid.InBounds(startCell) || !grid.IsWalkable(startCell))
            {
                return AStarStatus.StartInvalid;
            }
            if (!grid.InBounds(endCell) || !grid.IsWalkable(endCell))
            {
                return AStarStatus.EndInvalid;
            }

            var startIndex = grid.Index(startCell);
            var endIndex = grid.Index(endCell);
            if (startIndex == endIndex)
            {
                path.Add(grid.CellCenter(startCell));
                return AStarStatus.Found;
            }

            scratch.BeginQuery();

            var limit = config.MaxVisitedNodes > 0 ? config.MaxVisitedNodes : grid.CellCount;
            // 两种算法共用同一套最优优先搜索：差别只在入堆的 f 值。
            // Dijkstra 的启发式恒为 0（权重取 0），HeuristicWeight 因此不参与。
            var weight = config.Algorithm == PathfindingAlgorithm.Dijkstra ? 0f : math.max(1f, config.HeuristicWeight);
            var eight = grid.Neighbourhood == AStarNeighbourhood.Eight;

            scratch.SetG(startIndex, 0f);
            scratch.MarkOpen(startIndex);
            scratch.HeapPush(startIndex, Heuristic(startCell, endCell, eight) * weight);

            var found = false;
            while (!scratch.HeapIsEmpty)
            {
                var current = scratch.HeapPop();
                if (scratch.State(current) == 2)
                {
                    continue;   // 堆里的陈旧条目（同一格被更优路径重新入堆过）
                }
                scratch.MarkClosed(current);

                if (scratch.VisitedCount >= limit)
                {
                    return AStarStatus.ExceededLimit;
                }
                scratch.CountVisited();

                if (current == endIndex)
                {
                    found = true;
                    break;
                }

                var cell = grid.Cell(current);
                var count = eight ? 8 : 4;
                for (var k = 0; k < count; k++)
                {
                    var offset = Offset(k);
                    var next = cell + offset;
                    if (!grid.IsWalkable(next))
                    {
                        continue;
                    }

                    var diagonal = offset.x != 0 && offset.y != 0;
                    if (diagonal && !grid.AllowCornerCutting)
                    {
                        if (!grid.IsWalkable(new int2(cell.x + offset.x, cell.y)) ||
                            !grid.IsWalkable(new int2(cell.x, cell.y + offset.y)))
                        {
                            continue;
                        }
                    }

                    var nextIndex = grid.Index(next);
                    if (scratch.State(nextIndex) == 2)
                    {
                        continue;
                    }

                    var step = diagonal ? k_Sqrt2 : 1f;
                    var tentative = scratch.G(current) + step;
                    var state = scratch.State(nextIndex);
                    if (state == 1 && tentative >= scratch.G(nextIndex))
                    {
                        continue;
                    }

                    scratch.SetG(nextIndex, tentative);
                    scratch.SetCameFrom(nextIndex, current);
                    scratch.MarkOpen(nextIndex);
                    scratch.HeapPush(nextIndex, tentative + Heuristic(next, endCell, eight) * weight);
                }
            }

            if (!found)
            {
                return AStarStatus.NoPath;
            }

            // 回溯：从终点沿 CameFrom 走到起点，得到「终点 → 起点」，写进 path 后再原地反转。
            var first = path.Length;
            var walk = endIndex;
            while (true)
            {
                path.Add(grid.CellCenter(grid.Cell(walk)));
                if (walk == startIndex)
                {
                    break;
                }
                walk = scratch.CameFrom(walk);
            }
            Reverse(path, first);

            if (config.SmoothPath)
            {
                Smooth(grid, ref path, first);
            }
            return AStarStatus.Found;
        }

        /// <summary>便捷版：自己开一块 <see cref="Allocator.Temp"/> 工作区（只适合主线程 / 编辑器）。</summary>
        public static AStarStatus FindPath(in AStarGrid grid, float3 start, float3 end, in AStarConfig config,
                                          ref UnsafeList<float3> path)
        {
            var scratch = new AStarScratch();
            scratch.EnsureCapacity(grid.CellCount, Allocator.Temp);
            var status = FindPath(grid, start, end, config, ref scratch, ref path);
            scratch.Dispose();
            return status;
        }

        private static int2 Offset(int k)
        {
            switch (k)
            {
                case 0: return new int2(1, 0);
                case 1: return new int2(0, 1);
                case 2: return new int2(-1, 0);
                case 3: return new int2(0, -1);
                case 4: return new int2(1, 1);
                case 5: return new int2(-1, 1);
                case 6: return new int2(-1, -1);
                default: return new int2(1, -1);
            }
        }

        private static float Heuristic(int2 from, int2 to, bool eight)
        {
            var dx = math.abs(from.x - to.x);
            var dy = math.abs(from.y - to.y);
            if (!eight)
            {
                return dx + dy;
            }
            var min = math.min(dx, dy);
            return dx + dy + (k_Sqrt2 - 2f) * min;
        }

        private static void Reverse(UnsafeList<float3> path, int from)
        {
            var i = from;
            var j = path.Length - 1;
            while (i < j)
            {
                var tmp = path[i];
                path[i] = path[j];
                path[j] = tmp;
                i++;
                j--;
            }
        }

        /// <summary>视线拉直：从左端开始，能直达就尽量跳到最远的那一点。</summary>
        private static void Smooth(in AStarGrid grid, ref UnsafeList<float3> path, int from)
        {
            var count = path.Length - from;
            if (count <= 2)
            {
                return;
            }

            var write = from;
            var anchor = from;
            for (var i = from + 2; i < path.Length; i++)
            {
                var lineOfSight = HasLineOfSight(grid, grid.WorldToCell(path[anchor]), grid.WorldToCell(path[i]));
                if (lineOfSight && i != path.Length - 1)
                {
                    continue;
                }

                var keep = lineOfSight ? i : i - 1;
                write++;
                path[write] = path[keep];
                anchor = keep;
                if (keep == path.Length - 1)
                {
                    path.Length = write + 1;
                    return;
                }
            }
            path.Length = write + 1;
        }

        /// <summary>两格之间是否有一条只穿过可走格的直线（Bresenham 走格）。</summary>
        private static bool HasLineOfSight(in AStarGrid grid, int2 a, int2 b)
        {
            var dx = math.abs(b.x - a.x);
            var dy = math.abs(b.y - a.y);
            var sx = a.x < b.x ? 1 : -1;
            var sy = a.y < b.y ? 1 : -1;
            var x = a.x;
            var y = a.y;
            var err = dx - dy;

            while (true)
            {
                if (!grid.IsWalkable(new int2(x, y)))
                {
                    return false;
                }
                if (x == b.x && y == b.y)
                {
                    return true;
                }

                var e2 = 2 * err;
                var stepX = e2 > -dy;
                var stepY = e2 < dx;
                if (stepX && stepY && !grid.AllowCornerCutting)
                {
                    if (!grid.IsWalkable(new int2(x + sx, y)) || !grid.IsWalkable(new int2(x, y + sy)))
                    {
                        return false;
                    }
                }
                if (stepX)
                {
                    err -= dy;
                    x += sx;
                }
                if (stepY)
                {
                    err += dx;
                    y += sy;
                }
            }
        }
    }
}
