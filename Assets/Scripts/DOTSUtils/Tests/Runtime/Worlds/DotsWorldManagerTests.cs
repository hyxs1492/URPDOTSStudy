using System.Collections.Generic;
using System.Linq;
using DOTSUtils.RenderBridge;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;

namespace DOTSUtils.Worlds.Tests
{
    // 测试专用模块：组 + 系统，全部 [DisableAutoCreation]，定义在命名空间级。
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class TestModuleGroup : DotsModuleGroup { }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial class TestLateModuleGroup : DotsModuleGroup { }

    // 故意缺 [UpdateInGroup] 的模块组，装配时会抛异常。
    [DisableAutoCreation]
    public partial class TestBadModuleGroup : DotsModuleGroup { }

    // 模块类（继承 DotsModule）。
    public sealed class TestModule : DotsModule
    {
        public override string Name => "TestModule";
        public override IReadOnlyList<DotsModuleGroupDescriptor> Groups => new[]
        {
            new DotsModuleGroupDescriptor(typeof(TestModuleGroup), typeof(TestModuleSystem)),
        };
    }

    /// <summary>SortKey 比 TestModule 大，用于验证按 SortKey 排序装配。</summary>
    public sealed class TestLateModule : DotsModule
    {
        public override string Name => "TestLateModule";
        public override int SortKey => 100;
        public override IReadOnlyList<DotsModuleGroupDescriptor> Groups => new[]
        {
            new DotsModuleGroupDescriptor(typeof(TestLateModuleGroup)),
        };
    }

    /// <summary>它的组故意缺 [UpdateInGroup]，用于验证 CreateWorld 的异常回收路径。</summary>
    public sealed class TestBadModule : DotsModule
    {
        public override string Name => "TestBadModule";
        public override IReadOnlyList<DotsModuleGroupDescriptor> Groups => new[]
        {
            new DotsModuleGroupDescriptor(typeof(TestBadModuleGroup)),
        };
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(TestModuleGroup))]
    public partial class TestModuleSystem : SystemBase
    {
        public int UpdateCount;

        protected override void OnUpdate() => UpdateCount++;
    }

    /// <summary>World 管理与模块热插拔。除活跃性用例外，创建 World 一律传 makeActive: false，避免冻结默认 World。</summary>
    public class DotsWorldManagerTests
    {
        [TearDown]
        public void TearDown()
        {
            // 本文件的用例会改 ActiveWorld，必须恢复默认 World，否则同会话其它测试的系统不再更新。
            var defaultWorld = World.DefaultGameObjectInjectionWorld;
            if (defaultWorld != null && defaultWorld.IsCreated)
                DotsWorldManager.SetActiveWorld(defaultWorld);

            DotsModules.Clear();
            DotsWorldTemplates.Clear();
            DotsWorldRegistry.Reset();
        }

        /// <summary>登记「TestModule = TestModuleGroup(+TestModuleSystem)」这套测试模块与模板。</summary>
        static void RegisterTestModule()
        {
            DotsModules.Register(new DotsModuleDescriptor("TestModule",
                new DotsModuleGroupDescriptor(typeof(TestModuleGroup), typeof(TestModuleSystem))));
            // 先清模板表：Register 幂等，残留会让本次登记被静默忽略。
            DotsWorldTemplates.Clear();
            // 显式登记：模板按 Type 引用模块，要求注册表里已有该类型，不依赖自动发现的时机。
            DotsModules.Register(new TestModule().ToDescriptor());
            DotsWorldTemplates.Register(new DotsWorldTemplate("TestTemplate", typeof(TestModule)));
        }

        [Test]
        public void 模块注册表_可注册并查询()
        {
            DotsModules.Clear();
            var descriptor = new DotsModuleDescriptor("TestModule",
                new DotsModuleGroupDescriptor(typeof(TestModuleGroup), typeof(TestModuleSystem)));

            DotsModules.Register(descriptor);

            Assert.IsTrue(DotsModules.TryGet("TestModule", out var found));
            Assert.AreSame(descriptor, found);
            Assert.AreEqual(1, found.Groups.Count);
            Assert.AreEqual(typeof(TestModuleGroup), found.Groups[0].GroupType);
            Assert.AreEqual(1, found.Groups[0].Systems.Count);
            Assert.AreEqual(typeof(TestModuleSystem), found.Groups[0].Systems[0]);
        }

        [Test]
        public void 模块注册表_重名不覆盖()
        {
            DotsModules.Clear();
            DotsModules.Register(new DotsModuleDescriptor("M", new DotsModuleGroupDescriptor(typeof(TestModuleGroup))));
            DotsModules.Register(new DotsModuleDescriptor("M", new DotsModuleGroupDescriptor(typeof(TestLateModuleGroup))));

            Assert.IsTrue(DotsModules.TryGet("M", out var found));
            Assert.AreEqual(typeof(TestModuleGroup), found.Groups[0].GroupType, "首次注册应胜出（幂等）");
            Assert.AreEqual(1, DotsModules.All.Count);
        }

        [Test]
        public void 模板注册表_可注册并查询()
        {
            DotsWorldTemplates.Clear();
            DotsWorldTemplates.Register(
                new DotsWorldTemplate("TestTemplate", typeof(TestModule), typeof(TestLateModule)));

            Assert.IsTrue(DotsWorldTemplates.TryGet("TestTemplate", out var found));
            Assert.AreEqual(new[] { typeof(TestModule), typeof(TestLateModule) }, found.Modules.ToArray());
        }

        [Test]
        public void 注册表_Clear后可重新登记()
        {
            DotsModules.Register(new DotsModuleDescriptor("M", new DotsModuleGroupDescriptor(typeof(TestModuleGroup))));
            DotsModules.Clear();

            Assert.IsFalse(DotsModules.TryGet("M", out _));
            Assert.AreEqual(0, DotsModules.All.Count);

            // 清空后应能重新登记并查到。
            DotsModules.Register(new DotsModuleDescriptor("M", new DotsModuleGroupDescriptor(typeof(TestLateModuleGroup))));
            Assert.IsTrue(DotsModules.TryGet("M", out var found));
            Assert.AreEqual(typeof(TestLateModuleGroup), found.Groups[0].GroupType);
        }

        [Test]
        public void 创建World_按模板装配模块并挂到父组()
        {
            DotsWorldRegistry.Reset();
            RegisterTestModule();

            var world = DotsWorldManager.CreateWorld("TestWorld_A", "TestTemplate", makeActive: false);
            try
            {
                var group = world.GetExistingSystemManaged<TestModuleGroup>();
                Assert.IsNotNull(group, "模块组应被创建");

                var simulation = world.GetExistingSystemManaged<SimulationSystemGroup>();
                Assert.IsTrue(simulation.ManagedSystems.Contains(group), "模块组应挂到 SimulationSystemGroup 的更新列表");

                var system = world.GetExistingSystemManaged<TestModuleSystem>();
                Assert.IsNotNull(system, "组内 System 应被创建");
                Assert.IsTrue(group.ManagedSystems.Contains(system), "System 应在模块组内");

                Assert.IsTrue(DotsWorldManager.TryGetWorld("TestWorld_A", out var found));
                Assert.AreSame(world, found);
                Assert.IsTrue(DotsWorldManager.Worlds.Contains(world));
            }
            finally
            {
                DotsWorldManager.DestroyWorld(world);
            }
        }

        [Test]
        public void 创建World_会被挂进PlayerLoop()
        {
            DotsWorldRegistry.Reset();
            RegisterTestModule();

            var world = DotsWorldManager.CreateWorld("TestWorld_PL", "TestTemplate");
            try
            {
                // 必须由 CreateWorld 自己保证挂进 PlayerLoop。
                Assert.IsTrue(ScriptBehaviourUpdateOrder.IsWorldInCurrentPlayerLoop(world));
            }
            finally
            {
                DotsWorldManager.DestroyWorld(world);
            }
        }

        [Test]
        public void 创建World_同名重复创建被拒绝()
        {
            DotsWorldRegistry.Reset();
            RegisterTestModule();

            var world = DotsWorldManager.CreateWorld("TestWorld_B", "TestTemplate", makeActive: false);
            try
            {
                Assert.Throws<System.ArgumentException>(() =>
                    DotsWorldManager.CreateWorld("TestWorld_B", "TestTemplate"));
            }
            finally
            {
                DotsWorldManager.DestroyWorld(world);
            }
        }

        [Test]
        public void 销毁World_从注册表移除且已Dispose()
        {
            DotsWorldRegistry.Reset();
            RegisterTestModule();

            var world = DotsWorldManager.CreateWorld("TestWorld_C", "TestTemplate", makeActive: false);
            var name = world.Name;

            Assert.IsTrue(DotsWorldManager.DestroyWorld(world));

            Assert.IsFalse(DotsWorldManager.TryGetWorld(name, out _));
            Assert.IsFalse(world.IsCreated, "World 应已 Dispose");
            Assert.IsFalse(DotsWorldManager.DestroyWorld(world), "重复销毁应返回 false");
        }

        [Test]
        public void AddModule_重复调用返回false()
        {
            DotsWorldRegistry.Reset();
            RegisterTestModule();

            var world = DotsWorldManager.CreateWorld("TestWorld_D", "TestTemplate", makeActive: false);
            try
            {
                Assert.IsFalse(DotsWorldManager.AddModule(world, "TestModule"), "模板已装配，重复装配应返回 false");
                Assert.IsFalse(DotsWorldManager.AddModule(world, "NoSuchModule"), "未登记的模块名应返回 false");
            }
            finally
            {
                DotsWorldManager.DestroyWorld(world);
            }
        }

        [Test]
        public void 默认World_装配了RenderBridge模块组()
        {
            var world = DotsWorldManager.DefaultWorld;
            Assert.IsNotNull(world, "默认 World 应存在（由 DotsWorldBootstrap 创建）");
            Assert.IsTrue(world.IsCreated);

            var conversionGroup = world.GetExistingSystemManaged<RenderBridgeConversionGroup>();
            var inputGroup = world.GetExistingSystemManaged<RenderBridgeInputGroup>();
            var outputGroup = world.GetExistingSystemManaged<RenderBridgeOutputGroup>();
            Assert.IsNotNull(conversionGroup, "Conversion 组应存在");
            Assert.IsNotNull(inputGroup, "Input 组应存在");
            Assert.IsNotNull(outputGroup, "Output 组应存在");

            var initialization = world.GetExistingSystemManaged<InitializationSystemGroup>();
            var simulation = world.GetExistingSystemManaged<SimulationSystemGroup>();
            Assert.IsTrue(initialization.ManagedSystems.Contains(conversionGroup), "Conversion 组应挂 InitializationSystemGroup");
            Assert.IsTrue(simulation.ManagedSystems.Contains(inputGroup), "Input 组应挂 SimulationSystemGroup");
            Assert.IsTrue(simulation.ManagedSystems.Contains(outputGroup), "Output 组应挂 SimulationSystemGroup");

            // 既有系统仍按名字可查。
            Assert.IsNotNull(world.GetExistingSystemManaged<DOTSUtils.RenderBridge.DotsRenderConversionSystem>());
            Assert.IsNotNull(world.GetExistingSystemManaged<DOTSUtils.RenderBridge.DotsRenderTransformSyncSystem>());
            Assert.IsNotNull(world.GetExistingSystemManaged<DOTSUtils.RenderBridge.DotsRenderTransformPushSystem>());
        }

        [Test]
        public void 默认World_模块组的相对更新顺序与迁移前一致()
        {
            var world = DotsWorldManager.DefaultWorld;
            var simulation = world.GetExistingSystemManaged<SimulationSystemGroup>();

            var inputGroup = world.GetExistingSystemManaged<RenderBridgeInputGroup>();
            var outputGroup = world.GetExistingSystemManaged<RenderBridgeOutputGroup>();
            var inputIndex = IndexOf(simulation.ManagedSystems, inputGroup);
            var outputIndex = IndexOf(simulation.ManagedSystems, outputGroup);

            Assert.GreaterOrEqual(inputIndex, 0, "Input 组应在 Simulation 更新列表里");
            Assert.GreaterOrEqual(outputIndex, 0, "Output 组应在 Simulation 更新列表里");
            Assert.Less(inputIndex, outputIndex, "Input（OrderFirst）应排在 Output（OrderLast）之前");

            // Output 组带 OrderLast，用相对位置断言（绝对「末位」下标会因其它默认 OrderLast 系统而不成立）。
            var lastDefaultIndex = LastIndexWithoutOrderLast(simulation.ManagedSystems, outputGroup);
            Assert.Less(lastDefaultIndex, outputIndex, "Output（OrderLast）应排在所有默认顺序组之后");
        }

        static int IndexOf(System.Collections.Generic.IReadOnlyList<ComponentSystemBase> list, ComponentSystemBase target)
        {
            for (var i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], target)) return i;
            return -1;
        }

        /// <summary>列表中最后一个「非 OrderLast」系统的下标（忽略 <paramref name="except"/> 自身）；没有则 -1。</summary>
        static int LastIndexWithoutOrderLast(
            System.Collections.Generic.IReadOnlyList<ComponentSystemBase> list, ComponentSystemBase except)
        {
            var last = -1;
            for (var i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], except)) continue;
                var attr = (UpdateInGroupAttribute)System.Attribute.GetCustomAttribute(
                    list[i].GetType(), typeof(UpdateInGroupAttribute));
                if (attr == null || !attr.OrderLast) last = i;
            }
            return last;
        }

        // ------------------------------------------------------------------
        //  模块热插拔
        // ------------------------------------------------------------------

        [Test]
        public void 卸载模块_组保留但不再更新_重新装配即恢复()
        {
            DotsWorldRegistry.Reset();
            RegisterTestModule();

            var world = DotsWorldManager.CreateWorld("TestWorld_E", "TestTemplate", makeActive: false);
            try
            {
                var simulation = world.GetExistingSystemManaged<SimulationSystemGroup>();
                var group = world.GetExistingSystemManaged<TestModuleGroup>();
                Assert.IsTrue(simulation.ManagedSystems.Contains(group));

                Assert.IsTrue(DotsWorldManager.RemoveModule(world, "TestModule"));

                Assert.IsFalse(simulation.ManagedSystems.Contains(group), "卸载后组不应在父组更新列表里");
                Assert.IsNotNull(world.GetExistingSystemManaged<TestModuleGroup>(), "卸载只摘除，不销毁实例");
                Assert.IsNotNull(world.GetExistingSystemManaged<TestModuleSystem>(), "组内 System 实例也应保留");
                Assert.IsFalse(DotsWorldManager.HasModule(world, "TestModule"));
                Assert.AreEqual(0, DotsWorldManager.GetModules(world).Count);

                Assert.IsTrue(DotsWorldManager.AddModule(world, "TestModule"));
                Assert.IsTrue(simulation.ManagedSystems.Contains(group), "重新装配后组回到父组更新列表");
                Assert.IsTrue(DotsWorldManager.HasModule(world, "TestModule"));
                Assert.AreEqual(new[] { "TestModule" }, DotsWorldManager.GetModules(world).ToArray());
            }
            finally
            {
                DotsWorldManager.DestroyWorld(world);
            }
        }

        [Test]
        public void 卸载模块_未装配或重复卸载返回false()
        {
            DotsWorldRegistry.Reset();
            RegisterTestModule();

            var world = DotsWorldManager.CreateWorld("TestWorld_F", "TestTemplate", makeActive: false);
            try
            {
                Assert.IsFalse(DotsWorldManager.RemoveModule(world, "NoSuchModule"), "未登记的模块名应返回 false");
                Assert.IsTrue(DotsWorldManager.RemoveModule(world, "TestModule"));
                Assert.IsFalse(DotsWorldManager.RemoveModule(world, "TestModule"), "已卸载，重复卸载应返回 false");
            }
            finally
            {
                DotsWorldManager.DestroyWorld(world);
            }
        }

        [Test]
        public void 销毁模块_组与系统实例被释放_World仍在()
        {
            DotsWorldRegistry.Reset();
            RegisterTestModule();

            var world = DotsWorldManager.CreateWorld("TestWorld_G", "TestTemplate", makeActive: false);
            var name = world.Name;
            try
            {
                Assert.IsTrue(DotsWorldManager.DestroyModule(world, "TestModule"));

                Assert.IsNull(world.GetExistingSystemManaged<TestModuleGroup>(), "组应已销毁");
                Assert.IsNull(world.GetExistingSystemManaged<TestModuleSystem>(), "组内 System 应已销毁");
                Assert.IsFalse(DotsWorldManager.HasModule(world, "TestModule"));

                Assert.IsTrue(DotsWorldManager.TryGetWorld(name, out _), "World 本身仍在");
            }
            finally
            {
                DotsWorldManager.DestroyWorld(world);
            }
        }

        [Test]
        public void 销毁模块_再装配可重建()
        {
            DotsWorldRegistry.Reset();
            RegisterTestModule();

            var world = DotsWorldManager.CreateWorld("TestWorld_H", "TestTemplate", makeActive: false);
            try
            {
                Assert.IsTrue(DotsWorldManager.DestroyModule(world, "TestModule"));
                Assert.IsTrue(DotsWorldManager.AddModule(world, "TestModule"), "销毁后可重新装配（重建实例）");

                var group = world.GetExistingSystemManaged<TestModuleGroup>();
                Assert.IsNotNull(group);
                Assert.IsTrue(world.GetExistingSystemManaged<SimulationSystemGroup>().ManagedSystems.Contains(group));
            }
            finally
            {
                DotsWorldManager.DestroyWorld(world);
            }
        }

        // ------------------------------------------------------------------
        //  唯一活跃渲染 World
        // ------------------------------------------------------------------

        [Test]
        public void 切换活跃World_旧的被冻结_新的在PlayerLoop中()
        {
            DotsWorldRegistry.Reset();
            RegisterTestModule();

            var worldA = DotsWorldManager.CreateWorld("TestWorld_I", "TestTemplate");
            var worldB = DotsWorldManager.CreateWorld("TestWorld_J", "TestTemplate");
            try
            {
                Assert.AreSame(worldB, DotsWorldManager.ActiveWorld, "后创建的 World 应成为活跃 World");
                Assert.IsFalse(ScriptBehaviourUpdateOrder.IsWorldInCurrentPlayerLoop(worldA), "旧的应被冻结");
                Assert.IsTrue(ScriptBehaviourUpdateOrder.IsWorldInCurrentPlayerLoop(worldB), "新的应在 PlayerLoop 中");

                DotsWorldManager.SetActiveWorld(worldA);
                Assert.AreSame(worldA, DotsWorldManager.ActiveWorld);
                Assert.IsTrue(ScriptBehaviourUpdateOrder.IsWorldInCurrentPlayerLoop(worldA));
                Assert.IsFalse(ScriptBehaviourUpdateOrder.IsWorldInCurrentPlayerLoop(worldB));

                DotsWorldManager.SetActiveWorld(worldA);   // 幂等
                Assert.IsTrue(ScriptBehaviourUpdateOrder.IsWorldInCurrentPlayerLoop(worldA));
            }
            finally
            {
                DotsWorldManager.DestroyWorld(worldA);
                DotsWorldManager.DestroyWorld(worldB);
            }
        }

        [Test]
        public void 创建World_makeActive为false时不切换()
        {
            DotsWorldRegistry.Reset();
            RegisterTestModule();

            var world = DotsWorldManager.CreateWorld("TestWorld_K", "TestTemplate", makeActive: false);
            try
            {
                Assert.IsFalse(ScriptBehaviourUpdateOrder.IsWorldInCurrentPlayerLoop(world),
                    "makeActive=false 时不应挂进 PlayerLoop");
            }
            finally
            {
                DotsWorldManager.DestroyWorld(world);
            }
        }

        [Test]
        public void 销毁活跃World_ActiveWorld置空()
        {
            DotsWorldRegistry.Reset();
            RegisterTestModule();

            var world = DotsWorldManager.CreateWorld("TestWorld_L", "TestTemplate");
            Assert.AreSame(world, DotsWorldManager.ActiveWorld);

            DotsWorldManager.DestroyWorld(world);
            Assert.IsNull(DotsWorldManager.ActiveWorld, "活跃 World 被销毁后应为 null");
        }

        // ------------------------------------------------------------------
        //  CreateWorld 异常回收路径
        // ------------------------------------------------------------------

        [Test]
        public void 创建World_装配失败_回收World并恢复此前活跃World()
        {
            DotsWorldRegistry.Reset();
            RegisterTestModule();
            DotsModules.Register(new TestBadModule().ToDescriptor());
            DotsWorldTemplates.Register(new DotsWorldTemplate("TestBadTemplate", typeof(TestBadModule)));

            var good = DotsWorldManager.CreateWorld("TestWorld_Good", "TestTemplate", makeActive: true);
            try
            {
                Assert.AreSame(good, DotsWorldManager.ActiveWorld, "前置条件：good World 应为活跃 World");

                // 装配失败（测试模块组缺 [UpdateInGroup]）时，CreateWorld 应回收半初始化的 World 并抛出。
                Assert.Throws<System.InvalidOperationException>(() =>
                    DotsWorldManager.CreateWorld("TestWorld_Bad", "TestBadTemplate"));

                Assert.IsFalse(DotsWorldManager.TryGetWorld("TestWorld_Bad", out _),
                    "装配失败的 World 不应进注册表");
                Assert.AreSame(good, DotsWorldManager.ActiveWorld, "旧活跃 World 应被恢复为活跃");
                Assert.IsTrue(ScriptBehaviourUpdateOrder.IsWorldInCurrentPlayerLoop(good),
                    "旧活跃 World 应重新挂回 PlayerLoop");
            }
            finally
            {
                DotsWorldManager.DestroyWorld(good);
                DotsWorldManager.DestroyWorld("TestWorld_Bad");
            }
        }
    }
}
