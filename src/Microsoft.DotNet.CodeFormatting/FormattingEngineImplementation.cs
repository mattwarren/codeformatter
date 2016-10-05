// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.DotNet.CodeFormatting
{
    [Export(typeof(IFormattingEngine))]
    internal sealed class FormattingEngineImplementation : IFormattingEngine
    {
        /// <summary>
        /// Developers who want to opt out of the code formatter for items like unicode
        /// tables can surround them with #if !DOTNET_FORMATTER.  
        /// </summary>
        internal const string TablePreprocessorSymbolName = "DOTNET_FORMATTER";

        private readonly Options _options;
        private readonly IEnumerable<IFormattingFilter> _filters;
        private readonly IEnumerable<Lazy<ISyntaxFormattingRule, IRuleMetadata>> _syntaxRules;
        private readonly IEnumerable<Lazy<ILocalSemanticFormattingRule, IRuleMetadata>> _localSemanticRules;
        private readonly IEnumerable<Lazy<IGlobalSemanticFormattingRule, IRuleMetadata>> _globalSemanticRules;
        private readonly Stopwatch _watch = new Stopwatch();
        private readonly Dictionary<string, bool> _ruleMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private bool _allowTables;
        private bool _verbose;

        public ImmutableArray<string> CopyrightHeader
        {
            get { return _options.CopyrightHeader; }
            set { _options.CopyrightHeader = value; }
        }

        public ImmutableArray<string[]> PreprocessorConfigurations
        {
            get { return _options.PreprocessorConfigurations; }
            set { _options.PreprocessorConfigurations = value; }
        }

        public ImmutableArray<string> FileNames
        {
            get { return _options.FileNames; }
            set { _options.FileNames = value; }
        }

        public IFormatLogger FormatLogger
        {
            get { return _options.FormatLogger; }
            set { _options.FormatLogger = value; }
        }

        public bool AllowTables
        {
            get { return _allowTables; }
            set { _allowTables = value; }
        }

        public bool Verbose
        {
            get { return _verbose; }
            set { _verbose = value; }
        }

        public ImmutableArray<IRuleMetadata> AllRules
        {
            get
            {
                var list = new List<IRuleMetadata>();
                list.AddRange(_syntaxRules.Select(x => x.Metadata));
                list.AddRange(_localSemanticRules.Select(x => x.Metadata));
                list.AddRange(_globalSemanticRules.Select(x => x.Metadata));
                return list.ToImmutableArray();
            }
        }

        [ImportingConstructor]
        internal FormattingEngineImplementation(
            Options options,
            [ImportMany] IEnumerable<IFormattingFilter> filters,
            [ImportMany] IEnumerable<Lazy<ISyntaxFormattingRule, IRuleMetadata>> syntaxRules,
            [ImportMany] IEnumerable<Lazy<ILocalSemanticFormattingRule, IRuleMetadata>> localSemanticRules,
            [ImportMany] IEnumerable<Lazy<IGlobalSemanticFormattingRule, IRuleMetadata>> globalSemanticRules)
        {
            _options = options;
            _filters = filters;
            _syntaxRules = syntaxRules;
            _localSemanticRules = localSemanticRules;
            _globalSemanticRules = globalSemanticRules;

            foreach (var rule in AllRules)
            {
                _ruleMap[rule.Name] = rule.DefaultRule;
            }
        }

        private IEnumerable<TRule> GetOrderedRules<TRule>(IEnumerable<Lazy<TRule, IRuleMetadata>> rules)
            where TRule : IFormattingRule
        {
            return rules
                .OrderBy(r => r.Metadata.Order)
                .Where(r => _ruleMap[r.Metadata.Name])
                .Select(r => r.Value)
                .ToList();
        }

        public Task FormatSolutionAsync(Solution solution, CancellationToken cancellationToken)
        {
            var documentIds = solution.Projects.SelectMany(x => x.DocumentIds).ToList();
            return FormatAsync(solution.Workspace, documentIds, cancellationToken);
        }

        public Task FormatProjectAsync(Project project, CancellationToken cancellationToken)
        {
            return FormatAsync(project.Solution.Workspace, project.DocumentIds, cancellationToken);
        }

        public async Task FormatWorkspaceAsync(Workspace workspace, Project project, CancellationToken cancellationToken)
        {
            var overallTimer = Stopwatch.StartNew();
            
            var orderedSyntaxRules = GetOrderedRules(_syntaxRules);
            var orderedLocalSemanticRules = GetOrderedRules(_localSemanticRules);
            foreach (var document in project.Documents)
            {
                var timer = Stopwatch.StartNew();
                var fileName = Path.GetFileName(document.FilePath);
                SourceText sourceText;
                using (var fsSource = new FileStream(document.FilePath, FileMode.Open, FileAccess.Read))
                {
                    sourceText = SourceText.From(fsSource);                    
                }
                
                var syntaxTree = CSharpSyntaxTree.ParseText(sourceText, CSharpParseOptions.Default, document.FilePath, cancellationToken);
                var syntaxRoot = await syntaxTree.GetRootAsync(cancellationToken);
                var originalContents = syntaxRoot.ToFullString();

                // Run the Syntax Formatting rules
                var syntaxRootAfterSyntaxChange = syntaxRoot;
                foreach (var rule in orderedSyntaxRules)
                {
                    if (rule.SupportsLanguage(document.Project.Language))
                    {
                        syntaxRootAfterSyntaxChange = rule.Process(syntaxRootAfterSyntaxChange, document.Project.Language);
                    }
                }                
                syntaxRoot = syntaxRootAfterSyntaxChange;

                // Run the Semantic Formatting rules
                var syntaxRootAfterSemanticChanges = syntaxRoot;
                foreach (var localSemanticRule in orderedLocalSemanticRules)
                {
                    syntaxRootAfterSemanticChanges = await localSemanticRule.ProcessAsync(document, syntaxRootAfterSemanticChanges, cancellationToken);
                }
                syntaxRoot = syntaxRootAfterSemanticChanges;

                // Seems like the Syntax/Semantic fixes don't always leave the code formatted correctly!!
                var finalResults = Formatter.Format(syntaxRoot, workspace).ToFullString();
                var modified = finalResults != originalContents;
                if (modified)
                {
                    File.WriteAllText(document.FilePath, finalResults, sourceText.Encoding);
                    FormatLogger.WriteLine("    {0} was reformatted - {1:N2} msecs", fileName, timer.Elapsed.TotalMilliseconds);
                }
                else if (_verbose)
                {
                    FormatLogger.WriteLine("    {0} - {1:N2} msecs", fileName, timer.Elapsed.TotalMilliseconds);
                }
            }

            overallTimer.Stop();
            FormatLogger.WriteLine("Total time {0} ({1:N2} msecs)", overallTimer.Elapsed, overallTimer.Elapsed.TotalMilliseconds);
        }

        public void ToggleRuleEnabled(IRuleMetadata ruleMetaData, bool enabled)
        {
            _ruleMap[ruleMetaData.Name] = enabled;
        }

        private async Task FormatAsync(Workspace workspace, IReadOnlyList<DocumentId> documentIds, CancellationToken cancellationToken)
        {
            var watch = new Stopwatch();
            watch.Start();

            var originalSolution = workspace.CurrentSolution;
            var solution = await FormatCoreAsync(originalSolution, documentIds, cancellationToken);

            watch.Stop();

            if (!workspace.TryApplyChanges(solution))
            {
                FormatLogger.WriteErrorLine("Unable to save changes to disk");
            }

            FormatLogger.WriteLine("Total time {0}", watch.Elapsed);
        }

        private Solution AddTablePreprocessorSymbol(Solution solution)
        {
            var projectIds = solution.ProjectIds;
            foreach (var projectId in projectIds)
            {
                var project = solution.GetProject(projectId);
                var parseOptions = project.ParseOptions as CSharpParseOptions;
                if (parseOptions != null)
                {
                    var list = new List<string>();
                    list.AddRange(parseOptions.PreprocessorSymbolNames);
                    list.Add(TablePreprocessorSymbolName);
                    parseOptions = parseOptions.WithPreprocessorSymbols(list);
                    solution = project.WithParseOptions(parseOptions).Solution;
                }
            }

            return solution;
        }

        /// <summary>
        /// Remove the added table preprocessor symbol.  Don't want that saved into the project
        /// file as a change. 
        /// </summary>
        private Solution RemoveTablePreprocessorSymbol(Solution newSolution, Solution oldSolution)
        {
            var projectIds = newSolution.ProjectIds;
            foreach (var projectId in projectIds)
            {
                var oldProject = oldSolution.GetProject(projectId);
                var newProject = newSolution.GetProject(projectId);
                newSolution = newProject.WithParseOptions(oldProject.ParseOptions).Solution;
            }

            return newSolution;
        }

        internal async Task<Solution> FormatCoreAsync(Solution originalSolution, IReadOnlyList<DocumentId> documentIds, CancellationToken cancellationToken)
        {
            var solution = originalSolution;

            if (_allowTables)
            {
                solution = AddTablePreprocessorSymbol(originalSolution);
            }

            solution = await RunSyntaxPass(solution, documentIds, cancellationToken);
            solution = await RunLocalSemanticPass(solution, documentIds, cancellationToken);
            solution = await RunGlobalSemanticPass(solution, documentIds, cancellationToken);

            if (_allowTables)
            {
                solution = RemoveTablePreprocessorSymbol(solution, originalSolution);
            }

            return solution;
        }

        private bool ShouldBeProcessed(Document document)
        {
            foreach (var filter in _filters)
            {
                var shouldBeProcessed = filter.ShouldBeProcessed(document);
                if (!shouldBeProcessed)
                    return false;
            }

            return true;
        }

        private Task<SyntaxNode> GetSyntaxRootAndFilter(Document document, CancellationToken cancellationToken)
        {
            if (!ShouldBeProcessed(document))
            {
                return Task.FromResult<SyntaxNode>(null);
            }

            return document.GetSyntaxRootAsync(cancellationToken);
        }

        private Task<SyntaxNode> GetSyntaxRootAndFilter(IFormattingRule formattingRule, Document document, CancellationToken cancellationToken)
        {
            if (!formattingRule.SupportsLanguage(document.Project.Language))
            {
                return Task.FromResult<SyntaxNode>(null);
            }

            return GetSyntaxRootAndFilter(document, cancellationToken);
        }

        private void StartDocument()
        {
            _watch.Restart();
        }

        private void EndDocument(Document document)
        {
            _watch.Stop();
            if (_verbose)
            {
                FormatLogger.WriteLine("    {0} {1} seconds", document.Name, _watch.Elapsed.TotalSeconds);
            }
        }

        /// <summary>
        /// Semantics is not involved in this pass at all.  It is just a straight modification of the 
        /// parse tree so there are no issues about ensuring the version of <see cref="SemanticModel"/> and
        /// the <see cref="SyntaxNode"/> line up.  Hence we do this by iteraning every <see cref="Document"/> 
        /// and processing all rules against them at once 
        /// </summary>
        private async Task<Solution> RunSyntaxPass(Solution originalSolution, IReadOnlyList<DocumentId> documentIds, CancellationToken cancellationToken)
        {
            FormatLogger.WriteLine("\tSyntax Pass");

            var currentSolution = originalSolution;
            foreach (var documentId in documentIds)
            {
                var document = originalSolution.GetDocument(documentId);
                var syntaxRoot = await GetSyntaxRootAndFilter(document, cancellationToken);
                if (syntaxRoot == null)
                {
                    continue;
                }

                StartDocument();
                var newRoot = RunSyntaxPass(syntaxRoot, document.Project.Language);
                EndDocument(document);

                if (newRoot != syntaxRoot)
                {
                    currentSolution = currentSolution.WithDocumentSyntaxRoot(document.Id, newRoot);
                }
            }

            return currentSolution;
        }

        private SyntaxNode RunSyntaxPass(SyntaxNode root, string languageName)
        {
            foreach (var rule in GetOrderedRules(_syntaxRules))
            {
                if (rule.SupportsLanguage(languageName))
                {
                    root = rule.Process(root, languageName);
                }
            }

            return root;
        }

        private async Task<Solution> RunLocalSemanticPass(Solution solution, IReadOnlyList<DocumentId> documentIds, CancellationToken cancellationToken)
        {
            FormatLogger.WriteLine("\tLocal Semantic Pass");
            foreach (var localSemanticRule in GetOrderedRules(_localSemanticRules))
            {
                solution = await RunLocalSemanticPass(solution, documentIds, localSemanticRule, cancellationToken);
            }

            return solution;
        }

        private async Task<Solution> RunLocalSemanticPass(Solution originalSolution, IReadOnlyList<DocumentId> documentIds, ILocalSemanticFormattingRule localSemanticRule, CancellationToken cancellationToken)
        {
            if (_verbose)
            {
                FormatLogger.WriteLine("  {0}", localSemanticRule.GetType().Name);
            }

            var currentSolution = originalSolution;
            foreach (var documentId in documentIds)
            {
                var document = originalSolution.GetDocument(documentId);
                var syntaxRoot = await GetSyntaxRootAndFilter(localSemanticRule, document, cancellationToken);
                if (syntaxRoot == null)
                {
                    continue;
                }

                StartDocument();
                var newRoot = await localSemanticRule.ProcessAsync(document, syntaxRoot, cancellationToken);
                EndDocument(document);

                if (syntaxRoot != newRoot)
                {
                    currentSolution = currentSolution.WithDocumentSyntaxRoot(documentId, newRoot);
                }
            }

            return currentSolution;
        }

        private async Task<Solution> RunGlobalSemanticPass(Solution solution, IReadOnlyList<DocumentId> documentIds, CancellationToken cancellationToken)
        {
            FormatLogger.WriteLine("\tGlobal Semantic Pass");
            foreach (var globalSemanticRule in GetOrderedRules(_globalSemanticRules))
            {
                solution = await RunGlobalSemanticPass(solution, documentIds, globalSemanticRule, cancellationToken);
            }

            return solution;
        }

        private async Task<Solution> RunGlobalSemanticPass(Solution solution, IReadOnlyList<DocumentId> documentIds, IGlobalSemanticFormattingRule globalSemanticRule, CancellationToken cancellationToken)
        {
            if (_verbose)
            {
                FormatLogger.WriteLine("  {0}", globalSemanticRule.GetType().Name);
            }

            foreach (var documentId in documentIds)
            {
                var document = solution.GetDocument(documentId);
                var syntaxRoot = await GetSyntaxRootAndFilter(globalSemanticRule, document, cancellationToken);
                if (syntaxRoot == null)
                {
                    continue;
                }

                StartDocument();
                solution = await globalSemanticRule.ProcessAsync(document, syntaxRoot, cancellationToken);
                EndDocument(document);
            }

            return solution;
        }
    }
}
