using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Entities;
using Unity.Rendering;
using UnityEngine;
using UnityEngine.TestTools;

namespace DOTSUtils.RenderBridge.Tests
{
    /// <summary>运行时换材质（立即生效），断言不依赖原 MeshRenderer 是否存活。资产一律走 <see cref="TestRenderAssets"/>：会话级共享、用例之间不销毁。</summary>
    public class RenderMaterialTests
    {
        GameObject m_Root;
        GameObject m_Child;
        MeshRenderer m_Renderer;
        Mesh m_Mesh;
        Material m_MaterialA;
        Material m_MaterialB;

        [SetUp]
        public void SetUp()
        {
            DotsRenderConversionQueue.Clear();

            m_Root = new GameObject("material_root");

            m_Child = new GameObject("material_child");
            m_Child.transform.SetParent(m_Root.transform);

            m_Mesh = TestRenderAssets.NewMesh("material_mesh", 1);
            m_MaterialA = TestRenderAssets.NewMaterial();
            m_MaterialB = TestRenderAssets.NewMaterial();

            m_Child.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
            m_Renderer = m_Child.AddComponent<MeshRenderer>();
            m_Renderer.sharedMaterial = m_MaterialA;
        }

        [TearDown]
        public void TearDown()
        {
            DotsRenderConversionQueue.Clear();
            if (m_Root != null) Object.DestroyImmediate(m_Root);

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;
            var system = world.GetExistingSystemManaged<DotsRenderConversionSystem>();
            if (system == null) return;

            ConversionTests.DestroyAllBridgedEntities();

            // 改注册表前必须先收掉两个 job（它们持有 m_Entities 的读锁）。
            world.GetExistingSystemManaged<DotsRenderTransformSyncSystem>()?.PendingJob.Complete();
            world.GetExistingSystemManaged<DotsRenderTransformPushSystem>()?.PendingJob.Complete();

            // 回收注册表槽位：条目跨用例残留会污染绝对计数断言。
            var registry = system.Registry;
            var entities = registry.Entities;
            var snapshot = new Entity[entities.Length];
            for (var i = 0; i < snapshot.Length; i++) snapshot[i] = entities[i];
            foreach (var entity in snapshot)
                if (entity != Entity.Null) registry.MarkDead(entity);

            registry.PruneDestroyedTransforms();
        }

        static Material[] MaterialsOf(EntityManager em, Entity entity)
        {
            var array = em.GetSharedComponentManaged<RenderMeshArray>(entity);
            var info = em.GetComponentData<MaterialMeshInfo>(entity);
            return array.GetMaterials(info).ToArray();
        }

        [UnityTest]
        public IEnumerator 换材质立即生效()
        {
            var authoring = m_Root.AddComponent<DotsRenderAuthoring>();
            authoring.MeshRenderers.Add(m_Renderer);
            yield return null;
            yield return null;

            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var entities = new List<Entity>();
            Assert.IsTrue(authoring.TryGetEntities(DotsRenderEntityScope.RenderTargets, entities));
            Assert.AreEqual(1, entities.Count);

            Assert.AreEqual(DotsRenderOpResult.Applied,
                authoring.SetRenderMaterials(m_Child, new[] { m_MaterialB }));

            var materials = MaterialsOf(em, entities[0]);
            Assert.AreSame(m_MaterialB, materials[0], "渲染实体的材质应已换成 m_MaterialB");
        }

        [UnityTest]
        public IEnumerator 组件销毁后仍能换材质()
        {
            var authoring = m_Root.AddComponent<DotsRenderAuthoring>();
            authoring.MeshRenderers.Add(m_Renderer);
            authoring.Disposal = OriginalRendererDisposal.DestroyComponents;

            yield return null; // 转换（快照在这里补采）
            yield return null; // Object.Destroy 帧末生效

            Assert.IsTrue(m_Renderer == null, "前提：DestroyComponents 已销毁原 Renderer");

            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var entities = new List<Entity>();
            Assert.IsTrue(authoring.TryGetEntities(DotsRenderEntityScope.RenderTargets, entities));

            // 用 GameObject 作标识：组件没了，但 GameObject 与 DotsRenderLink.Target 都还在。
            Assert.AreEqual(DotsRenderOpResult.Applied,
                authoring.SetRenderMaterials(m_Child, new[] { m_MaterialB }));

            var materials = MaterialsOf(em, entities[0]);
            Assert.AreSame(m_MaterialB, materials[0], "组件已被销毁，但换材质仍应生效");
        }

        [UnityTest]
        public IEnumerator 换成同一组材质时共享同一个RenderMeshArray()
        {
            var second = new GameObject("material_second");
            second.transform.SetParent(m_Root.transform);
            second.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
            var secondRenderer = second.AddComponent<MeshRenderer>();
            secondRenderer.sharedMaterial = m_MaterialA;

            var authoring = m_Root.AddComponent<DotsRenderAuthoring>();
            authoring.MeshRenderers.Add(m_Renderer);
            authoring.MeshRenderers.Add(secondRenderer);
            yield return null;
            yield return null;

            Assert.AreEqual(DotsRenderOpResult.Applied, authoring.SetRenderMaterials(new[] { m_MaterialB }));

            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var entities = new List<Entity>();
            Assert.IsTrue(authoring.TryGetEntities(DotsRenderEntityScope.RenderTargets, entities));
            Assert.AreEqual(2, entities.Count);

            var a = em.GetSharedComponentManaged<RenderMeshArray>(entities[0]);
            var b = em.GetSharedComponentManaged<RenderMeshArray>(entities[1]);
            Assert.IsTrue(a.Equals(b), "同样的 (mesh, 材质) 组合应共享同一个 RenderMeshArray（池去重）");

            Object.DestroyImmediate(second);
        }

        [UnityTest]
        public IEnumerator 只改命中target的那个渲染实体()
        {
            var second = new GameObject("material_other");
            second.transform.SetParent(m_Root.transform);
            second.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
            var secondRenderer = second.AddComponent<MeshRenderer>();
            secondRenderer.sharedMaterial = m_MaterialA;

            var authoring = m_Root.AddComponent<DotsRenderAuthoring>();
            authoring.MeshRenderers.Add(m_Renderer);
            authoring.MeshRenderers.Add(secondRenderer);
            yield return null;
            yield return null;

            Assert.AreEqual(DotsRenderOpResult.Applied,
                authoring.SetRenderMaterials(m_Child, new[] { m_MaterialB }));

            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            var entities = new List<Entity>();
            Assert.IsTrue(authoring.TryGetEntities(DotsRenderEntityScope.RenderTargets, entities));
            Assert.AreEqual(2, entities.Count);

            var first = ConversionTests.FindChildByLinkName(em, "material_child");
            var other = ConversionTests.FindChildByLinkName(em, "material_other");
            Assert.AreNotEqual(Entity.Null, first);
            Assert.AreNotEqual(Entity.Null, other);

            Assert.AreSame(m_MaterialB, MaterialsOf(em, first)[0], "命中的目标应换材质");
            Assert.AreSame(m_MaterialA, MaterialsOf(em, other)[0], "未命中的目标不应被改动");

            Object.DestroyImmediate(second);
        }
    }
}
