using DOTSUtils.Worlds;
using Unity.Entities;

namespace DOTSUtils.Pathfinding
{
    /// <summary>
    /// A* 寻路的模块组。独立成组，便于用 <c>[UpdateBefore]</c> / <c>[UpdateAfter]</c> 与其它模块对齐。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class AStarGroup : DotsModuleGroup
    {
    }
}
