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
	[Tooltip("RuntimeOnly evaluates all fog lights in real time. StaticVoxelDynamicRealtime injects static-tagged lights into a runtime voxel volume and keeps dynamic lights in real time.")]
	public VolumetricFogLightingModeParameter lightingMode = new VolumetricFogLightingModeParameter(VolumetricFogLightingMode.RuntimeOnly);

	[Header("Static Voxel Lighting")]
	[Tooltip("When enabled and StaticVoxelDynamicRealtime is selected, the current main light is injected into the static voxel volume instead of being evaluated dynamically.")]
	public BoolParameter staticVoxelIncludeMainLight = new BoolParameter(false, BoolParameter.DisplayType.Checkbox, true);
	[Tooltip("World-space center of the static voxel lighting volume.")]
	public Vector3Parameter staticVoxelBoundsCenter = new Vector3Parameter(new Vector3(0.0f, 16.0f, 0.0f), true);
	[Tooltip("World-space size of the static voxel lighting volume.")]
	public Vector3Parameter staticVoxelBoundsSize = new Vector3Parameter(new Vector3(128.0f, 64.0f, 128.0f), true);
	[Tooltip("Voxel resolution on X axis.")]
	public ClampedIntParameter staticVoxelResolutionX = new ClampedIntParameter(64, 8, 256);
	[Tooltip("Voxel resolution on Y axis.")]
	public ClampedIntParameter staticVoxelResolutionY = new ClampedIntParameter(32, 8, 256);
	[Tooltip("Voxel resolution on Z axis.")]
	public ClampedIntParameter staticVoxelResolutionZ = new ClampedIntParameter(64, 8, 256);
	[Tooltip("Global multiplier for static voxel lighting contribution.")]
	public ClampedFloatParameter staticVoxelIntensity = new ClampedFloatParameter(1.0f, 0.0f, 8.0f);
	[Tooltip("When enabled, static voxel samples use directional phase from stored dominant light direction and anisotropy.")]
	public BoolParameter staticVoxelDirectionalPhase = new BoolParameter(true, BoolParameter.DisplayType.Checkbox, true);
	[Tooltip("Defines when the runtime static voxel volume should be rebuilt.")]
	public VolumetricFogStaticVoxelUpdateModeParameter staticVoxelUpdateMode = new VolumetricFogStaticVoxelUpdateModeParameter(VolumetricFogStaticVoxelUpdateMode.OnChange);
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
	[Tooltip("The number of times that the fog texture will be blurred. Higher values lead to softer volumetric god rays at the cost of some performance.")]
	public ClampedIntParameter blurIterations = new ClampedIntParameter(2, 0, 4);
	[Tooltip("When greater than zero, raymarching stops early once transmittance falls below this threshold. This improves performance in dense fog.")]
	public ClampedFloatParameter transmittanceThreshold = new ClampedFloatParameter(0.0f, 0.0f, 1.0f);
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

		Vector3 boundsSize = staticVoxelBoundsSize.value;
		boundsSize.x = Mathf.Max(0.01f, boundsSize.x);
		boundsSize.y = Mathf.Max(0.01f, boundsSize.y);
		boundsSize.z = Mathf.Max(0.01f, boundsSize.z);
		staticVoxelBoundsSize.value = boundsSize;
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
