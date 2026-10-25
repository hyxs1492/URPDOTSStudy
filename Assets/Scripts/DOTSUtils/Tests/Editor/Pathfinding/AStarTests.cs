using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace DOTSUtils.Pathfinding.Tests
{
    /// <summary>
    /// 网格换算与寻路内核：直线、绕墙、封死、非法端点、贴角、访问上限、工作区复用、拉直，
    /// 以及 A* 与 Dijkstra 两种算法的一致性。
    /// </summary>
    public class AStarTests
    {
        /// <summary>工具：只填寻路真正读取的那几个字段。</summary>
        private static AStarConfig Config(int maxVisitedNodes = 0, bool smooth = false, float weight = 1f,
                                          PathfindingAlgorithm algorithm = PathfindingAlgorithm.AStar) =>
            new AStarConfig
            {
                MaxVisitedNodes = maxVisitedNodes,
                SmoothPath = smooth,
                HeuristicWeight = weight,
                Algorithm = algorithm,
            };

        /// <summary>
        /// 用字符地图建网格：<c>'#'</c> = 阻挡，其余 = 可走；<c>rows[0]</c> 是最小 z 的那一行，格边长 1。
        /// </summary>
        private static AStarGrid Build(string[] rows, AStarNeighbourhood neighbourhood = AStarNeighbourhood.Four,
                                       bool allowCornerCutting = false)
        {
            const float cellSize = 1f;
            var height = rows.Length;
            var width = rows[0].Length;
            var extents = new float3(width * cellSize * 0.5f, 1f, height * cellSize * 0.5f);
            var config = new AStarConfig
            {
                Bounds = new AABB { Center = new float3(extents.x, 0f, extents.z), Extents = extents },
                CellSize = cellSize,
                Neighbourhood = neighbourhood,
                AllowCornerCutting = allowCornerCutting,
            };

            var mask = new byte[width * height];
            for (var z = 0; z < height; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    mask[z * width + x] = rows[z][x] == '#' ? (byte)0 : (byte)1;
                }
            }
            return new AStarGrid(config, mask, Allocator.Persistent);
        }

        private static AStarScratch NewScratch(in AStarGrid grid)
        {
            var scratch = new AStarScratch();
            scratch.EnsureCapacity(grid.CellCount, Allocator.Persistent);
            return scratch;
        }

        private static AStarStatus Solve(in AStarGrid grid, in AStarConfig config, int2 from, int2 to,
                                        out float3[] path, ref AStarScratch scratch)
        {
            var buffer = new UnsafeList<float3>(math.max(16, grid.CellCount), Allocator.Temp);
            var status = AStarPathfinder.FindPath(grid, grid.CellCenter(from), grid.CellCenter(to), config,
                                                 ref scratch, ref buffer);
            path = new float3[buffer.Length];
            for (var i = 0; i < buffer.Length; i++)
            {
                path[i] = buffer[i];
            }
            buffer.Dispose();
            return status;
        }

        private static void AssertAllWalkable(in AStarGrid grid, float3[] path)
        {
            for (var i = 0; i < path.Length; i++)
            {
                Assert.IsTrue(grid.IsWalkableWorld(path[i]), $"路径第 {i} 个点落在阻挡格上");
            }
        }

        // ------------------------------------------------------------------
        //  网格换算
        // ------------------------------------------------------------------

        [Test]
        public void 网格换算_尺寸与下标与往返一致()
        {
            var grid = Build(new[] { "....", "....", "...." });
            try
            {
                Assert.AreEqual(new int2(4, 3), grid.Size);
                Assert.AreEqual(12, grid.CellCount);
                Assert.AreEqual(12, grid.WalkableCount);

                var cell = new int2(2, 1);
                Assert.AreEqual(cell, grid.WorldToCell(grid.CellCenter(cell)), "格中心应当换算回同一格");
                Assert.AreEqual(cell, grid.Cell(grid.Index(cell)));

                Assert.IsFalse(grid.InBounds(new int2(4, 0)), "x 越界");
                Assert.IsFalse(grid.InBounds(new int2(0, 3)), "z 越界");
                Assert.AreEqual(-1, grid.Index(new int2(-1, 0)));
                Assert.IsFalse(grid.IsWalkable(new int2(-1, 0)), "越界算不可走");

                grid.SetWalkable(new int2(0, 0), false);
                Assert.IsFalse(grid.IsWalkable(new int2(0, 0)));
                Assert.IsFalse(grid.IsWalkable(grid.Index(new int2(0, 0))));
                Assert.AreEqual(11, grid.WalkableCount);
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 掩码为空时全部可走_导出掩码长度一致()
        {
            var config = new AStarConfig
            {
                Bounds = new AABB { Center = float3.zero, Extents = new float3(4f, 1f, 4f) },
                CellSize = 1f,
            };
            var grid = new AStarGrid(config, null, Allocator.Persistent);
            try
            {
                Assert.AreEqual(64, grid.CellCount);
                Assert.AreEqual(64, grid.WalkableCount);
                Assert.AreEqual(64, grid.ToMask().Length);
            }
            finally
            {
                grid.Dispose();
            }
        }

        // ------------------------------------------------------------------
        //  求解
        // ------------------------------------------------------------------

        [Test]
        public void 开敞网格上_四邻直线路径点数等于步数加一()
        {
            var grid = Build(new[] { "..........", "..........", ".........." });
            try
            {
                var scratch = NewScratch(grid);
                var status = Solve(grid, Config(), new int2(0, 0), new int2(9, 0), out var path, ref scratch);

                Assert.AreEqual(AStarStatus.Found, status);
                Assert.AreEqual(10, path.Length, "四邻直线上每格一步：10 格 = 10 个点");
                Assert.AreEqual(grid.CellCenter(new int2(0, 0)), path[0]);
                Assert.AreEqual(grid.CellCenter(new int2(9, 0)), path[path.Length - 1]);
                AssertAllWalkable(grid, path);
                scratch.Dispose();
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 起点与终点同格_路径只有一个点()
        {
            var grid = Build(new[] { "...", "..." });
            try
            {
                var scratch = NewScratch(grid);
                var status = Solve(grid, Config(), new int2(1, 1), new int2(1, 1), out var path, ref scratch);

                Assert.AreEqual(AStarStatus.Found, status);
                Assert.AreEqual(1, path.Length);
                Assert.AreEqual(grid.CellCenter(new int2(1, 1)), path[0]);
                scratch.Dispose();
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 墙上有缺口_必须绕行且不穿阻挡格()
        {
            var grid = Build(new[]
            {
                "..........",
                "..........",
                "####.#####",
                "..........",
                "..........",
            });
            try
            {
                var scratch = NewScratch(grid);
                var status = Solve(grid, Config(), new int2(0, 0), new int2(9, 4), out var path, ref scratch);

                Assert.AreEqual(AStarStatus.Found, status);
                Assert.AreEqual(14, path.Length, "起点到终点的最优格数是 14（曼哈顿 13 步 + 1 个点）");
                AssertAllWalkable(grid, path);

                var passesGap = false;
                for (var i = 0; i < path.Length; i++)
                {
                    if (grid.WorldToCell(path[i]).Equals(new int2(4, 2)))
                    {
                        passesGap = true;
                    }
                }
                Assert.IsTrue(passesGap, "必须穿过 x=4 的缺口");
                scratch.Dispose();
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 完全封死_返回NoPath()
        {
            var grid = Build(new[] { ".....", "#####", "....." });
            try
            {
                var scratch = NewScratch(grid);
                var status = Solve(grid, Config(), new int2(0, 0), new int2(0, 2), out _, ref scratch);
                Assert.AreEqual(AStarStatus.NoPath, status);
                scratch.Dispose();
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 起点或终点不可走或越界_返回对应状态()
        {
            var grid = Build(new[] { ".....", "..#..", "....." });
            try
            {
                var scratch = NewScratch(grid);

                Assert.AreEqual(AStarStatus.StartInvalid,
                    Solve(grid, Config(), new int2(2, 1), new int2(4, 2), out _, ref scratch), "起点在阻挡格上");
                Assert.AreEqual(AStarStatus.EndInvalid,
                    Solve(grid, Config(), new int2(0, 0), new int2(2, 1), out _, ref scratch), "终点在阻挡格上");

                // 越界：世界坐标落在网格外的格子
                var outside = grid.CellCenter(new int2(0, 0)) + new float3(-5f, 0f, 0f);
                var buffer = new UnsafeList<float3>(16, Allocator.Temp);
                var status = AStarPathfinder.FindPath(grid, outside, grid.CellCenter(new int2(4, 2)),
                                                     Config(), ref scratch, ref buffer);
                Assert.AreEqual(AStarStatus.StartInvalid, status, "起点越界");
                buffer.Dispose();
                scratch.Dispose();
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 八邻_禁贴角时不能斜穿缝隙_允许时可以()
        {
            var rows = new[] { ".#", "#." };

            var blocked = Build(rows, AStarNeighbourhood.Eight, allowCornerCutting: false);
            var allowed = Build(rows, AStarNeighbourhood.Eight, allowCornerCutting: true);
            try
            {
                var scratch = NewScratch(blocked);
                Assert.AreEqual(AStarStatus.NoPath,
                    Solve(blocked, Config(), new int2(0, 0), new int2(1, 1), out _, ref scratch),
                    "两侧正交邻居都不可走时不许走对角");
                scratch.Dispose();

                var allowedScratch = NewScratch(allowed);
                var status = Solve(allowed, Config(), new int2(0, 0), new int2(1, 1), out var path, ref allowedScratch);
                Assert.AreEqual(AStarStatus.Found, status, "允许贴角后可以走对角");
                Assert.AreEqual(2, path.Length);
                allowedScratch.Dispose();
            }
            finally
            {
                blocked.Dispose();
                allowed.Dispose();
            }
        }

        [Test]
        public void 超过访问上限_返回ExceededLimit()
        {
            var rows = new string[20];
            for (var i = 0; i < rows.Length; i++)
            {
                rows[i] = new string('.', 20);
            }

            var grid = Build(rows);
            try
            {
                var scratch = NewScratch(grid);
                var status = Solve(grid, Config(maxVisitedNodes: 3), new int2(0, 0), new int2(19, 19), out _, ref scratch);
                Assert.AreEqual(AStarStatus.ExceededLimit, status);
                scratch.Dispose();
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 复用同一块工作区_连续查询结果稳定()
        {
            var grid = Build(new[]
            {
                "..........",
                "..........",
                "####.#####",
                "..........",
                "..........",
            });
            try
            {
                var scratch = NewScratch(grid);
                float3[] first = null;
                for (var i = 0; i < 50; i++)
                {
                    var status = Solve(grid, Config(), new int2(0, 0), new int2(9, 4), out var path, ref scratch);
                    Assert.AreEqual(AStarStatus.Found, status, $"第 {i + 1} 次查询应当仍然找到路径");
                    if (first == null)
                    {
                        first = path;
                    }
                    else
                    {
                        CollectionAssert.AreEqual(first, path, $"第 {i + 1} 次结果应与第 1 次一致");
                    }
                }
                scratch.Dispose();
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 拉直后点数更少_且首尾不变()
        {
            var rows = new string[10];
            for (var i = 0; i < rows.Length; i++)
            {
                rows[i] = new string('.', 10);
            }

            var grid = Build(rows, AStarNeighbourhood.Eight);
            try
            {
                var raw = NewScratch(grid);
                var rawStatus = Solve(grid, Config(smooth: false), new int2(0, 0), new int2(9, 9), out var rawPath, ref raw);
                raw.Dispose();

                var smoothed = NewScratch(grid);
                var smoothStatus = Solve(grid, Config(smooth: true), new int2(0, 0), new int2(9, 9), out var smoothPath, ref smoothed);
                smoothed.Dispose();

                Assert.AreEqual(AStarStatus.Found, rawStatus);
                Assert.AreEqual(AStarStatus.Found, smoothStatus);
                Assert.Less(smoothPath.Length, rawPath.Length, "拉直应当去掉中间点");
                Assert.AreEqual(grid.CellCenter(new int2(0, 0)), smoothPath[0], "起点必须保留");
                Assert.AreEqual(grid.CellCenter(new int2(9, 9)), smoothPath[smoothPath.Length - 1], "终点必须保留");
                AssertAllWalkable(grid, smoothPath);
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 拉直不会穿过阻挡格()
        {
            var grid = Build(new[]
            {
                "..........",
                "..........",
                "####.#####",
                "..........",
                "..........",
            }, AStarNeighbourhood.Eight);
            try
            {
                var scratch = NewScratch(grid);
                var status = Solve(grid, Config(smooth: true), new int2(0, 0), new int2(9, 4), out var path, ref scratch);
                Assert.AreEqual(AStarStatus.Found, status);
                AssertAllWalkable(grid, path);
                scratch.Dispose();
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 启发式权重大于一_仍然只走可走格()
        {
            var grid = Build(new[]
            {
                "..........",
                "....#.....",
                "..........",
            });
            try
            {
                var scratch = NewScratch(grid);
                var status = Solve(grid, Config(weight: 3f), new int2(0, 0), new int2(9, 2), out var path, ref scratch);
                Assert.AreEqual(AStarStatus.Found, status);
                AssertAllWalkable(grid, path);
                scratch.Dispose();
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 全阻挡网格_返回NoPath()
        {
            var config = new AStarConfig
            {
                Bounds = new AABB { Center = float3.zero, Extents = new float3(2f, 1f, 2f) },
                CellSize = 1f,
            };
            var grid = new AStarGrid(config, null, Allocator.Persistent);
            try
            {
                grid.Fill(false);
                var scratch = NewScratch(grid);
                Assert.AreEqual(AStarStatus.StartInvalid,
                    Solve(grid, Config(), new int2(0, 0), new int2(3, 3), out _, ref scratch),
                    "起点就不可走");
                scratch.Dispose();
            }
            finally
            {
                grid.Dispose();
            }
        }

        // ------------------------------------------------------------------
        //  算法选择：A* / Dijkstra
        // ------------------------------------------------------------------

        /// <summary>10 格宽、中间一排墙留一个缺口的场地（4 邻最优 14 个点）。</summary>
        private static string[] GapRows() => new[]
        {
            "..........",
            "..........",
            "####.#####",
            "..........",
            "..........",
        };

        [Test]
        public void 两种算法_开敞网格上与绕墙时最优步数一致()
        {
            var open = new string[10];
            for (var i = 0; i < open.Length; i++)
            {
                open[i] = new string('.', 10);
            }

            var four = Build(open);
            var eight = Build(open, AStarNeighbourhood.Eight);
            var gap = Build(GapRows());
            try
            {
                foreach (var algorithm in new[] { PathfindingAlgorithm.AStar, PathfindingAlgorithm.Dijkstra })
                {
                    var fourScratch = NewScratch(four);
                    Assert.AreEqual(AStarStatus.Found,
                        Solve(four, Config(algorithm: algorithm), new int2(0, 0), new int2(9, 9), out var fourPath, ref fourScratch),
                        algorithm.ToString());
                    Assert.AreEqual(19, fourPath.Length, $"{algorithm} 四邻最短路是 18 步 19 个点");
                    fourScratch.Dispose();

                    var eightScratch = NewScratch(eight);
                    Assert.AreEqual(AStarStatus.Found,
                        Solve(eight, Config(algorithm: algorithm), new int2(0, 0), new int2(9, 9), out var eightPath, ref eightScratch),
                        algorithm.ToString());
                    Assert.AreEqual(10, eightPath.Length, $"{algorithm} 八邻可以走对角：9 步 10 个点");
                    eightScratch.Dispose();

                    var gapScratch = NewScratch(gap);
                    Assert.AreEqual(AStarStatus.Found,
                        Solve(gap, Config(algorithm: algorithm), new int2(0, 0), new int2(9, 4), out var gapPath, ref gapScratch),
                        algorithm.ToString());
                    Assert.AreEqual(14, gapPath.Length, $"{algorithm} 绕缺口的最优格数是 14");
                    gapScratch.Dispose();
                }
            }
            finally
            {
                four.Dispose();
                eight.Dispose();
                gap.Dispose();
            }
        }

        [Test]
        public void 两种算法_八邻贴角规则一致()
        {
            var rows = new[] { ".#", "#." };

            var blocked = Build(rows, AStarNeighbourhood.Eight, allowCornerCutting: false);
            var allowed = Build(rows, AStarNeighbourhood.Eight, allowCornerCutting: true);
            try
            {
                foreach (var algorithm in new[] { PathfindingAlgorithm.AStar, PathfindingAlgorithm.Dijkstra })
                {
                    var blockedScratch = NewScratch(blocked);
                    Assert.AreEqual(AStarStatus.NoPath,
                        Solve(blocked, Config(algorithm: algorithm), new int2(0, 0), new int2(1, 1), out _, ref blockedScratch),
                        $"{algorithm}：禁贴角时不许斜穿缝隙");
                    blockedScratch.Dispose();

                    var allowedScratch = NewScratch(allowed);
                    Assert.AreEqual(AStarStatus.Found,
                        Solve(allowed, Config(algorithm: algorithm), new int2(0, 0), new int2(1, 1), out var path, ref allowedScratch),
                        $"{algorithm}：允许贴角时可以走对角");
                    Assert.AreEqual(2, path.Length);
                    allowedScratch.Dispose();
                }
            }
            finally
            {
                blocked.Dispose();
                allowed.Dispose();
            }
        }

        [Test]
        public void Dijkstra_不受启发式权重影响()
        {
            var grid = Build(GapRows());
            try
            {
                var lightScratch = NewScratch(grid);
                var heavyScratch = NewScratch(grid);
                var aStarScratch = NewScratch(grid);

                Assert.AreEqual(AStarStatus.Found,
                    Solve(grid, Config(weight: 1f, algorithm: PathfindingAlgorithm.Dijkstra),
                          new int2(0, 0), new int2(9, 4), out var light, ref lightScratch));
                Assert.AreEqual(AStarStatus.Found,
                    Solve(grid, Config(weight: 8f, algorithm: PathfindingAlgorithm.Dijkstra),
                          new int2(0, 0), new int2(9, 4), out var heavy, ref heavyScratch));
                Assert.AreEqual(AStarStatus.Found,
                    Solve(grid, Config(weight: 1f), new int2(0, 0), new int2(9, 4), out var aStar, ref aStarScratch));

                CollectionAssert.AreEqual(light, heavy, "Dijkstra 的结果与启发式权重无关");
                Assert.AreEqual(aStar.Length, light.Length, "权重 1 的 A* 与 Dijkstra 都是最优解");

                lightScratch.Dispose();
                heavyScratch.Dispose();
                aStarScratch.Dispose();
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 两种算法_只走可走格_首尾正确_平滑后点数不增()
        {
            var grid = Build(GapRows(), AStarNeighbourhood.Eight);
            try
            {
                foreach (var algorithm in new[] { PathfindingAlgorithm.AStar, PathfindingAlgorithm.Dijkstra })
                {
                    var rawScratch = NewScratch(grid);
                    var smoothScratch = NewScratch(grid);

                    Assert.AreEqual(AStarStatus.Found,
                        Solve(grid, Config(smooth: false, algorithm: algorithm),
                              new int2(0, 0), new int2(9, 4), out var raw, ref rawScratch));
                    Assert.AreEqual(AStarStatus.Found,
                        Solve(grid, Config(smooth: true, algorithm: algorithm),
                              new int2(0, 0), new int2(9, 4), out var smooth, ref smoothScratch));

                    AssertAllWalkable(grid, raw);
                    AssertAllWalkable(grid, smooth);
                    Assert.AreEqual(grid.CellCenter(new int2(0, 0)), raw[0], $"{algorithm} 起点");
                    Assert.AreEqual(grid.CellCenter(new int2(9, 4)), raw[raw.Length - 1], $"{algorithm} 终点");
                    Assert.AreEqual(grid.CellCenter(new int2(0, 0)), smooth[0], $"{algorithm} 拉直后起点");
                    Assert.AreEqual(grid.CellCenter(new int2(9, 4)), smooth[smooth.Length - 1], $"{algorithm} 拉直后终点");
                    Assert.LessOrEqual(smooth.Length, raw.Length, $"{algorithm} 拉直后点数不增");

                    rawScratch.Dispose();
                    smoothScratch.Dispose();
                }
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void Dijkstra_封死与访问上限的状态与A星一致()
        {
            var sealedGrid = Build(new[] { ".....", "#####", "....." });
            var big = new string[20];
            for (var i = 0; i < big.Length; i++)
            {
                big[i] = new string('.', 20);
            }
            var open = Build(big);
            try
            {
                var sealedScratch = NewScratch(sealedGrid);
                Assert.AreEqual(AStarStatus.NoPath,
                    Solve(sealedGrid, Config(algorithm: PathfindingAlgorithm.Dijkstra),
                          new int2(0, 0), new int2(0, 2), out _, ref sealedScratch));
                sealedScratch.Dispose();

                var openScratch = NewScratch(open);
                Assert.AreEqual(AStarStatus.ExceededLimit,
                    Solve(open, Config(maxVisitedNodes: 3, algorithm: PathfindingAlgorithm.Dijkstra),
                          new int2(0, 0), new int2(19, 19), out _, ref openScratch));
                openScratch.Dispose();
            }
            finally
            {
                sealedGrid.Dispose();
                open.Dispose();
            }
        }
    }
}
