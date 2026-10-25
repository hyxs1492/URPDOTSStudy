using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DOTSUtils.RenderBridge.EditorTools
{
    public static class DotsRenderAuthoringMenu
    {
        private const string k_MenuRoot = "Tools/DOTS Render/";

        [MenuItem(k_MenuRoot + "Add Marker (Selection, Recursive)")]
        private static void AddMarkerToSelection()
        {
            MarkRecursive(Selection.gameObjects);
        }

        [MenuItem(k_MenuRoot + "Remove Marker (Selection, Recursive)")]
        private static void RemoveMarkerFromSelection()
        {
            UnmarkRecursive(Selection.gameObjects);
        }

        [MenuItem(k_MenuRoot + "Add Marker to Prefabs in Selected Folder")]
        private static void AddMarkerToFolder()
        {
            var folder = Selection.activeObject != null ? AssetDatabase.GetAssetPath(Selection.activeObject) : null;
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
            {
                Debug.LogWarning("[DotsRender] 请先选中一个文件夹。");
                return;
            }

            // 仅在确有改动（新增标记或列表变化）时落盘。
            var guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder });
            var anyChanged = false;
            foreach (var guid in guids)
            {
                if (MarkPrefabAsset(AssetDatabase.GUIDToAssetPath(guid)))
                    anyChanged = true;
            }

            if (anyChanged)
            {
                AssetDatabase.SaveAssets();
            }
        }

        /// <summary>收集 <paramref name="root"/> 的后代中带 <see cref="MeshRenderer"/> 或 <see cref="LODGroup"/> 的对象（不含 root 自身），深度优先、按层级顺序返回。</summary>
        public static List<GameObject> CollectTargets(GameObject root)
        {
            var result = new List<GameObject>();
            if (root == null)
            {
                return result;
            }

            var transform = root.transform;
            for (var i = 0; i < transform.childCount; i++)
                Collect(transform.GetChild(i).gameObject, result);
            return result;
        }

        private static void Collect(GameObject go, List<GameObject> result)
        {
            if (go == null)
            {
                return;
            }

            // 只认这两种目标类型。
            if (go.GetComponent<MeshRenderer>() != null || go.GetComponent<LODGroup>() != null)
                result.Add(go);

            var transform = go.transform;
            for (var i = 0; i < transform.childCount; i++)
                Collect(transform.GetChild(i).gameObject, result);
        }

        public static void MarkRecursive(IEnumerable<GameObject> roots)
        {
            foreach (var root in roots)
            {
                if (root == null)
                {
                    continue;
                }

                var assetPath = PrefabUtility.IsPartOfPrefabAsset(root)
                    ? AssetDatabase.GetAssetPath(root)
                    : null;
                if (!string.IsNullOrEmpty(assetPath))
                {
                    MarkPrefabAsset(assetPath);
                    continue;
                }

                // 标记只挂在被选中的父物体上，其子物体里的渲染目标分类填入两个列表。
                var authoring = EnsureMarker(root);
                FillTargets(authoring, CollectTargets(root));
                EditorUtility.SetDirty(authoring);
            }
        }

        public static void UnmarkRecursive(IEnumerable<GameObject> roots)
        {
            foreach (var root in roots)
            {
                if (root == null)
                {
                    continue;
                }

                var assetPath = PrefabUtility.IsPartOfPrefabAsset(root)
                    ? AssetDatabase.GetAssetPath(root)
                    : null;
                if (!string.IsNullOrEmpty(assetPath))
                {
                    UnmarkPrefabAsset(assetPath);
                    continue;
                }

                // 标记挂在父物体上；同时清掉后代里可能残留的标记。
                RemoveMarkerInScene(root);
                foreach (var target in CollectTargets(root))
                    RemoveMarkerInScene(target);
            }
        }

        /// <summary>给 prefab 资产根加标记并把子目标填进两个列表；仅在确有改动时写盘，没有可转换的子目标则跳过。</summary>
        /// <returns>是否改动了资产（据此决定是否需要 <see cref="AssetDatabase.SaveAssets"/>）。</returns>
        public static bool MarkPrefabAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".prefab"))
            {
                return false;
            }

            var contents = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                var targets = CollectTargets(contents);

                // 没有任何可转换的子目标：不加标记、也不动已有列表，直接跳过。
                if (targets.Count == 0)
                {
                    Debug.Log($"[DotsRender] {assetPath} 根对象没有任何可转换的子目标（MeshRenderer/LODGroup），已跳过。");
                    return false;
                }

                var authoring = contents.GetComponent<DotsRenderAuthoring>();
                var addedMarker = authoring == null;
                if (addedMarker)
                    authoring = contents.AddComponent<DotsRenderAuthoring>();

                var targetsChanged = FillTargets(authoring, targets);

                if (!addedMarker && !targetsChanged)
                {
                    return false;
                }

                PrefabUtility.SaveAsPrefabAsset(contents, assetPath);
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        public static void UnmarkPrefabAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".prefab"))
            {
                return;
            }

            var contents = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                var changed = RemoveMarkerInPrefab(contents);
                foreach (var target in CollectTargets(contents))
                {
                    if (RemoveMarkerInPrefab(target))
                    {
                        changed = true;
                    }
                }

                if (changed)
                {
                    PrefabUtility.SaveAsPrefabAsset(contents, assetPath);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // 已存在标记时记录一次 Undo，便于回滚随后的列表变更。
        private static DotsRenderAuthoring EnsureMarker(GameObject root)
        {
            var authoring = root.GetComponent<DotsRenderAuthoring>();
            if (authoring == null)
                return Undo.AddComponent<DotsRenderAuthoring>(root);

            Undo.RecordObject(authoring, "Fill DotsRender Targets");
            return authoring;
        }

        // 把收集结果分类写入两个列表：MeshRenderer → MeshRenderers；LODGroup → LodGroups。
        // 同一对象同时带两者时 LODGroup 优先，不重复收入 MeshRenderers。
        // 返回两个列表相对现状是否发生了变化（供调用方决定是否写盘）。
        private static bool FillTargets(DotsRenderAuthoring authoring, List<GameObject> targets)
        {
            var meshRenderers = new List<MeshRenderer>();
            var lodGroups = new List<LODGroup>();

            foreach (var target in targets)
            {
                var lodGroup = target.GetComponent<LODGroup>();
                if (lodGroup != null)
                {
                    lodGroups.Add(lodGroup);
                    continue;
                }

                var meshRenderer = target.GetComponent<MeshRenderer>();
                if (meshRenderer != null)
                    meshRenderers.Add(meshRenderer);
            }

            if (SameContents(authoring.MeshRenderers, meshRenderers)
                && SameContents(authoring.LodGroups, lodGroups))
                return false;

            authoring.MeshRenderers.Clear();
            authoring.MeshRenderers.AddRange(meshRenderers);
            authoring.LodGroups.Clear();
            authoring.LodGroups.AddRange(lodGroups);
            return true;
        }

        // 逐元素比较两个列表（顺序敏感；元素为 Unity 对象，引用相等即视为同一条目）。
        static bool SameContents<T>(List<T> a, List<T> b) where T : Object
        {
            if (a.Count != b.Count)
            {
                return false;
            }
            for (var i = 0; i < a.Count; i++)
                if (a[i] != b[i])
                {
                    return false;
                }
            return true;
        }

        private static void RemoveMarkerInScene(GameObject go)
        {
            var authoring = go.GetComponent<DotsRenderAuthoring>();
            if (authoring != null)
            {
                Undo.DestroyObjectImmediate(authoring);
            }
        }

        private static bool RemoveMarkerInPrefab(GameObject go)
        {
            var authoring = go.GetComponent<DotsRenderAuthoring>();
            if (authoring == null)
            {
                return false;
            }
            Object.DestroyImmediate(authoring, true);
            return true;
        }
    }
}
