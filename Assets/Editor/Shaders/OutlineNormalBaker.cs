using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 把「按位置平均后的法线」（平滑法线）烘焙进 mesh 的 UV3 通道（物体空间）。
///
/// 为什么需要：
///   反转外壳描边靠沿法线外扩。硬边模型上同一位置会有多个法线不同的顶点，
///   它们各自朝不同方向外扩，外壳就被撕开 —— 表现为描边在硬边处出现缺口/断裂。
///   把这些顶点在「相同位置」上取法线平均，外扩方向就一致了，外壳保持水密。
///
/// 为什么放 UV3：
///   - 只增加一个顶点属性，运行时零成本（不像逐顶点求邻近平均，那要每帧几十 MB 显存流量）
///   - 不动 position / normal / tangent / uv0，主体光照与纹理完全不受影响
///   - 本工程 mesh 的 UV1~UV7 均空闲，UV3 是安全选择
///
/// 注意：
///   - 这是**改 mesh 资产**的一次性操作，换模型/改模型后需要重烘。
///   - 会跳过 UV3 已被占用的 mesh，避免破坏别人的数据。
/// </summary>
public static class OutlineNormalBaker
{
    /// <summary>菜单入口：烘焙当前选中物体（含子物体）下的所有 mesh。</summary>
    [MenuItem("Tools/CustomLit/Bake Smooth Normals (UV3)")]
    static void BakeMenu()
    {
        int n = BakeSelection();
        if (n == 0)
            Debug.LogWarning("[OutlineNormalBaker] 没有烘焙任何 mesh：请先选中带 MeshFilter 的物体，" +
                             "并确认它们的 UV3 是空的（用 UV3 存别的数据的 mesh 会被跳过）。");
    }

    /// <summary>烘焙选中物体（含子物体）下所有 mesh。</summary>
    /// <returns>成功烘焙的 mesh 数量。</returns>
    public static int BakeSelection()
    {
        var seen = new HashSet<Mesh>();
        int count = 0;

        foreach (GameObject go in Selection.gameObjects)
        {
            foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                if (!seen.Add(mf.sharedMesh)) continue;          // 同一 mesh 只烘一次
                if (BakeMesh(mf.sharedMesh)) count++;
            }

            // SkinnedMeshRenderer 也一并处理
            foreach (SkinnedMeshRenderer smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                if (!seen.Add(smr.sharedMesh)) continue;
                if (BakeMesh(smr.sharedMesh)) count++;
            }
        }

        if (count > 0)
        {
            AssetDatabase.SaveAssets();
            Debug.Log(string.Format("[OutlineNormalBaker] 已烘焙 {0} 个 mesh 的平滑法线到 UV3。", count));
        }
        return count;
    }

    /// <summary>对单个 mesh 烘焙。UV3 被占用或无顶点数据时返回 false。</summary>
    public static bool BakeMesh(Mesh mesh)
    {
        if (mesh == null) return false;

        if (!mesh.isReadable)
        {
            Debug.LogWarning(string.Format(
                "[OutlineNormalBaker] 跳过 \"{0}\"：mesh 不可读（请在模型导入设置里勾选 Read/Write Enabled）。", mesh.name));
            return false;
        }

        if (mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord3) > 0)
        {
            Debug.LogWarning(string.Format(
                "[OutlineNormalBaker] 跳过 \"{0}\"：UV3 已被占用，避免破坏原有数据。", mesh.name));
            return false;
        }

        Vector3[] vertices = mesh.vertices;
        Vector3[] normals = mesh.normals;
        int n = vertices.Length;
        if (n == 0 || normals == null || normals.Length != n) return false;

        // 1) 按位置分组，累加法线
        var sums = new Dictionary<Vector3Int, Vector3>(n);
        var keys = new Vector3Int[n];
        for (int i = 0; i < n; i++)
        {
            // 量化到 1e-4，避免浮点误差把同一位置的顶点分到不同组
            var key = new Vector3Int(
                Mathf.RoundToInt(vertices[i].x * 10000f),
                Mathf.RoundToInt(vertices[i].y * 10000f),
                Mathf.RoundToInt(vertices[i].z * 10000f));
            keys[i] = key;

            Vector3 cur;
            sums[key] = sums.TryGetValue(key, out cur) ? cur + normals[i] : normals[i];
        }

        // 2) 归一化后写回每个顶点（同位置顶点拿到同一个平滑法线）
        var smoothNormals = new List<Vector3>(n);
        for (int i = 0; i < n; i++)
        {
            Vector3 avg = sums[keys[i]];
            float mag = avg.magnitude;
            smoothNormals.Add(mag > 1e-6f ? avg / mag : normals[i]);
        }

        mesh.SetUVs(3, smoothNormals);
        EditorUtility.SetDirty(mesh);
        return true;
    }
}
