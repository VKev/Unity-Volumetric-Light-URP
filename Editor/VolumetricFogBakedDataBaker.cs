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
		public float spotScale;
		public float spotOffset;
		public bool castShadows;
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
			Debug.Log("Volumetric fog bake: collider-based occlusion is enabled. Baked lights with shadows will be blocked by colliders.", fogVolume);

		int resolutionX = Mathf.Clamp(bakedData.ResolutionX, 4, 256);
		int resolutionY = Mathf.Clamp(bakedData.ResolutionY, 4, 256);
		int resolutionZ = Mathf.Clamp(bakedData.ResolutionZ, 4, 256);
		int voxelCount = resolutionX * resolutionY * resolutionZ;
		Color[] bakedColors = new Color[voxelCount];

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
			directionalDistance = Mathf.Max(1.0f, bakedData.DirectionalShadowDistance)
		};

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

						Vector3 bakedColor = EvaluateBakedLightingAtPosition(positionWS, bakedLights, occlusionSettings);
						bakedColors[rowOffset + x] = new Color(bakedColor.x, bakedColor.y, bakedColor.z, 1.0f);
					}
				}
			}
		}
		finally
		{
			EditorUtility.ClearProgressBar();
		}

		Texture3D bakedTexture = GetOrCreateBakedTextureAsset(bakedData, resolutionX, resolutionY, resolutionZ);
		if (bakedTexture == null)
			return false;

		bakedTexture.SetPixels(bakedColors);
		bakedTexture.Apply(false, false);

		Undo.RecordObject(bakedData, "Bake Volumetric Fog Lighting");
		bakedData.SetLightingTexture(bakedTexture);
		bakedData.SetBakedLightsCount(bakedLightsCount);
		EditorUtility.SetDirty(bakedTexture);
		EditorUtility.SetDirty(bakedData);

		AssetDatabase.SaveAssets();
		AssetDatabase.ImportAsset(bakedDataAssetPath, ImportAssetOptions.ForceUpdate);

		return true;
	}

	private static Texture3D GetOrCreateBakedTextureAsset(VolumetricFogBakedData bakedData, int resolutionX, int resolutionY, int resolutionZ)
	{
		Texture3D texture = bakedData.LightingTexture;
		bool needsCreate = texture == null
			|| texture.width != resolutionX
			|| texture.height != resolutionY
			|| texture.depth != resolutionZ
			|| texture.format != TextureFormat.RGBAHalf;

		if (needsCreate)
		{
			texture = new Texture3D(resolutionX, resolutionY, resolutionZ, TextureFormat.RGBAHalf, false, true);
			texture.name = "VolumetricFogBakedLighting";
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
			if (light.type == LightType.Directional)
			{
				if (mainLightScattering <= 0.0001f)
					continue;

				scattering = mainLightScattering;
				color = Vector3.Scale(color, mainLightTint);
			}
			else if (light.TryGetComponent(out VolumetricAdditionalLight volumetricAdditionalLight))
			{
				scattering = Mathf.Max(0.0f, volumetricAdditionalLight.Scattering);
			}
			else
			{
				// Allow baked contribution for point/spot lights without VolumetricAdditionalLight using a default scattering.
				scattering = 1.0f;
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
				invRangeSq = 0.0f,
				radiusSq = 0.04f,
				spotScale = 0.0f,
				spotOffset = 0.0f,
				castShadows = light.shadows != LightShadows.None
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

	private static Vector3 EvaluateBakedLightingAtPosition(Vector3 positionWS, List<BakedLightSample> bakedLights, in BakedOcclusionSettings occlusionSettings)
	{
		const float AveragePhase = 0.0795774715f; // 1 / (4 * PI), view-independent phase approximation.
		Vector3 accumulatedColor = Vector3.zero;

		for (int i = 0; i < bakedLights.Count; ++i)
		{
			BakedLightSample light = bakedLights[i];

			if (light.type == LightType.Directional)
			{
				if (IsOccluded(positionWS, light, occlusionSettings))
					continue;

				accumulatedColor += light.color * (light.scattering * AveragePhase);
				continue;
			}

			Vector3 toPoint = positionWS - light.position;
			float distanceSq = Vector3.Dot(toPoint, toPoint);
			if (distanceSq >= light.rangeSq)
				continue;

			if (IsOccluded(positionWS, light, occlusionSettings))
				continue;

			// Approximate URP distance attenuation to avoid broad over-bright baked punctual contribution.
			float distanceAttenuation = 1.0f / Mathf.Max(distanceSq, 0.0001f);
			float smoothFactor = 1.0f - (distanceSq * light.invRangeSq);
			smoothFactor = Mathf.Clamp01(smoothFactor);
			smoothFactor *= smoothFactor;
			float attenuation = distanceAttenuation * smoothFactor;

			// Match runtime smoothing near light origin to reduce noise and energy spike.
			float radiusSq = Mathf.Max(light.radiusSq, 0.000001f);
			float radialFade = Mathf.Clamp01(distanceSq / radiusSq);
			radialFade = radialFade * radialFade * (3.0f - (2.0f * radialFade));
			radialFade *= radialFade;
			attenuation *= radialFade;

			if (light.type == LightType.Spot)
			{
				float invDistance = 1.0f / Mathf.Sqrt(Mathf.Max(distanceSq, 0.000001f));
				Vector3 directionToPoint = toPoint * invDistance;
				float cosAngle = Vector3.Dot(light.direction, directionToPoint);
				float spotAttenuation = Mathf.Clamp01(cosAngle * light.spotScale + light.spotOffset);
				attenuation *= spotAttenuation * spotAttenuation;
			}

			accumulatedColor += light.color * (attenuation * light.scattering * AveragePhase);
		}

		return accumulatedColor;
	}

	private static bool IsOccluded(Vector3 positionWS, in BakedLightSample light, in BakedOcclusionSettings settings)
	{
		if (!settings.enabled || !light.castShadows)
			return false;

		if (light.type == LightType.Directional)
		{
			Vector3 directionToLight = -light.direction.normalized;
			float maxDistance = settings.directionalDistance;
			Vector3 origin = positionWS + directionToLight * settings.rayBias;
			return Physics.Raycast(origin, directionToLight, maxDistance, settings.layerMask, QueryTriggerInteraction.Ignore);
		}

		Vector3 toSample = positionWS - light.position;
		float distance = Mathf.Sqrt(Mathf.Max(Vector3.Dot(toSample, toSample), 0.000001f));
		if (distance <= settings.rayBias)
			return false;

		Vector3 direction = toSample / distance;
		Vector3 rayOrigin = light.position + direction * settings.rayBias;
		float rayDistance = Mathf.Max(distance - settings.rayBias * 2.0f, 0.0f);
		if (rayDistance <= 0.0f)
			return false;

		return Physics.Raycast(rayOrigin, direction, rayDistance, settings.layerMask, QueryTriggerInteraction.Ignore);
	}

	private static bool IsLightConfiguredAsBaked(Light light)
	{
		if (light == null)
			return false;

		LightmapBakeType bakingOutputType = light.bakingOutput.lightmapBakeType;
#pragma warning disable 0618
		LightmapBakeType configuredType = light.lightmapBakeType;
#pragma warning restore 0618

		return bakingOutputType == LightmapBakeType.Baked || configuredType == LightmapBakeType.Baked;
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
