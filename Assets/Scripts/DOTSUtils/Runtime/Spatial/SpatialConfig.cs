using Unity.Entities;
using Unity.Mathematics;

namespace DOTSUtils.Spatial
{

    /// <summary>
    /// 空间索引**维护哪一棵树**。单选：一个 World 里同一时刻只有一棵树。
    /// </summary>
    public enum SpatialIndexKind
    {
        /// <summary>
        /// 四叉树（只取位置的 xz）。
        /// </summary>
        Quadtree = 0,

        /// <summary>
        /// 八叉树（用完整 xyz）。
        /// </summary>
        Octree = 1,
    }

    /// <summary>
    /// 树的更新模式（两棵树共用同一份语义）。
    /// </summary>
    public enum SpatialUpdateMode : byte
    {
        /// <summary>
        /// 每帧清空重建。
        /// </summary>
        Bulk = 0,

        /// <summary>
        /// 只增量应用新增/移动/销毁。
        /// </summary>
        Incremental = 1,
    }

    /// <summary>
    /// 装载 Spatial 模块时要传的**全部参数**：用哪棵树 + 树的一份配置。
    ///
    /// 用法（在创建 World / 装配 Spatial 模块**之前**设置一次）：
    /// <code>
    /// SpatialSettings.Configure(SpatialIndexKind.Octree, SpatialTreeConfig.Default);
    /// var world = DotsWorldManager.CreateWorld("MyWorld", "Default");
    /// </code>
    /// 系统在 <c>OnCreate</c> 会把这份参数**快照**成该 World 的运行态（<see cref="SpatialSingleton"/>），
    /// 之后改这里的值不会影响已经建好的 World。
    ///
    /// 必须是 <b>struct（unmanaged）</b>：<c>SpatialBuildSystem</c> 是 <c>ISystem</c>，unmanaged 系统不能有引用类型字段。
    /// </summary>
    public struct SpatialConfig
    {
        /// <summary>
        /// 用哪棵树。
        /// </summary>
        public SpatialIndexKind Index;

        /// <summary>
        /// 树的配置。
        /// </summary>
        public SpatialTreeConfig Tree;

        /// <summary>
        /// 默认：四叉树 + <see cref="SpatialTreeConfig.Default"/>。
        /// </summary>
        public static SpatialConfig Default => new SpatialConfig(SpatialIndexKind.Quadtree, SpatialTreeConfig.Default);

        public SpatialConfig(SpatialIndexKind index, SpatialTreeConfig tree)
        {
            Index = index;
            Tree = tree;
        }
    }

    /// <summary>
    /// **树的一份配置** —— 四叉树与八叉树共用（它们形状参数的含义是一样的：范围 + 深度 + 叶容量 + 更新模式）。
    ///
    /// 注意 <see cref="Bounds"/> 是三维的 <see cref="AABB"/>：四叉树只用它的 **xz** 分量，
    /// 八叉树用完整三维。
    ///
    /// 改 <see cref="Bounds"/> / <see cref="MaxDepth"/> / <see cref="MaxLeafElements"/> 会触发重建，
    /// 旧句柄全部失效（系统会自动清掉维护位，下一帧重新入树）。
    /// </summary>
    public struct SpatialTreeConfig
    {
        /// <summary>
        /// 索引范围（三维，闭区间）。四叉树投影到 xz。
        /// </summary>
        public AABB Bounds;

        /// <summary>
        /// 最大深度，上限 <c>NativeSpatialTree.MaxSupportedDepth</c>（= 7）；超出会在建树时抛
        /// <c>ArgumentOutOfRangeException</c>。
        /// </summary>
        public int MaxDepth;

        public short MaxLeafElements;
        public SpatialUpdateMode Mode;

        /// <summary>
        /// 默认 true：首次出现越界元素时打一次 warning（不是每帧打）。
        /// </summary>
        public bool WarnOnOutOfBounds;

        public static SpatialTreeConfig Default => new SpatialTreeConfig
        {
            // 用三维 AABB；Extents 给「半长」⇒ 覆盖 ±2000 的立方体（xz 上同理）。
            Bounds = new AABB
            {
                Center = float3.zero,
                Extents = new float3(2000f),
            },
            MaxDepth = 6,
            MaxLeafElements = 16,
            Mode = SpatialUpdateMode.Incremental,
            WarnOnOutOfBounds = true,
        };
    }

    /// <summary>
    /// 运行态：由 <c>SpatialBuildSystem</c> 在 <c>OnCreate</c> 建立、<c>OnDestroy</c> 释放。
    /// 其他系统**只读**（走 <c>SpatialAccess.TryGetIndex</c>）；写只能由持有它的系统走写路径。
    /// 「用四叉树还是八叉树」是这棵树自己的模式（<c>Tree.Kind</c>）。
    /// </summary>
    public struct SpatialSingleton : IComponentData
    {
        /// <summary>
        /// 本 World 维护的树（装配时由 <see cref="SpatialConfig"/> 快照而来）。
        /// </summary>
        public NativeSpatialTree Tree;

        /// <summary>
        /// 每次重建 / 应用增量后自增；查询方可用它判断「这一帧的树变了没有」。
        /// </summary>
        public uint BuildVersion;

        /// <summary>
        /// 上一次更新实际应用的操作数（Bulk 模式下 = 被接受的元素数）。
        /// </summary>
        public int LastAppliedOps;
    }

}
