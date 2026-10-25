using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace DOTSUtils.Pathfinding.Tests
{
    /// <summary>
    /// 阻挡掩码 → 静态障碍物边界：线段形态的条数（合并共线格边）与内侧方向，
    /// 多边形形态的闭环拓扑（首尾相接、洞的反向环、共线合并、退化输入与顶点数上界）。
    /// </summary>
    public class PathObstacleBuilderTests
    {
        /// <summary>演示用的网格资产（60×40、366 个阻挡格）。</summary>
        private const string k_ShipGridAssetPath = "Assets/Pathfinding/AStarShipGrid.asset";

        /// <summary>
        /// 用字符地图建网格：<c>'#'</c> = 阻挡，其余 = 可走；<c>rows[0]</c> 是最小 z 的那一行，格边长 1。
        /// </summary>
        private static AStarGrid Build(string[] rows)
        {
            var height = rows.Length;
            var width = rows[0].Length;
            var mask = new byte[width * height];
            for (var z = 0; z < height; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    mask[z * width + x] = rows[z][x] == '#' ? (byte)0 : (byte)1;
                }
            }
            return BuildFromMask(mask, width, height);
        }

        /// <summary>用行主序掩码（非 0 = 可走）建格边长 1 的网格。</summary>
        private static AStarGrid BuildFromMask(byte[] mask, int width, int height)
        {
            const float cellSize = 1f;
            var extents = new float3(width * cellSize * 0.5f, 1f, height * cellSize * 0.5f);
            var config = new AStarConfig
            {
                Bounds = new AABB { Center = new float3(extents.x, 0f, extents.z), Extents = extents },
                CellSize = cellSize,
            };
            return new AStarGrid(config, mask, Allocator.Persistent);
        }

        private static int CountSegments(in AStarGrid grid, out PathObstacleSegment[] segments)
            => PathObstacleBuilder.Build(in grid, out segments);

        private static int CountPolygons(in AStarGrid grid, out float2[] vertices, out PathObstaclePolygon[] polygons)
            => PathObstacleBuilder.BuildPolygons(in grid, out vertices, out polygons);

        /// <summary>网格里的阻挡格数。</summary>
        private static int CountSolid(in AStarGrid grid)
        {
            var count = 0;
            var size = grid.Size;
            for (var z = 0; z < size.y; z++)
            {
                for (var x = 0; x < size.x; x++)
                {
                    if (!grid.IsWalkable(new int2(x, z)))
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        /// <summary>一个多边形的有向面积（xz 平面，阻挡区在环内为正、洞的环为负）。</summary>
        private static float SignedArea(float2[] vertices, in PathObstaclePolygon polygon)
        {
            var area = 0f;
            for (var i = 0; i < polygon.Count; i++)
            {
                var index = polygon.Start + i;
                var a = vertices[index];
                var b = vertices[polygon.NextVertex(index)];
                area += (a.x * b.y) - (b.x * a.y);
            }
            return area * 0.5f;
        }

        /// <summary>构建一条 62×5、中间一整排 60 格墙的地图。</summary>
        private static string[] SixtyCellWall()
        {
            var rows = new string[5];
            for (var z = 0; z < rows.Length; z++)
            {
                rows[z] = z == 2 ? "." + new string('#', 60) + "." : new string('.', 62);
            }
            return rows;
        }

        /// <summary>
        /// 线段内侧判定：取线段中点沿左法线推进半格得到的取样点，它必须是网格内的阻挡格，
        /// 且该格中心满足 <c>det(B - A, P - A) &gt; 0</c>。
        /// </summary>
        private static void AssertObstacleSideIsLeft(in AStarGrid grid, in PathObstacleSegment segment)
            => AssertObstacleSideIsLeft(in grid, segment.A, segment.B);

        /// <summary>同上，直接给一条边 A→B（多边形的一条边或一条独立线段）。</summary>
        private static void AssertObstacleSideIsLeft(in AStarGrid grid, float2 a, float2 b)
        {
            var direction = b - a;
            Assert.IsTrue(math.all(math.isfinite(a)) && math.all(math.isfinite(b)),
                "端点不得是 NaN/Inf");
            Assert.Greater(math.length(direction), 0f, "边不得是零长");

            var mid = (a + b) * 0.5f;
            var leftNormal = math.normalize(new float2(-direction.y, direction.x));
            var probe = mid + leftNormal * (grid.CellSize * 0.5f);
            var cell = grid.WorldToCell(new float3(probe.x, 0f, probe.y));

            Assert.IsTrue(grid.InBounds(cell), "A→B 左侧的取样点应当落在网格内");
            Assert.IsFalse(grid.IsWalkable(cell), "A→B 左侧的取样点应当是阻挡格");

            var center = grid.CellCenter(cell);
            var det = direction.x * (center.z - a.y) - direction.y * (center.x - a.x);
            Assert.Greater(det, 0f, $"阻挡格 ({cell.x},{cell.y}) 的中心不在 A→B 的左侧");
        }

        // ───────────────────────────── 条数与合并 ─────────────────────────────

        [Test]
        public void 单个阻挡格_输出四条线段()
        {
            var grid = Build(new[]
            {
                ".....",
                ".#...",
                ".....",
                ".....",
                ".....",
            });
            try
            {
                var count = CountSegments(in grid, out var segments);

                Assert.AreEqual(4, count, "一个阻挡格有四条格边");
                Assert.AreEqual(4, segments.Length);
                Assert.GreaterOrEqual(PathObstacleBuilder.MaxSegmentCount(in grid), count);
                for (var i = 0; i < count; i++)
                {
                    AssertObstacleSideIsLeft(in grid, in segments[i]);
                }
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 两个相邻阻挡格_共线格边合并成四条()
        {
            var grid = Build(new[]
            {
                ".....",
                ".##..",
                ".....",
                ".....",
                ".....",
            });
            try
            {
                var count = CountSegments(in grid, out var segments);

                // 上下各一条跨两格的长边、左右各一条一格长的短边，共 4 条。
                Assert.AreEqual(4, count);
                Assert.AreEqual(4, segments.Length);
                for (var i = 0; i < count; i++)
                {
                    AssertObstacleSideIsLeft(in grid, in segments[i]);
                }
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 一整排六十格墙_合并成四条且长边跨六十格()
        {
            var grid = Build(SixtyCellWall());
            try
            {
                var count = CountSegments(in grid, out var segments);

                Assert.AreEqual(4, count, "上下两条长边 + 两端各一条短边");

                // 长边：z = 2 上从 x = 1 到 x = 61（以及反向的对称边）。
                var longCount = 0;
                var shortCount = 0;
                for (var i = 0; i < count; i++)
                {
                    var length = math.length(segments[i].B - segments[i].A);
                    if (math.abs(length - 60f) < 1e-4f)
                    {
                        longCount++;
                    }
                    else if (math.abs(length - 1f) < 1e-4f)
                    {
                        shortCount++;
                    }
                    AssertObstacleSideIsLeft(in grid, in segments[i]);
                }

                Assert.AreEqual(2, longCount, "六十格墙的两条长边各是一条线段");
                Assert.AreEqual(2, shortCount, "两端各一条一格长的短边");
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 合并后的长边_端点落在墙两端()
        {
            var grid = Build(SixtyCellWall());
            try
            {
                var count = CountSegments(in grid, out var segments);
                Assert.AreEqual(4, count);

                var found = false;
                for (var i = 0; i < count; i++)
                {
                    var segment = segments[i];
                    if (math.abs(segment.A.y - 2f) > 1e-5f || math.abs(segment.B.y - 2f) > 1e-5f)
                    {
                        continue;
                    }
                    var minX = math.min(segment.A.x, segment.B.x);
                    var maxX = math.max(segment.A.x, segment.B.x);
                    if (math.abs(minX - 1f) <= 1e-5f && math.abs(maxX - 61f) <= 1e-5f)
                    {
                        found = true;
                    }
                }

                Assert.IsTrue(found, "应当有一条从 (1,2) 到 (61,2) 的长边");
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 追加语义_重复构建累加且返回值是本次条数()
        {
            var grid = Build(new[]
            {
                ".....",
                ".#...",
                ".....",
                ".....",
                ".....",
            });
            var list = new NativeList<PathObstacleSegment>(16, Allocator.Temp);
            try
            {
                Assert.AreEqual(4, PathObstacleBuilder.Build(in grid, list));
                Assert.AreEqual(4, list.Length);

                Assert.AreEqual(4, PathObstacleBuilder.Build(in grid, list));
                Assert.AreEqual(8, list.Length);

                Assert.AreEqual(4, CountSegments(in grid, out var array));
                Assert.AreEqual(4, array.Length);
            }
            finally
            {
                list.Dispose();
                grid.Dispose();
            }
        }

        // ───────────────────────────── 方向 ─────────────────────────────

        [Test]
        public void 每条线段_阻挡格中心都在A到B的左侧()
        {
            var maps = new[]
            {
                new[]
                {
                    "##...",
                    "#....",
                    "...#.",
                    ".....",
                    ".#.#.",
                },
                new[]
                {
                    "#####",
                    "#####",
                    "#####",
                    "#####",
                    "#####",
                },
                new[]
                {
                    ".....",
                    ".###.",
                    ".#.#.",
                    ".###.",
                    ".....",
                },
                SixtyCellWall(),
            };

            for (var m = 0; m < maps.Length; m++)
            {
                var grid = Build(maps[m]);
                try
                {
                    var count = CountSegments(in grid, out var segments);
                    Assert.Greater(count, 0, $"地图 {m} 应当有线段");
                    for (var i = 0; i < count; i++)
                    {
                        AssertObstacleSideIsLeft(in grid, in segments[i]);
                    }
                }
                finally
                {
                    grid.Dispose();
                }
            }
        }

        // ───────────────────────────── 退化 ─────────────────────────────

        [Test]
        public void 全可走_零条线段()
        {
            var grid = Build(new[]
            {
                ".....",
                ".....",
                ".....",
                ".....",
                ".....",
            });
            try
            {
                Assert.AreEqual(0, CountSegments(in grid, out var segments));
                Assert.AreEqual(0, segments.Length);
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 全阻挡_只有外框四条()
        {
            var grid = Build(new[]
            {
                "#####",
                "#####",
                "#####",
                "#####",
                "#####",
            });
            try
            {
                var count = CountSegments(in grid, out var segments);

                Assert.AreEqual(4, count, "外框合并成四条");
                for (var i = 0; i < count; i++)
                {
                    Assert.AreEqual(5f, math.length(segments[i].B - segments[i].A), 1e-4f, "外框每条边与网格同宽");
                    AssertObstacleSideIsLeft(in grid, in segments[i]);
                }
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 空网格_未创建的网格与列表都返回零条()
        {
            var list = new NativeList<PathObstacleSegment>(4, Allocator.Temp);
            try
            {
                var notCreated = default(AStarGrid);
                Assert.AreEqual(0, PathObstacleBuilder.Build(in notCreated, list));
                Assert.AreEqual(0, list.Length);
                Assert.AreEqual(0, PathObstacleBuilder.MaxSegmentCount(in notCreated));

                Assert.AreEqual(0, CountSegments(in notCreated, out var segments));
                Assert.AreEqual(0, segments.Length);

                var uncreatedList = default(NativeList<PathObstacleSegment>);
                Assert.IsFalse(uncreatedList.IsCreated, "前提：默认列表未创建");
                Assert.AreEqual(0, PathObstacleBuilder.Build(in notCreated, uncreatedList));
            }
            finally
            {
                list.Dispose();
            }
        }

        [Test]
        public void 掩码为空_全部可走时返回零条()
        {
            var config = new AStarConfig
            {
                Bounds = new AABB { Center = float3.zero, Extents = new float3(4f, 1f, 4f) },
                CellSize = 1f,
            };
            var grid = new AStarGrid(config, null, Allocator.Persistent);
            try
            {
                Assert.AreEqual(64, grid.WalkableCount, "前提：掩码为空时全部可走");
                Assert.AreEqual(0, CountSegments(in grid, out var segments));
            }
            finally
            {
                grid.Dispose();
            }
        }

        // ───────────────────────────── 多边形：一致性 ─────────────────────────────

        /// <summary>
        /// 全部多边形的一致性：下标合法、环内顶点不重复、每条边有限且长度 &gt; 0、环闭合、prev/next 首尾相接、
        /// 相邻同向共线边已经合并、每条边的左侧都是阻挡格（含 <c>det &gt; 0</c>）。
        /// </summary>
        private static void AssertPolygonsConsistent(in AStarGrid grid, float2[] vertices, PathObstaclePolygon[] polygons)
        {
            Assert.IsNotNull(vertices);
            Assert.IsNotNull(polygons);

            for (var p = 0; p < polygons.Length; p++)
            {
                var polygon = polygons[p];
                Assert.GreaterOrEqual(polygon.Start, 0, "多边形首顶点下标");
                Assert.GreaterOrEqual(polygon.Count, 2, "多边形顶点数");
                Assert.LessOrEqual(polygon.Start + polygon.Count, vertices.Length, "多边形顶点都要在顶点数组里");

                var sum = float2.zero;
                for (var i = 0; i < polygon.Count; i++)
                {
                    var index = polygon.Start + i;
                    var a = vertices[index];
                    var b = vertices[polygon.NextVertex(index)];
                    Assert.AreEqual(index, polygon.NextVertex(polygon.PrevVertex(index)), "prev/next 在环内首尾相接");
                    Assert.AreEqual(index, polygon.PrevVertex(polygon.NextVertex(index)), "prev/next 在环内首尾相接");

                    var edge = b - a;
                    sum += edge;
                    AssertObstacleSideIsLeft(in grid, a, b);
                }

                Assert.Less(math.length(sum), grid.CellSize * 0.1f, "环必须首尾相接（边向量之和为零）");

                for (var i = 0; i < polygon.Count; i++)
                {
                    var previous = vertices[polygon.PrevVertex(polygon.Start + i)];
                    var current = vertices[polygon.Start + i];
                    var next = vertices[polygon.NextVertex(polygon.Start + i)];
                    var inEdge = current - previous;
                    var outEdge = next - current;
                    var cross = (inEdge.x * outEdge.y) - (inEdge.y * outEdge.x);
                    Assert.IsFalse(math.abs(cross) < 1e-6f && math.dot(inEdge, outEdge) > 0f,
                        "相邻同向的共线边必须已经合并成一条长边");
                }

                for (var i = 0; i < polygon.Count; i++)
                {
                    for (var j = i + 1; j < polygon.Count; j++)
                    {
                        Assert.IsFalse(math.all(vertices[polygon.Start + i] == vertices[polygon.Start + j]),
                            "环内不得出现重复顶点");
                    }
                }
            }

            // 两个入口合并同一批「极大共线格边」：多边形顶点数与合并后的线段条数相同。
            var segmentCount = CountSegments(in grid, out var segments);
            Assert.AreEqual(segmentCount, segments.Length);
            Assert.AreEqual(segmentCount, vertices.Length, "多边形顶点数应当与合并共线后的线段条数一致");
            Assert.LessOrEqual(vertices.Length, PathObstacleBuilder.MaxVertexCount(in grid), "顶点数不得超过上界");
            Assert.LessOrEqual(polygons.Length, PathObstacleBuilder.MaxPolygonCount(in grid), "多边形数不得超过上界");
        }

        /// <summary>全部环的有向面积之和 = 阻挡格总面积（洞的环反向，自动抵消）。</summary>
        private static void AssertSignedAreaEqualsSolid(in AStarGrid grid, float2[] vertices, PathObstaclePolygon[] polygons)
        {
            var area = 0f;
            for (var i = 0; i < polygons.Length; i++)
            {
                area += SignedArea(vertices, in polygons[i]);
            }

            var expected = CountSolid(in grid) * grid.CellSize * grid.CellSize;
            Assert.AreEqual(expected, area, 1e-2f, "环的有向面积之和应当等于阻挡区面积");
        }

        // ───────────────────────────── 多边形：形状与方向 ─────────────────────────────

        [Test]
        public void 多边形_单个阻挡格_一个四边形四顶点()
        {
            var grid = Build(new[]
            {
                ".....",
                ".#...",
                ".....",
                ".....",
                ".....",
            });
            try
            {
                var count = CountPolygons(in grid, out var vertices, out var polygons);

                Assert.AreEqual(1, count, "一个阻挡格是一个闭环");
                Assert.AreEqual(1, polygons.Length);
                Assert.AreEqual(4, polygons[0].Count, "一个阻挡格是四边形");
                Assert.AreEqual(4, vertices.Length);
                Assert.AreEqual(1f, SignedArea(vertices, in polygons[0]), 1e-4f, "阻挡区在环内 → 环是逆时针");
                AssertPolygonsConsistent(in grid, vertices, polygons);
                AssertSignedAreaEqualsSolid(in grid, vertices, polygons);
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 多边形_水平相邻两格_共线合并成一个四边形()
        {
            var grid = Build(new[]
            {
                ".....",
                ".##..",
                ".....",
                ".....",
                ".....",
            });
            try
            {
                var count = CountPolygons(in grid, out var vertices, out var polygons);

                // 上下各一条跨两格的长边、左右各一条一格长的短边。
                Assert.AreEqual(1, count);
                Assert.AreEqual(4, polygons[0].Count, "两个相邻阻挡格合并成一个四边形");
                Assert.AreEqual(4, vertices.Length);
                AssertPolygonsConsistent(in grid, vertices, polygons);
                AssertSignedAreaEqualsSolid(in grid, vertices, polygons);
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 多边形_一整排六十格墙_一个四边形且长边跨六十格()
        {
            var grid = Build(SixtyCellWall());
            try
            {
                var count = CountPolygons(in grid, out var vertices, out var polygons);

                Assert.AreEqual(1, count);
                Assert.AreEqual(4, polygons[0].Count, "六十格墙是一个四边形");
                AssertPolygonsConsistent(in grid, vertices, polygons);
                AssertSignedAreaEqualsSolid(in grid, vertices, polygons);

                var maxEdge = 0f;
                for (var i = 0; i < polygons[0].Count; i++)
                {
                    var index = polygons[0].Start + i;
                    maxEdge = math.max(maxEdge, math.length(vertices[polygons[0].NextVertex(index)] - vertices[index]));
                }
                Assert.AreEqual(60f, maxEdge, 1e-4f, "长边跨六十格");
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 多边形_L形三格_一个多边形六顶点()
        {
            var grid = Build(new[]
            {
                ".....",
                ".##..",
                ".#...",
                ".....",
                ".....",
            });
            try
            {
                var count = CountPolygons(in grid, out var vertices, out var polygons);

                Assert.AreEqual(1, count, "L 形是一块连通阻挡区");
                Assert.AreEqual(6, polygons[0].Count, "共线合并后是六个顶点");
                AssertPolygonsConsistent(in grid, vertices, polygons);
                AssertSignedAreaEqualsSolid(in grid, vertices, polygons);
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 多边形_环形带洞_两个环且每个环的每条边阻挡区都在左()
        {
            var grid = Build(new[]
            {
                ".....",
                ".###.",
                ".#.#.",
                ".###.",
                ".....",
            });
            try
            {
                var count = CountPolygons(in grid, out var vertices, out var polygons);

                Assert.AreEqual(2, count, "外圈一个环 + 洞一个环");
                AssertPolygonsConsistent(in grid, vertices, polygons);
                AssertSignedAreaEqualsSolid(in grid, vertices, polygons);

                var outer = 0;
                var hole = 0;
                var area = 0f;
                for (var i = 0; i < polygons.Length; i++)
                {
                    var signed = SignedArea(vertices, in polygons[i]);
                    area += signed;
                    if (signed > 0f)
                    {
                        outer++;
                    }
                    else
                    {
                        hole++;
                    }
                }

                Assert.AreEqual(1, outer, "外环逆时针（阻挡区在环内）");
                Assert.AreEqual(1, hole, "洞的环顺时针（阻挡区在环外）");
                Assert.AreEqual(8f, area, 1e-3f, "八个阻挡格");
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 多边形_对角接触的两格_两个独立的环()
        {
            var grid = Build(new[]
            {
                ".....",
                ".#...",
                "..#..",
                ".....",
                ".....",
            });
            try
            {
                var count = CountPolygons(in grid, out var vertices, out var polygons);

                Assert.AreEqual(2, count, "只在一个角点接触的两格各自成环");
                for (var i = 0; i < polygons.Length; i++)
                {
                    Assert.AreEqual(4, polygons[i].Count);
                }
                AssertPolygonsConsistent(in grid, vertices, polygons);
                AssertSignedAreaEqualsSolid(in grid, vertices, polygons);
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 多边形_全阻挡_一个四边形()
        {
            var grid = Build(new[]
            {
                "#####",
                "#####",
                "#####",
                "#####",
                "#####",
            });
            try
            {
                var count = CountPolygons(in grid, out var vertices, out var polygons);

                Assert.AreEqual(1, count);
                Assert.AreEqual(4, polygons[0].Count, "全阻挡只有一圈外框");
                Assert.AreEqual(25f, SignedArea(vertices, in polygons[0]), 1e-3f);
                AssertPolygonsConsistent(in grid, vertices, polygons);
                AssertSignedAreaEqualsSolid(in grid, vertices, polygons);
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 多边形_全可走_零个()
        {
            var grid = Build(new[]
            {
                ".....",
                ".....",
                ".....",
                ".....",
                ".....",
            });
            try
            {
                Assert.AreEqual(0, CountPolygons(in grid, out var vertices, out var polygons));
                Assert.AreEqual(0, vertices.Length);
                Assert.AreEqual(0, polygons.Length);
            }
            finally
            {
                grid.Dispose();
            }
        }

        // ───────────────────────────── 多边形：退化与上界 ─────────────────────────────

        [Test]
        public void 多边形_各种凹角与叉路_没有零长边没有重复顶点()
        {
            var maps = new[]
            {
                new[]
                {
                    "##...",
                    "#....",
                    "...#.",
                    ".....",
                    ".#.#.",
                },
                new[]
                {
                    "#####",
                    "#...#",
                    "#.#.#",
                    "#...#",
                    "#####",
                },
                new[]
                {
                    ".....",
                    "..#..",
                    ".#.#.",
                    "..#..",
                    ".....",
                },
                new[]
                {
                    "#.#",
                    ".#.",
                    "#.#",
                },
                SixtyCellWall(),
            };

            for (var m = 0; m < maps.Length; m++)
            {
                var grid = Build(maps[m]);
                try
                {
                    CountPolygons(in grid, out var vertices, out var polygons);
                    Assert.Greater(polygons.Length, 0, $"地图 {m} 应当有环");
                    AssertPolygonsConsistent(in grid, vertices, polygons);
                    AssertSignedAreaEqualsSolid(in grid, vertices, polygons);
                }
                finally
                {
                    grid.Dispose();
                }
            }
        }

        [Test]
        public void 多边形_随机掩码_没有零长边没有重复顶点且阻挡区都在左()
        {
            var random = new Unity.Mathematics.Random(20240927u);
            const int width = 9;
            const int height = 7;

            for (var trial = 0; trial < 40; trial++)
            {
                var mask = new byte[width * height];
                for (var i = 0; i < mask.Length; i++)
                {
                    mask[i] = random.NextFloat() < 0.45f ? (byte)0 : (byte)1;
                }

                var grid = BuildFromMask(mask, width, height);
                try
                {
                    CountPolygons(in grid, out var vertices, out var polygons);
                    AssertPolygonsConsistent(in grid, vertices, polygons);
                    AssertSignedAreaEqualsSolid(in grid, vertices, polygons);
                }
                finally
                {
                    grid.Dispose();
                }
            }
        }

        [Test]
        public void 多边形_空网格与未创建的列表_返回零个()
        {
            var vertices = new NativeList<float2>(4, Allocator.Temp);
            var polygons = new NativeList<PathObstaclePolygon>(4, Allocator.Temp);
            try
            {
                var notCreated = default(AStarGrid);
                Assert.AreEqual(0, PathObstacleBuilder.BuildPolygons(in notCreated, vertices, polygons));
                Assert.AreEqual(0, vertices.Length);
                Assert.AreEqual(0, polygons.Length);
                Assert.AreEqual(0, PathObstacleBuilder.MaxVertexCount(in notCreated));
                Assert.AreEqual(0, PathObstacleBuilder.MaxPolygonCount(in notCreated));

                Assert.AreEqual(0, CountPolygons(in notCreated, out var array, out var polygonArray));
                Assert.AreEqual(0, array.Length);
                Assert.AreEqual(0, polygonArray.Length);

                var uncreatedVertices = default(NativeList<float2>);
                var uncreatedPolygons = default(NativeList<PathObstaclePolygon>);
                Assert.IsFalse(uncreatedVertices.IsCreated, "前提：默认列表未创建");
                Assert.IsFalse(uncreatedPolygons.IsCreated, "前提：默认列表未创建");
                Assert.AreEqual(0, PathObstacleBuilder.BuildPolygons(in notCreated, uncreatedVertices, uncreatedPolygons));
            }
            finally
            {
                polygons.Dispose();
                vertices.Dispose();
            }
        }

        [Test]
        public void 多边形_追加语义_重复构建累加且返回值是本次个数()
        {
            var grid = Build(new[]
            {
                ".....",
                ".#...",
                ".....",
                ".....",
                ".....",
            });
            var vertices = new NativeList<float2>(16, Allocator.Temp);
            var polygons = new NativeList<PathObstaclePolygon>(16, Allocator.Temp);
            try
            {
                Assert.AreEqual(1, PathObstacleBuilder.BuildPolygons(in grid, vertices, polygons));
                Assert.AreEqual(4, vertices.Length);
                Assert.AreEqual(1, polygons.Length);

                Assert.AreEqual(1, PathObstacleBuilder.BuildPolygons(in grid, vertices, polygons));
                Assert.AreEqual(8, vertices.Length);
                Assert.AreEqual(2, polygons.Length);
                Assert.AreEqual(4, polygons[1].Start, "第二次构建的顶点接在第一次后面");
            }
            finally
            {
                polygons.Dispose();
                vertices.Dispose();
            }
        }

        [Test]
        public void 多边形_演示掩码_在上界内且方向与形状一致()
        {
            var asset = AssetDatabase.LoadAssetAtPath<AStarGridAsset>(k_ShipGridAssetPath);
            Assert.IsNotNull(asset, $"缺少演示网格资产 {k_ShipGridAssetPath}");

            var grid = new AStarGrid(asset.ToConfig(), asset.Mask, Allocator.Persistent);
            try
            {
                Assert.AreEqual(2400, grid.CellCount, "前提：演示网格是 60×40");
                Assert.AreEqual(366, CountSolid(in grid), "前提：演示掩码有 366 个阻挡格");

                var count = CountPolygons(in grid, out var vertices, out var polygons);
                var maxVertices = PathObstacleBuilder.MaxVertexCount(in grid);
                var maxPolygons = PathObstacleBuilder.MaxPolygonCount(in grid);
                Debug.Log($"[PathObstacleBuilderTests] 演示掩码：多边形 {count} 个、顶点 {vertices.Length} 个；" +
                          $"上界 多边形 {maxPolygons} / 顶点 {maxVertices}");

                Assert.Greater(count, 0);
                Assert.LessOrEqual(count, maxPolygons);
                Assert.LessOrEqual(vertices.Length, maxVertices);
                AssertPolygonsConsistent(in grid, vertices, polygons);
                AssertSignedAreaEqualsSolid(in grid, vertices, polygons);
            }
            finally
            {
                grid.Dispose();
            }
        }

        [Test]
        public void 多边形_演示掩码_七个环五十二个顶点()
        {
            var asset = AssetDatabase.LoadAssetAtPath<AStarGridAsset>(k_ShipGridAssetPath);
            Assert.IsNotNull(asset, $"缺少演示网格资产 {k_ShipGridAssetPath}");

            var grid = new AStarGrid(asset.ToConfig(), asset.Mask, Allocator.Persistent);
            try
            {
                var count = CountPolygons(in grid, out var vertices, out var polygons);

                Assert.AreEqual(7, count, "外框 + 一个大洞 + 五块独立阻挡区");
                Assert.AreEqual(52, vertices.Length, "与合并共线后的 52 条线段同量");

                var quads = 0;
                var others = 0;
                for (var i = 0; i < polygons.Length; i++)
                {
                    if (polygons[i].Count == 4)
                    {
                        quads++;
                    }
                    else
                    {
                        others++;
                    }
                }

                Assert.AreEqual(6, quads, "外框与五块矩形的独立阻挡区各是一个四边形");
                Assert.AreEqual(1, others, "剩下的是 28 顶点的大洞环");
                Assert.AreEqual(2400f, SignedArea(vertices, in polygons[0]), 1e-2f, "外框环的面积是整块场地");
                AssertPolygonsConsistent(in grid, vertices, polygons);
                AssertSignedAreaEqualsSolid(in grid, vertices, polygons);
            }
            finally
            {
                grid.Dispose();
            }
        }
    }
}
