using UnityEngine;

namespace DOTSUtils.Samples.Pathfinding
{
    /// <summary>
    /// A* 演示的运行期参数（跟路径系统与调试画线系统每帧读它）。
    /// </summary>
    public struct AStarDemoConfig
    {
        /// <summary>所有 agent 所在的水平面高度（y）；只影响位置与画线的高度。</summary>
        public float PlaneHeight;

        /// <summary>判定「已到达路点」的水平距离（世界单位）。</summary>
        public float ArriveRadius;

        /// <summary>新目标与当前位置的最小距离（世界单位），避免挑到脚底下的格子。</summary>
        public float MinGoalDistance;

        /// <summary>请求失败 / 挑不到目标后的重试间隔，单位：秒。</summary>
        public float RetryDelay;

        /// <summary>转向角速度，单位：度/秒。</summary>
        public float TurnSpeedDegPerSec;

        /// <summary>用 Debug.DrawLine 画路径的 agent 个数（0 = 不画）。</summary>
        public int DebugPathCount;

        /// <summary>调试线离地的高度（世界单位）。</summary>
        public float DebugLineHeight;

        /// <summary>是否让全体 agent 以鼠标地面落点为目标；关掉就回到原来的随机游走。</summary>
        public bool FollowCursor;

        /// <summary>鼠标目标周围取随机偏移的半径（世界单位）；0 = 所有 agent 精确指向同一点。</summary>
        public float CursorSpreadRadius;

        /// <summary>鼠标目标换版本后的重规划错峰步长（帧）：agent 按自己的相位摊到这个窗口里响应。</summary>
        public int CursorRepathStride;

        /// <summary>是否画出鼠标目标标记（十字 + 目标格方框 + 竖线 + 偏移半径圈）。</summary>
        public bool DebugCursorMarker;

        /// <summary>是否每帧汇总统计单例（关掉就不读 agent 组件，省一次主线程拷贝）。</summary>
        public bool StatsEnabled;

        /// <summary>等待求解结果超过这个秒数就强制重发一次请求；&lt;= 0 表示不设看门狗。</summary>
        public float PendingTimeoutSeconds;

        /// <summary>默认参数。</summary>
        public static AStarDemoConfig Default => new AStarDemoConfig
        {
            PlaneHeight = 0.4f,
            ArriveRadius = 0.75f,
            MinGoalDistance = 24f,
            RetryDelay = 0.35f,
            TurnSpeedDegPerSec = 360f,
            DebugPathCount = 16,
            DebugLineHeight = 0.6f,
            FollowCursor = true,
            CursorSpreadRadius = 8f,
            CursorRepathStride = 64,
            DebugCursorMarker = true,
            StatsEnabled = true,
            PendingTimeoutSeconds = 0.5f,
        };
    }

    /// <summary>
    /// 演示参数的进程级入口：<see cref="AStarDemoBootstrap"/> 在 Awake 里写一次，
    /// 跟路径系统与调试画线系统每帧读。本工程关闭了域重载，所以进 Play 时复位。
    /// </summary>
    public static class AStarDemoSettings
    {
        private static AStarDemoConfig s_Config = AStarDemoConfig.Default;

        /// <summary>当前参数。</summary>
        public static AStarDemoConfig Config
        {
            get => s_Config;
            set => s_Config = value;
        }

        /// <summary>恢复默认参数。</summary>
        public static void Reset() => s_Config = AStarDemoConfig.Default;

        /// <summary>进入 Play 时复位（静态字段跨 Play 会话保留）。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlayModeEntry() => Reset();
    }
}
