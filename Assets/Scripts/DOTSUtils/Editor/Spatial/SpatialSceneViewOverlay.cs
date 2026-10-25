using UnityEditor;

namespace DOTSUtils.Spatial.EditorTools
{
    /// <summary>
    /// SceneView 绘制：把 <see cref="SpatialVisualizerWindow"/> 每帧取好的线段缓冲画到场景里。
    ///
    /// 取数与绘制开关都在窗口那边，这里只在 <c>SceneView.duringSceneGui</c> 里把结果交给窗口去画。
    /// </summary>
    [InitializeOnLoad]
    public static class SpatialSceneViewOverlay
    {
        static SpatialSceneViewOverlay()
        {
            // 先减再加以防重复注册。
            SceneView.duringSceneGui -= OnSceneGui;
            SceneView.duringSceneGui += OnSceneGui;
        }

        private static void OnSceneGui(SceneView view)
        {
            var window = SpatialVisualizerWindow.Instance;
            if (window == null)
            {
                return;
            }

            window.DrawSceneView(view);
        }
    }
}
