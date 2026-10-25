using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace DOTSUtils.Pathfinding.EditorTools
{
    /// <summary>
    /// SceneView 叠加绘制：把 <see cref="AStarGridEditorWindow"/> 留在静态字段里的网格、掩码与示例路径
    /// 画到场景里。开关与数据都来自窗口，这里只负责画。
    /// </summary>
    [InitializeOnLoad]
    internal static class AStarSceneViewOverlay
    {
        /// <summary>一次最多画多少个阻挡格。</summary>
        internal const int MaxDrawnCells = 4000;

        /// <summary>阻挡格不超过这个数量时额外画实心面。</summary>
        internal const int MaxSolidCells = 600;

        /// <summary>总格数不超过这个数量时才画每条格线。</summary>
        internal const int MaxCellLineCells = 2500;

        private const float k_PathWidth = 4f;

        private static readonly List<Vector3> s_GridLines = new List<Vector3>();
        private static readonly List<Vector3> s_BlockedLines = new List<Vector3>();
        private static readonly Vector3[] s_Quad = new Vector3[4];

        private static Vector3[] s_GridBuffer;
        private static Vector3[] s_BlockedBuffer;
        private static Vector3[] s_PathBuffer;

        private static AStarGridAsset s_CachedAsset;
        private static int s_CachedMaskHash = -1;
        private static int s_CachedWidth = -1;
        private static int s_CachedHeight = -1;
        private static int s_CachedRevision = -1;
        private static bool s_CachedCellLines;
        private static float s_CachedHeightOffset = float.NaN;
        private static int s_CachedBlockedCount;

        static AStarSceneViewOverlay()
        {
            SceneView.duringSceneGui -= OnSceneGui;
            SceneView.duringSceneGui += OnSceneGui;
        }

        private static void OnSceneGui(SceneView view)
        {
            var asset = AStarGridEditorWindow.CurrentAsset;
            var showGrid = AStarGridEditorWindow.ShowGrid;
            var showMask = AStarGridEditorWindow.ShowMask;
            var showPath = AStarGridEditorWindow.ShowPath;
            if (asset == null || (!showGrid && !showMask && !showPath))
            {
                return;
            }

            var previousColor = Handles.color;
            try
            {
                EnsureCache(asset);

                if (showGrid)
                {
                    DrawGridLines();
                }
                if (showMask)
                {
                    DrawMask(asset);
                }
                if (showPath)
                {
                    DrawPath(asset);
                }
            }
            finally
            {
                Handles.color = previousColor;
            }
        }

        /// <summary>网格外框与格线。</summary>
        private static void DrawGridLines()
        {
            if (s_GridLines.Count == 0)
            {
                return;
            }

            Handles.color = new Color(0.35f, 0.85f, 1f, 0.85f);
            Handles.DrawLines(AStarDebugDrawUtility.ToArray(s_GridLines, ref s_GridBuffer));
        }

        /// <summary>阻挡格：数量少时画实心面 + 轮廓，数量多时只画轮廓。</summary>
        private static void DrawMask(AStarGridAsset asset)
        {
            if (s_BlockedLines.Count == 0)
            {
                return;
            }

            var config = asset.ToConfig();
            var height = config.Bounds.Center.y + AStarGridEditorWindow.DrawHeightOffset;
            var size = asset.Size;

            if (s_CachedBlockedCount <= MaxSolidCells)
            {
                var face = new Color(1f, 0.25f, 0.2f, 0.25f);
                var outline = new Color(1f, 0.35f, 0.3f, 0.9f);
                var drawn = 0;
                for (var z = 0; z < size.y && drawn < MaxDrawnCells; z++)
                {
                    for (var x = 0; x < size.x && drawn < MaxDrawnCells; x++)
                    {
                        var cell = new int2(x, z);
                        if (asset.IsWalkable(cell))
                        {
                            continue;
                        }

                        AStarDebugDrawUtility.FillCellQuad(config, cell, height, s_Quad);
                        Handles.DrawSolidRectangleWithOutline(s_Quad, face, outline);
                        drawn++;
                    }
                }
            }

            Handles.color = new Color(1f, 0.35f, 0.3f, 0.9f);
            Handles.DrawLines(AStarDebugDrawUtility.ToArray(s_BlockedLines, ref s_BlockedBuffer));
        }

        /// <summary>示例路径与起点 / 终点标记。</summary>
        private static void DrawPath(AStarGridAsset asset)
        {
            if (!AStarGridEditorWindow.HasSample)
            {
                return;
            }

            var markerSize = Mathf.Max(0.25f, Mathf.Max(asset.CellSize, 1e-3f) * 0.5f);

            Handles.color = new Color(0.2f, 1f, 0.35f, 1f);
            Handles.SphereHandleCap(0, AStarGridEditorWindow.LastStart, Quaternion.identity, markerSize, EventType.Repaint);

            Handles.color = new Color(1f, 0.45f, 0.2f, 1f);
            Handles.SphereHandleCap(0, AStarGridEditorWindow.LastEnd, Quaternion.identity, markerSize, EventType.Repaint);

            var path = AStarGridEditorWindow.LastPath;
            if (path == null || path.Count < 2)
            {
                return;
            }

            Handles.color = new Color(1f, 0.95f, 0.2f, 1f);
            var points = AStarDebugDrawUtility.ToArray(path, ref s_PathBuffer);
            Handles.DrawAAPolyLine(k_PathWidth, points);
        }

        /// <summary>按资产、掩码内容与绘制开关核对几何缓存。</summary>
        private static void EnsureCache(AStarGridAsset asset)
        {
            var size = asset.Size;
            var config = asset.ToConfig();
            var height = config.Bounds.Center.y + AStarGridEditorWindow.DrawHeightOffset;
            var cellLines = AStarGridEditorWindow.ShowCellLines &&
                            size.x > 0 && size.y > 0 && size.x * size.y <= MaxCellLineCells;
            var maskHash = MaskHash(asset.Mask);

            var dirty = s_CachedAsset != asset ||
                        s_CachedMaskHash != maskHash ||
                        s_CachedWidth != size.x ||
                        s_CachedHeight != size.y ||
                        s_CachedRevision != AStarGridEditorWindow.MaskRevision ||
                        s_CachedCellLines != cellLines ||
                        !Mathf.Approximately(s_CachedHeightOffset, AStarGridEditorWindow.DrawHeightOffset);
            if (!dirty)
            {
                return;
            }

            s_GridLines.Clear();
            if (size.x > 0 && size.y > 0)
            {
                AStarDebugDrawUtility.FillGridOutline(config, size, height, s_GridLines);
                if (cellLines)
                {
                    AStarDebugDrawUtility.FillCellLines(config, size, height, s_GridLines);
                }
                s_CachedBlockedCount = AStarDebugDrawUtility.FillBlockedCellOutlines(
                    config, size, asset.Mask, height, MaxDrawnCells, s_BlockedLines);
            }
            else
            {
                s_BlockedLines.Clear();
                s_CachedBlockedCount = 0;
            }

            s_CachedAsset = asset;
            s_CachedMaskHash = maskHash;
            s_CachedWidth = size.x;
            s_CachedHeight = size.y;
            s_CachedRevision = AStarGridEditorWindow.MaskRevision;
            s_CachedCellLines = cellLines;
            s_CachedHeightOffset = AStarGridEditorWindow.DrawHeightOffset;
        }

        /// <summary>掩码内容的摘要，掩码为 <c>null</c> 时返回 0。</summary>
        private static int MaskHash(byte[] mask)
        {
            if (mask == null)
            {
                return 0;
            }

            unchecked
            {
                var hash = (int)2166136261;
                for (var i = 0; i < mask.Length; i++)
                {
                    hash = (hash ^ mask[i]) * 16777619;
                }
                return hash;
            }
        }
    }
}
