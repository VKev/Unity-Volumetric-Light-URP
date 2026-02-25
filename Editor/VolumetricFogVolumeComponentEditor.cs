using UnityEditor;
using UnityEditor.Rendering;

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
	private SerializedDataParameter blurIterations;
	private SerializedDataParameter transmittanceThreshold;
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
		blurIterations = Unpack(pf.Find(x => x.blurIterations));
		transmittanceThreshold = Unpack(pf.Find(x => x.transmittanceThreshold));
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
		PropertyField(blurIterations);
		PropertyField(transmittanceThreshold);

		PropertyField(enableStaticLightsBake);
		bool staticLightsBakeEnabled = enableStaticLightsBake.overrideState.boolValue && enableStaticLightsBake.value.boolValue;
		if (staticLightsBakeEnabled)
		{
			PropertyField(staticLightsBakeRevision);
			EditorGUILayout.HelpBox("Precomputes static reusable light data for performance while preserving the same visual result as unbaked mode. Increase Bake Revision after major scene/light setup changes.", MessageType.Info);
		}

		PropertyField(enabled);
		
		PropertyField(renderPassEvent);
	}

	#endregion
}
