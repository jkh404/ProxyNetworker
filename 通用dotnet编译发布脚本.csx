// Generic interactive .NET publish script.
// Prerequisite: dotnet tool install --global dotnet-script
// Run:          dotnet script build.csx
// Optional path: dotnet script build.csx -- path/to/app.cs
// Project discovery: with multiple projects, evaluated Exe/WinExe entry
// projects are preferred over class libraries and test projects.
// Native libraries: set OutputType=Library, PublishAot=true, and
// NativeLib=Shared (dynamic) or Static (static). Windows -> Linux AOT uses
// PublishAotClang 1.0.5 automatically when the project does not reference it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

const string PublishAotClangVersion = "1.0.5";

void Run(IReadOnlyList<string> scriptArguments)
{
    var firstRun = true;

    while (true)
    {
        RunOnce(firstRun ? scriptArguments : Array.Empty<string>());
        firstRun = false;

        Console.WriteLine();
        Console.Write("按回车返回第一步继续编译，输入 q 退出: ");
        var nextAction = Console.ReadLine();

        // Redirected/non-interactive input reaches EOF here. Exit instead of
        // entering a prompt loop that can never receive another selection.
        if (nextAction == null ||
            string.Equals(nextAction.Trim(), "q", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nextAction.Trim(), "quit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nextAction.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Console.WriteLine();
    }
}

void RunOnce(IReadOnlyList<string> scriptArguments)
{
    var scriptDirectory = GetScriptDirectory();
    var sdkMajorVersion = GetDotNetSdkMajorVersion(scriptDirectory);

    Console.WriteLine("通用 .NET 发布脚本");
    Console.WriteLine($"发现目录: {scriptDirectory}");
    Console.WriteLine($".NET SDK: {sdkMajorVersion}.x");
    Console.WriteLine();

    var buildInput = scriptArguments.Count > 0
        ? LocateExplicitBuildInput(scriptArguments[0], scriptDirectory)
        : LocateBuildInput(scriptDirectory);
    if (buildInput.Kind == BuildInputKind.FileBasedApp && sdkMajorVersion < 10)
    {
        throw new InvalidOperationException("File-based app 发布需要 .NET 10 SDK 或更高版本。");
    }

    var metadata = ReadBuildMetadata(buildInput);
    if (string.Equals(metadata.IsPublishable, "false", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"项目不可发布: {buildInput.Path}");
    }

    var targetFramework = SelectTargetFramework(metadata);
    if (buildInput.Kind == BuildInputKind.Project && !string.IsNullOrWhiteSpace(targetFramework))
    {
        metadata = ReadProjectMetadata(buildInput.Path, targetFramework);
    }

    var configuredNativeLib = NormalizeNativeLib(metadata.NativeLib);
    if (!string.IsNullOrWhiteSpace(configuredNativeLib) && metadata.IsExecutable)
    {
        throw new InvalidOperationException(
            $"项目 {metadata.Name} 同时是 {metadata.OutputType} 和 NativeLib={configuredNativeLib}。NativeLib=Static/Shared 应用于类库项目，请将 OutputType 设为 Library。");
    }

    Console.WriteLine();
    Console.WriteLine($"项目: {metadata.Name}");
    Console.WriteLine($"路径: {buildInput.Path}");
    if (!string.IsNullOrWhiteSpace(targetFramework))
    {
        Console.WriteLine($"目标框架: {targetFramework}");
    }

    var platforms = CreatePlatformOptions();
    var defaultPlatformIndex = GetDefaultPlatformIndex(platforms);
    var platform = SelectOne(
        "请选择发布平台",
        platforms,
        option => $"{option.DisplayName} [{option.Rid}]",
        defaultPlatformIndex);

    var modes = string.IsNullOrWhiteSpace(configuredNativeLib)
        ? new[]
        {
            new PublishMode("依赖 .NET Runtime（单文件）", false),
            new PublishMode("Native AOT（无需 .NET Runtime）", true)
        }
        : new[]
        {
            new PublishMode("Native AOT（Shared 动态库）", true, "Shared"),
            new PublishMode("Native AOT（Static 静态库）", true, "Static")
        };
    var defaultModeIndex = string.Equals(configuredNativeLib, "Static", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    var mode = SelectOne("请选择发布方式", modes, option => option.DisplayName, defaultModeIndex);

    if (mode.IsAot && !metadata.IsExecutable && string.IsNullOrWhiteSpace(metadata.NativeLib))
    {
        throw new InvalidOperationException(
            "所选项目不是可执行项目，也未设置 NativeLib=Shared/Static，不能直接发布 Native AOT。");
    }

    ValidateAotHost(platform, mode);

    var outputFolderName = BuildDefaultOutputFolderName(metadata.Name, platform.Rid, mode.IsAot, mode.NativeLib);
    var publishRoot = Path.Combine(scriptDirectory, "publish");
    var defaultOutputPath = Path.Combine(publishRoot, outputFolderName);
    var outputPath = PromptOutputPath(defaultOutputPath, publishRoot);

    Console.WriteLine();
    Console.WriteLine("发布配置:");
    Console.WriteLine($"  项目: {metadata.Name}");
    Console.WriteLine($"  平台: {platform.Rid}");
    Console.WriteLine($"  方式: {mode.DisplayName}");
    Console.WriteLine($"  输出: {outputPath}");
    Console.WriteLine();

    Directory.CreateDirectory(outputPath);

    string temporaryDirectory = null;
    try
    {
        var arguments = new List<string>
        {
            "publish",
            buildInput.Path,
            "-c", "Release",
            "-r", platform.Rid,
            "-o", outputPath,
            "--nologo",
            "-v", "minimal",
            "--self-contained", mode.IsAot ? "true" : "false",
            $"-p:PublishAot={mode.IsAot.ToString().ToLowerInvariant()}",
            $"-p:PublishSingleFile={(!mode.IsAot && metadata.IsExecutable && string.IsNullOrWhiteSpace(mode.NativeLib)).ToString().ToLowerInvariant()}",
            $"-p:PublishTrimmed={mode.IsAot.ToString().ToLowerInvariant()}",
            "-p:DebugSymbols=false",
            "-p:DebugType=None"
        };

        if (mode.IsAot)
        {
            arguments.Add("-p:JsonSerializerIsReflectionEnabledByDefault=false");
            arguments.Add("-p:IlcOptimizationPreference=Size");
            arguments.Add("-p:CopyOutputSymbolsToPublishDirectory=false");
        }

        if (!string.IsNullOrWhiteSpace(mode.NativeLib))
        {
            arguments.Add($"-p:NativeLib={mode.NativeLib}");
        }

        if (!string.IsNullOrWhiteSpace(targetFramework) && buildInput.Kind == BuildInputKind.Project)
        {
            arguments.Add("-f");
            arguments.Add(targetFramework);
        }

        if (NeedsPublishAotClang(platform, mode))
        {
            if (!HasPublishAotClangReference(buildInput))
            {
                temporaryDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "dotnet-publish-aot-clang-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryDirectory);

                var propsPath = Path.Combine(temporaryDirectory, "PublishAotClang.props");
                File.WriteAllText(
                    propsPath,
                    CreatePublishAotClangProps(),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                arguments.Add($"-p:CustomBeforeMicrosoftCommonProps={propsPath}");
            }

            Console.WriteLine(
                $"Windows -> Linux AOT 将使用 PublishAotClang {PublishAotClangVersion} 交叉编译。");
        }

        Console.WriteLine("执行:");
        Console.WriteLine("  dotnet " + string.Join(" ", arguments.Select(QuoteForDisplay)));
        Console.WriteLine();

        var exitCode = RunProcess("dotnet", arguments, buildInput.WorkingDirectory);
        if (exitCode != 0)
        {
            Environment.ExitCode = exitCode;
            throw new InvalidOperationException($"dotnet publish 失败，退出码: {exitCode}");
        }

        if (!string.IsNullOrWhiteSpace(mode.NativeLib))
        {
            ValidateNativeLibraryOutput(outputPath, platform.Rid, mode.NativeLib);
        }
    }
    finally
    {
        if (!string.IsNullOrWhiteSpace(temporaryDirectory) && Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    Console.WriteLine();
    Console.WriteLine($"发布完成: {outputPath}");
}

BuildInput LocateExplicitBuildInput(string rawPath, string baseDirectory)
{
    var path = ResolveUserPath(rawPath, baseDirectory);
    if (Directory.Exists(path))
    {
        return DiscoverBuildInput(path, confirmSingleInput: true)
            ?? throw new InvalidOperationException($"目录中未找到解决方案、项目或 .cs 文件: {path}");
    }

    if (!File.Exists(path))
    {
        throw new FileNotFoundException("输入路径不存在。", path);
    }

    var extension = Path.GetExtension(path);
    if (IsSolutionExtension(extension))
    {
        return SelectProjectFromSolution(path);
    }

    if (IsProjectExtension(extension))
    {
        var directory = Path.GetDirectoryName(path);
        return new BuildInput(BuildInputKind.Project, path, directory, directory);
    }

    if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase))
    {
        var directory = Path.GetDirectoryName(path);
        return new BuildInput(BuildInputKind.FileBasedApp, path, directory, directory);
    }

    throw new InvalidOperationException(
        "仅支持 .sln、.slnx、.csproj、.fsproj、.vbproj 和 file-based .cs 文件。");
}

BuildInput LocateBuildInput(string directory)
{
    while (true)
    {
        var discovered = DiscoverBuildInput(directory, confirmSingleInput: true);
        if (discovered != null)
        {
            return discovered;
        }

        Console.Write("请输入解决方案、项目、file-based .cs 文件或其所在目录: ");
        var rawPath = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            Console.WriteLine("路径不能为空。");
            continue;
        }

        var path = ResolveUserPath(rawPath, directory);
        if (Directory.Exists(path))
        {
            var inputFromDirectory = DiscoverBuildInput(path, confirmSingleInput: true);
            if (inputFromDirectory != null)
            {
                return inputFromDirectory;
            }

            Console.WriteLine($"目录中未找到解决方案、项目或 .cs 文件: {path}");
            continue;
        }

        if (!File.Exists(path))
        {
            Console.WriteLine($"路径不存在: {path}");
            continue;
        }

        var extension = Path.GetExtension(path);
        if (IsSolutionExtension(extension))
        {
            return SelectProjectFromSolution(path);
        }

        if (IsProjectExtension(extension))
        {
            if (Confirm($"确认编译项目 {Path.GetFileName(path)}", defaultYes: true))
            {
                return new BuildInput(
                    BuildInputKind.Project,
                    path,
                    Path.GetDirectoryName(path),
                    Path.GetDirectoryName(path));
            }

            continue;
        }

        if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase))
        {
            if (Confirm($"确认发布 file-based app {Path.GetFileName(path)}", defaultYes: true))
            {
                return new BuildInput(
                    BuildInputKind.FileBasedApp,
                    path,
                    Path.GetDirectoryName(path),
                    Path.GetDirectoryName(path));
            }

            continue;
        }

        Console.WriteLine("仅支持 .sln、.slnx、.csproj、.fsproj、.vbproj 和 file-based .cs 文件。");
    }
}

BuildInput DiscoverBuildInput(string directory, bool confirmSingleInput)
{
    var solutions = Directory.EnumerateFiles(directory, "*.sln", SearchOption.TopDirectoryOnly)
        .Concat(Directory.EnumerateFiles(directory, "*.slnx", SearchOption.TopDirectoryOnly))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (solutions.Count > 0)
    {
        var solution = solutions.Count == 1
            ? solutions[0]
            : SelectOne("发现多个解决方案，请选择", solutions, Path.GetFileName, 0);
        return SelectProjectFromSolution(solution);
    }

    var projects = EnumerateProjectFiles(directory).ToList();
    if (projects.Count > 0)
    {
        string project;
        if (projects.Count == 1)
        {
            project = projects[0];
            if (confirmSingleInput &&
                !Confirm($"发现项目 {Path.GetFileName(project)}，是否编译", defaultYes: true))
            {
                return null;
            }
        }
        else
        {
            return SelectProjectFromCandidates(projects, directory, "发现多个项目，请选择");
        }

        return new BuildInput(BuildInputKind.Project, project, directory, directory);
    }

    var fileBasedApps = Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (fileBasedApps.Count == 0)
    {
        return null;
    }

    string fileBasedApp;
    if (fileBasedApps.Count == 1)
    {
        fileBasedApp = fileBasedApps[0];
        if (confirmSingleInput &&
            !Confirm($"发现 file-based app {Path.GetFileName(fileBasedApp)}，是否发布", defaultYes: true))
        {
            return null;
        }
    }
    else
    {
        fileBasedApp = SelectOne("发现多个 .cs 文件，请选择 file-based app", fileBasedApps, Path.GetFileName, 0);
    }

    return new BuildInput(BuildInputKind.FileBasedApp, fileBasedApp, directory, directory);
}

BuildInput SelectProjectFromSolution(string solutionPath)
{
    var projects = ReadProjectsFromSolution(solutionPath)
        .Where(File.Exists)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (projects.Count == 0)
    {
        throw new InvalidOperationException($"解决方案中没有可用的 .NET 项目: {solutionPath}");
    }

    var solutionDirectory = Path.GetDirectoryName(solutionPath);
    return SelectProjectFromCandidates(
        projects,
        solutionDirectory,
        $"解决方案 {Path.GetFileName(solutionPath)}，请选择需要编译的项目");
}

BuildInput SelectProjectFromCandidates(
    IReadOnlyList<string> projects,
    string workingDirectory,
    string fallbackTitle)
{
    var inspected = projects
        .Select(InspectProject)
        .ToList();

    // OutputType is the evaluated MSBuild truth for an entry project. It also
    // covers top-level statements and WPF/WinForms generated Main methods,
    // where a literal `Main` is absent from the user's source tree.
    var executableProjects = inspected
        .Where(info => info.MetadataAvailable
            && info.Metadata.IsExecutable
            && !info.Metadata.IsTestProject
            && !string.Equals(info.Metadata.IsPublishable, "false", StringComparison.OrdinalIgnoreCase))
        .ToList();

    List<ProjectInspection> selectable;
    string selectionTitle;
    if (executableProjects.Count > 0)
    {
        selectable = executableProjects;
        selectionTitle = executableProjects.Count == 1
            ? "已识别到可执行入口项目"
            : "发现多个可执行入口项目，请选择";
    }
    else
    {
        // NativeLib projects are intentionally retained as a second-class
        // publish target: they have no Main by design, but the script supports
        // publishing Shared/Static native libraries.
        var nativeLibraries = inspected
            .Where(info => info.MetadataAvailable
                && !info.Metadata.IsExecutable
                && !info.Metadata.IsTestProject
                && !string.Equals(info.Metadata.IsPublishable, "false", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(info.Metadata.NativeLib, "Shared", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(info.Metadata.NativeLib, "Static", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        selectable = nativeLibraries.Count > 0
            ? nativeLibraries
            : inspected.Where(info => !info.MetadataAvailable || !info.Metadata.IsTestProject).ToList();
        if (selectable.Count == 0)
        {
            selectable = inspected;
        }
        selectionTitle = nativeLibraries.Count > 0
            ? nativeLibraries.Count == 1
                ? "已识别到 NativeLib 项目"
                : "发现多个 NativeLib 项目，请选择"
            : fallbackTitle + "（未识别到可执行入口项目）";
    }

    if (selectable.Count == 0)
    {
        throw new InvalidOperationException($"没有可选择的项目: {workingDirectory}");
    }

    ProjectInspection selected;
    if (selectable.Count == 1)
    {
        selected = selectable[0];
        Console.WriteLine($"{selectionTitle}: {FormatProjectSelection(selected, workingDirectory)}");
    }
    else
    {
        selected = SelectOne(
            selectionTitle,
            selectable,
            info => FormatProjectSelection(info, workingDirectory),
            0);
    }

    return new BuildInput(
        BuildInputKind.Project,
        selected.Path,
        workingDirectory,
        workingDirectory);
}

ProjectInspection InspectProject(string projectPath)
{
    try
    {
        return new ProjectInspection(projectPath, ReadProjectMetadata(projectPath, targetFramework: null));
    }
    catch (Exception exception)
    {
        // Keep an unreadable project available for explicit fallback selection,
        // but never mistake it for an entry project.
        Console.WriteLine($"读取项目属性失败，将保留为手动选项: {projectPath} ({exception.Message})");
        return new ProjectInspection(
            projectPath,
            new BuildMetadata(
                Path.GetFileNameWithoutExtension(projectPath),
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty),
            metadataAvailable: false);
    }
}

string FormatProjectSelection(ProjectInspection inspection, string baseDirectory)
{
    var relativePath = Path.GetRelativePath(baseDirectory, inspection.Path);
    if (!inspection.MetadataAvailable)
    {
        return $"{relativePath} [项目属性不可用]";
    }

    var role = inspection.Metadata.IsExecutable
        ? $"入口 {inspection.Metadata.OutputType}"
        : string.IsNullOrWhiteSpace(inspection.Metadata.NativeLib)
            ? "类库"
            : $"NativeLib={inspection.Metadata.NativeLib}";
    if (inspection.Metadata.IsTestProject)
    {
        role += ", 测试项目";
    }

    return $"{relativePath} [{role}]";
}

IEnumerable<string> ReadProjectsFromSolution(string solutionPath)
{
    var solutionDirectory = Path.GetDirectoryName(solutionPath);
    if (string.Equals(Path.GetExtension(solutionPath), ".slnx", StringComparison.OrdinalIgnoreCase))
    {
        var document = XDocument.Load(solutionPath);
        foreach (var element in document.Descendants().Where(node => node.Name.LocalName == "Project"))
        {
            var relativePath = element.Attribute("Path")?.Value;
            if (!string.IsNullOrWhiteSpace(relativePath) && IsProjectExtension(Path.GetExtension(relativePath)))
            {
                yield return ResolveSolutionRelativePath(solutionDirectory, relativePath);
            }
        }

        yield break;
    }

    var solutionText = File.ReadAllText(solutionPath);
    var pattern = @"Project\(""[^""]+""\)\s*=\s*""[^""]+"",\s*""(?<path>[^""]+\.(?:csproj|fsproj|vbproj))""";
    foreach (Match match in Regex.Matches(solutionText, pattern, RegexOptions.IgnoreCase))
    {
        yield return ResolveSolutionRelativePath(solutionDirectory, match.Groups["path"].Value);
    }
}

IEnumerable<string> EnumerateProjectFiles(string directory)
{
    return Directory.EnumerateFiles(directory, "*.*proj", SearchOption.TopDirectoryOnly)
        .Where(path => IsProjectExtension(Path.GetExtension(path)))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
}

BuildMetadata ReadBuildMetadata(BuildInput input)
{
    if (input.Kind == BuildInputKind.FileBasedApp)
    {
        return new BuildMetadata(
            Path.GetFileNameWithoutExtension(input.Path),
            "Exe",
            string.Empty,
            "net10.0",
            string.Empty,
            "true");
    }

    return ReadProjectMetadata(input.Path, targetFramework: null);
}

BuildMetadata ReadProjectMetadata(string projectPath, string targetFramework)
{
    var propertyNames = new[]
    {
        "AssemblyName",
        "OutputType",
        "NativeLib",
        "TargetFramework",
        "TargetFrameworks",
        "IsPublishable",
        "IsTestProject"
    };
    var arguments = new List<string> { "msbuild", projectPath, "-nologo" };
    if (!string.IsNullOrWhiteSpace(targetFramework))
    {
        arguments.Add($"-p:TargetFramework={targetFramework}");
    }

    arguments.AddRange(propertyNames.Select(name => $"-getProperty:{name}"));

    var result = CaptureProcess("dotnet", arguments, Path.GetDirectoryName(projectPath));
    if (result.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"无法读取项目属性。{Environment.NewLine}{result.StandardError}{result.StandardOutput}");
    }

    var jsonStart = result.StandardOutput.IndexOf('{');
    var jsonEnd = result.StandardOutput.LastIndexOf('}');
    if (jsonStart < 0 || jsonEnd < jsonStart)
    {
        throw new InvalidOperationException("dotnet msbuild 未返回可识别的项目属性 JSON。");
    }

    using var document = JsonDocument.Parse(result.StandardOutput.Substring(jsonStart, jsonEnd - jsonStart + 1));
    var properties = document.RootElement.GetProperty("Properties");

    string ReadProperty(string name)
    {
        return properties.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    }

    var assemblyName = ReadProperty("AssemblyName");
    if (string.IsNullOrWhiteSpace(assemblyName))
    {
        assemblyName = Path.GetFileNameWithoutExtension(projectPath);
    }

    return new BuildMetadata(
        assemblyName,
        ReadProperty("OutputType"),
        ReadProperty("NativeLib"),
        ReadProperty("TargetFramework"),
        ReadProperty("TargetFrameworks"),
        ReadProperty("IsPublishable"),
        ReadProperty("IsTestProject"));
}

string SelectTargetFramework(BuildMetadata metadata)
{
    var frameworks = (metadata.TargetFrameworks ?? string.Empty)
        .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    if (frameworks.Count == 0 && !string.IsNullOrWhiteSpace(metadata.TargetFramework))
    {
        frameworks.Add(metadata.TargetFramework);
    }

    if (frameworks.Count <= 1)
    {
        return frameworks.FirstOrDefault() ?? string.Empty;
    }

    return SelectOne("项目包含多个目标框架，请选择", frameworks, value => value, 0);
}

List<PlatformOption> CreatePlatformOptions()
{
    return new List<PlatformOption>
    {
        new PlatformOption("win-x86", "Windows x86"),
        new PlatformOption("win-x64", "Windows x64"),
        new PlatformOption("win-arm64", "Windows ARM64"),
        new PlatformOption("linux-x64", "Linux x64 (glibc)"),
        new PlatformOption("linux-arm", "Linux ARM 32-bit (glibc)"),
        new PlatformOption("linux-arm64", "Linux ARM64 (glibc)"),
        new PlatformOption("linux-musl-x64", "Linux x64 (musl/Alpine)"),
        new PlatformOption("linux-musl-arm", "Linux ARM 32-bit (musl/Alpine)"),
        new PlatformOption("linux-musl-arm64", "Linux ARM64 (musl/Alpine)"),
        new PlatformOption("osx-x64", "macOS x64"),
        new PlatformOption("osx-arm64", "macOS ARM64")
    };
}

int GetDefaultPlatformIndex(IReadOnlyList<PlatformOption> platforms)
{
    string rid;
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        rid = RuntimeInformation.OSArchitecture == Architecture.X86
            ? "win-x86"
            : RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        rid = RuntimeInformation.OSArchitecture == Architecture.Arm
            ? "linux-arm"
            : RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
    }
    else
    {
        rid = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
    }

    var index = platforms.ToList().FindIndex(option => option.Rid == rid);
    return index >= 0 ? index : 0;
}

void ValidateAotHost(PlatformOption platform, PublishMode mode)
{
    if (!mode.IsAot)
    {
        return;
    }

    var targetOs = platform.Rid.StartsWith("win-", StringComparison.OrdinalIgnoreCase)
        ? "windows"
        : platform.Rid.StartsWith("osx-", StringComparison.OrdinalIgnoreCase) ? "macos" : "linux";
    var hostOs = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos" : "linux";

    if (hostOs == targetOs || (hostOs == "windows" && targetOs == "linux"))
    {
        return;
    }

    throw new InvalidOperationException(
        $"Native AOT 不支持当前跨系统组合: {hostOs} -> {targetOs}。" +
        "本脚本额外支持 Windows -> Linux（PublishAotClang）。");
}

bool NeedsPublishAotClang(PlatformOption platform, PublishMode mode)
{
    return mode.IsAot &&
           RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
           platform.Rid.StartsWith("linux-", StringComparison.OrdinalIgnoreCase);
}

bool HasPublishAotClangReference(BuildInput input)
{
    if (input.Kind == BuildInputKind.FileBasedApp)
    {
        return File.ReadLines(input.Path).Take(100).Any(line =>
            line.Contains("PublishAotClang", StringComparison.OrdinalIgnoreCase));
    }

    try
    {
        var document = XDocument.Load(input.Path);
        return document.Descendants().Any(element =>
            element.Name.LocalName == "PackageReference" &&
            string.Equals(element.Attribute("Include")?.Value, "PublishAotClang", StringComparison.OrdinalIgnoreCase));
    }
    catch
    {
        return false;
    }
}

string CreatePublishAotClangProps()
{
    return $@"<Project>
  <ItemGroup>
    <PackageReference Include=""PublishAotClang"" Version=""{PublishAotClangVersion}"" PrivateAssets=""all"" />
  </ItemGroup>
</Project>
";
}

string PromptOutputPath(string defaultPath, string baseDirectory)
{
    Console.Write($"输出目录 [{defaultPath}]: ");
    var rawPath = Console.ReadLine();
    return string.IsNullOrWhiteSpace(rawPath)
        ? Path.GetFullPath(defaultPath)
        : ResolveUserPath(rawPath, baseDirectory);
}

T SelectOne<T>(string title, IReadOnlyList<T> options, Func<T, string> display, int defaultIndex)
{
    if (options.Count == 0)
    {
        throw new InvalidOperationException($"没有可选择项: {title}");
    }

    while (true)
    {
        Console.WriteLine();
        Console.WriteLine(title + ":");
        for (var index = 0; index < options.Count; index++)
        {
            var defaultMarker = index == defaultIndex ? " (默认)" : string.Empty;
            Console.WriteLine($"  {index + 1}. {display(options[index])}{defaultMarker}");
        }

        Console.Write($"请输入序号，回车使用默认值 [{defaultIndex + 1}]: ");
        var rawValue = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return options[defaultIndex];
        }

        if (int.TryParse(rawValue.Trim(), out var selected) && selected >= 1 && selected <= options.Count)
        {
            return options[selected - 1];
        }

        Console.WriteLine("输入无效，请输入列表中的序号。");
    }
}

bool Confirm(string prompt, bool defaultYes)
{
    while (true)
    {
        Console.Write($"{prompt} [{(defaultYes ? "Y/n" : "y/N")}]: ");
        var value = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultYes;
        }

        if (string.Equals(value.Trim(), "y", StringComparison.OrdinalIgnoreCase) || value.Trim() == "是")
        {
            return true;
        }

        if (string.Equals(value.Trim(), "n", StringComparison.OrdinalIgnoreCase) || value.Trim() == "否")
        {
            return false;
        }

        Console.WriteLine("请输入 y 或 n。");
    }
}

int GetDotNetSdkMajorVersion(string workingDirectory)
{
    var result = CaptureProcess("dotnet", new[] { "--version" }, workingDirectory);
    if (result.ExitCode != 0)
    {
        throw new InvalidOperationException("未找到可用的 dotnet SDK。");
    }

    var versionText = result.StandardOutput.Trim();
    var majorText = versionText.Split('.')[0];
    if (!int.TryParse(majorText, out var majorVersion))
    {
        throw new InvalidOperationException($"无法识别 dotnet SDK 版本: {versionText}");
    }

    return majorVersion;
}

int RunProcess(string fileName, IEnumerable<string> arguments, string workingDirectory)
{
    var startInfo = CreateProcessStartInfo(fileName, arguments, workingDirectory);
    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"无法启动进程: {fileName}");
    process.WaitForExit();
    return process.ExitCode;
}

CommandResult CaptureProcess(string fileName, IEnumerable<string> arguments, string workingDirectory)
{
    var startInfo = CreateProcessStartInfo(fileName, arguments, workingDirectory);
    startInfo.RedirectStandardOutput = true;
    startInfo.RedirectStandardError = true;

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"无法启动进程: {fileName}");
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    Task.WaitAll(outputTask, errorTask);

    return new CommandResult(process.ExitCode, outputTask.Result, errorTask.Result);
}

ProcessStartInfo CreateProcessStartInfo(string fileName, IEnumerable<string> arguments, string workingDirectory)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        WorkingDirectory = workingDirectory,
        UseShellExecute = false
    };
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    return startInfo;
}

string ResolveUserPath(string rawPath, string baseDirectory)
{
    var value = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"', '\''));
    if (value == "~")
    {
        value = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
    else if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
    {
        value = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            value.Substring(2));
    }

    return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(baseDirectory, value));
}

string ResolveSolutionRelativePath(string solutionDirectory, string relativePath)
{
    var normalized = relativePath
        .Replace('\\', Path.DirectorySeparatorChar)
        .Replace('/', Path.DirectorySeparatorChar);
    return Path.GetFullPath(Path.Combine(solutionDirectory, normalized));
}

string BuildDefaultOutputFolderName(string projectName, string rid, bool isAot, string nativeLib = "")
{
    var safeName = Regex.Replace(projectName, @"[^A-Za-z0-9._-]+", "_").Trim('_');
    if (string.IsNullOrWhiteSpace(safeName))
    {
        safeName = "app";
    }

    var nativeSuffix = string.IsNullOrWhiteSpace(nativeLib)
        ? string.Empty
        : $"_native_{nativeLib.ToLowerInvariant()}";
    return $"{safeName}_{rid.Replace('-', '_')}{(isAot ? "_aot" : string.Empty)}{nativeSuffix}";
}

string NormalizeNativeLib(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    if (string.Equals(value, "Static", StringComparison.OrdinalIgnoreCase))
    {
        return "Static";
    }

    if (string.Equals(value, "Shared", StringComparison.OrdinalIgnoreCase))
    {
        return "Shared";
    }

    throw new InvalidOperationException(
        $"NativeLib 的值不受支持: {value}。有效值为 Static 或 Shared。");
}

string GetNativeLibraryExtension(string rid, string nativeLib)
{
    if (string.Equals(nativeLib, "Static", StringComparison.OrdinalIgnoreCase))
    {
        return rid.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ? ".lib" : ".a";
    }

    if (rid.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
    {
        return ".dll";
    }

    if (rid.StartsWith("linux-", StringComparison.OrdinalIgnoreCase))
    {
        return ".so";
    }

    if (rid.StartsWith("osx-", StringComparison.OrdinalIgnoreCase))
    {
        return ".dylib";
    }

    throw new InvalidOperationException($"无法根据 RID 判断 NativeLib 输出扩展名: {rid}");
}

void ValidateNativeLibraryOutput(string outputPath, string rid, string nativeLib)
{
    var extension = GetNativeLibraryExtension(rid, nativeLib);
    var outputs = Directory.EnumerateFiles(outputPath, "*" + extension, SearchOption.TopDirectoryOnly)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (outputs.Count == 0)
    {
        throw new InvalidOperationException(
            $"NativeLib={nativeLib} 发布完成但未找到预期的 {extension} 输出: {outputPath}");
    }

    if (string.Equals(nativeLib, "Static", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine(
            "NativeLib=Static 将输出静态库；Windows -> Linux AOT 使用 PublishAotClang 提供的 ar 工具链，脚本会自动检测项目引用，缺少时临时注入 PublishAotClang。");
    }

    Console.WriteLine($"NativeLib 输出 ({nativeLib}): {string.Join(", ", outputs.Select(Path.GetFileName))}");
}

string QuoteForDisplay(string value)
{
    return value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
}

bool IsSolutionExtension(string extension)
{
    return string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase);
}

bool IsProjectExtension(string extension)
{
    return string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(extension, ".fsproj", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(extension, ".vbproj", StringComparison.OrdinalIgnoreCase);
}

string GetScriptDirectory([CallerFilePath] string scriptPath = "")
{
    return Path.GetDirectoryName(Path.GetFullPath(scriptPath));
}

try
{
    Run(Args.ToArray());
}
catch (Exception exception)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("发布失败: " + exception.Message);
    if (Environment.ExitCode == 0)
    {
        Environment.ExitCode = 1;
    }
}

enum BuildInputKind
{
    Project,
    FileBasedApp
}

sealed class BuildInput
{
    public BuildInput(BuildInputKind kind, string path, string workingDirectory, string defaultOutputRoot)
    {
        Kind = kind;
        Path = System.IO.Path.GetFullPath(path);
        WorkingDirectory = System.IO.Path.GetFullPath(workingDirectory);
        DefaultOutputRoot = System.IO.Path.GetFullPath(defaultOutputRoot);
    }

    public BuildInputKind Kind { get; }
    public string Path { get; }
    public string WorkingDirectory { get; }
    public string DefaultOutputRoot { get; }
}

sealed class ProjectInspection
{
    public ProjectInspection(string path, BuildMetadata metadata, bool metadataAvailable = true)
    {
        Path = System.IO.Path.GetFullPath(path);
        Metadata = metadata;
        MetadataAvailable = metadataAvailable;
    }

    public string Path { get; }
    public BuildMetadata Metadata { get; }
    public bool MetadataAvailable { get; }
}

sealed class BuildMetadata
{
    public BuildMetadata(
        string name,
        string outputType,
        string nativeLib,
        string targetFramework,
        string targetFrameworks,
        string isPublishable,
        string isTestProject = "")
    {
        Name = name;
        OutputType = outputType ?? string.Empty;
        NativeLib = nativeLib ?? string.Empty;
        TargetFramework = targetFramework ?? string.Empty;
        TargetFrameworks = targetFrameworks ?? string.Empty;
        IsPublishable = isPublishable ?? string.Empty;
        IsTestProject = string.Equals(isTestProject, "true", StringComparison.OrdinalIgnoreCase);
    }

    public string Name { get; }
    public string OutputType { get; }
    public string NativeLib { get; }
    public string TargetFramework { get; }
    public string TargetFrameworks { get; }
    public string IsPublishable { get; }
    public bool IsTestProject { get; }
    public bool IsExecutable => OutputType.EndsWith("Exe", StringComparison.OrdinalIgnoreCase);
}

sealed class PlatformOption
{
    public PlatformOption(string rid, string displayName)
    {
        Rid = rid;
        DisplayName = displayName;
    }

    public string Rid { get; }
    public string DisplayName { get; }
}

sealed class PublishMode
{
    public PublishMode(string displayName, bool isAot, string nativeLib = "")
    {
        DisplayName = displayName;
        IsAot = isAot;
        NativeLib = nativeLib ?? string.Empty;
    }

    public string DisplayName { get; }
    public bool IsAot { get; }
    public string NativeLib { get; }
}

sealed class CommandResult
{
    public CommandResult(int exitCode, string standardOutput, string standardError)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public int ExitCode { get; }
    public string StandardOutput { get; }
    public string StandardError { get; }
}
