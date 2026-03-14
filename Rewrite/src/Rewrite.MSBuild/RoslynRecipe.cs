using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Rewrite.Core;
using Rewrite.Core.Config;
using Rewrite.RewriteJava.Tree;
using Rewrite.RewriteText;

namespace Rewrite.MSBuild;

[DisplayName("Roslyn Fixup Recipe Runner")]
[Description("Executes Roslyn's Fixups as OpenRewrite Recipes")]
public class RoslynRecipe(IEnumerable<Assembly> recipeAssemblies, ILogger<RoslynRecipe> log) : ScanningRecipe<object>
{
    internal List<Assembly> RecipeAssemblies { get; set; } = recipeAssemblies.ToList();
    
    [DisplayName("Diagnostic Ids")]
    [Description("List of unique diagnostic IDs reported by roslyn analyzers that should have code fixup applied")]
    public HashSet<string> DiagnosticIdsToFix { get; init; } = new();
    
    [DisplayName("Description")]
    [Description("List of unique diagnostic IDs reported by roslyn analyzers that should be reported but no fixup applied")]
    public HashSet<string> DiagnosticIdsToReport { get; init; } = new();
    
    
    [DisplayName("Solution File Path")]
    [Description("The path to solution on which recipe is to be run")]
    public required string SolutionFilePath { get; set; }

    [DisplayName("Dry Run")]
    [Description("Run without applying")]
    public bool DryRun { get; set; } = false;
    public override Tree GetInitialValue(IExecutionContext ctx)
    {
        return J.Empty.Create();
    }

    public override ITreeVisitor<Tree, IExecutionContext> GetScanner(object acc)
    {
        return ITreeVisitor<Tree, IExecutionContext>.Noop();
    }

    public override ICollection<SourceFile> Generate(object acc, IExecutionContext ctx)
    {
        var dir = (AbsolutePath)Assembly.GetExecutingAssembly().Location / "tests";
        return base.Generate(acc, ctx);
    }


    /// <summary>
    /// Executes the recipe against source code stored in <see cref="SolutionFilePath"/>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Documents that have been changed, grouped by issue ID</returns>
    public async Task<RecipeExecutionResult> Execute(CancellationToken cancellationToken)
    {
        
   
        var allAnalyzers = RecipeAssemblies
            .SelectMany(x => x.GetTypes())
            .Where(x => typeof(DiagnosticAnalyzer).IsAssignableFrom(x) && !x.IsAbstract)
            .Select(Activator.CreateInstance)
            .Cast<DiagnosticAnalyzer>()
            .ToImmutableArray();
        
        var userSelectedDiagnosticIds = DiagnosticIdsToFix.Union(DiagnosticIdsToReport).ToHashSet();
        
        var fixers = RecipeAssemblies
            .SelectMany(x => x.GetTypes())
            .Where(x => typeof(CodeFixProvider).IsAssignableFrom(x) && !x.IsAbstract)
            .Select(Activator.CreateInstance)
            .Cast<CodeFixProvider>()
            .Where(x => x.FixableDiagnosticIds.Any())
            .SelectMany(fixer => fixer.FixableDiagnosticIds.Select(id => (Id: id, Fixer: fixer)))
            .DistinctBy(x => x.Id)
            .ToDictionary(x => x.Id, x => x.Fixer);
        
        
        var analyzersWithFixersById = allAnalyzers
            .SelectMany(analyzer => analyzer
                .SupportedDiagnostics
                .DistinctBy(x => x.Id)
                .Select(descriptor => (descriptor.Id, Analyzer: analyzer)))
            .LeftJoin(fixers, x => x.Id, x => x.Key, (a, b) => (a.Id, a.Analyzer, Fixer: b.Value))
            .Where(x => userSelectedDiagnosticIds.Contains(x.Id))
            .GroupBy(x => x.Id)
            .Select(x => (x.Key, x.First().Analyzer, x.FirstOrDefault().Fixer))
            .ToDictionary(x => x.Key, x => (x.Analyzer, x.Fixer));
        
        if (analyzersWithFixersById.Count == 0)
        {
            log.LogError("No analyzers targeting issue {DiagnosticIds} has been found", DiagnosticIdsToFix);
            return new RecipeExecutionResult(SolutionFilePath, TimeSpan.Zero, TimeSpan.Zero, [], []);
        }
        
        Stopwatch watch = new();
        watch.Start();
        var workspace = MSBuildWorkspace.Create();
        var result = ProcessTasks.StartProcess("dotnet", $"restore {SolutionFilePath}");
        result.WaitForExit();
        var originalSolution = await workspace.OpenSolutionAsync(SolutionFilePath, cancellationToken: cancellationToken);
        var solutionLoadTime = watch.Elapsed;
        log.LogDebug("Solution {SolutionFilePath} loaded in {Elapsed}", SolutionFilePath, solutionLoadTime);
        var solution = new SolutionEditor(originalSolution);
        
        var analyzersToApply = analyzersWithFixersById
            .Select(x => x.Value.Analyzer)
            .Distinct()
            .ToImmutableArray();
        
        var allDiagnostics = await GetDiagnostics(solution, analyzersToApply, cancellationToken);
        var diagnosticsForSelectedIds = allDiagnostics
            .Where(x => userSelectedDiagnosticIds.Contains(x.Id)).
            ToList();
        
        if (diagnosticsForSelectedIds.Count == 0)
        {
            log.LogDebug("No issues found in solution");
            return new RecipeExecutionResult(SolutionFilePath, TimeSpan.MinValue, TimeSpan.MinValue, [], []);
        }
        

        var fixableDiagnosticsIds = diagnosticsForSelectedIds
            .Where(x => DiagnosticIdsToFix.Contains(x.Id))
            .Select(x => x.Id)
            .ToHashSet();

        var codeFixProvidersToApply = analyzersWithFixersById
            .Where(x => x.Value.Fixer != null && fixableDiagnosticsIds.Contains(x.Key))
            .ToDictionary(x => x.Key, x => x.Value);
        
        var fixedIssues = await ApplyCodeFixers(codeFixProvidersToApply, solution, cancellationToken);

        var reportableDiagnosticsIds = diagnosticsForSelectedIds
            .Where(x => DiagnosticIdsToReport.Contains(x.Id) && !fixableDiagnosticsIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToList();

        var analyzersToHighlight = analyzersWithFixersById
            .Where(x => reportableDiagnosticsIds.Contains(x.Key))
            
            .ToDictionary(x => x.Key, x => x.Value.Analyzer);
        
        await ApplyAnalyzerHighlights(analyzersToHighlight, solution, cancellationToken);
        
        var changedDocuments = await GetChangedDocumentDiffs(solution, cancellationToken);

        if (!DryRun)
        {
            workspace.TryApplyChanges(solution.CurrentSolution);
        }

        watch.Stop();
        log.LogDebug("Executed recipes in {Elapsed}", watch.Elapsed);
        var recipeExecutionResult = new RecipeExecutionResult(SolutionFilePath, solutionLoadTime, watch.Elapsed, fixedIssues, changedDocuments);
        return recipeExecutionResult;
    }

    private async Task ApplyAnalyzerHighlights(Dictionary<string, DiagnosticAnalyzer> analyzersToHighlight, SolutionEditor solution, CancellationToken cancellationToken)
    {
        if (analyzersToHighlight.Count == 0)
            return;
        var diagnostics = await GetDiagnostics(solution, analyzersToHighlight.Values.Distinct().ToImmutableArray(), cancellationToken);
        var diagnosticsByDocument = diagnostics
            .Select(x => (Diagnostic: x, Document: solution.CurrentSolution.GetDocument(x.Location.SourceTree)))
            .Where(x => x.Document != null)
            .GroupBy(x => x.Document!, x => x.Diagnostic)
            .Where(x => x.Key is not SourceGeneratedDocument)
            .ToImmutableDictionary(x => x.Key, x => x.ToImmutableArray());

        foreach (var (document, currentDocumentDiagnostics) in diagnosticsByDocument)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

            // Group diagnostics by their target node span to handle multiple diagnostics on the same node
            var diagnosticsBySpan = currentDocumentDiagnostics
                .GroupBy(d => d.Location.SourceSpan)
                .ToDictionary(g => g.Key, g => g.ToList());

            var newRoot = root.ReplaceNodes(
                diagnosticsBySpan.Keys.Select(span => root.FindNode(span)),
                (originalNode, _) =>
                {
                    if (!diagnosticsBySpan.TryGetValue(originalNode.Span, out var nodeDiagnostics))
                        return originalNode;

                    var highlightComments = nodeDiagnostics
                        .Select(d => SyntaxFactory.Comment($"/* >> {d.Id} */"))
                        .Concat([SyntaxFactory.Space])
                        .ToList();

                    var existingTrivia = originalNode.GetLeadingTrivia();
                    var newTrivia = SyntaxFactory.TriviaList(highlightComments.Concat(existingTrivia));
                    return originalNode.WithLeadingTrivia(newTrivia);
                });

            solution.CurrentSolution = document.WithSyntaxRoot(newRoot).Project.Solution;
        }
    }

    private async Task<List<IssueFixResult>> ApplyCodeFixers(
        Dictionary<string, (DiagnosticAnalyzer Analyzer, CodeFixProvider Fixer)> fixesToApply, 
        SolutionEditor solution,
         CancellationToken cancellationToken)
    {
        List<IssueFixResult> fixedIssues = new();
        foreach (var (issueId, (analyzer, codeFixProvider)) in fixesToApply)
        {
            var recipeWatch = Stopwatch.StartNew();
            var diagnostics = await GetDiagnostics(solution, [analyzer], cancellationToken);
            diagnostics = diagnostics
                .Where(x => x.Id == issueId)
                .ToList();
            if (diagnostics.Count == 0)
            {
                continue;
            }

            var diagnosticsByDocument = diagnostics
                .Select(x => (Diagnostic: x, Document: solution.CurrentSolution.GetDocument(x.Location.SourceTree)))
                .Where(x => x.Document != null)
                .GroupBy(x => x.Document!, x => x.Diagnostic)
                .Where(x => x.Key is not SourceGeneratedDocument)
                .ToImmutableDictionary(x => x.Key, x => x.ToImmutableArray());

            var diagnosticProvider = new FixMultipleDiagnosticProvider(diagnosticsByDocument);
            // var codeFixProvider = analyzersWithFixersById[diagnosticIssue.Key].Fixer;
            var fixAllProvider = codeFixProvider.GetFixAllProvider() ?? WellKnownFixAllProviders.BatchFixer;

            Diagnostic? sampledDiagnostic = null;
            string? equivalenceKey = null;
            // try to find first viable fixup type for this issue type
            foreach(var diagnostic in diagnostics)
            {
                var document = solution.CurrentSolution.GetDocument(diagnostic.Location.SourceTree);
                if (document == null)
                    throw new Exception($"Could not find document associated with {diagnostic.Id} {diagnostic.Descriptor.Title}");

                var actions = new List<CodeAction>();
                var context = new CodeFixContext(document!, diagnostic, (a, d) => actions.Add(a), CancellationToken.None);
                try
                {
                    await codeFixProvider.RegisterCodeFixesAsync(context);
                }
                catch (Exception)
                {
                    continue;
                }

                if (actions.Count == 0)
                {
                    continue;
                }
                
                var codeFixAction = actions[0];
                
                if (!codeFixAction.NestedActions.IsEmpty)
                {
                    continue;
                }
                sampledDiagnostic = diagnostic;
                equivalenceKey = codeFixAction.EquivalenceKey;
                break;
            }

            if (sampledDiagnostic == null)
            {
                // generally we end up here when an analyzer has a fixer associated with it, but fixer determined that it can't actually fix it automatically (didn't register any code actions)
                log.LogDebug("No fixable issues found in solution");
                break;
            }

            log.LogDebug("Fixing {DiagnosticId}: '{Title}' using {TypeName} in {DocumentCount} documents ({OccurrenceCount} occurrences)",
                issueId,
                sampledDiagnostic.Descriptor.Title,
                codeFixProvider.GetType().Name,
                diagnosticsByDocument.Keys.Count(),
                diagnostics.Count()
            );
            
            var fixAllContext = new FixAllContext(
                diagnosticsByDocument.Keys.First(),
                codeFixProvider,
                FixAllScope.Solution,
                equivalenceKey,
                [issueId],
                diagnosticProvider,
                cancellationToken);
            Solution newSolution;
            try
            {
                var codeAction = await fixAllProvider.GetFixAsync(fixAllContext);
                if (codeAction == null)
                {
                    log.LogDebug("Skipping {IssueId}: FixAllProvider returned no bulk fix (individual fixes may exist but bulk application is not supported)", issueId);
                    continue;
                }
                var operations = await codeAction.GetOperationsAsync(cancellationToken);
                var applyChangesOperation = operations.OfType<ApplyChangesOperation>().FirstOrDefault();
                if (applyChangesOperation == null)
                {
                    log.LogDebug("Skipping {IssueId}: code action produced no ApplyChangesOperation", issueId);
                    continue;
                }
                newSolution = applyChangesOperation.ChangedSolution;
            }
            catch (Exception e)
            {
                log.LogError(e, "Unable to apply {IssueId} due to internal CodeFixup logic error", issueId);
                continue;
            }
            
            // var affectedDocumentIds = await GetChangedDocumentsAsync(solution.Solution, newSolution, cancellationToken);
            // var affectedDocuments = affectedDocumentIds
            //     .Select(docId => solution.Solution.GetDocument(docId)?.FilePath)
            //     .Where(x => x != null)
            //     .Select(x => ((AbsolutePath)SolutionFilePath).GetRelativePathTo((AbsolutePath)x));
            // changedFiles.AddRange(affectedDocuments);
            // foreach (var docId in affectedDocuments.Take(1))
            // {
            //     var before = (await solution.Solution.GetDocument(docId)!.GetTextAsync()).ToString();
            //     var after = (await newSolution.GetDocument(docId)!.GetTextAsync()).ToString();
            //
            //     var diffs = StringDiffer.GetDifferences(before, after);
            //     Console.WriteLine(solution.Solution.GetDocument(docId).FilePath);
            //     Console.WriteLine("======");
            //     Console.WriteLine(diffs);
            // }
            
            var solutionBeforeFix = solution.CurrentSolution;
            solution.CurrentSolution = newSolution;
            var affectedDocumentIds = await GetChangedDocumentsAsync(solution, cancellationToken);
            var affectedDocuments = affectedDocumentIds
                .Select(x => (Document: solution.CurrentSolution.GetDocument(x.DocumentId)!, x.ChangedLineNumbers))
                .ToList();
            var issueDiffs = await GetDocumentDiffsBetween(solutionBeforeFix, newSolution, cancellationToken);
            var issueFixResult = new IssueFixResult(
                IssueId: issueId,
                ExecutionTime: recipeWatch.Elapsed,
                Fixes: affectedDocuments
                    .Select(x => new DocumentFixResult(x.Document.FilePath!, x.ChangedLineNumbers))
                    .ToList(),
                Diffs: issueDiffs);
            fixedIssues.Add(issueFixResult);
            recipeWatch.Stop();
            
        }

        return fixedIssues;
    }

    private async Task<List<Diagnostic>> GetDiagnostics(
        SolutionEditor solution, 
        ImmutableArray<DiagnosticAnalyzer> analyzers, 
        CancellationToken cancellationToken)
    {
        
        var compilationTasks = solution.CurrentSolution.Projects.Select(p => p.GetCompilationAsync(cancellationToken));
        var compilations = await Task.WhenAll(compilationTasks);
        
        List<Diagnostic> diagnostics = [];
        foreach (var compilation in compilations.Where(x => x != null).Cast<Compilation>())
        {
            var withAnalyzers = compilation.WithAnalyzers(analyzers);
            var diags = await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken);
            diags = diags
                .Where(x => solution.CurrentSolution.GetDocument(x.Location.SourceTree) is not SourceGeneratedDocument)
                .ToImmutableArray();
            diagnostics.AddRange(diags);
        }

        return diagnostics;
    }
    
    static async Task<IEnumerable<(DocumentId DocumentId, List<int> ChangedLineNumbers)>> GetChangedDocumentsAsync(
        SolutionEditor solution,
        CancellationToken cancellationToken = default)
    {
        var changedDocumentIds = new List<(DocumentId, List<int> ChangedLineNumbers)>();

        foreach (var projectId in solution.CurrentSolution.ProjectIds)
        {
            var oldProject = solution.OriginalSolution.GetProject(projectId);
            var newProject = solution.CurrentSolution.GetProject(projectId);

            if (oldProject == null || newProject == null)
                continue;

            foreach (var documentId in newProject.DocumentIds)
            {
                var oldDoc = oldProject.GetDocument(documentId);
                var newDoc = newProject.GetDocument(documentId);

                if (oldDoc == null || newDoc == null)
                    continue;

                var oldText = await oldDoc.GetTextAsync(cancellationToken);
                var newText = await newDoc.GetTextAsync(cancellationToken);

                if (!oldText.ContentEquals(newText))
                {
                    var diffLineNumbers = DiffHelper.GetDifferentLineNumbers(oldText.ToString(), newText.ToString());
                    changedDocumentIds.Add((documentId,diffLineNumbers));
                    
                }
            }
        }

        return changedDocumentIds;
    }
    
    private static async Task<List<DocumentDiff>> GetChangedDocumentDiffs(
        SolutionEditor solution,
        CancellationToken cancellationToken)
    {
        var diffs = new List<DocumentDiff>();

        foreach (var projectId in solution.CurrentSolution.ProjectIds)
        {
            var oldProject = solution.OriginalSolution.GetProject(projectId);
            var newProject = solution.CurrentSolution.GetProject(projectId);

            if (oldProject == null || newProject == null)
                continue;

            foreach (var documentId in newProject.DocumentIds)
            {
                var oldDoc = oldProject.GetDocument(documentId);
                var newDoc = newProject.GetDocument(documentId);

                if (oldDoc == null || newDoc == null)
                    continue;

                var oldText = await oldDoc.GetTextAsync(cancellationToken);
                var newText = await newDoc.GetTextAsync(cancellationToken);

                if (!oldText.ContentEquals(newText) && newDoc.FilePath != null)
                {
                    diffs.Add(new DocumentDiff(newDoc.FilePath, oldText.ToString(), newText.ToString()));
                }
            }
        }

        return diffs;
    }

    private static async Task<List<DocumentDiff>> GetDocumentDiffsBetween(
        Solution oldSolution,
        Solution newSolution,
        CancellationToken cancellationToken)
    {
        var diffs = new List<DocumentDiff>();

        foreach (var projectId in newSolution.ProjectIds)
        {
            var oldProject = oldSolution.GetProject(projectId);
            var newProject = newSolution.GetProject(projectId);

            if (oldProject == null || newProject == null)
                continue;

            foreach (var documentId in newProject.DocumentIds)
            {
                var oldDoc = oldProject.GetDocument(documentId);
                var newDoc = newProject.GetDocument(documentId);

                if (oldDoc == null || newDoc == null)
                    continue;

                var oldText = await oldDoc.GetTextAsync(cancellationToken);
                var newText = await newDoc.GetTextAsync(cancellationToken);

                if (!oldText.ContentEquals(newText) && newDoc.FilePath != null)
                {
                    diffs.Add(new DocumentDiff(newDoc.FilePath, oldText.ToString(), newText.ToString()));
                }
            }
        }

        return diffs;
    }

    /// <summary>
    /// Used to track solution changes as it's updated between recipe executions.
    /// Doing it this way allows us to use it cleanly in lambdas so closures point to the most recent state, not when closure was made
    /// </summary>
    class SolutionEditor(Solution solution)
    {
        public Solution OriginalSolution { get; } = solution;
        public Solution CurrentSolution { get; set; } = solution;
    }

}