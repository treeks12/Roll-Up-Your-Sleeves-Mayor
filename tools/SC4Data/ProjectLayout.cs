using System.Text.Json;

namespace SC4Data;

public sealed class ProjectLayout
{
    private readonly string _projectRoot;

    private ProjectLayout(string projectRoot, string gameRoot)
    {
        _projectRoot = projectRoot;
        GameRoot = gameRoot;
        GeneratedRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, "analysis", "generated"));
    }

    public string GameRoot { get; }
    public string GeneratedRoot { get; }
    public string ProjectRoot => _projectRoot;

    public static ProjectLayout Load(string? projectRoot)
    {
        var root = projectRoot is null ? FindProjectRoot(Environment.CurrentDirectory) : System.IO.Path.GetFullPath(projectRoot);
        var configPath = System.IO.Path.Combine(root, "config", "local.paths.json");
        if (!File.Exists(configPath))
        {
            throw new InvalidDataException($"Project configuration was not found: {configPath}");
        }

        using var config = JsonDocument.Parse(File.ReadAllText(configPath));
        if (!config.RootElement.TryGetProperty("SimCity4", out var gamePathValue) || gamePathValue.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{configPath} must contain a string property named SimCity4.");
        }

        var configuredGamePath = gamePathValue.GetString();
        if (string.IsNullOrWhiteSpace(configuredGamePath))
        {
            throw new InvalidDataException($"The SimCity4 path in {configPath} is empty.");
        }

        var gameRoot = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(configuredGamePath)
            ? configuredGamePath
            : System.IO.Path.Combine(root, configuredGamePath));
        if (!Directory.Exists(gameRoot))
        {
            throw new DirectoryNotFoundException($"Configured SimCity4 directory does not exist: {gameRoot}");
        }

        return new ProjectLayout(root, gameRoot);
    }

    public string ResolveArchive(string suppliedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suppliedPath);
        var archivePath = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(suppliedPath)
            ? suppliedPath
            : System.IO.Path.Combine(GameRoot, suppliedPath));
        EnsureContained(GameRoot, archivePath, "Archive input must be inside the configured SimCity 4 install directory.");
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("DBPF archive was not found.", archivePath);
        }

        return archivePath;
    }

    public string ResolveGeneratedOutput(string suppliedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suppliedPath);
        var outputPath = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(suppliedPath)
            ? suppliedPath
            : System.IO.Path.Combine(_projectRoot, suppliedPath));
        EnsureContained(GeneratedRoot, outputPath, "Output path must be inside analysis/generated/.");
        if (string.Equals(outputPath, GeneratedRoot, PathComparison))
        {
            throw new InvalidDataException("Output path must name a file inside analysis/generated/.");
        }

        EnsureNoReparsePoints(GeneratedRoot);
        EnsureNoReparsePoints(System.IO.Path.GetDirectoryName(outputPath)!);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);
        EnsureNoReparsePoints(System.IO.Path.GetDirectoryName(outputPath)!);
        EnsureNoReparsePoints(outputPath);
        return outputPath;
    }

    public string ResolveGeneratedOutputIn(string suppliedPath, string generatedSubdirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedSubdirectory);
        var requiredRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(GeneratedRoot, generatedSubdirectory));
        EnsureContained(GeneratedRoot, requiredRoot, "Generated output subdirectory must remain inside analysis/generated/.");
        var requestedPath = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(suppliedPath)
            ? suppliedPath
            : System.IO.Path.Combine(_projectRoot, suppliedPath));
        EnsureContained(requiredRoot, requestedPath, $"Output path must be inside analysis/generated/{generatedSubdirectory}/.");
        return ResolveGeneratedOutput(suppliedPath);
    }

    private static string FindProjectRoot(string startDirectory)
    {
        var current = new DirectoryInfo(System.IO.Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            if (File.Exists(System.IO.Path.Combine(current.FullName, "config", "local.paths.json")) &&
                File.Exists(System.IO.Path.Combine(current.FullName, "scripts", "Enter-Project.ps1")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the project root. Pass --root with the SC4Evolution root directory.");
    }

    private static void EnsureContained(string root, string path, string message)
    {
        var relative = System.IO.Path.GetRelativePath(root, path);
        if (System.IO.Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, PathComparison) || relative.StartsWith(".." + System.IO.Path.AltDirectorySeparatorChar, PathComparison))
        {
            throw new InvalidDataException(message);
        }
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var driveRoot = System.IO.Path.GetPathRoot(fullPath)!;
        var current = driveRoot;
        var relative = fullPath[driveRoot.Length..];
        foreach (var segment in relative.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                continue;
            }

            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Output path contains a reparse point: {current}");
            }
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
