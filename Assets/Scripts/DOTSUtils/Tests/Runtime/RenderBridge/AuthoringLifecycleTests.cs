using NUnit.Framework;
using Unity.Entities;
using UnityEngine;

namespace DOTSUtils.RenderBridge.Tests
{
    /// <summary><see cref="DotsRenderAuthoring"/> 依赖 <c>OnEnable</c> / <c>OnDisable</c> 入队，这两个回调只在 PlayMode 触发，所以必须在 PlayMode 下验证。PlayMode 下 <c>AddComponent</c>、<c>enabled = false</c>、<c>DestroyImmediate</c> 都同步触发回调，可以立即断言；不能 yield —— <see cref="DotsRenderConversionSystem"/> 每帧消费队列，跨帧后队列已被消费。</summary>
    public class AuthoringLifecycleTests
    {
        GameObject m_A;

        [SetUp]
        public void SetUp()
        {
            DotsRenderConversionQueue.Clear();
            m_A = new GameObject("a");
        }

        [TearDown]
        public void TearDown()
        {
            if (m_A != null) Object.DestroyImmediate(m_A);
            DotsRenderConversionQueue.Clear();

            // 兜底：确保 registry 不再持有本用例已销毁对象的槽位。
            var world = World.DefaultGameObjectInjectionWorld;
            if (world != null && world.IsCreated)
            {
                var conversionSystem = world.GetExistingSystemManaged<DotsRenderConversionSystem>();
                conversionSystem?.Registry.PruneDestroyedTransforms();
            }
        }

        [Test]
        public void 添加标记组件时自动入注册队列()
        {
            m_A.AddComponent<DotsRenderAuthoring>();

            Assert.IsTrue(DotsRenderConversionQueue.TryDequeueRegister(out var go));
            Assert.AreSame(m_A, go);
        }

        [Test]
        public void 标记组件被禁用时自动入注销队列()
        {
            var authoring = m_A.AddComponent<DotsRenderAuthoring>();
            DotsRenderConversionQueue.Clear();

            authoring.enabled = false;

            Assert.IsTrue(DotsRenderConversionQueue.TryDequeueUnregister(out var disabled));
            Assert.AreSame(m_A, disabled);
        }

        [Test]
        public void 标记组件被销毁时自动入注销队列()
        {
            var authoring = m_A.AddComponent<DotsRenderAuthoring>();
            DotsRenderConversionQueue.Clear();

            // 必须让组件保持 enabled 再销毁：已 disable 的组件被销毁时 Unity 只调 OnDestroy，不调 OnDisable。
            Object.DestroyImmediate(authoring);

            Assert.IsTrue(DotsRenderConversionQueue.TryDequeueUnregister(out var destroyed));
            Assert.AreSame(m_A, destroyed);
        }

        [Test]
        public void 两个列表默认为非null空列表()
        {
            var authoring = m_A.AddComponent<DotsRenderAuthoring>();

            // 两个列表必须默认为空列表（而非 null）。
            Assert.IsNotNull(authoring.MeshRenderers, "MeshRenderers 必须默认初始化为 new()，不应为 null");
            Assert.AreEqual(0, authoring.MeshRenderers.Count);
            Assert.IsNotNull(authoring.LodGroups, "LodGroups 必须默认初始化为 new()，不应为 null");
            Assert.AreEqual(0, authoring.LodGroups.Count);
        }
    }
}
