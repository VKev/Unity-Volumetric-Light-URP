/// <summary>
/// Defines when the runtime static voxel lighting volume should be rebuilt.
/// </summary>
public enum VolumetricFogStaticVoxelUpdateMode : byte
{
	/// <summary>
	/// Build once and keep it until manually rebuilt.
	/// </summary>
	OnLoad = 0,
	/// <summary>
	/// Rebuild whenever tracked static voxel inputs change.
	/// </summary>
	OnChange = 1,
	/// <summary>
	/// Rebuild only when requested manually.
	/// </summary>
	Manual = 2
}
