using System.ComponentModel;
using System.Text.RegularExpressions;
using Rewrite.Core.Config;
using Rewrite.MSBuild;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Rewrite.Server.Commands;

[PublicAPI]
public class ListRecipesCommand(RecipeManager recipeManager) : AsyncCommand<ListRecipesCommand.Settings>
{
    [PublicAPI]
    public class Settings : BaseSettings
    {
        private Lazy<Regex> PackageNameRegex = new Lazy<Regex>(() =>
        {
            return new Regex(
                "^(?<id>[A-Za-z0-9](?:[A-Za-z0-9._-]{0,98}[A-Za-z0-9])?)(?::(?<version>(?:(?:0|[1-9]\\d*)\\.(?:0|[1-9]\\d*)\\.(?:0|[1-9]\\d*)(?:\\.(?:0|[1-9]\\d*))?(?:-(?:0|[1-9]\\d*|[A-Za-z-][0-9A-Za-z-]*)(?:\\.(?:0|[1-9]\\d*|[A-Za-z-][0-9A-Za-z-]*))*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?)|(?i:SNAPSHOT|RELEASE)))?$",
                RegexOptions.Compiled);
        });

        [CommandOption("-p|--package <NAME>")]
        [Description("Nuget Package Name. By default uses latest version. An explicit version can be specified using syntax <packageName>:<version>. " +
                     "If version is SNAPSHOT, uses latest prerelease version. " +
                     "If version is RELEASE, uses latest stable version." +
                     "Can be specified multiple times.")]
        public required string[] Packages { get; set; }

        public override ValidationResult Validate()
        {
            if (Packages.Length == 0)
            {
                return ValidationResult.Error("At least one package must be specified");
            }
            var invalidPackageNames = Packages
                .Select(x => PackageNameRegex.Value.Match(x)).Where(x => x.Success == false)
                .ToArray();
            if (invalidPackageNames.Length > 0)
            {
                return ValidationResult.Error("Invalid package names");
            }

            return base.Validate();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var packages = settings.Packages.Select(InstallableRecipeExtensions.GetLibraryRange).ToList();
        var recipeExecutionContext = await recipeManager.CreateExecutionContext(packages, cancellationToken);

        var recipes = recipeExecutionContext.Recipes;
        if (recipes.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No recipes found in the specified package(s).[/]");
            return 0;
        }

        var openRewriteRecipes = recipes.Where(r => r.Kind == RecipeKind.OpenRewrite).ToList();
        var roslynRecipes = recipes.Where(r => r.Kind is RecipeKind.RoslynAnalyzer or RecipeKind.RoslynFixer).ToList();

        if (roslynRecipes.Count > 0)
        {
            var fixerIds = roslynRecipes
                .Where(r => r.Kind == RecipeKind.RoslynFixer)
                .Select(r => r.Id)
                .ToHashSet();

            // Deduplicate by ID, preferring the fixer entry for display name/description
            var byId = roslynRecipes
                .GroupBy(r => r.Id)
                .Select(g => g.FirstOrDefault(r => r.Kind == RecipeKind.RoslynFixer) ?? g.First())
                .OrderBy(r => r.Id)
                .ToList();

            var table = new Table();
            table.Title = new TableTitle($"[bold]Roslyn Recipes[/] ({byId.Count})");
            table.Border(TableBorder.Rounded);
            table.AddColumn(new TableColumn("[bold]ID[/]").NoWrap());
            table.AddColumn(new TableColumn("[bold]Name[/]"));
            table.AddColumn(new TableColumn("[bold]Has Fix[/]").Centered());
            table.AddColumn(new TableColumn("[bold]Description[/]"));

            foreach (var recipe in byId)
            {
                var hasFix = fixerIds.Contains(recipe.Id);
                table.AddRow(
                    Markup.Escape(recipe.Id),
                    Markup.Escape(recipe.DisplayName),
                    hasFix ? "[green]Yes[/]" : "[dim]No[/]",
                    Markup.Escape(Truncate(recipe.Description, 80))
                );
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            var fixableCount = byId.Count(r => fixerIds.Contains(r.Id));
            var analyzerOnlyCount = byId.Count - fixableCount;
            AnsiConsole.MarkupLine($"  [green]{fixableCount}[/] with code fix, [dim]{analyzerOnlyCount}[/] analyzer only");
            AnsiConsole.WriteLine();
        }

        if (openRewriteRecipes.Count > 0)
        {
            var table = new Table();
            table.Title = new TableTitle($"[bold]OpenRewrite Recipes[/] ({openRewriteRecipes.Count})");
            table.Border(TableBorder.Rounded);
            table.AddColumn(new TableColumn("[bold]ID[/]").NoWrap());
            table.AddColumn(new TableColumn("[bold]Name[/]"));
            table.AddColumn(new TableColumn("[bold]Description[/]"));

            foreach (var recipe in openRewriteRecipes.OrderBy(r => r.Id))
            {
                table.AddRow(
                    Markup.Escape(recipe.Id),
                    Markup.Escape(recipe.DisplayName),
                    Markup.Escape(Truncate(recipe.Description, 80))
                );
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine($"[bold]Total: {recipes.Count} recipe(s)[/]");
        return 0;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..(maxLength - 3)] + "...";
    }
}
