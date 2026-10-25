using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace DOTSUtils.RenderBridge.Tests
{
    /// <summary>OriginalRendererGuard 的三模式行为：隐藏方式、可逆性与 LODGroup 的特殊口径；DestroyComponents 走 Object.Destroy，必须在 PlayMode 等一帧后断言。</summary>
    public class OriginalRendererGuardTests
    {
        GameObject m_Go;
        MeshRenderer m_Renderer;
        LODGroup m_LodGroup;

        [SetUp]
        public void SetUp()
        {
            OriginalRendererGuard.ResetWarnings();

            m_Go = new GameObject("guard-target");
            m_Go.AddComponent<MeshFilter>();
            m_Renderer = m_Go.AddComponent<MeshRenderer>();
            m_LodGroup = m_Go.AddComponent<LODGroup>();
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Go != null) Object.DestroyImmediate(m_Go);
        }

        [Test]
        public void ForceRenderingOff_停绘制但不关组件_且LODGroup必须被禁用()
        {
            OriginalRendererGuard.Hide(m_Renderer, OriginalRendererDisposal.ForceRenderingOff);
            OriginalRendererGuard.Hide(m_LodGroup, OriginalRendererDisposal.ForceRenderingOff);

            Assert.IsTrue(m_Renderer.forceRenderingOff, "ForceRenderingOff 模式应置 forceRenderingOff");
            Assert.IsTrue(m_Renderer.enabled, "ForceRenderingOff 模式不应改动组件启用状态");
            Assert.IsFalse(m_LodGroup.enabled, "LODGroup 没有 forceRenderingOff，只能禁用组件");
        }

        [Test]
        public void DisableComponents_关闭组件()
        {
            OriginalRendererGuard.Hide(m_Renderer, OriginalRendererDisposal.DisableComponents);
            OriginalRendererGuard.Hide(m_LodGroup, OriginalRendererDisposal.DisableComponents);

            Assert.IsFalse(m_Renderer.enabled);
            Assert.IsFalse(m_Renderer.forceRenderingOff, "DisableComponents 模式不动 forceRenderingOff");
            Assert.IsFalse(m_LodGroup.enabled);
        }

        [UnityTest]
        public IEnumerator DestroyComponents_销毁组件()
        {
            OriginalRendererGuard.Hide(m_Renderer, OriginalRendererDisposal.DestroyComponents);
            OriginalRendererGuard.Hide(m_LodGroup, OriginalRendererDisposal.DestroyComponents);

            yield return null; // Object.Destroy 在帧末生效

            Assert.IsTrue(m_Renderer == null, "DestroyComponents 模式应销毁 MeshRenderer");
            Assert.IsTrue(m_LodGroup == null, "DestroyComponents 模式应销毁 LODGroup");
        }

        [Test]
        public void 还原_ForceRenderingOff与DisableComponents_各自复位()
        {
            OriginalRendererGuard.Hide(m_Renderer, OriginalRendererDisposal.ForceRenderingOff);
            OriginalRendererGuard.Hide(m_LodGroup, OriginalRendererDisposal.ForceRenderingOff);
            OriginalRendererGuard.Restore(m_Renderer, OriginalRendererDisposal.ForceRenderingOff);
            OriginalRendererGuard.Restore(m_LodGroup, OriginalRendererDisposal.ForceRenderingOff);
            Assert.IsFalse(m_Renderer.forceRenderingOff);
            Assert.IsTrue(m_Renderer.enabled);
            Assert.IsTrue(m_LodGroup.enabled);

            OriginalRendererGuard.Hide(m_Renderer, OriginalRendererDisposal.DisableComponents);
            OriginalRendererGuard.Hide(m_LodGroup, OriginalRendererDisposal.DisableComponents);
            OriginalRendererGuard.Restore(m_Renderer, OriginalRendererDisposal.DisableComponents);
            OriginalRendererGuard.Restore(m_LodGroup, OriginalRendererDisposal.DisableComponents);
            Assert.IsTrue(m_Renderer.enabled);
            Assert.IsTrue(m_LodGroup.enabled);
        }

        [UnityTest]
        public IEnumerator 还原_DestroyComponents是不可逆的noop()
        {
            OriginalRendererGuard.Hide(m_Renderer, OriginalRendererDisposal.DestroyComponents);
            OriginalRendererGuard.Hide(m_LodGroup, OriginalRendererDisposal.DestroyComponents);
            yield return null;

            // 已销毁的引用传入必须是安全的 no-op，不能抛异常
            Assert.DoesNotThrow(() => OriginalRendererGuard.Restore(m_Renderer, OriginalRendererDisposal.DestroyComponents));
            Assert.IsTrue(m_Renderer == null, "DestroyComponents 注销后不应还原渲染");

            Assert.DoesNotThrow(() => OriginalRendererGuard.Restore(m_LodGroup, OriginalRendererDisposal.DestroyComponents));
            Assert.IsTrue(m_LodGroup == null);
        }

        [Test]
        public void 空引用传入_所有模式都不抛异常()
        {
            Assert.DoesNotThrow(() => OriginalRendererGuard.Hide((Renderer)null, OriginalRendererDisposal.ForceRenderingOff));
            Assert.DoesNotThrow(() => OriginalRendererGuard.Hide((LODGroup)null, OriginalRendererDisposal.DestroyComponents));
            Assert.DoesNotThrow(() => OriginalRendererGuard.Restore((Renderer)null, OriginalRendererDisposal.DisableComponents));
            Assert.DoesNotThrow(() => OriginalRendererGuard.Restore((LODGroup)null, OriginalRendererDisposal.DestroyComponents));
        }
    }
}
