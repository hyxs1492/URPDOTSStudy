using DOTSUtils.Spatial;
using DOTSUtils.Visibility;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace DOTSUtils.Rvo
{
    /// <summary>
    /// ORCA 求解系统：三阶段驱动，**三个阶段都是并行 job**。
    ///
    /// <list type="number">
    /// <item><see cref="RvoCollectNeighborsJob"/>（并行）：查邻居 → 写各自的 buffer；</item>
    /// <item><see cref="RvoSolveJob"/>（并行）：求解 → 写回速度；</item>
    /// <item><see cref="RvoIntegrateJob"/>（并行）：按需积分位置（关掉 <c>IntegratePosition</c> 就不调度）。</item>
    /// </list>
    ///
    /// 阶段 ① 收集的是**快照**，阶段 ② 只读快照、不读邻居当前的 <c>RvoAgent</c> ——
    /// 全体基于同一帧初的速度快照求解。<br/>
    ///
    /// <b>邻居索引来自 <c>Spatial</c> 模块</b>（本模块<b>不自建树</b>）：用哪一棵由
    /// 装配参数决定（运行态在 <c>SpatialSingleton.Tree.Kind</c> 里）。<br/>
    /// 没有可用的树时本帧<b>没有邻居</b>（不避障），<see cref="RvoStatus.Index"/> 会如实报告。
    /// <br/>
    /// agent 实体必须打 <c>SpatialIndexed</c>（树的元素集合就是这么来的）。
    ///
    /// <b>静态障碍物</b>：顶点由调用方通过 <c>RvoAccess.SetObstacles</c> 写进本系统建的单例实体
    /// （<see cref="RvoObstacleSingleton"/> + <c>DynamicBuffer&lt;RvoObstacleVertex&gt;</c> +
    /// <c>DynamicBuffer&lt;RvoObstaclePolygon&gt;</c>）；
    /// <see cref="RvoConfig.UseObstacles"/> 打开时阶段②把它们只读地并入 ORCA。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(RvoGroup))]
    public partial struct RvoSolveSystem : ISystem
    {
        private const int k_MaxNeighborsCap = 64;

        private Entity m_StateEntity;

        /// <summary>
        /// 静态障碍物单例（<see cref="RvoObstacleSingleton"/> 标记 + 顶点 / 多边形缓冲；
        /// 写入走 <c>RvoAccess.SetObstacles</c>）。
        /// </summary>
        private Entity m_ObstacleEntity;

        /// <summary>
        /// 本帧参与求解的 agent 集合（不可见的被排除）。建在 <see cref="OnCreate"/> 一次、缓存复用。
        /// </summary>
        private EntityQuery m_SolverQuery;

        /// <summary>「是 agent 但还没有邻居缓冲」的集合（补缓冲用）。</summary>
        private EntityQuery m_NeedNeighborBufferQuery;

        /// <summary>参与求解的 agent 数（<c>RvoStatus.AgentCount</c> 的口径）。</summary>
        private EntityQuery m_CountQuery;

        /// <summary>
        /// 找 Spatial 单例用的 query。<b>在 OnCreate 建好缓存</b>（在 OnUpdate 里建 query 会触发 Entities 警告）。
        /// </summary>
        private EntityQuery m_SpatialSingletonQuery;

        /// <summary>
        /// 状态实体（<c>RvoStatus</c> 挂在它身上；外部用 <c>RvoAccess.TryGetStateEntity</c>）。
        /// </summary>
        public Entity StateEntity => m_StateEntity;

        public void OnCreate(ref SystemState state)
        {
            m_StateEntity = state.EntityManager.CreateEntity(typeof(RvoStatus));
            CreateObstacleSingleton(ref state);

            // 显式 query 必须覆盖三个 job 的**全部**组件要求，否则调度时抛
            // InvalidOperationException（"the query must contain all the components required
            // for ... Execute() to run"）。
            // WithNone<VisibilityCulledTag>：被裁剪的（挂且启用）排除掉；没挂或挂+禁用的算可见。
            m_SolverQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAllRW<RvoAgent>()
                .WithAll<LocalTransform>()
                .WithAllRW<RvoNeighbor>()
                .WithAllRW<SpatialElement>()
                .WithNone<VisibilityCulledTag>()
                .Build(ref state);

            m_NeedNeighborBufferQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<RvoAgent>()
                .WithNone<RvoNeighbor>()
                .Build(ref state);

            m_CountQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAllRW<RvoAgent>()
                .WithAll<LocalTransform>()
                .WithNone<VisibilityCulledTag>()
                .Build(ref state);

            // 找 Spatial 单例的 query 也在 OnCreate 建好（在 OnUpdate 里建会触发 Entities 警告）。
            m_SpatialSingletonQuery = state.GetEntityQuery(ComponentType.ReadOnly<SpatialSingleton>());
        }

        public void OnUpdate(ref SystemState state)
        {
            var config = RvoSettings.Config;
            var dt = config.FixedTimeStep > 0f ? config.FixedTimeStep : SystemAPI.Time.DeltaTime;
            if (dt <= 0f)
            {
                // 时间没推进：求解无意义（也避免除零）。状态里如实写下 dt。
                WriteStatus(state.EntityManager, new RvoStatus
                {
                    Index = RvoNeighborIndex.None,
                    LastDt = dt,
                });
                return;
            }

            // 计数用「本帧参与求解的 agent」：与求解/积分 query 同口径；不可见（视口外）的不在其中。
            var count = m_CountQuery.CalculateEntityCount();
            if (count == 0)
            {
                WriteStatus(state.EntityManager, new RvoStatus
                {
                    Index = RvoNeighborIndex.None,
                    LastDt = dt,
                });
                return;
            }

            var maxNeighbors = math.clamp(config.MaxNeighbors, 0, k_MaxNeighborsCap);
            var index = ResolveIndex(ref state, config, out var reader);
            var solverQuery = m_SolverQuery;

            // 障碍物单例（被外部销毁 / 缓冲类型对不上时重建）与计数：求解 job 只读那份顶点缓冲。
            if (!state.EntityManager.Exists(m_ObstacleEntity) ||
                !state.EntityManager.HasBuffer<RvoObstacleVertex>(m_ObstacleEntity) ||
                !state.EntityManager.HasBuffer<RvoObstaclePolygon>(m_ObstacleEntity))
            {
                if (state.EntityManager.Exists(m_ObstacleEntity))
                {
                    state.EntityManager.DestroyEntity(m_ObstacleEntity);
                }
                CreateObstacleSingleton(ref state);
            }
            var obstacleVertices = SystemAPI.GetBufferLookup<RvoObstacleVertex>(isReadOnly: true);
            var obstaclePolygons = SystemAPI.GetBufferLookup<RvoObstaclePolygon>(isReadOnly: true);
            var obstacleCount = 0;
            var obstacleVertexCount = 0;
            if (config.UseObstacles)
            {
                if (obstaclePolygons.TryGetBuffer(m_ObstacleEntity, out var polygonBuffer))
                {
                    obstacleCount = polygonBuffer.Length;
                }
                if (obstacleVertices.TryGetBuffer(m_ObstacleEntity, out var vertexBuffer))
                {
                    obstacleVertexCount = vertexBuffer.Length;
                }
            }

            // ① 新 agent 补邻居缓冲（并行 job + ECB，帧末落地；本帧它们还不参与查询）。
            var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                               .CreateCommandBuffer(state.WorldUnmanaged);
            state.Dependency = new EnsureRvoNeighborBufferJob
            {
                Ecb = ecb.AsParallelWriter(),
            }.ScheduleParallel(m_NeedNeighborBufferQuery, state.Dependency);

            // ② 查邻居（并行；索引是统一视图，不关心背后是哪棵树）。
            state.Dependency = new RvoCollectNeighborsJob
            {
                Index = reader,
                MaxNeighbors = maxNeighbors,
                NeighborDist = math.max(0f, config.NeighborDist),
                Agents = SystemAPI.GetComponentLookup<RvoAgent>(isReadOnly: true),
                Visible = SystemAPI.GetComponentLookup<VisibilityCulledTag>(isReadOnly: true),
            }.ScheduleParallel(solverQuery, state.Dependency);

            // ③ 求解 + 积分（并行）
            state.Dependency = new RvoSolveJob
            {
                Config = config,
                Dt = dt,
                ObstacleEntity = m_ObstacleEntity,
                Obstacles = obstacleVertices,
            }.ScheduleParallel(solverQuery, state.Dependency);

            if (config.IntegratePosition)
            {
                state.Dependency = new RvoIntegrateJob
                {
                    Dt = dt,
                }.ScheduleParallel(solverQuery, state.Dependency);
            }

            WriteStatus(state.EntityManager, new RvoStatus
            {
                Index = index,
                AgentCount = count,
                LastDt = dt,
                ObstacleCount = obstacleCount,
                ObstacleVertexCount = obstacleVertexCount,
            });
        }



        public void OnDestroy(ref SystemState state)
        {
            try
            {
                if (m_StateEntity != Entity.Null && state.World.IsCreated &&
                    state.EntityManager.Exists(m_StateEntity))
                {
                    state.EntityManager.DestroyEntity(m_StateEntity);
                }
                if (m_ObstacleEntity != Entity.Null && state.World.IsCreated &&
                    state.EntityManager.Exists(m_ObstacleEntity))
                {
                    state.EntityManager.DestroyEntity(m_ObstacleEntity);
                }
            }
            catch (System.Exception)
            {
                // World 正在销毁：EntityManager 可能已经不可用，交给世界销毁流程回收。
            }
        }

        /// <summary>
        /// 建障碍物单例：顶点与多边形分段就是这个实体上的
        /// <c>DynamicBuffer&lt;RvoObstacleVertex&gt;</c> 与 <c>DynamicBuffer&lt;RvoObstaclePolygon&gt;</c>。
        /// </summary>
        private void CreateObstacleSingleton(ref SystemState state)
        {
            m_ObstacleEntity = state.EntityManager.CreateEntity(
                typeof(RvoObstacleSingleton), typeof(RvoObstacleVertex), typeof(RvoObstaclePolygon));
        }
        // ───────────────────────── 索引 ─────────────────────────

        /// <summary>
        /// 找本帧用的邻居索引：**用哪棵树看 <c>SpatialSettings.Pending.Index</c>**（和实体上的
        /// <c>SpatialIndexed</c> tag 是同一套配置）—— 两个都要时四叉树优先（RVO 只在 xz 平面避障）。
        /// </summary>
        private RvoNeighborIndex ResolveIndex(ref SystemState state, in RvoConfig config,
                                      out SpatialIndexReader reader)
        {
            reader = default;
            if (!config.UseSpatialIndex)
            {
                return RvoNeighborIndex.Disabled;
            }

            // 用 OnCreate 缓存好的 query。
            var query = m_SpatialSingletonQuery;
            if (query.IsEmptyIgnoreFilter)
            {
                return RvoNeighborIndex.None;
            }

            // **必须**用 EntityManager.GetComponentData（主线程读取 ⇒ 触发同步点），保证
            // SpatialBuildSystem 本帧写完树之后才读到 data.Tree。
            // 顺序前提（SpatialGroup → RvoGroup）由 RvoGroup 上的 [UpdateAfter(typeof(SpatialGroup))] 声明。
            var data = state.EntityManager.GetComponentData<SpatialSingleton>(query.GetSingletonEntity());
            if (!data.Tree.IsCreated)
            {
                return RvoNeighborIndex.None;
            }

            reader = new SpatialIndexReader(data.Tree);
            return data.Tree.Kind == SpatialIndexKind.Quadtree
                ? RvoNeighborIndex.Quadtree
                : RvoNeighborIndex.Octree;
        }

        private void WriteStatus(EntityManager em, in RvoStatus status)
        {
            if (m_StateEntity == Entity.Null || !em.Exists(m_StateEntity))
            {
                return;
            }
            em.SetComponentData(m_StateEntity, status);
        }
    }

    /// <summary>
    /// 给「是 agent、但还没有 <see cref="RvoNeighbor"/> 缓冲」的实体补一个空缓冲。<br/>
    /// <b>并行</b>（<c>IJobEntity</c>）+ ECB（帧末落地）。
    /// </summary>
    [BurstCompile]
    public partial struct EnsureRvoNeighborBufferJob : IJobEntity
    {
        public EntityCommandBuffer.ParallelWriter Ecb;

        private void Execute(Entity entity, [ChunkIndexInQuery] int sortKey)
        {
            Ecb.AddBuffer<SpatialElement>(sortKey, entity);   // 查询命中缓冲（就是空间索引的元素类型）
            Ecb.AddBuffer<RvoNeighbor>(sortKey, entity);      // 求解输入
        }
    }

    /// <summary>
    /// 阶段 ①：逐 agent 查邻居（<b>并行</b>：<c>IJobEntity</c>）。<br/>
    /// 索引树由 <c>Spatial</c> 模块提供（用哪棵由装配参数决定）；<br/>
    /// 缓冲先按 <c>MaxNeighbors + 1</c> 开（多出来那个给自己：自己距离 0 必定入选），查完把
    /// 「自己」从链表里摘掉再缩回实际条数。
    /// </summary>
    [BurstCompile]
    public partial struct RvoCollectNeighborsJob : IJobEntity
    {
        /// <summary>
        /// 当前生效的空间索引（统一入口；<c>Kind</c> 告诉它背后是哪棵树）。
        /// </summary>
        public SpatialIndexReader Index;

        public int MaxNeighbors;
        public float NeighborDist;

        /// <summary>
        /// 邻居的 <see cref="RvoAgent"/>（速度 / 半径）；只读。
        /// </summary>
        [ReadOnly] public ComponentLookup<RvoAgent> Agents;

        /// <summary>
        /// 邻居的可见性（见 <see cref="VisibilityAccess.IsCulled"/>）。<br/>
        /// 不可见的邻居<b>等同于不存在</b>。
        /// </summary>
        [ReadOnly] public ComponentLookup<VisibilityCulledTag> Visible;

        private void Execute(Entity self, in LocalTransform transform,
                     DynamicBuffer<SpatialElement> hits, DynamicBuffer<RvoNeighbor> neighbors)
        {
            hits.Clear();
            neighbors.Clear();
            if (!Index.IsCreated || MaxNeighbors <= 0)
            {
                return;
            }

            hits.ResizeUninitialized(MaxNeighbors + 1);      // +1 个给自己（距离 0，必定入选）

            // 四叉树 / 八叉树 查询邻居
            var found = Index.RangeQueryNearest(transform.Position, NeighborDist, hits.AsNativeArray());

            // 把命中的邻居转成求解输入（速度/半径从只读 ComponentLookup 里查）
            neighbors.ResizeUninitialized(found);
            var write = 0;
            for (var read = 0; read < found; read++)
            {
                var hit = hits[read];
                if (hit.Entity == self) //摘掉自己
                {
                    continue;
                }
                if (!Agents.TryGetComponent(hit.Entity, out var other)) //摘掉非Rvo对象
                {
                    continue;
                }
                if (VisibilityAccess.IsCulled(Visible, hit.Entity)) // 被裁剪的邻居等同于不存在
                {
                    continue;
                }

                neighbors[write++] = new RvoNeighbor
                {
                    State = new AgentState
                    {
                        Position = new float2(hit.Pos.x, hit.Pos.z),
                        Velocity = other.Velocity,
                        Radius = math.max(0f, other.Radius),
                    },
                };
            }

            neighbors.ResizeUninitialized(write);
        }
    }

    /// <summary>
    /// 阶段 ②：读邻居快照求解，写回 <see cref="RvoAgent.Velocity"/>（不碰位置）。
    ///
    /// <c>Config.UseObstacles</c> 打开时，还要把障碍物单例上的静态障碍物顶点并入约束
    /// （只读 <see cref="BufferLookup{T}"/>，job 内不做结构性变更）。
    /// </summary>
    [BurstCompile]
    public partial struct RvoSolveJob : IJobEntity
    {
        public RvoConfig Config;
        public float Dt;

        /// <summary>
        /// 障碍物单例实体（顶点缓冲挂在它身上）。
        /// </summary>
        public Entity ObstacleEntity;

        /// <summary>
        /// 静态障碍物顶点（扁平数组，拓扑在 <c>Prev</c>/<c>Next</c> 里）；只读。
        /// </summary>
        [ReadOnly] public BufferLookup<RvoObstacleVertex> Obstacles;

        private void Execute(ref RvoAgent agent, in LocalTransform transform,
                     in DynamicBuffer<RvoNeighbor> neighbors)
        {
            var self = new AgentState
            {
                Position = new float2(transform.Position.x, transform.Position.z),
                Velocity = agent.Velocity,
                Radius = math.max(0f, agent.Radius),
                MaxSpeed = math.max(0f, agent.MaxSpeed),
                PrefVelocity = agent.PrefVelocity,
            };

            // RvoNeighbor 与 AgentState 同为 32 字节 ⇒ 零拷贝重解释；缓冲长度就是有效邻居数
            var states = neighbors.Reinterpret<AgentState>();

            float2 newVelocity;
            if (Config.UseObstacles && Obstacles.TryGetBuffer(ObstacleEntity, out var obstacles) &&
                obstacles.Length > 0)
            {
                var vertices = obstacles.AsNativeArray();
                newVelocity = OrcaSolver.Solve(self, states.AsNativeArray(), states.Length,
                                               vertices, vertices.Length,
                                               Config.TimeHorizon, Config.TimeHorizonObst, Dt);
            }
            else
            {
                newVelocity = OrcaSolver.Solve(self, states.AsNativeArray(), states.Length,
                                               Config.TimeHorizon, Dt);
            }

            agent.NewVelocity = newVelocity;
            // 写回速度
            agent.Velocity = newVelocity;
        }
    }

    /// <summary>
    /// 阶段 ③：需要时积分位置（<c>y</c> 分量保持不动）。
    /// </summary>
    [BurstCompile]
    public partial struct RvoIntegrateJob : IJobEntity
    {
        public float Dt;

        private void Execute(ref RvoAgent agent, ref LocalTransform transform)
        {
            var position = transform.Position;
            position.x += agent.Velocity.x * Dt;
            position.z += agent.Velocity.y * Dt;
            transform.Position = position;
        }
    }

}
