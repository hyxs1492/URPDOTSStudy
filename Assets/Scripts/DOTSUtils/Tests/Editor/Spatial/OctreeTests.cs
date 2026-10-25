using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace DOTSUtils.Spatial.Tests
{
    /// <summary>3D 位交错 / 查表 / 边界的基本正确性。</summary>
    public class OctreeMortonTests
    {
        [Test]
        public void ExpandBits8_与朴素三重循环实现逐位对照()
        {
            for (var value = 0; value < 256; value++)
            {
                var fast = SpatialLookupTables.ExpandBits8((uint)value);
                var naive = 0u;
                for (var bit = 0; bit < 8; bit++)
                {
                    if (((value >> bit) & 1) != 0) naive |= 1u << (bit * 3);
                }

                Assert.AreEqual(naive, fast, $"value={value} 的位交错结果应一致");
            }
        }

        [Test]
        public void Morton_与朴素实现一致且各轴单调()
        {
            for (var x = 0; x < 17; x++)
            {
                for (var y = 0; y < 17; y++)
                {
                    for (var z = 0; z < 17; z++)
                    {
                        Assert.AreEqual(SpatialLookupTables.MortonNaive(x, y, z), SpatialLookupTables.Morton(x, y, z));
                    }
                }
            }

            // 单轴递增 ⇒ morton 递增
            Assert.Less(SpatialLookupTables.Morton(1, 0, 0), SpatialLookupTables.Morton(2, 0, 0));
            Assert.Less(SpatialLookupTables.Morton(0, 1, 0), SpatialLookupTables.Morton(0, 2, 0));
            Assert.Less(SpatialLookupTables.Morton(0, 0, 1), SpatialLookupTables.Morton(0, 0, 2));
        }

        [Test]
        public void DepthSizeLookup_等于等比数列和()
        {
            for (var depth = 0; depth < SpatialLookupTables.DepthSize8.Length; depth++)
            {
                var expected = 0;
                var power = 1;
                for (var k = 0; k <= depth; k++)
                {
                    expected += power;
                    power *= 8;
                }

                Assert.AreEqual(expected, SpatialLookupTables.DepthSize8[depth], $"depth={depth}");
            }
        }

        [Test]
        public void 构造_越界深度抛异常()
        {
            var bounds = new AABB(float3.zero, new float3(200f));
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => new NativeSpatialTree(SpatialIndexKind.Octree, bounds, Allocator.Temp, maxDepth: NativeSpatialTree.MaxSupportedDepth + 1));
            Assert.Throws<System.ArgumentException>(
                () => new NativeSpatialTree(SpatialIndexKind.Octree, new AABB(float3.zero, float3.zero), Allocator.Temp));
        }
    }

    /// <summary>批量构建。</summary>
    public class OctreeBulkTests
    {
        static readonly AABB k_Bounds = new AABB(float3.zero, new float3(200f));

        static SpatialElement E(float3 pos, int id) => new SpatialElement
        {
            Pos = pos,
            Entity = new Unity.Entities.Entity { Index = id + 1, Version = 1 },
        };

        [Test]
        public void 空树_统计全零()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, k_Bounds, Allocator.Temp, 5, 4);
            try
            {
                Assert.AreEqual(0, tree.ElementCount);
                Assert.AreEqual(0, tree.NodeCount);
                Assert.IsTrue(tree.IsEmpty);
            }
            finally { tree.Dispose(); }
        }

        [Test]
        public void 批量插入_统计与元素数与输入一致()
        {
            const int count = 500;
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, k_Bounds, Allocator.Temp, 5, 4);
            var input = new NativeArray<SpatialElement>(count, Allocator.Temp);
            var collected = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                var rnd = new Unity.Mathematics.Random(2026);
                for (var i = 0; i < count; i++)
                {
                    input[i] = E(new float3(rnd.NextFloat(-95f, 95f), rnd.NextFloat(-95f, 95f),
                                            rnd.NextFloat(-95f, 95f)), i);
                }

                Assert.AreEqual(count, tree.ClearAndBulkInsert(input));
                Assert.AreEqual(count, tree.ElementCount);

                tree.CollectElements(collected);
                Assert.AreEqual(count, collected.Length, "元素不该丢也不该重");

                Assert.AreEqual(tree.LeafCount + tree.SplitNodeCount, tree.NodeCount);
                Assert.LessOrEqual(tree.NodeCount, tree.NodeCapacity);
            }
            finally { tree.Dispose(); input.Dispose(); collected.Dispose(); }
        }

        [Test]
        public void 全同点_不无限分裂且元素不丢()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, k_Bounds, Allocator.Temp, 4, 2);
            var input = new NativeArray<SpatialElement>(8, Allocator.Temp);
            try
            {
                for (var i = 0; i < input.Length; i++) input[i] = E(float3.zero, i);
                tree.ClearAndBulkInsert(input);

                Assert.AreEqual(8, tree.ElementCount);
                Assert.LessOrEqual(tree.SplitNodeCount, 8, "最多分裂到 maxDepth");
            }
            finally { tree.Dispose(); input.Dispose(); }
        }

        [Test]
        public void 越界与NaN_被拒绝且计数正确()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, new AABB(float3.zero, new float3(100f)), Allocator.Temp, 4, 4);
            var input = new NativeArray<SpatialElement>(3, Allocator.Temp);
            try
            {
                input[0] = E(new float3(0f, 0f, 0f), 0);
                input[1] = E(new float3(999f, 0f, 0f), 1);
                input[2] = E(new float3(float.NaN, 0f, 0f), 2);

                Assert.AreEqual(1, tree.ClearAndBulkInsert(input));
                Assert.AreEqual(1, tree.ElementCount);
                Assert.AreEqual(2, tree.OutOfBoundsRejected);
            }
            finally { tree.Dispose(); input.Dispose(); }
        }

        [Test]
        public void y轴参与索引_同xz不同y不会塌到同一点()
        {
            // 同 xz、不同 y 的两点必须被区分开。
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, new AABB(float3.zero, new float3(100f)), Allocator.Temp, 3, 1);
            try
            {
                var a = tree.Insert(E(new float3(10f, -40f, 10f), 0));
                var b = tree.Insert(E(new float3(10f, 40f, 10f), 1));
                Assert.IsTrue(a.IsValid && b.IsValid);
                Assert.AreNotEqual(tree.MortonCode(new float3(10f, -40f, 10f)),
                                   tree.MortonCode(new float3(10f, 40f, 10f)), "y 不同 ⇒ morton 不同");
                Assert.AreEqual(2, tree.ElementCount);
            }
            finally { tree.Dispose(); }
        }
    }

    /// <summary>球 / AABB 查询与暴力对照。</summary>
    public class OctreeQueryTests
    {
        static readonly AABB k_Bounds = new AABB(float3.zero, new float3(200f));

        static SpatialElement E(float3 pos, int id) => new SpatialElement
        {
            Pos = pos,
            Entity = new Unity.Entities.Entity { Index = id + 1, Version = 1 },
        };

        static SpatialElement[] MakeRandom(int count, float halfExtent, uint seed)
        {
            var rnd = new Unity.Mathematics.Random(seed);
            var result = new SpatialElement[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = E(new float3(rnd.NextFloat(-halfExtent, halfExtent),
                                         rnd.NextFloat(-halfExtent, halfExtent),
                                         rnd.NextFloat(-halfExtent, halfExtent)), i);
            }

            return result;
        }

        [Test]
        public void 球查询_随机对照暴力()
        {
            var elements = MakeRandom(800, 95f, seed: 7);
            var input = new NativeArray<SpatialElement>(elements.Length, Allocator.Temp);
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, k_Bounds, Allocator.Temp, 5, 4);
            var hits = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                for (var i = 0; i < elements.Length; i++) input[i] = elements[i];
                tree.ClearAndBulkInsert(input);

                var rnd = new Unity.Mathematics.Random(99);
                for (var q = 0; q < 50; q++)
                {
                    var center = new float3(rnd.NextFloat(-100f, 100f), rnd.NextFloat(-100f, 100f),
                                            rnd.NextFloat(-100f, 100f));
                    var radius = rnd.NextFloat(1f, 60f);

                    hits.Clear();
                    tree.RangeQuery(center, radius, ref hits);

                    var expected = 0;
                    for (var i = 0; i < input.Length; i++)
                        if (math.distancesq(input[i].Pos, center) <= radius * radius) expected++;

                    Assert.AreEqual(expected, hits.Length, $"第 {q} 组球查询的命中数应等于暴力");
                }
            }
            finally { tree.Dispose(); input.Dispose(); hits.Dispose(); }
        }

        [Test]
        public void AABB查询_随机对照暴力且无重复()
        {
            var elements = MakeRandom(600, 90f, seed: 11);
            var input = new NativeArray<SpatialElement>(elements.Length, Allocator.Temp);
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, k_Bounds, Allocator.Temp, 5, 4);
            var hits = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                for (var i = 0; i < elements.Length; i++) input[i] = elements[i];
                tree.ClearAndBulkInsert(input);

                var rnd = new Unity.Mathematics.Random(123);
                for (var q = 0; q < 50; q++)
                {
                    var center = new float3(rnd.NextFloat(-90f, 90f), rnd.NextFloat(-90f, 90f),
                                            rnd.NextFloat(-90f, 90f));
                    var half = rnd.NextFloat(2f, 40f);
                    var query = new AABB(center, new float3(half * 2f));

                    hits.Clear();
                    tree.RangeQuery(query, ref hits);

                    var expected = 0;
                    for (var i = 0; i < input.Length; i++)
                        if (query.Contains(input[i].Pos)) expected++;

                    Assert.AreEqual(expected, hits.Length, $"第 {q} 组 AABB 查询的命中数应等于暴力");
                    Assert.AreEqual(hits.Length, hits.AsArray().Select(e => e.Entity).Distinct().Count(), "不应有重复");
                }
            }
            finally { tree.Dispose(); input.Dispose(); hits.Dispose(); }
        }

        [Test]
        public void 球查询_可填充distanceSq()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, k_Bounds, Allocator.Temp, 5, 4);
            var hits = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                tree.Insert(E(new float3(3f, 4f, 0f), 0));
                tree.RangeQuery(float3.zero, 10f, ref hits, computeDistanceSq: true);

                Assert.AreEqual(1, hits.Length);
                Assert.AreEqual(25f, hits[0].distancesq, 1e-3f);
            }
            finally { tree.Dispose(); hits.Dispose(); }
        }

        [Test]
        public void 空树与无命中_返回空结果()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, k_Bounds, Allocator.Temp, 5, 4);
            var hits = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                tree.RangeQuery(float3.zero, 10f, ref hits);
                Assert.AreEqual(0, hits.Length);

                tree.Insert(E(new float3(50f, 50f, 50f), 0));
                hits.Clear();
                tree.RangeQuery(new float3(-50f, -50f, -50f), 2f, ref hits);
                Assert.AreEqual(0, hits.Length);
            }
            finally { tree.Dispose(); hits.Dispose(); }
        }
    }

    /// <summary>增量增删改。</summary>
    public class OctreeIncrementalTests
    {
        // AABB 的第二个参数是 size（Extents = size/2），与 AABB2D 的 (center, radius) 重载不同。
        static readonly AABB k_Bounds = new AABB(float3.zero, new float3(200f));

        static SpatialElement E(float3 pos, int id) => new SpatialElement
        {
            Pos = pos,
            Entity = new Unity.Entities.Entity { Index = id + 1, Version = 1 },
        };

        [Test]
        public void 构造后直接插入_链表与统计自洽()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, k_Bounds, Allocator.Temp, 4, 4);
            var collected = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                var first = tree.Insert(E(float3.zero, 0));
                Assert.AreEqual(0, first.Slot);

                for (var i = 1; i < 10; i++) tree.Insert(E(new float3(i, i * 0.5f, -i), i));

                tree.CollectElements(collected);
                Assert.AreEqual(10, collected.Length);
                Assert.AreEqual(10, tree.ElementCount);
            }
            finally { tree.Dispose(); collected.Dispose(); }
        }

        [Test]
        public void 移动元素_句柄不变且查询跟随()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, k_Bounds, Allocator.Temp, 4, 4);
            var hits = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                var moved = tree.Insert(E(new float3(-60f, -60f, -60f), 0));
                tree.Insert(E(float3.zero, 1));

                Assert.IsTrue(tree.Update(moved, new float3(60f, 60f, 60f)));
                Assert.IsTrue(tree.TryGet(moved, out var element));
                Assert.AreEqual(60f, element.Pos.x);
                Assert.AreEqual(2, tree.ElementCount);

                tree.RangeQuery(new float3(60f, 60f, 60f), 2f, ref hits);
                Assert.AreEqual(1, hits.Length);

                hits.Clear();
                tree.RangeQuery(new float3(-60f, -60f, -60f), 2f, ref hits);
                Assert.AreEqual(0, hits.Length, "旧位置不应再命中");
            }
            finally { tree.Dispose(); hits.Dispose(); }
        }

        [Test]
        public void 陈旧句柄_更新与删除都返回false()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, k_Bounds, Allocator.Temp, 4, 4);
            try
            {
                var stale = tree.Insert(E(new float3(1f, 1f, 1f), 0));
                Assert.IsTrue(tree.Remove(stale));

                var reused = tree.Insert(E(new float3(2f, 2f, 2f), 1));
                Assert.AreNotEqual(stale, reused, "槽位复用时 version 必须变化");

                Assert.IsFalse(tree.Update(stale, new float3(3f, 3f, 3f)));
                Assert.IsFalse(tree.Remove(stale));
                Assert.AreEqual(1, tree.ElementCount);
            }
            finally { tree.Dispose(); }
        }

        [Test]
        public void 删除到空_叶子与分裂节点被回收()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, new AABB(float3.zero, new float3(50f)), Allocator.Temp, 4, 2);
            try
            {
                var refs = new SpatialElementRef[8];
                for (var i = 0; i < refs.Length; i++)
                    refs[i] = tree.Insert(E(new float3(-20f + i, -20f + i * 2f, -20f + i * 3f), i));

                Assert.Greater(tree.SplitNodeCount, 0, "8 个点聚在同一个八分体，应当分裂");
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
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, new AABB(float3.zero, new float3(120f)), Allocator.Temp, 4, 4);
            var hits = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                var rnd = new Unity.Mathematics.Random(4242);
                var live = new List<SpatialElementRef>();
                var nextId = 0;
                for (var i = 0; i < 300; i++)
                {
                    var roll = rnd.NextInt(0, 10);
                    if (roll < 2 && live.Count > 20)
                    {
                        var index = rnd.NextInt(0, live.Count);
                        Assert.IsTrue(tree.Remove(live[index]));
                        live.RemoveAt(index);
                    }
                    else if (roll < 4 && live.Count > 20)
                    {
                        var index = rnd.NextInt(0, live.Count);
                        var pos = new float3(rnd.NextFloat(-59f, 59f), rnd.NextFloat(-59f, 59f),
                                             rnd.NextFloat(-59f, 59f));
                        Assert.IsTrue(tree.Update(live[index], pos));
                    }
                    else
                    {
                        var pos = new float3(rnd.NextFloat(-59f, 59f), rnd.NextFloat(-59f, 59f),
                                             rnd.NextFloat(-59f, 59f));
                        var reference = tree.Insert(E(pos, nextId++));
                        Assert.IsTrue(reference.IsValid);
                        live.Add(reference);
                    }
                }

                Assert.AreEqual(live.Count, tree.ElementCount);

                var collected = new NativeList<SpatialElement>(Allocator.Temp);
                try
                {
                    tree.CollectElements(collected);
                    Assert.AreEqual(live.Count, collected.Length, "CollectElements 应恰好等于存活数");
                }
                finally { collected.Dispose(); }

                tree.RangeQuery(new AABB(float3.zero, new float3(140f)), ref hits);
                Assert.AreEqual(live.Count, hits.Length, "全范围查询应恰好命中所有存活元素");
                Assert.AreEqual(live.Count, hits.AsArray().Select(e => e.Entity).Distinct().Count(), "不应有重复");
            }
            finally { tree.Dispose(); hits.Dispose(); }
        }
    }

    /// <summary>包围盒计算与边界语义：八叉树三个轴都参与。</summary>
    public class OctreeBoundsTests
    {
        static NativeArray<float3> Positions(params float3[] points)
        {
            var array = new NativeArray<float3>(points.Length, Allocator.Temp);
            for (var i = 0; i < points.Length; i++) array[i] = points[i];
            return array;
        }

        static SpatialElement E(float3 pos, int id) => new SpatialElement
        {
            Pos = pos,
            Entity = new Unity.Entities.Entity { Index = id + 1, Version = 1 },
        };

        [Test]
        public void CalculateBounds_覆盖所有点且外扩padding()
        {
            var positions = Positions(new float3(-10f, -20f, 5f), new float3(30f, 0f, 45f));
            try
            {
                var bounds = NativeSpatialTree.CalculateBounds(positions);
                Assert.AreEqual(new float3(10f, -10f, 25f), bounds.Center);
                Assert.AreEqual(new float3(20f, 10f, 20f), bounds.Extents, "三个轴都要参与（与四叉树的关键差异）");

                for (var i = 0; i < positions.Length; i++)
                    Assert.IsTrue(bounds.Contains(positions[i]), "算出来的盒必须覆盖每一个输入点（闭区间）");

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
            var invalid = Positions(new float3(float.NaN, 0f, 0f), new float3(0f, float.PositiveInfinity, 0f));
            try
            {
                var fromEmpty = NativeSpatialTree.CalculateBounds(empty);
                Assert.AreEqual(float3.zero, fromEmpty.Center);
                Assert.IsTrue(math.all(fromEmpty.Extents > 0f), "必须给正体积，否则没法建树");

                var fromInvalid = NativeSpatialTree.CalculateBounds(invalid, padding: 3f);
                Assert.AreEqual(new float3(3f), fromInvalid.Extents, "外扩量应当体现在结果里");

                // 直接拿去建树不该抛
                var tree = new NativeSpatialTree(SpatialIndexKind.Octree, fromInvalid, Allocator.Temp);
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
            var positions = Positions(new float3(1f, 2f, 3f), new float3(float.NaN, 0f, 0f),
                                      new float3(5f, 6f, 7f), new float3(0f, 0f, float.NegativeInfinity));
            try
            {
                var bounds = NativeSpatialTree.CalculateBounds(positions);
                Assert.AreEqual(new float3(3f, 4f, 5f), bounds.Center);
                Assert.AreEqual(new float3(2f, 2f, 2f), bounds.Extents);
            }
            finally
            {
                positions.Dispose();
            }
        }

        [Test]
        public void 边界上的点_闭区间且只进一个叶()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, new AABB { Center = float3.zero, Extents = new float3(10f) },
                                        Allocator.Temp, 3, 2);
            var hits = new NativeList<SpatialElement>(Allocator.Temp);
            try
            {
                var corner = tree.Insert(E(new float3(10f, 10f, 10f), 0));
                Assert.IsTrue(corner.IsValid, "正好落在 Max 角上的点属于索引范围（闭区间）");
                Assert.AreEqual(0, tree.OutOfBoundsRejected);

                tree.RangeQuery(new AABB { Center = new float3(10f, 10f, 10f), Extents = new float3(0.5f) }, ref hits);
                Assert.AreEqual(1, hits.Length, "贴角的查询盒也应命中它");

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
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, new AABB(float3.zero, new float3(20f)), Allocator.Temp, 3, 2);
            var input = new NativeArray<SpatialElement>(3, Allocator.Temp);
            try
            {
                input[0] = E(new float3(0f, 0f, 0f), 0);
                input[1] = E(new float3(0f, 0f, 100f), 1);
                input[2] = E(new float3(float.NaN, 0f, 0f), 2);

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
