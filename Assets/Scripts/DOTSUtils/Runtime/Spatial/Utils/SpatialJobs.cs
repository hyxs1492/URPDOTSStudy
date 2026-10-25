using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace DOTSUtils.Spatial
{
    /// <summary>
    /// 树的一次写操作（供批量顺序应用）—— 四叉树与八叉树**共用**。
    /// </summary>
    public struct SpatialOp
    {
        public enum Kind : byte
        {
            Insert = 0,
            Update = 1,
            Remove = 2,
        }

        public Kind Type;

        /// <summary>
        /// <see cref="Kind.Insert"/> 用。
        /// </summary>
        public SpatialElement Element;

        /// <summary>
        /// <see cref="Kind.Update"/> / <see cref="Kind.Remove"/> 用。
        /// </summary>
        public SpatialElementRef Reference;

        /// <summary>
        /// <see cref="Kind.Update"/> 用。
        /// </summary>
        public float3 NewPosition;
    }

    /// <summary>
    /// 收集「带 <see cref="SpatialIndexed"/> + <c>LocalTransform</c>」的实体（Burst）—— 两棵树共用。
    /// </summary>
    [BurstCompile]
    [WithAll(typeof(SpatialIndexed))]
    public partial struct CollectSpatialElementsJob : IJobEntity
    {
        public NativeList<SpatialElement> Elements;

        private void Execute(Entity entity, in LocalTransform transform)
            => Elements.Add(new SpatialElement
            {
                Pos = transform.Position,
                Entity = entity,
            });
    }

    /// <summary>
    /// 收集「这一帧位置变了的成员」（Burst）：只读 <c>LocalTransform</c>，输出一批 <c>Update</c> op，
    /// 顺手把 <see cref="SpatialMember.IndexedPos"/> 更新掉（它就是「上次入树的位置」）。两棵树共用。
    ///
    /// 回写 <c>IndexedPos</c> 发生在<b>树应用之前</b>：「越界导致 <c>Update</c> 失败」的元素也会被
    /// 标记为已同步（下一帧不再重试）。
    /// </summary>
    [BurstCompile]
    [WithAll(typeof(SpatialIndexed))]
    public partial struct CollectSpatialMovesJob : IJobEntity
    {
        public NativeList<SpatialOp> Ops;

        private void Execute(in LocalTransform transform, ref SpatialMember member)
        {
            var position = transform.Position;
            if (math.all(position == member.IndexedPos))
            {
                return;
            }

            Ops.Add(new SpatialOp
            {
                Type = SpatialOp.Kind.Update,
                Reference = new SpatialElementRef
                {
                    Slot = member.Slot,
                    Version = member.Version,
                },
                NewPosition = position,
            });

            member.IndexedPos = position;
        }
    }
}
