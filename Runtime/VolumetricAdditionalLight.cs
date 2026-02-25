using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// This is a component that can be added to additional lights to set the parameters that will
/// affect how this light is considered for the volumetric fog effect.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Light), typeof(UniversalAdditionalLightData))]
public sealed class VolumetricAdditionalLight : MonoBehaviour
{
	#region Private Attributes

	private static readonly List<VolumetricAdditionalLight> RegisteredVolumetricLights = new List<VolumetricAdditionalLight>(128);

	[Tooltip("Higher positive values will make the fog affected by this light to appear brighter when directly looking to it, while lower negative values will make the fog to appear brighter when looking away from it. The closer the value is closer to 1 or -1, the less the brightness will spread. Most times, positive values higher than 0 and lower than 1 should be used.")]
	[Range(-1.0f, 1.0f)]
	[SerializeField] private float anisotropy = 0.25f;
	[Tooltip("Higher values will make fog affected by this light to appear brighter.")]
	[Range(0.0f, 16.0f)]
	[SerializeField] private float scattering = 1.0f;
	[Tooltip("Sets a falloff radius for this light. A higher value reduces noise towards the origin of the light.")]
	[Range(0.0f, 1.0f)]
	[SerializeField] private float radius = 0.2f;
	[Tooltip("Choose whether this light is evaluated in dynamic runtime fog or injected into static voxel lighting.")]
	[SerializeField] private VolumetricAdditionalLightMode lightMode = VolumetricAdditionalLightMode.DynamicRealtime;
	private Light cachedLight;

	#endregion

	#region Properties

	/// <summary>
	/// Runtime registry used to iterate volumetric additional lights without allocations.
	/// </summary>
	public static IReadOnlyList<VolumetricAdditionalLight> LightsRegistry
	{
		get { return RegisteredVolumetricLights; }
	}

	/// <summary>
	/// Cached Light component attached to this volumetric light.
	/// </summary>
	public Light CachedLight
	{
		get
		{
			if (cachedLight == null)
				TryGetComponent(out cachedLight);

			return cachedLight;
		}
	}

	public float Anisotropy
	{
		get { return anisotropy; }
		set
		{
			float clamped = Mathf.Clamp(value, -1.0f, 1.0f);
			if (Mathf.Abs(anisotropy - clamped) <= 0.0001f)
				return;

			anisotropy = clamped;
		}
	}

	public float Scattering
	{
		get { return scattering; }
		set
		{
			float clamped = Mathf.Clamp(value, 0.0f, 16.0f);
			if (Mathf.Abs(scattering - clamped) <= 0.0001f)
				return;

			scattering = clamped;
		}
	}

	public float Radius
	{
		get { return radius; }
		set
		{
			float clamped = Mathf.Clamp01(value);
			if (Mathf.Abs(radius - clamped) <= 0.0001f)
				return;

			radius = clamped;
		}
	}

	public VolumetricAdditionalLightMode LightMode
	{
		get { return lightMode; }
		set
		{
			if (lightMode == value)
				return;

			lightMode = value;
		}
	}

	#endregion

	#region Unity Methods

	private void Awake()
	{
		CacheLightComponent();
	}

	private void OnEnable()
	{
		CacheLightComponent();
		if (!RegisteredVolumetricLights.Contains(this))
			RegisteredVolumetricLights.Add(this);
	}

	private void OnDisable()
	{
		RegisteredVolumetricLights.Remove(this);
	}

	private void OnValidate()
	{
		anisotropy = Mathf.Clamp(anisotropy, -1.0f, 1.0f);
		scattering = Mathf.Clamp(scattering, 0.0f, 16.0f);
		radius = Mathf.Clamp01(radius);
		CacheLightComponent();
	}

	#endregion

	#region Private Methods

	private void CacheLightComponent()
	{
		if (cachedLight == null)
			TryGetComponent(out cachedLight);
	}

	#endregion
}
