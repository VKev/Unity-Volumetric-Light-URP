using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

/// <summary>
/// Custom editor for the volumetric fog volume component.
/// </summary>
[CustomEditor(typeof(VolumetricFogVolumeComponent))]
public sealed class VolumetricFogVolumeComponentEditor : VolumeComponentEditor
{
	#region Private Attributes

	private SerializedDataParameter distance;
	private SerializedDataParameter baseHeight;
	private SerializedDataParameter maximumHeight;

	private SerializedDataParameter enableGround;
	private SerializedDataParameter groundHeight;

	private SerializedDataParameter density;
	private SerializedDataParameter attenuationDistance;
	private SerializedDataParameter lightingMode;
	private SerializedDataParameter bakedData;
	private SerializedDataParameter bakedIntensity;
	private SerializedDataParameter bakedUseFroxelSampling;
#if UNITY_2023_1_OR_NEWER
	private SerializedDataParameter enableAPVContribution;
	private SerializedDataParameter APVContributionWeight;
#endif

	private SerializedDataParameter enableMainLightContribution;
	private SerializedDataParameter anisotropy;
	private SerializedDataParameter scattering;
	private SerializedDataParameter tint;

	private SerializedDataParameter enableAdditionalLightsContribution;

	private SerializedDataParameter downsampleMode;
	private SerializedDataParameter maxSteps;
	private SerializedDataParameter maxAdditionalLights;
	private SerializedDataParameter blurIterations;
	private SerializedDataParameter transmittanceThreshold;
	private SerializedDataParameter enabled;
	
	private SerializedDataParameter renderPassEvent;

	#endregion

	#region VolumeComponentEditor Methods

	/// <summary>
	/// <inheritdoc/>
	/// </summary>
	public override void OnEnable()
	{
		PropertyFetcher<VolumetricFogVolumeComponent> pf = new PropertyFetcher<VolumetricFogVolumeComponent>(serializedObject);

		distance = Unpack(pf.Find(x => x.distance));
		baseHeight = Unpack(pf.Find(x => x.baseHeight));
		maximumHeight = Unpack(pf.Find(x => x.maximumHeight));

		enableGround = Unpack(pf.Find(x => x.enableGround));
		groundHeight = Unpack(pf.Find(x => x.groundHeight));

		density = Unpack(pf.Find(x => x.density));
		attenuationDistance = Unpack(pf.Find(x => x.attenuationDistance));
		lightingMode = Unpack(pf.Find(x => x.lightingMode));
		bakedData = Unpack(pf.Find(x => x.bakedData));
		bakedIntensity = Unpack(pf.Find(x => x.bakedIntensity));
		bakedUseFroxelSampling = Unpack(pf.Find(x => x.bakedUseFroxelSampling));
#if UNITY_2023_1_OR_NEWER
		enableAPVContribution = Unpack(pf.Find(x => x.enableAPVContribution));
		APVContributionWeight = Unpack(pf.Find(x => x.APVContributionWeight));
#endif

		enableMainLightContribution = Unpack(pf.Find(x => x.enableMainLightContribution));
		anisotropy = Unpack(pf.Find(x => x.anisotropy));
		scattering = Unpack(pf.Find(x => x.scattering));
		tint = Unpack(pf.Find(x => x.tint));

		enableAdditionalLightsContribution = Unpack(pf.Find(x => x.enableAdditionalLightsContribution));

		downsampleMode = Unpack(pf.Find(x => x.downsampleMode));
		maxSteps = Unpack(pf.Find(x => x.maxSteps));
		maxAdditionalLights = Unpack(pf.Find(x => x.maxAdditionalLights));
		blurIterations = Unpack(pf.Find(x => x.blurIterations));
		transmittanceThreshold = Unpack(pf.Find(x => x.transmittanceThreshold));
		enabled = Unpack(pf.Find(x => x.enabled));
		
		renderPassEvent = Unpack(pf.Find(x => x.renderPassEvent));
	}

	/// <summary>
	/// <inheritdoc/>
	/// </summary>
	public override void OnInspectorGUI()
	{
		bool isEnabled = enabled.overrideState.boolValue && enabled.value.boolValue;

		if (!isEnabled)
		{
			PropertyField(enabled);
			return;
		}

		bool enabledGround = enableGround.overrideState.boolValue && enableGround.value.boolValue;
		bool enabledMainLightContribution = enableMainLightContribution.overrideState.boolValue && enableMainLightContribution.value.boolValue;
		bool enabledAdditionalLightsContribution = enableAdditionalLightsContribution.overrideState.boolValue && enableAdditionalLightsContribution.value.boolValue;

		PropertyField(distance);
		PropertyField(baseHeight);
		PropertyField(maximumHeight);

		PropertyField(enableGround);
		if (enabledGround)
			PropertyField(groundHeight);

		PropertyField(density);
		PropertyField(attenuationDistance);
		PropertyField(lightingMode);
		VolumetricFogLightingMode currentLightingMode = (VolumetricFogLightingMode)lightingMode.value.intValue;
		bool useHybridBaked = currentLightingMode == VolumetricFogLightingMode.HybridBaked;
		if (useHybridBaked)
		{
			PropertyField(bakedData);
			PropertyField(bakedIntensity);
			PropertyField(bakedUseFroxelSampling);
			if (bakedUseFroxelSampling.value.boolValue)
				EditorGUILayout.HelpBox("Baked Use Froxel Sampling is faster but less accurate. Disable it for closest match to realtime.", MessageType.Warning);
		}

		VolumetricFogVolumeComponent fogVolume = target as VolumetricFogVolumeComponent;
		if (fogVolume != null)
		{
			if (fogVolume.bakedData.value == null)
				EditorGUILayout.HelpBox("Bake uses only lights with Lightmap Bake Type = Baked. A baked data asset will be created if missing.", MessageType.Info);
			else
			{
				VolumetricFogBakedData bakedAsset = fogVolume.bakedData.value;
				EditorGUILayout.HelpBox($"Baked Data Resolution: {bakedAsset.ResolutionX} x {bakedAsset.ResolutionY} x {bakedAsset.ResolutionZ} | Baked Lights: {bakedAsset.BakedLightsCount}", MessageType.Info);
				EditorGUILayout.HelpBox($"Shadow Bake: {(bakedAsset.EnableShadowOcclusion ? "On" : "Off")} | Temp Mesh Colliders: {(bakedAsset.CreateTemporaryMeshColliders ? "On" : "Off")} | Soft Samples: {(bakedAsset.EnableSoftShadowSampling ? bakedAsset.SoftShadowSampleCount.ToString() : "Off")}", MessageType.None);

				if (GUILayout.Button("Select Baked Data Asset"))
					Selection.activeObject = bakedAsset;
			}

			using (new EditorGUILayout.HorizontalScope())
			{
				if (GUILayout.Button("Bake Baked Lights"))
				{
					serializedObject.ApplyModifiedProperties();
					VolumetricFogBakedDataBaker.BakeFromVolume(fogVolume);
					GUIUtility.ExitGUI();
				}

				using (new EditorGUI.DisabledScope(fogVolume.bakedData.value == null))
				{
					if (GUILayout.Button("Clear Baked Texture"))
					{
						serializedObject.ApplyModifiedProperties();
						VolumetricFogBakedDataBaker.ClearBakedTexture(fogVolume);
						GUIUtility.ExitGUI();
					}
				}
			}
		}

#if UNITY_2023_1_OR_NEWER
		bool enabledAPVContribution = enableAPVContribution.overrideState.boolValue && enableAPVContribution.value.boolValue;
		PropertyField(enableAPVContribution);
		if (enabledAPVContribution)
			PropertyField(APVContributionWeight);
#endif

		PropertyField(enableMainLightContribution);
		if (enabledMainLightContribution)
		{
			PropertyField(anisotropy);
			PropertyField(scattering);
			PropertyField(tint);
		}

		PropertyField(enableAdditionalLightsContribution);

		PropertyField(downsampleMode);
		PropertyField(maxSteps);
		PropertyField(maxAdditionalLights);
		PropertyField(blurIterations);
		PropertyField(transmittanceThreshold);
		PropertyField(enabled);
		
		PropertyField(renderPassEvent);
	}

	#endregion
}
