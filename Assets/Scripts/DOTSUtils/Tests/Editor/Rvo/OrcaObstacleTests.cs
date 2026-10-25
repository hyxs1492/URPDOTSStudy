using DOTSUtils.Rvo;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace DOTSUtils.Rvo.Tests
{
    /// <summary>
    /// ORCA 静态障碍物的 EditMode 用例。障碍物是「顶点 + 多边形拓扑」：
    /// 写入走 <see cref="RvoObstacleBuilder"/>，求解走 <see cref="OrcaSolver.BuildObstacleLines"/>。
    /// 内核是纯函数，断言只测不变量（无 NaN、速度上限、不进入障碍物内部、拓扑字段、邻边修正）。
    /// </summary>
    public class OrcaObstacleTests
    {
        const float k_TimeHorizon = 2f;
        const float k_TimeHorizonObst = 2f;
        const float k_TimeStep = 1f / 60f;
        const float k_Tol = 1e-4f;

        static AgentState Agent(float2 position, float2 velocity, float radius = 0.5f, float maxSpeed = 2f,
                                float2 pref = default)
            => new AgentState
            {
                Position = position,
                Velocity = velocity,
                Radius = radius,
                MaxSpeed = maxSpeed,
                PrefVelocity = pref,
            };

        /// <summary>扁平顶点位置 + 多边形顶点数 → 带拓扑的顶点数组（多边形分段另出）。</summary>
        static NativeList<RvoObstacleVertex> BuildObstacles(NativeArray<float2> points, int[] polygonVertexCounts,
                                                           out NativeList<RvoObstaclePolygon> polygons)
        {
            var vertices = new NativeList<RvoObstacleVertex>(Allocator.Temp);
            polygons = new NativeList<RvoObstaclePolygon>(Allocator.Temp);
            var start = 0;
            for (var i = 0; i < polygonVertexCounts.Length; i++)
            {
                RvoObstacleBuilder.Append(vertices, polygons, points, start, polygonVertexCounts[i]);
                start += polygonVertexCounts[i];
            }
            return vertices;
        }

        /// <summary>旧线段模型：每条线段各自成一个二顶点多边形。</summary>
        static NativeList<RvoObstacleVertex> BuildSegments(NativeArray<RvoObstacleSegment> segments,
                                                          out NativeList<RvoObstaclePolygon> polygons)
        {
            var vertices = new NativeList<RvoObstacleVertex>(Allocator.Temp);
            polygons = new NativeList<RvoObstaclePolygon>(Allocator.Temp);
            for (var i = 0; i < segments.Length; i++)
            {
                RvoObstacleBuilder.AppendSegment(vertices, polygons, segments[i].A, segments[i].B);
            }
            return vertices;
        }

        /// <summary>解一步（没有邻居）。</summary>
        static float2 Solve(in AgentState self, NativeArray<RvoObstacleVertex> obstacles)
            => OrcaSolver.Solve(self, default, 0, obstacles, obstacles.IsCreated ? obstacles.Length : 0,
                                k_TimeHorizon, k_TimeHorizonObst, k_TimeStep);

        static float2 SolveWithoutObstacles(in AgentState self)
            => OrcaSolver.Solve(self, default, 0, k_TimeHorizon, k_TimeStep);

        /// <summary>线段候选范围（<c>τ_obst·maxSpeed + radius</c>）的平方。</summary>
        static float RangeSq(in AgentState self)
        {
            var range = k_TimeHorizonObst * self.MaxSpeed + self.Radius;
            return range * range;
        }

        static void AssertFiniteAndCapped(in AgentState self, float2 velocity, string message)
        {
            Assert.IsTrue(math.all(math.isfinite(velocity)), $"{message}：不得产出 NaN/Inf");
            Assert.LessOrEqual(math.length(velocity), self.MaxSpeed + k_Tol, $"{message}：不得超速度上限");
        }

        /// <summary>按固定步长推进；每步都断言速度 / 位置有限、速度不超上限，并把位置交给 <paramref name="observe"/>。</summary>
        static float2 Step(in AgentState start, NativeArray<RvoObstacleVertex> obstacles, int steps,
                           System.Action<float2, float2> observe = null)
        {
            var self = start;
            for (var i = 0; i < steps; i++)
            {
                self.Velocity = Solve(self, obstacles);
                AssertFiniteAndCapped(self, self.Velocity, $"第 {i} 步");
                self.Position += self.Velocity * k_TimeStep;
                Assert.IsTrue(math.all(math.isfinite(self.Position)), $"第 {i} 步位置出现 NaN/Inf");
                observe?.Invoke(self.Position, self.Velocity);
            }
            return self.Position;
        }

        /// <summary>点是否在三角形内部（三条边的叉积符号是否一致）。</summary>
        static bool InsideTriangle(float2 point, float2 a, float2 b, float2 c)
        {
            var d1 = OrcaSolver.Det(b - a, point - a);
            var d2 = OrcaSolver.Det(c - b, point - b);
            var d3 = OrcaSolver.Det(a - c, point - c);
            var hasNegative = d1 < 0f || d2 < 0f || d3 < 0f;
            var hasPositive = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(hasNegative && hasPositive);
        }

        // ───────────────────────────── 拓扑模型 ─────────────────────────────

        [Test]
        public void 拓扑写入_二顶点墙与四边形与凹角()
        {
            var two = new NativeArray<float2>(2, Allocator.Temp);
            var four = new NativeArray<float2>(4, Allocator.Temp);
            var lShape = new NativeArray<float2>(6, Allocator.Temp);
            try
            {
                // 二顶点墙：Prev / Next 都指向另一个顶点，两个顶点都是凸的，UnitDir 相反
                two[0] = new float2(0f, 0f);
                two[1] = new float2(4f, 0f);
                var wall = BuildObstacles(two, new[] { 2 }, out var wallPolygons);
                try
                {
                    Assert.AreEqual(2, wall.Length);
                    Assert.AreEqual(1, wallPolygons.Length);
                    Assert.AreEqual(1, (int)wall[0].IsConvex);
                    Assert.AreEqual(1, (int)wall[1].IsConvex);
                    Assert.AreEqual(1, wall[0].Prev, "二顶点：Prev 指向另一个顶点");
                    Assert.AreEqual(1, wall[0].Next, "二顶点：Next 指向另一个顶点");
                    Assert.AreEqual(0, wall[1].Prev);
                    Assert.AreEqual(0, wall[1].Next);
                    Assert.AreEqual(1f, wall[0].UnitDir.x, 1e-4f);
                    Assert.AreEqual(-1f, wall[1].UnitDir.x, 1e-4f);
                }
                finally
                {
                    wall.Dispose();
                    wallPolygons.Dispose();
                }

                // CCW 正方形：4 个凸顶点，Prev / Next 环形相接
                four[0] = new float2(0f, 0f);
                four[1] = new float2(4f, 0f);
                four[2] = new float2(4f, 4f);
                four[3] = new float2(0f, 4f);
                var square = BuildObstacles(four, new[] { 4 }, out var squarePolygons);
                try
                {
                    Assert.AreEqual(4, square.Length);
                    Assert.AreEqual(1, squarePolygons.Length);
                    Assert.AreEqual(3, square[0].Prev, "首点的 Prev 是末点");
                    Assert.AreEqual(1, square[0].Next);
                    Assert.AreEqual(0, square[3].Next, "末点的 Next 回到首点");
                    for (var i = 0; i < 4; i++)
                    {
                        Assert.AreEqual(1, (int)square[i].IsConvex, $"CCW 正方形的第 {i} 个顶点是凸的");
                    }
                }
                finally
                {
                    square.Dispose();
                    squarePolygons.Dispose();
                }

                // L 形（CCW）：内凹顶点不是凸的
                lShape[0] = new float2(-4f, -4f);
                lShape[1] = new float2(4f, -4f);
                lShape[2] = new float2(4f, 0f);
                lShape[3] = new float2(0f, 0f);
                lShape[4] = new float2(0f, 4f);
                lShape[5] = new float2(-4f, 4f);
                var l = BuildObstacles(lShape, new[] { 6 }, out var lPolygons);
                try
                {
                    Assert.AreEqual(6, l.Length);
                    Assert.AreEqual(5, l[0].Prev, "六边形的第 0 个点 Prev 是末点");
                    Assert.AreEqual(0, (int)l[3].IsConvex, "内凹顶点不是凸的");
                    for (var i = 0; i < 6; i++)
                    {
                        if (i != 3)
                        {
                            Assert.AreEqual(1, (int)l[i].IsConvex, $"第 {i} 个顶点是凸的");
                        }
                    }
                }
                finally
                {
                    l.Dispose();
                    lPolygons.Dispose();
                }
            }
            finally
            {
                two.Dispose();
                four.Dispose();
                lShape.Dispose();
            }
        }

        // ───────────────────────────── 零厚度墙 ─────────────────────────────

        [Test]
        public void 二顶点墙_两侧都不穿()
        {
            var points = new NativeArray<float2>(2, Allocator.Temp);
            try
            {
                points[0] = new float2(-30f, 2f);
                points[1] = new float2(30f, 2f);
                var obstacles = BuildObstacles(points, new[] { 2 }, out var polygons);
                try
                {
                    var below = Agent(float2.zero, float2.zero, pref: new float2(0f, 2f));
                    var belowEnd = Step(below, obstacles.AsArray(), 240);
                    Assert.Less(belowEnd.y, 2f - 0.3f, "下方来的 agent 不许到墙的另一侧");
                    Assert.Greater(belowEnd.y, 1f, "应当朝墙前进（只是被拦住）");
                    Assert.AreEqual(0f, belowEnd.x, 1e-3f, "正对墙的对称场景不该产生侧向漂移");

                    var above = Agent(new float2(0f, 4f), float2.zero, pref: new float2(0f, -2f));
                    var aboveEnd = Step(above, obstacles.AsArray(), 240);
                    Assert.Greater(aboveEnd.y, 2f + 0.3f, "上方来的 agent 不许到墙的另一侧");
                    Assert.Less(aboveEnd.y, 3f, "应当朝墙前进（只是被拦住）");

                    // 反向副本（两条方向相反的独立线段）与单个二顶点多边形给出同一组约束
                    var segments = new NativeArray<RvoObstacleSegment>(2, Allocator.Temp);
                    try
                    {
                        segments[0] = new RvoObstacleSegment { A = points[0], B = points[1] };
                        segments[1] = new RvoObstacleSegment { A = points[1], B = points[0] };
                        var reversed = BuildSegments(segments, out var reversedPolygons);
                        try
                        {
                            var belowAgain = Step(below, reversed.AsArray(), 240);
                            Assert.Less(belowAgain.y, 2f - 0.3f, "反向副本也挡得住");
                            Assert.AreEqual(belowEnd.x, belowAgain.x, 1e-3f);
                            Assert.AreEqual(belowEnd.y, belowAgain.y, 1e-3f);
                        }
                        finally
                        {
                            reversed.Dispose();
                            reversedPolygons.Dispose();
                        }
                    }
                    finally
                    {
                        segments.Dispose();
                    }
                }
                finally
                {
                    obstacles.Dispose();
                    polygons.Dispose();
                }
            }
            finally
            {
                points.Dispose();
            }
        }

        [Test]
        public void 二顶点墙_贴着墙也不被推穿()
        {
            // agent 与墙的距离小于半径：走顶点 / 线段碰撞分支，只允许离开墙的方向
            var points = new NativeArray<float2>(2, Allocator.Temp);
            try
            {
                points[0] = new float2(-10f, 2f);
                points[1] = new float2(10f, 2f);
                var obstacles = BuildObstacles(points, new[] { 2 }, out var polygons);
                try
                {
                    var below = Agent(new float2(0f, 1.6f), float2.zero, pref: new float2(0f, 2f));
                    var belowEnd = Step(below, obstacles.AsArray(), 240);
                    Assert.LessOrEqual(belowEnd.y, 2f - 0.3f, "贴着墙也不许被推穿");
                    Assert.AreEqual(0f, belowEnd.x, 1e-3f);

                    var above = Agent(new float2(0f, 2.4f), float2.zero, pref: new float2(0f, -2f));
                    var aboveEnd = Step(above, obstacles.AsArray(), 240);
                    Assert.GreaterOrEqual(aboveEnd.y, 2f + 0.3f, "另一侧贴着墙也不许被推穿");
                    Assert.AreEqual(0f, aboveEnd.x, 1e-3f);
                }
                finally
                {
                    obstacles.Dispose();
                    polygons.Dispose();
                }
            }
            finally
            {
                points.Dispose();
            }
        }

        // ───────────────────────────── 闭合多边形 ─────────────────────────────

        [Test]
        public void 闭合方形_外侧朝每个面走都不穿()
        {
            var points = new NativeArray<float2>(4, Allocator.Temp);
            try
            {
                points[0] = new float2(-3f, -3f);
                points[1] = new float2(3f, -3f);
                points[2] = new float2(3f, 3f);
                points[3] = new float2(-3f, 3f);
                var obstacles = BuildObstacles(points, new[] { 4 }, out var polygons);
                try
                {
                    var starts = new[]
                    {
                        new float2(-8f, 0f), new float2(8f, 0f), new float2(0f, -8f), new float2(0f, 8f),
                        new float2(-6f, -6f), new float2(6f, 6f),
                    };
                    var prefs = new[]
                    {
                        new float2(2f, 0f), new float2(-2f, 0f), new float2(0f, 2f), new float2(0f, -2f),
                        new float2(2f, 2f), new float2(-2f, -2f),
                    };

                    for (var index = 0; index < starts.Length; index++)
                    {
                        var inside = false;
                        var end = Step(Agent(starts[index], float2.zero, pref: prefs[index]), obstacles.AsArray(),
                                       600, (position, _) =>
                                       {
                                           inside |= math.abs(position.x) < 3f - 0.1f &&
                                                     math.abs(position.y) < 3f - 0.1f;
                                       });
                        Assert.IsFalse(inside, $"第 {index} 条轨迹进到方形内部");
                        Assert.IsTrue(math.all(math.isfinite(end)));
                    }
                }
                finally
                {
                    obstacles.Dispose();
                    polygons.Dispose();
                }
            }
            finally
            {
                points.Dispose();
            }
        }

        [Test]
        public void 闭合方形_内部出发_数值安全且不超上限()
        {
            var points = new NativeArray<float2>(4, Allocator.Temp);
            try
            {
                points[0] = new float2(-3f, -3f);
                points[1] = new float2(3f, -3f);
                points[2] = new float2(3f, 3f);
                points[3] = new float2(-3f, 3f);
                var obstacles = BuildObstacles(points, new[] { 4 }, out var polygons);
                try
                {
                    // RVO2 假定 agent 在障碍物外侧；内部只要求数值安全、不更深
                    var end = Step(Agent(float2.zero, float2.zero, pref: new float2(2f, 2f)),
                                   obstacles.AsArray(), 300);
                    Assert.IsTrue(math.all(math.isfinite(end)));
                }
                finally
                {
                    obstacles.Dispose();
                    polygons.Dispose();
                }
            }
            finally
            {
                points.Dispose();
            }
        }

        [Test]
        public void 凹角L形_冲向内凹角不穿且数值安全()
        {
            var points = new NativeArray<float2>(6, Allocator.Temp);
            try
            {
                // CCW：外框 [-4,4]^2 减去右上角的 [0,4]x[0,4]，内凹角在 (0,0)
                points[0] = new float2(-4f, -4f);
                points[1] = new float2(4f, -4f);
                points[2] = new float2(4f, 0f);
                points[3] = new float2(0f, 0f);
                points[4] = new float2(0f, 4f);
                points[5] = new float2(-4f, 4f);
                var obstacles = BuildObstacles(points, new[] { 6 }, out var polygons);
                try
                {
                    var starts = new[] { new float2(2f, 2f), new float2(3f, 1f), new float2(1f, 3f) };
                    var prefs = new[] { new float2(-2f, -2f), new float2(-2f, -0.7f), new float2(-0.7f, -2f) };

                    for (var index = 0; index < starts.Length; index++)
                    {
                        // 缺口区是 x > 0 且 z > 0；障碍物在 x < 0 或 z < 0 —— 不许越过去
                        var entered = false;
                        Step(Agent(starts[index], float2.zero, pref: prefs[index]), obstacles.AsArray(), 400,
                             (position, _) => { entered |= position.x < -0.1f || position.y < -0.1f; });
                        Assert.IsFalse(entered, $"第 {index} 条轨迹越过内凹角进到障碍物里");
                    }
                }
                finally
                {
                    obstacles.Dispose();
                    polygons.Dispose();
                }
            }
            finally
            {
                points.Dispose();
            }
        }

        // ───────────────────────────── 邻边机制 ─────────────────────────────

        [Test]
        public void 邻边凸角_腿被换成邻边截断线方向()
        {
            // 20° 尖角三角形 A(0,0) B(10,0) C=(10cos20°, 10sin20°)：
            // agent 在 A 左侧斜看 A，A→B 的左腿会指进相邻的 C→A 边，因此被换成 −UnitDir(C→A)
            const float tip = 20f;
            var points = new NativeArray<float2>(3, Allocator.Temp);
            try
            {
                points[0] = new float2(0f, 0f);
                points[1] = new float2(10f, 0f);
                points[2] = new float2(10f * math.cos(math.radians(tip)), 10f * math.sin(math.radians(tip)));
                var obstacles = BuildObstacles(points, new[] { 3 }, out var polygons);
                try
                {
                    var self = Agent(new float2(-1f, 3f), new float2(1f, -1f), pref: new float2(1f, -1f));
                    var lines = new FixedList512Bytes<OrcaLine>();
                    var added = OrcaSolver.BuildObstacleLines(self, obstacles.AsArray(), obstacles.Length,
                                                             k_TimeHorizonObst, ref lines);

                    Assert.AreEqual(1, added, "这个位形下只应当产生一条障碍物约束");
                    var corner = obstacles[0];
                    var neighbor = obstacles[corner.Prev];
                    var expected = -neighbor.UnitDir;

                    Assert.AreEqual(expected.x, lines[0].Direction.x, 1e-3f, "腿应当被换成邻边的截断线方向");
                    Assert.AreEqual(expected.y, lines[0].Direction.y, 1e-3f);

                    // 方向不是本边自己的方向 —— 用的是邻边
                    Assert.Greater(math.abs(OrcaSolver.Det(lines[0].Direction, corner.UnitDir)), 0.1f,
                        "约束方向不应当来自本边");

                    // 这条约束不许把速度指向障碍物内部：整段轨迹都留在三角形外
                    var entered = false;
                    var approach = Agent(new float2(-6f, 1.2f), float2.zero, pref: new float2(2f, 0f));
                    Step(approach, obstacles.AsArray(), 400, (position, _) =>
                    {
                        entered |= InsideTriangle(position, points[0], points[1], points[2]);
                    });
                    Assert.IsFalse(entered, "不许进入三角形内部");
                }
                finally
                {
                    obstacles.Dispose();
                    polygons.Dispose();
                }
            }
            finally
            {
                points.Dispose();
            }
        }

        // ───────────────────────────── 退化输入 ─────────────────────────────

        [Test]
        public void 退化_零长边与NaN顶点与顶点数不足的多边形()
        {
            var points = new NativeArray<float2>(8, Allocator.Temp);
            try
            {
                points[0] = new float2(float.NaN, 0f);   // 非有限顶点
                points[1] = new float2(1f, 1f);
                points[2] = new float2(0f, 0f);          // 与 [3] 重合 ⇒ 零长边
                points[3] = new float2(0f, 0f);
                points[4] = new float2(5f, 0f);
                points[5] = new float2(-8f, -3f);        // 候选范围外
                points[6] = new float2(-8f, 3f);
                points[7] = new float2(9f, 9f);          // 顶点数不足，整段跳过

                var obstacles = BuildObstacles(points, new[] { 2, 3, 2, 1 }, out var polygons);
                try
                {
                    Assert.AreEqual(3, polygons.Length, "顶点数 < 2 的分段不是障碍物");
                    Assert.AreEqual(7, obstacles.Length);
                    Assert.IsTrue(math.all(math.isfinite(obstacles[0].Point)), "非有限顶点被写成了有限值");
                    Assert.AreEqual(float2.zero, obstacles[2].UnitDir, "零长边的 UnitDir 退化成零向量");

                    var onLine = Agent(float2.zero, new float2(1f, 0f), pref: new float2(0f, 2f));
                    var atVertex = Agent(new float2(5f, 0f), new float2(1f, 0f), pref: new float2(0f, 2f));
                    var nanPosition = Agent(new float2(float.NaN, 0f), float2.zero, pref: new float2(1f, 1f));

                    var line = Solve(onLine, obstacles.AsArray());
                    var vertex = Solve(atVertex, obstacles.AsArray());
                    var nan = Solve(nanPosition, obstacles.AsArray());

                    AssertFiniteAndCapped(onLine, line, "正好在线上");
                    AssertFiniteAndCapped(atVertex, vertex, "正好在顶点上");
                    AssertFiniteAndCapped(nanPosition, nan, "agent 位置为 NaN");

                    // 条数越界与负数都收敛到合法区间
                    var negative = OrcaSolver.Solve(onLine, default, 0, obstacles.AsArray(), -3,
                                                    k_TimeHorizon, k_TimeHorizonObst, k_TimeStep);
                    var overflow = OrcaSolver.Solve(onLine, default, 0, obstacles.AsArray(), 99,
                                                    k_TimeHorizon, k_TimeHorizonObst, k_TimeStep);
                    AssertFiniteAndCapped(onLine, negative, "负条数");
                    AssertFiniteAndCapped(onLine, overflow, "超长条数");

                    // 未创建的顶点数组什么都不做
                    var notCreated = OrcaSolver.Solve(onLine, default, 0, default(NativeArray<RvoObstacleVertex>), 0,
                                                      k_TimeHorizon, k_TimeHorizonObst, k_TimeStep);
                    AssertFiniteAndCapped(onLine, notCreated, "未创建的顶点数组");
                }
                finally
                {
                    obstacles.Dispose();
                    polygons.Dispose();
                }
            }
            finally
            {
                points.Dispose();
            }
        }

        [Test]
        public void 障碍物线超过半平面容量_被截断且求解仍然有限()
        {
            const int count = 40;
            var ring = new NativeArray<RvoObstacleSegment>(count, Allocator.Temp);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var angle = i * math.PI * 2f / count;
                    var direction = new float2(math.cos(angle), math.sin(angle));
                    var side = new float2(-direction.y, direction.x);
                    ring[i] = new RvoObstacleSegment
                    {
                        A = direction * 3f - side * 2f,
                        B = direction * 3f + side * 2f,
                    };
                }

                var obstacles = BuildSegments(ring, out var polygons);
                var self = Agent(float2.zero, float2.zero, pref: new float2(1f, 0f));
                try
                {
                    Assert.AreEqual(count, polygons.Length);
                    Assert.AreEqual(count * 2, obstacles.Length);

                    var lines = new FixedList512Bytes<OrcaLine>();
                    var added = OrcaSolver.BuildObstacleLines(self, obstacles.AsArray(), obstacles.Length,
                                                             k_TimeHorizonObst, ref lines);
                    Assert.Greater(added, 0, "环形障碍物里至少要有一条产生约束");
                    Assert.LessOrEqual(added, OrcaSolver.MaxLines, "半平面条数受容量限制");
                    Assert.AreEqual(added, lines.Length);

                    AssertFiniteAndCapped(self, Solve(self, obstacles.AsArray()), "环形障碍物");
                }
                finally
                {
                    obstacles.Dispose();
                    polygons.Dispose();
                }
            }
            finally
            {
                ring.Dispose();
            }
        }

        [Test]
        public void 点到线段距离_端点外取端点零长退化为点()
        {
            var a = new float2(0f, 0f);
            var b = new float2(4f, 0f);

            Assert.AreEqual(4f, OrcaSolver.DistSqPointLineSegment(a, b, new float2(2f, 2f)), 1e-3f);
            Assert.AreEqual(0f, OrcaSolver.DistSqPointLineSegment(a, b, new float2(2f, 0f)), 1e-4f);
            Assert.AreEqual(25f, OrcaSolver.DistSqPointLineSegment(a, b, new float2(-3f, 4f)), 1e-3f, "投影在 A 之外取到 A 的距离");
            Assert.AreEqual(25f, OrcaSolver.DistSqPointLineSegment(a, b, new float2(7f, 4f)), 1e-3f, "投影在 B 之外取到 B 的距离");
            Assert.AreEqual(9f, OrcaSolver.DistSqPointLineSegment(a, a, new float2(3f, 0f)), 1e-4f, "零长线段退化成点到点");
        }

        // ───────────────────────────── 与不含障碍物一致 ─────────────────────────────

        [Test]
        public void 无障碍物_结果与不含障碍物的求解完全一致()
        {
            var self = Agent(new float2(0f, 0f), new float2(1f, 0f), pref: new float2(2f, 0f));
            var other = Agent(new float2(3f, 0.4f), new float2(-1f, 0f));
            var neighbors = new NativeArray<AgentState>(1, Allocator.Temp);
            var empty = new NativeArray<RvoObstacleVertex>(0, Allocator.Temp);
            try
            {
                neighbors[0] = other;
                var baseline = OrcaSolver.Solve(self, neighbors, 1, k_TimeHorizon, k_TimeStep);

                // 未创建 / 长度为 0 / τ_obst <= 0 三条路都必须与不含障碍物时逐位相同
                var notCreated = OrcaSolver.Solve(self, neighbors, 1, default(NativeArray<RvoObstacleVertex>), 0,
                                                  k_TimeHorizon, k_TimeHorizonObst, k_TimeStep);
                var zeroCount = OrcaSolver.Solve(self, neighbors, 1, empty, 0,
                                                 k_TimeHorizon, k_TimeHorizonObst, k_TimeStep);
                var zeroHorizon = OrcaSolver.Solve(self, neighbors, 1, empty, 0,
                                                   k_TimeHorizon, 0f, k_TimeStep);

                Assert.AreEqual(baseline.x, notCreated.x, 0f);
                Assert.AreEqual(baseline.y, notCreated.y, 0f);
                Assert.AreEqual(baseline.x, zeroCount.x, 0f);
                Assert.AreEqual(baseline.y, zeroCount.y, 0f);
                Assert.AreEqual(baseline.x, zeroHorizon.x, 0f);
                Assert.AreEqual(baseline.y, zeroHorizon.y, 0f);
            }
            finally
            {
                neighbors.Dispose();
                empty.Dispose();
            }
        }

        [Test]
        public void 障碍物在背后_结果与无约束一致()
        {
            var points = new NativeArray<float2>(2, Allocator.Temp);
            try
            {
                points[0] = new float2(-3f, -6f);
                points[1] = new float2(-3f, 6f);
                var obstacles = BuildObstacles(points, new[] { 2 }, out var polygons);
                try
                {
                    var self = Agent(float2.zero, new float2(2f, 0f), pref: new float2(2f, 0f));

                    // 前提：这条边在候选范围内（距离过滤放行了它），所以「没影响」不是被距离过滤掉的
                    Assert.LessOrEqual(OrcaSolver.DistSqPointLineSegment(points[0], points[1], self.Position),
                                       RangeSq(self));

                    var baseline = SolveWithoutObstacles(self);
                    var result = Solve(self, obstacles.AsArray());

                    Assert.AreEqual(baseline.x, result.x, 0f);
                    Assert.AreEqual(baseline.y, result.y, 0f);
                }
                finally
                {
                    obstacles.Dispose();
                    polygons.Dispose();
                }
            }
            finally
            {
                points.Dispose();
            }
        }

        [Test]
        public void 候选范围_范围外的墙不产生约束()
        {
            var near = new NativeArray<float2>(2, Allocator.Temp);
            var far = new NativeArray<float2>(2, Allocator.Temp);
            try
            {
                near[0] = new float2(3f, 6f);
                near[1] = new float2(3f, -6f);
                far[0] = new float2(50f, 6f);
                far[1] = new float2(50f, -6f);
                var nearObstacles = BuildObstacles(near, new[] { 2 }, out var nearPolygons);
                var farObstacles = BuildObstacles(far, new[] { 2 }, out var farPolygons);
                try
                {
                    var self = Agent(float2.zero, new float2(2f, 0f), pref: new float2(2f, 0f));
                    var baseline = SolveWithoutObstacles(self);

                    Assert.Greater(OrcaSolver.DistSqPointLineSegment(far[0], far[1], self.Position), RangeSq(self),
                        "远墙应当落在候选范围外");
                    var farResult = Solve(self, farObstacles.AsArray());
                    Assert.AreEqual(baseline.x, farResult.x, 0f);
                    Assert.AreEqual(baseline.y, farResult.y, 0f);

                    Assert.LessOrEqual(OrcaSolver.DistSqPointLineSegment(near[0], near[1], self.Position), RangeSq(self),
                        "近墙应当落在候选范围内");
                    var nearResult = Solve(self, nearObstacles.AsArray());
                    AssertFiniteAndCapped(self, nearResult, "近墙");
                    Assert.Greater(math.distance(nearResult, baseline), 0.1f, "范围内的墙应当收紧速度");
                }
                finally
                {
                    nearObstacles.Dispose();
                    nearPolygons.Dispose();
                    farObstacles.Dispose();
                    farPolygons.Dispose();
                }
            }
            finally
            {
                near.Dispose();
                far.Dispose();
            }
        }

        // ───────────────────────────── 配置 ─────────────────────────────

        [Test]
        public void 默认配置_不启用障碍物且时间视野为正()
        {
            var config = RvoConfig.Default;
            Assert.IsFalse(config.UseObstacles, "默认必须关掉：关闭时行为与不含障碍物时一致");
            Assert.Greater(config.TimeHorizonObst, 0f);
        }
    }
}
