using Unity.Mathematics;

namespace DOTSUtils.Rvo
{
    /// <summary>
    /// 一条 ORCA 半平面。<b>可行侧是 <c>det(Direction, X - Point) &gt;= 0</c></b>（即 <c>X</c> 在半平面内），
    /// 这是 RVO2 的原始约定。
    ///
    /// 16 字节，可直接进 <c>FixedList512Bytes&lt;OrcaLine&gt;</c>。
    /// </summary>
    public struct OrcaLine
    {
        /// <summary>
        /// 半平面经过的点。
        /// </summary>
        public float2 Point;

        /// <summary>
        /// 半平面的方向（单位向量）。
        /// </summary>
        public float2 Direction;
    }
}
