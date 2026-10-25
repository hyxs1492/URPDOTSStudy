using Unity.Entities;
using Unity.Mathematics;

namespace DOTSUtils.Spatial
{
    /// <summary>
    /// **索引成员**：实体的索引句柄（系统维护，<b>用户不要手写</b>）。
    ///
    /// 句柄用**中立的 (Slot, Version) 对**表示，指向**当前生效那棵树**里的元素
    /// （切换 <see cref="SpatialConfig.Index"/> 会重建树并清掉它，下一帧重新入树）。
    /// </summary>
    public struct SpatialMember : IComponentData
    {
        /// <summary>
        /// 元素在索引池里的槽位（<c>&lt; 0</c> = 无效）。
        /// </summary>
        public int Slot;

        /// <summary>
        /// 槽位版本：用来识别「陈旧句柄」（槽位被复用时它就变了）。
        /// </summary>
        public int Version;

        /// <summary>
        /// 上次入树 / 更新时的位置，用来判断「这一帧动了没有」。
        /// </summary>
        public float3 IndexedPos;

        public bool IsValid => Slot >= 0;

        public static SpatialMember Invalid => new SpatialMember
        {
            Slot = -1,
            Version = 0,
        };
    }

    /// <summary>
    /// 实体销毁后仍留下的清理位（<see cref="ICleanupComponentData"/> 语义）：系统据此从树里摘元素，
    /// 摘完即移除本组件。句柄不随位置更新改变，只需写一次。
    /// </summary>
    public struct SpatialMemberDestroyed : ICleanupComponentData
    {
        public int Slot;
        public int Version;
    }

}
