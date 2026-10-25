using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace DOTSUtils.Visibility
{
    /// <summary>
    /// 逐帧判断「谁在相机视锥里」，把结果写进 <see cref="VisibilityCulledTag"/>（启用 = 被裁剪）。
    ///
    /// <b>本模块只负责标记，不负责行为。</b>
    ///
    /// <b>只处理显式挂了 <see cref="VisibilityCulledTag"/> 的实体</b>（<c>EnabledRefRW</c> 的要求）。
    /// 没挂的实体被当作始终可见。
    ///
    /// 拿不到相机时（或配置里关掉）退化成「全部可见」。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(VisibilityGroup))]
    public partial class VisibilitySystem : SystemBase
    {
        /// <summary>相机视锥的世界空间平面（法线朝内）。透视相机 6 个。</summary>
        private readonly float4[] m_Planes = new float4[6];

        /// <summary>本帧实际用到的平面数量。</summary>
        private int m_PlaneCount;

        protected override void OnUpdate()
        {
            var config = VisibilitySettings.Pending;

            // 关掉裁剪、或本帧拿不到视锥：全部恢复可见。
            if (!config.Enabled || !TryBuildFrustum())
            {
                new SetCulledJob { Culled = false }.ScheduleParallel();
                return;
            }

            // 平面逐个当字段传给 job（透视相机的平面数固定是 6），不塞进 FixedList / NativeArray 容器。
            new VisibilityJob
            {
                P0 = m_Planes[0],
                P1 = m_Planes[1],
                P2 = m_Planes[2],
                P3 = m_Planes[3],
                P4 = m_Planes[4],
                P5 = m_Planes[5],
                Margin = math.max(0f, config.Margin),
            }.ScheduleParallel();
        }

        /// <summary>从相机提取世界空间视锥平面；拿不到相机时返回 <c>false</c>。</summary>
        private bool TryBuildFrustum()
        {
            var provider = VisibilitySettings.CameraProvider;
            var camera = provider != null ? provider() : null;
            if (camera == null)
            {
                return false;
            }

            // 用 GeometryUtility.CalculateFrustumPlanes：它直接给出世界空间、法线朝内的平面，
            // 平面方程是 dot(normal, p) + distance = 0（透视相机 6 个）。
            var planes = GeometryUtility.CalculateFrustumPlanes(camera);
            var count = math.min(planes.Length, m_Planes.Length);
            for (var i = 0; i < count; i++)
            {
                var plane = planes[i];
                m_Planes[i] = new float4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
            }
            m_PlaneCount = count;
            return count > 0;
        }

        /// <summary>
        /// 判定：位置落在 6 个平面朝内的那一侧就算可见；任一平面判出「在外」即不可见。
        /// </summary>
        [BurstCompile]
        private partial struct VisibilityJob : IJobEntity
        {
            // 视锥的 6 个平面（世界空间，法线朝内），逐个当字段传。
            public float4 P0;
            public float4 P1;
            public float4 P2;
            public float4 P3;
            public float4 P4;
            public float4 P5;

            /// <summary>判定余量（世界单位）：贴着视口边缘的实体提前算可见。</summary>
            public float Margin;

            private void Execute(EnabledRefRW<VisibilityCulledTag> culledRef, in LocalTransform transform)
            {
                var position = transform.Position;
                // 法线朝内：有符号距离 + Margin < 0 表示这一点在该平面外侧。
                var outside = Outside(P0, position)
                           || Outside(P1, position)
                           || Outside(P2, position)
                           || Outside(P3, position)
                           || Outside(P4, position)
                           || Outside(P5, position);

                // 启用 = 被裁剪
                culledRef.ValueRW = outside;
            }

            private bool Outside(float4 plane, float3 position)
                => math.dot(plane.xyz, position) + plane.w + Margin < 0f;
        }

        /// <summary>不裁剪（或拿不到视锥）时用：把所有挂了标记的实体恢复成「可见」。</summary>
        [BurstCompile]
        private partial struct SetCulledJob : IJobEntity
        {
            public bool Culled;

            private void Execute(EnabledRefRW<VisibilityCulledTag> culledRef) => culledRef.ValueRW = Culled;
        }
    }
}
