using Microsoft.CodeAnalysis;

namespace Rewrite.MSBuild;

public record RecipeExecutionResult(string SolutionFile, TimeSpan SolutionLoadTime, TimeSpan ExecutionTime, List<IssueFixResult> FixedIssues, List<DocumentDiff> ChangedDocuments);

public record IssueFixResult(string IssueId, TimeSpan ExecutionTime, List<DocumentFixResult> Fixes, List<DocumentDiff> Diffs);

public record DocumentFixResult(string FileName, List<int> LineNumbers);

public record DocumentDiff(string FilePath, string OldText, string NewText);