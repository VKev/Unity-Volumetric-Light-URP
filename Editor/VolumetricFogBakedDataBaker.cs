using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor utility that bakes volumetric lighting from baked lights into a 3D texture asset.
/// </summary>
internal static class VolumetricFogBakedDataBaker
{
	private struct BakedOcclusionSettings
	{
		public bool enabled;
		public int layerMask;
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

		public static TemporaryBakeColliderScope Create(int layerMask)
		{
			TemporaryBakeColliderScope scope = new TemporaryBakeColliderScope();
			scope.CreateTemporaryMeshColliders(layerMask);
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

		private void CreateTemporaryMeshColliders(int layerMask)
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
			rayBias = Mathf.Max(0.0f, bakedData.ShadowRayBias),
			directionalDistance = Mathf.Max(1.0f, bakedData.DirectionalShadowDistance),
			createTemporaryMeshColliders = bakedData.CreateTemporaryMeshColliders,
			enableSoftShadowSampling = bakedData.EnableSoftShadowSampling,
			softShadowSampleCount = Mathf.Clamp(bakedData.SoftShadowSampleCount, 1, 16),
			directionalSoftConeAngleRadians = Mathf.Deg2Rad * Mathf.Max(0.0f, bakedData.DirectionalSoftShadowConeAngle),
			punctualSoftConeAngleRadians = Mathf.Deg2Rad * Mathf.Max(0.0f, bakedData.PunctualSoftShadowConeAngle)
		};

		using (TemporaryBakeColliderScope temporaryColliderScope = occlusionSettings.enabled && occlusionSettings.createTemporaryMeshColliders
			? TemporaryBakeColliderScope.Create(occlusionSettings.layerMask)
			: null)
		{
			if (temporaryColliderScope != null && temporaryColliderScope.CreatedCollidersCount > 0)
				Debug.Log($"Volumetric fog bake: created {temporaryColliderScope.CreatedCollidersCount} temporary mesh colliders for occlusion sampling.", fogVolume);

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
		if (bakedLightingTexture == null || bakedDirectionTexture == null)
			return false;

		bakedLightingTexture.SetPixels(bakedLightingColors);
		bakedLightingTexture.Apply(false, false);
		bakedDirectionTexture.SetPixels(bakedDirectionColors);
		bakedDirectionTexture.Apply(false, false);

		Undo.RecordObject(bakedData, "Bake Volumetric Fog Lighting");
		bakedData.SetLightingTexture(bakedLightingTexture);
		bakedData.SetDirectionTexture(bakedDirectionTexture);
		bakedData.SetBakedLightsCount(bakedLightsCount);
		EditorUtility.SetDirty(bakedLightingTexture);
		EditorUtility.SetDirty(bakedDirectionTexture);
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
				Vector3 toPoint = positionWS - light.position;
				float distanceSq = Vector3.Dot(toPoint, toPoint);
				if (distanceSq >= light.rangeSq)
					continue;

				float distance = Mathf.Sqrt(Mathf.Max(distanceSq, 0.000001f));
				directionToLight = (light.position - positionWS) / distance;

				// Approximate URP distance attenuation to avoid broad over-bright baked punctual contribution.
				float distanceAttenuation = 1.0f / Mathf.Max(distanceSq, 0.0001f);
				float rangeFactor = distanceSq * light.invRangeSq;
				float smoothFactor = 1.0f - (rangeFactor * rangeFactor);
				smoothFactor = Mathf.Clamp01(smoothFactor);
				smoothFactor *= smoothFactor;
				attenuation = distanceAttenuation * smoothFactor;

				// Match runtime smoothing near light origin to reduce noise and energy spike.
				float radiusSq = Mathf.Max(light.radiusSq, 0.000001f);
				float radialFade = Mathf.Clamp01(distanceSq / radiusSq);
				radialFade = radialFade * radialFade * (3.0f - (2.0f * radialFade));
				radialFade *= radialFade;
				attenuation *= radialFade;

				if (light.type == LightType.Spot)
				{
					float cosAngle = Vector3.Dot(light.direction, -directionToLight);
					float spotAttenuation = Mathf.Clamp01(cosAngle * light.spotScale + light.spotOffset);
					attenuation *= spotAttenuation * spotAttenuation;
				}

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
			bool occluded = RaycastOccluder(rayOrigin, sampleDirection, maxDistance, settings.layerMask);
			if (!occluded)
				visibleSamples++;
		}

		float visibility = visibleSamples / (float)sampleCount;
		return Mathf.Lerp(1.0f, visibility, light.shadowStrength);
	}

	private static bool RaycastOccluder(Vector3 rayOrigin, Vector3 rayDirection, float distance, int layerMask)
	{
		if (distance <= 0.0f)
			return false;

		return Physics.Raycast(rayOrigin, rayDirection, distance, layerMask, QueryTriggerInteraction.Ignore);
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
