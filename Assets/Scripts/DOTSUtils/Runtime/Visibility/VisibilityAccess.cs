using Unity.Entities;

namespace DOTSUtils.Visibility
{
    /// <summary>
    /// 可见性的读取入口：消费方（Rvo、RenderBridge …）统一走这里判断实体可见性。
    /// </summary>
    public static class VisibilityAccess
    {
        /// <summary>
        /// 该实体本帧是否**被裁剪**（不可见）。三种情况：
        /// <list type="bullet">
        /// <item>没挂 <see cref="VisibilityCulledTag"/> —— <c>false</c>（外部不管可见性，照常处理）；</item>
        /// <item>挂了且禁用 —— <c>false</c>（可见）；</item>
        /// <item>挂了且启用 —— <c>true</c>（被裁剪）。</item>
        /// </list>
        /// 语义与 query 里的 <c>WithNone&lt;VisibilityCulledTag&gt;()</c> 一致。
        ///
        /// 用在没法改 query 的场合：<c>IJobParallelForTransform</c>（如渲染桥的变换推送），
        /// 以及判断**别的**实体时（如 RVO 判断邻居是否被裁剪）。
        ///
        /// 可以在 Burst job 里用（<see cref="ComponentLookup{T}"/> 是只读视图）。
        /// 必须先 <c>HasComponent</c> 再 <c>IsComponentEnabled</c> ——
        /// 后者对「没挂该组件」的实体会抛异常，不是返回 false。
        /// </summary>
        public static bool IsCulled(in ComponentLookup<VisibilityCulledTag> tags, Entity entity)
            => tags.HasComponent(entity) && tags.IsComponentEnabled(entity);

        /// <summary>主线程版本（<c>EntityManager</c>），语义与上面完全一致。</summary>
        public static bool IsCulled(EntityManager em, Entity entity)
            => em.HasComponent<VisibilityCulledTag>(entity) && em.IsComponentEnabled<VisibilityCulledTag>(entity);

        /// <summary>上面两个的取反。</summary>
        public static bool IsVisible(in ComponentLookup<VisibilityCulledTag> tags, Entity entity)
            => !IsCulled(tags, entity);

        /// <inheritdoc cref="IsVisible(in ComponentLookup{VisibilityCulledTag}, Entity)"/>
        public static bool IsVisible(EntityManager em, Entity entity) => !IsCulled(em, entity);
    }
}
