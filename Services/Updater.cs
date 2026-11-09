using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InteractiveMenu.Services
{
    /// <summary>
    /// Автообновление из GitHub Releases (как в Dynamic Island):
    /// проверяет последний релиз → качает zip → сверяет SHA256 → распаковывает →
    /// cmd-скрипт ждёт, пока приложение закроется, копирует файлы поверх и запускает новую версию.
    /// Если новая версия не подтвердила запуск за 45 сек — откат на прежнюю.
    /// </summary>
    public sealed class Updater : IDisposable
    {
        private static readonly HttpClient Http = CreateClient();

        private readonly Func<bool> _autoUpdateEnabled;
        private readonly Action<string, string> _notify;
        private readonly Action _shutdown;
        private readonly SemaphoreSlim _busy = new SemaphoreSlim(1, 1);
        private Timer _timer;

        /// <summary>Текст для страницы «Обновления» в настройках.</summary>
        public string Status { get; private set; } = "Ещё не проверял";
        public string LatestVersion { get; private set; }
        public bool IsBusy { get; private set; }

        /// <summary>Вызывается из фонового потока — подписчики сами переходят в UI-поток.</summary>
        public event Action StatusChanged;

        public Updater(Func<bool> autoUpdateEnabled, Action<string, string> notify, Action shutdown)
        {
            _autoUpdateEnabled = autoUpdateEnabled;
            _notify = notify;
            _shutdown = shutdown;
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"InteractiveMenu-Updater/{AppInfo.Version}");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        public void Start()
        {
            // первая проверка через 15 сек после запуска, потом каждые 3 часа
            _timer = new Timer(_ => _ = CheckAsync(manual: false), null, TimeSpan.FromSeconds(15), TimeSpan.FromHours(3));
        }

        public async Task CheckAsync(bool manual)
        {
            if (!manual && !_autoUpdateEnabled()) return;
            if (!await _busy.WaitAsync(0)) return;

            try
            {
                SetStatus("Проверяю…", busy: true);

                if (AppInfo.IsDevBuild)
                {
                    SetStatus("Локальная сборка — обновления приходят только в версию из GitHub Releases");
                    if (manual) _notify("Это локальная сборка", "Обновления работают только у версии, скачанной из GitHub Releases");
                    return;
                }

                var release = await FetchLatestAsync();
                if (release == null)
                {
                    SetStatus("Не удалось проверить: нет сети или GitHub недоступен");
                    if (manual) _notify("Не удалось проверить обновления", "Нет сети или GitHub недоступен");
                    return;
                }

                var current = ParseVersion(AppInfo.Version);
                var latest = ParseVersion(release.Tag);
                LatestVersion = latest?.ToString();

                if (latest == null || current == null || latest <= current)
                {
                    SetStatus($"Установлена последняя версия · проверено в {DateTime.Now:HH:mm}");
                    if (manual) _notify("Обновлений нет", $"У тебя последняя версия {AppInfo.Version}");
                    return;
                }

                string newVersion = latest.ToString();

                // версию, которую уже откатывали (не запустилась), ставим только вручную
                if (!manual && IsBadVersion(newVersion))
                {
                    SetStatus($"Версия {newVersion} пропущена: в прошлый раз она не запустилась");
                    return;
                }

                Log.Info($"Доступна версия {newVersion} (сейчас {AppInfo.Version})");
                await InstallAsync(release, newVersion);
            }
            catch (Exception ex)
            {
                Log.Error("Проверка обновлений", ex);
                SetStatus("Ошибка: " + ex.Message);
                if (manual) _notify("Ошибка обновления", ex.Message);
            }
            finally
            {
                IsBusy = false;
                StatusChanged?.Invoke();
                _busy.Release();
            }
        }

        private void SetStatus(string text, bool busy = false)
        {
            Status = text;
            IsBusy = busy;
            StatusChanged?.Invoke();
        }

        // ------------------------------------------------------------ GitHub

        private sealed record Release(string Tag, string ZipUrl, string ShaUrl, string Notes);

        private static async Task<Release> FetchLatestAsync()
        {
            try
            {
                string url = $"https://api.github.com/repos/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/latest";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                var root = doc.RootElement;

                string tag = root.GetProperty("tag_name").GetString() ?? "";
                string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

                string zip = null, sha = null;
                foreach (var asset in root.GetProperty("assets").EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString();
                    string assetUrl = asset.GetProperty("browser_download_url").GetString();
                    if (name == AppInfo.ReleaseAsset) zip = assetUrl;
                    else if (name == AppInfo.ReleaseAsset + ".sha256") sha = assetUrl;
                }

                return zip == null ? null : new Release(tag, zip, sha, notes);
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось получить релиз: " + ex.Message);
                return null;
            }
        }

        private static Version ParseVersion(string text)
        {
            text = (text ?? "").Trim().TrimStart('v', 'V');
            int dash = text.IndexOf('-');
            if (dash >= 0) text = text.Substring(0, dash);
            return System.Version.TryParse(text, out var v) ? v : null;
        }

        // ------------------------------------------------------------ установка

        private async Task InstallAsync(Release release, string version)
        {
            try
            {
                SetStatus($"Скачиваю версию {version}…", busy: true);
                _notify($"Обновляюсь до {version}", "Скачиваю, через пару секунд перезапущусь");

                string work = Path.Combine(Path.GetTempPath(), "InteractiveMenu-update");
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
                Directory.CreateDirectory(work);

                string zipPath = Path.Combine(work, "update.zip");
                string staging = Path.Combine(work, "files");

                using (var response = await Http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    await using var input = await response.Content.ReadAsStreamAsync();
                    await using var output = File.Create(zipPath);
                    await input.CopyToAsync(output);
                }

                // контрольная сумма — защита от битого/подменённого архива
                if (release.ShaUrl != null)
                {
                    SetStatus("Проверяю архив…", busy: true);
                    string expected = (await Http.GetStringAsync(release.ShaUrl)).Trim().Split(' ', '\t', '\r', '\n')[0];
                    string actual;
                    await using (var zip = File.OpenRead(zipPath))
                        actual = Convert.ToHexString(await SHA256.HashDataAsync(zip));

                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("контрольная сумма архива не совпала — файл повреждён");
                }
                else
                {
                    Log.Warn("В релизе нет .sha256 — ставлю без проверки");
                }

                SetStatus("Распаковываю…", busy: true);
                ZipFile.ExtractToDirectory(zipPath, staging);
                if (!File.Exists(Path.Combine(staging, "InteractiveMenu.exe")))
                    throw new InvalidOperationException("в архиве нет InteractiveMenu.exe");

                string installDir = AppContext.BaseDirectory.TrimEnd('\\');
                string backupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InteractiveMenu", "backup");
                string script = Path.Combine(work, "apply-update.cmd");
                File.Delete(UpdateOkFile);
                File.WriteAllText(script, BuildScript(installDir, staging, backupDir, version), new UTF8Encoding(false));

                SetStatus("Перезапускаюсь…", busy: true);
                Log.Info($"Ставлю {version} в {installDir}");
                await Task.Delay(500);

                bool needAdmin = !CanWrite(installDir);
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = needAdmin, // runas работает только через ShellExecute
                    Verb = needAdmin ? "runas" : "",
                    WindowStyle = ProcessWindowStyle.Hidden
                });

                _shutdown();
            }
            catch (Exception ex)
            {
                Log.Error("Установка обновления", ex);
                SetStatus("Не удалось установить: " + ex.Message);
                _notify("Не удалось обновиться", ex.Message);
            }
        }

        /// <summary>
        /// cmd-скрипт обновления. Пауза — через ping (timeout не работает без консоли).
        /// 1) ждём, пока приложение закроется; 2) копия текущей версии в backup;
        /// 3) кладём новые файлы; 4) запускаем и ждём «я жив» (update-ok) до 45 сек;
        /// 5) не дождались — возвращаем backup и запускаем старую версию с --rollback.
        /// </summary>
        private static string BuildScript(string install, string staging, string backup, string version)
        {
            int pid = Environment.ProcessId;
            string exe = Path.Combine(install, "InteractiveMenu.exe");
            const string quiet = "/NFL /NDL /NJH /NJS /NP >nul";

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine($"set \"OK={UpdateOkFile}\"");
            sb.AppendLine(":wait");
            sb.AppendLine($"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (ping -n 2 127.0.0.1 >nul & goto wait)");
            sb.AppendLine("ping -n 2 127.0.0.1 >nul");
            sb.AppendLine($"robocopy \"{install}\" \"{backup}\" /MIR /R:2 /W:1 {quiet}");
            sb.AppendLine($"robocopy \"{staging}\" \"{install}\" /E /R:10 /W:1 {quiet}");
            sb.AppendLine($"start \"\" \"{exe}\" --updated {version}");
            sb.AppendLine("set /a n=0");
            sb.AppendLine(":health");
            sb.AppendLine("ping -n 2 127.0.0.1 >nul");
            sb.AppendLine("if exist \"%OK%\" goto healthy");
            sb.AppendLine("set /a n+=1");
            sb.AppendLine("if %n% lss 45 goto health");
            sb.AppendLine("rem новая версия не подтвердила, что запустилась — откат");
            sb.AppendLine("taskkill /im InteractiveMenu.exe /f >nul 2>&1");
            sb.AppendLine("ping -n 2 127.0.0.1 >nul");
            sb.AppendLine($"robocopy \"{backup}\" \"{install}\" /MIR /R:5 /W:1 {quiet}");
            sb.AppendLine($"start \"\" \"{exe}\" --rollback {version}");
            sb.AppendLine("goto done");
            sb.AppendLine(":healthy");
            sb.AppendLine("del \"%OK%\" >nul 2>&1");
            sb.AppendLine(":done");
            sb.AppendLine($"rmdir /s /q \"{staging}\" >nul 2>&1");
            sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
            return sb.ToString();
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------ «я жив» и откат

        public static string UpdateOkFile => Path.Combine(ConfigService.Dir, "update-ok");
        private static string BadVersionFile => Path.Combine(ConfigService.Dir, "bad-version.txt");

        /// <summary>Новая версия сообщает скрипту обновления, что запустилась нормально.</summary>
        public static void ConfirmHealthy()
        {
            try
            {
                Directory.CreateDirectory(ConfigService.Dir);
                File.WriteAllText(UpdateOkFile, AppInfo.Version);
                Log.Info("Обновление подтверждено: приложение работает");
            }
            catch { }
        }

        public static void MarkBadVersion(string version)
        {
            try
            {
                Directory.CreateDirectory(ConfigService.Dir);
                File.WriteAllText(BadVersionFile, version.Trim());
            }
            catch { }
            Log.Warn($"Версия {version} не запустилась — откатились и пропускаем её");
        }

        private static bool IsBadVersion(string version)
        {
            try { return File.Exists(BadVersionFile) && File.ReadAllText(BadVersionFile).Trim() == version; }
            catch { return false; }
        }

        public void Dispose() => _timer?.Dispose();
    }
}
