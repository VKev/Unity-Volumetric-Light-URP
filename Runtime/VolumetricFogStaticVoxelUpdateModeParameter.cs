using System;
using UnityEngine.Rendering;

/// <summary>
/// A volume parameter that holds a <see cref="VolumetricFogStaticVoxelUpdateMode"/> value.
/// </summary>
[Serializable]
public sealed class VolumetricFogStaticVoxelUpdateModeParameter : VolumeParameter<VolumetricFogStaticVoxelUpdateMode>
{
	/// <summary>
	/// Creates a new VolumetricFogStaticVoxelUpdateModeParameter instance.
	/// </summary>
	/// <param name="value"></param>
	/// <param name="overrideState"></param>
	public VolumetricFogStaticVoxelUpdateModeParameter(VolumetricFogStaticVoxelUpdateMode value, bool overrideState = false) : base(value, overrideState)
	{
	}
}
