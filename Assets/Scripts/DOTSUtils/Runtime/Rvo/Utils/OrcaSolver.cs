using Unity.Collections;
using Unity.Mathematics;

namespace DOTSUtils.Rvo
{
    /// <summary>
    /// RVO2/ORCA 求解内核：<b>纯函数</b> —— 不认识 ECS、不碰 World、不做托管分配。
    ///
    /// 语义逐条对齐 RVO2 Library（Jur van den Berg / UNC）的
    /// <c>Agent::computeNewVelocity</c> 与 <c>linearProgram1/2/3</c>，包括：
    /// <list type="bullet">
    /// <item>邻居半平面的三分支构造（截断圆锥投影 / 左右腿投影 / 已重叠时用 <c>1/timeStep</c>）；</item>
    /// <item>静态障碍物按 <b>顶点 + 多边形拓扑</b> 处理：候选顶点按到线段 <c>[Point, Next.Point]</c> 的距离升序，
    /// 已加入的障碍物线做覆盖判定，凸顶点的腿按 <c>Prev</c>/<c>Next</c> 的邻边方向修正；</item>
    /// <item><c>point = v + 0.5u</c> 的「各担一半责任」语义；</item>
    /// <item>顺序贪心的 <c>LinearProgram2</c> 与兜底的 <c>LinearProgram3</c>；</item>
    /// <item><see cref="Epsilon"/> 与 RVO2 的 <c>RVO_EPSILON</c> 同值。</item>
    /// </list>
    ///
    /// 与 RVO2 的差异（有意为之）：
    /// <list type="bullet">
    /// <item>半平面容量固定为 <see cref="MaxLines"/>（障碍物线先占位，邻居线用剩下的）；</item>
    /// <item>邻居由调用方给定，因此只保证不变量（速度上限、半平面约束、无 NaN），
    /// 不保证与某个参考实现逐位相等；</item>
    /// <item>退化输入（零半径、零距离、零速度、零长边、非有限顶点、agent 正好落在顶点上）会走显式兜底，
    /// 不产出 NaN/Inf。</item>
    /// </list>
    ///
    /// 线程约定：全部只读入参 + 栈上缓冲，可安全地在任意并行 job 里调用。
    /// </summary>
    public static class OrcaSolver
    {
        /// <summary>
        /// 与 RVO2 的 <c>RVO_EPSILON</c> 同值。
        /// </summary>
        public const float Epsilon = 1e-5f;

        /// <summary>
        /// 一次求解能容纳的邻居半平面数。取 <c>FixedList512Bytes&lt;OrcaLine&gt;</c> 的实际容量：
        /// 512 字节减去 FixedList 的 2 字节长度头，再按 <see cref="OrcaLine"/> 的 16 字节切分 = <b>31</b>。
        /// 超出的邻居会被截断（调用方应按距离先排序，保证被丢的是最远的那些）。
        /// </summary>
        public const int MaxLines = 31;

        /// <summary>
        /// 求 <paramref name="self"/> 的新速度（xz）。
        ///
        /// <paramref name="neighbors"/> 里只有前 <paramref name="neighborCount"/> 个元素参与求解；
        /// 传负值或超长时会自动收敛到 <c>[0, neighbors.Length]</c>。
        /// </summary>
        /// <param name="timeHorizon">时间视野 τ（&gt; 0）。默认 2 秒。</param>
        /// <param name="timeStep">当前帧长（&gt; 0），只影响「已经重叠」那一分支的推离强度。</param>
        public static float2 Solve(in AgentState self, NativeArray<AgentState> neighbors, int neighborCount,
                                   float timeHorizon, float timeStep)
        {
            var lines = new FixedList512Bytes<OrcaLine>();
            BuildLines(self, neighbors, neighborCount, timeHorizon, timeStep, ref lines);
            return SolveLines(self, lines, 0);
        }

        /// <summary>
        /// 便捷重载：把整个数组当邻居列表。
        /// </summary>
        public static float2 Solve(in AgentState self, NativeArray<AgentState> neighbors,
                                   float timeHorizon, float timeStep)
            => Solve(self, neighbors, neighbors.IsCreated ? neighbors.Length : 0, timeHorizon, timeStep);

        /// <summary>
        /// 求 <paramref name="self"/> 的新速度（xz），并把静态障碍物并入约束。
        ///
        /// <paramref name="vertices"/> 是扁平的障碍物顶点数组（拓扑在 <c>Prev</c>/<c>Next</c> 字段里，
        /// 见 <see cref="RvoObstacleBuilder"/>），只有前 <paramref name="vertexCount"/> 个参与求解；
        /// 障碍物半平面先于邻居半平面加入 —— <see cref="LinearProgram3"/> 会把它们当作必须保留的种子集。
        /// <paramref name="vertices"/> 未创建（<c>default</c>）或 <paramref name="vertexCount"/> 为 0 时
        /// 与 <see cref="Solve(in AgentState, NativeArray{AgentState}, int, float, float)"/> 等价。
        /// </summary>
        /// <param name="timeHorizon">时间视野 τ（&gt; 0）。默认 2 秒。</param>
        /// <param name="timeHorizonObst">障碍物时间视野 τ_obst（&gt; 0）；同时决定候选范围
        /// <c>τ_obst·maxSpeed + radius</c>，超出该范围的顶点不产生约束。</param>
        /// <param name="timeStep">当前帧长（&gt; 0），只影响「已经重叠」那一分支的推离强度。</param>
        public static float2 Solve(in AgentState self, NativeArray<AgentState> neighbors, int neighborCount,
                                   NativeArray<RvoObstacleVertex> vertices, int vertexCount,
                                   float timeHorizon, float timeHorizonObst, float timeStep)
        {
            var lines = new FixedList512Bytes<OrcaLine>();
            var obstacleLines = BuildObstacleLines(self, vertices, vertexCount, timeHorizonObst, ref lines);
            BuildLines(self, neighbors, neighborCount, timeHorizon, timeStep, ref lines);
            return SolveLines(self, lines, obstacleLines);
        }

        /// <summary>
        /// 解已经建好的半平面集合。<paramref name="obstacleLineCount"/> 是排在最前面的障碍物半平面条数，
        /// 它们不会被 <see cref="LinearProgram3"/> 的两两投影替换掉。
        /// </summary>
        private static float2 SolveLines(in AgentState self, in FixedList512Bytes<OrcaLine> lines,
                                         int obstacleLineCount)
        {
            var maxSpeed = math.max(0f, FiniteOr(self.MaxSpeed, 0f));
            var optimum = ClampLength(self.PrefVelocity, maxSpeed);

            if (lines.Length == 0)
                return math.all(math.isfinite(optimum)) ? optimum : float2.zero;

            var result = float2.zero;
            var failedLine = LinearProgram2(lines, maxSpeed, optimum, false, ref result);
            if (failedLine < lines.Length)
                LinearProgram3(lines, obstacleLineCount, failedLine, maxSpeed, ref result);

            // 兜底：任何数值意外都不该把 NaN 传出去。
            return math.all(math.isfinite(result)) ? result : float2.zero;
        }

        // ───────────────────────── 半平面构造 ─────────────────────────

        /// <summary>
        /// 为 <paramref name="self"/> 与每个邻居构造一条 ORCA 半平面，追加进 <paramref name="lines"/>。
        /// 返回实际加入的条数（上限 <see cref="MaxLines"/>）。
        /// </summary>
        public static int BuildLines(in AgentState self, NativeArray<AgentState> neighbors, int neighborCount,
                                     float timeHorizon, float timeStep, ref FixedList512Bytes<OrcaLine> lines)
        {
            if (!neighbors.IsCreated)
            {
                return 0;
            }

            var count = math.clamp(neighborCount, 0, neighbors.Length);
            var added = 0;
            for (var i = 0; i < count; i++)
            {
                if (lines.Length >= MaxLines)
                {
                    break;
                }
                BuildLine(self, neighbors[i], timeHorizon, timeStep, out var line);
                lines.Add(line);
                added++;
            }

            return added;
        }

        /// <summary>
        /// 单个邻居的半平面（RVO2 的 <c>Agent::computeNewVelocity</c> 里的那段循环体）。
        /// </summary>
        public static void BuildLine(in AgentState self, in AgentState other, float timeHorizon, float timeStep,
                                     out OrcaLine line)
        {
            var relativePosition = other.Position - self.Position;
            var relativeVelocity = self.Velocity - other.Velocity;
            var distSq = math.lengthsq(relativePosition);
            var combinedRadius = self.Radius + other.Radius;
            var combinedRadiusSq = combinedRadius * combinedRadius;

            float2 u;

            if (distSq > combinedRadiusSq)
            {
                // 还没撞上：截断圆锥（cut-off circle 或两条腿）
                var invTimeHorizon = 1f / timeHorizon;
                var w = relativeVelocity - invTimeHorizon * relativePosition;
                var wLengthSq = math.lengthsq(w);
                var dotProduct1 = math.dot(w, relativePosition);

                if (dotProduct1 < 0f && dotProduct1 * dotProduct1 > combinedRadiusSq * wLengthSq)
                {
                    // 投影到截断圆
                    var wLength = math.sqrt(wLengthSq);
                    var unitW = w / wLength;
                    line.Direction = new float2(unitW.y, -unitW.x);
                    u = (combinedRadius * invTimeHorizon - wLength) * unitW;
                }
                else
                {
                    // 投影到两条腿
                    var leg = math.sqrt(math.max(0f, distSq - combinedRadiusSq));
                    if (Det(relativePosition, w) > 0f)
                    {
                        // 左腿
                        line.Direction = new float2(relativePosition.x * leg - relativePosition.y * combinedRadius,
                                                    relativePosition.x * combinedRadius + relativePosition.y * leg) / distSq;
                    }
                    else
                    {
                        // 右腿
                        line.Direction = -new float2(relativePosition.x * leg + relativePosition.y * combinedRadius,
                                                     -relativePosition.x * combinedRadius + relativePosition.y * leg) / distSq;
                    }

                    var dotProduct2 = math.dot(relativeVelocity, line.Direction);
                    u = dotProduct2 * line.Direction - relativeVelocity;
                }
            }
            else
            {
                // 已经重叠：用 1/timeStep 把两者推开
                var invTimeStep = 1f / timeStep;
                var w = relativeVelocity - invTimeStep * relativePosition;
                var wLength = math.length(w);

                float2 unitW;
                if (wLength > Epsilon)
                {
                    unitW = w / wLength;
                }
                else
                {
                    // 退化：相对速度正好抵消且已重叠 —— 挑一个确定的方向（沿中心连线把两者推开）兜底。
                    unitW = distSq > Epsilon ? math.normalize(-relativePosition) : new float2(1f, 0f);
                }

                line.Direction = new float2(unitW.y, -unitW.x);
                u = (combinedRadius * invTimeStep - wLength) * unitW;
            }

            // 各担一半责任
            line.Point = self.Velocity + 0.5f * u;
        }

        // ───────────────────────── 静态障碍物半平面 ─────────────────────────

        /// <summary>
        /// 候选顶点数上限：<c>FixedList512Bytes&lt;ObstacleNeighbor&gt;</c> 按 8 字节一条算能放 63 条，留出余量取 60。
        /// 超出时丢掉最远的那些；半平面条数另由 <see cref="MaxLines"/> 兜住。
        /// </summary>
        private const int MaxObstacleCandidates = 60;

        /// <summary>
        /// 为 <paramref name="self"/> 与静态障碍物的每个顶点构造 ORCA 半平面，追加进 <paramref name="lines"/>，
        /// 返回加入的条数（RVO2 的 <c>numObstLines</c>）。
        ///
        /// 逐段对齐 RVO2 <c>Agent::computeNewVelocity</c> 的障碍物段：
        /// 候选顶点按到线段 <c>[Point, Next.Point]</c> 的距离<b>升序</b>处理（最近的先算，不会因为上限丢掉最近的），
        /// 已加入的障碍物线做覆盖判定，凸顶点的腿按 <c>Prev</c>/<c>Next</c> 的邻边方向修正，
        /// 最后取「截断线 / 左腿 / 右腿」里离当前速度最近的那条。
        ///
        /// <b>必须在邻居半平面之前调用</b>：内部用「已加入的线」做覆盖判定，
        /// <see cref="LinearProgram3"/> 也依赖障碍物线排在可行域列表最前面。
        /// 障碍物线与邻居线共用 <see cref="MaxLines"/> 容量，障碍物先占位，邻居可能因此被截断。
        /// </summary>
        /// <param name="vertices">扁平顶点数组（拓扑在 <c>Prev</c>/<c>Next</c> 里）；未创建时什么都不做。</param>
        /// <param name="vertexCount">参与求解的顶点数；负值 / 超长自动收敛到 <c>[0, vertices.Length]</c>。</param>
        /// <param name="timeHorizonObst">时间视野 τ_obst（<c>&lt;= 0</c> 时不产生约束）。</param>
        public static int BuildObstacleLines(in AgentState self, NativeArray<RvoObstacleVertex> vertices,
                                             int vertexCount, float timeHorizonObst,
                                             ref FixedList512Bytes<OrcaLine> lines)
        {
            if (!vertices.IsCreated || !(timeHorizonObst > 0f) || lines.Length >= MaxLines)
            {
                return 0;
            }

            var count = math.clamp(vertexCount, 0, vertices.Length);
            var invTimeHorizonObst = 1f / timeHorizonObst;
            var radius = math.max(0f, FiniteOr(self.Radius, 0f));
            var maxSpeed = math.max(0f, FiniteOr(self.MaxSpeed, 0f));
            var range = timeHorizonObst * maxSpeed + radius;      // 候选范围（RVO2 的 rangeSq 口径）
            var rangeSq = range * range;

            // 候选顶点：按到线段 [Point, Next.Point] 的距离升序（RVO2 的 obstacleNeighbors_ 排序）
            var candidates = new FixedList512Bytes<ObstacleNeighbor>();
            for (var i = 0; i < count; i++)
            {
                var vertex = vertices[i];
                if (!math.all(math.isfinite(vertex.Point)))
                {
                    continue;
                }

                var next = vertex.Next;
                if (next < 0 || next >= count)
                {
                    continue;
                }

                var nextPoint = vertices[next].Point;
                if (!math.all(math.isfinite(nextPoint)))
                {
                    continue;
                }

                var distSq = DistSqPointLineSegment(vertex.Point, nextPoint, self.Position);
                if (!(distSq < rangeSq))
                {
                    continue;                   // 范围外（位置是 NaN 时也落在这里）
                }

                InsertCandidate(ref candidates, distSq, i);
            }

            var startLength = lines.Length;
            for (var i = 0; i < candidates.Length && lines.Length < MaxLines; i++)
            {
                var obstacle1 = candidates[i].Index;
                var obstacle2 = vertices[obstacle1].Next;
                if (TryBuildObstacleLine(self, vertices, obstacle1, obstacle2, invTimeHorizonObst, radius,
                                         lines, out var line))
                {
                    lines.Add(line);
                }
            }

            return lines.Length - startLength;
        }

        /// <summary>
        /// 按距离升序插入一条候选；超过 <see cref="MaxObstacleCandidates"/> 时挤掉最远的那条。
        /// </summary>
        private static void InsertCandidate(ref FixedList512Bytes<ObstacleNeighbor> candidates,
                                            float distSq, int index)
        {
            var length = candidates.Length;
            if (length >= MaxObstacleCandidates)
            {
                if (distSq >= candidates[MaxObstacleCandidates - 1].DistSq)
                {
                    return;
                }
                length = MaxObstacleCandidates - 1;
            }

            var position = length;
            while (position > 0 && candidates[position - 1].DistSq > distSq)
            {
                position--;
            }

            if (candidates.Length < MaxObstacleCandidates)
            {
                candidates.Add(default);
            }
            for (var i = candidates.Length - 1; i > position; i--)
            {
                candidates[i] = candidates[i - 1];
            }
            candidates[position] = new ObstacleNeighbor { DistSq = distSq, Index = index };
        }

        /// <summary>
        /// 为一个障碍物顶点构造半平面：线段是 <c>[obstacle1.Point, obstacle2.Point]</c>（<c>obstacle2 = obstacle1.Next</c>）。
        /// 返回 false 表示这个顶点不产生约束（零长边 / 已被已加入的线覆盖 / 非凸顶点被忽略 /
        /// foreign 腿不加约束 / 方向退化）。
        ///
        /// 逐段对齐 RVO2 <c>Agent::computeNewVelocity</c>：覆盖判定 → 三个碰撞分支 → 腿构造 →
        /// 邻边修正（<c>leftNeighbor</c> / <c>isLeftLegForeign</c> / <c>isRightLegForeign</c>）→
        /// 截断圆分支 → 三条候选取最近。
        /// </summary>
        private static bool TryBuildObstacleLine(in AgentState self, NativeArray<RvoObstacleVertex> vertices,
                                                int obstacle1Index, int obstacle2Index,
                                                float invTimeHorizonObst, float radius,
                                                in FixedList512Bytes<OrcaLine> lines, out OrcaLine line)
        {
            line = default;
            var obstacle1 = vertices[obstacle1Index];
            var obstacle2 = vertices[obstacle2Index];

            var relativePosition1 = obstacle1.Point - self.Position;
            var relativePosition2 = obstacle2.Point - self.Position;

            // 覆盖判定：照 RVO2 的 alreadyCovered 段
            for (var j = 0; j < lines.Length; j++)
            {
                var existing = lines[j];
                if (Det(invTimeHorizonObst * relativePosition1 - existing.Point, existing.Direction) -
                    invTimeHorizonObst * radius >= -Epsilon &&
                    Det(invTimeHorizonObst * relativePosition2 - existing.Point, existing.Direction) -
                    invTimeHorizonObst * radius >= -Epsilon)
                {
                    return false;
                }
            }

            var distSq1 = math.lengthsq(relativePosition1);
            var distSq2 = math.lengthsq(relativePosition2);
            var radiusSq = radius * radius;
            var obstacleVector = obstacle2.Point - obstacle1.Point;
            var obstacleLengthSq = math.lengthsq(obstacleVector);
            if (!(obstacleLengthSq > Epsilon))
            {
                return false;                     // 零长边不产生约束
            }

            var s = -math.dot(relativePosition1, obstacleVector) / obstacleLengthSq;
            var distSqLine = math.lengthsq(-relativePosition1 - s * obstacleVector);

            // 三个碰撞分支：左顶点 / 右顶点 / 线段本体
            if (s < 0f && distSq1 <= radiusSq)
            {
                if (obstacle1.IsConvex == 0)
                {
                    return false;
                }
                line.Point = float2.zero;
                line.Direction = NormalizeOr(new float2(-relativePosition1.y, relativePosition1.x), float2.zero);
                return IsUsable(line);
            }

            if (s > 1f && distSq2 <= radiusSq)
            {
                if (obstacle2.IsConvex == 0 || Det(relativePosition2, obstacle2.UnitDir) < 0f)
                {
                    return false;
                }
                line.Point = float2.zero;
                line.Direction = NormalizeOr(new float2(-relativePosition2.y, relativePosition2.x), float2.zero);
                return IsUsable(line);
            }

            if (s >= 0f && s < 1f && distSqLine <= radiusSq)
            {
                line.Point = float2.zero;
                line.Direction = -obstacle1.UnitDir;
                return IsUsable(line);
            }

            // 无碰撞：算两条腿
            float2 leftLegDirection;
            float2 rightLegDirection;
            if (s < 0f && distSqLine <= radiusSq)
            {
                // 斜看：只有左顶点定义速度障碍
                if (obstacle1.IsConvex == 0)
                {
                    return false;
                }
                obstacle2Index = obstacle1Index;
                obstacle2 = obstacle1;
                var leg1 = math.sqrt(math.max(0f, distSq1 - radiusSq));
                leftLegDirection = new float2(relativePosition1.x * leg1 - relativePosition1.y * radius,
                                              relativePosition1.x * radius + relativePosition1.y * leg1) / distSq1;
                rightLegDirection = new float2(relativePosition1.x * leg1 + relativePosition1.y * radius,
                                               -relativePosition1.x * radius + relativePosition1.y * leg1) / distSq1;
            }
            else if (s > 1f && distSqLine <= radiusSq)
            {
                // 斜看：只有右顶点定义速度障碍
                if (obstacle2.IsConvex == 0)
                {
                    return false;
                }
                obstacle1Index = obstacle2Index;
                obstacle1 = obstacle2;
                var leg2 = math.sqrt(math.max(0f, distSq2 - radiusSq));
                leftLegDirection = new float2(relativePosition2.x * leg2 - relativePosition2.y * radius,
                                              relativePosition2.x * radius + relativePosition2.y * leg2) / distSq2;
                rightLegDirection = new float2(relativePosition2.x * leg2 + relativePosition2.y * radius,
                                               -relativePosition2.x * radius + relativePosition2.y * leg2) / distSq2;
            }
            else
            {
                // 常规：两个顶点各出一条腿；非凸顶点用截断线的延长方向
                if (obstacle1.IsConvex != 0)
                {
                    var leg1 = math.sqrt(math.max(0f, distSq1 - radiusSq));
                    leftLegDirection = new float2(relativePosition1.x * leg1 - relativePosition1.y * radius,
                                                  relativePosition1.x * radius + relativePosition1.y * leg1) / distSq1;
                }
                else
                {
                    leftLegDirection = -obstacle1.UnitDir;
                }

                if (obstacle2.IsConvex != 0)
                {
                    var leg2 = math.sqrt(math.max(0f, distSq2 - radiusSq));
                    rightLegDirection = new float2(relativePosition2.x * leg2 + relativePosition2.y * radius,
                                                   -relativePosition2.x * radius + relativePosition2.y * leg2) / distSq2;
                }
                else
                {
                    rightLegDirection = obstacle1.UnitDir;
                }
            }

            // 腿不许指进相邻边：指进去了就换成邻边的截断线方向（照 RVO2 的 leftNeighbor / foreign 段）
            var leftNeighbor = vertices[obstacle1.Prev];
            var isLeftLegForeign = false;
            var isRightLegForeign = false;
            if (obstacle1.IsConvex != 0 && Det(leftLegDirection, -leftNeighbor.UnitDir) >= 0f)
            {
                leftLegDirection = -leftNeighbor.UnitDir;
                isLeftLegForeign = true;
            }
            if (obstacle2.IsConvex != 0 && Det(rightLegDirection, obstacle2.UnitDir) <= 0f)
            {
                rightLegDirection = obstacle2.UnitDir;
                isRightLegForeign = true;
            }

            // 截断圆心（cutoff centers）
            var leftCutoff = invTimeHorizonObst * (obstacle1.Point - self.Position);
            var rightCutoff = invTimeHorizonObst * (obstacle2.Point - self.Position);
            var cutoffVec = rightCutoff - leftCutoff;
            var sameVertex = obstacle1Index == obstacle2Index;

            var t = sameVertex
                ? 0.5f
                : math.dot(self.Velocity - leftCutoff, cutoffVec) / math.lengthsq(cutoffVec);
            var tLeft = math.dot(self.Velocity - leftCutoff, leftLegDirection);
            var tRight = math.dot(self.Velocity - rightCutoff, rightLegDirection);

            if ((t < 0f && tLeft < 0f) || (sameVertex && tLeft < 0f && tRight < 0f))
            {
                // 当前速度投影在左侧截断圆
                var unitW = NormalizeOr(self.Velocity - leftCutoff, float2.zero);
                line.Direction = new float2(unitW.y, -unitW.x);
                line.Point = leftCutoff + radius * invTimeHorizonObst * unitW;
                return IsUsable(line);
            }

            if (t > 1f && tRight < 0f)
            {
                // 当前速度投影在右侧截断圆
                var unitW = NormalizeOr(self.Velocity - rightCutoff, float2.zero);
                line.Direction = new float2(unitW.y, -unitW.x);
                line.Point = rightCutoff + radius * invTimeHorizonObst * unitW;
                return IsUsable(line);
            }

            // 三条候选里取离当前速度最近的：截断线 / 左腿 / 右腿
            var distSqCutoff = (t < 0f || t > 1f || sameVertex)
                ? float.PositiveInfinity
                : math.lengthsq(self.Velocity - (leftCutoff + t * cutoffVec));
            var distSqLeft = tLeft < 0f
                ? float.PositiveInfinity
                : math.lengthsq(self.Velocity - (leftCutoff + tLeft * leftLegDirection));
            var distSqRight = tRight < 0f
                ? float.PositiveInfinity
                : math.lengthsq(self.Velocity - (rightCutoff + tRight * rightLegDirection));

            if (distSqCutoff <= distSqLeft && distSqCutoff <= distSqRight)
            {
                line.Direction = -obstacle1.UnitDir;
                line.Point = leftCutoff + radius * invTimeHorizonObst *
                             new float2(-line.Direction.y, line.Direction.x);
                return IsUsable(line);
            }

            if (distSqLeft <= distSqRight)
            {
                if (isLeftLegForeign)
                {
                    return false;                 // foreign 腿不加约束
                }
                line.Direction = leftLegDirection;
                line.Point = leftCutoff + radius * invTimeHorizonObst *
                             new float2(-line.Direction.y, line.Direction.x);
                return IsUsable(line);
            }

            if (isRightLegForeign)
            {
                return false;                     // foreign 腿不加约束
            }
            line.Direction = -rightLegDirection;
            line.Point = rightCutoff + radius * invTimeHorizonObst *
                         new float2(-line.Direction.y, line.Direction.x);
            return IsUsable(line);
        }

        /// <summary>候选顶点：到线段 <c>[Point, Next.Point]</c> 的距离平方与它在顶点数组里的下标。</summary>
        private struct ObstacleNeighbor
        {
            public float DistSq;
            public int Index;
        }

        /// <summary>
        /// 点到线段的最短距离平方（RVO2 的 <c>distSqPointLineSegment</c>）：零长线段退化成点到点。
        /// </summary>
        public static float DistSqPointLineSegment(float2 a, float2 b, float2 p)
        {
            var ab = b - a;
            var lengthSq = math.lengthsq(ab);
            if (lengthSq <= 0f)
            {
                return math.lengthsq(p - a);
            }

            var t = math.clamp(math.dot(p - a, ab) / lengthSq, 0f, 1f);
            return math.lengthsq(p - (a + t * ab));
        }

        /// <summary>单位化；长度过小时返回 <paramref name="fallback"/>（不产出 NaN）。</summary>
        private static float2 NormalizeOr(float2 v, float2 fallback)
        {
            var lengthSq = math.lengthsq(v);
            return lengthSq > Epsilon ? v * math.rsqrt(lengthSq) : fallback;
        }

        /// <summary>非有限的标量换成 <paramref name="fallback"/>。</summary>
        private static float FiniteOr(float value, float fallback)
            => math.isfinite(value) ? value : fallback;

        /// <summary>半平面是否可用：点和方向都是有限值，且方向是有意义的单位向量。</summary>
        private static bool IsUsable(in OrcaLine line)
            => math.all(math.isfinite(line.Point)) && math.all(math.isfinite(line.Direction)) &&
               math.lengthsq(line.Direction) > Epsilon;

        // ───────────────────────── 线性规划 ─────────────────────────

        /// <summary>
        /// 顺序贪心：逐个满足半平面；某条不满足就用 <see cref="LinearProgram1"/> 重算，
        /// 重算失败则回滚并返回该线号（调用方据此转 <see cref="LinearProgram3"/>）。
        /// 返回 <c>lines.Length</c> 表示全部满足。
        /// </summary>
        public static int LinearProgram2(in FixedList512Bytes<OrcaLine> lines, float radius, float2 optVelocity,
                                         bool directionOpt, ref float2 result)
        {
            if (directionOpt)
                result = optVelocity * radius;
            else if (math.lengthsq(optVelocity) > radius * radius)
                result = math.normalize(optVelocity) * radius;
            else
                result = optVelocity;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (Det(line.Direction, line.Point - result) > 0f)
                {
                    var tempResult = result;
                    if (!LinearProgram1(lines, i, radius, optVelocity, directionOpt, ref result))
                    {
                        result = tempResult;
                        return i;
                    }
                }
            }

            return lines.Length;
        }

        /// <summary>
        /// 在「最大速度圆」上、满足第 <paramref name="lineNo"/> 条线以及它之前所有线的交集里取最优。
        /// 返回 false 表示无解（该线把可行域切没了）。
        /// </summary>
        public static bool LinearProgram1(in FixedList512Bytes<OrcaLine> lines, int lineNo, float radius,
                                          float2 optVelocity, bool directionOpt, ref float2 result)
        {
            var line = lines[lineNo];
            var dotProduct = math.dot(line.Point, line.Direction);
            var discriminant = dotProduct * dotProduct + radius * radius - math.lengthsq(line.Point);
            if (discriminant < 0f)
            {
                return false; // 最大速度圆完全否定这条线
            }

            var sqrtDiscriminant = math.sqrt(discriminant);
            var tLeft = -dotProduct - sqrtDiscriminant;
            var tRight = -dotProduct + sqrtDiscriminant;

            for (var i = 0; i < lineNo; i++)
            {
                var other = lines[i];
                var denominator = Det(line.Direction, other.Direction);
                var numerator = Det(other.Direction, line.Point - other.Point);

                if (math.abs(denominator) <= Epsilon)
                {
                    // 两条线（几乎）平行
                    if (numerator < 0f)
                    {
                        return false;
                    }
                    continue;
                }

                var t = numerator / denominator;
                if (denominator >= 0f)
                {
                    tRight = math.min(tRight, t); // other 在右侧收窄
                }
                else tLeft = math.max(tLeft, t);                        // other 在左侧收窄

                if (tLeft > tRight)
                {
                    return false;
                }
            }

            if (directionOpt)
            {
                result = line.Point + (math.dot(optVelocity, line.Direction) > 0f ? tRight : tLeft) * line.Direction;
            }
            else
            {
                var t = math.dot(line.Direction, optVelocity - line.Point);
                t = math.clamp(t, tLeft, tRight);
                result = line.Point + t * line.Direction;
            }

            return true;
        }

        /// <summary>
        /// 兜底：<paramref name="beginLine"/> 之前的约束无法同时满足时，用「两两交集」构造新的投影线集，
        /// 转成「沿该线方向最优」的子问题再解一次。
        /// </summary>
        public static void LinearProgram3(in FixedList512Bytes<OrcaLine> lines, int beginLine, float radius,
                                          ref float2 result)
            => LinearProgram3(lines, 0, beginLine, radius, ref result);

        /// <summary>
        /// 带静态障碍物种子集的版本：前 <paramref name="numObstLines"/> 条线始终留在投影子问题里
        /// （障碍物约束不参与两两替换）。
        /// </summary>
        public static void LinearProgram3(in FixedList512Bytes<OrcaLine> lines, int numObstLines, int beginLine,
                                          float radius, ref float2 result)
        {
            var distance = 0f;
            numObstLines = math.clamp(numObstLines, 0, lines.Length);
            beginLine = math.clamp(beginLine, 0, lines.Length);

            for (var i = beginLine; i < lines.Length; i++)
            {
                var line = lines[i];
                if (Det(line.Direction, line.Point - result) <= distance)
                {
                    continue;
                }

                // 种子集 = 障碍物线，再加上 i 之前的线与第 i 条两两组合
                var projLines = new FixedList512Bytes<OrcaLine>();
                for (var k = 0; k < numObstLines; k++)
                {
                    projLines.Add(lines[k]);
                }

                for (var j = numObstLines; j < i; j++)
                {
                    var other = lines[j];
                    var determinant = Det(line.Direction, other.Direction);

                    OrcaLine proj;
                    if (math.abs(determinant) <= Epsilon)
                    {
                        if (math.dot(line.Direction, other.Direction) > 0f)
                        {
                            continue; // 同向：跳过
                        }
                        proj.Point = 0.5f * (line.Point + other.Point);                  // 反向：取中点
                    }
                    else
                    {
                        proj.Point = line.Point +
                                     (Det(other.Direction, line.Point - other.Point) / determinant) * line.Direction;
                    }

                    proj.Direction = math.normalize(other.Direction - line.Direction);
                    projLines.Add(proj);
                }

                var tempResult = result;
                var optVelocity = new float2(-line.Direction.y, line.Direction.x);
                if (LinearProgram2(projLines, radius, optVelocity, true, ref result) < projLines.Length)
                    result = tempResult;

                distance = Det(line.Direction, line.Point - result);
            }
        }

        // ───────────────────────── 小工具 ─────────────────────────

        /// <summary>
        /// 二维叉积（RVO2 的 <c>det</c>）。
        /// </summary>
        public static float Det(float2 a, float2 b) => a.x * b.y - a.y * b.x;

        /// <summary>
        /// 把向量长度限制在 <paramref name="maxLength"/> 内；零向量原样返回（不产出 NaN）。
        /// </summary>
        public static float2 ClampLength(float2 v, float maxLength)
        {
            var lengthSq = math.lengthsq(v);
            if (lengthSq <= 0f || maxLength <= 0f)
            {
                return float2.zero;
            }
            if (lengthSq <= maxLength * maxLength)
            {
                return v;
            }
            return v * (maxLength / math.sqrt(lengthSq));
        }
    }
}
