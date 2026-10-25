using DOTSUtils.Worlds;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace DOTSUtils.Spatial.EditorTools
{
    /// <summary>
    /// 空间索引可视化窗口：一个窗口、两个 tab（Quadtree / Octree）。
    ///
    /// <list type="bullet">
    /// <item><b>数据源</b>：Play 模式下的默认 World 树单例（<see cref="SpatialAccess"/>）/
    /// 选中的 <see cref="SpatialDebugSource"/> / 编辑期预览树（按选中对象收集 <c>Transform.position</c> 后自建临时树）；</item>
    /// <item><b>统计</b>：元素/节点/叶子/最大深度/每层叶子柱状/平均每叶元素/越界拒绝/BuildVersion；</item>
    /// <item><b>调参</b>：预览树的 MaxDepth / MaxLeafElements / padding（改完点「Rebuild Preview」）；</item>
    /// <item><b>查询探针</b>：中心 + 半径，显示命中数并可高亮命中元素；</item>
    /// <item><b>绘制开关</b>：节点线框 / 元素 / 查询范围 / 只画到第 N 层（<see cref="EditorPrefs"/> 持久化）。</item>
    /// </list>
    ///
    /// 绘制在 <see cref="SpatialSceneViewOverlay"/>（SceneView 回调）里做，本窗口只负责取数
    /// 与把结果摊成线段缓冲 —— 两边通过 <see cref="Instance"/> 解耦。
    /// </summary>
    public sealed class SpatialVisualizerWindow : EditorWindow
    {
        enum TreeKind
        {
            Quadtree = 0,
            Octree = 1,
        }

        enum DataSource
        {
            /// <summary>Play 模式：默认 World 里由 Spatial 模块维护的那棵树。</summary>
            World = 0,

            /// <summary>场景里选中的 DebugSource 组件持有的私有树。</summary>
            DebugSource = 1,

            /// <summary>编辑期预览：按选中对象临时建的树。</summary>
            Preview = 2,
        }

        private const string k_MenuPath = "Tools/DOTS Spatial/Spatial Visualizer";

        /// <summary>节点超过这个数就只画最浅两层，并在窗口里提示。</summary>
        private const int k_HeavyNodeThreshold = 20000;

        private const string k_PrefShowNodes = "DOTSUtils.Spatial.Visualizer.ShowNodes";
        private const string k_PrefShowElements = "DOTSUtils.Spatial.Visualizer.ShowElements";
        private const string k_PrefShowQuery = "DOTSUtils.Spatial.Visualizer.ShowQuery";
        private const string k_PrefMaxDrawDepth = "DOTSUtils.Spatial.Visualizer.MaxDrawDepth";

        static readonly string[] k_TabNames = { "Quadtree", "Octree" };

        /// <summary>当前打开的窗口（未打开为 null）—— SceneView overlay 靠它取绘制数据。</summary>
        public static SpatialVisualizerWindow Instance { get; private set; }

        [MenuItem(k_MenuPath)]
        private static void Open() => GetWindow<SpatialVisualizerWindow>("Spatial Visualizer");

        // ───────────────────────────── 可持久化状态 ─────────────────────────────

        [SerializeField] private TreeKind m_Kind = TreeKind.Quadtree;
        [SerializeField] private DataSource m_QuadSource = DataSource.World;
        [SerializeField] private DataSource m_OctSource = DataSource.World;

        [SerializeField] private int m_PreviewMaxDepth = 6;
        [SerializeField] private int m_PreviewMaxLeafElements = 16;
        [SerializeField] private float m_PreviewPadding = 10f;

        [SerializeField] private float m_QuadPlaneY;

        [SerializeField] private Vector3 m_QueryCenter = Vector3.zero;
        [SerializeField] private float m_QueryRadius = 25f;

        /// <summary>
        /// 本帧渲染布局用的树类型快照（GUI 交互改的是真实字段，布局只认这份快照）。
        /// 不加 <c>[SerializeField]</c>：每帧从真实字段同步，不需要持久化。
        /// </summary>
        private TreeKind m_LayoutKind;

        /// <summary>本帧布局用的四叉树数据源快照。</summary>
        private DataSource m_LayoutQuadSource;

        /// <summary>本帧布局用的八叉树数据源快照。</summary>
        private DataSource m_LayoutOctSource;

        // ───────────────────────────── 运行时容器 ─────────────────────────────

        private NativeSpatialTree m_QuadPreview;
        private NativeSpatialTree m_OctPreview;

        private NativeList<SpatialDebugNode> m_QuadNodes;
        private NativeList<SpatialElement> m_QuadElements;
        private NativeList<SpatialElement> m_QuadHits;
        private NativeList<SpatialDebugNode> m_OctNodes;
        private NativeList<SpatialElement> m_OctElements;
        private NativeList<SpatialElement> m_OctHits;
        private NativeArray<int> m_DepthCounts;

        private readonly List<List<Vector3>> m_LeafLinesByDepth = new List<List<Vector3>>();
        private readonly List<Vector3> m_InternalLines = new List<Vector3>();
        private readonly List<Vector3> m_ElementLines = new List<Vector3>();
        private readonly List<Vector3> m_QueryLines = new List<Vector3>();
        private readonly List<Vector3> m_HitLines = new List<Vector3>();
        private Vector3[] m_LineBuffer;

        // ───────────────────────────── 每帧刷新出来的读数 ─────────────────────────────

        private bool m_HasTree;
        private string m_Status = "还没有数据源。";
        private int m_ElementCount;
        private int m_NodeCount;
        private int m_LeafCount;
        private int m_MaxDepth;
        private int m_OutOfBoundsRejected;
        private int m_HitCount;
        private uint m_BuildVersion;
        /// <summary>编辑期预览用的中心高度偏移（世界单位）：把节点框抬一点，免得和地面重合。</summary>
        private float m_PreviewCenterY = 0.5f;

        private bool ShowNodes
        {
            get => EditorPrefs.GetBool(k_PrefShowNodes, true);
            set => EditorPrefs.SetBool(k_PrefShowNodes, value);
        }

        private bool ShowElements
        {
            get => EditorPrefs.GetBool(k_PrefShowElements, true);
            set => EditorPrefs.SetBool(k_PrefShowElements, value);
        }

        private bool ShowQuery
        {
            get => EditorPrefs.GetBool(k_PrefShowQuery, true);
            set => EditorPrefs.SetBool(k_PrefShowQuery, value);
        }

        private int MaxDrawDepth
        {
            get => EditorPrefs.GetInt(k_PrefMaxDrawDepth, 6);
            set => EditorPrefs.SetInt(k_PrefMaxDrawDepth, value);
        }

        /// <summary>节点太多时自动收缩绘制深度。</summary>
        private int EffectiveDrawDepth => m_NodeCount > k_HeavyNodeThreshold ? Mathf.Min(MaxDrawDepth, 2) : MaxDrawDepth;

        /// <summary>本帧画的是哪个 tab —— 读快照，保证一次重绘的两遍 <c>OnGUI</c> 布局一致。</summary>
        private bool IsQuadtree => m_LayoutKind == TreeKind.Quadtree;

        /// <summary>本帧的数据源（读快照；写回真实字段，下一帧生效）。</summary>
        private DataSource CurrentSource
        {
            get => IsQuadtree ? m_LayoutQuadSource : m_LayoutOctSource;
            set
            {
                if (IsQuadtree)
                {
                    m_QuadSource = value;
                }
                else m_OctSource = value;
            }
        }

        private void OnEnable()
        {
            Instance = this;
            m_QuadNodes = new NativeList<SpatialDebugNode>(Allocator.Persistent);
            m_QuadElements = new NativeList<SpatialElement>(Allocator.Persistent);
            m_QuadHits = new NativeList<SpatialElement>(Allocator.Persistent);
            m_OctNodes = new NativeList<SpatialDebugNode>(Allocator.Persistent);
            m_OctElements = new NativeList<SpatialElement>(Allocator.Persistent);
            m_OctHits = new NativeList<SpatialElement>(Allocator.Persistent);
        }

        private void OnDisable()
        {
            ClearSnapshot();
            if (m_QuadNodes.IsCreated)
            {
                m_QuadNodes.Dispose();
            }
            if (m_QuadElements.IsCreated)
            {
                m_QuadElements.Dispose();
            }
            if (m_QuadHits.IsCreated)
            {
                m_QuadHits.Dispose();
            }
            if (m_OctNodes.IsCreated)
            {
                m_OctNodes.Dispose();
            }
            if (m_OctElements.IsCreated)
            {
                m_OctElements.Dispose();
            }
            if (m_OctHits.IsCreated)
            {
                m_OctHits.Dispose();
            }
            if (m_DepthCounts.IsCreated)
            {
                m_DepthCounts.Dispose();
            }
            m_QuadPreview.Dispose();
            m_OctPreview.Dispose();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ───────────────────────────── OnGUI ─────────────────────────────

        private void OnGUI()
        {
            m_Kind = (TreeKind)GUILayout.Toolbar((int)m_LayoutKind, k_TabNames);

            if (IsQuadtree)
            {
                DrawQuadtreeTab();
            }
            else DrawOctreeTab();

            DrawDrawSettings();
        }

        /// <summary>
        /// 每帧取数一次，并把「本帧布局用的状态快照」同步过来（点 tab / 切数据源改动下一帧生效）。
        /// </summary>
        private void Update()
        {
            m_LayoutKind = m_Kind;
            m_LayoutQuadSource = m_QuadSource;
            m_LayoutOctSource = m_OctSource;

            if (IsQuadtree)
            {
                RefreshQuadtree();
            }
            else RefreshOctree();
        }

        private void DrawQuadtreeTab()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("数据源", EditorStyles.boldLabel);
            CurrentSource = (DataSource)EditorGUILayout.EnumPopup("来源", CurrentSource);

            switch (CurrentSource)
            {
                case DataSource.World:
                    EditorGUILayout.LabelField("说明", "Play 模式下由 Spatial 模块维护的四叉树");
                    break;
                case DataSource.DebugSource:
                    EditorGUILayout.LabelField("说明", "选中物体上 Kind = Quadtree 的 SpatialDebugSource（没选中就是空的）");
                    break;
                default:
                    EditorGUILayout.LabelField("说明", "按选中对象临时建一棵预览树");
                    EditorGUI.indentLevel++;
                    m_PreviewMaxDepth = EditorGUILayout.IntSlider("MaxDepth", m_PreviewMaxDepth, 1, NativeSpatialTree.MaxSupportedDepth);
                    m_PreviewMaxLeafElements = EditorGUILayout.IntSlider("MaxLeafElements", m_PreviewMaxLeafElements, 1, 64);
                    m_PreviewPadding = EditorGUILayout.FloatField("padding", m_PreviewPadding);
                    EditorGUI.indentLevel--;
                    if (GUILayout.Button("Rebuild Preview"))
                    {
                        RebuildQuadPreview();
                    }
                    break;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("查询探针（xz 平面，中心 + 半径）", EditorStyles.boldLabel);
            m_QueryCenter = EditorGUILayout.Vector3Field("中心", m_QueryCenter);
            m_QueryRadius = EditorGUILayout.FloatField("半径", m_QueryRadius);
            m_QuadPlaneY = EditorGUILayout.FloatField("绘制平面 y", m_QuadPlaneY);
        }

        private void DrawOctreeTab()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("数据源", EditorStyles.boldLabel);
            CurrentSource = (DataSource)EditorGUILayout.EnumPopup("来源", CurrentSource);

            switch (CurrentSource)
            {
                case DataSource.World:
                    EditorGUILayout.LabelField("说明", "Play 模式下由 Spatial 模块维护的八叉树");
                    break;
                case DataSource.DebugSource:
                    EditorGUILayout.LabelField("说明", "选中物体上 Kind = Octree 的 SpatialDebugSource（没选中就是空的）");
                    break;
                default:
                    EditorGUILayout.LabelField("说明", "按选中对象临时建一棵预览树");
                    EditorGUI.indentLevel++;
                    m_PreviewMaxDepth = EditorGUILayout.IntSlider("MaxDepth", m_PreviewMaxDepth, 1, NativeSpatialTree.MaxSupportedDepth);
                    m_PreviewMaxLeafElements = EditorGUILayout.IntSlider("MaxLeafElements", m_PreviewMaxLeafElements, 1, 64);
                    m_PreviewPadding = EditorGUILayout.FloatField("padding", m_PreviewPadding);
                    EditorGUI.indentLevel--;
                    if (GUILayout.Button("Rebuild Preview"))
                    {
                        RebuildOctreePreview();
                    }
                    break;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("查询探针（3D，中心 + 半径）", EditorStyles.boldLabel);
            m_QueryCenter = EditorGUILayout.Vector3Field("中心", m_QueryCenter);
            m_QueryRadius = EditorGUILayout.FloatField("半径", m_QueryRadius);
        }

        private void DrawDrawSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("绘制", EditorStyles.boldLabel);
            ShowNodes = EditorGUILayout.Toggle("节点线框", ShowNodes);
            ShowElements = EditorGUILayout.Toggle("元素", ShowElements);
            ShowQuery = EditorGUILayout.Toggle("查询范围", ShowQuery);
            MaxDrawDepth = EditorGUILayout.IntSlider("只画到第 N 层", MaxDrawDepth, 0, 8);

            if (m_NodeCount > k_HeavyNodeThreshold)
            {
                EditorGUILayout.HelpBox(
                    $"节点数 {m_NodeCount} > {k_HeavyNodeThreshold}：已自动只画到第 {EffectiveDrawDepth} 层，避免糊屏。",
                    MessageType.Info);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("读数", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("状态", m_Status);
            if (!m_HasTree)
            {
                return;
            }

            EditorGUILayout.LabelField("元素数", m_ElementCount.ToString());
            EditorGUILayout.LabelField("节点数（叶 + 分裂）", $"{m_NodeCount}（{m_LeafCount} + {m_NodeCount - m_LeafCount}）");
            EditorGUILayout.LabelField("最大深度", m_MaxDepth.ToString());
            EditorGUILayout.LabelField("平均每叶元素数",
                m_LeafCount > 0 ? (m_ElementCount / (float)m_LeafCount).ToString("0.00") : "0");
            EditorGUILayout.LabelField("越界拒绝数", m_OutOfBoundsRejected.ToString());
            EditorGUILayout.LabelField("查询命中数", m_HitCount.ToString());
            EditorGUILayout.LabelField("BuildVersion", m_BuildVersion > 0 ? m_BuildVersion.ToString() : "（非 World 树）");

            if (m_DepthCounts.IsCreated)
            {
                DrawDepthBars();
            }
        }

        private void DrawDepthBars()
        {
            EditorGUILayout.LabelField("每层叶子数", EditorStyles.miniBoldLabel);

            var peak = 1;
            for (var d = 0; d <= m_MaxDepth && d < m_DepthCounts.Length; d++)
            {
                peak = Mathf.Max(peak, m_DepthCounts[d]);
            }

            for (var d = 0; d <= m_MaxDepth && d < m_DepthCounts.Length; d++)
            {
                var rect = GUILayoutUtility.GetRect(0f, 14f, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(rect, new Color(0.16f, 0.16f, 0.16f));
                var width = rect.width * (m_DepthCounts[d] / (float)peak);
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, width, rect.height), SpatialDebugDrawUtility.DepthColor(d, m_MaxDepth));
                GUI.Label(rect, $"  depth {d}: {m_DepthCounts[d]}", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space();
        }

        // ───────────────────────────── 取数：四叉树 ─────────────────────────────

        /// <summary>把内核的 AABB 取 xz 平面，转成可视化工具收的 AABB2D。</summary>
        private static AABB2D ToRect(in AABB bounds)
            => new AABB2D(new float2(bounds.Center.x, bounds.Center.z),
                          new float2(bounds.Extents.x, bounds.Extents.z));

        private void RefreshQuadtree()
        {
            ClearSnapshot();
            m_Status = "四叉树：没有取到树。";
            m_BuildVersion = 0;

            if (!TryGetQuadtree(out var tree))
            {
                return;
            }

            m_HasTree = true;
            m_MaxDepth = tree.MaxDepth;
            m_ElementCount = tree.ElementCount;
            m_NodeCount = tree.NodeCount;
            m_LeafCount = tree.LeafCount;
            m_OutOfBoundsRejected = tree.OutOfBoundsRejected;
            m_Status = $"四叉树 {tree.Bounds.Center} ±{tree.Bounds.Extents}（maxDepth={tree.MaxDepth}，" +
                       $"maxLeafElements={tree.MaxLeafElements}）";

            EnsureDepthCounts(m_MaxDepth);
            tree.GetDepthDistribution(m_DepthCounts);

            // 节点线框：叶按深度着色，分裂节点统一灰
            var planY = CurrentSource == DataSource.Preview ? m_PreviewCenterY : m_QuadPlaneY;
            m_QuadNodes.Clear();                 // Collect* 是「追加」语义，每帧必须先清
            tree.CollectNodes(m_QuadNodes);
            for (var i = 0; i < m_QuadNodes.Length; i++)
            {
                var node = m_QuadNodes[i];
                var target = node.IsLeaf ? LinesForDepth(node.Depth) : m_InternalLines;
                SpatialDebugDrawUtility.FillRectLines(ToRect(node.Bounds), planY, target);
            }

            // 元素
            m_QuadElements.Clear();
            tree.CollectElements(m_QuadElements);
            for (var i = 0; i < m_QuadElements.Length; i++)
                SpatialDebugDrawUtility.FillCross(new float3(m_QuadElements[i].Pos.x, planY, m_QuadElements[i].Pos.z), 0.5f, m_ElementLines);

            // 查询探针
            var radius = Mathf.Max(m_QueryRadius, 0.01f);
            var query2D = new AABB2D(new float2(m_QueryCenter.x, m_QueryCenter.z), radius);
            SpatialDebugDrawUtility.FillRectLines(query2D, planY, m_QueryLines);
            var query = new AABB { Center = new float3(m_QueryCenter.x, 0f, m_QueryCenter.z),
                                   Extents = new float3(radius, 1f, radius) };
            m_QuadHits.Clear();
            tree.RangeQuery(query, ref m_QuadHits);
            m_HitCount = m_QuadHits.Length;
            for (var i = 0; i < m_QuadHits.Length; i++)
                SpatialDebugDrawUtility.FillCross(new float3(m_QuadHits[i].Pos.x, planY, m_QuadHits[i].Pos.z), 1.2f, m_HitLines);

            if (CurrentSource == DataSource.World)
            {
                m_BuildVersion = ReadQuadBuildVersion();
            }
        }

        private bool TryGetQuadtree(out NativeSpatialTree tree)
        {
            switch (CurrentSource)
            {
                case DataSource.World:
                    var world = DotsWorldManager.CurrentWorld;
                    if (world == null || !world.IsCreated)
                    {
                        break;
                    }
                    if (!SpatialAccess.TryGetSingletonData(world, out var quadData))
                    {
                        break;
                    }
                    if (quadData.Tree.Kind != SpatialIndexKind.Quadtree)
                    {
                        break;
                    }
                    tree = quadData.Tree;
                    if (tree.IsCreated)
                    {
                        return true;
                    }
                    break;

                case DataSource.DebugSource:
                    var source = Selection.activeGameObject != null
                        ? Selection.activeGameObject.GetComponent<SpatialDebugSource>()
                        : null;
                    if (source != null && source.IsCreated && source.Kind == SpatialIndexKind.Quadtree)
                    {
                        tree = source.Tree;
                        return true;
                    }

                    break;

                default:
                    if (m_QuadPreview.IsCreated)
                    {
                        tree = m_QuadPreview;
                        return true;
                    }

                    break;
            }

            tree = default;
            return false;
        }

        // ───────────────────────────── 取数：八叉树 ─────────────────────────────

        private void RefreshOctree()
        {
            ClearSnapshot();
            m_Status = "八叉树：没有取到树。";
            m_BuildVersion = 0;

            if (!TryGetOctree(out var tree))
            {
                return;
            }

            m_HasTree = true;
            m_MaxDepth = tree.MaxDepth;
            m_ElementCount = tree.ElementCount;
            m_NodeCount = tree.NodeCount;
            m_LeafCount = tree.LeafCount;
            m_OutOfBoundsRejected = tree.OutOfBoundsRejected;
            m_Status = $"八叉树 {tree.Bounds.Center} ±{tree.Bounds.Extents}（maxDepth={tree.MaxDepth}，" +
                       $"maxLeafElements={tree.MaxLeafElements}）";

            EnsureDepthCounts(m_MaxDepth);
            tree.GetDepthDistribution(m_DepthCounts);

            m_OctNodes.Clear();                  // Collect* 是「追加」语义，每帧必须先清
            tree.CollectNodes(m_OctNodes);
            for (var i = 0; i < m_OctNodes.Length; i++)
            {
                var node = m_OctNodes[i];
                var target = node.IsLeaf ? LinesForDepth(node.Depth) : m_InternalLines;
                SpatialDebugDrawUtility.FillBoxLines(node.Bounds, target);
            }

            m_OctElements.Clear();
            tree.CollectElements(m_OctElements);
            for (var i = 0; i < m_OctElements.Length; i++)
                SpatialDebugDrawUtility.FillCross(m_OctElements[i].Pos, 0.5f, m_ElementLines);

            var center = new float3(m_QueryCenter.x, m_QueryCenter.y, m_QueryCenter.z);
            SpatialDebugDrawUtility.FillBoxLines(new AABB(center, new float3(Mathf.Max(m_QueryRadius, 0.01f) * 2f)), m_QueryLines);
            m_OctHits.Clear();
            tree.RangeQuery(center, Mathf.Max(m_QueryRadius, 0.01f), ref m_OctHits);
            m_HitCount = m_OctHits.Length;
            for (var i = 0; i < m_OctHits.Length; i++)
                SpatialDebugDrawUtility.FillCross(m_OctHits[i].Pos, 1.2f, m_HitLines);

            if (CurrentSource == DataSource.World)
            {
                m_BuildVersion = ReadOctBuildVersion();
            }
        }

        private bool TryGetOctree(out NativeSpatialTree tree)
        {
            switch (CurrentSource)
            {
                case DataSource.World:
                    var world = DotsWorldManager.CurrentWorld;
                    if (world == null || !world.IsCreated)
                    {
                        break;
                    }
                    if (!SpatialAccess.TryGetSingletonData(world, out var octData))
                    {
                        break;
                    }
                    if (octData.Tree.Kind != SpatialIndexKind.Octree)
                    {
                        break;
                    }
                    tree = octData.Tree;
                    if (tree.IsCreated)
                    {
                        return true;
                    }
                    break;

                case DataSource.DebugSource:
                    var source = Selection.activeGameObject != null
                        ? Selection.activeGameObject.GetComponent<SpatialDebugSource>()
                        : null;
                    if (source != null && source.IsCreated && source.Kind == SpatialIndexKind.Octree)
                    {
                        tree = source.Tree;
                        return true;
                    }

                    break;

                default:
                    if (m_OctPreview.IsCreated)
                    {
                        tree = m_OctPreview;
                        return true;
                    }

                    break;
            }

            tree = default;
            return false;
        }

        // ───────────────────────────── 编辑期预览树 ─────────────────────────────

        private void RebuildQuadPreview()
        {
            DisposeQuadPreview();
            var positions = CollectSelectedPositions();
            try
            {
                if (positions.Length == 0)
                {
                    m_Status = "预览：没有选中任何对象。";
                    return;
                }

                var bounds = NativeSpatialTree.CalculateBounds(positions, m_PreviewPadding);
                m_QuadPreview = new NativeSpatialTree(SpatialIndexKind.Octree, bounds, Allocator.Persistent, m_PreviewMaxDepth,
                                                   (short)m_PreviewMaxLeafElements);

                var elements = new NativeArray<SpatialElement>(positions.Length, Allocator.Temp);
                try
                {
                    for (var i = 0; i < positions.Length; i++) elements[i] = new SpatialElement
                    {
                        Pos = positions[i],
                    };
                    m_QuadPreview.ClearAndBulkInsert(elements);
                }
                finally
                {
                    elements.Dispose();
                }

                m_Status = $"预览树已建：{positions.Length} 个对象，bounds {bounds.Center} ±{bounds.Extents}";
            }
            finally
            {
                positions.Dispose();
            }
        }

        private void RebuildOctreePreview()
        {
            DisposeOctreePreview();
            var positions = CollectSelectedPositions();
            try
            {
                if (positions.Length == 0)
                {
                    m_Status = "预览：没有选中任何对象。";
                    return;
                }

                var bounds = NativeSpatialTree.CalculateBounds(positions, m_PreviewPadding);
                m_OctPreview = new NativeSpatialTree(SpatialIndexKind.Octree, bounds, Allocator.Persistent, m_PreviewMaxDepth,
                                                (short)m_PreviewMaxLeafElements);

                var elements = new NativeArray<SpatialElement>(positions.Length, Allocator.Temp);
                try
                {
                    for (var i = 0; i < positions.Length; i++) elements[i] = new SpatialElement
                    {
                        Pos = positions[i],
                    };
                    m_OctPreview.ClearAndBulkInsert(elements);
                }
                finally
                {
                    elements.Dispose();
                }

                m_Status = $"预览树已建：{positions.Length} 个对象，bounds {bounds.Center} ±{bounds.Extents}";
            }
            finally
            {
                positions.Dispose();
            }
        }

        private static NativeArray<float3> CollectSelectedPositions()
        {
            var unique = new HashSet<Transform>();
            var selection = Selection.gameObjects;
            for (var i = 0; i < selection.Length; i++)
            {
                if (selection[i] == null)
                {
                    continue;
                }
                foreach (var transform in selection[i].GetComponentsInChildren<Transform>())
                {
                    unique.Add(transform);
                }
            }

            var array = new NativeArray<float3>(unique.Count, Allocator.Temp);
            var index = 0;
            foreach (var transform in unique)
            {
                array[index++] = transform.position;
            }
            return array;
        }

        // ───────────────────────────── SceneView 绘制 ─────────────────────────────

        /// <summary>由 <see cref="SpatialSceneViewOverlay"/> 在 <c>SceneView.duringSceneGui</c> 里调用。</summary>
        public void DrawSceneView(SceneView view)
        {
            if (!ShowNodes && !ShowElements && !ShowQuery)
            {
                return;
            }

            var previousColor = Handles.color;
            try
            {
                if (ShowNodes)
                {
                    Handles.color = new Color(0.55f, 0.55f, 0.55f, 0.35f);
                    DrawLines(m_InternalLines);

                    var limit = Mathf.Min(EffectiveDrawDepth, m_LeafLinesByDepth.Count - 1);
                    for (var depth = 0; depth <= limit; depth++)
                    {
                        var lines = m_LeafLinesByDepth[depth];
                        if (lines.Count == 0)
                        {
                            continue;
                        }

                        var color = SpatialDebugDrawUtility.DepthColor(depth, m_MaxDepth);
                        Handles.color = new Color(color.r, color.g, color.b, 0.9f);
                        DrawLines(lines);
                    }
                }

                if (ShowElements && m_ElementLines.Count > 0)
                {
                    Handles.color = new Color(1f, 0.9f, 0.3f, 0.9f);
                    DrawLines(m_ElementLines);
                }

                if (ShowQuery)
                {
                    if (m_QueryLines.Count > 0)
                    {
                        Handles.color = new Color(0.2f, 0.9f, 1f, 0.75f);
                        DrawLines(m_QueryLines);
                    }

                    if (m_HitLines.Count > 0)
                    {
                        Handles.color = new Color(1f, 0.35f, 0.35f, 1f);
                        DrawLines(m_HitLines);
                    }
                }
            }
            finally
            {
                Handles.color = previousColor;
            }
        }

        private void DrawLines(List<Vector3> lines)
        {
            if (lines.Count == 0)
            {
                return;
            }

            // m_LineBuffer 在每次 Handles.DrawLines 之前重建，可连续复用。
            Handles.DrawLines(SpatialDebugDrawUtility.ToArray(lines, ref m_LineBuffer));
        }

        // ───────────────────────────── 工具 ─────────────────────────────

        private List<Vector3> LinesForDepth(int depth)
        {
            while (m_LeafLinesByDepth.Count <= depth)
            {
                m_LeafLinesByDepth.Add(new List<Vector3>());
            }
            return m_LeafLinesByDepth[depth];
        }

        private void EnsureDepthCounts(int maxDepth)
        {
            if (m_DepthCounts.IsCreated && m_DepthCounts.Length == maxDepth + 1)
            {
                return;
            }
            if (m_DepthCounts.IsCreated)
            {
                m_DepthCounts.Dispose();
            }
            m_DepthCounts = new NativeArray<int>(maxDepth + 1, Allocator.Persistent);
        }

        private void ClearSnapshot()
        {
            m_HasTree = false;
            m_ElementCount = m_NodeCount = m_LeafCount = m_MaxDepth = m_OutOfBoundsRejected = m_HitCount = 0;
            m_LeafLinesByDepth.Clear();
            m_InternalLines.Clear();
            m_ElementLines.Clear();
            m_QueryLines.Clear();
            m_HitLines.Clear();
            if (m_DepthCounts.IsCreated) for (var i = 0; i < m_DepthCounts.Length; i++) m_DepthCounts[i] = 0;
        }

        private void DisposeQuadPreview()
        {
            if (!m_QuadPreview.IsCreated)
            {
                return;
            }
            m_QuadPreview.Dispose();
            m_QuadPreview = default;
        }

        private void DisposeOctreePreview()
        {
            if (!m_OctPreview.IsCreated)
            {
                return;
            }
            m_OctPreview.Dispose();
            m_OctPreview = default;
        }

        /// <summary>
        /// 从 World 树的单例实体上读 BuildVersion（非 World 数据源拿不到，返回 0）。
        /// </summary>
        private static uint ReadQuadBuildVersion()
        {
            var world = DotsWorldManager.CurrentWorld;
            if (world == null || !world.IsCreated)
            {
                return 0;
            }

            if (!SpatialAccess.TryGetSingletonData(world, out var data))
            {
                return 0;
            }
            return data.Tree.Kind == SpatialIndexKind.Quadtree ? data.BuildVersion : 0;
        }

        private static uint ReadOctBuildVersion()
        {
            var world = DotsWorldManager.CurrentWorld;
            if (world == null || !world.IsCreated)
            {
                return 0;
            }

            if (!SpatialAccess.TryGetSingletonData(world, out var data))
            {
                return 0;
            }
            return data.Tree.Kind == SpatialIndexKind.Octree ? data.BuildVersion : 0;
        }
    }
}
