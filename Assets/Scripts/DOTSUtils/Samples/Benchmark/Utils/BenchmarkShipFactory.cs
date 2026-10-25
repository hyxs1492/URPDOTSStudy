using System.Collections.Generic;
using DOTSUtils.RenderBridge;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace DOTSUtils.Benchmark
{
    /// <summary>
    /// benchmark 的船生成器，两条路径：
    /// - <see cref="SpawnPureEcs"/>：不实例化 GameObject，循环调
    ///   <see cref="DotsRenderConversionSystem.SpawnFromPrefab"/> 从源对象直接建实体树；
    ///   源对象可以不挂 <see cref="DotsRenderAuthoring"/>（没有就按子物体自动收集渲染目标）。
    /// - <see cref="SpawnThroughBridge"/>：<c>Instantiate</c> 源对象走现有渲染桥；可指定材质，
    ///   每艘随机取一个刷到它全部 <see cref="MeshRenderer"/> 上。
    ///
    /// 两条路径共用同一份转换逻辑，产出的实体结构一致（父实体 → 组实体 → 层实体），
    /// 材质都按 (mesh, 材质序列, 子网格数) 去重，同材质的船共享同一个 <c>RenderMeshArray</c>；
    /// 挂的是同一套 benchmark 组件（见 <see cref="BenchmarkShipComponents"/>）。
    /// </summary>
    public static class BenchmarkShipFactory
    {
        /// <summary>纯 ECS 路径：生成 <paramref name="count"/> 艘船，返回生成的「组实体」数量；全部实体（组 + 层）都会追加进 <paramref name="created"/> 以便一次性清理。</summary>
        public static int SpawnPureEcs(
            World world,
            GameObject source,
            int count,
            Material[] materials,
            float radius,
            uint seed,
            RenderMeshPool pool,
            List<Entity> created)
        {
            if (source == null)
            {
                Debug.LogWarning("[Benchmark] 纯 ECS 路径需要指定源对象。");
                return 0;
            }
            if (pool == null || materials == null || materials.Length == 0)
            {
                Debug.LogWarning("[Benchmark] 生成需要至少 1 个材质与 1 个 RenderMeshPool。");
                return 0;
            }

            if (world == null || !world.IsCreated)
            {
                Debug.LogWarning("[Benchmark] 纯 ECS 路径需要一个已创建的 World。");
                return 0;
            }
            var em = world.EntityManager;
            var conversion = world.GetExistingSystemManaged<DotsRenderConversionSystem>();
            if (conversion == null)
            {
                Debug.LogWarning("[Benchmark] 找不到 DotsRenderConversionSystem。");
                return 0;
            }

            var rng = new Unity.Mathematics.Random(seed == 0u ? 1u : seed);
            var single = new Material[1];
            var ships = 0;

            for (var i = 0; i < count; i++)
            {
                // 每艘随机取一个材质，经 SpawnFromPrefab 的 overrideMaterials 覆盖，再由 RenderMeshPool 去重。
                single[0] = materials[rng.NextInt(0, materials.Length)];

                var parent = conversion.SpawnFromPrefab(
                    source,
                    RandomInsideCircle(ref rng, radius),
                    quaternion.RotateY(rng.NextFloat(0f, math.PI * 2f)),
                    1f, single, pool);
                if (parent == Entity.Null)
                {
                    continue; // 没有可转换目标时 SpawnFromPrefab 已回退，不留空壳
                }

                // 组件打在父实体上：移动父实体即带动整棵树（与走桥路径同一个入口）。
                // maxSpeed 取巡航速度。
                BenchmarkShipComponents.Attach(em, parent, ref rng, BenchmarkRandomMoveSystem.MoveSpeed);

                if (created != null)
                {
                    // created 要含整棵树（组实体 + 各层实体）。
                    created.Add(parent);
                    var group = em.GetBuffer<LinkedEntityGroup>(parent);
                    for (var g = 0; g < group.Length; g++)
                        if (group[g].Value != parent)
                        {
                            created.Add(group[g].Value);
                        }
                }
                ships++;
            }

            return ships;
        }

        /// <summary>销毁 <see cref="SpawnPureEcs"/> 记录的全部实体并清空列表。</summary>
        public static void DestroyEntities(EntityManager em, List<Entity> entities)
        {
            if (entities == null)
            {
                return;
            }
            foreach (var entity in entities)
                if (entity != Entity.Null && em.Exists(entity))
                {
                    em.DestroyEntity(entity);
                }
            entities.Clear();
        }

        /// <summary>
        /// 走桥路径：<c>Instantiate</c> 源对象 <paramref name="count"/> 次，随机位置 / 朝向。
        /// <paramref name="materials"/> 非空时每艘随机取一个材质，刷到它全部 <see cref="MeshRenderer"/> 上（含 LODGroup 各层）；
        /// 每个实例都会补上 <see cref="ShipController"/>，由它在实体创建成功时挂 benchmark 组件。
        /// </summary>
        public static List<GameObject> SpawnThroughBridge(
            GameObject source, Material[] materials, int count, float radius, uint seed)
        {
            var spawned = new List<GameObject>();
            if (source == null)
            {
                Debug.LogWarning("[Benchmark] 走桥路径需要指定源对象。");
                return spawned;
            }

            var rng = new Unity.Mathematics.Random(seed == 0u ? 1u : seed);
            for (var i = 0; i < count; i++)
            {
                var go = Object.Instantiate(source);
                go.name = source.name + "_bench_" + i;
                go.transform.position = RandomInsideCircle(ref rng, radius);   // y = 0：同一个水平面
                go.transform.rotation = quaternion.RotateY(rng.NextFloat(0f, math.PI * 2f));

                // 必须在转换之前设置：桥在转换的那一刻从 renderer 读材质（GetComponentsInChildren 覆盖 LODGroup 各层）。
                if (materials != null && materials.Length > 0)
                {
                    var material = materials[rng.NextInt(0, materials.Length)];
                    foreach (var renderer in go.GetComponentsInChildren<MeshRenderer>())
                        renderer.sharedMaterial = material;
                }

                // 源对象按用法要求不挂 authoring，这里给实例补上 ShipController —— 它继承
                // DotsRenderAuthoring，会在实体创建成功时把 benchmark 组件挂上去（见 ShipController.OnCreateEntitySuccess）。
                // 收集规则：同一对象同时带 MeshRenderer 与 LODGroup 时只按 LODGroup 记。
                //
                // 若源对象自带了 DotsRenderAuthoring（实例会继承下来），必须先摘掉它：
                // 基类标了 [DisallowMultipleComponent]，留着会让 AddComponent<ShipController>() 返回 null。
                var authoring = go.GetComponent<ShipController>();
                if (authoring == null)
                {
                    var inherited = go.GetComponent<DotsRenderAuthoring>();
                    if (inherited != null)
                    {
                        Object.DestroyImmediate(inherited);
                    }
                    authoring = go.AddComponent<ShipController>();
                }
                if (authoring == null)
                {
                    Debug.LogWarning($"[Benchmark] 无法在 {go.name} 上挂 ShipController，跳过该实例。");
                    continue;
                }

                if (authoring.MeshRenderers.Count == 0 && authoring.LodGroups.Count == 0)
                {
                    AuthoringSnapshot.CollectRenderTargets(go, authoring.MeshRenderers, authoring.LodGroups);

                    if (authoring.MeshRenderers.Count == 0 && authoring.LodGroups.Count == 0)
                        Debug.LogWarning(
                            $"[Benchmark] {source.name} 及其子物体上没有 LODGroup/MeshRenderer，走桥路径没有可转换的目标。");
                }

                // 填完列表后显式采集快照（CaptureSnapshot 幂等，重复调用无副作用；转换系统 Register 时也会兜底补采）。
                authoring.InitializeEntity();

                //authoring.TransformDrivenByEntity = true;
                //authoring.Disposal = OriginalRendererDisposal.DestroyComponents;
                DotsRenderConversionQueue.EnqueueRegister(go);   // 幂等（系统内有 ContainsKey 守卫）
                spawned.Add(go);
            }
            return spawned;
        }

        /// <summary>在水平面内的圆里均匀取一个点（<c>y</c> 恒为 0）。</summary>
        private static float3 RandomInsideCircle(ref Unity.Mathematics.Random rng, float radius)
        {
            // 半径按 sqrt(u) 缩放，圆内才均匀。
            var angle = rng.NextFloat(0f, 2f * math.PI);
            var r = radius * math.sqrt(rng.NextFloat());
            return new float3(math.cos(angle) * r, 0f, math.sin(angle) * r);
        }
    }
}
