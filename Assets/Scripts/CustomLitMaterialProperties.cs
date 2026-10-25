using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;

namespace CustomLit
{
    // ============================================================================
    //  CustomLit 的 Entities Graphics per-instance 材质属性覆盖组件
    //
    //  只保留**运行时逐实体真的会变**的参数。静态外观参数（金属度、粗糙度、AO、
    //  法线强度、Detail 强度、Alpha 阈值、描边模式等）请在材质球上配置，不在这里。
    //
    //  用法：
    //    EntityManager.AddComponentData(entity, new OutlineWidthPixels { Value = 6f });
    //    EntityManager.SetComponentData(entity, new DissolveAmount { Value = 0.5f });
    //    或 Baker 从 MonoBehaviour / ScriptableObject 转换（推荐，避免运行中加组件类型）。
    //
    //  字段上的 [Range] 会被 Entities 内置的 SliderAttributeInspector 解析成滑条
    //  （对应 Unity 自带的 UnityEngine.RangeAttribute）。
    //
    //  ── 硬约束 ──────────────────────────────────────────────────────────────
    //  ① 类型必须与 shader 一致：
    //     CustomLitInput.hlsl 的 UNITY_DOTS_INSTANCING_START 里每个属性是 float 还是
    //     float4，必须和这里字段类型完全一致，否则 BatchRendererGroup 常量缓冲布局错位。
    //  ② 属性必须已声明为 DOTS 属性：新增可覆盖属性要同时改 shader 的 CBUFFER、
    //     UNITY_DOTS_INSTANCING_START、缓存、#define 四处。
    //  ③ 注册成本：只要声明了 MaterialProperty 组件类型，就占 Entities Graphics 的
    //     类型预算（kMaxTypes = 128，ComponentTypeCache.cs:82），与实体是否使用无关。
    //     本文件 14 个类型约占 11%。
    //  ④ 值从哪来：Entities Graphics 在地址 0 保留全零区域，因此**没加该组件的实体会
    //     读到 0**，不是垃圾数据、也不是材质球的值。所以「想让实体沿用材质参数就不要
    //     加该组件」；一旦加了请显式赋初值（_BaseColor 回落到 0 会渲染成纯黑）。
    //  ⑤ 与内置组件重名：Unity.Rendering 的 URPMaterialPropertyBaseColor / Cutoff /
    //     EmissionColor / Metallic / OcclusionStrength / BumpScale 也映射到同名属性。
    //     别给同一实体同时加两个映射到同一属性的组件（结果取决于注册顺序）。
    // ============================================================================

    // ---------------------------------------------------------------------------
    //  颜色 / 发光
    // ---------------------------------------------------------------------------

    /// <summary>主颜色（RGBA）。</summary>
    [MaterialProperty("_BaseColor")]
    public struct BaseColor : IComponentData
    {
        public float4 Value;
    }

    /// <summary>自发光颜色（HDR）。</summary>
    [MaterialProperty("_EmissionColor")]
    public struct EmissionColor : IComponentData
    {
        public float4 Value;
    }

    /// <summary>外发光（Rim）颜色（HDR）。</summary>
    [MaterialProperty("_RimColor")]
    public struct RimColor : IComponentData
    {
        public float4 Value;
    }

    /// <summary>外发光强度。</summary>
    [MaterialProperty("_RimIntensity")]
    public struct RimIntensity : IComponentData
    {
        [Range(0f, 16f)] public float Value;
    }

    

    // ---------------------------------------------------------------------------
    //  描边
    // ---------------------------------------------------------------------------

    /// <summary>描边颜色（HDR）。</summary>
    [MaterialProperty("_OutlineColor")]
    public struct OutlineColor : IComponentData
    {
        public float4 Value;
    }

    /// <summary>描边宽度（像素）。</summary>
    [MaterialProperty("_OutlineWidthPixels")]
    public struct OutlineWidthPixels : IComponentData
    {
        [Range(0f, 64f)] public float Value;
    }

    // ---------------------------------------------------------------------------
    //  溶解
    // ---------------------------------------------------------------------------

    /// <summary>溶解进度：0 = 全空（完全溶解），1 = 全显。</summary>
    [MaterialProperty("_DissolveAmount")]
    public struct DissolveAmount : IComponentData
    {
        [Range(0f, 1f)] public float Value;
    }

    /// <summary>溶解半径（物体单位）。随物体尺寸/溶解尺度变化。默认 1。</summary>
    [MaterialProperty("_DissolveRadius")]
    public struct DissolveRadius : IComponentData
    {
        [Range(0.0001f, 64f)] public float Value;
    }

    /// <summary>溶解噪声强度：0 = 边缘干净，越大越碎。</summary>
    [MaterialProperty("_DissolveNoiseStrength")]
    public struct DissolveNoiseStrength : IComponentData
    {
        [Range(0f, 1f)] public float Value;
    }

    /// <summary>溶解噪声缩放（空间频率）。默认 1。</summary>
    [MaterialProperty("_DissolveNoiseScale")]
    public struct DissolveNoiseScale : IComponentData
    {
        [Range(0.01f, 32f)] public float Value;
    }

    /// <summary>溶解阈值偏移（正 = 更晚溶解）。默认 0。</summary>
    [MaterialProperty("_DissolveCutoff")]
    public struct DissolveCutoff : IComponentData
    {
        [Range(-1f, 1f)] public float Value;
    }

    /// <summary>溶解边缘发光颜色（HDR）。</summary>
    [MaterialProperty("_DissolveEdgeColor")]
    public struct DissolveEdgeColor : IComponentData
    {
        public float4 Value;
    }

    /// <summary>溶解边缘宽度。默认 0.08。</summary>
    [MaterialProperty("_DissolveEdgeWidth")]
    public struct DissolveEdgeWidth : IComponentData
    {
        [Range(0.001f, 0.5f)] public float Value;
    }
}
