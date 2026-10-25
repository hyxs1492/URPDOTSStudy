using DOTSUtils.Spatial;
using DOTSUtils.Worlds;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace DOTSUtils.Rvo.Tests
{
    /// <summary>
    /// <see cref="RvoSolveSystem"/> 的静态障碍物行为：顶点 / 多边形由 <c>RvoAccess.SetObstacles</c> 写进障碍物单例，
    /// <see cref="RvoConfig.UseObstacles"/> 打开时才并入 ORCA。<br/>
    /// 断言都是离线几何（位置 vs 障碍物边界），不依赖计时。
    /// </summary>
    public class RvoObstacleTests
    {
        const string k_WorldName = "RvoObstacleWorld";
        const string k_Template = "RvoObstacleTemplate";
        const float k_Dt = 1f / 60f;
        const float k_Tol = 1e-3f;

        /// <summary>墙：内侧 = A→B 的左侧 = <c>x &gt; 3</c>（旧线段模型，每条线段算一个二顶点多边形）。</summary>
        static RvoObstacleSegment Wall => new RvoObstacleSegment
        {
            A = new float2(3f, 40f),
            B = new float2(3f, -40f),
        };

        /// <summary>闭合方形（CCW）：外框 <c>[-3,3]^2</c>，障碍物内部是每条边的左侧。</summary>
        static float2[] Square => new[]
        {
            new float2(-3f, -3f),
            new float2(3f, -3f),
            new float2(3f, 3f),
            new float2(-3f, 3f),
        };

        [SetUp]
        public void SetUp()
        {
            DotsModules.Clear();
            DotsWorldTemplates.Clear();
            DotsWorldRegistry.Reset();
            DotsWorldRegistry.EnsureInitialized();

            RvoSettings.Reset();
            SpatialSettings.Reset();

            DotsWorldTemplates.Register(new DotsWorldTemplate(k_Template, typeof(DotsRvoModule), typeof(DotsSpatialModule)));
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

        static World CreateWorld(bool useObstacles)
        {
            var world = DotsWorldManager.CreateWorld(k_WorldName, k_Template, makeActive: false);

            var config = RvoConfig.Default;
            config.FixedTimeStep = k_Dt;
            config.UseSpatialIndex = true;
            config.IntegratePosition = true;
            config.UseObstacles = useObstacles;
            RvoSettings.Set(config);

            return world;
        }

        static Entity CreateAgent(World world, float3 position, float2 prefVelocity, float2 velocity,
                                  float radius = 0.5f, float maxSpeed = 2f)
        {
            var em = world.EntityManager;
            var entity = em.CreateEntity(typeof(LocalTransform), typeof(RvoAgent), typeof(SpatialIndexed));
            em.SetComponentData(entity, LocalTransform.FromPosition(position));
            em.SetComponentData(entity, new RvoAgent
            {
                Radius = radius,
                MaxSpeed = maxSpeed,
                PrefVelocity = prefVelocity,
                Velocity = velocity,
            });
            return entity;
        }

        static float3 PositionOf(World world, Entity entity)
            => world.EntityManager.GetComponentData<LocalTransform>(entity).Position;

        static RvoStatus StatusOf(World world)
        {
            Assert.IsTrue(RvoAccess.TryGetStatus(world, out var status), "应当能读到 RvoStatus");
            return status;
        }

        static void RunFrames(World world, int frames)
        {
            for (var i = 0; i < frames; i++) world.Update();
        }

        // ───────────────────────────── 用例 ─────────────────────────────

        [Test]
        public void 设置障碍物后_轨迹不穿过线段()
        {
            var world = CreateWorld(useObstacles: true);
            Assert.IsTrue(RvoAccess.SetObstacles(world, new[] { Wall }), "应当能写进障碍物单例");
            var agent = CreateAgent(world, float3.zero, new float2(2f, 0f), float2.zero);

            for (var frame = 0; frame < 120; frame++)
            {
                world.Update();
                var position = PositionOf(world, agent);
                Assert.IsTrue(math.all(math.isfinite(position)), $"第 {frame} 帧位置出现 NaN");
                Assert.LessOrEqual(position.x, 3f + k_Tol, $"第 {frame} 帧跨过了线段（进到内侧）");
            }

            Assert.Greater(PositionOf(world, agent).x, 0.5f, "应当朝墙前进（只是被拦住）");
            var status = StatusOf(world);
            Assert.AreEqual(1, status.ObstacleCount, "本帧应当有 1 条障碍物线段参与求解");
        }

        [Test]
        public void 清空障碍物后_不再受约束()
        {
            var world = CreateWorld(useObstacles: true);
            Assert.IsTrue(RvoAccess.SetObstacles(world, new[] { Wall }));
            Assert.IsTrue(RvoAccess.TryGetObstacleCount(world, out var count));
            Assert.AreEqual(1, count);

            var agent = CreateAgent(world, float3.zero, new float2(2f, 0f), float2.zero);
            RunFrames(world, 120);
            Assert.LessOrEqual(PositionOf(world, agent).x, 3f + k_Tol);
            Assert.AreEqual(1, StatusOf(world).ObstacleCount);

            Assert.IsTrue(RvoAccess.ClearObstacles(world));
            Assert.IsTrue(RvoAccess.TryGetObstacleCount(world, out count));
            Assert.AreEqual(0, count);

            RunFrames(world, 120);
            Assert.Greater(PositionOf(world, agent).x, 3.5f, "清空后应当照旧穿过原来的线段");
            Assert.AreEqual(0, StatusOf(world).ObstacleCount);
        }

        [Test]
        public void 关掉障碍物_线段不生效()
        {
            var world = CreateWorld(useObstacles: false);
            Assert.IsTrue(RvoAccess.SetObstacles(world, new[] { Wall }));
            var agent = CreateAgent(world, float3.zero, new float2(2f, 0f), float2.zero);

            RunFrames(world, 120);

            Assert.Greater(PositionOf(world, agent).x, 3.5f, "UseObstacles 关闭时行为与不含障碍物时一致");
            Assert.AreEqual(0, StatusOf(world).ObstacleCount, "关掉时本帧不并入任何线段");
        }

        [Test]
        public void 一群agent朝墙走_没有一个被挤进内侧()
        {
            var world = CreateWorld(useObstacles: true);
            Assert.IsTrue(RvoAccess.SetObstacles(world, new[] { Wall }));

            const int columns = 6;
            const int rows = 5;
            const float spacing = 1.5f;
            var entities = new Entity[columns * rows];
            var index = 0;
            for (var column = 0; column < columns; column++)
            {
                for (var row = 0; row < rows; row++)
                {
                    var position = new float3(-8f + column * spacing,
                                              0f,
                                              -0.5f * (rows - 1) * spacing + row * spacing);
                    entities[index++] = CreateAgent(world, position, new float2(2f, 0f), new float2(2f, 0f));
                }
            }

            var deepest = float.NegativeInfinity;
            for (var frame = 0; frame < 240; frame++)
            {
                world.Update();
                foreach (var entity in entities)
                {
                    var position = PositionOf(world, entity);
                    Assert.IsTrue(math.all(math.isfinite(position)), $"第 {frame} 帧位置出现 NaN");
                    deepest = math.max(deepest, position.x - 3f);
                }
                Assert.LessOrEqual(deepest, k_Tol, $"第 {frame} 帧有 agent 被挤进线段内侧");
            }

            Assert.AreEqual(entities.Length, StatusOf(world).AgentCount);
            Assert.Less(deepest, -0.1f, "拥挤场景下也应当留出余量（不是刚好贴着边界）");
            Assert.Greater(deepest, -2f, "应当整体朝墙移动过");
        }

        [Test]
        public void 障碍物写入_API契约()
        {
            var world = CreateWorld(useObstacles: true);

            Assert.IsTrue(RvoAccess.TryGetObstacleCount(world, out var count), "装配后应当有障碍物单例");
            Assert.AreEqual(0, count);
            Assert.IsTrue(RvoAccess.ClearObstacles(world), "空集合也能清空");

            var segments = new[]
            {
                new RvoObstacleSegment { A = new float2(1f, 1f), B = new float2(1f, 5f) },
                new RvoObstacleSegment { A = new float2(2f, 1f), B = new float2(2f, 5f) },
                new RvoObstacleSegment { A = new float2(3f, 1f), B = new float2(3f, 5f) },
            };
            Assert.IsTrue(RvoAccess.SetObstacles(world, segments));
            Assert.IsTrue(RvoAccess.TryGetObstacleCount(world, out count));
            Assert.AreEqual(segments.Length, count);

            Assert.IsTrue(RvoAccess.SetObstacles(world, (RvoObstacleSegment[])null), "null 数组等同于清空");
            Assert.IsTrue(RvoAccess.TryGetObstacleCount(world, out count));
            Assert.AreEqual(0, count);

            var native = new NativeArray<RvoObstacleSegment>(2, Allocator.Temp);
            try
            {
                native[0] = segments[0];
                native[1] = segments[1];
                Assert.IsTrue(RvoAccess.SetObstacles(world, native));
                Assert.IsTrue(RvoAccess.TryGetObstacleCount(world, out count));
                Assert.AreEqual(2, count);
            }
            finally
            {
                native.Dispose();
            }

            Assert.IsTrue(RvoAccess.ClearObstacles(world));
            Assert.IsTrue(RvoAccess.TryGetObstacleCount(world, out count));
            Assert.AreEqual(0, count);
        }

        [Test]
        public void 没装Rvo模块_写入返回false()
        {
            DotsWorldTemplates.Register(new DotsWorldTemplate("RvoObstacleBareTemplate", typeof(DotsSpatialModule)));
            var bare = DotsWorldManager.CreateWorld("RvoObstacleBareWorld", "RvoObstacleBareTemplate",
                                                    makeActive: false);
            try
            {
                Assert.IsFalse(RvoAccess.TryGetObstacleCount(bare, out _), "没有 Rvo 模块时读不到障碍物");
                Assert.IsFalse(RvoAccess.TryGetObstacleVertexCount(bare, out _));
                Assert.IsFalse(RvoAccess.SetObstacles(bare, new[] { Wall }));
                Assert.IsFalse(RvoAccess.ClearObstacles(bare));
                Assert.IsFalse(RvoAccess.SetObstacles(null, new[] { Wall }), "world 为 null 时返回 false");
            }
            finally
            {
                if (bare.IsCreated)
                {
                    DotsWorldManager.DestroyWorld(bare);
                }
            }
        }

        // ───────────────────────────── 顶点 + 多边形模型 ─────────────────────────────

        [Test]
        public void 写入多边形与线段_计数契约()
        {
            var world = CreateWorld(useObstacles: true);

            Assert.IsTrue(RvoAccess.TryGetObstacleCount(world, out var polygonCount), "装配后应当有障碍物单例");
            Assert.AreEqual(0, polygonCount);
            Assert.IsTrue(RvoAccess.TryGetObstacleVertexCount(world, out var vertexCount));
            Assert.AreEqual(0, vertexCount);

            // 一个闭合方形：1 个多边形 / 4 个顶点
            Assert.IsTrue(RvoAccess.SetObstacles(world, Square, new[] { 4 }));
            Assert.IsTrue(RvoAccess.TryGetObstacleCount(world, out polygonCount));
            Assert.AreEqual(1, polygonCount, "一个多边形算一个障碍物");
            Assert.IsTrue(RvoAccess.TryGetObstacleVertexCount(world, out vertexCount));
            Assert.AreEqual(4, vertexCount);

            // 两个二顶点墙：2 个多边形 / 4 个顶点
            var points = new[]
            {
                new float2(1f, 1f), new float2(1f, 5f),
                new float2(2f, 1f), new float2(2f, 5f),
            };
            Assert.IsTrue(RvoAccess.SetObstacles(world, points, new[] { 2, 2 }));
            Assert.IsTrue(RvoAccess.TryGetObstacleCount(world, out polygonCount));
            Assert.AreEqual(2, polygonCount);
            Assert.IsTrue(RvoAccess.TryGetObstacleVertexCount(world, out vertexCount));
            Assert.AreEqual(4, vertexCount);

            // 显式 (Start, Count) 分段
            var polygons = new NativeArray<RvoObstaclePolygon>(1, Allocator.Temp);
            var nativePoints = new NativeArray<float2>(Square, Allocator.Temp);
            try
            {
                polygons[0] = new RvoObstaclePolygon { Start = 0, Count = 4 };
                Assert.IsTrue(RvoAccess.SetObstacles(world, nativePoints, polygons));
                Assert.IsTrue(RvoAccess.TryGetObstacleCount(world, out polygonCount));
                Assert.AreEqual(1, polygonCount);
                Assert.IsTrue(RvoAccess.TryGetObstacleVertexCount(world, out vertexCount));
                Assert.AreEqual(4, vertexCount);
            }
            finally
            {
                polygons.Dispose();
                nativePoints.Dispose();
            }

            // 旧线段模型：每条线段 = 一个二顶点多边形
            Assert.IsTrue(RvoAccess.SetObstacles(world, new[]
            {
                new RvoObstacleSegment { A = new float2(1f, 1f), B = new float2(1f, 5f) },
                new RvoObstacleSegment { A = new float2(2f, 1f), B = new float2(2f, 5f) },
                new RvoObstacleSegment { A = new float2(3f, 1f), B = new float2(3f, 5f) },
            }));
            Assert.IsTrue(RvoAccess.TryGetObstacleCount(world, out polygonCount));
            Assert.AreEqual(3, polygonCount);
            Assert.IsTrue(RvoAccess.TryGetObstacleVertexCount(world, out vertexCount));
            Assert.AreEqual(6, vertexCount);

            // null 托管数组等同于清空
            Assert.IsTrue(RvoAccess.SetObstacles(world, (float2[])null, (int[])null));
            Assert.IsTrue(RvoAccess.TryGetObstacleCount(world, out polygonCount));
            Assert.AreEqual(0, polygonCount);

            Assert.IsTrue(RvoAccess.ClearObstacles(world));
            Assert.IsTrue(RvoAccess.TryGetObstacleVertexCount(world, out vertexCount));
            Assert.AreEqual(0, vertexCount);
        }

        [Test]
        public void 闭合方形多边形_外侧agent不穿()
        {
            var world = CreateWorld(useObstacles: true);
            Assert.IsTrue(RvoAccess.SetObstacles(world, Square, new[] { 4 }));
            var agent = CreateAgent(world, new float3(-8f, 0f, 0f), new float2(2f, 0f), float2.zero);

            for (var frame = 0; frame < 180; frame++)
            {
                world.Update();
                var position = PositionOf(world, agent);
                Assert.IsTrue(math.all(math.isfinite(position)), $"第 {frame} 帧位置出现 NaN");
                Assert.LessOrEqual(position.x, -3f + k_Tol, $"第 {frame} 帧进到方形内部");
            }

            Assert.Greater(PositionOf(world, agent).x, -6f, "应当朝方形前进（只是被拦住）");
            var status = StatusOf(world);
            Assert.AreEqual(1, status.ObstacleCount, "本帧应当有 1 个障碍物");
            Assert.AreEqual(4, status.ObstacleVertexCount, "本帧应当有 4 个障碍物顶点");
        }
    }
}
