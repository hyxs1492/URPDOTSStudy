using System;
using Unity.Entities;
using Unity.Mathematics;

namespace DOTSUtils.Spatial
{
    /// <summary>
    /// **空间索引的元素**：位置 + 可回查的实体 —— 四叉树和八叉树**共用这一份类型**。
    ///
    /// 四叉树只用 <c>Pos</c> 的 <b>xz</b>（y 被忽略）、八叉树用完整 xyz。
    ///
    /// <see cref="distancesq"/> 只在圆形/球形查询且要求算距离时由查询写入，其余时候是默认值。
    /// </summary>
    public struct SpatialElement : IBufferElementData
    {
        public float3 Pos;
        public Entity Entity;
        public float distancesq;
    }

    /// <summary>
    /// **元素在树里的稳定句柄**（8 字节）—— 两棵树也共用这一份。
    ///
    /// 树的任何写操作（移动、分裂、合并）都<b>不会</b>改变已返回的句柄 —— 元素在池里的槽位不动，
    /// 分裂/合并只重挂链表。只有当元素被移除、其槽位又被后续插入复用时 <see cref="Version"/> 才变化，
    /// 旧句柄随即失效（<c>Update</c>/<c>Remove</c> 返回 false）。
    /// </summary>
    public struct SpatialElementRef : IEquatable<SpatialElementRef>
    {
        public int Slot;
        public int Version;

        /// <summary>
        /// 句柄是否「格式上有效」。是否真的还指向某个元素，要用树的 <c>TryGet</c> 校验。
        /// </summary>
        public bool IsValid => Slot >= 0;

        public static SpatialElementRef Invalid => new SpatialElementRef
        {
            Slot = -1,
            Version = 0,
        };

        public bool Equals(SpatialElementRef other) => Slot == other.Slot && Version == other.Version;
        public override bool Equals(object obj) => obj is SpatialElementRef other && Equals(other);
        public override int GetHashCode() => (Slot * 397) ^ Version;
        public override string ToString() => $"SpatialElementRef(slot={Slot}, version={Version})";
    }
}
