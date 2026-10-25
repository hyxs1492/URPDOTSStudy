using System.Collections.Generic;
using DOTSUtils.Worlds;
using Unity.Entities;

namespace DOTSUtils.RenderBridge
{

    /// <summary>
    /// RenderBridge 模块。继承 <see cref="DotsModule"/> 即被自动发现，默认 World 会自动装载它。
    /// </summary>
    public sealed class DotsRenderBridgeModule : DotsModule
    {
        public override string Name => "RenderBridge";

        /// <summary>
        /// 排序键：0。
        /// </summary>
        public override int SortKey => 0;

        public override IReadOnlyList<string> Templates => new[] { "Default" };

        public override IReadOnlyList<DotsModuleGroupDescriptor> Groups => new[]
        {
            new DotsModuleGroupDescriptor(typeof(RenderBridgeConversionGroup),
                typeof(DotsRenderConversionSystem)),
            new DotsModuleGroupDescriptor(typeof(RenderBridgeInputGroup),
                typeof(DotsRenderTransformSyncSystem)),
            new DotsModuleGroupDescriptor(typeof(RenderBridgeOutputGroup),
                typeof(DotsRenderTransformPushSystem)),
        };
    }

    /// <summary>
    /// RenderBridge 的转换组：消费转换队列、建立实体。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class RenderBridgeConversionGroup : DotsModuleGroup { }

    /// <summary>
    /// RenderBridge 的输入组：帧初 GameObject → 实体。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial class RenderBridgeInputGroup : DotsModuleGroup { }

    /// <summary>
    /// RenderBridge 的输出组：帧末 实体 → GameObject。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial class RenderBridgeOutputGroup : DotsModuleGroup { }

}
