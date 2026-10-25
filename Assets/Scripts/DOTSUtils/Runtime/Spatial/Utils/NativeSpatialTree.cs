using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace DOTSUtils.Spatial
{
    /// <summary>
    /// <b>空间索引树</b>：可增量更新、Burst 友好；<b>四叉树与八叉树是同一个类型的两种模式</b>
    /// （由 <see cref="Kind"/> 决定）。
    ///
    /// 两者只差三处，都在这个文件里就地分派：<see cref="MortonCode"/>（4 叉是 xz 两位交错、世界 z 翻转，
    /// 8 叉是 xyz 三位交错）、<see cref="IncrementIndex"/>（每层 2 位 vs 3 位）、<see cref="GetChildBounds"/>
    /// （4 个子盒、编号 0/1/2/3 = 世界空间的左上/右上/左下/右下，vs 8 个、位含义 = 该轴正方向）。
    /// 其余（节点布局、元素池、分裂/合并、bulk、Collect*）两侧相同，只通过 <see cref="ChildCount"/> /
    /// <see cref="DepthSize"/> 两个参数化点区分。
    ///
    /// 布局与不变式：节点用隐式路径存于连续数组，分裂/合并只改标记、不搬动元素；元素存在池里，
    /// 每个叶子持一条单元素链表（<c>m_Next</c> 存池内下标）；池槽位用 <c>m_FreeSlots</c> 复用，
    /// <c>m_Version</c> 识别陈旧句柄；<b>空链哨兵是 -1</b>，任何复位都必须用 <see cref="SpatialNode.Empty"/>，
    /// 不要用 <c>default</c>。
    ///
    /// 线程约定：读（查询 / Collect*）可多 job 并行；写必须单线程。
    /// 容器一律 <see cref="UnsafeList{T}"/>（值语义、无 safety handle），整个类型<b>不需要 unsafe 上下文</b>。
    /// </summary>
    public partial struct NativeSpatialTree : IDisposable
    {
        /// <summary>
        /// 最大深度上限 = 7（按 8 叉算：每轴 8 位 ⇒ 24 位 morton）。
        /// </summary>
        public const int MaxSupportedDepth = 7;

        internal const short k_FlagLeaf = 1;
        internal const short k_FlagUsed = 2;

        internal struct SpatialNode
        {
            public int Head;     // 链表头（池内下标），-1 表示空
            public short Count;
            public short Flags;

            public bool IsLeaf => (Flags & k_FlagLeaf) != 0;
            public bool IsUsed => (Flags & k_FlagUsed) != 0;

            /// <summary>
            /// 「未使用、空链」的节点值。永远用它复位，不要写 <c>default</c>。
            /// </summary>
            public static SpatialNode Empty => new SpatialNode
            {
                Head = -1,
                Count = 0,
                Flags = 0,
            };
        }

        internal UnsafeList<SpatialNode> m_Nodes;
        internal UnsafeList<SpatialElement> m_Elements;
        internal UnsafeList<int> m_Next;
        internal UnsafeList<int> m_Version;
        internal UnsafeList<int> m_FreeSlots;

        /// <summary>
        /// 「该节点被用过」的位图（每节点 1 bit）—— <b>只用来给 <see cref="m_UsedNodes"/> 去重</b>，
        /// 让「同一节点在一次 Clear 周期内只被登记一次」（标记是幂等的）。复位枚举走 <see cref="m_UsedNodes"/>。
        /// </summary>
        internal UnsafeList<byte> m_UsedBits;

        /// <summary>
        /// 本周期内被用过的节点索引（<b>不重复</b>，由 <see cref="m_UsedBits"/> 保证）。
        /// <see cref="Clear"/> 只遍历它复位。上界是「用过一次的全部节点数」，不随运行时间无限增长。
        /// </summary>
        internal UnsafeList<int> m_UsedNodes;

        /// <summary>
        /// bulk 构建用的「每个节点被多少元素穿过」（长度 = <c>NodeCapacity</c>）。
        /// <b>常驻复用</b>，每轮只清「本轮动过的」那部分（<see cref="m_TouchedCounts"/>）。
        /// </summary>
        internal UnsafeList<int> m_Counts;

        /// <summary>
        /// 本轮 bulk 累加过 <see cref="m_Counts"/> 的节点（允许重复），用来把它清回 0。
        /// </summary>
        internal UnsafeList<int> m_TouchedCounts;

        /// <summary>
        /// 这份树是四叉树还是八叉树 —— 唯一的模式开关。
        /// </summary>
        private SpatialIndexKind m_Kind;

        private AABB m_Bounds;
        private int m_MaxDepth;
        private short m_MaxLeafElements;
        private int m_NodeCapacity;
        private int m_NextFreeSlot;

        private int m_ElementCount;
        private int m_LeafCount;
        private int m_SplitNodeCount;
        private int m_OutOfBoundsRejected;
        private bool m_IsCreated;

        public bool IsCreated => m_IsCreated;

        /// <summary>
        /// 四叉树还是八叉树。
        /// </summary>
        public SpatialIndexKind Kind => m_Kind;

        /// <summary>
        /// 每个内部节点的子节点数：四叉树 4、八叉树 8。
        /// </summary>
        public int ChildCount => m_Kind == SpatialIndexKind.Quadtree ? 4 : 8;

        /// <summary>
        /// morton 每层占的位数：四叉树 2、八叉树 3。
        /// </summary>
        private int BitsPerLevel => m_Kind == SpatialIndexKind.Quadtree ? 2 : 3;

        /// <summary>
        /// 取「本层子序号」用的掩码：四叉树 0b11、八叉树 0b111。
        /// </summary>
        private int ChildMask => ChildCount - 1;

        /// <summary>
        /// 深度 <paramref name="d"/> 的子树节点数（含自身）—— 两棵树各查各的表。
        /// </summary>
        private int DepthSize(int d) => m_Kind == SpatialIndexKind.Quadtree
            ? SpatialLookupTables.DepthSize4[d]
            : SpatialLookupTables.DepthSize8[d];

        public AABB Bounds => m_Bounds;
        public int MaxDepth => m_MaxDepth;
        public int MaxLeafElements => m_MaxLeafElements;
        public int ElementCount => m_ElementCount;
        public int NodeCapacity => m_NodeCapacity;
        public int NodeCount => m_LeafCount + m_SplitNodeCount;
        public int LeafCount => m_LeafCount;
        public int SplitNodeCount => m_SplitNodeCount;
        public bool IsEmpty => m_ElementCount == 0;
        /// <summary>
        /// 累计被拒绝的元素数（越界 / NaN）。<c>Clear()</c> 不会重置它。
        /// </summary>
        public int OutOfBoundsRejected => m_OutOfBoundsRejected;

        /// <summary>
        /// 建树。越界元素一律被拒绝（不 clamp）。
        /// </summary>
        public NativeSpatialTree(SpatialIndexKind kind, AABB bounds, Allocator allocator = Allocator.Temp,
                            int maxDepth = 6, short maxLeafElements = 16,
                            int initialElementCapacity = 256) : this()
        {
            if (!math.all(math.isfinite(bounds.Center)) || !math.all(math.isfinite(bounds.Extents)) ||
                !math.all(bounds.Extents > 0f))
            {
                throw new ArgumentException(
                    $"bounds 必须有限且 Extents > 0（center={bounds.Center}, extents={bounds.Extents}）。", nameof(bounds));
            }

            if (maxDepth < 1 || maxDepth > MaxSupportedDepth)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth,
                    $"maxDepth 必须在 1..{MaxSupportedDepth} 之间：八叉树每轴 8 位（24 位 morton），" +
                    "再深节点数按 8^k 爆炸（maxDepth=7 的节点容量已是 239 万）。");
            }

            if (maxLeafElements < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLeafElements), maxLeafElements,
                    "maxLeafElements 必须 >= 1。");
            }

            m_Kind = kind;
            m_Bounds = bounds;
            m_MaxDepth = maxDepth;
            m_MaxLeafElements = maxLeafElements;
            m_NodeCapacity = DepthSize(maxDepth + 1);

            var elementCapacity = math.max(1, initialElementCapacity);
            m_Nodes = new UnsafeList<SpatialNode>(m_NodeCapacity, allocator);
            m_Nodes.Resize(m_NodeCapacity);
            m_Elements = new UnsafeList<SpatialElement>(elementCapacity, allocator);
            m_Elements.Resize(elementCapacity);
            m_Next = new UnsafeList<int>(elementCapacity, allocator);
            m_Next.Resize(elementCapacity);
            InitNextRange(0, elementCapacity);
            m_Version = new UnsafeList<int>(elementCapacity, allocator);
            m_Version.Resize(elementCapacity, NativeArrayOptions.ClearMemory);
            m_FreeSlots = new UnsafeList<int>(math.min(elementCapacity, 64), allocator);

            // 稀疏复位用的位图（每节点 1 bit）+ bulk 构建复用的「每节点元素数」缓冲。
            m_UsedBits = new UnsafeList<byte>((m_NodeCapacity + 7) >> 3, allocator);
            m_UsedBits.Resize((m_NodeCapacity + 7) >> 3, NativeArrayOptions.ClearMemory);
            m_UsedNodes = new UnsafeList<int>(math.min(m_NodeCapacity, 256), allocator);

            // m_Counts 先建 0 容量：只有 bulk 模式用得到，第一次走 bulk 时由 EnsureBulkCounts()
            // 一次性 Resize 到 NodeCapacity（UnsafeList 自带构造时给的 allocator）。
            m_Counts = new UnsafeList<int>(0, allocator);

            m_TouchedCounts = new UnsafeList<int>(math.min(m_NodeCapacity, 256), allocator);

            m_NextFreeSlot = 0;
            m_IsCreated = true;

            // 构造函数必须**全量**写 Empty（Head = -1）：增量插入路径（Insert）不经过 Clear()。
            // 之后每次 Clear() 才走稀疏复位。
            for (var i = 0; i < m_NodeCapacity; i++)
            {
                m_Nodes[i] = SpatialNode.Empty;
            }
        }

        /// <summary>
        /// 清空树（保留已分配容量）。<c>Version</c> 不复位，清空前的句柄在槽位复用后失配。
        /// </summary>
        public void Clear()
        {
            ThrowIfNotCreated();

            ResetNodes();
            m_FreeSlots.Clear();

            m_NextFreeSlot = 0;
            m_ElementCount = 0;
            m_LeafCount = 0;
            m_SplitNodeCount = 0;
        }

        public void ReserveElements(int capacity)
        {
            ThrowIfNotCreated();
            if (capacity <= m_Elements.Length)
            {
                return;
            }

            var previousCapacity = m_Next.Length;
            m_Elements.Resize(capacity);
            m_Next.Resize(capacity);
            InitNextRange(previousCapacity, capacity);
            m_Version.Resize(capacity, NativeArrayOptions.ClearMemory);
        }

        public void Dispose()
        {
            if (!m_IsCreated)
            {
                return;
            }

            m_IsCreated = false;
            m_Nodes.Dispose();
            m_Elements.Dispose();
            m_Next.Dispose();
            m_Version.Dispose();
            m_FreeSlots.Dispose();
            m_UsedBits.Dispose();
            m_UsedNodes.Dispose();
            // m_Counts 是惰性分配的：没走过 bulk 时它是 0 容量的空壳。
            if (m_Counts.IsCreated)
            {
                m_Counts.Dispose();
            }
            m_TouchedCounts.Dispose();

            m_Nodes = default;
            m_Elements = default;
            m_Next = default;
            m_Version = default;
            m_FreeSlots = default;
            m_UsedBits = default;
            m_UsedNodes = default;
            m_Counts = default;
            m_TouchedCounts = default;
        }

        /// <summary>
        /// 位置是否落在索引范围内（xyz，闭区间）。NaN/Inf 一律 false。
        /// </summary>
        public bool Contains(float3 position)
        {
            ThrowIfNotCreated();
            return IsPositionValid(position);
        }

        internal bool IsPositionValid(float3 position)
        {
            if (!math.all(math.isfinite(position)))
            {
                return false;
            }

            // 四叉树只在 xz 平面上索引，y 不参与范围判断。
            if (m_Kind == SpatialIndexKind.Quadtree)
            {
                return position.x >= m_Bounds.Center.x - m_Bounds.Extents.x
                       && position.x <= m_Bounds.Center.x + m_Bounds.Extents.x
                       && position.z >= m_Bounds.Center.z - m_Bounds.Extents.z
                       && position.z <= m_Bounds.Center.z + m_Bounds.Extents.z;
            }

            return m_Bounds.Contains(position);
        }

        internal void ThrowIfNotCreated()
        {
            if (!m_IsCreated)
            {
                throw new InvalidOperationException("NativeSpatialTree 未创建或已 Dispose。");
            }
        }

        /// <summary>
        /// 把节点数组复位成「未使用、空链」（必须显式写 Head = -1）。
        ///
        /// <b>稀疏</b>：只复位 <see cref="m_UsedNodes"/> 里登记过的节点。
        /// </summary>
        private void ResetNodes()
        {
            for (var i = 0; i < m_UsedNodes.Length; i++)
            {
                var index = m_UsedNodes[i];
                if (index < m_NodeCapacity)
                {
                    m_Nodes[index] = SpatialNode.Empty;
                }
                m_UsedBits[index >> 3] &= (byte)~(1 << (index & 7));   // 精确清位，别碰同字节的其它节点
            }

            m_UsedNodes.Clear();
        }

        /// <summary>
        /// 登记「该节点被用过」（幂等：位图去重，同一个节点在一个 Clear 周期内只进列表一次）。
        /// 所有把节点从「未使用」变成「使用中」的地方都要调它。
        /// </summary>
        internal void MarkUsed(int nodeIndex)
        {
            var b = nodeIndex >> 3;
            var bit = 1 << (nodeIndex & 7);
            if ((m_UsedBits[b] & bit) != 0)
            {
                return; // 本周期已登记过
            }

            m_UsedBits[b] |= (byte)bit;
            m_UsedNodes.Add(nodeIndex);
        }

        /// <summary>
        /// 把 <c>m_Next</c> 的 [from, to) 段填成空链哨兵 -1。
        /// </summary>
        private void InitNextRange(int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                m_Next[i] = -1;
            }
        }

        // ───────────────────────────── 编码与路径 ─────────────────────────────

        /// <summary>
        /// 把世界坐标编码成三维 morton 码（xyz 都参与）。
        ///
        /// 这里**不**做轴向翻转：morton 的位含义必须与 <see cref="GetChildBounds"/> 的「子节点区域」对应
        /// （第 k 层的第 b 位 = 1 ⇔ 该坐标落在本层中线的正侧）。
        /// </summary>
        public int MortonCode(float3 position)
        {
            if (m_Kind == SpatialIndexKind.Quadtree)
            {
                // ── 四叉树：只看 xz、两位交错，且世界 z 翻转（与 GetChildBounds 的子编号一致）──
                var center = new float2(m_Bounds.Center.x, m_Bounds.Center.z);
                var extents = new float2(m_Bounds.Extents.x, m_Bounds.Extents.z);
                var local2 = new float2(position.x, position.z) - center;
                local2.y = -local2.y;                                 // 世界 → 数组方向
                var normalized = (local2 + extents) * 0.5f;           // 落到 [0, extents]
                var scaled = normalized * (SpatialLookupTables.DepthLookup[m_MaxDepth] / extents);

                var maxValue = (1 << m_MaxDepth) - 1;
                var qx = math.clamp((int)scaled.x, 0, maxValue);
                var qy = math.clamp((int)scaled.y, 0, maxValue);
                return SpatialLookupTables.MortonLookup[qx] | (SpatialLookupTables.MortonLookup[qy] << 1);
            }

            // ── 八叉树：xyz 三位交错，不做任何轴向翻转 ──
            var local = position - m_Bounds.Center;
            var levels = SpatialLookupTables.LevelCountLookup[m_MaxDepth];
            var x = SpatialLookupTables.Quantize(local.x, 0f, m_Bounds.Extents.x, levels);
            var y = SpatialLookupTables.Quantize(local.y, 0f, m_Bounds.Extents.y, levels);
            var z = SpatialLookupTables.Quantize(local.z, 0f, m_Bounds.Extents.z, levels);
            return SpatialLookupTables.Morton(x, y, z);
        }

        /// <summary>
        /// 在 depth 层的节点上，按 morton 取它第 child 个子节点的索引（8 叉，每层 3 位）。
        /// </summary>
        internal int IncrementIndex(int depth, int morton, int atIndex)
        {
            var atDepth = math.max(0, m_MaxDepth - depth);
            if (m_Kind == SpatialIndexKind.Quadtree)
            {
                var quad = (morton >> ((atDepth - 1) * 2)) & 0b11;
                return atIndex + DepthSize(atDepth) * quad + 1;
            }

            var oct = (morton >> ((atDepth - 1) * 3)) & 0b111;
            return atIndex + DepthSize(atDepth) * oct + 1;
        }

        /// <summary>
        /// 从根下探到包含该 morton 的叶子。遇到「尚未使用」的节点时就地把它当作叶（只标记、不分裂）。
        /// </summary>
        internal int FindLeafIndex(int morton, out int depth)
        {
            var atIndex = 0;
            for (depth = 0; depth < m_MaxDepth; depth++)
            {
                var node = m_Nodes[atIndex];
                if (node.IsLeaf)
                {
                    return atIndex;
                }
                if (!node.IsUsed && depth > 0)
                {
                    return atIndex; // 空节点：就地当叶
                }
                atIndex = IncrementIndex(depth, morton, atIndex);
            }
            return atIndex;
        }

        /// <summary>
        /// 按子节点序号切分父盒 —— 四叉树 4 个子盒、八叉树 8 个。
        /// </summary>
        internal AABB GetChildBounds(AABB parentBounds, int childIndex)
        {
            var half = parentBounds.Extents * 0.5f;

            if (m_Kind == SpatialIndexKind.Quadtree)
            {
                // 0=左上, 1=右上, 2=左下, 3=右下（**世界空间**）：z 数值大的排在上面，
                // 与 MortonCode 里的 local.y 翻转对应。
                var cx = parentBounds.Center.x;
                var cz = parentBounds.Center.z;
                var cy = parentBounds.Center.y;
                return childIndex switch
                {
                    0 => new AABB(new float3(cx - half.x, cy, cz + half.z), half * 2f),
                    1 => new AABB(new float3(cx + half.x, cy, cz + half.z), half * 2f),
                    2 => new AABB(new float3(cx - half.x, cy, cz - half.z), half * 2f),
                    _ => new AABB(new float3(cx + half.x, cy, cz - half.z), half * 2f),
                };
            }

            // 八叉树：bit0=x, bit1=y, bit2=z；置位 = 该轴的正方向。
            var sign = new float3(
                (childIndex & 1) != 0 ? 1f : -1f,
                (childIndex & 2) != 0 ? 1f : -1f,
                (childIndex & 4) != 0 ? 1f : -1f);
            return new AABB(parentBounds.Center + half * sign, half * 2f);
        }

        // ───────────────────────────── 元素池与链表 ─────────────────────────────

        internal int AllocSlot()
        {
            int slot;
            if (m_FreeSlots.Length > 0)
            {
                var last = m_FreeSlots.Length - 1;
                slot = m_FreeSlots[last];
                m_FreeSlots.RemoveAtSwapBack(last);
            }
            else
            {
                if (m_NextFreeSlot >= m_Elements.Length)
                {
                    GrowPool();
                }
                slot = m_NextFreeSlot++;
            }

            m_Version[slot]++;
            return slot;
        }

        private void GrowPool()
        {
            var previousCapacity = m_Next.Length;
            var capacity = math.max(4, m_Elements.Length * 2);
            m_Elements.Resize(capacity);
            m_Next.Resize(capacity);
            InitNextRange(previousCapacity, capacity);
            m_Version.Resize(capacity, NativeArrayOptions.ClearMemory);
        }

        internal void PushFront(int leafIndex, int slot, in SpatialElement element)
        {
            m_Elements[slot] = element;
            PushFrontInternal(leafIndex, slot);
            m_ElementCount++;
        }

        internal void PushFrontExisting(int leafIndex, int slot) => PushFrontInternal(leafIndex, slot);

        private void PushFrontInternal(int leafIndex, int slot)
        {
            var node = m_Nodes[leafIndex];
            if (!node.IsUsed)
            {
                m_LeafCount++;                        // 空节点被就地变成叶
                MarkUsed(leafIndex);
            }

            m_Next[slot] = node.Head;
            node.Head = slot;
            node.Count++;
            node.Flags |= k_FlagUsed | k_FlagLeaf;
            m_Nodes[leafIndex] = node;
        }

        /// <summary>
        /// 从叶子链表里摘除。返回是否真的摘到了 —— <c>false</c> 表示调用方给的叶不对，
        /// 调用方<b>必须放弃本次修改</b>。
        /// </summary>
        internal bool Unlink(int leafIndex, int slot)
        {
            var node = m_Nodes[leafIndex];

            // 未使用的节点没有元素：调用方给的叶不对。
            if (!node.IsUsed)
            {
                return false;
            }

            if (node.Head == slot)
            {
                node.Head = m_Next[slot];
            }
            else
            {
                var prev = node.Head;
                var guard = m_ElementCount + 1;      // 上界取「活元素总数」：到达 maxDepth 的叶可以合法超阈值
                while (prev != -1 && m_Next[prev] != slot)
                {
                    prev = m_Next[prev];
                    if (--guard <= 0)
                        throw new InvalidOperationException(
                            $"叶 {leafIndex} 的链表疑似成环：Head={node.Head}, Count={node.Count}, slot={slot}, 活元素={m_ElementCount}");
                }

                if (prev == -1)
                {
                    return false; // 链上没有这个 slot：叶不对
                }
                m_Next[prev] = m_Next[slot];
            }

            node.Count--;
            m_Nodes[leafIndex] = node;
            return true;
        }

        // ───────────────────────────── 批量构建 ─────────────────────────────

        /// <summary>
        /// 清空并一次性建树（快路径）。返回<b>被接受</b>的元素数。
        /// </summary>
        public int ClearAndBulkInsert(NativeArray<SpatialElement> elements)
        {
            ThrowIfNotCreated();
            Clear();
            if (elements.Length == 0)
            {
                return 0;
            }

            EnsureBulkCounts();   // 惰性：第一次走 bulk 时才把表建出来
            var mortons = new NativeArray<int>(elements.Length, Allocator.Temp);
            try
            {
                var accepted = 0;
                m_TouchedCounts.Clear();                 // m_Counts 上一轮已被清回 0，这里只需记录本轮动过的节点
                for (var i = 0; i < elements.Length; i++)
                {
                    if (!IsPositionValid(elements[i].Pos))
                    {
                        m_OutOfBoundsRejected++;
                        mortons[i] = -1;
                        continue;
                    }

                    var morton = MortonCode(elements[i].Pos);
                    mortons[i] = morton;
                    accepted++;

                    var atIndex = 0;
                    for (var depth = 0; depth < m_MaxDepth; depth++)
                    {
                        if (m_Counts[atIndex]++ == 0)
                        {
                            m_TouchedCounts.Add(atIndex); // 只记「从 0 变非 0」的
                        }
                        atIndex = IncrementIndex(depth, morton, atIndex);
                    }
                    if (m_Counts[atIndex]++ == 0)
                    {
                        m_TouchedCounts.Add(atIndex);
                    }
                }

                PrepareLeavesFromCounts(0, 0);

                for (var i = 0; i < elements.Length; i++)
                {
                    if (mortons[i] < 0)
                    {
                        continue;
                    }
                    var leaf = FindLeafIndex(mortons[i], out _);
                    PushFront(leaf, AllocSlot(), elements[i]);
                }

                return accepted;
            }
            finally
            {
                // 把 m_Counts 清回 0：只清本轮动过的（重复下标多写一次，无害）。
                for (var i = 0; i < m_TouchedCounts.Length; i++)
                {
                    m_Counts[m_TouchedCounts[i]] = 0;
                }
                m_TouchedCounts.Clear();
                mortons.Dispose();
            }
        }

        /// <summary>
        /// 惰性建出 bulk 用的 <see cref="m_Counts"/>（长度 = <c>NodeCapacity</c>）。
        ///
        /// 只有 <see cref="SpatialUpdateMode.Bulk"/> 需要它；构造函数只建 0 容量空壳，
        /// 这里第一次真正需要时才分配。
        /// </summary>
        private void EnsureBulkCounts()
        {
            if (m_Counts.Length == m_NodeCapacity)
            {
                return;
            }
            m_Counts.Resize(m_NodeCapacity, NativeArrayOptions.ClearMemory);
        }

        /// <summary>
        /// 按「每节点元素数」决定哪些节点是叶、哪些要分裂。
        /// </summary>
        private void PrepareLeavesFromCounts(int parentIndex, int parentDepth)
        {
            var childDepth = parentDepth + 1;
            var blockSize = DepthSize(m_MaxDepth - parentDepth);
            for (var l = 0; l < ChildCount; l++)
            {
                var at = parentIndex + 1 + l * blockSize;
                var count = m_Counts[at];
                if (count == 0)
                {
                    continue;
                }

                if (count > m_MaxLeafElements && childDepth < m_MaxDepth)
                {
                    m_Nodes[at] = new SpatialNode
                    {
                        Head = -1,
                        Count = 0,
                        Flags = k_FlagUsed,
                    };
                    m_SplitNodeCount++;
                    MarkUsed(at);
                    PrepareLeavesFromCounts(at, childDepth);
                }
                else
                {
                    m_Nodes[at] = new SpatialNode
                    {
                        Head = -1,
                        Count = 0,
                        Flags = k_FlagUsed | k_FlagLeaf,
                    };
                    m_LeafCount++;
                    MarkUsed(at);
                }
            }
        }

        // ───────────────────────────── 增量写 ─────────────────────────────

        /// <summary>
        /// 插入一个元素。成功返回句柄；非法位置返回 <see cref="SpatialElementRef.Invalid"/> 并累加拒绝计数。
        /// </summary>
        public SpatialElementRef Insert(in SpatialElement element)
        {
            ThrowIfNotCreated();
            if (!IsPositionValid(element.Pos))
            {
                m_OutOfBoundsRejected++;
                return SpatialElementRef.Invalid;
            }

            var morton = MortonCode(element.Pos);
            var leafIndex = FindLeafIndex(morton, out var depth);
            var slot = AllocSlot();
            PushFront(leafIndex, slot, element);

            // 超过阈值就逐级分裂；同一位置的元素会分裂到 maxDepth 后停下。
            while (m_Nodes[leafIndex].Count > m_MaxLeafElements && depth < m_MaxDepth)
            {
                SplitLeaf(leafIndex, depth);
                leafIndex = FindLeafIndex(morton, out depth);
            }

            return new SpatialElementRef
            {
                Slot = slot,
                Version = m_Version[slot],
            };
        }

        /// <summary>
        /// 把一个叶子按下一层拆成 4 / 8 个子叶：只重挂链表，元素槽位与句柄都不变。
        /// </summary>
        internal void SplitLeaf(int leafIndex, int depth)
        {
            var blockSize = DepthSize(m_MaxDepth - depth);
            var shift = (m_MaxDepth - depth - 1) * BitsPerLevel;

            var guard = m_ElementCount + 1;
            for (var slot = m_Nodes[leafIndex].Head; slot != -1; )
            {
                if (--guard <= 0)
                    throw new InvalidOperationException(
                        $"叶 {leafIndex} 的链表疑似成环（分裂时遍历超过活元素数）：" +
                        $"Head={m_Nodes[leafIndex].Head}, Count={m_Nodes[leafIndex].Count}, 活元素={m_ElementCount}");

                var next = m_Next[slot];
                var child = (MortonCode(m_Elements[slot].Pos) >> shift) & ChildMask;
                var childIndex = leafIndex + 1 + child * blockSize;

                var childNode = m_Nodes[childIndex];
                if (!childNode.IsUsed)
                {
                    childNode.Flags = k_FlagUsed | k_FlagLeaf;
                    m_LeafCount++;
                    MarkUsed(childIndex);
                }

                m_Next[slot] = childNode.Head;
                childNode.Head = slot;
                childNode.Count++;
                m_Nodes[childIndex] = childNode;

                slot = next;
            }

            m_Nodes[leafIndex] = new SpatialNode
            {
                Head = -1,
                Count = 0,
                Flags = k_FlagUsed,
            };   // 不再是叶子
            m_LeafCount--;
            m_SplitNodeCount++;
        }

        /// <summary>
        /// 按句柄读元素（校验槽位版本，陈旧句柄返回 false）。
        /// </summary>
        public bool TryGet(in SpatialElementRef reference, out SpatialElement element)
        {
            if (!TryGetSlot(reference, out var slot))
            {
                element = default;
                return false;
            }

            element = m_Elements[slot];
            return true;
        }

        internal bool TryGetSlot(in SpatialElementRef reference, out int slot)
        {
            slot = reference.Slot;
            if (!m_IsCreated || slot < 0 || slot >= m_NextFreeSlot)
            {
                return false;
            }
            return m_Version[slot] == reference.Version;
        }

        /// <summary>
        /// 把元素移动到新位置。<b>句柄不变</b>；陈旧句柄或非法位置返回 false。
        /// </summary>
        public bool Update(in SpatialElementRef reference, float3 newPosition)
        {
            if (!TryGetSlot(reference, out var slot))
            {
                return false;
            }
            if (!IsPositionValid(newPosition))
            {
                return false;
            }

            var element = m_Elements[slot];
            var oldMorton = MortonCode(element.Pos);
            var newMorton = MortonCode(newPosition);

            if (oldMorton == newMorton)
            {
                element.Pos = newPosition;
                m_Elements[slot] = element;
                return true;                                  // 最深叶路径相同 ⇒ 必然同一叶
            }

            var oldLeaf = FindLeafIndex(oldMorton, out _);
            var newLeaf = FindLeafIndex(newMorton, out var newDepth);
            if (oldLeaf == newLeaf)
            {
                element.Pos = newPosition;
                m_Elements[slot] = element;
                return true;
            }

            if (!Unlink(oldLeaf, slot))
            {
                return false; // 不变式已破：不迁移、也不改元素
            }
            element.Pos = newPosition;
            m_Elements[slot] = element;
            PushFrontExisting(newLeaf, slot);

            while (m_Nodes[newLeaf].Count > m_MaxLeafElements && newDepth < m_MaxDepth)
            {
                SplitLeaf(newLeaf, newDepth);
                newLeaf = FindLeafIndex(newMorton, out newDepth);
            }

            if (m_Nodes[oldLeaf].Count == 0)
            {
                ReleaseEmptyLeaf(oldLeaf, oldMorton);
            }
            return true;
        }

        /// <summary>
        /// 只更新位置（元素其余字段保持不变）。
        /// </summary>
        public bool Update(in SpatialElementRef reference, in SpatialElement element) => Update(reference, element.Pos);

        /// <summary>
        /// 移除元素（槽位被回收，句柄随之失效）。陈旧句柄返回 false。
        /// </summary>
        public bool Remove(in SpatialElementRef reference)
        {
            if (!TryGetSlot(reference, out var slot))
            {
                return false;
            }

            var morton = MortonCode(m_Elements[slot].Pos);
            var leaf = FindLeafIndex(morton, out _);

            // 摘链没命中说明「元素位置 → 叶」这条不变式已经破了。此时什么都不能改。
            if (!Unlink(leaf, slot))
            {
                return false;
            }

            m_Next[slot] = -1;
            m_FreeSlots.Add(slot);            // 槽位回收；Version 留到下次分配时递增
            m_ElementCount--;

            if (m_Nodes[leaf].Count == 0)
            {
                ReleaseEmptyLeaf(leaf, morton);
            }
            return true;
        }

        /// <summary>
        /// 叶子被删空 → 释放该叶，并自底向上回收「8 个子节点全未使用」的内部节点。
        /// </summary>
        internal void ReleaseEmptyLeaf(int leafIndex, int morton)
        {
            if (m_Nodes[leafIndex].IsLeaf)
            {
                m_LeafCount--;
            }

            m_Nodes[leafIndex] = SpatialNode.Empty;
            CollapseSubtree(0, 0, morton);
        }

        /// <summary>
        /// 沿 morton 路径做一次自底向上的回收。返回「该子树是否完全未使用」。
        /// </summary>
        internal bool CollapseSubtree(int atIndex, int depth, int morton)
        {
            if (depth > 0)
            {
                var node = m_Nodes[atIndex];
                if (node.IsLeaf)
                {
                    return node.Count == 0;
                }
                if (!node.IsUsed)
                {
                    return true;
                }
            }

            if (depth == m_MaxDepth)
            {
                return true;
            }

            var blockSize = DepthSize(m_MaxDepth - depth);
            var child = (morton >> ((m_MaxDepth - depth - 1) * BitsPerLevel)) & ChildMask;
            if (!CollapseSubtree(atIndex + 1 + child * blockSize, depth + 1, morton))
            {
                return false;
            }
            if (depth == 0)
            {
                return false; // 根不回收
            }

            for (var l = 0; l < ChildCount; l++)
            {
                if (m_Nodes[atIndex + 1 + l * blockSize].IsUsed)
                {
                    return false;
                }
            }

            var cleared = m_Nodes[atIndex];
            if (cleared.IsLeaf)
            {
                m_LeafCount--;
            }
            else m_SplitNodeCount--;

            m_Nodes[atIndex] = SpatialNode.Empty;
            return true;
        }

        // ───────────────────────────── 可视化取数 ─────────────────────────────

        /// <summary>
        /// 输出所有「已使用」的节点（叶子 + 分裂节点），供编辑器可视化/调试绘制。
        /// </summary>
        public void CollectNodes(NativeList<SpatialDebugNode> nodes)
        {
            ThrowIfNotCreated();
            CollectNodesRecursive(m_Bounds, 0, 0, nodes);
        }

        private void CollectNodesRecursive(AABB nodeBounds, int atIndex, int depth, NativeList<SpatialDebugNode> nodes)
        {
            if (depth > 0)
            {
                var node = m_Nodes[atIndex];
                if (node.IsLeaf)
                {
                    nodes.Add(new SpatialDebugNode
                    {
                        Bounds = nodeBounds,
                        Depth = depth,
                        Count = node.Count,
                        IsLeaf = true,
                    });
                    return;
                }

                if (!node.IsUsed)
                {
                    return;
                }
                nodes.Add(new SpatialDebugNode
                {
                    Bounds = nodeBounds,
                    Depth = depth,
                    Count = 0,
                    IsLeaf = false,
                });
            }

            if (depth == m_MaxDepth)
            {
                return;
            }

            var blockSize = DepthSize(m_MaxDepth - depth);
            for (var l = 0; l < ChildCount; l++)
            {
                CollectNodesRecursive(GetChildBounds(nodeBounds, l), atIndex + 1 + l * blockSize, depth + 1, nodes);
            }
        }

        /// <summary>
        /// 输出树中所有元素（顺序不保证）。
        /// </summary>
        public void CollectElements(NativeList<SpatialElement> elements)
        {
            ThrowIfNotCreated();
            CollectElementsRecursive(m_Bounds, 0, 0, elements);
        }

        private void CollectElementsRecursive(AABB nodeBounds, int atIndex, int depth, NativeList<SpatialElement> elements)
        {
            if (depth > 0)
            {
                var node = m_Nodes[atIndex];
                if (node.IsLeaf)
                {
                    var guard = m_ElementCount + 1;
                    for (var slot = node.Head; slot != -1; slot = m_Next[slot])
                    {
                        elements.Add(m_Elements[slot]);
                        if (--guard <= 0)
                            throw new InvalidOperationException($"叶 {atIndex} 的链表疑似成环（遍历超过活元素数）");
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
                CollectElementsRecursive(GetChildBounds(nodeBounds, l), atIndex + 1 + l * blockSize, depth + 1, elements);
            }
        }

        /// <summary>
        /// 统计每层的叶子数（下标 = 深度，长度应为 <see cref="MaxDepth"/> + 1）。
        /// </summary>
        public void GetDepthDistribution(NativeArray<int> leafCountPerDepth)
        {
            ThrowIfNotCreated();
            for (var i = 0; i < leafCountPerDepth.Length; i++)
            {
                leafCountPerDepth[i] = 0;
            }
            CollectDepthDistribution(m_Bounds, 0, 0, leafCountPerDepth);
        }

        private void CollectDepthDistribution(AABB nodeBounds, int atIndex, int depth, NativeArray<int> counts)
        {
            if (depth > 0)
            {
                var node = m_Nodes[atIndex];
                if (node.IsLeaf)
                {
                    if (depth < counts.Length)
                    {
                        counts[depth]++;
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
                CollectDepthDistribution(GetChildBounds(nodeBounds, l), atIndex + 1 + l * blockSize, depth + 1, counts);
            }
        }

        // ───────────────────────────── 包围盒工具 ─────────────────────────────

        /// <summary>
        /// 按一批<b>世界坐标</b>算出能覆盖它们的包围盒（完整 xyz，与树的语义一致），
        /// 再外扩 <paramref name="padding"/>。用途：建树前先定范围（编辑器预览树、动态场景）。
        ///
        /// 约定：NaN / Inf 的位置被忽略；没有有效位置时返回「原点为中心、Extents = max(padding, 1)」的
        /// <b>可建树</b>包围盒而不抛异常；单点 / 全同点也得到正体积（最小 1e-3）。
        /// </summary>
        public static AABB CalculateBounds(NativeArray<float3> positions, float padding = 0f)
        {
            var min = new float3(float.MaxValue);
            var max = new float3(float.MinValue);
            var any = false;

            for (var i = 0; i < positions.Length; i++)
            {
                var p = positions[i];
                if (!math.all(math.isfinite(p)))
                {
                    continue;
                }
                min = math.min(min, p);
                max = math.max(max, p);
                any = true;
            }

            var pad = math.max(padding, 0f);
            if (!any) return new AABB
            {
                Center = float3.zero,
                Extents = new float3(math.max(pad, 1f)),
            };

            var extents = math.max(math.max((max - min) * 0.5f, float3.zero) + pad, new float3(1e-3f));
            return new AABB
            {
                Center = (min + max) * 0.5f,
                Extents = extents,
            };
        }
    }
}
