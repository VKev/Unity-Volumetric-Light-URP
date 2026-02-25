using System;
using UnityEngine;

/// <summary>
/// Serialized baked static light data used by hybrid baked runtime evaluation.
/// </summary>
[Serializable]
public struct VolumetricFogBakedStaticLightData
{
	[Tooltip("UnityEngine.LightType numeric value.")]
	public int lightType;
	[Tooltip("Linear light color multiplied by light intensity.")]
	public Vector3 color;
	[Tooltip("World-space light position.")]
	public Vector3 position;
	[Tooltip("World-space light forward direction.")]
	public Vector3 direction;
	[Tooltip("URP additional light attenuation params (xy distance attenuation, zw spot attenuation).")]
	public Vector4 attenuation;
	[Tooltip("Sqr range used for range clipping.")]
	public float rangeSq;
	[Tooltip("Inverse range squared used by URP distance attenuation.")]
	public float invRangeSq;
	[Tooltip("Additional light near-origin smoothing radius squared.")]
	public float radiusSq;
	[Tooltip("Volumetric scattering weight for this baked light.")]
	public float scattering;
	[Tooltip("Anisotropy parameter for this baked light.")]
	public float anisotropy;
	[Tooltip("Spot attenuation scale.")]
	public float spotScale;
	[Tooltip("Spot attenuation offset.")]
	public float spotOffset;
}

/// <summary>
/// Holds baked volumetric lighting data sampled by the fog shader.
/// </summary>
[CreateAssetMenu(fileName = "VolumetricFogBakedData", menuName = "Rendering/Volumetric Fog/Baked Data")]
public sealed class VolumetricFogBakedData : ScriptableObject
{
	#region Private Attributes

	[Tooltip("3D texture containing baked volumetric lighting in RGB.")]
	[SerializeField] private Texture3D lightingTexture;
	[Tooltip("3D texture containing baked dominant lighting direction in RGB.")]
	[SerializeField] private Texture3D directionTexture;
	[Tooltip("3D array storing precomputed shadow visibility per baked static light (one volume slice per light).")]
	[SerializeField] private Texture3DArray staticVisibilityTextureArray;
	[Tooltip("Serialized static baked light parameters used at runtime with the precomputed visibility volume.")]
	[SerializeField] private VolumetricFogBakedStaticLightData[] staticLights = Array.Empty<VolumetricFogBakedStaticLightData>();
	[Tooltip("World-space center of the baked volume bounds.")]
	[SerializeField] private Vector3 boundsCenter = new Vector3(0.0f, 8.0f, 0.0f);
	[Tooltip("World-space size of the baked volume bounds.")]
	[SerializeField] private Vector3 boundsSize = new Vector3(64.0f, 32.0f, 64.0f);
	[Tooltip("Bake resolution in X.")]
	[SerializeField, Min(4)] private int resolutionX = 64;
	[Tooltip("Bake resolution in Y.")]
	[SerializeField, Min(4)] private int resolutionY = 32;
	[Tooltip("Bake resolution in Z.")]
	[SerializeField, Min(4)] private int resolutionZ = 64;
	[Tooltip("Number of baked lights captured in the current baked texture.")]
	[SerializeField, Min(0)] private int bakedLightsCount = 0;
	[Tooltip("When enabled, baking tests visibility against scene colliders so baked volumetric lighting can be blocked by geometry.")]
	[SerializeField] private bool enableShadowOcclusion = true;
	[Tooltip("Layer mask used to test baked shadow occlusion against colliders.")]
	[SerializeField] private LayerMask occluderLayerMask = ~0;
	[Tooltip("When enabled, baked shadow rays will only consider static scene geometry as occluders.")]
	[SerializeField] private bool staticOccludersOnly = true;
	[Tooltip("When enabled, temporary mesh colliders are created for MeshRenderers that have no Collider so shadow bake can include render geometry too.")]
	[SerializeField] private bool createTemporaryMeshColliders = true;
	[Tooltip("Bias used on baked shadow rays to avoid immediate self-hits.")]
	[SerializeField, Min(0.0f)] private float shadowRayBias = 0.02f;
	[Tooltip("Maximum ray distance for directional baked shadow occlusion.")]
	[SerializeField, Min(1.0f)] private float directionalShadowDistance = 500.0f;
	[Tooltip("When enabled, soft shadows are approximated by multi-ray cone sampling during bake.")]
	[SerializeField] private bool enableSoftShadowSampling = true;
	[Tooltip("Number of rays used for soft shadow cone sampling. Higher values improve quality but increase bake time.")]
	[SerializeField, Range(1, 16)] private int softShadowSampleCount = 4;
	[Tooltip("Cone angle in degrees used for directional light soft shadow sampling.")]
	[SerializeField, Range(0.0f, 10.0f)] private float directionalSoftShadowConeAngle = 1.5f;
	[Tooltip("Cone angle in degrees used for point/spot light soft shadow sampling.")]
	[SerializeField, Range(0.0f, 10.0f)] private float punctualSoftShadowConeAngle = 2.0f;

	#endregion

	#region Public Attributes

	public Texture3D LightingTexture => lightingTexture;
	public Texture3D DirectionTexture => directionTexture;
	public Texture3DArray StaticVisibilityTextureArray => staticVisibilityTextureArray;
	public VolumetricFogBakedStaticLightData[] StaticLights => staticLights;
	public int StaticLightsCount => staticLights != null ? staticLights.Length : 0;
	public Vector3 BoundsCenter => boundsCenter;
	public Vector3 BoundsSize => boundsSize;
	public int ResolutionX => resolutionX;
	public int ResolutionY => resolutionY;
	public int ResolutionZ => resolutionZ;
	public Vector3Int Resolution => new Vector3Int(resolutionX, resolutionY, resolutionZ);
	public int BakedLightsCount => bakedLightsCount;
	public bool EnableShadowOcclusion => enableShadowOcclusion;
	public int OccluderLayerMask => occluderLayerMask.value;
	public bool StaticOccludersOnly => staticOccludersOnly;
	public bool CreateTemporaryMeshColliders => createTemporaryMeshColliders;
	public float ShadowRayBias => shadowRayBias;
	public float DirectionalShadowDistance => directionalShadowDistance;
	public bool EnableSoftShadowSampling => enableSoftShadowSampling;
	public int SoftShadowSampleCount => softShadowSampleCount;
	public float DirectionalSoftShadowConeAngle => directionalSoftShadowConeAngle;
	public float PunctualSoftShadowConeAngle => punctualSoftShadowConeAngle;

	public bool HasStaticLightsData
	{
		get
		{
			return staticVisibilityTextureArray != null
				&& staticLights != null
				&& staticLights.Length > 0
				&& staticVisibilityTextureArray.volumeDepth >= staticLights.Length;
		}
	}

	public bool IsValid
	{
		get
		{
			bool hasLegacyBakedVolumes = lightingTexture != null && lightingTexture.depth > 1;
			bool hasStaticBakedVolumes = HasStaticLightsData;
			return (hasLegacyBakedVolumes || hasStaticBakedVolumes)
				&& boundsSize.x > 0.0001f
				&& boundsSize.y > 0.0001f
				&& boundsSize.z > 0.0001f
				&& resolutionX >= 4
				&& resolutionY >= 4
				&& resolutionZ >= 4;
		}
	}

	#endregion

	#region Methods

	public void SetLightingTexture(Texture3D texture)
	{
		lightingTexture = texture;
	}

	public void SetDirectionTexture(Texture3D texture)
	{
		directionTexture = texture;
	}

	public void SetStaticVisibilityTextureArray(Texture3DArray textureArray)
	{
		staticVisibilityTextureArray = textureArray;
	}

	public void SetStaticLights(VolumetricFogBakedStaticLightData[] bakedLights)
	{
		staticLights = bakedLights ?? Array.Empty<VolumetricFogBakedStaticLightData>();
	}

	public void ClearStaticLightsData()
	{
		staticVisibilityTextureArray = null;
		staticLights = Array.Empty<VolumetricFogBakedStaticLightData>();
	}

	public void SetBakedLightsCount(int count)
	{
		bakedLightsCount = Mathf.Max(0, count);
	}

	#endregion

	#region ScriptableObject Methods

	private void OnValidate()
	{
		boundsSize.x = Mathf.Max(0.0001f, boundsSize.x);
		boundsSize.y = Mathf.Max(0.0001f, boundsSize.y);
		boundsSize.z = Mathf.Max(0.0001f, boundsSize.z);
		resolutionX = Mathf.Clamp(resolutionX, 4, 256);
		resolutionY = Mathf.Clamp(resolutionY, 4, 256);
		resolutionZ = Mathf.Clamp(resolutionZ, 4, 256);
		bakedLightsCount = Mathf.Max(0, bakedLightsCount);
		shadowRayBias = Mathf.Max(0.0f, shadowRayBias);
		directionalShadowDistance = Mathf.Max(1.0f, directionalShadowDistance);
		softShadowSampleCount = Mathf.Clamp(softShadowSampleCount, 1, 16);
		directionalSoftShadowConeAngle = Mathf.Clamp(directionalSoftShadowConeAngle, 0.0f, 10.0f);
		punctualSoftShadowConeAngle = Mathf.Clamp(punctualSoftShadowConeAngle, 0.0f, 10.0f);
		if (staticLights == null)
			staticLights = Array.Empty<VolumetricFogBakedStaticLightData>();
	}

	#endregion
}
