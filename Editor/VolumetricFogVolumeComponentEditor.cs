using System;
using System.Reflection;
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
	private SerializedDataParameter staticVoxelIncludeMainLight;
	private SerializedDataParameter staticVoxelBoundsCenter;
	private SerializedDataParameter staticVoxelBoundsSize;
	private SerializedDataParameter staticVoxelResolutionX;
	private SerializedDataParameter staticVoxelResolutionY;
	private SerializedDataParameter staticVoxelResolutionZ;
	private SerializedDataParameter staticVoxelIntensity;
	private SerializedDataParameter staticVoxelDirectionalPhase;
	private SerializedDataParameter staticVoxelUpdateMode;
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
		staticVoxelIncludeMainLight = Unpack(pf.Find(x => x.staticVoxelIncludeMainLight));
		staticVoxelBoundsCenter = Unpack(pf.Find(x => x.staticVoxelBoundsCenter));
		staticVoxelBoundsSize = Unpack(pf.Find(x => x.staticVoxelBoundsSize));
		staticVoxelResolutionX = Unpack(pf.Find(x => x.staticVoxelResolutionX));
		staticVoxelResolutionY = Unpack(pf.Find(x => x.staticVoxelResolutionY));
		staticVoxelResolutionZ = Unpack(pf.Find(x => x.staticVoxelResolutionZ));
		staticVoxelIntensity = Unpack(pf.Find(x => x.staticVoxelIntensity));
		staticVoxelDirectionalPhase = Unpack(pf.Find(x => x.staticVoxelDirectionalPhase));
		staticVoxelUpdateMode = Unpack(pf.Find(x => x.staticVoxelUpdateMode));
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
		bool staticVoxelLightingMode = (VolumetricFogLightingMode)lightingMode.value.intValue == VolumetricFogLightingMode.StaticVoxelDynamicRealtime;
		if (staticVoxelLightingMode)
		{
			PropertyField(staticVoxelIncludeMainLight);
			PropertyField(staticVoxelBoundsCenter);
			PropertyField(staticVoxelBoundsSize);
			PropertyField(staticVoxelResolutionX);
			PropertyField(staticVoxelResolutionY);
			PropertyField(staticVoxelResolutionZ);
			PropertyField(staticVoxelIntensity);
			PropertyField(staticVoxelDirectionalPhase);
			PropertyField(staticVoxelUpdateMode);

			if (GUILayout.Button("Rebuild Static Voxel Volume"))
				RequestStaticVoxelRebuild();
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

	private static void RequestStaticVoxelRebuild()
	{
		Type renderPassType = Type.GetType("VolumetricFogRenderPass, com.cqf.urpvolumetricfog.runtime");
		if (renderPassType == null)
			return;

		MethodInfo requestMethod = renderPassType.GetMethod("RequestStaticVoxelRebuild", BindingFlags.Public | BindingFlags.Static);
		requestMethod?.Invoke(null, null);
	}

	#endregion
}
