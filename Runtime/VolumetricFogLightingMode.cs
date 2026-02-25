/// <summary>
/// Selects which lighting path the volumetric fog should use.
/// </summary>
public enum VolumetricFogLightingMode : byte
{
	/// <summary>
	/// Evaluate all supported lights in real time.
	/// </summary>
	RuntimeOnly = 0,
	/// <summary>
	/// Evaluate static-tagged lights through a runtime voxel volume and keep dynamic lights in real time.
	/// </summary>
	StaticVoxelDynamicRealtime = 1
}
