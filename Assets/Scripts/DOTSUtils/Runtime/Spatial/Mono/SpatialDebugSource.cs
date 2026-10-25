using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace DOTSUtils.Spatial
{
    /// <summary>
    /// 「非 System 调用方式」的样例：自己在 MonoBehaviour 里持有一棵
    /// <see cref="NativeSpatialTree"/>、自己管生命周期，每帧把 <see cref="Targets"/> 的位置喂进去；
    /// 同时把树交给编辑器可视化窗口取数。
    ///
    /// 与模块里的 <see cref="SpatialSingleton"/> <b>无关</b> —— 那是 World 里的单例树，这是本组件的私有树，
    /// 两条路径各自独立，不要混用。
    /// </summary>
    [AddComponentMenu("DOTSUtils/Spatial/Spatial Debug Source")]
    public sealed class SpatialDebugSource : MonoBehaviour
    {
        [Tooltip("要入树的 Transform。留空则用本物体 + 所有子物体。")]
        public Transform[] Targets;

        /// <summary>
        /// 用哪种索引模式。四叉树：只看 xz（<c>Center.z</c> 当作世界 y，y 分量被忽略）；八叉树：完整 xyz。
        /// </summary>
        [Tooltip("四叉树：只看 xz；八叉树：完整 xyz。")]
        public SpatialIndexKind Kind = SpatialIndexKind.Quadtree;

        [Tooltip("索引范围中心。四叉树只用 xz（xz 平面）；八叉树用完整 xyz。")]
        public Vector3 Center = Vector3.zero;

        [Tooltip("索引范围半长。四叉树作用在 xz 平面；八叉树覆盖 ±HalfExtent 的正方体。")]
        public float HalfExtent = 100f;

        [Range(1, NativeSpatialTree.MaxSupportedDepth)]
        public int MaxDepth = 6;

        [Range(1, 256)]
        public int MaxLeafElements = 16;

        [Tooltip("勾上 = 每帧 bulk 重建（简单直接）；取消 = 首次 Insert 后按位置增量 Update。")]
        public bool BulkRebuildEveryFrame = true;

        private NativeSpatialTree m_Tree;
        private readonly List<SpatialElement> m_Elements = new List<SpatialElement>();
        private readonly List<SpatialElementRef> m_Refs = new List<SpatialElementRef>();
        private readonly List<Transform> m_Resolved = new List<Transform>();

        /// <summary>
        /// 树是否已建（<c>OnEnable</c> 之后为真）。
        /// </summary>
        public bool IsCreated => m_Tree.IsCreated;

        /// <summary>
        /// 供编辑器可视化窗口取数（只读）。
        /// </summary>
        public NativeSpatialTree Tree => m_Tree;

        /// <summary>
        /// 实际入树的 Transform 列表（<see cref="Targets"/> 为空时是自动收集的结果）。
        /// </summary>
        public IReadOnlyList<Transform> ResolvedTargets => m_Resolved;

        private void OnEnable()
        {
            ResolveTargets();
            Rebuild();
        }

        private void OnDisable() => DisposeTree();

        private void Update()
        {
            if (!m_Tree.IsCreated)
            {
                return;
            }
            if (BulkRebuildEveryFrame)
            {
                ResolveTargets();
            }
            SyncElements();
        }

        /// <summary>
        /// 按当前配置重建树（改了 Kind / Center / HalfExtent / MaxDepth 之后调用）。
        /// </summary>
        public void Rebuild()
        {
            DisposeTree();

            var bounds = Kind == SpatialIndexKind.Quadtree
                ? new AABB
                {
                    // 四叉树：Center.z 当作「世界 y」，xz 才是索引平面（y 分量忽略）
                    Center = new float3(Center.x, 0f, Center.z),
                    Extents = new float3(math.max(HalfExtent, 1e-3f), 1f, math.max(HalfExtent, 1e-3f)),
                }
                : new AABB
                {
                    Center = (float3)Center,
                    Extents = new float3(math.max(HalfExtent, 1e-3f)),
                };

            m_Tree = new NativeSpatialTree(Kind, bounds, Allocator.Persistent, MaxDepth, (short)MaxLeafElements);
            m_Refs.Clear();
            SyncElements();
        }

        private void DisposeTree()
        {
            if (!m_Tree.IsCreated)
            {
                return;
            }
            m_Tree.Dispose();
            m_Tree = default;
        }

        private void ResolveTargets()
        {
            m_Resolved.Clear();

            if (Targets != null && Targets.Length > 0)
            {
                for (var i = 0; i < Targets.Length; i++)
                    if (Targets[i] != null)
                    {
                        m_Resolved.Add(Targets[i]);
                    }
                return;
            }

            foreach (var t in GetComponentsInChildren<Transform>())
            {
                m_Resolved.Add(t);
            }
        }

        private void SyncElements()
        {
            if (!m_Tree.IsCreated)
            {
                return;
            }

            m_Elements.Clear();
            for (var i = 0; i < m_Resolved.Count; i++)
            {
                var p = m_Resolved[i].position;
                // 四叉树模式投影到 xz（y 不参与索引），八叉树模式用完整位置。
                m_Elements.Add(new SpatialElement
                {
                    Pos = Kind == SpatialIndexKind.Quadtree ? new float3(p.x, 0f, p.z) : (float3)p,
                });
            }

            if (BulkRebuildEveryFrame)
            {
                // 不能用 using var：readonly 变量无法按下标写 struct 类型的 NativeArray。
                var array = new NativeArray<SpatialElement>(m_Elements.Count, Allocator.Temp);
                try
                {
                    for (var i = 0; i < m_Elements.Count; i++)
                    {
                        array[i] = m_Elements[i];
                    }
                    m_Tree.ClearAndBulkInsert(array);
                }
                finally
                {
                    array.Dispose();
                }

                m_Refs.Clear();
                return;
            }

            if (m_Refs.Count != m_Elements.Count)
            {
                // 元素集合变了（首次 / 目标数量变化）→ 逐个 Insert 并记住句柄
                m_Refs.Clear();
                for (var i = 0; i < m_Elements.Count; i++)
                {
                    m_Refs.Add(m_Tree.Insert(m_Elements[i]));
                }
                return;
            }

            for (var i = 0; i < m_Elements.Count; i++)
                m_Tree.Update(m_Refs[i], m_Elements[i].Pos);
        }
    }
}
