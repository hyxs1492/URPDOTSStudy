using NUnit.Framework;
using UnityEngine;

namespace DOTSUtils.RenderBridge.Tests
{
    /// <summary>快照的采集与幂等规则。Mesh / Material 一律走 <see cref="TestRenderAssets"/>：会话级共享、用例之间不销毁。</summary>
    public class AuthoringSnapshotTests
    {
        GameObject m_Root;
        GameObject m_Child;
        MeshRenderer m_Renderer;
        Mesh m_Mesh;
        Material m_Material;

        [SetUp]
        public void SetUp()
        {
            DotsRenderConversionQueue.Clear();

            m_Root = new GameObject("snapshot_root");
            m_Child = new GameObject("snapshot_child");
            m_Child.transform.SetParent(m_Root.transform);

            m_Mesh = TestRenderAssets.NewMesh("snapshot_mesh", 1);
            m_Material = TestRenderAssets.NewMaterial();

            m_Child.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
            m_Renderer = m_Child.AddComponent<MeshRenderer>();
            m_Renderer.sharedMaterial = m_Material;
        }

        [TearDown]
        public void TearDown()
        {
            DotsRenderConversionQueue.Clear();
            if (m_Root != null) Object.DestroyImmediate(m_Root);
        }

        [Test]
        public void 保留渲染组件时采集到快照()
        {
            var authoring = m_Root.AddComponent<DotsRenderAuthoring>();
            authoring.MeshRenderers.Add(m_Renderer);

            // AddComponent 触发的 Awake/OnEnable 那一刻列表还是空的，快照要靠这次显式调用建立。
            authoring.CaptureSnapshot();

            Assert.AreEqual(1, authoring.SnapshotRenderTargetCount, "应采集到 1 个渲染目标");
        }

        [Test]
        public void 采集是幂等的且采不到数据时不会被清空()
        {
            var authoring = m_Root.AddComponent<DotsRenderAuthoring>();
            authoring.MeshRenderers.Add(m_Renderer);
            authoring.CaptureSnapshot();

            var before = authoring.SnapshotRenderTargetCount;
            Assert.AreEqual(1, before, "前提：已采集到 1 个渲染目标");

            // 模拟 DestroyComponents 之后的状态：组件没了，列表里只剩 fake-null。
            Object.DestroyImmediate(m_Renderer);
            authoring.MeshRenderers.Clear();

            authoring.CaptureSnapshot();

            Assert.AreEqual(before, authoring.SnapshotRenderTargetCount,
                "采不到有效数据时必须保留旧快照，不能清空");
        }

        [Test]
        public void 空列表不建立快照()
        {
            var authoring = m_Root.AddComponent<DotsRenderAuthoring>();

            authoring.CaptureSnapshot();

            Assert.AreEqual(0, authoring.SnapshotRenderTargetCount, "列表为空时不应建立快照");
        }
    }
}
