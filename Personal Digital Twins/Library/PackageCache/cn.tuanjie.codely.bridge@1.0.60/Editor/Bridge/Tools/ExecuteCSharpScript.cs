using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Codely.Newtonsoft.Json.Linq;
using Codely.Microsoft.CodeAnalysis;
using Codely.Microsoft.CodeAnalysis.CSharp;
using Codely.Microsoft.CodeAnalysis.CSharp.Scripting;
using Codely.Microsoft.CodeAnalysis.Scripting;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Tools
{
    public static class ExecuteCSharpScript
    {
        static readonly List<string> s_CapturedLogs = new List<string>();
        static bool s_IsCapturingLogs;

        static readonly string s_ShadowCopyDir = Path.Combine(
            Application.temporaryCachePath,
            "CodelyScriptRefs"
        );

        static readonly string[] s_ShadowCopyAssemblyNames =
        {
            "Assembly-CSharp",
            "Assembly-CSharp-Editor"
        };

        static readonly List<ScriptFixProvider> s_FixProviders = new List<ScriptFixProvider>
        {
            new FixMissingImports(),
            new FixMissingAssemblyReference(),
            new FixUnqualifiedUnityStaticMethod(),
            new FixMissingParenthesis(),
            new FixMissingBrace(),
            new FixMissingSquareBracket(),
            new FixMissingSemicolon(),
            new FixAmbiguousReference()
        };

        const int k_MaxFixIterations = 50;

        public static object HandleCommand(JObject @params)
        {
            string script = @params["script"]?.ToString();
            string scriptPath = @params["script_path"]?.ToString();
            string description = @params["description"]?.ToString();

            // At least one of script or script_path must be provided
            if (string.IsNullOrEmpty(script) && string.IsNullOrEmpty(scriptPath))
                return Response.Error("'script' parameter is required.");

            if (!string.IsNullOrEmpty(description))
                CodelyLogger.Log($"[ExecuteCSharpScript] Description: {description}");

            // If script_path is provided (legacy support), read the file content
            if (!string.IsNullOrEmpty(scriptPath))
            {
                try
                {
                    if (!File.Exists(scriptPath))
                        return Response.Error($"Script file not found: {scriptPath}");

                    script = File.ReadAllText(scriptPath);
                    CodelyLogger.Log($"[ExecuteCSharpScript] Loaded script from file: {scriptPath} ({script.Length} chars)");

                    if (string.IsNullOrWhiteSpace(script))
                        return Response.Error($"Script file is empty: {scriptPath}");
                }
                catch (IOException ioEx)
                {
                    return Response.Error($"Failed to read script file: {ioEx.Message}");
                }
                catch (UnauthorizedAccessException uaEx)
                {
                    return Response.Error($"Access denied to script file: {uaEx.Message}");
                }
            }
            // Auto-detect if script parameter is a file path
            // Heuristic: single line, ends with .cs, and file exists
            else if (!string.IsNullOrEmpty(script))
            {
                var trimmedScript = script.Trim();
                bool looksLikePath = !trimmedScript.Contains("\n") &&
                                     trimmedScript.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

                if (looksLikePath && File.Exists(trimmedScript))
                {
                    try
                    {
                        script = File.ReadAllText(trimmedScript);
                        CodelyLogger.Log($"[ExecuteCSharpScript] Auto-detected and loaded script from path: {trimmedScript} ({script.Length} chars)");

                        if (string.IsNullOrWhiteSpace(script))
                            return Response.Error($"Script file is empty: {trimmedScript}");
                    }
                    catch (IOException ioEx)
                    {
                        return Response.Error($"Failed to read script file: {ioEx.Message}");
                    }
                    catch (UnauthorizedAccessException uaEx)
                    {
                        return Response.Error($"Access denied to script file: {uaEx.Message}");
                    }
                }
            }

            bool captureLogs = @params["capture_logs"]?.ToObject<bool>() ?? true;
            string[] imports = @params["imports"]?.ToObject<string[]>() ?? new[]
            {
                "System",
                "System.Linq",
                "System.Collections.Generic",
                "UnityEngine",
                "UnityEditor",
                "UnityEditor.SceneManagement",
                "UnityEngine.SceneManagement"
            };

            try
            {
                CodelyLogger.Log($"[ExecuteCSharpScript] Executing script ({script.Length} chars, {imports.Length} imports)");
                StartLogCapture(captureLogs);

                object result;
                try
                {
                    result = ExecuteScriptInternal(script, imports);
                }
                finally
                {
                    // Always stop log capture, even on error
                }

                var logs = captureLogs ? StopLogCapture() : new List<string>();
                var response = Response.Success(
                    "C# script executed successfully.",
                    new { result = result?.ToString(), logs, log_count = logs.Count }
                );

                CodelyLogger.Log($"[ExecuteCSharpScript] Response: {Codely.Newtonsoft.Json.JsonConvert.SerializeObject(response)}");
                return response;
            }
            catch (Exception e)
            {
                var logs = captureLogs ? StopLogCapture() : new List<string>();
                var errorResponse = Response.Error(
                    $"C# script execution failed: {e.Message}",
                    new { logs, exception = e.ToString() }
                );
                CodelyLogger.Log($"[ExecuteCSharpScript] Error Response: {Codely.Newtonsoft.Json.JsonConvert.SerializeObject(errorResponse)}");
                return errorResponse;
            }
        }

        static object ExecuteScriptInternal(string script, string[] imports)
        {
            SaveScriptToTemp(script);

            try
            {
                // Build minimal base references — no pre-loaded optional modules
                var references = BuildBaseReferences();
                var fixedImports = new List<string>(imports);
                var fixedScript = script;

                // Compile and auto-fix before execution
                CompileAndAutoFix(ref fixedScript, fixedImports, references);

                var options = ScriptOptions.Default
                    .WithReferences(references)
                    .WithImports(fixedImports);

                var scriptTask = CSharpScript.EvaluateAsync(fixedScript, options);
                scriptTask.Wait();
                return scriptTask.Result;
            }
            catch (AggregateException ae)
            {
                if (ae.InnerException != null)
                    throw ae.InnerException;
                throw;
            }
        }

        static List<MetadataReference> BuildBaseReferences()
        {
            var references = new List<MetadataReference>();
            AddCoreAssemblyReferences(references);

            // Unity essentials only — optional modules are added on demand by CompileAndAutoFix
            references.Add(MetadataReference.CreateFromFile(typeof(UnityEngine.Debug).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(typeof(UnityEditor.EditorApplication).Assembly.Location));

            var addedLocations = new HashSet<string>(references
                .OfType<PortableExecutableReference>()
                .Select(r => r.FilePath ?? ""));

            foreach (var assemblyName in s_ShadowCopyAssemblyNames)
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == assemblyName);
                if (asm == null || string.IsNullOrEmpty(asm.Location))
                    continue;

                var shadowPath = CreateShadowCopy(asm.Location);
                if (addedLocations.Add(shadowPath))
                    references.Add(MetadataReference.CreateFromFile(shadowPath));
            }

            return references;
        }

        // Iteratively compiles the script using the same scripting engine as execution,
        // then applies auto-fixes until clean or exhausted.
        static void CompileAndAutoFix(ref string script, List<string> imports, List<MetadataReference> references)
        {
            HoistUsingDirectives(ref script, imports);

            var addedLocations = new HashSet<string>(references
                .OfType<PortableExecutableReference>()
                .Select(r => r.FilePath ?? ""));

            var context = new ScriptFixContext(imports, references, addedLocations);

            for (int iteration = 0; iteration < k_MaxFixIterations; iteration++)
            {
                var scriptOptions = ScriptOptions.Default
                    .WithReferences(references)
                    .WithImports(imports);
                var scriptObj = CSharpScript.Create(script, scriptOptions);
                var compilation = scriptObj.GetCompilation();
                var tree = compilation.SyntaxTrees.First();

                var errors = compilation.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                if (!errors.Any())
                {
                    CodelyLogger.Log($"[ExecuteCSharpScript] Compilation check passed (iteration {iteration})");
                    return;
                }

                int errorCountBefore = errors.Count;
                bool anyFixed = false;
                var updatedTree = tree;

                foreach (var diagnostic in errors)
                {
                    foreach (var fix in s_FixProviders)
                    {
                        if (!fix.CanFix(diagnostic))
                            continue;

                        var treeBeforeFix = updatedTree;
                        if (fix.ApplyFix(ref updatedTree, diagnostic, context))
                        {
                            anyFixed = true;
                            CodelyLogger.Log($"[ExecuteCSharpScript] {fix.GetType().Name} applied for {diagnostic.Id}");

                            // If the tree was modified, remaining diagnostic spans are stale.
                            // Break out and let the outer loop recompile with fresh diagnostics.
                            if (!ReferenceEquals(updatedTree, treeBeforeFix))
                                goto fixesApplied;
                        }
                    }
                }

                fixesApplied:
                if (!anyFixed)
                {
                    CodelyLogger.LogWarning("[ExecuteCSharpScript] Auto-fix could not resolve remaining errors:\n" +
                        string.Join("\n", errors.Select(e => $"  {e.Id}: {e.GetMessage()}")));
                    return;
                }

                if (!ReferenceEquals(updatedTree, tree))
                {
                    var candidate = updatedTree.GetText().ToString();

                    // Verify the fix reduced errors; if it made things worse, skip this fix
                    var checkOptions = ScriptOptions.Default
                        .WithReferences(references)
                        .WithImports(imports);
                    var checkErrors = CSharpScript.Create(candidate, checkOptions)
                        .GetCompilation().GetDiagnostics()
                        .Count(d => d.Severity == DiagnosticSeverity.Error);

                    if (checkErrors > errorCountBefore)
                    {
                        CodelyLogger.LogWarning($"[ExecuteCSharpScript] Auto-fix increased errors ({errorCountBefore} → {checkErrors}), reverting");
                        continue;
                    }

                    script = candidate;
                }
            }
        }

        // Parses top-level `using` directives out of the script, merges them into `imports`,
        // and returns the script body with those directives removed.
        static void HoistUsingDirectives(ref string script, List<string> imports)
        {
            var root = SyntaxFactory.ParseSyntaxTree(script).GetCompilationUnitRoot();
            if (root.Usings.Count == 0)
                return;

            foreach (var usingDirective in root.Usings)
            {
                var namespaceName = usingDirective.Name.ToString();
                if (!imports.Contains(namespaceName))
                    imports.Add(namespaceName);
            }

            // Remove the using directives from the script body
            var stripped = root.RemoveNodes(root.Usings, SyntaxRemoveOptions.KeepNoTrivia);
            script = stripped?.GetText().ToString().TrimStart() ?? script;
        }

        static void AddCoreAssemblyReferences(List<MetadataReference> references)
        {
            var coreTypes = new[]
            {
                typeof(object),
                typeof(System.Linq.Enumerable),
                typeof(System.Collections.Generic.List<>),
                typeof(System.Collections.ArrayList),
                typeof(System.Threading.Tasks.Task),
                typeof(System.Text.StringBuilder),
                typeof(System.IO.File),
                typeof(System.Text.RegularExpressions.Regex),
                typeof(System.Math),
            };

            var addedLocations = new HashSet<string>();
            foreach (var type in coreTypes)
            {
                var location = type.Assembly.Location;
                if (!string.IsNullOrEmpty(location) && addedLocations.Add(location))
                    references.Add(MetadataReference.CreateFromFile(location));
            }

            foreach (var name in new[] { "netstandard", "System.Runtime", "System.Core" })
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == name);
                if (asm != null && !string.IsNullOrEmpty(asm.Location) && addedLocations.Add(asm.Location))
                    references.Add(MetadataReference.CreateFromFile(asm.Location));
            }
        }

        static string CreateShadowCopy(string sourcePath)
        {
            var sourceTime = File.GetLastWriteTimeUtc(sourcePath).Ticks;
            var fileName = Path.GetFileNameWithoutExtension(sourcePath);
            var ext = Path.GetExtension(sourcePath);
            var versionedName = $"{fileName}_{sourceTime}{ext}";
            var destPath = Path.Combine(s_ShadowCopyDir, versionedName);

            Directory.CreateDirectory(s_ShadowCopyDir);

            if (File.Exists(destPath))
            {
                CodelyLogger.Log($"[ExecuteCSharpScript] Shadow copy exists: {versionedName}");
            }
            else
            {
                CleanupOldShadowCopies(fileName, sourceTime);
                File.Copy(sourcePath, destPath, overwrite: false);
                CodelyLogger.Log($"[ExecuteCSharpScript] Shadow copy created: {versionedName}");
            }

            var pdbSource = Path.ChangeExtension(sourcePath, ".pdb");
            var pdbDest = Path.ChangeExtension(destPath, ".pdb");
            if (File.Exists(pdbSource) && !File.Exists(pdbDest))
            {
                try { File.Copy(pdbSource, pdbDest, overwrite: false); }
                catch (IOException)
                {
                    CodelyLogger.LogWarning($"[ExecuteCSharpScript] Could not copy PDB for {fileName}");
                }
            }

            return destPath;
        }

        static void CleanupOldShadowCopies(string assemblyName, long currentTimestamp)
        {
            try
            {
                if (!Directory.Exists(s_ShadowCopyDir))
                    return;

                foreach (var file in Directory.GetFiles(s_ShadowCopyDir, $"{assemblyName}_*"))
                {
                    var nameNoExt = Path.GetFileNameWithoutExtension(file);
                    var lastUnderscore = nameNoExt.LastIndexOf('_');
                    if (lastUnderscore <= 0)
                        continue;

                    if (long.TryParse(nameNoExt.Substring(lastUnderscore + 1), out var fileTimestamp)
                        && fileTimestamp < currentTimestamp)
                    {
                        try { File.Delete(file); }
                        catch (IOException)
                        {
                            CodelyLogger.LogWarning($"[ExecuteCSharpScript] Could not delete old shadow copy: {Path.GetFileName(file)}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                CodelyLogger.LogWarning($"[ExecuteCSharpScript] Shadow copy cleanup failed: {e.Message}");
            }
        }

        static void SaveScriptToTemp(string script)
        {
            try
            {
                var timestamp = DateTime.Now.ToString("HHmmss");
                var tempPath = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "ExecutedCSharpScripts");
                Directory.CreateDirectory(tempPath);
                var filePath = Path.Combine(tempPath, $"script_{timestamp}_{script.Length}.cs");
                File.WriteAllText(filePath, script);
                CodelyLogger.Log($"[ExecuteCSharpScript] Script saved: {filePath}");
            }
            catch (Exception e)
            {
                CodelyLogger.LogWarning($"[ExecuteCSharpScript] Failed to save script to temp: {e.Message}");
            }
        }

        static void StartLogCapture(bool enabled)
        {
            if (!enabled)
            {
                s_IsCapturingLogs = false;
                return;
            }
            s_CapturedLogs.Clear();
            s_IsCapturingLogs = true;
            Application.logMessageReceived += OnLogMessageReceived;
        }

        static List<string> StopLogCapture()
        {
            Application.logMessageReceived -= OnLogMessageReceived;
            s_IsCapturingLogs = false;
            var logs = new List<string>(s_CapturedLogs);
            s_CapturedLogs.Clear();
            return logs;
        }

        static void OnLogMessageReceived(string logString, string stackTrace, LogType type)
        {
            if (!s_IsCapturingLogs)
                return;

            // Suppress this tool's own internal trace logs from the captured output —
            // the caller wants their script's logs, not our scaffolding.
            if (!string.IsNullOrEmpty(logString) && logString.StartsWith("[ExecuteCSharpScript]"))
                return;

            var entry = new StringBuilder();
            entry.Append($"[{type}] {logString}");
            if ((type == LogType.Error || type == LogType.Exception) && !string.IsNullOrEmpty(stackTrace))
                entry.Append($"\n{stackTrace}");

            s_CapturedLogs.Add(entry.ToString());
        }

    }
}
