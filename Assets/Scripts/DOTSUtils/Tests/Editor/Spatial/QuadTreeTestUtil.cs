using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace DOTSUtils.Spatial.Tests
{
    /// <summary>四叉树测试夹具：造元素、暴力查询、集合比较。</summary>
    public static class QuadTreeTestUtil
    {
        public const float k_HalfExtent = 100f;

        /// <summary>默认索引范围：[-100, 100] × [-100, 100]（xz 平面）。</summary>
        public static AABB2D DefaultBounds => new AABB2D(float2.zero, k_HalfExtent);

        public static SpatialElement MakeElement(float3 position, int id) => new SpatialElement
        {
            Pos = position,
            Entity = new Entity { Index = id + 1, Version = 1 },
        };

        public static SpatialElement[] MakeRandomElements(int count, float halfExtent, uint seed)
        {
            var random = new Random(seed);
            var result = new SpatialElement[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = MakeElement(
                    new float3(random.NextFloat(-halfExtent, halfExtent), 0f, random.NextFloat(-halfExtent, halfExtent)), i);
            }

            return result;
        }

        public static NativeArray<SpatialElement> ToNative(IReadOnlyList<SpatialElement> source, Allocator allocator)
        {
            var array = new NativeArray<SpatialElement>(source.Count, allocator);
            for (var i = 0; i < source.Count; i++) array[i] = source[i];
            return array;
        }

        public static List<SpatialElement> BruteForceAabb(IReadOnlyList<SpatialElement> elements, AABB2D query)
            => elements.Where(e => query.Contains(e.Pos.xz)).ToList();

        public static List<SpatialElement> BruteForceCircle(IReadOnlyList<SpatialElement> elements, float2 center, float radius)
            => elements.Where(e => math.distancesq(e.Pos.xz, center) <= radius * radius).ToList();

        /// <summary>按 Entity 排序后比较集合。</summary>
        public static void AssertSameSet(IReadOnlyList<SpatialElement> expected, IReadOnlyList<SpatialElement> actual,
                                         string message = null)
        {
            var expectedIds = expected.Select(e => e.Entity).OrderBy(e => e.Index).ThenBy(e => e.Version).ToArray();
            var actualIds = actual.Select(e => e.Entity).OrderBy(e => e.Index).ThenBy(e => e.Version).ToArray();
            CollectionAssert.AreEqual(expectedIds, actualIds, message);
        }

        public static void AssertSameEntities(IEnumerable<Entity> expected, IEnumerable<Entity> actual, string message = null)
        {
            CollectionAssert.AreEqual(
                expected.OrderBy(e => e.Index).ThenBy(e => e.Version).ToArray(),
                actual.OrderBy(e => e.Index).ThenBy(e => e.Version).ToArray(),
                message);
        }
    }
}
