using Unity.Entities;

namespace DOTSUtils.Spatial
{
    /// <summary>
    /// **空间索引的统一标记**：给实体打上它，就由 <c>Spatial</c> 模块把它维护进索引树里。
    /// 用**哪一棵**树由装配时的 <see cref="SpatialConfig.Index"/> 决定（四叉树或八叉树，单选）。
    ///
    /// 系统自动挂上去的维护位是 <see cref="SpatialMember"/>（当前生效索引的句柄，用户不要手写）。
    /// </summary>
    public struct SpatialIndexed : IComponentData
    {
    }
}
