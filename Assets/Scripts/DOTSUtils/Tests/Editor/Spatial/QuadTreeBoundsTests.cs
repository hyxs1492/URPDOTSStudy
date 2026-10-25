using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace DOTSUtils.Spatial.Tests
{
    /// <summary>包围盒计算与边界语义：CalculateBounds、越界拒绝、恰好落在边界上的点的归属（闭区间，且只会进一个叶）。</summary>
    public class QuadTreeBoundsTests
    {
        static NativeArray<float3> Positions(params float3[] points)
        {
            var array = new NativeArray<float3>(points.Length, Allocator.Temp);
            for (var i = 0; i < points.Length; i++) array[i] = points[i];
            return array;
        }

        static SpatialElement E(float3 position, int id) => new SpatialElement
        {
            Pos = position,
            Entity = new Unity.Entities.Entity { Index = id + 1, Version = 1 },
        };

        [Test]
        public void CalculateBounds_覆盖所有点且外扩padding()
        {
            var positions = Positions(new float3(-10f, 999f, -20f), new float3(30f, -999f, 45f));
            try
            {
                var bounds = NativeSpatialTree.CalculateBounds(positions);
                Assert.AreEqual(new float3(10f, 0f, 12.5f), bounds.Center);
                Assert.AreEqual(new float3(20f, 999f, 32.5f), bounds.Extents);

                for (var i = 0; i < positions.Length; i++)
                    Assert.IsTrue(bounds.Contains(new float3(positions[i].x, 0f, positions[i].z)), "算出来的盒必须覆盖每一个输入点（闭区间）");

                var padded = NativeSpatialTree.CalculateBounds(positions, padding: 5f);
                Assert.AreEqual(bounds.Center, padded.Center, "padding 不该动中心");
                Assert.AreEqual(bounds.Extents + new float3(5f), padded.Extents);
            }
            finally
            {
                positions.Dispose();
            }
        }

        [Test]
        public void CalculateBounds_空集合或全非法_返回可建树的盒()
        {
            var empty = Positions();
            var invalid = Positions(new float3(float.NaN, 0f, 0f), new float3(0f, 0f, float.PositiveInfinity));
            try
            {
                var fromEmpty = NativeSpatialTree.CalculateBounds(empty);
                Assert.AreEqual(float3.zero, fromEmpty.Center);
                Assert.IsTrue(math.all(fromEmpty.Extents > 0f), "必须给正体积，否则没法建树");

                var fromInvalid = NativeSpatialTree.CalculateBounds(invalid, padding: 3f);
                Assert.AreEqual(new float3(3f, 3f, 3f), fromInvalid.Extents, "外扩量应当体现在结果里");

                // 直接拿去建树不该抛
                var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, fromInvalid, Allocator.Temp);
                try
                {
                    Assert.IsTrue(tree.IsCreated);
                    Assert.AreEqual(0, tree.ElementCount);
                }
                finally
                {
                    tree.Dispose();
                }
            }
            finally
            {
                empty.Dispose();
                invalid.Dispose();
            }
        }

        [Test]
        public void CalculateBounds_忽略非法位置()
        {
            var positions = Positions(new float3(1f, 0f, 3f), new float3(float.NaN, 0f, 0f),
                                      new float3(5f, 0f, 7f), new float3(0f, 0f, float.NegativeInfinity));
            try
            {
                var bounds = NativeSpatialTree.CalculateBounds(positions);
                Assert.AreEqual(new float3(3f, 0f, 5f), bounds.Center);
                Assert.AreEqual(new float3(2f, 0.001f, 2f), bounds.Extents);   // y 退化时给最小厚度
            }
            finally
            {
                positions.Dispose();
            }
        }

        [Test]
        public void 边界上的点_闭区间且只进一个叶()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, new AABB2D { Center = float2.zero, Extents = new float2(10f) },
                                          Allocator.Temp, 3, 2);
            var hits = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                var corner = tree.Insert(E(new float3(10f, 0f, 10f), 0));
                Assert.IsTrue(corner.IsValid, "正好落在 Max 面上的点属于索引范围（闭区间）");
                Assert.AreEqual(0, tree.OutOfBoundsRejected);

                tree.RangeQuery(new AABB2D { Center = new float2(10f, 10f), Extents = new float2(0.5f) }, ref hits);
                Assert.AreEqual(1, hits.Length, "贴面的查询盒也应命中它");

                Assert.IsFalse(tree.Insert(E(new float3(10.001f, 0f, 0f), 1)).IsValid, "越界一点点就要拒绝");
                Assert.AreEqual(1, tree.OutOfBoundsRejected);
                Assert.AreEqual(1, tree.ElementCount, "被拒的元素不入树");
            }
            finally
            {
                tree.Dispose();
                hits.Dispose();
            }
        }

        [Test]
        public void 越界元素_批量路径跳过且计数()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Quadtree, new AABB2D(float2.zero, 10f), Allocator.Temp, 3, 2);
            var input = new NativeArray<SpatialElement>(3, Allocator.Temp);
            try
            {
                input[0] = E(new float3(0f, 0f, 0f), 0);
                input[1] = E(new float3(100f, 0f, 0f), 1);
                input[2] = E(new float3(0f, 0f, float.NaN), 2);

                Assert.AreEqual(1, tree.ClearAndBulkInsert(input), "越界与 NaN 都不该计入接受数");
                Assert.AreEqual(1, tree.ElementCount);
                Assert.AreEqual(2, tree.OutOfBoundsRejected);
            }
            finally
            {
                tree.Dispose();
                input.Dispose();
            }
        }
    }
}
