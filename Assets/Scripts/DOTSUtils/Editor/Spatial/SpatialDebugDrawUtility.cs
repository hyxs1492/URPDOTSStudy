using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace DOTSUtils.Spatial.EditorTools
{
    /// <summary>
    /// 可视化绘制的小工具：把包围盒摊成线段、按深度取色、复用线段缓冲。
    /// 只做「几何 → 顶点」的换算，不碰 Handles / Gizmos；线段以成对顶点追加（<c>Handles.DrawLines</c> 的约定）。
    /// </summary>
    public static class SpatialDebugDrawUtility
    {
        /// <summary>按深度着色：深度 0 → 红，最深 → 紫（HSV 扫 0.7 圈）。</summary>
        public static Color DepthColor(int depth, int maxDepth)
        {
            var t = maxDepth <= 0 ? 0f : Mathf.Clamp01(depth / (float)maxDepth);
            return Color.HSVToRGB(t * 0.7f, 0.8f, 0.9f);
        }

        /// <summary>立方体的 12 条棱 → 24 个顶点。</summary>
        public static void FillBoxLines(in AABB box, List<Vector3> lines)
        {
            var min = box.Min;
            var max = box.Max;
            var corners = new Vector3[8];
            for (var i = 0; i < 8; i++)
            {
                corners[i] = new Vector3(
                    (i & 1) != 0 ? max.x : min.x,
                    (i & 2) != 0 ? max.y : min.y,
                    (i & 4) != 0 ? max.z : min.z);
            }

            // 每个角只连「该位为 0」的那三个邻居。
            for (var i = 0; i < 8; i++)
            {
                if ((i & 1) == 0)
                {
                    AddLine(lines, corners[i], corners[i | 1]);
                }
                if ((i & 2) == 0)
                {
                    AddLine(lines, corners[i], corners[i | 2]);
                }
                if ((i & 4) == 0)
                {
                    AddLine(lines, corners[i], corners[i | 4]);
                }
            }
        }

        /// <summary>矩形（xz 平面）的 4 条边 → 8 个顶点，画在 <c>y = height</c> 平面上。</summary>
        public static void FillRectLines(in AABB2D rect, float height, List<Vector3> lines)
        {
            var min = rect.Min;
            var max = rect.Max;

            var a = new Vector3(min.x, height, min.y);
            var b = new Vector3(max.x, height, min.y);
            var c = new Vector3(max.x, height, max.y);
            var d = new Vector3(min.x, height, max.y);

            AddLine(lines, a, b);
            AddLine(lines, b, c);
            AddLine(lines, c, d);
            AddLine(lines, d, a);
        }

        /// <summary>三轴十字（标一个点）→ 3 段 6 个顶点。</summary>
        public static void FillCross(float3 center, float size, List<Vector3> lines)
        {
            var p = new Vector3(center.x, center.y, center.z);
            var d = new Vector3(size, 0f, 0f);
            AddLine(lines, p - d, p + d);
            d = new Vector3(0f, size, 0f);
            AddLine(lines, p - d, p + d);
            d = new Vector3(0f, 0f, size);
            AddLine(lines, p - d, p + d);
        }

        /// <summary>把线段缓冲落到 <paramref name="buffer"/> 上（长度一致时复用同一个数组）。</summary>
        public static Vector3[] ToArray(List<Vector3> lines, ref Vector3[] buffer)
        {
            if (buffer == null || buffer.Length != lines.Count)
            {
                buffer = new Vector3[lines.Count];
            }
            for (var i = 0; i < lines.Count; i++)
            {
                buffer[i] = lines[i];
            }
            return buffer;
        }

        private static void AddLine(List<Vector3> lines, Vector3 a, Vector3 b)
        {
            lines.Add(a);
            lines.Add(b);
        }
    }
}
