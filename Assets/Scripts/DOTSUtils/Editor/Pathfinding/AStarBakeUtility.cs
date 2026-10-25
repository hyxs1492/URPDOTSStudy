using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DOTSUtils.Pathfinding.EditorTools
{
    /// <summary>
    /// 一次「从场景烘焙」的结果：掩码 + 尺寸 + 可走格数。
    /// </summary>
    public struct AStarBakeResult
    {
        /// <summary>烘焙出来的可走掩码（行主序，1 = 可走）；失败时为 <c>null</c>。</summary>
        public byte[] Mask;

        /// <summary>掩码宽度（格，对应世界 x）。</summary>
        public int Width;

        /// <summary>掩码高度（格，对应世界 z）。</summary>
        public int Height;

        /// <summary>总格数。</summary>
        public int CellCount;

        /// <summary>可走格数。</summary>
        public int WalkableCount;

        /// <summary>阻挡格数。</summary>
        public int BlockedCount => CellCount - WalkableCount;
    }

    /// <summary>
    /// A* 网格的场景烘焙入口：按资产的参数逐格向下射线，把结果整理成一份掩码。
    ///
    /// 判据与「A* 寻路网格」窗口里那套完全一致：每格从「格中心 + <paramref name="startHeight"/>」向下发射线，
    /// 长度 <c>startHeight + maxDrop</c>；
    /// <list type="bullet">
    /// <item><c>hitWalkable = true</c>：命中且落差在 <c>[0, maxDrop]</c> 内算可走，其余算阻挡；</item>
    /// <item><c>hitWalkable = false</c>：命中且落差在 <c>[0, maxDrop]</c> 内算阻挡，其余算可走。</item>
    /// </list>
    /// 射线打的是**已加载场景**的物理世界，所以调用前请把要烘焙的场景设为当前场景。
    /// </summary>
    public static class AStarBakeUtility
    {
        /// <summary>
        /// 按 <paramref name="asset"/> 自己的范围 / 格边长烘焙一份掩码，**不修改资产**。
        /// 参数不合法或场景没加载时返回 <c>false</c>。
        /// </summary>
        public static bool Bake(Scene scene, AStarGridAsset asset, LayerMask layers, float startHeight,
                                float maxDrop, bool hitWalkable, out AStarBakeResult result)
        {
            result = default;

            if (asset == null)
            {
                Debug.LogWarning("[AStarBake] 没有指定网格资产。");
                return false;
            }
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogWarning("[AStarBake] 要烘焙的场景不是已加载的场景：射线打不到它的碰撞体。");
                return false;
            }

            asset.ResizeIfNeeded();

            // 把场景里刚摆好 / 刚改过的 Transform 同步进物理世界，保证射线打到的是当前位置的碰撞体。
            Physics.SyncTransforms();

            var size = asset.Size;
            var config = asset.ToConfig();
            var cellSize = math.max(config.CellSize, 1e-3f);
            var minX = config.Bounds.Center.x - config.Bounds.Extents.x;
            var minZ = config.Bounds.Center.z - config.Bounds.Extents.z;
            var start = math.max(0f, startHeight);
            var drop = math.max(0f, maxDrop);
            var rayLength = start + drop;

            var next = new byte[size.x * size.y];
            var walkableCount = 0;
            try
            {
                for (var z = 0; z < size.y; z++)
                {
                    EditorUtility.DisplayProgressBar("烘焙 A* 网格", $"第 {z + 1} / {size.y} 行",
                                                     (z + 1) / (float)size.y);

                    for (var x = 0; x < size.x; x++)
                    {
                        var origin = new Vector3(
                            minX + (x + 0.5f) * cellSize,
                            config.Bounds.Center.y + start,
                            minZ + (z + 0.5f) * cellSize);

                        var walkable = !hitWalkable;
                        if (Physics.Raycast(origin, Vector3.down, out var hit, rayLength,
                                            layers.value, QueryTriggerInteraction.Ignore))
                        {
                            var hitDrop = origin.y - hit.point.y;
                            var inRange = hitDrop >= 0f && hitDrop <= drop;
                            walkable = hitWalkable ? inRange : !inRange;
                        }

                        next[z * size.x + x] = walkable ? (byte)1 : (byte)0;
                        if (walkable)
                        {
                            walkableCount++;
                        }
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            result = new AStarBakeResult
            {
                Mask = next,
                Width = size.x,
                Height = size.y,
                CellCount = size.x * size.y,
                WalkableCount = walkableCount,
            };
            return true;
        }

        /// <summary>
        /// 烘焙并把掩码写回资产（记 Undo、标脏、刷新 SceneView）；返回是否成功。
        /// </summary>
        public static bool BakeAndApply(Scene scene, AStarGridAsset asset, LayerMask layers, float startHeight,
                                        float maxDrop, bool hitWalkable, out AStarBakeResult result)
        {
            if (!Bake(scene, asset, layers, startHeight, maxDrop, hitWalkable, out result))
            {
                return false;
            }

            Undo.RecordObject(asset, "烘焙 A* 网格");
            asset.Mask = result.Mask;
            EditorUtility.SetDirty(asset);
            SceneView.RepaintAll();

            Debug.Log($"[AStarBake] {asset.name}：{result.Width}×{result.Height} 格，可走 " +
                      $"{result.WalkableCount}/{result.CellCount}（阻挡 {result.BlockedCount}）；" +
                      $"层遮罩 {layers.value}、射线起点高 {startHeight}、最大落差 {maxDrop}、" +
                      $"命中算可走 = {hitWalkable}。");
            return true;
        }

        /// <summary>按当前活动场景烘焙并写回资产。</summary>
        public static bool BakeActiveSceneAndApply(AStarGridAsset asset, LayerMask layers, float startHeight,
                                                   float maxDrop, bool hitWalkable, out AStarBakeResult result)
            => BakeAndApply(SceneManager.GetActiveScene(), asset, layers, startHeight, maxDrop, hitWalkable,
                            out result);
    }
}
