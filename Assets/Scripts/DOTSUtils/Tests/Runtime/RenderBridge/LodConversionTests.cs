using System.Collections;
using NUnit.Framework;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace DOTSUtils.RenderBridge.Tests
{
    public class LodConversionTests
    {
        GameObject m_Root;
        GameObject m_LodGroupGo;
        MeshRenderer m_Lod0;
        MeshRenderer m_Lod1;
        Mesh m_Mesh;
        Material m_Material;

        [SetUp]
        public void SetUp()
        {
            DotsRenderConversionQueue.Clear();
            // Mesh/Material 由 TestRenderAssets 统一登记，用例之间绝不销毁。
            m_Mesh = TestRenderAssets.NewMesh("lod-mesh", 1);
            m_Material = TestRenderAssets.NewMaterial();
            m_Root = new GameObject("lod-root");
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Root != null) Object.DestroyImmediate(m_Root);
            DotsRenderConversionQueue.Clear();
            // 注销请求刚被上面的 Clear 丢弃，不在这里显式清理会让上一个用例的实体残留，污染绝对计数断言。
            ConversionTests.DestroyAllBridgedEntities();
            ReclaimRegistry();

            // 兜底：确保 registry 不再持有本用例已销毁对象的槽位。
            var world = World.DefaultGameObjectInjectionWorld;
            if (world != null && world.IsCreated)
            {
                var conversionSystem = world.GetExistingSystemManaged<DotsRenderConversionSystem>();
                conversionSystem?.Registry.PruneDestroyedTransforms();
            }
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

        /// <summary>父物体挂标记，把 1 个二层 LODGroup（两层各一个 MeshRenderer）填进 LodGroups。层级：父物体 → lod-group(LODGroup) → { lod0, lod1 }。</summary>
        DotsRenderAuthoring MarkTwoLevelLod()
        {
            m_LodGroupGo = new GameObject("lod-group");
            m_LodGroupGo.transform.SetParent(m_Root.transform);
            var group = m_LodGroupGo.AddComponent<LODGroup>();
            m_Lod0 = AddLodChild(m_LodGroupGo.transform, "lod0");
            m_Lod1 = AddLodChild(m_LodGroupGo.transform, "lod1");
            group.SetLODs(new[]
            {
                new LOD(0.6f, new Renderer[] { m_Lod0 }),
                new LOD(0.1f, new Renderer[] { m_Lod1 }),
            });
            group.RecalculateBounds();

            var authoring = m_Root.AddComponent<DotsRenderAuthoring>();
            authoring.LodGroups.Add(group);
            return authoring;
        }

        static int Count<T>(EntityManager em) where T : unmanaged, IComponentData
        {
            var query = em.CreateEntityQuery(ComponentType.ReadOnly<T>());
            var count = query.CalculateEntityCount();
            query.Dispose();
            return count;
        }

        /// <summary>回收注册表槽位，避免条目跨用例残留而干扰后续同步系统。</summary>
        static void ReclaimRegistry()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;

            var system = world.GetExistingSystemManaged<DotsRenderConversionSystem>();
            if (system == null) return;

            // MarkDead 会改动注册表底层容器，而两个 job 都持有它的视图，先完成依赖。
            world.GetExistingSystemManaged<DotsRenderTransformSyncSystem>()?.PendingJob.Complete();
            world.GetExistingSystemManaged<DotsRenderTransformPushSystem>()?.PendingJob.Complete();

            var registry = system.Registry;
            var entities = registry.Entities; // MarkDead 会改动底层，先做快照
            var snapshot = new Entity[entities.Length];
            for (var i = 0; i < snapshot.Length; i++) snapshot[i] = entities[i];
            foreach (var entity in snapshot)
                if (entity != Entity.Null) registry.MarkDead(entity);
        }

        [UnityTest]
        public IEnumerator LODGroup生成层级实体并挂在父实体下()
        {
            var authoring = MarkTwoLevelLod();
            yield return null;

            var em = World.DefaultGameObjectInjectionWorld.EntityManager;

            // 1 父实体 + 1 组实体 + 2 层实体 = 4 个桥接实体（都带 DotsRenderLink）。
            Assert.AreEqual(4, ConversionTests.CountBridgedEntities(em), "应恰好 4 个桥接实体（父 + 组 + 两层）");
            Assert.AreEqual(1, Count<MeshLODGroupComponent>(em), "应恰好 1 个 LOD 组实体");
            Assert.AreEqual(2, Count<MeshLODComponent>(em), "两层应各有一个实体");

            var parent = ConversionTests.GetParentEntity(em);
            var groupEntity = ConversionTests.FindChildByLinkName(em, "lod-group");
            Assert.AreNotEqual(Entity.Null, groupEntity, "应存在 LOD 组实体");

            Assert.IsTrue(em.HasComponent<MeshLODGroupComponent>(groupEntity), "组实体应带 MeshLODGroupComponent");
            Assert.AreEqual(parent, em.GetComponentData<Parent>(groupEntity).Value, "组实体必须挂在父实体下");

            var lod0Entity = ConversionTests.FindChildByLinkName(em, "lod0");
            var lod1Entity = ConversionTests.FindChildByLinkName(em, "lod1");
            Assert.AreNotEqual(Entity.Null, lod0Entity, "应存在 LOD0 层实体");
            Assert.AreNotEqual(Entity.Null, lod1Entity, "应存在 LOD1 层实体");

            Assert.AreEqual(groupEntity, em.GetComponentData<Parent>(lod0Entity).Value, "LOD0 层实体必须挂在组实体下");
            Assert.AreEqual(groupEntity, em.GetComponentData<Parent>(lod1Entity).Value, "LOD1 层实体必须挂在组实体下");
            Assert.AreEqual(groupEntity, em.GetComponentData<MeshLODComponent>(lod0Entity).Group, "LOD0 层实体必须指回组实体");
            Assert.AreEqual(groupEntity, em.GetComponentData<MeshLODComponent>(lod1Entity).Group, "LOD1 层实体必须指回组实体");
            Assert.AreEqual(1, em.GetComponentData<MeshLODComponent>(lod0Entity).LODMask, "LOD0 的掩码应为 1 << 0 == 1");
            Assert.AreEqual(2, em.GetComponentData<MeshLODComponent>(lod1Entity).LODMask, "LOD1 的掩码应为 1 << 1 == 2");

            // 层实体必须带渲染组件。
            Assert.IsTrue(em.HasComponent<MaterialMeshInfo>(lod0Entity), "LOD0 层实体缺少 MaterialMeshInfo");
            Assert.IsTrue(em.HasComponent<LocalToWorld>(lod0Entity), "LOD0 层实体缺少 LocalToWorld");

            Assert.IsTrue(m_Lod0.forceRenderingOff, "LOD0 的原绘制必须关闭");
            Assert.IsTrue(m_Lod1.forceRenderingOff, "LOD1 的原绘制必须关闭");

            // LOD 距离编码必须与官方烘焙器 LODGroupBaking 对齐。
            var groupComponent = em.GetComponentData<MeshLODGroupComponent>(groupEntity);
            var worldSpaceSize = m_LodGroupGo.GetComponent<LODGroup>().size; // 最大轴缩放 = 1
            Assert.AreEqual(worldSpaceSize / 0.6f, groupComponent.LODDistances0.x, 1e-4f,
                "LOD0 的距离应为 worldSpaceSize / 0.6，且落在 LODDistances0.x（无反转）");
            Assert.AreEqual(worldSpaceSize / 0.1f, groupComponent.LODDistances0.y, 1e-4f,
                "LOD1 的距离应为 worldSpaceSize / 0.1，且落在 LODDistances0.y");
            Assert.AreEqual(float.PositiveInfinity, groupComponent.LODDistances0.z, "只有两层，LOD2 分量应为 +Infinity");
            Assert.AreEqual(float.PositiveInfinity, groupComponent.LODDistances0.w, "只有两层，LOD3 分量应为 +Infinity");
            Assert.AreEqual(float.PositiveInfinity, groupComponent.LODDistances1.x, "LOD4 不存在，应为 +Infinity");
            Assert.AreEqual(float.PositiveInfinity, groupComponent.LODDistances1.y, "LOD5 不存在，应为 +Infinity");
            Assert.AreEqual(float.PositiveInfinity, groupComponent.LODDistances1.z, "LOD6 不存在，应为 +Infinity");
            Assert.AreEqual(float.PositiveInfinity, groupComponent.LODDistances1.w, "LOD7 不存在，应为 +Infinity");

            // 注销：全部实体销毁 + forceRenderingOff 复位。
            authoring.enabled = false;
            yield return null;

            Assert.AreEqual(0, ConversionTests.CountBridgedEntities(em), "注销后全部实体必须被销毁");
            Assert.IsFalse(m_Lod0.forceRenderingOff, "注销后 LOD0 原绘制必须恢复");
            Assert.IsFalse(m_Lod1.forceRenderingOff, "注销后 LOD1 原绘制必须恢复");
        }

        [UnityTest]
        public IEnumerator Disposal_ForceRenderingOff_禁用LODGroup且层渲染器用forceRenderingOff()
        {
            var authoring = MarkTwoLevelLod();
            authoring.Disposal = OriginalRendererDisposal.ForceRenderingOff;

            yield return null;
            yield return null;

            var group = m_LodGroupGo.GetComponent<LODGroup>();
            Assert.IsNotNull(group, "ForceRenderingOff 模式不应销毁 LODGroup");
            Assert.IsFalse(group.enabled, "LODGroup 没有 forceRenderingOff，必须被禁用");
            Assert.IsTrue(m_Lod0.forceRenderingOff, "层 renderer 用 forceRenderingOff");

            authoring.enabled = false;
            yield return null;
            yield return null;

            Assert.IsTrue(group.enabled, "注销后 LODGroup 必须恢复启用");
            Assert.IsFalse(m_Lod0.forceRenderingOff, "注销后层 renderer 必须恢复绘制");

            authoring.enabled = true;
        }

        [UnityTest]
        public IEnumerator Disposal_DestroyComponents_销毁LODGroup与层渲染器()
        {
            var authoring = MarkTwoLevelLod();
            authoring.Disposal = OriginalRendererDisposal.DestroyComponents;
            var em = World.DefaultGameObjectInjectionWorld.EntityManager;

            yield return null;
            yield return null;

            Assert.IsTrue(m_LodGroupGo.GetComponent<LODGroup>() == null, "应销毁 LODGroup");
            Assert.IsTrue(m_Lod0 == null, "应销毁层 renderer");
            Assert.IsTrue(m_Lod1 == null, "应销毁层 renderer");
            Assert.AreEqual(4, ConversionTests.CountBridgedEntities(em), "实体结构不应受影响（1 父 + 1 组 + 2 层）");

            authoring.enabled = false; // 注销必须安全
            yield return null;
            yield return null;

            Assert.IsTrue(m_LodGroupGo.GetComponent<LODGroup>() == null, "DestroyComponents 注销后不应还原");
            Assert.AreEqual(0, ConversionTests.CountBridgedEntities(em));

            authoring.enabled = true;
        }

        [UnityTest]
        public IEnumerator 普通MeshRenderer不走LOD路径()
        {
            var child = new GameObject("plain-child");
            child.transform.SetParent(m_Root.transform);
            child.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
            var renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = m_Material;

            var authoring = m_Root.AddComponent<DotsRenderAuthoring>();
            authoring.MeshRenderers.Add(renderer);
            yield return null;

            var em = World.DefaultGameObjectInjectionWorld.EntityManager;
            Assert.AreEqual(0, Count<MeshLODGroupComponent>(em), "普通 MeshRenderer 不应产生 LOD 组实体");
            Assert.AreEqual(0, Count<MeshLODComponent>(em), "普通 MeshRenderer 不应产生 LOD 层实体");
            Assert.AreEqual(2, ConversionTests.CountBridgedEntities(em), "1 父 + 1 子渲染实体");
        }
    }
}
