using System.Collections.Generic;
using DOTSUtils.Benchmark;
using DOTSUtils.Pathfinding;
using DOTSUtils.RenderBridge;
using DOTSUtils.Rvo;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace DOTSUtils.Samples.Pathfinding
{
    /// <summary>
    /// 生成一批 A* 演示 agent 需要的全部参数（场地 + 外观 + 运动）。
    /// </summary>
    public struct AStarDemoSpawnRequest
    {
        /// <summary>渲染源对象：渲染桥从它采集渲染目标，不实例化它本身。</summary>
        public GameObject Source;

        /// <summary>材质变体，每个 agent 随机取一个。</summary>
        public Material[] Materials;

        /// <summary>agent 数量。</summary>
        public int Count;

        /// <summary>可走掩码（行主序，1 = 可走）。</summary>
        public byte[] Mask;

        /// <summary>格数（x）。</summary>
        public int Width;

        /// <summary>格数（z）。</summary>
        public int Height;

        /// <summary>格子边长（世界单位）。</summary>
        public float CellSize;

        /// <summary>网格最小角的世界 x（掩码第 0 列的左边界）；默认 0 与「场地从世界原点起」一致。</summary>
        public float OriginX;

        /// <summary>网格最小角的世界 z（掩码第 0 行的下边界）；默认 0 与「场地从世界原点起」一致。</summary>
        public float OriginZ;

        /// <summary>所有 agent 所在的水平面高度（y）。</summary>
        public float PlaneHeight;

        /// <summary>Rvo 碰撞半径。</summary>
        public float AgentRadius;

        /// <summary>
        /// 渲染目标水平最大边长的目标值（世界单位）；&gt; 0 时按「该值 / 渲染源水平最大边长」等比缩放生成的 agent，
        /// &lt;= 0 时沿用渲染源自带的尺寸。
        /// </summary>
        public float ShipFootprint;

        /// <summary>巡航速度（世界单位/秒）。</summary>
        public float Speed;

        /// <summary>首次请求路径前的随机延迟上限，单位：秒；用来把一万个请求错开。</summary>
        public float InitialDelayMax;

        /// <summary>随机种子（0 按 1 处理）。</summary>
        public uint Seed;

        /// <summary>给前几个 agent 挂调试画线标记。</summary>
        public int DebugPathCount;

        /// <summary>鼠标重规划的错峰步长（帧）：每个 agent 的相位从 <c>[0, 步长)</c> 里随机取一个。</summary>
        public int RepathStride;
    }

    /// <summary>
    /// A* 演示的 agent 生成器：复用 benchmark 的纯 ECS 生成路径
    /// （<see cref="BenchmarkShipFactory.SpawnPureEcs"/>，内部走渲染桥的
    /// <c>SpawnFromPrefab</c> 与 <see cref="RenderMeshPool"/> 去重），
    /// 再把父实体摆到可走格上、挂上寻路与移动组件。整个场景里不为 agent 建 GameObject。
    /// </summary>
    public static class AStarDemoSpawner
    {
        /// <summary>
        /// 生成 <see cref="AStarDemoSpawnRequest.Count"/> 个 agent，返回实际落位的数量；
        /// 生成的**父实体**追加进 <paramref name="parents"/>（销毁父实体即级联销毁整棵渲染树）。
        /// </summary>
        public static int Spawn(World world, in AStarDemoSpawnRequest request, RenderMeshPool pool,
                                List<Entity> parents)
        {
            parents?.Clear();

            if (world == null || !world.IsCreated)
            {
                Debug.LogWarning("[AStarDemo] 生成 agent 需要一个已创建的 World。");
                return 0;
            }
            if (pool == null || request.Source == null ||
                request.Materials == null || request.Materials.Length == 0)
            {
                Debug.LogWarning("[AStarDemo] 生成 agent 需要 1 个源对象与至少 1 个材质。");
                return 0;
            }

            // 生成位置先落在一个覆盖整片场地的圆盘里；紧接着就会被摆到可走格中心。
            var radius = 0.5f * math.length(new float2(request.Width * request.CellSize,
                                                       request.Height * request.CellSize));

            // 纯 ECS 生成：不实例化 GameObject，渲染走 Entities Graphics；created 里是整棵树（父 + 渲染子实体）。
            var created = new List<Entity>(math.max(4, request.Count * 2));
            var spawned = BenchmarkShipFactory.SpawnPureEcs(
                world, request.Source, request.Count, request.Materials, radius, request.Seed, pool, created);
            if (spawned <= 0)
            {
                return 0;
            }

            var cells = AStarDemoGridMask.CollectWalkableCells(request.Mask, request.Width, request.Height);
            if (cells.Count == 0)
            {
                Debug.LogWarning("[AStarDemo] 掩码里没有可走格，agent 无处安放。");
                return 0;
            }

            var em = world.EntityManager;
            var rng = new Unity.Mathematics.Random(request.Seed == 0u ? 1u : request.Seed);

            // 目标尺寸换算出的等比缩放：渲染子实体的局部变换是「相对渲染树根」的，缩放挂在父实体上就带动整棵树。
            var uniformScale = 0f;
            if (request.ShipFootprint > 0f)
            {
                var footprint = MeasureHorizontalFootprint(request.Source);
                uniformScale = footprint > 1e-4f ? request.ShipFootprint / footprint : 1f;
                Debug.Log($"[AStarDemo] 渲染源 {request.Source.name} 水平边长 {footprint:0.###}，" +
                          $"按目标边长 {request.ShipFootprint:0.###} 缩放 {uniformScale:0.#####}。");
            }

            // 记下建表版本：跟路径系统用它判断网格是否被重建。
            var gridVersion = AStarAccess.TryGetSingletonData(world, out var data) ? data.BuildVersion : int.MinValue;

            var placed = 0;
            for (var i = 0; i < created.Count && placed < spawned; i++)
            {
                var entity = created[i];

                // 只认渲染树的父实体：渲染桥给每棵树的父实体挂 DotsRenderPrefabInstance，
                // 渲染子实体靠 Parent 跟随父实体。
                if (!em.HasComponent<DotsRenderPrefabInstance>(entity))
                {
                    continue;
                }

                // 可走格按行主序轮着分配：一万个 agent 铺满全场的可走格，密度均匀。
                PlaceAtCell(em, entity, cells[placed % cells.Count], request, uniformScale, ref rng);
                AttachComponents(em, entity, request, gridVersion, placed, ref rng);
                parents?.Add(entity);
                placed++;
            }

            return placed;
        }

        /// <summary>
        /// 把父实体摆到格中心（格内加一点抖动，避免同一格上的两个 agent 完全重叠），
        /// 并在 <paramref name="uniformScale"/> &gt; 0 时把等比缩放写进 <c>LocalTransform.Scale</c>。
        /// </summary>
        private static void PlaceAtCell(EntityManager em, Entity entity, int2 cell,
                                        in AStarDemoSpawnRequest request, float uniformScale,
                                        ref Unity.Mathematics.Random rng)
        {
            var centerX = request.OriginX + (cell.x + 0.5f) * request.CellSize;
            var centerZ = request.OriginZ + (cell.y + 0.5f) * request.CellSize;
            var jitter = rng.NextFloat2(new float2(-1f), new float2(1f)) * (request.CellSize * 0.3f);

            var transform = em.GetComponentData<LocalTransform>(entity);
            transform.Position = new float3(centerX + jitter.x, request.PlaneHeight, centerZ + jitter.y);
            transform.Rotation = quaternion.RotateY(rng.NextFloat(0f, 2f * math.PI));
            if (uniformScale > 0f)
            {
                transform.Scale = uniformScale;
            }
            em.SetComponentData(entity, transform);
        }

        /// <summary>
        /// 量出渲染源在自身根坐标系里的包围盒尺寸：合并全部 <see cref="Renderer"/>（含非激活子物体、
        /// LODGroup 各层）的局部包围盒。
        /// </summary>
        public static float3 MeasureLocalSize(GameObject source)
        {
            if (source == null)
            {
                return float3.zero;
            }

            var root = source.transform;
            var toRoot = (float4x4)root.worldToLocalMatrix;
            var min = new float3(float.MaxValue);
            var max = new float3(float.MinValue);
            var hasRenderer = false;

            var renderers = source.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                var matrix = math.mul(toRoot, (float4x4)renderer.transform.localToWorldMatrix);
                // 网格物体优先用网格自带的局部包围盒，其余渲染器（蒙皮等）用渲染器的局部包围盒。
                var local = renderer.localBounds;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                {
                    local = filter.sharedMesh.bounds;
                }
                var center = (float3)local.center;
                var extents = (float3)local.extents;

                for (var corner = 0; corner < 8; corner++)
                {
                    var offset = new float3(
                        (corner & 1) == 0 ? -extents.x : extents.x,
                        (corner & 2) == 0 ? -extents.y : extents.y,
                        (corner & 4) == 0 ? -extents.z : extents.z);
                    var point = math.transform(matrix, center + offset);
                    min = math.min(min, point);
                    max = math.max(max, point);
                    hasRenderer = true;
                }
            }

            return hasRenderer ? max - min : float3.zero;
        }

        /// <summary>渲染源水平方向（xz 平面）的最大边长（世界单位）；没有渲染目标时返回 0。</summary>
        public static float MeasureHorizontalFootprint(GameObject source)
        {
            var size = MeasureLocalSize(source);
            return math.max(size.x, size.z);
        }

        /// <summary>挂上演示组件：标记、行走状态、A* 请求 / 结果 / 路点缓冲，并覆盖 Rvo 参数。</summary>
        private static void AttachComponents(EntityManager em, Entity entity, in AStarDemoSpawnRequest request,
                                             int gridVersion, int index, ref Unity.Mathematics.Random rng)
        {
            // 生成路径顺带挂上的 benchmark 游走状态本演示不用，摘掉它：
            // 跟路径系统是唯一写 PrefVelocity 与积分位置的地方。
            if (em.HasComponent<BenchmarkMotion>(entity))
            {
                em.RemoveComponent<BenchmarkMotion>(entity);
            }
            if (em.HasComponent<BenchmarkShipTag>(entity))
            {
                em.RemoveComponent<BenchmarkShipTag>(entity);
            }

            em.AddComponentData(entity, default(AStarDemoAgentTag));
            var stride = math.max(1, request.RepathStride > 0
                                     ? request.RepathStride
                                     : AStarDemoConfig.Default.CursorRepathStride);
            em.AddComponentData(entity, new AStarDemoMotion
            {
                Speed = request.Speed,
                // 0 ~ InitialDelayMax 的随机延迟：一万个 agent 不要在同一帧一起请求路径。
                Cooldown = request.InitialDelayMax > 0f ? rng.NextFloat(0f, request.InitialDelayMax) : 0f,
                GridBuildVersion = gridVersion,
                Rng = new Unity.Mathematics.Random(rng.NextUInt(1u, uint.MaxValue)),
                // 错峰相位：鼠标换版本后，各 agent 在自己那一帧响应，整群摊到步长帧里。
                RepathPhase = rng.NextInt(0, stride),
                GoalKind = AStarDemoGoalKind.None,
            });

            // 请求与结果都留默认值（Version 0 = SolvedVersion 0 ⇒ A* 系统不会去解它），
            // 真正的第一次请求由跟路径系统在冷却结束后发出。
            em.AddComponentData(entity, default(PathRequest));
            em.AddComponentData(entity, default(PathStatus));
            em.AddBuffer<PathWaypoint>(entity);

            if (index < request.DebugPathCount)
            {
                em.AddComponentData(entity, new AStarDemoDebugPath { Order = index });
            }

            // RvoAgent 由 benchmark 的生成路径挂上，这里按演示参数覆盖半径与最大速度。
            var agent = em.GetComponentData<RvoAgent>(entity);
            agent.Radius = request.AgentRadius;
            agent.MaxSpeed = request.Speed;
            agent.Velocity = float2.zero;
            agent.PrefVelocity = float2.zero;
            em.SetComponentData(entity, agent);
        }
    }
}
