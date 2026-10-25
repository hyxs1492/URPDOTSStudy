using Unity.Collections;
using Unity.Mathematics;

namespace DOTSUtils.Rvo
{
    /// <summary>
    /// 静态障碍物的拓扑补全：把「扁平顶点位置 + 多边形分段」写成
    /// <see cref="RvoObstacleVertex"/>（含 <c>Prev</c>/<c>Next</c>/<c>UnitDir</c>/<c>IsConvex</c>）
    /// 与 <see cref="RvoObstaclePolygon"/>。语义照 RVO2 <c>RVOSimulator::addObstacle</c>：
    /// <list type="bullet">
    /// <item><c>Prev</c>/<c>Next</c> 是同一扁平顶点数组内的下标，多边形内首尾相接；
    /// 顶点数为 2 时两者都指向另一个顶点（<c>[A, B]</c> 的线段是 <c>A→B</c> 与 <c>B→A</c> 两条边）；</item>
    /// <item><c>UnitDir = normalize(Next.Point - Point)</c>；零长边取 <c>float2.zero</c>；</item>
    /// <item>顶点数为 2 时 <c>IsConvex = 1</c>，否则取
    /// <c>det(Point - Prev.Point, Next.Point - Point) &gt;= 0</c>（左转为凸）。</item>
    /// </list>
    /// 顶点数为 0 / 1 的多边形不是障碍物，直接跳过；非有限的顶点位置按 <c>float2.zero</c> 写入。
    /// 纯函数式写入：只往两个列表末尾追加，不做托管分配。
    /// </summary>
    public static class RvoObstacleBuilder
    {
        /// <summary>
        /// 追加一个多边形：<paramref name="points"/> 的 <c>[start, start + count)</c> 段。
        /// 越界或 <paramref name="count"/> &lt; 2 时什么都不做。
        /// </summary>
        public static void Append(NativeList<RvoObstacleVertex> vertices, NativeList<RvoObstaclePolygon> polygons,
                                  NativeArray<float2> points, int start, int count)
        {
            if (!vertices.IsCreated || !polygons.IsCreated || !points.IsCreated)
            {
                return;
            }
            if (count < 2 || start < 0 || start + count > points.Length)
            {
                return;
            }

            var baseIndex = vertices.Length;
            for (var i = 0; i < count; i++)
            {
                vertices.Add(new RvoObstacleVertex
                {
                    Point = Sanitize(points[start + i]),
                    UnitDir = float2.zero,
                    Prev = baseIndex + (i == 0 ? count - 1 : i - 1),
                    Next = baseIndex + (i == count - 1 ? 0 : i + 1),
                    IsConvex = 0,
                });
            }

            for (var i = 0; i < count; i++)
            {
                var index = baseIndex + i;
                var vertex = vertices[index];
                vertex.UnitDir = UnitDirection(vertex.Point, vertices[vertex.Next].Point);
                vertex.IsConvex = count == 2
                    ? (byte)1
                    : (byte)(OrcaSolver.Det(vertex.Point - vertices[vertex.Prev].Point,
                                            vertices[vertex.Next].Point - vertex.Point) >= 0f ? 1 : 0);
                vertices[index] = vertex;
            }

            polygons.Add(new RvoObstaclePolygon { Start = baseIndex, Count = count });
        }

        /// <summary>
        /// 追加一条线段的二顶点多边形：顶点 <c>[a, b]</c>，两条边 <c>a→b</c> 与 <c>b→a</c>，两个顶点都是凸顶点。
        /// </summary>
        public static void AppendSegment(NativeList<RvoObstacleVertex> vertices,
                                        NativeList<RvoObstaclePolygon> polygons, float2 a, float2 b)
        {
            if (!vertices.IsCreated || !polygons.IsCreated)
            {
                return;
            }

            a = Sanitize(a);
            b = Sanitize(b);
            var baseIndex = vertices.Length;
            vertices.Add(new RvoObstacleVertex
            {
                Point = a,
                UnitDir = UnitDirection(a, b),
                Prev = baseIndex + 1,
                Next = baseIndex + 1,
                IsConvex = 1,
            });
            vertices.Add(new RvoObstacleVertex
            {
                Point = b,
                UnitDir = UnitDirection(b, a),
                Prev = baseIndex,
                Next = baseIndex,
                IsConvex = 1,
            });
            polygons.Add(new RvoObstaclePolygon { Start = baseIndex, Count = 2 });
        }

        /// <summary>非有限的位置换成 <c>float2.zero</c>（保证拓扑与后续几何都是有限值）。</summary>
        public static float2 Sanitize(float2 point) => math.all(math.isfinite(point)) ? point : float2.zero;

        /// <summary>
        /// <c>normalize(to - from)</c>；长度平方不超过 <see cref="OrcaSolver.Epsilon"/> 时返回 <c>float2.zero</c>。
        /// </summary>
        public static float2 UnitDirection(float2 from, float2 to)
        {
            var direction = to - from;
            var lengthSq = math.lengthsq(direction);
            return lengthSq > OrcaSolver.Epsilon ? direction * math.rsqrt(lengthSq) : float2.zero;
        }
    }
}
