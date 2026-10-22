namespace BlazecoinWallet.Core.Services;

/// <summary>
/// The user's folders, resolved the same way on every desktop platform. Windows uses its known folder;
/// elsewhere the path is built from the home folder explicitly (as the Transactions export already does
/// for ~/Downloads), because <see cref="Environment.SpecialFolder.MyDocuments"/> maps differently across
/// .NET's plain Unix runtime and the Apple runtimes (added for the macOS 2.0.9 build, 2026-09-28).
/// </summary>
public static class UserFolders
{
    /// <summary>The user's Documents folder: <c>%USERPROFILE%\Documents</c> on Windows, <c>~/Documents</c> elsewhere.</summary>
    public static string Documents() => OperatingSystem.IsWindows()
        ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");

    /// <summary>An example full path for help text, in this platform's form.</summary>
    public static string ExampleDocumentsPath(string fileName) => OperatingSystem.IsWindows()
        ? $@"C:\Users\you\Documents\{fileName}"
        : $"/Users/you/Documents/{fileName}";
}
