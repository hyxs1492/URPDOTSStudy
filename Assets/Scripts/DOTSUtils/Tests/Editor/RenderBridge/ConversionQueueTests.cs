using NUnit.Framework;
using UnityEngine;

namespace DOTSUtils.RenderBridge.Tests
{
    public class ConversionQueueTests
    {
        GameObject m_A;
        GameObject m_B;

        [SetUp]
        public void SetUp()
        {
            DotsRenderConversionQueue.Clear();
            m_A = new GameObject("a");
            m_B = new GameObject("b");
        }

        [TearDown]
        public void TearDown()
        {
            if (m_A != null) Object.DestroyImmediate(m_A);
            if (m_B != null) Object.DestroyImmediate(m_B);
            DotsRenderConversionQueue.Clear();
        }

        [Test]
        public void 注册与注销进入各自队列()
        {
            DotsRenderConversionQueue.EnqueueRegister(m_A);
            DotsRenderConversionQueue.EnqueueUnregister(m_B);

            Assert.IsTrue(DotsRenderConversionQueue.TryDequeueRegister(out var reg));
            Assert.AreSame(m_A, reg);
            Assert.IsFalse(DotsRenderConversionQueue.TryDequeueRegister(out _), "注册队列应已空");

            Assert.IsTrue(DotsRenderConversionQueue.TryDequeueUnregister(out var unreg));
            Assert.AreSame(m_B, unreg);
        }

        [Test]
        public void 同一队列内保持FIFO顺序()
        {
            DotsRenderConversionQueue.EnqueueRegister(m_A);
            DotsRenderConversionQueue.EnqueueRegister(m_B);

            DotsRenderConversionQueue.TryDequeueRegister(out var first);
            DotsRenderConversionQueue.TryDequeueRegister(out var second);

            Assert.AreSame(m_A, first);
            Assert.AreSame(m_B, second);
        }

        [Test]
        public void Clear清空两个队列()
        {
            DotsRenderConversionQueue.EnqueueRegister(m_A);
            DotsRenderConversionQueue.EnqueueUnregister(m_B);

            DotsRenderConversionQueue.Clear();

            Assert.IsFalse(DotsRenderConversionQueue.TryDequeueRegister(out _));
            Assert.IsFalse(DotsRenderConversionQueue.TryDequeueUnregister(out _));
        }
    }
}
