#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Unity の Project ウィンドウに、選択中アセットのパス表示・コピー・直接入力・パンくずナビゲーションを追加する Editor 拡張です。
/// フォルダ階層をクリックして移動でき、Ctrl + L から絶対パスまたは相対パスを指定して対象アセットへ移動できます。
/// 別のアセットを選択すると、自動的に通常の相対パス表示へ戻ります。
/// </summary>

namespace Rin0700.Tools
{
    [InitializeOnLoad]
    public static class ProjectPathTool
    {
        private const string ElementName = "gugenka-project-path-copy";
        private const string PathFieldControlName = "gugenka-project-path-field";
        private const float BarHeight = 20f;
        private const float OpenButtonWidth = 48f;
        private const float CopyButtonWidth = 48f;
        private const double BrowserScanInterval = 1d;

        private static readonly Type ProjectBrowserType =
            typeof(EditorWindow).Assembly.GetType("UnityEditor.ProjectBrowser");

        private static readonly List<ProjectCopyState> Buttons = new List<ProjectCopyState>();
        private static double nextBrowserScanTime;
        private static GUIStyle pathSegmentStyle;

        static ProjectPathTool()
        {
            EditorApplication.delayCall += EnsureCopyButtons;
            EditorApplication.update += PollForProjectBrowsers;
            Selection.selectionChanged += HandleSelectionChanged;
            EditorApplication.projectChanged += RepaintButtons;
            AssemblyReloadEvents.beforeAssemblyReload += DetachAll;
        }

        private sealed class ProjectCopyState
        {
            public EditorWindow browser;
            public IMGUIContainer container;
            public Vector2 scrollPosition;
            public string currentAssetPath = "Assets";
            public string pathInput = string.Empty;
            public bool isEditingPath;
            public bool requestPathFocus;
        }

        private static void PollForProjectBrowsers()
        {
            if (EditorApplication.timeSinceStartup < nextBrowserScanTime)
            {
                return;
            }

            nextBrowserScanTime = EditorApplication.timeSinceStartup + BrowserScanInterval;
            EnsureCopyButtons();
        }

        private static void EnsureCopyButtons()
        {
            RemoveClosedProjectBrowsers();

            if (ProjectBrowserType == null)
            {
                return;
            }

            UnityEngine.Object[] browsers = Resources.FindObjectsOfTypeAll(ProjectBrowserType);
            for (int i = 0; i < browsers.Length; i++)
            {
                if (!(browsers[i] is EditorWindow browser) || HasButton(browser))
                {
                    continue;
                }

                AttachButton(browser);
            }
        }

        private static bool HasButton(EditorWindow browser)
        {
            for (int i = 0; i < Buttons.Count; i++)
            {
                if (Buttons[i].browser == browser)
                {
                    return true;
                }
            }

            return false;
        }

        private static void AttachButton(EditorWindow browser)
        {
            VisualElement root = browser.rootVisualElement;
            root.Q<VisualElement>(ElementName)?.RemoveFromHierarchy();

            var state = new ProjectCopyState
            {
                browser = browser,
                currentAssetPath = GetSelectedAssetPath()
            };

            state.container = new IMGUIContainer(() => DrawPathBar(state))
            {
                name = ElementName
            };
            state.container.style.position = Position.Absolute;
            state.container.style.left = 0f;
            state.container.style.right = 0f;
            state.container.style.bottom = 0f;
            state.container.style.height = BarHeight;

            root.Add(state.container);
            Buttons.Add(state);
            browser.Repaint();
        }

        private static void DrawPathBar(ProjectCopyState state)
        {
            HandlePathEditShortcut(state);

            string assetPath = GetSelectedAssetPath();
            if (!state.isEditingPath && !string.IsNullOrEmpty(assetPath) && assetPath != state.currentAssetPath)
            {
                state.currentAssetPath = assetPath;
                state.scrollPosition = Vector2.zero;
            }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar, GUILayout.ExpandWidth(true)))
            {
                state.scrollPosition = GUILayout.BeginScrollView(
                    state.scrollPosition,
                    false,
                    false,
                    GUIStyle.none,
                    GUIStyle.none,
                    GUIStyle.none,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (state.isEditingPath)
                    {
                        DrawPathInput(state);
                    }
                    else
                    {
                        DrawBreadcrumbs(state.currentAssetPath, state.browser);
                        DrawPathEditArea(state);
                    }
                }

                GUILayout.EndScrollView();

                bool canOpen = !string.IsNullOrEmpty(state.currentAssetPath) &&
                               !AssetDatabase.IsValidFolder(state.currentAssetPath) &&
                               AssetDatabase.LoadMainAssetAtPath(state.currentAssetPath) != null;

                using (new EditorGUI.DisabledScope(!canOpen))
                {
                    var content = new GUIContent("Open", "Open selected asset");
                    if (GUILayout.Button(
                        content,
                        EditorStyles.toolbarButton,
                        GUILayout.Width(OpenButtonWidth),
                        GUILayout.Height(18f)))
                    {
                        OpenAsset(state.currentAssetPath);
                    }
                }

                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(state.currentAssetPath)))
                {
                    var content = new GUIContent("Copy", "Copy selected asset path");
                    if (GUILayout.Button(
                        content,
                        EditorStyles.toolbarButton,
                        GUILayout.Width(CopyButtonWidth),
                        GUILayout.Height(18f)))
                    {
                        EditorGUIUtility.systemCopyBuffer = state.currentAssetPath;
                        state.browser.ShowNotification(new GUIContent($"Copied: {state.currentAssetPath}"));
                    }
                }
            }
        }

        private static void HandlePathEditShortcut(ProjectCopyState state)
        {
            Event currentEvent = Event.current;
            if (currentEvent.type != EventType.KeyDown ||
                !currentEvent.control ||
                currentEvent.keyCode != KeyCode.L)
            {
                return;
            }

            BeginPathEditing(state);
            currentEvent.Use();
        }

        private static void DrawPathEditArea(ProjectCopyState state)
        {
            Rect editArea = GUILayoutUtility.GetRect(
                8f,
                18f,
                GUILayout.ExpandWidth(true),
                GUILayout.Height(18f));

            EditorGUIUtility.AddCursorRect(editArea, MouseCursor.Text);

            Event currentEvent = Event.current;
            if (currentEvent.type == EventType.MouseDown &&
                currentEvent.button == 0 &&
                editArea.Contains(currentEvent.mousePosition))
            {
                BeginPathEditing(state);
                currentEvent.Use();
            }
        }

        private static void DrawPathInput(ProjectCopyState state)
        {
            Event currentEvent = Event.current;
            bool handleKey = currentEvent.type == EventType.KeyDown &&
                             GUI.GetNameOfFocusedControl() == PathFieldControlName;
            KeyCode keyCode = currentEvent.keyCode;

            GUI.SetNextControlName(PathFieldControlName);
            state.pathInput = EditorGUILayout.TextField(
                state.pathInput,
                EditorStyles.textField,
                GUILayout.ExpandWidth(true),
                GUILayout.Height(18f));

            if (state.requestPathFocus)
            {
                EditorGUI.FocusTextInControl(PathFieldControlName);
                state.requestPathFocus = false;
            }

            if (!handleKey)
            {
                return;
            }

            if (keyCode == KeyCode.Escape)
            {
                EndPathEditing(state);
                if (currentEvent.type != EventType.Used)
                {
                    currentEvent.Use();
                }

                return;
            }

            if (keyCode != KeyCode.Return && keyCode != KeyCode.KeypadEnter)
            {
                return;
            }

            if (TryNavigateToPath(state.pathInput, state.browser, out string assetPath))
            {
                state.currentAssetPath = assetPath;
                EndPathEditing(state);
            }
            else
            {
                state.browser.ShowNotification(new GUIContent($"Path not found: {state.pathInput}"));
                state.requestPathFocus = true;
            }

            if (currentEvent.type != EventType.Used)
            {
                currentEvent.Use();
            }
        }

        private static void BeginPathEditing(ProjectCopyState state)
        {
            if (!state.isEditingPath)
            {
                state.pathInput = GetAbsoluteProjectPath(state.currentAssetPath);
                state.isEditingPath = true;
            }

            state.requestPathFocus = true;
            state.scrollPosition = Vector2.zero;
            state.container.Focus();
            state.container.MarkDirtyRepaint();
        }

        private static void EndPathEditing(ProjectCopyState state)
        {
            state.isEditingPath = false;
            state.requestPathFocus = false;
            GUI.FocusControl(null);
            state.container.MarkDirtyRepaint();
            state.browser.Repaint();
        }

        private static bool TryNavigateToPath(
            string input,
            EditorWindow browser,
            out string assetPath)
        {
            assetPath = NormalizeAssetPath(input);
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            if (AssetDatabase.IsValidFolder(assetPath))
            {
                FocusFolder(assetPath, browser);
                return true;
            }

            if (AssetDatabase.LoadMainAssetAtPath(assetPath) == null)
            {
                return false;
            }

            FocusAsset(assetPath, browser);
            return true;
        }

        private static string NormalizeAssetPath(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            string path = input.Trim().Trim('"').Replace('\\', '/');
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;

            try
            {
                if (Path.IsPathRooted(path))
                {
                    if (string.IsNullOrEmpty(projectRoot))
                    {
                        return string.Empty;
                    }

                    string fullProjectRoot = Path.GetFullPath(projectRoot)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                        Path.DirectorySeparatorChar;
                    string fullInputPath = Path.GetFullPath(path);

                    if (!fullInputPath.StartsWith(fullProjectRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        return string.Empty;
                    }

                    path = fullInputPath.Substring(fullProjectRoot.Length).Replace('\\', '/');
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is NotSupportedException ||
                exception is PathTooLongException)
            {
                return string.Empty;
            }

            path = path.Trim('/');
            if (path.StartsWith("./", StringComparison.Ordinal))
            {
                path = path.Substring(2);
            }

            if (!string.Equals(path, "Assets", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(path, "Packages", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                path = "Assets/" + path;
            }

            return path;
        }

        private static string GetAbsoluteProjectPath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(assetPath))
            {
                return assetPath ?? string.Empty;
            }

            return Path.GetFullPath(Path.Combine(
                projectRoot,
                assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static void DrawBreadcrumbs(string assetPath, EditorWindow browser)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            string[] segments = assetPath.Split('/');
            string accumulatedPath = string.Empty;
            GUIStyle segmentStyle = GetPathSegmentStyle();

            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0)
                {
                    GUIContent separator = new GUIContent("/");
                    GUILayout.Label(
                        separator,
                        segmentStyle,
                        GUILayout.Width(segmentStyle.CalcSize(separator).x));
                    accumulatedPath += "/";
                }

                accumulatedPath += segments[i];
                string pathForButton = accumulatedPath;
                bool isFolder = AssetDatabase.IsValidFolder(pathForButton);
                GUIContent content = new GUIContent(segments[i], pathForButton);
                float width = segmentStyle.CalcSize(content).x;

                bool clicked = GUILayout.Button(content, segmentStyle, GUILayout.Width(width));
                EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);

                if (!clicked)
                {
                    continue;
                }

                if (isFolder)
                {
                    FocusFolder(pathForButton, browser);
                }
                else
                {
                    FocusAsset(pathForButton, browser);
                }
            }
        }

        private static GUIStyle GetPathSegmentStyle()
        {
            if (pathSegmentStyle != null)
            {
                return pathSegmentStyle;
            }

            pathSegmentStyle = new GUIStyle(EditorStyles.label)
            {
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                alignment = TextAnchor.MiddleLeft,
                fixedHeight = 18f
            };

            return pathSegmentStyle;
        }

        private static void OpenAsset(string assetPath)
        {
            UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
            {
                Debug.LogWarning($"Project Path Bar: Asset not found: {assetPath}");
                return;
            }

            if (!AssetDatabase.OpenAsset(asset))
            {
                Debug.LogWarning($"Project Path Bar: No editor could open: {assetPath}");
            }
        }

        private static void FocusFolder(string folderPath, EditorWindow browser)
        {
            DefaultAsset folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(folderPath);
            if (folder == null)
            {
                Debug.LogWarning($"Project Path Bar: Folder not found: {folderPath}");
                return;
            }

            Selection.activeObject = folder;
            EditorGUIUtility.PingObject(folder);
            TryShowFolderContents(folder.GetInstanceID(), browser);
        }

        private static void FocusAsset(string assetPath, EditorWindow browser)
        {
            UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
            {
                Debug.LogWarning($"Project Path Bar: Asset not found: {assetPath}");
                return;
            }

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            browser.Focus();
            browser.Repaint();
        }

        private static void TryShowFolderContents(int folderInstanceId, EditorWindow browser)
        {
            if (ProjectBrowserType == null || browser == null || !IsTwoColumnMode(browser))
            {
                return;
            }

            MethodInfo method = ProjectBrowserType.GetMethod(
                "ShowFolderContents",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(int), typeof(bool) },
                null);

            try
            {
                method?.Invoke(browser, new object[] { folderInstanceId, true });
            }
            catch (TargetInvocationException)
            {
                // Selection and Ping remain the fallback if Unity changes this internal API.
            }
            catch (ArgumentException)
            {
                // Selection and Ping remain the fallback if Unity changes this internal API.
            }

            browser.Focus();
            browser.Repaint();
        }

        private static bool IsTwoColumnMode(EditorWindow browser)
        {
            FieldInfo viewModeField = ProjectBrowserType.GetField(
                "m_ViewMode",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (viewModeField == null)
            {
                return false;
            }

            object viewMode = viewModeField.GetValue(browser);
            return viewMode != null && string.Equals(
                viewMode.ToString(),
                "TwoColumns",
                StringComparison.OrdinalIgnoreCase);
        }

        private static void HandleSelectionChanged()
        {
            string selectedAssetPath = GetSelectedAssetPath();

            if (string.IsNullOrEmpty(selectedAssetPath))
            {
                RepaintButtons();
                return;
            }

            for (int i = 0; i < Buttons.Count; i++)
            {
                ProjectCopyState state = Buttons[i];

                // Selecting another Project asset returns the bar to the
                // normal project-relative breadcrumb display.
                state.currentAssetPath = selectedAssetPath;
                state.isEditingPath = false;
                state.requestPathFocus = false;
                state.pathInput = string.Empty;
                state.scrollPosition = Vector2.zero;

                state.container?.MarkDirtyRepaint();
                state.browser?.Repaint();
            }
        }

        private static void RepaintButtons()
        {
            for (int i = 0; i < Buttons.Count; i++)
            {
                Buttons[i].container?.MarkDirtyRepaint();
                Buttons[i].browser?.Repaint();
            }
        }

        private static string GetSelectedAssetPath()
        {
            string[] selectedAssetGuids = Selection.assetGUIDs;
            if (selectedAssetGuids != null && selectedAssetGuids.Length > 0)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(selectedAssetGuids[0]);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    return assetPath;
                }
            }

            if (Selection.activeObject == null)
            {
                return string.Empty;
            }

            return AssetDatabase.GetAssetPath(Selection.activeObject);
        }

        private static void RemoveClosedProjectBrowsers()
        {
            for (int i = Buttons.Count - 1; i >= 0; i--)
            {
                ProjectCopyState state = Buttons[i];
                if (state.browser != null && state.container != null && state.container.parent != null)
                {
                    continue;
                }

                state.container?.RemoveFromHierarchy();
                Buttons.RemoveAt(i);
            }
        }

        private static void DetachAll()
        {
            for (int i = 0; i < Buttons.Count; i++)
            {
                Buttons[i].container?.RemoveFromHierarchy();
            }

            Buttons.Clear();
        }
    }
}
#endif
