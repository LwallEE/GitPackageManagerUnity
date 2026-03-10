using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace Lwalle.GitPackageManager
{
    public class TokenInputWindow : EditorWindow
    {
       
        private string token = "";
        private string repoUrl;
        private Action<string> onSubmit;
        private Action onCancel;

        public static void Show(string repoUrl, Action<string> onSubmit, Action onCancel = null)
        {
            var window = CreateInstance<TokenInputWindow>();
            window.repoUrl = repoUrl;
            window.onSubmit = onSubmit;
            window.onCancel = onCancel;
            window.titleContent = new GUIContent("GitHub Token Required");
            window.minSize = new Vector2(450, 150);
            window.maxSize = new Vector2(450, 150);
            window.ShowModalUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Private repo requires a GitHub Token:", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(repoUrl, EditorStyles.miniLabel);
            EditorGUILayout.Space(5);
            token = EditorGUILayout.PasswordField("Token", token);
            EditorGUILayout.Space(10);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("OK", GUILayout.Width(80), GUILayout.Height(25)))
            {
                onSubmit?.Invoke(token);
                Close();
            }

            if (GUILayout.Button("Skip", GUILayout.Width(80), GUILayout.Height(25)))
            {
                onCancel?.Invoke();
                Close();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void OnDestroy()
        {
            // If window closed without pressing OK or Skip, treat as cancel
        }
    }

    [InitializeOnLoad]
    public static class GitPackageManagerRestarter
    {
        static GitPackageManagerRestarter()
        {
            // This method will be called after a domain reload (e.g., after a package install).
            EditorApplication.delayCall += CheckForInterruptedInstallation;
        }

        private static void CheckForInterruptedInstallation()
        {
            // If our flag indicates an installation was running before the reload,
            // we ensure the window is open so it can resume itself.
            if (SessionState.GetBool(CustomPackageScanner.SESSION_INSTALL_RUNNING, false))
            {
                Debug.LogWarning("[GitPackageManager] Interrupted installation detected. Re-opening window to resume...");
                // Get or open the window. Its OnEnable method will handle the resume logic.
                EditorWindow.GetWindow<CustomPackageScanner>("Custom Package Scanner");
            }
        }
    }


    public class CustomPackageScanner : EditorWindow
    {
        private string searchQuery = "";
        private RepoConfigSO config;
        private Vector2 leftScrollPos;
        private Vector2 rightScrollPos;
        private RepoConfigSO.CachedPackageInfo selectedPackage;
        private ListRequest listRequest;

        private Dictionary<string, string>
            installedPackageVersions = new Dictionary<string, string>(); // name -> version

        // Session-only token storage using SessionState (survives recompile, lost when Unity closes)
        private const string SESSION_TOKEN_PREFIX = "GitPkgMgr_Token_";
        
        // Session state keys to make the installation process survive domain reloads
        public const string SESSION_INSTALL_RUNNING = "GitPkg_Install_Running";
        public const string SESSION_INSTALL_QUEUE = "GitPkg_Install_Queue";

        #region Editor Coroutine Runner
        // We integrate the coroutine logic directly here to simplify the class structure.
        // These need to be static to survive domain reloads, but their state is managed by the update loop.
        private static readonly List<IEnumerator> editorRoutines = new List<IEnumerator>();
        private static bool isCoroutineRunning = false;

        private static void StartEditorCoroutine(IEnumerator routine)
        {
            editorRoutines.Add(routine);
            if (!isCoroutineRunning)
            {
                isCoroutineRunning = true;
                EditorApplication.update += UpdateEditorCoroutines;
            }
        }

        private static void UpdateEditorCoroutines()
        {
            // Iterate backwards to allow removal.
            for (int i = editorRoutines.Count - 1; i >= 0; i--)
            {
                if (!editorRoutines[i].MoveNext())
                {
                    editorRoutines.RemoveAt(i);
                }
            }

            if (editorRoutines.Count == 0)
            {
                EditorApplication.update -= UpdateEditorCoroutines;
                isCoroutineRunning = false;
            }
        }
        #endregion

        private static string GetSessionToken(string repoUrl)
        {
            return SessionState.GetString(SESSION_TOKEN_PREFIX + repoUrl, "");
        }

        private static void SetSessionToken(string repoUrl, string token)
        {
            SessionState.SetString(SESSION_TOKEN_PREFIX + repoUrl, token);
        }

        private static bool HasSessionToken(string repoUrl)
        {
            return !string.IsNullOrEmpty(SessionState.GetString(SESSION_TOKEN_PREFIX + repoUrl, ""));
        }

        private static void ClearAllSessionTokens(List<RepoConfigSO.RepoEntry> repos)
        {
            foreach (var repo in repos)
                SessionState.EraseString(SESSION_TOKEN_PREFIX + repo.repoUrl);
        }

        private int selectedTab = 0;

        [MenuItem("Lwalle/GitPackageManager")]
        public static void ShowWindow()
        {
            var window = GetWindow<CustomPackageScanner>("Custom Package Scanner");
            
            window.minSize = new Vector2(1000, 600); // Đặt kích thước mặc định rộng hơn
            window.Show();
        }

        private void OnEnable()
        {
            config = Helper.LoadOrCreateSOAtPath<RepoConfigSO>(Constant.REPO_CONFIG_PATH);
            RefreshInstalledPackages();
            // Use delayCall to ensure the editor is fully initialized before we try to resume.
            EditorApplication.delayCall += TryResumeInstallation;
        }
        
        private void TryResumeInstallation()
        {
            if (SessionState.GetBool(SESSION_INSTALL_RUNNING, false))
            {
                string queueJson = SessionState.GetString(SESSION_INSTALL_QUEUE, "");
                if (!string.IsNullOrEmpty(queueJson))
                {
                    var wrapper = JsonUtility.FromJson<PackageListWrapper>(queueJson);
                    if (wrapper != null && wrapper.packages != null && wrapper.packages.Count > 0)
                    {
                        Debug.Log("[GitPackageManager] Resuming interrupted installation chain...");
                        StartEditorCoroutine(InstallChainCoroutine(wrapper.packages));
                    }
                    else
                    {
                        // Invalid state, clear it
                        SessionState.SetBool(SESSION_INSTALL_RUNNING, false);
                        SessionState.EraseString(SESSION_INSTALL_QUEUE);
                    }
                }
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
// ==================== CỘT TRÁI - RỘNG HƠN ĐỂ HIỂN THỊ VERSION ====================
            float leftWidth = position.width * 0.45f; // Tăng từ 0.35 lên 0.45 để rộng hơn
            EditorGUILayout.BeginVertical(GUILayout.Width(leftWidth), GUILayout.ExpandHeight(true));
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Repositories & Packages", EditorStyles.boldLabel);
            EditorGUILayout.EndVertical();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Scan Repos", GUILayout.Height(30)))
            {
                if (config == null)
                {
                    Debug.LogError("Chọn RepoConfigSO trước!");
                    return;
                }

                ScanRepos();
            }

            if (GUILayout.Button("Refresh Tokens", GUILayout.Height(30), GUILayout.Width(120)))
            {
                ClearAllSessionTokens(config.repos);
                Debug.Log("All session tokens cleared. They will be re-requested on next Scan or Install.");
            }

            EditorGUILayout.EndHorizontal();

            config = (RepoConfigSO)EditorGUILayout.ObjectField("Config SO", config, typeof(RepoConfigSO), false);
// ==================== THANH SEARCH ====================
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Search:", GUILayout.Width(60));
            searchQuery = EditorGUILayout.TextField(searchQuery, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Clear", GUILayout.Width(60)))
            {
                searchQuery = "";
                GUI.FocusControl(null); // bỏ focus khỏi textfield
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(5);
            leftScrollPos = GUILayout.BeginScrollView(leftScrollPos);
            bool hasAnyPackage = false;
            foreach (var group in config.cachedRepoGroups)
            {
// Lọc packages trong group theo search query
                var filteredPackages = group.packages.Where(pkg =>
                {
                    if (string.IsNullOrEmpty(searchQuery)) return true;
                    string lowerQuery = searchQuery.ToLower();
                    return (pkg.displayName != null && pkg.displayName.ToLower().Contains(lowerQuery)) ||
                           pkg.name.ToLower().Contains(lowerQuery);
                }).ToList();
                if (filteredPackages.Count == 0) continue;
                hasAnyPackage = true;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                group.isExpanded = EditorGUILayout.Foldout(group.isExpanded, "", true, EditorStyles.foldout);
                GUIStyle repoLabelStyle = new GUIStyle(EditorStyles.boldLabel);
                repoLabelStyle.normal.textColor = new Color(0.8f, 0.9f, 1f);
                EditorGUILayout.LabelField($"📁 {group.repoName}", repoLabelStyle);
                EditorGUILayout.EndHorizontal();
                if (group.isExpanded)
                {
                    EditorGUI.indentLevel++;
                    foreach (var cachedPkg in filteredPackages)
                    {
                        bool isInstalled = installedPackageVersions.ContainsKey(cachedPkg.name);
                        string displayVersion = isInstalled
                            ? installedPackageVersions[cachedPkg.name]
                            : (cachedPkg.versions.Count > 0 ? cachedPkg.versions[0] : "latest");
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Label(isInstalled ? "✓" : "○", GUILayout.Width(20));
                        GUIStyle packageStyle = new GUIStyle(EditorStyles.label);
                        if (selectedPackage == cachedPkg)
                        {
                            packageStyle.normal.textColor = Color.cyan;
                            packageStyle.fontStyle = FontStyle.Bold;
                        }

                        EditorGUILayout.BeginHorizontal();
                        if (GUILayout.Button(cachedPkg.displayName ?? cachedPkg.name, packageStyle,
                                GUILayout.ExpandWidth(true)))
                        {
                            selectedPackage = cachedPkg;
                            if (isInstalled)
                            {
                                selectedPackage.selectedVersionIndex =
                                    selectedPackage.versions.IndexOf(installedPackageVersions[selectedPackage.name]);
                            }
                            else
                            {
                                selectedPackage.selectedVersionIndex = 0;
                            }

                            selectedTab = 0;
                        }

                        GUIStyle versionStyle = new GUIStyle(EditorStyles.miniLabel);
                        versionStyle.alignment = TextAnchor.MiddleRight;
                        if (GUILayout.Button(displayVersion, versionStyle, GUILayout.Width(100)))
                        {
                            selectedPackage = cachedPkg;
                            if (isInstalled)
                            {
                                selectedPackage.selectedVersionIndex =
                                    selectedPackage.versions.IndexOf(installedPackageVersions[selectedPackage.name]);
                            }
                            else
                            {
                                selectedPackage.selectedVersionIndex = 0;
                            }

                            selectedTab = 0;
                        }

                        EditorGUILayout.EndHorizontal();
                        EditorGUILayout.EndHorizontal();
                    }

                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }

            if (!hasAnyPackage && !string.IsNullOrEmpty(searchQuery))
            {
                EditorGUILayout.HelpBox($"Doesn't find any packages with key : \"{searchQuery}\"", MessageType.Info);
            }
            else if (!hasAnyPackage)
            {
                EditorGUILayout.HelpBox("Doesn't have any packages", MessageType.Info);
            }

            GUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
// ==================== CỘT PHẢI ====================
            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            rightScrollPos = GUILayout.BeginScrollView(rightScrollPos);
            if (selectedPackage == null)
            {
                EditorGUILayout.HelpBox("Choose a package for details", MessageType.Info);
            }
            else
            {
                bool isInstalled = installedPackageVersions.ContainsKey(selectedPackage.name);
                string currentVersion = isInstalled ? installedPackageVersions[selectedPackage.name] : null;
                GUILayout.Label(selectedPackage.displayName ?? selectedPackage.name, EditorStyles.boldLabel);
                GUILayout.Label($"Name: {selectedPackage.name}", EditorStyles.miniLabel);
                if (isInstalled && currentVersion != null)
                    GUILayout.Label($"Installed version: {currentVersion}", EditorStyles.miniLabel);
                EditorGUILayout.Space(10);
                if (isInstalled)
                {
                    if (GUILayout.Button("Remove", GUILayout.Height(30)))
                    {
                        RemovePackage(selectedPackage.name);
                    }
                }
                else
                {
                    if (GUILayout.Button("Install", GUILayout.Height(30)))
                    {
                        InstallPackage(selectedPackage);
                    }
                }

                EditorGUILayout.Space(15);
                string[] tabNames = { "Description", "Versions", "Dependencies" };
                selectedTab = GUILayout.Toolbar(selectedTab, tabNames);
                switch (selectedTab)
                {
                    case 0:
                        if (!string.IsNullOrEmpty(selectedPackage.description))
                        {
                            GUILayout.TextArea(selectedPackage.description, EditorStyles.wordWrappedLabel,
                                GUILayout.MinHeight(100));
                        }
                        else
                        {
                            EditorGUILayout.HelpBox("No Description.", MessageType.Info);
                        }

                        break;
                    case 1:
                        if (selectedPackage.versions.Count == 1 && selectedPackage.versions[0] == "latest (no tags)")
                        {
                            EditorGUILayout.HelpBox("No version find.", MessageType.Info);
                        }
                        else
                        {
                            int newIndex = EditorGUILayout.Popup("Available versions",
                                selectedPackage.selectedVersionIndex,
                                selectedPackage.versions.ToArray());
                            if (newIndex != selectedPackage.selectedVersionIndex)
                            {
                                selectedPackage.selectedVersionIndex = newIndex;
                            }

                            EditorGUILayout.Space(10);
                            string selVer = selectedPackage.versions[selectedPackage.selectedVersionIndex];
                            if (!isInstalled || currentVersion != selVer)
                            {
                                if (GUILayout.Button("Install selected version", GUILayout.Height(30)))
                                {
                                    InstallPackage(selectedPackage);
                                }
                            }
                            else
                            {
                                EditorGUILayout.HelpBox("Version is already installed.", MessageType.Info);
                            }
                        }

                        break;
                    case 2:
                        int idx = selectedPackage.selectedVersionIndex;
                        var deps = selectedPackage.versionGitHubDependencies.Count > idx
                            ? selectedPackage.versionGitHubDependencies[idx]
                            : new RepoConfigSO.DependencyVersion();
                        var revDeps = selectedPackage.versionReverseDependencies.Count > idx
                            ? selectedPackage.versionReverseDependencies[idx]
                            : new RepoConfigSO.DependencyVersion();
                        if (deps.versions.Count == 0 && revDeps.versions.Count == 0)
                        {
                            EditorGUILayout.HelpBox("This package doesn't have any dependencies", MessageType.Info);
                        }
                        else
                        {
                            if (deps.versions.Count > 0)
                            {
                                GUILayout.Label("Is Depend On:", EditorStyles.boldLabel);
                                foreach (var pair in deps.versions)
                                {
                                    bool exists = config.cachedRepoGroups.SelectMany(g => g.packages)
                                        .Any(p => p.name == pair.key);
                                    Color old = GUI.color;
                                    if (!exists) GUI.color = Color.yellow;
                                    GUILayout.Label($"• {pair.key} (version: {pair.value})");
                                    GUI.color = old;
                                }

                                EditorGUILayout.Space(10);
                            }

                            if (revDeps.versions.Count > 0)
                            {
                                GUILayout.Label("Is Used By:", EditorStyles.boldLabel);
                                foreach (var pair in revDeps.versions)
                                {
                                    GUILayout.Label($"• {pair.key} (version: {pair.value})");
                                }
                            }
                        }

                        break;
                }
            }

            GUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private void ScanRepos()
        {
            if (config == null) return;

            // Collect tokens for private repos first, then scan
            CollectTokensAndScan(0);
        }

        private void CollectTokensAndScan(int repoIndex)
        {
            // Find next private repo that needs a token
            while (repoIndex < config.repos.Count)
            {
                var repo = config.repos[repoIndex];
                if (repo.isPrivate && !HasSessionToken(repo.repoUrl))
                {
                    TokenInputWindow.Show(repo.repoUrl,
                        token =>
                        {
                            if (!string.IsNullOrEmpty(token))
                                SetSessionToken(repo.repoUrl, token);
                            CollectTokensAndScan(repoIndex + 1);
                        },
                        () => CollectTokensAndScan(repoIndex + 1)
                    );
                    return; // Wait for modal window
                }

                repoIndex++;
            }

            // All tokens collected, proceed with scan
            ExecuteScan();
        }

        private string GetTokenForRepo(string repoUrl)
        {
            return GetSessionToken(repoUrl);
        }

        private void ExecuteScan()
        {
            config.cachedRepoGroups.Clear();
            selectedPackage = null;
            Dictionary<string, RepoConfigSO.CachedPackageInfo> allPackagesMap =
                new Dictionary<string, RepoConfigSO.CachedPackageInfo>();
            foreach (var repo in config.repos)
            {
                if (!TryParseRepoUrl(repo.repoUrl, out string owner, out string repoName)) continue;
                string token = GetTokenForRepo(repo.repoUrl);
                var treeEntries = GetRepoTree(owner, repoName, repo.defaultBranch, token);
                if (treeEntries == null) continue;
                var packageJsonPaths = treeEntries
                    .Where(e => e.type == "blob" && e.path.EndsWith("/package.json"))
                    .Select(e => e.path.Replace("/package.json", ""))
                    .ToList();
                var packages = new List<RepoConfigSO.CachedPackageInfo>();
                foreach (var subPath in packageJsonPaths)
                {
// Fetch package.json from default branch initially
                    string defaultJsonContent = GetFileContent(owner, repoName, repo.defaultBranch,
                        subPath + "/package.json", token);
                    if (string.IsNullOrEmpty(defaultJsonContent)) continue;
                    string name = ExtractPackageName(defaultJsonContent);
                    if (string.IsNullOrEmpty(name)) continue;
                    string displayName = ExtractField(defaultJsonContent, "displayName");
                    string description = ExtractField(defaultJsonContent, "description");
                    List<string> versions = GetVersionsFromTags(owner, repoName, name, token);
                    // Only show packages that have been tagged
                    if (versions.Count == 0) continue;

                    var versionDeps = new List<RepoConfigSO.DependencyVersion>();
                    for (int i = versions.Count - 1; i >= 0; i--)
                    {
                        string ver = versions[i];
                        string refStr = $"publish/{name}={ver}";
                        string verJsonContent = GetFileContent(owner, repoName, refStr, subPath + "/package.json",
                            token);
                        if (string.IsNullOrEmpty(verJsonContent))
                        {
                            versions.RemoveAt(i);
                            continue;
                        }

                        var verDeps = new RepoConfigSO.DependencyVersion(ExtractGitHubDependencies(verJsonContent));
                        versionDeps.Insert(0, verDeps);
                        // Use the highest version's displayName and description if available
                        if (i == 0)
                        {
                            displayName = ExtractField(verJsonContent, "displayName") ?? displayName;
                            description = ExtractField(verJsonContent, "description") ?? description;
                        }
                    }

                    if (versions.Count == 0) continue;

                    var cachedPkg = new RepoConfigSO.CachedPackageInfo
                    {
                        name = name,
                        displayName = displayName,
                        description = description,
                        repoUrl = repo.repoUrl,
                        subPath = subPath,
                        branch = repo.defaultBranch,
                        versions = versions,
                        selectedVersionIndex = 0,
                        versionGitHubDependencies = versionDeps,
                        versionReverseDependencies =
                            Enumerable.Repeat(new RepoConfigSO.DependencyVersion(), versions.Count).ToList()
                    };
                    packages.Add(cachedPkg);
                    allPackagesMap[name] = cachedPkg;
                }

                if (packages.Count > 0)
                {
                    config.cachedRepoGroups.Add(new RepoConfigSO.CachedRepoGroup
                    {
                        repoUrl = repo.repoUrl,
                        repoName = repoName,
                        isExpanded = config.cachedRepoGroups.Count == 0,
                        packages = packages
                    });
                }
            }

// Tính reverse dependencies
            foreach (var pkg in allPackagesMap.Values)
            {
                for (int i = 0; i < pkg.versionReverseDependencies.Count; i++)
                {
                    pkg.versionReverseDependencies[i] = new RepoConfigSO.DependencyVersion();
                }
            }

            foreach (var pkg in allPackagesMap.Values)
            {
                for (int i = 0; i < pkg.versions.Count; i++)
                {
                    string ver = pkg.versions[i];
                    var deps = pkg.versionGitHubDependencies[i];
                    foreach (var pair in deps.versions)
                    {
                        if (allPackagesMap.TryGetValue(pair.key, out var depPkg))
                        {
                            int depIdx = depPkg.versions.IndexOf(pair.value);
                            if (depIdx != -1)
                            {
                                depPkg.versionReverseDependencies[depIdx].versions.Add(new RepoConfigSO.StringPair
                                {
                                    key = pkg.name,
                                    value = ver
                                });
                            }
                        }
                    }
                }
            }

            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
            RefreshInstalledPackages();
            Repaint();
        }

        private List<RepoConfigSO.StringPair> ExtractGitHubDependencies(string jsonContent)
        {
            var deps = new List<RepoConfigSO.StringPair>();
            int index = jsonContent.IndexOf("\"dependencies\"");
            if (index == -1) return deps;
            int start = jsonContent.IndexOf('{', index);
            int end = jsonContent.IndexOf('}', start);
            if (start == -1 || end == -1) return deps;
            string depsSection = jsonContent.Substring(start + 1, end - start - 1);
            var entries = depsSection.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in entries)
            {
                var parts = entry.Split(new[] { ':' }, 2);
                if (parts.Length == 2)
                {
                    deps.Add(new RepoConfigSO.StringPair
                    {
                        key = parts[0].Trim().Trim('"'),
                        value = parts[1].Trim().Trim('"')
                    });
                }
            }

            return deps;
        }

        private bool IsRepoPrivate(string repoUrl)
        {
            return config.repos.Any(r => r.repoUrl == repoUrl && r.isPrivate);
        }


        private void InstallPackage(RepoConfigSO.CachedPackageInfo mainPkg)
        {
            // Collect all repo URLs that need tokens (main + deps)
            var repoUrlsNeedingToken = new HashSet<string>();
            if (IsRepoPrivate(mainPkg.repoUrl) && !HasSessionToken(mainPkg.repoUrl))
                repoUrlsNeedingToken.Add(mainPkg.repoUrl);

            int selectedIdx = mainPkg.selectedVersionIndex;
            var deps = mainPkg.versionGitHubDependencies[selectedIdx];
            foreach (var pair in deps.versions)
            {
                var depPkg = config.cachedRepoGroups.SelectMany(g => g.packages)
                    .FirstOrDefault(p => p.name == pair.key);
                if (depPkg != null && IsRepoPrivate(depPkg.repoUrl) && !HasSessionToken(depPkg.repoUrl))
                    repoUrlsNeedingToken.Add(depPkg.repoUrl);
            }

            if (repoUrlsNeedingToken.Count > 0)
            {
                CollectTokensForInstall(repoUrlsNeedingToken.ToList(), 0, () => ExecuteInstall(mainPkg));
            }
            else
            {
                ExecuteInstall(mainPkg);
            }
        }

        private void CollectTokensForInstall(List<string> repoUrls, int index, Action onComplete)
        {
            if (index >= repoUrls.Count)
            {
                onComplete?.Invoke();
                return;
            }

            TokenInputWindow.Show(repoUrls[index],
                token =>
                {
                    if (!string.IsNullOrEmpty(token))
                        SetSessionToken(repoUrls[index], token);
                    CollectTokensForInstall(repoUrls, index + 1, onComplete);
                },
                () => CollectTokensForInstall(repoUrls, index + 1, onComplete)
            );
        }

        /// <summary>
        /// Recursively builds a flat list of packages to install, in the correct order (dependencies first).
        /// This is a post-order traversal of the dependency graph.
        /// </summary>
        private void GetInstallationListRecursive(
            RepoConfigSO.CachedPackageInfo package,
            Dictionary<string, RepoConfigSO.CachedPackageInfo> allKnownPackages,
            List<RepoConfigSO.CachedPackageInfo> installList,
            HashSet<string> processedPackagesInPath)
        {
            // If this package has already been fully resolved and added to the final list, we can skip it.
            if (installList.Any(p => p.name == package.name))
            {
                return;
            }

            // Use this set to detect circular dependencies within a single traversal path.
            if (processedPackagesInPath.Contains(package.name))
            {
                Debug.LogError($"Circular dependency detected involving package '{package.name}'. Aborting installation of this path.");
                return;
            }
            processedPackagesInPath.Add(package.name);

            // Get direct dependencies for the selected version
            int selectedIdx = package.selectedVersionIndex;
            if (selectedIdx < 0 || selectedIdx >= package.versionGitHubDependencies.Count)
            {
                Debug.LogWarning($"Package '{package.name}' has an invalid version index. Cannot resolve its dependencies.");
                processedPackagesInPath.Remove(package.name); // Clean up before returning
                return;
            }
            var directDeps = package.versionGitHubDependencies[selectedIdx];

            // Recursively process each dependency first
            foreach (var pair in directDeps.versions)
            {
                string depName = pair.key;
                string depVersion = pair.value;

                if (allKnownPackages.TryGetValue(depName, out var depPkg))
                {
                    // Clone the dependency package info to avoid side-effects between different dependency branches.
                    var depPkgClone = depPkg.Clone();

                    // Find and set the correct version index for the dependency
                    int depVersionIndex = depPkgClone.versions.IndexOf(depVersion);
                    if (depVersionIndex == -1)
                    {
                        Debug.LogWarning($"Required version '{depVersion}' for dependency '{depName}' not found. It will be skipped.");
                        continue;
                    }
                    depPkgClone.selectedVersionIndex = depVersionIndex;

                    // Recursive call for the dependency
                    GetInstallationListRecursive(depPkgClone, allKnownPackages, installList, processedPackagesInPath);
                }
                else
                {
                    Debug.LogWarning($"Dependency '{depName}' is not a known package in any scanned repository. It will be skipped.");
                }
            }

            // After all dependencies of 'package' are in the list, add 'package' itself.
            installList.Add(package.Clone());

            // Remove from the path-tracking set, so other branches of the dependency tree can process this node if needed.
            processedPackagesInPath.Remove(package.name);
        }

        private void ExecuteInstall(RepoConfigSO.CachedPackageInfo mainPkg)
        {
            // Clone the main package to avoid modifying the original SO data during resolution.
            var mainPkgClone = mainPkg.Clone();

            // Build a flat list of all packages to install, in the correct dependency order.
            var allKnownPackages = config.cachedRepoGroups
                .SelectMany(g => g.packages)
                .ToDictionary(p => p.name, p => p);

            var installList = new List<RepoConfigSO.CachedPackageInfo>();
            var processedPackagesInPath = new HashSet<string>();

            GetInstallationListRecursive(mainPkgClone, allKnownPackages, installList, processedPackagesInPath);

            // DEBUG: In ra thứ tự cài đặt
            var installOrderLog = installList.Select(p => $"{p.name}@{p.versions[p.selectedVersionIndex]}").ToList();
            Debug.Log($"[GitPackageManager] Installation order determined ({installOrderLog.Count} packages):\n" +
                      string.Join("\n", installOrderLog.Select((pkg, i) => $"{i + 1}. {pkg}")));

            // Save state to SessionState before starting the coroutine
            var wrapper = new PackageListWrapper { packages = installList };
            SessionState.SetString(SESSION_INSTALL_QUEUE, JsonUtility.ToJson(wrapper));
            SessionState.SetBool(SESSION_INSTALL_RUNNING, true);

            // Start the installation chain with the generated list.
            StartEditorCoroutine(InstallChainCoroutine(installList));
        }

        private IEnumerator InstallChainCoroutine(List<RepoConfigSO.CachedPackageInfo> packagesToInstall)
        {
            int totalPackages = packagesToInstall.Count;
            int currentPackageIndex = 0;
            foreach (var pkg in packagesToInstall)
            {
                currentPackageIndex++;
                Debug.Log($"[GitPackageManager] Chain state: Installing package {currentPackageIndex}/{totalPackages} ('{pkg.name}').");
                // Skip if already installed with the correct version
                if (installedPackageVersions.TryGetValue(pkg.name, out string installedVersion) &&
                    installedVersion == pkg.versions[pkg.selectedVersionIndex])
                {
                    Debug.Log($"✓ Already installed: {pkg.name} @ {installedVersion}");
                    continue;
                }

                AddRequest request = InstallSinglePackage(pkg);

                // Wait until the request is completed
                while (!request.IsCompleted)
                {
                    yield return null;
                }

                if (request.Status == StatusCode.Success)
                {
                    Debug.Log($"✓ Installed: {request.Result.displayName} @ {request.Result.version}");
                    // Manually update our dictionary so the next check in the loop is aware
                    installedPackageVersions[request.Result.name] = request.Result.version;
                }
                else
                {
                    Debug.LogError($"✗ Failed to install {pkg.name}: {request.Error.message}");
                    // Optional: decide if we should stop the whole chain on failure
                    // For now, we continue
                }
            }

            Debug.Log("[GitPackageManager] All installations complete. Refreshing package list for UI.");
            
            // Clear the session state flag on successful completion
            SessionState.SetBool(SESSION_INSTALL_RUNNING, false);
            SessionState.EraseString(SESSION_INSTALL_QUEUE);
            
            RefreshInstalledPackages();
        }

        /// <summary>
        /// Store token into git credential manager so git can authenticate without embedding token in URL.
        /// Works on Mac (osxkeychain) and Windows (manager / manager-core).
        /// </summary>
        private const string GIT_CREDENTIAL_USERNAME = "oauth2";

        /// <summary>
        /// Store token into git credential manager with a specific username.
        /// Git will match credential by host + username, so using "oauth2" avoids
        /// conflict with other GitHub accounts already stored in the credential manager.
        /// Works on Mac (osxkeychain) and Windows (manager / manager-core).
        /// </summary>
        private static void StoreGitCredential(string host, string token)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "credential approve",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                process.StandardInput.WriteLine($"protocol=https");
                process.StandardInput.WriteLine($"host={host}");
                process.StandardInput.WriteLine($"username={GIT_CREDENTIAL_USERNAME}");
                process.StandardInput.WriteLine($"password={token}");
                process.StandardInput.WriteLine();
                process.StandardInput.Close();
                process.WaitForExit(5000);

                if (process.ExitCode == 0)
                    Debug.Log($"Git credential stored for {GIT_CREDENTIAL_USERNAME}@{host}");
                else
                    Debug.LogWarning($"Git credential store returned exit code {process.ExitCode}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to store git credential: {e.Message}");
            }
        }

        private AddRequest InstallSinglePackage(RepoConfigSO.CachedPackageInfo pkg)
        {
            string versionOrBranch = pkg.versions[pkg.selectedVersionIndex];
            string specifier = versionOrBranch.Contains("latest")
                ? pkg.branch
                : $"publish/{pkg.name}={versionOrBranch}";
            string cleanSubPath = pkg.subPath.Trim('/');
            string repoUrlWithGit = pkg.repoUrl.EndsWith(".git") ? pkg.repoUrl : pkg.repoUrl + ".git";
        
            // For private repos: store token in credential manager, use oauth2@ in URL
            string token = GetTokenForRepo(pkg.repoUrl);
            if (!string.IsNullOrEmpty(token) && repoUrlWithGit.StartsWith("https://"))
            {
                // Strip existing username if present (e.g. https://User@github.com -> https://github.com)
                string cleanRepoUrl = System.Text.RegularExpressions.Regex.Replace(
                    repoUrlWithGit, @"^https://[^@]+@", "https://"); 
                var uri = new Uri(cleanRepoUrl);
                StoreGitCredential(uri.Host, token);
        
                // URL with oauth2@ username - git will match this to the stored credential
                repoUrlWithGit = $"https://{GIT_CREDENTIAL_USERNAME}@{uri.Host}{uri.AbsolutePath}";
            }
        
            string gitUrl = $"{repoUrlWithGit}?path=/{cleanSubPath}#{specifier}";
            Debug.Log($"Installing: {pkg.name} @ {versionOrBranch}");
            return Client.Add(gitUrl);
        }

        private void RemovePackage(string packageName)
        {
// Lấy danh sách package installed đang phụ thuộc vào package này
            var dependingPackageNames = GetInstalledPackageNamesThatDependOn(packageName);
            if (dependingPackageNames.Count > 0)
            {
                string message = $"Package \"{packageName}\" is using by these other packages :\n\n";
                foreach (var name in dependingPackageNames)
                {
                    string ver = installedPackageVersions.ContainsKey(name)
                        ? installedPackageVersions[name]
                        : "unknown";
                    message += $"• {name} (installed version: {ver})\n";
                }

                message += "\nDo you want to remove? Some other package maybe error.";
                bool confirm =
                    EditorUtility.DisplayDialog("Confirm to remove", message, "Remove anyway", "Cancel");
                if (!confirm)
                {
                    Debug.Log($"Remove {packageName} is cancel.");
                    return;
                }
            }

// Thực hiện remove
            RemoveRequest request = Client.Remove(packageName);

            void WaitForRemove()
            {
                if (request.IsCompleted)
                {
                    EditorApplication.update -= WaitForRemove;
                    if (request.Status == StatusCode.Success)
                    {
                        Debug.Log($"Removed {packageName}");
                        RefreshInstalledPackages(); // Cập nhật dictionary installed versions
                        //ScanRepos(); // Cập nhật cache và reverse deps
                        if (selectedPackage?.name == packageName)
                            selectedPackage = null;
                    }
                    else
                    {
                        Debug.LogError($"Remove failed: {request.Error.message}");
                    }
                }
            }

            EditorApplication.update += WaitForRemove;
        }

// Hàm hỗ trợ: Lấy danh sách package installed đang phụ thuộc vào packageName
        private List<string> GetInstalledPackageNamesThatDependOn(string dependencyName)
        {
            List<string> result = new List<string>();
            if (config == null || config.cachedRepoGroups.Count == 0)
                return result;
            foreach (var group in config.cachedRepoGroups)
            {
                foreach (var cachedPkg in group.packages)
                {
// Chỉ kiểm tra các package đang installed
                    if (installedPackageVersions.TryGetValue(cachedPkg.name, out string instVer))
                    {
                        int idx = cachedPkg.versions.IndexOf(instVer);
                        if (idx == -1) continue;
                        var deps = cachedPkg.versionGitHubDependencies[idx];
// Kiểm tra xem package này có phụ thuộc vào dependencyName không
                        bool dependsOn = deps.versions.Any(pair => pair.key == dependencyName);
                        if (dependsOn)
                        {
                            result.Add(cachedPkg.name);
                        }
                    }
                }
            }

            return result;
        }

        private void RefreshInstalledPackages()
        {
            installedPackageVersions.Clear();
            listRequest = Client.List(false, true);
            EditorApplication.update += WaitForListRequest;
        }

        private void WaitForListRequest()
        {
            if (listRequest.IsCompleted)
            {
                EditorApplication.update -= WaitForListRequest;
                if (listRequest.Status == StatusCode.Success)
                {
                    foreach (var pkg in listRequest.Result)
                    {
                        installedPackageVersions[pkg.name] = pkg.version;
                    }
                }

                Repaint();
            }
        }

// ==================== CÁC HÀM PHỤ & CLASS HỖ TRỢ (giữ nguyên như cũ) ====================
        private bool TryParseRepoUrl(string url, out string owner, out string repo)
        {
            owner = null;
            repo = null;
            url = url.Replace(".git", "").TrimEnd('/');
            var parts = url.Split(new[] { '/', ':' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                owner = parts[parts.Length - 2];
                repo = parts[parts.Length - 1];
                return true;
            }

            return false;
        }

        private List<TreeEntry> GetRepoTree(string owner, string repo, string branch, string token)
        {
            string url = $"https://api.github.com/repos/{owner}/{repo}/git/trees/{branch}?recursive=1";
            string json = SendApiRequest(url, token);
            if (string.IsNullOrEmpty(json)) return null;
            TreeResponse response = JsonUtility.FromJson<TreeResponse>(json);
            if (response.truncated) Debug.LogWarning($"Tree truncated for {repo}");
            return response.tree;
        }

        private string GetFileContent(string owner, string repo, string branch, string path, string token)
        {
            string url = $"https://api.github.com/repos/{owner}/{repo}/contents/{path}?ref={branch}";
            string json = SendApiRequest(url, token);
            if (string.IsNullOrEmpty(json)) return null;
            ContentResponse response = JsonUtility.FromJson<ContentResponse>(json);
            if (response.encoding == "base64")
            {
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(response.content));
            }

            return null;
        }

        private List<string> GetVersionsFromTags(string owner, string repo, string packageName, string token)
        {
            List<string> versions = new List<string>();
            string pattern = $"publish/{packageName}=";
            int page = 1;
            const int perPage = 100;

            while (true)
            {
                string url = $"https://api.github.com/repos/{owner}/{repo}/tags?per_page={perPage}&page={page}";
                string json = SendApiRequest(url, token);
                if (string.IsNullOrEmpty(json)) break;

                TagResponse[] tags = JsonUtility.FromJson<WrappedTagResponse>("{\"tags\":" + json + "}").tags;
                if (tags == null || tags.Length == 0) break;

                foreach (var tag in tags)
                {
                    if (tag.name.StartsWith(pattern))
                    {
                        versions.Add(tag.name.Substring(pattern.Length));
                    }
                }

                // If we got less than perPage results, we've reached the last page
                if (tags.Length < perPage) break;

                page++;
            }

            versions.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(b, a));
            return versions;
        }

        private string SendApiRequest(string url, string token)
        {
            using (UnityWebRequest www = UnityWebRequest.Get(url))
            {
                www.SetRequestHeader("Accept", "application/vnd.github.v3+json");
                if (!string.IsNullOrEmpty(token))
                {
                    www.SetRequestHeader("Authorization", $"Bearer {token}");
                }

                www.SendWebRequest();
                while (!www.isDone)
                {
                }

                if (www.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"API error: {www.error} - URL: {url}");
                    return null;
                }

                return www.downloadHandler.text;
            }
        }

        private string ExtractPackageName(string jsonContent)
        {
            int searchFrom = 0;
            while (searchFrom < jsonContent.Length)
            {
                int nameIndex = jsonContent.IndexOf("\"name\"", searchFrom);
                if (nameIndex == -1) return null;

                // Skip whitespace after "name" and expect ':'
                int colonIndex = nameIndex + 6;
                while (colonIndex < jsonContent.Length && char.IsWhiteSpace(jsonContent[colonIndex]))
                    colonIndex++;

                if (colonIndex >= jsonContent.Length || jsonContent[colonIndex] != ':')
                {
                    searchFrom = colonIndex;
                    continue;
                }

                int start = jsonContent.IndexOf('"', colonIndex + 1);
                if (start == -1) return null;
                start++;

                int end = jsonContent.IndexOf('"', start);
                if (end == -1) return null;

                string value = jsonContent.Substring(start, end - start);
                if (value.StartsWith("com."))
                    return value;

                searchFrom = end + 1;
            }

            return null;
        }

        private string ExtractField(string json, string field)
        {
            int index = json.IndexOf($"\"{field}\"");
            if (index == -1) return null;

            // Skip whitespace after field name and expect ':'
            int colonIndex = index + field.Length + 2;
            while (colonIndex < json.Length && char.IsWhiteSpace(json[colonIndex]))
                colonIndex++;

            if (colonIndex >= json.Length || json[colonIndex] != ':')
                return null;

            int start = json.IndexOf('"', colonIndex + 1);
            if (start == -1) return null;
            start++;

            int end = json.IndexOf('"', start);
            return end > start ? json.Substring(start, end - start) : null;
        }

        [System.Serializable]
        private class TreeResponse
        {
            public string sha;
            public string url;
            public List<TreeEntry> tree;
            public bool truncated;
        }

        [System.Serializable]
        private class TreeEntry
        {
            public string path;
            public string mode;
            public string type;
            public string sha;
            public int size;
            public string url;
        }

        [System.Serializable]
        private class ContentResponse
        {
            public string name;
            public string path;
            public string sha;
            public int size;
            public string url;
            public string content;
            public string encoding;
        }

        [System.Serializable]
        private class TagResponse
        {
            public string name;
        }

        [System.Serializable]
        private class WrappedTagResponse
        {
            public TagResponse[] tags;
        }

        // Wrapper class to allow JsonUtility to serialize a List<T>
        [System.Serializable]
        private class PackageListWrapper
        {
            public List<RepoConfigSO.CachedPackageInfo> packages;
        }
    }
}