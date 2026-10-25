using Unity.Collections;
using Unity.Mathematics;

namespace DOTSUtils.Pathfinding
{
    /// <summary>
    /// 一条静态障碍物边界线段（xz 平面）：起点 <see cref="A"/>、终点 <see cref="B"/>。
    /// <b>内侧（障碍物一侧）是 A→B 的左侧</b>：<c>det(B - A, P - A) &gt; 0</c> 的点在障碍物内部。
    /// 布局与 <c>DOTSUtils.Rvo.RvoObstacleSegment</c> 一致，由调用方逐字段转换；本类型不引用 Rvo 类型。
    /// </summary>
    public struct PathObstacleSegment
    {
        /// <summary>起点（世界 xz）。</summary>
        public float2 A;

        /// <summary>终点（世界 xz）。</summary>
        public float2 B;
    }

    /// <summary>
    /// 一个闭合静态障碍物多边形在扁平顶点数组里的位置：<see cref="Start"/> 是首顶点下标、<see cref="Count"/> 是顶点数。
    /// 顶点按下标顺序首尾相接成环（边 i = 顶点 i → 顶点 i+1），<b>内侧（阻挡区）在 顶点 → 下一顶点 的左侧</b>；
    /// 被阻挡区包围的洞也各自成环，洞的环同样按这条约定摆放，所以洞的环走向与外环相反。
    ///
    /// 照 RVO2 <c>RVOSimulator::addObstacle</c> 的约定：边由 <c>vertices[i] → vertices[i+1]</c> 给出、
    /// <c>prev</c> / <c>next</c> 在环内首尾相接、<c>Count == 2</c> 的零厚度退化多边形合法（两条边都算凸）。
    /// </summary>
    public struct PathObstaclePolygon
    {
        /// <summary>首顶点在扁平顶点数组里的下标。</summary>
        public int Start;

        /// <summary>顶点数（&gt;= 2）。</summary>
        public int Count;

        /// <summary>环内顶点 <paramref name="vertex"/>（扁平下标）的前一个顶点下标。</summary>
        public int PrevVertex(int vertex) => Start + Wrap(vertex - Start - 1);

        /// <summary>环内顶点 <paramref name="vertex"/>（扁平下标）的后一个顶点下标。</summary>
        public int NextVertex(int vertex) => Start + Wrap(vertex - Start + 1);

        /// <summary>把环内偏移折回 <c>[0, Count)</c>。</summary>
        private int Wrap(int local) => Count > 0 ? ((local % Count) + Count) % Count : 0;
    }

    /// <summary>
    /// 把 <see cref="AStarGrid"/> 的阻挡掩码提取成静态障碍物边界：每个阻挡格与相邻可走格（以及网格外边界）
    /// 之间的那条格边输出成一条线段，同一行 / 列上连续的共线格边再合并成一条长线段。
    /// 线段方向按 <see cref="PathObstacleSegment"/> 的内侧约定摆放（阻挡格中心在 A→B 的左侧）。
    /// 另外提供闭合多边形形态（<c>BuildPolygons</c>）：把同一组单位格边接成首尾相接的闭环，
    /// 供 RVO2 形态的静态障碍物使用。
    /// 纯函数、不分配托管内存，job 与编辑器两侧都能调用。
    /// </summary>
    public static class PathObstacleBuilder
    {
        /// <summary>单位格边的四个方向：0 = +x、1 = +z、2 = -x、3 = -z（按逆时针排列）。</summary>
        private const int k_DirectionCount = 4;

        /// <summary>线段条数上限（每条格边各自成段时的条数），用来给输出缓冲预留容量。</summary>
        public static int MaxSegmentCount([ReadOnly] in AStarGrid grid)
            => grid.IsCreated ? 4 * math.max(0, grid.CellCount) : 0;

        /// <summary>
        /// 把 <paramref name="grid"/> 的阻挡边界**追加**到 <paramref name="segments"/> 末尾，返回追加的条数。
        /// 网格未创建 / 尺寸与格子边长不合法、或 <paramref name="segments"/> 未创建时返回 0 且不写入。
        /// </summary>
        public static int Build([ReadOnly] in AStarGrid grid, NativeList<PathObstacleSegment> segments)
        {
            if (!grid.IsCreated || !segments.IsCreated)
            {
                return 0;
            }

            var size = grid.Size;
            var cellSize = grid.CellSize;
            var min = grid.Min;
            if (size.x <= 0 || size.y <= 0 || cellSize <= 0f ||
                !math.isfinite(cellSize) || !math.all(math.isfinite(min)))
            {
                return 0;
            }

            var added = 0;

            // 水平格边：zLine 是格线行号，0..size.y。
            for (var zLine = 0; zLine <= size.y; zLine++)
            {
                // 阻挡格在线段 +z 侧 → 方向 +x。
                var runStart = -1;
                for (var x = 0; x <= size.x; x++)
                {
                    if (x < size.x && IsSolid(in grid, x, zLine) && IsOpen(in grid, x, zLine - 1))
                    {
                        if (runStart < 0)
                        {
                            runStart = x;
                        }
                        continue;
                    }

                    if (runStart >= 0)
                    {
                        segments.Add(new PathObstacleSegment
                        {
                            A = new float2(min.x + runStart * cellSize, min.z + zLine * cellSize),
                            B = new float2(min.x + x * cellSize, min.z + zLine * cellSize),
                        });
                        added++;
                        runStart = -1;
                    }
                }

                // 阻挡格在线段 -z 侧 → 方向 -x。
                runStart = -1;
                for (var x = 0; x <= size.x; x++)
                {
                    if (x < size.x && IsSolid(in grid, x, zLine - 1) && IsOpen(in grid, x, zLine))
                    {
                        if (runStart < 0)
                        {
                            runStart = x;
                        }
                        continue;
                    }

                    if (runStart >= 0)
                    {
                        segments.Add(new PathObstacleSegment
                        {
                            A = new float2(min.x + x * cellSize, min.z + zLine * cellSize),
                            B = new float2(min.x + runStart * cellSize, min.z + zLine * cellSize),
                        });
                        added++;
                        runStart = -1;
                    }
                }
            }

            // 竖直格边：xLine 是格线列号，0..size.x。
            for (var xLine = 0; xLine <= size.x; xLine++)
            {
                // 阻挡格在线段 -x 侧 → 方向 +z。
                var runStart = -1;
                for (var z = 0; z <= size.y; z++)
                {
                    if (z < size.y && IsSolid(in grid, xLine - 1, z) && IsOpen(in grid, xLine, z))
                    {
                        if (runStart < 0)
                        {
                            runStart = z;
                        }
                        continue;
                    }

                    if (runStart >= 0)
                    {
                        segments.Add(new PathObstacleSegment
                        {
                            A = new float2(min.x + xLine * cellSize, min.z + runStart * cellSize),
                            B = new float2(min.x + xLine * cellSize, min.z + z * cellSize),
                        });
                        added++;
                        runStart = -1;
                    }
                }

                // 阻挡格在线段 +x 侧 → 方向 -z。
                runStart = -1;
                for (var z = 0; z <= size.y; z++)
                {
                    if (z < size.y && IsSolid(in grid, xLine, z) && IsOpen(in grid, xLine - 1, z))
                    {
                        if (runStart < 0)
                        {
                            runStart = z;
                        }
                        continue;
                    }

                    if (runStart >= 0)
                    {
                        segments.Add(new PathObstacleSegment
                        {
                            A = new float2(min.x + xLine * cellSize, min.z + z * cellSize),
                            B = new float2(min.x + xLine * cellSize, min.z + runStart * cellSize),
                        });
                        added++;
                        runStart = -1;
                    }
                }
            }

            return added;
        }

        /// <summary>
        /// 托管数组重载（编辑器 / 测试用）：按网格规模建数组并写入边界线段，返回实际条数；
        /// 网格为空时返回 0 且 <paramref name="segments"/> 是空数组。
        /// </summary>
        public static int Build([ReadOnly] in AStarGrid grid, out PathObstacleSegment[] segments)
        {
            segments = System.Array.Empty<PathObstacleSegment>();

            var capacity = MaxSegmentCount(in grid);
            if (capacity <= 0)
            {
                return 0;
            }

            var list = new NativeList<PathObstacleSegment>(capacity, Allocator.Temp);
            try
            {
                var count = Build(in grid, list);
                if (count <= 0)
                {
                    return 0;
                }

                segments = new PathObstacleSegment[count];
                for (var i = 0; i < count; i++)
                {
                    segments[i] = list[i];
                }
                return count;
            }
            finally
            {
                list.Dispose();
            }
        }

        /// <summary>
        /// 顶点数上界：每个输出的多边形顶点都是一条单位格边的起点，而单位格边至多 <c>4 * 格数</c> 条。
        /// </summary>
        public static int MaxVertexCount([ReadOnly] in AStarGrid grid)
            => grid.IsCreated ? k_DirectionCount * math.max(0, grid.CellCount) : 0;

        /// <summary>
        /// 多边形数上界：每个闭环至少 4 条单位格边，而单位格边至多 <c>4 * 格数</c> 条。
        /// </summary>
        public static int MaxPolygonCount([ReadOnly] in AStarGrid grid)
            => grid.IsCreated ? math.max(0, grid.CellCount) : 0;

        /// <summary>
        /// 把 <paramref name="grid"/> 的阻挡边界**追加**成闭合多边形，返回追加的多边形个数：顶点扁平写进
        /// <paramref name="vertices"/>，每个多边形的 (起始下标, 顶点数) 写进 <paramref name="polygons"/>。
        ///
        /// 一个多边形 = 一组首尾相接、走向一致的单位格边（阻挡格在行进方向的左侧）；走到下一个角点时按
        /// 「左转 → 直行 → 右转 → 掉头」的顺序取第一条存在的格边，相邻同向的格边合并成一条长边，
        /// 合并后对齐的中间顶点丢掉；行程中重复经过的角点在环内拆成各自独立的闭环。被阻挡区包围的洞也各自成环。
        /// 多边形顶点序满足：洞之外的环（阻挡区在环内）逆时针、洞的环（阻挡区在环外）顺时针 —— 两者都是「阻挡区在左侧」。
        ///
        /// 网格未创建 / 尺寸与格子边长不合法 / 输出列表未创建时返回 0 且不写入；空网格或全可走时返回 0。
        /// 纯函数、不分配托管内存、不走异常路径，job 与编辑器两侧都能调用。
        /// </summary>
        public static int BuildPolygons([ReadOnly] in AStarGrid grid, NativeList<float2> vertices,
                                        NativeList<PathObstaclePolygon> polygons)
        {
            if (!grid.IsCreated || !vertices.IsCreated || !polygons.IsCreated)
            {
                return 0;
            }

            var size = grid.Size;
            var cellSize = grid.CellSize;
            var min = grid.Min;
            if (size.x <= 0 || size.y <= 0 || cellSize <= 0f ||
                !math.isfinite(cellSize) || !math.all(math.isfinite(min)))
            {
                return 0;
            }

            var cornersX = size.x + 1;
            var cornersZ = size.y + 1;
            var cornerCount = (long)cornersX * cornersZ;
            var stateCount = cornerCount * k_DirectionCount;
            if (stateCount > int.MaxValue)
            {
                return 0;
            }

            // 每条 (角点, 方向) 一个状态：0 = 不是格边、1 = 未用、2 = 已用。
            var states = new NativeArray<byte>((int)stateCount, Allocator.Temp);
            var stamps = new NativeArray<int>((int)cornerCount, Allocator.Temp);
            var positions = new NativeArray<int>((int)cornerCount, Allocator.Temp);
            var stack = new NativeList<int>(64, Allocator.Temp);
            try
            {
                var total = MarkEdges(in grid, states, cornersX, cornersZ);
                var added = 0;
                var walkId = 0;

                for (var cz = 0; cz < cornersZ; cz++)
                {
                    for (var cx = 0; cx < cornersX; cx++)
                    {
                        for (var startDir = 0; startDir < k_DirectionCount; startDir++)
                        {
                            if (states[EdgeIndex(cornersX, cx, cz, startDir)] != 1)
                            {
                                continue;
                            }

                            walkId++;
                            stack.Clear();

                            var currentX = cx;
                            var currentZ = cz;
                            var direction = startDir;
                            var steps = 0;
                            var closed = false;

                            while (steps <= total)
                            {
                                states[EdgeIndex(cornersX, currentX, currentZ, direction)] = 2;

                                // 再次经过走过的角点：把栈顶这一段拆成一个闭环；重复角点本身留在栈里当下一条边的起点。
                                var corner = (currentZ * cornersX) + currentX;
                                if (stamps[corner] == walkId)
                                {
                                    var at = positions[corner];
                                    added += EmitRing(stack, at, stack.Length - at, cornersX, in min, cellSize,
                                                      vertices, polygons);
                                    for (var i = at + 1; i < stack.Length; i++)
                                    {
                                        stamps[stack[i]] = 0;
                                    }
                                    stack.Length = at + 1;
                                }
                                else
                                {
                                    stamps[corner] = walkId;
                                    positions[corner] = stack.Length;
                                    stack.Add(corner);
                                }

                                var step = Step(direction);
                                var nextX = currentX + step.x;
                                var nextZ = currentZ + step.y;
                                var nextDir = FindSuccessor(in grid, nextX, nextZ, direction);
                                steps++;

                                if (nextDir < 0)
                                {
                                    break;
                                }
                                if (nextX == cx && nextZ == cz && nextDir == startDir)
                                {
                                    closed = true;
                                    break;
                                }

                                currentX = nextX;
                                currentZ = nextZ;
                                direction = nextDir;
                            }

                            if (!closed)
                            {
                                continue;
                            }

                            // 走完一圈后栈里剩下的是一个闭环。
                            added += EmitRing(stack, 0, stack.Length, cornersX, in min, cellSize, vertices, polygons);
                            for (var i = 0; i < stack.Length; i++)
                            {
                                stamps[stack[i]] = 0;
                            }
                        }
                    }
                }

                return added;
            }
            finally
            {
                stack.Dispose();
                positions.Dispose();
                stamps.Dispose();
                states.Dispose();
            }
        }

        /// <summary>
        /// 托管数组重载（编辑器 / 测试用）：建两个数组装下全部顶点与多边形，返回多边形个数；
        /// 网格为空时返回 0 且两个数组都是空数组。
        /// </summary>
        public static int BuildPolygons([ReadOnly] in AStarGrid grid, out float2[] vertices,
                                        out PathObstaclePolygon[] polygons)
        {
            vertices = System.Array.Empty<float2>();
            polygons = System.Array.Empty<PathObstaclePolygon>();

            var vertexCapacity = MaxVertexCount(in grid);
            var polygonCapacity = MaxPolygonCount(in grid);
            if (vertexCapacity <= 0 || polygonCapacity <= 0)
            {
                return 0;
            }

            var vertexList = new NativeList<float2>(vertexCapacity, Allocator.Temp);
            var polygonList = new NativeList<PathObstaclePolygon>(polygonCapacity, Allocator.Temp);
            try
            {
                var count = BuildPolygons(in grid, vertexList, polygonList);
                if (count <= 0)
                {
                    return 0;
                }

                vertices = new float2[vertexList.Length];
                for (var i = 0; i < vertexList.Length; i++)
                {
                    vertices[i] = vertexList[i];
                }

                polygons = new PathObstaclePolygon[count];
                for (var i = 0; i < count; i++)
                {
                    polygons[i] = polygonList[i];
                }
                return count;
            }
            finally
            {
                polygonList.Dispose();
                vertexList.Dispose();
            }
        }

        /// <summary>该格是否是网格内的阻挡格（网格外返回 false）。</summary>
        private static bool IsSolid([ReadOnly] in AStarGrid grid, int x, int z)
        {
            var size = grid.Size;
            if (x < 0 || z < 0 || x >= size.x || z >= size.y)
            {
                return false;
            }
            return !grid.IsWalkable(new int2(x, z));
        }

        /// <summary>该格是否不是阻挡格（可走格或网格外都返回 true）。</summary>
        private static bool IsOpen([ReadOnly] in AStarGrid grid, int x, int z) => !IsSolid(in grid, x, z);

        /// <summary>角点 (x, z) 朝 <paramref name="direction"/> 的格边在该角点状态数组里的下标。</summary>
        private static int EdgeIndex(int cornersX, int x, int z, int direction)
            => (((z * cornersX) + x) * k_DirectionCount) + direction;

        /// <summary>方向对应的一个角点位移。</summary>
        private static int2 Step(int direction)
        {
            switch (direction)
            {
                case 0:
                    return new int2(1, 0);
                case 1:
                    return new int2(0, 1);
                case 2:
                    return new int2(-1, 0);
                default:
                    return new int2(0, -1);
            }
        }

        /// <summary>
        /// 角点 (x, z) 朝 <paramref name="direction"/> 处是否有一条单位格边：该格边的左侧必须是阻挡格、
        /// 右侧必须是可走格（网格外算可走格）。内侧在左的约定见 <see cref="PathObstacleSegment"/>。
        /// </summary>
        private static bool EdgeExists([ReadOnly] in AStarGrid grid, int x, int z, int direction)
        {
            switch (direction)
            {
                case 0:
                    return IsSolid(in grid, x, z) && IsOpen(in grid, x, z - 1);
                case 1:
                    return IsSolid(in grid, x - 1, z) && IsOpen(in grid, x, z);
                case 2:
                    return IsSolid(in grid, x - 1, z - 1) && IsOpen(in grid, x - 1, z);
                default:
                    return IsSolid(in grid, x, z - 1) && IsOpen(in grid, x - 1, z - 1);
            }
        }

        /// <summary>标出全部单位格边（状态置 1）并返回条数。</summary>
        private static int MarkEdges([ReadOnly] in AStarGrid grid, NativeArray<byte> states, int cornersX, int cornersZ)
        {
            var total = 0;
            for (var z = 0; z < cornersZ; z++)
            {
                for (var x = 0; x < cornersX; x++)
                {
                    for (var direction = 0; direction < k_DirectionCount; direction++)
                    {
                        if (!EdgeExists(in grid, x, z, direction))
                        {
                            continue;
                        }
                        states[EdgeIndex(cornersX, x, z, direction)] = 1;
                        total++;
                    }
                }
            }
            return total;
        }

        /// <summary>
        /// 角点 (x, z) 上沿着 <paramref name="inDirection"/> 进来的下一条边：按「左转 → 直行 → 右转 → 掉头」
        /// 的顺序依次看四个方向，取第一条存在的格边（只有一条候选时就是它本身）。没有候选时返回 -1。
        /// </summary>
        private static int FindSuccessor([ReadOnly] in AStarGrid grid, int x, int z, int inDirection)
        {
            for (var k = 0; k < k_DirectionCount; k++)
            {
                var direction = (inDirection + 1 - k + k_DirectionCount) % k_DirectionCount;
                if (EdgeExists(in grid, x, z, direction))
                {
                    return direction;
                }
            }
            return -1;
        }

        /// <summary>
        /// 把 <paramref name="corners"/> 里 <c>[start, start + count)</c> 这一段当成一个闭环（末角点接回首角点），
        /// 丢掉与前一条边同向的共线顶点后把剩下的顶点追加到 <paramref name="vertices"/>；
        /// 顶点数 &gt;= 2 时再往 <paramref name="polygons"/> 追加一条记录并返回 1，否则把顶点回滚并返回 0。
        /// </summary>
        private static int EmitRing(NativeList<int> corners, int start, int count, int cornersX, in float3 min,
                                   float cellSize, NativeList<float2> vertices, NativeList<PathObstaclePolygon> polygons)
        {
            if (count < 2)
            {
                return 0;
            }

            var first = vertices.Length;
            var added = 0;
            for (var i = 0; i < count; i++)
            {
                var previous = corners[start + ((i + count - 1) % count)];
                var current = corners[start + i];
                var next = corners[start + ((i + 1) % count)];

                var inStep = StepOf(previous, current, cornersX);
                var outStep = StepOf(current, next, cornersX);
                if (inStep.x == outStep.x && inStep.y == outStep.y)
                {
                    continue;
                }

                vertices.Add(new float2(
                    min.x + (current % cornersX) * cellSize,
                    min.z + (current / cornersX) * cellSize));
                added++;
            }

            if (added < 2)
            {
                vertices.Length = first;
                return 0;
            }

            polygons.Add(new PathObstaclePolygon { Start = first, Count = added });
            return 1;
        }

        /// <summary>两个相邻角点之间的单位位移（格坐标）。</summary>
        private static int2 StepOf(int fromCorner, int toCorner, int cornersX)
            => new int2((toCorner % cornersX) - (fromCorner % cornersX),
                        (toCorner / cornersX) - (fromCorner / cornersX));
    }
}
