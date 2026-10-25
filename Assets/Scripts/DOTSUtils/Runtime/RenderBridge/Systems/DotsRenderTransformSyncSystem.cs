using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;
using UnityEngine.Jobs;
using DOTSUtils.Visibility;
using DOTSUtils.Worlds;

namespace DOTSUtils.RenderBridge
{
    /// <summary>
    /// 每帧把 GameObject 的世界**位置与旋转**写进实体 LocalTransform。
    /// 实体没有 ECS 父级，这里写入的就是世界变换。
    ///
    /// <b>Scale 不在这里同步</b>：只在转换时按 <c>lossyScale</c> 写一次（缩放必须等比）。
    ///
    /// <b>视口外的对象同样跳过</b>（与回写方向用同一判据，见 <see cref="VisibilityCulledTag"/>）：
    /// 两个方向必须用同一个判据。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(RenderBridgeInputGroup))]
    public partial class DotsRenderTransformSyncSystem : SystemBase
    {
        private DotsRenderConversionSystem m_Conversion;
        private ComponentLookup<LocalTransform> m_LocalTransforms;

        protected override void OnCreate()
        {
            m_Conversion = World.GetOrCreateSystemManaged<DotsRenderConversionSystem>();

            m_LocalTransforms = GetComponentLookup<LocalTransform>(isReadOnly: false);
        }

        /// <summary>
        /// 本帧同步 job 的句柄。
        /// </summary>
        public JobHandle PendingJob => Dependency;

        protected override void OnUpdate()
        {
            var registry = m_Conversion.Registry;

            // 需要时仍可显式调用：
            //registry.PruneDestroyedTransforms();

            if (registry.Count == 0)
            {
                //Dependency.Complete();
                return;
            }

            m_LocalTransforms.Update(this);

            var job = new SyncTransformsJob
            {
                LocalTransforms = m_LocalTransforms,
                Entities = registry.Entities,
                Culled = SystemAPI.GetComponentLookup<VisibilityCulledTag>(isReadOnly: true),
            };

            // ReadOnly 只作用于 Transform 数据（这里只读 Transform，写 ECS 组件）。
            Dependency = job.ScheduleReadOnly(registry.Transforms, 64, Dependency);
        }

        [BurstCompile]
        partial struct SyncTransformsJob : IJobParallelForTransform
        {
            [NativeDisableParallelForRestriction]
            public ComponentLookup<LocalTransform> LocalTransforms;

            [ReadOnly] public NativeArray<Entity> Entities;

            /// <summary>
            /// 被裁剪（视口外）就**不把 GameObject 的值写进实体**；
            /// 与 <c>PushTransformsJob</c> 用同一个判据。
            /// </summary>
            [ReadOnly] public ComponentLookup<VisibilityCulledTag> Culled;

            public void Execute(int index, TransformAccess transform)
            {
                // 失效兜底：transform.isValid 为 false 表示对象已销毁。
                if (!transform.isValid)
                {
                    return;
                }

                var entity = Entities[index];
                if (entity == Entity.Null)
                {
                    return;
                }
                if (!LocalTransforms.HasComponent(entity))
                {
                    return;
                }
                if (VisibilityAccess.IsCulled(Culled, entity))
                {
                    // 视口外：GO 的值可能已经过时，不能拿它覆盖实体。
                    return;
                }

                // 只写位置与旋转，**不动 Scale**：这里逐个字段赋值，保留转换时写下的 Scale。
                // 与回写方向（PushTransformsJob）同一口径：先比较，只在值真的变了才写。
                var local = LocalTransforms[entity];
                var position = transform.position;
                var rotation = transform.rotation;
                if (position.Equals(local.Position) && rotation.Equals(local.Rotation))
                {
                    return;
                }

                local.Position = position;
                local.Rotation = rotation;
                LocalTransforms[entity] = local;
            }
        }
    }
}
