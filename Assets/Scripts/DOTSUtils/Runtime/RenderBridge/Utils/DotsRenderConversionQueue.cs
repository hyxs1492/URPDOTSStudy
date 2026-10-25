using System;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace DOTSUtils.RenderBridge
{
    /// <summary>
    /// 主线程请求队列。MonoBehaviour 的 OnEnable/OnDisable 只入队，
    /// 真正的 ECS 结构性变更由 DotsRenderConversionSystem 在系统组内完成。
    /// 另外承载一类"实体就绪后再执行"的操作（<see cref="EnqueueOperation"/>）：
    /// 调用方在实体还没创建时想改实体（加组件等），就先把动作排队，转换完成后按调用顺序应用。
    ///
    /// 操作以 <see cref="EntityCommandBuffer"/> 为写入目标（而不是 EntityManager）：应用阶段先把
    /// 一批操作记录下来、再一次性 playback。
    /// </summary>
    public static class DotsRenderConversionQueue
    {
        private static readonly Queue<GameObject> s_Register = new();
        private static readonly Queue<GameObject> s_Unregister = new();
        /// <summary>
        /// 按 owner 分桶的「待应用操作」，桶内保持调用顺序。
        /// </summary>
        private static readonly Dictionary<GameObject, List<PendingEntityOperation>> s_PendingByOwner = new();

        public static void EnqueueRegister(GameObject go)
        {
            if (go != null)
            {
                s_Register.Enqueue(go);
            }
        }

        public static void EnqueueUnregister(GameObject go)
        {
            if (go != null)
            {
                s_Unregister.Enqueue(go);
            }
        }

        public static bool TryDequeueRegister(out GameObject go)
        {
            if (s_Register.Count == 0)
            {
                go = null;
                return false;
            }
            go = s_Register.Dequeue();
            return true;
        }

        public static bool TryDequeueUnregister(out GameObject go)
        {
            if (s_Unregister.Count == 0)
            {
                go = null;
                return false;
            }
            go = s_Unregister.Dequeue();
            return true;
        }

        // ------------------------------------------------------------------
        //  待应用操作（"实体还没创建就先记下来"）
        // ------------------------------------------------------------------

        /// <summary>
        /// 一个「实体就绪后再执行」的操作；泛型值通过闭包捕获，天然类型安全。
        /// </summary>
        internal readonly struct PendingEntityOperation
        {
            public readonly GameObject Owner;
            public readonly Action<EntityCommandBuffer, GameObject> Apply;

            public PendingEntityOperation(GameObject owner, Action<EntityCommandBuffer, GameObject> apply)
            {
                Owner = owner;
                Apply = apply;
            }
        }

        /// <summary>
        /// 待应用操作总数（调试用）。
        /// </summary>
        public static int PendingCount
        {
            get
            {
                var total = 0;
                foreach (var bucket in s_PendingByOwner.Values)
                {
                    total += bucket.Count;
                }
                return total;
            }
        }

        public static void EnqueueOperation(GameObject owner, Action<EntityCommandBuffer, GameObject> apply)
        {
            if (owner == null || apply == null)
            {
                return;
            }
            if (!s_PendingByOwner.TryGetValue(owner, out var bucket))
            {
                bucket = new List<PendingEntityOperation>();
                s_PendingByOwner[owner] = bucket;
            }
            bucket.Add(new PendingEntityOperation(owner, apply));
        }

        /// <summary>
        /// 取出某 owner 的全部待应用操作（保持调用顺序）并从队列移除。
        /// 调用方保证 <paramref name="results"/> 是空的（实际调用点每次都 <c>Clear()</c>）。
        /// </summary>
        internal static void TakeOperations(GameObject owner, List<PendingEntityOperation> results)
        {
            if (results == null || owner == null)
            {
                return;
            }
            if (!s_PendingByOwner.TryGetValue(owner, out var bucket))
            {
                return;
            }
            results.AddRange(bucket);          // 桶内即调用顺序，无需再 Reverse
            s_PendingByOwner.Remove(owner);
        }

        /// <summary>
        /// 丢弃某 owner 的全部待应用操作（转换被跳过 / 注销时）。
        /// </summary>
        public static void DropOperations(GameObject owner)
        {
            if (owner != null)
            {
                s_PendingByOwner.Remove(owner);
            }
        }

        /// <summary>
        /// 某 owner 当前挂着的待应用操作数（调试用）。
        /// </summary>
        public static int CountOperations(GameObject owner)
        {
            if (owner == null || !s_PendingByOwner.TryGetValue(owner, out var bucket))
            {
                return 0;
            }
            return bucket.Count;
        }

        public static void Clear()
        {
            s_Register.Clear();
            s_Unregister.Clear();
            s_PendingByOwner.Clear();
        }

        /// <summary>
        /// 进入 Play 前清空队列（编辑期入队的请求没有消费者）。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlayMode() => Clear();
    }
}
