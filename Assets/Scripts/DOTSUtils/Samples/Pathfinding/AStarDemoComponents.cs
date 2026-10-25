using Unity.Entities;
using Unity.Mathematics;

namespace DOTSUtils.Samples.Pathfinding
{
    /// <summary>
    /// 标记「A* 演示的一个 agent」。带这个标记的是渲染树的**父实体**（渲染子实体靠 Parent 跟随，不带它），
    /// 跟路径系统只处理带本标记的实体。
    /// </summary>
    public struct AStarDemoAgentTag : IComponentData
    {
    }

    /// <summary>
    /// 鼠标目标的全局单例：鼠标在地面平面上的落点 + 版本号。主线程每帧写，跟路径 job 只读。
    /// 只有环境里存在这个单例时才会启用鼠标跟随。
    /// </summary>
    public struct AStarDemoCursorTarget : IComponentData
    {
        /// <summary>鼠标落点的世界坐标（y = 演示平面高度）。</summary>
        public float3 Position;

        /// <summary>
        /// 目标版本：落点移动超过一格时加一，agent 用「已规划版本」判断自己是否跟上。
        /// 非 0 即视为**已启用**鼠标跟随（写目标时把版本加一就够了，不必额外置 <see cref="Valid"/>）。
        /// </summary>
        public int Version;

        /// <summary>非 0 表示已经收到过鼠标落点；0 时跟路径系统走原来的随机目标逻辑。</summary>
        public int Valid;

        /// <summary>写入者标记：演示 bootstrap 写 0，外部脚本可写自己的编号，便于判断目标是谁改的。</summary>
        public int Writer;

        /// <summary>版本被写入的次数（每次写目标都加一），用来观察目标有没有被反复覆盖。</summary>
        public int WriteCount;
    }

    /// <summary>
    /// 一次请求挑到的目标类型：跟路径系统把它记在 agent 上，统计单例按它分类汇总。
    /// </summary>
    public enum AStarDemoGoalKind : byte
    {
        /// <summary>还没挑过目标。</summary>
        None = 0,

        /// <summary>鼠标落点周围随机偏移得到的可走格。</summary>
        CursorSpread = 1,

        /// <summary>鼠标所在格（偏移半径为 0 或偏移采样都没落上可走格）。</summary>
        CursorCell = 2,

        /// <summary>鼠标附近最近的可走格（鼠标压在阻挡格或场地外）。</summary>
        NearCursor = 3,

        /// <summary>退回原来的随机目标逻辑。</summary>
        Random = 4,

        /// <summary>挑目标失败（起点无效 / 找不到可走格）。</summary>
        Failed = 5,
    }

    /// <summary>
    /// 沿 A* 路径行走的状态：巡航速度、错峰用的冷却、以及该实体专属的随机流。
    /// 路径数据本身用 A* 模块的 PathRequest / PathStatus / PathWaypoint。
    /// </summary>
    public struct AStarDemoMotion : IComponentData
    {
        /// <summary>巡航速度（世界单位/秒），同时写进 RvoAgent.MaxSpeed。</summary>
        public float Speed;

        /// <summary>
        /// 距离下次可以请求路径的剩余时间，单位：秒。
        /// &gt; 0 表示原地等待：初始错峰与「求解失败 / 挑不到目标」的重试都靠它。
        /// </summary>
        public float Cooldown;

        /// <summary>上次见到的网格建表版本；与当前版本不一致说明网格被重建，旧路径作废。</summary>
        public int GridBuildVersion;

        /// <summary>该实体已经按哪个鼠标目标版本改过道；与单例版本相同表示它已经跟上。</summary>
        public int PlannedCursorVersion;

        /// <summary>该实体专属的随机流：初始延迟、目标格、鼠标偏移、重试间隔都从它取。</summary>
        public Random Rng;

        /// <summary>
        /// 该实体的错峰相位，取值 <c>[0, CursorRepathStride)</c>：帧号加相位能被步长整除的那一帧
        /// 才轮到它响应新的鼠标版本，于是整群 agent 会被摊到步长帧里。
        /// </summary>
        public int RepathPhase;

        /// <summary>最近一次成功发出请求时的帧号（跟路径系统的帧计数器口径）。</summary>
        public int LastRepathFrame;

        /// <summary>最近一次成功挑到的目标类型。</summary>
        public AStarDemoGoalKind GoalKind;

        /// <summary>累计成功发出请求的次数。</summary>
        public int RepathCount;

        /// <summary>已经连续等待求解结果的帧数。</summary>
        public int PendingFrames;

        /// <summary>累计「等待求解超时、被强制重发请求」的次数。</summary>
        public int PendingTimeouts;

        /// <summary>路点游标所属的已求解版本；与 <c>PathStatus.SolvedVersion</c> 不同表示换了一条新路径。</summary>
        public int PathCursorVersion;

        /// <summary>
        /// 当前要去的路点下标。**只前进、不后退**；换到新路径时重置为 1
        /// （跳过 <c>waypoints[0]</c> —— 那是本船申请路径时所在的位置，指向它就会掉头往回走）。
        /// </summary>
        public int WaypointIndex;
    }

    /// <summary>
    /// 跟路径系统的逐帧统计快照（单例；系统每帧写，外部只读）。挂在 World 里名为
    /// <c>AStarDemoStats</c> 的实体上，可用 <see cref="AStarDemoStatsAccess"/> 取。
    /// </summary>
    public struct AStarDemoStats : IComponentData
    {
        /// <summary>统计覆盖的 agent 数。</summary>
        public int AgentCount;

        /// <summary>求解成功且有路点可跟的 agent 数。</summary>
        public int PathAgents;

        /// <summary>已经看到新鼠标版本、但这一帧还没改道的 agent 数。</summary>
        public int StaleAgents;

        /// <summary>请求已发出、求解结果还没回来的 agent 数。</summary>
        public int PendingAgents;

        /// <summary>本帧真的发出新请求的 agent 数。</summary>
        public int RepathedLastFrame;

        /// <summary>最近一次目标是「鼠标落点附近」的 agent 数。</summary>
        public int CursorGoalAgents;

        /// <summary>最近一次目标是「退回随机挑格」的 agent 数。</summary>
        public int RandomGoalAgents;

        /// <summary>最近一次挑目标失败的 agent 数。</summary>
        public int NoGoalAgents;

        /// <summary>当前的错峰步长（帧）。</summary>
        public int RepathWindow;

        /// <summary>单个 agent 连续等待求解的最长帧数。</summary>
        public int MaxPendingFrames;

        /// <summary>全体 agent 累计的求解超时次数。</summary>
        public int PendingTimeouts;

        /// <summary>统计时的帧号。</summary>
        public int FrameIndex;

        /// <summary>统计时鼠标目标单例里的版本号。</summary>
        public int CursorVersion;

        /// <summary>统计时鼠标目标单例里的可用标记。</summary>
        public int CursorValid;

        /// <summary>统计时鼠标目标单例里的写入者标记。</summary>
        public int CursorWriter;

        /// <summary>鼠标目标单例累计被写入的次数。</summary>
        public int CursorWriteCount;
    }

    /// <summary>
    /// 统计单例的读取入口（编辑器脚本 / 调试面板用）。
    /// </summary>
    public static class AStarDemoStatsAccess
    {
        /// <summary>取统计单例；World 未创建或还没统计过时返回 <c>false</c>。</summary>
        public static bool TryGet(World world, out AStarDemoStats stats)
        {
            stats = default;
            if (world == null || !world.IsCreated)
            {
                return false;
            }

            using (var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<AStarDemoStats>()))
            {
                if (query.IsEmptyIgnoreFilter)
                {
                    return false;
                }
                stats = world.EntityManager.GetComponentData<AStarDemoStats>(query.GetSingletonEntity());
                return true;
            }
        }
    }

    /// <summary>
    /// 标记「把当前路径用 Debug.DrawLine 画出来」的 agent。只给前几个 agent 挂，
    /// 这样一万个 agent 也不会各画一条线。
    /// </summary>
    public struct AStarDemoDebugPath : IComponentData
    {
        /// <summary>序号，只用来给调试线上色。</summary>
        public int Order;
    }
}
