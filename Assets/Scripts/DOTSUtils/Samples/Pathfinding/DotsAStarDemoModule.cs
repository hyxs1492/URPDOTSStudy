using System.Collections.Generic;
using DOTSUtils.Pathfinding;
using DOTSUtils.Rvo;
using DOTSUtils.Worlds;
using Unity.Entities;

namespace DOTSUtils.Samples.Pathfinding
{
    /// <summary>
    /// A* 演示专属的模块：把「跟路径」系统装进一个专门的组。
    ///
    /// **不声明 <see cref="Templates"/>** —— 不会自动进入任何 World，只由
    /// <see cref="AStarDemoBootstrap"/> 在 Awake 里用 <c>DotsWorldManager.AddModule</c> 显式装配。
    /// 装配它会连带装上依赖的 RenderBridge（生成渲染实体）、AStar（算路径）与
    /// Rvo（避障，又依赖 Spatial + Visibility）。
    /// </summary>
    public sealed class DotsAStarDemoModule : DotsModule
    {
        public override string Name => "AStarDemo";

        /// <summary>装配顺序键 300：排在 RenderBridge(0) → Spatial(200) → Visibility(220) → AStar(240) → Rvo(250) 之后。</summary>
        public override int SortKey => 300;

        /// <summary>
        /// 依赖：RenderBridge 提供 <c>DotsRenderConversionSystem</c>（纯 ECS 生成渲染实体用到），
        /// AStar 提供网格与请求组件，Rvo 提供避障（它会自动带上 Spatial 与 Visibility）。
        /// </summary>
        public override IReadOnlyList<string> Requires => new[] { "RenderBridge", "AStar", "Rvo" };

        public override IReadOnlyList<DotsModuleGroupDescriptor> Groups => new[]
        {
            new DotsModuleGroupDescriptor(typeof(AStarDemoGroup), typeof(AStarDemoFollowPathSystem)),
        };
    }

    /// <summary>
    /// A* 演示的模块组：在 A* 求解（<see cref="AStarGroup"/>）与 Rvo 求解（<see cref="RvoGroup"/>）**之后**运行。
    /// 这样读到的路径与速度都是本帧刚算出来的，而这里写进 <c>RvoAgent.PrefVelocity</c> 的期望速度
    /// 供下一帧的 Rvo 求解使用。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AStarGroup))]
    [UpdateAfter(typeof(RvoGroup))]
    public partial class AStarDemoGroup : DotsModuleGroup
    {
    }
}
