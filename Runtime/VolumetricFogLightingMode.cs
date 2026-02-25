/// <summary>
/// Selects how volumetric fog lighting is sourced.
/// </summary>
public enum VolumetricFogLightingMode : byte
{
	/// <summary>
	/// Use runtime light evaluation for all supported lights.
	/// </summary>
	RuntimeOnly = 0,
	/// <summary>
	/// Use baked volumetric data when available and keep realtime lights evaluated at runtime.
	/// If baked data is missing, the pass falls back to full runtime evaluation.
	/// </summary>
	HybridBaked = 1
}
