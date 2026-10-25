using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace DOTSUtils.Spatial
{
    /// <summary>
    /// 范围查询（<b>只读</b>）。树是 unmanaged struct，可直接拷进 job；只读查询可以多 job 并行，
    /// 各自持自己的结果缓冲。<b>不要</b>在本 job 里做写操作 —— 见 <see cref="SpatialTreeApplyOpsJob"/>。
    /// </summary>
    [BurstCompile]
    public struct SpatialTreeRangeQueryJob : IJob
    {
        public NativeSpatialTree Tree;
        public AABB Query;
        public NativeList<SpatialElement> Results;

        public void Execute() => Tree.RangeQuery(Query, ref Results);
    }

    /// <summary>
    /// 顺序应用一批写操作（<b>必须单线程</b>：树不是并发安全结构）。
    ///
    /// <c>Schedule()</c> 与 <c>Run()</c> 都会把 job 实例<b>按值拷走</b>，执行完之后<b>不要</b>从
    /// <c>job.Tree</c> 字段读结果；结果树一律通过 <see cref="ResultTree"/> 带出。
    ///
    /// <see cref="NativeSpatialTree"/> 是值语义 struct：元素池与节点数组是 <c>UnsafeList</c> 指向的内存
    /// （两边共享），但 <c>ElementCount</c> / <c>LeafCount</c> / <c>NextFreeSlot</c> 这些<b>值字段</b>
    /// 只更新在 job 内部的拷贝里；必须以 <see cref="ResultTree"/> 为准，由调用方写回组件。
    /// </summary>
    [BurstCompile]
    public struct SpatialTreeApplyOpsJob : IJob
    {
        public NativeSpatialTree Tree;

        [ReadOnly] public NativeArray<SpatialOp> Ops;

        /// <summary>
        /// 每个 op 的结果：Insert 返回新句柄；Update / Remove 成功回填原句柄，失败为 Invalid。
        /// </summary>
        public NativeArray<SpatialElementRef> Results;

        /// <summary>
        /// 输出：应用完所有操作之后的树（含更新过的计数字段）。
        /// </summary>
        public NativeReference<NativeSpatialTree> ResultTree;

        public void Execute()
        {
            if (!Results.IsCreated || Results.Length < Ops.Length)
                throw new System.InvalidOperationException("SpatialTreeApplyOpsJob.Results 长度必须 >= Ops.Length。");

            for (var i = 0; i < Ops.Length; i++)
            {
                var op = Ops[i];
                switch (op.Type)
                {
                    case SpatialOp.Kind.Insert:
                        Results[i] = Tree.Insert(op.Element);
                        break;

                    case SpatialOp.Kind.Update:
                        Results[i] = Tree.Update(op.Reference, op.NewPosition)
                            ? op.Reference
                            : SpatialElementRef.Invalid;
                        break;

                    default:
                        Results[i] = Tree.Remove(op.Reference) ? op.Reference : SpatialElementRef.Invalid;
                        break;
                }
            }

            ResultTree.Value = Tree;
        }
    }

    /// <summary>
    /// 清空 + 一次性 bulk 插入（快路径）。被接受的元素数写进 <see cref="Accepted"/>，
    /// 结果树写进 <see cref="ResultTree"/>。
    /// </summary>
    [BurstCompile]
    public struct SpatialTreeBuildJob : IJob
    {
        public NativeSpatialTree Tree;

        [ReadOnly] public NativeArray<SpatialElement> Elements;

        /// <summary>
        /// 输出：被接受的元素数（越界/NaN 的不计入）。
        /// </summary>
        public NativeReference<int> Accepted;

        /// <summary>
        /// 输出：构建完之后的树。
        /// </summary>
        public NativeReference<NativeSpatialTree> ResultTree;

        public void Execute()
        {
            Accepted.Value = Tree.ClearAndBulkInsert(Elements);
            ResultTree.Value = Tree;
        }
    }
}
