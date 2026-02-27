#ifndef DEPTH_AWARE_UPSAMPLE_INCLUDED
#define DEPTH_AWARE_UPSAMPLE_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "./DeclareDownsampledDepthTexture.hlsl"
#include "./ProjectionUtils.hlsl"

int _UpsampleBilateralRadius;

void AccumulateBilateralUpsampleTap(float2 sampleUv, float spatialWeight, float fullResLinearEyeDepth, float relativeDepthThreshold, TEXTURE2D_X(textureToUpsample), inout float4 accumulatedFog, inout float accumulatedWeight)
{
    float sampleDepth = SampleDownsampledSceneDepth(sampleUv);
    float sampleLinearEyeDepth = LinearEyeDepthConsiderProjection(sampleDepth);
    float depthDelta = abs(fullResLinearEyeDepth - sampleLinearEyeDepth);
    float depthWeight = saturate(1.0 - (depthDelta / relativeDepthThreshold));
    depthWeight *= depthWeight;

    float tapWeight = spatialWeight * depthWeight;
    float4 tapFog = SAMPLE_TEXTURE2D_X(textureToUpsample, sampler_PointClamp, sampleUv);
    accumulatedFog += tapFog * tapWeight;
    accumulatedWeight += tapWeight;
}

// Upsamples the given texture using both the downsampled and full resolution depth information.
float4 DepthAwareUpsample(float2 uv, TEXTURE2D_X(textureToUpsample))
{
    float2 downsampledTexelSize = _DownsampledCameraDepthTexture_TexelSize.xy;
    float2 downsampledTopLeftCornerUv = uv - (downsampledTexelSize * 0.5);
    float2 uvs[4] =
    {
        downsampledTopLeftCornerUv + float2(0.0, downsampledTexelSize.y),
        downsampledTopLeftCornerUv + downsampledTexelSize.xy,
        downsampledTopLeftCornerUv + float2(downsampledTexelSize.x, 0.0),
        downsampledTopLeftCornerUv
    };

    float4 downsampledDepths;
    
#if SHADER_TARGET >= 45
    downsampledDepths = GATHER_RED_TEXTURE2D_X(_DownsampledCameraDepthTexture, sampler_PointClamp, uv);
#else
    downsampledDepths.x = SampleDownsampledSceneDepth(uvs[0]);
    downsampledDepths.y = SampleDownsampledSceneDepth(uvs[1]);
    downsampledDepths.z = SampleDownsampledSceneDepth(uvs[2]);
    downsampledDepths.w = SampleDownsampledSceneDepth(uvs[3]);
#endif

    float fullResDepth = SampleSceneDepth(uv);
    float fullResLinearEyeDepth = LinearEyeDepthConsiderProjection(fullResDepth);
    float relativeDepthThreshold = max(fullResLinearEyeDepth * 0.1, 0.001);

    UNITY_BRANCH
    if (_UpsampleBilateralRadius > 0)
    {
        float4 accumulatedFog = 0.0;
        float accumulatedWeight = 0.0;

        UNITY_UNROLL
        for (int i = 0; i < 4; ++i)
            AccumulateBilateralUpsampleTap(uvs[i], 1.0, fullResLinearEyeDepth, relativeDepthThreshold, textureToUpsample, accumulatedFog, accumulatedWeight);

        UNITY_BRANCH
        if (_UpsampleBilateralRadius > 1)
        {
            AccumulateBilateralUpsampleTap(uv + float2(-downsampledTexelSize.x, 0.0), 0.6, fullResLinearEyeDepth, relativeDepthThreshold, textureToUpsample, accumulatedFog, accumulatedWeight);
            AccumulateBilateralUpsampleTap(uv + float2(downsampledTexelSize.x, 0.0), 0.6, fullResLinearEyeDepth, relativeDepthThreshold, textureToUpsample, accumulatedFog, accumulatedWeight);
            AccumulateBilateralUpsampleTap(uv + float2(0.0, -downsampledTexelSize.y), 0.6, fullResLinearEyeDepth, relativeDepthThreshold, textureToUpsample, accumulatedFog, accumulatedWeight);
            AccumulateBilateralUpsampleTap(uv + float2(0.0, downsampledTexelSize.y), 0.6, fullResLinearEyeDepth, relativeDepthThreshold, textureToUpsample, accumulatedFog, accumulatedWeight);
        }

        UNITY_BRANCH
        if (_UpsampleBilateralRadius > 2)
        {
            AccumulateBilateralUpsampleTap(uv + float2(-downsampledTexelSize.x, -downsampledTexelSize.y), 0.45, fullResLinearEyeDepth, relativeDepthThreshold, textureToUpsample, accumulatedFog, accumulatedWeight);
            AccumulateBilateralUpsampleTap(uv + float2(-downsampledTexelSize.x, downsampledTexelSize.y), 0.45, fullResLinearEyeDepth, relativeDepthThreshold, textureToUpsample, accumulatedFog, accumulatedWeight);
            AccumulateBilateralUpsampleTap(uv + float2(downsampledTexelSize.x, -downsampledTexelSize.y), 0.45, fullResLinearEyeDepth, relativeDepthThreshold, textureToUpsample, accumulatedFog, accumulatedWeight);
            AccumulateBilateralUpsampleTap(uv + float2(downsampledTexelSize.x, downsampledTexelSize.y), 0.45, fullResLinearEyeDepth, relativeDepthThreshold, textureToUpsample, accumulatedFog, accumulatedWeight);
        }

        UNITY_BRANCH
        if (_UpsampleBilateralRadius > 3)
        {
            float2 doubledTexelSize = downsampledTexelSize * 2.0;
            AccumulateBilateralUpsampleTap(uv + float2(-doubledTexelSize.x, 0.0), 0.25, fullResLinearEyeDepth, relativeDepthThreshold, textureToUpsample, accumulatedFog, accumulatedWeight);
            AccumulateBilateralUpsampleTap(uv + float2(doubledTexelSize.x, 0.0), 0.25, fullResLinearEyeDepth, relativeDepthThreshold, textureToUpsample, accumulatedFog, accumulatedWeight);
            AccumulateBilateralUpsampleTap(uv + float2(0.0, -doubledTexelSize.y), 0.25, fullResLinearEyeDepth, relativeDepthThreshold, textureToUpsample, accumulatedFog, accumulatedWeight);
            AccumulateBilateralUpsampleTap(uv + float2(0.0, doubledTexelSize.y), 0.25, fullResLinearEyeDepth, relativeDepthThreshold, textureToUpsample, accumulatedFog, accumulatedWeight);
        }

        UNITY_BRANCH
        if (accumulatedWeight > 0.0001)
            return accumulatedFog * rcp(accumulatedWeight);
    }
    
    float linearEyeDepth = LinearEyeDepthConsiderProjection(downsampledDepths[0]);
    float minLinearEyeDepthDist = abs(fullResLinearEyeDepth - linearEyeDepth);
    
    float2 nearestUv = uvs[0];
    int numValidDepths = minLinearEyeDepthDist < relativeDepthThreshold;
    
    UNITY_UNROLL
    for (int i = 1; i < 4; ++i)
    {
        linearEyeDepth = LinearEyeDepthConsiderProjection(downsampledDepths[i]);
        float linearEyeDepthDist = abs(fullResLinearEyeDepth - linearEyeDepth);

        bool updateNearest = linearEyeDepthDist < minLinearEyeDepthDist;
        minLinearEyeDepthDist = updateNearest ? linearEyeDepthDist : minLinearEyeDepthDist;
        nearestUv = updateNearest ? uvs[i] : nearestUv;
        
        numValidDepths += (linearEyeDepthDist < relativeDepthThreshold);
    }

    UNITY_BRANCH
    if (numValidDepths == 4)
        return SAMPLE_TEXTURE2D_X(textureToUpsample, sampler_LinearClamp, uv);
    else
        return SAMPLE_TEXTURE2D_X(textureToUpsample, sampler_PointClamp, nearestUv);
}

#endif
