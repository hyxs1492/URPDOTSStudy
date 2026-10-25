using Unity.Mathematics;

namespace DOTSUtils.Rvo
{
    /// <summary>
    /// 算法内核的入参：一个 agent 的避障相关状态，<b>只含 xz 平面</b>。
    /// 内核不认识 ECS —— 系统侧负责把它从 <c>LocalTransform</c> + <c>RvoAgent</c> 拼出来。
    /// </summary>
    public struct AgentState
    {
        /// <summary>
        /// 当前位置（xz）。
        /// </summary>
        public float2 Position;

        /// <summary>
        /// 当前速度（xz）。
        /// </summary>
        public float2 Velocity;

        /// <summary>
        /// 碰撞半径（&gt; 0）。
        /// </summary>
        public float Radius;

        /// <summary>
        /// 最大速度（&gt;= 0）。
        /// </summary>
        public float MaxSpeed;

        /// <summary>
        /// 期望速度（xz）—— 通常由目标系统/玩家输入给出。
        /// </summary>
        public float2 PrefVelocity;
    }
}
