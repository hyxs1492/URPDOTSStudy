#ifndef CUSTOM_LIT_OUTLINE_PASS_INCLUDED
#define CUSTOM_LIT_OUTLINE_PASS_INCLUDED

// ============================================================================
//  CustomLitOutlinePass.hlsl —— 世界空间外扩的反转外壳描边（Inverted Hull）
//
//  做法：沿真实法线把顶点在世界空间向外推 widthPixels 对应的世界距离，
//        再乘"到相机的距离"补偿透视缩放 —— 于是屏幕上宽度恒为 widthPixels。
//        Cull Front 只画外壳背面，深度测试与主体一致。
//
//  为什么不用"屏幕空间平面外扩"（把顶点沿法线在屏幕上的投影方向推开 N 像素）：
//    那个做法只能加粗"与自身方向垂直"的那段轮廓；水平方向的轮廓不会被加粗。
//    实测球体只有左右两条竖描边、上下完全没有，立方体同样只剩两条竖边 —— 明显是坏的。
//    沿真实法线做世界空间外扩则任何朝向的轮廓都被均匀加粗。
//
//  性能：只多一遍几何，Cull Front + 只在轮廓处产生像素；
//        _OUTLINE_ON 关闭时全部顶点折叠到裁剪范围外，零像素开销。
//        （对比全屏后处理描边：无需额外 prepass / RenderTarget / 全屏 pass，
//          也无需 ScriptableRendererFeature，因此能继续走 SRP Batcher 与 DOTS 合批。）
// ============================================================================

struct OutlineAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    // 平滑法线：烘焙工具把"按位置平均后的法线"写进 UV3（物体空间）。
    // 未烘焙时 UV3 为 0，normalize 后是非法值 —— 所以用 _OutlineSmoothNormals 开关控制，
    // 关闭时完全不看它。NORMAL 与 TEXCOORD3 可以共存（已实测编译通过）。
    float3 smoothNormalOS : TEXCOORD3;
#if defined(_DISSOLVE_ON)
    float2 texcoord   : TEXCOORD0;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct OutlineVaryings
{
#if defined(_DISSOLVE_ON)
    float2 uv            : TEXCOORD0;
    float3 positionWS    : TEXCOORD1;   // 溶解逐片元计算，与主 pass 边界严格一致
#endif
    half   fogFactor     : TEXCOORD2;
    float4 positionCS    : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

// ----------------------------------------------------------------------------
// 描边外扩
//   positionWS : 世界空间顶点位置
//   normalWS   : 世界空间法线
//   widthPixels: 描边宽度（像素）
//
// 为什么用「世界空间外扩 + 距离补偿」而不是「屏幕空间平面外扩」：
//   屏幕空间外扩（把顶点沿法线在屏幕上的投影方向推开 widthPixels）看似恒像素，
//   但它只能加粗"与自身方向垂直"的那段轮廓 —— 水平方向的轮廓完全不会被加粗。
//   实测：球体只有左右两条竖描边、上下没有；立方体同样只有两条竖边。所以那个方案是错的。
//   世界空间外扩沿真实法线推开，任何朝向的轮廓都被均匀加粗（这才是描边该有的样子）。
//
//   "恒像素"来自距离补偿：透视下屏幕尺寸 ∝ 1/距离，故 世界偏移 ∝ 距离 时像素恒定。
//   正交相机没有透视缩放，改用 orthographicSize 换算。
// ----------------------------------------------------------------------------
float3 ExpandOutlineWS(float3 positionWS, float3 normalWS, float widthPixels)
{
    // 归一化：mesh 法线长度不为 1 时会导致描边粗细不均
    float3 n = normalize(normalWS);

    float worldPerPixel;
    if (unity_OrthoParams.w > 0.5)
    {
        // 正交：屏幕高度对应 2*orthographicSize 个世界单位
        worldPerPixel = (2.0 * unity_OrthoParams.y) / max(_ScreenParams.y, 1.0);
    }
    else
    {
        // 透视：tan(fovY/2) = 1 / P[1][1]；再乘到相机的距离
        float tanHalfFovY = rcp(max(abs(UNITY_MATRIX_P._m11), 1e-5));
        worldPerPixel = (2.0 * tanHalfFovY / max(_ScreenParams.y, 1.0))
                      * distance(GetCurrentViewPosition(), positionWS);
    }

    return positionWS + n * (widthPixels * worldPerPixel);
}

OutlineVaryings CustomLitOutlinePassVertex(OutlineAttributes input)
{
    OutlineVaryings output = (OutlineVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    // 注意：这段变换与描边参数的读取必须在描边关闭时也执行，不能包在 #if _OUTLINE_ON 里。
    // 否则该变体完全不使用 per-instance 数据，编译器会把 UnityInstancingDOTS_InstanceVisibility
    // （以及 UnityPerMaterial）判为未使用而剥离；而 URP 每帧都会用 SRPDefaultUnlit 渲染本 pass，
    // 引擎提交该 batch 时便会报
    // "Trying to render a BatchRendererGroup (or Entities Graphics) batch with wrong cbuffer setup.
    //  Missing DOTS_INSTANCING_ON variant?"。
    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);

    // 外扩方向：优先用烘焙进 UV3 的平滑法线（修硬边缺口），否则用 mesh 法线。
    float3 normalOS = input.normalOS;
#if defined(_OUTLINE_SMOOTH_NORMALS)
    normalOS = input.smoothNormalOS;
#endif
    float3 normalWS = TransformObjectToWorldNormal(normalOS);

    float3 outlineWS  = ExpandOutlineWS(positionWS, normalWS, _OutlineWidthPixels);
    float4 positionCS = TransformWorldToHClip(outlineWS);

#if defined(_DISSOLVE_ON)
    // 溶解基于外扩前的原始位置；逐片元计算，与主 pass 的裁剪边界严格对齐
    output.positionWS = positionWS;
    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
#endif

#if defined(_OUTLINE_ON)
    output.positionCS = positionCS;
    output.fogFactor  = ComputeFogFactor(positionCS.z);
#else
    // 关键词关闭：所有顶点折叠到裁剪范围外（退化三角形，无光栅化开销）。
    // 乘以 positionCS.w 是为了让上面的变换结果必须被真正求值：否则编译器会把整条
    // per-instance / 材质常量读取视为死代码剥离，DOTS 常量缓冲随之消失。
    // 齐次除法后仍是 (2,2,2)，照样落在 NDC 之外。
    output.positionCS = float4(2.0, 2.0, 2.0, 1.0) * positionCS.w;
#endif
    return output;
}

half4 CustomLitOutlinePassFragment(OutlineVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);

#if defined(_OUTLINE_ON)
#if defined(_DISSOLVE_ON)
    ApplyDissolve(ComputeDissolveFactor(input.positionWS), input.uv);
#endif
    half3 color = _OutlineColor.rgb;
    color = MixFog(color, input.fogFactor);
    return half4(color, _OutlineColor.a);
#else
    return half4(0, 0, 0, 0);
#endif
}

#endif // CUSTOM_LIT_OUTLINE_PASS_INCLUDED
