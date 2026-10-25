using UnityEngine;

namespace DOTSUtils.Benchmark
{
    /// <summary>
    /// 为 benchmark 生成 N 个仅 <c>_BaseColor</c> 不同的材质（色相均匀分布，便于肉眼区分）。
    /// 材质是运行时对象，不落盘；清理请调 <see cref="DestroyAll"/>。
    /// </summary>
    public static class BenchmarkMaterialFactory
    {
        public static Material[] Create(int variants, Shader shader)
        {
            if (shader == null)
            {
                Debug.LogWarning("[Benchmark] 材质工厂拿到空 shader，无法生成材质。");
                return new Material[0];
            }

            variants = Mathf.Max(1, variants);
            var materials = new Material[variants];
            for (var i = 0; i < variants; i++)
            {
                var material = new Material(shader) { name = "BenchmarkMaterial_" + i };
                var hue = variants == 1 ? 0f : i / (float)variants;
                material.SetColor("_BaseColor", Color.HSVToRGB(hue, 0.65f, 0.95f));
                materials[i] = material;
            }
            return materials;
        }

        /// <summary>清理 <see cref="Create"/> 生成的材质；Play 用 <c>Object.Destroy</c>，EditMode 用 <c>DestroyImmediate</c>。</summary>
        public static void DestroyAll(Material[] materials)
        {
            if (materials == null)
            {
                return;
            }
            for (var i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (material == null)
                {
                    continue;
                }
                materials[i] = null;
                if (Application.isPlaying)
                {
                    Object.Destroy(material);
                }
                else Object.DestroyImmediate(material);
            }
        }
    }
}
