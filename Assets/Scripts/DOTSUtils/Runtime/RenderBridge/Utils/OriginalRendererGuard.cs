using UnityEngine;

namespace DOTSUtils.RenderBridge
{
    /// <summary>
    /// 按 <see cref="OriginalRendererDisposal"/> 集中实现「原对象不再绘制」与「注销时还原」，
    /// 供 <see cref="DotsRenderConversionSystem"/> 与 <see cref="DotsRenderLodSupport"/> 共用。
    ///
    /// 口径：
    /// - <c>ForceRenderingOff</c>：MeshRenderer 用 <c>forceRenderingOff</c>（不动 enabled）；
    ///   而 <c>LODGroup</c> 没有 forceRenderingOff，只能 <c>enabled = false</c>，所以对 LODGroup 来说
    ///   本模式与 <c>DisableComponents</c> 等价。
    /// - <c>DestroyComponents</c>：销毁组件，注销时不还原（传入已销毁引用必须是安全的 no-op）。
    /// </summary>
    public static class OriginalRendererGuard
    {
        private static bool s_WarnedDestroy;

        /// <summary>
        /// 按模式让原 MeshRenderer 不再绘制。
        /// </summary>
        public static void Hide(Renderer renderer, OriginalRendererDisposal mode)
        {
            if (renderer == null)
            {
                return;
            }

            switch (mode)
            {
                case OriginalRendererDisposal.DisableComponents:
                    renderer.enabled = false;
                    break;
                case OriginalRendererDisposal.DestroyComponents:
                    WarnDestroyOnce();
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter)
                    {
                        Object.Destroy(filter);
                    }
                    Object.Destroy(renderer);
                    break;
                default: // ForceRenderingOff
                    renderer.forceRenderingOff = true;
                    break;
            }
        }

        /// <summary>
        /// 按模式让原 LODGroup 停止参与渲染。
        /// LODGroup 没有 forceRenderingOff，两种非销毁模式都只能禁用组件来停掉它的 LOD 计算。
        /// </summary>
        public static void Hide(LODGroup lodGroup, OriginalRendererDisposal mode)
        {
            if (lodGroup == null)
            {
                return;
            }

            if (mode == OriginalRendererDisposal.DestroyComponents)
            {
                WarnDestroyOnce();
                Object.Destroy(lodGroup);
                return;
            }

            lodGroup.enabled = false;
        }

        /// <summary>
        /// 注销时按模式还原渲染。DestroyComponents 下组件已销毁，是安全的 no-op。
        /// </summary>
        public static void Restore(Renderer renderer, OriginalRendererDisposal mode)
        {
            if (renderer == null)
            {
                return;
            }
            if (mode == OriginalRendererDisposal.DestroyComponents)
            {
                return;
            }

            if (mode == OriginalRendererDisposal.DisableComponents)
                renderer.enabled = true;
            else
                renderer.forceRenderingOff = false;
        }

        /// <summary>
        /// 注销时按模式还原 LODGroup。DestroyComponents 下组件已销毁，是安全的 no-op。
        /// </summary>
        public static void Restore(LODGroup lodGroup, OriginalRendererDisposal mode)
        {
            if (lodGroup == null)
            {
                return;
            }
            if (mode == OriginalRendererDisposal.DestroyComponents)
            {
                return;
            }

            lodGroup.enabled = true;
        }

        private static void WarnDestroyOnce()
        {
            if (s_WarnedDestroy)
            {
                return;
            }
            s_WarnedDestroy = true;
            Debug.LogWarning(
                "[DotsRender] DestroyComponents：原 MeshRenderer/LODGroup 将被销毁，注销该对象时无法恢复（不可逆）。");
        }

        /// <summary>
        /// 清掉一次性警告标志。
        /// </summary>
        public static void ResetWarnings() => s_WarnedDestroy = false;

        /// <summary>
        /// 进入 Play 前重置警告标志。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlayMode() => s_WarnedDestroy = false;
    }
}
