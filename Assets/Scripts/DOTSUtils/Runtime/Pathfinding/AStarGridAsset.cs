using Unity.Mathematics;
using UnityEngine;

namespace DOTSUtils.Pathfinding
{
    /// <summary>
    /// A* 网格资产：保存网格参数与逐格「可走 / 阻挡」掩码，供编辑器窗口编辑。
    /// 掩码是行主序的一维数组，<c>index = z * Size.x + x</c>，非 0 表示可走，<c>null</c> 表示全部可走。
    /// </summary>
    [CreateAssetMenu(fileName = "AStarGridAsset", menuName = "DOTSUtils/Pathfinding/A* 网格资产")]
    public sealed class AStarGridAsset : ScriptableObject
    {
        [Tooltip("网格范围中心（只用 xz；格子中心的 y 取这里的 y）。")]
        [SerializeField] private Vector3 m_BoundsCenter = Vector3.zero;

        [Tooltip("网格范围半长（只用 xz）。")]
        [SerializeField] private Vector3 m_BoundsExtents = new Vector3(20f, 1f, 20f);

        [Tooltip("格子边长（世界单位）。")]
        [SerializeField] private float m_CellSize = 2f;

        [Tooltip("4 邻或 8 邻。")]
        [SerializeField] private AStarNeighbourhood m_Neighbourhood = AStarNeighbourhood.Eight;

        [Tooltip("8 邻时是否允许贴角穿过。")]
        [SerializeField] private bool m_AllowCornerCutting;

        [Tooltip("用哪种算法：A* 或 Dijkstra。")]
        [SerializeField] private PathfindingAlgorithm m_Algorithm = PathfindingAlgorithm.AStar;

        [Tooltip("启发式权重，>= 1；只对 A* 有效。")]
        [SerializeField] private float m_HeuristicWeight = 1f;

        [Tooltip("单次寻路最多访问多少格；<= 0 表示用网格总格数。")]
        [SerializeField] private int m_MaxVisitedNodes;

        [Tooltip("每帧最多解多少个请求；<= 0 表示不限。")]
        [SerializeField] private int m_MaxSolvesPerFrame;

        [Tooltip("是否对结果做视线拉直。")]
        [SerializeField] private bool m_SmoothPath;

        [Tooltip("可走掩码，行主序，非 0 可走；长度 = Width * Height。")]
        [SerializeField] private byte[] m_Mask;

        [Tooltip("掩码宽度（格，对应世界 x）。")]
        [SerializeField] private int m_Width = 1;

        [Tooltip("掩码高度（格，对应世界 z）。")]
        [SerializeField] private int m_Height = 1;

        /// <summary>网格范围中心。</summary>
        public Vector3 BoundsCenter
        {
            get => m_BoundsCenter;
            set => m_BoundsCenter = value;
        }

        /// <summary>网格范围半长。</summary>
        public Vector3 BoundsExtents
        {
            get => m_BoundsExtents;
            set => m_BoundsExtents = value;
        }

        /// <summary>格子边长。</summary>
        public float CellSize
        {
            get => m_CellSize;
            set => m_CellSize = value;
        }

        /// <summary>4 邻或 8 邻。</summary>
        public AStarNeighbourhood Neighbourhood
        {
            get => m_Neighbourhood;
            set => m_Neighbourhood = value;
        }

        /// <summary>8 邻时是否允许贴角穿过。</summary>
        public bool AllowCornerCutting
        {
            get => m_AllowCornerCutting;
            set => m_AllowCornerCutting = value;
        }

        /// <summary>用哪种算法。</summary>
        public PathfindingAlgorithm Algorithm
        {
            get => m_Algorithm;
            set => m_Algorithm = value;
        }

        /// <summary>启发式权重（只对 A* 有效）。</summary>
        public float HeuristicWeight
        {
            get => m_HeuristicWeight;
            set => m_HeuristicWeight = value;
        }

        /// <summary>单次寻路最多访问多少格；&lt;= 0 表示用网格总格数。</summary>
        public int MaxVisitedNodes
        {
            get => m_MaxVisitedNodes;
            set => m_MaxVisitedNodes = value;
        }

        /// <summary>每帧最多解多少个请求；&lt;= 0 表示不限。</summary>
        public int MaxSolvesPerFrame
        {
            get => m_MaxSolvesPerFrame;
            set => m_MaxSolvesPerFrame = value;
        }

        /// <summary>是否对结果做视线拉直。</summary>
        public bool SmoothPath
        {
            get => m_SmoothPath;
            set => m_SmoothPath = value;
        }

        /// <summary>可走掩码（行主序，非 0 可走）；可读可写。</summary>
        public byte[] Mask
        {
            get => m_Mask;
            set => m_Mask = value;
        }

        /// <summary>掩码尺寸（格）。</summary>
        public int2 Size => new int2(m_Width, m_Height);

        /// <summary>掩码格数。</summary>
        public int CellCount => m_Width * m_Height;

        /// <summary>可走格数；掩码为空时等于格数。</summary>
        public int WalkableCount
        {
            get
            {
                if (m_Mask == null)
                {
                    return CellCount;
                }

                var count = 0;
                for (var i = 0; i < m_Mask.Length; i++)
                {
                    if (m_Mask[i] != 0)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        /// <summary>按当前参数算出应有的掩码尺寸（宽高至少 1）。</summary>
        public int2 ComputeSize()
        {
            return ComputeSize(ToConfig());
        }

        /// <summary>把参数与掩码组装成运行时参数。</summary>
        public AStarConfig ToConfig()
        {
            return new AStarConfig
            {
                Bounds = new AABB
                {
                    Center = m_BoundsCenter,
                    Extents = m_BoundsExtents,
                },
                CellSize = m_CellSize,
                Neighbourhood = m_Neighbourhood,
                AllowCornerCutting = m_AllowCornerCutting,
                Algorithm = m_Algorithm,
                HeuristicWeight = m_HeuristicWeight,
                MaxVisitedNodes = m_MaxVisitedNodes,
                MaxSolvesPerFrame = m_MaxSolvesPerFrame,
                SmoothPath = m_SmoothPath,
            };
        }

        /// <summary>把运行时参数写回资产（不动掩码）。</summary>
        public void ApplyConfig(in AStarConfig config)
        {
            m_BoundsCenter = config.Bounds.Center;
            m_BoundsExtents = config.Bounds.Extents;
            m_CellSize = config.CellSize;
            m_Neighbourhood = config.Neighbourhood;
            m_AllowCornerCutting = config.AllowCornerCutting;
            m_Algorithm = config.Algorithm;
            m_HeuristicWeight = config.HeuristicWeight;
            m_MaxVisitedNodes = config.MaxVisitedNodes;
            m_MaxSolvesPerFrame = config.MaxSolvesPerFrame;
            m_SmoothPath = config.SmoothPath;
        }

        /// <summary>
        /// 按当前参数核对掩码尺寸：尺寸不符时重建掩码，旧掩码按格对齐搬过去，
        /// 新增的格默认可走，并更新 <c>Width</c> / <c>Height</c>。
        /// </summary>
        public void ResizeIfNeeded()
        {
            var size = ComputeSize(ToConfig());
            var width = math.max(1, size.x);
            var height = math.max(1, size.y);
            var cells = width * height;

            if (m_Mask != null && m_Width == width && m_Height == height && m_Mask.Length == cells)
            {
                return;
            }

            var next = new byte[cells];
            for (var i = 0; i < cells; i++)
            {
                next[i] = 1;
            }

            if (m_Mask != null && m_Width > 0 && m_Height > 0)
            {
                var copyWidth = math.min(m_Width, width);
                var copyHeight = math.min(m_Height, height);
                for (var z = 0; z < copyHeight; z++)
                {
                    for (var x = 0; x < copyWidth; x++)
                    {
                        var source = z * m_Width + x;
                        if (source < 0 || source >= m_Mask.Length)
                        {
                            continue;
                        }
                        next[z * width + x] = m_Mask[source] != 0 ? (byte)1 : (byte)0;
                    }
                }
            }

            m_Mask = next;
            m_Width = width;
            m_Height = height;
        }

        /// <summary>把当前参数与掩码副本交给 <see cref="AStarSettings"/>。</summary>
        public void PushToSettings()
        {
            var mask = m_Mask != null ? (byte[])m_Mask.Clone() : null;
            AStarSettings.Configure(ToConfig(), mask);
        }

        /// <summary>该格是否可走；越界返回 false。</summary>
        public bool IsWalkable(int2 cell)
        {
            if (cell.x < 0 || cell.y < 0 || cell.x >= m_Width || cell.y >= m_Height)
            {
                return false;
            }
            if (m_Mask == null)
            {
                return true;
            }

            var index = cell.y * m_Width + cell.x;
            return index < m_Mask.Length && m_Mask[index] != 0;
        }

        /// <summary>设置某格可走 / 阻挡；越界不做事。</summary>
        public void SetWalkable(int2 cell, bool walkable)
        {
            if (cell.x < 0 || cell.y < 0 || cell.x >= m_Width || cell.y >= m_Height)
            {
                return;
            }

            EnsureMask();
            var index = cell.y * m_Width + cell.x;
            if (index >= 0 && index < m_Mask.Length)
            {
                m_Mask[index] = walkable ? (byte)1 : (byte)0;
            }
        }

        /// <summary>把全部格设为同一个值。</summary>
        public void Fill(bool walkable)
        {
            ResizeIfNeeded();
            EnsureMask();
            for (var i = 0; i < m_Mask.Length; i++)
            {
                m_Mask[i] = walkable ? (byte)1 : (byte)0;
            }
        }

        /// <summary>新建一份默认参数的资产实例（只建在内存里，落盘由调用方处理）。</summary>
        public static AStarGridAsset CreateDefault()
        {
            var asset = CreateInstance<AStarGridAsset>();
            asset.Fill(true);
            asset.ResizeIfNeeded();
            return asset;
        }

        private static int2 ComputeSize(in AStarConfig config)
        {
            var cellSize = math.max(config.CellSize, 1e-3f);
            var full = config.Bounds.Extents * 2f;
            return new int2(
                math.max(1, (int)math.ceil(full.x / cellSize)),
                math.max(1, (int)math.ceil(full.z / cellSize)));
        }

        private void EnsureMask()
        {
            var cells = math.max(1, m_Width) * math.max(1, m_Height);
            if (m_Mask != null && m_Mask.Length == cells)
            {
                return;
            }

            var next = new byte[cells];
            for (var i = 0; i < cells; i++)
            {
                next[i] = 1;
            }

            if (m_Mask != null)
            {
                var copy = math.min(m_Mask.Length, cells);
                for (var i = 0; i < copy; i++)
                {
                    next[i] = m_Mask[i];
                }
            }

            m_Mask = next;
        }
    }
}
