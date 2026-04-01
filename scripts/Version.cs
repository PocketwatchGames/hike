public static partial class Version
{
	public const int Major = 0;
	public const int Minor = 1;

	public static string Display => $"v{Major}.{Minor}.{Build}";
	public static string Full => $"v{Major}.{Minor}.{Build} ({GitHash})";
}
