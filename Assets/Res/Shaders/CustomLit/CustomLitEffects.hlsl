#ifndef CUSTOM_LIT_EFFECTS_INCLUDED
#define CUSTOM_LIT_EFFECTS_INCLUDED

// ============================================================================
//  CustomLitEffects.hlsl —— 可扩展的效果层
//
//  这里集中放置所有"叠加式"视觉效果。扩展新效果的标准做法：
//   1. 在 CustomLit.shader 的 Properties 中添加属性；
//   2. 在 CustomLitInput.hlsl 的 CBUFFER（+ DOTS 元数据，如需 per-instance）中声明；
//   3. 在本文件编写效果函数，并在对应 pass 的 "效果 hooks" 处调用；
//   4. 在 CustomLit.shader 的相关 pass 添加 #pragma shader_feature_local _XXX；
//      在 Editor/CustomLitShaderGUI.cs 中暴露开关（[Toggle(_XXX)] 即可自动同步 keyword）。
//
//  注意：本文件必须晚于 CustomLitInput.hlsl 被包含（依赖其中的属性声明）。
// ============================================================================

// ----------------------------------------------------------------------------
// 溶解 (Dissolve) —— 方向可选：径向 / X / Y / Z
//
// 因子 f 的方向约定（关键）：
//   f = 0 表示"原点/起始端"（最先被保留），f = 1 表示"最远端"（最先被吃掉）。
//   保留区 = { p | f(p) + noise < threshold }，
//   threshold 会把噪声幅度也算进去，所以：
//     _DissolveAmount = 0 -> 连噪声最强处都切掉 -> 全空
//     _DissolveAmount = 1 -> 连噪声最弱处都保留 -> 全显
//   这样边缘发光不会在 Amount=0/1 时残留。
//
//   轴向：f = saturate(0.5 - offset·axis / radius)
//     => radius 取「该轴尺寸的一半」时，f 从 0 到 1 走完全程。
//   径向：f = saturate(1 - |p - origin| / radius)
//     => radius 取到最远角落的距离。
//
//   物体空间计算，所以物体平移/旋转不影响溶解图案。
//   注意：入参用 float3（positionWS 是 float），不要用 half 去接 SurfaceData 的量。
// ----------------------------------------------------------------------------
half ComputeDissolveFactor(float3 positionWS)
{
    float3 positionOS = TransformWorldToObject(positionWS);
    float3 offsetOS   = positionOS - _DissolveCenter.xyz;
    half   invRadius  = half(rcp(max(_DissolveRadius, 1e-5)));
    half   v;

#if defined(_DISSOLVE_AXIS_X)
    v = saturate(0.5h - offsetOS.x * invRadius);
#elif defined(_DISSOLVE_AXIS_Y)
    v = saturate(0.5h - offsetOS.y * invRadius);
#elif defined(_DISSOLVE_AXIS_Z)
    v = saturate(0.5h - offsetOS.z * invRadius);
#else   // _DISSOLVE_RADIAL 或未设置时的回退：径向
    v = saturate(1.0h - length(offsetOS) * invRadius);
#endif
    return v;
}

// 采样噪声并裁剪；返回边缘因子：0 = 紧贴溶解边界，1 = 完全处于保留区内部。
// 需要在所有 pass（Forward/Shadow/Depth/Outline）中调用，保证深度与阴影同步溶解。
half ApplyDissolve(half dissolveFactor, float2 uv)
{
    // 噪声以 0 为中心、幅度 ±_DissolveNoiseStrength；把这段幅度并入阈值映射，
    // 保证 Amount=0 时连噪声最强处也被切掉（否则会残留一条"边缘"）。
    half noiseAmp  = _DissolveNoiseStrength;
    half amount    = _DissolveAmount;
    half threshold = amount * (half(1.0) + noiseAmp * 2.0h) - noiseAmp - _DissolveCutoff;

    // frac 防止长时间运行后精度漂移
    half phase  = frac(_Time.y * 0.5);
    half noise  = SAMPLE_TEXTURE2D(_DissolveNoiseMap, sampler_DissolveNoiseMap,
                                   uv * _DissolveNoiseScale + phase).r;
    half t      = dissolveFactor + (noise - 0.5h) * noiseAmp;
    clip(threshold - t);
    // 用 Pow4 收窄过渡带，否则 Edge Width 一调大整块表面都会发亮
    half edge = saturate((threshold - t) * rcp(max(_DissolveEdgeWidth, 1e-4)));
    return Pow4(edge);
}

// 边缘发光颜色（叠加到 emission，可被 Bloom 捕获）
half3 ApplyDissolveEdgeGlow(half edgeFactor)
{
    return _DissolveEdgeColor.rgb * _DissolveEdgeColor.a * (1.0h - edgeFactor);
}

// ----------------------------------------------------------------------------
// 外发光 (Rim / Fresnel Glow)
// ----------------------------------------------------------------------------
half3 ApplyRim(half3 normalWS, half3 viewDirWS)
{
    half fresnel = 1.0h - saturate(dot(normalWS, viewDirWS));
    return _RimColor.rgb * _RimColor.a * pow(fresnel, _RimPower) * _RimIntensity;
}

#endif // CUSTOM_LIT_EFFECTS_INCLUDED
