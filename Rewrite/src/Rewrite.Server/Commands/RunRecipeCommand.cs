using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
// using Microsoft.Build.Exceptions;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.Extensions.Logging;
using NuGet.Configuration;
using NuGet.LibraryModel;
using Rewrite.Core;
using Rewrite.Core.Config;
using Rewrite.MSBuild;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Rewrite.Server.Commands;

[PublicAPI]
public class RunRecipeCommand(RecipeManager recipeManager, ILogger<RunRecipeCommand> logger) : AsyncCommand<RunRecipeCommand.Settings>
{
    [PublicAPI]
    public class Settings : BaseSettings
    {
        private Lazy<Regex> PackageNameRegex = new Lazy<Regex>(() =>
        {
            // Details:
            // id: NuGet package ID rules — starts/ends with alnum; allows ., _, -; max 100 chars.
            //     version: SemVer (NuGet) major.minor.patch[.revision][-prerelease][+build], plus the case-insensitive keywords SNAPSHOT or RELEASE.
            //     Examples that match:
            // Newtonsoft.Json:13.0.3
            // My.Package_Id:1.2.3.4
            // Foo.Bar:1.0.0-alpha.1+exp.sha.5114f85
            // Foo:SNAPSHOT
            // Foo:release

            return new Regex(
                "^(?<id>[A-Za-z0-9](?:[A-Za-z0-9._-]{0,98}[A-Za-z0-9])?)(?::(?<version>(?:(?:0|[1-9]\\d*)\\.(?:0|[1-9]\\d*)\\.(?:0|[1-9]\\d*)(?:\\.(?:0|[1-9]\\d*))?(?:-(?:0|[1-9]\\d*|[A-Za-z-][0-9A-Za-z-]*)(?:\\.(?:0|[1-9]\\d*|[A-Za-z-][0-9A-Za-z-]*))*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?)|(?i:SNAPSHOT|RELEASE)))?$",
                RegexOptions.Compiled);
        });
        
        [CommandOption("-s|--solution <SOLUTION>")]
        [Description("Path to solution file or source directory. If directory is used, it will recursively find all solutions under it")]
        public required string Path { get; set; }

        [CommandOption("-p|--package <NAME>")]
        [Description("Nuget Package Name. By default uses latest version. An explicit version can be specified using syntax <packageName>:<version>. " +
                     "If version is SNAPSHOT, uses latest prerelease version. " +
                     "If version is RELEASE, uses latest stable version." +
                     "Can be specified multiple times.")]
        public required string[] Packages { get; set; }

        [CommandOption("--dry-run")]
        [Description("Does not commit changes to disk")]
        public bool DryRun { get; set; } = false;

        [CommandOption("--gitpatch <PATH>")]
        [Description("Generate a git-style unified diff patch at the specified path instead of modifying source files. Implies --dry-run. " +
                     "When mode is 'combined', this is a file path. When mode is 'recipe', this is a directory where per-recipe patch files are written")]
        public string? GitPatchPath { get; set; }

        [CommandOption("--gitpatch-mode <MODE>")]
        [Description("Patch generation mode. 'combined' (default) merges all changes into a single patch file. " +
                     "'recipe' generates a separate patch file per recipe ID (e.g. MA0008.patch) in the directory specified by --gitpatch")]
        public GitPatchMode GitPatchMode { get; set; } = GitPatchMode.Combined;

        [CommandOption("-i|--id <VERSION>")]
        [Description("Recipe IDs. For Open Rewrite recipes this is namespace qualified type name. For Roslyn recipies this is the diagnostic ID (ex. CS1123). This parameter can be specified multiple times. " +
                     "If ommited, every fixable issue in the package will be applied.")]
        public string[] Ids { get; set; } = [];
        
        public override ValidationResult Validate()
        {
            // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (Path == null)
            {
                return ValidationResult.Error("Path must be specified");
            }
            // if (Ids == null || Ids.Length == 0)
            // {
            //     return ValidationResult.Error("Ids must be specified");
            // }

            if (Packages.Length == 0)
            {
                return ValidationResult.Error("Package Version must be specified");
            }
            var invalidPackageNames = Packages
                .Select(x => PackageNameRegex.Value.Match(x)).Where(x => x.Success == false)
                .ToArray();
            if (invalidPackageNames.Length > 0)
            {
                return ValidationResult.Error($"Invalid package names");
            }
            // if (FeedUrls.Count == 0)
            // {
            //     FeedUrls.Add("https://api.nuget.org/v3/index.json");
            // }
            // ReSharper enable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

            return base.Validate();
        }
    }
    
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (settings.GitPatchPath != null)
        {
            settings.DryRun = true;
        }

        logger.LogDebug("Executing {CommandName} with settings {@Settings}", nameof(RunRecipeCommand), settings);
        // var recipeManager = new RecipeManager();
        // CA1802: Use Literals Where Appropriate
        // CA1861: Avoid constant arrays as arguments
        var packages = settings.Packages.Select(InstallableRecipeExtensions.GetLibraryRange).ToList();
        // settings.NugetConfigRoot
        // var installableRecipes = settings.Ids
        //     .Select(id =>  new InstallableRecipe(id, settings.PackageName, settings.PackageVersion).GetLibraryRange())
        //     .ToList();
        var recipeExecutionContext = await recipeManager.CreateExecutionContext(packages, cancellationToken);
        // var recipesPackage = await recipeManager.InstallRecipePackage(installableRecipe, packageSources);

        List<string> solutionPaths = new();
        if (File.Exists(settings.Path))
        {
            solutionPaths.Add(settings.Path);
        }
        else
        {
            solutionPaths = Directory.EnumerateFiles(settings.Path, "*.sln", SearchOption.AllDirectories).ToList();
        }

        var analyzerIds = settings.Ids.Where(x => x.EndsWith(":A")).Select(x => Regex.Replace(x,":A$", "")).ToHashSet();
        var allOtherIds = settings.Ids.Where(x => !x.EndsWith(":A")).Select(x => Regex.Replace(x,":A$", "")).ToHashSet();

        List<RecipeExecutionResult> allResults = [];
        foreach (var solutionPath in solutionPaths)
        {
            var recipeStartInfos = recipeExecutionContext.Recipes
                .Where(x =>
                {
                    if(settings.Ids.Length == 0)
                        return true;
                    if (analyzerIds.Contains(x.Id))
                        return x.Kind == RecipeKind.RoslynAnalyzer;
                    return allOtherIds.Contains(x.Id);
                })
                .GroupBy(x => x.Id)
                .Select(g => g.Any(x => x.Kind == RecipeKind.RoslynFixer) // we're either analyzer or fixing - not both
                    ? g.First(x => x.Kind == RecipeKind.RoslynFixer)
                    : g.First())
                .Select(x => recipeExecutionContext
                    .CreateRecipeStartInfo(x)
                    .WithOption(nameof(RoslynRecipe.SolutionFilePath), solutionPath)
                    .WithOption(nameof(RoslynRecipe.DryRun), settings.DryRun))
                .ToList();
            var recipe = (RoslynRecipe)recipeExecutionContext.CreateRecipe(recipeStartInfos);
            try
            {
                var recipeResult = await recipe.Execute(CancellationToken.None);
                allResults.Add(recipeResult);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error running recipe in {Solution} due to {Error}", solutionPath, ex.Message);
                continue;
            }
        }

        if (settings.GitPatchPath != null)
        {
            await WritePatchOutput(settings, allResults, cancellationToken);
        }

        return 0;
    }

    private async Task WritePatchOutput(Settings settings, List<RecipeExecutionResult> results, CancellationToken cancellationToken)
    {
        switch (settings.GitPatchMode)
        {
            case GitPatchMode.Combined:
            {
                var allDiffs = results.SelectMany(r => r.ChangedDocuments).ToList();
                if (allDiffs.Count == 0) break;
                var patch = GenerateGitPatch(allDiffs);
                var patchDir = Path.GetDirectoryName(settings.GitPatchPath!);
                if (patchDir != null && !Directory.Exists(patchDir))
                    Directory.CreateDirectory(patchDir);
                await File.WriteAllTextAsync(settings.GitPatchPath!, patch, cancellationToken);
                logger.LogInformation("Patch written to {PatchPath} ({FileCount} files changed)", settings.GitPatchPath, allDiffs.Count);
                break;
            }
            case GitPatchMode.Recipe:
            {
                var outputDir = settings.GitPatchPath!;
                if (!Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);
                var issuesWithDiffs = results
                    .SelectMany(r => r.FixedIssues)
                    .Where(i => i.Diffs.Count > 0)
                    .ToList();
                foreach (var issue in issuesWithDiffs)
                {
                    var patch = GenerateGitPatch(issue.Diffs);
                    var patchFile = Path.Combine(outputDir, $"{issue.IssueId}.patch");
                    await File.WriteAllTextAsync(patchFile, patch, cancellationToken);
                    logger.LogInformation("Patch written to {PatchPath} ({FileCount} files changed)", patchFile, issue.Diffs.Count);
                }
                if (issuesWithDiffs.Count == 0)
                    logger.LogInformation("No patches generated - no fixable issues found");
                else
                    logger.LogInformation("Generated {Count} patch file(s) in {Directory}", issuesWithDiffs.Count, outputDir);
                break;
            }
        }
    }

    private static string GenerateGitPatch(List<DocumentDiff> diffs)
    {
        var sb = new StringBuilder();
        var differ = new Differ();

        foreach (var diff in diffs)
        {
            var filePath = diff.FilePath.Replace('\\', '/');
            sb.AppendLine($"diff --git a/{filePath} b/{filePath}");
            sb.AppendLine($"--- a/{filePath}");
            sb.AppendLine($"+++ b/{filePath}");

            var oldLines = diff.OldText.Split('\n');
            var newLines = diff.NewText.Split('\n');
            var result = differ.CreateLineDiffs(diff.OldText, diff.NewText, ignoreWhitespace: false);

            foreach (var diffBlock in result.DiffBlocks)
            {
                var contextLines = 3;
                var oldStart = Math.Max(0, diffBlock.InsertStartB - contextLines);
                var deleteEnd = diffBlock.DeleteStartA + diffBlock.DeleteCountA;
                var insertEnd = diffBlock.InsertStartB + diffBlock.InsertCountB;
                var oldEnd = Math.Min(oldLines.Length, deleteEnd + contextLines);
                var newEnd = Math.Min(newLines.Length, insertEnd + contextLines);

                var hunkOldStart = oldStart + 1;
                var hunkOldCount = oldEnd - oldStart;
                var hunkNewStart = oldStart + 1;
                var hunkNewCount = (oldEnd - oldStart) - diffBlock.DeleteCountA + diffBlock.InsertCountB;

                sb.AppendLine($"@@ -{hunkOldStart},{hunkOldCount} +{hunkNewStart},{hunkNewCount} @@");

                for (var i = oldStart; i < diffBlock.DeleteStartA; i++)
                    sb.AppendLine($" {oldLines[i]}");

                for (var i = diffBlock.DeleteStartA; i < deleteEnd; i++)
                    sb.AppendLine($"-{oldLines[i]}");

                for (var i = diffBlock.InsertStartB; i < insertEnd; i++)
                    sb.AppendLine($"+{newLines[i]}");

                for (var i = deleteEnd; i < oldEnd; i++)
                    sb.AppendLine($" {oldLines[i]}");
            }
        }

        return sb.ToString();
    }
}

public enum GitPatchMode
{
    Combined,
    Recipe
}