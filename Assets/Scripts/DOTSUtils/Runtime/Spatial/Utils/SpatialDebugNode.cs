namespace DOTSUtils.Spatial
{
    /// <summary>
    /// 可视化取数：空间索引的一个节点（包围盒 + 深度 + 元素数）。编辑器窗口与运行时 Gizmos 共用它。
    /// </summary>
    public struct SpatialDebugNode
    {
        public AABB Bounds;
        public int Depth;
        public short Count;
        public bool IsLeaf;
    }
}
