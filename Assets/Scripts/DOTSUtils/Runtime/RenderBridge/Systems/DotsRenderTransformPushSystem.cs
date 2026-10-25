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
    /// 每帧把实体 LocalTransform 回写到 GameObject 的 Transform（只回写父物体）。
    ///
    /// 与 <see cref="DotsRenderTransformSyncSystem"/>（帧初的 GameObject → 实体）配对：
    /// 帧初读输入，帧末写结果；同一帧内实体是权威。
    ///
    /// 只回写 position / rotation，**不碰 localScale**（缩放运行时不同步）。
    ///
    /// 与正向同步同一批对象：只处理登记表的父物体；勾了 <c>TransformDrivenByEntity</c> 的对象
    /// 既不读也不回写。
    ///
    /// <b>不可见（在相机视锥外）的对象跳过推送</b>（见 <see cref="VisibilityCulledTag"/>）。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(RenderBridgeOutputGroup))]
    public partial class DotsRenderTransformPushSystem : SystemBase
    {
        private DotsRenderConversionSystem m_Conversion;
        private DotsRenderTransformSyncSystem m_Sync;
        private ComponentLookup<LocalTransform> m_LocalTransforms;

        protected override void OnCreate()
        {
            m_Conversion = World.GetOrCreateSystemManaged<DotsRenderConversionSystem>();
            m_Sync = World.GetOrCreateSystemManaged<DotsRenderTransformSyncSystem>();
            m_LocalTransforms = GetComponentLookup<LocalTransform>(isReadOnly: true);
        }

        /// <summary>
        /// 本帧回写 job 的句柄。
        /// </summary>
        public JobHandle PendingJob => Dependency;

        protected override void OnUpdate()
        {
            var registry = m_Conversion.Registry;

            // 需要时仍可显式调用：
            //registry.PruneDestroyedTransforms();

            if (registry.Count == 0)
            {
                return;
            }

            m_LocalTransforms.Update(this);

            // VisibilityCulledTag 是**可选**类型：这里每帧用 SystemAPI.GetComponentLookup 现取，
            // 不在 OnCreate 缓存（SystemAPI 版本会自动登记依赖、保证本帧可用）。
            var job = new PushTransformsJob
            {
                LocalTransforms = m_LocalTransforms,
                Entities = registry.Entities,
                Culled = SystemAPI.GetComponentLookup<VisibilityCulledTag>(isReadOnly: true),
            };

            // 不带 ReadOnly：这个 job 要写 GameObject 的 Transform。
            // 注意 Schedule(TransformAccessArray, JobHandle) 没有 batchCount 重载（只有 ScheduleReadOnly 有）。
            Dependency = job.Schedule(registry.Transforms, JobHandle.CombineDependencies(m_Sync.PendingJob, Dependency));

            //Dependency.Complete();
        }

        [BurstCompile]
        partial struct PushTransformsJob : IJobParallelForTransform
        {
            [ReadOnly] public ComponentLookup<LocalTransform> LocalTransforms;
            [ReadOnly] public NativeArray<Entity> Entities;

            /// <summary>被裁剪（不可见）就不推。</summary>
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
                    return;                // 被裁剪（视口外）就不推
                }

                var local = LocalTransforms[entity];
                var position = transform.position;
                var rotation = transform.rotation;

                var needPosition = !position.Equals(local.Position);
                var needRotation = !rotation.Equals(local.Rotation);

                // 位置与旋转都没变就直接返回，不做任何写入。
                if (!needPosition && !needRotation)
                {
                    return;
                }

                // 写 position / rotation 会把 localScale 重置为 1：写之前先存、写之后补回来。
                var scale = transform.localScale;
                if (needPosition)
                {
                    transform.position = local.Position;
                }
                if (needRotation)
                {
                    transform.rotation = local.Rotation;
                }
                transform.localScale = scale;
            }
        }
    }
}
