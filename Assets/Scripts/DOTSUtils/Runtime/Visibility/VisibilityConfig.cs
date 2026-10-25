namespace DOTSUtils.Visibility
{
    /// <summary>
    /// 可见性模块的参数。它是**全局配置**（经 <see cref="VisibilitySettings"/> 攒起来），不是组件。
    /// </summary>
    public struct VisibilityConfig
    {
        /// <summary>
        /// 判定余量，单位：世界单位。把视锥平面各朝外扩这么多再判 ——
        /// 贴着视口边缘的实体提前算作可见。
        /// </summary>
        public float Margin;

        /// <summary>
        /// 关掉 = 不做裁剪（全部视为可见）。
        /// </summary>
        public bool Enabled;

        public static VisibilityConfig Default => new VisibilityConfig
        {
            Margin = 1f,
            Enabled = true,
        };
    }
}
