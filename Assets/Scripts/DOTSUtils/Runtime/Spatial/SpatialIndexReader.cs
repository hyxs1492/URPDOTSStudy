using Unity.Collections;
using Unity.Mathematics;

namespace DOTSUtils.Spatial
{
    /// <summary>
    /// 空间索引的**统一只读视图**：拿到它，就等于拿到「这个 World 维护的那棵树」。
    ///
    /// 查询自动按维度分派（四叉树只看 xz、八叉树看 xyz），分派发生在
    /// <see cref="NativeSpatialTree"/> 内部（<see cref="Kind"/> 是那棵树的模式）。
    ///
    /// 语义：里面是**拷贝**（内核的 <c>UnsafeList</c> 指向同一块内存），**只读**。
    /// </summary>
    public struct SpatialIndexReader
    {
        /// <summary>
        /// 有没有可用的索引（没装 Spatial / 树还没建 → false）。
        /// </summary>
        public bool IsCreated;

        /// <summary>
        /// 背后是四叉树还是八叉树。
        /// </summary>
        public SpatialIndexKind Kind;

        public int ElementCount;
        public int NodeCount;
        public int LeafCount;
        public int MaxDepth;

        /// <summary>
        /// 累计被拒绝的元素数（越界/NaN）。
        /// </summary>
        public int OutOfBoundsRejected;

        public float3 BoundsCenter;
        public float3 BoundsExtents;

        private NativeSpatialTree m_Tree;

        internal SpatialIndexReader(in NativeSpatialTree tree)
        {
            IsCreated = tree.IsCreated;
            Kind = tree.Kind;
            ElementCount = tree.ElementCount;
            NodeCount = tree.NodeCount;
            LeafCount = tree.LeafCount;
            MaxDepth = tree.MaxDepth;
            OutOfBoundsRejected = tree.OutOfBoundsRejected;
            BoundsCenter = tree.Bounds.Center;
            BoundsExtents = tree.Bounds.Extents;
            m_Tree = tree;
        }

        /// <summary>
        /// 最近的若干条（按距离升序、同距离按 <c>Entity.Index</c>），写进 <paramref name="results"/>，
        /// 返回实际写入条数（≤ <c>results.Length</c>）。四叉树只看 xz、八叉树看 xyz。
        /// </summary>
        public int RangeQueryNearest(float3 center, float radius, NativeArray<SpatialElement> results)
            => IsCreated ? m_Tree.RangeQueryNearest(center, radius, results) : 0;
    }
}
