using DOTSUtils.RenderBridge.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DOTSUtils.RenderBridge.Tests
{
    public class AuthoringMenuTests
    {
        GameObject m_Root;

        [SetUp]
        public void SetUp()
        {
            // 必须先清掉残留的 Undo 记录，否则 Unity flush 撤销栈时会抛 native 断言。
            Undo.ClearAll();
        }

        [TearDown]
        public void TearDown()
        {
            // 先清 Undo 栈、再销毁对象，顺序不能反：先销毁会在撤销栈里留下悬空记录，触发 native 断言。
            Undo.ClearAll();

            // 即使某个用例中途断言失败跳出，也保证场景对象被清干净。
            try
            {
                if (m_Root != null) Object.DestroyImmediate(m_Root);
            }
            finally
            {
                m_Root = null;
            }
        }

        [Test]
        public void CollectTargets_跳过root自身只收子物体()
        {
            m_Root = new GameObject("root");
            m_Root.AddComponent<MeshRenderer>(); // root 自身带 Renderer，但它是父物体，不应进列表

            var childRenderer = new GameObject("child-renderer");
            childRenderer.transform.SetParent(m_Root.transform);
            childRenderer.AddComponent<MeshRenderer>();

            var pureLogic = new GameObject("pure-logic");
            pureLogic.transform.SetParent(m_Root.transform);

            var targets = DotsRenderAuthoringMenu.CollectTargets(m_Root);

            CollectionAssert.Contains(targets, childRenderer);
            CollectionAssert.DoesNotContain(targets, m_Root, "root 是被标记的父物体，自身带 Renderer 也不应进列表");
            CollectionAssert.DoesNotContain(targets, pureLogic, "纯逻辑节点不应被收集");
        }

        [Test]
        public void CollectTargets_收集子物体的LODGroup()
        {
            m_Root = new GameObject("root");
            var lodChild = new GameObject("lod-child");
            lodChild.transform.SetParent(m_Root.transform);
            lodChild.AddComponent<LODGroup>();

            var targets = DotsRenderAuthoringMenu.CollectTargets(m_Root);

            CollectionAssert.Contains(targets, lodChild);
            CollectionAssert.DoesNotContain(targets, m_Root);
        }

        [Test]
        public void MarkRecursive_给父物体加标记并分类填充两个列表()
        {
            m_Root = new GameObject("root");
            m_Root.AddComponent<MeshRenderer>(); // root 自身带 Renderer，不应进列表

            var childRenderer = new GameObject("child-renderer");
            childRenderer.transform.SetParent(m_Root.transform);
            childRenderer.AddComponent<MeshRenderer>();

            var lodChild = new GameObject("lod-child");
            lodChild.transform.SetParent(m_Root.transform);
            lodChild.AddComponent<LODGroup>();

            // 同时带 MeshRenderer 与 LODGroup 的子物体：LODGroup 优先，只进 LodGroups。
            var bothChild = new GameObject("both-child");
            bothChild.transform.SetParent(m_Root.transform);
            bothChild.AddComponent<LODGroup>();
            bothChild.AddComponent<MeshRenderer>();

            DotsRenderAuthoringMenu.MarkRecursive(new[] { m_Root });

            var authoring = m_Root.GetComponent<DotsRenderAuthoring>();
            Assert.IsNotNull(authoring, "标记必须挂在被选中的父物体上，而不是子渲染物体上");

            // 深度优先、按层级顺序。
            CollectionAssert.AreEqual(
                new[] { childRenderer.GetComponent<MeshRenderer>() },
                authoring.MeshRenderers,
                "子物体的 MeshRenderer 应进 MeshRenderers；root 自身的与 LOD 子物体的都不应进");
            CollectionAssert.AreEqual(
                new[] { lodChild.GetComponent<LODGroup>(), bothChild.GetComponent<LODGroup>() },
                authoring.LodGroups,
                "子物体 LODGroup 按层级顺序进 LodGroups；同带两者的对象只记 LODGroup");
        }

        [Test]
        public void UnmarkRecursive_移除父物体上的标记()
        {
            m_Root = new GameObject("root");
            var childRenderer = new GameObject("child-renderer");
            childRenderer.transform.SetParent(m_Root.transform);
            childRenderer.AddComponent<MeshRenderer>();

            DotsRenderAuthoringMenu.MarkRecursive(new[] { m_Root });
            Assert.IsNotNull(m_Root.GetComponent<DotsRenderAuthoring>(), "标记应挂在父物体上");

            DotsRenderAuthoringMenu.UnmarkRecursive(new[] { m_Root });

            Assert.IsNull(m_Root.GetComponent<DotsRenderAuthoring>(), "移除后父物体不应再带标记");
        }

        [Test]
        public void MarkPrefabAsset_给根加标记并分类填充两个列表()
        {
            const string parentFolder = "Assets/Res/Prefabs";
            const string folder = parentFolder + "/Temp";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(parentFolder, "Temp");
            var path = $"{folder}/MarkerSubject.prefab";

            // 预制体：根（自身带 Renderer，不应进列表）+ 一个 MeshRenderer 子物体 + 一个 LODGroup 子物体。
            var source = new GameObject("prefab-subject");
            source.AddComponent<MeshRenderer>();
            var childRenderer = new GameObject("child-renderer");
            childRenderer.transform.SetParent(source.transform);
            childRenderer.AddComponent<MeshFilter>();
            childRenderer.AddComponent<MeshRenderer>();
            var lodChild = new GameObject("lod-child");
            lodChild.transform.SetParent(source.transform);
            lodChild.AddComponent<LODGroup>();

            PrefabUtility.SaveAsPrefabAsset(source, path);
            Object.DestroyImmediate(source);

            try
            {
                Assert.IsTrue(DotsRenderAuthoringMenu.MarkPrefabAsset(path),
                    "首次标记会新增组件并填列表，应返回 true（资产确有改动）");

                var contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var authoring = contents.GetComponent<DotsRenderAuthoring>();
                    Assert.IsNotNull(authoring, "Prefab 资产根对象必须带上标记组件");

                    Assert.AreEqual(1, authoring.MeshRenderers.Count, "只应收子物体的 MeshRenderer，root 自身的不收");
                    Assert.AreEqual("child-renderer", authoring.MeshRenderers[0].name);
                    Assert.AreEqual(1, authoring.LodGroups.Count);
                    Assert.AreEqual("lod-child", authoring.LodGroups[0].name);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }
            finally
            {
                // 清理：删掉临时 prefab 与 Temp 文件夹，不留仓库残留。
                AssetDatabase.DeleteAsset(path);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void MarkPrefabAsset_无可转换子目标时不加标记()
        {
            const string parentFolder = "Assets/Res/Prefabs";
            const string folder = parentFolder + "/Temp";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(parentFolder, "Temp");
            var path = $"{folder}/EmptySubject.prefab";

            // 只有纯逻辑子物体（无 MeshRenderer / LODGroup）：CollectTargets 为空。
            var source = new GameObject("empty-subject");
            var pureLogic = new GameObject("pure-logic");
            pureLogic.transform.SetParent(source.transform);

            PrefabUtility.SaveAsPrefabAsset(source, path);
            Object.DestroyImmediate(source);

            try
            {
                Assert.IsFalse(DotsRenderAuthoringMenu.MarkPrefabAsset(path),
                    "无可转换子目标时不应改动资产，应返回 false");

                var contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    Assert.IsNull(contents.GetComponent<DotsRenderAuthoring>(),
                        "根对象没有任何可转换子目标时，不应添加标记（否则运行时会打“两列表都为空”的警告）");
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
