using DOTSUtils.Spatial;
using DOTSUtils.Worlds;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace DOTSUtils.Rvo.Tests
{
    /// <summary><see cref="RvoSolveSystem"/> 的行为。邻居来自 Spatial 模块（agent 实体还要打 <see cref="SpatialIndexed"/>，模板里必须带上 Spatial），参数是全局配置 <c>RvoSettings</c>，邻居条数 = <see cref="RvoNeighbor"/> 缓冲长度。
    /// 手动 <c>world.Update()</c> 不推进 Unity 的 <c>Time</c>，真实帧长恒为 0，一律通过 <see cref="RvoConfig.FixedTimeStep"/> 指定固定步长。</summary>
    public class RvoSystemTests
    {
        const string k_WorldName = "RvoTestWorld";
        const string k_Template = "RvoTestTemplate";
        const float k_Dt = 1f / 60f;

        [SetUp]
        public void SetUp()
        {
            DotsModules.Clear();
            DotsWorldTemplates.Clear();
            DotsWorldRegistry.Reset();
            DotsWorldRegistry.EnsureInitialized();

            RvoSettings.Reset();          // 全局配置：不重置会串到别的用例
            SpatialSettings.Reset();      // 默认 Quadtree；本文件的用例都用四叉树

            // Rvo 依赖 Spatial（邻居索引），模板必须两个都装。
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

        static World CreateWorld(float fixedTimeStep = k_Dt, bool useSpatialIndex = true,
                                 bool integratePosition = true)
        {
            var world = DotsWorldManager.CreateWorld(k_WorldName, k_Template, makeActive: false);

            var config = RvoConfig.Default;
            config.FixedTimeStep = fixedTimeStep;
            config.UseSpatialIndex = useSpatialIndex;
            config.IntegratePosition = integratePosition;
            RvoSettings.Set(config);

            return world;
        }

        /// <summary>agent：<c>LocalTransform</c> + <see cref="RvoAgent"/> + <see cref="SpatialIndexed"/>（进 Spatial 的树）。</summary>
        static Entity CreateAgent(World world, float3 position, float2 prefVelocity, float2 velocity,
                                  float radius = 0.5f, float maxSpeed = 2f, bool indexed = true)
        {
            var em = world.EntityManager;
            var entity = indexed
                ? em.CreateEntity(typeof(LocalTransform), typeof(RvoAgent), typeof(SpatialIndexed))
                : em.CreateEntity(typeof(LocalTransform), typeof(RvoAgent));
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
        public void 装配后_状态被写出且索引就位()
        {
            var world = CreateWorld();
            CreateAgent(world, new float3(0f, 0f, 0f), new float2(1f, 0f), new float2(1f, 0f));

            world.Update();

            var status = StatusOf(world);
            Assert.AreEqual(RvoNeighborIndex.Quadtree, status.Index, "邻居索引应当来自 Spatial 的四叉树");
            Assert.AreEqual(1, status.AgentCount);
        }

        [Test]
        public void 状态报告_索引来自四叉树()
        {
            var world = CreateWorld();
            CreateAgent(world, new float3(0f, 0f, 0f), new float2(1f, 0f), float2.zero);
            CreateAgent(world, new float3(2f, 0f, 0f), new float2(-1f, 0f), float2.zero);

            world.Update();
            world.Update();                                    // 第二帧：邻居已填

            var status = StatusOf(world);
            Assert.AreEqual(RvoNeighborIndex.Quadtree, status.Index, "装了两个 Spatial 系统时默认用四叉树");
            Assert.AreEqual(2, status.AgentCount);
        }

        [Test]
        public void 邻居缓冲只补给agent_其它实体不受影响()
        {
            var world = CreateWorld();
            var em = world.EntityManager;
            var agent = CreateAgent(world, new float3(0f, 0f, 0f), float2.zero, float2.zero);

            var bare = em.CreateEntity();                                    // 什么组件都没有
            var transformed = em.CreateEntity(typeof(LocalTransform));        // 有 LocalTransform，但不是 agent
            em.SetComponentData(transformed, LocalTransform.FromPosition(new float3(5f, 0f, 5f)));

            world.Update();
            world.Update();

            Assert.IsTrue(em.HasBuffer<RvoNeighbor>(agent), "agent 应当被补上邻居缓冲");
            Assert.IsTrue(em.HasBuffer<SpatialElement>(agent), "agent 也应当有查询命中缓冲");

            Assert.IsFalse(em.HasBuffer<RvoNeighbor>(bare), "与 RVO 无关的实体不该被打上 RvoNeighbor");
            Assert.IsFalse(em.HasBuffer<SpatialElement>(bare), "与 RVO 无关的实体也不该被打上查询缓冲");
            Assert.IsFalse(em.HasBuffer<RvoNeighbor>(transformed),
                "有 LocalTransform 但不是 agent 的实体也不该被打上");
            Assert.IsFalse(em.HasBuffer<SpatialElement>(transformed),
                "有 LocalTransform 但不是 agent 的实体也不该被打上查询缓冲");
        }

        [Test]
        public void 只给部分agent打索引tag_树里只认打了tag的()
        {
            var world = CreateWorld();

            // A 打了索引 tag（在树里）、B 没打（不在树里）；两者靠近，B 朝 A 走。
            var a = CreateAgent(world, new float3(0f, 0f, 0f), new float2(1f, 0f), float2.zero);
            var b = CreateAgent(world, new float3(1.5f, 0f, 0f), new float2(-1f, 0f), float2.zero,
                                indexed: false);

            RunFrames(world, 60);

            // A 看不到 B（B 不在树里）→ 沿 PrefVelocity 直行：z 一直是 0。
            Assert.AreEqual(0f, PositionOf(world, a).z, 1e-3f, "A 看不到 B，应当走直线");
            // B 看得到 A → 被避让推开：z 偏离 0。
            Assert.Greater(math.abs(PositionOf(world, b).z), 1e-3f, "B 能看到 A，应当被推开");
        }

        [Test]
        public void 两船对撞_不会互相穿透()
        {
            var world = CreateWorld();
            var a = CreateAgent(world, new float3(-3f, 0f, 0f), new float2(2f, 0f), new float2(2f, 0f));
            var b = CreateAgent(world, new float3(3f, 0f, 0f), new float2(-2f, 0f), new float2(-2f, 0f));

            RunFrames(world, 120);

            var pa = PositionOf(world, a);
            var pb = PositionOf(world, b);
            var distance = math.distance(pa.xz, pb.xz);
            Assert.GreaterOrEqual(distance, 1f - 1e-3f, "两船中心距不应小于半径和（不穿透）");
        }

        [Test]
        public void 八船环形对冲_任意两船都不重叠()
        {
            var world = CreateWorld();
            const int count = 8;
            const float radius = 0.5f;
            var entities = new Entity[count];

            for (var i = 0; i < count; i++)
            {
                var angle = i * math.PI * 2f / count;
                var dir = new float2(math.cos(angle), math.sin(angle));
                entities[i] = CreateAgent(world, new float3(dir.x * 8f, 0f, dir.y * 8f), -dir * 2f, -dir * 2f);
            }

            RunFrames(world, 240);

            for (var i = 0; i < count; i++)
            {
                Assert.IsTrue(math.all(math.isfinite(PositionOf(world, entities[i]))), "位置应是有限值");
                for (var j = i + 1; j < count; j++)
                {
                    var distance = math.distance(PositionOf(world, entities[i]).xz, PositionOf(world, entities[j]).xz);
                    Assert.GreaterOrEqual(distance, radius * 2f - 2e-2f, $"第 {i} 与第 {j} 个 agent 不应重叠");
                }
            }
        }

        [Test]
        public void 关掉索引_不避障但仍按PrefVelocity前进()
        {
            var world = CreateWorld(useSpatialIndex: false);
            var a = CreateAgent(world, new float3(-3f, 0f, 0f), new float2(2f, 0f), new float2(2f, 0f));
            CreateAgent(world, new float3(3f, 0f, 0f), new float2(-2f, 0f), new float2(-2f, 0f));

            RunFrames(world, 30);

            Assert.AreEqual(RvoNeighborIndex.Disabled, StatusOf(world).Index);
            // 没有邻居 → ORCA 直接按 PrefVelocity 走（两船会互相穿过），但不会报错
            Assert.Greater(PositionOf(world, a).x, -3f, "仍然按 PrefVelocity 前进");
        }

        [Test]
        public void 关掉积分_位置不变但速度被更新()
        {
            var world = CreateWorld(integratePosition: false);
            var entity = CreateAgent(world, new float3(1f, 0f, 2f), new float2(1.5f, 0f), float2.zero);

            RunFrames(world, 10);

            Assert.AreEqual(new float3(1f, 0f, 2f), PositionOf(world, entity), "位置不应被本模块改动");
            var agent = world.EntityManager.GetComponentData<RvoAgent>(entity);
            Assert.Greater(math.length(agent.Velocity), 0f, "速度应当被更新");
        }

        [Test]
        public void 无agent时_系统空转不报错()
        {
            var world = CreateWorld();
            RunFrames(world, 3);      // 不应抛异常
            Assert.AreEqual(RvoNeighborIndex.None, StatusOf(world).Index, "没有 agent 时状态归零");
        }

        [Test]
        public void 销毁状态实体后_系统静默不报错()
        {
            var world = CreateWorld();
            var entity = CreateAgent(world, float3.zero, new float2(1f, 0f), new float2(1f, 0f));
            RunFrames(world, 3);

            Assert.IsTrue(RvoAccess.TryGetStateEntity(world, out var stateEntity));
            world.EntityManager.DestroyEntity(stateEntity);

            var before = PositionOf(world, entity);
            world.Update();                       // 状态实体没了：应当静默（只是写不进状态）
            Assert.IsTrue(math.all(math.isfinite(PositionOf(world, entity))), "仍然不该抛异常/产出 NaN");

            Assert.IsFalse(RvoAccess.TryGetStatus(world, out _), "状态实体没了就读不到状态");
        }

        [Test]
        public void y分量_全程不变()
        {
            var world = CreateWorld();
            var entity = CreateAgent(world, new float3(0f, 3.5f, 0f), new float2(2f, 1f), new float2(2f, 1f));

            RunFrames(world, 30);

            Assert.AreEqual(3.5f, PositionOf(world, entity).y, 1e-6f, "避障只在 xz 平面，y 不该被改");
        }

        [Test]
        public void 速度不超上限()
        {
            var world = CreateWorld();
            var entity = CreateAgent(world, float3.zero, new float2(100f, 100f), float2.zero);

            RunFrames(world, 20);

            var agent = world.EntityManager.GetComponentData<RvoAgent>(entity);
            Assert.LessOrEqual(math.length(agent.Velocity), 2f + 1e-3f);
        }

        [Test]
        public void 热插拔_销毁模块后不再推进位置()
        {
            DotsWorldTemplates.Register(new DotsWorldTemplate("RvoEmptyTemplate"));
            var world = DotsWorldManager.CreateWorld(k_WorldName, "RvoEmptyTemplate", makeActive: false);

            RvoSettings.Reset();
            var config = RvoConfig.Default;
            config.FixedTimeStep = k_Dt;
            // 本用例验证的正是「Rvo 会不会推进位置」，所以要显式打开位置积分。
            config.IntegratePosition = true;
            RvoSettings.Set(config);

            // Rvo 依赖 Spatial，两个都必须装（装配顺序由 SortKey 决定）。
            Assert.IsTrue(DotsWorldManager.AddModule(world, "Spatial"), "先装 Spatial");
            Assert.IsTrue(DotsWorldManager.AddModule(world, "Rvo"), "再装 Rvo");

            var entity = CreateAgent(world, float3.zero, new float2(2f, 0f), new float2(2f, 0f));
            RunFrames(world, 5);
            Assert.Greater(PositionOf(world, entity).x, 0.1f, "装配后应当被推进");

            var before = PositionOf(world, entity);
            Assert.IsTrue(DotsWorldManager.DestroyModule(world, "Rvo"));
            RunFrames(world, 5);
            Assert.AreEqual(before, PositionOf(world, entity), "销毁模块后不应再被推进");
        }

        [Test]
        public void 越界与NaN位置_不产生异常也不产出NaN()
        {
            var world = CreateWorld();
            var far = CreateAgent(world, new float3(1e6f, 0f, 1e6f), new float2(1f, 0f), new float2(1f, 0f));
            var nan = CreateAgent(world, new float3(float.NaN, 0f, float.NaN), new float2(1f, 0f), new float2(1f, 0f));

            RunFrames(world, 10);      // 不应抛异常

            Assert.IsTrue(math.all(math.isfinite(world.EntityManager.GetComponentData<RvoAgent>(far).Velocity)));
            Assert.IsTrue(math.all(math.isfinite(world.EntityManager.GetComponentData<RvoAgent>(nan).Velocity)),
                "NaN 输入也不该把 NaN 传出去");
        }
    }
}
