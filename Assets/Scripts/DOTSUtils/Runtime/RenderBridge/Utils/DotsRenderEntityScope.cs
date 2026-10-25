namespace DOTSUtils.RenderBridge
{
    /// <summary>
    /// 写操作的作用范围。
    /// </summary>
    public enum DotsRenderEntityScope
    {
        /// <summary>
        /// 只逻辑父实体（无渲染组件）。
        /// </summary>
        Parent,

        /// <summary>
        /// 只渲染实体：子渲染实体 + LOD 组实体 + LOD 层实体。
        /// </summary>
        RenderTargets,

        /// <summary>
        /// 父实体 + 全部渲染实体（写操作默认）。
        /// </summary>
        All,
    }

    /// <summary>
    /// 实体在桥的层级里扮演什么角色。
    /// </summary>
    public enum DotsRenderTargetKind
    {
        /// <summary>
        /// 逻辑父实体。
        /// </summary>
        Parent,

        /// <summary>
        /// 某个 MeshRenderer 对应的渲染实体。
        /// </summary>
        MeshRenderer,

        /// <summary>
        /// LOD 组实体。
        /// </summary>
        LodGroup,

        /// <summary>
        /// LOD 层实体。
        /// </summary>
        LodLayer,
    }

    /// <summary>
    /// 写操作的结果。
    /// </summary>
    public enum DotsRenderOpResult
    {
        /// <summary>
        /// 实体已就绪，当场执行成功。
        /// </summary>
        Applied,

        /// <summary>
        /// 实体尚未创建，已排队，转换完成后按序应用。
        /// </summary>
        Queued,

        /// <summary>
        /// 无法受理（没有 World / owner 未标记 / 已经注销等），已打警告。
        /// </summary>
        Failed,
    }
}
