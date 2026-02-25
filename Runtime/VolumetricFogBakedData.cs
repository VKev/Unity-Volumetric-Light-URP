using UnityEngine;

/// <summary>
/// Holds baked volumetric lighting data sampled by the fog shader.
/// </summary>
[CreateAssetMenu(fileName = "VolumetricFogBakedData", menuName = "Rendering/Volumetric Fog/Baked Data")]
public sealed class VolumetricFogBakedData : ScriptableObject
{
	#region Private Attributes

	[Tooltip("3D texture containing baked volumetric lighting in RGB.")]
	[SerializeField] private Texture3D lightingTexture;
	[Tooltip("World-space center of the baked volume bounds.")]
	[SerializeField] private Vector3 boundsCenter = new Vector3(0.0f, 8.0f, 0.0f);
	[Tooltip("World-space size of the baked volume bounds.")]
	[SerializeField] private Vector3 boundsSize = new Vector3(64.0f, 32.0f, 64.0f);
	[Tooltip("Bake resolution in X.")]
	[SerializeField, Min(4)] private int resolutionX = 64;
	[Tooltip("Bake resolution in Y.")]
	[SerializeField, Min(4)] private int resolutionY = 32;
	[Tooltip("Bake resolution in Z.")]
	[SerializeField, Min(4)] private int resolutionZ = 64;

	#endregion

	#region Public Attributes

	public Texture3D LightingTexture => lightingTexture;
	public Vector3 BoundsCenter => boundsCenter;
	public Vector3 BoundsSize => boundsSize;
	public int ResolutionX => resolutionX;
	public int ResolutionY => resolutionY;
	public int ResolutionZ => resolutionZ;
	public Vector3Int Resolution => new Vector3Int(resolutionX, resolutionY, resolutionZ);

	public bool IsValid
	{
		get
		{
			return lightingTexture != null && boundsSize.x > 0.0001f && boundsSize.y > 0.0001f && boundsSize.z > 0.0001f && resolutionX >= 4 && resolutionY >= 4 && resolutionZ >= 4;
		}
	}

	#endregion

	#region Methods

	public void SetLightingTexture(Texture3D texture)
	{
		lightingTexture = texture;
	}

	#endregion

	#region ScriptableObject Methods

	private void OnValidate()
	{
		boundsSize.x = Mathf.Max(0.0001f, boundsSize.x);
		boundsSize.y = Mathf.Max(0.0001f, boundsSize.y);
		boundsSize.z = Mathf.Max(0.0001f, boundsSize.z);
		resolutionX = Mathf.Clamp(resolutionX, 4, 256);
		resolutionY = Mathf.Clamp(resolutionY, 4, 256);
		resolutionZ = Mathf.Clamp(resolutionZ, 4, 256);
	}

	#endregion
}
