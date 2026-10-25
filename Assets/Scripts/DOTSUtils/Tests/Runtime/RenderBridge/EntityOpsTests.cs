using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace DOTSUtils.RenderBridge.Tests
{
    /// <summary>测试用的空组件，实现 <see cref="IEnableableComponent"/> 供 SetComponentEnabled 用例使用。</summary>
    public struct OpsProbe : IComponentData, IEnableableComponent
    {
        public int Value;
    }

    /// <summary>测试用的 buffer 元素。</summary>
    public struct OpsBuffer : IBufferElementData
    {
        public int Value;
    }

    /// <summary>记录就绪回调的测试用派生类；必须放在文件顶层，Unity 才能 <c>AddComponent</c>。</summary>
    public class RecordingAuthoring : DotsRenderAuthoring
    {
        public int CreatedCount;
        public Entity LastParent;
        public bool ParentHadProbeAtCallback;

        public override void OnCreateEntitySuccess(Entity e)
        {
            CreatedCount++;
            LastParent = e;

            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            ParentHadProbeAtCallback = em.Exists(e) && em.HasComponent<OpsProbe>(e);
        }
    }

    /// <summary>Entity Ops：<see cref="DotsRenderLink.Kind"/> 标注、实体查询、排队、就绪回调、门面读写。与其它用例共用同一个 World，断言一律用相对量，并只清理自己造的东西。</summary>
    public class EntityOpsTests
    {
        GameObject m_Source;
        GameObject m_Root;
        Mesh m_Mesh;
        Material m_Material;

        [SetUp]
        public void SetUp()
        {
            DotsRenderConversionQueue.Clear();
            m_Mesh = TestRenderAssets.NewMesh("ops-mesh", 1);
            // 非零包围盒：RenderBounds / WorldRenderBounds 由 mesh.bounds 推导。
            m_Mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            m_Material = TestRenderAssets.NewMaterial();

            m_Source = new GameObject("ops-source");
            m_Root = new GameObject("ops-root");
            m_Root.transform.SetParent(m_Source.transform);

            var group = m_Root.AddComponent<LODGroup>();
            var lod0 = AddLodChild(m_Root.transform, "lod0");
            var lod1 = AddLodChild(m_Root.transform, "lod1");
            group.SetLODs(new[]
            {
                new LOD(0.6f, new Renderer[] { lod0 }),
                new LOD(0.1f, new Renderer[] { lod1 }),
            });
            group.RecalculateBounds();
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Source != null) Object.DestroyImmediate(m_Source);

            var world = World.DefaultGameObjectInjectionWorld;
            if (world != null && world.IsCreated)
            {
                // 显式清一遍桥接实体（注销请求会被下面的 Clear 丢弃）。
                ConversionTests.DestroyAllBridgedEntities();
            }
            DotsRenderConversionQueue.Clear();
        }

        MeshRenderer AddLodChild(Transform parent, string name)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent);
            child.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
            var renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = m_Material;
            return renderer;
        }

        /// <summary>给父物体挂标记、把 LODGroup 填进 LodGroups 列表。</summary>
        DotsRenderAuthoring MarkLod()
        {
            var authoring = m_Source.AddComponent<DotsRenderAuthoring>();
            authoring.LodGroups.Add(m_Root.GetComponent<LODGroup>());
            return authoring;
        }

        /// <summary>世界里各类 Kind 的桥接实体数量，用于相对计数。</summary>
        static Dictionary<DotsRenderTargetKind, int> CountByKind(EntityManager em)
        {
            var counts = new Dictionary<DotsRenderTargetKind, int>();
            var query = em.CreateEntityQuery(ComponentType.ReadOnly<DotsRenderLink>());
            var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                var kind = em.GetComponentData<DotsRenderLink>(entity).Kind;
                counts.TryGetValue(kind, out var count);
                counts[kind] = count + 1;
            }
            entities.Dispose();
            query.Dispose();
            return counts;
        }

        static int CountOf(Dictionary<DotsRenderTargetKind, int> counts, DotsRenderTargetKind kind) =>
            counts.TryGetValue(kind, out var value) ? value : 0;

        // ------------------------------------------------------------------
        //  Kind 标注
        // ------------------------------------------------------------------

        [UnityTest]
        public IEnumerator DotsRenderLink_Kind标注父组层与子渲染实体()
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var before = CountByKind(em);

            MarkLod();
            yield return null;   // 转换
            yield return null;

            var after = CountByKind(em);
            Assert.AreEqual(1, CountOf(after, DotsRenderTargetKind.Parent) - CountOf(before, DotsRenderTargetKind.Parent),
                "应新增 1 个父实体");
            Assert.AreEqual(1, CountOf(after, DotsRenderTargetKind.LodGroup) - CountOf(before, DotsRenderTargetKind.LodGroup),
                "应新增 1 个 LOD 组实体");
            Assert.AreEqual(2, CountOf(after, DotsRenderTargetKind.LodLayer) - CountOf(before, DotsRenderTargetKind.LodLayer),
                "应新增 2 个层实体");
            Assert.AreEqual(0, CountOf(after, DotsRenderTargetKind.MeshRenderer) - CountOf(before, DotsRenderTargetKind.MeshRenderer),
                "本夹具的 MeshRenderer 都在 LODGroup 下，不该产生 MeshRenderer 类实体");
        }

        // ------------------------------------------------------------------
        //  实体查询 API
        // ------------------------------------------------------------------

        [UnityTest]
        public IEnumerator 查询API_按scope返回实体_未转换时为false()
        {
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var conversion = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<DotsRenderConversionSystem>();
            var buffer = new List<Entity>();

            Assert.IsFalse(conversion.TryGetEntities(m_Source, DotsRenderEntityScope.All, buffer), "未转换时应返回 false");
            Assert.IsFalse(conversion.TryGetParentEntity(m_Source, out _), "未转换时没有父实体");
            Assert.AreEqual(0, conversion.GetRenderEntities(m_Source, buffer));

            var unmarked = new GameObject("ops-unmarked");
            try
            {
                Assert.IsFalse(conversion.TryGetEntities(unmarked, DotsRenderEntityScope.All, buffer),
                    "未标记的对象应返回 false");
            }
            finally
            {
                Object.DestroyImmediate(unmarked);
            }

            MarkLod();
            yield return null;
            yield return null;

            Assert.IsTrue(conversion.TryGetParentEntity(m_Source, out var parent), "转换后应能拿到父实体");
            Assert.AreEqual(DotsRenderTargetKind.Parent, em.GetComponentData<DotsRenderLink>(parent).Kind);

            Assert.AreEqual(3, conversion.GetRenderEntities(m_Source, buffer), "1 组 + 2 层 = 3 个渲染实体");
            Assert.IsTrue(conversion.TryGetEntities(m_Source, DotsRenderEntityScope.All, buffer));
            Assert.AreEqual(4, buffer.Count, "All = 父实体 + 3 个渲染实体");
            Assert.AreEqual(parent, buffer[0], "父实体恒为第一个（created 顺序）");

            Assert.IsTrue(conversion.TryGetEntities(m_Source, DotsRenderEntityScope.RenderTargets, buffer));
            Assert.AreEqual(3, buffer.Count, "RenderTargets 不含父实体");
        }

        // ------------------------------------------------------------------
        //  排队与就绪回调
        // ------------------------------------------------------------------

        [UnityTest]
        public IEnumerator 排队_转换前记录的操作在实体就绪后生效()
        {
            var authoring = MarkLod();
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;

            authoring.EnqueueForTest((ecb, entity) => ecb.AddComponent<OpsProbe>(entity));
            Assert.AreEqual(1, authoring.PendingOperationCountForTest, "未就绪的调用应进队列");

            yield return null;   // 转换 + 记录到 ECB + playback
            yield return null;

            Assert.AreEqual(0, authoring.PendingOperationCountForTest, "应用后队列应清空");
            Assert.IsTrue(authoring.TryGetParentEntity(out var parent));
            Assert.IsTrue(em.HasComponent<OpsProbe>(parent), "排队的操作应在实体就绪后生效（经 ECB playback）");
        }

        [UnityTest]
        public IEnumerator 就绪回调_转换后被调用一次且发生在排队操作之后()
        {
            var authoring = m_Source.AddComponent<RecordingAuthoring>();
            authoring.LodGroups.Add(m_Root.GetComponent<LODGroup>());
            authoring.EnqueueForTest((ecb, entity) => ecb.AddComponent<OpsProbe>(entity));

            yield return null;
            yield return null;

            Assert.AreEqual(1, authoring.CreatedCount, "转换后应恰好回调一次");
            Assert.IsTrue(authoring.TryGetParentEntity(out var parent));
            Assert.AreEqual(parent, authoring.LastParent, "回调参数应是父实体");
            Assert.IsTrue(authoring.ParentHadProbeAtCallback, "回调应发生在排队操作 playback 之后");
        }

        [UnityTest]
        public IEnumerator 排队_对象未转换时被丢弃并警告()
        {
            var bare = new GameObject("ops-bare");
            try
            {
                var authoring = bare.AddComponent<DotsRenderAuthoring>();
                authoring.EnqueueForTest((ecb, entity) => ecb.AddComponent<OpsProbe>(entity));

                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("两个列表都为空"));

                yield return null;
                yield return null;

                Assert.AreEqual(0, authoring.PendingOperationCountForTest, "未转换对象的排队操作应被丢弃");
            }
            finally
            {
                Object.DestroyImmediate(bare);
            }
        }

        // ------------------------------------------------------------------
        //  门面
        // ------------------------------------------------------------------

        [UnityTest]
        public IEnumerator 门面_就绪后读写与开关与buffer与移除都生效()
        {
            var authoring = MarkLod();
            yield return null;
            yield return null;

            Assert.AreEqual(DotsRenderOpResult.Applied, authoring.AddComponent<OpsProbe>(), "就绪后应立即执行");
            Assert.AreEqual(DotsRenderOpResult.Applied, authoring.SetComponent(new OpsProbe { Value = 7 }), "先 Add 再 Set");
            Assert.AreEqual(7, authoring.GetComponent<OpsProbe>().Value, "写经 ECB playback，返回后应立即可读");
            Assert.IsTrue(authoring.TryGetComponent<OpsProbe>(out var probe));
            Assert.AreEqual(7, probe.Value);

            Assert.AreEqual(DotsRenderOpResult.Applied, authoring.SetComponentEnabled<OpsProbe>(false));

            Assert.AreEqual(DotsRenderOpResult.Applied, authoring.AddBuffer<OpsBuffer>());
            Assert.IsTrue(authoring.TryGetBuffer<OpsBuffer>(out var buffer));
            buffer.Add(new OpsBuffer { Value = 3 });
            Assert.AreEqual(1, authoring.GetBuffer<OpsBuffer>().Length, "buffer 写入应可见");

            Assert.AreEqual(DotsRenderOpResult.Applied, authoring.RemoveComponent<OpsProbe>());
            Assert.IsFalse(authoring.TryGetComponent<OpsProbe>(out _), "移除后读不到");
        }

        [UnityTest]
        public IEnumerator 门面_scope分别作用父实体与渲染实体()
        {
            var authoring = MarkLod();
            yield return null;
            yield return null;

            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var before = ConversionTests.CountBridgedEntities(em);

            Assert.AreEqual(DotsRenderOpResult.Applied, authoring.AddComponent<OpsProbe>(DotsRenderEntityScope.Parent));
            Assert.IsTrue(authoring.TryGetParentEntity(out var parent));
            Assert.IsTrue(em.HasComponent<OpsProbe>(parent), "Parent scope 应作用于父实体");
            Assert.AreEqual(before, ConversionTests.CountBridgedEntities(em), "加组件不改变桥接实体数");

            var renderEntities = new List<Entity>();
            Assert.IsTrue(authoring.TryGetEntities(DotsRenderEntityScope.RenderTargets, renderEntities));
            Assert.AreEqual(3, renderEntities.Count, "1 组 + 2 层");
            foreach (var entity in renderEntities)
                Assert.IsFalse(em.HasComponent<OpsProbe>(entity), "Parent scope 不应波及渲染实体");

            Assert.AreEqual(DotsRenderOpResult.Applied,
                authoring.AddBuffer<OpsBuffer>(DotsRenderEntityScope.RenderTargets));
            foreach (var entity in renderEntities)
                Assert.IsTrue(em.HasBuffer<OpsBuffer>(entity), "RenderTargets scope 应作用于全部渲染实体");
            Assert.IsFalse(em.HasBuffer<OpsBuffer>(parent), "RenderTargets scope 不应给父实体加 buffer");
        }

        [UnityTest]
        public IEnumerator 门面_安全失败路径()
        {
            var authoring = MarkLod();

            Assert.AreEqual(DotsRenderOpResult.Queued, authoring.AddComponent<OpsProbe>(), "未就绪应排队");
            Assert.AreEqual(1, authoring.PendingOperationCountForTest);
            Assert.AreEqual(DotsRenderOpResult.Queued, authoring.SetComponent(new OpsProbe { Value = 1 }), "排队同样受理");

            yield return null;
            yield return null;

            Assert.IsTrue(authoring.TryGetComponent<OpsProbe>(out var probe), "排队操作应已应用");
            Assert.AreEqual(1, probe.Value, "按调用顺序：先 Add 再 Set，最终值是 Set 的");

            authoring.enabled = false;
            Assert.AreEqual(DotsRenderOpResult.Failed, authoring.AddComponent<OpsProbe>(), "禁用状态应 Failed 且不排队");
            Assert.AreEqual(0, authoring.PendingOperationCountForTest);
            authoring.enabled = true;
        }
    }
}
