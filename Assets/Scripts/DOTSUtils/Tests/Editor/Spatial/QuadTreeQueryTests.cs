using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace DOTSUtils.Spatial.Tests
{
    public class QuadTreeQueryTests
    {
        [Test]
        public void 矩形查询_与暴力一致_随机200组()
        {
            var elements = QuadTreeTestUtil.MakeRandomElements(1500, 95f, seed: 7);
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, QuadTreeTestUtil.DefaultBounds, Allocator.Temp, maxDepth: 6, maxLeafElements: 8);
            var input = QuadTreeTestUtil.ToNative(elements, Allocator.Temp);
            var results = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                tree.ClearAndBulkInsert(input);

                var random = new Random(99);
                for (var i = 0; i < 200; i++)
                {
                    var query = new AABB2D(
                        new float2(random.NextFloat(-100f, 100f), random.NextFloat(-100f, 100f)),
                        random.NextFloat(1f, 60f));

                    results.Clear();
                    tree.RangeQuery(query, ref results);

                    QuadTreeTestUtil.AssertSameSet(
                        QuadTreeTestUtil.BruteForceAabb(elements, query), results.AsArray().ToArray(), $"第 {i} 组查询不一致");
                }
            }
            finally { tree.Dispose(); input.Dispose(); results.Dispose(); }
        }

        [Test]
        public void 圆形查询_与暴力一致且填充distanceSq()
        {
            var elements = QuadTreeTestUtil.MakeRandomElements(800, 95f, seed: 11);
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, QuadTreeTestUtil.DefaultBounds, Allocator.Temp, maxDepth: 5, maxLeafElements: 8);
            var input = QuadTreeTestUtil.ToNative(elements, Allocator.Temp);
            var results = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                tree.ClearAndBulkInsert(input);

                var center = new float2(20f, -35f);
                const float radius = 40f;
                tree.RangeQuery(new float3(center.x, 0f, center.y), radius, ref results, computeDistanceSq: true);

                QuadTreeTestUtil.AssertSameSet(
                    QuadTreeTestUtil.BruteForceCircle(elements, center, radius), results.AsArray().ToArray());

                foreach (var hit in results.AsArray())
                    Assert.AreEqual(math.distancesq(hit.Pos.xz, center), hit.distancesq, 1e-3f);
            }
            finally { tree.Dispose(); input.Dispose(); results.Dispose(); }
        }

        [Test]
        public void 查询结果无重复_且不遗漏_恰好等于暴力数量()
        {
            var elements = QuadTreeTestUtil.MakeRandomElements(1000, 95f, seed: 17);
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, QuadTreeTestUtil.DefaultBounds, Allocator.Temp, maxDepth: 6, maxLeafElements: 4);
            var input = QuadTreeTestUtil.ToNative(elements, Allocator.Temp);
            var results = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                tree.ClearAndBulkInsert(input);
                tree.RangeQuery(new AABB2D(float2.zero, 200f), ref results);   // 覆盖全范围

                Assert.AreEqual(elements.Length, results.Length);
                Assert.AreEqual(elements.Length, results.AsArray().Select(e => e.Entity).Distinct().Count());
            }
            finally { tree.Dispose(); input.Dispose(); results.Dispose(); }
        }

        [Test]
        public void 空树与无命中_返回空结果()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, new AABB2D(float2.zero, 10f), Allocator.Temp);
            var results = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                tree.RangeQuery(new AABB2D(float2.zero, 5f), ref results);
                Assert.AreEqual(0, results.Length);

                tree.Insert(QuadTreeTestUtil.MakeElement(new float3(9f, 0f, 9f), 0));
                results.Clear();
                tree.RangeQuery(new AABB { Center = new float3(-9f, 0f, -9f), Extents = new float3(1f, 1f, 1f) }, ref results);
                Assert.AreEqual(0, results.Length);
            }
            finally { tree.Dispose(); results.Dispose(); }
        }

        [Test]
        public void 便捷重载_返回结果表由调用方释放()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, new AABB2D(float2.zero, 10f), Allocator.Temp);
            try
            {
                tree.Insert(QuadTreeTestUtil.MakeElement(new float3(1f, 0f, 1f), 0));
                var results = tree.RangeQuery(new AABB2D(float2.zero, 5f), Allocator.Temp);
                try
                {
                    Assert.AreEqual(1, results.Length);
                }
                finally { results.Dispose(); }
            }
            finally { tree.Dispose(); }
        }
    }
}
