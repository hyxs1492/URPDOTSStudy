using System;
using UnityEngine;

namespace DOTSUtils.Visibility
{
    /// <summary>
    /// 可见性模块的静态设置：先 <see cref="Configure"/> 攒好参数，模块装配到某个 World 时把它取走。
    /// </summary>
    public static class VisibilitySettings
    {
        private static VisibilityConfig s_Pending = VisibilityConfig.Default;

        /// <summary>待生效的参数；模块装配时读取。</summary>
        public static VisibilityConfig Pending
        {
            get => s_Pending;
            set => s_Pending = value;
        }

        /// <summary>
        /// 提供视锥的相机来源。默认 <c>Camera.main</c>。多相机场景里用它指定「以哪个相机为准」；
        /// 返回 <c>null</c> 表示本帧拿不到视锥（模块会退化成「全部可见」）。
        /// 每次取用都在主线程。
        /// </summary>
        public static Func<Camera> CameraProvider { get; set; } = () => Camera.main;

        public static void Configure(in VisibilityConfig config) => s_Pending = config;

        public static void Reset()
        {
            s_Pending = VisibilityConfig.Default;
            CameraProvider = () => Camera.main;
        }

        /// <summary>
        /// 进入 Play 时复位。本工程关闭了域重载（<c>m_EnterPlayModeOptions = 3</c>），静态字段跨会话保留。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlayModeEntry() => Reset();
    }
}
