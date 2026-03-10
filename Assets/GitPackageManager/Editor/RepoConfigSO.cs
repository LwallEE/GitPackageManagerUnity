using System.Collections.Generic;
using UnityEngine;

namespace Lwalle.GitPackageManager
{
    [CreateAssetMenu(fileName = "RepoConfig", menuName = "Repo Config", order = 1)]
    public class RepoConfigSO : ScriptableObject
    {
        [System.Serializable]
        public class RepoEntry
        {
            public string repoUrl;
            public string defaultBranch = "main";
            public bool isPrivate = false;
        }

        public List<RepoEntry> repos = new List<RepoEntry>();

// ==================== KEY-VALUE ĐỂ THAY DICTIONARY ====================
        [System.Serializable]
        public class StringPair
        {
            public string key;
            public string value;
        }

        [System.Serializable]
        public class DependencyVersion
        {
            public List<StringPair> versions = new List<StringPair>();

            public DependencyVersion(List<StringPair> versions)
            {
                this.versions = versions;
            }

            public DependencyVersion()
            {
                this.versions = new List<StringPair>();
            }
        }

// ==================== CACHE DỮ LIỆU SCAN ====================
        [System.Serializable]
        public class CachedPackageInfo
        {
            public string name;
            public string displayName;
            public string description;
            public string repoUrl;
            public string subPath;
            public string branch;
            public List<string> versions = new List<string>();

            public int selectedVersionIndex = 0;
            public CachedPackageInfo Clone()
            {
                return (CachedPackageInfo)this.MemberwiseClone();
            }
// Dependencies per version
            public List<DependencyVersion> versionGitHubDependencies = new List<DependencyVersion>();
            public List<DependencyVersion> versionReverseDependencies = new List<DependencyVersion>();
        }

        [System.Serializable]
        public class CachedRepoGroup
        {
            public string repoUrl;
            public string repoName;
            public bool isExpanded = true;
            public List<CachedPackageInfo> packages = new List<CachedPackageInfo>();
        }

        public List<CachedRepoGroup> cachedRepoGroups = new List<CachedRepoGroup>();
    }
}