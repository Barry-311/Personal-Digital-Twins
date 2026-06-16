using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityTcp.Editor.Helpers
{
    /// <summary>
    /// Centralized state composition and internal publish tracking for Unity Editor state.
    /// Provides consistent state snapshots and incremental state_delta generation.
    /// </summary>
    public static class StateComposer
    {
        // Internal published-state revision counter.
        private static int _globalRevision = 0;
        private static readonly object _revisionLock = new object();
        private static bool _dirtyPending = false;
        private static int _lastObservationInvalidatedNotificationRev = -1;
        
        // Console state tracking (shared with ReadConsole)
        private static string _currentConsoleToken = null;
        private static int _consoleUnreadCount = 0;
        private static readonly List<object> _lastConsoleErrors = new List<object>();
        private static readonly HashSet<string> _invalidatedConsoleTokens = new HashSet<string>();
        private static readonly object _consoleLock = new object();
        
        // Touched assets tracking
        private static readonly List<object> _touchedAssets = new List<object>();
        private static readonly object _assetsLock = new object();
        
        // Pending operations tracking
        private static readonly List<object> _pendingOperations = new List<object>();
        private static readonly object _operationsLock = new object();

        /// <summary>
        /// Increment and return the next global revision number.
        /// Thread-safe.
        /// </summary>
        public static int IncrementRevision()
        {
            lock (_revisionLock)
            {
                return ++_globalRevision;
            }
        }

        /// <summary>
        /// Mark state dirty without publishing a new state revision.
        /// </summary>
        public static void MarkDirty(bool invalidateConsoleTokenOnPublish = true)
        {
            lock (_revisionLock)
            {
                _dirtyPending = true;
            }
        }

        /// <summary>
        /// Returns whether writes have occurred since the last published state.
        /// </summary>
        public static bool HasDirtyPending()
        {
            lock (_revisionLock)
            {
                return _dirtyPending;
            }
        }

        /// <summary>
        /// Publish dirty state once, bumping the internal revision only when needed.
        /// </summary>
        public static int PublishDirtyStateIfNeeded(bool emitObservationInvalidatedNotification = true)
        {
            int publishedRev;
            bool didPublish = false;

            lock (_revisionLock)
            {
                if (_dirtyPending)
                {
                    _dirtyPending = false;
                    _globalRevision++;
                    didPublish = true;
                }

                publishedRev = _globalRevision;
            }

            if (didPublish)
            {
                InvalidateCurrentConsoleToken();
                if (emitObservationInvalidatedNotification)
                {
                    EmitObservationInvalidatedNotificationOnce(publishedRev);
                }
            }

            return publishedRev;
        }

        /// <summary>
        /// Get current global revision without incrementing.
        /// </summary>
        public static int GetCurrentRevision()
        {
            lock (_revisionLock)
            {
                return _globalRevision;
            }
        }
        
        /// <summary>
        /// Builds a complete Unity state snapshot. Internal publish markers are
        /// intentionally not included in the returned state.
        /// </summary>
        public static object BuildFullState()
        {
            var state = new
            {
                editor = BuildEditorState(),
                project = BuildProjectState(),
                scene = BuildSceneState(),
                selection = BuildSelectionState(),
                console = BuildConsoleState(),
                assets = BuildAssetsState(),
                operations = BuildOperationsState(),
                policy = BuildPolicyState()
            };

            return state;
        }
        
        /// <summary>
        /// Publishes pending dirty state if needed, then builds a complete Unity
        /// state snapshot. The internal published-state marker is not exposed.
        /// </summary>
        public static object BuildFullStateAfterPublishingDirty()
        {
            PublishDirtyStateIfNeeded(false);
            
            var state = new
            {
                editor = BuildEditorState(),
                project = BuildProjectState(),
                scene = BuildSceneState(),
                selection = BuildSelectionState(),
                console = BuildConsoleState(),
                assets = BuildAssetsState(),
                operations = BuildOperationsState(),
                policy = BuildPolicyState()
            };

            return state;
        }

        [Obsolete("Use BuildFullStateAfterPublishingDirty() instead. The method no longer increments on clean reads.")]
        public static object BuildFullStateAndIncrement()
        {
            return BuildFullStateAfterPublishingDirty();
        }

        /// <summary>
        /// Builds editor-specific state.
        /// </summary>
        public static object BuildEditorState()
        {
            var playMode = EditorApplication.isPlaying ? "playing" :
                          (EditorApplication.isPaused ? "paused" : "stopped");

            // Get focused window
            string focusedWindow = null;
            if (EditorWindow.focusedWindow != null)
            {
                focusedWindow = EditorWindow.focusedWindow.GetType().Name;
            }

            // Determine if operations require focus
            // This is a heuristic - some operations need the editor to be focused
            bool requiresFocusForOperations = DetermineIfFocusRequired();

            return new
            {
                playMode = playMode,
                focusedWindow = focusedWindow,
                requiresFocusForOperations = requiresFocusForOperations,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                lastCompilation = BuildLastCompilationState(),
                timeSinceStartup = (float)EditorApplication.timeSinceStartup
            };
        }

        /// <summary>
        /// Builds last compilation state.
        /// 
        /// NOTE:
        /// - This is intentionally minimal and only reports whether Unity is
        ///   currently compiling ("started" vs "idle").
        /// - It is NOT a per-compilation snapshot and does NOT expose error/
        ///   warning counts for any specific pipeline.
        /// - For accurate diagnostics (including error/warning counts), callers
        ///   must use:
        ///     * Compilation deltas from StateComposer.CreateCompilationDelta
        ///       (returned by wait_for_compile), and
        ///     * The Unity console (read_console / unity_console) with sinceToken.
        /// </summary>
        private static object BuildLastCompilationState()
        {
            var status = EditorApplication.isCompiling ? "started" : "idle";

            return new
            {
                status = status
            };
        }

        /// <summary>
        /// Determines if current operations require focus.
        /// </summary>
        private static bool DetermineIfFocusRequired()
        {
            // Heuristic: Some operations need focus, especially during Play mode
            // or when performing visual operations like scene manipulation
            if (EditorApplication.isPlaying || EditorApplication.isPaused)
            {
                return true;
            }

            // Check if SceneView needs focus for certain operations
            var sceneView = EditorWindow.focusedWindow as SceneView;
            if (sceneView != null)
            {
                return false; // Already focused
            }

            return false; // Default: focus not strictly required
        }

        /// <summary>
        /// Builds project-specific state.
        /// </summary>
        public static object BuildProjectState()
        {
            // Detect Render Pipeline
            string srp = "builtin";
            var currentRP = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (currentRP != null)
            {
                string rpName = currentRP.GetType().Name.ToLowerInvariant();
                if (rpName.Contains("urp") || rpName.Contains("universal"))
                {
                    srp = "urp";
                }
                else if (rpName.Contains("hdrp") || rpName.Contains("highdefinition"))
                {
                    srp = "hdrp";
                }
            }

            return new
            {
                srp = srp,
                defineSymbols = GetScriptingDefineSymbols(),
                packages = GetInstalledPackages(),
                dirty = false // Would track if project settings are modified
            };
        }

        private static string[] GetScriptingDefineSymbols()
        {
            // Get scripting define symbols for current build target
            var buildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
            var symbols = PlayerSettings.GetScriptingDefineSymbolsForGroup(buildTargetGroup);
            return string.IsNullOrEmpty(symbols) ?
                new string[0] :
                symbols.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string[] GetInstalledPackages()
        {
            // Simplified - in production would use PackageManager API
            return new string[0];
        }

        /// <summary>
        /// Builds scene-specific state.
        /// </summary>
        public static object BuildSceneState()
        {
            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            
            return new
            {
                activeScenePath = activeScene.path,
                dirty = activeScene.isDirty,
                hasNavMeshData = HasNavMeshData(),
                hasLightingData = HasLightingData()
            };
        }

        private static bool HasNavMeshData()
        {
            // Check if current scene has NavMesh data using runtime reflection
            try
            {
                // First, try to check NavMeshSurface components (com.unity.ai.navigation package)
                Type navMeshSurfaceType = Type.GetType("Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation");
                if (navMeshSurfaceType == null)
                {
                    // Fallback: search in loaded assemblies
                    foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        navMeshSurfaceType = assembly.GetType("Unity.AI.Navigation.NavMeshSurface");
                        if (navMeshSurfaceType != null) break;
                    }
                }

                if (navMeshSurfaceType != null)
                {
                    // Check NavMeshSurface components for navMeshData
                    var activeSurfacesProperty = navMeshSurfaceType.GetProperty("activeSurfaces", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (activeSurfacesProperty != null)
                    {
                        var activeSurfaces = activeSurfacesProperty.GetValue(null);
                        if (activeSurfaces is System.Collections.IList surfaceList && surfaceList.Count > 0)
                        {
                            var navMeshDataProperty = navMeshSurfaceType.GetProperty("navMeshData");
                            if (navMeshDataProperty != null)
                            {
                                foreach (var surface in surfaceList)
                                {
                                    if (surface != null)
                                    {
                                        var navMeshData = navMeshDataProperty.GetValue(surface);
                                        if (navMeshData != null)
                                        {
                                            return true;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Also check all NavMeshSurface components in the scene (including inactive)
                    var allSurfaces = Resources.FindObjectsOfTypeAll(navMeshSurfaceType);
                    if (allSurfaces != null && allSurfaces.Length > 0)
                    {
                        var navMeshDataProperty = navMeshSurfaceType.GetProperty("navMeshData");
                        if (navMeshDataProperty != null)
                        {
                            foreach (var surface in allSurfaces)
                            {
                                if (surface != null)
                                {
                                    var navMeshData = navMeshDataProperty.GetValue(surface);
                                    if (navMeshData != null)
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }

                // Fallback: Try to find NavMesh type using reflection (for built-in NavMesh)
                Type navMeshType = Type.GetType("UnityEngine.AI.NavMesh, UnityEngine.AIModule");
                if (navMeshType == null)
                {
                    // Fallback: search in loaded assemblies
                    foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        navMeshType = assembly.GetType("UnityEngine.AI.NavMesh");
                        if (navMeshType != null) break;
                    }
                }

                if (navMeshType == null)
                    return false;

                // Get CalculateTriangulation method
                MethodInfo calculateTriangulationMethod = navMeshType.GetMethod("CalculateTriangulation", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (calculateTriangulationMethod == null)
                    return false;

                // Call CalculateTriangulation using reflection
                var triangulation = calculateTriangulationMethod.Invoke(null, null);
                if (triangulation == null)
                    return false;

                // Get vertices property
                var verticesProperty = triangulation.GetType().GetProperty("vertices");
                if (verticesProperty == null)
                    return false;

                var vertices = verticesProperty.GetValue(triangulation) as Array;
                return vertices != null && vertices.Length > 0;
            }
            catch
            {
                // If any error occurs, assume no NavMesh data
                return false;
            }
        }

        private static bool HasLightingData()
        {
            // Check if current scene has baked lighting
            return Lightmapping.giWorkflowMode == Lightmapping.GIWorkflowMode.OnDemand ||
                   Lightmapping.lightingDataAsset != null;
        }

        /// <summary>
        /// Builds selection state.
        /// </summary>
        public static object BuildSelectionState()
        {
            var activeObject = Selection.activeGameObject;
            object activeObjectInfo = null;

            if (activeObject != null)
            {
                activeObjectInfo = new
                {
                    id = activeObject.GetInstanceID(),
                    name = activeObject.name,
                    hierarchy_path = GetHierarchyPath(activeObject)
                };
            }

            return new
            {
                activeObject = activeObjectInfo
            };
        }

        private static string GetHierarchyPath(GameObject go)
        {
            if (go == null) return "";
            
            var path = go.name;
            var parent = go.transform.parent;
            
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            
            return path;
        }

        /// <summary>
        /// Builds console state with real tracking data.
        /// </summary>
        public static object BuildConsoleState()
        {
            lock (_consoleLock)
            {
                return new
                {
                    sinceToken = _currentConsoleToken,
                    unreadCount = _consoleUnreadCount,
                    lastErrors = _lastConsoleErrors.ToArray()
                };
            }
        }
        
        /// <summary>
        /// Updates console state tracking. Called by ReadConsole.
        /// </summary>
        public static void UpdateConsoleState(string sinceToken, int unreadCount = 0, object[] lastErrors = null)
        {
            lock (_consoleLock)
            {
                _currentConsoleToken = sinceToken;
                if (!string.IsNullOrEmpty(sinceToken))
                {
                    _invalidatedConsoleTokens.Remove(sinceToken);
                }
                _consoleUnreadCount = unreadCount;
                _lastConsoleErrors.Clear();
                if (lastErrors != null)
                {
                    _lastConsoleErrors.AddRange(lastErrors);
                }
            }
        }

        public static bool IsConsoleTokenInvalidated(string sinceToken)
        {
            if (string.IsNullOrEmpty(sinceToken))
            {
                return false;
            }

            lock (_consoleLock)
            {
                return _invalidatedConsoleTokens.Contains(sinceToken);
            }
        }

        private static void InvalidateCurrentConsoleToken()
        {
            lock (_consoleLock)
            {
                if (!string.IsNullOrEmpty(_currentConsoleToken))
                {
                    _invalidatedConsoleTokens.Add(_currentConsoleToken);
                }
            }
        }

        private static void EmitObservationInvalidatedNotificationOnce(int publishedRev)
        {
            lock (_revisionLock)
            {
                if (_lastObservationInvalidatedNotificationRev == publishedRev)
                {
                    return;
                }
                _lastObservationInvalidatedNotificationRev = publishedRev;
            }

            try
            {
                var payload = Codely.Newtonsoft.Json.Linq.JObject.FromObject(new
                {
                    state = "stale",
                    console = "token_invalidated"
                });
                UnityTcp.Editor.UnityTcpBridge.NotifyAll("unity_observation_invalidated", payload);
            }
            catch
            {
                // Notifications are best-effort; publishing state must not fail.
            }
        }
        
        /// <summary>
        /// Gets the current console token.
        /// </summary>
        public static string GetCurrentConsoleToken()
        {
            lock (_consoleLock)
            {
                return _currentConsoleToken;
            }
        }

        /// <summary>
        /// Builds assets state with tracked touched assets.
        /// </summary>
        public static object BuildAssetsState()
        {
            lock (_assetsLock)
            {
                return new
                {
                    touched = _touchedAssets.ToArray()
                };
            }
        }
        
        /// <summary>
        /// Adds a touched asset to tracking. Called by asset operations.
        /// </summary>
        public static void AddTouchedAsset(string path, bool imported = false, bool hasMeta = true)
        {
            lock (_assetsLock)
            {
                _touchedAssets.Add(new { path, imported, hasMeta });
                // Keep only last 100 entries
                while (_touchedAssets.Count > 100)
                {
                    _touchedAssets.RemoveAt(0);
                }
            }
        }
        
        /// <summary>
        /// Clears touched assets list.
        /// </summary>
        public static void ClearTouchedAssets()
        {
            lock (_assetsLock)
            {
                _touchedAssets.Clear();
            }
        }

        /// <summary>
        /// Builds pending operations state from AsyncOperationTracker.
        /// </summary>
        public static object BuildOperationsState()
        {
            // Get pending operations from AsyncOperationTracker
            var pendingJobs = AsyncOperationTracker.GetPendingJobs();
            var pending = pendingJobs.Select(job => new
            {
                id = job.OpId,
                type = job.Type.ToString(),
                progress = job.Progress,
                message = job.Message
            }).ToArray();
            
            return new
            {
                pending = pending
            };
        }

        /// <summary>
        /// Builds policy state.
        /// </summary>
        public static object BuildPolicyState()
        {
            return new
            {
                writeGuardInPlayMode = "deny", // Default: deny writes in Play mode
                refreshMode = "debounced",
                consoleReadPolicy = "must_clear_before_read"
            };
        }

        /// <summary>
        /// Creates a Console state delta.
        /// </summary>
        public static object CreateConsoleDelta(string sinceToken = null, int? unreadCount = null, object[] lastErrors = null)
        {
            var consoleDelta = new Dictionary<string, object>();
            
            if (sinceToken != null) consoleDelta["sinceToken"] = sinceToken;
            if (unreadCount.HasValue) consoleDelta["unreadCount"] = unreadCount.Value;
            if (lastErrors != null) consoleDelta["lastErrors"] = lastErrors;

            return new { console = consoleDelta };
        }

        /// <summary>
        /// Creates a Compilation state delta.
        /// </summary>
        public static object CreateCompilationDelta(bool? isCompiling = null, string status = null, int? errors = null, int? warnings = null)
        {
            var editorDelta = new Dictionary<string, object>();
            var compilationDelta = new Dictionary<string, object>();

            if (isCompiling.HasValue) editorDelta["isCompiling"] = isCompiling.Value;
            
            if (status != null) compilationDelta["status"] = status;
            if (errors.HasValue) compilationDelta["errors"] = errors.Value;
            if (warnings.HasValue) compilationDelta["warnings"] = warnings.Value;

            if (compilationDelta.Count > 0)
            {
                editorDelta["lastCompilation"] = compilationDelta;
            }

            return new { editor = editorDelta };
        }

        /// <summary>
        /// Creates a Scene state delta.
        /// </summary>
        public static object CreateSceneDelta(string activeScenePath = null, bool? dirty = null)
        {
            var sceneDelta = new Dictionary<string, object>();
            
            if (activeScenePath != null) sceneDelta["activeScenePath"] = activeScenePath;
            if (dirty.HasValue) sceneDelta["dirty"] = dirty.Value;

            return new { scene = sceneDelta };
        }

        /// <summary>
        /// Creates an Asset state delta.
        /// </summary>
        public static object CreateAssetDelta(object[] touchedAssets)
        {
            return new
            {
                assets = new
                {
                    touched = touchedAssets
                }
            };
        }

        /// <summary>
        /// Creates an Editor state delta.
        /// </summary>
        public static object CreateEditorDelta(string focusedWindow = null, bool? isUpdating = null)
        {
            var editorDelta = new Dictionary<string, object>();
            
            if (focusedWindow != null) editorDelta["focusedWindow"] = focusedWindow;
            if (isUpdating.HasValue) editorDelta["isUpdating"] = isUpdating.Value;

            return new { editor = editorDelta };
        }

        /// <summary>
        /// Creates an Operations state delta.
        /// </summary>
        public static object CreateOperationsDelta(object[] pendingOperations)
        {
            return new
            {
                operations = new
                {
                    pending = pendingOperations
                }
            };
        }

        /// <summary>
        /// Client-provided revisions are accepted for backward compatibility but
        /// never block writes. Revision is an internal published-state marker.
        /// </summary>
        public static object ValidateClientRevision(int? clientRev)
        {
            return null;
        }
        
        /// <summary>
        /// Accepts legacy revision params without enforcing a conflict.
        /// </summary>
        public static object ValidateClientRevisionFromParams(Codely.Newtonsoft.Json.Linq.JObject @params)
        {
            return null;
        }
        
        /// <summary>
        /// Merges multiple state deltas into one combined delta.
        /// </summary>
        public static object MergeStateDeltas(params object[] deltas)
        {
            if (deltas == null || deltas.Length == 0) return null;
            if (deltas.Length == 1) return deltas[0];

            // Preserve legacy behavior: if only one non-null delta is provided, return it as-is.
            int nonNullCount = 0;
            object single = null;
            foreach (var d in deltas)
            {
                if (d == null) continue;
                nonNullCount++;
                single = d;
                if (nonNullCount > 1) break;
            }
            if (nonNullCount == 0) return null;
            if (nonNullCount == 1) return single;
            
            var merged = new Dictionary<string, object>();
            
            foreach (var delta in deltas)
            {
                if (delta == null) continue;

                // Prefer a JSON/dictionary representation to avoid reflection issues
                // (e.g., when a state_delta is already a JObject/JToken).
                Dictionary<string, object> deltaDict = null;
                try
                {
                    // Codely.Newtonsoft.Json.Linq types (JObject / JToken)
                    if (delta is Codely.Newtonsoft.Json.Linq.JObject jObj)
                    {
                        deltaDict = jObj.ToObject<Dictionary<string, object>>();
                    }
                    else if (delta is Codely.Newtonsoft.Json.Linq.JToken jTok &&
                             jTok.Type == Codely.Newtonsoft.Json.Linq.JTokenType.Object)
                    {
                        var asObj = jTok as Codely.Newtonsoft.Json.Linq.JObject;
                        deltaDict = (asObj ?? Codely.Newtonsoft.Json.Linq.JObject.FromObject(jTok))
                            .ToObject<Dictionary<string, object>>();
                    }
                    else if (delta is IDictionary<string, object> iDict)
                    {
                        deltaDict = new Dictionary<string, object>(iDict);
                    }
                    else
                    {
                        // Last resort: serialize arbitrary objects into a JObject then into a dictionary.
                        var obj = Codely.Newtonsoft.Json.Linq.JObject.FromObject(delta);
                        deltaDict = obj.ToObject<Dictionary<string, object>>();
                    }
                }
                catch
                {
                    deltaDict = null;
                }

                if (deltaDict != null)
                {
                    foreach (var kv in deltaDict)
                    {
                        if (kv.Value == null) continue;

                        if (merged.ContainsKey(kv.Key))
                        {
                            // Merge nested dictionaries (one level deep, consistent with legacy behavior)
                            var existingDict = merged[kv.Key] as Dictionary<string, object>;
                            var newDict = kv.Value as Dictionary<string, object>;
                            if (existingDict != null && newDict != null)
                            {
                                foreach (var nk in newDict)
                                {
                                    existingDict[nk.Key] = nk.Value;
                                }
                            }
                            else
                            {
                                merged[kv.Key] = kv.Value;
                            }
                        }
                        else
                        {
                            merged[kv.Key] = kv.Value;
                        }
                    }
                    continue;
                }

                // Fallback: reflection-based merge (skip indexer properties to avoid invocation errors)
                try
                {
                    var props = delta.GetType().GetProperties();
                    foreach (var prop in props)
                    {
                        if (prop.GetIndexParameters().Length > 0) continue;

                        object value = null;
                        try { value = prop.GetValue(delta); } catch { continue; }
                        if (value == null) continue;

                        if (merged.ContainsKey(prop.Name))
                        {
                            // Merge nested dictionaries
                            if (merged[prop.Name] is Dictionary<string, object> existingDict &&
                                value is Dictionary<string, object> newDict)
                            {
                                foreach (var kv in newDict)
                                {
                                    existingDict[kv.Key] = kv.Value;
                                }
                            }
                            else
                            {
                                merged[prop.Name] = value;
                            }
                        }
                        else
                        {
                            merged[prop.Name] = value;
                        }
                    }
                }
                catch
                {
                    // Ignore merge errors from unexpected delta shapes.
                }
            }
            
            return merged.Count > 0 ? merged : null;
        }
    }
}

