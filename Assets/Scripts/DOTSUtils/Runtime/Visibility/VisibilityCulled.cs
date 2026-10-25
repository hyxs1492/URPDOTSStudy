using Unity.Entities;

namespace DOTSUtils.Visibility
{
    /// <summary>
    /// 「这个实体本帧不可见（在相机视锥外）」。与 Spatial / Rvo 同级的一个**通用能力** ——
    /// 任何「只想处理看得见的东西」的系统都能拿它当依据，不必各自算一遍视锥：
    /// <list type="bullet">
    /// <item>Rvo：不可见的跳过求解；</item>
    /// <item>RenderBridge：不可见的跳过变换推送。</item>
    /// </list>
    ///
    /// 是 <see cref="IEnableableComponent"/>，<b>启用 = 被裁剪 = 不可见</b>。切换只改 chunk 的位掩码、
    /// 不动 archetype。
    ///
    /// 三种状态：
    /// <list type="bullet">
    /// <item><b>没挂这个组件</b> —— 不参与裁剪，<b>照常处理</b>；</item>
    /// <item><b>挂了 + 禁用</b> —— 可见（<see cref="VisibilitySystem"/> 每帧写；也是刚 <c>AddComponent</c> 后的初始态）；</item>
    /// <item><b>挂了 + 启用</b> —— 不可见，本帧跳过。</item>
    /// </list>
    ///
    /// 消费方直接在 query 里排除被裁剪的即可，用**泛型** <c>WithNone&lt;VisibilityCulledTag&gt;()</c>：
    /// <code>
    /// SystemAPI.QueryBuilder().WithAllRW&lt;RvoAgent&gt;().WithAll&lt;LocalTransform&gt;()
    ///                        .WithNone&lt;VisibilityCulledTag&gt;()   // 没挂 或 挂+禁用 ⇒ 都算可见
    ///                        .Build()
    /// </code>
    /// 必须用**泛型**重载。<c>EntityQueryDesc.None</c>（接受 <c>ComponentType</c> 的那个）对
    /// <see cref="IEnableableComponent"/> 只匹配「没挂」，**匹配不到「挂但禁用」**。
    ///
    /// 没法改 query 的场合（<c>IJobParallelForTransform</c>、或判断**别的**实体时）用
    /// <see cref="VisibilityAccess.IsCulled"/>，它的语义与上面那条 query **完全一致**。
    ///
    /// 想被裁剪的实体需要**显式挂上它**（<c>AddComponent&lt;VisibilityCulledTag&gt;()</c>）。
    /// </summary>
    public struct VisibilityCulledTag : IComponentData, IEnableableComponent
    {
    }
}
