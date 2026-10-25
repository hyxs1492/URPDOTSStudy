using DOTSUtils.Rvo;
using DOTSUtils.Spatial;
using DOTSUtils.Visibility;
using Unity.Entities;
using Unity.Mathematics;

namespace DOTSUtils.Benchmark
{
    /// <summary>
    /// 标记「这是 benchmark 的一艘船（组实体 / 走桥路径的父实体）」。移动系统只驱动带本标记的实体；
    /// 层实体也带本标记，但没有 <see cref="BenchmarkMotion"/>，不会被驱动。
    /// </summary>
    public struct BenchmarkShipTag : IComponentData
    {
    }

    /// <summary>
    /// 游走状态：实体朝自身 <see cref="LocalTransform"/> 的 forward 方向前进，
    /// 每隔一段随机时间换一个新的目标朝向，再以固定角速度平滑转过去。
    /// </summary>
    public struct BenchmarkMotion : IComponentData
    {
        /// <summary>当前的目标朝向（世界空间），由移动 job 逐帧逼近。</summary>
        public quaternion TargetRotation;

        /// <summary>距离下次换向的剩余时间，单位：秒。</summary>
        public float TimeUntilRetarget;

        /// <summary>该实体专属的随机流：换向间隔与目标朝向都从它取。刻意不命名为 <c>Random</c>，避免与类型名同名。</summary>
        public Random Rng;
    }

    /// <summary>给一艘船挂上 benchmark 组件；三条路径（纯 ECS 生成 / 走桥后补标 / authoring 转换）共用这一份定义。</summary>
    public static class BenchmarkShipComponents
    {
        /// <summary>船的 RVO 碰撞半径（&gt; 0），由 <see cref="BenchmarkSuite"/> 每帧写入；所有船共用同一个值（是演示用的全局参数，不是逐艘属性）。</summary>
        public static float ShipRadius = 1f;

        /// <summary>
        /// 抽一份 <see cref="RvoAgent"/>：半径取 <see cref="ShipRadius"/>，最大速度取巡航速度。
        /// <see cref="RvoAgent.PrefVelocity"/>（期望速度）留空，由 <see cref="BenchmarkRandomMoveSystem"/> 每帧写入。
        /// </summary>
        public static RvoAgent CreateAgent(float maxSpeed) => new RvoAgent
        {
            Radius = ShipRadius,
            MaxSpeed = maxSpeed,
        };

        /// <summary>
        /// 抽一份初始游走状态：随机朝向 + 随机首次换向时刻。
        /// 朝向只绕 Y 轴（所有船都在同一个 xz 平面上活动）。
        /// </summary>
        public static BenchmarkMotion CreateMotion(ref Random rng) => new BenchmarkMotion
        {
            TargetRotation = RandomHeadingAroundY(ref rng),
            TimeUntilRetarget = rng.NextFloat(0f, 1f),
            Rng = new Random(rng.NextUInt(1u, uint.MaxValue)),
        };

        /// <summary>抽一个只在水平面内的朝向（只绕 Y 轴旋转，因此 forward 的 y 分量为 0）。</summary>
        public static quaternion RandomHeadingAroundY(ref Random rng)
            => quaternion.RotateY(rng.NextFloat(0f, 2f * math.PI));

        /// <summary><see cref="CreateMotion(ref Random)"/> 的定种子版本，供没有现成随机流的调用方使用（如 authoring 路径）。</summary>
        public static BenchmarkMotion CreateMotion(uint seed)
        {
            var rng = new Random(seed == 0u ? 1u : seed);
            return CreateMotion(ref rng);
        }

        /// <summary>
        /// 给实体挂上「船标记 + 游走状态 + 空间索引标记 + RVO 参与标记」（<see cref="EntityManager"/> 路径用）；
        /// authoring 路径（<see cref="ShipController"/>）取同样的一份状态，由 authoring API 写入。
        /// </summary>
        public static void Attach(EntityManager em, Entity entity, ref Random rng, float maxSpeed)
        {
            em.AddComponentData(entity, default(BenchmarkShipTag));
            em.AddComponentData(entity, CreateMotion(ref rng));
            em.AddComponentData(entity, default(SpatialIndexed));
            // RvoAgent 必须挂上，否则进不了 RvoSolveSystem 的查询（WithAllRW<RvoAgent>）。
            em.AddComponentData(entity, CreateAgent(maxSpeed));
            // 挂上可见性标记：Visibility 模块才会管这艘船，视口外时把它标成被裁剪。
            em.AddComponentData(entity, default(VisibilityCulledTag));
        }
    }
}
