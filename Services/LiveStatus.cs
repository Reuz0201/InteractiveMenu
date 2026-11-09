using System;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace InteractiveMenu.Services
{
    /// <summary>
    /// «Живые состояния» для шариков: громкость, mute, устройство вывода, что играет, яркость.
    /// Быстрое (звук) читается сразу, медленное (трек, яркость по DDC) — в фоне.
    /// </summary>
    public sealed class LiveStatus
    {
        public int? Volume { get; private set; }
        public bool Muted { get; private set; }
        public string OutputDevice { get; private set; }
        public string MediaTitle { get; private set; }
        public bool? MediaPlaying { get; private set; }
        public int? BrightnessLevel { get; private set; }

        /// <summary>Быстрые значения — можно звать в UI-потоке при открытии меню.</summary>
        public static LiveStatus ReadFast()
        {
            var s = new LiveStatus();
            try
            {
                var v = AudioDevices.GetVolume();
                if (v != null)
                {
                    s.Volume = v.Value.Volume;
                    s.Muted = v.Value.Muted;
                }
                s.OutputDevice = AudioDevices.GetDefaultOutput()?.Name;
            }
            catch (Exception ex)
            {
                Log.Warn("Живой статус (звук): " + ex.Message);
            }
            return s;
        }

        /// <summary>Медленные значения — в фоне, потом UI обновит шарики.</summary>
        public async Task ReadSlowAsync(bool needMedia, bool needBrightness)
        {
            if (needMedia)
            {
                try
                {
                    var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                    var session = manager.GetCurrentSession();
                    if (session != null)
                    {
                        var props = await session.TryGetMediaPropertiesAsync();
                        string title = props?.Title ?? "";
                        string artist = props?.Artist ?? "";
                        MediaTitle = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} — {title}";
                        if (string.IsNullOrWhiteSpace(MediaTitle)) MediaTitle = null;
                        MediaPlaying = session.GetPlaybackInfo()?.PlaybackStatus ==
                                       GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Живой статус (медиа): " + ex.Message);
                }
            }

            if (needBrightness)
            {
                try
                {
                    BrightnessLevel = await Task.Run(() => Brightness.Get());
                }
                catch (Exception ex)
                {
                    Log.Warn("Живой статус (яркость): " + ex.Message);
                }
            }
        }
    }
}
