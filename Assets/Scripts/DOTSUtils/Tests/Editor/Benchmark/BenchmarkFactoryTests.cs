using DOTSUtils.RenderBridge;
using NUnit.Framework;
using UnityEngine;

namespace DOTSUtils.Benchmark.Tests
{
    /// <summary>benchmark 的纯逻辑部分（EditMode 可覆盖）：RenderMeshPool 的 Renderer-free 重载、LOD 距离换算的公开 API、材质工厂。本程序集不引用 Runtime 测试程序集，用不了 TestRenderAssets，需自建 mesh / 材质。</summary>
    public class BenchmarkFactoryTests
    {
        Mesh m_Mesh;
        Material m_Material;

        [SetUp]
        public void SetUp()
        {
            m_Mesh = new Mesh { name = "pool-mesh" };
            m_Mesh.subMeshCount = 1;
            m_Material = NewMaterial();
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Mesh != null) Object.DestroyImmediate(m_Mesh);
            if (m_Material != null) Object.DestroyImmediate(m_Material);
        }

        static Material NewMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            return new Material(shader);
        }

        [Test]
        public void 池_Mesh与材质重载_可用且相同输入命中同一条目()
        {
            var pool = new RenderMeshPool();

            Assert.IsTrue(pool.TryGetOrAdd(m_Mesh, new[] { m_Material }, 1, out var array1, out var info1));
            Assert.IsTrue(pool.TryGetOrAdd(m_Mesh, new[] { m_Material }, 1, out var array2, out var info2));

            Assert.IsNotNull(array1);
            Assert.AreEqual(array1, array2, "相同 (mesh, 材质序列) 必须复用同一个 RenderMeshArray 实例");
            Assert.AreEqual(info1.MaterialMeshIndexRange.start, info2.MaterialMeshIndexRange.start);
            Assert.AreEqual(info1.MaterialMeshIndexRange.length, info2.MaterialMeshIndexRange.length);
            Assert.AreEqual(1, array1.Materials.Length);
            Assert.AreEqual(1, array1.Meshes.Length);
        }

        [Test]
        public void 池_相同网格不同材质_生成不同条目()
        {
            var other = NewMaterial();
            var pool = new RenderMeshPool();
            try
            {
                Assert.IsTrue(pool.TryGetOrAdd(m_Mesh, new[] { m_Material }, 1, out var array1, out var info1));
                Assert.IsTrue(pool.TryGetOrAdd(m_Mesh, new[] { other }, 1, out var array2, out var info2));

                // 已发出的 RenderMeshArray 是那一刻的快照：扩容返回新实例，旧实例不被就地修改。
                Assert.AreEqual(1, array1.Materials.Length, "先发出的数组实例保持原样（1 个材质）");
                Assert.AreEqual(2, array2.Materials.Length, "后一次调用拿到扩容后的新数组实例（2 个材质）");
                Assert.AreNotEqual(info1.MaterialMeshIndexRange.start, info2.MaterialMeshIndexRange.start,
                    "两个不同条目应落在数组的不同区间");
            }
            finally
            {
                Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void LOD距离换算_公开且与手算一致()
        {
            var go = new GameObject("lod-src");
            try
            {
                go.transform.localScale = Vector3.one * 2f;
                var group = go.AddComponent<LODGroup>();
                group.SetLODs(new[]
                {
                    new LOD(0.6f, new Renderer[0]),
                    new LOD(0.2f, new Renderer[0]),
                });
                // SetLODs 会重算 LODGroup.size，所以 size 必须在它之后设置。
                group.size = 3f;

                var largestScale = 2f;
                Assert.AreEqual(3f, group.size, 1e-4f, "size 应在 SetLODs 之后被保留");
                Assert.AreEqual(largestScale * group.size, DotsRenderLodSupport.GetWorldSpaceSize(group), 1e-4f,
                    "worldSpaceSize = 最大轴缩放 × size");

                var d0 = DotsRenderLodSupport.ComputeDistances(group, 0);
                Assert.AreEqual(6f / 0.6f, d0.x, 1e-4f, "LOD0 距离 = worldSpaceSize / 0.6");
                Assert.AreEqual(6f / 0.2f, d0.y, 1e-4f, "LOD1 距离 = worldSpaceSize / 0.2");
                Assert.AreEqual(float.PositiveInfinity, d0.z);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void 材质工厂_生成N个不同颜色的材质并可清理()
        {
            var shader = Shader.Find("Custom/CustomLit");
            Assert.IsNotNull(shader, "测试依赖项目自带的 Custom/CustomLit");

            var mats = BenchmarkMaterialFactory.Create(4, shader);

            Assert.AreEqual(4, mats.Length);
            for (var i = 0; i < mats.Length; i++)
            {
                Assert.IsNotNull(mats[i]);
                Assert.AreEqual("BenchmarkMaterial_" + i, mats[i].name);
                Assert.AreEqual(shader, mats[i].shader);
                for (var j = i + 1; j < mats.Length; j++)
                    Assert.AreNotEqual(mats[i].GetColor("_BaseColor"), mats[j].GetColor("_BaseColor"),
                        "不同材质应有不同 _BaseColor");
            }

            // DestroyAll 在 EditMode 下走 DestroyImmediate。
            BenchmarkMaterialFactory.DestroyAll(mats);
            for (var i = 0; i < mats.Length; i++) Assert.IsTrue(mats[i] == null, "清理后材质应被销毁");
        }
    }
}
