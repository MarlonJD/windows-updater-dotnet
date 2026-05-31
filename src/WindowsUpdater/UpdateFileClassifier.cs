namespace WindowsUpdater;

public static class UpdateFileClassifier
{
    public static UpdateFileKind Classify(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        var extension = Path.GetExtension(relativePath);

        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateFileKind.Executable;
        }

        if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateFileKind.DynamicLibrary;
        }

        if (extension.Equals(".winmd", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateFileKind.NativeMetadata;
        }

        if (extension.Equals(".pri", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateFileKind.PriResource;
        }

        if (extension.Equals(".cat", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateFileKind.Catalog;
        }

        if (fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateFileKind.DependencyManifest;
        }

        if (fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateFileKind.RuntimeConfig;
        }

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateFileKind.Manifest;
        }

        if (relativePath.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".resw", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateFileKind.Resource;
        }

        return UpdateFileKind.Other;
    }

    public static bool IsRequiredAtLaunch(UpdateFileKind kind, string relativePath)
    {
        return kind is UpdateFileKind.Executable
                or UpdateFileKind.DynamicLibrary
                or UpdateFileKind.NativeMetadata
                or UpdateFileKind.DependencyManifest
                or UpdateFileKind.RuntimeConfig
                or UpdateFileKind.PriResource
            || relativePath.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
    }
}
