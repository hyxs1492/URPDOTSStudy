using System;
using DOTSUtils.Spatial;
using DOTSUtils.Worlds;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace DOTSUtils.Rvo.Tests
{
    /// <summary>RVO 与 Spatial 的联合关系：Rvo 不自建索引，邻居来自 Spatial（四叉树优先，没有就用八叉树）；「必须有 Spatial」由装配时的依赖强制。</summary>
    public class RvoIndexTests
    {
        const string k_WorldName = "RvoIndexWorld";
        const float k_Dt = 1f / 60f;

        [SetUp]
        public void SetUp()
        {
            DotsModules.Clear();
            DotsWorldTemplates.Clear();
            DotsWorldRegistry.Reset();
            DotsWorldRegistry.EnsureInitialized();

            RvoSettings.Reset();
            SpatialSettings.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            if (DotsWorldManager.TryGetWorld(k_WorldName, out var world) && world.IsCreated)
                DotsWorldManager.DestroyWorld(world);

            RvoSettings.Reset();
            SpatialSettings.Reset();

            DotsModules.Clear();
            DotsWorldTemplates.Clear();
            DotsWorldRegistry.Reset();
        }

        static World CreateWorld(params Type[] modules)
        {
            DotsWorldTemplates.Register(new DotsWorldTemplate("RvoIndexTemplate", modules));
            var world = DotsWorldManager.CreateWorld(k_WorldName, "RvoIndexTemplate", makeActive: false);

            var config = RvoConfig.Default;
            config.FixedTimeStep = k_Dt;
            config.IntegratePosition = false;
            RvoSettings.Set(config);

            return world;
        }

        /// <summary>agent：打统一的索引 tag（<c>SpatialIndexed</c>），具体进哪棵树看配置。</summary>
        static Entity CreateAgent(World world, float3 position)
        {
            var em = world.EntityManager;
            var entity = em.CreateEntity(typeof(LocalTransform), typeof(RvoAgent), typeof(SpatialIndexed));
            em.SetComponentData(entity, LocalTransform.FromPosition(position));
            em.SetComponentData(entity, new RvoAgent
            {
                Radius = 0.5f,
                MaxSpeed = 2f,
                PrefVelocity = new float2(1f, 0f),
                Velocity = float2.zero,
            });
            return entity;
        }

        static RvoStatus StatusOf(World world)
        {
            Assert.IsTrue(RvoAccess.TryGetStatus(world, out var status), "应当能读到 RvoStatus");
            return status;
        }

        // ───────────────────────────── 用例 ─────────────────────────────

        [Test]
        public void 依赖自动装配_只列Rvo也会把Spatial装进来()
        {
            // 模板只列 Rvo，Spatial 是它的依赖，由框架自动装配。
            DotsWorldTemplates.Register(new DotsWorldTemplate("RvoOnlyTemplate", typeof(DotsRvoModule)));
            var world = DotsWorldManager.CreateWorld(k_WorldName, "RvoOnlyTemplate", makeActive: false);

            Assert.IsTrue(DotsWorldManager.HasModule(world, "Rvo"), "列了 Rvo 就该装上");
            Assert.IsTrue(DotsWorldManager.HasModule(world, "Spatial"), "Spatial 是 Rvo 的依赖，应被自动装配");
            Assert.IsFalse(DotsWorldManager.AddModule(world, "Rvo"), "重复装配应幂等返回 false");
        }

        [Test]
        public void 装了Spatial_用四叉树索引()
        {
            var world = CreateWorld(typeof(DotsSpatialModule), typeof(DotsRvoModule));
            var a = CreateAgent(world, new float3(0f, 0f, 0f));
            var b = CreateAgent(world, new float3(1.5f, 0f, 0f));

            world.Update();
            world.Update();                       // 第二帧：邻居已填

            var status = StatusOf(world);
            Assert.AreEqual(RvoNeighborIndex.Quadtree, status.Index);
            Assert.AreEqual(2, status.AgentCount);
        }

        [Test]
        public void 配置选八叉树_只用八叉树()
        {
            SpatialSettings.Configure(SpatialIndexKind.Octree);
            var world = CreateWorld(typeof(DotsSpatialModule), typeof(DotsRvoModule));
            CreateAgent(world, new float3(0f, 0f, 0f));
            CreateAgent(world, new float3(1.5f, 0f, 0f));

            world.Update();
            world.Update();

            Assert.AreEqual(RvoNeighborIndex.Octree, StatusOf(world).Index, "RVO 应当用配置指定的八叉树");
            Assert.IsTrue(SpatialAccess.TryGetIndex(world, out var index));
            Assert.AreEqual(SpatialIndexKind.Octree, index.Kind);
            Assert.IsTrue(SpatialAccess.TryGetIndexSingleton(world, out var singleton));
            Assert.IsTrue(world.EntityManager.HasComponent<SpatialSingleton>(singleton),
                "选八叉树时，唯一的索引单例就是八叉树那个（四叉树连单例都不建）");
        }

        [Test]
        public void agent在索引范围外_被树拒绝并计数()
        {
            var world = CreateWorld(typeof(DotsSpatialModule), typeof(DotsRvoModule));

            // 四叉树默认 Bounds 是 ±200：其中一个 agent 故意放到外面（树会拒绝它）
            CreateAgent(world, new float3(0f, 0f, 0f));
            CreateAgent(world, new float3(9999f, 0f, 0f));

            world.Update();
            world.Update();

            Assert.IsTrue(SpatialAccess.TryGetIndex(world, out var tree));
            Assert.GreaterOrEqual(tree.OutOfBoundsRejected, 1,
                "越界元素被树拒绝并计数 —— 它是「Bounds 配小了」的唯一提示（RVO 侧不会自己扩张别人的树）");
        }
    }
}
