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
	[Tooltip("Render volumetric fog only for the camera tagged MainCamera.")]
	[SerializeField] private bool renderOnlyMainCameraView;
	[Tooltip("In Scene View, render fog only inside the Main Camera frustum so you can preview the game camera region.")]
	[SerializeField] private bool renderMainCameraRegionInSceneView;

	private Material downsampleDepthMaterial;
	private Material volumetricFogMaterial;

	private VolumetricFogRenderPass volumetricFogRenderPass;

	#endregion

	#region Scriptable Renderer Feature Methods

	/// <summary>
	/// <inheritdoc/>
	/// </summary>
	public override void Create()
	{
		ValidateResourcesForVolumetricFogRenderPass(true);

		volumetricFogRenderPass = new VolumetricFogRenderPass(downsampleDepthMaterial, volumetricFogMaterial, VolumetricFogRenderPass.DefaultRenderPassEvent);
	}

	/// <summary>
	/// <inheritdoc/>
	/// </summary>
	/// <param name="renderer"></param>
	/// <param name="renderingData"></param>
	public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
	{
		bool shouldPreviewMainCameraRegionInSceneView = ShouldPreviewMainCameraRegionInSceneView(renderingData.cameraData);
		bool isPostProcessEnabled = renderingData.postProcessingEnabled && renderingData.cameraData.postProcessEnabled;
		bool shouldAddVolumetricFogRenderPass = isPostProcessEnabled && ShouldAddVolumetricFogRenderPass(renderingData.cameraData, shouldPreviewMainCameraRegionInSceneView);
		
		if (shouldAddVolumetricFogRenderPass)
		{
			volumetricFogRenderPass.SetupSceneViewMainCameraMask(shouldPreviewMainCameraRegionInSceneView, Camera.main);
			volumetricFogRenderPass.renderPassEvent = GetRenderPassEvent();
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

		CoreUtils.Destroy(downsampleDepthMaterial);
		CoreUtils.Destroy(volumetricFogMaterial);
	}

	#endregion

	#region Methods

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
	/// <returns></returns>
	private bool ShouldAddVolumetricFogRenderPass(CameraData cameraData, bool shouldPreviewMainCameraRegionInSceneView)
	{
		VolumetricFogVolumeComponent fogVolume = VolumeManager.instance.stack.GetComponent<VolumetricFogVolumeComponent>();
		CameraType cameraType = cameraData.cameraType;
		Camera camera = cameraData.camera;
		bool isOverlayCamera = cameraData.renderType == CameraRenderType.Overlay;
		bool isSceneViewCamera = cameraType == CameraType.SceneView;
		bool isMainCamera = camera != null && camera.CompareTag("MainCamera");

		bool isVolumeOk = fogVolume != null && fogVolume.IsActive();
		bool isCameraOk = cameraType != CameraType.Preview && cameraType != CameraType.Reflection;
		isCameraOk &= renderInSceneView || !isSceneViewCamera || shouldPreviewMainCameraRegionInSceneView;
		isCameraOk &= renderInOverlayCameras || !isOverlayCamera;
		isCameraOk &= !renderOnlyMainCameraView || isMainCamera || shouldPreviewMainCameraRegionInSceneView;
		bool areResourcesOk = ValidateResourcesForVolumetricFogRenderPass(false);

		return isActive && isVolumeOk && isCameraOk && areResourcesOk;
	}

	/// <summary>
	/// Gets whether scene view should preview volumetric fog only in the main camera frustum.
	/// </summary>
	/// <param name="cameraData"></param>
	/// <returns></returns>
	private bool ShouldPreviewMainCameraRegionInSceneView(CameraData cameraData)
	{
		if (!renderMainCameraRegionInSceneView || cameraData.cameraType != CameraType.SceneView)
			return false;

		return Camera.main != null;
	}

	/// <summary>
	/// Returns the render pass event for the volumetric fog.
	/// </summary>
	/// <returns></returns>
	private RenderPassEvent GetRenderPassEvent()
	{
		VolumetricFogVolumeComponent fogVolume = VolumeManager.instance.stack.GetComponent<VolumetricFogVolumeComponent>();
		
		return (RenderPassEvent)fogVolume.renderPassEvent.value;
	}

	#endregion
}
