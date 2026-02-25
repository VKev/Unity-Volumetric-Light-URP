using System;
using UnityEngine.Rendering;

/// <summary>
/// A volume parameter that holds a <see cref="VolumetricFogLightingMode"/> value.
/// </summary>
[Serializable]
public sealed class VolumetricFogLightingModeParameter : VolumeParameter<VolumetricFogLightingMode>
{
	/// <summary>
	/// Creates a new VolumetricFogLightingModeParameter instance.
	/// </summary>
	/// <param name="value"></param>
	/// <param name="overrideState"></param>
	public VolumetricFogLightingModeParameter(VolumetricFogLightingMode value, bool overrideState = false) : base(value, overrideState)
	{
	}
}
