using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace DOTSUtils.RenderBridge.Tests
{
    public class RenderMeshPoolTests
    {
        Mesh m_Mesh;
        Material m_MatA;
        Material m_MatB;
        GameObject m_Go;

        [SetUp]
        public void SetUp()
        {
            m_Mesh = new Mesh { name = "test-mesh" };
            m_Mesh.subMeshCount = 2;
            m_MatA = NewMaterial();
            m_MatB = NewMaterial();
            m_Go = new GameObject("subject");
            m_Go.AddComponent<MeshFilter>().sharedMesh = m_Mesh;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(m_Go);
            Object.DestroyImmediate(m_Mesh);
            Object.DestroyImmediate(m_MatA);
            Object.DestroyImmediate(m_MatB);
        }

        static Material NewMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            return new Material(shader);
        }

        Renderer NewRenderer(params Material[] materials)
        {
            var mr = m_Go.GetComponent<MeshRenderer>();
            if (mr == null) mr = m_Go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = materials;
            return mr;
        }

        [Test]
        public void 相同mesh与材质组合返回相同索引()
        {
            var pool = new RenderMeshPool();
            var r = NewRenderer(m_MatA, m_MatB);

            Assert.IsTrue(pool.TryGetOrAdd(r, out var array1, out var info1));
            Assert.IsTrue(pool.TryGetOrAdd(r, out var array2, out var info2));

            Assert.AreEqual(info1, info2, "相同组合必须复用同一 MaterialMeshInfo");
            Assert.AreEqual(array1, array2, "相同组合必须属于同一 RenderMeshArray");
            Assert.AreEqual(2, pool.EntryCount, "复用不应新增条目");
        }

        [Test]
        public void 追加新组合后旧索引仍指向原条目()
        {
            var pool = new RenderMeshPool();
            var r = NewRenderer(m_MatA, m_MatB);
            Assert.IsTrue(pool.TryGetOrAdd(r, out var array, out var first));

            // 换一组材质，产生新条目
            r.sharedMaterials = new[] { m_MatB, m_MatA };
            Assert.IsTrue(pool.TryGetOrAdd(r, out _, out var second));

            Assert.AreNotEqual(first.MaterialMeshIndexRange.start, second.MaterialMeshIndexRange.start);
            Assert.AreEqual(first.MaterialMeshIndexRange.start, 0, "旧条目的起始索引不应被改写");
            Assert.AreEqual(2, array.MaterialMeshIndices.Length, "旧 array 实例只含自己那一代的条目");
        }

        [Test]
        public void 多材质槽生成连续索引且子网格序号从零递增()
        {
            var pool = new RenderMeshPool();
            var r = NewRenderer(m_MatA, m_MatB);

            Assert.IsTrue(pool.TryGetOrAdd(r, out var array, out var info));

            Assert.IsTrue(info.HasMaterialMeshIndexRange);
            var range = info.MaterialMeshIndexRange;
            Assert.AreEqual(2, range.length);

            for (var i = 0; i < range.length; i++)
            {
                var idx = array.MaterialMeshIndices[range.start + i];
                Assert.AreEqual(i, idx.SubMeshIndex, "子网格序号必须与槽位一致");
                Assert.AreEqual(array.MaterialMeshIndices[range.start].MeshIndex, idx.MeshIndex,
                    "同一个 mesh 的所有子网格共享同一 MeshIndex（MeshIndex 指向 Meshes 数组，子网格序号在 SubMeshIndex）");
            }
            Assert.AreSame(m_MatA, array.MaterialReferences[array.MaterialMeshIndices[range.start].MaterialIndex].Value);
            Assert.AreSame(m_MatB, array.MaterialReferences[array.MaterialMeshIndices[range.start + 1].MaterialIndex].Value);
        }

        [Test]
        public void 材质数量多于子网格时截断并警告()
        {
            var pool = new RenderMeshPool();
            var r = NewRenderer(m_MatA, m_MatB, m_MatA);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("多余的将被忽略"));

            Assert.IsTrue(pool.TryGetOrAdd(r, out _, out var info));
            Assert.AreEqual(2, info.MaterialMeshIndexRange.length);
        }

        [Test]
        public void 材质数量少于子网格时复用最后一个材质并警告()
        {
            var pool = new RenderMeshPool();
            var r = NewRenderer(m_MatA);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("复用最后一个材质"));

            Assert.IsTrue(pool.TryGetOrAdd(r, out var array, out var info));
            Assert.AreEqual(2, info.MaterialMeshIndexRange.length);
            var range = info.MaterialMeshIndexRange;
            Assert.AreEqual(
                array.MaterialMeshIndices[range.start].MaterialIndex,
                array.MaterialMeshIndices[range.start + 1].MaterialIndex);
        }

        [Test]
        public void 超过单数组上限时自动分代且索引继续可用()
        {
            var pool = new RenderMeshPool(maxEntriesPerArray: 2); // 每个 mesh 占 2 条
            var r = NewRenderer(m_MatA, m_MatB);
            Assert.IsTrue(pool.TryGetOrAdd(r, out var gen1Array, out var gen1Info));
            Assert.AreEqual(1, pool.GenerationCount);

            r.sharedMaterials = new[] { m_MatB, m_MatA };
            Assert.IsTrue(pool.TryGetOrAdd(r, out var gen2Array, out var gen2Info));

            Assert.AreEqual(2, pool.GenerationCount, "超出上限必须开新代");
            Assert.AreEqual(0, gen2Info.MaterialMeshIndexRange.start, "新代索引从 0 开始");
            Assert.AreEqual(2, gen2Array.MaterialMeshIndices.Length);
            Assert.AreEqual(2, gen1Array.MaterialMeshIndices.Length, "旧代不受影响");
            Assert.AreSame(m_MatA, gen1Array.MaterialReferences[gen1Array.MaterialMeshIndices[gen1Info.MaterialMeshIndexRange.start].MaterialIndex].Value);
        }

        [Test]
        public void 跨代重复请求复用旧代条目而不新增()
        {
            var pool = new RenderMeshPool(maxEntriesPerArray: 2); // 每个 mesh 占 2 条，一代只装得下一个组合
            var r = NewRenderer(m_MatA, m_MatB);
            Assert.IsTrue(pool.TryGetOrAdd(r, out var firstArray, out var firstInfo));
            Assert.AreEqual(1, pool.GenerationCount);
            Assert.AreEqual(2, pool.EntryCount);

            // 换一组材质填满第 1 代并开第 2 代
            r.sharedMaterials = new[] { m_MatB, m_MatA };
            Assert.IsTrue(pool.TryGetOrAdd(r, out _, out _));
            Assert.AreEqual(2, pool.GenerationCount, "第 1 代已满必须开第 2 代");
            Assert.AreEqual(4, pool.EntryCount);

            // 再次请求落在第 1 代的组合：必须命中旧代缓存，返回与第一次完全相同的 array 与 info
            r.sharedMaterials = new[] { m_MatA, m_MatB };
            Assert.IsTrue(pool.TryGetOrAdd(r, out var againArray, out var againInfo));

            Assert.AreEqual(firstInfo, againInfo, "跨代命中必须返回与第一次完全相同的 MaterialMeshInfo");
            Assert.AreEqual(firstArray, againArray, "跨代命中必须返回同一代的 RenderMeshArray");
            Assert.AreEqual(4, pool.EntryCount, "跨代命中不应新增条目（EntryCount 是累计条目数，命中旧代时保持不变）");
            Assert.AreEqual(2, pool.GenerationCount, "跨代命中不应新增代");
        }

        [Test]
        public void SkinnedMeshRenderer被拒绝()
        {
            var pool = new RenderMeshPool();
            var skinned = m_Go.AddComponent<SkinnedMeshRenderer>();
            skinned.sharedMesh = m_Mesh;
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("不支持"));

            Assert.IsFalse(pool.TryGetOrAdd(skinned, out _, out _));
        }

        [Test]
        public void 子网格数超过127时截断并警告()
        {
            var pool = new RenderMeshPool();
            m_Mesh.subMeshCount = 130;
            var materials = new Material[130];
            for (var i = 0; i < materials.Length; i++)
                materials[i] = NewMaterial();
            var r = NewRenderer(materials);
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("超过上限"));

            Assert.IsTrue(pool.TryGetOrAdd(r, out var array, out var info));
            Assert.AreEqual(127, info.MaterialMeshIndexRange.length, "range length 只有 7 bits，上限必须为 127");
            Assert.AreEqual(127, array.MaterialMeshIndices.Length, "截断后只应生成 127 条索引");

            foreach (var m in materials)
                Object.DestroyImmediate(m);
        }
    }
}
