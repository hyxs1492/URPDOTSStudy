using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace DOTSUtils.Pathfinding.EditorTools
{
    /// <summary>
    /// A* 网格可视化的小工具：把网格外框、格线与阻挡格摊成线段顶点，并复用线段缓冲。
    /// 只做「几何 → 顶点」的换算，不碰 Handles；线段以成对顶点追加（<c>Handles.DrawLines</c> 的约定）。
    /// </summary>
    internal static class AStarDebugDrawUtility
    {
        /// <summary>网格外框（xz 平面上的矩形）的 4 条边 → 8 个顶点。</summary>
        public static void FillGridOutline(in AStarConfig config, int2 size, float height, List<Vector3> lines)
        {
            if (size.x <= 0 || size.y <= 0)
            {
                return;
            }

            var cellSize = math.max(config.CellSize, 1e-3f);
            var minX = config.Bounds.Center.x - config.Bounds.Extents.x;
            var minZ = config.Bounds.Center.z - config.Bounds.Extents.z;
            var maxX = minX + size.x * cellSize;
            var maxZ = minZ + size.y * cellSize;

            AddLine(lines, new Vector3(minX, height, minZ), new Vector3(maxX, height, minZ));
            AddLine(lines, new Vector3(maxX, height, minZ), new Vector3(maxX, height, maxZ));
            AddLine(lines, new Vector3(maxX, height, maxZ), new Vector3(minX, height, maxZ));
            AddLine(lines, new Vector3(minX, height, maxZ), new Vector3(minX, height, minZ));
        }

        /// <summary>每条格线的两个方向 → 顶点。</summary>
        public static void FillCellLines(in AStarConfig config, int2 size, float height, List<Vector3> lines)
        {
            if (size.x <= 0 || size.y <= 0)
            {
                return;
            }

            var cellSize = math.max(config.CellSize, 1e-3f);
            var minX = config.Bounds.Center.x - config.Bounds.Extents.x;
            var minZ = config.Bounds.Center.z - config.Bounds.Extents.z;
            var maxX = minX + size.x * cellSize;
            var maxZ = minZ + size.y * cellSize;

            for (var x = 0; x <= size.x; x++)
            {
                var px = minX + x * cellSize;
                AddLine(lines, new Vector3(px, height, minZ), new Vector3(px, height, maxZ));
            }
            for (var z = 0; z <= size.y; z++)
            {
                var pz = minZ + z * cellSize;
                AddLine(lines, new Vector3(minX, height, pz), new Vector3(maxX, height, pz));
            }
        }

        /// <summary>
        /// 每格一个方块轮廓（4 条边 → 8 个顶点），最多写 <paramref name="maxCells"/> 个格；
        /// 返回整个网格里阻挡格的总数。掩码为 <c>null</c> 时返回 0。
        /// </summary>
        public static int FillBlockedCellOutlines(in AStarConfig config, int2 size, byte[] mask, float height,
                                                 int maxCells, List<Vector3> lines)
        {
            lines.Clear();
            if (mask == null || size.x <= 0 || size.y <= 0)
            {
                return 0;
            }

            var cellSize = math.max(config.CellSize, 1e-3f);
            var minX = config.Bounds.Center.x - config.Bounds.Extents.x;
            var minZ = config.Bounds.Center.z - config.Bounds.Extents.z;

            var blocked = 0;
            var drawn = 0;
            for (var z = 0; z < size.y; z++)
            {
                for (var x = 0; x < size.x; x++)
                {
                    var index = z * size.x + x;
                    if (index < 0 || index >= mask.Length || mask[index] != 0)
                    {
                        continue;
                    }

                    blocked++;
                    if (drawn >= maxCells)
                    {
                        continue;
                    }

                    var a = new Vector3(minX + x * cellSize, height, minZ + z * cellSize);
                    var b = new Vector3(a.x + cellSize, height, a.z);
                    var c = new Vector3(b.x, height, a.z + cellSize);
                    var d = new Vector3(a.x, height, c.z);
                    AddLine(lines, a, b);
                    AddLine(lines, b, c);
                    AddLine(lines, c, d);
                    AddLine(lines, d, a);
                    drawn++;
                }
            }

            return blocked;
        }

        /// <summary>一格的 4 个角（世界坐标），供 <c>Handles.DrawSolidRectangleWithOutline</c> 使用。</summary>
        public static void FillCellQuad(in AStarConfig config, int2 cell, float height, Vector3[] quad)
        {
            var cellSize = math.max(config.CellSize, 1e-3f);
            var minX = config.Bounds.Center.x - config.Bounds.Extents.x + cell.x * cellSize;
            var minZ = config.Bounds.Center.z - config.Bounds.Extents.z + cell.y * cellSize;
            var maxX = minX + cellSize;
            var maxZ = minZ + cellSize;

            quad[0] = new Vector3(minX, height, minZ);
            quad[1] = new Vector3(minX, height, maxZ);
            quad[2] = new Vector3(maxX, height, maxZ);
            quad[3] = new Vector3(maxX, height, minZ);
        }

        /// <summary>把点列表落到数组上（长度一致时复用同一个数组）。</summary>
        public static Vector3[] ToArray(List<Vector3> points, ref Vector3[] buffer)
        {
            if (buffer == null || buffer.Length != points.Count)
            {
                buffer = new Vector3[points.Count];
            }
            for (var i = 0; i < points.Count; i++)
            {
                buffer[i] = points[i];
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
