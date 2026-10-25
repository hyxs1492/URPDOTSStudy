using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace DOTSUtils.Pathfinding
{
    /// <summary>
    /// 网格来源组件：把 <see cref="AStarGridAsset"/> 里的参数与掩码推给运行时
    /// （<see cref="AStarSettings"/>），并在没有运行时网格时按资产现建一份给调用方使用。
    /// </summary>
    [AddComponentMenu("DOTSUtils/Pathfinding/A* Grid Source")]
    public sealed class AStarGridSource : MonoBehaviour
    {
        [Tooltip("网格资产；启用时把它的参数与掩码交给 AStarSettings，A* 模块按这份参数建网格。")]
        public AStarGridAsset GridAsset;

        private void OnEnable()
        {
            if (GridAsset == null)
            {
                return;
            }

            GridAsset.ResizeIfNeeded();
            GridAsset.PushToSettings();
        }

        /// <summary>取运行时网格；返回 false 表示既没有运行时网格也没有可用的资产。</summary>
        public bool TryGetRuntimeGrid(out AStarGrid grid)
        {
            return TryGetRuntimeGrid(out grid, out _);
        }

        /// <summary>
        /// 取运行时网格。优先用默认 World 里 A* 模块建好的网格，此时 <paramref name="owned"/> 为 false；
        /// 没有时按 <see cref="GridAsset"/> 现建一份，此时 <paramref name="owned"/> 为 true，调用方负责
        /// <see cref="AStarGrid.Dispose"/>。
        /// </summary>
        public bool TryGetRuntimeGrid(out AStarGrid grid, out bool owned)
        {
            owned = false;
            grid = default;

            if (AStarAccess.TryGetGrid(World.DefaultGameObjectInjectionWorld, out grid))
            {
                return true;
            }

            grid = default;
            if (GridAsset == null)
            {
                return false;
            }

            GridAsset.ResizeIfNeeded();
            grid = new AStarGrid(GridAsset.ToConfig(), GridAsset.Mask, Allocator.Persistent);
            owned = true;
            return true;
        }
    }
}
