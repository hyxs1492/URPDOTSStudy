using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace DOTSUtils.RenderBridge.Tests
{
    /// <summary>统一登记本会话创建的 Mesh / Material：它们会被持久单例 <see cref="DotsRenderConversionSystem"/> 的 <see cref="RenderMeshPool"/> 复用，用例之间绝不销毁，只在会话结束时一次性清理。</summary>
    internal static class TestRenderAssets
    {
        static readonly List<Object> s_Created = new();

        public static Mesh NewMesh(string name, int subMeshCount)
        {
            var mesh = new Mesh { name = name, subMeshCount = subMeshCount };
            s_Created.Add(mesh);
            return mesh;
        }

        public static Material NewMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var material = new Material(shader);
            s_Created.Add(material);
            return material;
        }

        public static void DisposeAll()
        {
            foreach (var asset in s_Created)
                if (asset != null) Object.DestroyImmediate(asset);
            s_Created.Clear();
        }
    }

    /// <summary>会话级清理：在所有 PlayMode 用例之后销毁共享资产。</summary>
    [SetUpFixture]
    public class RenderTestSessionScope
    {
        /// <summary>会话开始前统一复位三个跨 Play 会话会残留的静态配置（本工程关闭了域重载与场景重载，静态字段会跨 Play 保留）。</summary>
        [OneTimeSetUp]
        public void ResetSharedStaticState()
        {
            DOTSUtils.Spatial.SpatialSettings.Reset();
            DOTSUtils.Rvo.RvoSettings.Reset();
            DOTSUtils.Visibility.VisibilitySettings.Reset();
        }

        [OneTimeTearDown]
        public void Cleanup()
        {
            // 再做一次，避免本会话最后的用例把非默认配置带进下一次 Play。
            DOTSUtils.Spatial.SpatialSettings.Reset();
            DOTSUtils.Rvo.RvoSettings.Reset();
            DOTSUtils.Visibility.VisibilitySettings.Reset();
            TestRenderAssets.DisposeAll();
        }
    }
}
