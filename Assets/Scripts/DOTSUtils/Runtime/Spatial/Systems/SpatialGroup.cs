using DOTSUtils.Worlds;
using Unity.Entities;

namespace DOTSUtils.Spatial
{
    /// <summary>
    /// 空间索引（四叉树 / 八叉树）的模块组：承载建树 System，位于 <see cref="SimulationSystemGroup"/>。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class SpatialGroup : DotsModuleGroup
    {
    }
}
