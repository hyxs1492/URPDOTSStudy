using System.Collections.Generic;
using DOTSUtils.Worlds;

namespace DOTSUtils.Rvo
{
    /// <summary>
    /// RVO/ORCA 避障模块。继承 <see cref="DotsModule"/> 即被自动发现。
    ///
    /// 不想进默认 World 就把 <see cref="Templates"/> 清空，改为按需 <c>AddModule(world, "Rvo")</c>。
    /// </summary>
    public sealed class DotsRvoModule : DotsModule
    {
        public override string Name => "Rvo";

        /// <summary>
        /// 装配顺序键 250（RenderBridge 0 → Benchmark 100 → Spatial 200 → Visibility 220 → Rvo 250）。
        /// 必须排在 **Spatial 之后**：它的邻居索引就是 Spatial 维护的那棵树，排在后面读到的才是本帧的位置。
        /// </summary>
        public override int SortKey => 250;

        /// <summary>
        /// Rvo 的邻居索引**不自建** —— 必须有 <c>Spatial</c>（四叉树或八叉树）提供，缺失时
        /// <c>DotsWorldManager.AddModule</c> 会拒绝装配。
        ///
        /// 另外**必须带上 <c>Visibility</c>**：<see cref="RvoGroup"/> 上声明了
        /// <c>[UpdateAfter(typeof(VisibilityGroup))]</c>，缺少该模块时这条排序约束指向一个不存在的组，
        /// 每次装配都会打一条警告。
        /// </summary>
        public override IReadOnlyList<string> Requires => new[] { "Spatial", "Visibility" };

        public override IReadOnlyList<string> Templates => new[] { "Default" };

        public override IReadOnlyList<DotsModuleGroupDescriptor> Groups => new[]
        {
            new DotsModuleGroupDescriptor(typeof(RvoGroup), typeof(RvoSolveSystem)),
        };
    }
}
