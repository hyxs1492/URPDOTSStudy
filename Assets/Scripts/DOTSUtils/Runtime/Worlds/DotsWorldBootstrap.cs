using Unity.Entities;

namespace DOTSUtils.Worlds
{
    /// <summary>
    /// 接管默认 World 的创建：按 "Default" 模板装配模块，World 名沿用 Unity 传入的 <c>defaultWorldName</c>。
    /// Editor World 不走这里（<c>Initialize(editorWorld: true)</c> 不调用 ICustomBootstrap）。
    /// </summary>
    public sealed class DotsWorldBootstrap : ICustomBootstrap
    {
        public bool Initialize(string defaultWorldName)
        {
            var world = DotsWorldManager.CreateWorld(defaultWorldName, "Default");
            World.DefaultGameObjectInjectionWorld = world;
            return true;
        }
    }
}
