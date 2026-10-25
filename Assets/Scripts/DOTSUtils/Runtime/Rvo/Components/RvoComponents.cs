using Unity.Entities;
using Unity.Mathematics;

namespace DOTSUtils.Rvo
{
    /// <summary>
    /// 本帧邻居索引用的是哪一棵树（诊断用；系统写进 <see cref="RvoStatus"/>）。
    ///
    /// 邻居索引由 <c>Spatial</c> 模块提供，没装 Spatial / 树还没建时本帧就<b>没有邻居</b>（不避障）。
    /// </summary>
    public enum RvoNeighborIndex
    {
        /// <summary>
        /// 没有可用索引（没装 Spatial 模块，或它的树还没建）—— 本帧邻居全空。
        /// </summary>
        None = 0,

        /// <summary>
        /// 用 Spatial 模块的**四叉树**（xz 平面；RVO 的默认选择）。
        /// </summary>
        Quadtree = 1,

        /// <summary>
        /// 用 Spatial 模块的**八叉树**（xyz；没有四叉树时用它）。
        /// </summary>
        Octree = 2,

        /// <summary>
        /// <see cref="RvoConfig.UseSpatialIndex"/> 被关掉了 —— 本帧邻居全空。
        /// </summary>
        Disabled = 3,
    }

    /// <summary>
    /// 系统的运行状态（<b>系统每帧写、用户只读</b>）。用
    /// <c>RvoAccess.TryGetStatus(world, out var status)</c> 读它：
    /// 系统是 <c>ISystem</c>（没有托管实例），所以状态落在组件上而不是属性上。
    /// </summary>
    public struct RvoStatus : IComponentData
    {
        /// <summary>
        /// 本帧邻居索引来自哪棵树（<see cref="RvoNeighborIndex.None"/> = 本帧没有邻居）。
        /// </summary>
        public RvoNeighborIndex Index;

        /// <summary>
        /// 本帧参与求解的 agent 数。
        /// </summary>
        public int AgentCount;

        /// <summary>
        /// 本帧用的步长（诊断用：<c>&lt;= 0</c> 时系统直接跳过，什么都不会发生）。
        /// </summary>
        public float LastDt;

        /// <summary>
        /// 本帧配置的静态障碍物**个数**（多边形；旧线段模型下每条线段算一个二顶点多边形）。
        /// <c>UseObstacles</c> 关闭时为 0。不是每个 agent 各自范围内的个数。
        /// </summary>
        public int ObstacleCount;

        /// <summary>
        /// 本帧配置的静态障碍物**顶点**数（扁平顶点数组的长度）—— 求解逐顶点构造约束，候选范围就是这些顶点。
        /// <c>UseObstacles</c> 关闭时为 0。
        /// </summary>
        public int ObstacleVertexCount;

    }

    /// <summary>
    /// 挂在「带 <c>LocalTransform</c>」的实体上，表示它参与 ORCA 避障。
    ///
    /// 只存 <b>xz 平面</b>的速度：<c>LocalTransform.Position.y</c> 全程不动。
    /// </summary>
    public struct RvoAgent : IComponentData
    {
        /// <summary>
        /// 碰撞半径（&gt; 0）。
        /// </summary>
        public float Radius;

        /// <summary>
        /// 最大速度（&gt; 0）。
        /// </summary>
        public float MaxSpeed;

        /// <summary>
        /// 期望速度（xz）—— 由调用方/目标系统写。
        /// </summary>
        public float2 PrefVelocity;

        /// <summary>
        /// 当前速度（xz）。本模块是它的权威写入方。
        /// </summary>
        public float2 Velocity;

        /// <summary>
        /// 求解输出（系统内部字段，<b>用户不要写</b>）。
        /// </summary>
        public float2 NewVelocity;

        // 本帧要不要跳过求解，由 Visibility 模块的 VisibilityCulledTag 决定（见 RvoSolveSystem）。
    }

    /// <summary>
    /// RVO 的参数。**不是组件** —— 它是**全局配置**（<c>RvoSettings.Config</c>），所有 World 共用一份；
    /// 运行状态见 <see cref="RvoStatus"/>。
    /// </summary>
    public struct RvoConfig
    {
        /// <summary>
        /// 时间视野 τ（&gt; 0）。越大越早开始避让。
        /// </summary>
        public float TimeHorizon;

        /// <summary>
        /// 邻居判定距离（RVO2 语义：<c>distSq &lt;= neighborDist²</c>）。
        /// </summary>
        public float NeighborDist;

        /// <summary>
        /// 每个 agent 最多参与求解的邻居数（按距离取前 N）。
        /// </summary>
        public int MaxNeighbors;

        /// <summary>
        /// 是否由本模块积分位置（关掉 = 只算速度，位置交给别的系统）。
        /// </summary>
        public bool IntegratePosition;

        /// <summary>
        /// 是否用 <c>Spatial</c> 模块的索引树查邻居。关掉 = 不查邻居（本帧无避障）。
        /// </summary>
        public bool UseSpatialIndex;

        /// <summary>
        /// 是否把**静态障碍物**并入 ORCA 求解。关掉时求解路径与不含障碍物时完全一致（也不读取障碍物缓冲）。
        /// </summary>
        public bool UseObstacles;

        /// <summary>
        /// 静态障碍物的时间视野 τ_obst（&gt; 0）；同时决定候选范围 <c>τ_obst·maxSpeed + radius</c>
        /// （顶点到线段 <c>[Point, Next.Point]</c> 的距离超过它就不产生约束）。
        /// </summary>
        public float TimeHorizonObst;

        /// <summary>
        /// 固定步长（&gt; 0 时用它代替 <c>Time.DeltaTime</c>）。不推进 Unity <c>Time</c> 的调用方
        /// （例如手动 <c>world.Update()</c>）必须靠它，否则帧长恒为 0。
        /// </summary>
        public float FixedTimeStep;

        public static RvoConfig Default => new RvoConfig
        {
            TimeHorizon = 2f,
            NeighborDist = 15f,
            MaxNeighbors = 10,
            // 默认**不**抢位置：谁写 LocalTransform 是使用方的决定。
            IntegratePosition = false,
            UseSpatialIndex = true,
            // 默认**不**避让静态障碍物：关掉时行为与不含该功能时一致。
            UseObstacles = false,
            TimeHorizonObst = 2f,
            FixedTimeStep = 0f,
        };
    }

    /// <summary>
    /// 邻居的**求解输入**（系统内部用；长度就是有效邻居数）。
    /// 内容就是内核的 <see cref="AgentState"/>（32 字节），求解时
    /// <c>Reinterpret&lt;AgentState&gt;()</c> 零拷贝喂给 <see cref="OrcaSolver"/> —— job 里不需要临时分配。
    /// </summary>
    public struct RvoNeighbor : IBufferElementData
    {
        public AgentState State;
    }

    /// <summary>
    /// 静态障碍物的一个**顶点**（xz 平面）。线段由 <see cref="Next"/> 隐含给出：<c>[Point, Next.Point]</c>；
    /// <see cref="Prev"/>/<see cref="Next"/> 是同一扁平顶点数组里的下标，多边形内首尾相接
    /// （顶点数为 2 时两者都指向另一个顶点）。
    ///
    /// 写入走 <c>RvoAccess.SetObstacles</c>：调用方只给顶点位置 + 多边形分段，
    /// <see cref="UnitDir"/> / <see cref="Prev"/> / <see cref="Next"/> / <see cref="IsConvex"/>
    /// 由 <see cref="RvoObstacleBuilder"/> 按 RVO2 <c>RVOSimulator::addObstacle</c> 的语义补全。
    /// 28 字节。
    /// </summary>
    public struct RvoObstacleVertex : IBufferElementData
    {
        /// <summary>
        /// 顶点位置。
        /// </summary>
        public float2 Point;

        /// <summary>
        /// <c>normalize(Next.Point - Point)</c>；零长边取 <c>float2.zero</c>。
        /// </summary>
        public float2 UnitDir;

        /// <summary>
        /// 多边形内的前一个顶点（同一顶点数组里的下标）。
        /// </summary>
        public int Prev;

        /// <summary>
        /// 多边形内的后一个顶点（同一顶点数组里的下标）。
        /// </summary>
        public int Next;

        /// <summary>
        /// <c>1</c> = 凸顶点；<c>0</c> = 凹顶点。顶点数为 2 的多边形两个顶点都是 <c>1</c>。
        /// </summary>
        public byte IsConvex;
    }

    /// <summary>
    /// 一个多边形障碍物在扁平顶点数组里的分段：<c>[<see cref="Start"/>, <see cref="Start"/> + <see cref="Count"/>)</c>。
    /// 8 字节。
    /// </summary>
    public struct RvoObstaclePolygon : IBufferElementData
    {
        /// <summary>
        /// 该多边形第一个顶点在顶点数组里的下标。
        /// </summary>
        public int Start;

        /// <summary>
        /// 顶点个数（&gt;= 2；为 2 时是零厚度墙）。
        /// </summary>
        public int Count;
    }

    /// <summary>
    /// 一条静态障碍物线段（xz 平面）—— **旧线段模型**的输入。<b>内侧（障碍物一侧）是 A→B 的左侧</b>：
    /// <c>det(B - A, P - A) &gt; 0</c> 的点视为障碍物内部，方向由调用方按这个约定摆放。
    ///
    /// <c>RvoAccess.SetObstacles</c> 收下它时，每条线段按一个**二顶点多边形**并入新模型
    /// （两条边 <c>A→B</c> 与 <c>B→A</c>，两侧都挡）。16 字节。
    /// </summary>
    public struct RvoObstacleSegment : IBufferElementData
    {
        /// <summary>
        /// 起点。
        /// </summary>
        public float2 A;

        /// <summary>
        /// 终点。
        /// </summary>
        public float2 B;
    }

    /// <summary>
    /// 静态障碍物集合的**单例标记**：顶点本体存在同一个实体的
    /// <c>DynamicBuffer&lt;RvoObstacleVertex&gt;</c> 上、多边形分段存在
    /// <c>DynamicBuffer&lt;RvoObstaclePolygon&gt;</c> 上。写入走 <c>RvoAccess.SetObstacles</c> /
    /// <c>ClearObstacles</c>，读取走 <c>RvoAccess.TryGetObstacleCount</c> /
    /// <c>RvoAccess.TryGetObstacleVertexCount</c>。<br/>
    /// 实体由 <c>RvoSolveSystem</c> 在 <c>OnCreate</c> 建、<c>OnDestroy</c> 销毁。
    /// </summary>
    public struct RvoObstacleSingleton : IComponentData
    {
        /// <summary>
        /// 每次写入（含清空）自增，用于判断障碍物是否换过。
        /// </summary>
        public int Version;
    }
}
