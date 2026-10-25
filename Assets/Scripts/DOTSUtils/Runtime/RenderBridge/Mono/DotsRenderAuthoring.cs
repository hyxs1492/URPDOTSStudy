using System;
using System.Collections.Generic;
using DOTSUtils.Worlds;
using Unity.Entities;
using UnityEngine;

namespace DOTSUtils.RenderBridge
{
    /// <summary>
    /// 转换后原 MeshRenderer / LODGroup 的处理方式。
    /// </summary>
    public enum OriginalRendererDisposal
    {
        /// <summary>
        /// 只停止绘制：MeshRenderer 用 forceRenderingOff，LODGroup 只能 enabled = false。可逆。
        /// </summary>
        ForceRenderingOff = 0,

        /// <summary>
        /// 禁用组件：renderer.enabled = false，LODGroup 同样 enabled = false。可逆。
        /// </summary>
        DisableComponents = 1,

        /// <summary>销毁组件：Destroy(renderer) / Destroy(lodGroup)。**注销时不还原渲染**（原组件不会回来），
        /// 但转换数据已快照，再次 Enable 会用快照重建实体。</summary>
        DestroyComponents = 2,
    }

    /// <summary>
    /// 挂在父物体上，把 <see cref="MeshRenderers"/> / <see cref="LodGroups"/> 里显式指定的子对象绘制
    /// 交给 Entities Graphics。父物体对应 1 个逻辑实体（只同步变换），每个目标生成 1 个（组）子实体。
    /// 其余组件（自定义脚本、Collider、Rigidbody 等）保持原样工作。
    ///
    /// 可以派生（例如 <c>ShipController : DotsRenderAuthoring</c>）；重写 <see cref="OnEnable"/> /
    /// <see cref="OnDisable"/> 时**必须调用 base**，否则转换与注销不会入队。
    ///
    /// 还能直接读写「其实体」：<see cref="AddComponent{T}"/> / <see cref="SetComponent{T}"/> /
    /// <see cref="GetComponent{T}"/> / <see cref="SetComponentEnabled{T}"/> / …（实体还没创建时自动排队）；
    /// 或重写 <see cref="OnCreateEntitySuccess"/> 在实体就绪后做初始化。
    ///
    /// **写入路径**：结构性变更记录到 <c>DotsRenderConversionSystem</c> 持有的
    /// <see cref="EntityCommandBuffer"/>，记录完立即 playback（一次调用 = N 条记录 = 一次 playback），
    /// 方法返回后立刻可读。
    /// </summary>
    [DisallowMultipleComponent]
    public class DotsRenderAuthoring : MonoBehaviour
    {
        [Header("转DOTS")]
        [Tooltip("要转成 DOTS 渲染的 MeshRenderer（显式指定，不会自动收集）。")]
        public List<MeshRenderer> MeshRenderers = new();

        [Tooltip("要转成 DOTS 渲染的 LODGroup（显式指定，不会自动收集）。")]
        public List<LODGroup> LodGroups = new();

        [Tooltip("转换后原 MeshRenderer / LODGroup 如何处理。DestroyComponents 不还原原组件（注销时它们回不来），" +
                 "但转换数据已快照，再次 Enable 会用快照重建实体。")]
        public OriginalRendererDisposal Disposal = OriginalRendererDisposal.ForceRenderingOff;

        [Tooltip("勾选后，该父物体的变换不再由 GameObject 同步到实体（父实体的 LocalTransform 交给实体侧驱动）。")]
        public bool TransformDrivenByEntity;

        private AuthoringSnapshot m_Snapshot;

        /// <summary>
        /// 快照里的渲染目标数；0 表示尚未采集到快照。调试用。
        /// </summary>
        public int SnapshotRenderTargetCount => m_Snapshot?.RenderTargetCount ?? 0;

        internal AuthoringSnapshot SnapshotForConversion => m_Snapshot;

        protected virtual void OnEnable()
        {
            InitializeEntity();
        }

        public virtual void InitializeEntity()
        {
            CaptureSnapshot();

            DotsRenderConversionQueue.EnqueueRegister(gameObject);
        }

        /// <summary>
        /// 把转换所需的数据固化成不依赖组件存活的快照。幂等：已有快照时直接返回。
        /// 采不到有效数据（两个列表为空，或组件已被销毁导致全是 fake-null）时**保留旧快照**。
        ///
        /// 注意：快照记录的是**采集那一刻**的配置，之后改 renderer 的材质/mesh 不会反映进来。
        /// 要立刻换材质请用 <c>SetRenderMaterials</c>。
        /// </summary>
        public void CaptureSnapshot()
        {
            if (m_Snapshot != null)
            {
                return;
            }

            var system = ResolveSystem();
            if (system == null)
            {
                return; // 没有 World（编辑期）时不采集
            }

            var captured = AuthoringSnapshot.TryCapture(MeshRenderers, LodGroups, transform, system.Pool);
            if (captured != null)
            {
                m_Snapshot = captured;
            }
        }



        /// <summary>
        /// 该对象的所有实体创建完成后回调一次（主线程）。参数是逻辑父实体；
        /// 此刻 <see cref="TryGetParentEntity"/> / <see cref="TryGetEntities"/> 已可用，门面方法也会立即生效
        /// （排队操作在此之前已经记录并 playback 过了）。
        /// </summary>
        public virtual void OnCreateEntitySuccess(Entity e)
        {
        }

        protected virtual void OnDisable() => DotsRenderConversionQueue.EnqueueUnregister(gameObject);

        // ------------------------------------------------------------------
        //  句柄查询（只读，主线程）
        // ------------------------------------------------------------------

        /// <summary>
        /// 该父物体的逻辑父实体；未转换 / 已注销时为 false。
        /// </summary>
        public bool TryGetParentEntity(out Entity entity)
        {
            entity = Entity.Null;
            var system = ResolveSystem();
            return system != null && system.TryGetParentEntity(gameObject, out entity);
        }

        /// <summary>
        /// 渲染实体数（不含父实体）；未转换 / 已注销时为 0。
        /// </summary>
        public int RenderEntityCount
        {
            get
            {
                var system = ResolveSystem();
                return system == null ? 0 : system.GetRenderEntities(gameObject, s_Scratch);
            }
        }

        /// <summary>
        /// 按下标取渲染实体（index ∈ [0, <see cref="RenderEntityCount"/>)）。
        /// </summary>
        public bool TryGetRenderEntity(int index, out Entity entity, out DotsRenderTargetKind kind)
        {
            entity = Entity.Null;
            kind = DotsRenderTargetKind.Parent;

            var system = ResolveSystem();
            if (system == null)
            {
                return false;
            }
            if (!system.TryGetEntities(gameObject, DotsRenderEntityScope.RenderTargets, s_Scratch))
            {
                return false;
            }
            if (index < 0 || index >= s_Scratch.Count)
            {
                return false;
            }

            entity = s_Scratch[index];
            kind = DotsWorldManager.CurrentWorld.EntityManager.GetComponentData<DotsRenderLink>(entity).Kind;
            return true;
        }

        /// <summary>
        /// 按 scope 收集实体；未转换 / 已注销时返回 false。
        /// </summary>
        public bool TryGetEntities(DotsRenderEntityScope scope, List<Entity> results)
        {
            var system = ResolveSystem();
            return system != null && system.TryGetEntities(gameObject, scope, results);
        }

        // ------------------------------------------------------------------
        //  写（默认只作用父实体）—— 记录到 ECB，随后立即 playback
        //
        //  要一并改渲染实体，显式传 scope（DotsRenderEntityScope.RenderTargets / All）。
        // ------------------------------------------------------------------

        public DotsRenderOpResult AddComponent<T>(DotsRenderEntityScope scope = DotsRenderEntityScope.Parent)
            where T : unmanaged, IComponentData =>
            ApplyToScope(scope, (em, ecb, entity) =>
            {
                if (!em.HasComponent<T>(entity))
                {
                    ecb.AddComponent<T>(entity);
                }
            });

        public DotsRenderOpResult AddBuffer<T>(DotsRenderEntityScope scope = DotsRenderEntityScope.Parent)
            where T : unmanaged, IBufferElementData =>
            ApplyToScope(scope, (em, ecb, entity) =>
            {
                if (!em.HasBuffer<T>(entity))
                {
                    ecb.AddBuffer<T>(entity);
                }
            });

        public DotsRenderOpResult SetComponent<T>(T value, DotsRenderEntityScope scope = DotsRenderEntityScope.Parent)
            where T : unmanaged, IComponentData =>
            ApplyToScope(scope, (em, ecb, entity) =>
            {
                if (!em.HasComponent<T>(entity))
                {
                    Debug.LogWarning($"[DotsRender] {name} 的实体没有组件 {typeof(T).Name}，SetComponent 被忽略。");
                    return;
                }
                ecb.SetComponent(entity, value);
            });

        /// <summary>
        /// 把某个渲染目标（用它的 GameObject 标识）的材质换成 <paramref name="materials"/>，**立即生效**。
        ///
        /// 用 GameObject 而不是组件来标识：<see cref="OriginalRendererDisposal.DestroyComponents"/> 会把
        /// MeshRenderer 销毁，而 GameObject 与实体上的 <see cref="DotsRenderLink.Target"/> 都还在 ——
        /// 它是组件消失后唯一稳定的定位方式。
        /// </summary>
        public DotsRenderOpResult SetRenderMaterials(GameObject target, Material[] materials)
        {
            if (target == null)
            {
                return DotsRenderOpResult.Failed;
            }

            var system = ResolveSystem();
            if (system == null)
            {
                Debug.LogWarning($"[DotsRender] {name} 需要 Play 模式（没有 EntityManager）才能操作实体。");
                return DotsRenderOpResult.Failed;
            }

            var entities = new List<Entity>();
            if (!system.TryGetEntities(gameObject, DotsRenderEntityScope.RenderTargets, entities))
            {
                // 实体还没创建：排队，等转换完成后再应用。
                DotsRenderConversionQueue.EnqueueOperation(gameObject, (_, _) =>
                {
                    var lateSystem = ResolveSystem();
                    if (lateSystem == null)
                    {
                        return;
                    }

                    var late = new List<Entity>();
                    if (!lateSystem.TryGetEntities(gameObject, DotsRenderEntityScope.RenderTargets, late))
                    {
                        return;
                    }

                    foreach (var entity in late)
                    {
                        ApplyMaterialsIfMatch(lateSystem, entity, target, materials);
                    }
                });
                return DotsRenderOpResult.Queued;
            }

            var applied = false;
            foreach (var entity in entities)
                applied |= ApplyMaterialsIfMatch(system, entity, target, materials);

            return applied ? DotsRenderOpResult.Applied : DotsRenderOpResult.Failed;
        }

        /// <summary>
        /// 便利重载：作用于该对象的所有渲染实体（LOD 各层会换成同一组材质）。
        /// </summary>
        public DotsRenderOpResult SetRenderMaterials(Material[] materials)
        {
            var system = ResolveSystem();
            if (system == null)
            {
                return DotsRenderOpResult.Failed;
            }

            var entities = new List<Entity>();
            if (!system.TryGetEntities(gameObject, DotsRenderEntityScope.RenderTargets, entities))
                return DotsRenderOpResult.Failed;

            var applied = false;
            foreach (var entity in entities)
                applied |= system.SetEntityMaterials(entity, materials);

            return applied ? DotsRenderOpResult.Applied : DotsRenderOpResult.Failed;
        }

        private static bool ApplyMaterialsIfMatch(DotsRenderConversionSystem system, Entity entity, GameObject target, Material[] materials)
        {
            var em = DotsWorldManager.CurrentWorld.EntityManager;
            if (!em.HasComponent<DotsRenderLink>(entity))
            {
                return false;
            }
            if (em.GetComponentData<DotsRenderLink>(entity).Target.Value != target)
            {
                return false;
            }
            return system.SetEntityMaterials(entity, materials);
        }

        public DotsRenderOpResult RemoveComponent<T>(DotsRenderEntityScope scope = DotsRenderEntityScope.Parent)
            where T : unmanaged, IComponentData =>
            ApplyToScope(scope, (em, ecb, entity) =>
            {
                if (em.HasComponent<T>(entity))
                {
                    ecb.RemoveComponent<T>(entity);
                }
            });

        public DotsRenderOpResult SetComponentEnabled<T>(bool enabled, DotsRenderEntityScope scope = DotsRenderEntityScope.Parent)
            where T : unmanaged, IEnableableComponent =>
            ApplyToScope(scope, (em, ecb, entity) =>
            {
                if (!em.HasComponent<T>(entity))
                {
                    Debug.LogWarning($"[DotsRender] {name} 的实体没有可启用组件 {typeof(T).Name}，SetComponentEnabled 被忽略。");
                    return;
                }
                ecb.SetComponentEnabled<T>(entity, enabled);
            });

        // ------------------------------------------------------------------
        //  读（默认只读父实体；多个渲染实体各持有一份值，"读谁"没有唯一答案）
        //  写操作都是"记录完立即 playback"，这里直接读 EntityManager 就是最新值。
        // ------------------------------------------------------------------

        public bool TryGetComponent<T>(out T value, DotsRenderEntityScope scope = DotsRenderEntityScope.Parent)
            where T : unmanaged, IComponentData
        {
            value = default;

            var system = ResolveSystem();
            if (system == null)
            {
                return false;
            }
            if (!system.TryGetEntities(gameObject, scope, s_Scratch))
            {
                return false;
            }

            var em = DotsWorldManager.CurrentWorld.EntityManager;
            var entity = s_Scratch[0];
            if (!em.HasComponent<T>(entity))
            {
                return false;
            }
            value = em.GetComponentData<T>(entity);
            return true;
        }

        public T GetComponent<T>(DotsRenderEntityScope scope = DotsRenderEntityScope.Parent)
            where T : unmanaged, IComponentData
        {
            if (TryGetComponent<T>(out var value, scope))
            {
                return value;
            }

            Debug.LogWarning($"[DotsRender] {name} 读不到组件 {typeof(T).Name}（实体未就绪或没有该组件）。");
            return default;
        }

        public bool TryGetBuffer<T>(out DynamicBuffer<T> buffer, DotsRenderEntityScope scope = DotsRenderEntityScope.Parent)
            where T : unmanaged, IBufferElementData
        {
            buffer = default;

            var system = ResolveSystem();
            if (system == null)
            {
                return false;
            }
            if (!system.TryGetEntities(gameObject, scope, s_Scratch))
            {
                return false;
            }

            var em = DotsWorldManager.CurrentWorld.EntityManager;
            var entity = s_Scratch[0];
            if (!em.HasBuffer<T>(entity))
            {
                return false;
            }
            buffer = em.GetBuffer<T>(entity);
            return true;
        }

        public DynamicBuffer<T> GetBuffer<T>(DotsRenderEntityScope scope = DotsRenderEntityScope.Parent)
            where T : unmanaged, IBufferElementData
        {
            if (TryGetBuffer<T>(out var buffer, scope))
            {
                return buffer;
            }

            Debug.LogWarning($"[DotsRender] {name} 读不到 buffer {typeof(T).Name}（实体未就绪或没有该 buffer）。");
            return default;
        }

        // ------------------------------------------------------------------
        //  调试入口（门面在"未就绪"时走的也是这同一条排队路径）
        // ------------------------------------------------------------------

        /// <summary>
        /// 把一个"对每个命中实体记录一次"的操作排队（等价于门面在实体未就绪时的行为）。
        /// 记录目标是 <see cref="EntityCommandBuffer"/>，由转换系统在应用阶段统一 playback。
        /// </summary>
        public void EnqueueForTest(Action<EntityCommandBuffer, Entity> recordOnEachEntity)
        {
            if (recordOnEachEntity == null)
            {
                return;
            }

            DotsRenderConversionQueue.EnqueueOperation(gameObject, (ecb, _) =>
            {
                var system = ResolveSystem();
                if (system == null)
                {
                    return;
                }

                var entities = new List<Entity>();
                if (!system.TryGetEntities(gameObject, DotsRenderEntityScope.All, entities))
                {
                    return;
                }
                foreach (var entity in entities)
                {
                    recordOnEachEntity(ecb, entity);
                }
            });
        }

        /// <summary>
        /// 当前待应用操作数（调试用）。
        /// </summary>
        public int PendingOperationCountForTest => DotsRenderConversionQueue.CountOperations(gameObject);

        // ------------------------------------------------------------------
        //  内部
        // ------------------------------------------------------------------

        private static readonly List<Entity> s_Scratch = new();

        private static DotsRenderConversionSystem ResolveSystem()
        {
            var world = DotsWorldManager.CurrentWorld;
            return world != null && world.IsCreated
                ? world.GetExistingSystemManaged<DotsRenderConversionSystem>()
                : null;
        }

        /// <summary>
        /// 把操作记录到系统的 ECB 并立刻 playback；实体还没创建就把整个操作排队
        /// （转换完成后由系统统一记录 + playback）。
        /// 记录前用 EntityManager 做幂等/存在性检查 —— 上一批已经 playback，看到的是最新状态。
        /// </summary>
        private DotsRenderOpResult ApplyToScope(DotsRenderEntityScope scope, Action<EntityManager, EntityCommandBuffer, Entity> record)
        {
            var system = ResolveSystem();
            if (system == null)
            {
                Debug.LogWarning($"[DotsRender] {name} 需要 Play 模式（没有 EntityManager）才能操作实体。");
                return DotsRenderOpResult.Failed;
            }
            if (!enabled)
            {
                Debug.LogWarning($"[DotsRender] {name} 的 DotsRenderAuthoring 已禁用（已注销或即将注销），操作被拒绝。");
                return DotsRenderOpResult.Failed;
            }

            var em = DotsWorldManager.CurrentWorld.EntityManager;
            var entities = new List<Entity>();
            if (system.TryGetEntities(gameObject, scope, entities))
            {
                // 一次调用 = N 条记录 = 一次 playback（同一批内的结构性变更合并成一次同步）。
                var ecb = system.CommandBuffer;
                foreach (var entity in entities)
                {
                    record(em, ecb, entity);
                }
                system.FlushCommands();
                return DotsRenderOpResult.Applied;
            }

            DotsRenderConversionQueue.EnqueueOperation(gameObject, (ecb, _) =>
            {
                var lateSystem = ResolveSystem();
                if (lateSystem == null)
                {
                    return;
                }

                var lateEm = DotsWorldManager.CurrentWorld.EntityManager;
                var late = new List<Entity>();
                if (!lateSystem.TryGetEntities(gameObject, scope, late))
                {
                    return;
                }
                foreach (var entity in late)
                {
                    record(lateEm, ecb, entity);
                }
            });
            return DotsRenderOpResult.Queued;
        }
    }
}
