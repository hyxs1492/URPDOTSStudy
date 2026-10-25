using UnityEngine;

namespace DOTSUtils.Spatial
{
    /// <summary>
    /// Spatial 模块的**装配参数入口**：装载模块（创建 World）之前设置一次。
    ///
    /// 系统（<see cref="SpatialBuildSystem"/>）在 <c>OnCreate</c> 会把它<b>快照</b>成该 World 的运行态，
    /// 之后改这里不会影响已经建好的 World。
    ///
    /// 用法：
    /// <code>
    /// // 八叉树 + 自定义树参数：
    /// var tree = SpatialTreeConfig.Default;
    /// tree.Bounds = new AABB { Center = float3.zero, Extents = new float3(500f) };
    /// SpatialSettings.Configure(SpatialIndexKind.Octree, tree);
    ///
    /// var world = DotsWorldManager.CreateWorld("MyWorld", "Default");
    ///
    /// // 用默认（四叉树 + SpatialTreeConfig.Default）时什么都不用调：
    /// var world2 = DotsWorldManager.CreateWorld("Other", "Default");
    /// </code>
    /// </summary>
    public static class SpatialSettings
    {
        private static SpatialConfig s_Pending = SpatialConfig.Default;

        /// <summary>
        /// 待装配的参数（<see cref="SpatialBuildSystem"/> 在 <c>OnCreate</c> 读走一份拷贝）。
        /// </summary>
        public static SpatialConfig Pending
        {
            get => s_Pending;
            set => s_Pending = value;
        }

        /// <summary>
        /// 选哪棵树 + 树的一份配置。
        /// </summary>
        public static void Configure(SpatialIndexKind index, SpatialTreeConfig tree)
        {
            s_Pending.Index = index;
            s_Pending.Tree = tree;
        }

        /// <summary>
        /// 只选哪棵树，树参数用 <see cref="SpatialTreeConfig.Default"/>。
        /// </summary>
        public static void Configure(SpatialIndexKind index) => Configure(index, SpatialTreeConfig.Default);

        /// <summary>
        /// 恢复默认。
        /// </summary>
        public static void Reset() => s_Pending = SpatialConfig.Default;

        /// <summary>
        /// 进入 Play 时复位静态参数（本工程关闭了域重载与场景重载，静态字段会跨 Play 会话保留）。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlayModeEntry() => Reset();
    }
}
