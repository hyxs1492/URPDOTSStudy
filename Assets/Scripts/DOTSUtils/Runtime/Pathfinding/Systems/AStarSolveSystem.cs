using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;

namespace DOTSUtils.Pathfinding
{
    /// <summary>
    /// 一个请求的求解结果（批处理的中间产物）。
    /// </summary>
    public struct AStarSolveResult
    {
        /// <summary>结果状态。</summary>
        public AStarStatus Status;

        /// <summary>该路径在批输出里的起始下标。</summary>
        public int PathOffset;

        /// <summary>该路径的点数。</summary>
        public int PathLength;

        /// <summary>非 0 表示这一帧没有结果（请求版本没变，或受 <see cref="AStarConfig.MaxSolvesPerFrame"/> 限制留到下一帧）。</summary>
        public byte Skipped;
    }

    /// <summary>
    /// 逐帧处理带 <see cref="PathRequest"/> 的实体：把路径写进同实体的 <see cref="PathWaypoint"/> 缓冲，
    /// 结果写进 <see cref="PathStatus"/>。
    ///
    /// 请求与状态按 <c>ArchetypeChunk</c> 批量读取，求解是并行的（一个请求一个 job 下标），
    /// 工作区按 job 线程各一份；job 只写扁平结果，<see cref="PathWaypoint"/> 缓冲的写回在主线程完成。
    /// 网格只在 <see cref="AStarSettings.Version"/> 变化时重建。
    /// </summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(AStarGroup))]
    public partial class AStarSolveSystem : SystemBase
    {
        private EntityQuery m_RequestQuery;
        private Entity m_SingletonEntity;
        private AStarGrid m_Grid;
        private int m_BuiltVersion = int.MinValue;

        /// <summary>按 job 线程索引的内核工作区池。</summary>
        private NativeArray<AStarScratch> m_ScratchPool;

        /// <summary>按 job 线程索引的路径块池：本线程这一帧解出的路径依次拼在自己那块里。</summary>
        private NativeArray<UnsafeList<float3>> m_PathPool;

        /// <summary>一个请求的求解中间结果（job 写、主线程读）。</summary>
        private struct SolveSlot
        {
            /// <summary>结果状态。</summary>
            public AStarStatus Status;

            /// <summary>路径所在的那一块（= 写它的 job 线程索引）。</summary>
            public int Block;

            /// <summary>路径在块内的起始下标。</summary>
            public int BlockOffset;

            /// <summary>路径点数。</summary>
            public int Length;

            /// <summary>非 0 表示这个请求这一帧解过。</summary>
            public byte Solved;
        }

        protected override void OnCreate()
        {
            m_SingletonEntity = EntityManager.CreateEntity(typeof(AStarSingleton));
            EntityManager.SetName(m_SingletonEntity, "AStarSingleton");

            m_RequestQuery = GetEntityQuery(
                ComponentType.ReadOnly<PathRequest>(),
                ComponentType.ReadWrite<PathStatus>());

            m_ScratchPool = new NativeArray<AStarScratch>(JobsUtility.MaxJobThreadCount, Allocator.Persistent);
            m_PathPool = new NativeArray<UnsafeList<float3>>(JobsUtility.MaxJobThreadCount, Allocator.Persistent);

            // 装配时就建一次网格：这样 World 刚建好 AStarAccess 就能用（不必等第一帧）。
            // 之后 AStarSettings 版本变化时 OnUpdate 会重建。
            RebuildGridIfNeeded();
        }

        protected override void OnDestroy()
        {
            for (var i = 0; i < m_ScratchPool.Length; i++)
            {
                var scratch = m_ScratchPool[i];
                scratch.Dispose();
            }
            for (var i = 0; i < m_PathPool.Length; i++)
            {
                var block = m_PathPool[i];
                if (block.IsCreated)
                {
                    block.Dispose();
                }
            }

            m_ScratchPool.Dispose();
            m_PathPool.Dispose();

            if (m_Grid.IsCreated)
            {
                m_Grid.Dispose();
            }
        }

        protected override unsafe void OnUpdate()
        {
            RebuildGridIfNeeded();
            if (!m_Grid.IsCreated || m_RequestQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            var entityHandle = GetEntityTypeHandle();
            var waypointHandle = GetBufferTypeHandle<PathWaypoint>(false);

            var chunks = m_RequestQuery.ToArchetypeChunkArray(Allocator.TempJob);

            // 缺 PathWaypoint 的实体补一个空缓冲；结构性变更会重建 chunk 列表，所以重新取一次。
            var missing = new NativeList<Entity>(Allocator.Temp);
            for (var c = 0; c < chunks.Length; c++)
            {
                var chunk = chunks[c];
                if (chunk.Has(ref waypointHandle))
                {
                    continue;
                }

                var entities = chunk.GetNativeArray(entityHandle);
                for (var i = 0; i < chunk.Count; i++)
                {
                    missing.Add(entities[i]);
                }
            }
            if (missing.Length > 0)
            {
                for (var i = 0; i < missing.Length; i++)
                {
                    EntityManager.AddBuffer<PathWaypoint>(missing[i]);
                }
                chunks.Dispose();
                chunks = m_RequestQuery.ToArchetypeChunkArray(Allocator.TempJob);
            }
            missing.Dispose();

            // 请求 / 状态 / 缓冲的句柄在结构性变更之后重新取一次。
            var requestHandle = GetComponentTypeHandle<PathRequest>(true);
            var statusHandle = GetComponentTypeHandle<PathStatus>(false);
            waypointHandle = GetBufferTypeHandle<PathWaypoint>(false);

            var total = 0;
            for (var c = 0; c < chunks.Length; c++)
            {
                total += chunks[c].Count;
            }
            if (total == 0)
            {
                chunks.Dispose();
                return;
            }

            var requests = new NativeArray<PathRequest>(total, Allocator.TempJob);
            var solved = new NativeArray<int>(total, Allocator.TempJob);
            var slots = new NativeArray<SolveSlot>(total, Allocator.TempJob);
            var results = new NativeArray<AStarSolveResult>(total, Allocator.TempJob);
            var solveIndices = new NativeList<int>(Allocator.TempJob);

            var baseIndex = 0;
            for (var c = 0; c < chunks.Length; c++)
            {
                var chunk = chunks[c];
                NativeArray<PathRequest>.Copy(chunk.GetNativeArray(ref requestHandle), 0, requests, baseIndex, chunk.Count);

                var chunkStatuses = chunk.GetNativeArray(ref statusHandle);
                for (var i = 0; i < chunk.Count; i++)
                {
                    solved[baseIndex + i] = chunkStatuses[i].SolvedVersion;
                }
                baseIndex += chunk.Count;
            }

            var config = EntityManager.GetComponentData<AStarSingleton>(m_SingletonEntity).Config;

            // 每帧最多解 MaxSolvesPerFrame 个（<= 0 不限）：没轮上的请求不写状态，下一帧继续。
            var budget = config.MaxSolvesPerFrame > 0 ? math.min(config.MaxSolvesPerFrame, total) : total;
            for (var i = 0; i < total && solveIndices.Length < budget; i++)
            {
                if (solved[i] != requests[i].Version)
                {
                    solveIndices.Add(i);
                }
            }

            if (solveIndices.Length > 0)
            {
                ClearPathBlocks();
                Dependency = new SolvePathsJob
                {
                    Grid = m_Grid,
                    Config = config,
                    Requests = requests,
                    SolveIndices = solveIndices.AsArray(),
                    Slots = slots,
                    ScratchPool = m_ScratchPool,
                    PathPool = m_PathPool,
                    PathBlockCapacity = math.max(64, m_Grid.CellCount),
                }.Schedule(solveIndices.Length, 1, Dependency);
                Dependency.Complete();
            }

            // 把各线程块里的路径拼成一份扁平输出，并记下每个请求在其中的片段。
            var paths = new NativeList<float3>(math.max(16, total * 4), Allocator.TempJob);
            for (var i = 0; i < total; i++)
            {
                var slot = slots[i];
                if (slot.Solved == 0)
                {
                    results[i] = new AStarSolveResult { Skipped = 1 };
                    continue;
                }

                var offset = paths.Length;
                var block = m_PathPool[slot.Block];
                paths.AddRange(block.Ptr + slot.BlockOffset, slot.Length);
                results[i] = new AStarSolveResult
                {
                    Status = slot.Status,
                    PathOffset = offset,
                    PathLength = slot.Length,
                };
            }

            // 写回：按 chunk 批量写 PathWaypoint 缓冲与 PathStatus。
            baseIndex = 0;
            for (var c = 0; c < chunks.Length; c++)
            {
                var chunk = chunks[c];
                var chunkRequests = chunk.GetNativeArray(ref requestHandle);
                var chunkStatuses = chunk.GetNativeArray(ref statusHandle);
                var chunkWaypoints = chunk.GetBufferAccessor(ref waypointHandle);

                for (var i = 0; i < chunk.Count; i++)
                {
                    var result = results[baseIndex + i];
                    if (result.Skipped != 0)
                    {
                        continue;
                    }

                    var waypoints = chunkWaypoints[i];
                    waypoints.Clear();
                    for (var p = 0; p < result.PathLength; p++)
                    {
                        waypoints.Add(new PathWaypoint { Position = paths[result.PathOffset + p] });
                    }

                    chunkStatuses[i] = new PathStatus
                    {
                        Status = result.Status,
                        WaypointCount = result.PathLength,
                        SolvedVersion = chunkRequests[i].Version,
                    };
                }
                baseIndex += chunk.Count;
            }

            chunks.Dispose();
            requests.Dispose();
            solved.Dispose();
            slots.Dispose();
            results.Dispose();
            solveIndices.Dispose();
            paths.Dispose();
        }

        /// <summary>清空每个线程的路径块（求解前调用，块里是上一帧解出的路径）。</summary>
        private void ClearPathBlocks()
        {
            for (var i = 0; i < m_PathPool.Length; i++)
            {
                var block = m_PathPool[i];
                if (!block.IsCreated)
                {
                    continue;
                }
                block.Clear();
                m_PathPool[i] = block;
            }
        }

        /// <summary>按 <see cref="AStarSettings"/> 重建网格（版本没变就不动）。</summary>
        private void RebuildGridIfNeeded()
        {
            if (m_BuiltVersion == AStarSettings.Version)
            {
                return;
            }
            m_BuiltVersion = AStarSettings.Version;

            if (m_Grid.IsCreated)
            {
                EntityManager.SetComponentData(m_SingletonEntity, default(AStarSingleton));
                m_Grid.Dispose();
            }

            m_Grid = new AStarGrid(AStarSettings.Pending, AStarSettings.PendingMask, Allocator.Persistent);
            EntityManager.SetComponentData(m_SingletonEntity, new AStarSingleton
            {
                Grid = m_Grid,
                Config = AStarSettings.Pending,
                Algorithm = AStarSettings.Pending.Algorithm,
                BuildVersion = m_BuiltVersion,
            });
        }

        /// <summary>
        /// 批量求解 job：下标 <c>i</c> 处理 <c>SolveIndices[i]</c> 这一个请求，
        /// 路径追加到本线程自己的路径块，结果写进 <see cref="SolveSlot"/>。
        /// </summary>
        [BurstCompile]
        private struct SolvePathsJob : IJobParallelFor
        {
            [ReadOnly] public AStarGrid Grid;
            public AStarConfig Config;

            [ReadOnly] public NativeArray<PathRequest> Requests;
            [ReadOnly] public NativeArray<int> SolveIndices;

            [NativeDisableParallelForRestriction] public NativeArray<SolveSlot> Slots;
            [NativeDisableParallelForRestriction] public NativeArray<AStarScratch> ScratchPool;
            [NativeDisableParallelForRestriction] public NativeArray<UnsafeList<float3>> PathPool;

            /// <summary>新建路径块时的初始容量。</summary>
            public int PathBlockCapacity;

            [NativeSetThreadIndex] public int ThreadIndex;

            public void Execute(int i)
            {
                var index = SolveIndices[i];

                var scratch = ScratchPool[ThreadIndex];
                scratch.EnsureCapacity(Grid.CellCount, Allocator.Persistent);

                var block = PathPool[ThreadIndex];
                if (!block.IsCreated)
                {
                    block = new UnsafeList<float3>(PathBlockCapacity, Allocator.Persistent);
                }
                var offset = block.Length;

                var status = AStarPathfinder.FindPath(Grid, Requests[index].Start, Requests[index].End, Config,
                                                      ref scratch, ref block);

                ScratchPool[ThreadIndex] = scratch;
                PathPool[ThreadIndex] = block;
                Slots[index] = new SolveSlot
                {
                    Solved = 1,
                    Status = status,
                    Block = ThreadIndex,
                    BlockOffset = offset,
                    Length = block.Length - offset,
                };
            }
        }
    }
}
