using System;
using UnityEngine.Rendering;

/// <summary>
/// A volume parameter that holds a <see cref="VolumetricFogBakedData"/> value.
/// </summary>
[Serializable]
public sealed class VolumetricFogBakedDataParameter : VolumeParameter<VolumetricFogBakedData>
{
	/// <summary>
	/// Creates a new VolumetricFogBakedDataParameter instance.
	/// </summary>
	/// <param name="value"></param>
	/// <param name="overrideState"></param>
	public VolumetricFogBakedDataParameter(VolumetricFogBakedData value, bool overrideState = false) : base(value, overrideState)
	{
	}
}
