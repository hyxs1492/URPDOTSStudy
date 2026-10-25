using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace DOTSUtils.Spatial.Tests
{
    public class QuadTreeIncrementalTests
    {
        /// <summary>构造后不经过 Clear 直接插入时，链表哨兵仍正确。</summary>
        [Test]
        public void 构造后直接插入_不经过Clear_链表哨兵正确()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, new AABB2D(float2.zero, 10f), Allocator.Temp, maxDepth: 4, maxLeafElements: 4);
            var collected = new NativeList<SpatialElement>(Allocator.Temp);
            var hits = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                var first = tree.Insert(QuadTreeTestUtil.MakeElement(float3.zero, 0));
                Assert.AreEqual(0, first.Slot, "第一个元素应当拿到槽位 0（自环只有在 slot 0 时才立刻暴露）");

                tree.CollectElements(collected);                          // 遍历链表
                Assert.AreEqual(1, collected.Length);

                tree.RangeQuery(new AABB2D(float2.zero, 1f), ref hits);    // 查询同样遍历链表
                Assert.AreEqual(1, hits.Length);

                for (var i = 1; i < 8; i++)
                    tree.Insert(QuadTreeTestUtil.MakeElement(new float3(i, 0f, -i), i));

                collected.Clear();
                tree.CollectElements(collected);
                Assert.AreEqual(8, collected.Length);
                Assert.AreEqual(8, tree.ElementCount);
            }
            finally { tree.Dispose(); collected.Dispose(); hits.Dispose(); }
        }

        [Test]
        public void 池扩容后_链表哨兵仍然正确()
        {
            // 初始容量给 2，逼出 GrowPool()：新槽位的 m_Next 必须是 -1。
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, new AABB2D(float2.zero, 50f), Allocator.Temp, maxDepth: 5, maxLeafElements: 4,
                initialElementCapacity: 2);
            var collected = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                for (var i = 0; i < 40; i++)
                    tree.Insert(QuadTreeTestUtil.MakeElement(new float3(-40f + i, 0f, -40f + i * 0.5f), i));

                tree.CollectElements(collected);
                Assert.AreEqual(40, collected.Length);
                Assert.AreEqual(40, tree.ElementCount);
            }
            finally { tree.Dispose(); collected.Dispose(); }
        }

        [Test]
        public void 删除后槽位复用_链表仍然自洽()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, new AABB2D(float2.zero, 20f), Allocator.Temp, maxDepth: 5, maxLeafElements: 4);
            var collected = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                var refs = new List<SpatialElementRef>();
                for (var i = 0; i < 12; i++)
                    refs.Add(tree.Insert(QuadTreeTestUtil.MakeElement(new float3(-15f + i * 2.5f, 0f, -15f + i), i)));

                for (var i = 0; i < 6; i++) Assert.IsTrue(tree.Remove(refs[i]));

                for (var i = 0; i < 6; i++)
                    refs.Add(tree.Insert(QuadTreeTestUtil.MakeElement(new float3(i, 0f, i), 100 + i)));

                collected.Clear();
                tree.CollectElements(collected);
                Assert.AreEqual(12, tree.ElementCount);
                Assert.AreEqual(12, collected.Length);
            }
            finally { tree.Dispose(); collected.Dispose(); }
        }

        [Test]
        public void 逐一插入_与批量构建的查询结果一致()
        {
            var elements = QuadTreeTestUtil.MakeRandomElements(600, 95f, seed: 21);
            var input = QuadTreeTestUtil.ToNative(elements, Allocator.Temp);
            var bulk = new NativeSpatialTree(SpatialIndexKind.Quadtree, QuadTreeTestUtil.DefaultBounds, Allocator.Temp, 6, 4);
            var incremental = new NativeSpatialTree(SpatialIndexKind.Quadtree, QuadTreeTestUtil.DefaultBounds, Allocator.Temp, 6, 4);
            var bulkHits = new NativeList<SpatialElement>(Allocator.Temp);
            var incrementalHits = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                bulk.ClearAndBulkInsert(input);
                for (var i = 0; i < input.Length; i++)
                    Assert.IsTrue(incremental.Insert(input[i]).IsValid, $"第 {i} 个元素插入失败");

                Assert.AreEqual(bulk.ElementCount, incremental.ElementCount);

                var random = new Random(404);
                for (var q = 0; q < 100; q++)
                {
                    var query = new AABB2D(
                        new float2(random.NextFloat(-100f, 100f), random.NextFloat(-100f, 100f)),
                        random.NextFloat(1f, 50f));

                    bulkHits.Clear();
                    incrementalHits.Clear();
                    bulk.RangeQuery(query, ref bulkHits);
                    incremental.RangeQuery(query, ref incrementalHits);

                    QuadTreeTestUtil.AssertSameSet(bulkHits.AsArray().ToArray(), incrementalHits.AsArray().ToArray(),
                        $"第 {q} 组查询：bulk 与增量结果不一致");
                }
            }
            finally
            {
                bulk.Dispose(); incremental.Dispose(); input.Dispose();
                bulkHits.Dispose(); incrementalHits.Dispose();
            }
        }

        [Test]
        public void 插入触发分裂_统计正确()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, new AABB2D(float2.zero, 10f), Allocator.Temp, maxDepth: 4, maxLeafElements: 2);
            try
            {
                var refs = new SpatialElementRef[6];
                for (var i = 0; i < refs.Length; i++)
                    refs[i] = tree.Insert(QuadTreeTestUtil.MakeElement(new float3(-8f + i * 3f, 0f, -8f + i * 2f), i));

                Assert.IsTrue(System.Array.TrueForAll(refs, r => r.IsValid));
                Assert.AreEqual(6, tree.ElementCount);
                Assert.Greater(tree.SplitNodeCount, 0, "元素分散在不同象限，应当发生分裂");
                Assert.Greater(tree.LeafCount, 0);
                Assert.LessOrEqual(tree.NodeCount, tree.NodeCapacity);
            }
            finally { tree.Dispose(); }
        }

        [Test]
        public void 越界与NaN_插入被拒绝且计数正确()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, new AABB2D(float2.zero, 10f), Allocator.Temp);
            try
            {
                Assert.IsFalse(tree.Insert(QuadTreeTestUtil.MakeElement(new float3(50f, 0f, 0f), 0)).IsValid);
                Assert.IsFalse(tree.Insert(QuadTreeTestUtil.MakeElement(new float3(float.NaN, 0f, 0f), 1)).IsValid);
                Assert.AreEqual(2, tree.OutOfBoundsRejected);
                Assert.AreEqual(0, tree.ElementCount);
                Assert.AreEqual(0, tree.LeafCount);
            }
            finally { tree.Dispose(); }
        }

        [Test]
        public void 移动元素_句柄不变且查询跟随()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, new AABB2D(float2.zero, 50f), Allocator.Temp, 5, 4);
            var hits = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                var moved = tree.Insert(QuadTreeTestUtil.MakeElement(new float3(-40f, 0f, -40f), 0));
                tree.Insert(QuadTreeTestUtil.MakeElement(new float3(0f, 0f, 0f), 1));
                tree.Insert(QuadTreeTestUtil.MakeElement(new float3(30f, 0f, 30f), 2));

                Assert.IsTrue(tree.Update(moved, new float3(45f, 0f, 45f)));   // 跨象限迁移
                Assert.IsTrue(tree.TryGet(moved, out var element));
                Assert.AreEqual(45f, element.Pos.x);
                Assert.AreEqual(3, tree.ElementCount);

                tree.RangeQuery(new AABB { Center = new float3(45f, 0f, 45f), Extents = new float3(3f, 1f, 3f) }, ref hits);
                Assert.AreEqual(1, hits.Length);
                Assert.AreEqual(1, hits[0].Entity.Index);

                hits.Clear();
                tree.RangeQuery(new AABB { Center = new float3(-40f, 0f, -40f), Extents = new float3(3f, 1f, 3f) }, ref hits);
                Assert.AreEqual(0, hits.Length, "旧位置不应再命中");
            }
            finally { tree.Dispose(); hits.Dispose(); }
        }

        [Test]
        public void 同一叶内微小移动_只改位置()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, new AABB2D(float2.zero, 50f), Allocator.Temp, 6, 16);
            try
            {
                var reference = tree.Insert(QuadTreeTestUtil.MakeElement(new float3(1f, 0f, 1f), 0));
                var leavesBefore = tree.LeafCount;
                var splitsBefore = tree.SplitNodeCount;

                Assert.IsTrue(tree.Update(reference, new float3(1.001f, 0f, 1.001f)));

                Assert.AreEqual(1, tree.ElementCount);
                Assert.AreEqual(leavesBefore, tree.LeafCount);
                Assert.AreEqual(splitsBefore, tree.SplitNodeCount);
            }
            finally { tree.Dispose(); }
        }

        [Test]
        public void 陈旧句柄_更新与删除都返回false且不动树()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, new AABB2D(float2.zero, 50f), Allocator.Temp, 5, 4);
            try
            {
                var stale = tree.Insert(QuadTreeTestUtil.MakeElement(new float3(1f, 0f, 1f), 0));
                Assert.IsTrue(tree.Remove(stale));

                var reused = tree.Insert(QuadTreeTestUtil.MakeElement(new float3(2f, 0f, 2f), 1));
                Assert.AreNotEqual(stale, reused, "槽位复用时 version 必须变化");

                Assert.IsFalse(tree.Update(stale, new float3(3f, 0f, 3f)));
                Assert.IsFalse(tree.Remove(stale));
                Assert.AreEqual(1, tree.ElementCount);
            }
            finally { tree.Dispose(); }
        }

        [Test]
        public void 删除到空_叶子与分裂节点被回收()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, new AABB2D(float2.zero, 50f), Allocator.Temp, 5, 2);
            try
            {
                var refs = new SpatialElementRef[4];
                for (var i = 0; i < refs.Length; i++)
                    refs[i] = tree.Insert(QuadTreeTestUtil.MakeElement(new float3(-40f + i * 5f, 0f, -40f + i * 4f), i));

                Assert.Greater(tree.SplitNodeCount, 0);
                for (var i = 0; i < refs.Length; i++) Assert.IsTrue(tree.Remove(refs[i]), $"删除第 {i} 个失败");

                Assert.AreEqual(0, tree.ElementCount);
                Assert.AreEqual(0, tree.LeafCount, "全部删空后不应留下叶子");
                Assert.AreEqual(0, tree.SplitNodeCount, "全部删空后分裂节点应被回收");
            }
            finally { tree.Dispose(); }
        }

        [Test]
        public void 随机增删移动_统计自洽且全范围查询命中所有存活元素()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, new AABB2D(float2.zero, 50f), Allocator.Temp, 5, 4);
            var hits = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                var random = new Random(2024);
                var live = new List<SpatialElementRef>();
                // id 必须单调唯一：MakeElement 的 id 直接写进 Entity.Index，而本用例会删元素。
                var nextId = 0;
                for (var i = 0; i < 400; i++)
                {
                    var roll = random.NextInt(0, 10);
                    if (roll < 2 && live.Count > 20)
                    {
                        var index = random.NextInt(0, live.Count);
                        Assert.IsTrue(tree.Remove(live[index]));
                        live.RemoveAt(index);
                    }
                    else if (roll < 4 && live.Count > 20)
                    {
                        var index = random.NextInt(0, live.Count);
                        var position = new float3(random.NextFloat(-49f, 49f), 0f, random.NextFloat(-49f, 49f));
                        Assert.IsTrue(tree.Update(live[index], position));
                    }
                    else
                    {
                        live.Add(tree.Insert(QuadTreeTestUtil.MakeElement(
                            new float3(random.NextFloat(-49f, 49f), 0f, random.NextFloat(-49f, 49f)), nextId++)));
                    }
                }

                Assert.AreEqual(live.Count, tree.ElementCount);
                Assert.LessOrEqual(tree.NodeCount, tree.NodeCapacity);

                tree.RangeQuery(new AABB2D(float2.zero, 60f), ref hits);
                Assert.AreEqual(live.Count, hits.Length, "全范围查询应恰好命中所有存活元素");
                Assert.AreEqual(live.Count, hits.AsArray().Select(e => e.Entity).Distinct().Count(), "不应有重复");
            }
            finally { tree.Dispose(); hits.Dispose(); }
        }
    }
}
