using Unity.Collections;
using Unity.Mathematics;

namespace DOTSUtils.Spatial
{
    public partial struct NativeSpatialTree
    {
        /// <summary>
        /// AABB 范围查询，结果追加到 <paramref name="results"/>（<b>调用方持有缓冲</b>）。
        /// 结果顺序不保证、无重复。
        /// </summary>
        public void RangeQuery(AABB bounds, ref NativeList<SpatialElement> results)
        {
            ThrowIfNotCreated();
            QueryRecursive(m_Bounds, false, 0, 0, bounds, ref results, false, default, 0f);
        }

        /// <summary>
        /// 球形范围查询。<paramref name="computeDistanceSq"/> 为真时把到球心的距离平方写进结果的
        /// <c>distancesq</c> 字段。
        /// </summary>
        public void RangeQuery(float3 center, float radius, ref NativeList<SpatialElement> results,
                               bool computeDistanceSq = false)
        {
            ThrowIfNotCreated();
            var bounds = MakeQueryBox(center, radius);
            QueryRecursive(m_Bounds, false, 0, 0, bounds, ref results, computeDistanceSq, center, radius);
        }

        /// <summary>
        /// 球/盒查询用的包围盒。<b>四叉树模式只在 xz 平面索引</b>，xz 之外的那一轴在包围盒里放开。
        /// </summary>
        private AABB MakeQueryBox(float3 center, float radius)
        {
            var extents = new float3(radius * 2f);
            if (m_Kind == SpatialIndexKind.Quadtree)
            {
                extents.y = float.MaxValue;
            }
            return new AABB(center, extents);
        }

        /// <summary>
        /// 两点距离平方 —— 四叉树模式下只比 xz（它索引的就是 xz）。
        /// </summary>
        private float DistanceSq(float3 a, float3 b) => m_Kind == SpatialIndexKind.Quadtree
            ? math.distancesq(new float2(a.x, a.z), new float2(b.x, b.z))
            : math.distancesq(a, b);

        /// <summary>便捷重载：按给定 allocator 新建结果表并返回（内部不 Dispose，由调用方负责）。
        /// 只适合主线程/编辑器代码；Job 内请用调用方缓冲的重载。</summary>
        public NativeList<SpatialElement> RangeQuery(AABB bounds, Allocator allocator)
        {
            var results = new NativeList<SpatialElement>(allocator);
            RangeQuery(bounds, ref results);
            return results;
        }

        private void QueryRecursive(AABB nodeBounds, bool contained, int atIndex, int depth, AABB query,
                            ref NativeList<SpatialElement> results, bool computeDistanceSq, float3 center, float radius)
        {
            if (depth > 0)
            {
                var node = m_Nodes[atIndex];
                if (node.IsLeaf)
                {
                    var radiusSq = radius * radius;
                    var guard = m_ElementCount + 1;
                    for (var slot = node.Head; slot != -1; slot = m_Next[slot])
                    {
                        if (--guard <= 0)
                            throw new System.InvalidOperationException(
                                $"叶 {atIndex} 的链表疑似成环（范围查询遍历超过活元素数）");

                        var element = m_Elements[slot];
                        if (!contained && !query.Contains(element.Pos))
                        {
                            continue;
                        }

                        if (radius > 0f)
                        {
                            var distanceSq = DistanceSq(element.Pos, center);
                            if (distanceSq > radiusSq)
                            {
                                continue;
                            }
                            if (computeDistanceSq)
                            {
                                element.distancesq = distanceSq;
                            }
                        }

                        results.Add(element);
                    }

                    return;
                }

                if (!node.IsUsed)
                {
                    return;
                }
            }

            if (depth == m_MaxDepth)
            {
                return;
            }

            var blockSize = DepthSize(m_MaxDepth - depth);
            for (var l = 0; l < ChildCount; l++)
            {
                var childBounds = GetChildBounds(nodeBounds, l);

                var childContained = contained;
                if (!childContained)
                {
                    // AABB 没有 Contains(AABB) 重载，这里自己判「盒含盒」
                    if (ContainsBox(query, childBounds))
                    {
                        childContained = true;
                    }
                    else if (!query.Intersects(childBounds))
                    {
                        continue;
                    }
                }

                QueryRecursive(childBounds, childContained, atIndex + 1 + l * blockSize, depth + 1,
                    query, ref results, computeDistanceSq, center, radius);
            }
        }

        /// <summary>
        /// 外部盒是否完全包含内部盒（闭区间）。
        /// </summary>
        public static bool ContainsBox(in AABB outer, in AABB inner)
            => math.all(outer.Min <= inner.Min) && math.all(outer.Max >= inner.Max);

        /// <summary>
        /// 球形范围查询，但只保留**距离 <paramref name="center"/> 最近**的若干条（按距离升序、同距离按
        /// <c>Entity.Index</c> 打破平局），写进 <paramref name="results"/>，返回实际写入条数
        /// （≤ <c>results.Length</c>）。
        ///
        /// 语义与「全量 <c>RangeQuery</c> + 自己排序取前 N」等价；不分配缓冲、结果集有界，
        /// 可在 <c>IJobParallelFor</c> 里并行跑（只读树 + 只写调用方给的那一段）。
        /// </summary>
        public int RangeQueryNearest(float3 center, float radius, NativeArray<SpatialElement> results)
        {
            ThrowIfNotCreated();
            var count = 0;
            if (results.Length > 0)
            {
                var bounds = MakeQueryBox(center, radius);
                QueryNearestRecursive(m_Bounds, false, 0, 0, bounds, center, radius * radius, results, ref count);
            }

            return count;
        }

        private void QueryNearestRecursive(AABB nodeBounds, bool contained, int atIndex, int depth, AABB query,
                                   float3 center, float radiusSq, NativeArray<SpatialElement> results, ref int count)
        {
            if (depth > 0)
            {
                var node = m_Nodes[atIndex];
                if (node.IsLeaf)
                {
                    var guard = m_ElementCount + 1;
                    for (var slot = node.Head; slot != -1; slot = m_Next[slot])
                    {
                        if (--guard <= 0)
                            throw new System.InvalidOperationException(
                                $"叶 {atIndex} 的链表疑似成环（最近邻查询遍历超过活元素数）");

                        var element = m_Elements[slot];
                        if (!contained && !query.Contains(element.Pos))
                        {
                            continue;
                        }

                        var distanceSq = DistanceSq(element.Pos, center);
                        if (distanceSq > radiusSq)
                        {
                            continue;
                        }

                        element.distancesq = distanceSq;      // 结果里带上距离，InsertNearest 直接比它
                        InsertNearest(results, ref count, element, distanceSq);
                    }

                    return;
                }

                if (!node.IsUsed)
                {
                    return;
                }
            }

            if (depth == m_MaxDepth)
            {
                return;
            }

            var blockSize = DepthSize(m_MaxDepth - depth);
            for (var l = 0; l < ChildCount; l++)
            {
                var childBounds = GetChildBounds(nodeBounds, l);

                var childContained = contained;
                if (!childContained)
                {
                    if (ContainsBox(query, childBounds))
                    {
                        childContained = true;
                    }
                    else if (!query.Intersects(childBounds))
                    {
                        continue;
                    }
                }

                QueryNearestRecursive(childBounds, childContained, atIndex + 1 + l * blockSize, depth + 1,
                    query, center, radiusSq, results, ref count);
            }
        }

        /// <summary>
        /// 把一条命中插进「按距离升序、同距离按 <c>Entity.Index</c>」的有界结果集
        /// （容量 = <paramref name="results"/>.Length）。满了之后比最远那条还远的直接丢。
        /// </summary>
        private static void InsertNearest(NativeArray<SpatialElement> results, ref int count, in SpatialElement element,
                                  float distanceSq)
        {
            var capacity = results.Length;
            if (capacity == 0)
            {
                return;
            }

            var limit = math.min(count, capacity);
            var index = limit;
            while (index > 0)
            {
                var previous = results[index - 1];
                var previousDistanceSq = previous.distancesq;
                if (distanceSq < previousDistanceSq ||
                    (distanceSq == previousDistanceSq && element.Entity.Index < previous.Entity.Index))
                {
                    index--;
                    continue;
                }

                break;
            }

            if (index >= capacity)
            {
                return; // 比容量内的都远：丢弃
            }

            var top = math.min(limit, capacity - 1);
            for (var i = top; i > index; i--)
            {
                results[i] = results[i - 1];
            }
            results[index] = element;
            count = math.min(count + 1, capacity);
        }
    }
}
