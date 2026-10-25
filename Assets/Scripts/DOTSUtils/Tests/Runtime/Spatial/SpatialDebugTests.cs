using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace DOTSUtils.Spatial.Tests
{
    /// <summary>SpatialDebugSource 自己建树、自己管生命周期，与 World 里的单例树无关。</summary>
    public class SpatialDebugTests
    {
        GameObject m_Root;

        [TearDown]
        public void TearDown()
        {
            if (m_Root != null) Object.DestroyImmediate(m_Root);
            m_Root = null;
        }

        [Test]
        public void DebugSource_启用即建树并收纳自身与子物体()
        {
            m_Root = new GameObject("SpatialDebugSourceTest");
            var child = new GameObject("child");
            child.transform.SetParent(m_Root.transform);
            child.transform.position = new Vector3(3f, 0f, 4f);

            var source = m_Root.AddComponent<SpatialDebugSource>();
            source.HalfExtent = 50f;

            Assert.IsTrue(source.IsCreated, "OnEnable 后树应已建立");
            Assert.AreEqual(2, source.ResolvedTargets.Count, "Targets 为空时应收本物体 + 子物体");
            Assert.AreEqual(2, source.Tree.ElementCount);
        }

        [Test]
        public void DebugSource_位置变化后查询跟随()
        {
            m_Root = new GameObject("SpatialDebugSourceMove");
            var child = new GameObject("mover");
            child.transform.SetParent(m_Root.transform);

            var source = m_Root.AddComponent<SpatialDebugSource>();
            source.HalfExtent = 50f;

            child.transform.position = new Vector3(10f, 0f, 10f);
            source.Rebuild();

            var hits = source.Tree.RangeQuery(new AABB { Center = new float3(10f, 0f, 10f), Extents = new float3(2f) }, Allocator.Temp);
            try
            {
                Assert.AreEqual(1, hits.Length, "新位置应命中一个元素（子物体）");
            }
            finally
            {
                hits.Dispose();
            }
        }

        [Test]
        public void DebugSource_取数可交给可视化窗口()
        {
            m_Root = new GameObject("SpatialDebugSourceNodes");
            m_Root.AddComponent<SpatialDebugSource>().HalfExtent = 50f;

            var nodes = new NativeList<SpatialDebugNode>(Allocator.Temp);
            var elements = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                TreeOf(m_Root).CollectNodes(nodes);
                Assert.Greater(nodes.Length, 0, "至少应有根下那一层的节点");

                TreeOf(m_Root).CollectElements(elements);
                Assert.AreEqual(1, elements.Length, "只有根物体一个元素");
            }
            finally
            {
                nodes.Dispose();
                elements.Dispose();
            }
        }

        static NativeSpatialTree TreeOf(GameObject go) => go.GetComponent<SpatialDebugSource>().Tree;

        [Test]
        public void DebugSource_禁用后释放树()
        {
            m_Root = new GameObject("SpatialDebugSourceDispose");
            var source = m_Root.AddComponent<SpatialDebugSource>();
            Assert.IsTrue(source.IsCreated);

            source.enabled = false;          // OnDisable → Dispose
            Assert.IsFalse(source.IsCreated, "禁用后树应被释放");
        }
    }
}
