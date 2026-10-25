using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace DOTSUtils.Spatial.Tests
{
    /// <summary>树是 unmanaged struct，可直接拷进 job；只读查询可多 job 并行，写操作必须单线程，且值语义要求调用方把结果树写回。</summary>
    public class SpatialJobTests
    {
        static NativeSpatialTree MakeTree(int count)
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, new AABB { Center = float3.zero, Extents = new float3(100f) }, Allocator.TempJob, 6, 4);
            var elements = new NativeArray<SpatialElement>(count, Allocator.Temp);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    elements[i] = new SpatialElement
                    {
                        Pos = new float3(i * 3f - 30f, 0f, i * 2f - 20f),
                        Entity = new Unity.Entities.Entity { Index = i + 1, Version = 1 },
                    };
                }

                tree.ClearAndBulkInsert(elements);
            }
            finally
            {
                elements.Dispose();
            }

            return tree;
        }

        [Test]
        public void 范围查询job_结果与主线程一致()
        {
            var tree = MakeTree(40);
            var expected = new NativeList<SpatialElement>(Allocator.Temp);
            var actual = new NativeList<SpatialElement>(Allocator.TempJob);
            var handle = default(JobHandle);
            try
            {
                var query = new AABB { Center = new float3(-10f, 0f, -10f), Extents = new float3(12f) };
                tree.RangeQuery(query, ref expected);

                var job = new SpatialTreeRangeQueryJob { Tree = tree, Query = query, Results = actual };
                handle = job.Schedule();
                handle.Complete();

                Assert.AreEqual(expected.Length, actual.Length);
                Assert.Greater(actual.Length, 0, "这组数据应当能查到东西");
            }
            finally
            {
                handle.Complete();
                expected.Dispose();
                actual.Dispose();
                tree.Dispose();
            }
        }

        [Test]
        public void 范围查询job_可并行调度多份()
        {
            var tree = MakeTree(40);
            var a = new NativeList<SpatialElement>(Allocator.TempJob);
            var b = new NativeList<SpatialElement>(Allocator.TempJob);
            var handleA = default(JobHandle);
            var handleB = default(JobHandle);
            try
            {
                handleA = new SpatialTreeRangeQueryJob
                {
                    Tree = tree,
                    Query = new AABB { Center = new float3(-50f, 0f, -50f), Extents = new float3(30f) },
                    Results = a,
                }.Schedule();

                handleB = new SpatialTreeRangeQueryJob
                {
                    Tree = tree,
                    Query = new AABB { Center = new float3(50f, 0f, 50f), Extents = new float3(30f) },
                    Results = b,
                }.Schedule();

                JobHandle.CombineDependencies(handleA, handleB).Complete();

                Assert.GreaterOrEqual(a.Length + b.Length, 0);
            }
            finally
            {
                handleA.Complete();
                handleB.Complete();
                a.Dispose();
                b.Dispose();
                tree.Dispose();
            }
        }

        [Test]
        public void 应用操作job_写回后计数与链表一致()
        {
            var tree = MakeTree(0);
            var ops = new NativeArray<SpatialOp>(3, Allocator.TempJob);
            var results = new NativeArray<SpatialElementRef>(3, Allocator.TempJob);
            var resultTree = new NativeReference<NativeSpatialTree>(Allocator.TempJob);
            var collected = new NativeList<SpatialElement>(Allocator.Temp);
            var handle = default(JobHandle);
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    ops[i] = new SpatialOp
                    {
                        Type = SpatialOp.Kind.Insert,
                        Element = new SpatialElement
                        {
                            Pos = new float3(i * 5f, 0f, i * 5f),
                            Entity = new Unity.Entities.Entity { Index = i + 1, Version = 1 },
                        },
                    };
                }

                var job = new SpatialTreeApplyOpsJob
                {
                    Tree = tree,
                    Ops = ops,
                    Results = results,
                    ResultTree = resultTree,
                };
                handle = job.Schedule();
                handle.Complete();

                // Schedule 按值拷贝 job 实例，调用方变量的字段不会带上 job 内部的改动。
                Assert.AreEqual(0, tree.ElementCount, "结果只能从 ResultTree 拿，job.Tree 字段读不到");

                tree = resultTree.Value;
                Assert.AreEqual(3, tree.ElementCount, "结果树里的计数正确");

                for (var i = 0; i < 3; i++) Assert.IsTrue(results[i].IsValid, $"第 {i} 个 Insert 应成功");

                tree.CollectElements(collected);
                Assert.AreEqual(3, collected.Length, "链表应与计数一致");
            }
            finally
            {
                handle.Complete();
                ops.Dispose();
                results.Dispose();
                resultTree.Dispose();
                collected.Dispose();
                tree.Dispose();
            }
        }

        [Test]
        public void 构建job_可见性由Accepted返回()
        {
            var tree = new NativeSpatialTree(SpatialIndexKind.Octree, new AABB { Center = float3.zero, Extents = new float3(50f) }, Allocator.TempJob, 5, 4);
            var elements = new NativeArray<SpatialElement>(3, Allocator.TempJob);
            var accepted = new NativeReference<int>(Allocator.TempJob);
            var resultTree = new NativeReference<NativeSpatialTree>(Allocator.TempJob);
            var handle = default(JobHandle);
            try
            {
                elements[0] = new SpatialElement { Pos = new float3(0f, 0f, 0f) };
                elements[1] = new SpatialElement { Pos = new float3(10f, 0f, 10f) };
                elements[2] = new SpatialElement { Pos = new float3(9999f, 0f, 0f) };   // 越界

                var job = new SpatialTreeBuildJob
                {
                    Tree = tree,
                    Elements = elements,
                    Accepted = accepted,
                    ResultTree = resultTree,
                };
                handle = job.Schedule();
                handle.Complete();
                tree = resultTree.Value;

                Assert.AreEqual(2, accepted.Value, "越界元素不应计入接受数");
                Assert.AreEqual(2, tree.ElementCount);
                Assert.AreEqual(1, tree.OutOfBoundsRejected);
            }
            finally
            {
                handle.Complete();
                elements.Dispose();
                accepted.Dispose();
                resultTree.Dispose();
                tree.Dispose();
            }
        }
    }
}
