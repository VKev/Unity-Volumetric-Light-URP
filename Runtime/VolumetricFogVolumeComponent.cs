using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Volume component for the volumetric fog.
/// </summary>
#if UNITY_2023_1_OR_NEWER
[VolumeComponentMenu("Custom/Volumetric Fog")]
#if UNITY_6000_0_OR_NEWER
[VolumeRequiresRendererFeatures(typeof(VolumetricFogRendererFeature))]
#endif
[SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
#else
[VolumeComponentMenuForRenderPipeline("Custom/Volumetric Fog", typeof(UniversalRenderPipeline))]
#endif
public sealed class VolumetricFogVolumeComponent : VolumeComponent, IPostProcessComponent
{
	#region Public Attributes

	private const int MaxVisibleAdditionalLights = 256;

	[Header("Distances")]
	[Tooltip("The maximum distance from the camera that the fog will be rendered up to.")]
	public ClampedFloatParameter distance = new ClampedFloatParameter(64.0f, 0.0f, 512.0f);
	[Tooltip("The world height at which the fog will have the density specified in the volume.")]
	public FloatParameter baseHeight = new FloatParameter(0.0f, true);
	[Tooltip("The world height at which the fog will have no density at all.")]
	public FloatParameter maximumHeight = new FloatParameter(50.0f, true);

	[Header("Ground")]
	[Tooltip("When enabled, allows to define a world height. Below it, fog will have no density at all.")]
	public BoolParameter enableGround = new BoolParameter(false, BoolParameter.DisplayType.Checkbox, true);
	[Tooltip("Below this world height, fog will have no density at all.")]
	public FloatParameter groundHeight = new FloatParameter(0.0f);

	[Header("Lighting")]
	[Tooltip("How dense is the fog.")]
	public ClampedFloatParameter density = new ClampedFloatParameter(0.2f, 0.0f, 1.0f);
	[Tooltip("Value that defines how much the fog attenuates light as distance increases. Lesser values lead to a darker image.")]
	public MinFloatParameter attenuationDistance = new MinFloatParameter(128.0f, 0.05f);
#if UNITY_2023_1_OR_NEWER
	[Tooltip("When enabled, adaptive probe volumes (APV) will be sampled to contribute to fog.")]
	public BoolParameter enableAPVContribution = new BoolParameter(false, BoolParameter.DisplayType.Checkbox, true);
	[Tooltip("A weight factor for the light coming from adaptive probe volumes (APV) when the probe volume contribution is enabled.")]
	public ClampedFloatParameter APVContributionWeight = new ClampedFloatParameter(1.0f, 0.0f, 1.0f);
#endif

	[Header("Main Light")]
	[Tooltip("Disabling this will avoid computing the main light contribution to fog, which in most cases will lead to better performance.")]
	public BoolParameter enableMainLightContribution = new BoolParameter(false, BoolParameter.DisplayType.Checkbox, true);
	[Tooltip("Higher positive values will make the fog affected by the main light to appear brighter when directly looking to it, while lower negative values will make the fog to appear brighter when looking away from it. The closer the value is closer to 1 or -1, the less the brightness will spread. Most times, positive values higher than 0 and lower than 1 should be used.")]
	public ClampedFloatParameter anisotropy = new ClampedFloatParameter(0.4f, -1.0f, 1.0f);
	[Tooltip("Higher values will make fog affected by the main light to appear brighter.")]
	public ClampedFloatParameter scattering = new ClampedFloatParameter(0.15f, 0.0f, 1.0f);
	[Tooltip("A multiplier color to tint the main light fog.")]
	public ColorParameter tint = new ColorParameter(Color.white, true, false, true);

	[Header("Additional Lights")]
	[Tooltip("Disabling this will avoid computing additional lights contribution to fog, which in most cases will lead to better performance.")]
	public BoolParameter enableAdditionalLightsContribution = new BoolParameter(false, BoolParameter.DisplayType.Checkbox, true);

	[Header("Performance & Quality")]
	[Tooltip("Resolution at which volumetric fog is raymarched before upsampling. Quarter resolution is significantly faster but blurrier.")]
	public VolumetricFogDownsampleModeParameter downsampleMode = new VolumetricFogDownsampleModeParameter(VolumetricFogDownsampleMode.Half);
	[Tooltip("Raymarching steps. Greater values will increase the fog quality at the expense of performance.")]
	public ClampedIntParameter maxSteps = new ClampedIntParameter(128, 8, 256);
	[Tooltip("Maximum additional lights considered during fog raymarching. Lower values improve performance in scenes with many lights.")]
	public ClampedIntParameter maxAdditionalLights = new ClampedIntParameter(MaxVisibleAdditionalLights, 0, MaxVisibleAdditionalLights);
	[Tooltip("Minimum selected additional-light count required before clustered froxel evaluation is enabled. Higher values reduce CPU overhead in low-light scenes.")]
	public ClampedIntParameter froxelClusterMinLights = new ClampedIntParameter(0, 0, MaxVisibleAdditionalLights);
	[Tooltip("When enabled, raymarch step count adapts to fog density along the view ray. Disabled keeps exact fixed-step behavior.")]
	public BoolParameter enableAdaptiveStepCount = new BoolParameter(false, BoolParameter.DisplayType.Checkbox, true);
	[Tooltip("Minimum step count used by adaptive stepping when enabled.")]
	public ClampedIntParameter adaptiveMinSteps = new ClampedIntParameter(48, 4, 256);
	[Tooltip("Density scale controlling how quickly adaptive stepping reaches max steps.")]
	public ClampedFloatParameter adaptiveStepDensityScale = new ClampedFloatParameter(2.0f, 0.1f, 16.0f);
	[Tooltip("Evaluates main/additional lighting every N raymarch steps and reuses the last lighting sample in-between. Value 1 keeps exact realtime behavior.")]
	public ClampedIntParameter lightingSampleStride = new ClampedIntParameter(1, 1, 8);
	[Tooltip("When enabled, blends current low-resolution fog with reprojected history. Keep disabled for exact per-frame behavior.")]
	public BoolParameter enableTemporalReprojection = new BoolParameter(false, BoolParameter.DisplayType.Checkbox, true);
	[Tooltip("Temporal history blend amount. Higher values reduce noise and allow fewer steps at the cost of more ghosting risk.")]
	public ClampedFloatParameter temporalBlendFactor = new ClampedFloatParameter(0.9f, 0.0f, 0.98f);
	[Tooltip("The number of times that the fog texture will be blurred. Higher values lead to softer volumetric god rays at the cost of some performance.")]
	public ClampedIntParameter blurIterations = new ClampedIntParameter(2, 0, 4);
	[Tooltip("When greater than zero, raymarching stops early once transmittance falls below this threshold. This improves performance in dense fog.")]
	public ClampedFloatParameter transmittanceThreshold = new ClampedFloatParameter(0.0f, 0.0f, 1.0f);
	[Header("Baked 3D Field")]
	[Tooltip("When enabled and both baked textures are assigned, fog integrates baked 3D density/radiance fields inside the configured world volume.")]
	public BoolParameter enableBaked3DMode = new BoolParameter(false, BoolParameter.DisplayType.Checkbox, true);
	[Tooltip("When enabled in Baked 3D mode, realtime dynamic lights are added on top of baked radiance.")]
	public BoolParameter baked3DAddRealtimeLights = new BoolParameter(true, BoolParameter.DisplayType.Checkbox, true);
	[Tooltip("Baked 3D extinction (density * absorption) texture.")]
	public TextureParameter baked3DExtinctionTexture = new TextureParameter(null);
	[Tooltip("Baked 3D in-scattered radiance texture (HDR).")]
	public TextureParameter baked3DRadianceTexture = new TextureParameter(null);
	[Tooltip("World-space center of the baked 3D field volume.")]
	public Vector3Parameter baked3DVolumeCenter = new Vector3Parameter(Vector3.zero);
	[Tooltip("World-space size of the baked 3D field volume.")]
	public Vector3Parameter baked3DVolumeSize = new Vector3Parameter(new Vector3(128.0f, 64.0f, 128.0f));
	[Tooltip("Voxel resolution per axis used when baking 3D textures from the inspector Bake 3D button.")]
	public ClampedIntParameter baked3DResolution = new ClampedIntParameter(64, 16, 256);
	[Header("Static Light Bake")]
	[Tooltip("When enabled, static lights (GameObject Static or Light Mode Mixed/Baked) and static-object occlusion (from static colliders) use baked snapshot data until Bake Revision changes. Dynamic lights and camera-dependent computations remain live.")]
	public BoolParameter enableStaticLightsBake = new BoolParameter(false, BoolParameter.DisplayType.Checkbox, true);
	[Tooltip("Internal bake stamp. The inspector Bake button increases this to refresh cached static light data.")]
	public MinIntParameter staticLightsBakeRevision = new MinIntParameter(0, 0);
	[Tooltip("Disabling this will completely remove any feature from the volumetric fog from being rendered at all.")]
	public BoolParameter enabled = new BoolParameter(false, BoolParameter.DisplayType.Checkbox, true);

	[Header("Render Pass Event")]
	[Tooltip("The URP render pass event to render the volumetric fog.")]
	public VolumetricFogRenderPassEventParameter renderPassEvent = new VolumetricFogRenderPassEventParameter(VolumetricFogRenderPass.DefaultVolumetricFogRenderPassEvent);

	#endregion

	#region Initialization Methods

	public VolumetricFogVolumeComponent() : base()
	{
		displayName = "Volumetric Fog";
	}

	#endregion

	#region Volume Component Methods

	private void OnValidate()
	{
		maximumHeight.overrideState = baseHeight.overrideState;
		maximumHeight.value = Mathf.Max(baseHeight.value, maximumHeight.value);
		baseHeight.value = Mathf.Min(baseHeight.value, maximumHeight.value);
		adaptiveMinSteps.value = Mathf.Min(adaptiveMinSteps.value, maxSteps.value);
		Vector3 bakedVolumeSize = baked3DVolumeSize.value;
		baked3DVolumeSize.value = new Vector3(Mathf.Max(0.01f, bakedVolumeSize.x), Mathf.Max(0.01f, bakedVolumeSize.y), Mathf.Max(0.01f, bakedVolumeSize.z));
	}

	#endregion

	#region IPostProcessComponent Methods

#if !UNITY_2023_1_OR_NEWER

	/// <summary>
	/// <inheritdoc/>
	/// </summary>
	/// <returns></returns>
	public bool IsTileCompatible()
	{
		return true;
	}

#endif

	/// <summary>
	/// <inheritdoc/>
	/// </summary>
	/// <returns></returns>
	public bool IsActive()
	{
		return enabled.value && distance.value > 0.0f && groundHeight.value < maximumHeight.value && density.value > 0.0f;
	}

	#endregion
}
