using DOTSUtils.Worlds;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace DOTSUtils.Pathfinding.Tests
{
    /// <summary>
    /// A* 模块的装配、网格重建、同步门面与请求系统。
    /// </summary>
    public class AStarSystemTests
    {
        private const string k_WorldName = "AStarTestWorld";
        private const string k_TemplateName = "AStarTestTemplate";

        [SetUp]
        public void SetUp()
        {
            DotsModules.Clear();
            DotsWorldTemplates.Clear();
            DotsWorldRegistry.Reset();
            DotsWorldRegistry.EnsureInitialized();

            AStarSettings.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            if (DotsWorldManager.TryGetWorld(k_WorldName, out var world) && world.IsCreated)
            {
                DotsWorldManager.DestroyWorld(world);
            }

            AStarSettings.Reset();

            DotsModules.Clear();
            DotsWorldTemplates.Clear();
            DotsWorldRegistry.Reset();
        }

        /// <summary>把装配参数设成一个默认全可走的网格。</summary>
        private static void ConfigureSettings(int width, int height, float cellSize = 1f, byte[] mask = null,
                                              PathfindingAlgorithm algorithm = PathfindingAlgorithm.AStar,
                                              int maxSolvesPerFrame = 0)
        {
            var extents = new float3(width * cellSize * 0.5f, 1f, height * cellSize * 0.5f);
            AStarSettings.Configure(new AStarConfig
            {
                Bounds = new AABB { Center = new float3(extents.x, 0f, extents.z), Extents = extents },
                CellSize = cellSize,
                Neighbourhood = AStarNeighbourhood.Four,
                Algorithm = algorithm,
                MaxSolvesPerFrame = maxSolvesPerFrame,
            }, mask);
        }

        private static World CreateWorld(int width = 10, int height = 10, float cellSize = 1f, byte[] mask = null,
                                         PathfindingAlgorithm algorithm = PathfindingAlgorithm.AStar,
                                         int maxSolvesPerFrame = 0)
        {
            ConfigureSettings(width, height, cellSize, mask, algorithm, maxSolvesPerFrame);
            DotsWorldTemplates.Register(new DotsWorldTemplate(k_TemplateName, typeof(DotsAStarModule)));
            var world = DotsWorldManager.CreateWorld(k_WorldName, k_TemplateName, makeActive: false);
            world.Update();
            return world;
        }

        private static float3 CenterOf(in AStarGrid grid, int x, int z) => grid.CellCenter(new int2(x, z));

        // ------------------------------------------------------------------
        //  装配
        // ------------------------------------------------------------------

        [Test]
        public void 模块被自动发现_并声明加入Default模板()
        {
            Assert.IsTrue(DotsModules.TryGet("AStar", out var descriptor), "AStar 模块应被自动发现");
            Assert.AreEqual(1, descriptor.Groups.Count);
            Assert.AreEqual(typeof(AStarGroup), descriptor.Groups[0].GroupType);
            CollectionAssert.Contains(descriptor.Groups[0].Systems, typeof(AStarSolveSystem));

            Assert.IsTrue(DotsWorldTemplates.TryGet("Default", out var template));
            CollectionAssert.Contains(template.Modules, typeof(DotsAStarModule), "AStar 应声明进 Default 模板");
        }

        [Test]
        public void 装配后能取到网格_尺寸与配置一致()
        {
            var world = CreateWorld(width: 10, height: 8);

            Assert.IsTrue(DotsWorldManager.HasModule(world, "AStar"));
            Assert.IsTrue(AStarAccess.TryGetGrid(world, out var grid), "装配后应能取到网格");
            Assert.IsTrue(grid.IsCreated);
            Assert.AreEqual(new int2(10, 8), grid.Size);
            Assert.AreEqual(80, grid.CellCount);
            Assert.AreEqual(80, grid.WalkableCount, "没有掩码时全部可走");
        }

        [Test]
        public void 没装模块的World_取不到网格()
        {
            DotsWorldTemplates.Register(new DotsWorldTemplate(k_TemplateName));
            var world = DotsWorldManager.CreateWorld(k_WorldName, k_TemplateName, makeActive: false);
            world.Update();

            Assert.IsFalse(AStarAccess.TryGetGrid(world, out _), "没装 AStar 时不应取到网格");
        }

        [Test]
        public void 改配置后_网格按新尺寸重建()
        {
            var world = CreateWorld(width: 10, height: 10);
            Assert.IsTrue(AStarAccess.TryGetGrid(world, out var before));
            Assert.AreEqual(new int2(10, 10), before.Size);

            ConfigureSettings(width: 20, height: 5);
            world.Update();

            Assert.IsTrue(AStarAccess.TryGetGrid(world, out var after));
            Assert.AreEqual(new int2(20, 5), after.Size, "参数版本变化后应重建网格");
            Assert.AreEqual(100, after.CellCount);
        }

        // ------------------------------------------------------------------
        //  同步门面
        // ------------------------------------------------------------------

        [Test]
        public void 同步门面_在开敞网格上找到路径()
        {
            var world = CreateWorld(width: 10, height: 10);
            Assert.IsTrue(AStarAccess.TryGetGrid(world, out var grid));

            var path = new NativeList<float3>(Allocator.Temp);
            var status = AStarAccess.FindPath(world, CenterOf(grid, 0, 0), CenterOf(grid, 9, 9), path);

            Assert.AreEqual(AStarStatus.Found, status);
            Assert.Greater(path.Length, 0);
            Assert.AreEqual(CenterOf(grid, 0, 0), path[0]);
            Assert.AreEqual(CenterOf(grid, 9, 9), path[path.Length - 1]);
            path.Dispose();
        }

        [Test]
        public void 同步门面_被掩码挡住时返回NoPath()
        {
            // 中间一整排阻挡，把网格切成两半。
            var width = 5;
            var height = 5;
            var mask = new byte[width * height];
            for (var i = 0; i < mask.Length; i++)
            {
                mask[i] = 1;
            }
            for (var x = 0; x < width; x++)
            {
                mask[2 * width + x] = 0;
            }

            var world = CreateWorld(width, height, mask: mask);
            Assert.IsTrue(AStarAccess.TryGetGrid(world, out var grid));

            var path = new NativeList<float3>(Allocator.Temp);
            var status = AStarAccess.FindPath(world, CenterOf(grid, 0, 0), CenterOf(grid, 0, 4), path);

            Assert.AreEqual(AStarStatus.NoPath, status);
            Assert.AreEqual(0, path.Length);
            path.Dispose();
        }

        // ------------------------------------------------------------------
        //  请求系统
        // ------------------------------------------------------------------

        [Test]
        public void 请求实体_跑一帧后拿到路径与状态()
        {
            var world = CreateWorld(width: 10, height: 10);
            Assert.IsTrue(AStarAccess.TryGetGrid(world, out var grid));

            var entityManager = world.EntityManager;
            var entity = entityManager.CreateEntity();
            AStarAccess.RequestPath(entityManager, entity, CenterOf(grid, 0, 0), CenterOf(grid, 9, 9));

            world.Update();

            var status = entityManager.GetComponentData<PathStatus>(entity);
            Assert.AreEqual(AStarStatus.Found, status.Status);
            Assert.Greater(status.WaypointCount, 0);

            var waypoints = entityManager.GetBuffer<PathWaypoint>(entity);
            Assert.AreEqual(status.WaypointCount, waypoints.Length);
            Assert.AreEqual(CenterOf(grid, 0, 0), waypoints[0].Position);
            Assert.AreEqual(CenterOf(grid, 9, 9), waypoints[waypoints.Length - 1].Position);
        }

        [Test]
        public void 请求版本没变时_不重复求解()
        {
            var world = CreateWorld(width: 10, height: 10);
            Assert.IsTrue(AStarAccess.TryGetGrid(world, out var grid));

            var entityManager = world.EntityManager;
            var entity = entityManager.CreateEntity();
            AStarAccess.RequestPath(entityManager, entity, CenterOf(grid, 0, 0), CenterOf(grid, 9, 0));
            world.Update();

            var solved = entityManager.GetComponentData<PathStatus>(entity).SolvedVersion;

            // 手工把结果清掉：版本没变的话系统这一帧不该再写回来。
            entityManager.GetBuffer<PathWaypoint>(entity).Clear();
            world.Update();

            Assert.AreEqual(0, entityManager.GetBuffer<PathWaypoint>(entity).Length,
                "请求版本没变时不该重算");
            Assert.AreEqual(solved, entityManager.GetComponentData<PathStatus>(entity).SolvedVersion);

            // 再加一版就该重算。
            AStarAccess.RequestPath(entityManager, entity, CenterOf(grid, 0, 0), CenterOf(grid, 9, 0));
            world.Update();

            Assert.Greater(entityManager.GetBuffer<PathWaypoint>(entity).Length, 0, "版本加一后应重算");
            Assert.Greater(entityManager.GetComponentData<PathStatus>(entity).SolvedVersion, solved);
        }

        [Test]
        public void 请求起终点非法_状态如实反映()
        {
            var world = CreateWorld(width: 8, height: 8);
            Assert.IsTrue(AStarAccess.TryGetGrid(world, out var grid));

            var entityManager = world.EntityManager;
            var outside = CenterOf(grid, 0, 0) + new float3(-100f, 0f, 0f);
            var entity = entityManager.CreateEntity();
            AStarAccess.RequestPath(entityManager, entity, outside, CenterOf(grid, 4, 4));

            world.Update();

            Assert.AreEqual(AStarStatus.StartInvalid, entityManager.GetComponentData<PathStatus>(entity).Status);
            Assert.AreEqual(0, entityManager.GetBuffer<PathWaypoint>(entity).Length);
        }

        // ------------------------------------------------------------------
        //  算法选择
        // ------------------------------------------------------------------

        [Test]
        public void 选Dijkstra_单例如实报告且请求能解出路径()
        {
            var world = CreateWorld(width: 10, height: 10, algorithm: PathfindingAlgorithm.Dijkstra);

            Assert.IsTrue(AStarAccess.TryGetSingletonData(world, out var data), "装配后应能取到单例");
            Assert.AreEqual(PathfindingAlgorithm.Dijkstra, data.Algorithm);

            Assert.IsTrue(AStarAccess.TryGetGrid(world, out var grid));
            var entityManager = world.EntityManager;
            var entity = entityManager.CreateEntity();
            AStarAccess.RequestPath(entityManager, entity, CenterOf(grid, 0, 0), CenterOf(grid, 9, 9));

            world.Update();

            var status = entityManager.GetComponentData<PathStatus>(entity);
            Assert.AreEqual(AStarStatus.Found, status.Status);
            Assert.Greater(status.WaypointCount, 0);
            Assert.AreEqual(CenterOf(grid, 0, 0), entityManager.GetBuffer<PathWaypoint>(entity)[0].Position);
        }

        [Test]
        public void 改算法后_单例如实更新()
        {
            var world = CreateWorld(width: 10, height: 10);
            Assert.IsTrue(AStarAccess.TryGetSingletonData(world, out var before));
            Assert.AreEqual(PathfindingAlgorithm.AStar, before.Algorithm);

            ConfigureSettings(width: 10, height: 10, algorithm: PathfindingAlgorithm.Dijkstra);
            world.Update();

            Assert.IsTrue(AStarAccess.TryGetSingletonData(world, out var after));
            Assert.AreEqual(PathfindingAlgorithm.Dijkstra, after.Algorithm, "参数版本变化后算法应跟着更新");

            world.Update();
            Assert.IsTrue(AStarAccess.TryGetSingletonData(world, out var stable));
            Assert.AreEqual(PathfindingAlgorithm.Dijkstra, stable.Algorithm, "版本没变时算法保持");
        }

        [Test]
        public void 每帧求解上限_超出的请求留到下一帧()
        {
            var world = CreateWorld(width: 10, height: 10, maxSolvesPerFrame: 1);
            Assert.IsTrue(AStarAccess.TryGetGrid(world, out var grid));

            var entityManager = world.EntityManager;
            var entities = new Entity[3];
            for (var i = 0; i < entities.Length; i++)
            {
                entities[i] = entityManager.CreateEntity();
                AStarAccess.RequestPath(entityManager, entities[i], CenterOf(grid, 0, i), CenterOf(grid, 9, i));
            }

            Assert.AreEqual(3, CountUnsolved(entityManager, entities), "三个请求都还没解");

            world.Update();
            Assert.AreEqual(2, CountUnsolved(entityManager, entities), "每帧上限 1：第一帧只解 1 个");
            world.Update();
            Assert.AreEqual(1, CountUnsolved(entityManager, entities), "第二帧再解 1 个");
            world.Update();
            Assert.AreEqual(0, CountUnsolved(entityManager, entities), "第三帧全部解完");

            for (var i = 0; i < entities.Length; i++)
            {
                Assert.AreEqual(AStarStatus.Found, entityManager.GetComponentData<PathStatus>(entities[i]).Status);
                Assert.Greater(entityManager.GetBuffer<PathWaypoint>(entities[i]).Length, 0);
            }
        }

        /// <summary>数一数还有几个请求没被解过（请求版本是 1，解过就会写回 1）。</summary>
        private static int CountUnsolved(EntityManager entityManager, Entity[] entities)
        {
            var count = 0;
            for (var i = 0; i < entities.Length; i++)
            {
                if (entityManager.GetComponentData<PathStatus>(entities[i]).SolvedVersion != 1)
                {
                    count++;
                }
            }
            return count;
        }
    }
}
