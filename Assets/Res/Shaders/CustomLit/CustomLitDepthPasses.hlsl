#ifndef CUSTOM_LIT_DEPTH_PASSES_INCLUDED
#define CUSTOM_LIT_DEPTH_PASSES_INCLUDED

// ============================================================================
//  CustomLitDepthPasses.hlsl —— ShadowCaster / DepthOnly / DepthNormals
//  这些 pass 同样执行 Alpha 裁剪与径向溶解裁剪，保证深度、阴影与可见效果一致。
// ============================================================================

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
#if defined(LOD_FADE_CROSSFADE)
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

// ----------------------------------------------------------------------------
// ShadowCaster
// ----------------------------------------------------------------------------
float3 _LightDirection;
float3 _LightPosition;

struct ShadowCasterAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
#if defined(_ALPHATEST_ON) || defined(_DISSOLVE_ON)
    float2 texcoord   : TEXCOORD0;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct ShadowCasterVaryings
{
#if defined(_ALPHATEST_ON) || defined(_DISSOLVE_ON)
    float2 uv : TEXCOORD0;
#endif
#if defined(_DISSOLVE_ON)
    float3 positionWS : TEXCOORD1;   // 溶解逐片元计算，低模也有正确梯度
#endif
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

ShadowCasterVaryings CustomLitShadowPassVertex(ShadowCasterAttributes input)
{
    ShadowCasterVaryings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    float3 normalWS   = TransformObjectToWorldNormal(input.normalOS);

#if defined(_ALPHATEST_ON) || defined(_DISSOLVE_ON)
    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
#endif
#if defined(_DISSOLVE_ON)
    output.positionWS = positionWS;
#endif

#if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif

    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

#if UNITY_REVERSED_Z
    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#else
    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#endif

    output.positionCS = positionCS;
    return output;
}

half4 CustomLitShadowPassFragment(ShadowCasterVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);

#ifdef LOD_FADE_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif

#if defined(_ALPHATEST_ON)
    half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
    clip(alpha - _Cutoff);
#endif
#if defined(_DISSOLVE_ON)
    ApplyDissolve(ComputeDissolveFactor(input.positionWS), input.uv);
#endif
    return 0;
}

// ----------------------------------------------------------------------------
// DepthOnly（深度预处理）
// ----------------------------------------------------------------------------
struct DepthOnlyAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
#if defined(_ALPHATEST_ON) || defined(_DISSOLVE_ON)
    float2 texcoord   : TEXCOORD0;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct DepthOnlyVaryings
{
#if defined(_ALPHATEST_ON) || defined(_DISSOLVE_ON)
    float2 uv : TEXCOORD0;
#endif
#if defined(_DISSOLVE_ON)
    float3 positionWS : TEXCOORD1;   // 溶解逐片元计算，低模也有正确梯度
#endif
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

DepthOnlyVaryings CustomLitDepthOnlyVertex(DepthOnlyAttributes input)
{
    DepthOnlyVaryings output = (DepthOnlyVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);

#if defined(_ALPHATEST_ON) || defined(_DISSOLVE_ON)
    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
#endif
#if defined(_DISSOLVE_ON)
    output.positionWS = positionWS;
#endif

    output.positionCS = TransformWorldToHClip(positionWS);
    return output;
}

half CustomLitDepthOnlyFragment(DepthOnlyVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);

#ifdef LOD_FADE_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif

#if defined(_ALPHATEST_ON)
    half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
    clip(alpha - _Cutoff);
#endif
#if defined(_DISSOLVE_ON)
    ApplyDissolve(ComputeDissolveFactor(input.positionWS), input.uv);
#endif
    return 0;
}

// ----------------------------------------------------------------------------
// DepthNormals（法线预处理，供 SSAO / 屏幕空间效果使用）
// ----------------------------------------------------------------------------
struct DepthNormalsAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 tangentOS  : TANGENT;
    float2 texcoord   : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct DepthNormalsVaryings
{
    float2 uv         : TEXCOORD0;
#if defined(_DISSOLVE_ON)
    float3 positionWS : TEXCOORD1;   // 溶解逐片元计算，低模也有正确梯度
#endif
    float3 normalWS   : TEXCOORD2;
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

DepthNormalsVaryings CustomLitDepthNormalsVertex(DepthNormalsAttributes input)
{
    DepthNormalsVaryings output = (DepthNormalsVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
#if defined(_DISSOLVE_ON)
    output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
#endif

    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);

    // 与 URP 一致：DepthNormals 输出插值顶点法线（不采样法线贴图）
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);
    output.normalWS = NormalizeNormalPerVertex(normalInput.normalWS);
    return output;
}

void CustomLitDepthNormalsFragment(
    DepthNormalsVaryings input
    , out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out float4 outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

#if defined(_ALPHATEST_ON)
    half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
    clip(alpha - _Cutoff);
#endif
#if defined(_DISSOLVE_ON)
    ApplyDissolve(ComputeDissolveFactor(input.positionWS), input.uv);
#endif

#ifdef LOD_FADE_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif

#if defined(_GBUFFER_NORMALS_OCT)
    float3 normalWS = normalize(input.normalWS);
    float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
    float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
    half3 packedNormalWS = PackFloat2To888(remappedOctNormalWS);
    outNormalWS = half4(packedNormalWS, 0.0);
#else
    float3 normalWS = NormalizeNormalPerPixel(input.normalWS);
    outNormalWS = half4(normalWS, 0.0);
#endif

#ifdef _WRITE_RENDERING_LAYERS
    uint renderingLayers = GetMeshRenderingLayer();
    outRenderingLayers = float4(EncodeMeshRenderingLayer(renderingLayers), 0, 0, 0);
#endif
}

#endif // CUSTOM_LIT_DEPTH_PASSES_INCLUDED
