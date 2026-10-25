using DOTSUtils.Spatial;
using UnityEngine;

namespace DOTSUtils.Rvo
{
    /// <summary>
    /// RVO/ORCA 的**全局配置**（进程级，所有 World 共用一份）。
    ///
    /// <code>
    /// var config = RvoSettings.Config;
    /// config.NeighborDist = 8f;
    /// RvoSettings.Set(config);
    /// </code>
    /// </summary>
    public static class RvoSettings
    {
        private static RvoConfig s_Config = RvoConfig.Default;

        /// <summary>
        /// 配置版本：每次 <see cref="Set"/> / <see cref="Reset"/> 自增。
        /// </summary>
        public static int Version { get; private set; }

        /// <summary>
        /// 当前配置（默认 <see cref="RvoConfig.Default"/>）。
        /// </summary>
        public static RvoConfig Config => s_Config;

        public static void Set(in RvoConfig config)
        {
            s_Config = config;
            Version++;
        }

        /// <summary>
        /// 设 RVO 参数，并<b>顺手带上它依赖的 Spatial 模块</b>的索引参数。
        /// 两个 Spatial 参数都不传时只设 RVO，Spatial 保持现状。
        /// </summary>
        public static void Configure(in RvoConfig config,
                                     SpatialIndexKind? spatialIndex = null,
                                     SpatialTreeConfig? spatialTree = null)
        {
            if (spatialIndex.HasValue || spatialTree.HasValue)
            {
                SpatialSettings.Configure(
                    spatialIndex ?? SpatialSettings.Pending.Index,
                    spatialTree ?? SpatialSettings.Pending.Tree);
            }
            Set(config);
        }

        /// <summary>
        /// 恢复默认配置。
        /// </summary>
        public static void Reset()
        {
            s_Config = RvoConfig.Default;
            Version++;
        }

        /// <summary>
        /// 进入 Play 时复位。本工程关闭了域重载（<c>m_EnterPlayModeOptions = 3</c>），静态字段跨会话保留。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlayModeEntry() => Reset();
    }
}
