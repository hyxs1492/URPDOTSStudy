using DOTSUtils.Spatial;
using DOTSUtils.Worlds;
using NUnit.Framework;
using Unity.Entities;

namespace DOTSUtils.Spatial.Tests
{
    /// <summary>模块被自动发现并声明进 Default 模板；装配后能取到树，销毁模块后取不到。</summary>
    public class SpatialModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            DotsModules.Clear();
            DotsWorldTemplates.Clear();
            DotsWorldRegistry.Reset();
            DotsWorldRegistry.EnsureInitialized();

            SpatialSettings.Reset();      // 默认 Quadtree；本文件的断言依赖它
        }

        [TearDown]
        public void TearDown()
        {
            if (DotsWorldManager.TryGetWorld("SpatialModuleWorld", out var world) && world.IsCreated)
                DotsWorldManager.DestroyWorld(world);

            SpatialSettings.Reset();

            DotsModules.Clear();
            DotsWorldTemplates.Clear();
            DotsWorldRegistry.Reset();
        }

        [Test]
        public void 模块被自动发现_并声明加入Default模板()
        {
            Assert.IsTrue(DotsModules.TryGet("Spatial", out var descriptor), "Spatial 模块应被自动发现");
            Assert.AreEqual(1, descriptor.Groups.Count);
            Assert.AreEqual(typeof(SpatialGroup), descriptor.Groups[0].GroupType);
            CollectionAssert.Contains(descriptor.Groups[0].Systems, typeof(SpatialBuildSystem));
            CollectionAssert.Contains(descriptor.Groups[0].Systems, typeof(SpatialBuildSystem));

            Assert.IsTrue(DotsWorldTemplates.TryGet("Default", out var template));
            CollectionAssert.Contains(template.Modules, typeof(DotsSpatialModule),
                "Spatial 应声明进 Default 模板");
        }

        [Test]
        public void 空World_取不到树()
        {
            DotsWorldTemplates.Register(new DotsWorldTemplate("SpatialEmptyTemplate"));
            var world = DotsWorldManager.CreateWorld("SpatialModuleWorld", "SpatialEmptyTemplate", makeActive: false);

            world.Update();
            Assert.IsFalse(SpatialAccess.TryGetIndex(world, out _), "没装配 Spatial 时不应取到索引");
        }

        [Test]
        public void 热插拔_装配后可取树_销毁后取不到()
        {
            DotsWorldTemplates.Register(new DotsWorldTemplate("SpatialEmptyTemplate"));
            var world = DotsWorldManager.CreateWorld("SpatialModuleWorld", "SpatialEmptyTemplate", makeActive: false);

            Assert.IsTrue(DotsWorldManager.AddModule(world, "Spatial"), "应当能装配 Spatial 模块");
            world.Update();

            Assert.IsTrue(SpatialAccess.TryGetIndex(world, out var index), "装配后应能取到索引");
            Assert.IsTrue(index.IsCreated);
            Assert.AreEqual(SpatialIndexKind.Quadtree, index.Kind, "默认维护四叉树");
            // 只有一个 SpatialSingleton，模式就在这棵树上。
            Assert.IsTrue(SpatialAccess.TryGetIndexSingleton(world, out var singleton));
            var data = world.EntityManager.GetComponentData<SpatialSingleton>(singleton);
            Assert.IsTrue(data.Tree.IsCreated);
            Assert.AreEqual(SpatialIndexKind.Quadtree, data.Tree.Kind, "树的模式就是装配时选的那棵");
            Assert.IsTrue(DotsWorldManager.HasModule(world, "Spatial"));

            Assert.IsTrue(DotsWorldManager.DestroyModule(world, "Spatial"), "应当能销毁 Spatial 模块");
            Assert.IsFalse(SpatialAccess.TryGetIndexSingleton(world, out _),
                "模块销毁后单例实体应当一并销毁（不然重新装配会出现两个单例）");
            Assert.IsFalse(SpatialAccess.TryGetIndex(world, out _), "模块销毁后不应再取到索引");
        }

        [Test]
        public void 用EntityManager重载_也能取到同一棵树()
        {
            DotsWorldTemplates.Register(new DotsWorldTemplate("SpatialEmptyTemplate"));
            var world = DotsWorldManager.CreateWorld("SpatialModuleWorld", "SpatialEmptyTemplate", makeActive: false);
            Assert.IsTrue(DotsWorldManager.AddModule(world, "Spatial"));
            world.Update();

            Assert.IsTrue(SpatialAccess.TryGetIndex(world, out var byWorld));
            Assert.IsTrue(SpatialAccess.TryGetIndex(world.EntityManager, out var byManager));
            Assert.AreEqual(byWorld.Kind, byManager.Kind);
            Assert.AreEqual(byWorld.BoundsCenter, byManager.BoundsCenter);
            Assert.AreEqual(byWorld.BoundsExtents, byManager.BoundsExtents);
            Assert.AreEqual(byWorld.ElementCount, byManager.ElementCount);
        }
    }
}
