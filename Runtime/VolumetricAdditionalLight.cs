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

	private const int MaxVisibleAdditionalLights = 256;
	private static readonly Dictionary<int, VolumetricAdditionalLight> ActiveLights = new Dictionary<int, VolumetricAdditionalLight>(MaxVisibleAdditionalLights);

	[Tooltip("Higher positive values will make the fog affected by this light to appear brighter when directly looking to it, while lower negative values will make the fog to appear brighter when looking away from it. The closer the value is closer to 1 or -1, the less the brightness will spread. Most times, positive values higher than 0 and lower than 1 should be used.")]
	[Range(-1.0f, 1.0f)]
	[SerializeField] private float anisotropy = 0.25f;
	[Tooltip("Higher values will make fog affected by this light to appear brighter.")]
	[Range(0.0f, 16.0f)]
	[SerializeField] private float scattering = 1.0f;
	[Tooltip("Sets a falloff radius for this light. A higher value reduces noise towards the origin of the light.")]
	[Range(0.0f, 1.0f)]
	[SerializeField] private float radius = 0.2f;

	private Light cachedLight;

	#endregion

	#region Properties

	public float Anisotropy
	{
		get { return anisotropy; }
		set { anisotropy = Mathf.Clamp(value, -1.0f, 1.0f); }
	}

	public float Scattering
	{
		get { return scattering; }
		set { scattering = Mathf.Clamp(value, 0.0f, 16.0f); }
	}

	public float Radius
	{
		get { return radius; }
		set { radius = Mathf.Clamp01(value); }
	}

	#endregion

	#region MonoBehaviour Methods

	private void Awake()
	{
		CacheLight();
	}

	private void OnEnable()
	{
		Register();
	}

	private void OnDisable()
	{
		Unregister();
	}

	private void OnDestroy()
	{
		Unregister();
	}

	#endregion

	#region Methods

	internal static bool TryGetFromLight(Light light, out VolumetricAdditionalLight volumetricLight)
	{
		if (light != null && ActiveLights.TryGetValue(light.GetInstanceID(), out volumetricLight))
			return volumetricLight != null && volumetricLight.isActiveAndEnabled;

		volumetricLight = null;
		return false;
	}

	private void CacheLight()
	{
		if (cachedLight == null)
			TryGetComponent(out cachedLight);
	}

	private void Register()
	{
		CacheLight();

		if (cachedLight == null)
			return;

		ActiveLights[cachedLight.GetInstanceID()] = this;
	}

	private void Unregister()
	{
		if (cachedLight == null)
			return;

		int lightInstanceId = cachedLight.GetInstanceID();
		if (ActiveLights.TryGetValue(lightInstanceId, out VolumetricAdditionalLight registeredLight) && registeredLight == this)
			ActiveLights.Remove(lightInstanceId);
	}

	#endregion
}
