using Unity.Entities;
using Unity.Mathematics;

namespace DOTSUtils.Pathfinding
{
    /// <summary>
    /// 网格单例：模块装配时按 <see cref="AStarSettings"/> 建好，job 只读。
    /// </summary>
    public struct AStarSingleton : IComponentData
    {
        /// <summary>当前网格。</summary>
        public AStarGrid Grid;

        /// <summary>当前参数。</summary>
        public AStarConfig Config;

        /// <summary>本 World 实际使用的算法（装配时由 <see cref="AStarConfig.Algorithm"/> 快照而来）。</summary>
        public PathfindingAlgorithm Algorithm;

        /// <summary>建这份网格时用的 <see cref="AStarSettings.Version"/>。</summary>
        public int BuildVersion;
    }

    /// <summary>
    /// 寻路请求。改 <see cref="Start"/> / <see cref="End"/> 后把 <see cref="Version"/> 加一，
    /// 系统在 <c>PathStatus.SolvedVersion</c> 落后于它时才重算。
    /// </summary>
    public struct PathRequest : IComponentData
    {
        /// <summary>起点（世界坐标）。</summary>
        public float3 Start;

        /// <summary>终点（世界坐标）。</summary>
        public float3 End;

        /// <summary>请求版本；加一即要求重算。</summary>
        public int Version;
    }

    /// <summary>
    /// 寻路结果状态，与 <see cref="PathRequest"/> 同实体。
    /// </summary>
    public struct PathStatus : IComponentData
    {
        /// <summary>上次求解的结果。</summary>
        public AStarStatus Status;

        /// <summary>路径点数。</summary>
        public int WaypointCount;

        /// <summary>已求解到的请求版本。</summary>
        public int SolvedVersion;
    }

    /// <summary>路径上的一个点（世界坐标）。</summary>
    [InternalBufferCapacity(8)]
    public struct PathWaypoint : IBufferElementData
    {
        /// <summary>世界坐标。</summary>
        public float3 Position;
    }
}
