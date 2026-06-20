using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Codely.Newtonsoft.Json;
using System.Reflection;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityTcp.Editor.Helpers;

#if UNITY_2021_2_OR_NEWER
using UnityEngine.UIElements;
#endif

namespace UnityTcp.Editor.Tools
{
    /// <summary>
    /// Handles screenshot capture operations within Unity Editor.
    ///
    /// Actions:
    ///   capture_game_view        – Capture what the Game View currently shows.
    ///   capture_scene_view       – Capture the Scene View from one or more directions.
    ///   capture_main_camera      – Render Camera.main to a texture.
    ///   capture_specific_camera  – Render a named camera to a texture.
    ///   capture_asset            – Instantiate asset(s) in the current scene and render a preview.
    ///   capture_ui_toolkit       – Render a UIDocument (.uxml) to a texture.
    ///
    /// Legacy aliases (kept for backward compatibility):
    ///   capture          → capture_game_view
    ///   capture_scene_camera → capture_scene_view
    ///
    /// Common parameters (most actions):
    ///   path (string)         – Custom save directory (default: <ProjectRoot>/screenshots).
    ///   filename (string)     – Custom filename (with or without extension).
    ///   over_time (string)    – "N" or "NxS": capture N frames with S simulation steps
    ///                           between each.
    ///                           Applies to: capture_game_view, capture_scene_view,
    ///                                       capture_main_camera, capture_specific_camera.
    ///
    /// capture_game_view:
    ///   Width/height are always taken from the Game View's actual size.
    ///
    /// capture_scene_view / capture_asset:
    ///   width (int)           – Output width in pixels (default: 1024).
    ///   height (int)          – Output height in pixels (default: 1024).
    ///   view (string)         – "cardinal" (default), "current", "front", "back", "left", "right",
    ///                           "top", "bottom", "iso", "all".
    ///                           Note: view is only used by capture_scene_view and capture_asset.
    ///
    /// capture_main_camera / capture_specific_camera:
    ///   Always renders from the camera's current position, rotation and projection.
    ///   width (int)           – Output width in pixels (default: camera pixel width).
    ///   height (int)          – Output height in pixels (default: camera pixel height).
    ///   Note: view and orthographic parameters are not applicable to camera captures.
    ///
    /// Auto-scaling:
    ///   When neither width nor height is supplied, each individual screenshot is
    ///   downsampled so its longest edge equals 256 pixels (aspect ratio preserved).
    ///   Supplying either width or height disables this behavior.
    /// </summary>
    public static class ManageScreenshot
    {
        private static readonly List<string> ValidActions = new List<string>
        {
            "capture_game_view",
            "capture_scene_view",
            "capture_main_camera",
            "capture_specific_camera",
            "capture_asset",
            "capture_ui_toolkit",
            // Legacy aliases
            "capture",
            "capture_scene_camera",
        };

        // ======================== sync entry point (over_time forced to 1) ========================

        /// <summary>
        /// Synchronous command handler. over_time is always treated as 1 — no frame-stepping
        /// occurs, so this runs entirely within the current editor frame.
        /// capture_ui_toolkit is not supported here;
        /// </summary>
        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                return Response.Success("Parameters cannot be null.");

            string action = @params["action"]?.ToString()?.ToLower();
            if (string.IsNullOrEmpty(action))
                return Response.Success("Action parameter is required.");
            if (!ValidActions.Contains(action))
                return Response.Success($"Unknown action: '{action}'. Valid actions are: {string.Join(", ", ValidActions)}");

            if (action == "capture_ui_toolkit")
                return Response.Success("capture_ui_toolkit requires multi-frame rendering; use HandleCommandCoroutine.");

            // Clone params and pin over_time to "1" so no intermediate yields occur.
            var p = new JObject(@params) { ["over_time"] = "1" };

            object captureResult = null;
            try
            {
                switch (action)
                {
                    case "capture_game_view":
                    case "capture":
                    {
                        var co = CaptureGameViewCoroutine(p, r => captureResult = r);
                        while (co.MoveNext()) { }
                        break;
                    }
                    case "capture_scene_view":
                    case "capture_scene_camera":
                    {
                        var co = CaptureSceneViewCoroutine(p, r => captureResult = r);
                        while (co.MoveNext()) { }
                        break;
                    }
                    case "capture_main_camera":
                    {
                        Camera cam = Camera.main;
                        if (cam == null) return Response.Success("No main camera (Camera.main) found in the scene.");
                        var co = CaptureFromCameraCoroutine(cam, p, "MainCamera", r => captureResult = r);
                        while (co.MoveNext()) { }
                        break;
                    }
                    case "capture_specific_camera":
                    {
                        string camName = p["camera_name"]?.ToString();
                        if (string.IsNullOrEmpty(camName)) return Response.Success("'camera_name' parameter is required.");
                        Camera cam = FindCameraByName(camName);
                        if (cam == null) return Response.Success($"Camera '{camName}' not found in the scene.");
                        var co = CaptureFromCameraCoroutine(cam, p, camName, r => captureResult = r);
                        while (co.MoveNext()) { }
                        break;
                    }
                    case "capture_asset":
                        captureResult = CaptureAsset(p);
                        break;
                    default:
                        return Response.Success($"Unknown action: '{action}'.");
                }
            }
            catch (Exception e)
            {
                CodelyLogger.LogError($"[ManageScreenshot] HandleCommand '{action}' failed: {e}");
                return Response.Success($"Internal error processing action '{action}': {e.Message}");
            }

            return captureResult ?? Response.Success("No result produced.");
        }

        // ======================== coroutine entry point ========================

        public static IEnumerator HandleCommandCoroutine(JObject @params, Action<string> setResult)
        {
            string action = @params["action"]?.ToString().ToLower();
            if (string.IsNullOrEmpty(action))
            {
                setResult(JsonConvert.SerializeObject(Response.Success("Action parameter is required.")));
                yield break;
            }
            if (!ValidActions.Contains(action))
            {
                setResult(JsonConvert.SerializeObject(Response.Success($"Unknown action: '{action}'. Valid actions are: {string.Join(", ", ValidActions)}")));
                yield break;
            }

            object captureResult = null;
            IEnumerator inner = null;
            Exception caughtEx = null;

            try
            {
                switch (action)
                {
                    case "capture_game_view":
                    case "capture":
                        inner = CaptureGameViewCoroutine(@params, r => captureResult = r);
                        break;

                    case "capture_scene_view":
                    case "capture_scene_camera":
                        inner = CaptureSceneViewCoroutine(@params, r => captureResult = r);
                        break;

                    case "capture_main_camera":
                    {
                        Camera cam = Camera.main;
                        if (cam == null) { captureResult = Response.Success("No main camera (Camera.main) found in the scene."); break; }
                        inner = CaptureFromCameraCoroutine(cam, @params, "MainCamera", r => captureResult = r);
                        break;
                    }

                    case "capture_specific_camera":
                    {
                        string camName = @params["camera_name"]?.ToString();
                        if (string.IsNullOrEmpty(camName)) { captureResult = Response.Success("'camera_name' parameter is required."); break; }
                        Camera cam = FindCameraByName(camName);
                        if (cam == null) { captureResult = Response.Success($"Camera '{camName}' not found in the scene."); break; }
                        inner = CaptureFromCameraCoroutine(cam, @params, camName, r => captureResult = r);
                        break;
                    }

                    case "capture_asset":
                        captureResult = CaptureAsset(@params);
                        break;

                    case "capture_ui_toolkit":
                        inner = CaptureUIToolkitCoroutine(@params, r => captureResult = r);
                        break;

                    default:
                        captureResult = Response.Success($"Unknown action: '{action}'.");
                        break;
                }
            }
            catch (Exception e)
            {
                CodelyLogger.LogError($"[ManageScreenshot] Action '{action}' failed: {e}");
                setResult(JsonConvert.SerializeObject(Response.Success($"Internal error processing action '{action}': {e.Message}")));
                yield break;
            }

            if (inner != null)
            {
                while (true)
                {
                    bool hasNext;
                    try { hasNext = inner.MoveNext(); }
                    catch (Exception e) { caughtEx = e; break; }
                    if (!hasNext) break;
                    yield return inner.Current;
                }
            }
            else
            {
                yield return null;
            }

            if (caughtEx != null)
            {
                CodelyLogger.LogError($"[ManageScreenshot] Action '{action}' failed: {caughtEx}");
                setResult(JsonConvert.SerializeObject(Response.Success($"Internal error: {caughtEx.Message}")));
            }
            else
            {
                setResult(JsonConvert.SerializeObject(captureResult ?? Response.Success("No result produced.")));
            }
        }

        // ======================== capture_game_view ========================
        // Parameters:
        //   over_time (string) – "N" or "NxS": capture N frames, yielding S editor ticks between each.
        //   path, filename
        private static IEnumerator CaptureGameViewCoroutine(JObject p, Action<object> setResult)
        {
            string customPath = p["path"]?.ToString();
            string customFile = p["filename"]?.ToString();
            OverTimeParams ot = ParseOverTime(p["over_time"]);
            List<Texture2D> captures = new List<Texture2D>();

            for (int i = 0; i < ot.Count; i++)
            {
                if (i > 0)
                {
                    int steps = Math.Max(1, ot.StepsPerInterval);
                    for (int s = 0; s < steps; s++)
                        yield return null;
                }

            Texture2D tex = CaptureGameViewTexture();
                if (tex == null) continue;
                tex = MaybeAutoScale(p, tex);
                if (ot.Count > 1) DrawLabelOnTexture(tex, FrameLabel(i));
                captures.Add(tex);
            }

            if (captures.Count == 0)
                setResult(Response.Success("Failed to capture Game View. Ensure a Game View window exists."));
            else
                setResult(BuildImageResponse(StitchOrSingle(captures), customPath, customFile, "GameView"));
        }

        // ======================== capture_scene_view ========================
        private static IEnumerator CaptureSceneViewCoroutine(JObject p, Action<object> setResult)
        {
            SceneView sv = SceneView.lastActiveSceneView;
            if (sv == null) { setResult(Response.Success("No active Scene View found. Please ensure a Scene View is open.")); yield break; }
            Camera sceneCam = sv.camera;
            if (sceneCam == null) { setResult(Response.Success("Scene View camera not found.")); yield break; }

            int    width      = p["width"]?.ToObject<int?>()  ?? 480;
            int    height     = p["height"]?.ToObject<int?>() ?? 270;
            string customPath = p["path"]?.ToString();
            string customFile = p["filename"]?.ToString();
            string viewParam  = p["view"]?.ToString()?.ToLower() ?? "cardinal";
            bool   orthographic = p["orthographic"]?.ToObject<bool?>() ?? false;
            OverTimeParams ot = ParseOverTime(p["over_time"]);

            Bounds? selBounds = null;
            string captureWarning = null;
            JObject focusObjEntry = p["focus_object"] as JObject;
            if (focusObjEntry != null)
            {
                string objPath  = focusObjEntry["focusobject"]?.ToString();
                int    objIndex = focusObjEntry["index"]?.ToObject<int?>() ?? 0;
                if (!string.IsNullOrEmpty(objPath))
                {
                    GameObject go = FindGameObjectByPath(objPath, objIndex);
                    if (go != null)
                    {
                        selBounds = CalculateSingleObjectBounds(go);
                        sv.Frame(selBounds.Value, true);
                    }
                    else
                    {
                        captureWarning = $"focus_object not found: path='{objPath}' index={objIndex}. Falling back to scene bounds.";
                        CodelyLogger.LogWarning($"[ManageScreenshot] {captureWarning}");
                    }
                }
            }

            List<string>    views    = GetViewList(viewParam);
            List<Texture2D> captures = new List<Texture2D>();
            Quaternion origRot   = sv.rotation;
            bool       origOrtho = sv.orthographic;

            GameObject tempGO  = new GameObject("__CodelyTempSceneCam__");
            Camera     tempCam = tempGO.AddComponent<Camera>();
            tempCam.enabled = false;
            CopyCameraSettings(sceneCam, tempCam);
            Bounds captureBounds = selBounds ?? GetSceneBounds();

            try
            {
                for (int ti = 0; ti < ot.Count; ti++)
                {
                    if (ti > 0)
                    {
                        int steps = Math.Max(1, ot.StepsPerInterval);
                        for (int s = 0; s < steps; s++)
                            yield return null;
                        CopyCameraSettings(sceneCam, tempCam);
                    }

                    foreach (string view in views)
                    {
                        if (view == "current")
                        {
                            tempCam.transform.SetPositionAndRotation(sceneCam.transform.position, sceneCam.transform.rotation);
                            tempCam.orthographic     = orthographic;
                            tempCam.orthographicSize = sv.size * 0.5f;
                        }
                        else
                        {
                            PositionCameraForView(tempCam, captureBounds, view, orthographic);
                        }

                        Texture2D tex = RenderCameraToTexture(tempCam, width, height);
                        if (tex != null)
                        {
                            tex = MaybeAutoScale(p, tex);
                            bool multiView = views.Count > 1;
                            bool multiTime = ot.Count > 1;
                            if (multiView && multiTime)       DrawLabelOnTexture(tex, ViewAbbreviation(view) +"-"+ FrameLabel(ti));
                            else if (multiView)              DrawLabelOnTexture(tex, ViewAbbreviation(view));
                            else if (multiTime)              DrawLabelOnTexture(tex, FrameLabel(ti));
                            captures.Add(tex);
                        }
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tempGO);
                sv.rotation     = origRot;
                sv.orthographic = origOrtho;
            }

            if (captures.Count == 0)
                setResult(Response.Success("Failed to capture any Scene View screenshots."));
            else
                setResult(BuildImageResponse(StitchOrSingle(captures), customPath, customFile, "SceneView", captureWarning));
        }

        // ======================== capture_asset ========================
        // Instantiates one or more assets, frames them with a temporary camera, renders one or
        // more views, stitches the result into a grid, and destroys all temporary objects.
        //
        // Supported asset types: GameObject/Prefab, Mesh, Material, Texture2D, Sprite.
        //
        // Parameters:
        //   assets (string[], required) – Asset paths (e.g. "Assets/Prefabs/Hero.prefab").
        //   scene        – Path to a .unity scene file (e.g. "Assets/Scenes/Preview.unity").
        //                  When provided the scene is opened additively, assets are instantiated
        //                  into it, and the scene is closed and removed when capture finishes.
        //                  Without this parameter assets are staged far from the origin in the
        //                  currently active scene.
        //   view         – Direction(s) to render; defaults to "cardinal".
        //   orthographic – bool, default false.
        //   width, height, path, filename
        private static object CaptureAsset(JObject p)
        {
            if (EditorApplication.isPlaying)
                return Response.Success("capture_asset requires Edit Mode. Exit Play Mode first.");

            JArray assetsArray = p["assets"] as JArray;
            if (assetsArray == null || assetsArray.Count == 0)
                return Response.Success("'assets' must be a non-empty array of asset paths.");

            Camera svCam   = SceneView.lastActiveSceneView?.camera;
            int    width   = p["width"]?.ToObject<int?>()  ?? svCam?.pixelWidth  ?? 480;
            int    height  = p["height"]?.ToObject<int?>() ?? svCam?.pixelHeight ?? 270;
            string customPath  = p["path"]?.ToString();
            string customFile  = p["filename"]?.ToString();
            string viewParam   = p["view"]?.ToString()?.ToLower() ?? "cardinal";
            bool   orthographic= p["orthographic"]?.ToObject<bool?>() ?? false;
            string scenePath   = p["scene"]?.ToString();

            // --- Open target scene additively (if requested) ---
            UnityEngine.SceneManagement.Scene originalScene =
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            UnityEngine.SceneManagement.Scene? targetScene = null;

            if (!string.IsNullOrEmpty(scenePath))
            {
                string sceneRel = NormalizePath(scenePath);
                if (string.IsNullOrEmpty(sceneRel) ||
                    (!sceneRel.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) 
                    && !sceneRel.EndsWith(".scene", StringComparison.OrdinalIgnoreCase)))
                    return Response.Success($"Invalid scene path: '{scenePath}'. Must be a .unity file.");

                try
                {
                    var opened = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                        sceneRel, UnityEditor.SceneManagement.OpenSceneMode.Additive);
                    if (!opened.IsValid())
                        return Response.Success($"Failed to open scene: '{scenePath}'.");
                    targetScene = opened;
                    UnityEditor.SceneManagement.EditorSceneManager.SetActiveScene(opened);
                }
                catch (Exception e)
                {
                    return Response.Success($"Failed to open scene '{scenePath}': {e.Message}");
                }
            }

            // Staging area far from origin so instantiated objects don't interfere
            Vector3 stageOrigin = new Vector3(50000f, 50000f, 50000f);

            List<GameObject> instantiated = new List<GameObject>();
            GameObject tempCamGO = null;

            try
            {
                // --- Instantiate assets ---
                float xCursor = 0f;
                foreach (var token in assetsArray)
                {
                    string assetPath = token.ToString();
                    try
                    {
                        string rel = NormalizePath(assetPath);
                        if (string.IsNullOrEmpty(rel)) continue;

                        Type assetType = AssetDatabase.GetMainAssetTypeAtPath(rel);
                        if (assetType == null) continue;

                        GameObject obj = InstantiateAsset(rel, assetType);
                        if (obj == null) continue;

                        instantiated.Add(obj);

                        Bounds objBounds = CalculateSingleObjectBounds(obj);
                        obj.transform.position = stageOrigin + new Vector3(xCursor + objBounds.extents.x, 0f, 0f);
                        xCursor += objBounds.size.x + 1f;
                    }
                    catch (Exception e)
                    {
                        CodelyLogger.LogWarning($"[ManageScreenshot] Failed to instantiate '{assetPath}': {e.Message}");
                    }
                }

                if (instantiated.Count == 0)
                    return Response.Success("No assets could be instantiated. Verify the asset paths are correct.");

                // --- Set up temp camera ---
                tempCamGO = new GameObject("__CodelyAssetPreviewCam__");
                Camera cam = tempCamGO.AddComponent<Camera>();
                cam.enabled         = false;
               
                cam.cullingMask     = -1;
                cam.nearClipPlane   = 0.01f;
                cam.farClipPlane    = 10000f;
                cam.fieldOfView     = 45f;
                cam.orthographic    = orthographic;

                foreach (GameObject go in instantiated)
                    foreach (Canvas canvas in go.GetComponentsInChildren<Canvas>())
                    {
                        canvas.renderMode  = RenderMode.WorldSpace;
                        canvas.worldCamera = cam;
                    }

                // --- Render views ---
                Bounds overallBounds = CalculateBounds(instantiated);
                List<string>    views    = GetViewList(viewParam);
                List<Texture2D> captures = new List<Texture2D>();

                foreach (string view in views)
                {
                    PositionCameraForView(cam, overallBounds, view, orthographic);
                    Texture2D tex = RenderCameraToTexture(cam, width, height);
                    if (tex != null)
                    {
                        tex = MaybeAutoScale(p, tex);
                        if (views.Count > 1)
                            DrawLabelOnTexture(tex, ViewAbbreviation(view));
                        captures.Add(tex);
                    }
                }

                if (captures.Count == 0)
                    return Response.Success("Failed to render asset preview.");

                Texture2D result = StitchOrSingle(captures);
                return BuildImageResponse(result, customPath, customFile, "AssetPreview");
            }
            finally
            {
                foreach (GameObject go in instantiated)
                {
                    if (go == null) continue;
                    // Destroy runtime materials created by InstantiateAsset (not project assets)
                    foreach (Renderer r in go.GetComponentsInChildren<Renderer>())
                        if (r.sharedMaterial != null && !AssetDatabase.Contains(r.sharedMaterial))
                            UnityEngine.Object.DestroyImmediate(r.sharedMaterial);
                    UnityEngine.Object.DestroyImmediate(go);
                }
                if (tempCamGO != null) UnityEngine.Object.DestroyImmediate(tempCamGO);

                // Close the additively opened scene and restore the original active scene
                if (targetScene.HasValue && targetScene.Value.IsValid())
                {
                    UnityEditor.SceneManagement.EditorSceneManager.SetActiveScene(originalScene);
                    UnityEditor.SceneManagement.EditorSceneManager.CloseScene(targetScene.Value, true);
                }
            }
        }

        // ======================== capture_ui_toolkit ========================
        // Renders a UIDocument (.uxml file) to a RenderTexture via UIElements PanelSettings.
        // Note: UIElements rendering is driven by the Unity player loop. This method forces
        // an immediate panel repaint via reflection; the first call may yield a blank image if
        // no prior editor frame has processed the panel. Running the command a second time
        // (after at least one editor update) should produce the correct result.
        //
        // Parameters:
        //   document_path (string, required) – Path to the .uxml asset (e.g. "Assets/UI/HUD.uxml").
        //   width, height – Defaults to the main GameView size or 1920×1080.
        //   format        – "png" (default, preserves alpha) or "jpg".
        //   path, filename

        private static IEnumerator CaptureUIToolkitCoroutine(JObject p, Action<object> setResult)
        {
#if UNITY_2021_2_OR_NEWER
            if (EditorApplication.isPlaying)
            {
                CodelyLogger.LogError("capture_ui_toolkit requires Edit Mode. Exit Play Mode first.");
                setResult(Response.Success("capture_ui_toolkit requires Edit Mode. Exit Play Mode first."));
                yield break;
            }

            string docPath = p["document_path"]?.ToString();
            if (string.IsNullOrEmpty(docPath))
            {
                setResult(Response.Success("'document_path' parameter is required."));
                yield break;
            }

            string rel = NormalizePath(docPath);
            if (string.IsNullOrEmpty(rel))
            {
                setResult(Response.Success($"Invalid document_path: '{docPath}'."));
                yield break;
            }

            string customPath = p["path"]?.ToString();
            string customFile = p["filename"]?.ToString();

            Vector2 gameViewSize = Handles.GetMainGameViewSize();
            int width  = p["width"]?.ToObject<int?>()  ?? Math.Min((int)gameViewSize.x, 480);
            int height = p["height"]?.ToObject<int?>() ?? Math.Min((int)gameViewSize.y, 270);

            // Force reimport so any unsaved changes to the uxml are picked up
            if (AssetDatabase.GetMainAssetTypeAtPath(rel) == typeof(VisualTreeAsset))
                AssetDatabase.ImportAsset(rel, ImportAssetOptions.ForceSynchronousImport);

            VisualTreeAsset vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(rel);
            if (vta == null)
            {
                setResult(Response.Success($"UIDocument asset not found at: '{docPath}'."));
                yield break;
            }

            PanelSettings ps = null;
            RenderTexture rt = null;
            GameObject    go = null;

            try
            {
                ps                   = ScriptableObject.CreateInstance<PanelSettings>();
                ps.hideFlags         = HideFlags.HideAndDontSave;
                // ConstantPixelSize + scale=1 + matching DPI keeps the panel's resolved
                // layout size equal to the RT dimensions regardless of the editor's HiDPI
                // setting. Without this, on HiDPI displays the panel may lay out at half
                // size or report a 0x0 logical viewport and render nothing.
                ps.scaleMode         = PanelScaleMode.ConstantPixelSize;
                ps.scale             = 1f;
                ps.referenceDpi      = 96f;
                ps.fallbackDpi       = 96f;
                ps.clearColor        = true;
                // Must clear depth too — otherwise stale depth values from previous
                // retry iterations cause subsequent UI quads to be depth-rejected and
                // the texture stays blank.
                ps.clearDepthStencil = true;
                ps.colorClearValue   = Color.clear;

                rt           = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                rt.hideFlags = HideFlags.HideAndDontSave;
                rt.Create();
                ps.targetTexture = rt;

                go = new GameObject("__CodelyTempUIDocument__");
                // DontSave (not HideAndDontSave): the GameObject must be a regular
                // active scene member so the runtime panel system registers its panel
                // for off-screen rendering.
                go.hideFlags = HideFlags.DontSave;

                UIDocument uiDoc = go.AddComponent<UIDocument>();
                uiDoc.panelSettings   = ps;
                uiDoc.visualTreeAsset = vta;

                ThemeStyleSheet theme = GetUIBuilderTheme(rel) ?? GetDefaultRuntimeTheme();
                if (theme != null)
                    ps.themeStyleSheet = theme;

                // The root element inherits its size from the panel, which in edit mode
                // may resolve to 0x0 before the first layout tick. Pin it to the RT size
                // so the very first paint produces a non-empty image.
                VisualElement root = uiDoc.rootVisualElement;
                if (root != null)
                {
                    root.style.position = Position.Absolute;
                    root.style.left     = 0;
                    root.style.top      = 0;
                    root.style.width    = width;
                    root.style.height   = height;
                }
            }
            catch (Exception e)
            {
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
                if (ps != null) UnityEngine.Object.DestroyImmediate(ps);
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
                setResult(Response.Success($"Failed to set up UIDocument: {e.Message}"));
                yield break;
            }

            UIDocument uiDocRef = go.GetComponent<UIDocument>();

            // Kick the runtime panel system immediately so the panel runs its first
            // layout pass and produces a non-empty image before we start sampling.
            TryForceUIDocumentRepaint(uiDocRef);

            // In edit mode, runtime panels do not get repainted automatically — neither
            // the player loop nor UIElementsRuntimeUtility.RepaintOverlayPanels() will
            // flush draw commands to a targetTexture-bound PanelSettings. The actual
            // work is done by TryForceUIDocumentRepaint, which on Unity 2021/2022 calls
            // the panel's Repaint(Event) and on Unity 6 additionally calls the panel's
            // parameterless Render() to synchronously flush UIR draws on this thread
            // (no foreground / player-loop dependency). The QueuePlayerLoopUpdate /
            // RepaintAllViews calls below give Yoga's layout pass a chance to settle
            // between paints (useful when stylesheets reference assets that load async).
            for (int i = 0; i < 10; i++)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                InternalEditorUtility.RepaintAllViews();
                yield return null;
                TryForceUIDocumentRepaint(uiDocRef);
            }

            const int maxRetry = 60;
            for (int i = 0; i < maxRetry && IsRenderTextureBlack(rt); i++)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                InternalEditorUtility.RepaintAllViews();
                yield return null;
                TryForceUIDocumentRepaint(uiDocRef);
            }

            try
            {
                Texture2D tex = ReadRenderTexture(rt, width, height);
                if (tex == null)
                    setResult(Response.Success("Failed to read UIDocument render texture."));
                else
                {
                    tex = MaybeAutoScale(p, tex);
                    setResult(BuildImageResponse(tex, customPath, customFile, "UIToolkit"));
                }
            }
            catch (Exception e)
            {
                setResult(Response.Success($"Failed to capture UIDocument: {e.Message}"));
            }
            finally
            {
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
                if (ps != null) UnityEngine.Object.DestroyImmediate(ps);
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
            }
#else
            setResult(Response.Success("capture_ui_toolkit requires Unity 2021.2 or newer."));
            yield break;
#endif
        }

        // ==================== Shared helpers ====================

        // Shared implementation for capture_main_camera and capture_specific_camera.
        // Always renders from the camera's current position/rotation/projection.
        // Supports over_time: captures across multiple simulation frames.
        private static IEnumerator CaptureFromCameraCoroutine(Camera cam, JObject p, string label, Action<object> setResult)
        {
            int    width      = p["width"]?.ToObject<int?>()  ?? cam?.pixelWidth ?? 480;
            int    height     = p["height"]?.ToObject<int?>() ?? cam?.pixelHeight ?? 270;
            string customPath = p["path"]?.ToString();
            string customFile = p["filename"]?.ToString();
            OverTimeParams ot = ParseOverTime(p["over_time"]);

            RenderTexture origRT = cam.targetTexture;
            List<Texture2D> captures = new List<Texture2D>();

            try
            {
                for (int ti = 0; ti < ot.Count; ti++)
                {
                    if (ti > 0)
                    {
                        int steps = Math.Max(1, ot.StepsPerInterval);
                        for (int s = 0; s < steps; s++)
                            yield return null;
                    }

                    Texture2D tex = RenderCameraToTexture(cam, width, height);
                    if (tex != null)
                    {
                        tex = MaybeAutoScale(p, tex);
                        if (ot.Count > 1) DrawLabelOnTexture(tex, FrameLabel(ti));
                        captures.Add(tex);
                    }
                }
            }
            finally
            {
                cam.targetTexture = origRT;
            }

            if (captures.Count == 0)
                setResult(Response.Success($"Failed to capture from camera '{label}'."));
            else
                setResult(BuildImageResponse(StitchOrSingle(captures), customPath, customFile, label));
        }

        private static Camera FindCameraByName(string name)
        {
            var go = GameObject.Find(name);
            Camera cam = go?.GetComponent<Camera>();
            if (cam != null) return cam;
            foreach (Camera c in UnityEngine.Object.FindObjectsOfType<Camera>())
                if (c.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return c;
            return null;
        }

        // ==================== Game View RT capture ====================

        private static Texture2D CaptureGameViewTexture()
        {
            // Primary path plus fallbacks: read a play-view window's internal RenderTexture
            // via reflection. We probe every play-mode view (Game View, Device Simulator, HMI
            // Simulator, …) most-likely-live first; the first source that yields a non-black
            // frame wins. A black capture is discarded in favor of the next source, and if
            // every source is black we still return the first frame we got.
            try
            {
                Texture2D firstCapture = null; // first non-null (possibly black) capture, kept as last resort
                List<EditorWindow> windows = GetPlayModeViewWindows();
                if (windows.Count == 0)
                    CodelyLogger.LogWarning("[ManageScreenshot] No PlayModeView windows found to capture.");

                foreach (EditorWindow window in windows)
                {
                    if (window == null) continue;
                    RenderTexture srcRT = GetGameViewRT(window);
                    Texture2D tex = CaptureWindowRT(window);
                    bool black = tex == null || IsTextureBlack(tex);
                    string rtInfo = srcRT == null
                        ? "null"
                        : $"{srcRT.width}x{srcRT.height} {srcRT.format} created={srcRT.IsCreated()}";
                    string outcome = tex == null ? "no-texture" : (black ? "black" : "FRAME");
                    CodelyLogger.Log($"[ManageScreenshot] probe {window.GetType().FullName}: rt={rtInfo} -> {outcome}");
                    if (tex == null) continue;
                    if (!black) return tex;                        // a real frame — done
                    if (firstCapture == null) firstCapture = tex;  // remember, keep probing later sources
                    else UnityEngine.Object.DestroyImmediate(tex);
                }

                if (firstCapture != null) return firstCapture;
            }
            catch (Exception e)
            {
                CodelyLogger.LogWarning($"[ManageScreenshot] GameView RT reflection failed: {e.Message}. Falling back to camera rendering.");
            }

            // Fallback: render all active cameras using the actual Game View size
            Vector2 gameViewSize = Handles.GetMainGameViewSize();
            int fw = Mathf.Max((int)gameViewSize.x, 1);
            int fh = Mathf.Max((int)gameViewSize.y, 1);

            return RenderAllCamerasToTexture(fw, fh);
        }

        // Blits a play-view window's internal RenderTexture (Game View / Simulator /
        // HMISimulator) into a CPU-side, vertically-corrected Texture2D. Returns null when
        // the window is null or exposes no readable render texture.
        private static Texture2D CaptureWindowRT(EditorWindow window)
        {
            if (window == null) return null;
            RenderTexture rt = GetGameViewRT(window);
            if (rt == null || rt.width <= 0 || rt.height <= 0) return null;

            int w = rt.width;
            int h = rt.height;
            RenderTexture tmp = RenderTexture.GetTemporary(w, h, 0, rt.format);
            Graphics.Blit(rt, tmp);
            Texture2D tex = ReadRenderTexture(tmp, w, h);
            RenderTexture.ReleaseTemporary(tmp);
            if (tex != null) FlipTextureVertically(tex);
            return tex;
        }


        // CPU-side companion to IsRenderTextureBlack (which is gated to newer Unity and reads
        // from a RenderTexture). Samples a center block of an already-read texture and reports
        // true when every sampled pixel is (near-)black. Alpha is ignored so an opaque black
        // frame — the usual "nothing rendered" case for a play view — counts as black.
        private static bool IsTextureBlack(Texture2D tex)
        {
            if (tex == null) return true;
            int sampleW = Mathf.Min(32, tex.width);
            int sampleH = Mathf.Min(32, tex.height);
            if (sampleW <= 0 || sampleH <= 0) return true;
            int startX = (tex.width  - sampleW) / 2;
            int startY = (tex.height - sampleH) / 2;

            Color[] block = tex.GetPixels(startX, startY, sampleW, sampleH);
            foreach (Color c in block)
                if (c.r > 0.01f || c.g > 0.01f || c.b > 0.01f)
                    return false;
            return true;
        }

        // Returns the play-mode view windows to probe for a rendered frame.
        //
        // Discovery is by base-type NAME, not by a resolved Type. The Device/HMI simulators are
        // built into separate editor module assemblies (UnityEditor.DeviceSimulatorModule /
        // UnityEditor.HMISimulatorModule) whose assembly-qualified names differ across
        // Unity/Tuanjie versions, so Type.GetType("…,UnityEditor") returns null for them and
        // they get skipped — the original bug. Instead we enumerate every loaded EditorWindow
        // and keep the ones whose base-type chain contains UnityEditor.PlayModeView (Game View,
        // Device Simulator, HMI Simulator, …), regardless of which assembly each lives in.
        //
        // Ordering puts the most-likely-live source first: the focused window, then simulators
        // ahead of the plain Game View (so an active simulated frame wins over a stale Game
        // View RT). The caller still discards any black frame in favor of the next source.
        private static List<EditorWindow> GetPlayModeViewWindows()
        {
            var result = new List<EditorWindow>();

            EditorWindow[] all = Resources.FindObjectsOfTypeAll<EditorWindow>();
            foreach (EditorWindow w in all)
                if (w != null && IsPlayModeView(w.GetType()))
                    result.Add(w);

            EditorWindow focused = EditorWindow.focusedWindow;
            result.Sort((a, b) => RankPlayModeView(a, focused).CompareTo(RankPlayModeView(b, focused)));
            return result;
        }

        // True when any type in the chain is the internal UnityEditor.PlayModeView base class.
        private static bool IsPlayModeView(Type t)
        {
            for (; t != null && t != typeof(object); t = t.BaseType)
                if (t.Name == "PlayModeView")
                    return true;
            return false;
        }

        // Lower rank = probed earlier. Focused window first, then simulators, then Game View.
        private static int RankPlayModeView(EditorWindow w, EditorWindow focused)
        {
            if (w == focused) return 0;
            string n = w.GetType().Name;
            if (n.IndexOf("Simulator", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (n.IndexOf("GameView", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            return 3;
        }

        // Reads the RenderTexture a play-mode view last rendered into. Two storage schemes exist:
        //   GameView                  -> its own private "m_RenderTexture"
        //   Device SimulatorWindow    -> base PlayModeView's private "m_TargetTexture"
        //   HMI SimulatorWindow       -> NEITHER (Tuanjie only). It bypasses base RenderView() and
        //                                renders each screen into its DeviceView.PreviewTexture,
        //                                leaving m_TargetTexture null.
        // So we first try the RT fields (walking the type hierarchy, because PlayModeView's
        // m_TargetTexture is a *private base-class* field that GetField won't surface from the
        // derived type without DeclaredOnly + climbing BaseType). If that yields nothing — the
        // HMI case, compiled only on Tuanjie — we walk the window's visual tree for an element
        // exposing a RenderTexture "PreviewTexture", which is how the simulators publish frames.
        private static RenderTexture GetGameViewRT(EditorWindow gv)
        {
            if (gv == null) return null;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public |
                                       BindingFlags.Instance | BindingFlags.DeclaredOnly;
            string[] candidates = { "targetTexture", "m_TargetTexture", "m_RenderTexture" };

            foreach (string name in candidates)
            {
                for (Type t = gv.GetType(); t != null && t != typeof(object); t = t.BaseType)
                {
                    PropertyInfo pi = t.GetProperty(name, flags);
                    if (pi != null && pi.GetValue(gv) is RenderTexture prt && prt.IsCreated()) return prt;

                    FieldInfo fi = t.GetField(name, flags);
                    if (fi != null && fi.GetValue(gv) is RenderTexture frt && frt.IsCreated()) return frt;
                }
            }

#if TUANJIE_1_0_OR_NEWER
            // HMI Simulator is a Tuanjie-only window that renders into DeviceView.PreviewTexture
            // rather than the base PlayModeView render texture, so reach into its visual tree.
            // Tuanjie is 2022.3-based, so UNITY_2021_2_OR_NEWER (UIElements `using`) always holds.
            RenderTexture preview = FindPreviewTextureInVisualTree(gv.rootVisualElement);
            if (preview != null) return preview;
#endif
            return null;
        }

#if TUANJIE_1_0_OR_NEWER
        // Depth-first search of a window's UI Toolkit tree for an element that publishes its
        // rendered frame through a "RenderTexture PreviewTexture" property (both the Device and
        // HMI simulators' DeviceView do this). Used for the Tuanjie-only HMI Simulator, whose
        // frame never reaches the base PlayModeView render texture. Reflection keeps us
        // decoupled from the simulators' internal, separately-compiled DeviceView types.
        private static RenderTexture FindPreviewTextureInVisualTree(VisualElement element)
        {
            if (element == null) return null;

            PropertyInfo pi = element.GetType().GetProperty("PreviewTexture",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (pi != null && pi.PropertyType == typeof(RenderTexture)
                && pi.GetValue(element) is RenderTexture rt && rt.IsCreated())
                return rt;

            int childCount = element.hierarchy.childCount;
            for (int i = 0; i < childCount; i++)
            {
                RenderTexture found = FindPreviewTextureInVisualTree(element.hierarchy[i]);
                if (found != null) return found;
            }
            return null;
        }
#endif

        private static Texture2D RenderAllCamerasToTexture(int width, int height)
        {
            RenderTexture rt        = new RenderTexture(width, height, 24);
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.black);

            Camera[] cameras = Camera.allCameras;
            System.Array.Sort(cameras, (a, b) => a.depth.CompareTo(b.depth));

            foreach (Camera cam in cameras)
            {
                if (cam == null || !cam.enabled || !cam.gameObject.activeInHierarchy) continue;
                RenderTexture prev = cam.targetTexture;
                cam.targetTexture  = rt;
                cam.Render();
                cam.targetTexture  = prev;
            }

            Texture2D tex = ReadRenderTexture(rt, width, height);
            RenderTexture.active = prevActive;
            UnityEngine.Object.DestroyImmediate(rt);
            return tex;
        }

        // ==================== Camera helpers ====================

        private static Texture2D RenderCameraToTexture(Camera cam, int width, int height)
        {
            if (cam == null) return null;
            RenderTexture rt   = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.Default);
            RenderTexture prev = cam.targetTexture;
            cam.targetTexture  = rt;
            cam.Render();
            cam.targetTexture  = prev;
            Texture2D tex = ReadRenderTexture(rt, width, height);
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }

        private static Texture2D ReadRenderTexture(RenderTexture rt, int width, int height)
        {
            if (rt == null) return null;
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(width, height, TextureFormat.ARGB32, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            RenderTexture.active = prevActive;
            return tex;
        }

        private static void CopyCameraSettings(Camera src, Camera dst)
        {
            dst.clearFlags      = src.clearFlags;
            dst.backgroundColor = src.backgroundColor;
            dst.cullingMask     = src.cullingMask;
            dst.nearClipPlane   = src.nearClipPlane;
            dst.farClipPlane    = src.farClipPlane;
            dst.fieldOfView     = src.fieldOfView;
            dst.renderingPath   = src.renderingPath;
            dst.allowHDR        = src.allowHDR;
            dst.allowMSAA       = src.allowMSAA;
        }

        // ==================== Asset instantiation ====================

        private static GameObject InstantiateAsset(string relPath, Type assetType)
        {
            if (assetType == typeof(GameObject))
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(relPath);
                if (prefab == null) return null;
                return (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            }

            if (assetType == typeof(Mesh))
            {
                Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(relPath);
                if (mesh == null) return null;
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                return go;
            }

            if (assetType == typeof(Material))
            {
                Material mat = AssetDatabase.LoadAssetAtPath<Material>(relPath);
                if (mat == null) return null;
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.transform.localScale = Vector3.one * 3f;
                go.GetComponent<Renderer>().sharedMaterial = mat;
                return go;
            }

            if (assetType == typeof(Texture2D))
            {
                // Try as sprite first; fall back to a textured quad
                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(relPath);
                if (sprite != null)
                {
                    GameObject go = new GameObject("SpritePreview");
                    go.AddComponent<SpriteRenderer>().sprite = sprite;
                    return go;
                }

                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(relPath);
                if (tex == null) return null;
                GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Material mat = new Material(Shader.Find("Unlit/Texture")) { mainTexture = tex };
                quad.GetComponent<Renderer>().sharedMaterial = mat;
                return quad;
            }

            return null;
        }

        // ==================== Over-time helpers ====================

        private struct OverTimeParams
        {
            /// <summary>Total number of frames to capture.</summary>
            public int Count;
            /// <summary>
            /// How many simulation steps to advance between captures.
            /// 0 means no stepping (captures taken at same instant).
            /// </summary>
            public int StepsPerInterval;
        }

        /// <summary>
        /// Parses the <c>over_time</c> parameter.
        /// Accepted formats:
        ///   "N"    – capture N frames with no simulation stepping.
        ///   "NxS"  – capture N frames, stepping S simulation frames between each.
        /// Examples: "4", "4x1", "6x3"
        /// </summary>
        private static OverTimeParams ParseOverTime(JToken token)
        {
            if (token == null) return new OverTimeParams { Count = 1, StepsPerInterval = 0 };

            string raw = token.ToString().Trim();
            if (string.IsNullOrEmpty(raw)) return new OverTimeParams { Count = 1, StepsPerInterval = 0 };

            int separatorIdx = raw.IndexOf('x');
            if (separatorIdx < 0)
            {
                int.TryParse(raw, out int count);
                return new OverTimeParams { Count = Math.Max(1, count), StepsPerInterval = 0 };
            }
            else
            {
                int.TryParse(raw.Substring(0, separatorIdx), out int count);
                int.TryParse(raw.Substring(separatorIdx + 1), out int steps);
                return new OverTimeParams
                {
                    Count            = Math.Max(1, count),
                    StepsPerInterval = Math.Max(0, steps),
                };
            }
        }


        /// <summary>Short uppercase label for a zero-based frame index ("F0", "F1", …).</summary>
        private static string FrameLabel(int frameIndex) => "FRAME" + frameIndex;

        // ==================== View direction / framing ====================

        /// <summary>Expands "all" / "cardinal" shorthands into a list of named directions.</summary>
        private static List<string> GetViewList(string viewParam)
        {
            switch (viewParam)
            {
                case "all":      return new List<string> { "front", "back", "left", "right", "top", "bottom", "iso" };
                case "cardinal": return new List<string> { "front", "right", "top", "iso" };
                default:         return new List<string> { string.IsNullOrEmpty(viewParam) ? "current" : viewParam };
            }
        }

        private static string ViewAbbreviation(string view)
        {
            switch (view)
            {
                case "front":  return "FRONT";
                case "back":   return "BACK";
                case "left":   return "LEFT";
                case "right":  return "RIGHT";
                case "top":    return "TOP";
                case "bottom": return "BOTTOM";
                case "iso":    return "ISO";
                default:       return view.ToUpper();
            }
        }

        /// <summary>
        /// Positions and orients <paramref name="cam"/> so that <paramref name="bounds"/> fills
        /// the view from the given named direction, with a 20% padding margin.
        /// </summary>
        private static void PositionCameraForView(Camera cam, Bounds bounds, string view, bool orthographic)
        {
            cam.orthographic = orthographic;

            Vector3 center = bounds.center;
            Vector3 dir, up;

            switch (view)
            {
                case "front":  dir = Vector3.back;             up = Vector3.up;      break;
                case "back":   dir = Vector3.forward;          up = Vector3.up;      break;
                case "right":  dir = Vector3.left;             up = Vector3.up;      break;
                case "left":   dir = Vector3.right;            up = Vector3.up;      break;
                case "top":    dir = Vector3.down;             up = Vector3.forward; break;
                case "bottom": dir = Vector3.up;               up = Vector3.back;    break;
                case "iso":    dir = new Vector3(-1, -1, -1).normalized; up = Vector3.up; break;
                default: return; // "current" — caller is responsible for positioning
            }

            float maxExtent = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z);
            const float padding = 1.2f;

            if (orthographic)
            {
                cam.orthographicSize = maxExtent * padding;
                float dist = maxExtent * 3f + cam.nearClipPlane;
                cam.transform.position = center - dir * dist;
            }
            else
            {
                float fovRad = cam.fieldOfView * Mathf.Deg2Rad;
                float dist   = (maxExtent * padding) / Mathf.Tan(fovRad * 0.5f);
                dist = Mathf.Max(dist, cam.nearClipPlane + 0.1f);
                cam.transform.position = center - dir * dist;
            }

            cam.transform.rotation = Quaternion.LookRotation(dir, up);
        }

        private static Bounds GetSceneBounds()
        {
            Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            if (renderers.Length > 0)
            {
                Bounds b = renderers[0].bounds;
                foreach (Renderer r in renderers)
                    b.Encapsulate(r.bounds);
                return b;
            }
            return new Bounds(Vector3.zero, Vector3.one * 10f);
        }

        private static Bounds CalculateBounds(List<GameObject> objects)
        {
            if (objects == null || objects.Count == 0)
                return new Bounds(Vector3.zero, Vector3.one);

            Bounds b = new Bounds(objects[0].transform.position, Vector3.zero);
            foreach (GameObject go in objects)
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>())
                    b.Encapsulate(r.bounds);
            return b;
        }

        private static Bounds CalculateSingleObjectBounds(GameObject go)
        {
            Bounds b = new Bounds(go.transform.position, Vector3.zero);
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>())
                b.Encapsulate(r.bounds);
            // Ensure non-zero extents for objects without renderers (e.g. empty prefabs)
            if (b.size == Vector3.zero)
                b.size = Vector3.one;
            return b;
        }

        // ==================== GameObject path lookup ====================

        /// <summary>
        /// Finds a GameObject in the active scene by hierarchy path.
        /// Path segments are separated by "/" with no index notation in the string.
        /// <paramref name="targetIndex"/> selects the nth same-named sibling only at the
        /// final path segment (0-based, default 0). Intermediate segments always resolve
        /// to the first matching child.
        /// Examples:
        ///   FindGameObjectByPath("Player", 0)          – first root named "Player"
        ///   FindGameObjectByPath("Root/Enemies/Boss")  – nested, first match each level
        ///   FindGameObjectByPath("Root/Bullet", 2)     – 3rd child of Root named "Bullet"
        /// Returns null if any segment cannot be resolved.
        /// </summary>
        private static GameObject FindGameObjectByPath(string path, int targetIndex = 0)
        {
            if (string.IsNullOrEmpty(path)) return null;

            path = path.Replace('\\', '/').TrimStart('/');

            // Fast path: index 0 is handled directly by the built-in method which
            // supports "/" separated hierarchy paths and returns the first match.
            if (targetIndex == 0)
                return GameObject.Find(path);

            // index > 0: find the parent via built-in, then pick the nth same-named
            // sibling at the final segment manually.
            int lastSlash = path.LastIndexOf('/');
            string finalName = lastSlash < 0 ? path : path.Substring(lastSlash + 1);

            // Collect candidates at the final level
            List<Transform> candidates = new List<Transform>();
            if (lastSlash < 0)
            {
                // Root-level objects
                foreach (var r in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
                    if (r.name == finalName) candidates.Add(r.transform);
            }
            else
            {
                string parentPath = path.Substring(0, lastSlash);
                GameObject parent = GameObject.Find(parentPath);
                if (parent == null) return null;
                foreach (Transform child in parent.transform)
                    if (child.name == finalName) candidates.Add(child);
            }

            return targetIndex < candidates.Count ? candidates[targetIndex].gameObject : null;
        }

        // ==================== Image encoding / saving ====================

        private static object BuildImageResponse(
            Texture2D tex,
            string customPath, string customFilename,
            string prefix, string warning = null)
        {
            try
            {
                byte[] bytes = tex.EncodeToPNG();

                string fn = !string.IsNullOrEmpty(customFilename)
                    ? (customFilename.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        ? customFilename
                        : customFilename + ".png")
                    : $"{prefix}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";

                string dir = !string.IsNullOrEmpty(customPath)
                    ? customPath
                    : Path.Combine(Path.GetDirectoryName(Application.dataPath), "screenshots");

                Directory.CreateDirectory(dir);
                string savePath = Path.Combine(dir, fn);
                File.WriteAllBytes(savePath, bytes);
                CodelyLogger.Log($"[ManageScreenshot] Saved: {savePath}");

                int tw = tex.width, th = tex.height;
                UnityEngine.Object.DestroyImmediate(tex);
                return Response.Success("Screenshot captured successfully.", new
                {
                    path    = savePath,
                    width   = tw,
                    height  = th,
                    warning = string.IsNullOrEmpty(warning) ? null : warning,
                });
            }
            catch (Exception e)
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                return Response.Success($"Failed to encode/save screenshot: {e.Message}");
            }
        }

        // ==================== Texture utilities ====================

        // When neither width nor height is supplied by the caller, captured textures
        // are downsampled so their longest edge equals this value.
        private const int AutoScaleMaxEdge = 480;

        private static Texture2D MaybeAutoScale(JObject p, Texture2D tex)
        {
            if (tex == null) return tex;
            int? w = p?["width"]?.ToObject<int?>();
            int? h = p?["height"]?.ToObject<int?>();
            if (w != null || h != null) return tex;
            return ScaleTextureToMaxEdge(tex, AutoScaleMaxEdge);
        }

        private static Texture2D ScaleTextureToMaxEdge(Texture2D src, int maxEdge)
        {
            if (src == null) return null;
            int srcW = src.width;
            int srcH = src.height;
            int longest = Mathf.Max(srcW, srcH);
            if (longest <= maxEdge) return src;

            float scale = (float)maxEdge / longest;
            int dstW = Mathf.Max(1, Mathf.RoundToInt(srcW * scale));
            int dstH = Mathf.Max(1, Mathf.RoundToInt(srcH * scale));

            RenderTexture rt = RenderTexture.GetTemporary(dstW, dstH, 0, RenderTextureFormat.ARGB32);
            rt.filterMode = FilterMode.Bilinear;
            FilterMode prevFilter = src.filterMode;
            src.filterMode = FilterMode.Bilinear;
            RenderTexture prevActive = RenderTexture.active;

            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            Texture2D scaled = new Texture2D(dstW, dstH, TextureFormat.ARGB32, false);
            scaled.ReadPixels(new Rect(0, 0, dstW, dstH), 0, 0);
            scaled.Apply();

            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);
            src.filterMode = prevFilter;
            UnityEngine.Object.DestroyImmediate(src);
            return scaled;
        }

        private static void FlipTextureVertically(Texture2D tex)
        {
            if (tex == null || tex.height <= 1) return;
            int w = tex.width, h = tex.height;
            Color[] pixels = tex.GetPixels();
            for (int y = 0; y < h / 2; y++)
            {
                int top = y * w, bottom = (h - 1 - y) * w;
                for (int x = 0; x < w; x++)
                {
                    Color tmp          = pixels[top + x];
                    pixels[top + x]    = pixels[bottom + x];
                    pixels[bottom + x] = tmp;
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
        }

        private static Texture2D StitchOrSingle(List<Texture2D> textures)
        {
            if (textures.Count == 1) return textures[0];

            int cols  = Mathf.CeilToInt(Mathf.Sqrt(textures.Count));
            int rows  = Mathf.CeilToInt((float)textures.Count / cols);
            int cellW = textures[0].width;
            int cellH = textures[0].height;

            Texture2D result = new Texture2D(cellW * cols, cellH * rows, TextureFormat.RGB24, false);

            for (int i = 0; i < textures.Count; i++)
            {
                int col = i % cols;
                int row = rows - 1 - (i / cols); // Unity Y=0 is bottom
                if (textures[i] != null)
                    result.SetPixels(col * cellW, row * cellH, cellW, cellH, textures[i].GetPixels());
            }

            result.Apply();

            foreach (var t in textures)
                if (t != null) UnityEngine.Object.DestroyImmediate(t);

            return result;
        }

        // Minimal 5×7 bitmap font for label rendering on captured textures.
        // Each entry contains 7 row bitmasks; bit 4 = leftmost pixel, bit 0 = rightmost.
        private static readonly Dictionary<char, int[]> s_BitmapFont = new Dictionary<char, int[]>
        {
            // Letters
            ['A'] = new[] { 14, 17, 17, 31, 17, 17,  0 },
            ['B'] = new[] { 30, 17, 17, 30, 17, 17, 30 },
            ['C'] = new[] { 14, 17, 16, 16, 16, 17, 14 },
            ['D'] = new[] { 30, 17, 17, 17, 17, 17, 30 },
            ['E'] = new[] { 31, 16, 16, 28, 16, 16, 31 },
            ['F'] = new[] { 31, 16, 30, 16, 16, 16,  0 },
            ['G'] = new[] { 15, 16, 16, 19, 17, 14,  0 },
            ['H'] = new[] { 17, 17, 31, 17, 17, 17,  0 },
            ['I'] = new[] { 31,  4,  4,  4,  4, 31,  0 },
            ['K'] = new[] { 17, 18, 20, 24, 20, 18, 17 },
            ['L'] = new[] { 16, 16, 16, 16, 16, 16, 31 },
            ['M'] = new[] { 17, 27, 21, 17, 17, 17,  0 },
            ['N'] = new[] { 17, 25, 21, 19, 17, 17,  0 },
            ['O'] = new[] { 14, 17, 17, 17, 17, 14,  0 },
            ['P'] = new[] { 30, 17, 17, 30, 16, 16,  0 },
            ['R'] = new[] { 30, 17, 17, 30, 20, 18,  0 },
            ['S'] = new[] { 14, 17, 16, 14,  1, 17, 14 },
            ['T'] = new[] { 31,  4,  4,  4,  4,  4,  0 },
            ['-'] = new[] {  0,  0,  0, 31,  0,  0,  0 },
            // Digits (for frame labels: F0, F1, …)
            ['0'] = new[] { 14, 17, 19, 21, 25, 17, 14 },
            ['1'] = new[] {  4, 12,  4,  4,  4,  4, 14 },
            ['2'] = new[] { 14, 17,  1,  6,  8, 16, 31 },
            ['3'] = new[] { 14, 17,  1,  6,  1, 17, 14 },
            ['4'] = new[] {  2,  6, 10, 18, 31,  2,  2 },
            ['5'] = new[] { 31, 16, 30,  1,  1, 17, 14 },
            ['6'] = new[] {  6,  8, 16, 30, 17, 17, 14 },
            ['7'] = new[] { 31,  1,  2,  4,  8,  8,  8 },
            ['8'] = new[] { 14, 17, 17, 14, 17, 17, 14 },
            ['9'] = new[] { 14, 17, 17, 15,  1,  2, 12 },
        };

        private static void DrawLabelOnTexture(Texture2D tex, string label)
        {
            if (tex == null || string.IsNullOrEmpty(label)) return;

            const int charW = 5, charH = 7, scale = 7, spacing = 1, pad = 4;
            int totalW = label.Length * (charW + spacing) * scale - spacing * scale + pad * 2;
            int totalH = charH * scale + pad * 2;

            Color[] pixels = tex.GetPixels();
            int texW = tex.width, texH = tex.height;
            int bgX = pad, bgY = texH - totalH - pad;

            // Darken background box
            for (int y = bgY; y < bgY + totalH; y++)
                for (int x = bgX; x < bgX + totalW; x++)
                {
                    if (x < 0 || x >= texW || y < 0 || y >= texH) continue;
                    Color c = pixels[y * texW + x];
                    pixels[y * texW + x] = new Color(c.r * 0.25f, c.g * 0.25f, c.b * 0.25f, 1f);
                }

            // Draw characters in white
            for (int ci = 0; ci < label.Length; ci++)
            {
                if (!s_BitmapFont.TryGetValue(label[ci], out int[] rows)) continue;
                int charOX = bgX + pad + ci * (charW + spacing) * scale;
                int charOY = bgY + pad;

                for (int row = 0; row < charH; row++)
                {
                    int bits = rows[row];
                    int py   = charOY + (charH - 1 - row) * scale;
                    for (int col = 0; col < charW; col++)
                    {
                        if (((bits >> (charW - 1 - col)) & 1) == 0) continue;
                        int px = charOX + col * scale;
                        for (int sy = 0; sy < scale; sy++)
                            for (int sx = 0; sx < scale; sx++)
                            {
                                int fx = px + sx, fy = py + sy;
                                if (fx >= 0 && fx < texW && fy >= 0 && fy < texH)
                                    pixels[fy * texW + fx] = Color.white;
                            }
                    }
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
        }

        // ==================== Path utilities ====================

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            path = path.Replace('\\', '/');

            string dataPath   = Application.dataPath.Replace('\\', '/');
            string projectRoot = dataPath.Substring(0, dataPath.LastIndexOf('/'));

            if (path.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                path = path.Substring(projectRoot.Length).TrimStart('/');

            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                path = "Assets/" + path.TrimStart('/');

            return path;
        }

        // ==================== UIToolkit helpers ====================

#if UNITY_2021_2_OR_NEWER
        // Samples a small grid of pixels from the center of the RT to decide whether
        // the panel has rendered anything yet. Returns true when every sampled pixel
        // is opaque black (i.e. nothing has been drawn).
        private static bool IsRenderTextureBlack(RenderTexture rt)
        {
            if (rt == null) return true;
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            // Read a 4×4 block from the center of the texture.
            int sampleW = Mathf.Min(4, rt.width);
            int sampleH = Mathf.Min(4, rt.height);
            int startX  = (rt.width  - sampleW) / 2;
            int startY  = (rt.height - sampleH) / 2;

            Texture2D probe = new Texture2D(sampleW, sampleH, TextureFormat.ARGB32, false);
            probe.ReadPixels(new Rect(startX, startY, sampleW, sampleH), 0, 0);
            probe.Apply();
            RenderTexture.active = prev;

            Color32[] pixels = probe.GetPixels32();
            UnityEngine.Object.DestroyImmediate(probe);

            foreach (Color32 c in pixels)
            {
                // If any pixel is non-black or has alpha > 0, the panel has rendered.
                if (c.r > 2 || c.g > 2 || c.b > 2 || c.a > 2)
                    return false;
            }
            return true;
        }

        // Unity ships a default runtime theme under the com.unity.ui package. When the
        // UI Builder's per-uxml theme map can't be located (Builder package not present,
        // or this uxml hasn't been opened in Builder), we fall back to this so text and
        // controls render with sensible defaults instead of an empty stylesheet.
        private static ThemeStyleSheet GetDefaultRuntimeTheme()
        {
            string[] candidates =
            {
                "Packages/com.unity.ui/PackageResources/StyleSheets/Generated/UnityDefaultRuntimeTheme.tss",
                "Packages/com.unity.toolkits.ui/PackageResources/StyleSheets/Generated/UnityDefaultRuntimeTheme.tss",
                "UI Toolkit/UnityDefaultRuntimeTheme.tss",
            };
            foreach (string p in candidates)
            {
                var tss = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(p);
                if (tss != null) return tss;
            }
            return null;
        }

        private static ThemeStyleSheet GetUIBuilderTheme(string uxmlPath)
        {
            Type builderDocType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                builderDocType = asm.GetType("Unity.UI.Builder.BuilderDocument");
                if (builderDocType != null) break;
            }
            if (builderDocType == null) return null;

            UnityEngine.Object[] instances = Resources.FindObjectsOfTypeAll(builderDocType);
            if (instances.Length == 0) return null;

            var instance = instances[0];
            BindingFlags nonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
            BindingFlags pub = BindingFlags.Instance | BindingFlags.Public;

            if (uxmlPath != null)
            {
                var listField = builderDocType.GetField("m_SavedBuilderUxmlToThemeStyleSheetList", nonPublic);
                if (listField?.GetValue(instance) is IList list)
                {
                    foreach (object entry in list)
                    {
                        Type entryType = entry.GetType();
                        if (entryType.GetField("UxmlURI", pub)?.GetValue(entry) is string uxmlUri
                            && uxmlUri.Contains(uxmlPath)
                            && entryType.GetField("ThemeStyleSheetURI", pub)?.GetValue(entry) is string themeUri
                            && themeUri.StartsWith("project://database/"))
                        {
                            int qIdx = themeUri.IndexOf('?');
                            if (qIdx < 0) qIdx = themeUri.Length;
                            string assetPath = Uri.UnescapeDataString(
                                themeUri.Substring("project://database/".Length, qIdx - "project://database/".Length));
                            ThemeStyleSheet tss = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(assetPath);
                            if (tss != null) return tss;
                        }
                    }
                }
            }

            return builderDocType.GetField("m_CurrentCanvasThemeStyleSheetReference", nonPublic)
                ?.GetValue(instance) as ThemeStyleSheet;
        }

        // ----- Why this exists (bisected 2026-05-14, verified on this codebase) -----
        // In Play Mode, a UIDocument's panel renders via UIElementsRuntimeUtility's
        // player-loop hook. In Edit Mode that hook is dormant, so a PanelSettings with
        // targetTexture set will lay out correctly but never flush draw commands —
        // the RT stays at its clear color forever. Neither
        //   UIElementsRuntimeUtility.UpdateRuntimePanels()   (runs layout only)
        //   UIElementsRuntimeUtility.RepaintOverlayPanels()  (no-op in edit mode here)
        //   PanelSettings.GetOrCreatePanel()                 (panel already exists)
        //   gameView.Repaint()                               (works, but redundant)
        // is required.
        //
        // Unity 2021/2022:
        //   BaseRuntimePanel.Repaint(Event) runs both the visual-tree pass AND the
        //   synchronous draw submission, so calling that one method is enough.
        //
        // Unity 6 (6000.x):
        //   The pipeline was split. Repaint(Event) now only marks UIR's render chain
        //   as needing work; the draw submission moved into a separate zero-arg
        //   Render()-family method that the player loop normally calls. In Edit Mode
        //   nothing calls it, AND a foregrounded editor frame is required for the
        //   render thread to actually flush UIR's mesh chunks. So on Unity 6 we must
        //   ALSO invoke DirectPanelRender(), which is the synchronous "do the draw
        //   right now on this thread" entry that bypasses focus / player-loop gates.
        //
        // The UpdateRuntimePanels call is kept as a cheap defensive step so Yoga's
        // layout pass can settle if a stylesheet loads asynchronously.
        // ---------------------------------------------------------------------------

        private static MethodInfo s_UpdateRuntimePanelsMethod;
        private static bool       s_UpdateRuntimePanelsSearched;

        // Cached per panel type — Unity 6 added a zero-arg synchronous draw entry on
        // BaseRuntimePanel; the exact name has churned across versions, so we resolve
        // by signature (parameterless instance method) and cache the first hit.
        private static MethodInfo s_DirectRenderMethod;
        private static Type       s_DirectRenderOwnerType;

        private static void EnsureUpdateRuntimePanelsLoaded()
        {
            if (s_UpdateRuntimePanelsSearched) return;
            s_UpdateRuntimePanelsSearched = true;
            try
            {
                Type runtimeUtilType = typeof(VisualElement).Assembly
                    .GetType("UnityEngine.UIElements.UIElementsRuntimeUtility");
                s_UpdateRuntimePanelsMethod = runtimeUtilType?.GetMethod(
                    "UpdateRuntimePanels",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            }
            catch { /* type not available in this Unity version */ }
        }

        // Walks up the panel's type hierarchy to find Repaint(Event) and invokes it
        // with a null event. On 2021/2022 this also issues draws; on Unity 6 this
        // only requests them — DirectPanelRender below performs the actual submission.
        private static void DirectPanelRepaint(IPanel panel)
        {
            if (panel == null) return;
            Type t = panel.GetType();
            while (t != null)
            {
                MethodInfo repaint = t.GetMethod("Repaint",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(Event) }, null);
                if (repaint != null)
                {
                    try { repaint.Invoke(panel, new object[] { null }); return; }
                    catch { /* try base class */ }
                }
                t = t.BaseType;
            }
        }

        // Synchronously flushes the panel's UIR command list to PanelSettings.targetTexture.
        // No-op on Unity versions that don't have a parameterless render method (2021/2022
        // — there DirectPanelRepaint already does the draw). On Unity 6 this is what
        // removes the dependency on the editor being the foreground OS window: the call
        // executes inline on the calling thread instead of waiting for a focus-throttled
        // editor frame submission.
        private static void DirectPanelRender(IPanel panel)
        {
            if (panel == null) return;

            Type panelType = panel.GetType();
            if (s_DirectRenderMethod == null || s_DirectRenderOwnerType != panelType)
            {
                s_DirectRenderOwnerType = panelType;
                s_DirectRenderMethod    = null;

                // Names seen across UI Toolkit revisions. First parameterless match wins.
                // We deliberately exclude "Repaint" — that one's handled separately and
                // taking a parameterless overload would skip its Event-driven setup.
                string[] candidates = { "Render", "RenderImmediately", "DoRender", "DoRepaint" };

                Type t = panelType;
                while (t != null && s_DirectRenderMethod == null)
                {
                    foreach (string name in candidates)
                    {
                        MethodInfo m = t.GetMethod(name,
                            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                            null, Type.EmptyTypes, null);
                        if (m != null) { s_DirectRenderMethod = m; break; }
                    }
                    t = t.BaseType;
                }
            }

            if (s_DirectRenderMethod != null)
            {
                try { s_DirectRenderMethod.Invoke(panel, null); } catch { /* ignore */ }
            }
        }

        private static void TryForceUIDocumentRepaint(UIDocument uiDoc)
        {
            if (uiDoc == null) return;
            try
            {
                EnsureUpdateRuntimePanelsLoaded();

                VisualElement root = uiDoc.rootVisualElement;
                if (root != null) root.MarkDirtyRepaint();

                // Layout pass (lets Yoga settle if anything is still resolving).
                try { s_UpdateRuntimePanelsMethod?.Invoke(null, null); } catch { }

                IPanel panel = root?.panel;
                // 2021/2022: this also draws. Unity 6: this only requests a draw.
                DirectPanelRepaint(panel);
                // Unity 6 only: actually submit the draw on this thread. No-op elsewhere.
                DirectPanelRender(panel);
            }
            catch { /* Ignore — reflection targets may not exist in all Unity versions */ }
        }
#endif

        // ==================== Public API (backward-compatible) ====================

        public static void CaptureScreenshotMenuItem() => CaptureGameViewAndSave();

        public static string CaptureGameViewAndSave(
            string customPath = null, string customFilename = null,
            int? width = null, int? height = null)
        {
            Texture2D tex = CaptureGameViewTexture();
            if (tex == null) return null;
            FlipTextureVertically(tex);

            string fn  = !string.IsNullOrEmpty(customFilename)
                ? (customFilename.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? customFilename : customFilename + ".png")
                : $"GameView-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";

            string dir = !string.IsNullOrEmpty(customPath)
                ? customPath
                : Path.Combine(Path.GetDirectoryName(Application.dataPath), "Screenshots");

            Directory.CreateDirectory(dir);
            string savePath = Path.Combine(dir, fn);
            File.WriteAllBytes(savePath, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            CodelyLogger.Log($"[ManageScreenshot] Saved: {savePath}");
            return savePath;
        }

        public static string CaptureAndSave(
            string customPath = null, string customFilename = null,
            int? width = null, int? height = null,
            Camera specificCamera = null, string cameraPrefix = null)
        {
            Camera cam = specificCamera ?? Camera.main;
            if (cam == null) { CodelyLogger.LogError("[ManageScreenshot] No camera found!"); return null; }

            int w = width  ?? 1920;
            int h = height ?? 1080;
            Texture2D tex = RenderCameraToTexture(cam, w, h);
            if (tex == null) return null;

            byte[] bytes = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);

            string prefix = cameraPrefix ?? (specificCamera != null ? specificCamera.name : "MainCamera");
            string fn = !string.IsNullOrEmpty(customFilename)
                ? (customFilename.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? customFilename : customFilename + ".png")
                : $"{prefix}-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";

            string dir = !string.IsNullOrEmpty(customPath)
                ? customPath
                : Path.Combine(Path.GetDirectoryName(Application.dataPath), "Screenshots");

            Directory.CreateDirectory(dir);
            string savePath = Path.Combine(dir, fn);
            File.WriteAllBytes(savePath, bytes);
            CodelyLogger.Log($"[ManageScreenshot] Saved: {savePath}");
            return savePath;
        }
    }
}
