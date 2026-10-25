using System.Reflection;
using Unity.Entities;
using Unity.Rendering;
using UnityEngine;

namespace DOTSUtils.Benchmark
{

    /// <summary>
    /// 指标采集：批次数等来自 <c>EntitiesGraphicsSystem.m_Stats</c>（internal 字段，反射读；取不到就把
    /// <see cref="BenchmarkSnapshot.StatsAvailable"/> 置 false）。
    /// 帧时间 / FPS 由 <c>Time.unscaledDeltaTime</c> 指数平滑得到。
    /// </summary>
    public static class BenchmarkMetrics
    {
        private static readonly FieldInfo s_StatsField =
            typeof(EntitiesGraphicsSystem).GetField("m_Stats", BindingFlags.NonPublic | BindingFlags.Instance);

        private static float s_SmoothedFrameMs;

        /// <summary>读一帧指标；<paramref name="world"/> 是要观察的 World（示例用的是自建 World，不能写死默认 World）。</summary>
        public static BenchmarkSnapshot Read(World world)
        {
            var frameMs = Smooth(Time.unscaledDeltaTime * 1000f);
            var fps = frameMs > 0.0001f ? 1000f / frameMs : 0f;

            var system = ResolveSystem(world);
            if (system == null || s_StatsField == null)
                return new BenchmarkSnapshot(0, 0, 0, 0, 0, frameMs, fps, false);

            var stats = (EntitiesGraphicsStats)s_StatsField.GetValue(system);
            if (!LooksValid(stats))
                return new BenchmarkSnapshot(0, 0, 0, 0, 0, frameMs, fps, false);

            return new BenchmarkSnapshot(
                stats.BatchCount, stats.DrawCommandCount, stats.RenderedInstanceCount,
                stats.ChunkCountAnyLod, stats.LodChanged, frameMs, fps, true);
        }

        /// <summary>明显非法的统计值（负数 / 过大）一律当作「不可用」。</summary>
        private static bool LooksValid(EntitiesGraphicsStats stats) =>
            stats.BatchCount >= 0 && stats.BatchCount <= 10000000 &&
            stats.DrawCommandCount >= 0 && stats.DrawCommandCount <= 10000000 &&
            stats.RenderedInstanceCount >= 0 && stats.RenderedInstanceCount <= 10000000 &&
            stats.ChunkCountAnyLod >= 0;

        private static float Smooth(float value)
        {
            s_SmoothedFrameMs = s_SmoothedFrameMs <= 0f ? value : Mathf.Lerp(s_SmoothedFrameMs, value, 0.1f);
            return s_SmoothedFrameMs;
        }

        private static EntitiesGraphicsSystem ResolveSystem(World world)
        {
            if (world == null || !world.IsCreated)
            {
                return null;
            }
            return world.GetExistingSystemManaged<EntitiesGraphicsSystem>();
        }
    }

    /// <summary>一帧的 benchmark 指标快照。</summary>
    public readonly struct BenchmarkSnapshot
    {
        public readonly int BatchCount;
        public readonly int DrawCommandCount;
        public readonly int RenderedInstanceCount;
        public readonly int ChunkCountAnyLod;
        public readonly int LodChanged;
        public readonly float FrameMilliseconds;
        public readonly float Fps;
        public readonly bool StatsAvailable;

        public BenchmarkSnapshot(int batchCount, int drawCommandCount, int renderedInstanceCount, int chunkCountAnyLod,
            int lodChanged, float frameMilliseconds, float fps, bool statsAvailable)
        {
            BatchCount = batchCount;
            DrawCommandCount = drawCommandCount;
            RenderedInstanceCount = renderedInstanceCount;
            ChunkCountAnyLod = chunkCountAnyLod;
            LodChanged = lodChanged;
            FrameMilliseconds = frameMilliseconds;
            Fps = fps;
            StatsAvailable = statsAvailable;
        }
    }

}
