using Unity.Entities;

namespace DOTSUtils.Spatial
{
    /// <summary>
    /// 取「空间索引」的**唯一入口**。
    ///
    /// 它返回一个 <see cref="SpatialIndexReader"/> —— 拿到的就是**这个 World 实际维护的那棵树**
    /// （用哪棵由装配参数决定，运行态记录在 <see cref="SpatialSingleton"/> 的 <c>Tree.Kind</c> 里）；
    /// 查询方法自动按维度分派。
    ///
    /// 拿到的是**拷贝、且只读**（内核 <c>UnsafeList</c> 指向同一块内存）。
    /// 这里的实现每次都会新建一次查询，适合编辑器工具 / 偶发调用；系统内部每帧取请自行用
    /// <c>SystemState.GetEntityQuery</c> 缓存查询。
    /// </summary>
    public static class SpatialAccess
    {
        /// <summary>
        /// 取当前那棵树。没装 Spatial / 树还没建 → 返回 false。
        /// </summary>
        public static bool TryGetIndex(World world, out SpatialIndexReader index)
        {
            index = default;
            if (world == null || !world.IsCreated)
            {
                return false;
            }

            return TryGetIndex(world.EntityManager, out index);
        }

        /// <summary>
        /// 同上，但入参是 <see cref="EntityManager"/>（编辑器工具 / 非 System 代码更方便）。
        /// </summary>
        public static bool TryGetIndex(EntityManager entityManager, out SpatialIndexReader index)
        {
            index = default;
            if (!entityManager.World.IsCreated)
            {
                return false;
            }
            if (!TryGetSingletonData(entityManager, out var data))
            {
                return false;
            }
            if (!data.Tree.IsCreated)
            {
                return false;
            }

            index = new SpatialIndexReader(data.Tree);
            return true;
        }

        /// <summary>
        /// 索引的单例实体（读运行态 / 诊断用）。
        /// </summary>
        public static bool TryGetIndexSingleton(World world, out Entity singleton)
        {
            singleton = Entity.Null;
            if (world == null || !world.IsCreated)
            {
                return false;
            }

            var entityManager = world.EntityManager;
            var query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<SpatialSingleton>());
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
        /// 索引的运行态（装配时快照的 <see cref="SpatialSingleton"/> 的 <c>Tree.Kind</c> / <c>BuildVersion</c> / <c>LastAppliedOps</c>）。
        /// </summary>
        public static bool TryGetSingletonData(World world, out SpatialSingleton data)
        {
            data = default;
            if (world == null || !world.IsCreated)
            {
                return false;
            }

            return TryGetSingletonData(world.EntityManager, out data);
        }

        private static bool TryGetSingletonData(EntityManager entityManager, out SpatialSingleton data)
            => TryGetSingleton(entityManager, out data);

        static bool TryGetSingleton<T>(EntityManager entityManager, out T value)
            where T : unmanaged, IComponentData
        {
            value = default;

            var query = entityManager.CreateEntityQuery(ComponentType.ReadOnly<T>());
            try
            {
                if (query.IsEmptyIgnoreFilter)
                {
                    return false;
                }
                value = entityManager.GetComponentData<T>(query.GetSingletonEntity());
                return true;
            }
            finally
            {
                query.Dispose();
            }
        }
    }
}
