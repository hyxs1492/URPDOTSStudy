using System.Runtime.CompilerServices;

// Samples 是独立程序集（DOTSUtils.Samples），但 benchmark 例子要用 Runtime 里的 internal 工具
// （例如 AuthoringSnapshot.CollectRenderTargets —— 走桥路径要给实例补齐渲染目标）。
// 这里只开放给 Samples，不改变这些类型的公开可见性。
[assembly: InternalsVisibleTo("DOTSUtils.Samples")]
