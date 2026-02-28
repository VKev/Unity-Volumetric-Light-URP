#ifndef VOLUMETRIC_FOG_INCLUDED
#define VOLUMETRIC_FOG_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Macros.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/VolumeRendering.hlsl"
#if UNITY_VERSION >= 202310 && _APV_CONTRIBUTION_ENABLED
    #if defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
        #include "Packages/com.unity.render-pipelines.core/Runtime/Lighting/ProbeVolume/ProbeVolume.hlsl"
    #endif
#endif
#include "./DeclareDownsampledDepthTexture.hlsl"
#include "./VolumetricShadows.hlsl"
#include "./ProjectionUtils.hlsl"

int _FrameCount;
uint _CustomAdditionalLightsCount;
float _Distance;
float _BaseHeight;
float _MaximumHeight;
float _GroundHeight;
float _Density;
float _Absortion;
float _APVContributionWeight;
float _TransmittanceThreshold;
float3 _Tint;
int _MaxSteps;

float _Anisotropies[MAX_VISIBLE_LIGHTS + 1];
float _Scatterings[MAX_VISIBLE_LIGHTS + 1];
float _RadiiSq[MAX_VISIBLE_LIGHTS];
float _AdditionalLightIndices[MAX_VISIBLE_LIGHTS];
float4 _FroxelGridDimensions;
float4 _FroxelNearFar;

#if defined(_FROXEL_CLUSTERED_ADDITIONAL_LIGHTS) && (SHADER_TARGET >= 45)
StructuredBuffer<int2> _FroxelMetaBuffer;
StructuredBuffer<int> _FroxelLightIndicesBuffer;
#endif

struct MainLightContext
{
    half3 color;
    half phase;
};

struct FroxelRaymarchContext
{
    int froxelXYBase;
    int froxelPlaneStride;
    float nearPlane;
    float farPlane;
    float invLogDepthRange;
    float viewOriginZ;
    float viewDirZ;
};

// Computes the ray origin, direction, and returns the reconstructed world position for orthographic projection.
float3 ComputeOrthographicParams(float2 uv, float depth, out float3 ro, out float3 rd)
{
    float4x4 viewMatrix = UNITY_MATRIX_V;
    float2 ndc = uv * 2.0 - 1.0;
    
    rd = normalize(-viewMatrix[2].xyz);
    float3 rightOffset = normalize(viewMatrix[0].xyz) * (ndc.x * unity_OrthoParams.x);
    float3 upOffset = normalize(viewMatrix[1].xyz) * (ndc.y * unity_OrthoParams.y);
    float3 fwdOffset = rd * depth;
    
    float3 posWs = GetCameraPositionWS() + fwdOffset + rightOffset + upOffset;
    ro = posWs - fwdOffset;

    return posWs;
}

// Calculates the initial raymarching parameters.
void CalculateRaymarchingParams(float2 uv, out float3 ro, out float3 rd, out float iniOffsetToNearPlane, out float offsetLength, out float3 rdPhase)
{
    float depth = SampleDownsampledSceneDepth(uv);
    float3 posWS;
    
    UNITY_BRANCH
    if (unity_OrthoParams.w <= 0)
    {
        ro = GetCameraPositionWS();
#if !UNITY_REVERSED_Z
        depth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, depth);
#endif
        posWS = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
        float3 offset = posWS - ro;
        offsetLength = length(offset);
        rd = offset / offsetLength;
        rdPhase = rd;
        
        // In perspective, ray direction should vary in length depending on which fragment we are at.
        float3 camFwd = normalize(-UNITY_MATRIX_V[2].xyz);
        float cos = dot(camFwd, rd);
        float fragElongation = 1.0 / cos;
        iniOffsetToNearPlane = fragElongation * _ProjectionParams.y;
    }
    else
    {
        depth = LinearEyeDepthOrthographic(depth);
        posWS = ComputeOrthographicParams(uv, depth, ro, rd);
        offsetLength = depth;
        
        // Fake the ray direction that will be used to calculate the phase, so we can still use anisotropy in orthographic mode.
        rdPhase = normalize(posWS - GetCameraPositionWS());
        iniOffsetToNearPlane = _ProjectionParams.y;
    }
}

// Caches main-light values that stay constant through the raymarch.
MainLightContext CreateMainLightContext(float3 rdPhase)
{
    MainLightContext context;
    context.color = half3(0.0, 0.0, 0.0);
    context.phase = half(0.0);

#if !_MAIN_LIGHT_CONTRIBUTION_DISABLED
    Light mainLight = GetMainLight();
    context.color = (half3)mainLight.color * (half3)_Tint * (half)_Scatterings[_CustomAdditionalLightsCount];
    context.phase = (half)CornetteShanksPhaseFunction(_Anisotropies[_CustomAdditionalLightsCount], dot(rdPhase, mainLight.direction));
#endif

    return context;
}

// Gets the fog density at the given world height.
float GetFogDensity(float posWSy)
{
    float t = saturate((posWSy - _BaseHeight) / (_MaximumHeight - _BaseHeight));
    t = 1.0 - t;
    t = lerp(t, 0.0, posWSy < _GroundHeight);

    return _Density * t;
}

// Gets the GI evaluation from the adaptive probe volume at one raymarch step.
float3 GetStepAdaptiveProbeVolumeEvaluation(float2 uv, float3 posWS, float density)
{
    half3 apvDiffuseGI = half3(0.0, 0.0, 0.0);
    
#if UNITY_VERSION >= 202310 && _APV_CONTRIBUTION_ENABLED
    #if defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
        EvaluateAdaptiveProbeVolume(posWS, uv * _ScreenSize.xy, apvDiffuseGI);
        apvDiffuseGI = apvDiffuseGI * (half)_APVContributionWeight * (half)density;
    #endif
#endif
 
    return (float3)apvDiffuseGI;
}

// Gets the main light color at one raymarch step.
float3 GetStepMainLightColor(float3 currPosWS, MainLightContext mainLightContext, float density)
{
#if _MAIN_LIGHT_CONTRIBUTION_DISABLED
    return float3(0.0, 0.0, 0.0);
#endif
    float4 shadowCoord = TransformWorldToShadowCoord(currPosWS);
    half shadowAttenuation = VolumetricMainLightRealtimeShadow(shadowCoord);
#if _LIGHT_COOKIES
    half3 mainLightColor = mainLightContext.color * (half3)SampleMainLightCookie(currPosWS);
#else
    half3 mainLightColor = mainLightContext.color;
#endif
    return (float3)(mainLightColor * (shadowAttenuation * mainLightContext.phase * (half)density));
}

// Gets the accumulated color from additional lights at one raymarch step.
float3 EvaluateCompactAdditionalLight(int compactLightIndex, float3 currPosWS, float3 rd, float density)
{
    float scattering = _Scatterings[compactLightIndex];
    UNITY_BRANCH
    if (scattering <= 0.0)
        return float3(0.0, 0.0, 0.0);

    int additionalLightIndex = (int)_AdditionalLightIndices[compactLightIndex];

    Light additionalLight = GetAdditionalPerObjectLight(additionalLightIndex, currPosWS);
    additionalLight.shadowAttenuation = VolumetricAdditionalLightRealtimeShadow(additionalLightIndex, currPosWS, additionalLight.direction);
#if _LIGHT_COOKIES
    additionalLight.color *= SampleAdditionalLightCookie(additionalLightIndex, currPosWS);
#endif
    // See universal\ShaderLibrary\RealtimeLights.hlsl - GetAdditionalPerObjectLight.
#if USE_STRUCTURED_BUFFER_FOR_LIGHT_DATA
    float4 additionalLightPos = _AdditionalLightsBuffer[additionalLightIndex].position;
#else
    float4 additionalLightPos = _AdditionalLightsPosition[additionalLightIndex];
#endif
    // This is useful for both spotlights and pointlights. For the latter it is specially true when the point light is inside some geometry and casts shadows.
    // Gradually reduce additional lights scattering to zero at their origin to try to avoid flicker-aliasing.
    float3 distToPos = additionalLightPos.xyz - currPosWS;
    float distToPosMagnitudeSq = dot(distToPos, distToPos);
    float newScattering = smoothstep(0.0, _RadiiSq[compactLightIndex], distToPosMagnitudeSq);
    newScattering *= newScattering;
    newScattering *= scattering;

    // If directional lights are also considered as additional lights when more than 1 is used, ignore the previous code when it is a directional light.
    // They store direction in additionalLightPos.xyz and have .w set to 0, while point and spotlights have it set to 1.
    // newScattering = lerp(1.0, newScattering, additionalLightPos.w);

    half phase = CornetteShanksPhaseFunction(_Anisotropies[compactLightIndex], dot(rd, additionalLight.direction));
    return (float3)((half3)additionalLight.color * (additionalLight.shadowAttenuation * additionalLight.distanceAttenuation * phase * density * newScattering));
}

#if defined(_FROXEL_CLUSTERED_ADDITIONAL_LIGHTS) && (SHADER_TARGET >= 45)
FroxelRaymarchContext CreateFroxelRaymarchContext(float2 uv, float3 roNearPlaneWS, float3 rd)
{
    FroxelRaymarchContext context;
    context.froxelXYBase = -1;
    context.froxelPlaneStride = 0;
    context.nearPlane = 0.0;
    context.farPlane = 0.0;
    context.invLogDepthRange = 0.0;
    context.viewOriginZ = 0.0;
    context.viewDirZ = 0.0;

    int froxelWidth = (int)_FroxelGridDimensions.x;
    int froxelHeight = (int)_FroxelGridDimensions.y;
    float clampedU = saturate(uv.x);
    float clampedV = saturate(uv.y);

    context.froxelXYBase = min(froxelWidth - 1, (int)(clampedU * froxelWidth));
    context.froxelXYBase += min(froxelHeight - 1, (int)(clampedV * froxelHeight)) * froxelWidth;
    context.froxelPlaneStride = froxelWidth * froxelHeight;
    context.nearPlane = max(_FroxelNearFar.x, 0.0001);
    context.farPlane = max(_FroxelNearFar.y, context.nearPlane + 0.0001);
    context.invLogDepthRange = rcp(max(log(context.farPlane / context.nearPlane), 0.0001));

    float3 roNearPlaneVS = mul(UNITY_MATRIX_V, float4(roNearPlaneWS, 1.0)).xyz;
    float3 rdVS = mul((float3x3)UNITY_MATRIX_V, rd);
    context.viewOriginZ = roNearPlaneVS.z;
    context.viewDirZ = rdVS.z;

    return context;
}

int GetFroxelIndex(FroxelRaymarchContext context, float rayDistance)
{
    UNITY_BRANCH
    if (context.froxelXYBase < 0)
        return -1;

    float eyeDepth = -(context.viewOriginZ + context.viewDirZ * rayDistance);
    UNITY_BRANCH
    if (eyeDepth <= context.nearPlane || eyeDepth >= context.farPlane)
        return -1;

    int froxelDepth = (int)_FroxelGridDimensions.z;
    float depth01 = saturate(log(max(eyeDepth / context.nearPlane, 1.0)) * context.invLogDepthRange);
    int froxelZ = min(froxelDepth - 1, (int)(depth01 * froxelDepth));
    return context.froxelXYBase + (froxelZ * context.froxelPlaneStride);
}
#endif

float3 GetStepAdditionalLightsColor(float3 currPosWS, float3 rd, float density, int2 froxelMeta)
{
#if _ADDITIONAL_LIGHTS_CONTRIBUTION_DISABLED
    return float3(0.0, 0.0, 0.0);
#endif
    half3 additionalLightsColor = half3(0.0, 0.0, 0.0);

#if defined(_FROXEL_CLUSTERED_ADDITIONAL_LIGHTS) && (SHADER_TARGET >= 45)
    UNITY_LOOP
    for (int i = 0; i < froxelMeta.y; ++i)
    {
        int compactLightIndex = _FroxelLightIndicesBuffer[froxelMeta.x + i];
        additionalLightsColor += (half3)EvaluateCompactAdditionalLight(compactLightIndex, currPosWS, rd, density);
    }
#else
    UNITY_LOOP
    for (uint compactLightIndex = 0; compactLightIndex < _CustomAdditionalLightsCount; ++compactLightIndex)
    {
        additionalLightsColor += (half3)EvaluateCompactAdditionalLight(compactLightIndex, currPosWS, rd, density);
    }
#endif

    return (float3)additionalLightsColor;
}

// Calculates the volumetric fog. Returns the color in the RGB channels and transmittance in alpha.
float4 VolumetricFog(float2 uv, float2 positionCS)
{
    float3 ro;
    float3 rd;
    float iniOffsetToNearPlane;
    float offsetLength;
    float3 rdPhase;

    CalculateRaymarchingParams(uv, ro, rd, iniOffsetToNearPlane, offsetLength, rdPhase);

    offsetLength -= iniOffsetToNearPlane;
    float maxRaymarchDistance = min(offsetLength, _Distance - iniOffsetToNearPlane);
    UNITY_BRANCH
    if (maxRaymarchDistance <= 0.0)
        return float4(0.0, 0.0, 0.0, 1.0);

    float3 roNearPlane = ro + rd * iniOffsetToNearPlane;
    float stepLength = maxRaymarchDistance / (float)_MaxSteps;
    float jitter = stepLength * InterleavedGradientNoise(positionCS, _FrameCount);

    MainLightContext mainLightContext = CreateMainLightContext(rdPhase);
    float minusStepLengthTimesAbsortion = -stepLength * _Absortion;
    FroxelRaymarchContext froxelRaymarchContext;
    froxelRaymarchContext.froxelXYBase = -1;
    froxelRaymarchContext.froxelPlaneStride = 0;
    froxelRaymarchContext.nearPlane = 0.0;
    froxelRaymarchContext.farPlane = 0.0;
    froxelRaymarchContext.invLogDepthRange = 0.0;
    froxelRaymarchContext.viewOriginZ = 0.0;
    froxelRaymarchContext.viewDirZ = 0.0;
#if defined(_FROXEL_CLUSTERED_ADDITIONAL_LIGHTS) && (SHADER_TARGET >= 45)
    froxelRaymarchContext = CreateFroxelRaymarchContext(uv, roNearPlane, rd);
#endif
                
    float3 volumetricFogColor = float3(0.0, 0.0, 0.0);
    half transmittance = 1.0;
    int currentFroxelIndex = -1;
    int2 currentFroxelMeta = int2(0, 0);

    UNITY_LOOP
    for (int i = 0; i < _MaxSteps; ++i)
    {
        float dist = jitter + i * stepLength;
        
        UNITY_BRANCH
        if (dist >= maxRaymarchDistance)
            break;

        // We are making the space between the camera position and the near plane "non existant", as if fog did not exist there.
        // However, it removes a lot of noise when in closed environments with an attenuation that makes the scene darker
        // and certain combinations of field of view, raymarching resolution and camera near plane.
        // In those edge cases, it looks so much better, specially when near plane is higher than the minimum (0.01) allowed.
        float3 currPosWS = roNearPlane + rd * dist;
        float density = GetFogDensity(currPosWS.y);
                    
        UNITY_BRANCH
        if (density <= 0.0)
            continue;

        half stepAttenuation = exp(minusStepLengthTimesAbsortion * density);
        transmittance *= stepAttenuation;

#if defined(_FROXEL_CLUSTERED_ADDITIONAL_LIGHTS) && (SHADER_TARGET >= 45)
        int froxelIndex = GetFroxelIndex(froxelRaymarchContext, dist);
        UNITY_BRANCH
        if (froxelIndex != currentFroxelIndex)
        {
            currentFroxelIndex = froxelIndex;
            currentFroxelMeta = int2(0, 0);

            UNITY_BRANCH
            if (froxelIndex >= 0)
                currentFroxelMeta = _FroxelMetaBuffer[froxelIndex];
        }
#endif

        half3 apvColor = (half3)GetStepAdaptiveProbeVolumeEvaluation(uv, currPosWS, density);
        half3 mainLightColor = (half3)GetStepMainLightColor(currPosWS, mainLightContext, density);
        half3 additionalLightsColor = (half3)GetStepAdditionalLightsColor(currPosWS, rd, density, currentFroxelMeta);
        
        // TODO: Additional contributions? Reflection probes, etc...
        half3 stepColor = apvColor + mainLightColor + additionalLightsColor;
        volumetricFogColor += ((float3)stepColor * (transmittance * stepLength));

        UNITY_BRANCH
        if (_TransmittanceThreshold > 0.0 && transmittance <= _TransmittanceThreshold)
            break;
    }

    return float4(volumetricFogColor, transmittance);
}

#endif
