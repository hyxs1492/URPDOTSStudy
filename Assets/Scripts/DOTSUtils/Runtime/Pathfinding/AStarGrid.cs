using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace DOTSUtils.Pathfinding
{
    /// <summary>
    /// XZ 平面上的均匀方格网格：参数 + 每格「可走 / 阻挡」掩码。
    ///
    /// 槽位与 <c>SpatialIndexReader</c> 同思路：内部是 <see cref="UnsafeList{T}"/>，按值拷贝时共享内存，
    /// 所以副本只读、写回要走单例组件。
    /// </summary>
    public struct AStarGrid : IDisposable
    {
        private AABB m_Bounds;
        private float m_CellSize;
        private int2 m_Size;
        private AStarNeighbourhood m_Neighbourhood;
        private bool m_AllowCornerCutting;
        private bool m_IsCreated;

        /// <summary>每格 1 字节：非 0 = 可走，0 = 阻挡。行主序，<c>index = z * Size.x + x</c>。</summary>
        internal UnsafeList<byte> m_Walkable;

        /// <summary>网格覆盖范围（只取 xz）。</summary>
        public AABB Bounds => m_Bounds;

        /// <summary>格子边长（世界单位）。</summary>
        public float CellSize => m_CellSize;

        /// <summary>格子数（x 向右、y 对应世界 z）。</summary>
        public int2 Size => m_Size;

        /// <summary>4 邻或 8 邻。</summary>
        public AStarNeighbourhood Neighbourhood => m_Neighbourhood;

        /// <summary>8 邻时是否允许贴角穿过。</summary>
        public bool AllowCornerCutting => m_AllowCornerCutting;

        /// <summary>是否已创建。</summary>
        public bool IsCreated => m_IsCreated;

        /// <summary>总格数。</summary>
        public int CellCount => m_Size.x * m_Size.y;

        /// <summary>网格最小角（xz）。</summary>
        public float3 Min => new float3(m_Bounds.Center.x - m_Bounds.Extents.x, m_Bounds.Center.y,
                                        m_Bounds.Center.z - m_Bounds.Extents.z);

        /// <summary>可走格数。</summary>
        public int WalkableCount
        {
            get
            {
                var count = 0;
                for (var i = 0; i < m_Walkable.Length; i++)
                {
                    if (m_Walkable[i] != 0)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        /// <summary>
        /// 按参数与掩码建网格。<paramref name="mask"/> 为 <c>null</c> 时全部可走；
        /// 长度不足的部分保持不可走。
        /// </summary>
        public AStarGrid(in AStarConfig config, byte[] mask, Allocator allocator)
        {
            m_Bounds = config.Bounds;
            m_CellSize = math.max(config.CellSize, 1e-3f);
            m_Neighbourhood = config.Neighbourhood;
            m_AllowCornerCutting = config.AllowCornerCutting;

            var full = config.Bounds.Extents * 2f;
            m_Size = new int2(
                math.max(1, (int)math.ceil(full.x / m_CellSize)),
                math.max(1, (int)math.ceil(full.z / m_CellSize)));

            var cells = m_Size.x * m_Size.y;
            m_Walkable = new UnsafeList<byte>(cells, allocator);
            m_Walkable.Resize(cells, NativeArrayOptions.ClearMemory);

            for (var i = 0; i < cells; i++)
            {
                m_Walkable[i] = mask != null && i < mask.Length ? (mask[i] != 0 ? (byte)1 : (byte)0) : (byte)(mask == null ? 1 : 0);
            }

            m_IsCreated = true;
        }

        public void Dispose()
        {
            if (!m_IsCreated)
            {
                return;
            }
            m_IsCreated = false;
            m_Walkable.Dispose();
            m_Walkable = default;
        }

        /// <summary>把世界坐标换算成格坐标（向下取整）。</summary>
        public int2 WorldToCell(float3 world)
        {
            var min = Min;
            return new int2(
                (int)math.floor((world.x - min.x) / m_CellSize),
                (int)math.floor((world.z - min.z) / m_CellSize));
        }

        /// <summary>格中心的世界坐标（y 取 <c>Bounds.Center.y</c>）。</summary>
        public float3 CellCenter(int2 cell)
        {
            var min = Min;
            return new float3(
                min.x + (cell.x + 0.5f) * m_CellSize,
                m_Bounds.Center.y,
                min.z + (cell.y + 0.5f) * m_CellSize);
        }

        /// <summary>格坐标是否在网格内。</summary>
        public bool InBounds(int2 cell) =>
            cell.x >= 0 && cell.y >= 0 && cell.x < m_Size.x && cell.y < m_Size.y;

        /// <summary>格坐标 → 一维下标；越界返回 -1。</summary>
        public int Index(int2 cell) => InBounds(cell) ? cell.y * m_Size.x + cell.x : -1;

        /// <summary>一维下标 → 格坐标。</summary>
        public int2 Cell(int index) => new int2(index % m_Size.x, index / m_Size.x);

        /// <summary>该一维下标是否可走。</summary>
        public bool IsWalkable(int index) => index >= 0 && index < m_Walkable.Length && m_Walkable[index] != 0;

        /// <summary>该格是否可走。</summary>
        public bool IsWalkable(int2 cell) => IsWalkable(Index(cell));

        /// <summary>世界坐标处的格是否可走（越界算不可走）。</summary>
        public bool IsWalkableWorld(float3 world) => IsWalkable(WorldToCell(world));

        /// <summary>设置某格可走 / 阻挡。</summary>
        public void SetWalkable(int2 cell, bool walkable)
        {
            var index = Index(cell);
            if (index >= 0)
            {
                m_Walkable[index] = walkable ? (byte)1 : (byte)0;
            }
        }

        /// <summary>把全部格设为同一个值。</summary>
        public void Fill(bool walkable)
        {
            for (var i = 0; i < m_Walkable.Length; i++)
            {
                m_Walkable[i] = walkable ? (byte)1 : (byte)0;
            }
        }

        /// <summary>导出可走掩码（行主序副本）。</summary>
        public byte[] ToMask()
        {
            var mask = new byte[m_Walkable.Length];
            for (var i = 0; i < m_Walkable.Length; i++)
            {
                mask[i] = m_Walkable[i];
            }
            return mask;
        }
    }
}
