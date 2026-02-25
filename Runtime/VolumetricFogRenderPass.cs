using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Unity.Collections;

#if UNITY_6000_0_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

/// <summary>
/// The volumetric fog render pass.
/// </summary>
public sealed class VolumetricFogRenderPass : ScriptableRenderPass
{
	#region Definitions

	private struct FroxelMeta
	{
		public int offset;
		public int count;
	}

	private struct DebugSolidCubeDraw
	{
		public Vector3 center;
		public float halfExtent;
		public Color color;
	}

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
		public Camera camera;
		public bool debugRestrictToMainCameraFrustum;
		public Vector4[] debugMainCameraFrustumPlanes;
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
	private static readonly int DebugColorId = Shader.PropertyToID("_Color");
	private static readonly int BakedVolumetricFogLightingTextureId = Shader.PropertyToID("_BakedVolumetricFogLightingTex");
	private static readonly int BakedVolumetricFogDirectionTextureId = Shader.PropertyToID("_BakedVolumetricFogDirectionTex");
	private static readonly int BakedStaticVisibilityTextureArrayId = Shader.PropertyToID("_BakedStaticVisibilityTexArray");
	private static readonly int BakedStaticVisibilityTexParamsId = Shader.PropertyToID("_BakedStaticVisibilityTexParams");
	private static readonly int BakedVolumetricFogBoundsMinId = Shader.PropertyToID("_BakedVolumetricFogBoundsMin");
	private static readonly int BakedVolumetricFogBoundsSizeInvId = Shader.PropertyToID("_BakedVolumetricFogBoundsSizeInv");
	private static readonly int BakedVolumetricFogIntensityId = Shader.PropertyToID("_BakedVolumetricFogIntensity");
	private static readonly int BakedStaticLightsCountId = Shader.PropertyToID("_BakedStaticLightsCount");
	private static readonly int BakedStaticLightColorsArrayId = Shader.PropertyToID("_BakedStaticLightColors");
	private static readonly int BakedStaticLightPositionsArrayId = Shader.PropertyToID("_BakedStaticLightPositions");
	private static readonly int BakedStaticLightDirectionsArrayId = Shader.PropertyToID("_BakedStaticLightDirections");
	private static readonly int BakedStaticLightParams0ArrayId = Shader.PropertyToID("_BakedStaticLightParams0");
	private static readonly int BakedStaticLightParams1ArrayId = Shader.PropertyToID("_BakedStaticLightParams1");
	private static readonly int BakedFroxelLightingBufferId = Shader.PropertyToID("_BakedFroxelLightingBuffer");
	private static readonly int BakedFroxelDirectionBufferId = Shader.PropertyToID("_BakedFroxelDirectionBuffer");

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
	private static readonly int FroxelGridDimensionsId = Shader.PropertyToID("_FroxelGridDimensions");
	private static readonly int FroxelNearFarId = Shader.PropertyToID("_FroxelNearFar");
	private static readonly int FroxelMetaBufferId = Shader.PropertyToID("_FroxelMetaBuffer");
	private static readonly int FroxelLightIndicesBufferId = Shader.PropertyToID("_FroxelLightIndicesBuffer");

	private static readonly int AnisotropiesArrayId = Shader.PropertyToID("_Anisotropies");
	private static readonly int ScatteringsArrayId = Shader.PropertyToID("_Scatterings");
	private static readonly int RadiiSqArrayId = Shader.PropertyToID("_RadiiSq");
	private static readonly int AdditionalLightIndicesArrayId = Shader.PropertyToID("_AdditionalLightIndices");

	private const int MaxVisibleAdditionalLights = 256;
	private const int FroxelGridWidth = 16;
	private const int FroxelGridHeight = 9;
	private const int FroxelGridDepth = 24;
	private const int FroxelMaxLightsPerCell = 24;
	private const int FroxelCount = FroxelGridWidth * FroxelGridHeight * FroxelGridDepth;
	private const int MaxBakedStaticLights = 64;
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
	private static readonly Vector3[] SelectedLightPositions = new Vector3[MaxVisibleAdditionalLights];
	private static readonly float[] SelectedLightRanges = new float[MaxVisibleAdditionalLights];
	private static readonly FroxelMeta[] FroxelMetas = new FroxelMeta[FroxelCount];
	private static readonly int[] FroxelLightIndices = new int[FroxelCount * FroxelMaxLightsPerCell];
	private static readonly int[] FroxelCellLightCounts = new int[FroxelCount];
	private static readonly Vector4[] BakedStaticLightColors = new Vector4[MaxBakedStaticLights];
	private static readonly Vector4[] BakedStaticLightPositions = new Vector4[MaxBakedStaticLights];
	private static readonly Vector4[] BakedStaticLightDirections = new Vector4[MaxBakedStaticLights];
	private static readonly Vector4[] BakedStaticLightParams0 = new Vector4[MaxBakedStaticLights];
	private static readonly Vector4[] BakedStaticLightParams1 = new Vector4[MaxBakedStaticLights];

	private static ComputeBuffer froxelMetaBuffer;
	private static ComputeBuffer froxelLightIndicesBuffer;
	private static ComputeBuffer bakedFroxelLightingBuffer;
	private static ComputeBuffer bakedFroxelDirectionBuffer;
	private static readonly Vector4[] BakedFroxelLightingData = new Vector4[FroxelCount];
	private static readonly Vector4[] BakedFroxelDirectionData = new Vector4[FroxelCount];
	private static Color[] bakedLightingPixelsCache;
	private static Color[] bakedDirectionPixelsCache;
	private static Texture3D cachedBakedLightingTexture;
	private static Texture3D cachedBakedDirectionTexture;
	private static Hash128 cachedBakedLightingTextureContentsHash;
	private static Hash128 cachedBakedDirectionTextureContentsHash;
	private static int cachedBakedTextureWidth;
	private static int cachedBakedTextureHeight;
	private static int cachedBakedTextureDepth;

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
	private static bool debugDrawFroxelClusters;
	private static bool debugDrawOnlyOccupiedFroxels = true;
	private static int debugMaxFroxelsToDraw = 256;
	private static bool debugDrawWorldSpaceCubes = true;
	private static float debugWorldSpaceCubeFillOpacity = 0.08f;
	private static Color debugFroxelColor = new Color(0.0f, 1.0f, 1.0f, 1.0f);
	private static Material debugSolidCubeMaterial;
	private static Mesh debugUnitCubeMesh;
	private static MaterialPropertyBlock debugSolidCubePropertyBlock;
	private static readonly DebugSolidCubeDraw[] debugSolidCubeDraws = new DebugSolidCubeDraw[FroxelCount];
	private static int debugSolidCubeDrawCount;

	private static bool isMaterialStateInitialized;
	private static bool cachedMainLightContributionEnabled;
	private static bool cachedAdditionalLightsContributionEnabled;
	private static bool cachedBakedVolumetricLightingEnabled;
	private static bool cachedBakedStaticLightsEnabled;
	private static bool cachedBakedDirectionalPhaseEnabled;
	private static bool cachedBakedFroxelSamplingEnabled;
	private static bool cachedSceneViewMainCameraFrustumMaskEnabled;
	private static bool cachedFroxelClusteredLightsEnabled;
#if UNITY_2023_1_OR_NEWER
	private static bool cachedAPVContributionEnabled;
#endif
	private static int cachedAdditionalLightsCount;
	private static int cachedLightsHash;
	private static int cachedMainCameraFrustumPlanesHash;
	private static int cachedFroxelHash;
	private static int cachedBakedFroxelHash;
	private static int cachedBakedStaticLightsHash;
	private static float cachedDistance;
	private static float cachedBaseHeight;
	private static float cachedMaximumHeight;
	private static float cachedGroundHeight;
	private static float cachedDensity;
	private static float cachedAbsortion;
	private static float cachedBakedVolumetricFogIntensity;
#if UNITY_2023_1_OR_NEWER
	private static float cachedAPVContributionWeight;
#endif
	private static Color cachedTint;
	private static int cachedMaxSteps;
	private static float cachedTransmittanceThreshold;
	private static int cachedBakedVolumetricLightingHash;

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

	/// <summary>
	/// Configures froxel debug line drawing.
	/// </summary>
	/// <param name="enableDebugDrawing"></param>
	/// <param name="drawOnlyOccupiedFroxels"></param>
	/// <param name="maxFroxelsToDraw"></param>
	/// <param name="drawWorldSpaceCubes"></param>
	/// <param name="worldSpaceCubeFillOpacity"></param>
	/// <param name="froxelColor"></param>
	public void SetupFroxelDebugDrawing(bool enableDebugDrawing, bool drawOnlyOccupiedFroxels, int maxFroxelsToDraw, bool drawWorldSpaceCubes, float worldSpaceCubeFillOpacity, Color froxelColor)
	{
		debugDrawFroxelClusters = enableDebugDrawing;
		debugDrawOnlyOccupiedFroxels = drawOnlyOccupiedFroxels;
		debugMaxFroxelsToDraw = Mathf.Max(1, maxFroxelsToDraw);
		debugDrawWorldSpaceCubes = drawWorldSpaceCubes;
		debugWorldSpaceCubeFillOpacity = Mathf.Clamp01(worldSpaceCubeFillOpacity);
		debugFroxelColor = froxelColor;
	}

	/// <summary>
	/// Invalidates cached material state so all fog parameters are pushed again on next execution.
	/// </summary>
	public void InvalidateMaterialStateCache()
	{
		ResetMaterialStateCache();
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
			UpdateVolumetricFogMaterialParameters(volumetricFogMaterial, fogVolume, renderingData.cameraData.camera, cameraPosition, renderingData.lightData.mainLightIndex, renderingData.lightData.additionalLightsCount, renderingData.lightData.visibleLights, sceneViewMainCameraFrustumMaskEnabled, mainCameraFrustumPlanes);
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
			DrawQueuedDebugSolidCubes(cmd, renderingData.cameraData.camera);
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
		Camera sceneCamera = cameraData.camera;
		Vector3 cameraPosition = sceneCamera != null ? sceneCamera.transform.position : Vector3.zero;
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
			passData.camera = sceneCamera;
			passData.debugRestrictToMainCameraFrustum = sceneViewMainCameraFrustumMaskEnabled;
			passData.debugMainCameraFrustumPlanes = mainCameraFrustumPlanes;

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
			passData.camera = sceneCamera;

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
	private static void UpdateVolumetricFogMaterialParameters(Material volumetricFogMaterial, VolumetricFogVolumeComponent fogVolume, Camera camera, Vector3 cameraPosition, int mainLightIndex, int additionalLightsCount, NativeArray<VisibleLight> visibleLights, bool debugRestrictToMainCameraFrustum, Vector4[] debugMainCameraFrustumPlanes)
	{
		if (fogVolume == null)
			return;

		bool useHybridBakedMode = fogVolume.lightingMode.value == VolumetricFogLightingMode.HybridBaked;
		VolumetricFogBakedData bakedLightingData = useHybridBakedMode ? fogVolume.bakedData.value : null;
		float bakedIntensity = fogVolume.bakedIntensity.value;
		bool hasValidBakedLightingData = useHybridBakedMode
			&& bakedLightingData != null
			&& bakedLightingData.IsValid
			&& bakedLightingData.LightingTexture != null
			&& bakedLightingData.BakedLightsCount > 0
			&& bakedIntensity > 0.0f;
		// Fallback path: if full baked volumetric data is unavailable, use static-light runtime
		// evaluation with precomputed visibility volumes.
		bool hasValidBakedStaticLightsData = !hasValidBakedLightingData
			&& useHybridBakedMode
			&& bakedLightingData != null
			&& bakedLightingData.HasStaticLightsData
			&& bakedLightingData.BakedLightsCount > 0
			&& bakedIntensity > 0.0f;
		bool classifyLightsByBakeType = hasValidBakedLightingData || hasValidBakedStaticLightsData;

		if (!isMaterialStateInitialized || cachedBakedVolumetricLightingEnabled != hasValidBakedLightingData)
		{
			if (hasValidBakedLightingData)
				volumetricFogMaterial.EnableKeyword("_BAKED_VOLUMETRIC_FOG_ENABLED");
			else
				volumetricFogMaterial.DisableKeyword("_BAKED_VOLUMETRIC_FOG_ENABLED");

			cachedBakedVolumetricLightingEnabled = hasValidBakedLightingData;
		}

		UploadBakedVolumetricLightingParameters(volumetricFogMaterial, bakedLightingData, hasValidBakedLightingData, bakedIntensity);
		UploadBakedStaticLightsParameters(volumetricFogMaterial, bakedLightingData, hasValidBakedStaticLightsData, bakedIntensity);
		bool enableBakedFroxelSampling = hasValidBakedLightingData && fogVolume.bakedUseFroxelSampling.value;
		int bakedFroxelHash = 0;
		if (enableBakedFroxelSampling)
			enableBakedFroxelSampling = TryConfigureBakedFroxelSampling(volumetricFogMaterial, camera, bakedLightingData, debugRestrictToMainCameraFrustum, debugMainCameraFrustumPlanes, out bakedFroxelHash);

		if (!isMaterialStateInitialized || cachedBakedFroxelSamplingEnabled != enableBakedFroxelSampling)
		{
			if (enableBakedFroxelSampling)
				volumetricFogMaterial.EnableKeyword("_BAKED_VOLUMETRIC_FROXEL_SAMPLING");
			else
				volumetricFogMaterial.DisableKeyword("_BAKED_VOLUMETRIC_FROXEL_SAMPLING");

			cachedBakedFroxelSamplingEnabled = enableBakedFroxelSampling;
		}

		cachedBakedFroxelHash = bakedFroxelHash;

		bool enableMainLightContribution = fogVolume.enableMainLightContribution.value && fogVolume.scattering.value > 0.0f && mainLightIndex > -1;
		if (enableMainLightContribution && classifyLightsByBakeType)
			enableMainLightContribution = ShouldEvaluateLightInRuntime(mainLightIndex, visibleLights);

		bool enableAdditionalLightsContribution = fogVolume.enableAdditionalLightsContribution.value && additionalLightsCount > 0 && fogVolume.maxAdditionalLights.value > 0;

		int lightsHash = 0;
		int effectiveAdditionalLightsCount = 0;
		if (enableMainLightContribution || enableAdditionalLightsContribution)
			effectiveAdditionalLightsCount = UpdateLightsParameters(fogVolume, enableMainLightContribution, enableAdditionalLightsContribution, classifyLightsByBakeType, mainLightIndex, additionalLightsCount, visibleLights, cameraPosition, out lightsHash);

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

		bool enableFroxelClusteredLights = enableAdditionalLightsContribution && effectiveAdditionalLightsCount > 0;
		int froxelHash = 0;
		if (enableFroxelClusteredLights)
			enableFroxelClusteredLights = TryConfigureFroxelClusteredLights(volumetricFogMaterial, camera, effectiveAdditionalLightsCount, debugRestrictToMainCameraFrustum, debugMainCameraFrustumPlanes, out froxelHash);

		if (!isMaterialStateInitialized || cachedFroxelClusteredLightsEnabled != enableFroxelClusteredLights)
		{
			if (enableFroxelClusteredLights)
				volumetricFogMaterial.EnableKeyword("_FROXEL_CLUSTERED_ADDITIONAL_LIGHTS");
			else
				volumetricFogMaterial.DisableKeyword("_FROXEL_CLUSTERED_ADDITIONAL_LIGHTS");

			cachedFroxelClusteredLightsEnabled = enableFroxelClusteredLights;
		}

		cachedFroxelHash = froxelHash;

		isMaterialStateInitialized = true;
	}

	/// <summary>
	/// Uploads baked volumetric lighting parameters to the fog material.
	/// </summary>
	/// <param name="material"></param>
	/// <param name="bakedLightingData"></param>
	/// <param name="enabled"></param>
	/// <param name="intensity"></param>
	private static void UploadBakedVolumetricLightingParameters(Material material, VolumetricFogBakedData bakedLightingData, bool enabled, float intensity)
	{
		int dataHash = 0;
		float safeIntensity = enabled ? Mathf.Max(0.0f, intensity) : 0.0f;
		bool hasDirectionalTexture = enabled && bakedLightingData.DirectionTexture != null;
		if (!isMaterialStateInitialized || cachedBakedDirectionalPhaseEnabled != hasDirectionalTexture)
		{
			if (hasDirectionalTexture)
				material.EnableKeyword("_BAKED_VOLUMETRIC_DIRECTIONAL_PHASE");
			else
				material.DisableKeyword("_BAKED_VOLUMETRIC_DIRECTIONAL_PHASE");

			cachedBakedDirectionalPhaseEnabled = hasDirectionalTexture;
		}

		if (enabled)
		{
			Vector3 boundsSize = bakedLightingData.BoundsSize;
			Vector3 boundsCenter = bakedLightingData.BoundsCenter;
			Vector3 boundsMin = boundsCenter - (boundsSize * 0.5f);
			Vector3 boundsSizeInv = new Vector3(1.0f / boundsSize.x, 1.0f / boundsSize.y, 1.0f / boundsSize.z);
			dataHash = ComputeBakedLightingHash(bakedLightingData, safeIntensity, boundsMin, boundsSizeInv);

			if (!isMaterialStateInitialized || dataHash != cachedBakedVolumetricLightingHash)
			{
				material.SetTexture(BakedVolumetricFogLightingTextureId, bakedLightingData.LightingTexture);
				material.SetTexture(BakedVolumetricFogDirectionTextureId, bakedLightingData.DirectionTexture);
				material.SetVector(BakedVolumetricFogBoundsMinId, new Vector4(boundsMin.x, boundsMin.y, boundsMin.z, 0.0f));
				material.SetVector(BakedVolumetricFogBoundsSizeInvId, new Vector4(boundsSizeInv.x, boundsSizeInv.y, boundsSizeInv.z, 0.0f));
				cachedBakedVolumetricLightingHash = dataHash;
			}
		}
		else if (!isMaterialStateInitialized || cachedBakedVolumetricLightingHash != 0)
		{
			material.SetTexture(BakedVolumetricFogLightingTextureId, null);
			material.SetTexture(BakedVolumetricFogDirectionTextureId, null);
			material.SetVector(BakedVolumetricFogBoundsMinId, Vector4.zero);
			material.SetVector(BakedVolumetricFogBoundsSizeInvId, Vector4.zero);
			cachedBakedVolumetricLightingHash = 0;
		}

		SetFloatIfChanged(material, BakedVolumetricFogIntensityId, safeIntensity, ref cachedBakedVolumetricFogIntensity);
	}

	/// <summary>
	/// Uploads baked static lights data (with precomputed visibility volumes) for runtime camera-dependent evaluation.
	/// </summary>
	/// <param name="material"></param>
	/// <param name="bakedLightingData"></param>
	/// <param name="enabled"></param>
	/// <param name="intensity"></param>
	private static void UploadBakedStaticLightsParameters(Material material, VolumetricFogBakedData bakedLightingData, bool enabled, float intensity)
	{
		if (!enabled || bakedLightingData == null || bakedLightingData.StaticVisibilityTextureArray == null || bakedLightingData.StaticLights == null || bakedLightingData.StaticLights.Length == 0)
		{
			if (!isMaterialStateInitialized || cachedBakedStaticLightsEnabled)
			{
				material.DisableKeyword("_BAKED_STATIC_LIGHTS_RUNTIME_EVAL");
				material.SetInteger(BakedStaticLightsCountId, 0);
				material.SetTexture(BakedStaticVisibilityTextureArrayId, null);
				material.SetVector(BakedStaticVisibilityTexParamsId, Vector4.zero);
				cachedBakedStaticLightsEnabled = false;
				cachedBakedStaticLightsHash = int.MinValue;
			}

			return;
		}

		Texture2DArray visibilityTextureArray = bakedLightingData.StaticVisibilityTextureArray;
		VolumetricFogBakedStaticLightData[] staticLights = bakedLightingData.StaticLights;
		int slicesPerLight = Mathf.Max(1, bakedLightingData.StaticVisibilityResolutionZ);
		int textureLightCapacity = visibilityTextureArray.depth / slicesPerLight;
		int staticLightsCount = Mathf.Min(staticLights.Length, MaxBakedStaticLights, textureLightCapacity);
		float safeIntensity = Mathf.Max(0.0f, intensity);

		if (staticLightsCount <= 0)
		{
			if (!isMaterialStateInitialized || cachedBakedStaticLightsEnabled)
			{
				material.DisableKeyword("_BAKED_STATIC_LIGHTS_RUNTIME_EVAL");
				material.SetInteger(BakedStaticLightsCountId, 0);
				material.SetTexture(BakedStaticVisibilityTextureArrayId, null);
				material.SetVector(BakedStaticVisibilityTexParamsId, Vector4.zero);
				cachedBakedStaticLightsEnabled = false;
				cachedBakedStaticLightsHash = int.MinValue;
			}

			return;
		}

		int hash = ComputeBakedStaticLightsHash(bakedLightingData, staticLightsCount, safeIntensity);
		if (!isMaterialStateInitialized || !cachedBakedStaticLightsEnabled)
		{
			material.EnableKeyword("_BAKED_STATIC_LIGHTS_RUNTIME_EVAL");
			cachedBakedStaticLightsEnabled = true;
		}

		SetFloatIfChanged(material, BakedVolumetricFogIntensityId, safeIntensity, ref cachedBakedVolumetricFogIntensity);

		if (!isMaterialStateInitialized || hash != cachedBakedStaticLightsHash)
		{
			Vector3 boundsSize = bakedLightingData.BoundsSize;
			Vector3 boundsCenter = bakedLightingData.BoundsCenter;
			Vector3 boundsMin = boundsCenter - (boundsSize * 0.5f);
			Vector3 boundsSizeInv = new Vector3(1.0f / boundsSize.x, 1.0f / boundsSize.y, 1.0f / boundsSize.z);

			for (int i = 0; i < staticLightsCount; ++i)
			{
				VolumetricFogBakedStaticLightData lightData = staticLights[i];
				Vector4 attenuation = lightData.attenuation;
				if (attenuation == Vector4.zero)
				{
					float invRangeSq = lightData.invRangeSq > 0.0f ? lightData.invRangeSq : (lightData.rangeSq > 0.0001f ? 1.0f / lightData.rangeSq : 0.0f);
					attenuation = new Vector4(invRangeSq, 0.0f, 0.0f, 1.0f);
					if (lightData.lightType == (int)LightType.Spot)
					{
						attenuation.z = lightData.spotScale;
						attenuation.w = lightData.spotOffset;
					}
				}

				BakedStaticLightColors[i] = new Vector4(lightData.color.x, lightData.color.y, lightData.color.z, 1.0f);
				BakedStaticLightPositions[i] = new Vector4(lightData.position.x, lightData.position.y, lightData.position.z, lightData.rangeSq);
				BakedStaticLightDirections[i] = new Vector4(lightData.direction.x, lightData.direction.y, lightData.direction.z, 0.0f);
				BakedStaticLightParams0[i] = attenuation;
				BakedStaticLightParams1[i] = new Vector4(lightData.radiusSq, lightData.scattering, lightData.anisotropy, lightData.lightType);
			}

			material.SetInteger(BakedStaticLightsCountId, staticLightsCount);
			material.SetTexture(BakedStaticVisibilityTextureArrayId, visibilityTextureArray);
			material.SetVector(BakedStaticVisibilityTexParamsId, new Vector4(slicesPerLight, slicesPerLight > 1 ? 1.0f / (slicesPerLight - 1.0f) : 0.0f, 0.0f, 0.0f));
			material.SetVectorArray(BakedStaticLightColorsArrayId, BakedStaticLightColors);
			material.SetVectorArray(BakedStaticLightPositionsArrayId, BakedStaticLightPositions);
			material.SetVectorArray(BakedStaticLightDirectionsArrayId, BakedStaticLightDirections);
			material.SetVectorArray(BakedStaticLightParams0ArrayId, BakedStaticLightParams0);
			material.SetVectorArray(BakedStaticLightParams1ArrayId, BakedStaticLightParams1);
			material.SetVector(BakedVolumetricFogBoundsMinId, new Vector4(boundsMin.x, boundsMin.y, boundsMin.z, 0.0f));
			material.SetVector(BakedVolumetricFogBoundsSizeInvId, new Vector4(boundsSizeInv.x, boundsSizeInv.y, boundsSizeInv.z, 0.0f));
			cachedBakedStaticLightsHash = hash;
		}
	}

	/// <summary>
	/// Computes a hash of baked static lights bindings.
	/// </summary>
	/// <param name="bakedLightingData"></param>
	/// <param name="staticLightsCount"></param>
	/// <param name="intensity"></param>
	/// <returns></returns>
	private static int ComputeBakedStaticLightsHash(VolumetricFogBakedData bakedLightingData, int staticLightsCount, float intensity)
	{
		unchecked
		{
			int hash = 17;
			hash = (hash * 31) + staticLightsCount;
			hash = (hash * 31) + Mathf.Max(0.0f, intensity).GetHashCode();
			hash = (hash * 31) + (bakedLightingData != null ? bakedLightingData.GetHashCode() : 0);
			hash = (hash * 31) + (bakedLightingData != null && bakedLightingData.StaticVisibilityTextureArray != null ? bakedLightingData.StaticVisibilityTextureArray.GetHashCode() : 0);
			hash = (hash * 31) + (bakedLightingData != null && bakedLightingData.StaticVisibilityTextureArray != null ? bakedLightingData.StaticVisibilityTextureArray.imageContentsHash.GetHashCode() : 0);
			hash = (hash * 31) + (bakedLightingData != null ? bakedLightingData.StaticVisibilityResolutionX : 0);
			hash = (hash * 31) + (bakedLightingData != null ? bakedLightingData.StaticVisibilityResolutionY : 0);
			hash = (hash * 31) + (bakedLightingData != null ? bakedLightingData.StaticVisibilityResolutionZ : 0);
			hash = (hash * 31) + (bakedLightingData != null && bakedLightingData.StaticVisibilityTextureArray != null ? bakedLightingData.StaticVisibilityTextureArray.depth : 0);
			hash = (hash * 31) + (bakedLightingData != null ? bakedLightingData.BoundsCenter.GetHashCode() : 0);
			hash = (hash * 31) + (bakedLightingData != null ? bakedLightingData.BoundsSize.GetHashCode() : 0);
			if (bakedLightingData != null && bakedLightingData.StaticLights != null)
			{
				VolumetricFogBakedStaticLightData[] staticLights = bakedLightingData.StaticLights;
				for (int i = 0; i < staticLightsCount && i < staticLights.Length; ++i)
				{
					VolumetricFogBakedStaticLightData lightData = staticLights[i];
					hash = (hash * 31) + lightData.lightType;
					hash = (hash * 31) + lightData.color.GetHashCode();
					hash = (hash * 31) + lightData.position.GetHashCode();
					hash = (hash * 31) + lightData.direction.GetHashCode();
					hash = (hash * 31) + lightData.attenuation.GetHashCode();
					hash = (hash * 31) + lightData.rangeSq.GetHashCode();
					hash = (hash * 31) + lightData.invRangeSq.GetHashCode();
					hash = (hash * 31) + lightData.radiusSq.GetHashCode();
					hash = (hash * 31) + lightData.scattering.GetHashCode();
					hash = (hash * 31) + lightData.anisotropy.GetHashCode();
					hash = (hash * 31) + lightData.spotScale.GetHashCode();
					hash = (hash * 31) + lightData.spotOffset.GetHashCode();
				}
			}
			return hash;
		}
	}

	/// <summary>
	/// Computes a hash of baked volumetric lighting bindings.
	/// </summary>
	/// <param name="bakedLightingData"></param>
	/// <param name="intensity"></param>
	/// <param name="boundsMin"></param>
	/// <param name="boundsSizeInv"></param>
	/// <returns></returns>
	private static int ComputeBakedLightingHash(VolumetricFogBakedData bakedLightingData, float intensity, Vector3 boundsMin, Vector3 boundsSizeInv)
	{
		unchecked
		{
			int hash = 17;
			hash = (hash * 31) + (bakedLightingData != null ? bakedLightingData.GetHashCode() : 0);
			hash = (hash * 31) + (bakedLightingData != null && bakedLightingData.LightingTexture != null ? bakedLightingData.LightingTexture.GetHashCode() : 0);
			hash = (hash * 31) + (bakedLightingData != null && bakedLightingData.DirectionTexture != null ? bakedLightingData.DirectionTexture.GetHashCode() : 0);
			hash = (hash * 31) + (bakedLightingData != null ? bakedLightingData.BakedLightsCount : 0);
			hash = (hash * 31) + intensity.GetHashCode();
			hash = (hash * 31) + boundsMin.GetHashCode();
			hash = (hash * 31) + boundsSizeInv.GetHashCode();
			return hash;
		}
	}

	/// <summary>
	/// Configures baked froxel buffers from baked voxel textures for camera-view sampling.
	/// </summary>
	/// <param name="material"></param>
	/// <param name="camera"></param>
	/// <param name="bakedData"></param>
	/// <param name="restrictToMainCameraFrustum"></param>
	/// <param name="mainCameraFrustumPlanes"></param>
	/// <param name="bakedFroxelHash"></param>
	/// <returns></returns>
	private static bool TryConfigureBakedFroxelSampling(Material material, Camera camera, VolumetricFogBakedData bakedData, bool restrictToMainCameraFrustum, Vector4[] mainCameraFrustumPlanes, out int bakedFroxelHash)
	{
		bakedFroxelHash = 0;
		if (camera == null || bakedData == null || bakedData.LightingTexture == null || SystemInfo.graphicsShaderLevel < 45)
			return false;

		if (!TryEnsureBakedTextureCaches(bakedData))
			return false;

		EnsureBakedFroxelBuffersAllocated();
		if (bakedFroxelLightingBuffer == null || bakedFroxelDirectionBuffer == null)
			return false;

		bakedFroxelHash = ComputeBakedFroxelInputHash(camera, bakedData, restrictToMainCameraFrustum, mainCameraFrustumPlanes);
		if (!isMaterialStateInitialized || bakedFroxelHash != cachedBakedFroxelHash)
		{
			BuildBakedFroxelData(camera, bakedData, restrictToMainCameraFrustum, mainCameraFrustumPlanes);
			bakedFroxelLightingBuffer.SetData(BakedFroxelLightingData);
			bakedFroxelDirectionBuffer.SetData(BakedFroxelDirectionData);
		}

		material.SetBuffer(BakedFroxelLightingBufferId, bakedFroxelLightingBuffer);
		material.SetBuffer(BakedFroxelDirectionBufferId, bakedFroxelDirectionBuffer);
		material.SetVector(FroxelGridDimensionsId, new Vector4(FroxelGridWidth, FroxelGridHeight, FroxelGridDepth, FroxelMaxLightsPerCell));
		material.SetVector(FroxelNearFarId, new Vector4(camera.nearClipPlane, camera.farClipPlane, 0.0f, 0.0f));
		return true;
	}

	/// <summary>
	/// Ensures CPU-side baked voxel texture caches are available.
	/// </summary>
	/// <param name="bakedData"></param>
	/// <returns></returns>
	private static bool TryEnsureBakedTextureCaches(VolumetricFogBakedData bakedData)
	{
		Texture3D lightingTexture = bakedData.LightingTexture;
		Texture3D directionTexture = bakedData.DirectionTexture;
		if (lightingTexture == null)
			return false;

		Hash128 lightingContentsHash = lightingTexture.imageContentsHash;
		bool lightingTextureChanged = cachedBakedLightingTexture != lightingTexture
			|| cachedBakedLightingTextureContentsHash != lightingContentsHash
			|| bakedLightingPixelsCache == null
			|| cachedBakedTextureWidth != lightingTexture.width
			|| cachedBakedTextureHeight != lightingTexture.height
			|| cachedBakedTextureDepth != lightingTexture.depth;

		if (lightingTextureChanged)
		{
			try
			{
				bakedLightingPixelsCache = lightingTexture.GetPixels();
				cachedBakedLightingTexture = lightingTexture;
				cachedBakedLightingTextureContentsHash = lightingContentsHash;
				cachedBakedTextureWidth = lightingTexture.width;
				cachedBakedTextureHeight = lightingTexture.height;
				cachedBakedTextureDepth = lightingTexture.depth;
			}
			catch (Exception)
			{
				bakedLightingPixelsCache = null;
				cachedBakedLightingTexture = null;
				cachedBakedLightingTextureContentsHash = default;
				return false;
			}
		}

		bool hasDirectionTexture = directionTexture != null
			&& directionTexture.width == cachedBakedTextureWidth
			&& directionTexture.height == cachedBakedTextureHeight
			&& directionTexture.depth == cachedBakedTextureDepth;

		if (!hasDirectionTexture)
		{
			bakedDirectionPixelsCache = null;
			cachedBakedDirectionTexture = null;
			cachedBakedDirectionTextureContentsHash = default;
			return bakedLightingPixelsCache != null;
		}

		Hash128 directionContentsHash = directionTexture.imageContentsHash;
		bool directionTextureChanged = cachedBakedDirectionTexture != directionTexture
			|| cachedBakedDirectionTextureContentsHash != directionContentsHash
			|| bakedDirectionPixelsCache == null;
		if (directionTextureChanged)
		{
			try
			{
				bakedDirectionPixelsCache = directionTexture.GetPixels();
				cachedBakedDirectionTexture = directionTexture;
				cachedBakedDirectionTextureContentsHash = directionContentsHash;
			}
			catch (Exception)
			{
				bakedDirectionPixelsCache = null;
				cachedBakedDirectionTexture = null;
				cachedBakedDirectionTextureContentsHash = default;
			}
		}

		return bakedLightingPixelsCache != null;
	}

	/// <summary>
	/// Ensures baked froxel buffers are allocated.
	/// </summary>
	private static void EnsureBakedFroxelBuffersAllocated()
	{
		if (bakedFroxelLightingBuffer == null || bakedFroxelLightingBuffer.count != FroxelCount || bakedFroxelLightingBuffer.stride != sizeof(float) * 4)
		{
			bakedFroxelLightingBuffer?.Release();
			bakedFroxelLightingBuffer = new ComputeBuffer(FroxelCount, sizeof(float) * 4, ComputeBufferType.Structured);
		}

		if (bakedFroxelDirectionBuffer == null || bakedFroxelDirectionBuffer.count != FroxelCount || bakedFroxelDirectionBuffer.stride != sizeof(float) * 4)
		{
			bakedFroxelDirectionBuffer?.Release();
			bakedFroxelDirectionBuffer = new ComputeBuffer(FroxelCount, sizeof(float) * 4, ComputeBufferType.Structured);
		}
	}

	/// <summary>
	/// Builds camera-view froxel lighting data from baked voxel textures.
	/// </summary>
	/// <param name="camera"></param>
	/// <param name="bakedData"></param>
	/// <param name="restrictToMainCameraFrustum"></param>
	/// <param name="mainCameraFrustumPlanes"></param>
	private static void BuildBakedFroxelData(Camera camera, VolumetricFogBakedData bakedData, bool restrictToMainCameraFrustum, Vector4[] mainCameraFrustumPlanes)
	{
		if (camera == null || bakedData == null || bakedLightingPixelsCache == null)
		{
			Array.Clear(BakedFroxelLightingData, 0, BakedFroxelLightingData.Length);
			Array.Clear(BakedFroxelDirectionData, 0, BakedFroxelDirectionData.Length);
			return;
		}

		float near = Mathf.Max(camera.nearClipPlane, 0.01f);
		float far = Mathf.Max(camera.farClipPlane, near + 0.01f);
		Vector3 boundsSize = bakedData.BoundsSize;
		Vector3 boundsMin = bakedData.BoundsCenter - (boundsSize * 0.5f);
		Vector3 boundsSizeInv = new Vector3(1.0f / boundsSize.x, 1.0f / boundsSize.y, 1.0f / boundsSize.z);

		int index = 0;
		for (int z = 0; z < FroxelGridDepth; ++z)
		{
			float depth = FroxelSliceToDepth01((z + 0.5f) / FroxelGridDepth, near, far);
			for (int y = 0; y < FroxelGridHeight; ++y)
			{
				float v = (y + 0.5f) / FroxelGridHeight;
				for (int x = 0; x < FroxelGridWidth; ++x, ++index)
				{
					float u = (x + 0.5f) / FroxelGridWidth;
					Vector3 positionWS = camera.ViewportToWorldPoint(new Vector3(u, v, depth));

					if (restrictToMainCameraFrustum && !IsPositionInsideFrustumPlanes(positionWS, mainCameraFrustumPlanes))
					{
						BakedFroxelLightingData[index] = Vector4.zero;
						BakedFroxelDirectionData[index] = new Vector4(0.0f, 0.0f, 1.0f, 0.0f);
						continue;
					}

					Vector3 uvw = (positionWS - boundsMin);
					uvw.x *= boundsSizeInv.x;
					uvw.y *= boundsSizeInv.y;
					uvw.z *= boundsSizeInv.z;

					if (uvw.x < 0.0f || uvw.x > 1.0f || uvw.y < 0.0f || uvw.y > 1.0f || uvw.z < 0.0f || uvw.z > 1.0f)
					{
						BakedFroxelLightingData[index] = Vector4.zero;
						BakedFroxelDirectionData[index] = new Vector4(0.0f, 0.0f, 1.0f, 0.0f);
						continue;
					}

					Color lighting = SampleBakedTextureTrilinear(bakedLightingPixelsCache, cachedBakedTextureWidth, cachedBakedTextureHeight, cachedBakedTextureDepth, uvw);
					Color directionEncoded = bakedDirectionPixelsCache != null
						? SampleBakedTextureTrilinear(bakedDirectionPixelsCache, cachedBakedTextureWidth, cachedBakedTextureHeight, cachedBakedTextureDepth, uvw)
						: new Color(0.5f, 0.5f, 1.0f, 1.0f);

					float anisotropy = Mathf.Clamp(lighting.a * 2.0f - 1.0f, -0.99f, 0.99f);
					Vector3 direction = new Vector3(directionEncoded.r * 2.0f - 1.0f, directionEncoded.g * 2.0f - 1.0f, directionEncoded.b * 2.0f - 1.0f);
					if (direction.sqrMagnitude > 0.000001f)
						direction.Normalize();
					else
						direction = Vector3.forward;

					BakedFroxelLightingData[index] = new Vector4(lighting.r, lighting.g, lighting.b, anisotropy);
					BakedFroxelDirectionData[index] = new Vector4(direction.x, direction.y, direction.z, 0.0f);
				}
			}
		}
	}

	/// <summary>
	/// Samples a 3D texture represented by CPU pixel data using trilinear filtering.
	/// </summary>
	/// <param name="pixels"></param>
	/// <param name="width"></param>
	/// <param name="height"></param>
	/// <param name="depth"></param>
	/// <param name="uvw"></param>
	/// <returns></returns>
	private static Color SampleBakedTextureTrilinear(Color[] pixels, int width, int height, int depth, Vector3 uvw)
	{
		if (pixels == null || pixels.Length == 0 || width <= 0 || height <= 0 || depth <= 0)
			return Color.clear;

		float x = Mathf.Clamp01(uvw.x) * (width - 1);
		float y = Mathf.Clamp01(uvw.y) * (height - 1);
		float z = Mathf.Clamp01(uvw.z) * (depth - 1);

		int x0 = Mathf.FloorToInt(x);
		int y0 = Mathf.FloorToInt(y);
		int z0 = Mathf.FloorToInt(z);
		int x1 = Mathf.Min(x0 + 1, width - 1);
		int y1 = Mathf.Min(y0 + 1, height - 1);
		int z1 = Mathf.Min(z0 + 1, depth - 1);

		float tx = x - x0;
		float ty = y - y0;
		float tz = z - z0;

		Color c000 = GetPixel3D(pixels, width, height, x0, y0, z0);
		Color c100 = GetPixel3D(pixels, width, height, x1, y0, z0);
		Color c010 = GetPixel3D(pixels, width, height, x0, y1, z0);
		Color c110 = GetPixel3D(pixels, width, height, x1, y1, z0);
		Color c001 = GetPixel3D(pixels, width, height, x0, y0, z1);
		Color c101 = GetPixel3D(pixels, width, height, x1, y0, z1);
		Color c011 = GetPixel3D(pixels, width, height, x0, y1, z1);
		Color c111 = GetPixel3D(pixels, width, height, x1, y1, z1);

		Color c00 = Color.LerpUnclamped(c000, c100, tx);
		Color c10 = Color.LerpUnclamped(c010, c110, tx);
		Color c01 = Color.LerpUnclamped(c001, c101, tx);
		Color c11 = Color.LerpUnclamped(c011, c111, tx);
		Color c0 = Color.LerpUnclamped(c00, c10, ty);
		Color c1 = Color.LerpUnclamped(c01, c11, ty);
		return Color.LerpUnclamped(c0, c1, tz);
	}

	/// <summary>
	/// Gets a texel from CPU-side 3D texture pixels.
	/// </summary>
	/// <param name="pixels"></param>
	/// <param name="width"></param>
	/// <param name="height"></param>
	/// <param name="x"></param>
	/// <param name="y"></param>
	/// <param name="z"></param>
	/// <returns></returns>
	private static Color GetPixel3D(Color[] pixels, int width, int height, int x, int y, int z)
	{
		int index = x + y * width + z * width * height;
		if (index < 0 || index >= pixels.Length)
			return Color.clear;

		return pixels[index];
	}

	/// <summary>
	/// Computes the depth at a normalized logarithmic froxel slice coordinate.
	/// </summary>
	/// <param name="t"></param>
	/// <param name="near"></param>
	/// <param name="far"></param>
	/// <returns></returns>
	private static float FroxelSliceToDepth01(float t, float near, float far)
	{
		float clampedT = Mathf.Clamp01(t);
		return near * Mathf.Exp(Mathf.Log(far / near) * clampedT);
	}

	/// <summary>
	/// Computes baked froxel cache hash.
	/// </summary>
	/// <param name="camera"></param>
	/// <param name="bakedData"></param>
	/// <param name="restrictToMainCameraFrustum"></param>
	/// <param name="mainCameraFrustumPlanes"></param>
	/// <returns></returns>
	private static int ComputeBakedFroxelInputHash(Camera camera, VolumetricFogBakedData bakedData, bool restrictToMainCameraFrustum, Vector4[] mainCameraFrustumPlanes)
	{
		unchecked
		{
			int hash = 17;
			hash = (hash * 31) + (camera != null ? camera.transform.position.GetHashCode() : 0);
			hash = (hash * 31) + (camera != null ? camera.transform.rotation.GetHashCode() : 0);
			hash = (hash * 31) + (camera != null ? camera.fieldOfView.GetHashCode() : 0);
			hash = (hash * 31) + (camera != null ? camera.aspect.GetHashCode() : 0);
			hash = (hash * 31) + (camera != null ? camera.nearClipPlane.GetHashCode() : 0);
			hash = (hash * 31) + (camera != null ? camera.farClipPlane.GetHashCode() : 0);
			hash = (hash * 31) + (camera != null ? camera.orthographicSize.GetHashCode() : 0);
			hash = (hash * 31) + (camera != null && camera.orthographic ? 1 : 0);
			hash = (hash * 31) + (camera != null ? camera.pixelRect.width.GetHashCode() : 0);
			hash = (hash * 31) + (camera != null ? camera.pixelRect.height.GetHashCode() : 0);
			hash = (hash * 31) + (restrictToMainCameraFrustum ? 1 : 0);
			if (restrictToMainCameraFrustum && mainCameraFrustumPlanes != null)
				hash = (hash * 31) + ComputeVector4ArrayHash(mainCameraFrustumPlanes, mainCameraFrustumPlanes.Length);

			hash = (hash * 31) + (bakedData != null && bakedData.LightingTexture != null ? bakedData.LightingTexture.GetHashCode() : 0);
			hash = (hash * 31) + (bakedData != null && bakedData.DirectionTexture != null ? bakedData.DirectionTexture.GetHashCode() : 0);
			hash = (hash * 31) + (bakedData != null && bakedData.LightingTexture != null ? bakedData.LightingTexture.imageContentsHash.GetHashCode() : 0);
			hash = (hash * 31) + (bakedData != null && bakedData.DirectionTexture != null ? bakedData.DirectionTexture.imageContentsHash.GetHashCode() : 0);
			hash = (hash * 31) + (bakedData != null ? bakedData.BoundsCenter.GetHashCode() : 0);
			hash = (hash * 31) + (bakedData != null ? bakedData.BoundsSize.GetHashCode() : 0);
			return hash;
		}
	}

	/// <summary>
	/// Returns whether a visible light should still be evaluated in runtime fog shading.
	/// </summary>
	/// <param name="visibleLightIndex"></param>
	/// <param name="visibleLights"></param>
	/// <returns></returns>
	private static bool ShouldEvaluateLightInRuntime(int visibleLightIndex, NativeArray<VisibleLight> visibleLights)
	{
		if (visibleLightIndex < 0 || visibleLightIndex >= visibleLights.Length)
			return true;

		Light light = visibleLights[visibleLightIndex].light;
		if (light == null)
			return true;

		return !IsLightConfiguredAsBaked(light);
	}

	/// <summary>
	/// Returns whether a light is configured as baked (using both configured and runtime output bake type).
	/// </summary>
	/// <param name="light"></param>
	/// <returns></returns>
	private static bool IsLightConfiguredAsBaked(Light light)
	{
		if (light == null)
			return false;

		LightBakingOutput bakingOutput = light.bakingOutput;
		LightmapBakeType bakingOutputType = bakingOutput.lightmapBakeType;
#pragma warning disable 0618
		LightmapBakeType configuredType = light.lightmapBakeType;
#pragma warning restore 0618
		return bakingOutput.isBaked || bakingOutputType == LightmapBakeType.Baked || configuredType == LightmapBakeType.Baked;
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
	private static int UpdateLightsParameters(VolumetricFogVolumeComponent fogVolume, bool enableMainLightContribution, bool enableAdditionalLightsContribution, bool classifyLightsByBakeType, int mainLightIndex, int additionalLightsCount, NativeArray<VisibleLight> visibleLights, Vector3 cameraPosition, out int lightsHash)
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

				if (!TryGetAdditionalLightCandidate(visibleLight, classifyLightsByBakeType, cameraPosition, fogDistance, fogMinHeight, fogMaxHeight, out float anisotropy, out float scattering, out float radiusSq, out float score, out Vector3 lightPosition, out float lightRange))
					continue;

				TryInsertSelectedAdditionalLight(additionalLightIndex, anisotropy, scattering, radiusSq, score, lightPosition, lightRange, maxAdditionalLights, ref effectiveAdditionalLightsCount);
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
	private static bool TryGetAdditionalLightCandidate(in VisibleLight visibleLight, bool classifyLightsByBakeType, in Vector3 cameraPosition, float fogDistance, float fogMinHeight, float fogMaxHeight, out float anisotropy, out float scattering, out float radiusSq, out float score, out Vector3 lightPosition, out float lightRange)
	{
		anisotropy = 0.0f;
		scattering = 0.0f;
		radiusSq = 0.0f;
		score = 0.0f;
		lightPosition = Vector3.zero;
		lightRange = 0.0f;

		Light light = visibleLight.light;
		if (light == null || !light.enabled || !light.gameObject.activeInHierarchy)
			return false;

		if (classifyLightsByBakeType && IsLightConfiguredAsBaked(light))
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
		Vector3 lightWorldPosition = light.transform.position;
		float maxDistance = fogDistance + range;
		float distanceSq = (lightWorldPosition - cameraPosition).sqrMagnitude;
		if (distanceSq > maxDistance * maxDistance)
			return false;

		float lightMinY = lightWorldPosition.y - range;
		float lightMaxY = lightWorldPosition.y + range;
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

		lightPosition = lightWorldPosition;
		lightRange = range;

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
	private static void TryInsertSelectedAdditionalLight(int additionalLightIndex, float anisotropy, float scattering, float radiusSq, float score, Vector3 lightPosition, float lightRange, int maxAdditionalLights, ref int selectedCount)
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
		SelectedLightPositions[selectedIndex] = lightPosition;
		SelectedLightRanges[selectedIndex] = lightRange;
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
	/// Configures clustered froxel buffers and bindings for additional lights.
	/// </summary>
	/// <param name="material"></param>
	/// <param name="camera"></param>
	/// <param name="selectedAdditionalLightsCount"></param>
	/// <param name="froxelHash"></param>
	/// <returns></returns>
	private static bool TryConfigureFroxelClusteredLights(Material material, Camera camera, int selectedAdditionalLightsCount, bool debugRestrictToMainCameraFrustum, Vector4[] debugMainCameraFrustumPlanes, out int froxelHash)
	{
		froxelHash = 0;
		if (camera == null || selectedAdditionalLightsCount <= 0 || !SystemInfo.supportsComputeShaders)
			return false;

		froxelHash = ComputeFroxelInputHash(camera, selectedAdditionalLightsCount);

		EnsureFroxelBuffersAllocated();
		if (froxelMetaBuffer == null || froxelLightIndicesBuffer == null)
			return false;

		if (!isMaterialStateInitialized || froxelHash != cachedFroxelHash)
		{
			BuildFroxelClusters(camera, selectedAdditionalLightsCount);
			froxelMetaBuffer.SetData(FroxelMetas);
			froxelLightIndicesBuffer.SetData(FroxelLightIndices);
		}

		material.SetBuffer(FroxelMetaBufferId, froxelMetaBuffer);
		material.SetBuffer(FroxelLightIndicesBufferId, froxelLightIndicesBuffer);
		material.SetVector(FroxelGridDimensionsId, new Vector4(FroxelGridWidth, FroxelGridHeight, FroxelGridDepth, FroxelMaxLightsPerCell));
		material.SetVector(FroxelNearFarId, new Vector4(camera.nearClipPlane, camera.farClipPlane, 0.0f, 0.0f));
		DrawFroxelDebug(camera, debugRestrictToMainCameraFrustum, debugMainCameraFrustumPlanes);

		return true;
	}

	/// <summary>
	/// Ensures froxel buffers are allocated.
	/// </summary>
	private static void EnsureFroxelBuffersAllocated()
	{
		if (froxelMetaBuffer == null || froxelMetaBuffer.count != FroxelCount || froxelMetaBuffer.stride != sizeof(int) * 2)
		{
			froxelMetaBuffer?.Release();
			froxelMetaBuffer = new ComputeBuffer(FroxelCount, sizeof(int) * 2, ComputeBufferType.Structured);
		}

		int froxelLightIndicesCount = FroxelCount * FroxelMaxLightsPerCell;
		if (froxelLightIndicesBuffer == null || froxelLightIndicesBuffer.count != froxelLightIndicesCount || froxelLightIndicesBuffer.stride != sizeof(int))
		{
			froxelLightIndicesBuffer?.Release();
			froxelLightIndicesBuffer = new ComputeBuffer(froxelLightIndicesCount, sizeof(int), ComputeBufferType.Structured);
		}
	}

	/// <summary>
	/// Builds clustered froxel light lists from selected additional lights.
	/// </summary>
	/// <param name="camera"></param>
	/// <param name="selectedAdditionalLightsCount"></param>
	private static void BuildFroxelClusters(Camera camera, int selectedAdditionalLightsCount)
	{
		Array.Clear(FroxelCellLightCounts, 0, FroxelCellLightCounts.Length);
		for (int i = 0; i < FroxelLightIndices.Length; ++i)
			FroxelLightIndices[i] = -1;

		float near = Mathf.Max(camera.nearClipPlane, 0.01f);
		float far = Mathf.Max(camera.farClipPlane, near + 0.01f);

		Matrix4x4 viewMatrix = camera.worldToCameraMatrix;
		Matrix4x4 viewProjectionMatrix = camera.projectionMatrix * viewMatrix;
		Vector3 cameraRight = camera.transform.right;
		Vector3 cameraUp = camera.transform.up;

		int froxelPlaneStride = FroxelGridWidth * FroxelGridHeight;
		float invLogDepthRange = 1.0f / Mathf.Max(Mathf.Log(far / near), 0.0001f);

		for (int compactLightIndex = 0; compactLightIndex < selectedAdditionalLightsCount; ++compactLightIndex)
		{
			Vector3 lightPosition = SelectedLightPositions[compactLightIndex];
			float lightRange = Mathf.Max(SelectedLightRanges[compactLightIndex], 0.01f);

			Vector3 lightPositionView = viewMatrix.MultiplyPoint(lightPosition);
			float lightEyeDepth = -lightPositionView.z;

			float minDepth = Mathf.Max(near, lightEyeDepth - lightRange);
			float maxDepth = Mathf.Min(far, lightEyeDepth + lightRange);
			if (maxDepth <= minDepth)
				continue;

			int minZ = DepthToFroxelSlice(minDepth, near, invLogDepthRange);
			int maxZ = DepthToFroxelSlice(maxDepth, near, invLogDepthRange);
			if (maxZ < minZ)
				continue;

			if (!TryGetLightScreenBounds(lightPosition, lightRange, cameraRight, cameraUp, viewProjectionMatrix, out float minU, out float maxU, out float minV, out float maxV))
				continue;

			int minX = Mathf.Clamp(Mathf.FloorToInt(minU * FroxelGridWidth), 0, FroxelGridWidth - 1);
			int maxX = Mathf.Clamp(Mathf.FloorToInt(maxU * FroxelGridWidth), 0, FroxelGridWidth - 1);
			int minY = Mathf.Clamp(Mathf.FloorToInt(minV * FroxelGridHeight), 0, FroxelGridHeight - 1);
			int maxY = Mathf.Clamp(Mathf.FloorToInt(maxV * FroxelGridHeight), 0, FroxelGridHeight - 1);
			if (maxX < minX || maxY < minY)
				continue;

			for (int z = minZ; z <= maxZ; ++z)
			{
				int froxelPlaneBase = z * froxelPlaneStride;
				for (int y = minY; y <= maxY; ++y)
				{
					int froxelRowBase = froxelPlaneBase + y * FroxelGridWidth;
					for (int x = minX; x <= maxX; ++x)
					{
						int froxelIndex = froxelRowBase + x;
						int count = FroxelCellLightCounts[froxelIndex];
						if (count >= FroxelMaxLightsPerCell)
							continue;

						int offset = froxelIndex * FroxelMaxLightsPerCell + count;
						FroxelLightIndices[offset] = compactLightIndex;
						FroxelCellLightCounts[froxelIndex] = count + 1;
					}
				}
			}
		}

		for (int i = 0; i < FroxelCount; ++i)
		{
			FroxelMetas[i].offset = i * FroxelMaxLightsPerCell;
			FroxelMetas[i].count = FroxelCellLightCounts[i];
		}
	}

	/// <summary>
	/// Draws froxel debug lines in scene view using Unity debug lines.
	/// </summary>
	/// <param name="camera"></param>
	private static void DrawFroxelDebug(Camera camera, bool restrictToMainCameraFrustum, Vector4[] mainCameraFrustumPlanes)
	{
		debugSolidCubeDrawCount = 0;

		if (!debugDrawFroxelClusters || camera == null || camera.cameraType != CameraType.SceneView)
			return;

		float near = Mathf.Max(camera.nearClipPlane, 0.01f);
		float far = Mathf.Max(camera.farClipPlane, near + 0.01f);
		int maxCellsToDraw = Mathf.Max(1, debugMaxFroxelsToDraw);
		int remainingCells = maxCellsToDraw;
		int maxCellsPerSlice = Mathf.Max(1, maxCellsToDraw / FroxelGridDepth);

		for (int z = 0; z < FroxelGridDepth; ++z)
		{
			if (remainingCells <= 0)
				return;

			float sliceNearDepth = FroxelSliceToDepth(z, near, far);
			float sliceFarDepth = FroxelSliceToDepth(z + 1, near, far);
			int sliceBudget = debugDrawWorldSpaceCubes ? remainingCells : Mathf.Min(remainingCells, maxCellsPerSlice);
			int drawnInSlice = 0;

			for (int y = 0; y < FroxelGridHeight; ++y)
			{
				if (drawnInSlice >= sliceBudget)
					break;

				for (int x = 0; x < FroxelGridWidth; ++x)
				{
					if (drawnInSlice >= sliceBudget || remainingCells <= 0)
						break;

					int froxelIndex = x + (y * FroxelGridWidth) + (z * FroxelGridWidth * FroxelGridHeight);
					int lightCount = FroxelCellLightCounts[froxelIndex];
					if (debugDrawOnlyOccupiedFroxels && lightCount <= 0)
						continue;

					if (restrictToMainCameraFrustum && !IsFroxelCellInsideMainCameraFrustum(camera, x, y, sliceNearDepth, sliceFarDepth, mainCameraFrustumPlanes))
						continue;

					Color drawColor = debugFroxelColor;
					if (lightCount > 0)
					{
						float t = Mathf.Clamp01(lightCount / (float)FroxelMaxLightsPerCell);
						drawColor = Color.Lerp(debugFroxelColor * 0.35f, debugFroxelColor, t);
					}
					else
					{
						drawColor *= 0.35f;
					}

					if (debugDrawWorldSpaceCubes)
						DrawFroxelCellWorldCube(camera, x, y, sliceNearDepth, sliceFarDepth, drawColor);
					else
						DrawFroxelCellFrustum(camera, x, y, sliceNearDepth, sliceFarDepth, drawColor);

					drawnInSlice++;
					remainingCells--;
				}
			}
		}
	}

	/// <summary>
	/// Draws one froxel cell as a wireframe box.
	/// </summary>
	/// <param name="camera"></param>
	/// <param name="x"></param>
	/// <param name="y"></param>
	/// <param name="nearDepth"></param>
	/// <param name="farDepth"></param>
	/// <param name="color"></param>
	private static void DrawFroxelCellFrustum(Camera camera, int x, int y, float nearDepth, float farDepth, Color color)
	{
		float u0 = x / (float)FroxelGridWidth;
		float u1 = (x + 1) / (float)FroxelGridWidth;
		float v0 = y / (float)FroxelGridHeight;
		float v1 = (y + 1) / (float)FroxelGridHeight;

		Vector3 n00 = camera.ViewportToWorldPoint(new Vector3(u0, v0, nearDepth));
		Vector3 n10 = camera.ViewportToWorldPoint(new Vector3(u1, v0, nearDepth));
		Vector3 n11 = camera.ViewportToWorldPoint(new Vector3(u1, v1, nearDepth));
		Vector3 n01 = camera.ViewportToWorldPoint(new Vector3(u0, v1, nearDepth));

		Vector3 f00 = camera.ViewportToWorldPoint(new Vector3(u0, v0, farDepth));
		Vector3 f10 = camera.ViewportToWorldPoint(new Vector3(u1, v0, farDepth));
		Vector3 f11 = camera.ViewportToWorldPoint(new Vector3(u1, v1, farDepth));
		Vector3 f01 = camera.ViewportToWorldPoint(new Vector3(u0, v1, farDepth));

		// Near face.
		Debug.DrawLine(n00, n10, color, 0.0f, true);
		Debug.DrawLine(n10, n11, color, 0.0f, true);
		Debug.DrawLine(n11, n01, color, 0.0f, true);
		Debug.DrawLine(n01, n00, color, 0.0f, true);

		// Far face.
		Debug.DrawLine(f00, f10, color, 0.0f, true);
		Debug.DrawLine(f10, f11, color, 0.0f, true);
		Debug.DrawLine(f11, f01, color, 0.0f, true);
		Debug.DrawLine(f01, f00, color, 0.0f, true);

		// Side edges.
		Debug.DrawLine(n00, f00, color, 0.0f, true);
		Debug.DrawLine(n10, f10, color, 0.0f, true);
		Debug.DrawLine(n11, f11, color, 0.0f, true);
		Debug.DrawLine(n01, f01, color, 0.0f, true);
	}

	/// <summary>
	/// Draws one occupied froxel as an approximate world-space cube.
	/// </summary>
	/// <param name="camera"></param>
	/// <param name="x"></param>
	/// <param name="y"></param>
	/// <param name="nearDepth"></param>
	/// <param name="farDepth"></param>
	/// <param name="color"></param>
	private static void DrawFroxelCellWorldCube(Camera camera, int x, int y, float nearDepth, float farDepth, Color color)
	{
		float u0 = x / (float)FroxelGridWidth;
		float u1 = (x + 1) / (float)FroxelGridWidth;
		float v0 = y / (float)FroxelGridHeight;
		float v1 = (y + 1) / (float)FroxelGridHeight;
		float uc = (u0 + u1) * 0.5f;
		float vc = (v0 + v1) * 0.5f;
		float depth = (nearDepth + farDepth) * 0.5f;

		Vector3 center = camera.ViewportToWorldPoint(new Vector3(uc, vc, depth));
		Vector3 worldX0 = camera.ViewportToWorldPoint(new Vector3(u0, vc, depth));
		Vector3 worldX1 = camera.ViewportToWorldPoint(new Vector3(u1, vc, depth));
		Vector3 worldY0 = camera.ViewportToWorldPoint(new Vector3(uc, v0, depth));
		Vector3 worldY1 = camera.ViewportToWorldPoint(new Vector3(uc, v1, depth));

		float width = Vector3.Distance(worldX0, worldX1);
		float height = Vector3.Distance(worldY0, worldY1);
		float cubeSize = Mathf.Max(0.001f, (width + height) * 0.5f);
		float halfExtent = cubeSize * 0.45f;

		DrawWorldSpaceSolidCube(center, halfExtent, color, camera);
	}

	/// <summary>
	/// Draws a translucent world-space solid cube.
	/// </summary>
	/// <param name="center"></param>
	/// <param name="halfExtent"></param>
	/// <param name="color"></param>
	/// <param name="camera"></param>
	private static void DrawWorldSpaceSolidCube(Vector3 center, float halfExtent, Color color, Camera camera)
	{
		if (camera == null || debugWorldSpaceCubeFillOpacity <= 0.0f || debugSolidCubeDrawCount >= debugSolidCubeDraws.Length)
			return;

		debugSolidCubeDraws[debugSolidCubeDrawCount].center = center;
		debugSolidCubeDraws[debugSolidCubeDrawCount].halfExtent = halfExtent;
		debugSolidCubeDraws[debugSolidCubeDrawCount].color = color;
		debugSolidCubeDrawCount++;
	}

	/// <summary>
	/// Draws all queued translucent debug cubes through the current command buffer.
	/// </summary>
	/// <param name="cmd"></param>
	/// <param name="camera"></param>
	private static void DrawQueuedDebugSolidCubes(CommandBuffer cmd, Camera camera)
	{
		if (cmd == null || camera == null || debugSolidCubeDrawCount <= 0 || debugWorldSpaceCubeFillOpacity <= 0.0f)
		{
			debugSolidCubeDrawCount = 0;
			return;
		}

		if (!EnsureDebugSolidCubeResources())
		{
			debugSolidCubeDrawCount = 0;
			return;
		}

		if (debugSolidCubePropertyBlock == null)
			debugSolidCubePropertyBlock = new MaterialPropertyBlock();

		float fillAlpha = Mathf.Clamp01(debugWorldSpaceCubeFillOpacity * Mathf.Max(debugFroxelColor.a, 0.0001f));
		for (int i = 0; i < debugSolidCubeDrawCount; ++i)
		{
			DebugSolidCubeDraw cube = debugSolidCubeDraws[i];
			Color fillColor = cube.color;
			fillColor.a = fillAlpha;

			debugSolidCubePropertyBlock.SetColor(DebugColorId, fillColor);

			Vector3 scale = Vector3.one * (cube.halfExtent * 2.0f);
			Matrix4x4 matrix = Matrix4x4.TRS(cube.center, Quaternion.identity, scale);
			cmd.DrawMesh(debugUnitCubeMesh, matrix, debugSolidCubeMaterial, 0, 0, debugSolidCubePropertyBlock);
		}

		debugSolidCubeDrawCount = 0;
	}

#if UNITY_6000_0_OR_NEWER
	/// <summary>
	/// Draws all queued translucent debug cubes through a render-graph raster command buffer.
	/// </summary>
	/// <param name="cmd"></param>
	/// <param name="camera"></param>
	private static void DrawQueuedDebugSolidCubes(RasterCommandBuffer cmd, Camera camera)
	{
		if (camera == null || debugSolidCubeDrawCount <= 0 || debugWorldSpaceCubeFillOpacity <= 0.0f)
		{
			debugSolidCubeDrawCount = 0;
			return;
		}

		if (!EnsureDebugSolidCubeResources())
		{
			debugSolidCubeDrawCount = 0;
			return;
		}

		if (debugSolidCubePropertyBlock == null)
			debugSolidCubePropertyBlock = new MaterialPropertyBlock();

		float fillAlpha = Mathf.Clamp01(debugWorldSpaceCubeFillOpacity * Mathf.Max(debugFroxelColor.a, 0.0001f));
		for (int i = 0; i < debugSolidCubeDrawCount; ++i)
		{
			DebugSolidCubeDraw cube = debugSolidCubeDraws[i];
			Color fillColor = cube.color;
			fillColor.a = fillAlpha;

			debugSolidCubePropertyBlock.SetColor(DebugColorId, fillColor);

			Vector3 scale = Vector3.one * (cube.halfExtent * 2.0f);
			Matrix4x4 matrix = Matrix4x4.TRS(cube.center, Quaternion.identity, scale);
			cmd.DrawMesh(debugUnitCubeMesh, matrix, debugSolidCubeMaterial, 0, 0, debugSolidCubePropertyBlock);
		}

		debugSolidCubeDrawCount = 0;
	}
#endif

	/// <summary>
	/// Ensures resources needed to draw translucent debug cubes are allocated.
	/// </summary>
	/// <returns></returns>
	private static bool EnsureDebugSolidCubeResources()
	{
		if (debugSolidCubeMaterial == null)
		{
			Shader debugShader = Shader.Find("Hidden/VolumetricFogDebugSolid");
			bool usingFallbackInternalColored = false;
			if (debugShader == null)
			{
				debugShader = Shader.Find("Hidden/Internal-Colored");
				usingFallbackInternalColored = debugShader != null;
			}

			if (debugShader == null)
				return false;

			debugSolidCubeMaterial = new Material(debugShader);
			debugSolidCubeMaterial.hideFlags = HideFlags.HideAndDontSave;

			if (usingFallbackInternalColored)
			{
				debugSolidCubeMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
				debugSolidCubeMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
				debugSolidCubeMaterial.SetInt("_Cull", (int)CullMode.Back);
				debugSolidCubeMaterial.SetInt("_ZWrite", 0);
				debugSolidCubeMaterial.SetInt("_ZTest", (int)CompareFunction.LessEqual);
			}
		}

		if (debugUnitCubeMesh == null)
			debugUnitCubeMesh = CreateUnitCubeMesh();

		return debugSolidCubeMaterial != null && debugUnitCubeMesh != null;
	}

	/// <summary>
	/// Creates a unit cube mesh centered at world origin.
	/// </summary>
	/// <returns></returns>
	private static Mesh CreateUnitCubeMesh()
	{
		Mesh mesh = new Mesh();
		mesh.name = "VolumetricFogDebugUnitCube";

		Vector3[] vertices =
		{
			new Vector3(-0.5f, -0.5f, -0.5f),
			new Vector3(0.5f, -0.5f, -0.5f),
			new Vector3(0.5f, 0.5f, -0.5f),
			new Vector3(-0.5f, 0.5f, -0.5f),
			new Vector3(-0.5f, -0.5f, 0.5f),
			new Vector3(0.5f, -0.5f, 0.5f),
			new Vector3(0.5f, 0.5f, 0.5f),
			new Vector3(-0.5f, 0.5f, 0.5f)
		};

		int[] triangles =
		{
			0, 2, 1, 0, 3, 2,
			4, 5, 6, 4, 6, 7,
			0, 1, 5, 0, 5, 4,
			2, 3, 7, 2, 7, 6,
			0, 4, 7, 0, 7, 3,
			1, 2, 6, 1, 6, 5
		};

		mesh.SetVertices(vertices);
		mesh.SetTriangles(triangles, 0, true);
		mesh.RecalculateBounds();
		mesh.UploadMeshData(true);
		return mesh;
	}

	/// <summary>
	/// Releases debug draw resources.
	/// </summary>
	private static void ReleaseDebugSolidCubeResources()
	{
		CoreUtils.Destroy(debugSolidCubeMaterial);
		debugSolidCubeMaterial = null;

		if (debugUnitCubeMesh != null)
		{
			if (Application.isPlaying)
				UnityEngine.Object.Destroy(debugUnitCubeMesh);
			else
				UnityEngine.Object.DestroyImmediate(debugUnitCubeMesh);
		}

		debugUnitCubeMesh = null;
		debugSolidCubePropertyBlock = null;
		debugSolidCubeDrawCount = 0;
	}

	/// <summary>
	/// Returns whether the froxel center lies inside the main camera frustum planes.
	/// </summary>
	/// <param name="camera"></param>
	/// <param name="x"></param>
	/// <param name="y"></param>
	/// <param name="nearDepth"></param>
	/// <param name="farDepth"></param>
	/// <param name="frustumPlanes"></param>
	/// <returns></returns>
	private static bool IsFroxelCellInsideMainCameraFrustum(Camera camera, int x, int y, float nearDepth, float farDepth, Vector4[] frustumPlanes)
	{
		if (frustumPlanes == null || frustumPlanes.Length < 6)
			return true;

		float u = (x + 0.5f) / FroxelGridWidth;
		float v = (y + 0.5f) / FroxelGridHeight;
		float depth = 0.5f * (nearDepth + farDepth);
		Vector3 centerWorldPosition = camera.ViewportToWorldPoint(new Vector3(u, v, depth));
		return IsPositionInsideFrustumPlanes(centerWorldPosition, frustumPlanes);
	}

	/// <summary>
	/// Returns whether a world-space position is inside all frustum planes.
	/// </summary>
	/// <param name="positionWS"></param>
	/// <param name="frustumPlanes"></param>
	/// <returns></returns>
	private static bool IsPositionInsideFrustumPlanes(Vector3 positionWS, Vector4[] frustumPlanes)
	{
		if (frustumPlanes == null || frustumPlanes.Length < 6)
			return true;

		for (int i = 0; i < 6; ++i)
		{
			Vector4 plane = frustumPlanes[i];
			float signedDistance = (plane.x * positionWS.x) + (plane.y * positionWS.y) + (plane.z * positionWS.z) + plane.w;
			if (signedDistance < 0.0f)
				return false;
		}

		return true;
	}

	/// <summary>
	/// Converts froxel slice index to eye depth.
	/// </summary>
	/// <param name="sliceIndex"></param>
	/// <param name="near"></param>
	/// <param name="far"></param>
	/// <returns></returns>
	private static float FroxelSliceToDepth(int sliceIndex, float near, float far)
	{
		float t = Mathf.Clamp01(sliceIndex / (float)FroxelGridDepth);
		return near * Mathf.Exp(Mathf.Log(far / near) * t);
	}

	/// <summary>
	/// Converts eye depth to a froxel Z slice.
	/// </summary>
	/// <param name="eyeDepth"></param>
	/// <param name="near"></param>
	/// <param name="invLogDepthRange"></param>
	/// <returns></returns>
	private static int DepthToFroxelSlice(float eyeDepth, float near, float invLogDepthRange)
	{
		float normalizedDepth = Mathf.Log(Mathf.Max(eyeDepth / near, 1.0f)) * invLogDepthRange;
		int slice = Mathf.FloorToInt(normalizedDepth * FroxelGridDepth);
		return Mathf.Clamp(slice, 0, FroxelGridDepth - 1);
	}

	/// <summary>
	/// Gets screen-space bounds for a spherical light proxy.
	/// </summary>
	/// <param name="lightPosition"></param>
	/// <param name="lightRange"></param>
	/// <param name="cameraRight"></param>
	/// <param name="cameraUp"></param>
	/// <param name="viewProjectionMatrix"></param>
	/// <param name="minU"></param>
	/// <param name="maxU"></param>
	/// <param name="minV"></param>
	/// <param name="maxV"></param>
	/// <returns></returns>
	private static bool TryGetLightScreenBounds(Vector3 lightPosition, float lightRange, Vector3 cameraRight, Vector3 cameraUp, Matrix4x4 viewProjectionMatrix, out float minU, out float maxU, out float minV, out float maxV)
	{
		minU = 0.0f;
		maxU = 1.0f;
		minV = 0.0f;
		maxV = 1.0f;

		Vector4 clipCenter = viewProjectionMatrix * new Vector4(lightPosition.x, lightPosition.y, lightPosition.z, 1.0f);
		if (clipCenter.w <= 0.0001f)
			return false;

		Vector2 ndcCenter = new Vector2(clipCenter.x, clipCenter.y) / clipCenter.w;
		Vector4 clipRight = viewProjectionMatrix * new Vector4(lightPosition.x + cameraRight.x * lightRange, lightPosition.y + cameraRight.y * lightRange, lightPosition.z + cameraRight.z * lightRange, 1.0f);
		Vector4 clipUp = viewProjectionMatrix * new Vector4(lightPosition.x + cameraUp.x * lightRange, lightPosition.y + cameraUp.y * lightRange, lightPosition.z + cameraUp.z * lightRange, 1.0f);
		if (clipRight.w <= 0.0001f || clipUp.w <= 0.0001f)
			return false;

		Vector2 ndcRight = new Vector2(clipRight.x, clipRight.y) / clipRight.w;
		Vector2 ndcUp = new Vector2(clipUp.x, clipUp.y) / clipUp.w;
		float radiusX = Mathf.Abs(ndcRight.x - ndcCenter.x);
		float radiusY = Mathf.Abs(ndcUp.y - ndcCenter.y);
		float radius = Mathf.Max(Mathf.Max(radiusX, radiusY), 0.001f);

		float minNdcX = ndcCenter.x - radius;
		float maxNdcX = ndcCenter.x + radius;
		float minNdcY = ndcCenter.y - radius;
		float maxNdcY = ndcCenter.y + radius;
		if (maxNdcX < -1.0f || minNdcX > 1.0f || maxNdcY < -1.0f || minNdcY > 1.0f)
			return false;

		minU = Mathf.Clamp01(minNdcX * 0.5f + 0.5f);
		maxU = Mathf.Clamp01(maxNdcX * 0.5f + 0.5f);
		minV = Mathf.Clamp01(minNdcY * 0.5f + 0.5f);
		maxV = Mathf.Clamp01(maxNdcY * 0.5f + 0.5f);
		return true;
	}

	/// <summary>
	/// Computes the hash used to validate froxel clusters cache.
	/// </summary>
	/// <param name="camera"></param>
	/// <param name="selectedAdditionalLightsCount"></param>
	/// <returns></returns>
	private static int ComputeFroxelInputHash(Camera camera, int selectedAdditionalLightsCount)
	{
		unchecked
		{
			int hash = 17;
			hash = (hash * 31) + selectedAdditionalLightsCount;
			hash = (hash * 31) + camera.transform.position.GetHashCode();
			hash = (hash * 31) + camera.transform.rotation.GetHashCode();
			hash = (hash * 31) + camera.nearClipPlane.GetHashCode();
			hash = (hash * 31) + camera.farClipPlane.GetHashCode();

			for (int i = 0; i < selectedAdditionalLightsCount; ++i)
			{
				hash = (hash * 31) + SelectedAdditionalIndices[i];
				hash = (hash * 31) + SelectedLightPositions[i].GetHashCode();
				hash = (hash * 31) + SelectedLightRanges[i].GetHashCode();
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
		cachedBakedVolumetricLightingEnabled = false;
		cachedBakedStaticLightsEnabled = false;
		cachedBakedDirectionalPhaseEnabled = false;
		cachedBakedFroxelSamplingEnabled = false;
		cachedSceneViewMainCameraFrustumMaskEnabled = false;
		cachedFroxelClusteredLightsEnabled = false;
#if UNITY_2023_1_OR_NEWER
		cachedAPVContributionEnabled = false;
#endif
		cachedAdditionalLightsCount = int.MinValue;
		cachedLightsHash = int.MinValue;
		cachedMainCameraFrustumPlanesHash = int.MinValue;
		cachedFroxelHash = int.MinValue;
		cachedBakedFroxelHash = int.MinValue;
		cachedBakedStaticLightsHash = int.MinValue;
		cachedDistance = float.NaN;
		cachedBaseHeight = float.NaN;
		cachedMaximumHeight = float.NaN;
		cachedGroundHeight = float.NaN;
		cachedDensity = float.NaN;
		cachedAbsortion = float.NaN;
		cachedBakedVolumetricFogIntensity = float.NaN;
#if UNITY_2023_1_OR_NEWER
		cachedAPVContributionWeight = float.NaN;
#endif
		cachedTint = new Color(float.NaN, float.NaN, float.NaN, float.NaN);
		cachedMaxSteps = int.MinValue;
		cachedTransmittanceThreshold = float.NaN;
		cachedBakedVolumetricLightingHash = int.MinValue;
	}

	/// <summary>
	/// Releases clustered froxel GPU buffers.
	/// </summary>
	private static void ReleaseFroxelBuffers()
	{
		froxelMetaBuffer?.Release();
		froxelMetaBuffer = null;

		froxelLightIndicesBuffer?.Release();
		froxelLightIndicesBuffer = null;

		bakedFroxelLightingBuffer?.Release();
		bakedFroxelLightingBuffer = null;

		bakedFroxelDirectionBuffer?.Release();
		bakedFroxelDirectionBuffer = null;

		bakedLightingPixelsCache = null;
		bakedDirectionPixelsCache = null;
		cachedBakedLightingTexture = null;
		cachedBakedDirectionTexture = null;
		cachedBakedLightingTextureContentsHash = default;
		cachedBakedDirectionTextureContentsHash = default;
		cachedBakedTextureWidth = 0;
		cachedBakedTextureHeight = 0;
		cachedBakedTextureDepth = 0;
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
			UpdateVolumetricFogMaterialParameters(passData.material, passData.fogVolume, passData.camera, passData.cameraPosition, passData.lightData.mainLightIndex, passData.lightData.additionalLightsCount, passData.lightData.visibleLights, passData.debugRestrictToMainCameraFrustum, passData.debugMainCameraFrustumPlanes);
		}
		else if (stage == PassStage.VolumetricFogUpsampleComposition)
		{
			passData.material.SetTexture(VolumetricFogTextureId, passData.volumetricFogRenderTarget);
		}

		Blitter.BlitTexture(context.cmd, passData.source, Vector2.one, passData.material, passData.materialPassIndex);

		if (stage == PassStage.VolumetricFogUpsampleComposition)
			DrawQueuedDebugSolidCubes(context.cmd, passData.camera);
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
		ReleaseDebugSolidCubeResources();
		ReleaseFroxelBuffers();
		ResetMaterialStateCache();
	}

	#endregion
}
