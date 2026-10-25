using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace DOTSUtils.Rvo
{
    /// <summary>
    /// 读 RVO 运行状态的入口。
    ///
    /// <c>RvoSolveSystem</c> 是 <c>ISystem</c>（struct、没有托管实例），
    /// 只能拿 <c>SystemHandle</c>、再用 <c>WorldUnmanaged.GetUnsafeSystemRef</c> 读它的字段
    /// （状态实体就在字段里）。
    ///
    /// 参数不在这里：它是**全局配置**（<c>RvoSettings.Config</c>），所有 World 共用一份。
    /// </summary>
    public static class RvoAccess
    {
        /// <summary>
        /// 状态实体（<c>RvoStatus</c> 挂在它身上）。
        /// </summary>
        public static bool TryGetStateEntity(World world, out Entity entity)
        {
            entity = Entity.Null;
            if (world == null || !world.IsCreated)
            {
                return false;
            }

            var handle = world.GetExistingSystem<RvoSolveSystem>();
            if (handle == SystemHandle.Null)
            {
                return false;
            }

            entity = world.Unmanaged.GetUnsafeSystemRef<RvoSolveSystem>(handle).StateEntity;
            return entity != Entity.Null;
        }

        /// <summary>
        /// 读运行状态（系统每帧写）—— 想知道「本帧用了哪棵索引树、有几个 agent」时用它。
        /// </summary>
        public static bool TryGetStatus(World world, out RvoStatus status)
        {
            status = default;
            if (!TryGetStateEntity(world, out var entity))
            {
                return false;
            }
            if (!world.EntityManager.Exists(entity))
            {
                return false;
            }
            if (!world.EntityManager.HasComponent<RvoStatus>(entity))
            {
                return false;
            }

            status = world.EntityManager.GetComponentData<RvoStatus>(entity);
            return true;
        }

        // ───────────────────────── 静态障碍物 ─────────────────────────

        /// <summary>
        /// 障碍物单例实体（顶点 / 多边形缓冲挂在它身上）。
        /// </summary>
        public static bool TryGetObstacleEntity(World world, out Entity entity)
        {
            entity = Entity.Null;
            if (world == null || !world.IsCreated)
            {
                return false;
            }

            var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<RvoObstacleSingleton>());
            try
            {
                if (query.IsEmptyIgnoreFilter)
                {
                    return false;
                }
                entity = query.GetSingletonEntity();
                return true;
            }
            finally
            {
                query.Dispose();
            }
        }

        /// <summary>
        /// 写入静态障碍物：<paramref name="vertices"/> 是**扁平顶点位置**，
        /// <paramref name="polygonVertexCounts"/> 是**多边形分段** —— 第 <c>i</c> 个多边形占用
        /// <c>vertices[offset, offset + polygonVertexCounts[i])</c>，<c>offset</c> 从 0 起累加。
        /// 顶点数 &lt; 2 的分段不是障碍物，直接跳过。
        ///
        /// 覆盖旧内容；<c>Prev</c>/<c>Next</c>/<c>UnitDir</c>/<c>IsConvex</c> 由
        /// <see cref="RvoObstacleBuilder"/> 按 RVO2 <c>RVOSimulator::addObstacle</c> 的语义补全。
        /// <paramref name="vertices"/> 未创建或为空等同于 <see cref="ClearObstacles"/>（主线程调用）。
        ///
        /// 调用时机是**帧之间**（世界不在 <c>Update</c> 里、上一次求解的 job 已结束）——
        /// 求解 job 持有障碍物缓冲的只读句柄，帧内改写会被安全系统拦下。
        /// 没装 Rvo 模块（没有单例实体）时返回 false。
        /// </summary>
        public static bool SetObstacles(World world, NativeArray<float2> vertices, NativeArray<int> polygonVertexCounts)
        {
            if (!TryGetObstacleEntity(world, out var entity) || !HasObstacleBuffers(world, entity))
            {
                return false;
            }

            var builtVertices = new NativeList<RvoObstacleVertex>(Allocator.Temp);
            var builtPolygons = new NativeList<RvoObstaclePolygon>(Allocator.Temp);
            try
            {
                if (vertices.IsCreated && polygonVertexCounts.IsCreated)
                {
                    var start = 0;
                    for (var i = 0; i < polygonVertexCounts.Length; i++)
                    {
                        var count = polygonVertexCounts[i];
                        RvoObstacleBuilder.Append(builtVertices, builtPolygons, vertices, start, count);
                        start += math.max(0, count);
                    }
                }

                WriteObstacles(world, entity, builtVertices, builtPolygons);
                return true;
            }
            finally
            {
                builtVertices.Dispose();
                builtPolygons.Dispose();
            }
        }

        /// <summary>
        /// 同上，入参是托管数组。写进缓冲后与入参再无关系。
        /// </summary>
        public static bool SetObstacles(World world, float2[] vertices, int[] polygonVertexCounts)
        {
            if (vertices == null || polygonVertexCounts == null)
            {
                return SetObstacles(world, default(NativeArray<float2>), default(NativeArray<int>));
            }

            var points = new NativeArray<float2>(vertices, Allocator.Temp);
            var counts = new NativeArray<int>(polygonVertexCounts, Allocator.Temp);
            try
            {
                return SetObstacles(world, points, counts);
            }
            finally
            {
                points.Dispose();
                counts.Dispose();
            }
        }

        /// <summary>
        /// 写入静态障碍物：<paramref name="vertices"/> 是**扁平顶点位置**，
        /// <paramref name="polygons"/> 给出每个多边形在顶点数组里的 <c>(Start, Count)</c> 分段。
        /// </summary>
        public static bool SetObstacles(World world, NativeArray<float2> vertices,
                                        NativeArray<RvoObstaclePolygon> polygons)
        {
            if (!TryGetObstacleEntity(world, out var entity) || !HasObstacleBuffers(world, entity))
            {
                return false;
            }

            var builtVertices = new NativeList<RvoObstacleVertex>(Allocator.Temp);
            var builtPolygons = new NativeList<RvoObstaclePolygon>(Allocator.Temp);
            try
            {
                if (vertices.IsCreated && polygons.IsCreated)
                {
                    for (var i = 0; i < polygons.Length; i++)
                    {
                        RvoObstacleBuilder.Append(builtVertices, builtPolygons, vertices,
                                                  polygons[i].Start, polygons[i].Count);
                    }
                }

                WriteObstacles(world, entity, builtVertices, builtPolygons);
                return true;
            }
            finally
            {
                builtVertices.Dispose();
                builtPolygons.Dispose();
            }
        }

        /// <summary>
        /// 同上，入参是托管数组。
        /// </summary>
        public static bool SetObstacles(World world, float2[] vertices, RvoObstaclePolygon[] polygons)
        {
            if (vertices == null || polygons == null)
            {
                return SetObstacles(world, default(NativeArray<float2>),
                                    default(NativeArray<RvoObstaclePolygon>));
            }

            var points = new NativeArray<float2>(vertices, Allocator.Temp);
            var segments = new NativeArray<RvoObstaclePolygon>(polygons, Allocator.Temp);
            try
            {
                return SetObstacles(world, points, segments);
            }
            finally
            {
                points.Dispose();
                segments.Dispose();
            }
        }

        /// <summary>
        /// **旧线段模型**入口：每条线段按一个二顶点多边形并入
        /// （两条边 <c>A→B</c> 与 <c>B→A</c>，两个顶点都是凸顶点，两侧都挡）。
        /// 方向约定见 <see cref="RvoObstacleSegment"/>。未创建或为空等同于 <see cref="ClearObstacles"/>。
        /// </summary>
        public static bool SetObstacles(World world, NativeArray<RvoObstacleSegment> segments)
        {
            if (!TryGetObstacleEntity(world, out var entity) || !HasObstacleBuffers(world, entity))
            {
                return false;
            }

            var builtVertices = new NativeList<RvoObstacleVertex>(Allocator.Temp);
            var builtPolygons = new NativeList<RvoObstaclePolygon>(Allocator.Temp);
            try
            {
                if (segments.IsCreated)
                {
                    for (var i = 0; i < segments.Length; i++)
                    {
                        RvoObstacleBuilder.AppendSegment(builtVertices, builtPolygons, segments[i].A, segments[i].B);
                    }
                }

                WriteObstacles(world, entity, builtVertices, builtPolygons);
                return true;
            }
            finally
            {
                builtVertices.Dispose();
                builtPolygons.Dispose();
            }
        }

        /// <summary>
        /// 同上，入参是托管数组。
        /// </summary>
        public static bool SetObstacles(World world, RvoObstacleSegment[] segments)
        {
            if (segments == null)
            {
                return SetObstacles(world, default(NativeArray<RvoObstacleSegment>));
            }

            var native = new NativeArray<RvoObstacleSegment>(segments, Allocator.Temp);
            try
            {
                return SetObstacles(world, native);
            }
            finally
            {
                native.Dispose();
            }
        }

        /// <summary>
        /// 清空静态障碍物（主线程调用）。返回 false 表示没有障碍物单例（没装 Rvo 模块）或缓冲类型缺失。
        /// </summary>
        public static bool ClearObstacles(World world)
        {
            if (!TryGetObstacleEntity(world, out var entity) || !HasObstacleBuffers(world, entity))
            {
                return false;
            }

            world.EntityManager.GetBuffer<RvoObstacleVertex>(entity).Clear();
            world.EntityManager.GetBuffer<RvoObstaclePolygon>(entity).Clear();
            BumpObstacleVersion(world, entity);
            return true;
        }

        /// <summary>
        /// 当前配置的障碍物**个数**（多边形）。返回 false 表示读不到（没装 Rvo 模块 / 单例被销毁）。
        /// 本帧实际参与求解的个数看 <see cref="RvoStatus.ObstacleCount"/>。
        /// </summary>
        public static bool TryGetObstacleCount(World world, out int count)
        {
            count = 0;
            if (!TryGetObstacleEntity(world, out var entity) ||
                !world.EntityManager.HasBuffer<RvoObstaclePolygon>(entity))
            {
                return false;
            }

            count = world.EntityManager.GetBuffer<RvoObstaclePolygon>(entity).Length;
            return true;
        }

        /// <summary>
        /// 当前配置的障碍物**顶点**数（扁平顶点数组的长度）。返回 false 表示读不到。
        /// 本帧实际参与求解的顶点数看 <see cref="RvoStatus.ObstacleVertexCount"/>。
        /// </summary>
        public static bool TryGetObstacleVertexCount(World world, out int count)
        {
            count = 0;
            if (!TryGetObstacleEntity(world, out var entity) ||
                !world.EntityManager.HasBuffer<RvoObstacleVertex>(entity))
            {
                return false;
            }

            count = world.EntityManager.GetBuffer<RvoObstacleVertex>(entity).Length;
            return true;
        }

        private static bool HasObstacleBuffers(World world, Entity entity)
            => world.EntityManager.HasBuffer<RvoObstacleVertex>(entity) &&
               world.EntityManager.HasBuffer<RvoObstaclePolygon>(entity);

        private static void WriteObstacles(World world, Entity entity, NativeList<RvoObstacleVertex> vertices,
                                           NativeList<RvoObstaclePolygon> polygons)
        {
            var vertexBuffer = world.EntityManager.GetBuffer<RvoObstacleVertex>(entity);
            vertexBuffer.ResizeUninitialized(vertices.Length);
            for (var i = 0; i < vertices.Length; i++)
            {
                vertexBuffer[i] = vertices[i];
            }

            var polygonBuffer = world.EntityManager.GetBuffer<RvoObstaclePolygon>(entity);
            polygonBuffer.ResizeUninitialized(polygons.Length);
            for (var i = 0; i < polygons.Length; i++)
            {
                polygonBuffer[i] = polygons[i];
            }

            BumpObstacleVersion(world, entity);
        }

        private static void BumpObstacleVersion(World world, Entity entity)
        {
            var singleton = world.EntityManager.GetComponentData<RvoObstacleSingleton>(entity);
            singleton.Version++;
            world.EntityManager.SetComponentData(entity, singleton);
        }
    }
}
