using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace DOTSUtils.Spatial
{
    /// <summary>
    /// 空间索引的**唯一**建树系统 —— 按装配时给的 <see cref="SpatialConfig"/>（<see cref="SpatialSettings.Pending"/>）
    /// 建一棵 <see cref="NativeSpatialTree"/>，模式（四叉树 / 八叉树）就是这棵树自己的
    /// <see cref="NativeSpatialTree.Kind"/>。
    ///
    /// 形态：<see cref="ISystem"/>（struct、无托管实例），热点逻辑下沉到 <b>Burst job</b>：
    /// Bulk 走 <see cref="CollectSpatialElementsJob"/> + <see cref="SpatialTreeBuildJob"/>；
    /// 增量由主线程处理新增/销毁，位置变化走 <see cref="CollectSpatialMovesJob"/> + <see cref="SpatialTreeApplyOpsJob"/>。
    ///
    /// 结构性变更一律用**系统自己的 <c>EntityCommandBuffer(Allocator.Temp)</c> + 立刻 Playback**。
    ///
    /// <see cref="SpatialConfig"/> 必须是 struct（unmanaged）—— <c>ISystem</c> 里不能有引用类型字段。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SpatialGroup))]
    public partial struct SpatialBuildSystem : ISystem
    {
        /// <summary>
        /// 装配时快照下来的参数（该 World 一份，之后不受 <see cref="SpatialSettings"/> 影响）。
        /// </summary>
        private SpatialConfig m_Config;

        private Entity m_Singleton;
        private bool m_WarnedOutOfBounds;

        // 这两个 job 的 Execute 都带组件参数，走"结构即查询"的无 query 版：组件清单来自 Execute 签名。

        /// <summary>
        /// 单例实体（<see cref="Entity.Null"/> = 尚未创建）。外部请用 <c>SpatialAccess.TryGetIndexSingleton</c>。
        /// </summary>
        public Entity SingletonEntity => m_Singleton;

        public void OnCreate(ref SystemState state)
        {
            m_Config = SpatialSettings.Pending;   // struct ⇒ 赋值即快照


            CreateSingleton(ref state);
        }

        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            if (!em.Exists(m_Singleton))
            {
                CreateSingleton(ref state);
            }

            var data = em.GetComponentData<SpatialSingleton>(m_Singleton);
            var tree = data.Tree;

            if (!Matches(tree, m_Config))
            {
                if (tree.IsCreated)
                {
                    tree.Dispose();
                }
                tree = new NativeSpatialTree(m_Config.Index, m_Config.Tree.Bounds, Allocator.Persistent,
                                             m_Config.Tree.MaxDepth, m_Config.Tree.MaxLeafElements);
                ClearAllMembers(ref state);      // 旧句柄全部失效：清掉，下一帧重新入树
                data.BuildVersion++;
            }

            data.LastAppliedOps = m_Config.Tree.Mode == SpatialUpdateMode.Bulk
                ? UpdateBulk(ref state, ref tree)
                : UpdateIncremental(ref state, ref tree);

            data.Tree = tree;
            data.BuildVersion++;
            em.SetComponentData(m_Singleton, data);
        }



        public void OnDestroy(ref SystemState state) => Dispose(ref state);

        /// <summary>
        /// 只建一个「空」单例：**树留到第一次 <c>OnUpdate</c> 再建**（<c>Matches()</c> 会发现树还没建）。
        /// </summary>
        private void CreateSingleton(ref SystemState state)
        {
            m_Singleton = state.EntityManager.CreateEntity(typeof(SpatialSingleton));
            state.EntityManager.SetComponentData(m_Singleton, new SpatialSingleton
            {
                BuildVersion = 1,
            });
        }
        // ───────────────────────────── 全量 ─────────────────────────────

        private int UpdateBulk(ref SystemState state, ref NativeSpatialTree tree)
        {
            // 收集 + 建树都在 worker 线程上 Burst 执行，末尾 join 一次。
            var elements = new NativeList<SpatialElement>(Allocator.TempJob);
            var accepted = new NativeReference<int>(Allocator.TempJob);
            var resultTree = new NativeReference<NativeSpatialTree>(Allocator.TempJob);
            try
            {
                state.Dependency = new CollectSpatialElementsJob
                {
                    Elements = elements,
                }.Schedule(state.Dependency);
                state.Dependency = IJobExtensions.Schedule(new SpatialTreeBuildJob
                {
                    Tree = tree,
                    Elements = elements.AsDeferredJobArray(),
                    Accepted = accepted,
                    ResultTree = resultTree,
                }, state.Dependency);
                state.Dependency.Complete();

                tree = resultTree.Value;              // 值语义：结果只能从 ResultTree 拿
                ClearAllMembers(ref state);
                WarnIfOutOfBounds(tree.OutOfBoundsRejected);
                return accepted.Value;
            }
            finally
            {
                elements.Dispose();
                accepted.Dispose();
                resultTree.Dispose();
            }
        }

        // ───────────────────────────── 增量 ─────────────────────────────

        private int UpdateIncremental(ref SystemState state, ref NativeSpatialTree tree)
        {
            var applied = 0;

            // ① 新增：有 Indexed 但还没有 Member（结构性变更，走主线程）。
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (transform, entity) in SystemAPI.Query<RefRO<LocalTransform>>()
                         .WithAll<SpatialIndexed>().WithNone<SpatialMember>().WithEntityAccess())
            {
                var position = transform.ValueRO.Position;
                var reference = tree.Insert(new SpatialElement
                {
                    Pos = position,
                    Entity = entity,
                });
                if (!reference.IsValid)
                {
                    continue; // 越界：拒绝，下一帧再试
                }

                AddMember(ecb, entity, reference, position);
                applied++;
            }

            // 遍历结束再落地（结构性变更不能在遍历中做）
            ecb.Playback(state.EntityManager);
            ecb.Dispose();

            // ② 位置变化：收集（Burst）→ 应用到树（Burst）。
            var ops = new NativeList<SpatialOp>(Allocator.TempJob);
            try
            {
                state.Dependency = new CollectSpatialMovesJob
                {
                    Ops = ops,
                }.Schedule(state.Dependency);
                state.Dependency.Complete();          // join 一次：ops 要读、树要写回组件
                applied += ApplyOps(ref state, ops, ref tree);
            }
            finally
            {
                ops.Dispose();
            }

            applied += RemoveDestroyed(ref state, ref tree);
            WarnIfOutOfBounds(tree.OutOfBoundsRejected);
            return applied;
        }

        /// <summary>
        /// 把收集到的移动应用到树（<see cref="SpatialTreeApplyOpsJob"/>，Burst）。
        /// </summary>
        private int ApplyOps(ref SystemState state, NativeList<SpatialOp> ops, ref NativeSpatialTree tree)
        {
            if (ops.Length == 0)
            {
                return 0;
            }

            var array = ops.AsArray();
            var results = new NativeArray<SpatialElementRef>(array.Length, Allocator.TempJob);
            var resultTree = new NativeReference<NativeSpatialTree>(Allocator.TempJob);
            try
            {
                state.Dependency = IJobExtensions.Schedule(new SpatialTreeApplyOpsJob
                {
                    Tree = tree,
                    Ops = array,
                    Results = results,
                    ResultTree = resultTree,
                }, state.Dependency);
                state.Dependency.Complete();

                tree = resultTree.Value;
                var applied = 0;
                for (var i = 0; i < results.Length; i++)
                    if (results[i].IsValid)
                    {
                        applied++;
                    }
                return applied;
            }
            finally
            {
                results.Dispose();
                resultTree.Dispose();
            }
        }

        // ───────────────────────────── 工具 ─────────────────────────────

        private static void AddMember(EntityCommandBuffer ecb, Entity entity, in SpatialElementRef reference, float3 position)
        {
            ecb.AddComponent(entity, new SpatialMember
            {
                Slot = reference.Slot,
                Version = reference.Version,
                IndexedPos = position,
            });
            // 句柄在整个生命周期内不变（Update 不改 ref），清理位只写一次。
            ecb.AddComponent(entity, new SpatialMemberDestroyed
            {
                Slot = reference.Slot,
                Version = reference.Version,
            });
        }

        /// <summary>
        /// 销毁：实体没了但清理位还在（<c>ICleanupComponentData</c> 语义）。
        ///
        /// 树参数必须是 <c>ref</c>：<see cref="NativeSpatialTree"/> 是**值语义** struct
        /// （元素池是 <c>UnsafeList</c>、共享内存，但 <c>ElementCount</c>/<c>LeafCount</c> 这些是值字段）。
        ///
        /// 必须排除「还活着」的实体：活实体身上同样挂着这个清理位（它是句柄备份），
        /// 唯一可靠的区分是 <c>WithNone&lt;SpatialIndexed&gt;</c>。
        /// </summary>
        private int RemoveDestroyed(ref SystemState state, ref NativeSpatialTree tree)
        {
            var applied = 0;
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (destroyed, entity) in SystemAPI.Query<RefRO<SpatialMemberDestroyed>>()
                         .WithNone<SpatialIndexed>().WithEntityAccess())
            {
                if (tree.Remove(new SpatialElementRef
                {
                    Slot = destroyed.ValueRO.Slot,
                    Version = destroyed.ValueRO.Version,
                }))
                    applied++;
                ecb.RemoveComponent<SpatialMemberDestroyed>(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            return applied;
        }

        private void ClearAllMembers(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (_, entity) in SystemAPI.Query<RefRO<SpatialMember>>().WithEntityAccess())
                ecb.RemoveComponent<SpatialMember>(entity);
            foreach (var (_, entity) in SystemAPI.Query<RefRO<SpatialMemberDestroyed>>().WithEntityAccess())
                ecb.RemoveComponent<SpatialMemberDestroyed>(entity);

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>
        /// 树的模式/形状与配置是否一致；不一致要重建（树的 Bounds 不可变）。
        /// </summary>
        private static bool Matches(in NativeSpatialTree tree, in SpatialConfig config)
        {
            if (!tree.IsCreated)
            {
                return false;
            }
            if (tree.Kind != config.Index)
            {
                return false;
            }

            var bounds = config.Tree.Bounds;
            var sameBounds = tree.Bounds.Center.Equals(bounds.Center) && tree.Bounds.Extents.Equals(bounds.Extents);
            return tree.MaxDepth == config.Tree.MaxDepth
                   && tree.MaxLeafElements == config.Tree.MaxLeafElements
                   && sameBounds;
        }

        private void WarnIfOutOfBounds(int rejected)
        {
            if (m_WarnedOutOfBounds || !m_Config.Tree.WarnOnOutOfBounds || rejected == 0)
            {
                return;
            }
            m_WarnedOutOfBounds = true;
            Debug.LogWarning(
                $"[Spatial] 有元素落在 {m_Config.Index} 的 Bounds 之外并被拒绝（累计 {rejected} 个）；" +
                $"当前 bounds={m_Config.Tree.Bounds.Center} ±{m_Config.Tree.Bounds.Extents}。" +
                "请调大 SpatialTreeConfig.Bounds。");
        }

        private void Dispose(ref SystemState state)
        {
            if (m_Singleton == Entity.Null)
            {
                return;
            }
            try
            {
                if (!state.World.IsCreated || !state.EntityManager.Exists(m_Singleton))
                {
                    return;
                }

                var data = state.EntityManager.GetComponentData<SpatialSingleton>(m_Singleton);
                if (data.Tree.IsCreated)
                {
                    data.Tree.Dispose();
                }

                // 单例实体也一起清掉。
                state.EntityManager.DestroyEntity(m_Singleton);
                m_Singleton = Entity.Null;
            }
            catch (System.Exception)
            {
                // World 正在销毁：EntityManager 可能已经不可用，这里静默处理。
            }
        }
    }
}
