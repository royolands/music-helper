using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace MusicHelper
{
    public class MediaData
    {
        public bool isPlaying { get; set; }
        public string? artist { get; set; }
        public string? title { get; set; }
        public string? source { get; set; }
        public string? thumbnail { get; set; } 
        public long durationMs { get; set; } 
        public long positionMs { get; set; } 
        public string? debug { get; set; }
    }

    [JsonSerializable(typeof(MediaData))]
    internal partial class MediaDataContext : JsonSerializerContext { }

    class Program
    {
        static GlobalSystemMediaTransportControlsSessionManager? sessionManager;
        static List<GlobalSystemMediaTransportControlsSession> trackedSessions = new List<GlobalSystemMediaTransportControlsSession>();
        static readonly object lockObj = new object();

        static bool lastIsPlaying = false;
        static string lastArtist = "";
        static string lastTitle = "";
        static long lastDurationMs = 0;
        static long lastPositionMs = 0;
        static DateTime lastEmitTime = DateTime.MinValue;
        static string? cachedThumbnail = null; 

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            EmitDebug("success.");

            try
            {
                sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (sessionManager != null)
                {
                    sessionManager.SessionsChanged += async (s, e) => await UpdateSessionsAsync();
                    await UpdateSessionsAsync();
                }
                else
                {
                    EmitDebug("error GSMTC API");
                }
            }
            catch (Exception ex)
            {
                EmitDebug($"error: {ex.Message}");
            }

            await Task.Delay(-1);
        }

        private static async Task UpdateSessionsAsync()
        {
            if (sessionManager == null) return;

            foreach (var oldSession in trackedSessions)
            {
                try 
                {
                    oldSession.PlaybackInfoChanged -= SessionChanged;
                    oldSession.MediaPropertiesChanged -= SessionChanged;
                    oldSession.TimelinePropertiesChanged -= SessionChanged;
                } 
                catch { }
            }
            trackedSessions.Clear();

            var sessions = sessionManager.GetSessions();
            foreach (var session in sessions)
            {
                var source = session.SourceAppUserModelId?.ToLower() ?? "";
                if (source.Contains("spotify") || source.Contains("yandex"))
                {
                    try 
                    {
                        session.PlaybackInfoChanged += SessionChanged;
                        session.MediaPropertiesChanged += SessionChanged;
                        session.TimelinePropertiesChanged += SessionChanged;
                        trackedSessions.Add(session);
                    }
                    catch { }
                }
            }

            await ReevaluateStateAsync();
        }

        private static async void SessionChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        {
            await ReevaluateStateAsync();
        }

        private static async Task ReevaluateStateAsync()
        {
            GlobalSystemMediaTransportControlsSession? activeSession = null;

            foreach (var session in trackedSessions)
            {
                try 
                {
                    var pbInfo = session.GetPlaybackInfo();
                    if (pbInfo != null && pbInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        activeSession = session; 
                        break;
                    }
                }
                catch { }
            }

            bool newIsPlaying = activeSession != null;

            if (newIsPlaying)
            {
                try
                {
                    var mediaProps = await activeSession!.TryGetMediaPropertiesAsync();
                    var timeline = activeSession.GetTimelineProperties();
                    
                    string newArtist = string.IsNullOrEmpty(mediaProps.Artist) ? "?" : mediaProps.Artist;
                    string newTitle = string.IsNullOrEmpty(mediaProps.Title) ? "?" : mediaProps.Title;
                    long newDurationMs = (long)timeline.EndTime.TotalMilliseconds;
                    long newPositionMs = (long)timeline.Position.TotalMilliseconds;

                    bool isNewTrack = (newTitle != lastTitle || newArtist != lastArtist);
                    bool shouldEmit = false;

                    if (!lastIsPlaying || isNewTrack || newDurationMs != lastDurationMs)
                    {
                        shouldEmit = true; 
                    }
                    else
                    {
                        long elapsedMs = (long)(DateTime.UtcNow - lastEmitTime).TotalMilliseconds;
                        long expectedPositionMs = lastPositionMs + elapsedMs;

                        if (Math.Abs(newPositionMs - expectedPositionMs) > 3000)
                        {
                            shouldEmit = true;
                        }
                    }

                    if (!shouldEmit) return; 

                    if (isNewTrack || cachedThumbnail == null)
                    {
                        cachedThumbnail = await GetThumbnailBase64Async(mediaProps);
                    }

                    lastIsPlaying = true;
                    lastArtist = newArtist;
                    lastTitle = newTitle;
                    lastDurationMs = newDurationMs;
                    lastPositionMs = newPositionMs;
                    lastEmitTime = DateTime.UtcNow;

                    var data = new MediaData
                    {
                        isPlaying = true,
                        artist = newArtist,
                        title = newTitle,
                        source = activeSession.SourceAppUserModelId,
                        thumbnail = cachedThumbnail,
                        durationMs = newDurationMs,
                        positionMs = newPositionMs
                    };

                    EmitJson(JsonSerializer.Serialize(data, MediaDataContext.Default.MediaData));
                }
                catch 
                {
                    if (lastIsPlaying) { lastIsPlaying = false; EmitJson("{\"isPlaying\":false}"); }
                }
            }
            else
            {
                if (lastIsPlaying) 
                {
                    lastIsPlaying = false;
                    EmitJson("{\"isPlaying\":false}");
                }
            }
        }

        private static async Task<string?> GetThumbnailBase64Async(GlobalSystemMediaTransportControlsSessionMediaProperties properties)
        {
            if (properties.Thumbnail == null) return null;
            try
            {
                using var winrtStream = await properties.Thumbnail.OpenReadAsync();
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(winrtStream);
                using var outStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                
                var encodingOptions = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, Windows.Graphics.Imaging.BitmapTypedValue>>
                {
                    new System.Collections.Generic.KeyValuePair<string, Windows.Graphics.Imaging.BitmapTypedValue>(
                        "ImageQuality", new Windows.Graphics.Imaging.BitmapTypedValue(0.7, Windows.Foundation.PropertyType.Single)
                    )
                };
                
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                    Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, outStream, encodingOptions);
                
                var pixelData = await decoder.GetPixelDataAsync();
                encoder.SetPixelData(
                    decoder.BitmapPixelFormat, decoder.BitmapAlphaMode,
                    decoder.PixelWidth, decoder.PixelHeight, decoder.DpiX, decoder.DpiY, pixelData.DetachPixelData());
                
                uint targetSize = 256;
                double scale = Math.Min((double)targetSize / decoder.PixelWidth, (double)targetSize / decoder.PixelHeight);
                if (scale < 1.0)
                {
                    encoder.BitmapTransform.ScaledWidth = (uint)(decoder.PixelWidth * scale);
                    encoder.BitmapTransform.ScaledHeight = (uint)(decoder.PixelHeight * scale);
                    encoder.BitmapTransform.InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant;
                }
                
                await encoder.FlushAsync();
                
                using var ms = new MemoryStream();
                var classicStream = outStream.AsStreamForRead();
                classicStream.Position = 0;
                await classicStream.CopyToAsync(ms);
                return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
            }
            catch { return null; }
        }

        private static void EmitDebug(string message)
        {
            EmitJson(JsonSerializer.Serialize(new MediaData { debug = message }, MediaDataContext.Default.MediaData));
        }

        private static void EmitJson(string json)
        {
            lock (lockObj)
            {
                Console.WriteLine(json);
                Console.Out.Flush();
            }
        }
    }
}