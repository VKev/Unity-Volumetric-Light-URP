/// <summary>
/// Selects how a volumetric additional light contributes to fog.
/// </summary>
public enum VolumetricAdditionalLightMode : byte
{
	/// <summary>
	/// Evaluate this light in the runtime dynamic fog pass.
	/// </summary>
	DynamicRealtime = 0,
	/// <summary>
	/// Inject this light into the static voxel lighting volume.
	/// </summary>
	StaticVoxel = 1
}
