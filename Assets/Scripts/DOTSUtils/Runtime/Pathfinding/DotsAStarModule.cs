using System.Collections.Generic;
using DOTSUtils.Worlds;

namespace DOTSUtils.Pathfinding
{
    /// <summary>
    /// A* 寻路模块。继承 <see cref="DotsModule"/> 即被自动发现（见 <c>DotsWorldRegistry</c>）。
    ///
    /// 不依赖其它模块；网格由 <see cref="AStarSettings"/> 提供，未配置时用 <see cref="AStarConfig.Default"/>
    /// 并全部格子可走。
    /// </summary>
    public sealed class DotsAStarModule : DotsModule
    {
        public override string Name => "AStar";

        /// <summary>顺序：Spatial(200) → Visibility(220) → AStar(240) → Rvo(250)。</summary>
        public override int SortKey => 240;

        public override IReadOnlyList<string> Templates => new[] { "Default" };

        public override IReadOnlyList<DotsModuleGroupDescriptor> Groups => new[]
        {
            new DotsModuleGroupDescriptor(typeof(AStarGroup), typeof(AStarSolveSystem)),
        };
    }
}
