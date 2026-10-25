#ifndef CUSTOM_LIT_INPUT_INCLUDED
#define CUSTOM_LIT_INPUT_INCLUDED

// ============================================================================
//  CustomLitInput.hlsl
//  材质属性（UnityPerMaterial CBUFFER + DOTS instancing 元数据）与表面数据采样。
//
//  约束（与 URP Lit 保持一致，保证 SRP Batcher / Entities Graphics 兼容）：
//   1. 所有材质标量必须放在同一个 UnityPerMaterial CBUFFER 中，禁止用 #ifdef 分割。
//   2. 需要 per-instance 覆盖（Entities Graphics）的属性，同时声明到
//      UNITY_DOTS_INSTANCING 元数据块中，并在 DOTS 开启时重定向到静态缓存。
//   3. 贴图不能被 DOTS instancing，只能作为普通 TEXTURE2D 声明。
//
//  MRO 贴图通道约定（glTF 风格）：
//   R = Metallic, G = Roughness, B = Occlusion(AO)
//   （smoothness = 1 - roughness，在采样时转换）
// ============================================================================

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"

// _BaseMap / _BumpMap / _EmissionMap 及 SampleAlbedoAlpha/SampleNormal/SampleEmission
// 等辅助函数已由 SurfaceInput.hlsl 声明，此处只声明额外贴图。
TEXTURE2D(_MROMap);           SAMPLER(sampler_MROMap);          // R:Metallic G:Roughness B:Occlusion
TEXTURE2D(_DissolveNoiseMap); SAMPLER(sampler_DissolveNoiseMap); // R: 噪声

// Detail Inputs（与 URP Lit 对齐）
TEXTURE2D(_DetailMask);       SAMPLER(sampler_DetailMask);       // A: 细节遮罩
TEXTURE2D(_DetailAlbedoMap);  SAMPLER(sampler_DetailAlbedoMap);  // RGB: 细节颜色
TEXTURE2D(_DetailNormalMap);  SAMPLER(sampler_DetailNormalMap);  // RGB: 细节法线

// NOTE: Do not ifdef the properties here as SRP batcher can not handle different layouts.
CBUFFER_START(UnityPerMaterial)
float4 _BaseMap_ST;

half4 _BaseColor;
half  _Cutoff;
half  _Surface;

// ---- Surface Options ----
half  _WorkflowMode;
half  _Blend;
half  _BlendModePreserveSpecular;
half  _ZWrite;
half  _SrcBlend;
half  _DstBlend;

// ---- PBR (MRO) ----
half  _Metallic;
half  _Roughness;
half  _OcclusionStrength;

// ---- 法线 ----
half  _BumpScale;

// ---- Detail Inputs ----
float4 _DetailAlbedoMap_ST;
half  _DetailAlbedoMapScale;
half  _DetailNormalMapScale;

// ---- 自发光 ----
half4 _EmissionColor;

// ---- 外发光 (Rim) ----
half4 _RimColor;
half  _RimPower;
half  _RimIntensity;

// ---- 溶解 (Dissolve) ----
half4 _DissolveEdgeColor;
float4 _DissolveCenter;
float  _DissolveRadius;
half   _DissolveAmount;
half   _DissolveNoiseStrength;
half   _DissolveDirectionMode;
half   _DissolveNoiseScale;
half   _DissolveCutoff;
half   _DissolveEdgeWidth;

// ---- 描边 (Outline) ----
half4 _OutlineColor;
half  _OutlineWidthPixels;
half  _OutlineZTest;
half  _OutlineZWrite;
CBUFFER_END

// NOTE: Do not ifdef the properties for dots instancing, but ifdef the actual usage.
// Otherwise you might break CPU-side as property constant-buffer offsets change per variant.
// NOTE: Dots instancing is orthogonal to the constant buffer above.
#ifdef UNITY_DOTS_INSTANCING_ENABLED

UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
    // 顺序必须与上面的 UnityPerMaterial CBUFFER 保持一致（纹理 ST 除外）：
    // 否则 DOTS 材质常量缓冲与 SRP Batcher 的 CBUFFER 布局错位，BRG 渲染会报
    // "wrong cbuffer setup. Missing DOTS_INSTANCING_ON variant?"。
    UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
    UNITY_DOTS_INSTANCED_PROP(float , _Cutoff)
    UNITY_DOTS_INSTANCED_PROP(float , _Surface)
    UNITY_DOTS_INSTANCED_PROP(float , _WorkflowMode)
    UNITY_DOTS_INSTANCED_PROP(float , _Blend)
    UNITY_DOTS_INSTANCED_PROP(float , _BlendModePreserveSpecular)
    UNITY_DOTS_INSTANCED_PROP(float , _ZWrite)
    UNITY_DOTS_INSTANCED_PROP(float , _SrcBlend)
    UNITY_DOTS_INSTANCED_PROP(float , _DstBlend)
    UNITY_DOTS_INSTANCED_PROP(float , _Metallic)
    UNITY_DOTS_INSTANCED_PROP(float , _Roughness)
    UNITY_DOTS_INSTANCED_PROP(float , _OcclusionStrength)
    UNITY_DOTS_INSTANCED_PROP(float , _BumpScale)
    UNITY_DOTS_INSTANCED_PROP(float , _DetailAlbedoMapScale)
    UNITY_DOTS_INSTANCED_PROP(float , _DetailNormalMapScale)
    UNITY_DOTS_INSTANCED_PROP(float4, _EmissionColor)
    UNITY_DOTS_INSTANCED_PROP(float4, _RimColor)
    UNITY_DOTS_INSTANCED_PROP(float , _RimPower)
    UNITY_DOTS_INSTANCED_PROP(float , _RimIntensity)
    UNITY_DOTS_INSTANCED_PROP(float4, _DissolveEdgeColor)
    UNITY_DOTS_INSTANCED_PROP(float4, _DissolveCenter)
    UNITY_DOTS_INSTANCED_PROP(float , _DissolveRadius)
    UNITY_DOTS_INSTANCED_PROP(float , _DissolveAmount)
    UNITY_DOTS_INSTANCED_PROP(float , _DissolveNoiseStrength)
    UNITY_DOTS_INSTANCED_PROP(float , _DissolveDirectionMode)
    UNITY_DOTS_INSTANCED_PROP(float , _DissolveNoiseScale)
    UNITY_DOTS_INSTANCED_PROP(float , _DissolveCutoff)
    UNITY_DOTS_INSTANCED_PROP(float , _DissolveEdgeWidth)
    UNITY_DOTS_INSTANCED_PROP(float4, _OutlineColor)
    UNITY_DOTS_INSTANCED_PROP(float , _OutlineWidthPixels)
    UNITY_DOTS_INSTANCED_PROP(float , _OutlineZTest)
    UNITY_DOTS_INSTANCED_PROP(float , _OutlineZWrite)
UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

// 属性读取缓存到静态变量，避免编译器在每个使用点重新生成加载代码（参考 URP Lit 的做法）。
static float4 unity_DOTS_Sampled_BaseColor;
static float4 unity_DOTS_Sampled_EmissionColor;
static float4 unity_DOTS_Sampled_RimColor;
static float4 unity_DOTS_Sampled_DissolveEdgeColor;
static float4 unity_DOTS_Sampled_DissolveCenter;
static float4 unity_DOTS_Sampled_OutlineColor;
static float  unity_DOTS_Sampled_Cutoff;
static float  unity_DOTS_Sampled_WorkflowMode;
static float  unity_DOTS_Sampled_Blend;
static float  unity_DOTS_Sampled_BlendModePreserveSpecular;
static float  unity_DOTS_Sampled_ZWrite;
static float  unity_DOTS_Sampled_SrcBlend;
static float  unity_DOTS_Sampled_DstBlend;
static float  unity_DOTS_Sampled_Metallic;
static float  unity_DOTS_Sampled_Roughness;
static float  unity_DOTS_Sampled_OcclusionStrength;
static float  unity_DOTS_Sampled_BumpScale;
static float  unity_DOTS_Sampled_DetailAlbedoMapScale;
static float  unity_DOTS_Sampled_DetailNormalMapScale;
static float  unity_DOTS_Sampled_RimPower;
static float  unity_DOTS_Sampled_RimIntensity;
static float  unity_DOTS_Sampled_DissolveAmount;
static float  unity_DOTS_Sampled_DissolveRadius;
static float  unity_DOTS_Sampled_DissolveNoiseStrength;
static float  unity_DOTS_Sampled_DissolveDirectionMode;
static float  unity_DOTS_Sampled_DissolveNoiseScale;
static float  unity_DOTS_Sampled_DissolveCutoff;
static float  unity_DOTS_Sampled_DissolveEdgeWidth;
static float  unity_DOTS_Sampled_OutlineWidthPixels;
static float  unity_DOTS_Sampled_OutlineZTest;
static float  unity_DOTS_Sampled_OutlineZWrite;
static float  unity_DOTS_Sampled_Surface;

void SetupDOTSCustomLitMaterialPropertyCaches()
{
    unity_DOTS_Sampled_BaseColor              = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor);
    unity_DOTS_Sampled_EmissionColor          = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _EmissionColor);
    unity_DOTS_Sampled_RimColor               = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _RimColor);
    unity_DOTS_Sampled_DissolveEdgeColor      = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _DissolveEdgeColor);
    unity_DOTS_Sampled_DissolveCenter         = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _DissolveCenter);
    unity_DOTS_Sampled_OutlineColor           = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _OutlineColor);
    unity_DOTS_Sampled_Cutoff                 = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Cutoff);
    unity_DOTS_Sampled_WorkflowMode           = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _WorkflowMode);
    unity_DOTS_Sampled_Blend                  = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Blend);
    unity_DOTS_Sampled_BlendModePreserveSpecular = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _BlendModePreserveSpecular);
    unity_DOTS_Sampled_ZWrite                 = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _ZWrite);
    unity_DOTS_Sampled_SrcBlend               = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _SrcBlend);
    unity_DOTS_Sampled_DstBlend               = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DstBlend);
    unity_DOTS_Sampled_Metallic               = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Metallic);
    unity_DOTS_Sampled_Roughness              = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Roughness);
    unity_DOTS_Sampled_OcclusionStrength      = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _OcclusionStrength);
    unity_DOTS_Sampled_BumpScale              = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _BumpScale);
    unity_DOTS_Sampled_DetailAlbedoMapScale   = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DetailAlbedoMapScale);
    unity_DOTS_Sampled_DetailNormalMapScale   = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DetailNormalMapScale);
    unity_DOTS_Sampled_RimPower               = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _RimPower);
    unity_DOTS_Sampled_RimIntensity           = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _RimIntensity);
    unity_DOTS_Sampled_DissolveAmount         = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DissolveAmount);
    unity_DOTS_Sampled_DissolveRadius         = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DissolveRadius);
    unity_DOTS_Sampled_DissolveNoiseStrength  = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DissolveNoiseStrength);
    unity_DOTS_Sampled_DissolveDirectionMode  = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DissolveDirectionMode);
    unity_DOTS_Sampled_DissolveNoiseScale     = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DissolveNoiseScale);
    unity_DOTS_Sampled_DissolveCutoff         = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DissolveCutoff);
    unity_DOTS_Sampled_DissolveEdgeWidth      = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DissolveEdgeWidth);
    unity_DOTS_Sampled_OutlineWidthPixels     = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _OutlineWidthPixels);
    unity_DOTS_Sampled_OutlineZTest           = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _OutlineZTest);
    unity_DOTS_Sampled_OutlineZWrite          = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _OutlineZWrite);
    unity_DOTS_Sampled_Surface                = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Surface);
}

#undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
#define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupDOTSCustomLitMaterialPropertyCaches()

#define _BaseColor              unity_DOTS_Sampled_BaseColor
#define _EmissionColor          unity_DOTS_Sampled_EmissionColor
#define _RimColor               unity_DOTS_Sampled_RimColor
#define _DissolveEdgeColor      unity_DOTS_Sampled_DissolveEdgeColor
#define _DissolveCenter         unity_DOTS_Sampled_DissolveCenter
#define _OutlineColor           unity_DOTS_Sampled_OutlineColor
#define _Cutoff                 unity_DOTS_Sampled_Cutoff
#define _WorkflowMode           unity_DOTS_Sampled_WorkflowMode
#define _Blend                  unity_DOTS_Sampled_Blend
#define _BlendModePreserveSpecular unity_DOTS_Sampled_BlendModePreserveSpecular
#define _ZWrite                 unity_DOTS_Sampled_ZWrite
#define _SrcBlend               unity_DOTS_Sampled_SrcBlend
#define _DstBlend               unity_DOTS_Sampled_DstBlend
#define _Metallic               unity_DOTS_Sampled_Metallic
#define _Roughness              unity_DOTS_Sampled_Roughness
#define _OcclusionStrength      unity_DOTS_Sampled_OcclusionStrength
#define _BumpScale              unity_DOTS_Sampled_BumpScale
#define _DetailAlbedoMapScale   unity_DOTS_Sampled_DetailAlbedoMapScale
#define _DetailNormalMapScale   unity_DOTS_Sampled_DetailNormalMapScale
#define _RimPower               unity_DOTS_Sampled_RimPower
#define _RimIntensity           unity_DOTS_Sampled_RimIntensity
#define _DissolveAmount         unity_DOTS_Sampled_DissolveAmount
#define _DissolveRadius         unity_DOTS_Sampled_DissolveRadius
#define _DissolveNoiseStrength  unity_DOTS_Sampled_DissolveNoiseStrength
#define _DissolveDirectionMode  unity_DOTS_Sampled_DissolveDirectionMode
#define _DissolveNoiseScale     unity_DOTS_Sampled_DissolveNoiseScale
#define _DissolveCutoff         unity_DOTS_Sampled_DissolveCutoff
#define _DissolveEdgeWidth      unity_DOTS_Sampled_DissolveEdgeWidth
#define _OutlineWidthPixels     unity_DOTS_Sampled_OutlineWidthPixels
#define _OutlineZTest           unity_DOTS_Sampled_OutlineZTest
#define _OutlineZWrite          unity_DOTS_Sampled_OutlineZWrite
#define _Surface                unity_DOTS_Sampled_Surface

#endif // UNITY_DOTS_INSTANCING_ENABLED

// ----------------------------------------------------------------------------
// Detail Inputs（与 URP Lit 对齐）
//   _DETAIL          : 有 Detail 贴图时由 GUI 打开
//   _DETAIL_MULX2    : Detail Albedo 直接 ×2（Scale == 1，URP 默认路径）
//   _DETAIL_SCALED   : Detail Albedo 按 Scale 缩放（Scale != 1）
//   Detail 与 BaseMap 共用 UV（URP 的做法），Tiling/Offset 取自 _DetailAlbedoMap_ST
//
//   注意：这两个函数的入参/返回一律用 float3，不能写 half3。
//   SurfaceData.albedo / normalTS 是 float3，若函数用 half3 会在调用点把数据
//   收窄到 half 再转回 float —— 即使 _DETAIL 关闭（函数体退化为 return 原值）
//   也会改变光照结果，实测会造成整机 25.8/255 的平均亮度偏移（"麻点"）。
// ----------------------------------------------------------------------------
float3 ApplyDetailAlbedo(float3 albedo, float2 uv)
{
#if defined(_DETAIL)
    half detailMask = SAMPLE_TEXTURE2D(_DetailMask, sampler_DetailMask, uv).a;
    float2 detailUv = uv * _DetailAlbedoMap_ST.xy + _DetailAlbedoMap_ST.zw;
    half3 detailAlbedo = SAMPLE_TEXTURE2D(_DetailAlbedoMap, sampler_DetailAlbedoMap, detailUv).rgb;
#if defined(_DETAIL_SCALED)
    // URP ScaleDetailAlbedo：Scale 越大，亮暗被拉得越开
    detailAlbedo = half(2.0) * detailAlbedo * _DetailAlbedoMapScale - _DetailAlbedoMapScale + half(1.0);
#else
    detailAlbedo = half(2.0) * detailAlbedo;
#endif
    albedo = albedo * LerpWhiteTo(detailAlbedo, detailMask);
#endif
    return albedo;
}

float3 ApplyDetailNormal(float3 normalTS, float2 uv)
{
#if defined(_DETAIL) && defined(_NORMALMAP)
    half detailMask = SAMPLE_TEXTURE2D(_DetailMask, sampler_DetailMask, uv).a;
    float2 detailUv = uv * _DetailAlbedoMap_ST.xy + _DetailAlbedoMap_ST.zw;
    half3 detailNormalTS = UnpackNormalScale(
        SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap, detailUv), _DetailNormalMapScale);
    detailNormalTS = normalize(detailNormalTS);
    normalTS = lerp(normalTS, BlendNormalRNM(normalTS, detailNormalTS), detailMask);
#endif
    return normalTS;
}

// ----------------------------------------------------------------------------
// 表面数据初始化（Forward / 描边等可见性 pass 使用）
// ----------------------------------------------------------------------------
void InitializeCustomLitSurfaceData(float2 uv, out SurfaceData surfaceData)
{
    surfaceData = (SurfaceData)0;

    half4 albedoAlpha = SampleAlbedoAlpha(uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap));
    surfaceData.alpha = Alpha(albedoAlpha.a, _BaseColor, _Cutoff);
    surfaceData.albedo = albedoAlpha.rgb * _BaseColor.rgb;

#if defined(_NORMALMAP)
    surfaceData.normalTS = SampleNormal(uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _BumpScale);
#else
    surfaceData.normalTS = half3(0.0h, 0.0h, 1.0h);
#endif

    surfaceData.emission = SampleEmission(uv, _EmissionColor.rgb, TEXTURE2D_ARGS(_EmissionMap, sampler_EmissionMap));

    // ---- MRO: R=Metallic, G=Roughness, B=Occlusion ----
#if defined(_MRO_MAP)
    half4 mro = SAMPLE_TEXTURE2D(_MROMap, sampler_MROMap, uv);
    surfaceData.metallic   = mro.r * _Metallic;
    surfaceData.smoothness = 1.0h - saturate(mro.g * _Roughness);
    surfaceData.occlusion  = LerpWhiteTo(mro.b, _OcclusionStrength);
#else
    surfaceData.metallic   = _Metallic;
    surfaceData.smoothness = 1.0h - saturate(_Roughness);
    surfaceData.occlusion  = 1.0h;
#endif

    surfaceData.specular            = half3(0.0h, 0.0h, 0.0h);
    surfaceData.clearCoatMask       = 0.0h;
    surfaceData.clearCoatSmoothness = 0.0h;
    //surfaceData.specularOcclusion   = 1.0h;
}

#endif // CUSTOM_LIT_INPUT_INCLUDED
