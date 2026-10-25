using Unity.Entities;
using UnityEngine;

namespace DOTSUtils.RenderBridge
{
    /// <summary>
    /// 把渲染实体指回它的 GameObject，并用 <see cref="Kind"/> 标注它在层级里的角色。
    /// </summary>
    public struct DotsRenderLink : IComponentData
    {
        public UnityObjectRef<GameObject> Target;
        public DotsRenderTargetKind Kind;
    }
}
