using DOTSUtils.Worlds;
using Unity.Entities;

namespace DOTSUtils.Visibility
{
    /// <summary>
    /// 可见性模块的更新组。
    /// <b>顺序前提</b>：任何消费 <see cref="VisibilityCulledTag"/> 的系统都必须声明
    /// <c>[UpdateAfter(typeof(VisibilityGroup))]</c>。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class VisibilityGroup : DotsModuleGroup
    {
    }
}
