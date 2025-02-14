﻿using ExampleExtractor;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Newtonsoft.Json;
using System.Reflection;
using System.Text;
using Utilities;

namespace ExampleTester;

internal class GeneratedExample
{
    static GeneratedExample()
    {
        MSBuildLocator.RegisterDefaults();
    }

    private readonly string directory;
    internal ExampleMetadata Metadata { get; }

    private GeneratedExample(string directory)
    {
        this.directory = directory;
        string metadataJson = File.ReadAllText(Path.Combine(directory, ExampleMetadata.MetadataFile));
        Metadata = JsonConvert.DeserializeObject<ExampleMetadata>(metadataJson) ?? throw new ArgumentException($"Invalid (null) metadata in {directory}");
    }

    internal static List<GeneratedExample> LoadAllExamples(string parentDirectory) =>
        Directory.GetDirectories(parentDirectory).Select(Load).ToList();

    private static GeneratedExample Load(string directory)
    {
        return new GeneratedExample(directory);
    }

    internal async Task<bool> Test(TesterConfiguration configuration, StatusCheckLogger logger)
    {
        logger.ConsoleOnlyLog(Metadata.Source, Metadata.StartLine, Metadata.EndLine, $"Testing {Metadata.Name} from {Metadata.Source}", "ExampleTester");

        // Explicitly do a release build, to avoid implicitly defining DEBUG.
        var properties = new Dictionary<string, string> { { "Configuration", "Release" } };
        using var workspace = MSBuildWorkspace.Create(properties);
        // TODO: Validate this more cleanly.
        var projectFile = Metadata.Project is string specifiedProject
            ? Path.Combine(directory, $"{specifiedProject}.csproj")
            : Directory.GetFiles(directory, "*.csproj").Single();
        var project = await workspace.OpenProjectAsync(projectFile);
        var compilation = await project.GetCompilationAsync();
        if (compilation is null)
        {
            throw new InvalidOperationException("Project has no Compilation");
        }

        bool ret = true;
        ret &= ValidateDiagnostics("errors", DiagnosticSeverity.Error, Metadata.ExpectedErrors, logger);
        ret &= ValidateDiagnostics("warnings", DiagnosticSeverity.Warning, Metadata.ExpectedWarnings, logger, Metadata.IgnoredWarnings);
        // Don't try to validate output if we've already failed in terms of errors and warnings, or if we expect errors.
        if (ret && Metadata.ExpectedErrors is null)
        {
            ret &= ValidateOutput();
        }
        return ret;

        bool ValidateDiagnostics(string type, DiagnosticSeverity severity, List<string> expected, StatusCheckLogger logger, List<string>? ignored = null)
        {
            expected ??= new List<string>();
            ignored ??= new List<string>();
            var actualDiagnostics = compilation.GetDiagnostics()
                .Where(d => d.Severity == severity)
                .OrderBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
                .ThenBy(d => d.Id);
            var actualIds = actualDiagnostics
                .Select(d => d.Id)
                .Where(id => !ignored.Contains(id))
                .ToList();
            bool ret = ValidateExpectedAgainstActual(type, expected, actualIds);
            if (!ret)
            {
                logger.LogFailure(Metadata.Source, Metadata.StartLine, Metadata.EndLine, $"  Details of actual {type}:", "ExampleTester");
                foreach (var diagnostic in actualDiagnostics)
                {
                    logger.LogFailure(Metadata.Source, Metadata.StartLine, Metadata.EndLine,
                        $"    Line {diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1}: {diagnostic.Id}: {diagnostic.GetMessage()}",
                        "ExampleTester");
                }
            }
            return ret;
        }

        bool ValidateOutput()
        {
            var entryPoint = compilation.GetEntryPoint(cancellationToken: default);
            if (entryPoint is null)
            {
                if (Metadata.ExpectedOutput != null)
                {
                    logger.LogFailure(Metadata.Source, Metadata.StartLine, Metadata.EndLine, "  Output expected, but project has no entry point.", "ExampleTester");
                    return false;
                }
                return true;
            }

            string typeName = entryPoint.ContainingType.MetadataName;
            if (entryPoint.ContainingNamespace?.MetadataName is string ns)
            {
                typeName = $"{ns}.{typeName}";
            }
            string methodName = entryPoint.MetadataName;

            var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms);
            if (!emitResult.Success)
            {
                logger.LogFailure(Metadata.Source, Metadata.StartLine, Metadata.EndLine, "  Failed to emit assembly", "ExampleTester");
                return false;
            }

            var generatedAssembly = Assembly.Load(ms.ToArray());
            var type = generatedAssembly.GetType(typeName);
            if (type is null)
            {
                logger.LogFailure(Metadata.Source, Metadata.StartLine, Metadata.EndLine, $"  Failed to find entry point type {typeName}", "ExampleTester");
                return false;
            }
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method is null)
            {
                logger.LogFailure(Metadata.Source, Metadata.StartLine, Metadata.EndLine, $"  Failed to find entry point method {typeName}.{methodName}", "ExampleTester");
                return false;
            }
            var arguments = method.GetParameters().Any()
                ? new object[] { Metadata.ExecutionArgs ?? new string[0] }
                : new object[0];

            var oldOut = Console.Out;
            List<string> actualLines;
            Exception? actualException = null;
            try
            {
                var builder = new StringBuilder();
                Console.SetOut(new StringWriter(builder));
                try
                {
                    var result = method.Invoke(null, arguments);
                    // For async Main methods, the compilation's entry point is still the Main
                    // method, so we explicitly wait for the returned task just like the synthesized
                    // entry point would.
                    if (result is Task task)
                    {
                        task.GetAwaiter().GetResult();
                    }
                    // For some reason, we don't *actually* get the result of all finalizers
                    // without this. We shouldn't need it (as relevant examples already have it) but
                    // code that works outside the test harness doesn't work inside it.
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                catch (TargetInvocationException outer)
                {
                    actualException = outer.InnerException ?? throw new InvalidOperationException("TargetInvocationException had no nested exception");
                }
                // Skip blank lines, to avoid unnecessary trailing empties.
                // Also trim the end of each actual line, to avoid trailing spaces being necessary in the metadata
                // or listed console output.
                actualLines = builder.ToString()
                    .Replace("\r\n", "\n")
                    .Split('\n')
                    .Select(line => line.TrimEnd())
                    .Where(line => line != "").ToList();
            }
            finally
            {
                Console.SetOut(oldOut);
            }
            var expectedLines = Metadata.ExpectedOutput ?? new List<string>();
            return ValidateException(actualException, Metadata.ExpectedException) &&
                (Metadata.IgnoreOutput || ValidateExpectedAgainstActual("output", expectedLines, actualLines));
        }

        bool ValidateException(Exception? actualException, string? expectedExceptionName)
        {
            return (actualException, expectedExceptionName) switch
            {
                (null, null) => true,
                (Exception ex, string name) =>
                    MaybeReportError(ex.GetType().Name == name, $"  Mismatched exception type: Expected {name}; Was {ex.GetType().Name}"),
                (null, string name) =>
                    MaybeReportError(false, $"  Expected exception type {name}; no exception was thrown"),
                (Exception ex, null) =>
                    MaybeReportError(false, $"  Exception type {ex.GetType().Name} was thrown unexpectedly; Message: {ex.Message}")
            };

            bool MaybeReportError(bool result, string message)
            {
                if (!result)
                {
                    logger.LogFailure(Metadata.Source, Metadata.StartLine, Metadata.EndLine, message, "ExampleTester");
                }
                return result;
            }
        }

        bool ValidateExpectedAgainstActual(string type, List<string> expected, List<string> actual)
        {
            if (!expected.SequenceEqual(actual))
            {
                logger.LogFailure(Metadata.Source, Metadata.StartLine, Metadata.EndLine,
                    $"  Mismatched {type}: Expected {string.Join(", ", expected)}; Was {string.Join(", ", actual)}", "ExampleTester");
                return false;
            }
            return true;
        }
    }
}
