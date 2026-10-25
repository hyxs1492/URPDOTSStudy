using System.Collections.Generic;
using NUnit.Framework;
using Unity.Entities;

namespace DOTSUtils.Worlds.Tests
{
    // ---- 被扫描发现的测试模块（顶层类型）----

    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class DiscoveredModuleGroup : DotsModuleGroup { }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(DiscoveredModuleGroup))]
    public partial class DiscoveredModuleSystem : SystemBase
    {
        protected override void OnUpdate() { }
    }

    /// <summary>只实现 DotsModule —— 没有任何显式注册代码，用来验证「自动发现」。</summary>
    public sealed class DiscoveredTestModule : DotsModule
    {
        public override string Name => "DiscoveredTestModule";
        public override int SortKey => 42;

        public override IReadOnlyList<DotsModuleGroupDescriptor> Groups => new[]
        {
            new DotsModuleGroupDescriptor(typeof(DiscoveredModuleGroup), typeof(DiscoveredModuleSystem)),
        };
    }

    /// <summary>自报要加入某个模板的模块（Groups 为空，只验证模板追加）。</summary>
    public sealed class SelfDeclaredTemplateModule : DotsModule
    {
        public override string Name => "SelfDeclaredTemplateModule";
        public override int SortKey => 7;

        public override IReadOnlyList<string> Templates => new[] { "TestDiscoveryTemplate" };

        public override IReadOnlyList<DotsModuleGroupDescriptor> Groups =>
            System.Array.Empty<DotsModuleGroupDescriptor>();
    }

    // ---- 顺序用：名字字母序与 SortKey 刻意相反的模块 ----

    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class OrderTestGroupA : DotsModuleGroup { }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class OrderTestGroupB : DotsModuleGroup { }

    /// <summary>名字排序在后（Zzz）、SortKey 在前（100）。</summary>
    public sealed class ZzzOrderTestModule : DotsModule
    {
        public override string Name => "ZzzOrderTestModule";
        public override int SortKey => 100;

        public override IReadOnlyList<string> Templates => new[] { "TestOrderTemplate" };

        public override IReadOnlyList<DotsModuleGroupDescriptor> Groups => new[]
        {
            new DotsModuleGroupDescriptor(typeof(OrderTestGroupA)),
        };
    }

    /// <summary>名字排序在前（Aaa）、SortKey 在后（200）。</summary>
    public sealed class AaaOrderTestModule : DotsModule
    {
        public override string Name => "AaaOrderTestModule";
        public override int SortKey => 200;

        public override IReadOnlyList<string> Templates => new[] { "TestOrderTemplate" };

        public override IReadOnlyList<DotsModuleGroupDescriptor> Groups => new[]
        {
            new DotsModuleGroupDescriptor(typeof(OrderTestGroupB)),
        };
    }

    /// <summary>模块的自动发现、自报模板与 SortKey 装配顺序。</summary>
    public class DotsModuleDiscoveryTests
    {
        [TearDown]
        public void TearDown()
        {
            var defaultWorld = World.DefaultGameObjectInjectionWorld;
            if (defaultWorld != null && defaultWorld.IsCreated)
                DotsWorldManager.SetActiveWorld(defaultWorld);

            DotsModules.Clear();
            DotsWorldTemplates.Clear();
            DotsWorldRegistry.Reset();
        }

        [Test]
        public void 自动发现_实现DotsModule的类型无需显式注册即进入注册表()
        {
            DotsWorldRegistry.Reset();
            DotsWorldRegistry.EnsureInitialized();

            Assert.IsTrue(DotsModules.TryGet("DiscoveredTestModule", out var discovered),
                "实现 DotsModule 的类型应被自动发现并登记（无需任何注册代码）");
            Assert.AreEqual(42, discovered.SortKey);
            Assert.AreEqual(1, discovered.Groups.Count);
            Assert.AreEqual(typeof(DiscoveredModuleGroup), discovered.Groups[0].GroupType);
        }

        [Test]
        public void 自动发现_模块自报的Templates会被追加到对应模板()
        {
            DotsWorldTemplates.Register(new DotsWorldTemplate("TestDiscoveryTemplate"));

            DotsWorldRegistry.Reset();
            DotsWorldRegistry.EnsureInitialized();

            Assert.IsTrue(DotsWorldTemplates.TryGet("TestDiscoveryTemplate", out var template));
            CollectionAssert.Contains(template.Modules, typeof(SelfDeclaredTemplateModule),
                "模块自报的 Templates 应被追加进该模板");
        }

        [Test]
        public void 自动发现_实现DotsModule的类型也会被ReadOnly注册表清空后重新发现()
        {
            DotsWorldRegistry.Reset();
            DotsWorldRegistry.EnsureInitialized();
            Assert.IsTrue(DotsModules.TryGet("DiscoveredTestModule", out _));

            // 模拟测试隔离：清空 + Reset 后，扫描必须能重新把它找回来。
            DotsModules.Clear();
            DotsWorldRegistry.Reset();
            DotsWorldRegistry.EnsureInitialized();

            Assert.IsTrue(DotsModules.TryGet("DiscoveredTestModule", out _),
                "Reset 后重新扫描应恢复被发现的模块");
        }

        [Test]
        public void 装配顺序_按SortKey升序而非模块名字母序()
        {
            DotsWorldTemplates.Register(new DotsWorldTemplate("TestOrderTemplate"));

            DotsWorldRegistry.Reset();
            DotsWorldRegistry.EnsureInitialized();

            var world = DotsWorldManager.CreateWorld("TestWorld_Order", "TestOrderTemplate", makeActive: false);
            try
            {
                var simulation = world.GetExistingSystemManaged<SimulationSystemGroup>();
                var groupA = world.GetExistingSystemManaged<OrderTestGroupA>();   // SortKey 100，名字 Zzz
                var groupB = world.GetExistingSystemManaged<OrderTestGroupB>();   // SortKey 200，名字 Aaa

                Assert.IsNotNull(groupA, "SortKey 100 的模块应被装配");
                Assert.IsNotNull(groupB, "SortKey 200 的模块应被装配");

                var indexA = IndexOf(simulation.ManagedSystems, groupA);
                var indexB = IndexOf(simulation.ManagedSystems, groupB);
                Assert.GreaterOrEqual(indexA, 0);
                Assert.GreaterOrEqual(indexB, 0);
                Assert.Less(indexA, indexB,
                    "SortKey 小（100）的 Zzz 模块必须先于 SortKey 大（200）的 Aaa 模块 —— 证明按 SortKey 而非名字/扫描顺序");
            }
            finally
            {
                DotsWorldManager.DestroyWorld(world);
            }
        }

        static int IndexOf(IReadOnlyList<ComponentSystemBase> list, ComponentSystemBase target)
        {
            for (var i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], target)) return i;
            return -1;
        }
    }
}
