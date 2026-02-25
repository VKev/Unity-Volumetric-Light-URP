using System;
using UnityEngine.Rendering;

/// <summary>
/// A volume parameter that holds a <see cref="VolumetricFogDownsampleMode"/> value.
/// </summary>
[Serializable]
public sealed class VolumetricFogDownsampleModeParameter : VolumeParameter<VolumetricFogDownsampleMode>
{
	/// <summary>
	/// Creates a new VolumetricFogDownsampleModeParameter instance.
	/// </summary>
	/// <param name="value"></param>
	/// <param name="overrideState"></param>
	public VolumetricFogDownsampleModeParameter(VolumetricFogDownsampleMode value, bool overrideState = false) : base(value, overrideState)
	{
	}
}
