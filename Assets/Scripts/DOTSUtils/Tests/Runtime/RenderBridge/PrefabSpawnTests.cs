using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace DOTSUtils.RenderBridge.Tests
{
    /// <summary>从预制体直接生成实体，不实例化 GameObject。Mesh / Material 一律走 <see cref="TestRenderAssets"/>：会话级共享、用例之间不销毁。</summary>
    public class PrefabSpawnTests
    {
        GameObject m_Prefab;
        Mesh m_Mesh;
        Material m_Material;

        static EntityManager Em => World.DefaultGameObjectInjectionWorld.EntityManager;

        [SetUp]
        public void SetUp()
        {
            DotsRenderConversionQueue.Clear();

            m_Mesh = TestRenderAssets.NewMesh("prefab-spawn-mesh", 1);
            m_Material = TestRenderAssets.NewMaterial();

            // 搭一个「预制体」：根节点挂 DotsRenderAuthoring，子物体带 MeshRenderer。
            m_Prefab = new GameObject("prefab_root");
            var child = new GameObject("prefab_child");
            child.transform.SetParent(m_Prefab.transform);
            child.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
            var renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = m_Material;

            var authoring = m_Prefab.AddComponent<DotsRenderAuthoring>();
            authoring.MeshRenderers.Add(renderer);

            // AddComponent 触发的 OnEnable 会把 m_Prefab 当成场景实例入队，这里清掉，让它只作「配置载体」存在。
            DotsRenderConversionQueue.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            DotsRenderConversionQueue.Clear();
            if (m_Prefab != null) Object.DestroyImmediate(m_Prefab);

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;

            ConversionTests.DestroyAllBridgedEntities();

            // 改注册表前必须先收掉两个 job（它们持有 m_Entities 的读锁）。
            world.GetExistingSystemManaged<DotsRenderTransformSyncSystem>()?.PendingJob.Complete();
            world.GetExistingSystemManaged<DotsRenderTransformPushSystem>()?.PendingJob.Complete();

            var system = world.GetExistingSystemManaged<DotsRenderConversionSystem>();
            if (system == null) return;

            var registry = system.Registry;
            var entities = registry.Entities;
            var snapshot = new Entity[entities.Length];
            for (var i = 0; i < snapshot.Length; i++) snapshot[i] = entities[i];
            foreach (var entity in snapshot)
                if (entity != Entity.Null) registry.MarkDead(entity);

            registry.PruneDestroyedTransforms();
        }

        static int CountGameObjectsInScene() => Object.FindObjectsOfType<GameObject>(true).Length;

        static int CountChildrenOf(EntityManager em, Entity parent)
        {
            var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<DotsRenderLink>(),
                ComponentType.ReadOnly<Parent>());
            var entities = query.ToEntityArray(Allocator.Temp);
            var count = 0;
            for (var i = 0; i < entities.Length; i++)
                if (em.GetComponentData<Parent>(entities[i]).Value == parent) count++;
            entities.Dispose();
            query.Dispose();
            return count;
        }

        static Entity FirstChildOf(EntityManager em, Entity parent)
        {
            var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<DotsRenderLink>(),
                ComponentType.ReadOnly<Parent>());
            var entities = query.ToEntityArray(Allocator.Temp);
            var found = Entity.Null;
            for (var i = 0; i < entities.Length; i++)
            {
                if (em.GetComponentData<Parent>(entities[i]).Value != parent) continue;
                found = entities[i];
                break;
            }
            entities.Dispose();
            query.Dispose();
            return found;
        }

        [UnityTest]
        public IEnumerator 生成实体但不产生任何GameObject()
        {
            var before = CountGameObjectsInScene();
            yield return null;

            var parent = DotsRenderSpawner.Spawn(m_Prefab, new Vector3(1f, 2f, 3f), Quaternion.identity, 2f);
            Assert.AreNotEqual(Entity.Null, parent, "应成功生成父实体");

            yield return null;

            Assert.AreEqual(before, CountGameObjectsInScene(), "Spawn 不应产生任何 GameObject");
            Assert.IsTrue(Em.HasComponent<DotsRenderPrefabInstance>(parent), "应带来源标记");
            Assert.AreEqual(1, CountChildrenOf(Em, parent), "预制体里 1 个 MeshRenderer 应产生 1 个子渲染实体");
            Assert.IsTrue(Em.HasComponent<LinkedEntityGroup>(parent), "应有 LinkedEntityGroup 供级联销毁");
        }

        [UnityTest]
        public IEnumerator 父实体变换取自参数()
        {
            var parent = DotsRenderSpawner.Spawn(
                m_Prefab, new Vector3(4f, -1f, 2.5f), Quaternion.Euler(0f, 90f, 0f), 3f);
            yield return null;

            var lt = Em.GetComponentData<LocalTransform>(parent);
            Assert.AreEqual(4f, lt.Position.x, 0.001f);
            Assert.AreEqual(-1f, lt.Position.y, 0.001f);
            Assert.AreEqual(2.5f, lt.Position.z, 0.001f);
            Assert.AreEqual(90f, math.degrees(math.Euler(lt.Rotation)).y, 0.01f);
            Assert.AreEqual(3f, lt.Scale, 0.001f);
        }

        [UnityTest]
        public IEnumerator 不触碰预制体里的组件()
        {
            var renderer = m_Prefab.GetComponentInChildren<MeshRenderer>();

            DotsRenderSpawner.Spawn(m_Prefab, Vector3.zero, Quaternion.identity, 1f);
            yield return null;

            Assert.IsTrue(renderer != null, "预制体里的 MeshRenderer 不应被销毁");
            Assert.IsTrue(renderer.enabled, "不应被禁用");
            Assert.IsFalse(renderer.forceRenderingOff, "不应被强制关绘制");
        }

        [UnityTest]
        public IEnumerator 不参与变换同步()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var system = world.GetExistingSystemManaged<DotsRenderConversionSystem>();

            // 读 Count 之前先收掉两个 job：它们持有 m_Entities 的读锁。
            world.GetExistingSystemManaged<DotsRenderTransformSyncSystem>()?.PendingJob.Complete();
            world.GetExistingSystemManaged<DotsRenderTransformPushSystem>()?.PendingJob.Complete();
            system.Registry.PruneDestroyedTransforms();
            var before = system.Registry.Count;

            DotsRenderSpawner.Spawn(m_Prefab, Vector3.zero, Quaternion.identity, 1f);
            yield return null;

            Assert.AreEqual(before, system.Registry.Count, "预制体路径不应登记进同步表");
        }

        [UnityTest]
        public IEnumerator Destroy级联销毁整棵树()
        {
            var parent = DotsRenderSpawner.Spawn(m_Prefab, Vector3.zero, Quaternion.identity, 1f);
            yield return null;

            Assert.AreEqual(1, CountChildrenOf(Em, parent), "前提：应有 1 个子渲染实体");

            Assert.IsTrue(DotsRenderSpawner.Destroy(parent), "应成功销毁");
            yield return null;

            Assert.IsFalse(Em.Exists(parent), "父实体应被销毁");
            Assert.AreEqual(0, CountChildrenOf(Em, parent), "子实体应随 LinkedEntityGroup 一起销毁");
        }

        [UnityTest]
        public IEnumerator Destroy拒绝非本API生成的实体()
        {
            // 造一个场景实例转换来的实体（不带 DotsRenderPrefabInstance）。
            var instance = Object.Instantiate(m_Prefab);
            yield return null;
            yield return null;

            var system = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<DotsRenderConversionSystem>();
            Assert.IsTrue(system.TryGetParentEntity(instance, out var sceneParent), "前提：场景实例应已转换");

            Assert.IsFalse(DotsRenderSpawner.Destroy(sceneParent), "不应销毁非本 API 生成的实体");
            yield return null;
            Assert.IsTrue(Em.Exists(sceneParent), "实体应仍然存在");

            Object.DestroyImmediate(instance);
        }

        [UnityTest]
        public IEnumerator 没有可转换目标时返回空实体()
        {
            var bare = new GameObject("prefab_bare");
            bare.AddComponent<DotsRenderAuthoring>();   // 两个列表都为空
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("没有可用的渲染目标"));

            var parent = DotsRenderSpawner.Spawn(bare, Vector3.zero, Quaternion.identity, 1f);
            yield return null;

            Assert.AreEqual(Entity.Null, parent, "采不到快照时应返回 Entity.Null");
            Assert.AreEqual(0, ConversionTests.CountBridgedEntities(Em), "不应留下任何实体");

            Object.DestroyImmediate(bare);
        }

        [UnityTest]
        public IEnumerator 结构与实例化路径等价()
        {
            // 同一预制体：一条走 Instantiate + 转换，一条走 Spawn。
            var instance = Object.Instantiate(m_Prefab);
            yield return null;
            yield return null;

            var system = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<DotsRenderConversionSystem>();
            Assert.IsTrue(system.TryGetParentEntity(instance, out var sceneParent), "前提：场景实例应已转换");

            var spawnedParent = DotsRenderSpawner.Spawn(m_Prefab, Vector3.zero, Quaternion.identity, 1f);
            yield return null;

            Assert.AreEqual(CountChildrenOf(Em, sceneParent), CountChildrenOf(Em, spawnedParent),
                "两条路径的渲染实体数应一致");

            // 材质口径一致：两边第一个子实体的 RenderMeshArray 应指向同一组材质。
            var sceneChild = FirstChildOf(Em, sceneParent);
            var spawnedChild = FirstChildOf(Em, spawnedParent);
            Assert.AreNotEqual(Entity.Null, sceneChild);
            Assert.AreNotEqual(Entity.Null, spawnedChild);

            var sceneArray = Em.GetSharedComponentManaged<RenderMeshArray>(sceneChild);
            var spawnedArray = Em.GetSharedComponentManaged<RenderMeshArray>(spawnedChild);
            var sceneMats = sceneArray.GetMaterials(Em.GetComponentData<MaterialMeshInfo>(sceneChild));
            var spawnedMats = spawnedArray.GetMaterials(Em.GetComponentData<MaterialMeshInfo>(spawnedChild));

            Assert.AreEqual(sceneMats.Count, spawnedMats.Count, "材质槽数应一致");
            for (var i = 0; i < sceneMats.Count; i++)
                Assert.AreSame(sceneMats[i], spawnedMats[i], $"第 {i} 个材质应一致");

            Object.DestroyImmediate(instance);
        }
    }
}
