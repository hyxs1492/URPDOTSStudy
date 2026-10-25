using DOTSUtils.Pathfinding;
using DOTSUtils.Rvo;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace DOTSUtils.Samples.Pathfinding
{
    /// <summary>
    /// 让每个 agent 沿自己的 A* 路径走：
    /// <list type="number">
    /// <item>读 <c>PathWaypoint</c> 缓冲找到当前要去的路点，朝它算出期望速度；</item>
    /// <item>期望速度写进 <see cref="RvoAgent.PrefVelocity"/>（供**下一帧**的 Rvo 求解使用）；</item>
    /// <item>用 Rvo 本帧解算出的 <see cref="RvoAgent.Velocity"/> 积分 <c>LocalTransform.Position</c>（y 保持固定平面）；</item>
    /// <item>需要新目标时经 <see cref="EntityCommandBuffer"/>（并行写入器）把 <c>PathRequest</c> 换成新目标，
    /// 不在 job 里做结构性变更；鼠标跟随开着时目标取「鼠标落点 + 本 agent 的随机偏移」，
    /// 鼠标目标换版本后每个 agent 按自己的相位在 <c>CursorRepathStride</c> 帧窗口内响应一次；</item>
    /// <item>等待求解结果超时会强制重发请求，避免卡在「请求已发出、结果永不到来」；</item>
    /// <item>每帧把 <see cref="AStarDemoStats"/> 单例写成当前快照（改道数 / 未跟上数 / 目标类型分布）；</item>
    /// <item>主线程上把鼠标目标画成调试标记。</item>
    /// </list>
    /// 只处理带 <see cref="AStarDemoAgentTag"/> 的实体，也就是渲染树的父实体。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AStarDemoGroup))]
    public partial class AStarDemoFollowPathSystem : SystemBase
    {
        /// <summary>起点落在阻挡格时，向四周找可走格的搜索圈数上限。</summary>
        private const int k_StartSearchRings = 8;

        /// <summary>鼠标落点被压在阻挡格上时，向四周找可走格的搜索圈数上限。</summary>
        private const int k_CursorSearchRings = 16;

        /// <summary>挑新目标时最多试几次随机格。</summary>
        private const int k_GoalAttempts = 24;

        /// <summary>前几次尝试要求目标离当前位置足够远，之后放宽，避免小场地里挑不到目标。</summary>
        private const int k_FarAttempts = 8;

        /// <summary>单帧参与位移计算的最长时间（秒）：长帧时不会一步跨过路点。</summary>
        private const float k_MaxStepSeconds = 1f / 30f;

        /// <summary>A* 网格单例（网格 + 参数 + 建表版本）。</summary>
        private EntityQuery m_AStarQuery;

        /// <summary>鼠标目标单例。</summary>
        private EntityQuery m_CursorQuery;

        /// <summary>要画路径的 agent 集合（只含前 <see cref="AStarDemoConfig.DebugPathCount"/> 个）。</summary>
        private EntityQuery m_DebugQuery;

        /// <summary>参与统计的 agent 集合（带标记 + 拿得到运动与寻路状态）。</summary>
        private EntityQuery m_AgentQuery;

        /// <summary>统计单例实体。</summary>
        private Entity m_StatsEntity;

        /// <summary>已跑过的帧数，用来算每个 agent 的错峰槽位。</summary>
        private uint m_FrameIndex;

        /// <summary>本帧实际使用的帧号（统计与 job 用同一个值）。</summary>
        private int m_LastFrameIndex;

        protected override void OnCreate()
        {
            m_AStarQuery = GetEntityQuery(ComponentType.ReadOnly<AStarSingleton>());
            m_CursorQuery = GetEntityQuery(ComponentType.ReadOnly<AStarDemoCursorTarget>());
            m_DebugQuery = GetEntityQuery(
                ComponentType.ReadOnly<AStarDemoDebugPath>(),
                ComponentType.ReadOnly<PathWaypoint>());
            m_AgentQuery = GetEntityQuery(
                ComponentType.ReadOnly<AStarDemoAgentTag>(),
                ComponentType.ReadOnly<AStarDemoMotion>(),
                ComponentType.ReadOnly<PathRequest>(),
                ComponentType.ReadOnly<PathStatus>());

            m_StatsEntity = EntityManager.CreateEntity(typeof(AStarDemoStats));
            EntityManager.SetName(m_StatsEntity, "AStarDemoStats");
        }

        protected override void OnUpdate()
        {
            // 网格还没建好（没装 A* 模块 / 刚装配）：本帧什么都不做。
            if (m_AStarQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            var data = EntityManager.GetComponentData<AStarSingleton>(m_AStarQuery.GetSingletonEntity());
            if (!data.Grid.IsCreated)
            {
                return;
            }

            var config = AStarDemoSettings.Config;

            // 鼠标目标由主线程每帧写；没有这个单例时取默认值（Valid = 0 ⇒ 走原来的随机目标逻辑）。
            var cursor = m_CursorQuery.IsEmptyIgnoreFilter
                ? default
                : EntityManager.GetComponentData<AStarDemoCursorTarget>(m_CursorQuery.GetSingletonEntity());

            // 帧号每帧只取一次；每个 agent 的错峰相位存在它自己的 RepathPhase 上。
            var frameIndex = (int)(m_FrameIndex % int.MaxValue);
            m_FrameIndex++;
            m_LastFrameIndex = frameIndex;

            var stride = math.max(1, config.CursorRepathStride);
            var deltaTime = SystemAPI.Time.DeltaTime;
            // 求解看门狗阈值（帧）：<= 0 表示不看门狗。
            var timeoutFrames = config.PendingTimeoutSeconds > 0f && deltaTime > 0f
                ? math.max(1, (int)math.ceil(config.PendingTimeoutSeconds / deltaTime))
                : 0;

            var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                               .CreateCommandBuffer(World.Unmanaged)
                               .AsParallelWriter();

            new FollowPathJob
            {
                DeltaTime = deltaTime,
                Config = config,
                Grid = data.Grid,
                GridBuildVersion = data.BuildVersion,
                Cursor = cursor,
                FrameIndex = (uint)frameIndex,
                PendingTimeoutFrames = timeoutFrames,
                Ecb = ecb,
            }.ScheduleParallel();

            // 命令缓冲由帧末的 ECB 系统回放：把写入方（上面这个 job）登记给它，回放前会等 job 收尾。
            World.GetExistingSystemManaged<EndSimulationEntityCommandBufferSystem>()
                 ?.AddJobHandleForProducer(Dependency);

            // 鼠标标记只读单例，不依赖上面这个 job，可以直接画。
            if (config.DebugCursorMarker && config.FollowCursor)
            {
                DrawCursorMarker(in cursor, in data.Grid);
            }

            // 画线要读上面这个 job 刚碰过的路点缓冲，先把依赖收掉再读。
            if (config.DebugPathCount > 0)
            {
                CompleteDependency();
                DrawDebugPaths();
            }

            if (config.StatsEnabled)
            {
                UpdateStats(in cursor, stride);
            }
        }

        /// <summary>
        /// 在鼠标目标处画标记：十字、竖线、目标格方框，以及随机偏移半径的圆圈。
        /// 只在编辑器 / 开了 Gizmos 时可见。
        /// </summary>
        private void DrawCursorMarker(in AStarDemoCursorTarget cursor, in AStarGrid grid)
        {
            if (cursor.Valid == 0 && cursor.Version == 0)
            {
                return;
            }

            var config = AStarDemoSettings.Config;
            var height = config.DebugLineHeight;
            var color = new Color(1f, 0.4f, 0.1f);
            var center = new Vector3(cursor.Position.x, height, cursor.Position.z);

            // 十字：标出落点本身。
            const float cross = 2f;
            Debug.DrawLine(center - new Vector3(cross, 0f, 0f), center + new Vector3(cross, 0f, 0f), color);
            Debug.DrawLine(center - new Vector3(0f, 0f, cross), center + new Vector3(0f, 0f, cross), color);

            // 从地面竖直拉一根线，俯视时也看得见落点在哪。
            Debug.DrawLine(new Vector3(center.x, 0f, center.z),
                           new Vector3(center.x, height * 3f, center.z), color);

            // 目标所在格子的方框。
            if (grid.IsCreated)
            {
                var cell = grid.WorldToCell(cursor.Position);
                if (grid.InBounds(cell))
                {
                    var cellCenter = grid.CellCenter(cell);
                    var half = grid.CellSize * 0.5f;
                    var a = new Vector3(cellCenter.x - half, height, cellCenter.z - half);
                    var b = new Vector3(cellCenter.x + half, height, cellCenter.z - half);
                    var c = new Vector3(cellCenter.x + half, height, cellCenter.z + half);
                    var d = new Vector3(cellCenter.x - half, height, cellCenter.z + half);
                    Debug.DrawLine(a, b, color);
                    Debug.DrawLine(b, c, color);
                    Debug.DrawLine(c, d, color);
                    Debug.DrawLine(d, a, color);
                }
            }

            // 随机偏移的半径圈：agent 的目标就落在这个圆盘内。
            var spread = config.CursorSpreadRadius;
            if (spread <= 1e-3f)
            {
                return;
            }

            const int segments = 24;
            var previous = center + new Vector3(spread, 0f, 0f);
            for (var i = 1; i <= segments; i++)
            {
                var angle = i * (2f * Mathf.PI / segments);
                var next = center + new Vector3(Mathf.Cos(angle) * spread, 0f, Mathf.Sin(angle) * spread);
                Debug.DrawLine(previous, next, color);
                previous = next;
            }
        }

        /// <summary>
        /// 用 <see cref="Debug.DrawLine"/> 画出前几个 agent 的当前路径（只在编辑器 / 开了 Gizmos 时可见）。
        /// 线只给 <see cref="AStarDemoConfig.DebugPathCount"/> 个 agent 画：一万个 agent 各画一条线是不可行的。
        /// </summary>
        private void DrawDebugPaths()
        {
            if (m_DebugQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            var config = AStarDemoSettings.Config;
            var height = config.DebugLineHeight;
            var entities = m_DebugQuery.ToEntityArray(Allocator.Temp);
            var markers = m_DebugQuery.ToComponentDataArray<AStarDemoDebugPath>(Allocator.Temp);

            for (var i = 0; i < entities.Length; i++)
            {
                var order = markers[i].Order;
                if (order < 0 || order >= config.DebugPathCount)
                {
                    continue;
                }

                var waypoints = EntityManager.GetBuffer<PathWaypoint>(entities[i]);
                if (waypoints.Length == 0)
                {
                    continue;
                }

                var color = Color.HSVToRGB(config.DebugPathCount <= 1 ? 0f : order / (float)config.DebugPathCount,
                                           0.85f, 1f);
                for (var p = 0; p + 1 < waypoints.Length; p++)
                {
                    var a = waypoints[p].Position;
                    var b = waypoints[p + 1].Position;
                    Debug.DrawLine(new Vector3(a.x, height, a.z), new Vector3(b.x, height, b.z), color);
                }

                // 终点立一根短竖线，方便看它正朝哪个格子去。
                var end = waypoints[waypoints.Length - 1].Position;
                Debug.DrawLine(new Vector3(end.x, 0f, end.z), new Vector3(end.x, height * 2f, end.z), color);
            }

            entities.Dispose();
            markers.Dispose();
        }

        /// <summary>
        /// 汇总 <see cref="AStarDemoStats"/> 单例：把上面那个 job 写下的运动 / 寻路状态读回主线程分类计数，
        /// 得到「本帧改道了多少船、还有多少船没跟上新目标、目标挑到了哪一类」。
        /// </summary>
        private void UpdateStats(in AStarDemoCursorTarget cursor, int stride)
        {
            if (m_StatsEntity == Entity.Null || !EntityManager.Exists(m_StatsEntity))
            {
                m_StatsEntity = EntityManager.CreateEntity(typeof(AStarDemoStats));
                EntityManager.SetName(m_StatsEntity, "AStarDemoStats");
            }

            // 上面那个 job 写的就是这些组件：先收依赖，再用托管方式读回来。
            CompleteDependency();

            var motions = m_AgentQuery.ToComponentDataArray<AStarDemoMotion>(Allocator.Temp);
            var statuses = m_AgentQuery.ToComponentDataArray<PathStatus>(Allocator.Temp);
            var requests = m_AgentQuery.ToComponentDataArray<PathRequest>(Allocator.Temp);

            var config = AStarDemoSettings.Config;
            var cursorActive = config.FollowCursor && (cursor.Version != 0 || cursor.Valid != 0);
            var stats = new AStarDemoStats
            {
                AgentCount = motions.Length,
                RepathWindow = stride,
                FrameIndex = m_LastFrameIndex,
                CursorVersion = cursor.Version,
                CursorValid = cursor.Valid,
                CursorWriter = cursor.Writer,
                CursorWriteCount = cursor.WriteCount,
            };

            for (var i = 0; i < motions.Length; i++)
            {
                var motion = motions[i];
                switch (motion.GoalKind)
                {
                    case AStarDemoGoalKind.CursorSpread:
                    case AStarDemoGoalKind.CursorCell:
                    case AStarDemoGoalKind.NearCursor:
                        stats.CursorGoalAgents++;
                        break;
                    case AStarDemoGoalKind.Random:
                        stats.RandomGoalAgents++;
                        break;
                    case AStarDemoGoalKind.Failed:
                        stats.NoGoalAgents++;
                        break;
                }

                if (motion.LastRepathFrame == m_LastFrameIndex)
                {
                    stats.RepathedLastFrame++;
                }
                if (cursorActive && motion.PlannedCursorVersion != cursor.Version)
                {
                    stats.StaleAgents++;
                }
                if (motion.PendingFrames > stats.MaxPendingFrames)
                {
                    stats.MaxPendingFrames = motion.PendingFrames;
                }
                stats.PendingTimeouts += motion.PendingTimeouts;

                var status = statuses[i];
                if (status.SolvedVersion != requests[i].Version)
                {
                    stats.PendingAgents++;
                }
                // Found 是 0，默认值也报 Found：必须配 SolvedVersion 才说明真的解过。
                if (status.SolvedVersion > 0 && status.Status == AStarStatus.Found && status.WaypointCount > 0)
                {
                    stats.PathAgents++;
                }
            }

            motions.Dispose();
            statuses.Dispose();
            requests.Dispose();

            EntityManager.SetComponentData(m_StatsEntity, stats);
        }

        /// <summary>逐 agent 沿路径推进：写期望速度、积分位置、到达后请求下一个目标。</summary>
        [BurstCompile]
        [WithAll(typeof(AStarDemoAgentTag))]
        private partial struct FollowPathJob : IJobEntity
        {
            public float DeltaTime;
            public AStarDemoConfig Config;

            /// <summary>网格只读视图（内核与单例共享同一块内存）。</summary>
            public AStarGrid Grid;

            /// <summary>当前网格的建表版本。</summary>
            public int GridBuildVersion;

            /// <summary>鼠标目标单例（主线程写、job 只读）；版本与可用标记都为 0 表示还没收到过落点。</summary>
            public AStarDemoCursorTarget Cursor;

            /// <summary>本帧的帧号；agent 用它和自己的相位算「这一帧轮不轮到我」。</summary>
            public uint FrameIndex;

            /// <summary>等待求解超过多少帧就强制重发请求；&lt;= 0 表示不看门狗。</summary>
            public int PendingTimeoutFrames;

            public EntityCommandBuffer.ParallelWriter Ecb;

            private void Execute(Entity entity, [ChunkIndexInQuery] int sortKey,
                                 ref LocalTransform transform, ref AStarDemoMotion motion, ref RvoAgent agent,
                                 ref PathRequest request, in PathStatus status,
                                 DynamicBuffer<PathWaypoint> waypoints)
            {
                var dt = math.min(DeltaTime, k_MaxStepSeconds);
                var position = transform.Position;
                var arriveSq = Config.ArriveRadius * Config.ArriveRadius;

                // 冷却按真实帧长走（它是墙钟时间），位移用上面截断过的 dt。
                motion.Cooldown -= DeltaTime;

                // 网格被重建（掩码 / 参数变了）→ 旧路径作废：丢掉路点，安排一次错峰重规划。
                if (motion.GridBuildVersion != GridBuildVersion)
                {
                    motion.GridBuildVersion = GridBuildVersion;
                    motion.Cooldown = math.max(motion.Cooldown, motion.Rng.NextFloat(0f, Config.RetryDelay));
                    motion.PlannedCursorVersion = 0;
                    waypoints.Clear();
                }

                var pending = status.SolvedVersion != request.Version;

                // 求解迟迟不回来（模块没装配 / 网格没建 / 请求没被消费）时强制重发，避免永久停在等待里。
                var timedOut = false;
                if (pending)
                {
                    motion.PendingFrames++;
                    if (PendingTimeoutFrames > 0 && motion.PendingFrames >= PendingTimeoutFrames)
                    {
                        timedOut = true;
                        motion.PendingFrames = 0;
                        motion.PendingTimeouts++;
                    }
                }
                else
                {
                    motion.PendingFrames = 0;
                }
                if (timedOut)
                {
                    pending = false;
                }

                var hasPath = status.Status == AStarStatus.Found && waypoints.Length > 0;

                // 当前要去的路点：换到新路径就把游标重置为 1（跳过 waypoints[0] —— 那是本船申请路径时所在的位置），
                // 之后游标只前进不后退，避免在「掉头回自己的起点」与「去目标」之间来回摆动。
                var arrived = false;
                if (hasPath)
                {
                    if (motion.PathCursorVersion != status.SolvedVersion)
                    {
                        motion.PathCursorVersion = status.SolvedVersion;
                        motion.WaypointIndex = waypoints.Length > 1 ? 1 : 0;
                    }
                    if (motion.WaypointIndex < 0 || motion.WaypointIndex >= waypoints.Length)
                    {
                        motion.WaypointIndex = waypoints.Length - 1;
                    }

                    while (motion.WaypointIndex < waypoints.Length - 1 &&
                           DistanceSqXZ(waypoints[motion.WaypointIndex].Position, position) <= arriveSq)
                    {
                        motion.WaypointIndex++;
                    }

                    arrived = motion.WaypointIndex >= waypoints.Length - 1 &&
                              DistanceSqXZ(waypoints[motion.WaypointIndex].Position, position) <= arriveSq;
                }
                else
                {
                    motion.WaypointIndex = 0;
                }

                // 鼠标跟随：单例可用、且本 agent 还没规划过当前版本 ⇒ 它需要为鼠标改一次道。
                var cursorActive = Config.FollowCursor && (Cursor.Version != 0 || Cursor.Valid != 0);
                var cursorStale = cursorActive && motion.PlannedCursorVersion != Cursor.Version;
                // 只有「鼠标换了版本」需要等自己的错峰相位；到达 / 无路可跟本身是摊开的，不必再等。
                var due = !cursorStale || IsRepathDue(FrameIndex, motion.RepathPhase, Config.CursorRepathStride);

                // 需要新目标：没有可跟的路径 / 已经到达 / 鼠标换了版本 / 求解超时被强制重发。
                var needsGoal = !hasPath || arrived || cursorStale || timedOut;
                // 到达后的停顿只约束「随机挑下一个目标」那种节奏；鼠标换了版本时不受它限制。
                var cooldownReady = motion.Cooldown <= 0f || cursorStale;

                if (needsGoal && !pending && cooldownReady && due)
                {
                    RequestGoal(entity, sortKey, position, request, cursorActive, ref motion);

                    // 站在目标点上再挑新目标：给一点停顿，别让同一艘船每帧都换目标。
                    if (arrived && !cursorStale)
                    {
                        motion.Cooldown = Config.RetryDelay;
                    }
                }

                if (!hasPath)
                {
                    // 没有可跟的路径：原地等（期望速度归零），位置仍按 Rvo 解算出的速度推进。
                    Stand(ref agent);
                    Integrate(ref transform, in agent, dt, Config.PlaneHeight, in Grid);

                    // 求解回来却没有可用路径（终点被占 / 无通路 / 超限）：等一段再试，别每帧重复求解。
                    if (!pending && motion.Cooldown <= 0f)
                    {
                        motion.Cooldown = Config.RetryDelay;
                    }
                    return;
                }

                // 沿路径走：期望速度指向当前路点，朝向平滑转过去。
                var target = waypoints[motion.WaypointIndex].Position;
                var to = new float2(target.x - position.x, target.z - position.z);
                var distance = math.length(to);

                agent.MaxSpeed = motion.Speed;
                agent.PrefVelocity = distance > 1e-3f ? to * (motion.Speed / distance) : float2.zero;

                if (distance > 1e-3f)
                {
                    var heading = quaternion.LookRotationSafe(new float3(to.x, 0f, to.y), math.up());
                    transform.Rotation = RotateTowards(transform.Rotation, heading,
                                                       math.radians(Config.TurnSpeedDegPerSec) * dt);
                }

                Integrate(ref transform, in agent, dt, Config.PlaneHeight, in Grid);
            }

            /// <summary>原地不动：期望速度归零，位置仍按 Rvo 解算出的速度推进（减速到停）。</summary>
            private static void Stand(ref RvoAgent agent) => agent.PrefVelocity = float2.zero;

            /// <summary>
            /// 用 Rvo 本帧解算出的速度积分位置（y 固定在平面高度），并把结果**约束在可走区域内**：
            /// 直接落进阻挡格时先做分轴滑动（只走能走的那个轴），两个轴都被挡就退回上一位置；
            /// 连上一位置都不可走（历史上被挤进去过）则吸附到最近可走格中心。
            /// </summary>
            private static void Integrate(ref LocalTransform transform, in RvoAgent agent, float dt, float planeHeight,
                                          in AStarGrid grid)
            {
                var velocity = agent.Velocity;
                var current = transform.Position;
                var next = current + new float3(velocity.x, 0f, velocity.y) * dt;
                next = new float3(next.x, planeHeight, next.z);

                if (!grid.IsCreated || grid.IsWalkableWorld(next))
                {
                    transform.Position = next;
                    return;
                }

                var slideZ = new float3(current.x, planeHeight, next.z);
                if (grid.IsWalkableWorld(slideZ))
                {
                    transform.Position = slideZ;
                    return;
                }
                var slideX = new float3(next.x, planeHeight, current.z);
                if (grid.IsWalkableWorld(slideX))
                {
                    transform.Position = slideX;
                    return;
                }

                if (grid.IsWalkableWorld(current))
                {
                    transform.Position = current;
                    return;
                }
                if (TryFindNearbyWalkable(grid, current, k_StartSearchRings, out var safe))
                {
                    transform.Position = safe;
                }
            }

            /// <summary>
            /// 换目标：成功就记下本 agent 已经跟上了当前鼠标版本，挑不到任何目标就等一段冷却再试。
            /// </summary>
            private void RequestGoal(Entity entity, int sortKey, float3 position, in PathRequest request,
                                     bool cursorActive, ref AStarDemoMotion motion)
            {
                if (!TryRequestGoal(entity, sortKey, position, request, cursorActive, ref motion))
                {
                    motion.GoalKind = AStarDemoGoalKind.Failed;
                    motion.Cooldown = Config.RetryDelay;
                    return;
                }

                if (cursorActive)
                {
                    motion.PlannedCursorVersion = Cursor.Version;
                }
                motion.LastRepathFrame = (int)(FrameIndex & 0x7FFFFFFF);
                motion.RepathCount++;
                motion.PendingFrames = 0;
                motion.Cooldown = 0f;
            }

            /// <summary>
            /// 本帧是否轮到该 agent 响应鼠标新版本：帧号加上自己的相位能被步长整除时轮到它，
            /// 于是同一批 agent 会在 <paramref name="stride"/> 帧的窗口里各轮到一次。
            /// </summary>
            private static bool IsRepathDue(uint frameIndex, int phase, int stride)
            {
                var step = (uint)math.max(1, stride);
                var slot = (uint)math.clamp(phase, 0, (int)step - 1);
                return (frameIndex + slot) % step == 0u;
            }

            /// <summary>
            /// 经 ECB 把 <c>PathRequest</c> 换成新目标（版本加一），成功返回 true。
            /// 起点落在阻挡格时（例如被别的 agent 挤进墙里）退回最近的可走格中心。
            /// </summary>
            private bool TryRequestGoal(Entity entity, int sortKey, float3 position, in PathRequest request,
                                        bool cursorActive, ref AStarDemoMotion motion)
            {
                var start = position;
                if (!Grid.IsWalkableWorld(start) &&
                    !TryFindNearbyWalkable(Grid, start, k_StartSearchRings, out start))
                {
                    return false;
                }

                if (!TryPickGoalForAgent(start, cursorActive, ref motion.Rng, out var goal, out var kind))
                {
                    return false;
                }

                motion.GoalKind = kind;
                Ecb.SetComponent(sortKey, entity, new PathRequest
                {
                    Start = start,
                    End = goal,
                    Version = request.Version + 1,
                });
                return true;
            }

            /// <summary>
            /// 挑新目标：鼠标跟随可用时先按「鼠标落点 + 本 agent 的随机偏移」挑，
            /// 挑不到可走格就退回原来的随机目标逻辑；<paramref name="kind"/> 记录这次用的是哪一类。
            /// </summary>
            private bool TryPickGoalForAgent(float3 from, bool cursorActive, ref Unity.Mathematics.Random rng,
                                             out float3 goal, out AStarDemoGoalKind kind)
            {
                if (cursorActive &&
                    TryPickCursorGoal(Grid, from, Cursor.Position, Config.CursorSpreadRadius, ref rng,
                                      out goal, out kind))
                {
                    return true;
                }

                kind = AStarDemoGoalKind.Random;
                return TryPickGoal(Grid, from, ref rng, Config.MinGoalDistance, out goal);
            }

            /// <summary>
            /// 在鼠标落点周围 <paramref name="spreadRadius"/> 内随机挑一个可走格当目标
            /// （半径为 0 时全体 agent 取同一个点）。都挑不到就退回鼠标所在格，
            /// 那一格也不可走（例如鼠标压在墙上或场地外）就取它附近最近的可走格。
            /// </summary>
            private static bool TryPickCursorGoal(AStarGrid grid, float3 from, float3 cursor, float spreadRadius,
                                                  ref Unity.Mathematics.Random rng, out float3 goal,
                                                  out AStarDemoGoalKind kind)
            {
                kind = AStarDemoGoalKind.CursorSpread;
                var size = grid.Size;
                if (size.x <= 0 || size.y <= 0)
                {
                    goal = from;
                    kind = AStarDemoGoalKind.Failed;
                    return false;
                }

                var spreading = spreadRadius > 1e-3f;
                var attempts = spreading ? k_GoalAttempts : 1;
                for (var attempt = 0; attempt < attempts; attempt++)
                {
                    var sample = cursor;
                    if (spreading)
                    {
                        // 半径开平方：点才会在圆盘内均匀分布，而不是挤在圆心。
                        var radius = spreadRadius * math.sqrt(rng.NextFloat());
                        var direction = rng.NextFloat2Direction();
                        sample = cursor + new float3(direction.x * radius, 0f, direction.y * radius);
                    }

                    var cell = grid.WorldToCell(sample);
                    if (!grid.IsWalkable(cell))
                    {
                        continue;
                    }

                    var center = grid.CellCenter(cell);
                    goal = new float3(center.x, from.y, center.z);
                    kind = spreading ? AStarDemoGoalKind.CursorSpread : AStarDemoGoalKind.CursorCell;
                    return true;
                }

                var cursorCell = grid.WorldToCell(cursor);
                if (grid.IsWalkable(cursorCell))
                {
                    var center = grid.CellCenter(cursorCell);
                    goal = new float3(center.x, from.y, center.z);
                    kind = AStarDemoGoalKind.CursorCell;
                    return true;
                }

                if (TryFindNearbyWalkable(grid, new float3(cursor.x, from.y, cursor.z),
                                          k_CursorSearchRings, out goal))
                {
                    kind = AStarDemoGoalKind.NearCursor;
                    return true;
                }

                kind = AStarDemoGoalKind.Failed;
                return false;
            }

            /// <summary>从 <paramref name="from"/> 所在的格向外一圈圈找最近的可走格，返回它的格中心。</summary>
            private static bool TryFindNearbyWalkable(AStarGrid grid, float3 from, int maxRings, out float3 center)
            {
                center = from;
                var size = grid.Size;
                if (size.x <= 0 || size.y <= 0)
                {
                    return false;
                }

                var cell = math.clamp(grid.WorldToCell(from), int2.zero, size - 1);
                for (var ring = 0; ring <= maxRings; ring++)
                {
                    for (var dz = -ring; dz <= ring; dz++)
                    {
                        for (var dx = -ring; dx <= ring; dx++)
                        {
                            // 只看当前这一圈。
                            if (math.max(math.abs(dx), math.abs(dz)) != ring)
                            {
                                continue;
                            }

                            var candidate = cell + new int2(dx, dz);
                            if (!grid.IsWalkable(candidate))
                            {
                                continue;
                            }

                            var cellCenter = grid.CellCenter(candidate);
                            center = new float3(cellCenter.x, from.y, cellCenter.z);
                            return true;
                        }
                    }
                }

                return false;
            }

            /// <summary>随机挑一个可走格中心当新目标；前几次要求离 <paramref name="from"/> 足够远。</summary>
            private static bool TryPickGoal(AStarGrid grid, float3 from, ref Unity.Mathematics.Random rng, float minDistance,
                                            out float3 goal)
            {
                goal = from;
                var size = grid.Size;
                if (size.x < 3 || size.y < 3)
                {
                    return false;
                }

                var minDistanceSq = minDistance * minDistance;
                for (var attempt = 0; attempt < k_GoalAttempts; attempt++)
                {
                    var cell = new int2(rng.NextInt(1, size.x - 1), rng.NextInt(1, size.y - 1));
                    if (!grid.IsWalkable(cell))
                    {
                        continue;
                    }

                    var cellCenter = grid.CellCenter(cell);
                    var candidate = new float3(cellCenter.x, from.y, cellCenter.z);
                    if (attempt < k_FarAttempts && DistanceSqXZ(candidate, from) < minDistanceSq)
                    {
                        continue;
                    }

                    goal = candidate;
                    return true;
                }

                return false;
            }

            /// <summary>两个点在水面（xz）上的距离平方。</summary>
            private static float DistanceSqXZ(float3 a, float3 b)
            {
                var dx = a.x - b.x;
                var dz = a.z - b.z;
                return dx * dx + dz * dz;
            }

            /// <summary>从 <paramref name="from"/> 朝 <paramref name="to"/> 转，单帧最多 <paramref name="maxRadians"/> 弧度。</summary>
            private static quaternion RotateTowards(quaternion from, quaternion to, float maxRadians)
            {
                var dot = math.dot(from, to);
                if (dot < 0f)
                {
                    to.value = -to.value;
                    dot = -dot;
                }

                var angle = 2f * math.acos(math.clamp(dot, -1f, 1f));
                if (angle <= maxRadians)
                {
                    return to;
                }

                return math.slerp(from, to, maxRadians / angle);
            }
        }
    }
}
