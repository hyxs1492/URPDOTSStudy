using System.Collections.Generic;
using DOTSUtils.Worlds;
using DOTSUtils.Spatial;
using DOTSUtils.Rvo;
using Unity.Entities;

namespace DOTSUtils.Benchmark
{
    /// <summary>
    /// Benchmark 示例专属的模块：把移动系统装进一个专门的组。
    ///
    /// 定义在 <c>Samples</c> 里，<see cref="Templates"/> 保持默认的空 —— 不自动加入任何 World 模板，
    /// 只能由 <c>BenchmarkSuite</c> 的模板显式列出，或由外部用
    /// <c>DotsWorldManager.AddModule(world, "Benchmark")</c> 装配。
    /// </summary>
    public sealed class DotsBenchmarkModule : DotsModule
    {
        public override string Name => "Benchmark";

        /// <summary>
        /// 模块装配顺序用的键：RenderBridge(0) → Benchmark(100) → Spatial(200) → Rvo(250)。
        ///
        /// 它只决定「谁先被装进 World」；运行时执行顺序由各组上的
        /// <c>[UpdateAfter]</c> / <c>[UpdateBefore]</c> 决定。
        /// </summary>
        public override int SortKey => 100;

        public override IReadOnlyList<DotsModuleGroupDescriptor> Groups => new[]
        {
            new DotsModuleGroupDescriptor(typeof(BenchmarkGroup), typeof(BenchmarkRandomMoveSystem)),
        };
    }

    /// <summary>Benchmark 的模块组：承载随机移动系统。</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    // 顺序：Spatial（建索引）→ Rvo（求解速度）→ 本组（写期望速度、并把 Rvo 的速度落到位置上）。
    [UpdateAfter(typeof(SpatialGroup))]
    [UpdateAfter(typeof(RvoGroup))]
    public partial class BenchmarkGroup : DotsModuleGroup { }
}
