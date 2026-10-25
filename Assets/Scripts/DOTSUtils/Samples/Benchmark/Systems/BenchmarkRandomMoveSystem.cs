using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using DOTSUtils.Rvo;
using Unity.Transforms;
using DOTSUtils.Worlds;

namespace DOTSUtils.Benchmark
{
    /// <summary>
    /// 让每艘船朝自身 forward 巡航：每隔一段随机时间换一个新的目标朝向，再以固定角速度平滑转过去
    /// （状态见 <see cref="BenchmarkMotion"/>）。
    ///
    /// 本系统是本示例唯一写 <see cref="LocalTransform"/> 的地方：朝向自己逐帧插值，
    /// 期望速度写进 <see cref="RvoAgent.PrefVelocity"/>，位置读 Rvo 解算出的 <see cref="RvoAgent.Velocity"/> 再积分；
    /// 出界时把期望速度与目标朝向都改成指向原点，并把位置夹在 <see cref="Bounds"/> 内。
    ///
    /// 只驱动「同时带 <see cref="BenchmarkShipTag"/> 与 <see cref="BenchmarkMotion"/>」的实体；
    /// 层实体没有 <see cref="BenchmarkMotion"/>，靠 Parent 层级跟随。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(BenchmarkGroup))]
    public partial class BenchmarkRandomMoveSystem : SystemBase
    {
        /// <summary>由 <see cref="BenchmarkSuite"/> 每帧写入；单位：世界单位/秒。</summary>
        public static float MoveSpeed = 10f;

        /// <summary>由 <see cref="BenchmarkSuite"/> 每帧写入；船被约束在 [-Bounds, Bounds] 立方体内。</summary>
        public static float Bounds = 200f;

        /// <summary>由 <see cref="BenchmarkSuite"/> 每帧写入；转向角速度，单位：度/秒（越小转得越慢）。</summary>
        public static float TurnSpeedDegPerSec = 120f;

        /// <summary>由 <see cref="BenchmarkSuite"/> 每帧写入；两次换向的间隔下限，单位：秒。</summary>
        public static float MinRetargetSeconds = 1f;

        /// <summary>由 <see cref="BenchmarkSuite"/> 每帧写入；两次换向的间隔上限，单位：秒。</summary>
        public static float MaxRetargetSeconds = 10f;

        protected override void OnUpdate()
        {
            if (MoveSpeed <= 0f)
            {
                return;
            }

            new MoveJob
            {
                DeltaTime = SystemAPI.Time.DeltaTime,
                Speed = MoveSpeed,
                Bounds = Bounds,
                TurnSpeedRad = math.radians(TurnSpeedDegPerSec),
                MinRetarget = MinRetargetSeconds,
                MaxRetarget = MaxRetargetSeconds,
            }.ScheduleParallel();
        }

        [BurstCompile]
        private partial struct MoveJob : IJobEntity
        {
            public float DeltaTime;
            public float Speed;
            public float Bounds;

            /// <summary>转向角速度，单位：弧度/秒。</summary>
            public float TurnSpeedRad;

            /// <summary>换向间隔的下限与上限，单位：秒。</summary>
            public float MinRetarget;
            public float MaxRetarget;

            private void Execute(ref LocalTransform transform, ref BenchmarkMotion motion, ref RvoAgent agent)
            {
                // ① 到点换向：抽一个新的目标朝向，并把计时重置为 [MinRetarget, MaxRetarget] 秒。
                motion.TimeUntilRetarget -= DeltaTime;
                if (motion.TimeUntilRetarget <= 0f)
                {
                    motion.TargetRotation = RandomHeadingAroundY(ref motion.Rng);
                    motion.TimeUntilRetarget = motion.Rng.NextFloat(MinRetarget, MaxRetarget);
                }

                // ② 平滑转向：本帧最多转 TurnSpeedRad * dt 弧度（走短弧）。
                transform.Rotation = RotateTowards(transform.Rotation, motion.TargetRotation,
                                                   TurnSpeedRad * DeltaTime);

                // ③ 期望速度 = 当前 forward 的水平分量 × 巡航速度。
                //    只给 xz：RvoAgent 的所有速度都是 float2。
                var forward = math.forward(transform.Rotation);
                var flat = new float2(forward.x, forward.z);
                var flatLen = math.length(flat);
                var desired = flatLen > 1e-4f ? flat / flatLen * Speed : float2.zero;

                // ④ 越界：期望速度与目标朝向都指向原点，让它游回来。
                //    只算 xz：所有船都在同一个水平面上，朝向也只在水平面内。
                var limit = new float3(Bounds);
                if (math.any(math.abs(transform.Position) > limit))
                {
                    var toCenter = -transform.Position;
                    var inward = new float2(toCenter.x, toCenter.z);
                    if (math.length(inward) > 1e-4f)
                    {
                        desired = math.normalize(inward) * Speed;
                        // 用 LookRotationSafe 且把 y 压成 0，保证朝向落在水平面内。
                        motion.TargetRotation =
                            quaternion.LookRotationSafe(math.normalize(new float3(inward.x, 0f, inward.y)), math.up());
                    }
                }

                // ⑤ 期望速度交给 Rvo 当下一帧的输入。
                agent.PrefVelocity = desired;

                // ⑥ 位置：用 Rvo 本帧解算出的速度积分（本示例唯一的落地点）。
                var velocity = agent.Velocity;
                var next = transform.Position + new float3(velocity.x, 0f, velocity.y) * DeltaTime;
                transform.Position = math.clamp(next, -limit, limit);
            }

            /// <summary>抽一个只在水平面内的朝向（只绕 Y 轴）。</summary>
            private static quaternion RandomHeadingAroundY(ref Random rng)
                => quaternion.RotateY(rng.NextFloat(0f, 2f * math.PI));

            /// <summary>从 <paramref name="from"/> 朝 <paramref name="to"/> 转，单帧最多 <paramref name="maxRadians"/> 弧度。</summary>
            private static quaternion RotateTowards(quaternion from, quaternion to, float maxRadians)
            {
                var dot = math.dot(from, to);
                if (dot < 0f)
                {
                    to.value = -to.value;
                    dot = -dot;
                }

                var angle = 2f * math.acos(math.clamp(dot, -1f, 1f));
                if (angle <= maxRadians)
                {
                    return to;
                }

                return math.slerp(from, to, maxRadians / angle);
            }
        }
    }
}
