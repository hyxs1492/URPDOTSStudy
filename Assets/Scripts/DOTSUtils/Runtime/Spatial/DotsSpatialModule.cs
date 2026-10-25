using System.Collections.Generic;
using DOTSUtils.Worlds;

namespace DOTSUtils.Spatial
{
    /// <summary>
    /// 空间索引模块。继承 <see cref="DotsModule"/> 即被自动发现。四叉树与八叉树的建树系统共用同一个组。
    /// </summary>
    public sealed class DotsSpatialModule : DotsModule
    {
        public override string Name => "Spatial";

        /// <summary>
        /// 排在 RenderBridge（0）与 Benchmark（100）之后。
        /// </summary>
        public override int SortKey => 200;

        public override IReadOnlyList<string> Templates => new[] { "Default" };

        public override IReadOnlyList<DotsModuleGroupDescriptor> Groups => new[]
        {
            new DotsModuleGroupDescriptor(typeof(SpatialGroup),
                typeof(SpatialBuildSystem)),
        };
    }
}
