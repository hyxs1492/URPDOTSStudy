using System.Collections;
using System.Collections.Generic;
using DOTSUtils.RenderBridge;
using DOTSUtils.Worlds;
using DOTSUtils.RenderBridge.Tests;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;

namespace DOTSUtils.Benchmark.Tests
{
    /// <summary>benchmark 两条生成路径与移动系统的运行时行为。与其它用例共用同一个 World，断言一律用相对量，并只在 TearDown 清理自己生成的东西。</summary>
    public class BenchmarkSpawnTests
    {
        GameObject m_Source;
        GameObject m_Root;
        Mesh m_Mesh;
        Material m_Material;
        readonly List<Entity> m_Spawned = new();

        /// <summary>收集带 <see cref="BenchmarkShipTag"/> 且没有 <see cref="Parent"/> 的实体。</summary>
        static void CollectTaggedParents(EntityManager em, List<Entity> into)
        {
            var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<BenchmarkShipTag>(), ComponentType.Exclude<Parent>());
            var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                foreach (var e in entities)
                {
                    into.Add(e);
                }
            }
            finally
            {
                entities.Dispose();
                query.Dispose();
            }
        }

        [SetUp]
        public void SetUp()
        {
            // 移动系统由 DotsBenchmarkModule 装配，该模块 Templates 为空，默认 World 模板不含它，这里显式按名装配。
            var world = World.DefaultGameObjectInjectionWorld;
            if (world != null && world.IsCreated)
            {
                DotsWorldRegistry.EnsureInitialized();
                DotsWorldManager.AddModule(world, "Benchmark");
                // 移动系统从 RvoAgent.Velocity 取位移，必须有 Rvo 在跑（它会一并装配依赖的 Spatial）。
                DotsWorldManager.AddModule(world, "Rvo");
                // 可见性模块是独立的可选能力，这里显式装配。
                DotsWorldManager.AddModule(world, "Visibility");
            }

            m_Mesh = TestRenderAssets.NewMesh("bench-mesh", 1);
            // 给测试网格一个非零包围盒（RenderBounds / WorldRenderBounds 由 mesh.bounds 推导）。
            m_Mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            m_Material = TestRenderAssets.NewMaterial();

            m_Source = new GameObject("bench-source");
            m_Root = new GameObject("bench-root");
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
                BenchmarkShipFactory.DestroyEntities(world.EntityManager, m_Spawned);
                // 源对象可能挂了 DotsRenderAuthoring，其注销请求会被 Queue.Clear 丢弃，这里显式清掉残留实体。
                ConversionTests.DestroyAllBridgedEntities();
            }
            m_Spawned.Clear();

            // 移动系统的静态参数是全局的，复位以免影响后续用例。
            BenchmarkRandomMoveSystem.MoveSpeed = 10f;
            BenchmarkRandomMoveSystem.Bounds = 200f;
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

        static int Count<T>(EntityManager em) where T : unmanaged, IComponentData
        {
            var query = em.CreateEntityQuery(ComponentType.ReadOnly<T>());
            var count = query.CalculateEntityCount();
            query.Dispose();
            return count;
        }

        [Test]
        public void 纯ECS生成_实体数等于船数乘层数结构()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            var shipsBefore = Count<BenchmarkShipTag>(em);
            var groupsBefore = Count<MeshLODGroupComponent>(em);
            var layersBefore = Count<MeshLODComponent>(em);

            var ships = BenchmarkShipFactory.SpawnPureEcs(
                world, m_Source, 5, new[] { m_Material }, 100f, 12345u, new RenderMeshPool(), m_Spawned);

            Assert.AreEqual(5, ships, "应生成 5 艘船");
            Assert.AreEqual(shipsBefore + 5, Count<BenchmarkShipTag>(em),
                "BenchmarkShipTag 现在打在父实体上（走 SpawnFromPrefab 的实体树：父 → 组 → 层）");
            Assert.AreEqual(groupsBefore + 5, Count<MeshLODGroupComponent>(em), "应有 5 个 LOD 组实体");
            Assert.AreEqual(layersBefore + 10, Count<MeshLODComponent>(em), "5 艘船 × 2 层 = 10 个层实体");
            Assert.AreEqual(5 + 5 + 10, m_Spawned.Count, "created 列表应记录整棵树（父 + 组 + 层）");
            Assert.AreEqual(shipsBefore + 5, Count<BenchmarkMotion>(em), "移动状态打在父实体上，每艘一个");
        }

        [Test]
        public void 纯ECS生成_清理后实体全部销毁()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            var groupsBefore = Count<MeshLODGroupComponent>(em);
            var layersBefore = Count<MeshLODComponent>(em);

            BenchmarkShipFactory.SpawnPureEcs(world, m_Source, 3, new[] { m_Material }, 50f, 7u, new RenderMeshPool(), m_Spawned);
            BenchmarkShipFactory.DestroyEntities(em, m_Spawned);

            Assert.AreEqual(0, m_Spawned.Count);
            Assert.AreEqual(groupsBefore, Count<MeshLODGroupComponent>(em), "清理后不应残留 LOD 组实体");
            Assert.AreEqual(layersBefore, Count<MeshLODComponent>(em), "清理后不应残留层实体");
        }

        /// <summary>所有船都待在同一水平面内：生成时 y = 0，跑一阵之后 y 仍然是 0。</summary>
        [UnityTest]
        public IEnumerator 所有船都在同一水平面内_移动不离开xz平面()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            BenchmarkRandomMoveSystem.MoveSpeed = 50f;
            BenchmarkRandomMoveSystem.Bounds = 50f;
            BenchmarkRandomMoveSystem.TurnSpeedDegPerSec = 120f;

            var ships = new List<Entity>();
            BenchmarkShipFactory.SpawnPureEcs(world, m_Source, 20, new[] { m_Material }, 20f, 7u,
                                              new RenderMeshPool(), ships);

            var roots = new List<Entity>();
            foreach (var e in ships)
                if (!em.HasComponent<Parent>(e)) roots.Add(e);
            Assert.AreEqual(20, roots.Count, "应能挑出 20 个父实体（每艘船一个）");

            foreach (var e in roots)
            {
                Assert.AreEqual(0f, em.GetComponentData<LocalTransform>(e).Position.y, 1e-5f,
                    "生成时就应该落在水平面上");
            }

            for (var frame = 0; frame < 30; frame++) yield return null;

            foreach (var e in roots)
            {
                var transform = em.GetComponentData<LocalTransform>(e);
                Assert.AreEqual(0f, transform.Position.y, 1e-4f, "移动过程中也不该离开水平面");

                // 朝向也只在水平面内：forward 的 y 分量应为 0。
                var forward = math.forward(transform.Rotation);
                Assert.AreEqual(0f, forward.y, 1e-4f, "朝向不该带俯仰分量");
            }

            BenchmarkShipFactory.DestroyEntities(em, ships);
        }

        /// <summary>端到端检查：用示例自己的生成路径造一批船，跑一阵之后看有没有互相穿透。断言取「半径和的 80%」。</summary>
        [UnityTest]
        public IEnumerator 同一个水平面内_船之间不会互相穿透()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            BenchmarkRandomMoveSystem.MoveSpeed = 30f;
            BenchmarkRandomMoveSystem.Bounds = 30f;
            BenchmarkRandomMoveSystem.TurnSpeedDegPerSec = 120f;

            const int count = 40;
            var ships = new List<Entity>();
            BenchmarkShipFactory.SpawnPureEcs(world, m_Source, count, new[] { m_Material }, 10f, 11u,
                                              new RenderMeshPool(), ships);

            var roots = new List<Entity>();
            foreach (var e in ships)
                if (!em.HasComponent<Parent>(e)) roots.Add(e);
            Assert.AreEqual(count, roots.Count, "应能挑出每艘船一个父实体");

            for (var frame = 0; frame < 60; frame++) yield return null;

            // 两两量 xz 平面内的距离，取最小值。
            var positions = new List<float3>();
            foreach (var e in roots)
            {
                var p = em.GetComponentData<LocalTransform>(e).Position;
                positions.Add(new float3(p.x, 0f, p.z));
            }

            var minDistance = float.MaxValue;
            var minPair = "";
            for (var i = 0; i < positions.Count; i++)
            {
                for (var j = i + 1; j < positions.Count; j++)
                {
                    var d = math.distance(positions[i], positions[j]);
                    if (d < minDistance)
                    {
                        minDistance = d;
                        minPair = $"{i}-{j}";
                    }
                }
            }

            var radiusSum = BenchmarkShipComponents.ShipRadius * 2f;   // 两艘船的半径之和 = 允许的最小间距
            Debug.Log($"[碰撞检查] {count} 艘船跑 60 帧后的最小间距 = {minDistance:F4}" +
                      $"（船半径和 = {radiusSum:F3}，最近的一对 = {minPair}）");
            Assert.Greater(minDistance, radiusSum * 0.8f,
                $"{count} 艘船跑 60 帧后最小间距 {minDistance:F3}（船半径和 {radiusSum:F3}，最近的是 {minPair}）");

            BenchmarkShipFactory.DestroyEntities(em, ships);
        }

        [UnityTest]
        public IEnumerator 移动系统_船会移动且不超出边界()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            BenchmarkRandomMoveSystem.MoveSpeed = 50f;
            BenchmarkRandomMoveSystem.Bounds = 10f;
            // 显式设转向速度：这是静态字段，别的用例可能改过它。
            BenchmarkRandomMoveSystem.TurnSpeedDegPerSec = 120f;

            var ships = new List<Entity>();
            BenchmarkShipFactory.SpawnPureEcs(world, m_Source, 20, new[] { m_Material }, 5f, 99u, new RenderMeshPool(), ships);

            // 移动状态在父实体上（父实体无 Parent），挑出它们就是「每艘船一个」。
            var roots = new List<Entity>();
            foreach (var e in ships)
                if (!em.HasComponent<Parent>(e)) roots.Add(e);
            Assert.AreEqual(20, roots.Count, "应能从事务列表里挑出 20 个父实体（每艘船一个）");

            var start = new List<float3>();
            foreach (var e in roots) start.Add(em.GetComponentData<LocalTransform>(e).Position);

            for (var frame = 0; frame < 30; frame++) yield return null;

            // 越过 Bounds 后靠「期望速度指向原点」游回来，允许短暂越界；上界取一帧的最大位移 + 1。
            var slack = BenchmarkRandomMoveSystem.Bounds + BenchmarkRandomMoveSystem.MoveSpeed * 0.05f + 1f;

            var moved = 0;
            for (var i = 0; i < roots.Count; i++)
            {
                var p = em.GetComponentData<LocalTransform>(roots[i]).Position;
                if (math.distance(p, start[i]) > 0.01f) moved++;
                Assert.LessOrEqual(math.abs(p.x), slack, "不应跑出边界太多");
                Assert.LessOrEqual(math.abs(p.y), slack);
                Assert.LessOrEqual(math.abs(p.z), slack);
            }
            Assert.Greater(moved, 0, "至少有一部分船应该动起来");

            BenchmarkShipFactory.DestroyEntities(em, ships);
        }

        [UnityTest]
        public IEnumerator 两条路径产出的实体结构一致()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            var layersBefore = Count<MeshLODComponent>(em);
            var groupsBefore = Count<MeshLODGroupComponent>(em);

            // 纯 ECS 路径：一次调用立即产出。
            var pure = new List<Entity>();
            var pureShips = BenchmarkShipFactory.SpawnPureEcs(
                world, m_Source, 1, new[] { m_Material }, 10f, 3u, new RenderMeshPool(), pure);
            Assert.AreEqual(1, pureShips);

            var pureLayers = Count<MeshLODComponent>(em) - layersBefore;
            var pureGroups = Count<MeshLODGroupComponent>(em) - groupsBefore;
            Assert.AreEqual(2, pureLayers, "1 艘 × 2 层 = 2 个层实体");
            Assert.AreEqual(1, pureGroups, "1 艘 = 1 个组实体");

            // 走桥路径：同一个源对象，Instantiate + 转换。
            var instance = Object.Instantiate(m_Source);
            var authoring = instance.AddComponent<DotsRenderAuthoring>();
            authoring.LodGroups.Add(instance.transform.Find("bench-root").GetComponent<LODGroup>());
            authoring.TransformDrivenByEntity = true;   // 与纯 ECS 路径一致：变换不由 GameObject 同步
            DotsRenderConversionQueue.EnqueueRegister(instance);
            yield return null;
            yield return null;

            var bridgeLayers = Count<MeshLODComponent>(em) - layersBefore - pureLayers;
            var bridgeGroups = Count<MeshLODGroupComponent>(em) - groupsBefore - pureGroups;
            Assert.AreEqual(pureLayers, bridgeLayers, "同一源对象的两条路径应产出同样多的层实体");
            Assert.AreEqual(pureGroups, bridgeGroups, "同一源对象的两条路径应产出同样多的组实体");

            // 纯 ECS 路径的移动状态在父实体上（无 Parent）。
            var pureRoots = 0;
            foreach (var e in pure)
                if (!em.HasComponent<Parent>(e)) pureRoots++;
            Assert.AreEqual(1, pureRoots, "每艘船一个父实体，移动状态打在它上面");

            // 清理
            Object.DestroyImmediate(instance);
            DotsRenderConversionQueue.Clear();
            BenchmarkShipFactory.DestroyEntities(em, pure);
            ConversionTests.DestroyAllBridgedEntities();
        }

        [UnityTest]
        public IEnumerator 走桥路径_实例化后父实体被打标且不占用同步槽位()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            var conversion = world.GetExistingSystemManaged<DotsRenderConversionSystem>();
            var slotsBefore = conversion.Registry.Count;

            // 源对象自己先按普通（非实体驱动）方式转换，作为同步槽位的对照。
            var authoring = m_Source.AddComponent<DotsRenderAuthoring>();
            authoring.LodGroups.Add(m_Root.GetComponent<LODGroup>());
            yield return null;
            yield return null;

            var slotsAfterSource = conversion.Registry.Count;
            Assert.AreEqual(slotsBefore + 1, slotsAfterSource, "普通父物体应占用 1 个同步槽位");

            var spawned = BenchmarkShipFactory.SpawnThroughBridge(m_Source, new[] { m_Material }, 3, 40f, 4242u);
            Assert.AreEqual(3, spawned.Count, "应实例化 3 个副本");

            yield return null;   // 转换系统消费队列
            yield return null;   // 实体创建完成 → ShipController.OnCreateEntitySuccess 已挂组件

            // 数一下带标记的父实体。
            CollectTaggedParents(em, m_Spawned);
            Assert.AreEqual(3, m_Spawned.Count, "3 个父实体应被打上 BenchmarkShipTag（由 ShipController 挂上）");

            // 再给几帧，确认打标后的父实体确实被移动系统驱动。
            BenchmarkRandomMoveSystem.MoveSpeed = 100f;
            BenchmarkRandomMoveSystem.Bounds = 100f;
            var startPositions = new List<float3>();
            foreach (var e in m_Spawned) startPositions.Add(em.GetComponentData<LocalTransform>(e).Position);
            for (var frame = 0; frame < 10; frame++) yield return null;

            var moved = 0;
            for (var i = 0; i < m_Spawned.Count; i++)
                if (math.distance(em.GetComponentData<LocalTransform>(m_Spawned[i]).Position, startPositions[i]) > 0.01f)
                    moved++;
            Assert.AreEqual(m_Spawned.Count, moved, "打标后的父实体应全部被移动系统驱动");

            foreach (var go in spawned)
                if (go != null) Object.DestroyImmediate(go);
        }

        [UnityTest]
        public IEnumerator 走桥路径_源对象未标记时自动补齐标记并转换()
        {
            // 源对象不挂 DotsRenderAuthoring（真实用法）。
            Assert.IsNull(m_Source.GetComponent<DotsRenderAuthoring>(), "前提：源对象未标记");

            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            var conversion = world.GetExistingSystemManaged<DotsRenderConversionSystem>();
            var slotsBefore = conversion.Registry.Count;

            var spawned = BenchmarkShipFactory.SpawnThroughBridge(m_Source, new[] { m_Material }, 2, 40f, 7u);
            Assert.AreEqual(2, spawned.Count, "应实例化 2 个副本");

            foreach (var go in spawned)
            {
                var authoring = go.GetComponent<ShipController>();
                Assert.IsNotNull(authoring, "实例应被自动补上 ShipController");
                Assert.IsTrue(authoring.LodGroups.Count > 0, "应自动收集到源对象的 LODGroup");
            }

            yield return null;   // 转换系统消费队列
            yield return null;

            CollectTaggedParents(em, m_Spawned);
            Assert.AreEqual(2, m_Spawned.Count,
                "未标记的源对象，实例化后也应转换出父实体并被打标（由 ShipController 挂）");

            foreach (var go in spawned)
                if (go != null) Object.DestroyImmediate(go);
        }

        [UnityTest]
        public IEnumerator 纯ECS生成_层实体的世界变换跟随其组实体()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var em = world.EntityManager;
            // 用 m_Spawned 而非局部列表：断言失败时 TearDown 也会清理。
            BenchmarkShipFactory.SpawnPureEcs(world, m_Source, 3, new[] { m_Material }, 50f, 4242u, new RenderMeshPool(), m_Spawned);

            yield return null;   // 让 TransformSystemGroup 推导世界变换
            yield return null;

            var groupWorld = new Dictionary<Entity, float3>();
            var layerCount = 0;
            foreach (var e in m_Spawned)
            {
                // 用组件精确挑实体：树上多了「父实体」这一层，不能靠「有没有 Parent」区分组实体与层实体。
                if (em.HasComponent<MeshLODGroupComponent>(e))
                {
                    Assert.IsTrue(em.HasComponent<LocalToWorld>(e),
                        "组实体必须带 LocalToWorld —— 否则父→子层级的世界变换推导断裂，渲染实体会全部堆在原点");
                    groupWorld[e] = em.GetComponentData<LocalToWorld>(e).Position;
                    continue;
                }
                if (!em.HasComponent<MeshLODComponent>(e)) continue;   // 父实体与普通渲染实体跳过

                var group = em.GetComponentData<Parent>(e).Value;
                var layerWorld = em.GetComponentData<LocalToWorld>(e).Position;
                Assert.Less(math.distance(groupWorld[group], layerWorld), 1f,
                    "层实体的世界位置应紧邻它的组实体（且不是原点）");
                Assert.Greater(em.GetComponentData<WorldRenderBounds>(e).Value.Extents.x, 0f,
                    "层实体的 WorldRenderBounds 不应是零尺寸，否则会被剔除");
                layerCount++;
            }

            Assert.Greater(layerCount, 0, "应至少检查到一个层实体");
        }
    }
}
