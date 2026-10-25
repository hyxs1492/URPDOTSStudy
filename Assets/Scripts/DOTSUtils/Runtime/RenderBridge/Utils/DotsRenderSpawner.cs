using DOTSUtils.Worlds;
using Unity.Entities;
using UnityEngine;

namespace DOTSUtils.RenderBridge
{
    /// <summary>
    /// 从预制体直接生成实体树的入口（**不实例化 GameObject**）。
    ///
    /// 与场景实例路径的分工：
    /// <list type="bullet">
    /// <item>场景实例路径：GameObject 是权威输入端，参与双向变换同步，由 <c>Disposal</c> 决定原组件怎么处理。</item>
    /// <item>本路径：**完全没有 GameObject**，变换纯由实体侧驱动，也绝不触碰预制体里的组件。</item>
    /// </list>
    /// 需要 GameObject 驱动（挂物理、跑 MonoBehaviour 逻辑）就继续用场景实例路径。
    /// </summary>
    public static class DotsRenderSpawner
    {
        /// <summary>
        /// 用给定变换从预制体生成实体树。
        /// </summary>
        public static Entity Spawn(GameObject prefab, Vector3 position, Quaternion rotation, float uniformScale = 1f)
            => Spawn(prefab, position, rotation, uniformScale, null, null);

        /// <summary>
        /// 用给定变换从预制体生成实体树，并覆盖材质 / 指定池。
        /// 返回父实体；失败返回 <see cref="Entity.Null"/>。必须在主线程调用，且需要 Play 模式。
        /// </summary>
        /// <param name="overrideMaterials">
        /// 非 null 时用它替换每个渲染目标的材质（benchmark 的「每艘随机取一个材质」靠它）。
        /// </param>
        /// <param name="pool">非 null 时用它取 <c>RenderMeshArray</c> 条目；默认用系统的池。</param>
        public static Entity Spawn(GameObject prefab, Vector3 position, Quaternion rotation, float uniformScale,
            Material[] overrideMaterials, RenderMeshPool pool)
        {
            var system = ResolveSystem();
            if (system == null)
            {
                Debug.LogWarning("[DotsRender] Spawn 需要 Play 模式（没有 EntityManager）。");
                return Entity.Null;
            }

            return system.SpawnFromPrefab(prefab, position, rotation, uniformScale, overrideMaterials, pool);
        }

        /// <summary>
        /// 用预制体自带的 transform 值生成实体树。
        /// </summary>
        public static Entity Spawn(GameObject prefab)
        {
            if (prefab == null)
            {
                Debug.LogWarning("[DotsRender] Spawn 收到空预制体。");
                return Entity.Null;
            }

            var t = prefab.transform;
            // 缩放必须等比，按 x 轴取值（同 DotsRenderConversionSystem.UniformScale 口径）。
            return Spawn(prefab, t.position, t.rotation, t.lossyScale.x);
        }

        /// <summary>
        /// 销毁由 <see cref="Spawn(GameObject,Vector3,Quaternion,float)"/> 生成的整棵实体树。
        /// 传入的不是本 API 的产物（没有 <see cref="DotsRenderPrefabInstance"/>）时返回 false 且不做任何事。
        /// </summary>
        public static bool Destroy(Entity parentEntity)
        {
            var system = ResolveSystem();
            return system != null && system.DestroySpawned(parentEntity);
        }

        private static DotsRenderConversionSystem ResolveSystem()
        {
            var world = DotsWorldManager.CurrentWorld;
            return world != null && world.IsCreated
                ? world.GetExistingSystemManaged<DotsRenderConversionSystem>()
                : null;
        }
    }
}
