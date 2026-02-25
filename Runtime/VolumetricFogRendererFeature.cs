using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// The volumetric fog renderer feature.
/// </summary>
[Tooltip("Adds support to render volumetric fog.")]
[DisallowMultipleRendererFeature("Volumetric Fog")]
public sealed class VolumetricFogRendererFeature : ScriptableRendererFeature
{
	#region Private Attributes

	[HideInInspector]
	[SerializeField] private Shader downsampleDepthShader;
	[HideInInspector]
	[SerializeField] private Shader volumetricFogShader;
	[Tooltip("Render volumetric fog in Scene View cameras.")]
	[SerializeField] private bool renderInSceneView;
	[Tooltip("Render volumetric fog in overlay cameras from camera stacks.")]
	[SerializeField] private bool renderInOverlayCameras;
	[Tooltip("In Scene View, render fog only inside the Main Camera frustum so you can preview the game camera region.")]
	[SerializeField] private bool renderMainCameraRegionInSceneView;
	[Header("Debug")]
	[Tooltip("Draw froxel cluster debug lines in Scene view. Useful to verify clustered light partitioning.")]
	[SerializeField] private bool debugDrawFroxelClusters;
	[Tooltip("When enabled, only draw froxel cells that have at least one clustered light.")]
	[SerializeField] private bool debugDrawOnlyOccupiedFroxels = true;
	[Tooltip("Maximum number of froxel cells drawn each frame to keep debug cost bounded.")]
	[Min(1)]
	[SerializeField] private int debugMaxFroxelsToDraw = 256;
	[Tooltip("Draw debug cells as world-space cubes instead of frustum-shaped froxels.")]
	[SerializeField] private bool debugDrawWorldSpaceCubes = true;
	[Tooltip("Opacity for solid debug cube fill (world-space cube mode).")]
	[Range(0.0f, 1.0f)]
	[SerializeField] private float debugWorldSpaceCubeFillOpacity = 0.08f;
	[Tooltip("Color used for froxel debug lines.")]
	[SerializeField] private Color debugFroxelColor = new Color(0.0f, 1.0f, 1.0f, 1.0f);

	private Material downsampleDepthMaterial;
	private Material volumetricFogMaterial;
	private Camera lastKnownMainCamera;

	private VolumetricFogRenderPass volumetricFogRenderPass;

	#endregion

	#region Scriptable Renderer Feature Methods

	/// <summary>
	/// <inheritdoc/>
	/// </summary>
	public override void Create()
	{
		EnsureVolumetricFogRenderPassInitialized(true);
	}

	/// <summary>
	/// <inheritdoc/>
	/// </summary>
	/// <param name="renderer"></param>
	/// <param name="renderingData"></param>
	public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
	{
		if (!EnsureVolumetricFogRenderPassInitialized(false))
			return;

		Camera mainCamera = Camera.main;
		if (mainCamera != null)
			lastKnownMainCamera = mainCamera;
		Camera scenePreviewMainCamera = mainCamera != null ? mainCamera : lastKnownMainCamera;

		VolumetricFogVolumeComponent fogVolume = VolumeManager.instance.stack.GetComponent<VolumetricFogVolumeComponent>();
		bool shouldPreviewMainCameraRegionInSceneView = ShouldPreviewMainCameraRegionInSceneView(renderingData.cameraData, scenePreviewMainCamera);
		bool isPostProcessEnabled = renderingData.postProcessingEnabled && renderingData.cameraData.postProcessEnabled;
		bool shouldAddVolumetricFogRenderPass = isPostProcessEnabled && ShouldAddVolumetricFogRenderPass(renderingData.cameraData, shouldPreviewMainCameraRegionInSceneView, fogVolume);
		
		if (shouldAddVolumetricFogRenderPass)
		{
			volumetricFogRenderPass.SetupSceneViewMainCameraMask(shouldPreviewMainCameraRegionInSceneView, scenePreviewMainCamera);
			volumetricFogRenderPass.SetupFroxelDebugDrawing(debugDrawFroxelClusters, debugDrawOnlyOccupiedFroxels, debugMaxFroxelsToDraw, debugDrawWorldSpaceCubes, debugWorldSpaceCubeFillOpacity, debugFroxelColor);
			volumetricFogRenderPass.renderPassEvent = GetRenderPassEvent(fogVolume);
			volumetricFogRenderPass.ConfigureInput(ScriptableRenderPassInput.Depth);
			renderer.EnqueuePass(volumetricFogRenderPass);
		}
	}

	/// <summary>
	/// <inheritdoc/>
	/// </summary>
	/// <param name="disposing"></param>
	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);

		volumetricFogRenderPass?.Dispose();
		lastKnownMainCamera = null;

		CoreUtils.Destroy(downsampleDepthMaterial);
		CoreUtils.Destroy(volumetricFogMaterial);
	}

	#endregion

	#region Methods

	/// <summary>
	/// Ensures pass resources are valid and the render pass is constructed with the current materials.
	/// </summary>
	/// <param name="forceRefresh"></param>
	/// <returns></returns>
	private bool EnsureVolumetricFogRenderPassInitialized(bool forceRefresh)
	{
		bool resourcesOk = ValidateResourcesForVolumetricFogRenderPass(false);
		bool shouldRecreatePass = forceRefresh || !resourcesOk || volumetricFogRenderPass == null;
		if (shouldRecreatePass)
		{
			if (!ValidateResourcesForVolumetricFogRenderPass(true))
				return false;

			volumetricFogRenderPass?.Dispose();
			volumetricFogRenderPass = new VolumetricFogRenderPass(downsampleDepthMaterial, volumetricFogMaterial, VolumetricFogRenderPass.DefaultRenderPassEvent);
		}

		return volumetricFogRenderPass != null;
	}

	/// <summary>
	/// Validates the resources used by the volumetric fog render pass.
	/// </summary>
	/// <param name="forceRefresh"></param>
	/// <returns></returns>
	private bool ValidateResourcesForVolumetricFogRenderPass(bool forceRefresh)
	{
		if (forceRefresh)
		{
#if UNITY_EDITOR
			downsampleDepthShader = Shader.Find("Hidden/DownsampleDepth");
			volumetricFogShader = Shader.Find("Hidden/VolumetricFog");
#endif
			CoreUtils.Destroy(downsampleDepthMaterial);
			downsampleDepthMaterial = CoreUtils.CreateEngineMaterial(downsampleDepthShader);

			CoreUtils.Destroy(volumetricFogMaterial);
			volumetricFogMaterial = CoreUtils.CreateEngineMaterial(volumetricFogShader);
		}

		bool okDepth = downsampleDepthShader != null && downsampleDepthMaterial != null;
		bool okVolumetric = volumetricFogShader != null && volumetricFogMaterial != null;
		
		return okDepth && okVolumetric;
	}

	/// <summary>
	/// Gets whether the volumetric fog render pass should be enqueued to the renderer.
	/// </summary>
	/// <param name="cameraData"></param>
	/// <param name="shouldPreviewMainCameraRegionInSceneView"></param>
	/// <param name="fogVolume"></param>
	/// <returns></returns>
	private bool ShouldAddVolumetricFogRenderPass(CameraData cameraData, bool shouldPreviewMainCameraRegionInSceneView, VolumetricFogVolumeComponent fogVolume)
	{
		CameraType cameraType = cameraData.cameraType;
		bool isOverlayCamera = cameraData.renderType == CameraRenderType.Overlay;
		bool isSceneViewCamera = cameraType == CameraType.SceneView;

		bool isVolumeOk = fogVolume != null && fogVolume.IsActive();
		bool isCameraOk = cameraType != CameraType.Preview && cameraType != CameraType.Reflection;
		isCameraOk &= renderInSceneView || !isSceneViewCamera || shouldPreviewMainCameraRegionInSceneView;
		isCameraOk &= renderInOverlayCameras || !isOverlayCamera;
		bool areResourcesOk = ValidateResourcesForVolumetricFogRenderPass(false);

		return isActive && isVolumeOk && isCameraOk && areResourcesOk;
	}

	/// <summary>
	/// Gets whether scene view should preview volumetric fog only in the main camera frustum.
	/// </summary>
	/// <param name="cameraData"></param>
	/// <param name="mainCamera"></param>
	/// <returns></returns>
	private bool ShouldPreviewMainCameraRegionInSceneView(CameraData cameraData, Camera mainCamera)
	{
		if (!renderMainCameraRegionInSceneView || cameraData.cameraType != CameraType.SceneView)
			return false;

		return mainCamera != null;
	}

	/// <summary>
	/// Returns the render pass event for the volumetric fog.
	/// </summary>
	/// <param name="fogVolume"></param>
	/// <returns></returns>
	private RenderPassEvent GetRenderPassEvent(VolumetricFogVolumeComponent fogVolume)
	{
		return (RenderPassEvent)fogVolume.renderPassEvent.value;
	}

	#endregion
}
