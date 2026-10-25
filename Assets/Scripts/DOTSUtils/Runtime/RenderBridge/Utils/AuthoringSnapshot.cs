using System;
using System.Collections.Generic;
using Unity.Rendering;
using UnityEngine;

namespace DOTSUtils.RenderBridge
{

    /// <summary>
    /// 转换所需数据的纯数据快照。采集之后不再依赖原 MeshRenderer / LODGroup 是否存活，
    /// <see cref="OriginalRendererDisposal.DestroyComponents"/> 之后仍能重建实体。
    ///
    /// 只快照「组件销毁后拿不到」的东西：mesh、材质、LOD 层结构、绘制开关。
    /// <see cref="Transform"/> 与 <c>GameObject.layer</c> 不随组件销毁，位置/旋转/缩放/layer 直接读实时值。
    /// </summary>
    internal sealed class AuthoringSnapshot
    {
        public readonly RenderTargetSnapshot[] MeshRenderers;
        public readonly LodGroupSnapshot[] LodGroups;

        private AuthoringSnapshot(RenderTargetSnapshot[] meshRenderers, LodGroupSnapshot[] lodGroups)
        {
            MeshRenderers = meshRenderers;
            LodGroups = lodGroups;
        }

        /// <summary>
        /// 快照里的渲染目标总数（含 LOD 各层）。0 表示这份快照没有可用目标。
        /// </summary>
        public int RenderTargetCount
        {
            get
            {
                var count = MeshRenderers?.Length ?? 0;
                if (LodGroups == null)
                {
                    return count;
                }

                foreach (var group in LodGroups)
                {
                    if (group.Levels == null)
                    {
                        continue;
                    }
                    foreach (var level in group.Levels)
                        count += level.Renderers?.Length ?? 0;
                }
                return count;
            }
        }

        /// <summary>
        /// 采集快照。**采不到任何有效目标时返回 null** —— 调用方据此保留旧快照。
        /// 组件已被 <see cref="OriginalRendererDisposal.DestroyComponents"/> 销毁时，两个列表里会全是 fake-null，
        /// 此时返回 null 而不是一份空快照。
        /// </summary>
        /// <param name="overrideMaterials">
        /// 非 null 且非空时，把每个目标的材质序列整体替换成它（benchmark 的「每艘随机取一个材质」靠它）。
        /// 子网格数不变，取条目时按 mesh 归一化。
        /// </param>
        public static AuthoringSnapshot TryCapture(
            List<MeshRenderer> meshRenderers, List<LODGroup> lodGroups, Transform owner, RenderMeshPool pool,
            Material[] overrideMaterials = null)
        {
            if (owner == null || pool == null)
            {
                return null;
            }

            var meshTargets = CaptureMeshRenderers(meshRenderers, owner, pool);
            var lodTargets = CaptureLodGroups(lodGroups, owner, pool);

            var snapshot = new AuthoringSnapshot(meshTargets, lodTargets);
            if (overrideMaterials != null && overrideMaterials.Length > 0)
                snapshot = snapshot.WithMaterials(overrideMaterials);

            return snapshot.RenderTargetCount > 0 ? snapshot : null;
        }

        /// <summary>
        /// 按子物体收集渲染目标。同一对象同时带 MeshRenderer 与 LODGroup 时只按 LODGroup 记。
        ///
        /// 两条路径共用：<c>BenchmarkShipFactory.SpawnThroughBridge</c>（给实例自动补齐标记）与
        /// <see cref="DotsRenderConversionSystem.SpawnFromPrefab"/>（源对象没有标记时）。
        /// </summary>
        internal static void CollectRenderTargets(
            GameObject root, List<MeshRenderer> meshRenderers, List<LODGroup> lodGroups)
        {
            if (root == null || meshRenderers == null || lodGroups == null)
            {
                return;
            }

            foreach (var lodGroup in root.GetComponentsInChildren<LODGroup>())
                lodGroups.Add(lodGroup);

            // 标记「被任何已收集 LODGroup 的层引用」的 renderer：它们由 LOD 层级单独处理，
            // 不能再作为独立 MeshRenderer 目标（层 renderer 的 LODGroup 挂在父物体上，不是自身）。
            var inLod = new HashSet<Renderer>();
            foreach (var lodGroup in lodGroups)
            {
                foreach (var lod in lodGroup.GetLODs())
                {
                    if (lod.renderers == null)
                    {
                        continue;
                    }
                    foreach (var renderer in lod.renderers)
                        if (renderer != null)
                        {
                            inLod.Add(renderer);
                        }
                }
            }

            foreach (var meshRenderer in root.GetComponentsInChildren<MeshRenderer>())
            {
                if (inLod.Contains(meshRenderer))
                {
                    continue; // 归 LODGroup 管
                }
                meshRenderers.Add(meshRenderer);
            }
        }

        /// <summary>
        /// 复制一份快照，把每个渲染目标的材质序列替换成 <paramref name="materials"/>。
        /// </summary>
        private AuthoringSnapshot WithMaterials(Material[] materials)
        {
            var meshTargets = new RenderTargetSnapshot[MeshRenderers.Length];
            for (var i = 0; i < meshTargets.Length; i++)
                meshTargets[i] = MeshRenderers[i].WithMaterials(materials);

            var lodTargets = new LodGroupSnapshot[LodGroups.Length];
            for (var i = 0; i < lodTargets.Length; i++)
                lodTargets[i] = LodGroups[i].WithMaterials(materials);

            return new AuthoringSnapshot(meshTargets, lodTargets);
        }

        private static RenderTargetSnapshot[] CaptureMeshRenderers(
            List<MeshRenderer> meshRenderers, Transform owner, RenderMeshPool pool)
        {
            if (meshRenderers == null || meshRenderers.Count == 0)
            {
                return Array.Empty<RenderTargetSnapshot>();
            }

            var results = new List<RenderTargetSnapshot>(meshRenderers.Count);
            var seen = new HashSet<MeshRenderer>();

            foreach (var meshRenderer in meshRenderers)
            {
                if (meshRenderer == null)
                {
                    Debug.LogWarning($"[DotsRender] {owner.name} 的 MeshRenderers 含空槽，已跳过。");
                    continue;
                }
                if (!seen.Add(meshRenderer))
                {
                    Debug.LogWarning($"[DotsRender] {owner.name} 的 MeshRenderers 重复项 {meshRenderer.name}，已跳过。");
                    continue;
                }
                if (!meshRenderer.transform.IsChildOf(owner))
                {
                    Debug.LogWarning($"[DotsRender] {meshRenderer.name} 不在 {owner.name} 层级下，已跳过。");
                    continue;
                }

                if (TryCaptureTarget(meshRenderer, pool, out var target))
                {
                    results.Add(target);
                }
            }

            return results.ToArray();
        }

        private static LodGroupSnapshot[] CaptureLodGroups(List<LODGroup> lodGroups, Transform owner, RenderMeshPool pool)
        {
            if (lodGroups == null || lodGroups.Count == 0)
            {
                return Array.Empty<LodGroupSnapshot>();
            }

            var results = new List<LodGroupSnapshot>(lodGroups.Count);
            var seen = new HashSet<LODGroup>();

            foreach (var lodGroup in lodGroups)
            {
                if (lodGroup == null)
                {
                    Debug.LogWarning($"[DotsRender] {owner.name} 的 LodGroups 含空槽，已跳过。");
                    continue;
                }
                if (!seen.Add(lodGroup))
                {
                    Debug.LogWarning($"[DotsRender] {owner.name} 的 LodGroups 重复项 {lodGroup.name}，已跳过。");
                    continue;
                }
                if (!lodGroup.transform.IsChildOf(owner))
                {
                    Debug.LogWarning($"[DotsRender] {lodGroup.name} 不在 {owner.name} 层级下，已跳过。");
                    continue;
                }

                var lods = lodGroup.GetLODs();
                var levels = new LodLevelSnapshot[lods.Length];

                for (var i = 0; i < lods.Length; i++)
                {
                    var renderers = lods[i].renderers;
                    if (renderers == null)
                    {
                        levels[i] = new LodLevelSnapshot(lods[i].screenRelativeTransitionHeight, Array.Empty<RenderTargetSnapshot>());
                        continue;
                    }

                    var targets = new List<RenderTargetSnapshot>(renderers.Length);
                    var seenRenderers = new HashSet<Renderer>();

                    foreach (var renderer in renderers)
                    {
                        if (renderer == null)
                        {
                            continue;
                        }
                        if (!seenRenderers.Add(renderer))
                        {
                            continue;
                        }

                        if (renderer is not MeshRenderer meshRenderer)
                        {
                            Debug.LogWarning($"[DotsRender] LOD 层里 {renderer.GetType().Name} 暂不支持，跳过 {renderer.name}。");
                            continue;
                        }
                        if (TryCaptureTarget(meshRenderer, pool, out var target))
                        {
                            targets.Add(target);
                        }
                    }

                    // 空层也保留：层实体的 LODMask = 1 << lodIndex 依赖下标等于 LOD 序号。
                    levels[i] = new LodLevelSnapshot(lods[i].screenRelativeTransitionHeight, targets.ToArray());
                }

                results.Add(new LodGroupSnapshot(lodGroup.transform, lodGroup.size, lodGroup.localReferencePoint, levels));
            }

            return results.ToArray();
        }

        private static bool TryCaptureTarget(MeshRenderer meshRenderer, RenderMeshPool pool, out RenderTargetSnapshot target)
        {
            target = default;

            var filter = meshRenderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null)
            {
                Debug.LogWarning($"[DotsRender] {meshRenderer.name} 没有有效的 Mesh（MeshFilter.sharedMesh 为空），跳过。");
                return false;
            }

            var subMeshCount = pool.ResolveSubMeshCount(mesh, meshRenderer.name);
            if (subMeshCount <= 0)
            {
                return false;
            }

            var materials = meshRenderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
            {
                Debug.LogWarning($"[DotsRender] {meshRenderer.name} 没有材质，跳过。");
                return false;
            }

            target = new RenderTargetSnapshot(
                meshRenderer.transform, mesh, materials, subMeshCount,
                DotsRenderConversionSystem.Describe(meshRenderer, meshRenderer.gameObject.layer));
            return true;
        }
    }

    /// <summary>
    /// 一个渲染目标在组件销毁后仍需要的全部数据。
    /// </summary>
    internal readonly struct RenderTargetSnapshot
    {
        public readonly Transform Transform;
        public readonly Mesh Mesh;
        public readonly Material[] Materials;
        public readonly int SubMeshCount;
        public readonly RenderMeshDescription Description;

        public RenderTargetSnapshot(Transform transform, Mesh mesh, Material[] materials, int subMeshCount,
            RenderMeshDescription description)
        {
            Transform = transform;
            Mesh = mesh;
            Materials = materials;
            SubMeshCount = subMeshCount;
            Description = description;
        }

        /// <summary>
        /// 换一组材质（其余字段保持不变）。
        /// </summary>
        public RenderTargetSnapshot WithMaterials(Material[] materials) =>
            new(Transform, Mesh, materials, SubMeshCount, Description);
    }

    /// <summary>
    /// 一层 LOD。空层也保留占位，使数组下标 == LOD 序号。
    /// </summary>
    internal readonly struct LodLevelSnapshot
    {
        public readonly float ScreenRelativeTransitionHeight;
        public readonly RenderTargetSnapshot[] Renderers;

        public LodLevelSnapshot(float screenRelativeTransitionHeight, RenderTargetSnapshot[] renderers)
        {
            ScreenRelativeTransitionHeight = screenRelativeTransitionHeight;
            Renderers = renderers;
        }

        /// <summary>
        /// 换掉本层所有 renderer 的材质（空层原样返回）。
        /// </summary>
        public LodLevelSnapshot WithMaterials(Material[] materials)
        {
            if (Renderers == null || Renderers.Length == 0)
            {
                return this;
            }

            var replaced = new RenderTargetSnapshot[Renderers.Length];
            for (var i = 0; i < replaced.Length; i++)
                replaced[i] = Renderers[i].WithMaterials(materials);
            return new LodLevelSnapshot(ScreenRelativeTransitionHeight, replaced);
        }
    }

    /// <summary>
    /// 一个 LODGroup 的层级配置（不含它挂在哪、朝向如何 —— 那些在 Transform 里）。
    /// </summary>
    internal readonly struct LodGroupSnapshot
    {
        public readonly Transform Transform;
        public readonly float Size;
        public readonly Vector3 LocalReferencePoint;
        public readonly LodLevelSnapshot[] Levels;

        public LodGroupSnapshot(Transform transform, float size, Vector3 localReferencePoint, LodLevelSnapshot[] levels)
        {
            Transform = transform;
            Size = size;
            LocalReferencePoint = localReferencePoint;
            Levels = levels;
        }

        /// <summary>
        /// 换掉全部层里所有 renderer 的材质。
        /// </summary>
        public LodGroupSnapshot WithMaterials(Material[] materials)
        {
            if (Levels == null || Levels.Length == 0)
            {
                return this;
            }

            var replaced = new LodLevelSnapshot[Levels.Length];
            for (var i = 0; i < replaced.Length; i++)
                replaced[i] = Levels[i].WithMaterials(materials);
            return new LodGroupSnapshot(Transform, Size, LocalReferencePoint, replaced);
        }
    }

}
