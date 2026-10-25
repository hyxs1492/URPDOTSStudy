using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace DOTSUtils.Pathfinding.EditorTools
{
    /// <summary>
    /// A* 寻路网格窗口：编辑 <see cref="AStarGridAsset"/>（参数、掩码、场景烘焙、圆形笔刷），
    /// 试算一条示例路径，并把网格 / 掩码 / 路径的绘制开关交给 SceneView 叠加层。
    /// </summary>
    public sealed class AStarGridEditorWindow : EditorWindow
    {
        private const string k_MenuPath = "Tools/DOTSUtils/A* 寻路网格";
        private const string k_WindowTitle = "A* 寻路网格";
        private const string k_DefaultAssetName = "AStarGrid";
        private const string k_UndoParameters = "修改 A* 网格参数";
        private const string k_UndoPaint = "刷 A* 网格";

        /// <summary>SceneView 叠加层读取的绘制开关。</summary>
        internal static bool ShowGrid = true;

        /// <summary>是否画每条格线。</summary>
        internal static bool ShowCellLines;

        /// <summary>是否画阻挡格。</summary>
        internal static bool ShowMask = true;

        /// <summary>是否画示例路径与起点 / 终点标记。</summary>
        internal static bool ShowPath = true;

        /// <summary>绘制平面相对网格中心的高度偏移（世界单位）。</summary>
        internal static float DrawHeightOffset = 0.02f;

        /// <summary>叠加层读取的当前资产。</summary>
        internal static AStarGridAsset CurrentAsset;

        /// <summary>掩码每次变化时自增，叠加层据此判断几何缓存是否过期。</summary>
        internal static int MaskRevision;

        /// <summary>是否已经算过一次示例路径。</summary>
        internal static bool HasSample;

        /// <summary>最近一次示例寻路的状态。</summary>
        internal static AStarStatus LastStatus = AStarStatus.NoGrid;

        /// <summary>最近一次示例寻路的路径点（世界坐标）。</summary>
        internal static List<Vector3> LastPath = new List<Vector3>();

        /// <summary>最近一次示例寻路的路径总长度。</summary>
        internal static float LastPathLength;

        /// <summary>最近一次示例寻路的起点。</summary>
        internal static Vector3 LastStart;

        /// <summary>最近一次示例寻路的终点。</summary>
        internal static Vector3 LastEnd;

        /// <summary>当前打开的窗口（未打开为 null）。</summary>
        public static AStarGridEditorWindow Instance { get; private set; }

        [SerializeField] private AStarGridAsset m_Asset;
        [SerializeField] private Vector3 m_Start = Vector3.zero;
        [SerializeField] private Vector3 m_End = new Vector3(10f, 0f, 10f);
        [SerializeField] private bool m_PickMode;
        [SerializeField] private bool m_PickStart = true;
        [SerializeField] private bool m_PaintMode;
        [SerializeField] private int m_BrushRadius = 2;
        [SerializeField] private LayerMask m_BakeLayers = ~0;
        [SerializeField] private float m_BakeStartHeight = 50f;
        [SerializeField] private float m_BakeMaxDrop = 5f;
        [SerializeField] private bool m_BakeHitWalkable = true;
        [SerializeField] private string m_MaskText = string.Empty;
        [SerializeField] private Vector2 m_Scroll;

        private string m_Message = string.Empty;
        private MessageType m_MessageType = MessageType.Info;

        private static GUIStyle s_StatsBoxStyle;

        private static GUIStyle StatsBoxStyle
        {
            get
            {
                if (s_StatsBoxStyle == null)
                {
                    s_StatsBoxStyle = new GUIStyle(EditorStyles.helpBox)
                    {
                        padding = new RectOffset(6, 6, 4, 4),
                    };
                }
                return s_StatsBoxStyle;
            }
        }

        [MenuItem(k_MenuPath)]
        private static void Open()
        {
            var window = GetWindow<AStarGridEditorWindow>(k_WindowTitle);
            window.minSize = new Vector2(380f, 480f);
            window.Show();
        }

        private void OnEnable()
        {
            Instance = this;
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
            SyncCurrentAsset();
            SceneView.RepaintAll();
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            if (Instance == this)
            {
                Instance = null;
            }
            CurrentAsset = null;
            SceneView.RepaintAll();
        }

        // ───────────────────────────── OnGUI ─────────────────────────────

        private void OnGUI()
        {
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);

            DrawAssetSection();
            EditorGUILayout.Space();

            if (m_Asset == null)
            {
                EditorGUILayout.HelpBox("先指定一个网格资产，或点「新建并保存资产」。", MessageType.Info);
            }
            else
            {
                DrawParameterSection();
                EditorGUILayout.Space();
                DrawStatisticsSection();
                EditorGUILayout.Space();
                DrawMaskSection();
                EditorGUILayout.Space();
                DrawBakeSection();
                EditorGUILayout.Space();
                DrawPaintSection();
                EditorGUILayout.Space();
                DrawPathSection();
            }

            EditorGUILayout.Space();
            DrawOverlaySection();

            if (!string.IsNullOrEmpty(m_Message))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(m_Message, m_MessageType);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawAssetSection()
        {
            EditorGUILayout.LabelField("网格资产", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            var asset = (AStarGridAsset)EditorGUILayout.ObjectField("资产", m_Asset, typeof(AStarGridAsset), false);
            if (EditorGUI.EndChangeCheck())
            {
                m_Asset = asset;
                SyncCurrentAsset();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("新建并保存资产"))
                {
                    CreateAndSaveAsset();
                }

                using (new EditorGUI.DisabledScope(m_Asset == null))
                {
                    if (GUILayout.Button("定位资产"))
                    {
                        EditorGUIUtility.PingObject(m_Asset);
                    }
                    if (GUILayout.Button("保存资产"))
                    {
                        AssetDatabase.SaveAssets();
                    }
                }
            }
        }

        private void DrawParameterSection()
        {
            EditorGUILayout.LabelField("参数", EditorStyles.boldLabel);

            var config = m_Asset.ToConfig();
            var center = (Vector3)config.Bounds.Center;
            var extents = (Vector3)config.Bounds.Extents;
            var cellSize = config.CellSize;
            var neighbourhood = config.Neighbourhood;
            var cornerCutting = config.AllowCornerCutting;
            var algorithm = config.Algorithm;
            var weight = config.HeuristicWeight;
            var maxVisited = config.MaxVisitedNodes;
            var maxSolves = config.MaxSolvesPerFrame;
            var smooth = config.SmoothPath;

            EditorGUI.BeginChangeCheck();
            center = EditorGUILayout.Vector3Field("Bounds 中心", center);
            extents = EditorGUILayout.Vector3Field("Bounds 半长", extents);
            cellSize = EditorGUILayout.FloatField("格边长", cellSize);
            neighbourhood = (AStarNeighbourhood)EditorGUILayout.EnumPopup("邻接方式", neighbourhood);
            cornerCutting = EditorGUILayout.Toggle("允许贴角穿过", cornerCutting);
            algorithm = (PathfindingAlgorithm)EditorGUILayout.EnumPopup("算法", algorithm);
            using (new EditorGUI.DisabledScope(algorithm == PathfindingAlgorithm.Dijkstra))
            {
                weight = EditorGUILayout.FloatField("启发式权重（只对 A* 有效）", weight);
            }
            maxVisited = EditorGUILayout.IntField("最大访问格数（<=0 用总格数）", maxVisited);
            maxSolves = EditorGUILayout.IntField("每帧最大求解数（<=0 不限）", maxSolves);
            smooth = EditorGUILayout.Toggle("拉直路径", smooth);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(m_Asset, k_UndoParameters);
                config.Bounds = new AABB { Center = center, Extents = extents };
                config.CellSize = cellSize;
                config.Neighbourhood = neighbourhood;
                config.AllowCornerCutting = cornerCutting;
                config.Algorithm = algorithm;
                config.HeuristicWeight = weight;
                config.MaxVisitedNodes = maxVisited;
                config.MaxSolvesPerFrame = maxSolves;
                config.SmoothPath = smooth;
                m_Asset.ApplyConfig(config);
                m_Asset.ResizeIfNeeded();
                NotifyAssetChanged();
            }
        }

        private void DrawStatisticsSection()
        {
            EditorGUILayout.LabelField("统计", EditorStyles.boldLabel);

            var size = m_Asset.Size;
            var cells = m_Asset.CellCount;
            var walkable = m_Asset.WalkableCount;

            using (new EditorGUILayout.VerticalScope(StatsBoxStyle))
            {
                EditorGUILayout.LabelField("尺寸（格）", $"{size.x} × {size.y}");
                EditorGUILayout.LabelField("总格数", cells.ToString());
                EditorGUILayout.LabelField("可走格数", $"{walkable}（阻挡 {math.max(0, cells - walkable)}）");
                EditorGUILayout.LabelField("掩码", m_Asset.Mask != null ? $"{m_Asset.Mask.Length} 字节" : "无（全部可走）");
            }
        }

        private void DrawMaskSection()
        {
            EditorGUILayout.LabelField("掩码编辑", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("全部可走"))
                {
                    FillAll(true);
                }
                if (GUILayout.Button("全部阻挡"))
                {
                    FillAll(false);
                }
                if (GUILayout.Button("反转掩码"))
                {
                    InvertMask();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("导入掩码（从文本）"))
                {
                    ImportMaskFromText();
                }
                if (GUILayout.Button("导出掩码到文本"))
                {
                    ExportMaskToText();
                }
                if (GUILayout.Button("复制掩码到剪贴板"))
                {
                    CopyMaskToClipboard();
                }
            }

            EditorGUILayout.LabelField("掩码文本（每行 width 个 0/1）");
            m_MaskText = EditorGUILayout.TextArea(m_MaskText, GUILayout.Height(90f));
        }

        private void DrawBakeSection()
        {
            EditorGUILayout.LabelField("从场景烘焙（会改资产）", EditorStyles.boldLabel);

            m_BakeLayers = EditorGUILayout.MaskField("层遮罩", m_BakeLayers.value, InternalEditorUtility.layers);
            m_BakeStartHeight = EditorGUILayout.FloatField("射线起点高度", m_BakeStartHeight);
            m_BakeMaxDrop = EditorGUILayout.FloatField("最大落差", m_BakeMaxDrop);
            m_BakeHitWalkable = EditorGUILayout.Toggle("命中算可走", m_BakeHitWalkable);

            EditorGUILayout.HelpBox(
                "逐格从「格中心 + 起点高度」向下射线：勾上「命中算可走」时，命中且落差在范围内算可走；" +
                "取消时命中即算阻挡。按钮会覆盖整个掩码。",
                MessageType.Info);

            if (GUILayout.Button("从场景烘焙（覆盖整个掩码）"))
            {
                BakeFromScene();
            }
        }

        private void DrawPaintSection()
        {
            EditorGUILayout.LabelField("绘制模式", EditorStyles.boldLabel);

            m_PaintMode = EditorGUILayout.Toggle("启用（在 SceneView 里刷格）", m_PaintMode);
            using (new EditorGUI.DisabledScope(!m_PaintMode))
            {
                m_BrushRadius = EditorGUILayout.IntSlider("笔刷半径（格）", m_BrushRadius, 0, 32);
            }
            EditorGUILayout.LabelField("鼠标左键 = 可走，右键 = 阻挡，按住拖动连续刷。");
        }

        private void DrawPathSection()
        {
            EditorGUILayout.LabelField("示例寻路", EditorStyles.boldLabel);

            m_Start = EditorGUILayout.Vector3Field("起点（世界）", m_Start);
            m_End = EditorGUILayout.Vector3Field("终点（世界）", m_End);

            m_PickMode = EditorGUILayout.Toggle("在 SceneView 里点选起点 / 终点", m_PickMode);
            using (new EditorGUI.DisabledScope(!m_PickMode))
            {
                m_PickStart = EditorGUILayout.Toggle("下一次点选设为起点", m_PickStart);
            }

            if (GUILayout.Button("算一条路径"))
            {
                SolveSamplePath();
            }

            using (new EditorGUILayout.VerticalScope(StatsBoxStyle))
            {
                EditorGUILayout.LabelField("状态", StatusText(LastStatus));
                EditorGUILayout.LabelField("点数", LastPath.Count.ToString());
                EditorGUILayout.LabelField("总长度", LastPathLength.ToString("0.###"));
            }
        }

        private void DrawOverlaySection()
        {
            EditorGUILayout.LabelField("SceneView 叠加", EditorStyles.boldLabel);

            ShowGrid = EditorGUILayout.Toggle("网格外框", ShowGrid);
            ShowCellLines = EditorGUILayout.Toggle("每条格线", ShowCellLines);
            ShowMask = EditorGUILayout.Toggle("阻挡格", ShowMask);
            ShowPath = EditorGUILayout.Toggle("示例路径与端点", ShowPath);
            DrawHeightOffset = EditorGUILayout.FloatField("绘制高度偏移", DrawHeightOffset);

            if (m_Asset == null)
            {
                return;
            }

            if (ShowCellLines && m_Asset.CellCount > AStarSceneViewOverlay.MaxCellLineCells)
            {
                EditorGUILayout.HelpBox(
                    $"格数 {m_Asset.CellCount} 超过 {AStarSceneViewOverlay.MaxCellLineCells}，不画每条格线。",
                    MessageType.Info);
            }
            if (ShowMask && m_Asset.CellCount > AStarSceneViewOverlay.MaxDrawnCells)
            {
                EditorGUILayout.HelpBox(
                    $"格数 {m_Asset.CellCount} 超过 {AStarSceneViewOverlay.MaxDrawnCells}，只画前 {AStarSceneViewOverlay.MaxDrawnCells} 个阻挡格。",
                    MessageType.Info);
            }
        }

        // ───────────────────────────── 场景交互 ─────────────────────────────

        private void OnSceneGUI(SceneView view)
        {
            if (m_Asset == null || Event.current == null)
            {
                return;
            }

            var current = Event.current;
            if (m_PickMode)
            {
                HandlePick(current);
                return;
            }

            if (!m_PaintMode)
            {
                return;
            }
            if (current.type != EventType.MouseDown && current.type != EventType.MouseDrag)
            {
                return;
            }
            if (current.button != 0 && current.button != 1)
            {
                return;
            }
            if (current.alt)
            {
                return;
            }

            var ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
            if (!TryRayToCell(ray, out var cell))
            {
                return;
            }

            PaintCircle(cell, current.button == 0);
            current.Use();
        }

        private void HandlePick(Event current)
        {
            if (current.type != EventType.MouseDown || current.button != 0 || current.alt)
            {
                return;
            }

            var ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
            if (!TryPickWorldPoint(ray, out var point))
            {
                return;
            }

            if (m_PickStart)
            {
                m_Start = point;
            }
            else
            {
                m_End = point;
            }
            m_PickStart = !m_PickStart;
            current.Use();
            Repaint();
        }

        /// <summary>把屏幕射线落到网格平面（xz），换算成格坐标。</summary>
        private bool TryRayToCell(Ray ray, out int2 cell)
        {
            cell = default;

            var config = m_Asset.ToConfig();
            var plane = new Plane(Vector3.up, new Vector3(config.Bounds.Center.x, config.Bounds.Center.y, config.Bounds.Center.z));
            if (!plane.Raycast(ray, out var distance))
            {
                return false;
            }

            var point = ray.GetPoint(distance);
            var cellSize = math.max(config.CellSize, 1e-3f);
            var minX = config.Bounds.Center.x - config.Bounds.Extents.x;
            var minZ = config.Bounds.Center.z - config.Bounds.Extents.z;
            var size = m_Asset.Size;

            cell = new int2(
                Mathf.FloorToInt((point.x - minX) / cellSize),
                Mathf.FloorToInt((point.z - minZ) / cellSize));
            return cell.x >= 0 && cell.y >= 0 && cell.x < size.x && cell.y < size.y;
        }

        /// <summary>取一个世界坐标点：先打物理，未命中则落到网格平面。</summary>
        private bool TryPickWorldPoint(Ray ray, out Vector3 point)
        {
            point = default;

            if (Physics.Raycast(ray, out var hit, float.MaxValue, m_BakeLayers.value, QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                return true;
            }

            var config = m_Asset.ToConfig();
            var plane = new Plane(Vector3.up, new Vector3(config.Bounds.Center.x, config.Bounds.Center.y, config.Bounds.Center.z));
            if (plane.Raycast(ray, out var distance))
            {
                point = ray.GetPoint(distance);
                return true;
            }

            return false;
        }

        /// <summary>用圆形笔刷把一批格刷成可走 / 阻挡。</summary>
        private void PaintCircle(int2 center, bool walkable)
        {
            var size = m_Asset.Size;
            var radius = math.max(0, m_BrushRadius);
            var radiusSq = radius * radius;

            Undo.RecordObject(m_Asset, k_UndoPaint);
            for (var z = center.y - radius; z <= center.y + radius; z++)
            {
                for (var x = center.x - radius; x <= center.x + radius; x++)
                {
                    if (x < 0 || z < 0 || x >= size.x || z >= size.y)
                    {
                        continue;
                    }

                    var dx = x - center.x;
                    var dz = z - center.y;
                    if (dx * dx + dz * dz > radiusSq)
                    {
                        continue;
                    }

                    m_Asset.SetWalkable(new int2(x, z), walkable);
                }
            }

            NotifyAssetChanged();
        }

        // ───────────────────────────── 掩码操作 ─────────────────────────────

        private void FillAll(bool walkable)
        {
            m_Asset.ResizeIfNeeded();
            Undo.RecordObject(m_Asset, walkable ? "A* 网格全部可走" : "A* 网格全部阻挡");
            m_Asset.Fill(walkable);
            NotifyAssetChanged();
            m_Message = walkable ? "已把全部格设为可走。" : "已把全部格设为阻挡。";
            m_MessageType = MessageType.Info;
        }

        private void InvertMask()
        {
            m_Asset.ResizeIfNeeded();
            Undo.RecordObject(m_Asset, "反转 A* 网格掩码");

            var size = m_Asset.Size;
            for (var z = 0; z < size.y; z++)
            {
                for (var x = 0; x < size.x; x++)
                {
                    var cell = new int2(x, z);
                    m_Asset.SetWalkable(cell, !m_Asset.IsWalkable(cell));
                }
            }

            NotifyAssetChanged();
            m_Message = "已反转掩码。";
            m_MessageType = MessageType.Info;
        }

        private void ImportMaskFromText()
        {
            m_Asset.ResizeIfNeeded();
            var size = m_Asset.Size;

            var rows = new List<string>();
            var lines = (m_MaskText ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length > 0)
                {
                    rows.Add(line);
                }
            }

            if (rows.Count != size.y)
            {
                m_Message = $"文本有 {rows.Count} 行，网格高度是 {size.y} 行。";
                m_MessageType = MessageType.Error;
                return;
            }

            var next = new byte[size.x * size.y];
            for (var z = 0; z < size.y; z++)
            {
                var row = rows[z];
                if (row.Length != size.x)
                {
                    m_Message = $"第 {z + 1} 行有 {row.Length} 个字符，网格宽度是 {size.x}。";
                    m_MessageType = MessageType.Error;
                    return;
                }

                for (var x = 0; x < size.x; x++)
                {
                    var value = row[x];
                    if (value != '0' && value != '1')
                    {
                        m_Message = $"第 {z + 1} 行第 {x + 1} 个字符是 '{value}'，只接受 0 或 1。";
                        m_MessageType = MessageType.Error;
                        return;
                    }
                    next[z * size.x + x] = value == '1' ? (byte)1 : (byte)0;
                }
            }

            Undo.RecordObject(m_Asset, "导入 A* 网格掩码");
            m_Asset.Mask = next;
            NotifyAssetChanged();
            m_Message = $"已导入 {size.x} × {size.y} 掩码。";
            m_MessageType = MessageType.Info;
        }

        private void ExportMaskToText()
        {
            m_MaskText = BuildMaskText();
            m_Message = $"已导出 {m_Asset.Size.x} × {m_Asset.Size.y} 掩码到文本框。";
            m_MessageType = MessageType.Info;
            Repaint();
        }

        private void CopyMaskToClipboard()
        {
            m_MaskText = BuildMaskText();
            EditorGUIUtility.systemCopyBuffer = m_MaskText;
            m_Message = "掩码已复制到剪贴板。";
            m_MessageType = MessageType.Info;
            Repaint();
        }

        private string BuildMaskText()
        {
            m_Asset.ResizeIfNeeded();
            var size = m_Asset.Size;
            var builder = new StringBuilder(size.x * (size.y + 1));
            for (var z = 0; z < size.y; z++)
            {
                for (var x = 0; x < size.x; x++)
                {
                    builder.Append(m_Asset.IsWalkable(new int2(x, z)) ? '1' : '0');
                }
                builder.Append('\n');
            }
            return builder.ToString();
        }

        // ───────────────────────────── 烘焙 ─────────────────────────────

        /// <summary>逐格向下射线，按命中与落差重写整张掩码（实现见 <see cref="AStarBakeUtility"/>）。</summary>
        private void BakeFromScene()
        {
            if (!AStarBakeUtility.BakeActiveSceneAndApply(m_Asset, m_BakeLayers, m_BakeStartHeight,
                                                          m_BakeMaxDrop, m_BakeHitWalkable, out var result))
            {
                m_Message = "烘焙失败：先指定网格资产，并让要烘焙的场景处于打开状态。";
                m_MessageType = MessageType.Error;
                return;
            }

            NotifyAssetChanged();
            m_Message = $"烘焙完成：{result.Width} × {result.Height} 格，可走 {result.WalkableCount}、" +
                        $"阻挡 {result.BlockedCount}。";
            m_MessageType = MessageType.Info;
        }

        // ───────────────────────────── 示例寻路 ─────────────────────────────

        private void SolveSamplePath()
        {
            m_Asset.ResizeIfNeeded();

            var config = m_Asset.ToConfig();
            var grid = new AStarGrid(config, m_Asset.Mask, Allocator.Temp);
            var buffer = new UnsafeList<float3>(math.max(16, grid.CellCount), Allocator.Temp);
            try
            {
                LastStatus = AStarPathfinder.FindPath(grid, m_Start, m_End, config, ref buffer);
                LastPath.Clear();
                for (var i = 0; i < buffer.Length; i++)
                {
                    LastPath.Add(buffer[i]);
                }
                LastPathLength = MeasurePath(LastPath);
                LastStart = m_Start;
                LastEnd = m_End;
                HasSample = true;

                m_Message = $"寻路结果：{StatusText(LastStatus)}，{LastPath.Count} 个点，总长 {LastPathLength:0.###}。";
                m_MessageType = LastStatus == AStarStatus.Found ? MessageType.Info : MessageType.Warning;
            }
            finally
            {
                buffer.Dispose();
                grid.Dispose();
                SceneView.RepaintAll();
                Repaint();
            }
        }

        private static float MeasurePath(List<Vector3> path)
        {
            var total = 0f;
            for (var i = 1; i < path.Count; i++)
            {
                total += Vector3.Distance(path[i - 1], path[i]);
            }
            return total;
        }

        private static string StatusText(AStarStatus status)
        {
            switch (status)
            {
                case AStarStatus.Found:
                    return "找到路径";
                case AStarStatus.NoGrid:
                    return "没有网格";
                case AStarStatus.StartInvalid:
                    return "起点无效（越界或阻挡）";
                case AStarStatus.EndInvalid:
                    return "终点无效（越界或阻挡）";
                case AStarStatus.NoPath:
                    return "没有通路";
                case AStarStatus.ExceededLimit:
                    return "超过访问上限";
                default:
                    return status.ToString();
            }
        }

        // ───────────────────────────── 工具 ─────────────────────────────

        private void CreateAndSaveAsset()
        {
            var path = EditorUtility.SaveFilePanelInProject("新建 A* 网格资产", k_DefaultAssetName, "asset", "选择保存位置");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var asset = AStarGridAsset.CreateDefault();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();

            m_Asset = asset;
            SyncCurrentAsset();
            EditorGUIUtility.PingObject(asset);

            m_Message = $"已创建 {path}。";
            m_MessageType = MessageType.Info;
        }

        private void SyncCurrentAsset()
        {
            if (m_Asset != null)
            {
                m_Asset.ResizeIfNeeded();
            }

            CurrentAsset = m_Asset;
            MaskRevision++;
        }

        /// <summary>资产改动后统一落盘标记与重绘。</summary>
        private void NotifyAssetChanged()
        {
            EditorUtility.SetDirty(m_Asset);
            MaskRevision++;
            SceneView.RepaintAll();
            Repaint();
        }
    }
}
