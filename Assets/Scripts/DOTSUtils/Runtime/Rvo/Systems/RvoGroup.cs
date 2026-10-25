using DOTSUtils.Spatial;
using DOTSUtils.Worlds;
using DOTSUtils.Visibility;
using Unity.Entities;

namespace DOTSUtils.Rvo
{
    /// <summary>
    /// RVO/ORCA 避障的模块组：求解在「业务把 <c>PrefVelocity</c> 写好之后、位置被消费之前」运行，
    /// 单独成组便于用 <c>[UpdateBefore]/[UpdateAfter]</c> 与其它模块对齐。
    ///
    /// <b>跨模块时序契约</b>：本组必须在 <see cref="SpatialGroup"/> 之后运行 ——
    /// <see cref="RvoSolveSystem"/> 经 <c>SpatialIndexReader</c> 读空间索引树，而树由
    /// <c>SpatialBuildSystem</c> 在 <see cref="SpatialGroup"/> 内建好 / 更新。
    /// 未装配 Spatial 模块时，Unity 会忽略指向未创建系统的排序约束。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(SpatialGroup))]
    // 可见性必须在本组之前标好：本组按它决定跳过哪些 agent 的求解。
    [UpdateAfter(typeof(VisibilityGroup))]
    public partial class RvoGroup : DotsModuleGroup
    {
    }
}
