﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Scripting;
using Mono.Cecil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RoslynPad.Build.ILDecompiler;
using RoslynPad.NuGet;
using RoslynPad.Roslyn;
using RoslynPad.Roslyn.Scripting;
using RoslynPad.Runtime;
using RoslynPad.Utilities;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace RoslynPad.Build
{
    /// <summary>
    /// An <see cref="IExecutionHost"/> implementation that compiles scripts to disk as EXEs and executes them in their own process.
    /// </summary>
    internal class ExecutionHost : IExecutionHost
    {
        private static Lazy<string> CurrentPid { get; } = new Lazy<string>(() => Process.GetCurrentProcess().Id.ToString());

        private readonly ExecutionHostParameters _parameters;
        private readonly IRoslynHost _roslynHost;
        private readonly SyntaxTree _initHostSyntax;
        private readonly HashSet<LibraryRef> _libraries;
        private ScriptOptions _scriptOptions;
        private CancellationTokenSource? _executeCts;
        private Task? _restoreTask;
        private CancellationTokenSource? _restoreCts;
        private ExecutionPlatform? _platform;
        private string? _assemblyPath;
        private PlatformVersion? _platformVersion;
        private string _name;
        private bool _running;
        private bool _initializeBuildPathAfterRun;
        private TextWriter? _processInputStream;

        public ExecutionPlatform Platform
        {
            get => _platform ?? throw new InvalidOperationException("No platform selected");
            set
            {
                _platform = value;

                if (!value.HasVersions)
                {
                    _platformVersion = null;
                    InitializeBuildPath(stop: true);
                    TryRestore();
                }
            }
        }

        public PlatformVersion? PlatformVersion
        {
            get => _platformVersion;
            set
            {
                _platformVersion = value;
                InitializeBuildPath(stop: true);
                TryRestore();
            }
        }

        public bool HasPlatform => _platform != null && (!_platform.HasVersions || _platformVersion != null);

        public string Name
        {
            get => _name;
            set
            {
                if (!string.Equals(_name, value, StringComparison.Ordinal))
                {
                    _name = value;
                    InitializeBuildPath(stop: false);
                    TryRestore();
                }
            }
        }

        private readonly JsonSerializer _jsonSerializer;

        private string BuildPath => _parameters.BuildPath;

        public ExecutionHost(ExecutionHostParameters parameters, IRoslynHost roslynHost)
        {
            _jsonSerializer = new JsonSerializer
            {
                TypeNameHandling = TypeNameHandling.Auto,
                TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple
            };

            _name = "";
            _parameters = parameters;
            _roslynHost = roslynHost;
            _libraries = new HashSet<LibraryRef>();
            _scriptOptions = ScriptOptions.Default
                   .WithImports(parameters.Imports)
                   .WithMetadataResolver(new CachedScriptMetadataResolver(parameters.WorkingDirectory));

            _initHostSyntax = ParseSyntaxTree(@"RoslynPad.Runtime.RuntimeInitializer.Initialize();", roslynHost.ParseOptions);

            MetadataReferences = ImmutableArray<MetadataReference>.Empty;
        }

        private static void WriteJson(string path, JToken token)
        {
            using var file = File.CreateText(path);
            using var writer = new JsonTextWriter(file);
            token.WriteTo(writer);
        }

        public event Action<IList<CompilationErrorResultObject>>? CompilationErrors;
        public event Action<string>? Disassembled;
        public event Action<ResultObject>? Dumped;
        public event Action<ExceptionResultObject>? Error;
        public event Action? ReadInput;
        public event Action? RestoreStarted;
        public event Action<RestoreResult>? RestoreCompleted;

        public void Dispose()
        {
        }

        private void InitializeBuildPath(bool stop)
        {
            if (!HasPlatform)
            {
                return;
            }

            if (stop)
            {
                StopProcess();
            }
            else if (_running)
            {
                _initializeBuildPathAfterRun = true;
                return;
            }

            CleanupBuildPath();
        }

        private void CleanupBuildPath()
        {
            StopProcess();

            foreach (var file in IOUtilities.EnumerateFilesRecursive(BuildPath))
            {
                IOUtilities.PerformIO(() => File.Delete(file));
            }
        }

        public async Task ExecuteAsync(string code, bool disassemble, OptimizationLevel? optimizationLevel)
        {
            await new NoContextYieldAwaitable();

            await RestoreTask.ConfigureAwait(false);

            try
            {
                _running = true;

                using var executeCts = new CancellationTokenSource();
                var cancellationToken = executeCts.Token;

                var script = CreateScriptRunner(code, optimizationLevel);

                _assemblyPath = Path.Combine(BuildPath, "bin", $"rp-{Name}.{AssemblyExtension}");

                var diagnostics = await script.SaveAssembly(_assemblyPath, cancellationToken).ConfigureAwait(false);
                SendDiagnostics(diagnostics);

                if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                {
                    return;
                }

                if (disassemble)
                {
                    Disassemble();
                }

                _executeCts = executeCts;

                await RunProcess(_assemblyPath, cancellationToken);
            }
            finally
            {
                _executeCts = null;
                _running = false;

                if (_initializeBuildPathAfterRun)
                {
                    _initializeBuildPathAfterRun = false;
                    InitializeBuildPath(stop: false);
                }
            }
        }

        private void Disassemble()
        {
            using var assembly = AssemblyDefinition.ReadAssembly(_assemblyPath);
            var output = new PlainTextOutput();
            var disassembler = new ReflectionDisassembler(output, false, CancellationToken.None);
            disassembler.WriteModuleContents(assembly.MainModule);
            Disassembled?.Invoke(output.ToString());
        }

        private string AssemblyExtension => Platform.IsCore ? "dll" : "exe";

        public ImmutableArray<MetadataReference> MetadataReferences { get; private set; }

        private ScriptRunner CreateScriptRunner(string code, OptimizationLevel? optimizationLevel)
        {
            Platform platform = Platform.Architecture == Architecture.X86
                ? Microsoft.CodeAnalysis.Platform.AnyCpu32BitPreferred
                : Microsoft.CodeAnalysis.Platform.AnyCpu;

            return new ScriptRunner(code: null,
                                    syntaxTrees: ImmutableList.Create(_initHostSyntax, ParseCode(code)),
                                    _roslynHost.ParseOptions as CSharpParseOptions,
                                    OutputKind.ConsoleApplication,
                                    platform,
                                    _scriptOptions.MetadataReferences,
                                    _scriptOptions.Imports,
                                    _scriptOptions.FilePath,
                                    _parameters.WorkingDirectory,
                                    _scriptOptions.MetadataResolver,
                                    optimizationLevel: optimizationLevel ?? _parameters.OptimizationLevel,
                                    checkOverflow: _parameters.CheckOverflow,
                                    allowUnsafe: _parameters.AllowUnsafe);
        }

        private async Task RunProcess(string assemblyPath, CancellationToken cancellationToken)
        {
            using (var process = new Process
            {
                StartInfo = GetProcessStartInfo(assemblyPath)
            })
            using (cancellationToken.Register(() =>
            {
                try
                {
                    _processInputStream = null;
                    process.Kill();
                }
                catch { }
            }))
            {
                if (process.Start())
                {
                    _processInputStream = new StreamWriter(process.StandardInput.BaseStream, Encoding.UTF8);

                    await Task.WhenAll(
                        Task.Run(() => ReadObjectProcessStream(process.StandardOutput)),
                        Task.Run(() => ReadProcessStream(process.StandardError)));
                }
            }

            ProcessStartInfo GetProcessStartInfo(string assemblyPath)
            {
                return new ProcessStartInfo
                {
                    FileName = Platform.IsCore ? Platform.HostPath : assemblyPath,
                    Arguments = $"\"{assemblyPath}\" --pid {CurrentPid.Value}",
                    WorkingDirectory = _parameters.WorkingDirectory,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
            }
        }

        public async Task SendInputAsync(string message)
        {
            var stream = _processInputStream;
            if (stream != null)
            {
                await stream.WriteLineAsync(message).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
        }

        private async Task ReadProcessStream(StreamReader reader)
        {
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line != null)
                {
                    Dumped?.Invoke(ResultObject.Create(line, DumpQuotas.Default));
                }
            }
        }

        private void ReadObjectProcessStream(StreamReader reader)
        {
            using var jsonReader = new JsonTextReader(reader) { SupportMultipleContent = true };
            while (jsonReader.Read())
            {
                try
                {
                    var result = _jsonSerializer.Deserialize<ResultObject>(jsonReader);

                    switch (result)
                    {
                        case ExceptionResultObject exceptionResult:
                            Error?.Invoke(exceptionResult);
                            break;
                        case InputReadRequest _:
                            ReadInput?.Invoke();
                            break;
                        default:
                            Dumped?.Invoke(result);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Dumped?.Invoke(ResultObject.Create("Error deserializing result: " + ex.Message, DumpQuotas.Default));
                }
            }
        }

        private SyntaxTree ParseCode(string code)
        {
            var tree = ParseSyntaxTree(code, _roslynHost.ParseOptions);
            var root = tree.GetRoot();

            if (root is CompilationUnitSyntax c)
            {
                var members = c.Members;

                // add .Dump() to the last bare expression
                var lastMissingSemicolon = c.Members.OfType<GlobalStatementSyntax>()
                    .LastOrDefault(m => m.Statement is ExpressionStatementSyntax expr && expr.SemicolonToken.IsMissing);
                if (lastMissingSemicolon != null)
                {
                    var statement = (ExpressionStatementSyntax)lastMissingSemicolon.Statement;

                    members = members.Replace(lastMissingSemicolon,
                        GlobalStatement(
                            ExpressionStatement(
                            InvocationExpression(
                                MemberAccessExpression(
                                    SyntaxKind.SimpleMemberAccessExpression,
                                    statement.Expression,
                                    IdentifierName(nameof(ObjectExtensions.Dump)))))));
                }

                root = c.WithMembers(members);
            }

            return tree.WithRootAndOptions(root, _roslynHost.ParseOptions);
        }

        private void SendDiagnostics(ImmutableArray<Diagnostic> diagnostics)
        {
            if (diagnostics.Length > 0)
            {
                CompilationErrors?.Invoke(diagnostics.Where(d => !_parameters.DisabledDiagnostics.Contains(d.Id))
                    .Select(d => GetCompilationErrorResultObject(d)).ToImmutableArray());
            }
        }

        private static CompilationErrorResultObject GetCompilationErrorResultObject(Diagnostic diagnostic)
        {
            var lineSpan = diagnostic.Location.GetLineSpan();

            var result = CompilationErrorResultObject.Create(diagnostic.Severity.ToString(),
                    diagnostic.Id, diagnostic.GetMessage(),
                    lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character);
            return result;
        }

        public Task TerminateAsync()
        {
            StopProcess();
            return Task.CompletedTask;
        }

        private void StopProcess()
        {
            _executeCts?.Cancel();
        }

        public void UpdateLibraries(IList<LibraryRef> libraries)
        {
            lock (_libraries)
            {
                if (!_libraries.SetEquals(libraries))
                {
                    _libraries.Clear();
                    _libraries.UnionWith(libraries);

                    TryRestore();
                }
            }
        }

        private Task RestoreTask => _restoreTask ?? Task.CompletedTask;

        public void TryRestore()
        {
            if (!HasPlatform || string.IsNullOrEmpty(Name))
            {
                return;
            }

            if (_restoreCts != null)
            {
                _restoreCts.Cancel();
                _restoreCts.Dispose();
            }

            RestoreStarted?.Invoke();

            var restoreCts = new CancellationTokenSource();
            _restoreTask = RestoreAsync(RestoreTask, restoreCts.Token);
            _restoreCts = restoreCts;

            async Task RestoreAsync(Task previousTask, CancellationToken cancellationToken)
            {
                try
                {
                    await previousTask.ConfigureAwait(false);
                }
                catch { }

                try
                {
                    await BuildGlobalJson().ConfigureAwait(false);
                    var csprojPath = await BuildCsproj().ConfigureAwait(false);

                    var errorsPath = Path.Combine(BuildPath, "errors.log");
                    File.Delete(errorsPath);

                    cancellationToken.ThrowIfCancellationRequested();

                    var result = await ProcessUtil.RunProcess("dotnet", $"build -nologo -flp:errorsonly;logfile={errorsPath} {csprojPath}", cancellationToken).ConfigureAwait(false);

                    if (result.ExitCode != 0)
                    {
                        var error = await GetErrorAsync(errorsPath, result, cancellationToken);
                        RestoreCompleted?.Invoke(RestoreResult.FromError(error));
                        return;
                    }

                    var referencesPath = Path.Combine(BuildPath, "references.txt");
                    var references = await File.ReadAllLinesAsync(referencesPath, cancellationToken);

                    cancellationToken.ThrowIfCancellationRequested();

                    MetadataReferences = references
                        .Where(r => !string.IsNullOrWhiteSpace(r))
                        .Select(r => _roslynHost.CreateMetadataReference(r)).ToImmutableArray();

                    _scriptOptions = _scriptOptions.WithReferences(MetadataReferences);

                    RestoreCompleted?.Invoke(RestoreResult.SuccessResult);
                }
                catch (Exception ex)
                {
                    RestoreCompleted?.Invoke(RestoreResult.FromError(ex.Message));
                }
            }

            async Task BuildGlobalJson()
            {
                if (Platform?.IsCore == true && PlatformVersion != null)
                {
                    var global = new JObject(
                        new JProperty("sdk", new JObject(
                            new JProperty("version", PlatformVersion.FrameworkVersion))));

                    await File.WriteAllTextAsync(Path.Combine(BuildPath, "global.json"), global.ToString());
                }
            }

            async Task<string> BuildCsproj()
            {
                var targetFrameworkMoniker = PlatformVersion?.TargetFrameworkMoniker ?? Platform.TargetFrameworkMoniker;

                var csproj = MSBuildHelper.CreateCsproj(
                    targetFrameworkMoniker,
                    _libraries);
                var csprojPath = Path.Combine(BuildPath, $"rp-{Name}.csproj");

                await Task.Run(() => csproj.Save(csprojPath)).ConfigureAwait(false);
                return csprojPath;
            }

            static async Task<string> GetErrorAsync(string errorsPath, ProcessUtil.ProcessResult result, CancellationToken cancellationToken)
            {
                string error;
                try
                {
                    error = await File.ReadAllTextAsync(errorsPath, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(error))
                    {
                        error = ErrorFromResult(result);
                    }
                }
                catch (FileNotFoundException)
                {
                    error = ErrorFromResult(result);
                }

                return error;
            }

            static string ErrorFromResult(ProcessUtil.ProcessResult result)
            {
                return string.Join(Environment.NewLine, result.StandardOutput, result.StandardError);
            }
        }
    }
}