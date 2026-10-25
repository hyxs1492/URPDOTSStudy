using Unity.Entities;

namespace DOTSUtils.Worlds
{
    /// <summary>
    /// 模块组的统一基类。具体组各自显式标注 <c>[DisableAutoCreation]</c>（该特性不继承）与
    /// <c>[UpdateInGroup(typeof(父组), OrderFirst/Last = ...)]</c> 声明挂载点。
    /// </summary>
    public abstract partial class DotsModuleGroup : ComponentSystemGroup
    {
    }
}
