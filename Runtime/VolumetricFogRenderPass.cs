using System;
using System.Collections.Generic;
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

	private struct BakedAdditionalLightData
	{
		public Light light;
		public VolumetricAdditionalLight volumetricLight;
		public bool isStaticAtBake;
		public int bakedStaticLightIndex;
		public float anisotropy;
		public float scattering;
		public float radiusSq;
		public Vector3 position;
		public float range;
		public float intensity;
		public float spotAngle;
		public float spotFactor;
		public bool isSpotAtBake;
		public Vector3 color;
		public Vector3 spotDirection;
		public float spotCosOuter;
		public float spotInvCosRange;
	}

	private struct BakedMainLightData
	{
		public bool isValid;
		public Vector3 direction;
		public Vector3 color;
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
	private static readonly int BakedMainLightEnabledId = Shader.PropertyToID("_BakedMainLightEnabled");
	private static readonly int BakedMainLightDirectionId = Shader.PropertyToID("_BakedMainLightDirection");
	private static readonly int BakedMainLightColorId = Shader.PropertyToID("_BakedMainLightColor");
	private static readonly int BakedAdditionalLightPositionsArrayId = Shader.PropertyToID("_BakedAdditionalLightPositions");
	private static readonly int BakedAdditionalLightColorsArrayId = Shader.PropertyToID("_BakedAdditionalLightColors");
	private static readonly int BakedAdditionalLightDirectionsArrayId = Shader.PropertyToID("_BakedAdditionalLightDirections");
	private static readonly int BakedAdditionalLightSpotDataArrayId = Shader.PropertyToID("_BakedAdditionalLightSpotData");
	private static readonly int BakedAdditionalLightOcclusionGridBufferId = Shader.PropertyToID("_BakedAdditionalLightOcclusionGrid");
	private static readonly int BakedAdditionalLightOcclusionGridSizeId = Shader.PropertyToID("_BakedAdditionalLightOcclusionGridSize");

	private const int MaxVisibleAdditionalLights = 256;
	private const int FroxelGridWidth = 16;
	private const int FroxelGridHeight = 9;
	private const int FroxelGridDepth = 24;
	private const int FroxelMaxLightsPerCell = 24;
	private const int FroxelCount = FroxelGridWidth * FroxelGridHeight * FroxelGridDepth;
	private const int BakedAdditionalLightOcclusionGridSize = 10;
	private const int BakedAdditionalLightOcclusionVoxelCount = BakedAdditionalLightOcclusionGridSize * BakedAdditionalLightOcclusionGridSize * BakedAdditionalLightOcclusionGridSize;
	private const int StaticOcclusionRaycastHitsCapacity = 64;
	private const float StaticOcclusionRayBias = 0.01f;
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
	private static readonly Dictionary<int, BakedAdditionalLightData> BakedAdditionalLightsByInstanceId = new Dictionary<int, BakedAdditionalLightData>(MaxVisibleAdditionalLights);
	private static readonly Vector4[] BakedAdditionalLightPositions = new Vector4[MaxVisibleAdditionalLights];
	private static readonly Vector4[] BakedAdditionalLightColors = new Vector4[MaxVisibleAdditionalLights];
	private static readonly Vector4[] BakedAdditionalLightDirections = new Vector4[MaxVisibleAdditionalLights];
	private static readonly Vector4[] BakedAdditionalLightSpotData = new Vector4[MaxVisibleAdditionalLights];
	private static readonly float[] BakedAdditionalLightOcclusionGrid = new float[MaxVisibleAdditionalLights * BakedAdditionalLightOcclusionVoxelCount];
	private static readonly RaycastHit[] StaticOcclusionHits = new RaycastHit[StaticOcclusionRaycastHitsCapacity];
	private static readonly FroxelMeta[] FroxelMetas = new FroxelMeta[FroxelCount];
	private static readonly int[] FroxelLightIndices = new int[FroxelCount * FroxelMaxLightsPerCell];
	private static readonly int[] FroxelCellLightCounts = new int[FroxelCount];

	private static ComputeBuffer froxelMetaBuffer;
	private static ComputeBuffer froxelLightIndicesBuffer;
	private static ComputeBuffer bakedAdditionalLightOcclusionGridBuffer;

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
	private static bool cachedSceneViewMainCameraFrustumMaskEnabled;
	private static bool cachedFroxelClusteredLightsEnabled;
#if UNITY_2023_1_OR_NEWER
	private static bool cachedAPVContributionEnabled;
#endif
	private static int cachedAdditionalLightsCount;
	private static int cachedLightsHash;
	private static int cachedMainCameraFrustumPlanesHash;
	private static int cachedFroxelHash;
	private static int cachedStaticLightsBakeRevision;
	private static int bakedStaticLightsCount;
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
	private static bool hasValidStaticLightsBake;
	private static BakedMainLightData bakedMainLight;

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
		ResetStaticLightsBakeCache();
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

		bool enableStaticLightsBake = fogVolume.enableStaticLightsBake.value;
		bool enableMainLightContribution = fogVolume.enableMainLightContribution.value && fogVolume.scattering.value > 0.0f && mainLightIndex > -1;
		bool enableAdditionalLightsContribution = fogVolume.enableAdditionalLightsContribution.value && additionalLightsCount > 0 && fogVolume.maxAdditionalLights.value > 0;
		bool staticLightsBakeDataChanged = false;

		if (enableStaticLightsBake)
		{
			int staticLightsBakeRevision = fogVolume.staticLightsBakeRevision.value;
			if (!hasValidStaticLightsBake || cachedStaticLightsBakeRevision != staticLightsBakeRevision)
			{
				BakeStaticLightsData(mainLightIndex, visibleLights);
				staticLightsBakeDataChanged = true;
			}

			cachedStaticLightsBakeRevision = staticLightsBakeRevision;
		}

		int lightsHash = 0;
		int effectiveAdditionalLightsCount = 0;
		if (enableMainLightContribution || enableAdditionalLightsContribution)
		{
			effectiveAdditionalLightsCount = enableStaticLightsBake
				? UpdateBakedLightsParameters(fogVolume, enableMainLightContribution, enableAdditionalLightsContribution, mainLightIndex, additionalLightsCount, visibleLights, cameraPosition, out lightsHash)
				: UpdateLightsParameters(fogVolume, enableMainLightContribution, enableAdditionalLightsContribution, mainLightIndex, additionalLightsCount, visibleLights, cameraPosition, out lightsHash);
		}

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

		bool useBakedMainLight = enableStaticLightsBake && hasValidStaticLightsBake && enableMainLightContribution && bakedMainLight.isValid;
		volumetricFogMaterial.SetInteger(BakedMainLightEnabledId, useBakedMainLight ? 1 : 0);
		if (useBakedMainLight)
		{
			volumetricFogMaterial.SetVector(BakedMainLightDirectionId, new Vector4(bakedMainLight.direction.x, bakedMainLight.direction.y, bakedMainLight.direction.z, 0.0f));
			volumetricFogMaterial.SetColor(BakedMainLightColorId, new Color(bakedMainLight.color.x, bakedMainLight.color.y, bakedMainLight.color.z, 1.0f));
		}
		bool enableBakedAdditionalLightOcclusionGrid = enableStaticLightsBake && hasValidStaticLightsBake && bakedStaticLightsCount > 0 && SystemInfo.graphicsShaderLevel >= 45;
		volumetricFogMaterial.SetInteger(BakedAdditionalLightOcclusionGridSizeId, enableBakedAdditionalLightOcclusionGrid ? BakedAdditionalLightOcclusionGridSize : 0);

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
			bool shouldUploadLightArrays = !isMaterialStateInitialized || lightsHash != cachedLightsHash || staticLightsBakeDataChanged;
			if (shouldUploadLightArrays)
			{
				volumetricFogMaterial.SetFloatArray(AnisotropiesArrayId, Anisotropies);
				volumetricFogMaterial.SetFloatArray(ScatteringsArrayId, Scatterings);
				volumetricFogMaterial.SetFloatArray(RadiiSqArrayId, RadiiSq);
				volumetricFogMaterial.SetFloatArray(AdditionalLightIndicesArrayId, AdditionalLightIndices);
				if (enableStaticLightsBake && hasValidStaticLightsBake && bakedStaticLightsCount > 0)
				{
					volumetricFogMaterial.SetVectorArray(BakedAdditionalLightPositionsArrayId, BakedAdditionalLightPositions);
					volumetricFogMaterial.SetVectorArray(BakedAdditionalLightColorsArrayId, BakedAdditionalLightColors);
					volumetricFogMaterial.SetVectorArray(BakedAdditionalLightDirectionsArrayId, BakedAdditionalLightDirections);
					volumetricFogMaterial.SetVectorArray(BakedAdditionalLightSpotDataArrayId, BakedAdditionalLightSpotData);
					if (enableBakedAdditionalLightOcclusionGrid)
					{
						EnsureBakedAdditionalLightOcclusionBufferAllocated();
						if (bakedAdditionalLightOcclusionGridBuffer != null)
						{
							bakedAdditionalLightOcclusionGridBuffer.SetData(BakedAdditionalLightOcclusionGrid);
							volumetricFogMaterial.SetBuffer(BakedAdditionalLightOcclusionGridBufferId, bakedAdditionalLightOcclusionGridBuffer);
						}
					}
				}
				cachedLightsHash = lightsHash;
			}
		}

		bool enableFroxelClusteredLights = enableAdditionalLightsContribution && effectiveAdditionalLightsCount > 0;
		int froxelHash = 0;
		if (enableFroxelClusteredLights)
			enableFroxelClusteredLights = TryConfigureFroxelClusteredLights(volumetricFogMaterial, camera, effectiveAdditionalLightsCount, staticLightsBakeDataChanged, debugRestrictToMainCameraFrustum, debugMainCameraFrustumPlanes, out froxelHash);

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
	/// Updates and selects additional lights using baked static light data.
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
	private static int UpdateBakedLightsParameters(VolumetricFogVolumeComponent fogVolume, bool enableMainLightContribution, bool enableAdditionalLightsContribution, int mainLightIndex, int additionalLightsCount, NativeArray<VisibleLight> visibleLights, Vector3 cameraPosition, out int lightsHash)
	{
		int effectiveAdditionalLightsCount = 0;

		if (enableAdditionalLightsContribution && hasValidStaticLightsBake && visibleLights.Length > 0)
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

				int liveAdditionalLightIndex = additionalLightSlotIndex++;
				VisibleLight visibleLight = visibleLights[i];

				if (!TryGetAdditionalLightCandidateBaked(visibleLight, liveAdditionalLightIndex, cameraPosition, fogDistance, fogMinHeight, fogMaxHeight, out int selectedAdditionalLightIndex, out float anisotropy, out float scattering, out float radiusSq, out float score, out Vector3 lightPosition, out float lightRange))
					continue;

				TryInsertSelectedAdditionalLight(selectedAdditionalLightIndex, anisotropy, scattering, radiusSq, score, lightPosition, lightRange, maxAdditionalLights, ref effectiveAdditionalLightsCount);
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
	/// Builds the static additional lights bake cache.
	/// </summary>
	/// <param name="mainLightIndex"></param>
	/// <param name="visibleLights"></param>
	private static void BakeStaticLightsData(int mainLightIndex, NativeArray<VisibleLight> visibleLights)
	{
		BakedAdditionalLightsByInstanceId.Clear();
		bakedStaticLightsCount = 0;
		bakedMainLight = default;

		for (int i = 0; i < BakedAdditionalLightPositions.Length; ++i)
		{
			BakedAdditionalLightPositions[i] = Vector4.zero;
			BakedAdditionalLightColors[i] = Vector4.zero;
			BakedAdditionalLightDirections[i] = Vector4.zero;
			BakedAdditionalLightSpotData[i] = Vector4.zero;
		}
		for (int i = 0; i < BakedAdditionalLightOcclusionGrid.Length; ++i)
			BakedAdditionalLightOcclusionGrid[i] = 1.0f;

#if UNITY_2023_1_OR_NEWER
		Light[] sceneLights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
		Light[] sceneLights = UnityEngine.Object.FindObjectsOfType<Light>();
#endif

		Light mainLight = (mainLightIndex >= 0 && mainLightIndex < visibleLights.Length) ? visibleLights[mainLightIndex].light : null;
		if (TryGetBakedMainLightData(mainLight, out BakedMainLightData bakedMainLightData))
			bakedMainLight = bakedMainLightData;

		for (int i = 0; i < sceneLights.Length; ++i)
		{
			Light light = sceneLights[i];
			if (light == null || !light.enabled || !light.gameObject.activeInHierarchy)
				continue;

			if (light.type != LightType.Point && light.type != LightType.Spot)
				continue;

			if (!light.TryGetComponent(out VolumetricAdditionalLight volumetricLight) || !volumetricLight.enabled || !volumetricLight.gameObject.activeInHierarchy)
				continue;

			bool isStaticLight = IsLightStaticForBake(light);
			int bakedStaticLightIndex = -1;
			if (isStaticLight && bakedStaticLightsCount < MaxVisibleAdditionalLights)
				bakedStaticLightIndex = bakedStaticLightsCount++;

			BakedAdditionalLightData bakedData = BuildBakedAdditionalLightData(light, volumetricLight, isStaticLight, bakedStaticLightIndex);
			BakedAdditionalLightsByInstanceId[light.GetInstanceID()] = bakedData;

			if (bakedStaticLightIndex >= 0)
			{
				BakedAdditionalLightPositions[bakedStaticLightIndex] = new Vector4(bakedData.position.x, bakedData.position.y, bakedData.position.z, 1.0f / Mathf.Max(bakedData.range * bakedData.range, 0.0001f));
				BakedAdditionalLightColors[bakedStaticLightIndex] = new Vector4(bakedData.color.x, bakedData.color.y, bakedData.color.z, 0.0f);
				BakedAdditionalLightDirections[bakedStaticLightIndex] = new Vector4(bakedData.spotDirection.x, bakedData.spotDirection.y, bakedData.spotDirection.z, 0.0f);
				BakedAdditionalLightSpotData[bakedStaticLightIndex] = new Vector4(bakedData.isSpotAtBake ? 1.0f : 0.0f, bakedData.spotCosOuter, bakedData.spotInvCosRange, 0.0f);
				BakeStaticAdditionalLightOcclusionGrid(bakedStaticLightIndex, bakedData);
			}
		}

		hasValidStaticLightsBake = true;
	}

	/// <summary>
	/// Resets static additional lights bake cache.
	/// </summary>
	private static void ResetStaticLightsBakeCache()
	{
		hasValidStaticLightsBake = false;
		bakedStaticLightsCount = 0;
		cachedStaticLightsBakeRevision = int.MinValue;
		BakedAdditionalLightsByInstanceId.Clear();
		bakedMainLight = default;
	}

	/// <summary>
	/// Returns whether this game object should be treated as static for volumetric bake.
	/// </summary>
	/// <param name="gameObject"></param>
	/// <returns></returns>
	private static bool IsGameObjectStaticForBake(GameObject gameObject)
	{
		if (gameObject == null)
			return false;

		if (gameObject.isStatic)
			return true;

#if UNITY_EDITOR
		UnityEditor.StaticEditorFlags staticFlags = UnityEditor.GameObjectUtility.GetStaticEditorFlags(gameObject);
		if ((staticFlags & UnityEditor.StaticEditorFlags.ContributeGI) != 0)
			return true;
#endif

		return false;
	}

	/// <summary>
	/// Returns whether this light should be treated as static for volumetric bake.
	/// </summary>
	/// <param name="light"></param>
	/// <returns></returns>
	private static bool IsLightStaticForBake(Light light)
	{
		if (light == null || light.gameObject == null)
			return false;

		if (IsGameObjectStaticForBake(light.gameObject))
			return true;

		LightmapBakeType bakeType = light.lightmapBakeType;
		return bakeType == LightmapBakeType.Baked || bakeType == LightmapBakeType.Mixed;
	}

	/// <summary>
	/// Returns whether the current main light can be baked.
	/// </summary>
	/// <param name="mainLight"></param>
	/// <param name="bakedMainLightData"></param>
	/// <returns></returns>
	private static bool TryGetBakedMainLightData(Light mainLight, out BakedMainLightData bakedMainLightData)
	{
		bakedMainLightData = default;
		if (mainLight == null || !mainLight.enabled || !mainLight.gameObject.activeInHierarchy || !IsLightStaticForBake(mainLight))
			return false;

		if (mainLight.type != LightType.Directional || mainLight.intensity <= MinAdditionalLightIntensity)
			return false;

		Color linearColor = mainLight.color.linear;
		bakedMainLightData.isValid = true;
		bakedMainLightData.direction = -mainLight.transform.forward;
		bakedMainLightData.color = new Vector3(linearColor.r, linearColor.g, linearColor.b) * mainLight.intensity;
		return true;
	}

	/// <summary>
	/// Creates baked light data containing only static values that can be reused safely.
	/// </summary>
	/// <param name="light"></param>
	/// <param name="volumetricLight"></param>
	/// <param name="isStaticLight"></param>
	/// <param name="bakedStaticLightIndex"></param>
	/// <returns></returns>
	private static BakedAdditionalLightData BuildBakedAdditionalLightData(Light light, VolumetricAdditionalLight volumetricLight, bool isStaticLight, int bakedStaticLightIndex)
	{
		float range = Mathf.Max(light.range, 0.01f);
		Color linearColor = light.color.linear;
		bool isSpot = light.type == LightType.Spot;
		float spotAngle = isSpot ? light.spotAngle : 0.0f;
		float spotCosOuter = -1.0f;
		float spotInvCosRange = 0.0f;
		Vector3 spotDirection = Vector3.forward;
		if (isSpot)
		{
			float cosOuter = Mathf.Cos(0.5f * Mathf.Deg2Rad * spotAngle);
			float innerAngle = light.innerSpotAngle;
			float cosInner = Mathf.Cos(0.5f * Mathf.Deg2Rad * innerAngle);
			spotCosOuter = cosOuter;
			spotInvCosRange = 1.0f / Mathf.Max(cosInner - cosOuter, 0.001f);
			spotDirection = light.transform.forward;
		}

		return new BakedAdditionalLightData
		{
			light = light,
			volumetricLight = volumetricLight,
			isStaticAtBake = isStaticLight,
			bakedStaticLightIndex = bakedStaticLightIndex,
			anisotropy = volumetricLight.Anisotropy,
			scattering = volumetricLight.Scattering,
			radiusSq = volumetricLight.Radius * volumetricLight.Radius,
			position = light.transform.position,
			range = range,
			intensity = light.intensity,
			spotAngle = spotAngle,
			spotFactor = isSpot ? ComputeSpotLightScoreFactor(spotAngle) : 1.0f,
			isSpotAtBake = isSpot,
			color = new Vector3(linearColor.r, linearColor.g, linearColor.b) * light.intensity,
			spotDirection = spotDirection,
			spotCosOuter = spotCosOuter,
			spotInvCosRange = spotInvCosRange
		};
	}

	/// <summary>
	/// Bakes static-object occlusion into a light-local 3D grid for one static baked additional light.
	/// </summary>
	/// <param name="bakedStaticLightIndex"></param>
	/// <param name="bakedLightData"></param>
	private static void BakeStaticAdditionalLightOcclusionGrid(int bakedStaticLightIndex, in BakedAdditionalLightData bakedLightData)
	{
		if (bakedStaticLightIndex < 0 || bakedStaticLightIndex >= MaxVisibleAdditionalLights)
			return;

		int gridSize = BakedAdditionalLightOcclusionGridSize;
		int gridSlice = gridSize * gridSize;
		int baseOffset = bakedStaticLightIndex * BakedAdditionalLightOcclusionVoxelCount;
		float range = Mathf.Max(bakedLightData.range, 0.01f);
		Vector3 lightPosition = bakedLightData.position;

		for (int z = 0; z < gridSize; ++z)
		{
			float nz = ((z + 0.5f) / gridSize) * 2.0f - 1.0f;
			for (int y = 0; y < gridSize; ++y)
			{
				float ny = ((y + 0.5f) / gridSize) * 2.0f - 1.0f;
				for (int x = 0; x < gridSize; ++x)
				{
					float nx = ((x + 0.5f) / gridSize) * 2.0f - 1.0f;
					Vector3 samplePosition = lightPosition + new Vector3(nx, ny, nz) * range;
					int voxelOffset = x + y * gridSize + z * gridSlice;
					BakedAdditionalLightOcclusionGrid[baseOffset + voxelOffset] = ComputeStaticRayOcclusion(lightPosition, samplePosition);
				}
			}
		}
	}

	/// <summary>
	/// Computes binary static occlusion between two world positions.
	/// </summary>
	/// <param name="origin"></param>
	/// <param name="target"></param>
	/// <returns></returns>
	private static float ComputeStaticRayOcclusion(in Vector3 origin, in Vector3 target)
	{
		Vector3 toTarget = target - origin;
		float distance = toTarget.magnitude;
		if (distance <= StaticOcclusionRayBias)
			return 1.0f;

		Vector3 direction = toTarget / distance;
		Ray ray = new Ray(origin + direction * StaticOcclusionRayBias, direction);
		float rayDistance = distance - StaticOcclusionRayBias;
		int hitCount = Physics.RaycastNonAlloc(ray, StaticOcclusionHits, rayDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
		if (hitCount <= 0)
			return 1.0f;

		for (int i = 0; i < hitCount; ++i)
		{
			Collider hitCollider = StaticOcclusionHits[i].collider;
			if (hitCollider == null)
				continue;

			if (StaticOcclusionHits[i].distance <= 0.0001f)
				continue;

			if (IsGameObjectStaticForBake(hitCollider.gameObject))
				return 0.0f;
		}

		return 1.0f;
	}

	/// <summary>
	/// Returns whether a visible light is a valid additional light candidate using baked static data when possible.
	/// </summary>
	/// <param name="visibleLight"></param>
	/// <param name="liveAdditionalLightIndex"></param>
	/// <param name="cameraPosition"></param>
	/// <param name="fogDistance"></param>
	/// <param name="fogMinHeight"></param>
	/// <param name="fogMaxHeight"></param>
	/// <param name="selectedAdditionalLightIndex"></param>
	/// <param name="anisotropy"></param>
	/// <param name="scattering"></param>
	/// <param name="radiusSq"></param>
	/// <param name="score"></param>
	/// <param name="lightPosition"></param>
	/// <param name="lightRange"></param>
	/// <returns></returns>
	private static bool TryGetAdditionalLightCandidateBaked(in VisibleLight visibleLight, int liveAdditionalLightIndex, in Vector3 cameraPosition, float fogDistance, float fogMinHeight, float fogMaxHeight, out int selectedAdditionalLightIndex, out float anisotropy, out float scattering, out float radiusSq, out float score, out Vector3 lightPosition, out float lightRange)
	{
		selectedAdditionalLightIndex = liveAdditionalLightIndex;
		anisotropy = 0.0f;
		scattering = 0.0f;
		radiusSq = 0.0f;
		score = 0.0f;
		lightPosition = Vector3.zero;
		lightRange = 0.0f;

		Light light = visibleLight.light;
		if (light == null || !light.enabled || !light.gameObject.activeInHierarchy)
			return false;

		if (light.type != LightType.Point && light.type != LightType.Spot)
			return false;

		if (!TryGetCachedBakedAdditionalLightData(light, out BakedAdditionalLightData bakedLight))
			return TryGetAdditionalLightCandidate(visibleLight, cameraPosition, fogDistance, fogMinHeight, fogMaxHeight, out anisotropy, out scattering, out radiusSq, out score, out lightPosition, out lightRange);

		if (bakedLight.isStaticAtBake && bakedLight.bakedStaticLightIndex >= 0)
		{
			float range = Mathf.Max(bakedLight.range, 0.01f);
			Vector3 bakedPosition = bakedLight.position;
			float bakedMaxDistance = fogDistance + range;
			float bakedDistanceSq = (bakedPosition - cameraPosition).sqrMagnitude;
			if (bakedDistanceSq > bakedMaxDistance * bakedMaxDistance)
				return false;

			float bakedLightMinY = bakedPosition.y - range;
			float bakedLightMaxY = bakedPosition.y + range;
			if (bakedLightMaxY < fogMinHeight || bakedLightMinY > fogMaxHeight)
				return false;

			anisotropy = bakedLight.anisotropy;
			scattering = bakedLight.scattering;
			radiusSq = bakedLight.radiusSq;
			lightPosition = bakedPosition;
			lightRange = range;

			float bakedDistanceWeight = (range * range) / Mathf.Max(bakedDistanceSq, 1.0f);
			score = scattering * bakedLight.intensity * bakedDistanceWeight * bakedLight.spotFactor;
			if (score <= 0.0f)
				return false;

			// Negative index encodes a baked static light slot for shader-side lookup.
			selectedAdditionalLightIndex = -(bakedLight.bakedStaticLightIndex + 1);
			return true;
		}

		VolumetricAdditionalLight volumetricLight = bakedLight.volumetricLight;
		float currentScattering = volumetricLight.Scattering;
		float currentIntensity = light.intensity;
		float currentRange = Mathf.Max(light.range, 0.01f);
		if (currentScattering <= MinAdditionalLightScattering || currentIntensity <= MinAdditionalLightIntensity)
			return false;

		lightPosition = light.transform.position;
		float maxDistance = fogDistance + currentRange;
		float distanceSq = (lightPosition - cameraPosition).sqrMagnitude;
		if (distanceSq > maxDistance * maxDistance)
			return false;

		float lightMinY = lightPosition.y - currentRange;
		float lightMaxY = lightPosition.y + currentRange;
		if (lightMaxY < fogMinHeight || lightMinY > fogMaxHeight)
			return false;

		bool useBakedConstants = AreBakedAdditionalLightConstantsStillValid(light, volumetricLight, bakedLight, currentRange, currentIntensity, currentScattering);

		anisotropy = useBakedConstants ? bakedLight.anisotropy : volumetricLight.Anisotropy;
		scattering = useBakedConstants ? bakedLight.scattering : currentScattering;
		radiusSq = useBakedConstants ? bakedLight.radiusSq : volumetricLight.Radius * volumetricLight.Radius;
		lightRange = currentRange;
		float spotFactor = light.type == LightType.Spot
			? (useBakedConstants ? bakedLight.spotFactor : ComputeSpotLightScoreFactor(light.spotAngle))
			: 1.0f;

		float distanceWeight = (currentRange * currentRange) / Mathf.Max(distanceSq, 1.0f);
		score = scattering * currentIntensity * distanceWeight * spotFactor;
		return score > 0.0f;
	}

	/// <summary>
	/// Returns whether cached baked data can be used for this light.
	/// </summary>
	/// <param name="light"></param>
	/// <param name="bakedLight"></param>
	/// <returns></returns>
	private static bool TryGetCachedBakedAdditionalLightData(Light light, out BakedAdditionalLightData bakedLight)
	{
		bakedLight = default;
		if (!hasValidStaticLightsBake || light == null || !BakedAdditionalLightsByInstanceId.TryGetValue(light.GetInstanceID(), out bakedLight))
			return false;

		if (bakedLight.light != light || bakedLight.volumetricLight == null)
			return false;

		if (!bakedLight.volumetricLight.enabled || !bakedLight.volumetricLight.gameObject.activeInHierarchy)
			return false;

		return true;
	}

	/// <summary>
	/// Returns whether baked constants are still valid for this light.
	/// </summary>
	/// <param name="light"></param>
	/// <param name="volumetricLight"></param>
	/// <param name="bakedLight"></param>
	/// <param name="range"></param>
	/// <param name="intensity"></param>
	/// <param name="scattering"></param>
	/// <returns></returns>
	private static bool AreBakedAdditionalLightConstantsStillValid(Light light, VolumetricAdditionalLight volumetricLight, in BakedAdditionalLightData bakedLight, float range, float intensity, float scattering)
	{
		const float Epsilon = 0.0001f;
		if (light == null || volumetricLight == null)
			return false;

		if (Mathf.Abs(range - bakedLight.range) > Epsilon)
			return false;

		if (Mathf.Abs(intensity - bakedLight.intensity) > Epsilon)
			return false;

		if (Mathf.Abs(scattering - bakedLight.scattering) > Epsilon)
			return false;

		if (Mathf.Abs(volumetricLight.Anisotropy - bakedLight.anisotropy) > Epsilon)
			return false;

		if (Mathf.Abs(volumetricLight.Radius * volumetricLight.Radius - bakedLight.radiusSq) > Epsilon)
			return false;

		if (light.type == LightType.Spot && Mathf.Abs(light.spotAngle - bakedLight.spotAngle) > Epsilon)
			return false;

		return true;
	}

	/// <summary>
	/// Computes the score factor multiplier used for spot lights.
	/// </summary>
	/// <param name="spotAngle"></param>
	/// <returns></returns>
	private static float ComputeSpotLightScoreFactor(float spotAngle)
	{
		float spotAngleCos = Mathf.Cos(0.5f * Mathf.Deg2Rad * spotAngle);
		return Mathf.Max(0.25f, spotAngleCos);
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

				if (!TryGetAdditionalLightCandidate(visibleLight, cameraPosition, fogDistance, fogMinHeight, fogMaxHeight, out float anisotropy, out float scattering, out float radiusSq, out float score, out Vector3 lightPosition, out float lightRange))
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
	private static bool TryGetAdditionalLightCandidate(in VisibleLight visibleLight, in Vector3 cameraPosition, float fogDistance, float fogMinHeight, float fogMaxHeight, out float anisotropy, out float scattering, out float radiusSq, out float score, out Vector3 lightPosition, out float lightRange)
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
	/// <param name="forceRebuild"></param>
	/// <param name="froxelHash"></param>
	/// <returns></returns>
	private static bool TryConfigureFroxelClusteredLights(Material material, Camera camera, int selectedAdditionalLightsCount, bool forceRebuild, bool debugRestrictToMainCameraFrustum, Vector4[] debugMainCameraFrustumPlanes, out int froxelHash)
	{
		froxelHash = 0;
		if (camera == null || selectedAdditionalLightsCount <= 0 || !SystemInfo.supportsComputeShaders)
			return false;

		froxelHash = ComputeFroxelInputHash(camera, selectedAdditionalLightsCount);

		EnsureFroxelBuffersAllocated();
		if (froxelMetaBuffer == null || froxelLightIndicesBuffer == null)
			return false;

		if (!isMaterialStateInitialized || froxelHash != cachedFroxelHash || forceRebuild)
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
	/// Ensures baked additional-light occlusion buffer is allocated.
	/// </summary>
	private static void EnsureBakedAdditionalLightOcclusionBufferAllocated()
	{
		int count = MaxVisibleAdditionalLights * BakedAdditionalLightOcclusionVoxelCount;
		if (bakedAdditionalLightOcclusionGridBuffer == null || bakedAdditionalLightOcclusionGridBuffer.count != count || bakedAdditionalLightOcclusionGridBuffer.stride != sizeof(float))
		{
			bakedAdditionalLightOcclusionGridBuffer?.Release();
			bakedAdditionalLightOcclusionGridBuffer = new ComputeBuffer(count, sizeof(float), ComputeBufferType.Structured);
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
		cachedSceneViewMainCameraFrustumMaskEnabled = false;
		cachedFroxelClusteredLightsEnabled = false;
#if UNITY_2023_1_OR_NEWER
		cachedAPVContributionEnabled = false;
#endif
		cachedAdditionalLightsCount = int.MinValue;
		cachedLightsHash = int.MinValue;
		cachedMainCameraFrustumPlanesHash = int.MinValue;
		cachedFroxelHash = int.MinValue;
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

	/// <summary>
	/// Releases clustered froxel GPU buffers.
	/// </summary>
	private static void ReleaseFroxelBuffers()
	{
		froxelMetaBuffer?.Release();
		froxelMetaBuffer = null;

		froxelLightIndicesBuffer?.Release();
		froxelLightIndicesBuffer = null;

		bakedAdditionalLightOcclusionGridBuffer?.Release();
		bakedAdditionalLightOcclusionGridBuffer = null;
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
		ResetStaticLightsBakeCache();
	}

	#endregion
}
