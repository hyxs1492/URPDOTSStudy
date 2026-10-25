using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using DOTSUtils.Worlds;

namespace DOTSUtils.RenderBridge
{
    /// <summary>
    /// 消费请求队列，为带标记的父物体建立实体结构：
    /// 1 个逻辑父实体（世界变换，无渲染组件）+ 每个 MeshRenderer/LODGroup 目标 1 个子（组）渲染实体（<see cref="Parent"/> 指向父实体）。
    /// 放在 InitializationSystemGroup，保证当帧渲染前数据已就绪。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(RenderBridgeConversionGroup))]
    public partial class DotsRenderConversionSystem : SystemBase
    {
        private readonly RenderMeshPool m_Pool = new();
        /// <summary>
        /// 父物体 → 其名下的实体集合（父实体 + 若干子渲染实体，含 LOD 组/层）。
        /// 由 <c>RenderEntityRegistry</c> 同步维护。
        /// </summary>
        private readonly Dictionary<GameObject, List<Entity>> m_EntitiesOf = new();

        private readonly RenderEntityRegistry m_Registry = new();
        private DotsRenderTransformSyncSystem m_SyncSystem;
        private DotsRenderTransformPushSystem m_PushSystem;

        public RenderEntityRegistry Registry => m_Registry;

        /// <summary>
        /// 供 <see cref="DotsRenderAuthoring"/> 在采集快照时复用子网格数校验。
        /// </summary>
        public RenderMeshPool Pool => m_Pool;

        // ------------------------------------------------------------------
        //  owner 维度的实体查询（供 DotsRenderAuthoring 的门面使用）
        // ------------------------------------------------------------------

        private static readonly List<Entity> s_QueryScratch = new();
        private readonly List<DotsRenderConversionQueue.PendingEntityOperation> m_PendingScratch = new();

        /// <summary>
        /// 预制体路径上没有 authoring 时收集渲染目标的临时列表（复用）。
        /// </summary>
        private readonly List<MeshRenderer> m_SpawnMeshScratch = new();
        private readonly List<LODGroup> m_SpawnLodScratch = new();

        /// <summary>
        /// 按 owner 查实体，<paramref name="scope"/> 过滤种类。未转换 / 已注销（没有登记）时返回 false；
        /// 命中时把结果写入 <paramref name="results"/>（顺序 = created 顺序：父实体 → 子渲染实体 → 组实体 → 层实体）。
        /// 只读查询，主线程调用。
        /// </summary>
        public bool TryGetEntities(GameObject owner, DotsRenderEntityScope scope, List<Entity> results)
        {
            if (results == null)
            {
                return false;
            }
            results.Clear();
            if (owner == null || !m_EntitiesOf.TryGetValue(owner, out var entities))
            {
                return false;
            }

            foreach (var entity in entities)
            {
                if (!EntityManager.Exists(entity))
                {
                    continue;
                }
                if (!MatchesScope(scope, EntityManager.GetComponentData<DotsRenderLink>(entity).Kind))
                {
                    continue;
                }
                results.Add(entity);
            }
            return results.Count > 0;
        }

        /// <summary>
        /// 该 owner 的逻辑父实体；未转换 / 已注销时为 false。
        /// </summary>
        public bool TryGetParentEntity(GameObject owner, out Entity entity)
        {
            entity = Entity.Null;
            if (!TryGetEntities(owner, DotsRenderEntityScope.Parent, s_QueryScratch))
            {
                return false;
            }
            entity = s_QueryScratch[0];
            return true;
        }

        /// <summary>
        /// 该 owner 的渲染实体数（不含父实体）；未转换 / 已注销时为 0。
        /// </summary>
        public int GetRenderEntities(GameObject owner, List<Entity> results) =>
            TryGetEntities(owner, DotsRenderEntityScope.RenderTargets, results) ? results.Count : 0;

        private static bool MatchesScope(DotsRenderEntityScope scope, DotsRenderTargetKind kind) => scope switch
        {
            DotsRenderEntityScope.Parent => kind == DotsRenderTargetKind.Parent,
            DotsRenderEntityScope.RenderTargets => kind != DotsRenderTargetKind.Parent,
            _ => true,
        };

        // ------------------------------------------------------------------
        //  结构性写入的 ECB 通路
        //
        //  门面（DotsRenderAuthoring）与"待应用操作"都记录到这里，再统一 playback。
        // ------------------------------------------------------------------

        private EntityCommandBuffer m_CommandBuffer;

        /// <summary>
        /// 记录结构性变更用的 ECB（惰性创建）。
        /// 记录完必须调 <see cref="FlushCommands"/> 才会真正生效。
        /// </summary>
        public EntityCommandBuffer CommandBuffer
        {
            get
            {
                if (!m_CommandBuffer.IsCreated)
                    m_CommandBuffer = new EntityCommandBuffer(Allocator.Persistent);
                return m_CommandBuffer;
            }
        }

        /// <summary>
        /// Playback 并释放当前 ECB。ECB 是 SinglePlayback 的（重复播放会出错），
        /// 播放完即丢弃，下次访问 <see cref="CommandBuffer"/> 时重建。
        /// 调用方保证「记录 → flush」之间不跑其它代码。
        /// 空批次直接返回（保留这个 ECB 复用）。
        /// </summary>
        public void FlushCommands()
        {
            if (!m_CommandBuffer.IsCreated)
            {
                return;
            }
            if (m_CommandBuffer.IsEmpty)
            {
                return;
            }

            m_CommandBuffer.Playback(EntityManager);
            m_CommandBuffer.Dispose();
        }

        /// <summary>
        /// 把一个渲染实体的材质换成 <paramref name="materials"/>，**立即生效**。
        ///
        /// mesh 与子网格数从实体自身反查（<see cref="RenderMeshArray.GetMesh"/> +
        /// <see cref="MaterialMeshInfo.MaterialMeshIndexRange"/>），**不依赖原 MeshRenderer 是否存活** ——
        /// <see cref="OriginalRendererDisposal.DestroyComponents"/> 之后同样可用。
        ///
        /// 材质组合经 <see cref="RenderMeshPool"/> 去重：同材质的对象会落到同一个 RenderMeshArray。
        /// </summary>
        public bool SetEntityMaterials(Entity entity, Material[] materials)
        {
            if (materials == null || materials.Length == 0)
            {
                return false;
            }
            if (!EntityManager.Exists(entity))
            {
                return false;
            }
            if (!EntityManager.HasComponent<MaterialMeshInfo>(entity))
            {
                return false;
            }
            if (!EntityManager.HasComponent<RenderMeshArray>(entity))
            {
                return false;
            }

            var array = EntityManager.GetSharedComponentManaged<RenderMeshArray>(entity);
            var info = EntityManager.GetComponentData<MaterialMeshInfo>(entity);

            var mesh = array.GetMesh(info);
            if (mesh == null)
            {
                return false;
            }

            // 单材质槽（MeshID / MaterialID 路径）没有 range，退回用 mesh 自身的子网格数。
            var subMeshCount = info.HasMaterialMeshIndexRange
                ? info.MaterialMeshIndexRange.length
                : mesh.subMeshCount;

            if (!m_Pool.TryGetOrAdd(mesh, materials, subMeshCount, out var newArray, out var newInfo))
            {
                return false;
            }

            EntityManager.SetComponentData(entity, newInfo);
            EntityManager.SetSharedComponentManaged(entity, newArray);
            return true;
        }

        /// <summary>
        /// 应用该 owner 的排队操作（保持调用顺序），随后触发"实体已就绪"回调。
        /// </summary>
        private void ApplyPendingOperations(GameObject go, DotsRenderAuthoring authoring, Entity parentEntity)
        {
            m_PendingScratch.Clear();
            DotsRenderConversionQueue.TakeOperations(go, m_PendingScratch);

            // 每条操作单独「记录 → playback」：下一条操作要能看到上一条的结果
            // （SetComponent 的记录阶段会检查组件是否存在，看不到未播放的记录）。
            foreach (var operation in m_PendingScratch)
            {
                if (go == null || !EntityManager.Exists(parentEntity))
                {
                    break; // 期间被销毁
                }
                operation.Apply(CommandBuffer, go);
                FlushCommands();
            }

            m_PendingScratch.Clear();

            if (authoring != null && EntityManager.Exists(parentEntity))
                authoring.OnCreateEntitySuccess(parentEntity);
        }

        protected override void OnUpdate()
        {
            // Add/MarkDead 会写注册表底层的 NativeList/TransformAccessArray，而同步与回写两个 job
            // 都持有它的视图：**两个 PendingJob 都要先 Complete**。
            // 惰性获取系统引用。
            m_SyncSystem ??= World.GetOrCreateSystemManaged<DotsRenderTransformSyncSystem>();
            m_SyncSystem.PendingJob.Complete();

            m_PushSystem ??= World.GetOrCreateSystemManaged<DotsRenderTransformPushSystem>();
            m_PushSystem.PendingJob.Complete();

            // 注销先处理。
            while (DotsRenderConversionQueue.TryDequeueUnregister(out var unregister))
                Unregister(unregister);

            while (DotsRenderConversionQueue.TryDequeueRegister(out var register))
                Register(register);
        }

        /// <summary>
        /// 从预制体直接生成实体树，**不实例化 GameObject**。
        /// 返回父实体；失败（采不到快照 / 没有可转换目标）返回 <see cref="Entity.Null"/>。
        ///
        /// 源对象**不要求**带 <see cref="DotsRenderAuthoring"/>：没有就按子物体自动收集渲染目标；
        /// 带的话用它的两个列表。
        ///
        /// 与场景实例路径的区别有三处：
        /// 1. **不登记同步表** —— 没有 Transform 可同步，变换纯由实体侧驱动；
        /// 2. **不 Hide 任何组件** —— 预制体里的 MeshRenderer / LODGroup 是共享资产；
        /// 3. **不进 m_EntitiesOf** —— 销毁改由父实体上的 <see cref="LinkedEntityGroup"/> 级联完成。
        /// </summary>
        /// <param name="overrideMaterials">
        /// 非 null 且非空时替换每个渲染目标的材质序列（benchmark 的「每艘随机取一个材质」靠它）。
        /// </param>
        /// <param name="poolOverride">非 null 时用它取 <c>RenderMeshArray</c> 条目；默认用系统的池。</param>
        public Entity SpawnFromPrefab(
            GameObject prefab, Vector3 position, Quaternion rotation, float scale,
            Material[] overrideMaterials = null, RenderMeshPool poolOverride = null)
        {
            if (prefab == null)
            {
                Debug.LogWarning("[DotsRender] SpawnFromPrefab 收到空预制体。");
                return Entity.Null;
            }

            // 源对象带 authoring 就用它的两个列表；没有就按子物体自动收集（不写回组件）。
            var authoring = prefab.GetComponent<DotsRenderAuthoring>();
            List<MeshRenderer> meshRenderers;
            List<LODGroup> lodGroups;
            if (authoring != null)
            {
                meshRenderers = authoring.MeshRenderers;
                lodGroups = authoring.LodGroups;
            }
            else
            {
                m_SpawnMeshScratch.Clear();
                m_SpawnLodScratch.Clear();
                AuthoringSnapshot.CollectRenderTargets(prefab, m_SpawnMeshScratch, m_SpawnLodScratch);
                meshRenderers = m_SpawnMeshScratch;
                lodGroups = m_SpawnLodScratch;
            }

            // 每次现采，不写回 authoring 的快照字段。
            var pool = poolOverride ?? m_Pool;
            var snapshot = AuthoringSnapshot.TryCapture(
                meshRenderers, lodGroups, prefab.transform, pool, overrideMaterials);
            if (snapshot == null)
            {
                Debug.LogWarning($"[DotsRender] {prefab.name} 没有可用的渲染目标（列表为空），未生成实体。");
                return Entity.Null;
            }

            var created = new List<Entity>();
            var parentEntity = BuildEntityTree(
                snapshot, pool, prefab.transform, position, rotation, scale, prefab, created);

            if (created.Count == 1)
            {
                // 一个目标都没建起来（只剩父实体）→ 回退，不留下空壳。
                EntityManager.DestroyEntity(parentEntity);
                Debug.LogWarning($"[DotsRender] 预制体 {prefab.name} 没有任何可转换的目标，未生成实体。");
                return Entity.Null;
            }

            // 级联销毁：LinkedEntityGroup 让 DestroyEntity(parentEntity) 一次带走整棵树。
            // created 的第 0 项就是父实体本身，foreach 已覆盖全树。
            var group = EntityManager.AddBuffer<LinkedEntityGroup>(parentEntity);
            foreach (var entity in created)
                group.Add(new LinkedEntityGroup
                {
                    Value = entity,
                });

            EntityManager.AddComponentData(parentEntity, default(DotsRenderPrefabInstance));
            return parentEntity;
        }

        /// <summary>
        /// 销毁由 <see cref="SpawnFromPrefab"/> 生成的整棵实体树。只接受带
        /// <see cref="DotsRenderPrefabInstance"/> 的父实体（即本 API 的产物）。
        /// </summary>
        public bool DestroySpawned(Entity parentEntity)
        {
            if (!EntityManager.Exists(parentEntity))
            {
                return false;
            }
            if (!EntityManager.HasComponent<DotsRenderPrefabInstance>(parentEntity))
            {
                return false;
            }

            // LinkedEntityGroup 会级联销毁整棵树。
            EntityManager.DestroyEntity(parentEntity);
            return true;
        }

        /// <summary>
        /// 按快照建父实体 + 全部渲染实体。**纯数据到实体**：不触碰原组件、不做 Hide、不登记同步表。
        /// 两条路径共用：场景实例（<see cref="Register"/>）与预制体（<see cref="SpawnFromPrefab"/>）。
        /// </summary>
        /// <param name="root">
        /// 子实体 LocalTransform 的参照物。子实体存的是「相对 owner」的变换（<c>Parent</c> 指向父实体，
        /// 不是指向直接父物体），场景实例传实例 transform、预制体路径传 <c>prefab.transform</c>。
        /// </param>
        /// <param name="linkTarget">写进父实体 <see cref="DotsRenderLink.Target"/> 的对象（来源标识）。</param>
        private Entity BuildEntityTree(
            AuthoringSnapshot snapshot, RenderMeshPool pool, Transform root,
            Vector3 position, Quaternion rotation, float scale,
            GameObject linkTarget, List<Entity> created)
        {
            // 父实体（逻辑实体）：世界变换，不挂渲染组件。
            var parentEntity = EntityManager.CreateEntity(typeof(LocalTransform), typeof(LocalToWorld));
            EntityManager.SetComponentData(parentEntity, LocalTransform.FromPositionRotationScale(
                position, rotation, scale));
            EntityManager.AddComponentData(parentEntity, new DotsRenderLink
            {
                Target = linkTarget,
                Kind = DotsRenderTargetKind.Parent,
            });
            created.Add(parentEntity);

            // MeshRenderer 目标：全部数据来自快照，不触碰原组件。
            foreach (var target in snapshot.MeshRenderers)
            {
                if (target.Transform == null)
                {
                    continue;
                }

                if (!pool.TryGetOrAdd(target.Mesh, target.Materials, target.SubMeshCount, out var array, out var info))
                    continue;

                var t = target.Transform;
                var childEntity = EntityManager.CreateEntity(typeof(LocalTransform), typeof(Parent));
                // 子实体的 Parent 指向父实体（对应 owner），故 LocalTransform 必须取「相对 owner」的变换，
                // 而非相对直接父物体的 localPosition/localRotation。
                EntityManager.SetComponentData(childEntity, LocalTransform.FromPositionRotationScale(
                    root.InverseTransformPoint(t.position),
                    Quaternion.Inverse(root.rotation) * t.rotation,
                    UniformScaleRatio(t.lossyScale, root.lossyScale, t.localScale, t.name)));
                EntityManager.SetComponentData(childEntity, new Parent
                {
                    Value = parentEntity,
                });

                RenderMeshUtility.AddComponents(childEntity, EntityManager, target.Description, array, info);
                EntityManager.AddComponentData(childEntity, new DotsRenderLink
                {
                    Target = t.gameObject,
                    Kind = DotsRenderTargetKind.MeshRenderer,
                });
                created.Add(childEntity);
            }

            // LODGroup 目标：同样只读快照。
            // owner 传 linkTarget —— 它只被用来取 owner.transform（即 root），调用方保证两者一致。
            foreach (var lodGroup in snapshot.LodGroups)
                DotsRenderLodSupport.CreateLodHierarchy(
                    EntityManager, linkTarget, lodGroup, parentEntity, pool, created, default);

            return parentEntity;
        }

        /// <summary>
        /// 让快照对应的原组件停止绘制。**只有场景实例路径才调** ——
        /// 预制体里的 MeshRenderer / LODGroup 是共享资产。
        /// </summary>
        private void HideOriginalRenderers(AuthoringSnapshot snapshot, OriginalRendererDisposal disposal)
        {
            foreach (var target in snapshot.MeshRenderers)
            {
                if (target.Transform == null)
                {
                    continue;
                }
                OriginalRendererGuard.Hide(target.Transform.GetComponent<MeshRenderer>(), disposal);
            }

            foreach (var group in snapshot.LodGroups)
            {
                if (group.Transform == null)
                {
                    continue;
                }
                OriginalRendererGuard.Hide(group.Transform.GetComponent<LODGroup>(), disposal);

                if (group.Levels == null)
                {
                    continue;
                }
                foreach (var level in group.Levels)
                {
                    if (level.Renderers == null)
                    {
                        continue;
                    }
                    foreach (var levelTarget in level.Renderers)
                    {
                        if (levelTarget.Transform == null)
                        {
                            continue;
                        }
                        OriginalRendererGuard.Hide(levelTarget.Transform.GetComponent<MeshRenderer>(), disposal);
                    }
                }
            }
        }

        private void Register(GameObject go)
        {
            if (go == null)
            {
                return;
            }
            if (m_EntitiesOf.ContainsKey(go))
            {
                return; // 幂等
            }

            var authoring = go.GetComponent<DotsRenderAuthoring>();
            if (authoring == null)
            {
                return;
            }

            // 兜底补采：运行时 AddComponent 之后才填两个列表的路径，这里补一次 ——
            // CaptureSnapshot 幂等，且采不到有效数据时不会覆盖已有快照。
            authoring.CaptureSnapshot();

            // 一律从快照读转换输入：Disposal = DestroyComponents 之后原组件已被销毁，
            // 只有快照还留着 mesh / 材质 / LOD 层结构。
            var snapshot = authoring.SnapshotForConversion;
            if (snapshot == null)
            {
                Debug.LogWarning($"[DotsRender] {go.name} 两个列表都为空（或组件已被销毁），没有可用的转换快照，跳过。");
                DotsRenderConversionQueue.DropOperations(go);
                return;
            }

            var root = go.transform;
            var created = new List<Entity>();

            var parentEntity = BuildEntityTree(
                snapshot, m_Pool, root, root.position, root.rotation,
                UniformScale(root.lossyScale, go.name), go, created);

            if (created.Count == 1)
            {
                // 一个目标都没转成（只剩父实体）→ 回退：销毁父实体，不留下空壳。
                EntityManager.DestroyEntity(parentEntity);
                Debug.LogWarning($"[DotsRender] {go.name} 没有任何可转换的目标，未创建实体。");
                DotsRenderConversionQueue.DropOperations(go);
                return;
            }

            // 原组件的处理只在场景实例路径做（预制体路径不会走到这里）。
            HideOriginalRenderers(snapshot, authoring.Disposal);

            // 实体侧的变换驱动不登记进同步表。
            if (!authoring.TransformDrivenByEntity)
                m_Registry.Add(root, parentEntity);   // 同步表只登记父物体（不变式：条目数 == 未被排除的标记父物体数）
            m_EntitiesOf.Add(go, created);

            // 先应用排队操作，再通知「实体已就绪」。
            ApplyPendingOperations(go, authoring, parentEntity);
        }

        private void Unregister(GameObject go)
        {
            // 注销时丢弃该对象的待应用操作。
            DotsRenderConversionQueue.DropOperations(go);

            // 恢复原绘制：分别遍历 MeshRenderers 与 LodGroups（LODGroup 遍历其各层 renderers）。
            // 对象已被销毁时跳过。
            if (go != null)
            {
                var authoring = go.GetComponent<DotsRenderAuthoring>();
                if (authoring != null)
                {
                    var disposal = authoring.Disposal;
                    var meshRenderers = authoring.MeshRenderers;
                    if (meshRenderers != null)
                    {
                        foreach (var meshRenderer in meshRenderers)
                        {
                            if (meshRenderer == null)
                            {
                                continue;
                            }
                            OriginalRendererGuard.Restore(meshRenderer, disposal);
                        }
                    }

                    var lodGroups = authoring.LodGroups;
                    if (lodGroups != null)
                    {
                        foreach (var lodGroup in lodGroups)
                        {
                            if (lodGroup == null)
                            {
                                continue;
                            }
                            OriginalRendererGuard.Restore(lodGroup, disposal);
                            foreach (var lod in lodGroup.GetLODs())
                            {
                                if (lod.renderers == null)
                                {
                                    continue;
                                }
                                foreach (var r in lod.renderers)
                                    if (r != null)
                                    {
                                        OriginalRendererGuard.Restore(r, disposal);
                                    }
                            }
                        }
                    }
                }
            }

            if (!m_EntitiesOf.TryGetValue(go, out var entities))
            {
                return;
            }

            // 按层级显式逐个销毁全部实体。
            foreach (var entity in entities)
                if (EntityManager.Exists(entity))
                {
                    EntityManager.DestroyEntity(entity);
                }

            m_EntitiesOf.Remove(go);

            // 注册表只 MarkDead 父物体对应的那一条（created[0] 恒为父实体）。
            if (entities.Count > 0)
            {
                m_Registry.MarkDead(entities[0]);
            }
        }

        /// <summary>
        /// 缩放三轴不一致时 <c>Debug.LogWarning</c>，统一按 <c>.x</c> 取值。
        /// 仅用于“父实体”的写入：调用处传父物体的世界尺度 <c>root.lossyScale</c>。
        /// 子渲染实体的缩放须用 <see cref="UniformScaleRatio"/> 取“相对父”的比值。
        /// </summary>
        internal static float UniformScale(Vector3 scale, string label)
        {
            if (!Mathf.Approximately(scale.x, scale.y) || !Mathf.Approximately(scale.x, scale.z))
                Debug.LogWarning($"[DotsRender] {label} 的缩放 ({scale.x}, {scale.y}, {scale.z}) 三轴不一致，已按 x 轴取值 {scale.x}。");
            return scale.x;
        }

        /// <summary>
        /// 子实体相对父实体的缩放：取两者的世界尺度之比，保证“父实体 Scale（<c>parentWorldScale.x</c>）× 子实体 Scale”
        /// 恰好还原子物体的世界尺度 <c>childWorldScale.x</c>。
        /// 不能用 <c>childWorldScale</c> 原值，也不能用 <c>localScale</c>。
        /// 父物体世界尺度近似为 0 时无法还原世界缩放，退回子物体的局部缩放 <paramref name="childLocalScale"/>.x 并警告。
        /// 与 <see cref="UniformScale"/> 同风格：三轴不一致时同样警告并按 x 轴取值。
        /// </summary>
        internal static float UniformScaleRatio(Vector3 childWorldScale, Vector3 parentWorldScale, Vector3 childLocalScale, string label)
        {
            if (!Mathf.Approximately(childWorldScale.x, childWorldScale.y) || !Mathf.Approximately(childWorldScale.x, childWorldScale.z))
                Debug.LogWarning($"[DotsRender] {label} 的世界缩放 ({childWorldScale.x}, {childWorldScale.y}, {childWorldScale.z}) 三轴不一致，已按 x 轴取值。");

            if (Mathf.Approximately(parentWorldScale.x, 0f))
            {
                Debug.LogWarning($"[DotsRender] 父物体世界缩放为 0，无法还原 {label} 的世界缩放，已退回其局部缩放 {childLocalScale.x}。");
                return childLocalScale.x;
            }

            return childWorldScale.x / parentWorldScale.x;
        }

        /// <summary>
        /// 构造 7 参 <see cref="RenderMeshDescription"/>。
        /// <paramref name="layer"/> 由调用方传入目标所在 GameObject 的层。
        /// </summary>
        internal static RenderMeshDescription Describe(Renderer r, int layer)
        {
            return new RenderMeshDescription(
                r.shadowCastingMode,
                r.receiveShadows,
                r.motionVectorGenerationMode,
                layer,
                r.renderingLayerMask,
                r.lightProbeUsage,
                r.staticShadowCaster);
        }

        protected override void OnDestroy()
        {
            Dependency.Complete();

            // 持有 m_Registry 底层容器（NativeList / TransformAccessArray）视图的是同步与回写两个 job：
            // 必须先把它们收掉再 Dispose。
            m_SyncSystem?.PendingJob.Complete();
            m_PushSystem?.PendingJob.Complete();

            // Persistent 分配必须显式释放（系统随 World 销毁 / 编辑器域重载时）。
            if (m_CommandBuffer.IsCreated)
            {
                m_CommandBuffer.Dispose();
            }

            m_Registry.Dispose();
        }
    }
}
