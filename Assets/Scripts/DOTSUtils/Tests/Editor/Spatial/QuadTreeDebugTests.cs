using System;
using NUnit.Framework;
using Unity.Collections;

namespace DOTSUtils.Spatial.Tests
{
    public class QuadTreeDebugTests
    {
        [Test]
        public void CollectNodes_与统计自洽()
        {
            var elements = QuadTreeTestUtil.MakeRandomElements(500, 95f, seed: 31);
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, QuadTreeTestUtil.DefaultBounds, Allocator.Temp, 6, 8);
            var input = QuadTreeTestUtil.ToNative(elements, Allocator.Temp);
            var nodes = new NativeList<SpatialDebugNode>(Allocator.Temp);
            try
            {
                tree.ClearAndBulkInsert(input);
                tree.CollectNodes(nodes);

                var leaves = 0;
                var splits = 0;
                var elementsInLeaves = 0;
                var maxDepth = 0;
                foreach (var node in nodes)
                {
                    if (node.IsLeaf)
                    {
                        leaves++;
                        elementsInLeaves += node.Count;
                    }
                    else
                    {
                        splits++;
                    }

                    maxDepth = Math.Max(maxDepth, node.Depth);
                }

                Assert.Greater(leaves, 0);
                Assert.AreEqual(tree.LeafCount, leaves);
                Assert.AreEqual(tree.SplitNodeCount, splits);
                Assert.AreEqual(tree.ElementCount, elementsInLeaves);
                Assert.LessOrEqual(maxDepth, tree.MaxDepth);
            }
            finally { tree.Dispose(); input.Dispose(); nodes.Dispose(); }
        }

        [Test]
        public void CollectNodes_增量修改后仍然自洽()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, QuadTreeTestUtil.DefaultBounds, Allocator.Temp, 5, 4);
            var nodes = new NativeList<SpatialDebugNode>(Allocator.Temp);
            try
            {
                var random = new Unity.Mathematics.Random(77);
                var refs = new SpatialElementRef[64];
                for (var i = 0; i < refs.Length; i++)
                    refs[i] = tree.Insert(QuadTreeTestUtil.MakeElement(
                        new Unity.Mathematics.float3(random.NextFloat(-99f, 99f), 0f, random.NextFloat(-99f, 99f)), i));

                for (var i = 0; i < refs.Length; i += 2) tree.Remove(refs[i]);
                for (var i = 1; i < refs.Length; i += 4)
                    tree.Update(refs[i], new Unity.Mathematics.float3(random.NextFloat(-99f, 99f), 0f, random.NextFloat(-99f, 99f)));

                tree.CollectNodes(nodes);

                var leaves = 0;
                var splits = 0;
                var elementsInLeaves = 0;
                foreach (var node in nodes)
                {
                    if (node.IsLeaf) { leaves++; elementsInLeaves += node.Count; }
                    else splits++;
                }

                Assert.AreEqual(tree.LeafCount, leaves);
                Assert.AreEqual(tree.SplitNodeCount, splits);
                Assert.AreEqual(tree.ElementCount, elementsInLeaves);
            }
            finally { tree.Dispose(); nodes.Dispose(); }
        }

        [Test]
        public void CollectElements_与插入的元素一致()
        {
            var elements = QuadTreeTestUtil.MakeRandomElements(200, 95f, seed: 41);
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, QuadTreeTestUtil.DefaultBounds, Allocator.Temp, 5, 8);
            var input = QuadTreeTestUtil.ToNative(elements, Allocator.Temp);
            var collected = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                tree.ClearAndBulkInsert(input);
                tree.CollectElements(collected);

                Assert.AreEqual(elements.Length, collected.Length);
                QuadTreeTestUtil.AssertSameSet(elements, collected.AsArray().ToArray());
            }
            finally { tree.Dispose(); input.Dispose(); collected.Dispose(); }
        }

        [Test]
        public void 清空后_取数为空()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, QuadTreeTestUtil.DefaultBounds, Allocator.Temp, 5, 4);
            var nodes = new NativeList<SpatialDebugNode>(Allocator.Temp);
            var collected = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                tree.Insert(QuadTreeTestUtil.MakeElement(Unity.Mathematics.float3.zero, 0));
                tree.Clear();
                tree.CollectNodes(nodes);
                tree.CollectElements(collected);

                Assert.AreEqual(0, nodes.Length);
                Assert.AreEqual(0, collected.Length);
                Assert.AreEqual(0, tree.NodeCount);
            }
            finally { tree.Dispose(); nodes.Dispose(); collected.Dispose(); }
        }
    }
}
