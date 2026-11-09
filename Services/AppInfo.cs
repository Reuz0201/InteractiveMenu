using System;
using System.Reflection;

namespace InteractiveMenu.Services
{
    public static class AppInfo
    {
        public const string RepoOwner = "Reuz0201";
        public const string RepoName = "InteractiveMenu";
        public const string ReleaseAsset = "InteractiveMenu-win-x64.zip";

        public static string RepoUrl => $"https://github.com/{RepoOwner}/{RepoName}";

        public static string Version { get; } = ReadVersion();

        /// <summary>Локальная сборка (не из GitHub Releases) — автообновление отключено.</summary>
        public static bool IsDevBuild =>
            Version.Contains("-dev", StringComparison.OrdinalIgnoreCase) || Version.StartsWith("0.0.0");

        private static string ReadVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            string info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(info)) info = asm.GetName().Version?.ToString(3) ?? "0.0.0";
            int plus = info.IndexOf('+');
            return plus >= 0 ? info.Substring(0, plus) : info;
        }
    }
}
