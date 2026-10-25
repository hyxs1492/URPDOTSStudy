using Unity.Entities;

namespace DOTSUtils.RenderBridge
{
    /// <summary>
    /// 标记「这棵实体树由 <see cref="DotsRenderSpawner.Spawn(GameObject,UnityEngine.Vector3,UnityEngine.Quaternion,float)"/>
    /// 从预制体直接生成」，与「场景实例转换而来」的实体区分开。
    /// 只有带这个组件的实体能被 <see cref="DotsRenderSpawner.Destroy"/> 销毁。
    ///
    /// 空组件：只是来源标签。变换、层级与渲染数据都在其他组件里。
    /// </summary>
    public readonly struct DotsRenderPrefabInstance : IComponentData
    {
    }
}
