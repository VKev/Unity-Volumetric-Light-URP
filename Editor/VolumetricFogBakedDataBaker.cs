using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Editor utility that bakes volumetric lighting from baked lights into a 3D texture asset.
/// </summary>
internal static class VolumetricFogBakedDataBaker
{
	private const int MaxBakedStaticLights = 64;
	private static readonly MethodInfo UrpGetLightAttenuationAndSpotDirectionMethod = ResolveUrpAttenuationMethod();

	private struct BakedOcclusionSettings
	{
		public bool enabled;
		public int layerMask;
		public bool staticOccludersOnly;
		public float rayBias;
		public float directionalDistance;
		public bool createTemporaryMeshColliders;
		public bool enableSoftShadowSampling;
		public int softShadowSampleCount;
		public float directionalSoftConeAngleRadians;
		public float punctualSoftConeAngleRadians;
	}

	private struct BakedLightSample
	{
		public LightType type;
		public Vector3 color;
		public Vector3 position;
		public Vector3 direction;
		public Vector4 attenuation;
		public float rangeSq;
		public float invRangeSq;
		public float radiusSq;
		public float scattering;
		public float anisotropy;
		public float spotScale;
		public float spotOffset;
		public bool castShadows;
		public bool softShadows;
		public float shadowStrength;
	}

	private struct BakedVoxelSample
	{
		public Vector3 color;
		public Vector3 dominantDirection;
		public float anisotropy;
	}

	/// <summary>
	/// Temporary helper that creates mesh colliders for render-only geometry during bake and cleans them afterwards.
	/// </summary>
	private sealed class TemporaryBakeColliderScope : IDisposable
	{
		private readonly List<GameObject> temporaryColliderObjects = new List<GameObject>(256);

		public int CreatedCollidersCount => temporaryColliderObjects.Count;

		public static TemporaryBakeColliderScope Create(int layerMask, bool staticOccludersOnly)
		{
			TemporaryBakeColliderScope scope = new TemporaryBakeColliderScope();
			scope.CreateTemporaryMeshColliders(layerMask, staticOccludersOnly);
			return scope;
		}

		public void Dispose()
		{
			for (int i = 0; i < temporaryColliderObjects.Count; ++i)
			{
				GameObject go = temporaryColliderObjects[i];
				if (go != null)
					UnityEngine.Object.DestroyImmediate(go);
			}

			temporaryColliderObjects.Clear();
		}

		private void CreateTemporaryMeshColliders(int layerMask, bool staticOccludersOnly)
		{
#if UNITY_2023_1_OR_NEWER
			Renderer[] renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
			Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
#endif

			for (int i = 0; i < renderers.Length; ++i)
			{
				Renderer renderer = renderers[i];
				if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
					continue;

				if (!(renderer is MeshRenderer))
					continue;

				if (staticOccludersOnly && !IsHierarchyStatic(renderer.gameObject))
					continue;

				if (renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off)
					continue;

				int rendererLayerBit = 1 << renderer.gameObject.layer;
				if ((layerMask & rendererLayerBit) == 0)
					continue;

				MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
				if (meshFilter == null || meshFilter.sharedMesh == null)
					continue;

				if (renderer.GetComponent<Collider>() != null)
					continue;

				GameObject tempColliderObject = new GameObject("__VolumetricFogBakeCollider");
				tempColliderObject.hideFlags = HideFlags.HideAndDontSave;
				tempColliderObject.isStatic = true;
				tempColliderObject.layer = renderer.gameObject.layer;
				tempColliderObject.transform.SetParent(renderer.transform, false);
				tempColliderObject.transform.localPosition = Vector3.zero;
				tempColliderObject.transform.localRotation = Quaternion.identity;
				tempColliderObject.transform.localScale = Vector3.one;

				MeshCollider meshCollider = tempColliderObject.AddComponent<MeshCollider>();
				meshCollider.sharedMesh = meshFilter.sharedMesh;
				meshCollider.convex = false;

				temporaryColliderObjects.Add(tempColliderObject);
			}
		}
	}

	/// <summary>
	/// Bakes baked-light volumetric contribution for the provided fog volume.
	/// </summary>
	/// <param name="fogVolume"></param>
	public static void BakeFromVolume(VolumetricFogVolumeComponent fogVolume)
	{
		if (fogVolume == null)
			return;

		VolumetricFogBakedData bakedData = fogVolume.bakedData.value;
		if (bakedData == null)
		{
			if (!TryCreateBakedDataAsset(out bakedData))
				return;

			Undo.RecordObject(fogVolume, "Assign Volumetric Fog Baked Data");
			fogVolume.bakedData.value = bakedData;
			fogVolume.bakedData.overrideState = true;
			EditorUtility.SetDirty(fogVolume);
		}

		if (!Bake(fogVolume, bakedData, out int bakedLightsCount))
			return;

		Undo.RecordObject(fogVolume, "Set Volumetric Fog Lighting Mode");
		fogVolume.lightingMode.value = bakedLightsCount > 0 ? VolumetricFogLightingMode.HybridBaked : VolumetricFogLightingMode.RuntimeOnly;
		fogVolume.lightingMode.overrideState = true;
		fogVolume.bakedData.overrideState = true;
		EditorUtility.SetDirty(fogVolume);

		if (bakedLightsCount > 0)
			Debug.Log($"Volumetric fog bake completed. Baked lights: {bakedLightsCount}. Mode switched to HybridBaked.", fogVolume);
		else
			Debug.LogWarning("Volumetric fog bake completed with 0 baked lights. Lighting mode stayed RuntimeOnly so realtime fallback is preserved.", fogVolume);
	}

	/// <summary>
	/// Clears current baked volumetric texture reference.
	/// </summary>
	/// <param name="fogVolume"></param>
	public static void ClearBakedTexture(VolumetricFogVolumeComponent fogVolume)
	{
		if (fogVolume == null)
			return;

		VolumetricFogBakedData bakedData = fogVolume.bakedData.value;
		if (bakedData == null)
			return;

		Undo.RecordObject(bakedData, "Clear Volumetric Fog Baked Texture");
		bakedData.SetLightingTexture(null);
		bakedData.SetDirectionTexture(null);
		if (bakedData.StaticVisibilityTextureArray != null)
			UnityEngine.Object.DestroyImmediate(bakedData.StaticVisibilityTextureArray, true);
		bakedData.ClearStaticLightsData();
		bakedData.SetBakedLightsCount(0);
		EditorUtility.SetDirty(bakedData);
		AssetDatabase.SaveAssets();
	}

	private static bool Bake(VolumetricFogVolumeComponent fogVolume, VolumetricFogBakedData bakedData, out int bakedLightsCount)
	{
		bakedLightsCount = 0;

		string bakedDataAssetPath = AssetDatabase.GetAssetPath(bakedData);
		if (string.IsNullOrEmpty(bakedDataAssetPath))
		{
			Debug.LogError("Volumetric fog baked data must be saved as an asset before baking.", fogVolume);
			return false;
		}

		List<BakedLightSample> bakedLights = CollectBakedLights(fogVolume, out int defaultAdditionalScatteringLightsCount);
		if (bakedLights.Count > MaxBakedStaticLights)
		{
			bakedLights.RemoveRange(MaxBakedStaticLights, bakedLights.Count - MaxBakedStaticLights);
			Debug.LogWarning($"Volumetric fog bake trimmed baked static lights to {MaxBakedStaticLights} for runtime shader limits.", fogVolume);
		}
		bakedLightsCount = bakedLights.Count;
		if (bakedLightsCount == 0)
			Debug.LogWarning("Volumetric fog bake found no lights configured as Baked that contribute to fog. Check each Light Mode and bake volume bounds. A black baked texture will be generated.", fogVolume);
		else if (defaultAdditionalScatteringLightsCount > 0)
			Debug.LogWarning($"Volumetric fog bake used default scattering for {defaultAdditionalScatteringLightsCount} baked point/spot lights that are missing VolumetricAdditionalLight.", fogVolume);
		if (bakedLightsCount > 0 && bakedData.EnableShadowOcclusion)
			Debug.Log("Volumetric fog bake: shadow occlusion sampling is enabled. Static baked lights will use precomputed shadow visibility.", fogVolume);

		int resolutionX = Mathf.Clamp(bakedData.ResolutionX, 4, 256);
		int resolutionY = Mathf.Clamp(bakedData.ResolutionY, 4, 256);
		int resolutionZ = Mathf.Clamp(bakedData.ResolutionZ, 4, 256);
		int voxelCount = resolutionX * resolutionY * resolutionZ;
		Color[] bakedLightingColors = new Color[voxelCount];
		Color[] bakedDirectionColors = new Color[voxelCount];

		Vector3 boundsSize = bakedData.BoundsSize;
		Vector3 boundsMin = bakedData.BoundsCenter - (boundsSize * 0.5f);
		float invResolutionX = 1.0f / resolutionX;
		float invResolutionY = 1.0f / resolutionY;
		float invResolutionZ = 1.0f / resolutionZ;
		int xyStride = resolutionX * resolutionY;
		BakedOcclusionSettings occlusionSettings = new BakedOcclusionSettings
		{
			enabled = bakedData.EnableShadowOcclusion,
			layerMask = bakedData.OccluderLayerMask,
			staticOccludersOnly = bakedData.StaticOccludersOnly,
			rayBias = Mathf.Max(0.0f, bakedData.ShadowRayBias),
			directionalDistance = Mathf.Max(1.0f, bakedData.DirectionalShadowDistance),
			createTemporaryMeshColliders = bakedData.CreateTemporaryMeshColliders,
			enableSoftShadowSampling = bakedData.EnableSoftShadowSampling,
			softShadowSampleCount = Mathf.Clamp(bakedData.SoftShadowSampleCount, 1, 16),
			directionalSoftConeAngleRadians = Mathf.Deg2Rad * Mathf.Max(0.0f, bakedData.DirectionalSoftShadowConeAngle),
			punctualSoftConeAngleRadians = Mathf.Deg2Rad * Mathf.Max(0.0f, bakedData.PunctualSoftShadowConeAngle)
		};

		using (TemporaryBakeColliderScope temporaryColliderScope = occlusionSettings.enabled && occlusionSettings.createTemporaryMeshColliders
			? TemporaryBakeColliderScope.Create(occlusionSettings.layerMask, occlusionSettings.staticOccludersOnly)
			: null)
		{
			if (temporaryColliderScope != null && temporaryColliderScope.CreatedCollidersCount > 0)
				Debug.Log($"Volumetric fog bake: created {temporaryColliderScope.CreatedCollidersCount} temporary mesh colliders for occlusion sampling ({(occlusionSettings.staticOccludersOnly ? "static-only" : "all layer objects")}).", fogVolume);

			try
			{
				for (int z = 0; z < resolutionZ; ++z)
				{
					float progress = resolutionZ > 1 ? z / (float)(resolutionZ - 1) : 1.0f;
					if (EditorUtility.DisplayCancelableProgressBar("Volumetric Fog Bake", "Baking baked lights into volumetric texture...", progress))
					{
						Debug.LogWarning("Volumetric fog bake cancelled by user.", fogVolume);
						return false;
					}

					float vz = (z + 0.5f) * invResolutionZ;
					float positionZ = boundsMin.z + vz * boundsSize.z;

					for (int y = 0; y < resolutionY; ++y)
					{
						float vy = (y + 0.5f) * invResolutionY;
						float positionY = boundsMin.y + vy * boundsSize.y;
						int rowOffset = y * resolutionX + z * xyStride;

						for (int x = 0; x < resolutionX; ++x)
						{
							float vx = (x + 0.5f) * invResolutionX;
							float positionX = boundsMin.x + vx * boundsSize.x;
							Vector3 positionWS = new Vector3(positionX, positionY, positionZ);

							BakedVoxelSample sample = EvaluateBakedLightingAtPosition(positionWS, bakedLights, occlusionSettings);
							int voxelIndex = rowOffset + x;
							float anisotropyEncoded = Mathf.Clamp01(sample.anisotropy * 0.5f + 0.5f);
							Vector3 directionEncoded = sample.dominantDirection * 0.5f + Vector3.one * 0.5f;
							bakedLightingColors[voxelIndex] = new Color(sample.color.x, sample.color.y, sample.color.z, anisotropyEncoded);
							bakedDirectionColors[voxelIndex] = new Color(directionEncoded.x, directionEncoded.y, directionEncoded.z, 1.0f);
						}
					}
				}
			}
			finally
			{
				EditorUtility.ClearProgressBar();
			}
		}

		Texture3D bakedLightingTexture = GetOrCreateBakedTextureAsset(bakedData, resolutionX, resolutionY, resolutionZ, isDirectionTexture: false);
		Texture3D bakedDirectionTexture = GetOrCreateBakedTextureAsset(bakedData, resolutionX, resolutionY, resolutionZ, isDirectionTexture: true);
		Texture2DArray bakedVisibilityTextureArray = GetOrCreateBakedVisibilityTextureArrayAsset(bakedData, resolutionX, resolutionY, resolutionZ, bakedLightsCount);
		if (bakedLightingTexture == null || bakedDirectionTexture == null)
			return false;

		if (bakedLightsCount <= 0 && bakedData.StaticVisibilityTextureArray != null)
			UnityEngine.Object.DestroyImmediate(bakedData.StaticVisibilityTextureArray, true);

		bakedLightingTexture.SetPixels(bakedLightingColors);
		bakedLightingTexture.Apply(false, false);
		bakedDirectionTexture.SetPixels(bakedDirectionColors);
		bakedDirectionTexture.Apply(false, false);

		if (bakedLightsCount > 0)
		{
			if (bakedVisibilityTextureArray == null)
				return false;

			if (!BakeStaticVisibilityTextureArray(bakedVisibilityTextureArray, bakedLights, occlusionSettings, boundsMin, boundsSize, resolutionX, resolutionY, resolutionZ, fogVolume))
				return false;
		}

		VolumetricFogBakedStaticLightData[] staticLightsData = BuildStaticLightsData(bakedLights);

		Undo.RecordObject(bakedData, "Bake Volumetric Fog Lighting");
		bakedData.SetLightingTexture(bakedLightingTexture);
		bakedData.SetDirectionTexture(bakedDirectionTexture);
		bakedData.SetStaticVisibilityTextureArray(bakedLightsCount > 0 ? bakedVisibilityTextureArray : null);
		bakedData.SetStaticLights(staticLightsData);
		bakedData.SetBakedLightsCount(bakedLightsCount);
		EditorUtility.SetDirty(bakedLightingTexture);
		EditorUtility.SetDirty(bakedDirectionTexture);
		if (bakedVisibilityTextureArray != null)
			EditorUtility.SetDirty(bakedVisibilityTextureArray);
		EditorUtility.SetDirty(bakedData);

		AssetDatabase.SaveAssets();
		AssetDatabase.ImportAsset(bakedDataAssetPath, ImportAssetOptions.ForceUpdate);

		return true;
	}

	private static Texture3D GetOrCreateBakedTextureAsset(VolumetricFogBakedData bakedData, int resolutionX, int resolutionY, int resolutionZ, bool isDirectionTexture)
	{
		Texture3D texture = isDirectionTexture ? bakedData.DirectionTexture : bakedData.LightingTexture;
		bool needsCreate = texture == null
			|| texture.width != resolutionX
			|| texture.height != resolutionY
			|| texture.depth != resolutionZ
			|| texture.format != TextureFormat.RGBAHalf;

		if (needsCreate)
		{
			if (texture != null)
				UnityEngine.Object.DestroyImmediate(texture, true);

			texture = new Texture3D(resolutionX, resolutionY, resolutionZ, TextureFormat.RGBAHalf, false, true);
			texture.name = isDirectionTexture ? "VolumetricFogBakedDirection" : "VolumetricFogBakedLighting";
			texture.wrapMode = TextureWrapMode.Clamp;
			texture.filterMode = FilterMode.Bilinear;
			texture.anisoLevel = 0;
			texture.hideFlags = HideFlags.HideInHierarchy;
			AssetDatabase.AddObjectToAsset(texture, bakedData);
		}
		else
		{
			texture.wrapMode = TextureWrapMode.Clamp;
			texture.filterMode = FilterMode.Bilinear;
			texture.anisoLevel = 0;
			texture.hideFlags = HideFlags.HideInHierarchy;
		}

		return texture;
	}

	private static Texture2DArray GetOrCreateBakedVisibilityTextureArrayAsset(VolumetricFogBakedData bakedData, int resolutionX, int resolutionY, int resolutionZ, int lightCount)
	{
		Texture2DArray textureArray = bakedData.StaticVisibilityTextureArray;
		if (lightCount <= 0)
			return null;

		int layerCount = Mathf.Max(1, resolutionZ) * lightCount;
		bool needsCreate = textureArray == null
			|| textureArray.width != resolutionX
			|| textureArray.height != resolutionY
			|| textureArray.depth != layerCount
			|| textureArray.format != TextureFormat.RGBAHalf;

		if (needsCreate)
		{
			if (textureArray != null)
				UnityEngine.Object.DestroyImmediate(textureArray, true);

			textureArray = new Texture2DArray(resolutionX, resolutionY, layerCount, TextureFormat.RGBAHalf, false, true);
			textureArray.name = "VolumetricFogBakedVisibility";
			textureArray.wrapMode = TextureWrapMode.Clamp;
			textureArray.filterMode = FilterMode.Bilinear;
			textureArray.anisoLevel = 0;
			textureArray.hideFlags = HideFlags.HideInHierarchy;
			AssetDatabase.AddObjectToAsset(textureArray, bakedData);
		}
		else
		{
			textureArray.wrapMode = TextureWrapMode.Clamp;
			textureArray.filterMode = FilterMode.Bilinear;
			textureArray.anisoLevel = 0;
			textureArray.hideFlags = HideFlags.HideInHierarchy;
		}

		return textureArray;
	}

	private static bool BakeStaticVisibilityTextureArray(Texture2DArray visibilityTextureArray, List<BakedLightSample> bakedLights, in BakedOcclusionSettings occlusionSettings, Vector3 boundsMin, Vector3 boundsSize, int resolutionX, int resolutionY, int resolutionZ, VolumetricFogVolumeComponent fogVolume)
	{
		if (visibilityTextureArray == null || bakedLights == null || bakedLights.Count == 0)
			return true;

		float invResolutionX = 1.0f / resolutionX;
		float invResolutionY = 1.0f / resolutionY;
		float invResolutionZ = 1.0f / resolutionZ;
		int xySliceCount = resolutionX * resolutionY;

		using (TemporaryBakeColliderScope temporaryColliderScope = occlusionSettings.enabled && occlusionSettings.createTemporaryMeshColliders
			? TemporaryBakeColliderScope.Create(occlusionSettings.layerMask, occlusionSettings.staticOccludersOnly)
			: null)
		{
			if (temporaryColliderScope != null && temporaryColliderScope.CreatedCollidersCount > 0)
				Debug.Log($"Volumetric fog bake: created {temporaryColliderScope.CreatedCollidersCount} temporary mesh colliders for static visibility volume ({(occlusionSettings.staticOccludersOnly ? "static-only" : "all layer objects")}).", fogVolume);

			try
			{
				for (int lightIndex = 0; lightIndex < bakedLights.Count; ++lightIndex)
				{
					BakedLightSample light = bakedLights[lightIndex];

					for (int z = 0; z < resolutionZ; ++z)
					{
						Color[] sliceVisibility = new Color[xySliceCount];
						float globalProgress = ((lightIndex * resolutionZ) + z) / (float)Mathf.Max(1, bakedLights.Count * resolutionZ - 1);
						if (EditorUtility.DisplayCancelableProgressBar("Volumetric Fog Bake", $"Baking static shadow visibility ({lightIndex + 1}/{bakedLights.Count})...", globalProgress))
						{
							Debug.LogWarning("Volumetric fog visibility bake cancelled by user.", fogVolume);
							return false;
						}

						float vz = (z + 0.5f) * invResolutionZ;
						float positionZ = boundsMin.z + vz * boundsSize.z;

						for (int y = 0; y < resolutionY; ++y)
						{
							float vy = (y + 0.5f) * invResolutionY;
							float positionY = boundsMin.y + vy * boundsSize.y;
							int rowOffset = y * resolutionX;

							for (int x = 0; x < resolutionX; ++x)
							{
								float vx = (x + 0.5f) * invResolutionX;
								float positionX = boundsMin.x + vx * boundsSize.x;
								Vector3 positionWS = new Vector3(positionX, positionY, positionZ);
								float visibility = Mathf.Clamp01(ComputeShadowVisibility(positionWS, light, occlusionSettings));
								int sliceIndex = rowOffset + x;
								sliceVisibility[sliceIndex] = new Color(visibility, 0.0f, 0.0f, 1.0f);
							}
						}

						int arrayLayer = lightIndex * resolutionZ + z;
						visibilityTextureArray.SetPixels(sliceVisibility, arrayLayer, 0);
					}
				}
			}
			finally
			{
				EditorUtility.ClearProgressBar();
			}
		}

		visibilityTextureArray.Apply(false, false);
		return true;
	}

	private static VolumetricFogBakedStaticLightData[] BuildStaticLightsData(List<BakedLightSample> bakedLights)
	{
		if (bakedLights == null || bakedLights.Count == 0)
			return Array.Empty<VolumetricFogBakedStaticLightData>();

		VolumetricFogBakedStaticLightData[] staticLightsData = new VolumetricFogBakedStaticLightData[bakedLights.Count];
		for (int i = 0; i < bakedLights.Count; ++i)
		{
			BakedLightSample bakedLight = bakedLights[i];
			staticLightsData[i] = new VolumetricFogBakedStaticLightData
			{
				lightType = (int)bakedLight.type,
				color = bakedLight.color,
				position = bakedLight.position,
				direction = bakedLight.direction,
				attenuation = bakedLight.attenuation,
				rangeSq = bakedLight.rangeSq,
				invRangeSq = bakedLight.invRangeSq,
				radiusSq = bakedLight.radiusSq,
				scattering = bakedLight.scattering,
				anisotropy = bakedLight.anisotropy,
				spotScale = bakedLight.spotScale,
				spotOffset = bakedLight.spotOffset
			};
		}

		return staticLightsData;
	}

	private static List<BakedLightSample> CollectBakedLights(VolumetricFogVolumeComponent fogVolume, out int defaultAdditionalScatteringLightsCount)
	{
		List<BakedLightSample> bakedLights = new List<BakedLightSample>(32);
		defaultAdditionalScatteringLightsCount = 0;

#if UNITY_2023_1_OR_NEWER
		Light[] sceneLights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
		Light[] sceneLights = UnityEngine.Object.FindObjectsOfType<Light>();
#endif

		Color tintLinear = fogVolume.tint.value.linear;
		Vector3 mainLightTint = new Vector3(tintLinear.r, tintLinear.g, tintLinear.b);
		float mainLightScattering = Mathf.Max(0.0f, fogVolume.scattering.value);

		for (int i = 0; i < sceneLights.Length; ++i)
		{
			Light light = sceneLights[i];
			if (light == null || !light.enabled || !light.gameObject.activeInHierarchy)
				continue;

			if (!IsLightConfiguredAsBaked(light))
				continue;

			if (light.type != LightType.Directional && light.type != LightType.Point && light.type != LightType.Spot)
				continue;

			Color lightColorLinear = light.color.linear * Mathf.Max(0.0f, light.intensity);
			Vector3 color = new Vector3(lightColorLinear.r, lightColorLinear.g, lightColorLinear.b);

			float scattering;
			float anisotropy;
			if (light.type == LightType.Directional)
			{
				if (mainLightScattering <= 0.0001f)
					continue;

				scattering = mainLightScattering;
				anisotropy = Mathf.Clamp(fogVolume.anisotropy.value, -0.99f, 0.99f);
				color = Vector3.Scale(color, mainLightTint);
			}
			else if (light.TryGetComponent(out VolumetricAdditionalLight volumetricAdditionalLight))
			{
				scattering = Mathf.Max(0.0f, volumetricAdditionalLight.Scattering);
				anisotropy = Mathf.Clamp(volumetricAdditionalLight.Anisotropy, -0.99f, 0.99f);
			}
			else
			{
				// Allow baked contribution for point/spot lights without VolumetricAdditionalLight using a default scattering.
				scattering = 1.0f;
				anisotropy = 0.25f;
				defaultAdditionalScatteringLightsCount++;
			}

			if (scattering <= 0.0001f)
				continue;

			BakedLightSample bakedLight = new BakedLightSample
			{
				type = light.type,
				color = color,
				position = light.transform.position,
				direction = light.transform.forward.normalized,
				attenuation = new Vector4(1.0f / Mathf.Max(light.range * light.range, 0.0001f), 0.0f, 0.0f, 1.0f),
				rangeSq = Mathf.Max(light.range * light.range, 0.0001f),
				scattering = scattering,
				anisotropy = anisotropy,
				invRangeSq = 0.0f,
				radiusSq = 0.04f,
				spotScale = 0.0f,
				spotOffset = 0.0f,
				castShadows = light.shadows != LightShadows.None,
				softShadows = light.shadows == LightShadows.Soft,
				shadowStrength = Mathf.Clamp01(light.shadowStrength)
			};

			if (light.type == LightType.Point || light.type == LightType.Spot)
			{
				bakedLight.invRangeSq = 1.0f / bakedLight.rangeSq;
				TryGetUrpLightAttenuationAndSpotDirection(light, out Vector4 urpAttenuation, out Vector3 urpSpotDirection);
				bakedLight.attenuation = urpAttenuation;
				bakedLight.direction = urpSpotDirection.sqrMagnitude > 0.000001f ? urpSpotDirection.normalized : bakedLight.direction;
			}

			if (light.type == LightType.Point || light.type == LightType.Spot)
			{
				float radius = 0.2f;
				if (light.TryGetComponent(out VolumetricAdditionalLight volumetricAdditionalLightRadius))
					radius = volumetricAdditionalLightRadius.Radius;

				bakedLight.radiusSq = Mathf.Max(radius * radius, 0.000001f);
			}

			if (light.type == LightType.Spot)
			{
				float outerCos = Mathf.Cos(0.5f * Mathf.Deg2Rad * light.spotAngle);
				float innerSpotAngle = Mathf.Min(light.innerSpotAngle, light.spotAngle);
				float innerCos = Mathf.Cos(0.5f * Mathf.Deg2Rad * innerSpotAngle);
				float denom = Mathf.Max(0.0001f, innerCos - outerCos);
				bakedLight.spotScale = 1.0f / denom;
				bakedLight.spotOffset = -outerCos * bakedLight.spotScale;
			}

			bakedLights.Add(bakedLight);
		}

		return bakedLights;
	}

	private static BakedVoxelSample EvaluateBakedLightingAtPosition(Vector3 positionWS, List<BakedLightSample> bakedLights, in BakedOcclusionSettings occlusionSettings)
	{
		Vector3 accumulatedColor = Vector3.zero;
		Vector3 weightedDirection = Vector3.zero;
		float weightedAnisotropy = 0.0f;
		float totalWeight = 0.0f;

		for (int i = 0; i < bakedLights.Count; ++i)
		{
			BakedLightSample light = bakedLights[i];
			Vector3 directionToLight = Vector3.forward;
			float attenuation = 1.0f;

			if (light.type == LightType.Directional)
			{
				float visibility = ComputeShadowVisibility(positionWS, light, occlusionSettings);
				if (visibility <= 0.0001f)
					continue;

				directionToLight = -light.direction.normalized;
				attenuation = visibility;
			}
			else
			{
				Vector3 toLight = light.position - positionWS;
				float distanceSq = Vector3.Dot(toLight, toLight);
				if (distanceSq >= light.rangeSq)
					continue;

				float distance = Mathf.Sqrt(Mathf.Max(distanceSq, 0.000001f));
				directionToLight = toLight / distance;
				attenuation = EvaluatePunctualDistanceAndAngleAttenuation(light, directionToLight, distanceSq);

				// Match runtime smoothing near light origin to reduce noise and energy spike.
				float radiusSq = Mathf.Max(light.radiusSq, 0.000001f);
				float radialFade = Mathf.Clamp01(distanceSq / radiusSq);
				radialFade = radialFade * radialFade * (3.0f - (2.0f * radialFade));
				radialFade *= radialFade;
				attenuation *= radialFade;

				float visibility = ComputeShadowVisibility(positionWS, light, occlusionSettings);
				if (visibility <= 0.0001f)
					continue;

				attenuation *= visibility;
			}

			Vector3 contribution = light.color * (attenuation * light.scattering);
			accumulatedColor += contribution;

			float weight = Mathf.Max(0.000001f, GetLuminance(contribution));
			weightedDirection += directionToLight * weight;
			weightedAnisotropy += light.anisotropy * weight;
			totalWeight += weight;
		}

		BakedVoxelSample sample = new BakedVoxelSample
		{
			color = accumulatedColor,
			dominantDirection = Vector3.forward,
			anisotropy = 0.0f
		};

		if (totalWeight > 0.000001f)
		{
			Vector3 normalizedDirection = weightedDirection.normalized;
			if (normalizedDirection.sqrMagnitude > 0.000001f)
				sample.dominantDirection = normalizedDirection;

			sample.anisotropy = Mathf.Clamp(weightedAnisotropy / totalWeight, -0.99f, 0.99f);
		}

		return sample;
	}

	private static float GetLuminance(Vector3 color)
	{
		return color.x * 0.2126f + color.y * 0.7152f + color.z * 0.0722f;
	}

	private static float EvaluatePunctualDistanceAndAngleAttenuation(in BakedLightSample light, in Vector3 directionToLight, float distanceSq)
	{
		Vector4 attenuationParams = light.attenuation;
		float distanceAttenuation = EvaluateDistanceAttenuation(distanceSq, attenuationParams.x, attenuationParams.y);
		float angleAttenuation = light.type == LightType.Spot
			? EvaluateAngleAttenuation(light.direction, directionToLight, attenuationParams.z, attenuationParams.w)
			: 1.0f;
		return distanceAttenuation * angleAttenuation;
	}

	private static float EvaluateDistanceAttenuation(float distanceSq, float attenuationX, float attenuationY)
	{
		float safeDistanceSq = Mathf.Max(distanceSq, 0.0001f);

		// Keep compatibility with both URP attenuation packings:
		// - High quality uses only attenuationX (attenuationY ~= 0)
		// - Low quality uses attenuationX/attenuationY linear remap.
		float smoothFactor;
		if (Mathf.Abs(attenuationY) < 0.000001f)
		{
			float factor = safeDistanceSq * attenuationX;
			smoothFactor = 1.0f - factor * factor;
		}
		else
		{
			smoothFactor = safeDistanceSq * attenuationX + attenuationY;
		}

		smoothFactor = Mathf.Clamp01(smoothFactor);
		smoothFactor *= smoothFactor;
		return smoothFactor / safeDistanceSq;
	}

	private static float EvaluateAngleAttenuation(in Vector3 spotDirection, in Vector3 directionToLight, float scale, float offset)
	{
		float spotAttenuation = Mathf.Clamp01(Vector3.Dot(spotDirection, directionToLight) * scale + offset);
		return spotAttenuation * spotAttenuation;
	}

	private static float ComputeShadowVisibility(Vector3 positionWS, in BakedLightSample light, in BakedOcclusionSettings settings)
	{
		if (!settings.enabled || !light.castShadows)
			return 1.0f;

		Vector3 baseDirectionToLight;
		float maxDistance;

		if (light.type == LightType.Directional)
		{
			baseDirectionToLight = -light.direction.normalized;
			maxDistance = settings.directionalDistance;
		}
		else
		{
			Vector3 toLight = light.position - positionWS;
			float distance = Mathf.Sqrt(Mathf.Max(Vector3.Dot(toLight, toLight), 0.000001f));
			if (distance <= settings.rayBias)
				return 1.0f;

			baseDirectionToLight = toLight / distance;
			maxDistance = Mathf.Max(distance - settings.rayBias * 2.0f, 0.0f);
			if (maxDistance <= 0.0f)
				return 1.0f;
		}

		int sampleCount = 1;
		float coneAngleRadians = 0.0f;
		if (settings.enableSoftShadowSampling && light.softShadows)
		{
			sampleCount = Mathf.Clamp(settings.softShadowSampleCount, 1, 16);
			coneAngleRadians = light.type == LightType.Directional
				? settings.directionalSoftConeAngleRadians
				: settings.punctualSoftConeAngleRadians;
			if (coneAngleRadians <= 0.0001f)
				sampleCount = 1;
		}

		int visibleSamples = 0;
		for (int sampleIndex = 0; sampleIndex < sampleCount; ++sampleIndex)
		{
			Vector3 sampleDirection = sampleCount > 1
				? GetConeSampleDirection(baseDirectionToLight, sampleIndex, sampleCount, coneAngleRadians)
				: baseDirectionToLight;

			Vector3 rayOrigin = positionWS + sampleDirection * settings.rayBias;
			bool occluded = RaycastOccluder(rayOrigin, sampleDirection, maxDistance, settings);
			if (!occluded)
				visibleSamples++;
		}

		float visibility = visibleSamples / (float)sampleCount;
		return Mathf.Lerp(1.0f, visibility, light.shadowStrength);
	}

	private static bool RaycastOccluder(Vector3 rayOrigin, Vector3 rayDirection, float distance, in BakedOcclusionSettings settings)
	{
		if (distance <= 0.0f)
			return false;

		if (!settings.staticOccludersOnly)
			return Physics.Raycast(rayOrigin, rayDirection, distance, settings.layerMask, QueryTriggerInteraction.Ignore);

		RaycastHit[] hits = Physics.RaycastAll(rayOrigin, rayDirection, distance, settings.layerMask, QueryTriggerInteraction.Ignore);
		for (int i = 0; i < hits.Length; ++i)
		{
			Collider hitCollider = hits[i].collider;
			if (hitCollider == null)
				continue;

			if (IsHierarchyStatic(hitCollider.gameObject))
				return true;
		}

		return false;
	}

	private static Vector3 GetConeSampleDirection(Vector3 baseDirection, int sampleIndex, int sampleCount, float coneAngleRadians)
	{
		if (sampleCount <= 1 || coneAngleRadians <= 0.0001f)
			return baseDirection;

		BuildOrthonormalBasis(baseDirection, out Vector3 tangent, out Vector3 bitangent);

		float t = (sampleIndex + 0.5f) / sampleCount;
		float radius01 = Mathf.Sqrt(t);
		float phi = sampleIndex * 2.39996323f;
		float sinPhi = Mathf.Sin(phi);
		float cosPhi = Mathf.Cos(phi);
		float sampleAngle = coneAngleRadians * radius01;
		float sinSampleAngle = Mathf.Sin(sampleAngle);
		float cosSampleAngle = Mathf.Cos(sampleAngle);

		Vector3 sampleDirection = (baseDirection * cosSampleAngle)
			+ (tangent * (cosPhi * sinSampleAngle))
			+ (bitangent * (sinPhi * sinSampleAngle));
		return sampleDirection.normalized;
	}

	private static void BuildOrthonormalBasis(Vector3 normal, out Vector3 tangent, out Vector3 bitangent)
	{
		Vector3 up = Mathf.Abs(normal.y) < 0.99f ? Vector3.up : Vector3.right;
		tangent = Vector3.Cross(up, normal).normalized;
		bitangent = Vector3.Cross(normal, tangent);
	}

	private static bool IsHierarchyStatic(GameObject gameObject)
	{
		if (gameObject == null)
			return false;

		Transform current = gameObject.transform;
		while (current != null)
		{
			if (current.gameObject.isStatic)
				return true;

			current = current.parent;
		}

		return false;
	}

	private static MethodInfo ResolveUrpAttenuationMethod()
	{
		MethodInfo[] methods = typeof(UniversalRenderPipeline).GetMethods(BindingFlags.Public | BindingFlags.Static);
		for (int i = 0; i < methods.Length; ++i)
		{
			MethodInfo method = methods[i];
			if (method.Name != "GetLightAttenuationAndSpotDirection")
				continue;

			ParameterInfo[] parameters = method.GetParameters();
			if (parameters.Length < 6)
				continue;

			bool hasLightType = false;
			bool hasRange = false;
			bool hasMatrix = false;
			int outVector4Count = 0;

			for (int p = 0; p < parameters.Length; ++p)
			{
				ParameterInfo parameter = parameters[p];
				Type paramType = parameter.ParameterType;
				if (paramType == typeof(LightType))
					hasLightType = true;
				else if (paramType == typeof(Matrix4x4))
					hasMatrix = true;
				else if (paramType == typeof(float))
					hasRange = true;
				else if (parameter.IsOut && paramType == typeof(Vector4).MakeByRefType())
					outVector4Count++;
			}

			if (hasLightType && hasRange && hasMatrix && outVector4Count >= 2)
				return method;
		}

		return null;
	}

	private static bool TryGetUrpLightAttenuationAndSpotDirection(Light light, out Vector4 attenuation, out Vector3 spotDirection)
	{
		float rangeSq = Mathf.Max(light.range * light.range, 0.0001f);
		attenuation = new Vector4(1.0f / rangeSq, 0.0f, 0.0f, 1.0f);
		spotDirection = -light.transform.forward.normalized;

		if (light.type == LightType.Spot)
		{
			float outerCos = Mathf.Cos(0.5f * Mathf.Deg2Rad * light.spotAngle);
			float innerSpotAngle = Mathf.Min(light.innerSpotAngle, light.spotAngle);
			float innerCos = Mathf.Cos(0.5f * Mathf.Deg2Rad * innerSpotAngle);
			float denom = Mathf.Max(0.0001f, innerCos - outerCos);
			attenuation.z = 1.0f / denom;
			attenuation.w = -outerCos * attenuation.z;
		}

		MethodInfo method = UrpGetLightAttenuationAndSpotDirectionMethod;
		if (method == null)
			return false;

		ParameterInfo[] parameters = method.GetParameters();
		object[] args = new object[parameters.Length];
		int floatParamIndex = 0;
		List<int> outVectorIndices = new List<int>(2);

		for (int i = 0; i < parameters.Length; ++i)
		{
			ParameterInfo parameter = parameters[i];
			Type paramType = parameter.ParameterType;
			bool isOutVector4 = parameter.IsOut && paramType == typeof(Vector4).MakeByRefType();
			if (isOutVector4)
			{
				args[i] = null;
				outVectorIndices.Add(i);
				continue;
			}

			if (paramType == typeof(LightType))
			{
				args[i] = light.type;
				continue;
			}

			if (paramType == typeof(Matrix4x4))
			{
				args[i] = light.transform.localToWorldMatrix;
				continue;
			}

			if (paramType == typeof(float))
			{
				if (floatParamIndex == 0)
					args[i] = light.range;
				else if (floatParamIndex == 1)
					args[i] = light.spotAngle;
				else
					args[i] = light.innerSpotAngle;

				floatParamIndex++;
				continue;
			}

			if (paramType == typeof(float?))
			{
				args[i] = light.type == LightType.Spot ? (float?)light.innerSpotAngle : null;
				continue;
			}

			if (parameter.HasDefaultValue)
			{
				args[i] = parameter.DefaultValue;
			}
			else
			{
				Type targetType = paramType.IsByRef ? paramType.GetElementType() : paramType;
				args[i] = targetType != null && targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
			}
		}

		try
		{
			method.Invoke(null, args);
			if (outVectorIndices.Count < 2)
				return false;

			Vector4 outAttenuation = (Vector4)args[outVectorIndices[0]];
			Vector4 outSpotDirection = (Vector4)args[outVectorIndices[1]];
			attenuation = outAttenuation;
			Vector3 spotDir = new Vector3(outSpotDirection.x, outSpotDirection.y, outSpotDirection.z);
			spotDirection = spotDir.sqrMagnitude > 0.000001f ? spotDir.normalized : spotDirection;
			return true;
		}
		catch
		{
			return false;
		}
	}

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

	private static bool TryCreateBakedDataAsset(out VolumetricFogBakedData bakedData)
	{
		bakedData = null;

		string savePath = EditorUtility.SaveFilePanelInProject(
			"Create Volumetric Fog Baked Data",
			"VolumetricFogBakedData",
			"asset",
			"Select where to save the baked volumetric data asset.");

		if (string.IsNullOrEmpty(savePath))
			return false;

		bakedData = ScriptableObject.CreateInstance<VolumetricFogBakedData>();
		AssetDatabase.CreateAsset(bakedData, savePath);
		AssetDatabase.SaveAssets();
		AssetDatabase.ImportAsset(savePath, ImportAssetOptions.ForceUpdate);
		Selection.activeObject = bakedData;

		return true;
	}
}
