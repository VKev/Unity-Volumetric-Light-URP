using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Unity.Collections;

#if UNITY_6000_0_OR_NEWER
using System;
using UnityEngine.Rendering.RenderGraphModule;
#endif

/// <summary>
/// The volumetric fog render pass.
/// </summary>
public sealed class VolumetricFogRenderPass : ScriptableRenderPass
{
	#region Definitions

#if UNITY_6000_0_OR_NEWER

	/// <summary>
	/// The subpasses the volumetric fog render pass is made of.
	/// </summary>
	private enum PassStage : byte
	{
		DownsampleDepth,
		VolumetricFogRender,
		VolumetricFogBlur,
		VolumetricFogUpsampleComposition
	}

	/// <summary>
	/// Holds the data needed by the execution of the volumetric fog render pass subpasses.
	/// </summary>
	private class PassData
	{
		public PassStage stage;

		public TextureHandle source;
		public TextureHandle target;

		public Material material;
		public int materialPassIndex;
		public int materialAdditionalPassIndex;

		public TextureHandle downsampledCameraDepthTarget;
		public TextureHandle volumetricFogRenderTarget;
		public UniversalLightData lightData;
		public VolumetricFogVolumeComponent fogVolume;
		public Vector3 cameraPosition;
		public int blurIterations;
	}

#endif

	#endregion

	#region Public Attributes

	public const RenderPassEvent DefaultRenderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
	public const VolumetricFogRenderPassEvent DefaultVolumetricFogRenderPassEvent = (VolumetricFogRenderPassEvent)DefaultRenderPassEvent;

	#endregion

	#region Private Attributes

	private const string DownsampledCameraDepthRTName = "_DownsampledCameraDepth";
	private const string VolumetricFogRenderRTName = "_VolumetricFog";
	private const string VolumetricFogBlurRTName = "_VolumetricFogBlur";
	private const string VolumetricFogUpsampleCompositionRTName = "_VolumetricFogUpsampleComposition";

	private static readonly int DownsampledCameraDepthTextureId = Shader.PropertyToID("_DownsampledCameraDepthTexture");
	private static readonly int VolumetricFogTextureId = Shader.PropertyToID("_VolumetricFogTexture");
	private static readonly int SceneViewMainCameraFrustumMaskEnabledId = Shader.PropertyToID("_SceneViewMainCameraFrustumMaskEnabled");
	private static readonly int MainCameraFrustumPlanesId = Shader.PropertyToID("_MainCameraFrustumPlanes");

	private static readonly int FrameCountId = Shader.PropertyToID("_FrameCount");
	private static readonly int CustomAdditionalLightsCountId = Shader.PropertyToID("_CustomAdditionalLightsCount");
	private static readonly int DistanceId = Shader.PropertyToID("_Distance");
	private static readonly int BaseHeightId = Shader.PropertyToID("_BaseHeight");
	private static readonly int MaximumHeightId = Shader.PropertyToID("_MaximumHeight");
	private static readonly int GroundHeightId = Shader.PropertyToID("_GroundHeight");
	private static readonly int DensityId = Shader.PropertyToID("_Density");
	private static readonly int AbsortionId = Shader.PropertyToID("_Absortion");
#if UNITY_2023_1_OR_NEWER
	private static readonly int APVContributionWeigthId = Shader.PropertyToID("_APVContributionWeight");
#endif
	private static readonly int TintId = Shader.PropertyToID("_Tint");
	private static readonly int MaxStepsId = Shader.PropertyToID("_MaxSteps");
	private static readonly int TransmittanceThresholdId = Shader.PropertyToID("_TransmittanceThreshold");

	private static readonly int AnisotropiesArrayId = Shader.PropertyToID("_Anisotropies");
	private static readonly int ScatteringsArrayId = Shader.PropertyToID("_Scatterings");
	private static readonly int RadiiSqArrayId = Shader.PropertyToID("_RadiiSq");
	private static readonly int AdditionalLightIndicesArrayId = Shader.PropertyToID("_AdditionalLightIndices");

	private const int MaxVisibleAdditionalLights = 256;
	private const float MinAdditionalLightScattering = 0.0001f;
	private const float MinAdditionalLightIntensity = 0.001f;
	private static int LightsParametersLength = MaxVisibleAdditionalLights + 1;

	private static readonly float[] Anisotropies = new float[LightsParametersLength];
	private static readonly float[] Scatterings = new float[LightsParametersLength];
	private static readonly float[] RadiiSq = new float[MaxVisibleAdditionalLights];
	private static readonly float[] AdditionalLightIndices = new float[MaxVisibleAdditionalLights];

	private static readonly int[] SelectedAdditionalIndices = new int[MaxVisibleAdditionalLights];
	private static readonly float[] SelectedAnisotropies = new float[MaxVisibleAdditionalLights];
	private static readonly float[] SelectedScatterings = new float[MaxVisibleAdditionalLights];
	private static readonly float[] SelectedRadiiSq = new float[MaxVisibleAdditionalLights];
	private static readonly float[] SelectedScores = new float[MaxVisibleAdditionalLights];

	private int downsampleDepthPassIndex;
	private int volumetricFogRenderPassIndex;
	private int volumetricFogHorizontalBlurPassIndex;
	private int volumetricFogVerticalBlurPassIndex;
	private int volumetricFogUpsampleCompositionPassIndex;

	private Material downsampleDepthMaterial;
	private Material volumetricFogMaterial;

	private RTHandle downsampledCameraDepthRTHandle;
	private RTHandle volumetricFogRenderRTHandle;
	private RTHandle volumetricFogBlurRTHandle;
	private RTHandle volumetricFogUpsampleCompositionRTHandle;

	private ProfilingSampler downsampleDepthProfilingSampler;
	private readonly Vector4[] mainCameraFrustumPlanes = new Vector4[6];
	private bool sceneViewMainCameraFrustumMaskEnabled;

	private static bool isMaterialStateInitialized;
	private static bool cachedMainLightContributionEnabled;
	private static bool cachedAdditionalLightsContributionEnabled;
	private static bool cachedSceneViewMainCameraFrustumMaskEnabled;
#if UNITY_2023_1_OR_NEWER
	private static bool cachedAPVContributionEnabled;
#endif
	private static int cachedAdditionalLightsCount;
	private static int cachedLightsHash;
	private static int cachedMainCameraFrustumPlanesHash;
	private static float cachedDistance;
	private static float cachedBaseHeight;
	private static float cachedMaximumHeight;
	private static float cachedGroundHeight;
	private static float cachedDensity;
	private static float cachedAbsortion;
#if UNITY_2023_1_OR_NEWER
	private static float cachedAPVContributionWeight;
#endif
	private static Color cachedTint;
	private static int cachedMaxSteps;
	private static float cachedTransmittanceThreshold;

	#endregion

	#region Initialization Methods

	/// <summary>
	/// Constructor.
	/// </summary>
	/// <param name="downsampleDepthMaterial"></param>
	/// <param name="volumetricFogMaterial"></param>
	/// <param name="passEvent"></param>
	public VolumetricFogRenderPass(Material downsampleDepthMaterial, Material volumetricFogMaterial, RenderPassEvent passEvent) : base()
	{
		profilingSampler = new ProfilingSampler("Volumetric Fog");
		downsampleDepthProfilingSampler = new ProfilingSampler("Downsample Depth");
		renderPassEvent = passEvent;
#if UNITY_6000_0_OR_NEWER
		requiresIntermediateTexture = false;
#endif

		this.downsampleDepthMaterial = downsampleDepthMaterial;
		this.volumetricFogMaterial = volumetricFogMaterial;

		InitializePassesIndices();
		ResetMaterialStateCache();
	}

	/// <summary>
	/// Initializes the passes indices.
	/// </summary>
	private void InitializePassesIndices()
	{
		downsampleDepthPassIndex = downsampleDepthMaterial.FindPass("DownsampleDepth");
		volumetricFogRenderPassIndex = volumetricFogMaterial.FindPass("VolumetricFogRender");
		volumetricFogHorizontalBlurPassIndex = volumetricFogMaterial.FindPass("VolumetricFogHorizontalBlur");
		volumetricFogVerticalBlurPassIndex = volumetricFogMaterial.FindPass("VolumetricFogVerticalBlur");
		volumetricFogUpsampleCompositionPassIndex = volumetricFogMaterial.FindPass("VolumetricFogUpsampleComposition");
	}

	/// <summary>
	/// Enables or disables scene view masking to only apply fog in the main camera frustum.
	/// </summary>
	/// <param name="enableMask"></param>
	/// <param name="mainCamera"></param>
	public void SetupSceneViewMainCameraMask(bool enableMask, Camera mainCamera)
	{
		sceneViewMainCameraFrustumMaskEnabled = enableMask && mainCamera != null;
		if (!sceneViewMainCameraFrustumMaskEnabled)
			return;

		Plane[] frustumPlanes = GeometryUtility.CalculateFrustumPlanes(mainCamera);
		for (int i = 0; i < mainCameraFrustumPlanes.Length; ++i)
		{
			Plane plane = frustumPlanes[i];
			mainCameraFrustumPlanes[i] = new Vector4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
		}
	}

	#endregion

	#region Scriptable Render Pass Methods

	/// <summary>
	/// <inheritdoc/>
	/// </summary>
	/// <param name="cmd"></param>
	/// <param name="renderingData"></param>
#if UNITY_6000_0_OR_NEWER
	[Obsolete]
#endif
	public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
	{
		base.OnCameraSetup(cmd, ref renderingData);

		RenderTextureDescriptor cameraTargetDescriptor = renderingData.cameraData.cameraTargetDescriptor;
		cameraTargetDescriptor.depthBufferBits = (int)DepthBits.None;
		VolumetricFogVolumeComponent fogVolume = VolumeManager.instance.stack.GetComponent<VolumetricFogVolumeComponent>();

		RenderTextureFormat originalColorFormat = cameraTargetDescriptor.colorFormat;
		Vector2Int originalResolution = new Vector2Int(cameraTargetDescriptor.width, cameraTargetDescriptor.height);
		int downsampleFactor = GetDownsampleFactor(fogVolume);

		cameraTargetDescriptor.width = Mathf.Max(1, cameraTargetDescriptor.width / downsampleFactor);
		cameraTargetDescriptor.height = Mathf.Max(1, cameraTargetDescriptor.height / downsampleFactor);
		cameraTargetDescriptor.graphicsFormat = GraphicsFormat.R32_SFloat;
		ReAllocateIfNeeded(ref downsampledCameraDepthRTHandle, cameraTargetDescriptor, wrapMode: TextureWrapMode.Clamp, name: DownsampledCameraDepthRTName);

		cameraTargetDescriptor.colorFormat = RenderTextureFormat.ARGBHalf;
		ReAllocateIfNeeded(ref volumetricFogRenderRTHandle, cameraTargetDescriptor, wrapMode: TextureWrapMode.Clamp, name: VolumetricFogRenderRTName);
		ReAllocateIfNeeded(ref volumetricFogBlurRTHandle, cameraTargetDescriptor, wrapMode: TextureWrapMode.Clamp, name: VolumetricFogBlurRTName);

		cameraTargetDescriptor.width = originalResolution.x;
		cameraTargetDescriptor.height = originalResolution.y;
		cameraTargetDescriptor.colorFormat = originalColorFormat;
		ReAllocateIfNeeded(ref volumetricFogUpsampleCompositionRTHandle, cameraTargetDescriptor, wrapMode: TextureWrapMode.Clamp, name: VolumetricFogUpsampleCompositionRTName);
	}

	/// <summary>
	/// <inheritdoc/>
	/// </summary>
	/// <param name="context"></param>
	/// <param name="renderingData"></param>
#if UNITY_6000_0_OR_NEWER
	[Obsolete]
#endif
	public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
	{
		CommandBuffer cmd = CommandBufferPool.Get();
		VolumetricFogVolumeComponent fogVolume = VolumeManager.instance.stack.GetComponent<VolumetricFogVolumeComponent>();
		Vector3 cameraPosition = renderingData.cameraData.camera != null ? renderingData.cameraData.camera.transform.position : Vector3.zero;
		ApplySceneViewMainCameraMaskParameters(volumetricFogMaterial);

		using (new ProfilingScope(cmd, downsampleDepthProfilingSampler))
		{
			Blitter.BlitCameraTexture(cmd, downsampledCameraDepthRTHandle, downsampledCameraDepthRTHandle, downsampleDepthMaterial, downsampleDepthPassIndex);
			volumetricFogMaterial.SetTexture(DownsampledCameraDepthTextureId, downsampledCameraDepthRTHandle);
		}

		using (new ProfilingScope(cmd, profilingSampler))
		{
			UpdateVolumetricFogMaterialParameters(volumetricFogMaterial, fogVolume, cameraPosition, renderingData.lightData.mainLightIndex, renderingData.lightData.additionalLightsCount, renderingData.lightData.visibleLights);
			Blitter.BlitCameraTexture(cmd, volumetricFogRenderRTHandle, volumetricFogRenderRTHandle, volumetricFogMaterial, volumetricFogRenderPassIndex);

			int blurIterations = fogVolume != null ? fogVolume.blurIterations.value : 0;

			for (int i = 0; i < blurIterations; ++i)
			{
				Blitter.BlitCameraTexture(cmd, volumetricFogRenderRTHandle, volumetricFogBlurRTHandle, volumetricFogMaterial, volumetricFogHorizontalBlurPassIndex);
				Blitter.BlitCameraTexture(cmd, volumetricFogBlurRTHandle, volumetricFogRenderRTHandle, volumetricFogMaterial, volumetricFogVerticalBlurPassIndex);
			}

			volumetricFogMaterial.SetTexture(VolumetricFogTextureId, volumetricFogRenderRTHandle);

			RTHandle cameraColorRt = renderingData.cameraData.renderer.cameraColorTargetHandle;
			Blitter.BlitCameraTexture(cmd, cameraColorRt, volumetricFogUpsampleCompositionRTHandle, volumetricFogMaterial, volumetricFogUpsampleCompositionPassIndex);
			Blitter.BlitCameraTexture(cmd, volumetricFogUpsampleCompositionRTHandle, cameraColorRt);
		}

		context.ExecuteCommandBuffer(cmd);

		cmd.Clear();

		CommandBufferPool.Release(cmd);
	}

#if UNITY_6000_0_OR_NEWER

	/// <summary>
	/// <inheritdoc/>
	/// </summary>
	/// <param name="renderGraph"></param>
	/// <param name="frameData"></param>
	public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
	{
		UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
		UniversalLightData lightData = frameData.Get<UniversalLightData>();
		UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
		VolumetricFogVolumeComponent fogVolume = VolumeManager.instance.stack.GetComponent<VolumetricFogVolumeComponent>();
		Vector3 cameraPosition = cameraData.camera != null ? cameraData.camera.transform.position : Vector3.zero;
		ApplySceneViewMainCameraMaskParameters(volumetricFogMaterial);

		CreateRenderGraphTextures(renderGraph, cameraData, fogVolume, out TextureHandle downsampledCameraDepthTarget, out TextureHandle volumetricFogRenderTarget, out TextureHandle volumetricFogBlurRenderTarget, out TextureHandle volumetricFogUpsampleCompositionTarget);

		using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Downsample Depth Pass", out PassData passData, downsampleDepthProfilingSampler))
		{
			passData.stage = PassStage.DownsampleDepth;
			passData.source = resourceData.cameraDepthTexture;
			passData.target = downsampledCameraDepthTarget;
			passData.material = downsampleDepthMaterial;
			passData.materialPassIndex = downsampleDepthPassIndex;

			builder.SetRenderAttachment(downsampledCameraDepthTarget, 0, AccessFlags.WriteAll);
			builder.UseTexture(resourceData.cameraDepthTexture);
			builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
		}

		using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Volumetric Fog Render Pass", out PassData passData, profilingSampler))
		{
			passData.stage = PassStage.VolumetricFogRender;
			passData.source = downsampledCameraDepthTarget;
			passData.target = volumetricFogRenderTarget;
			passData.material = volumetricFogMaterial;
			passData.materialPassIndex = volumetricFogRenderPassIndex;
			passData.downsampledCameraDepthTarget = downsampledCameraDepthTarget;
			passData.lightData = lightData;
			passData.fogVolume = fogVolume;
			passData.cameraPosition = cameraPosition;

			builder.SetRenderAttachment(volumetricFogRenderTarget, 0, AccessFlags.WriteAll);
			builder.UseTexture(downsampledCameraDepthTarget);
			if (resourceData.mainShadowsTexture.IsValid())
				builder.UseTexture(resourceData.mainShadowsTexture);
			if (resourceData.additionalShadowsTexture.IsValid())
				builder.UseTexture(resourceData.additionalShadowsTexture);
			builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
		}

		using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass("Volumetric Fog Blur Pass", out PassData passData, profilingSampler))
		{
			passData.stage = PassStage.VolumetricFogBlur;
			passData.source = volumetricFogRenderTarget;
			passData.target = volumetricFogBlurRenderTarget;
			passData.material = volumetricFogMaterial;
			passData.materialPassIndex = volumetricFogHorizontalBlurPassIndex;
			passData.materialAdditionalPassIndex = volumetricFogVerticalBlurPassIndex;
			passData.blurIterations = fogVolume != null ? fogVolume.blurIterations.value : 0;

			builder.UseTexture(volumetricFogRenderTarget, AccessFlags.ReadWrite);
			builder.UseTexture(volumetricFogBlurRenderTarget, AccessFlags.ReadWrite);
			builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecuteUnsafeBlurPass(data, context));
		}

		using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("Volumetric Fog Upsample Composition Pass", out PassData passData, profilingSampler))
		{
			passData.stage = PassStage.VolumetricFogUpsampleComposition;
			passData.source = resourceData.cameraColor;
			passData.target = volumetricFogUpsampleCompositionTarget;
			passData.material = volumetricFogMaterial;
			passData.materialPassIndex = volumetricFogUpsampleCompositionPassIndex;
			passData.volumetricFogRenderTarget = volumetricFogRenderTarget;

			builder.SetRenderAttachment(volumetricFogUpsampleCompositionTarget, 0, AccessFlags.WriteAll);
			builder.UseTexture(resourceData.cameraDepthTexture);
			builder.UseTexture(downsampledCameraDepthTarget);
			builder.UseTexture(volumetricFogRenderTarget);
			builder.UseTexture(resourceData.cameraColor);
			builder.SetRenderFunc((PassData data, RasterGraphContext context) => ExecutePass(data, context));
		}

		resourceData.cameraColor = volumetricFogUpsampleCompositionTarget;
	}

#endif

	#endregion

	#region Methods

	/// <summary>
	/// Updates the material parameters that control scene view masking to the main camera frustum.
	/// </summary>
	/// <param name="material"></param>
	private void ApplySceneViewMainCameraMaskParameters(Material material)
	{
		bool maskEnabled = sceneViewMainCameraFrustumMaskEnabled;
		if (!isMaterialStateInitialized || cachedSceneViewMainCameraFrustumMaskEnabled != maskEnabled)
		{
			material.SetInteger(SceneViewMainCameraFrustumMaskEnabledId, maskEnabled ? 1 : 0);
			cachedSceneViewMainCameraFrustumMaskEnabled = maskEnabled;
		}

		if (maskEnabled)
		{
			int planesHash = ComputeVector4ArrayHash(mainCameraFrustumPlanes, mainCameraFrustumPlanes.Length);
			if (!isMaterialStateInitialized || planesHash != cachedMainCameraFrustumPlanesHash)
			{
				material.SetVectorArray(MainCameraFrustumPlanesId, mainCameraFrustumPlanes);
				cachedMainCameraFrustumPlanesHash = planesHash;
			}
		}
	}

	/// <summary>
	/// Updates the volumetric fog material parameters.
	/// </summary>
	/// <param name="volumetricFogMaterial"></param>
	/// <param name="fogVolume"></param>
	/// <param name="cameraPosition"></param>
	/// <param name="mainLightIndex"></param>
	/// <param name="additionalLightsCount"></param>
	/// <param name="visibleLights"></param>
	private static void UpdateVolumetricFogMaterialParameters(Material volumetricFogMaterial, VolumetricFogVolumeComponent fogVolume, Vector3 cameraPosition, int mainLightIndex, int additionalLightsCount, NativeArray<VisibleLight> visibleLights)
	{
		if (fogVolume == null)
			return;

		bool enableMainLightContribution = fogVolume.enableMainLightContribution.value && fogVolume.scattering.value > 0.0f && mainLightIndex > -1;
		bool enableAdditionalLightsContribution = fogVolume.enableAdditionalLightsContribution.value && additionalLightsCount > 0 && fogVolume.maxAdditionalLights.value > 0;

		int lightsHash = 0;
		int effectiveAdditionalLightsCount = 0;
		if (enableMainLightContribution || enableAdditionalLightsContribution)
			effectiveAdditionalLightsCount = UpdateLightsParameters(fogVolume, enableMainLightContribution, enableAdditionalLightsContribution, mainLightIndex, additionalLightsCount, visibleLights, cameraPosition, out lightsHash);

		enableAdditionalLightsContribution &= effectiveAdditionalLightsCount > 0;

#if UNITY_2023_1_OR_NEWER
		bool enableAPVContribution = fogVolume.enableAPVContribution.value && fogVolume.APVContributionWeight.value > 0.0f;
		if (!isMaterialStateInitialized || cachedAPVContributionEnabled != enableAPVContribution)
		{
			if (enableAPVContribution)
				volumetricFogMaterial.EnableKeyword("_APV_CONTRIBUTION_ENABLED");
			else
				volumetricFogMaterial.DisableKeyword("_APV_CONTRIBUTION_ENABLED");

			cachedAPVContributionEnabled = enableAPVContribution;
		}
#endif

		if (!isMaterialStateInitialized || cachedMainLightContributionEnabled != enableMainLightContribution)
		{
			if (enableMainLightContribution)
				volumetricFogMaterial.DisableKeyword("_MAIN_LIGHT_CONTRIBUTION_DISABLED");
			else
				volumetricFogMaterial.EnableKeyword("_MAIN_LIGHT_CONTRIBUTION_DISABLED");

			cachedMainLightContributionEnabled = enableMainLightContribution;
		}

		if (!isMaterialStateInitialized || cachedAdditionalLightsContributionEnabled != enableAdditionalLightsContribution)
		{
			if (enableAdditionalLightsContribution)
				volumetricFogMaterial.DisableKeyword("_ADDITIONAL_LIGHTS_CONTRIBUTION_DISABLED");
			else
				volumetricFogMaterial.EnableKeyword("_ADDITIONAL_LIGHTS_CONTRIBUTION_DISABLED");

			cachedAdditionalLightsContributionEnabled = enableAdditionalLightsContribution;
		}

		volumetricFogMaterial.SetInteger(FrameCountId, Time.renderedFrameCount % 64);
		SetIntIfChanged(volumetricFogMaterial, CustomAdditionalLightsCountId, effectiveAdditionalLightsCount, ref cachedAdditionalLightsCount);
		SetFloatIfChanged(volumetricFogMaterial, DistanceId, fogVolume.distance.value, ref cachedDistance);
		SetFloatIfChanged(volumetricFogMaterial, BaseHeightId, fogVolume.baseHeight.value, ref cachedBaseHeight);
		SetFloatIfChanged(volumetricFogMaterial, MaximumHeightId, fogVolume.maximumHeight.value, ref cachedMaximumHeight);

		float groundHeight = (fogVolume.enableGround.overrideState && fogVolume.enableGround.value) ? fogVolume.groundHeight.value : float.MinValue;
		SetFloatIfChanged(volumetricFogMaterial, GroundHeightId, groundHeight, ref cachedGroundHeight);
		SetFloatIfChanged(volumetricFogMaterial, DensityId, fogVolume.density.value, ref cachedDensity);
		SetFloatIfChanged(volumetricFogMaterial, AbsortionId, 1.0f / fogVolume.attenuationDistance.value, ref cachedAbsortion);
#if UNITY_2023_1_OR_NEWER
		float apvContributionWeight = fogVolume.enableAPVContribution.value ? fogVolume.APVContributionWeight.value : 0.0f;
		SetFloatIfChanged(volumetricFogMaterial, APVContributionWeigthId, apvContributionWeight, ref cachedAPVContributionWeight);
#endif
		SetColorIfChanged(volumetricFogMaterial, TintId, fogVolume.tint.value, ref cachedTint);
		SetIntIfChanged(volumetricFogMaterial, MaxStepsId, fogVolume.maxSteps.value, ref cachedMaxSteps);
		SetFloatIfChanged(volumetricFogMaterial, TransmittanceThresholdId, fogVolume.transmittanceThreshold.value, ref cachedTransmittanceThreshold);

		if (enableMainLightContribution || effectiveAdditionalLightsCount > 0)
		{
			if (!isMaterialStateInitialized || lightsHash != cachedLightsHash)
			{
				volumetricFogMaterial.SetFloatArray(AnisotropiesArrayId, Anisotropies);
				volumetricFogMaterial.SetFloatArray(ScatteringsArrayId, Scatterings);
				volumetricFogMaterial.SetFloatArray(RadiiSqArrayId, RadiiSq);
				volumetricFogMaterial.SetFloatArray(AdditionalLightIndicesArrayId, AdditionalLightIndices);
				cachedLightsHash = lightsHash;
			}
		}

		isMaterialStateInitialized = true;
	}

	/// <summary>
	/// Updates and selects additional lights that will contribute to fog.
	/// </summary>
	/// <param name="fogVolume"></param>
	/// <param name="enableMainLightContribution"></param>
	/// <param name="enableAdditionalLightsContribution"></param>
	/// <param name="mainLightIndex"></param>
	/// <param name="additionalLightsCount"></param>
	/// <param name="visibleLights"></param>
	/// <param name="cameraPosition"></param>
	/// <param name="lightsHash"></param>
	/// <returns></returns>
	private static int UpdateLightsParameters(VolumetricFogVolumeComponent fogVolume, bool enableMainLightContribution, bool enableAdditionalLightsContribution, int mainLightIndex, int additionalLightsCount, NativeArray<VisibleLight> visibleLights, Vector3 cameraPosition, out int lightsHash)
	{
		// TODO: Forward+ and deferred+ visibleLights.Length is 256. In forward, it is 257 so the main light is considered apart. In deferred it seems to not have any limit (seen 1.6k and beyond).
		// All rendering paths have maxVisibleAdditionalLights at 256.
		// For the time being, compact the best candidates and send a trimmed list to shader.
		int effectiveAdditionalLightsCount = 0;

		if (enableAdditionalLightsContribution && visibleLights.Length > 0)
		{
			int maxAdditionalLights = Mathf.Clamp(fogVolume.maxAdditionalLights.value, 0, MaxVisibleAdditionalLights);
			int lastIndex = Mathf.Min(visibleLights.Length - 1, LightsParametersLength - 1);
			float fogDistance = fogVolume.distance.value;
			bool groundEnabled = fogVolume.enableGround.overrideState && fogVolume.enableGround.value;
			float fogMinHeight = groundEnabled ? fogVolume.groundHeight.value : float.NegativeInfinity;
			float fogMaxHeight = fogVolume.maximumHeight.value;
			int additionalLightSlotIndex = 0;

			for (int i = 0; i <= lastIndex && additionalLightSlotIndex < additionalLightsCount; ++i)
			{
				if (i == mainLightIndex)
					continue;

				int additionalLightIndex = additionalLightSlotIndex++;
				VisibleLight visibleLight = visibleLights[i];

				if (!TryGetAdditionalLightCandidate(visibleLight, cameraPosition, fogDistance, fogMinHeight, fogMaxHeight, out float anisotropy, out float scattering, out float radiusSq, out float score))
					continue;

				TryInsertSelectedAdditionalLight(additionalLightIndex, anisotropy, scattering, radiusSq, score, maxAdditionalLights, ref effectiveAdditionalLightsCount);
			}
		}

		for (int i = 0; i < effectiveAdditionalLightsCount; ++i)
		{
			AdditionalLightIndices[i] = SelectedAdditionalIndices[i];
			Anisotropies[i] = SelectedAnisotropies[i];
			Scatterings[i] = SelectedScatterings[i];
			RadiiSq[i] = SelectedRadiiSq[i];
		}

		if (enableMainLightContribution)
		{
			Anisotropies[effectiveAdditionalLightsCount] = fogVolume.anisotropy.value;
			Scatterings[effectiveAdditionalLightsCount] = fogVolume.scattering.value;
		}

		lightsHash = ComputeLightsHash(effectiveAdditionalLightsCount, enableMainLightContribution);
		return effectiveAdditionalLightsCount;
	}

	/// <summary>
	/// Returns whether a visible light is a valid additional light candidate for fog.
	/// </summary>
	/// <param name="visibleLight"></param>
	/// <param name="cameraPosition"></param>
	/// <param name="fogDistance"></param>
	/// <param name="fogMinHeight"></param>
	/// <param name="fogMaxHeight"></param>
	/// <param name="anisotropy"></param>
	/// <param name="scattering"></param>
	/// <param name="radiusSq"></param>
	/// <param name="score"></param>
	/// <returns></returns>
	private static bool TryGetAdditionalLightCandidate(in VisibleLight visibleLight, in Vector3 cameraPosition, float fogDistance, float fogMinHeight, float fogMaxHeight, out float anisotropy, out float scattering, out float radiusSq, out float score)
	{
		anisotropy = 0.0f;
		scattering = 0.0f;
		radiusSq = 0.0f;
		score = 0.0f;

		Light light = visibleLight.light;
		if (light == null || !light.enabled || !light.gameObject.activeInHierarchy)
			return false;

		// Volumetric additional lights are only expected to be point and spot lights.
		if (light.type != LightType.Point && light.type != LightType.Spot)
			return false;

		if (!light.TryGetComponent(out VolumetricAdditionalLight volumetricLight) || !volumetricLight.enabled || !volumetricLight.gameObject.activeInHierarchy)
			return false;

		scattering = volumetricLight.Scattering;
		if (scattering <= MinAdditionalLightScattering || light.intensity <= MinAdditionalLightIntensity)
			return false;

		float range = Mathf.Max(light.range, 0.01f);
		Vector3 lightPosition = light.transform.position;
		float maxDistance = fogDistance + range;
		float distanceSq = (lightPosition - cameraPosition).sqrMagnitude;
		if (distanceSq > maxDistance * maxDistance)
			return false;

		float lightMinY = lightPosition.y - range;
		float lightMaxY = lightPosition.y + range;
		if (lightMaxY < fogMinHeight || lightMinY > fogMaxHeight)
			return false;

		anisotropy = volumetricLight.Anisotropy;
		radiusSq = volumetricLight.Radius * volumetricLight.Radius;

		float distanceWeight = (range * range) / Mathf.Max(distanceSq, 1.0f);
		score = scattering * light.intensity * distanceWeight;
		if (light.type == LightType.Spot)
		{
			float spotAngleCos = Mathf.Cos(0.5f * Mathf.Deg2Rad * light.spotAngle);
			score *= Mathf.Max(0.25f, spotAngleCos);
		}

		return score > 0.0f;
	}

	/// <summary>
	/// Inserts a candidate additional light into the compact selected list.
	/// </summary>
	/// <param name="additionalLightIndex"></param>
	/// <param name="anisotropy"></param>
	/// <param name="scattering"></param>
	/// <param name="radiusSq"></param>
	/// <param name="score"></param>
	/// <param name="maxAdditionalLights"></param>
	/// <param name="selectedCount"></param>
	private static void TryInsertSelectedAdditionalLight(int additionalLightIndex, float anisotropy, float scattering, float radiusSq, float score, int maxAdditionalLights, ref int selectedCount)
	{
		if (maxAdditionalLights <= 0)
			return;

		int selectedIndex = -1;
		if (selectedCount < maxAdditionalLights)
		{
			selectedIndex = selectedCount++;
		}
		else
		{
			float minScore = SelectedScores[0];
			int minScoreIndex = 0;
			for (int i = 1; i < selectedCount; ++i)
			{
				if (SelectedScores[i] < minScore)
				{
					minScore = SelectedScores[i];
					minScoreIndex = i;
				}
			}

			if (score <= minScore)
				return;

			selectedIndex = minScoreIndex;
		}

		SelectedAdditionalIndices[selectedIndex] = additionalLightIndex;
		SelectedAnisotropies[selectedIndex] = anisotropy;
		SelectedScatterings[selectedIndex] = scattering;
		SelectedRadiiSq[selectedIndex] = radiusSq;
		SelectedScores[selectedIndex] = score;
	}

	/// <summary>
	/// Computes a hash from selected light parameters to avoid redundant material array uploads.
	/// </summary>
	/// <param name="additionalLightsCount"></param>
	/// <param name="includeMainLight"></param>
	/// <returns></returns>
	private static int ComputeLightsHash(int additionalLightsCount, bool includeMainLight)
	{
		unchecked
		{
			int hash = 17;
			hash = (hash * 31) + additionalLightsCount;

			for (int i = 0; i < additionalLightsCount; ++i)
			{
				hash = (hash * 31) + AdditionalLightIndices[i].GetHashCode();
				hash = (hash * 31) + Anisotropies[i].GetHashCode();
				hash = (hash * 31) + Scatterings[i].GetHashCode();
				hash = (hash * 31) + RadiiSq[i].GetHashCode();
			}

			if (includeMainLight)
			{
				hash = (hash * 31) + Anisotropies[additionalLightsCount].GetHashCode();
				hash = (hash * 31) + Scatterings[additionalLightsCount].GetHashCode();
			}

			return hash;
		}
	}

	/// <summary>
	/// Computes a hash for a vector array.
	/// </summary>
	/// <param name="array"></param>
	/// <param name="count"></param>
	/// <returns></returns>
	private static int ComputeVector4ArrayHash(Vector4[] array, int count)
	{
		unchecked
		{
			int hash = 17;
			for (int i = 0; i < count; ++i)
				hash = (hash * 31) + array[i].GetHashCode();

			return hash;
		}
	}

	/// <summary>
	/// Sets an integer material property only when it changes.
	/// </summary>
	/// <param name="material"></param>
	/// <param name="propertyId"></param>
	/// <param name="value"></param>
	/// <param name="cachedValue"></param>
	private static void SetIntIfChanged(Material material, int propertyId, int value, ref int cachedValue)
	{
		if (!isMaterialStateInitialized || cachedValue != value)
		{
			material.SetInteger(propertyId, value);
			cachedValue = value;
		}
	}

	/// <summary>
	/// Sets a float material property only when it changes.
	/// </summary>
	/// <param name="material"></param>
	/// <param name="propertyId"></param>
	/// <param name="value"></param>
	/// <param name="cachedValue"></param>
	private static void SetFloatIfChanged(Material material, int propertyId, float value, ref float cachedValue)
	{
		if (!isMaterialStateInitialized || Mathf.Abs(cachedValue - value) > 0.0001f)
		{
			material.SetFloat(propertyId, value);
			cachedValue = value;
		}
	}

	/// <summary>
	/// Sets a color material property only when it changes.
	/// </summary>
	/// <param name="material"></param>
	/// <param name="propertyId"></param>
	/// <param name="value"></param>
	/// <param name="cachedValue"></param>
	private static void SetColorIfChanged(Material material, int propertyId, Color value, ref Color cachedValue)
	{
		bool changed = !isMaterialStateInitialized;
		changed |= Mathf.Abs(cachedValue.r - value.r) > 0.0001f;
		changed |= Mathf.Abs(cachedValue.g - value.g) > 0.0001f;
		changed |= Mathf.Abs(cachedValue.b - value.b) > 0.0001f;
		changed |= Mathf.Abs(cachedValue.a - value.a) > 0.0001f;

		if (changed)
		{
			material.SetColor(propertyId, value);
			cachedValue = value;
		}
	}

	/// <summary>
	/// Resets all cached material state for dirty-checking.
	/// </summary>
	private static void ResetMaterialStateCache()
	{
		isMaterialStateInitialized = false;
		cachedMainLightContributionEnabled = false;
		cachedAdditionalLightsContributionEnabled = false;
		cachedSceneViewMainCameraFrustumMaskEnabled = false;
#if UNITY_2023_1_OR_NEWER
		cachedAPVContributionEnabled = false;
#endif
		cachedAdditionalLightsCount = int.MinValue;
		cachedLightsHash = int.MinValue;
		cachedMainCameraFrustumPlanesHash = int.MinValue;
		cachedDistance = float.NaN;
		cachedBaseHeight = float.NaN;
		cachedMaximumHeight = float.NaN;
		cachedGroundHeight = float.NaN;
		cachedDensity = float.NaN;
		cachedAbsortion = float.NaN;
#if UNITY_2023_1_OR_NEWER
		cachedAPVContributionWeight = float.NaN;
#endif
		cachedTint = new Color(float.NaN, float.NaN, float.NaN, float.NaN);
		cachedMaxSteps = int.MinValue;
		cachedTransmittanceThreshold = float.NaN;
	}

#if UNITY_6000_0_OR_NEWER

	/// <summary>
	/// Creates and returns all the necessary render graph textures.
	/// </summary>
	/// <param name="renderGraph"></param>
	/// <param name="cameraData"></param>
	/// <param name="downsampledCameraDepthTarget"></param>
	/// <param name="volumetricFogRenderTarget"></param>
	/// <param name="volumetricFogBlurRenderTarget"></param>
	/// <param name="volumetricFogUpsampleCompositionTarget"></param>
	private void CreateRenderGraphTextures(RenderGraph renderGraph, UniversalCameraData cameraData, VolumetricFogVolumeComponent fogVolume, out TextureHandle downsampledCameraDepthTarget, out TextureHandle volumetricFogRenderTarget, out TextureHandle volumetricFogBlurRenderTarget, out TextureHandle volumetricFogUpsampleCompositionTarget)
	{
		RenderTextureDescriptor cameraTargetDescriptor = cameraData.cameraTargetDescriptor;
		cameraTargetDescriptor.depthBufferBits = (int)DepthBits.None;

		RenderTextureFormat originalColorFormat = cameraTargetDescriptor.colorFormat;
		Vector2Int originalResolution = new Vector2Int(cameraTargetDescriptor.width, cameraTargetDescriptor.height);
		int downsampleFactor = GetDownsampleFactor(fogVolume);

		cameraTargetDescriptor.width = Mathf.Max(1, cameraTargetDescriptor.width / downsampleFactor);
		cameraTargetDescriptor.height = Mathf.Max(1, cameraTargetDescriptor.height / downsampleFactor);
		cameraTargetDescriptor.graphicsFormat = GraphicsFormat.R32_SFloat;
		downsampledCameraDepthTarget = UniversalRenderer.CreateRenderGraphTexture(renderGraph, cameraTargetDescriptor, DownsampledCameraDepthRTName, false);

		cameraTargetDescriptor.colorFormat = RenderTextureFormat.ARGBHalf;
		volumetricFogRenderTarget = UniversalRenderer.CreateRenderGraphTexture(renderGraph, cameraTargetDescriptor, VolumetricFogRenderRTName, false);
		volumetricFogBlurRenderTarget = UniversalRenderer.CreateRenderGraphTexture(renderGraph, cameraTargetDescriptor, VolumetricFogBlurRTName, false);

		cameraTargetDescriptor.width = originalResolution.x;
		cameraTargetDescriptor.height = originalResolution.y;
		cameraTargetDescriptor.colorFormat = originalColorFormat;
		volumetricFogUpsampleCompositionTarget = UniversalRenderer.CreateRenderGraphTexture(renderGraph, cameraTargetDescriptor, VolumetricFogUpsampleCompositionRTName, false);
	}

	/// <summary>
	/// Executes the pass with the information from the pass data.
	/// </summary>
	/// <param name="passData"></param>
	/// <param name="context"></param>
	private static void ExecutePass(PassData passData, RasterGraphContext context)
	{
		PassStage stage = passData.stage;

		if (stage == PassStage.VolumetricFogRender)
		{
			passData.material.SetTexture(DownsampledCameraDepthTextureId, passData.downsampledCameraDepthTarget);
			UpdateVolumetricFogMaterialParameters(passData.material, passData.fogVolume, passData.cameraPosition, passData.lightData.mainLightIndex, passData.lightData.additionalLightsCount, passData.lightData.visibleLights);
		}
		else if (stage == PassStage.VolumetricFogUpsampleComposition)
		{
			passData.material.SetTexture(VolumetricFogTextureId, passData.volumetricFogRenderTarget);
		}

		Blitter.BlitTexture(context.cmd, passData.source, Vector2.one, passData.material, passData.materialPassIndex);
	}

	/// <summary>
	/// Executes the unsafe pass that does up to multiple separable blurs to the volumetric fog.
	/// </summary>
	/// <param name="passData"></param>
	/// <param name="context"></param>
	private static void ExecuteUnsafeBlurPass(PassData passData, UnsafeGraphContext context)
	{
		CommandBuffer unsafeCmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

		int blurIterations = passData.blurIterations;

		for (int i = 0; i < blurIterations; ++i)
		{
			Blitter.BlitCameraTexture(unsafeCmd, passData.source, passData.target, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, passData.material, passData.materialPassIndex);
			Blitter.BlitCameraTexture(unsafeCmd, passData.target, passData.source, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, passData.material, passData.materialAdditionalPassIndex);
		}
	}

#endif

	/// <summary>
	/// Returns the selected downsample factor from the active volume.
	/// </summary>
	/// <returns></returns>
	private static int GetDownsampleFactor(VolumetricFogVolumeComponent fogVolume)
	{
		if (fogVolume == null)
			return (int)VolumetricFogDownsampleMode.Half;

		VolumetricFogDownsampleMode downsampleMode = fogVolume.downsampleMode.value;
		return downsampleMode == VolumetricFogDownsampleMode.Quarter ? (int)VolumetricFogDownsampleMode.Quarter : (int)VolumetricFogDownsampleMode.Half;
	}

	/// <summary>
	/// Re-allocate fixed-size RTHandle if it is not allocated or doesn't match the descriptor.
	/// </summary>
	/// <param name="handle"></param>
	/// <param name="descriptor"></param>
	/// <param name="wrapMode"></param>
	/// <param name="name"></param>
	private void ReAllocateIfNeeded(ref RTHandle handle, in RenderTextureDescriptor descriptor, TextureWrapMode wrapMode, string name)
	{
#if UNITY_6000_0_OR_NEWER
		RenderingUtils.ReAllocateHandleIfNeeded(ref handle, descriptor, wrapMode: wrapMode, name: name);
#else
		RenderingUtils.ReAllocateIfNeeded(ref handle, descriptor, wrapMode: wrapMode, name: name);
#endif
	}

	/// <summary>
	/// Disposes the resources used by this pass.
	/// </summary>
	public void Dispose()
	{
		downsampledCameraDepthRTHandle?.Release();
		volumetricFogRenderRTHandle?.Release();
		volumetricFogBlurRTHandle?.Release();
		volumetricFogUpsampleCompositionRTHandle?.Release();
		ResetMaterialStateCache();
	}

	#endregion
}
