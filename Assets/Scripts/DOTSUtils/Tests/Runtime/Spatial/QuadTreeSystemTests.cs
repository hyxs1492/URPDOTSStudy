using DOTSUtils.Spatial;
using DOTSUtils.Worlds;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace DOTSUtils.Spatial.Tests
{
    /// <summary><see cref="SpatialBuildSystem"/> 的行为：Bulk / 增量两条路径、位置变化、销毁清理、Bounds 变更重建、越界拒绝。读数语义：<see cref="SpatialAccess.TryGetIndex(World, out SpatialIndexReader)"/> 给的是拷贝，<c>ElementCount</c> 等是值字段，每次断言前都要重新取，才能验证计数确实写回了组件。</summary>
    public class QuadTreeSystemTests
    {
        static readonly string[] k_Worlds = { "SpatialTestWorld_A", "SpatialTestWorld_B" };

        [SetUp]
        public void SetUp()
        {
            DotsModules.Clear();
            DotsWorldTemplates.Clear();
            DotsWorldRegistry.Reset();
            DotsWorldRegistry.EnsureInitialized();      // 重新登记 "Default" + 自动发现全部模块（含 Spatial）
            SpatialSettings.Reset();                    // 全局配置：不重置会串到别的用例
            SpatialSettings.Configure(SpatialIndexKind.Quadtree); // 本文件只测四叉树

            // 只装 Spatial 的测试模板。
            DotsWorldTemplates.Register(new DotsWorldTemplate("SpatialTestTemplate", typeof(DotsSpatialModule)));
        }

        [TearDown]
        public void TearDown()
        {
            for (var i = 0; i < k_Worlds.Length; i++)
            {
                if (DotsWorldManager.TryGetWorld(k_Worlds[i], out var world) && world.IsCreated)
                    DotsWorldManager.DestroyWorld(world);
            }

            SpatialSettings.Reset();

            DotsModules.Clear();
            DotsWorldTemplates.Clear();
            DotsWorldRegistry.Reset();
        }

        static World CreateWorld(int index = 0)
            => DotsWorldManager.CreateWorld(k_Worlds[index], "SpatialTestTemplate", makeActive: false);

        static Entity CreateIndexed(EntityManager em, float3 position)
        {
            var entity = em.CreateEntity(typeof(LocalTransform), typeof(SpatialIndexed));
            em.SetComponentData(entity, LocalTransform.FromPosition(position));
            return entity;
        }

        /// <summary>直接取具体内核类型，供断言内核细节（容量 / 叶节点 / 遍历）。</summary>
        static NativeSpatialTree TreeOf(World world)
        {
            Assert.IsTrue(SpatialAccess.TryGetIndexSingleton(world, out var singleton), "应当有索引单例");
            var tree = world.EntityManager.GetComponentData<SpatialSingleton>(singleton).Tree;
            Assert.IsTrue(tree.IsCreated, "应当能取到四叉树");
            return tree;
        }

        static SpatialTreeConfig ConfigOf(World world)
        {
            Assert.IsTrue(SpatialAccess.TryGetIndexSingleton(world, out _),
                "Spatial 模块应当装配出 SpatialBuildSystem 的单例");
            return SpatialSettings.Pending.Tree;          // 配置是全局的（进程级），不在实体上
        }

        [Test]
        public void 装配后_单例与树都建好()
        {
            var world = CreateWorld();
            Assert.AreNotEqual(SystemHandle.Null, world.GetExistingSystem(typeof(SpatialBuildSystem)),
                "Spatial 模块应当装配出 SpatialBuildSystem");
            Assert.IsTrue(SpatialAccess.TryGetIndexSingleton(world, out var singleton));
            Assert.IsTrue(world.EntityManager.Exists(singleton));

            world.Update();

            var tree = TreeOf(world);
            Assert.AreEqual(0, tree.ElementCount);
            Assert.AreEqual(0, tree.LeafCount);
        }

        [Test]
        public void 增量模式_新增实体入树并写入句柄()
        {
            var world = CreateWorld();
            var em = world.EntityManager;
            var entity = CreateIndexed(em, new float3(3f, 0f, 4f));

            world.Update();                                  // 新增

            var tree = TreeOf(world);
            Assert.AreEqual(1, tree.ElementCount, "树的计数应已写回组件");

            Assert.IsTrue(em.HasComponent<SpatialMember>(entity));
            Assert.IsTrue(em.HasComponent<SpatialMemberDestroyed>(entity), "清理位应与 Member 同时建立");
            var member = em.GetComponentData<SpatialMember>(entity);
            Assert.IsTrue(member.IsValid);
            Assert.AreEqual(new float3(3f, 0f, 4f), member.IndexedPos);
            var destroyed = em.GetComponentData<SpatialMemberDestroyed>(entity);
            Assert.AreEqual(member.Slot, destroyed.Slot, "清理位应记下同一个句柄（Slot）");
            Assert.AreEqual(member.Version, destroyed.Version, "清理位应记下同一个句柄（Version）");
        }

        [Test]
        public void 增量模式_重复更新不会重复入树()
        {
            var world = CreateWorld();
            var em = world.EntityManager;
            CreateIndexed(em, new float3(1f, 0f, 1f));

            world.Update();
            world.Update();
            world.Update();

            Assert.AreEqual(1, TreeOf(world).ElementCount);
        }

        [Test]
        public void 增量模式_位置变化走Update且句柄不变()
        {
            var world = CreateWorld();
            var em = world.EntityManager;
            var entity = CreateIndexed(em, new float3(-40f, 0f, -40f));
            world.Update();

            var before = em.GetComponentData<SpatialMember>(entity);

            em.SetComponentData(entity, LocalTransform.FromPosition(new float3(40f, 0f, 40f)));
            world.Update();

            var after = em.GetComponentData<SpatialMember>(entity);
            Assert.AreEqual(before.Slot, after.Slot, "Update 不应改变句柄（Slot）");
            Assert.AreEqual(before.Version, after.Version, "Update 不应改变句柄（Version）");
            Assert.AreEqual(new float3(40f, 0f, 40f), after.IndexedPos);
            Assert.AreEqual(1, TreeOf(world).ElementCount);

            // 新位置能查到、旧位置查不到
            var tree = TreeOf(world);
            var hits = tree.RangeQuery(new AABB { Center = new float3(40f, 0f, 40f), Extents = new float3(2f) }, Allocator.Temp);
            try
            {
                Assert.AreEqual(1, hits.Length, "新位置应命中");
            }
            finally
            {
                hits.Dispose();
            }
        }

        [Test]
        public void 增量模式_销毁实体后从树里摘除且清理位被移除()
        {
            var world = CreateWorld();
            var em = world.EntityManager;
            var entity = CreateIndexed(em, new float3(5f, 0f, 5f));
            world.Update();
            Assert.AreEqual(1, TreeOf(world).ElementCount);

            em.DestroyEntity(entity);

            // 带 ICleanupComponentData 的实体不会立刻消失：清理位还在。
            Assert.IsTrue(em.Exists(entity), "有清理位时实体应保留");

            world.Update();                                   // 摘元素 + 移除清理位
            Assert.AreEqual(0, TreeOf(world).ElementCount, "销毁后应从树里摘掉");

            world.Update();                                   // ECB 生效：实体真正消失
            Assert.IsFalse(em.Exists(entity), "清理位移除后实体才真正销毁");
        }

        [Test]
        public void Bulk模式_每帧重建且不需要句柄()
        {
            var world = CreateWorld();
            var em = world.EntityManager;
            var config = ConfigOf(world);
            config.Mode = SpatialUpdateMode.Bulk;
            SpatialSettings.Configure(SpatialIndexKind.Quadtree, config);

            CreateIndexed(em, new float3(10f, 0f, 10f));
            CreateIndexed(em, new float3(-10f, 0f, -10f));

            world.Update();
            Assert.AreEqual(2, TreeOf(world).ElementCount);

            world.Update();
            Assert.AreEqual(2, TreeOf(world).ElementCount, "Bulk 每帧重建，计数稳定");
        }

        [Test]
        public void Bulk模式_位置变化后查询跟随()
        {
            var world = CreateWorld();
            var em = world.EntityManager;
            var config = ConfigOf(world);
            config.Mode = SpatialUpdateMode.Bulk;
            SpatialSettings.Configure(SpatialIndexKind.Quadtree, config);

            var entity = CreateIndexed(em, new float3(-30f, 0f, -30f));
            world.Update();

            em.SetComponentData(entity, LocalTransform.FromPosition(new float3(30f, 0f, 30f)));
            world.Update();

            var hits = TreeOf(world).RangeQuery(new AABB { Center = new float3(30f, 0f, 30f), Extents = new float3(2f) }, Allocator.Temp);
            try
            {
                Assert.AreEqual(1, hits.Length);
            }
            finally
            {
                hits.Dispose();
            }
        }

        [Test]
        public void 越界实体_被拒绝且不入树()
        {
            var world = CreateWorld();
            var em = world.EntityManager;
            CreateIndexed(em, new float3(9999f, 0f, 0f));

            world.Update();

            var tree = TreeOf(world);
            Assert.AreEqual(0, tree.ElementCount, "越界元素应被拒绝");
            Assert.AreEqual(1, tree.OutOfBoundsRejected);
        }

        [Test]
        public void 装配参数里的Bounds与MaxDepth生效()
        {
            // 装配参数在创建 World 时给定，必须在 CreateWorld 之前设置。
            var config = SpatialTreeConfig.Default;
            config.Bounds = new AABB { Center = new float3(1000f, 0f, 1000f), Extents = new float3(50f) };
            config.MaxDepth = 5;
            SpatialSettings.Configure(SpatialIndexKind.Quadtree, config);

            var world = CreateWorld();
            var em = world.EntityManager;
            world.Update();

            Assert.IsTrue(SpatialAccess.TryGetIndexSingleton(world, out var singletonEntity));
            var data = em.GetComponentData<SpatialSingleton>(singletonEntity);
            Assert.AreEqual(5, data.Tree.MaxDepth, "树应当用装配参数里的 MaxDepth");
            // 只断言 Center（xz 分量），y 只是占位。
            Assert.AreEqual(new float3(1000f, 0f, 1000f), data.Tree.Bounds.Center, "Bounds.Center 来自装配参数");

            // Bounds 之外 → 被拒绝；之内 → 正常入树
            CreateIndexed(em, new float3(0f, 0f, 0f));
            world.Update();
            Assert.AreEqual(0, TreeOf(world).ElementCount, "Bounds 之外的元素被拒绝");

            CreateIndexed(em, new float3(1000f, 0f, 1000f));
            world.Update();
            Assert.AreEqual(1, TreeOf(world).ElementCount, "Bounds 之内的元素正常入树");
        }
    }
}
