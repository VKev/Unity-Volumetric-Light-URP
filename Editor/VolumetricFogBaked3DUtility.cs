using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Utility that bakes approximate volumetric 3D extinction/radiance textures for static lighting.
/// </summary>
internal static class VolumetricFogBaked3DUtility
{
	private const float MinRange = 0.01f;
	private const float MinRayBias = 0.01f;
	private const float MinIntensity = 0.001f;
	private const float MinScattering = 0.0001f;
	private static readonly RaycastHit[] RaycastHits = new RaycastHit[64];

	private struct StaticAdditionalLightData
	{
		public Vector3 position;
		public Vector3 color;
		public Vector3 spotDirection;
		public float invRangeSq;
		public float radiusSq;
		public float scattering;
		public float spotCosOuter;
		public float spotInvCosRange;
		public bool isSpot;
	}

	/// <summary>
	/// Bakes 3D textures for the provided volume component and assigns them back to the profile.
	/// </summary>
	/// <param name="volume"></param>
	public static void Bake(VolumetricFogVolumeComponent volume)
	{
		if (volume == null)
			return;

		int resolution = Mathf.Clamp(volume.baked3DResolution.value, 16, 256);
		Vector3 center = volume.baked3DVolumeCenter.value;
		Vector3 size = volume.baked3DVolumeSize.value;
		Vector3 sizeSafe = new Vector3(Mathf.Max(0.01f, size.x), Mathf.Max(0.01f, size.y), Mathf.Max(0.01f, size.z));
		float absorption = 1.0f / Mathf.Max(volume.attenuationDistance.value, 0.05f);

		List<StaticAdditionalLightData> staticAdditionalLights = GatherStaticAdditionalLights();
		bool hasStaticMainLight = TryGetStaticMainLight(volume, out Vector3 mainLightDirection, out Vector3 mainLightColor);
		Vector3 tintLinear = volume.tint.value.linear;
		float mainScattering = volume.scattering.value;
		float mainShadowRayDistance = sizeSafe.magnitude;

		int voxelCount = resolution * resolution * resolution;
		Color[] extinctionPixels = new Color[voxelCount];
		Color[] radiancePixels = new Color[voxelCount];

		int index = 0;
		for (int z = 0; z < resolution; ++z)
		{
			float wz = ((z + 0.5f) / resolution) - 0.5f;
			for (int y = 0; y < resolution; ++y)
			{
				float wy = ((y + 0.5f) / resolution) - 0.5f;
				float worldY = center.y + wy * sizeSafe.y;
				float density = EvaluateFogDensity(volume, worldY);
				float extinction = density * absorption;

				for (int x = 0; x < resolution; ++x, ++index)
				{
					float wx = ((x + 0.5f) / resolution) - 0.5f;
					Vector3 worldPos = center + new Vector3(wx * sizeSafe.x, wy * sizeSafe.y, wz * sizeSafe.z);

					Vector3 radiance = Vector3.zero;
					if (density > 0.0f)
					{
						if (hasStaticMainLight)
						{
							float mainOcclusion = ComputeDirectionalStaticOcclusion(worldPos, mainLightDirection, mainShadowRayDistance);
							if (mainOcclusion > 0.0f)
								radiance += mainLightColor * tintLinear * (mainScattering * density * mainOcclusion);
						}

						for (int i = 0; i < staticAdditionalLights.Count; ++i)
						{
							StaticAdditionalLightData light = staticAdditionalLights[i];
							Vector3 toPoint = light.position - worldPos;
							float distSq = Mathf.Max(toPoint.sqrMagnitude, 0.0001f);
							float distanceAttenuation = Mathf.Clamp01(1.0f - distSq * light.invRangeSq);
							if (distanceAttenuation <= 0.0f)
								continue;

							distanceAttenuation *= distanceAttenuation;
							if (light.isSpot)
							{
								Vector3 dirFromLightToPoint = -toPoint / Mathf.Sqrt(distSq);
								float cd = Vector3.Dot(light.spotDirection, dirFromLightToPoint);
								float angleAttenuation = Mathf.Clamp01((cd - light.spotCosOuter) * light.spotInvCosRange);
								angleAttenuation *= angleAttenuation;
								distanceAttenuation *= angleAttenuation;
								if (distanceAttenuation <= 0.0f)
									continue;
							}

							float localScattering = Mathf.SmoothStep(0.0f, light.radiusSq, distSq);
							localScattering *= localScattering;
							localScattering *= light.scattering;
							if (localScattering <= 0.0f)
								continue;

							float occlusion = ComputeStaticRayOcclusion(light.position, worldPos);
							if (occlusion <= 0.0f)
								continue;

							radiance += light.color * (distanceAttenuation * localScattering * density * occlusion);
						}
					}

					extinctionPixels[index] = new Color(extinction, 0.0f, 0.0f, 1.0f);
					radiancePixels[index] = new Color(radiance.x, radiance.y, radiance.z, 1.0f);
				}
			}
		}

		Texture3D extinctionTexture = EnsureBakeTexture(volume.baked3DExtinctionTexture.value as Texture3D, resolution, TextureFormat.RHalf, "Extinction");
		Texture3D radianceTexture = EnsureBakeTexture(volume.baked3DRadianceTexture.value as Texture3D, resolution, TextureFormat.RGBAHalf, "Radiance");

		extinctionTexture.SetPixels(extinctionPixels);
		extinctionTexture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
		radianceTexture.SetPixels(radiancePixels);
		radianceTexture.Apply(updateMipmaps: false, makeNoLongerReadable: false);

		PersistBakeTextureAsset(volume, extinctionTexture, "Extinction");
		PersistBakeTextureAsset(volume, radianceTexture, "Radiance");

		volume.enableBaked3DMode.overrideState = true;
		volume.enableBaked3DMode.value = true;
		volume.baked3DExtinctionTexture.overrideState = true;
		volume.baked3DRadianceTexture.overrideState = true;
		volume.baked3DExtinctionTexture.value = extinctionTexture;
		volume.baked3DRadianceTexture.value = radianceTexture;
	}

	private static float EvaluateFogDensity(VolumetricFogVolumeComponent volume, float worldY)
	{
		float baseHeight = volume.baseHeight.value;
		float maxHeight = Mathf.Max(baseHeight, volume.maximumHeight.value);
		float t = Mathf.Clamp01((worldY - baseHeight) / Mathf.Max(maxHeight - baseHeight, 0.0001f));
		t = 1.0f - t;
		if (volume.enableGround.value && worldY < volume.groundHeight.value)
			t = 0.0f;
		return Mathf.Max(0.0f, volume.density.value * t);
	}

	private static List<StaticAdditionalLightData> GatherStaticAdditionalLights()
	{
		List<StaticAdditionalLightData> result = new List<StaticAdditionalLightData>(32);
#if UNITY_2023_1_OR_NEWER
		Light[] sceneLights = Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
		Light[] sceneLights = Object.FindObjectsOfType<Light>();
#endif
		for (int i = 0; i < sceneLights.Length; ++i)
		{
			Light light = sceneLights[i];
			if (light == null || !light.enabled || !light.gameObject.activeInHierarchy)
				continue;
			if (light.type != LightType.Point && light.type != LightType.Spot)
				continue;
			if (!IsLightStaticForBake(light))
				continue;
			if (!light.TryGetComponent(out VolumetricAdditionalLight volumetricLight) || !volumetricLight.enabled || !volumetricLight.gameObject.activeInHierarchy)
				continue;
			if (light.intensity <= MinIntensity || volumetricLight.Scattering <= MinScattering)
				continue;

			float range = Mathf.Max(light.range, MinRange);
			float rangeSq = range * range;
			Color linearColor = light.color.linear;
			Vector3 color = new Vector3(linearColor.r, linearColor.g, linearColor.b) * light.intensity;
			bool isSpot = light.type == LightType.Spot;
			float spotCosOuter = -1.0f;
			float spotInvCosRange = 0.0f;
			Vector3 spotDirection = Vector3.forward;
			if (isSpot)
			{
				float cosOuter = Mathf.Cos(0.5f * Mathf.Deg2Rad * light.spotAngle);
				float cosInner = Mathf.Cos(0.5f * Mathf.Deg2Rad * light.innerSpotAngle);
				spotCosOuter = cosOuter;
				spotInvCosRange = 1.0f / Mathf.Max(cosInner - cosOuter, 0.001f);
				spotDirection = light.transform.forward;
			}

			result.Add(new StaticAdditionalLightData
			{
				position = light.transform.position,
				color = color,
				spotDirection = spotDirection,
				invRangeSq = 1.0f / Mathf.Max(rangeSq, 0.0001f),
				radiusSq = volumetricLight.Radius * volumetricLight.Radius,
				scattering = volumetricLight.Scattering,
				spotCosOuter = spotCosOuter,
				spotInvCosRange = spotInvCosRange,
				isSpot = isSpot
			});
		}

		return result;
	}

	private static bool TryGetStaticMainLight(VolumetricFogVolumeComponent volume, out Vector3 mainLightDirection, out Vector3 mainLightColor)
	{
		mainLightDirection = Vector3.forward;
		mainLightColor = Vector3.zero;

		if (!volume.enableMainLightContribution.value || volume.scattering.value <= MinScattering)
			return false;

		Light candidate = RenderSettings.sun;
		if (candidate == null || !candidate.enabled || !candidate.gameObject.activeInHierarchy || candidate.type != LightType.Directional || !IsLightStaticForBake(candidate))
		{
#if UNITY_2023_1_OR_NEWER
			Light[] sceneLights = Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
			Light[] sceneLights = Object.FindObjectsOfType<Light>();
#endif
			for (int i = 0; i < sceneLights.Length; ++i)
			{
				Light light = sceneLights[i];
				if (light == null || !light.enabled || !light.gameObject.activeInHierarchy)
					continue;
				if (light.type != LightType.Directional || !IsLightStaticForBake(light))
					continue;
				candidate = light;
				break;
			}
		}

		if (candidate == null || candidate.intensity <= MinIntensity)
			return false;

		mainLightDirection = -candidate.transform.forward;
		Color linearColor = candidate.color.linear;
		mainLightColor = new Vector3(linearColor.r, linearColor.g, linearColor.b) * candidate.intensity;
		return true;
	}

	private static float ComputeDirectionalStaticOcclusion(Vector3 origin, Vector3 directionToLight, float maxDistance)
	{
		if (directionToLight.sqrMagnitude <= 0.000001f)
			return 1.0f;

		Vector3 dir = directionToLight.normalized;
		Ray ray = new Ray(origin + dir * MinRayBias, dir);
		int hitCount = Physics.RaycastNonAlloc(ray, RaycastHits, Mathf.Max(0.01f, maxDistance), Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
		if (hitCount <= 0)
			return 1.0f;

		for (int i = 0; i < hitCount; ++i)
		{
			Collider hitCollider = RaycastHits[i].collider;
			if (hitCollider != null && IsGameObjectStaticForBake(hitCollider.gameObject))
				return 0.0f;
		}

		return 1.0f;
	}

	private static float ComputeStaticRayOcclusion(in Vector3 origin, in Vector3 target)
	{
		Vector3 toTarget = target - origin;
		float distance = toTarget.magnitude;
		if (distance <= MinRayBias)
			return 1.0f;

		Vector3 direction = toTarget / distance;
		Ray ray = new Ray(origin + direction * MinRayBias, direction);
		float rayDistance = distance - MinRayBias;
		int hitCount = Physics.RaycastNonAlloc(ray, RaycastHits, rayDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
		if (hitCount <= 0)
			return 1.0f;

		for (int i = 0; i < hitCount; ++i)
		{
			Collider hitCollider = RaycastHits[i].collider;
			if (hitCollider != null && IsGameObjectStaticForBake(hitCollider.gameObject))
				return 0.0f;
		}

		return 1.0f;
	}

	private static bool IsLightStaticForBake(Light light)
	{
		if (light == null || light.gameObject == null)
			return false;
		if (IsGameObjectStaticForBake(light.gameObject))
			return true;
		LightmapBakeType bakeType = light.lightmapBakeType;
		return bakeType == LightmapBakeType.Baked || bakeType == LightmapBakeType.Mixed;
	}

	private static bool IsGameObjectStaticForBake(GameObject gameObject)
	{
		if (gameObject == null)
			return false;
		if (gameObject.isStatic)
			return true;
		StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(gameObject);
		return (flags & StaticEditorFlags.ContributeGI) != 0;
	}

	private static Texture3D EnsureBakeTexture(Texture3D current, int resolution, TextureFormat format, string suffix)
	{
		if (current != null && current.width == resolution && current.height == resolution && current.depth == resolution && current.format == format)
			return current;

		Texture3D texture = new Texture3D(resolution, resolution, resolution, format, mipChain: false)
		{
			wrapMode = TextureWrapMode.Clamp,
			filterMode = FilterMode.Trilinear,
			anisoLevel = 0,
			name = $"VolumetricFogBaked3D_{suffix}_{resolution}"
		};

		return texture;
	}

	private static void PersistBakeTextureAsset(VolumetricFogVolumeComponent volume, Texture3D texture, string suffix)
	{
		if (texture == null)
			return;

		if (AssetDatabase.Contains(texture))
		{
			EditorUtility.SetDirty(texture);
			return;
		}

		string sceneName = volume.gameObject.scene.IsValid() ? volume.gameObject.scene.name : "Scene";
		string volumeName = SanitizeName(volume.gameObject.name);
		string baseDir = "Assets/VolumetricFogBakes";
		if (!Directory.Exists(baseDir))
			Directory.CreateDirectory(baseDir);

		string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{baseDir}/{sceneName}_{volumeName}_{suffix}.asset");
		AssetDatabase.CreateAsset(texture, assetPath);
	}

	private static string SanitizeName(string value)
	{
		if (string.IsNullOrEmpty(value))
			return "Volume";

		char[] chars = value.ToCharArray();
		for (int i = 0; i < chars.Length; ++i)
		{
			char c = chars[i];
			if (!char.IsLetterOrDigit(c) && c != '_')
				chars[i] = '_';
		}

		return new string(chars);
	}
}
