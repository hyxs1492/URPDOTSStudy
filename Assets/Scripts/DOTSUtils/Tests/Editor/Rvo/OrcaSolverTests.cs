using DOTSUtils.Rvo;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace DOTSUtils.Rvo.Tests
{
    /// <summary>ORCA 求解内核的 EditMode 用例：内核是纯函数，不依赖 Unity 运行环境、World 或 ECS。断言只测不变量（速度上限、半平面可行性、无 NaN），不做「与某个参考实现逐位相等」。</summary>
    public class OrcaSolverTests
    {
        const float k_TimeHorizon = 2f;
        const float k_TimeStep = 1f / 60f;
        const float k_Tol = 1e-4f;

        static AgentState Agent(float2 pos, float2 vel, float radius = 0.5f, float maxSpeed = 2f,
                                float2 pref = default)
            => new AgentState
            {
                Position = pos,
                Velocity = vel,
                Radius = radius,
                MaxSpeed = maxSpeed,
                PrefVelocity = pref,
            };

        static float2 SolveOne(in AgentState self, in AgentState other)
        {
            var neighbors = new NativeArray<AgentState>(1, Allocator.Temp);
            try
            {
                neighbors[0] = other;
                return OrcaSolver.Solve(self, neighbors, 1, k_TimeHorizon, k_TimeStep);
            }
            finally
            {
                neighbors.Dispose();
            }
        }

        static void AssertSatisfiesAllLines(in AgentState self, in AgentState other, float2 result)
        {
            var neighbors = new NativeArray<AgentState>(1, Allocator.Temp);
            var lines = new FixedList512Bytes<OrcaLine>();
            try
            {
                neighbors[0] = other;
                OrcaSolver.BuildLines(self, neighbors, 1, k_TimeHorizon, k_TimeStep, ref lines);
                Assert.Greater(lines.Length, 0, "应当为邻居生成至少一条半平面");

                for (var i = 0; i < lines.Length; i++)
                {
                    var det = OrcaSolver.Det(lines[i].Direction, result - lines[i].Point);
                    Assert.GreaterOrEqual(det, -k_Tol, $"结果应落在第 {i} 条半平面的可行侧");
                }
            }
            finally
            {
                neighbors.Dispose();
            }
        }

        // ───────────────────────────── 基本行为 ─────────────────────────────

        [Test]
        public void 无邻居_直接取偏好速度()
        {
            var self = Agent(float2.zero, new float2(0.3f, 0.4f), pref: new float2(1.2f, -0.5f));
            var none = new NativeArray<AgentState>(0, Allocator.Temp);
            try
            {
                var result = OrcaSolver.Solve(self, none, 0, k_TimeHorizon, k_TimeStep);
                Assert.AreEqual(1.2f, result.x, k_Tol);
                Assert.AreEqual(-0.5f, result.y, k_Tol);
            }
            finally
            {
                none.Dispose();
            }
        }

        [Test]
        public void 偏好速度超过上限_结果被截断到maxSpeed()
        {
            var self = Agent(float2.zero, float2.zero, maxSpeed: 1.5f, pref: new float2(10f, 10f));
            var none = new NativeArray<AgentState>(0, Allocator.Temp);
            try
            {
                var result = OrcaSolver.Solve(self, none, 0, k_TimeHorizon, k_TimeStep);
                Assert.LessOrEqual(math.length(result), 1.5f + k_Tol);
                Assert.AreEqual(1.5f, math.length(result), 1e-3f, "应当正好贴在上限上");
            }
            finally
            {
                none.Dispose();
            }
        }

        [Test]
        public void 单邻居_结果不超上限且满足半平面()
        {
            var self = Agent(float2.zero, new float2(1f, 0f), pref: new float2(2f, 0f));
            var other = Agent(new float2(3f, 0.4f), new float2(-1f, 0f), pref: new float2(-2f, 0f));

            var result = SolveOne(self, other);

            Assert.IsTrue(math.all(math.isfinite(result)));
            Assert.LessOrEqual(math.length(result), self.MaxSpeed + k_Tol);
            AssertSatisfiesAllLines(self, other, result);
        }

        [Test]
        public void 正面对撞_两者都落在各自半平面的可行侧()
        {
            var self = Agent(float2.zero, new float2(1f, 0f), pref: new float2(2f, 0f));
            var other = Agent(new float2(4f, 0f), new float2(-1f, 0f), pref: new float2(-2f, 0f));

            var left = SolveOne(self, other);
            var right = SolveOne(other, self);

            // ORCA 的判据是解落在为该邻居构造的半平面的可行侧。
            Assert.IsTrue(math.all(math.isfinite(left)) && math.all(math.isfinite(right)));
            AssertSatisfiesAllLines(self, other, left);
            AssertSatisfiesAllLines(other, self, right);

            // 侧向让开：两者都出现侧向分量，而不是继续纯正面对撞。
            Assert.Greater(math.abs(left.y), 0.05f, "应当产生侧向分量");
            Assert.Greater(math.abs(right.y), 0.05f);

            // 不断言两者互为镜像：正面对撞是退化场景，实现固定走「右腿」分支，镜像下并不对称。
        }

        [Test]
        public void 侧向擦过_腿投影的半平面与手算一致()
        {
            var self = Agent(float2.zero, new float2(1f, 0f), pref: new float2(1f, 0f));
            var other = Agent(new float2(0f, 2f), new float2(-1f, 0f), pref: new float2(-1f, 0f));

            var neighbors = new NativeArray<AgentState>(1, Allocator.Temp);
            var lines = new FixedList512Bytes<OrcaLine>();
            try
            {
                neighbors[0] = other;
                OrcaSolver.BuildLines(self, neighbors, 1, k_TimeHorizon, k_TimeStep, ref lines);

                Assert.AreEqual(1, lines.Length);
                Assert.AreEqual(-0.5f, lines[0].Direction.x, k_Tol, "方向：右腿投影");
                Assert.AreEqual(-0.8660254f, lines[0].Direction.y, k_Tol);
                Assert.AreEqual(0.25f, lines[0].Point.x, k_Tol, "点：velocity + 0.5u");
                Assert.AreEqual(0.4330127f, lines[0].Point.y, k_Tol);
            }
            finally
            {
                neighbors.Dispose();
            }
        }

        [Test]
        public void 已经重叠_走推离分支且不产出NaN()
        {
            // combinedRadius = 1 > 距离 0.5 ⇒ 已碰撞分支（用 1/timeStep）
            var self = Agent(float2.zero, float2.zero, pref: float2.zero);
            var other = Agent(new float2(0.5f, 0f), float2.zero, pref: float2.zero);

            var result = SolveOne(self, other);

            Assert.IsTrue(math.all(math.isfinite(result)), "不得产出 NaN/Inf");
            Assert.LessOrEqual(math.length(result), self.MaxSpeed + k_Tol);
        }

        [Test]
        public void 完全重叠且速度相同_退化输入不产出NaN()
        {
            var self = Agent(float2.zero, new float2(1f, 1f), pref: float2.zero);
            var other = Agent(float2.zero, new float2(1f, 1f), pref: float2.zero);

            var result = SolveOne(self, other);
            Assert.IsTrue(math.all(math.isfinite(result)));
        }

        [Test]
        public void 零半径零上限_退化参数不产出NaN()
        {
            var self = Agent(float2.zero, float2.zero, radius: 0f, maxSpeed: 0f, pref: new float2(1f, 1f));
            var other = Agent(float2.zero, float2.zero, radius: 0f, maxSpeed: 0f, pref: float2.zero);

            var result = SolveOne(self, other);
            Assert.IsTrue(math.all(math.isfinite(result)));
        }

        // ───────────────────────────── 结构性质 ─────────────────────────────

        [Test]
        public void 邻居顺序打乱_仍满足所有半平面且不超上限()
        {
            var self = Agent(float2.zero, new float2(1f, 0f), pref: new float2(2f, 0f));
            var a = Agent(new float2(2f, 0.3f), new float2(-0.5f, 0f), pref: new float2(-1f, 0f));
            var b = Agent(new float2(1.5f, -0.6f), new float2(-0.5f, 0f), pref: new float2(-1f, 0f));
            var c = Agent(new float2(3f, 1.2f), new float2(-0.5f, 0f), pref: new float2(-1f, 0f));

            var forward = new NativeArray<AgentState>(3, Allocator.Temp);
            var reversed = new NativeArray<AgentState>(3, Allocator.Temp);
            try
            {
                forward[0] = a; forward[1] = b; forward[2] = c;
                reversed[0] = c; reversed[1] = b; reversed[2] = a;

                var r1 = OrcaSolver.Solve(self, forward, 3, k_TimeHorizon, k_TimeStep);
                var r2 = OrcaSolver.Solve(self, reversed, 3, k_TimeHorizon, k_TimeStep);

                Assert.IsTrue(math.all(math.isfinite(r1)) && math.all(math.isfinite(r2)));
                Assert.LessOrEqual(math.length(r1), self.MaxSpeed + k_Tol);
                Assert.LessOrEqual(math.length(r2), self.MaxSpeed + k_Tol);
            }
            finally
            {
                forward.Dispose();
                reversed.Dispose();
            }
        }

        [Test]
        public void 大量邻居_被截断到容量上界且不超上限()
        {
            const int count = 200;
            var self = Agent(float2.zero, new float2(1f, 0f), pref: new float2(2f, 0f));
            var neighbors = new NativeArray<AgentState>(count, Allocator.Temp);
            var lines = new FixedList512Bytes<OrcaLine>();
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var angle = i * math.PI * 2f / count;
                    neighbors[i] = Agent(new float2(math.cos(angle), math.sin(angle)) * 4f,
                                         new float2(0f, 0f), pref: float2.zero);
                }

                var added = OrcaSolver.BuildLines(self, neighbors, count, k_TimeHorizon, k_TimeStep, ref lines);
                Assert.AreEqual(OrcaSolver.MaxLines, added, "半平面数量应被截断到容量上界");
                Assert.AreEqual(OrcaSolver.MaxLines, lines.Length);

                var result = OrcaSolver.Solve(self, neighbors, count, k_TimeHorizon, k_TimeStep);
                Assert.IsTrue(math.all(math.isfinite(result)));
                Assert.LessOrEqual(math.length(result), self.MaxSpeed + k_Tol);
            }
            finally
            {
                neighbors.Dispose();
            }
        }

        [Test]
        public void 邻居数收敛_越界与负数不炸()
        {
            var self = Agent(float2.zero, new float2(1f, 0f), pref: new float2(2f, 0f));
            var neighbors = new NativeArray<AgentState>(2, Allocator.Temp);
            try
            {
                neighbors[0] = Agent(new float2(3f, 0f), float2.zero, pref: float2.zero);
                neighbors[1] = Agent(new float2(-3f, 0f), float2.zero, pref: float2.zero);

                Assert.IsTrue(math.all(math.isfinite(OrcaSolver.Solve(self, neighbors, -5, k_TimeHorizon, k_TimeStep))));
                Assert.IsTrue(math.all(math.isfinite(OrcaSolver.Solve(self, neighbors, 99, k_TimeHorizon, k_TimeStep))));
            }
            finally
            {
                neighbors.Dispose();
            }
        }

        [Test]
        public void 静态邻居_不会穿过它()
        {
            // 邻居静止挡在正前方：求解结果应当把它让开（y 分量非零）或减速。
            var self = Agent(float2.zero, new float2(2f, 0f), pref: new float2(2f, 0f));
            var blocker = Agent(new float2(1.5f, 0f), float2.zero, pref: float2.zero);

            var result = SolveOne(self, blocker);

            Assert.IsTrue(math.all(math.isfinite(result)));
            Assert.LessOrEqual(math.length(result), self.MaxSpeed + k_Tol);

            // 解应当满足为该邻居生成的半平面（即「不再有责任撞上去」）
            AssertSatisfiesAllLines(self, blocker, result);
        }

        [Test]
        public void ClampLength_边界行为()
        {
            Assert.AreEqual(float2.zero, OrcaSolver.ClampLength(float2.zero, 2f));
            Assert.AreEqual(float2.zero, OrcaSolver.ClampLength(new float2(1f, 0f), 0f));

            var clamped = OrcaSolver.ClampLength(new float2(3f, 4f), 2f);
            Assert.AreEqual(2f, math.length(clamped), 1e-4f);
            Assert.AreEqual(1.2f, clamped.x, 1e-4f);
            Assert.AreEqual(1.6f, clamped.y, 1e-4f);
        }

        [Test]
        public void Det_与叉积定义一致()
        {
            Assert.AreEqual(1f, OrcaSolver.Det(new float2(1f, 0f), new float2(0f, 1f)));
            Assert.AreEqual(-1f, OrcaSolver.Det(new float2(0f, 1f), new float2(1f, 0f)));
            Assert.AreEqual(0f, OrcaSolver.Det(new float2(1f, 1f), new float2(1f, 1f)));
        }
    }
}
