using System.Collections.Generic;
using Unity.Mathematics;

namespace DOTSUtils.Samples.Pathfinding
{
    /// <summary>
    /// A* 演示的场地布局：外圈封闭 + 三道带缺口的内部墙。
    ///
    /// 掩码按行主序（<c>index = z * width + x</c>），1 = 可走。
    /// 缺口的位置刻意与墙所在的行 / 列错开，因此整片场地是**一个** 4 邻连通分量，
    /// 可用 <see cref="CountComponents"/> 复核。
    /// </summary>
    public static class AStarDemoGridMask
    {
        /// <summary>竖墙一所在列（宽度的 1/3）。</summary>
        public static int WallColumnA(int width) => width / 3;

        /// <summary>竖墙二所在列（宽度的 2/3）。</summary>
        public static int WallColumnB(int width) => width * 2 / 3;

        /// <summary>横墙所在行（高度的 2/3）。</summary>
        public static int WallRow(int height) => height * 2 / 3;

        /// <summary>两道竖墙上缺口的行。</summary>
        public static int GapRowA(int height) => height / 4;

        /// <inheritdoc cref="GapRowA"/>
        public static int GapRowB(int height) => height * 3 / 4;

        /// <summary>横墙上缺口的列。</summary>
        public static int GapColumn(int width) => width / 2;

        /// <summary>
        /// 场地是否够大、能摆下三道内部墙：墙所在行 / 列必须与缺口所在行 / 列互不相同，
        /// 否则缺口会被另一道墙堵死（那样场地就会被切成两块）。
        /// </summary>
        public static bool CanPlaceInteriorWalls(int width, int height)
        {
            if (width < 12 || height < 12)
            {
                return false;
            }

            var columnA = WallColumnA(width);
            var columnB = WallColumnB(width);
            var row = WallRow(height);
            var gapColumn = GapColumn(width);
            var gapRowA = GapRowA(height);
            var gapRowB = GapRowB(height);

            return columnA != columnB && columnA != gapColumn && columnB != gapColumn &&
                   gapRowA != gapRowB && row != gapRowA && row != gapRowB;
        }

        /// <summary>
        /// 按内置布局建掩码：外圈是墙，内部两道竖墙（各留上下两个缺口）与一道横墙（中间留一个缺口）。
        /// 场地太小时只留外圈，内部整块可走。
        /// </summary>
        public static byte[] Build(int width, int height)
        {
            width = math.max(3, width);
            height = math.max(3, height);

            var mask = new byte[width * height];
            var interior = CanPlaceInteriorWalls(width, height);
            var columnA = WallColumnA(width);
            var columnB = WallColumnB(width);
            var row = WallRow(height);
            var gapColumn = GapColumn(width);
            var gapRowA = GapRowA(height);
            var gapRowB = GapRowB(height);

            for (var z = 0; z < height; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    // 外圈一律是墙。
                    var walkable = x > 0 && z > 0 && x < width - 1 && z < height - 1;

                    if (walkable && interior)
                    {
                        if (x == columnA || x == columnB)
                        {
                            // 竖墙：只在两个缺口的行上留口。
                            walkable = z == gapRowA || z == gapRowB;
                        }
                        else if (z == row)
                        {
                            // 横墙：只在中间留一个口。
                            walkable = x == gapColumn;
                        }
                    }

                    mask[z * width + x] = walkable ? (byte)1 : (byte)0;
                }
            }

            return mask;
        }

        /// <summary>
        /// 数一数掩码里 4 邻连通的可走分量个数（连通场地应为 1，0 表示一格可走都没有）。
        /// 用显式栈做广度 / 深度优先，不递归。
        /// </summary>
        public static int CountComponents(byte[] mask, int width, int height)
        {
            if (mask == null || width <= 0 || height <= 0 || mask.Length < width * height)
            {
                return 0;
            }

            var total = width * height;
            var visited = new bool[total];
            var pending = new Stack<int>();
            var components = 0;

            for (var start = 0; start < total; start++)
            {
                if (visited[start] || mask[start] == 0)
                {
                    continue;
                }

                components++;
                visited[start] = true;
                pending.Push(start);

                while (pending.Count > 0)
                {
                    var index = pending.Pop();
                    var x = index % width;
                    var z = index / width;

                    if (x > 0)
                    {
                        Visit(index - 1, width, mask, visited, pending);
                    }
                    if (x < width - 1)
                    {
                        Visit(index + 1, width, mask, visited, pending);
                    }
                    if (z > 0)
                    {
                        Visit(index - width, width, mask, visited, pending);
                    }
                    if (z < height - 1)
                    {
                        Visit(index + width, width, mask, visited, pending);
                    }
                }
            }

            return components;
        }

        /// <summary>把「可走且还没访问过」的格子压进待访问栈（连通分量搜索用）。</summary>
        private static void Visit(int index, int width, byte[] mask, bool[] visited, Stack<int> pending)
        {
            if (index < 0 || index >= visited.Length || visited[index] || mask[index] == 0)
            {
                return;
            }
            visited[index] = true;
            pending.Push(index);
        }

        /// <summary>按行主序收集全部可走格的格坐标。</summary>
        public static List<int2> CollectWalkableCells(byte[] mask, int width, int height)
        {
            var cells = new List<int2>(width * height);
            if (mask == null || width <= 0 || height <= 0 || mask.Length < width * height)
            {
                return cells;
            }

            for (var z = 0; z < height; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (mask[z * width + x] != 0)
                    {
                        cells.Add(new int2(x, z));
                    }
                }
            }

            return cells;
        }
    }
}
