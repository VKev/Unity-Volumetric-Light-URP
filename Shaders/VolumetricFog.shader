Shader "Hidden/VolumetricFog"
{
    SubShader
    {
        Tags
        { 
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "VolumetricFogRender"

            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off

            HLSLPROGRAM

            #include "./VolumetricFog.hlsl"

            #pragma target 4.5

            #pragma multi_compile _ _FORWARD_PLUS

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
#if UNITY_VERSION >= 202310
            #pragma multi_compile_fragment _ PROBE_VOLUMES_L1 PROBE_VOLUMES_L2
#endif

            #pragma multi_compile_local_fragment _ _MAIN_LIGHT_CONTRIBUTION_DISABLED
            #pragma multi_compile_local_fragment _ _ADDITIONAL_LIGHTS_CONTRIBUTION_DISABLED
            #pragma multi_compile_local_fragment _ _APV_CONTRIBUTION_ENABLED
            #pragma multi_compile_local_fragment _ _FROXEL_CLUSTERED_ADDITIONAL_LIGHTS
            #pragma multi_compile_local_fragment _ _ADAPTIVE_STEP_COUNT

            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                return VolumetricFog(input.texcoord, input.positionCS.xy);
            }

            ENDHLSL
        }

        Pass
        {
            Name "VolumetricFogHorizontalBlur"
            
            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "./DepthAwareGaussianBlur.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag

#if UNITY_VERSION < 202320
            float4 _BlitTexture_TexelSize;
#endif

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                return DepthAwareGaussianBlur(input.texcoord, float2(1.0, 0.0), _BlitTexture, sampler_PointClamp, _BlitTexture_TexelSize.xy);
            }

            ENDHLSL
        }

        Pass
        {
            Name "VolumetricFogTemporalReprojection"

            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "./DeclareDownsampledDepthTexture.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag

#if UNITY_VERSION < 202320
            float4 _BlitTexture_TexelSize;
#endif

            TEXTURE2D_X(_PrevVolumetricFogTexture);
            SAMPLER(sampler_PrevVolumetricFogTexture);
            float4x4 _CurrInvViewProj;
            float4x4 _PrevViewProj;
            float _TemporalBlendFactor;
            int _TemporalHistoryValid;

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float4 currentFog = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, input.texcoord);
                UNITY_BRANCH
                if (_TemporalHistoryValid <= 0)
                    return currentFog;

                float depth = SampleDownsampledSceneDepth(input.texcoord);
#if !UNITY_REVERSED_Z
                depth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, depth);
#endif

                float3 positionWS = ComputeWorldSpacePosition(input.texcoord, depth, _CurrInvViewProj);
                float4 prevClip = mul(_PrevViewProj, float4(positionWS, 1.0));
                UNITY_BRANCH
                if (prevClip.w <= 0.0001)
                    return currentFog;

                float2 prevUv = prevClip.xy / prevClip.w;
                prevUv = prevUv * 0.5 + 0.5;
                UNITY_BRANCH
                if (any(prevUv < 0.0) || any(prevUv > 1.0))
                    return currentFog;

                float4 historyFog = SAMPLE_TEXTURE2D_X(_PrevVolumetricFogTexture, sampler_PrevVolumetricFogTexture, prevUv);

                // Clamp history to current-neighborhood bounds to reduce reprojection ghosting.
                float2 texel = _BlitTexture_TexelSize.xy;
                float4 n0 = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, input.texcoord + float2(texel.x, 0.0));
                float4 n1 = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, input.texcoord + float2(-texel.x, 0.0));
                float4 n2 = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, input.texcoord + float2(0.0, texel.y));
                float4 n3 = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, input.texcoord + float2(0.0, -texel.y));

                float4 neighborhoodMin = min(min(min(currentFog, n0), n1), min(n2, n3));
                float4 neighborhoodMax = max(max(max(currentFog, n0), n1), max(n2, n3));
                historyFog = clamp(historyFog, neighborhoodMin, neighborhoodMax);

                float blend = saturate(_TemporalBlendFactor);
                return lerp(currentFog, historyFog, blend);
            }

            ENDHLSL
        }

        Pass
        {
            Name "VolumetricFogVerticalBlur"
            
            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "./DepthAwareGaussianBlur.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag

#if UNITY_VERSION < 202320
            float4 _BlitTexture_TexelSize;
#endif

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                return DepthAwareGaussianBlur(input.texcoord, float2(0.0, 1.0), _BlitTexture, sampler_PointClamp, _BlitTexture_TexelSize.xy);
            }

            ENDHLSL
        }

        Pass
        {
            Name "VolumetricFogUpsampleComposition"
            
            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "./DepthAwareUpsample.hlsl"

            #pragma target 4.5

            #pragma vertex Vert
            #pragma fragment Frag

            TEXTURE2D_X(_VolumetricFogTexture);
            SAMPLER(sampler_BlitTexture);
            int _SceneViewMainCameraFrustumMaskEnabled;
            float4 _MainCameraFrustumPlanes[6];

            bool IsInsideMainCameraFrustum(float3 positionWS)
            {
                UNITY_UNROLL
                for (int i = 0; i < 6; ++i)
                {
                    if (dot(_MainCameraFrustumPlanes[i], float4(positionWS, 1.0)) < 0.0)
                        return false;
                }

                return true;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float4 cameraColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, input.texcoord);
                UNITY_BRANCH
                if (_SceneViewMainCameraFrustumMaskEnabled > 0)
                {
                    float depth = SampleSceneDepth(input.texcoord);
#if !UNITY_REVERSED_Z
                    depth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, depth);
#endif
                    float3 positionWS = ComputeWorldSpacePosition(input.texcoord, depth, UNITY_MATRIX_I_VP);

                    UNITY_BRANCH
                    if (!IsInsideMainCameraFrustum(positionWS))
                        return cameraColor;
                }

                float4 volumetricFog = DepthAwareUpsample(input.texcoord, _VolumetricFogTexture);

                return float4(cameraColor.rgb * volumetricFog.a + volumetricFog.rgb, cameraColor.a);
            }

            ENDHLSL
        }
    }

    SubShader
    {
        Tags
        { 
            "RenderPipeline" = "UniversalPipeline"
        }

        UsePass "Hidden/VolumetricFog/VOLUMETRICFOGRENDER"

        UsePass "Hidden/VolumetricFog/VOLUMETRICFOGTEMPORALREPROJECTION"

        UsePass "Hidden/VolumetricFog/VOLUMETRICFOGHORIZONTALBLUR"
            
        UsePass "Hidden/VolumetricFog/VOLUMETRICFOGVERTICALBLUR"

        Pass
        {
            Name "VolumetricFogUpsampleComposition"
            
            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off

            HLSLPROGRAM

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "./DepthAwareUpsample.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag

            TEXTURE2D_X(_VolumetricFogTexture);
            SAMPLER(sampler_BlitTexture);
            int _SceneViewMainCameraFrustumMaskEnabled;
            float4 _MainCameraFrustumPlanes[6];

            bool IsInsideMainCameraFrustum(float3 positionWS)
            {
                UNITY_UNROLL
                for (int i = 0; i < 6; ++i)
                {
                    if (dot(_MainCameraFrustumPlanes[i], float4(positionWS, 1.0)) < 0.0)
                        return false;
                }

                return true;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float4 cameraColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, input.texcoord);
                UNITY_BRANCH
                if (_SceneViewMainCameraFrustumMaskEnabled > 0)
                {
                    float depth = SampleSceneDepth(input.texcoord);
#if !UNITY_REVERSED_Z
                    depth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, depth);
#endif
                    float3 positionWS = ComputeWorldSpacePosition(input.texcoord, depth, UNITY_MATRIX_I_VP);

                    UNITY_BRANCH
                    if (!IsInsideMainCameraFrustum(positionWS))
                        return cameraColor;
                }

                float4 volumetricFog = DepthAwareUpsample(input.texcoord, _VolumetricFogTexture);

                return float4(cameraColor.rgb * volumetricFog.a + volumetricFog.rgb, cameraColor.a);
            }

            ENDHLSL
        }
    }

    Fallback Off
}
