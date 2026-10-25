using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace DOTSUtils.Spatial.Tests
{
    public class QuadTreeConstructionTests
    {
        static readonly AABB2D k_Bounds = new AABB2D(float2.zero, 100f);

        [Test]
        public void 构造后为空树且统计为零()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, k_Bounds, Allocator.Temp);
            try
            {
                Assert.IsTrue(tree.IsCreated);
                Assert.IsTrue(tree.IsEmpty);
                Assert.AreEqual(0, tree.ElementCount);
                Assert.AreEqual(0, tree.LeafCount);
                Assert.AreEqual(0, tree.SplitNodeCount);
                Assert.AreEqual(0, tree.NodeCount);
                Assert.AreEqual((AABB)k_Bounds, tree.Bounds);
                Assert.AreEqual(6, tree.MaxDepth);
                Assert.AreEqual(16, tree.MaxLeafElements);
                Assert.AreEqual(SpatialLookupTables.DepthSize4[7], tree.NodeCapacity);
            }
            finally { tree.Dispose(); }
        }

        [Test]
        public void maxDepth_超上限_抛异常()
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
                new NativeSpatialTree(SpatialIndexKind.Quadtree, k_Bounds, Allocator.Temp, maxDepth: 9));
            StringAssert.Contains("8", ex.Message);
        }

        [Test]
        public void bounds_非法_抛异常()
        {
            Assert.Throws<ArgumentException>(() => new NativeSpatialTree(SpatialIndexKind.Quadtree, new AABB2D(float2.zero, 0f), Allocator.Temp));
            Assert.Throws<ArgumentException>(() =>
                new NativeSpatialTree(SpatialIndexKind.Quadtree, new AABB { Center = new float3(float.NaN, 0f, 0f), Extents = new float3(10f, 1f, 10f) }, Allocator.Temp));
        }

        [Test]
        public void Dispose_之后_IsCreated_为假且可重复调用()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, k_Bounds, Allocator.Temp);
            tree.Dispose();
            tree.Dispose();

            Assert.IsFalse(tree.IsCreated);
            Assert.Throws<InvalidOperationException>(() => tree.Clear());
        }

        [Test]
        public void Contains_用闭区间()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, k_Bounds, Allocator.Temp);
            try
            {
                Assert.IsTrue(tree.Contains(new float3(-100f, 0f, -100f)));
                Assert.IsTrue(tree.Contains(float3.zero));
                Assert.IsTrue(tree.Contains(new float3(100f, 5f, 100f)));   // y 不参与，只看 xz
                Assert.IsFalse(tree.Contains(new float3(100.01f, 0f, 0f)));
                Assert.IsFalse(tree.Contains(new float3(float.NaN, 0f, 0f)));
            }
            finally { tree.Dispose(); }
        }
    }

    public class QuadTreeBulkTests
    {
        static AABB2D Bounds(float half = 100f) => new AABB2D(float2.zero, half);

        [Test]
        public void 批量插入_统计与元素数与输入一致()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, Bounds(), Allocator.Temp, maxDepth: 6, maxLeafElements: 4);
            var input = new NativeArray<SpatialElement>(16, Allocator.Temp);
            try
            {
                for (var i = 0; i < input.Length; i++)
                    input[i] = QuadTreeTestUtil.MakeElement(new float3(i * 10f - 70f, 0f, i * 5f - 40f), i);

                var accepted = tree.ClearAndBulkInsert(input);

                Assert.AreEqual(16, accepted);
                Assert.AreEqual(16, tree.ElementCount);
                Assert.AreEqual(0, tree.OutOfBoundsRejected);
                Assert.Greater(tree.LeafCount, 0);
                Assert.LessOrEqual(tree.NodeCount, tree.NodeCapacity);
            }
            finally { tree.Dispose(); input.Dispose(); }
        }

        [Test]
        public void 批量插入_越界元素被拒绝()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, Bounds(10f), Allocator.Temp);
            var input = new NativeArray<SpatialElement>(3, Allocator.Temp);
            try
            {
                input[0] = QuadTreeTestUtil.MakeElement(new float3(0f, 0f, 0f), 0);
                input[1] = QuadTreeTestUtil.MakeElement(new float3(50f, 0f, 0f), 1);    // 越界
                input[2] = QuadTreeTestUtil.MakeElement(new float3(10f, 0f, -10f), 2);  // 恰在角上（闭区间，接受）

                Assert.AreEqual(2, tree.ClearAndBulkInsert(input));
                Assert.AreEqual(2, tree.ElementCount);
                Assert.AreEqual(1, tree.OutOfBoundsRejected);
            }
            finally { tree.Dispose(); input.Dispose(); }
        }

        [Test]
        public void 批量插入_全同点不超深且元素不丢()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, Bounds(), Allocator.Temp, maxDepth: 4, maxLeafElements: 2);
            var input = new NativeArray<SpatialElement>(8, Allocator.Temp);
            try
            {
                for (var i = 0; i < input.Length; i++) input[i] = QuadTreeTestUtil.MakeElement(float3.zero, i);

                tree.ClearAndBulkInsert(input);

                Assert.AreEqual(8, tree.ElementCount);
                Assert.AreEqual(1, tree.LeafCount);          // 全落在一个叶
                Assert.LessOrEqual(tree.SplitNodeCount, 4);  // 最多分裂到 maxDepth
            }
            finally { tree.Dispose(); input.Dispose(); }
        }

        [Test]
        public void morton_忽略y_且不同位置编码不同()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, Bounds(), Allocator.Temp, maxDepth: 6);
            try
            {
                Assert.AreEqual(tree.MortonCode(new float3(1f, 0f, 2f)), tree.MortonCode(new float3(1f, 9f, 2f)));
                Assert.AreNotEqual(tree.MortonCode(new float3(-10f, 0f, -10f)), tree.MortonCode(new float3(10f, 0f, 10f)));
            }
            finally { tree.Dispose(); }
        }

        [Test]
        public void 深度分布_与叶子数一致()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, Bounds(), Allocator.Temp, maxDepth: 5, maxLeafElements: 2);
            var elements = QuadTreeTestUtil.MakeRandomElements(64, 90f, seed: 1234);
            var input = QuadTreeTestUtil.ToNative(elements, Allocator.Temp);
            var distribution = new NativeArray<int>(6, Allocator.Temp);
            try
            {
                tree.ClearAndBulkInsert(input);
                tree.GetDepthDistribution(distribution);

                var sum = 0;
                for (var i = 0; i < distribution.Length; i++) sum += distribution[i];
                Assert.AreEqual(tree.LeafCount, sum);
            }
            finally { tree.Dispose(); input.Dispose(); distribution.Dispose(); }
        }

        [Test]
        public void 空输入_不抛异常且保持为空()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, Bounds(), Allocator.Temp);
            var input = new NativeArray<SpatialElement>(0, Allocator.Temp);
            try
            {
                Assert.AreEqual(0, tree.ClearAndBulkInsert(input));
                Assert.IsTrue(tree.IsEmpty);
                Assert.AreEqual(0, tree.LeafCount);
            }
            finally { tree.Dispose(); input.Dispose(); }
        }
    }
}
