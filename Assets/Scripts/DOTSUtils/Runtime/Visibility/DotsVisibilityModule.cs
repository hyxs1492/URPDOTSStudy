using System.Collections.Generic;
using DOTSUtils.Worlds;

namespace DOTSUtils.Visibility
{
    /// <summary>
    /// 可见性模块：把「谁在相机视锥里」做成一个**独立能力**，供别的系统当过滤条件用
    ///（Rvo 拿它跳过求解、RenderBridge 拿它跳过变换推送）。继承 <see cref="DotsModule"/> 即被自动发现。
    ///
    /// <b>不声明 <c>Templates</c></b>：不会自动进入任何 World。想用就显式列进模板，
    /// 或让别的模块用 <c>Requires</c> 把它拖进来。
    /// </summary>
    public sealed class DotsVisibilityModule : DotsModule
    {
        public override string Name => "Visibility";

        /// <summary>
        /// 装配顺序键 220（排在 Spatial 200 之后、Rvo 250 之前）。
        /// </summary>
        public override int SortKey => 220;

        public override IReadOnlyList<DotsModuleGroupDescriptor> Groups => new[]
        {
            new DotsModuleGroupDescriptor(typeof(VisibilityGroup), typeof(VisibilitySystem)),
        };
    }
}
