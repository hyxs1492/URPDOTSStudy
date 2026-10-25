using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Jobs;

namespace DOTSUtils.RenderBridge
{
    /// <summary>
    /// TransformAccessArray 与 Entity 的严格索引对齐表。
    /// TransformAccessArray 只能在主线程增删，因此所有变更都经过这里。
    /// 注销时立即以 swap-back 方式移除对应槽位：三个容器（Entities / TransformRefs / Transforms）
    /// 索引始终严格对齐，且数组内不残留已销毁的 Transform。
    /// 线程约定：同步 job 只读 Entities 视图；转换系统在写本表前先完成相关 job 依赖，
    /// 容器级 safety check 会在 job 仍在读时进一步拦截结构性写操作。
    /// </summary>
    public sealed class RenderEntityRegistry : IDisposable
    {
        private readonly List<Transform> m_TransformRefs = new();
        private readonly Dictionary<Entity, int> m_SlotOf = new();
        private NativeList<Entity> m_Entities = new(0, Allocator.Persistent);
        private TransformAccessArray m_Transforms = new(0);
        private bool m_Disposed;

        public int Count => m_Entities.Length;
        public TransformAccessArray Transforms => m_Transforms;
        public NativeArray<Entity> Entities => m_Entities.AsArray();

        public void Add(Transform transform, Entity entity)
        {
            if (transform == null || entity == Entity.Null)
            {
                return;
            }

            m_TransformRefs.Add(transform);
            m_Entities.Add(entity);
            m_SlotOf[entity] = m_Entities.Length - 1;
            m_Transforms.Add(transform);
        }

        /// <summary>
        /// 立即移除该实体对应的槽位（swap-back），保持三个容器索引严格对齐，且不残留已销毁的 Transform。
        /// 只能在主线程调用，且调用前必须完成同步 job 的依赖（见 <see cref="DotsRenderConversionSystem"/>）。
        /// </summary>
        public void MarkDead(Entity entity)
        {
            if (!m_SlotOf.Remove(entity, out var slot))
            {
                return;
            }

            var last = m_Entities.Length - 1;
            if (slot != last)
            {
                var lastEntity = m_Entities[last];
                m_Entities[slot] = lastEntity;
                m_TransformRefs[slot] = m_TransformRefs[last];
                m_SlotOf[lastEntity] = slot;
            }

            m_Entities.RemoveAt(last);
            m_TransformRefs.RemoveAt(last);
            // TransformAccessArray 的 swap-back：把末尾 transform 搬到 slot 再缩短长度（slot == last 时等价于移除末尾）。
            m_Transforms.RemoveAtSwapBack(slot);
        }

        /// <summary>
        /// 移除所有已销毁的 Transform 及其实体的槽位（从后往前，保证三容器索引严格对齐）。
        /// 作为注销路径的兜底（注销请求丢失时也不会残留已销毁的 Transform）。
        /// </summary>
        public int PruneDestroyedTransforms()
        {
            UnityEngine.Profiling.Profiler.BeginSample("PruneDestroyedTransforms");

            var removed = 0;
            // 从后往前，与 MarkDead 同构：三容器统一走 swap-back（把末尾存活元素搬到 i，再删末尾），
            // 并同步 m_SlotOf，确保 Entities / TransformRefs / Transforms 索引严格对齐。
            // TransformAccessArray 只有 RemoveAtSwapBack（没有顺序 RemoveAt），两个 List 也必须走 swap-back。
            for (var i = m_TransformRefs.Count - 1; i >= 0; i--)
            {
                if (m_TransformRefs[i] != null)
                {
                    continue; // Unity 的 fake-null：对象已销毁
                }

                m_SlotOf.Remove(m_Entities[i]);

                var last = m_Entities.Length - 1;
                if (i != last)
                {
                    var lastEntity = m_Entities[last];
                    m_Entities[i] = lastEntity;
                    m_TransformRefs[i] = m_TransformRefs[last];
                    m_SlotOf[lastEntity] = i;
                }

                m_Entities.RemoveAt(last);
                m_TransformRefs.RemoveAt(last);
                m_Transforms.RemoveAtSwapBack(i);
                removed++;
            }
            UnityEngine.Profiling.Profiler.EndSample();
            return removed;
        }

        public void Dispose()
        {
            if (m_Disposed)
            {
                return;
            }
            m_Disposed = true;

            if (m_Transforms.isCreated)
            {
                m_Transforms.Dispose();
            }
            if (m_Entities.IsCreated)
            {
                m_Entities.Dispose();
            }
            m_TransformRefs.Clear();
            m_SlotOf.Clear();
        }
    }
}
