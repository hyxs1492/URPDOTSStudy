using DOTSUtils.Benchmark;
using DOTSUtils.RenderBridge;
using DOTSUtils.Rvo;
using DOTSUtils.Visibility;
using Unity.Entities;

public class ShipController : DotsRenderAuthoring
{
    protected override void OnEnable()
    {
    }

    public override void OnCreateEntitySuccess(Entity e)
    {
        base.OnCreateEntitySuccess(e);

        // 与两条生成路径共用同一份组件定义（见 BenchmarkShipComponents）。
        AddComponent<BenchmarkShipTag>();
        AddComponent<BenchmarkMotion>();
        SetComponent(BenchmarkShipComponents.CreateMotion((uint)UnityEngine.Random.Range(1, int.MaxValue)));
        AddComponent<DOTSUtils.Spatial.SpatialIndexed>();   // 统一的索引 tag（树由 Spatial 模块按配置维护
        // RVO：挂上 RvoAgent 才会进 RvoSolveSystem 的查询；期望速度由 BenchmarkRandomMoveSystem 每帧填。
        AddComponent<RvoAgent>();
        SetComponent(BenchmarkShipComponents.CreateAgent(BenchmarkRandomMoveSystem.MoveSpeed));

        // 可见性标记：让 Visibility 模块在视口外把它标成被裁剪。
        AddComponent<VisibilityCulledTag>();
    }
}
