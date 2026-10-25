using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;

namespace DOTSUtils.RenderBridge
{
    /// <summary>
    /// (Mesh, 材质数组) 去重池。索引只追加、不重排，
    /// 因此已发出的 MaterialMeshInfo 在任何后续扩容后依然有效。
    /// 注意：已发出的 <see cref="RenderMeshArray"/> 是那一时刻的**快照** —— 扩容会构造新的数组实例返回给
    /// 后续调用，旧实例不会被就地修改（旧实体继续持有旧实例，仍然有效）。
    /// 单个 RenderMeshArray 的条目上限受 SubMeshIndexInfo32 限制（range start 为 ushort）。
    /// </summary>
    public sealed class RenderMeshPool
    {
        public const int DefaultMaxEntriesPerArray = 65535;
        /// <summary>
        /// 单个网格允许的子网格数上限。取值域 <c>[1, 127]</c>：<c>SubMeshIndexInfo32</c> 的
        /// range length 只有 7 bits，超出会触发其构造函数断言。
        /// </summary>
        private const int MaxSubMeshesPerMesh = 127;

        private readonly int m_MaxEntriesPerArray;
        private readonly List<Generation> m_Generations = new();

        public RenderMeshPool(int maxEntriesPerArray = DefaultMaxEntriesPerArray)
        {
            m_MaxEntriesPerArray = math.clamp(maxEntriesPerArray, 1, DefaultMaxEntriesPerArray);
            m_Generations.Add(new Generation());
        }

        public int GenerationCount => m_Generations.Count;
        public int EntryCount { get; private set; }

        public bool TryGetOrAdd(Renderer renderer, out RenderMeshArray array, out MaterialMeshInfo info)
        {
            array = default;
            info = default;

            if (renderer == null)
            {
                Debug.LogWarning("[DotsRender] 传入的 Renderer 为空，跳过。");
                return false;
            }

            if (renderer is not MeshRenderer meshRenderer)
            {
                Debug.LogWarning($"[DotsRender] {renderer.GetType().Name} 暂不支持，本批次只处理 MeshRenderer，跳过 {renderer.name}。");
                return false;
            }

            var filter = meshRenderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null)
            {
                Debug.LogWarning($"[DotsRender] {renderer.name} 没有有效的 Mesh（MeshFilter.sharedMesh 为空），跳过。");
                return false;
            }

            var subMeshCount = ResolveSubMeshCount(mesh, renderer.name);
            if (subMeshCount <= 0)
            {
                return false;
            }

            var source = meshRenderer.sharedMaterials;
            if (source == null || source.Length == 0)
            {
                Debug.LogWarning($"[DotsRender] {renderer.name} 没有材质，跳过。");
                return false;
            }
            if (source.Length > subMeshCount)
                Debug.LogWarning($"[DotsRender] {renderer.name} 材质数({source.Length})多于子网格数({subMeshCount})，多余的将被忽略。");
            else if (source.Length < subMeshCount)
                Debug.LogWarning($"[DotsRender] {renderer.name} 材质数({source.Length})少于子网格数({subMeshCount})，缺失的槽将复用最后一个材质。");

            return TryGetOrAdd(mesh, source, subMeshCount, out array, out info);
        }

        /// <summary>
        /// 与 Renderer 无关的条目申请：纯 ECS 路径（没有 Renderer/GameObject，例如 benchmark 的批量生成）
        /// 直接用它拿 <see cref="RenderMeshArray"/> + <see cref="MaterialMeshInfo"/>。
        /// 材质数组长度须 ≥ 子网格数，缺失的槽会复用最后一个材质（与 Renderer 重载口径一致）。
        /// </summary>
        public bool TryGetOrAdd(Mesh mesh, Material[] materials, int subMeshCount, out RenderMeshArray array, out MaterialMeshInfo info)
        {
            array = default;
            info = default;

            if (mesh == null || materials == null || materials.Length == 0)
            {
                return false;
            }
            if (subMeshCount <= 0)
            {
                return false;
            }
            if (materials.Length < subMeshCount)
                Debug.LogWarning($"[DotsRender] {mesh.name} 材质数({materials.Length})少于子网格数({subMeshCount})，缺失的槽将复用最后一个材质。");

            var key = new SubMeshKey(mesh, materials, subMeshCount);

            // 先在所有代里查找：旧代数组依然有效，命中旧代直接返回旧代的 array 与 info。
            for (var i = 0; i < m_Generations.Count; i++)
            {
                var candidate = m_Generations[i];
                if (candidate.Cache.TryGetValue(key, out var cached))
                {
                    array = candidate.Array;
                    info = cached;
                    return true;
                }
            }

            var gen = m_Generations[m_Generations.Count - 1];
            if (!gen.CanFit(subMeshCount, m_MaxEntriesPerArray))
            {
                gen.Freeze();
                gen = new Generation();
                m_Generations.Add(gen);
            }

            info = gen.Append(key, subMeshCount, m_MaxEntriesPerArray);
            array = gen.Array;
            EntryCount += subMeshCount;
            return true;
        }

        /// <summary>子网格数校验（上限 127 来自 SubMeshIndexInfo32 的 range length 位宽）。
        /// internal：<see cref="DotsRenderAuthoring"/> 采集快照时复用同一口径。</summary>
        internal int ResolveSubMeshCount(Mesh mesh, string label)
        {
            var subMeshCount = math.min(mesh.subMeshCount, MaxSubMeshesPerMesh);
            if (subMeshCount <= 0)
            {
                Debug.LogWarning($"[DotsRender] {label} 的 Mesh 没有子网格，跳过。");
                return 0;
            }
            if (mesh.subMeshCount > MaxSubMeshesPerMesh)
                Debug.LogWarning($"[DotsRender] {label} 的子网格数 {mesh.subMeshCount} 超过上限 {MaxSubMeshesPerMesh}，多余部分将被截断。");
            return subMeshCount;
        }

        /// <summary>
        /// 规范化后的 (mesh, 材质序列) 组合：材质数组长度恒等于子网格数。
        /// </summary>
        internal readonly struct SubMeshKey : IEquatable<SubMeshKey>
        {
            private readonly Mesh m_Mesh;
            private readonly Material[] m_Materials;
            readonly int m_Hash;

            public SubMeshKey(Mesh mesh, Material[] materials, int subMeshCount)
            {
                m_Mesh = mesh;
                m_Materials = new Material[subMeshCount];
                for (var i = 0; i < subMeshCount; i++)
                    m_Materials[i] = materials[math.min(i, materials.Length - 1)];

                var hash = (mesh != null ? mesh.GetHashCode() : 0) * 397;
                for (var i = 0; i < subMeshCount; i++)
                    hash = hash * 397 ^ (m_Materials[i] != null ? m_Materials[i].GetHashCode() : 0);
                m_Hash = hash;
            }

            public Mesh Mesh => m_Mesh;
            public int Count => m_Materials.Length;
            public Material MaterialAt(int subMesh) => m_Materials[subMesh];

            public bool Equals(SubMeshKey other)
            {
                if (m_Hash != other.m_Hash)
                {
                    return false;
                }
                if (!ReferenceEquals(m_Mesh, other.m_Mesh))
                {
                    return false;
                }
                if (m_Materials.Length != other.m_Materials.Length)
                {
                    return false;
                }
                for (var i = 0; i < m_Materials.Length; i++)
                    if (!ReferenceEquals(m_Materials[i], other.m_Materials[i]))
                    {
                        return false;
                    }
                return true;
            }

            public override bool Equals(object obj) => obj is SubMeshKey other && Equals(other);
            public override int GetHashCode() => m_Hash;
        }

        sealed class Generation
        {
            private readonly List<Material> m_Materials = new();
            private readonly List<Mesh> m_Meshes = new();
            private readonly List<MaterialMeshIndex> m_Indices = new();
            private readonly Dictionary<Material, int> m_MaterialToIndex = new();
            private readonly Dictionary<Mesh, int> m_MeshToIndex = new();
            private RenderMeshArray m_Array;
            private bool m_ArrayDirty = true;

            internal Dictionary<SubMeshKey, MaterialMeshInfo> Cache { get; } = new();

            internal RenderMeshArray Array
            {
                get
                {
                    if (m_ArrayDirty)
                    {
                        m_Array = new RenderMeshArray(m_Materials.ToArray(), m_Meshes.ToArray(), m_Indices.ToArray());
                        m_ArrayDirty = false;
                    }
                    return m_Array;
                }
            }

            internal bool CanFit(int count, int max) => m_Indices.Count + count <= max;

            /// <summary>
            /// 封存本代：提前构建数组，后续不再修改。
            /// </summary>
            internal void Freeze() => _ = Array;

            internal MaterialMeshInfo Append(SubMeshKey key, int count, int maxEntries)
            {
                AssertCanFit(count, maxEntries);

                var start = m_Indices.Count;
                for (var i = 0; i < count; i++)
                {
                    m_Indices.Add(new MaterialMeshIndex
                    {
                        MeshIndex = GetMeshIndex(key.Mesh),
                        MaterialIndex = GetMaterialIndex(key.MaterialAt(i)),
                        SubMeshIndex = i,
                    });
                }

                var info = MaterialMeshInfo.FromMaterialMeshIndexRange(start, count);
                Cache[key] = info;
                m_ArrayDirty = true;
                return info;
            }

            private void AssertCanFit(int count, int maxEntries)
            {
                if (!CanFit(count, maxEntries))
                    throw new InvalidOperationException($"RenderMeshPool 分代逻辑错误：本代已满仍尝试追加 {count} 条。");
            }

            private int GetMeshIndex(Mesh mesh)
            {
                if (m_MeshToIndex.TryGetValue(mesh, out var index))
                {
                    return index;
                }
                index = m_Meshes.Count;
                m_Meshes.Add(mesh);
                m_MeshToIndex.Add(mesh, index);
                return index;
            }

            private int GetMaterialIndex(Material material)
            {
                if (m_MaterialToIndex.TryGetValue(material, out var index))
                {
                    return index;
                }
                index = m_Materials.Count;
                m_Materials.Add(material);
                m_MaterialToIndex.Add(material, index);
                return index;
            }
        }
    }
}
