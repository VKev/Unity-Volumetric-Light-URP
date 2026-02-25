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
float4 _StaticVoxelBoundsMin;
float4 _StaticVoxelBoundsSizeInv;
float _StaticVoxelIntensity;

TEXTURE3D(_StaticVoxelLightingTex);
SAMPLER(sampler_StaticVoxelLightingTex);
TEXTURE3D(_StaticVoxelDirectionTex);
SAMPLER(sampler_StaticVoxelDirectionTex);

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

// Gets the main light phase function.
float GetMainLightPhase(float3 rd)
{
#if _MAIN_LIGHT_CONTRIBUTION_DISABLED
    return 0.0;
#else
    return CornetteShanksPhaseFunction(_Anisotropies[_CustomAdditionalLightsCount], dot(rd, GetMainLight().direction));
#endif
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

// Gets static voxel lighting color at one raymarch step.
float3 GetStepStaticVoxelLightingColor(float3 currPosWS, float3 rd, float density)
{
#if _STATIC_VOXEL_LIGHTING_ENABLED
    const float isotropicPhase = 0.0795774715;

    float3 voxelUv = (currPosWS - _StaticVoxelBoundsMin.xyz) * _StaticVoxelBoundsSizeInv.xyz;
    UNITY_BRANCH
    if (any(voxelUv < 0.0) || any(voxelUv > 1.0))
        return float3(0.0, 0.0, 0.0);

    float4 voxelLighting = SAMPLE_TEXTURE3D(_StaticVoxelLightingTex, sampler_StaticVoxelLightingTex, voxelUv);
    float3 voxelColor = voxelLighting.rgb;
    UNITY_BRANCH
    if (dot(voxelColor, voxelColor) <= 0.0000001)
        return float3(0.0, 0.0, 0.0);

#if _STATIC_VOXEL_DIRECTIONAL_PHASE
    float3 voxelDirection = SAMPLE_TEXTURE3D(_StaticVoxelDirectionTex, sampler_StaticVoxelDirectionTex, voxelUv).rgb * 2.0 - 1.0;
    float dirLenSq = dot(voxelDirection, voxelDirection);
    float anisotropy = voxelLighting.a * 2.0 - 1.0;
    UNITY_BRANCH
    if (dirLenSq > 0.000001)
    {
        voxelDirection *= rsqrt(dirLenSq);
        float phase = CornetteShanksPhaseFunction(anisotropy, dot(rd, voxelDirection));
        voxelColor *= phase;
    }
    else
    {
        voxelColor *= isotropicPhase;
    }
#else
    voxelColor *= isotropicPhase;
#endif

    return voxelColor * (_StaticVoxelIntensity * density);
#else
    return float3(0.0, 0.0, 0.0);
#endif
}

// Gets the main light color at one raymarch step.
float3 GetStepMainLightColor(float3 currPosWS, float phaseMainLight, float density)
{
#if _MAIN_LIGHT_CONTRIBUTION_DISABLED
    return float3(0.0, 0.0, 0.0);
#endif
    Light mainLight = GetMainLight();
    float4 shadowCoord = TransformWorldToShadowCoord(currPosWS);
    mainLight.shadowAttenuation = VolumetricMainLightRealtimeShadow(shadowCoord);
#if _LIGHT_COOKIES
    mainLight.color *= SampleMainLightCookie(currPosWS);
#endif
    half3 tint = (half3)_Tint;
    half scattering = (half)_Scatterings[_CustomAdditionalLightsCount];
    return (float3)((half3)mainLight.color * tint * (mainLight.shadowAttenuation * (half)phaseMainLight * (half)density * scattering));
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
int GetFroxelIndex(float3 currPosWS)
{
    float3 currPosVS = mul(UNITY_MATRIX_V, float4(currPosWS, 1.0)).xyz;
    float eyeDepth = -currPosVS.z;
    float nearPlane = _FroxelNearFar.x;
    float farPlane = _FroxelNearFar.y;

    UNITY_BRANCH
    if (eyeDepth <= nearPlane || eyeDepth >= farPlane)
        return -1;

    float4 clipPos = mul(UNITY_MATRIX_P, float4(currPosVS, 1.0));
    float2 ndc = clipPos.xy / max(clipPos.w, 0.0001);
    float2 uv = ndc * 0.5 + 0.5;

    UNITY_BRANCH
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
        return -1;

    int froxelWidth = (int)_FroxelGridDimensions.x;
    int froxelHeight = (int)_FroxelGridDimensions.y;
    int froxelDepth = (int)_FroxelGridDimensions.z;

    int froxelX = min(froxelWidth - 1, (int)(uv.x * froxelWidth));
    int froxelY = min(froxelHeight - 1, (int)(uv.y * froxelHeight));
    float depth01 = saturate(log(max(eyeDepth / nearPlane, 1.0)) / max(log(farPlane / nearPlane), 0.0001));
    int froxelZ = min(froxelDepth - 1, (int)(depth01 * froxelDepth));

    return froxelX + (froxelY * froxelWidth) + (froxelZ * froxelWidth * froxelHeight);
}
#endif

float3 GetStepAdditionalLightsColor(float3 currPosWS, float3 rd, float density)
{
#if _ADDITIONAL_LIGHTS_CONTRIBUTION_DISABLED
    return float3(0.0, 0.0, 0.0);
#endif
    half3 additionalLightsColor = half3(0.0, 0.0, 0.0);

#if defined(_FROXEL_CLUSTERED_ADDITIONAL_LIGHTS) && (SHADER_TARGET >= 45)
    int froxelIndex = GetFroxelIndex(currPosWS);
    UNITY_BRANCH
    if (froxelIndex >= 0)
    {
        int2 froxelMeta = _FroxelMetaBuffer[froxelIndex];

        UNITY_LOOP
        for (int i = 0; i < froxelMeta.y; ++i)
        {
            int compactLightIndex = _FroxelLightIndicesBuffer[froxelMeta.x + i];
            UNITY_BRANCH
            if (compactLightIndex >= 0)
                additionalLightsColor += (half3)EvaluateCompactAdditionalLight(compactLightIndex, currPosWS, rd, density);
        }
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

    half phaseMainLight = GetMainLightPhase(rdPhase);
    float minusStepLengthTimesAbsortion = -stepLength * _Absortion;
                
    float3 volumetricFogColor = float3(0.0, 0.0, 0.0);
    half transmittance = 1.0;

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

        half3 apvColor = (half3)GetStepAdaptiveProbeVolumeEvaluation(uv, currPosWS, density);
        half3 staticVoxelColor = (half3)GetStepStaticVoxelLightingColor(currPosWS, rd, density);
        half3 mainLightColor = (half3)GetStepMainLightColor(currPosWS, phaseMainLight, density);
        half3 additionalLightsColor = (half3)GetStepAdditionalLightsColor(currPosWS, rd, density);
        
        // TODO: Additional contributions? Reflection probes, etc...
        half3 stepColor = apvColor + staticVoxelColor + mainLightColor + additionalLightsColor;
        volumetricFogColor += ((float3)stepColor * (transmittance * stepLength));

        UNITY_BRANCH
        if (_TransmittanceThreshold > 0.0 && transmittance <= _TransmittanceThreshold)
            break;
    }

    return float4(volumetricFogColor, transmittance);
}

#endif
