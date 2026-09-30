#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UdonSharp;
using UnityEditor;
using UnityEngine;

namespace Rin0700.Tools
{
    /// <summary>
    /// UdonSharp script batch renamer.
    ///
    /// - Drag & drop one or more UdonSharp .cs files (folders are also accepted).
    /// - Edit the destination name per script.
    /// - Renames the MonoScript asset while preserving its GUID.
    /// - Renames the UdonSharpBehaviour class to match the .cs file name.
    /// - Renames the one-and-only-one UdonSharpProgramAsset linked to the script.
    /// - Optionally updates matching C# identifiers in Assets/**/*.cs.
    ///
    /// Important UdonSharp invariant:
    /// One UdonSharp script must be connected to exactly one UdonSharpProgramAsset.
    /// This tool never reassigns sourceCsScript; it only renames the existing asset,
    /// preserving both the MonoScript GUID and UdonSharpProgramAsset GUID.
    /// </summary>
    public sealed class UdonSharpBatchRenamer : EditorWindow
    {
        private const string MenuPath = "Tools/Rin0700/UdonSharp Script Renamer";
        private const float DropAreaHeight = 90f;

        [SerializeField]
        private List<RenameItem> _items = new List<RenameItem>();

        [SerializeField]
        private bool _updateCSharpReferences = true;

        [SerializeField]
        private Vector2 _scrollPosition;

        private static readonly HashSet<string> CSharpKeywords =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char",
                "checked", "class", "const", "continue", "decimal", "default", "delegate", "do",
                "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed",
                "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
                "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator",
                "out", "override", "params", "private", "protected", "public", "readonly", "ref",
                "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
                "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
                "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while"
            };

        [MenuItem(MenuPath, false, 100)]
        private static void Open()
        {
            UdonSharpBatchRenamer window = GetWindow<UdonSharpBatchRenamer>();
            window.titleContent = new GUIContent("U# Renamer");
            window.minSize = new Vector2(760f, 460f);
            window.Show();
        }

        private void OnEnable()
        {
            RefreshAllBindings();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("UdonSharp Script Renamer", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "UdonSharpの.csファイルをドラッグ&ドロップし、各行で新しい名前を指定します。\n" +
                "実行時は .cs / UdonSharpBehaviourクラス / 対応UdonSharpProgramAsset を同時に変更します。\n" +
                "GUIDは維持されるため、Scene / Prefab の参照を張り直しません。",
                MessageType.Info);

            DrawDropArea();

            EditorGUILayout.Space(6f);
            _updateCSharpReferences = EditorGUILayout.ToggleLeft(
                "Assets配下のC#型参照も更新する",
                _updateCSharpReferences);

            EditorGUILayout.Space(8f);
            DrawToolbar();
            EditorGUILayout.Space(8f);
            DrawItems();
        }

        private void DrawDropArea()
        {
            Rect dropArea = GUILayoutUtility.GetRect(
                0f,
                DropAreaHeight,
                GUILayout.ExpandWidth(true));

            GUI.Box(dropArea, GUIContent.none, EditorStyles.helpBox);

            GUIStyle centered = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                fontStyle = FontStyle.Bold
            };

            GUI.Label(
                dropArea,
                "UdonSharp .cs files\nDrag & Drop",
                centered);

            Event currentEvent = Event.current;
            if (!dropArea.Contains(currentEvent.mousePosition))
                return;

            if (currentEvent.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                currentEvent.Use();
                return;
            }

            if (currentEvent.type != EventType.DragPerform)
                return;

            DragAndDrop.AcceptDrag();

            foreach (UnityEngine.Object draggedObject in DragAndDrop.objectReferences)
            {
                AddDraggedObject(draggedObject);
            }

            currentEvent.Use();
            RefreshAllBindings();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Add Selected", GUILayout.Height(28f)))
            {
                foreach (UnityEngine.Object selectedObject in Selection.objects)
                    AddDraggedObject(selectedObject);

                RefreshAllBindings();
            }

            if (GUILayout.Button("Refresh", GUILayout.Height(28f)))
            {
                RefreshAllBindings();
            }

            if (GUILayout.Button("Clear", GUILayout.Height(28f)))
            {
                _items.Clear();
                GUI.FocusControl(null);
            }

            GUILayout.FlexibleSpace();

            bool canApply = _items.Count > 0 && !HasAnyValidationError();
            EditorGUI.BeginDisabledGroup(!canApply);

            if (GUILayout.Button("Apply All", GUILayout.Width(130f), GUILayout.Height(28f)))
            {
                ApplyRename();
            }

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawItems()
        {
            if (_items.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "対象のUdonSharpスクリプトを上の領域へドラッグ&ドロップしてください。",
                    MessageType.None);
                return;
            }

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            for (int i = 0; i < _items.Count; i++)
            {
                RenameItem item = _items[i];

                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();

                EditorGUILayout.LabelField(
                    (i + 1).ToString(),
                    EditorStyles.boldLabel,
                    GUILayout.Width(28f));

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ObjectField(
                    item.Script,
                    typeof(MonoScript),
                    false,
                    GUILayout.MinWidth(210f));
                EditorGUI.EndDisabledGroup();

                GUILayout.Label("→", GUILayout.Width(20f));

                EditorGUI.BeginChangeCheck();
                string newName = EditorGUILayout.TextField(
                    item.NewName,
                    GUILayout.MinWidth(200f));
                if (EditorGUI.EndChangeCheck())
                {
                    item.NewName = newName.Trim();
                    ValidateAllItems();
                }

                if (GUILayout.Button("×", GUILayout.Width(28f)))
                {
                    _items.RemoveAt(i);
                    ValidateAllItems();
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                    i--;
                    continue;
                }

                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(3f);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(28f);

                EditorGUILayout.LabelField(
                    "Script",
                    GUILayout.Width(55f));
                EditorGUILayout.SelectableLabel(
                    item.ScriptPath ?? "-",
                    EditorStyles.miniLabel,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));

                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(28f);

                EditorGUILayout.LabelField(
                    "Program",
                    GUILayout.Width(55f));

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ObjectField(
                    item.ProgramAsset,
                    typeof(UdonSharpProgramAsset),
                    false,
                    GUILayout.MinWidth(220f));
                EditorGUI.EndDisabledGroup();

                if (item.ProgramAsset != null)
                {
                    EditorGUILayout.SelectableLabel(
                        item.ProgramAssetPath ?? "-",
                        EditorStyles.miniLabel,
                        GUILayout.Height(EditorGUIUtility.singleLineHeight));
                }

                EditorGUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(item.Error))
                {
                    EditorGUILayout.HelpBox(item.Error, MessageType.Error);
                }
                else if (!string.IsNullOrEmpty(item.Warning))
                {
                    EditorGUILayout.HelpBox(item.Warning, MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        item.CurrentName + "  →  " + item.NewName,
                        MessageType.None);
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(3f);
            }

            EditorGUILayout.EndScrollView();
        }

        private void AddDraggedObject(UnityEngine.Object draggedObject)
        {
            if (draggedObject == null)
                return;

            MonoScript monoScript = draggedObject as MonoScript;
            if (monoScript != null)
            {
                AddScript(monoScript);
                return;
            }

            string path = AssetDatabase.GetAssetPath(draggedObject);
            if (string.IsNullOrEmpty(path) || !AssetDatabase.IsValidFolder(path))
                return;

            string[] scriptGuids = AssetDatabase.FindAssets("t:MonoScript", new[] { path });
            foreach (string guid in scriptGuids)
            {
                string scriptPath = AssetDatabase.GUIDToAssetPath(guid);
                MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
                if (script != null)
                    AddScript(script);
            }
        }

        private void AddScript(MonoScript script)
        {
            if (script == null)
                return;

            if (_items.Any(item => item.Script == script))
                return;

            string path = AssetDatabase.GetAssetPath(script);
            if (string.IsNullOrEmpty(path) ||
                !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string currentName = Path.GetFileNameWithoutExtension(path);

            RenameItem newItem = new RenameItem
            {
                Script = script,
                CurrentName = currentName,
                NewName = currentName
            };

            _items.Add(newItem);
            RefreshBinding(newItem);
            ValidateAllItems();
        }

        private void RefreshAllBindings()
        {
            _items.RemoveAll(item => item == null || item.Script == null);

            foreach (RenameItem item in _items)
                RefreshBinding(item);

            ValidateAllItems();
        }

        private static void RefreshBinding(RenameItem item)
        {
            item.BindingError = null;
            item.Warning = null;
            item.ProgramAsset = null;
            item.ScriptPath = null;
            item.ProgramAssetPath = null;

            if (item.Script == null)
            {
                item.BindingError = "MonoScriptが見つかりません。";
                return;
            }

            string scriptPath = NormalizePath(AssetDatabase.GetAssetPath(item.Script));
            item.ScriptPath = scriptPath;
            item.CurrentName = Path.GetFileNameWithoutExtension(scriptPath);

            if (string.IsNullOrEmpty(item.NewName))
                item.NewName = item.CurrentName;

            if (!scriptPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                item.BindingError = "Packages等の読み取り専用スクリプトは変更できません。Assets配下のファイルを指定してください。";
                return;
            }

            Type scriptType = item.Script.GetClass();
            if (scriptType == null)
            {
                item.BindingError = "MonoScriptからクラス型を取得できません。現在のコンパイルエラーを解消してください。";
                return;
            }

            if (!typeof(UdonSharpBehaviour).IsAssignableFrom(scriptType))
            {
                item.BindingError = "このスクリプトはUdonSharpBehaviourではありません。";
                return;
            }

            if (!string.Equals(scriptType.Name, item.CurrentName, StringComparison.Ordinal))
            {
                item.BindingError =
                    "UdonSharpの要件に反しています。現在のクラス名 (" + scriptType.Name +
                    ") とファイル名 (" + item.CurrentName + ") が一致していません。";
                return;
            }

            List<UdonSharpProgramAsset> linkedAssets = FindLinkedProgramAssets(item.Script);
            if (linkedAssets.Count == 0)
            {
                item.BindingError = "この.csに紐づくUdonSharpProgramAssetが見つかりません。";
                return;
            }

            if (linkedAssets.Count > 1)
            {
                item.BindingError =
                    "同じ.csを参照するUdonSharpProgramAssetが複数あります。UdonSharpでは1:1である必要があります。";
                return;
            }

            item.ProgramAsset = linkedAssets[0];
            item.ProgramAssetPath = NormalizePath(AssetDatabase.GetAssetPath(item.ProgramAsset));
        }

        private static List<UdonSharpProgramAsset> FindLinkedProgramAssets(MonoScript script)
        {
            List<UdonSharpProgramAsset> result = new List<UdonSharpProgramAsset>();
            string[] guids = AssetDatabase.FindAssets("t:UdonSharpProgramAsset", new[] { "Assets" });

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                UdonSharpProgramAsset programAsset =
                    AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(path);

                if (programAsset != null && programAsset.sourceCsScript == script)
                    result.Add(programAsset);
            }

            return result;
        }

        private bool HasAnyValidationError()
        {
            ValidateAllItems();
            return _items.Any(item => !string.IsNullOrEmpty(item.Error));
        }

        private void ValidateAllItems()
        {
            foreach (RenameItem item in _items)
            {
                item.Error = item.BindingError;
                item.Warning = null;

                if (!string.IsNullOrEmpty(item.Error))
                    continue;

                if (!IsValidCSharpIdentifier(item.NewName))
                {
                    item.Error = "変更後の名前が有効なC#識別子ではありません。";
                    continue;
                }

                if (string.Equals(item.CurrentName, item.NewName, StringComparison.Ordinal))
                    item.Warning = "名前は変更されません。";
            }

            Dictionary<string, int> destinationNameCounts =
                new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (RenameItem item in _items)
            {
                if (string.IsNullOrEmpty(item.NewName))
                    continue;

                int count;
                destinationNameCounts.TryGetValue(item.NewName, out count);
                destinationNameCounts[item.NewName] = count + 1;
            }

            foreach (RenameItem item in _items)
            {
                if (!string.IsNullOrEmpty(item.Error))
                    continue;

                int count;
                if (destinationNameCounts.TryGetValue(item.NewName, out count) && count > 1)
                    item.Error = "複数の項目が同じクラス名へ変更されます。";
            }

            ValidateDestinationCollisions();
        }

        private void ValidateDestinationCollisions()
        {
            HashSet<string> movingSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> movingProgramPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (RenameItem item in _items)
            {
                if (item.Script != null && !string.IsNullOrEmpty(item.ScriptPath))
                    movingSourcePaths.Add(item.ScriptPath);

                if (item.ProgramAsset != null && !string.IsNullOrEmpty(item.ProgramAssetPath))
                    movingProgramPaths.Add(item.ProgramAssetPath);
            }

            foreach (RenameItem item in _items)
            {
                if (!string.IsNullOrEmpty(item.Error) ||
                    item.Script == null ||
                    item.ProgramAsset == null ||
                    string.Equals(item.CurrentName, item.NewName, StringComparison.Ordinal))
                {
                    continue;
                }

                string scriptDirectory = NormalizePath(Path.GetDirectoryName(item.ScriptPath));
                string scriptDestination = NormalizePath(
                    Path.Combine(scriptDirectory, item.NewName + ".cs"));

                if (movingSourcePaths.Contains(scriptDestination))
                {
                    item.Error =
                        "変更先が一括変更対象の.csです。名前の交換・連鎖変更には対応していません: " +
                        scriptDestination;
                    continue;
                }

                UnityEngine.Object existingScript =
                    AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scriptDestination);

                if (existingScript != null)
                {
                    item.Error = "変更先に同名の.csが存在します: " + scriptDestination;
                    continue;
                }

                string programDirectory = NormalizePath(Path.GetDirectoryName(item.ProgramAssetPath));
                string programExtension = Path.GetExtension(item.ProgramAssetPath);
                string programDestination = NormalizePath(
                    Path.Combine(programDirectory, item.NewName + programExtension));

                if (movingProgramPaths.Contains(programDestination))
                {
                    item.Error =
                        "変更先が一括変更対象のUdonSharpProgramAssetです。名前の交換・連鎖変更には対応していません: " +
                        programDestination;
                    continue;
                }

                UnityEngine.Object existingProgram =
                    AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(programDestination);

                if (existingProgram != null)
                {
                    item.Error = "変更先に同名のUdonSharpProgramAssetが存在します: " + programDestination;
                }
            }
        }

        private void ApplyRename()
        {
            RefreshAllBindings();

            List<RenameItem> activeItems = _items
                .Where(item =>
                    string.IsNullOrEmpty(item.Error) &&
                    !string.Equals(item.CurrentName, item.NewName, StringComparison.Ordinal))
                .ToList();

            if (activeItems.Count == 0)
            {
                EditorUtility.DisplayDialog("UdonSharp Script Renamer", "変更対象がありません。", "OK");
                return;
            }

            if (_items.Any(item => !string.IsNullOrEmpty(item.Error)))
            {
                EditorUtility.DisplayDialog(
                    "UdonSharp Script Renamer",
                    "エラーのある項目があります。修正してから実行してください。",
                    "OK");
                return;
            }

            string summary = string.Join(
                "\n",
                activeItems.Select(item => "• " + item.CurrentName + "  →  " + item.NewName).ToArray());

            bool confirmed = EditorUtility.DisplayDialog(
                "Confirm Rename",
                activeItems.Count + "件を変更します。\n\n" + summary +
                "\n\n.cs / class / UdonSharpProgramAsset のGUIDは維持されます。\n" +
                "実行前にGitでコミットしておくことを推奨します。",
                "Rename",
                "Cancel");

            if (!confirmed)
                return;

            Dictionary<string, string> renameMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (RenameItem item in activeItems)
                renameMap.Add(item.CurrentName, item.NewName);

            List<RenamePlan> plans = BuildRenamePlans(activeItems);
            List<FileChange> fileChanges = BuildFileChanges(renameMap, activeItems);
            string backupDirectory = CreateBackup(fileChanges);

            bool assetEditing = false;

            try
            {
                AssetDatabase.StartAssetEditing();
                assetEditing = true;

                // 1) Rewrite class declarations / type references first.
                foreach (FileChange change in fileChanges)
                {
                    WriteUtf8(change.Path, change.NewText, change.HasUtf8Bom);
                }

                // Move each asset directly from its original path to its final path.
                // AssetDatabase.StartAssetEditing defers path updates, so an
                // in-batch temporary-path rename cannot be resolved reliably.
                // ValidateDestinationCollisions rejects swap/chain renames.
                foreach (RenamePlan plan in plans)
                {
                    RenameAssetOrThrow(plan.OldScriptPath, plan.NewName);
                    plan.ScriptRenamed = true;

                    RenameAssetOrThrow(plan.OldProgramAssetPath, plan.NewName);
                    plan.ProgramAssetRenamed = true;
                }

                AssetDatabase.StopAssetEditing();
                assetEditing = false;

                AssetDatabase.SaveAssets();

                string log =
                    "[UdonSharpBatchRenamer] Rename complete. " +
                    "Scripts: " + plans.Count +
                    ", Modified C# files: " + fileChanges.Count +
                    "\nBackup: " + backupDirectory;

                Debug.Log(log);

                _items.Clear();
                Repaint();

                // Final state is already consistent at this point:
                // file name == UdonSharpBehaviour class name,
                // and the existing ProgramAsset still points at the same MonoScript GUID.
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);

                // Keep rollback batched as well, so Unity/UdonSharp never imports
                // an intentional intermediate file/class mismatch.
                if (!assetEditing)
                {
                    AssetDatabase.StartAssetEditing();
                    assetEditing = true;
                }

                try
                {
                    Rollback(plans, fileChanges);
                }
                catch (Exception rollbackException)
                {
                    Debug.LogException(rollbackException);
                }

                if (assetEditing)
                {
                    AssetDatabase.StopAssetEditing();
                    assetEditing = false;
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

                EditorUtility.DisplayDialog(
                    "Rename Failed",
                    exception.Message +
                    "\n\n可能な範囲でロールバックしました。" +
                    "\nバックアップ: " + backupDirectory +
                    "\nConsoleも確認してください。",
                    "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();

                if (assetEditing)
                    AssetDatabase.StopAssetEditing();
            }
        }

        private static List<RenamePlan> BuildRenamePlans(List<RenameItem> items)
        {
            List<RenamePlan> plans = new List<RenamePlan>();

            for (int i = 0; i < items.Count; i++)
            {
                RenameItem item = items[i];
                string oldScriptPath = item.ScriptPath;
                string oldProgramAssetPath = item.ProgramAssetPath;
                string scriptDirectory = NormalizePath(Path.GetDirectoryName(oldScriptPath));
                string programAssetDirectory = NormalizePath(
                    Path.GetDirectoryName(oldProgramAssetPath));
                string scriptExtension = Path.GetExtension(oldScriptPath);
                string programAssetExtension = Path.GetExtension(oldProgramAssetPath);

                plans.Add(new RenamePlan
                {
                    OldScriptName = item.CurrentName,
                    OldProgramName = Path.GetFileNameWithoutExtension(oldProgramAssetPath),
                    NewName = item.NewName,
                    OldScriptPath = oldScriptPath,
                    OldProgramAssetPath = oldProgramAssetPath,
                    NewScriptPath = NormalizePath(
                        Path.Combine(scriptDirectory, item.NewName + scriptExtension)),
                    NewProgramAssetPath = NormalizePath(
                        Path.Combine(programAssetDirectory, item.NewName + programAssetExtension))
                });
            }

            return plans;
        }

        private List<FileChange> BuildFileChanges(
            Dictionary<string, string> renameMap,
            List<RenameItem> activeItems)
        {
            List<string> paths;

            if (_updateCSharpReferences)
            {
                paths = AssetDatabase
                    .GetAllAssetPaths()
                    .Where(path =>
                        path.StartsWith("Assets/", StringComparison.Ordinal) &&
                        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path)
                    .ToList();
            }
            else
            {
                paths = activeItems
                    .Select(item => item.ScriptPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            List<FileChange> changes = new List<FileChange>();

            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];

                EditorUtility.DisplayProgressBar(
                    "Scanning C# References",
                    path,
                    paths.Count == 0 ? 1f : (float)i / paths.Count);

                Utf8TextFile file = ReadUtf8(path);
                string updated = ReplaceIdentifiersInCode(file.Text, renameMap);

                if (string.Equals(file.Text, updated, StringComparison.Ordinal))
                    continue;

                changes.Add(new FileChange
                {
                    Path = path,
                    OriginalBytes = file.OriginalBytes,
                    NewText = updated,
                    HasUtf8Bom = file.HasUtf8Bom
                });
            }

            EditorUtility.ClearProgressBar();
            return changes;
        }

        private static void Rollback(List<RenamePlan> plans, List<FileChange> fileChanges)
        {
            foreach (RenamePlan plan in plans)
            {
                if (plan.ProgramAssetRenamed)
                {
                    RenameAssetOrThrow(plan.NewProgramAssetPath, plan.OldProgramName);
                    plan.ProgramAssetRenamed = false;
                }

                if (plan.ScriptRenamed)
                {
                    RenameAssetOrThrow(plan.NewScriptPath, plan.OldScriptName);
                    plan.ScriptRenamed = false;
                }
            }

            // Selected scripts now have their original paths again, so the
            // byte-for-byte source backups can be restored safely.
            foreach (FileChange change in fileChanges)
            {
                File.WriteAllBytes(change.Path, change.OriginalBytes);
            }
        }

        private static void RenameAssetOrThrow(string currentPath, string newName)
        {
            if (string.IsNullOrEmpty(currentPath))
                throw new InvalidOperationException("Asset path is empty.");

            string error = AssetDatabase.RenameAsset(currentPath, newName);
            if (!string.IsNullOrEmpty(error))
            {
                throw new InvalidOperationException(
                    "Asset rename failed:\n" +
                    currentPath + "\n→ " + newName + "\n\n" + error);
            }
        }

        /// <summary>
        /// Replaces only C# identifiers. Comments and string/char literals are intentionally skipped.
        /// This is a lexical rename, not a Roslyn semantic rename.
        /// </summary>
        private static string ReplaceIdentifiersInCode(
            string source,
            Dictionary<string, string> replacements)
        {
            if (string.IsNullOrEmpty(source) || replacements.Count == 0)
                return source;

            StringBuilder builder = new StringBuilder(source.Length);
            int index = 0;

            while (index < source.Length)
            {
                int segmentEnd;
                if (TryConsumeNonCodeSegment(source, index, out segmentEnd))
                {
                    builder.Append(source, index, segmentEnd - index);
                    index = segmentEnd;
                    continue;
                }

                char current = source[index];
                if (IsIdentifierStart(current))
                {
                    int start = index++;
                    while (index < source.Length && IsIdentifierPart(source[index]))
                        index++;

                    string identifier = source.Substring(start, index - start);
                    string replacement;
                    builder.Append(
                        replacements.TryGetValue(identifier, out replacement)
                            ? replacement
                            : identifier);
                    continue;
                }

                builder.Append(current);
                index++;
            }

            return builder.ToString();
        }

        private static bool TryConsumeNonCodeSegment(string source, int start, out int end)
        {
            end = start;
            int length = source.Length;

            if (start >= length)
                return false;

            // // line comment
            if (start + 1 < length && source[start] == '/' && source[start + 1] == '/')
            {
                int index = start + 2;
                while (index < length && source[index] != '\n')
                    index++;

                end = index;
                return true;
            }

            // /* block comment */
            if (start + 1 < length && source[start] == '/' && source[start + 1] == '*')
            {
                int index = start + 2;
                while (index + 1 < length)
                {
                    if (source[index] == '*' && source[index + 1] == '/')
                    {
                        end = index + 2;
                        return true;
                    }
                    index++;
                }

                end = length;
                return true;
            }

            // $""" / $$""" ... raw interpolated string
            if (source[start] == '$')
            {
                int dollarEnd = start;
                while (dollarEnd < length && source[dollarEnd] == '$')
                    dollarEnd++;

                int quoteCount = CountConsecutiveQuotes(source, dollarEnd);
                if (quoteCount >= 3)
                {
                    end = ConsumeRawString(source, dollarEnd, quoteCount);
                    return true;
                }
            }

            // """ raw string
            if (source[start] == '"')
            {
                int quoteCount = CountConsecutiveQuotes(source, start);
                if (quoteCount >= 3)
                {
                    end = ConsumeRawString(source, start, quoteCount);
                    return true;
                }
            }

            // $@"..."
            if (start + 2 < length &&
                source[start] == '$' &&
                source[start + 1] == '@' &&
                source[start + 2] == '"')
            {
                end = ConsumeVerbatimString(source, start + 2);
                return true;
            }

            // @$"..."
            if (start + 2 < length &&
                source[start] == '@' &&
                source[start + 1] == '$' &&
                source[start + 2] == '"')
            {
                end = ConsumeVerbatimString(source, start + 2);
                return true;
            }

            // @"..."
            if (start + 1 < length && source[start] == '@' && source[start + 1] == '"')
            {
                end = ConsumeVerbatimString(source, start + 1);
                return true;
            }

            // $"..."
            if (start + 1 < length && source[start] == '$' && source[start + 1] == '"')
            {
                end = ConsumeRegularString(source, start + 1);
                return true;
            }

            // "..."
            if (source[start] == '"')
            {
                end = ConsumeRegularString(source, start);
                return true;
            }

            // 'x'
            if (source[start] == '\'')
            {
                end = ConsumeCharLiteral(source, start);
                return true;
            }

            return false;
        }

        private static int ConsumeRegularString(string source, int quoteIndex)
        {
            int index = quoteIndex + 1;
            while (index < source.Length)
            {
                if (source[index] == '\\')
                {
                    index += 2;
                    continue;
                }

                if (source[index] == '"')
                    return index + 1;

                index++;
            }

            return source.Length;
        }

        private static int ConsumeVerbatimString(string source, int quoteIndex)
        {
            int index = quoteIndex + 1;
            while (index < source.Length)
            {
                if (source[index] != '"')
                {
                    index++;
                    continue;
                }

                if (index + 1 < source.Length && source[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                return index + 1;
            }

            return source.Length;
        }

        private static int ConsumeCharLiteral(string source, int quoteIndex)
        {
            int index = quoteIndex + 1;
            while (index < source.Length)
            {
                if (source[index] == '\\')
                {
                    index += 2;
                    continue;
                }

                if (source[index] == '\'')
                    return index + 1;

                index++;
            }

            return source.Length;
        }

        private static int ConsumeRawString(string source, int quoteIndex, int quoteCount)
        {
            string closing = new string('"', quoteCount);
            int searchStart = quoteIndex + quoteCount;
            int closingIndex = source.IndexOf(closing, searchStart, StringComparison.Ordinal);
            return closingIndex < 0 ? source.Length : closingIndex + quoteCount;
        }

        private static int CountConsecutiveQuotes(string source, int start)
        {
            int count = 0;
            while (start + count < source.Length && source[start + count] == '"')
                count++;

            return count;
        }

        private static bool IsValidCSharpIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) || !IsIdentifierStart(value[0]))
                return false;

            for (int i = 1; i < value.Length; i++)
            {
                if (!IsIdentifierPart(value[i]))
                    return false;
            }

            return !CSharpKeywords.Contains(value);
        }

        private static bool IsIdentifierStart(char value)
        {
            return value == '_' || char.IsLetter(value);
        }

        private static bool IsIdentifierPart(char value)
        {
            return value == '_' || char.IsLetterOrDigit(value);
        }

        private static string CreateBackup(List<FileChange> changes)
        {
            string root = NormalizePath(
                Path.Combine(
                    "Library",
                    "UdonSharpBatchRenamerBackups",
                    DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8)));

            foreach (FileChange change in changes)
            {
                string backupPath = NormalizePath(Path.Combine(root, change.Path));
                string directory = Path.GetDirectoryName(backupPath);

                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllBytes(backupPath, change.OriginalBytes);
            }

            return root;
        }

        private static Utf8TextFile ReadUtf8(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            bool hasBom =
                bytes.Length >= 3 &&
                bytes[0] == 0xEF &&
                bytes[1] == 0xBB &&
                bytes[2] == 0xBF;

            int offset = hasBom ? 3 : 0;
            UTF8Encoding encoding = new UTF8Encoding(false, true);
            string text = encoding.GetString(bytes, offset, bytes.Length - offset);

            return new Utf8TextFile
            {
                Text = text,
                OriginalBytes = bytes,
                HasUtf8Bom = hasBom
            };
        }

        private static void WriteUtf8(string path, string text, bool withBom)
        {
            UTF8Encoding encoding = new UTF8Encoding(withBom);
            File.WriteAllText(path, text, encoding);
        }

        private static string NormalizePath(string path)
        {
            return string.IsNullOrEmpty(path)
                ? string.Empty
                : path.Replace('\\', '/');
        }

        [Serializable]
        private sealed class RenameItem
        {
            public MonoScript Script;
            public UdonSharpProgramAsset ProgramAsset;
            public string CurrentName;
            public string NewName;
            public string ScriptPath;
            public string ProgramAssetPath;
            public string BindingError;
            public string Error;
            public string Warning;
        }

        private sealed class RenamePlan
        {
            public string OldScriptName;
            public string OldProgramName;
            public string NewName;
            public string OldScriptPath;
            public string OldProgramAssetPath;
            public string NewScriptPath;
            public string NewProgramAssetPath;
            public bool ScriptRenamed;
            public bool ProgramAssetRenamed;
        }

        private sealed class FileChange
        {
            public string Path;
            public byte[] OriginalBytes;
            public string NewText;
            public bool HasUtf8Bom;
        }

        private sealed class Utf8TextFile
        {
            public string Text;
            public byte[] OriginalBytes;
            public bool HasUtf8Bom;
        }
    }
}

#endif
