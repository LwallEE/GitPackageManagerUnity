
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Lwalle.GitPackageManager
{
    public static class Helper
    {
        public static T LoadOrCreateSOAtPath<T>(string path) where T : ScriptableObject
        {
            // Kiểm tra nếu ScriptableObject đã tồn tại chưa
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null)
            {
                return asset as T;
            }

            Debug.Log($"Creating new ScriptableObject at {path}");
            // Tạo thư mục nếu chưa có
            string folder = Path.GetDirectoryName(path);
            CreateFolderIfNotExists(folder);

            // Tạo instance ScriptableObject
            T settings = ScriptableObject.CreateInstance<T>();

            // Khởi tạo giá trị mặc định nếu cần
            // settings.someValue = 100;

            AssetDatabase.CreateAsset(settings, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return settings;
        }

        public static void CreateFolderIfNotExists(string fullPath)
        {
            fullPath = fullPath.Replace("\\", "/");

            if (AssetDatabase.IsValidFolder(fullPath)) return;

            string parent = Path.GetDirectoryName(fullPath).Replace("\\", "/");
            string folderName = Path.GetFileName(fullPath);

            // Nếu thư mục cha chưa tồn tại → tạo cha trước
            if (!AssetDatabase.IsValidFolder(parent))
                CreateFolderIfNotExists(parent);

            AssetDatabase.CreateFolder(parent, folderName);
        }
    }
}