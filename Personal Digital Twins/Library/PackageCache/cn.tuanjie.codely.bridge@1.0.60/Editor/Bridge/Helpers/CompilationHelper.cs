using UnityEditor;
using UnityEngine;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Helper class for Unity compilation status checking and error tracking
    /// </summary>
    public static class CompilationHelper
    {
        // Track last known compilation error/warning counts
        // IMPORTANT: Keep these nullable. Returning 0 when counts are unknown is misleading
        // (it can be interpreted as "validated: no errors/warnings").
        private static int? _lastErrorCount = null;
        private static int? _lastWarningCount = null;

        /// <summary>
        /// Helper to check compilation status across Unity versions
        /// </summary>
        public static bool IsCompiling()
        {
            if (EditorApplication.isCompiling)
            {
                return true;
            }
            try
            {
                System.Type pipeline = System.Type.GetType("UnityEditor.Compilation.CompilationPipeline, UnityEditor");
                var prop = pipeline?.GetProperty("isCompiling", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (prop != null)
                {
                    return (bool)prop.GetValue(null);
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Gets the count of compilation errors from the console.
        /// This is an approximation based on console log entries.
        /// </summary>
        public static int? GetCompilationErrors()
        {
            try
            {
                // Try to get error count from LogEntries (internal API)
                var logEntriesType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries");
                if (logEntriesType != null)
                {
                    var getCountMethod = logEntriesType.GetMethod(
                        "GetCount",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    );
                    
                    // Get count with error filter (mode = 1 for errors)
                    var getCountByTypeMethod = logEntriesType.GetMethod(
                        "GetCountsByType",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    );

                    if (getCountByTypeMethod != null)
                    {
                        // GetCountsByType returns counts for errors, warnings, logs
                        var counts = new int[3];
                        getCountByTypeMethod.Invoke(null, new object[] { counts });
                        _lastErrorCount = counts[0]; // Errors
                        return _lastErrorCount;
                    }
                }
            }
            catch (System.Exception e)
            {
                CodelyLogger.LogWarning($"[CompilationHelper] Failed to get error count: {e.Message}");
            }

            return _lastErrorCount;
        }

        /// <summary>
        /// Gets the count of compilation warnings from the console.
        /// This is an approximation based on console log entries.
        /// </summary>
        public static int? GetCompilationWarnings()
        {
            try
            {
                // Try to get warning count from LogEntries (internal API)
                var logEntriesType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries");
                if (logEntriesType != null)
                {
                    var getCountByTypeMethod = logEntriesType.GetMethod(
                        "GetCountsByType",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    );

                    if (getCountByTypeMethod != null)
                    {
                        // GetCountsByType returns counts for errors, warnings, logs
                        var counts = new int[3];
                        getCountByTypeMethod.Invoke(null, new object[] { counts });
                        _lastWarningCount = counts[1]; // Warnings
                        return _lastWarningCount;
                    }
                }
            }
            catch (System.Exception e)
            {
                CodelyLogger.LogWarning($"[CompilationHelper] Failed to get warning count: {e.Message}");
            }

            return _lastWarningCount;
        }

        /// <summary>
        /// Resets tracked error/warning counts.
        /// Should be called before starting a new compilation.
        /// </summary>
        public static void ResetCounts()
        {
            _lastErrorCount = null;
            _lastWarningCount = null;
        }
        
        /// <summary>
        /// Starts a standard compilation pipeline:
        /// 1. Clears console and gets since_token
        /// 2. Requests compilation
        /// 3. Returns pending response with token for later log reading
        /// 
        /// This is the recommended pattern after any script modification.
        /// </summary>
        public static object StartCompilationPipeline()
        {
            try
            {
                // Step 1: Clear console and get since_token
                var clearMethod = typeof(UnityTcp.Editor.Tools.ReadConsole).GetMethod(
                    "HandleCommand",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                );
                
                string sinceToken = null;
                if (clearMethod != null)
                {
                    var clearParams = new Codely.Newtonsoft.Json.Linq.JObject
                    {
                        ["action"] = "clear"
                    };
                    var clearResult = clearMethod.Invoke(null, new object[] { clearParams });
                    
                    // Extract since_token from result
                    if (clearResult != null)
                    {
                        var resultType = clearResult.GetType();
                        var dataProp = resultType.GetProperty("data");
                        if (dataProp != null)
                        {
                            var data = dataProp.GetValue(clearResult);
                            if (data != null)
                            {
                                var tokenProp = data.GetType().GetProperty("sinceToken");
                                sinceToken = tokenProp?.GetValue(data)?.ToString();
                            }
                        }
                    }
                }
                
                // Fallback: get token from StateComposer
                if (string.IsNullOrEmpty(sinceToken))
                {
                    sinceToken = StateComposer.GetCurrentConsoleToken();
                }
                
                // Step 2: Reset error counts
                ResetCounts();
                
                // Step 3: Create compilation job
                var job = AsyncOperationTracker.CreateJob(
                    AsyncOperationTracker.JobType.Compilation,
                    "Script compilation pipeline started"
                );
                
                // Step 4: Request compilation
                UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
                
                // Step 5: Return pending response with token and structured pipeline hints
                var response = AsyncOperationTracker.CreatePendingResponse(job) as System.Collections.Generic.Dictionary<string, object>;
                if (response != null)
                {
                    response["since_token"] = sinceToken;
                    response["pipeline"] = new
                    {
                        step = "compiling",
                        sinceToken = sinceToken
                    };
                    response["pipeline_kind"] = "compile";
                    response["requires_console_validation"] = true;
                }
                
                return response ?? AsyncOperationTracker.CreatePendingResponse(job);
            }
            catch (System.Exception e)
            {
                CodelyLogger.LogError($"[CompilationHelper] StartCompilationPipeline failed: {e}");
                return Response.Error($"Failed to start compilation pipeline: {e.Message}");
            }
        }
        
        /// <summary>
        /// Gets a summary of the last compilation result.
        /// </summary>
        public static object GetCompilationSummary()
        {
            var errors = GetCompilationErrors();
            var warnings = GetCompilationWarnings();

            // Only include fields that are actually known; returning 0 is misleading.
            var result = new System.Collections.Generic.Dictionary<string, object>
            {
                ["isCompiling"] = IsCompiling()
            };

            if (errors.HasValue) result["errors"] = errors.Value;
            if (warnings.HasValue) result["warnings"] = warnings.Value;
            if (errors.HasValue) result["success"] = errors.Value == 0;

            return result;
        }
    }
}
