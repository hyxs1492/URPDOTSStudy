using Unity.Mathematics;
using UnityEngine;

namespace DOTSUtils.Pathfinding
{
    /// <summary>
    /// 邻接方式：4 邻（上下左右）或 8 邻（含对角）。
    /// </summary>
    public enum AStarNeighbourhood : byte
    {
        Four = 0,
        Eight = 1,
    }

    /// <summary>
    /// 用哪种算法求解。两者共用同一套「最优优先搜索」内核，差别只在启发式：
    /// A* 的 f = g + h × <see cref="AStarConfig.HeuristicWeight"/>，Dijkstra 的启发式恒为 0。
    /// </summary>
    public enum PathfindingAlgorithm : byte
    {
        /// <summary>启发式 A*。</summary>
        AStar = 0,

        /// <summary>Dijkstra；<see cref="AStarConfig.HeuristicWeight"/> 不参与，恒为最优解。</summary>
        Dijkstra = 1,
    }

    /// <summary>
    /// 一次寻路的结果状态。
    /// </summary>
    public enum AStarStatus : byte
    {
        /// <summary>找到路径。</summary>
        Found = 0,

        /// <summary>没有可用的网格。</summary>
        NoGrid = 1,

        /// <summary>起点不在网格内，或该格不可走。</summary>
        StartInvalid = 2,

        /// <summary>终点不在网格内，或该格不可走。</summary>
        EndInvalid = 3,

        /// <summary>起点可达范围内找不到终点。</summary>
        NoPath = 4,

        /// <summary>访问节点数超过 <see cref="AStarConfig.MaxVisitedNodes"/> 而提前放弃。</summary>
        ExceededLimit = 5,
    }

    /// <summary>
    /// 寻路的网格参数与求解参数。网格是 XZ 平面上的均匀方格，<see cref="Bounds"/> 的 y 分量被忽略
    /// （格子中心沿用 <c>Bounds.Center.y</c>）。
    /// </summary>
    public struct AStarConfig
    {
        /// <summary>网格覆盖范围（只取 xz）。</summary>
        public AABB Bounds;

        /// <summary>格子边长（世界单位）。</summary>
        public float CellSize;

        /// <summary>4 邻或 8 邻。</summary>
        public AStarNeighbourhood Neighbourhood;

        /// <summary>8 邻时是否允许贴角穿过（两侧正交邻居都不可走时走对角）。</summary>
        public bool AllowCornerCutting;

        /// <summary>用 <see cref="PathfindingAlgorithm.AStar"/> 还是 <see cref="PathfindingAlgorithm.Dijkstra"/>。</summary>
        public PathfindingAlgorithm Algorithm;

        /// <summary>启发式权重，&gt;= 1；等于 1 是最优解，越大越快但可能不是最短。只对 <see cref="PathfindingAlgorithm.AStar"/> 有效。</summary>
        public float HeuristicWeight;

        /// <summary>单次寻路最多访问多少格；&lt;= 0 表示用网格总格数。</summary>
        public int MaxVisitedNodes;

        /// <summary>每帧最多解多少个请求；&lt;= 0 表示不限。当帧没轮上的请求留到下一帧继续求解。</summary>
        public int MaxSolvesPerFrame;

        /// <summary>是否对结果做视线拉直（去掉可省的中间点）。</summary>
        public bool SmoothPath;

        /// <summary>默认参数：40×40 世界单位、每格 2 单位、8 邻、不许贴角、A*、权重 1、不限制访问与每帧求解数、不拉直。</summary>
        public static AStarConfig Default => new AStarConfig
        {
            Bounds = new AABB
            {
                Center = float3.zero,
                Extents = new float3(20f, 1f, 20f),
            },
            CellSize = 2f,
            Neighbourhood = AStarNeighbourhood.Eight,
            AllowCornerCutting = false,
            Algorithm = PathfindingAlgorithm.AStar,
            HeuristicWeight = 1f,
            MaxVisitedNodes = 0,
            MaxSolvesPerFrame = 0,
            SmoothPath = false,
        };
    }

    /// <summary>
    /// 寻路的装配参数入口：装载模块（创建 World）之前设置一次，系统检测到版本变化时重建网格。
    /// 掩码用行主序一维数组（<c>index = z * width + x</c>），长度应为 <c>width * height</c>。
    /// </summary>
    public static class AStarSettings
    {
        private static AStarConfig s_Pending = AStarConfig.Default;
        private static byte[] s_PendingMask;

        /// <summary>掩码版本；每次 <see cref="Configure"/> / <see cref="Reset"/> 自增。</summary>
        public static int Version { get; private set; }

        /// <summary>待装配的网格参数。</summary>
        public static AStarConfig Pending => s_Pending;

        /// <summary>待装配的可走掩码；<c>null</c> 表示全部可走。</summary>
        public static byte[] PendingMask => s_PendingMask;

        /// <summary>
        /// 设置网格参数与可走掩码。<paramref name="mask"/> 为 <c>null</c> 时全部可走；
        /// 非 1 的字节都按阻挡处理。
        /// </summary>
        public static void Configure(AStarConfig config, byte[] mask = null)
        {
            s_Pending = config;
            s_PendingMask = mask;
            Version++;
        }

        /// <summary>恢复默认参数（无掩码）。</summary>
        public static void Reset()
        {
            s_Pending = AStarConfig.Default;
            s_PendingMask = null;
            Version++;
        }

        /// <summary>进入 Play 时复位（静态字段会跨 Play 会话保留）。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlayModeEntry() => Reset();
    }
}
