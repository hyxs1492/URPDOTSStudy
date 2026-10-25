using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace DOTSUtils.Pathfinding
{
    /// <summary>
    /// 取 A* 网格与求解路径的入口。
    ///
    /// <see cref="TryGetGrid"/> 拿到的是**拷贝、且只读**（内核 <see cref="UnsafeList{T}"/> 指向同一块内存）。
    /// 这里的实现每次新建一次查询，适合编辑器工具与偶发调用；系统内部请自行缓存查询。
    /// </summary>
    public static class AStarAccess
    {
        /// <summary>取当前网格；没装 A* 模块或网格还没建 → false。</summary>
        public static bool TryGetGrid(World world, out AStarGrid grid)
        {
            grid = default;
            if (world == null || !world.IsCreated)
            {
                return false;
            }
            return TryGetGrid(world.EntityManager, out grid);
        }

        /// <summary>同上，入参是 <see cref="EntityManager"/>。</summary>
        public static bool TryGetGrid(EntityManager entityManager, out AStarGrid grid)
        {
            grid = default;
            if (!entityManager.World.IsCreated)
            {
                return false;
            }
            if (!TryGetSingletonData(entityManager, out var data) || !data.Grid.IsCreated)
            {
                return false;
            }
            grid = data.Grid;
            return true;
        }

        /// <summary>取网格单例的运行态（网格 + 参数 + 建表版本）。</summary>
        public static bool TryGetSingletonData(World world, out AStarSingleton data)
        {
            data = default;
            if (world == null || !world.IsCreated)
            {
                return false;
            }
            return TryGetSingletonData(world.EntityManager, out data);
        }

        /// <summary>网格单例实体（诊断用）。</summary>
        public static bool TryGetSingletonEntity(World world, out Entity singleton)
        {
            singleton = Entity.Null;
            if (world == null || !world.IsCreated)
            {
                return false;
            }

            var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<AStarSingleton>());
            try
            {
                if (query.IsEmptyIgnoreFilter)
                {
                    return false;
                }
                singleton = query.GetSingletonEntity();
                return true;
            }
            finally
            {
                query.Dispose();
            }
        }

        /// <summary>
        /// 同步求一条路径，结果追加到 <paramref name="path"/>（调用方负责清空 / 释放）。
        /// 用网格单例里的参数。
        /// </summary>
        public static AStarStatus FindPath(World world, float3 start, float3 end, ref UnsafeList<float3> path)
        {
            if (world == null || !world.IsCreated)
            {
                return AStarStatus.NoGrid;
            }
            return FindPath(world.EntityManager, start, end, ref path);
        }

        /// <summary>同步求一条路径，结果写进 <paramref name="path"/>（会先清空）。</summary>
        public static AStarStatus FindPath(World world, float3 start, float3 end, NativeList<float3> path)
        {
            path.Clear();
            if (!TryGetGrid(world, out var grid) || !TryGetSingletonData(world, out var data))
            {
                return AStarStatus.NoGrid;
            }

            var buffer = new UnsafeList<float3>(math.max(16, grid.CellCount), Allocator.Temp);
            var status = AStarPathfinder.FindPath(grid, start, end, data.Config, ref buffer);
            for (var i = 0; i < buffer.Length; i++)
            {
                path.Add(buffer[i]);
            }
            buffer.Dispose();
            return status;
        }

        /// <summary>同步求一条路径，结果追加到 <paramref name="path"/>。</summary>
        public static AStarStatus FindPath(EntityManager entityManager, float3 start, float3 end,
                                          ref UnsafeList<float3> path)
        {
            if (!TryGetGrid(entityManager, out var grid) ||
                !TryGetSingletonData(entityManager, out var data))
            {
                return AStarStatus.NoGrid;
            }
            return AStarPathfinder.FindPath(grid, start, end, data.Config, ref path);
        }

        /// <summary>
        /// 给实体挂上 / 更新寻路请求（缺 <see cref="PathStatus"/> 与 <see cref="PathWaypoint"/> 缓冲时补上），
        /// 并把请求版本加一。
        /// </summary>
        public static void RequestPath(EntityManager entityManager, Entity entity, float3 start, float3 end)
        {
            if (!entityManager.Exists(entity))
            {
                return;
            }
            if (!entityManager.HasComponent<PathStatus>(entity))
            {
                entityManager.AddComponent<PathStatus>(entity);
            }
            if (!entityManager.HasBuffer<PathWaypoint>(entity))
            {
                entityManager.AddBuffer<PathWaypoint>(entity);
            }
            if (!entityManager.HasComponent<PathRequest>(entity))
            {
                entityManager.AddComponent<PathRequest>(entity);
            }

            var version = entityManager.GetComponentData<PathRequest>(entity).Version + 1;
            entityManager.SetComponentData(entity, new PathRequest
            {
                Start = start,
                End = end,
                Version = version,
            });
        }

        private static bool TryGetSingletonData(EntityManager entityManager, out AStarSingleton data)
        {
            data = default;
            var query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<AStarSingleton>());
            try
            {
                if (query.IsEmptyIgnoreFilter)
                {
                    return false;
                }
                data = entityManager.GetComponentData<AStarSingleton>(query.GetSingletonEntity());
                return true;
            }
            finally
            {
                query.Dispose();
            }
        }
    }
}
