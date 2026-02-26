using UnityEditor;
using UnityEditor.SceneManagement;
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
	private SerializedDataParameter froxelClusterMinLights;
	private SerializedDataParameter enableAdaptiveStepCount;
	private SerializedDataParameter adaptiveMinSteps;
	private SerializedDataParameter adaptiveStepDensityScale;
	private SerializedDataParameter lightingSampleStride;
	private SerializedDataParameter enableTemporalReprojection;
	private SerializedDataParameter temporalBlendFactor;
	private SerializedDataParameter blurIterations;
	private SerializedDataParameter transmittanceThreshold;
	private SerializedDataParameter enableBaked3DMode;
	private SerializedDataParameter baked3DAddRealtimeLights;
	private SerializedDataParameter baked3DExtinctionTexture;
	private SerializedDataParameter baked3DRadianceTexture;
	private SerializedDataParameter baked3DVolumeCenter;
	private SerializedDataParameter baked3DVolumeSize;
	private SerializedDataParameter baked3DResolution;
	private SerializedDataParameter enableStaticLightsBake;
	private SerializedDataParameter staticLightsBakeRevision;
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
		froxelClusterMinLights = Unpack(pf.Find(x => x.froxelClusterMinLights));
		enableAdaptiveStepCount = Unpack(pf.Find(x => x.enableAdaptiveStepCount));
		adaptiveMinSteps = Unpack(pf.Find(x => x.adaptiveMinSteps));
		adaptiveStepDensityScale = Unpack(pf.Find(x => x.adaptiveStepDensityScale));
		lightingSampleStride = Unpack(pf.Find(x => x.lightingSampleStride));
		enableTemporalReprojection = Unpack(pf.Find(x => x.enableTemporalReprojection));
		temporalBlendFactor = Unpack(pf.Find(x => x.temporalBlendFactor));
		blurIterations = Unpack(pf.Find(x => x.blurIterations));
		transmittanceThreshold = Unpack(pf.Find(x => x.transmittanceThreshold));
		enableBaked3DMode = Unpack(pf.Find(x => x.enableBaked3DMode));
		baked3DAddRealtimeLights = Unpack(pf.Find(x => x.baked3DAddRealtimeLights));
		baked3DExtinctionTexture = Unpack(pf.Find(x => x.baked3DExtinctionTexture));
		baked3DRadianceTexture = Unpack(pf.Find(x => x.baked3DRadianceTexture));
		baked3DVolumeCenter = Unpack(pf.Find(x => x.baked3DVolumeCenter));
		baked3DVolumeSize = Unpack(pf.Find(x => x.baked3DVolumeSize));
		baked3DResolution = Unpack(pf.Find(x => x.baked3DResolution));
		enableStaticLightsBake = Unpack(pf.Find(x => x.enableStaticLightsBake));
		staticLightsBakeRevision = Unpack(pf.Find(x => x.staticLightsBakeRevision));
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
		PropertyField(froxelClusterMinLights);
		PropertyField(enableAdaptiveStepCount);
		bool adaptiveEnabled = enableAdaptiveStepCount.overrideState.boolValue && enableAdaptiveStepCount.value.boolValue;
		if (adaptiveEnabled)
		{
			PropertyField(adaptiveMinSteps);
			PropertyField(adaptiveStepDensityScale);
		}
		PropertyField(lightingSampleStride);
		PropertyField(enableTemporalReprojection);
		bool temporalEnabled = enableTemporalReprojection.overrideState.boolValue && enableTemporalReprojection.value.boolValue;
		if (temporalEnabled)
			PropertyField(temporalBlendFactor);
		PropertyField(blurIterations);
		PropertyField(transmittanceThreshold);
		PropertyField(enableBaked3DMode);
		bool baked3DEnabled = enableBaked3DMode.overrideState.boolValue && enableBaked3DMode.value.boolValue;
		if (baked3DEnabled)
		{
			PropertyField(baked3DAddRealtimeLights);
			PropertyField(baked3DExtinctionTexture);
			PropertyField(baked3DRadianceTexture);
			PropertyField(baked3DVolumeCenter);
			PropertyField(baked3DVolumeSize);
			PropertyField(baked3DResolution);
			if (GUILayout.Button("Bake 3D", GUILayout.Height(22.0f)))
			{
				Bake3DForTargets();
				serializedObject.Update();
			}
			EditorGUILayout.HelpBox("For no visual change, keep Add Realtime Lights enabled (hybrid mode: baked extinction + realtime lighting). Disable it for pure baked radiance mode (faster but approximate).", MessageType.Info);
		}

		PropertyField(enableStaticLightsBake);
		bool staticLightsBakeEnabled = enableStaticLightsBake.overrideState.boolValue && enableStaticLightsBake.value.boolValue;
		if (staticLightsBakeEnabled)
		{
			EditorGUILayout.Space(2.0f);
			if (GUILayout.Button("Bake", GUILayout.Height(22.0f)))
			{
				Undo.RecordObjects(targets, "Bake Volumetric Static Lights");
				staticLightsBakeRevision.overrideState.boolValue = true;
				staticLightsBakeRevision.value.intValue = Mathf.Max(0, staticLightsBakeRevision.value.intValue + 1);
				serializedObject.ApplyModifiedProperties();
				for (int i = 0; i < targets.Length; ++i)
					EditorUtility.SetDirty(targets[i]);
			}

			EditorGUILayout.HelpBox("Static lights (GameObject Static or Light Mode Mixed/Baked) and static-object occlusion from static colliders use baked snapshot values until Bake Revision changes. Dynamic lights and camera-dependent behavior stay live.", MessageType.Info);
		}

		PropertyField(enabled);
		
		PropertyField(renderPassEvent);
	}

	#endregion

	#region Private Methods

	private void Bake3DForTargets()
	{
		for (int i = 0; i < targets.Length; ++i)
		{
			VolumetricFogVolumeComponent volume = targets[i] as VolumetricFogVolumeComponent;
			if (volume == null)
				continue;

			VolumetricFogBaked3DUtility.Bake(volume);
			EditorUtility.SetDirty(volume);
		}

		AssetDatabase.SaveAssets();
		EditorSceneManager.MarkAllScenesDirty();
	}

	#endregion
}
