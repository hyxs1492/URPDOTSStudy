using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace DOTSUtils.RenderBridge
{
    /// <summary>
    /// 把传统 <see cref="LODGroup"/> 转成 Entities Graphics 的组实体（<see cref="MeshLODGroupComponent"/>）
    /// 以及每层实体（<see cref="MeshLODComponent"/>），并严格按 GameObject 层级挂在父实体之下：
    /// 父实体 → LOD 组实体 → 每层实体。每帧的 LOD 计算与内部组件补齐由 Entities Graphics 自带的系统负责，
    /// 这里只负责建立数据与层级。
    ///
    /// 距离值的编码以官方烘焙器 <c>LODGroupBaking.cs</c> 为准：
    /// <list type="bullet">
    /// <item><c>LODDistances0.x/.y/.z/.w</c> 依次对应 LOD 0/1/2/3，<c>LODDistances1</c> 对应 LOD 4/5/6/7（顺序，非反序）。</item>
    /// <item>距离值为 <c>GetWorldSpaceScale(transform) * lodGroup.size / screenRelativeTransitionHeight</c>，
    /// 不使用 <c>CalculateLODSwitchDistance</c>。</item>
    /// </list>
    /// </summary>
    public static class DotsRenderLodSupport
    {
        public static bool IsLodGroup(GameObject go) => go != null && go.GetComponent<LODGroup>() != null;

        /// <summary>
        /// 按快照建立 LOD 层级：组实体挂在 <paramref name="parentEntity"/> 之下，每层实体挂在组实体之下。
        /// 只使用快照数据 + <paramref name="owner"/> 的 Transform，**不访问 LODGroup 组件本身** ——
        /// 它可能已被 <see cref="OriginalRendererDisposal.DestroyComponents"/> 销毁（重建路径）。
        /// 组实体与层实体都带 <see cref="DotsRenderLink"/>。
        /// **不处理原组件的绘制开关**：由调用方统一做（<c>DotsRenderConversionSystem.HideOriginalRenderers</c>）。
        /// 同步表只登记父物体，这里不接收 <see cref="RenderEntityRegistry"/>。
        /// </summary>
        /// <param name="disposal">
        /// **实际未使用，仅为稳定调用点保留**。
        /// </param>
        internal static void CreateLodHierarchy(
            EntityManager em,
            GameObject owner,
            LodGroupSnapshot lodGroup,
            Entity parentEntity,
            RenderMeshPool pool,
            List<Entity> created,
            OriginalRendererDisposal disposal)
        {
            var groupTransform = lodGroup.Transform;
            var ownerTransform = owner.transform;
            var worldSpaceSize = GetWorldSpaceSize(groupTransform, lodGroup.Size);

            // 组实体：LocalTransform 取 lodGroup 相对 owner 的局部变换（父物体的移动/旋转/缩放
            // 由 TransformSystemGroup 沿 Parent 层级推导组实体的 LocalToWorld）。
            // 必须带 LocalToWorld：LODRequirementsUpdateSystem 用它在运行时计算
            // LODGroupWorldReferencePoint（世界空间引用点），决定各层是否可见。
            var groupEntity = em.CreateEntity(
                typeof(LocalTransform), typeof(LocalToWorld), typeof(Parent), typeof(MeshLODGroupComponent));
            em.SetComponentData(groupEntity, LocalTransform.FromPositionRotationScale(
                ownerTransform.InverseTransformPoint(groupTransform.position),
                Quaternion.Inverse(ownerTransform.rotation) * groupTransform.rotation,
                DotsRenderConversionSystem.UniformScaleRatio(
                    groupTransform.lossyScale, ownerTransform.lossyScale, groupTransform.localScale, groupTransform.name)));
            em.SetComponentData(groupEntity, new Parent
            {
                Value = parentEntity,
            });
            em.SetComponentData(groupEntity, new MeshLODGroupComponent
            {
                ParentGroup = Entity.Null,
                ParentMask = 0,
                LODDistances0 = ComputeDistancesFromSnapshot(worldSpaceSize, lodGroup.Levels, 0),
                LODDistances1 = ComputeDistancesFromSnapshot(worldSpaceSize, lodGroup.Levels, 4),
                LocalReferencePoint = lodGroup.LocalReferencePoint,
            });
            em.AddComponentData(groupEntity, new DotsRenderLink
            {
                Target = groupTransform.gameObject,
                Kind = DotsRenderTargetKind.LodGroup,
            });
            created.Add(groupEntity);

            for (var lodIndex = 0; lodIndex < lodGroup.Levels.Length; lodIndex++)
            {
                var targets = lodGroup.Levels[lodIndex].Renderers;
                if (targets == null)
                {
                    continue; // 空层：不产生层实体（LODMask 的位序仍由下标决定）
                }

                foreach (var target in targets)
                {
                    if (target.Transform == null)
                    {
                        continue;
                    }
                    if (!pool.TryGetOrAdd(target.Mesh, target.Materials, target.SubMeshCount,
                            out var renderMeshArray, out var materialMeshInfo)) continue;

                    var rendererTransform = target.Transform;

                    // 层实体：LocalTransform 取该层 renderer 相对 LODGroup 的局部变换，挂在组实体下。
                    var entity = em.CreateEntity(
                        typeof(LocalTransform), typeof(Parent), typeof(MeshLODComponent));
                    em.SetComponentData(entity, LocalTransform.FromPositionRotationScale(
                        rendererTransform.localPosition, rendererTransform.localRotation,
                        DotsRenderConversionSystem.UniformScaleRatio(
                            rendererTransform.lossyScale, groupTransform.lossyScale,
                            rendererTransform.localScale, rendererTransform.name)));
                    em.SetComponentData(entity, new Parent
                    {
                        Value = groupEntity,
                    });
                    em.SetComponentData(entity, new MeshLODComponent
                    {
                        Group = groupEntity,
                        ParentGroup = Entity.Null,
                        // 位序与 LODRange 一致：bit0 == LOD 0。
                        LODMask = 1 << lodIndex,
                    });

                    RenderMeshUtility.AddComponents(entity, em, target.Description, renderMeshArray, materialMeshInfo);
                    em.AddComponentData(entity, new DotsRenderLink
                    {
                        Target = rendererTransform.gameObject,
                        Kind = DotsRenderTargetKind.LodLayer,
                    });

                    created.Add(entity);
                    // 不在这里 Hide 原组件：统一由调用方处理（DotsRenderConversionSystem.HideOriginalRenderers）。
                }
            }
        }

        /// <summary>
        /// 按快照把 <c>screenRelativeTransitionHeight</c> 换算成世界空间切换距离。
        /// 与 <see cref="ComputeDistances(LODGroup, int)"/> 同口径，但不访问 LODGroup
        /// （组件可能已被 DestroyComponents 销毁）。
        /// </summary>
        internal static float4 ComputeDistancesFromSnapshot(float worldSpaceSize, LodLevelSnapshot[] levels, int firstLod)
        {
            var distances = new float4(
                float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);

            if (levels == null)
            {
                return distances;
            }

            for (var i = 0; i < 4; i++)
            {
                var lodIndex = firstLod + i;
                if (lodIndex >= levels.Length)
                {
                    break;
                }

                var height = levels[lodIndex].ScreenRelativeTransitionHeight;
                // 最后一层通常 height 为 0，代表“始终可见”，除以 0 得 +Infinity，语义一致。
                distances[i] = height > 0f ? worldSpaceSize / height : float.PositiveInfinity;
            }

            return distances;
        }

        /// <summary>
        /// LOD 组的世界空间尺寸：最大轴缩放 * <paramref name="size"/>。
        /// 缩放取自 <see cref="Transform"/>（不随组件销毁），故重建路径也能用。
        /// </summary>
        public static float GetWorldSpaceSize(Transform transform, float size)
        {
            if (transform == null)
            {
                return size;
            }

            var scale = transform.lossyScale;
            var largestAxis = math.max(math.abs(scale.x), math.max(math.abs(scale.y), math.abs(scale.z)));
            return largestAxis * size;
        }

        /// <summary>
        /// 把 LOD 的 <c>screenRelativeTransitionHeight</c> 换算成世界空间切换距离，与官方烘焙器一致。
        /// <paramref name="firstLod"/> 为 0 时返回 LOD 0..3，为 4 时返回 LOD 4..7。
        /// </summary>
        public static float4 ComputeDistances(LODGroup lodGroup, int firstLod)
        {
            var lods = lodGroup.GetLODs();
            var worldSpaceSize = GetWorldSpaceSize(lodGroup);
            var distances = new float4(
                float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);

            for (var i = 0; i < 4; i++)
            {
                var lodIndex = firstLod + i;
                if (lodIndex >= lods.Length)
                {
                    break;
                }

                var height = lods[lodIndex].screenRelativeTransitionHeight;
                // 最后一层通常 height 为 0，代表“始终可见”，除以 0 得 +Infinity，语义一致。
                distances[i] = height > 0f ? worldSpaceSize / height : float.PositiveInfinity;
            }

            return distances;
        }

        /// <summary>
        /// LOD 组的世界空间尺寸：最大轴缩放 * <see cref="LODGroup.size"/>（对应官方 <c>GetWorldSpaceSize</c>）。
        /// </summary>
        public static float GetWorldSpaceSize(LODGroup lodGroup)
        {
            var scale = lodGroup.transform.lossyScale;
            var largestAxis = math.max(math.abs(scale.x), math.max(math.abs(scale.y), math.abs(scale.z)));
            return largestAxis * lodGroup.size;
        }
    }
}
