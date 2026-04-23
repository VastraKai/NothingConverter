using System;
using System.IO;
using System.Threading.Tasks;
using Android.Content;
using Android.OS;
using Android.Provider;
using Ffmpegkit.Droid;

#pragma warning disable CA1416

namespace NothingConverter;

public enum MediaCategory
{
    Audio,
    Video,
    Image
}

public static class FormatInfo
{
    public record OutputFormat(string Extension, string MimeType, MediaCategory Category, string FfmpegArgs);

    public static readonly OutputFormat[] All =
    {
        new("mp3", "audio/mpeg", MediaCategory.Audio, "-codec:a libmp3lame -qscale:a 0 -b:a 320k"),
        new("aac", "audio/aac", MediaCategory.Audio, "-codec:a aac -b:a 256k"),
        new("m4a", "audio/mp4", MediaCategory.Audio, "-codec:a aac -b:a 256k"),
        new("ogg", "audio/ogg", MediaCategory.Audio, "-codec:a libvorbis -qscale:a 6"),
        new("opus", "audio/opus", MediaCategory.Audio, "-codec:a libopus -b:a 128k"),
        new("flac", "audio/flac", MediaCategory.Audio, "-codec:a flac"),
        new("wav", "audio/wav", MediaCategory.Audio, "-codec:a pcm_s16le"),
        new("wma", "audio/x-ms-wma", MediaCategory.Audio, "-codec:a wmav2 -b:a 192k"),
        new("aiff", "audio/aiff", MediaCategory.Audio, "-codec:a pcm_s16be"),
        new("amr", "audio/amr", MediaCategory.Audio, "-codec:a libopencore_amrnb -ar 8000 -ac 1"),

        new("mp4", "video/mp4", MediaCategory.Video, "-codec:v libx264 -preset fast -crf 22 -codec:a aac -b:a 192k"),
        new("mkv", "video/x-matroska", MediaCategory.Video,
            "-codec:v libx264 -preset fast -crf 22 -codec:a aac -b:a 192k"),
        new("webm", "video/webm", MediaCategory.Video, "-codec:v libvpx-vp9 -crf 30 -b:v 0 -codec:a libopus -b:a 128k"),
        new("mov", "video/quicktime", MediaCategory.Video,
            "-codec:v libx264 -preset fast -crf 22 -codec:a aac -b:a 192k"),
        new("avi", "video/x-msvideo", MediaCategory.Video,
            "-codec:v libx264 -preset fast -crf 22 -codec:a mp3 -b:a 192k"),
        new("3gp", "video/3gpp", MediaCategory.Video, "-codec:v libx264 -preset fast -crf 28 -codec:a aac -b:a 96k"),
        new("ts", "video/mp2t", MediaCategory.Video, "-codec:v libx264 -preset fast -crf 22 -codec:a aac -b:a 192k"),

        new("gif", "image/gif", MediaCategory.Image, "-vf fps=10,scale=480:-1:flags=lanczos -loop 0"),
        new("webp", "image/webp", MediaCategory.Image,
            "-vf fps=10,scale=480:-1:flags=lanczos -loop 0 -codec:v libwebp -lossless 0 -q:v 80"),
        new("avif", "image/avif", MediaCategory.Image,
            "-vf fps=30,scale=480:-1:flags=lanczos -codec:v libaom-av1 -crf 30 -b:v 0")
    };

    public static string DefaultRelativePath(MediaCategory cat) => cat switch
    {
        MediaCategory.Video => "Movies",
        MediaCategory.Image => "Pictures/Converted",
        _ => "Recordings/Voice Recorder"
    };

    public static Android.Net.Uri MediaStoreCollection(MediaCategory cat) =>
        Build.VERSION.SdkInt >= BuildVersionCodes.Q
            ? cat switch
            {
                MediaCategory.Video => MediaStore.Video.Media.GetContentUri(MediaStore.VolumeExternalPrimary)!,
                MediaCategory.Image => MediaStore.Images.Media.GetContentUri(MediaStore.VolumeExternalPrimary)!,
                _ => MediaStore.Audio.Media.GetContentUri(MediaStore.VolumeExternalPrimary)!
            }
            : cat switch
            {
                MediaCategory.Video => MediaStore.Video.Media.ExternalContentUri!,
                MediaCategory.Image => MediaStore.Images.Media.ExternalContentUri!,
                _ => MediaStore.Audio.Media.ExternalContentUri!
            };

    public static string? GetMimeTypeForExtension(string ext) =>
        All.FirstOrDefault(f => f.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase))?.MimeType;
}

// Stupid ass callback system
public class FFmpegStatisticsCallback : Java.Lang.Object, IStatisticsCallback
{
    private readonly Action<int> _onProgress;
    private readonly long _totalDurationMs;

    public FFmpegStatisticsCallback(long totalDurationMs, Action<int> onProgress)
    {
        _totalDurationMs = totalDurationMs;
        _onProgress = onProgress;
    }

    public void Apply(Statistics stats)
    {
        Log.Info("NothingEncoder", $"got stats: {stats.Time}ms");

        if (_totalDurationMs <= 0) return;
        double progress = (stats.Time * 100.0) / _totalDurationMs;
        _onProgress?.Invoke(Math.Min(100, (int)progress));
    }
}

public class FFmpegCompleteCallback : Java.Lang.Object, IFFmpegSessionCompleteCallback
{
    private readonly TaskCompletionSource<bool> _tcs;
    public FFmpegCompleteCallback(TaskCompletionSource<bool> tcs) => _tcs = tcs;
    public void Apply(FFmpegSession session) => _tcs.TrySetResult(ReturnCode.IsSuccess(session.ReturnCode));
}

public class FFprobeCompleteCallback(TaskCompletionSource<long> tcs)
    : Java.Lang.Object, IMediaInformationSessionCompleteCallback
{
    private readonly TaskCompletionSource<long> _tcs = tcs;

    public void Apply(MediaInformationSession session)
    {
        if (ReturnCode.IsSuccess(session.ReturnCode) && session.MediaInformation != null)
        {
            string durationStr = session.MediaInformation.Duration; 
            
            if (double.TryParse(durationStr, System.Globalization.NumberStyles.Any, 
                    System.Globalization.CultureInfo.InvariantCulture, out double seconds))
            {
                _tcs.TrySetResult((long)(seconds * 1000));
                return;
            }
        }
        
        Log.Warn("NothingEncoder", $"FFprobe failed or couldn't parse. Logs: {session.AllLogsAsString}");
        _tcs.TrySetResult(0);
    }
}

public static class ConverterService
{
    public static string LastSavedFile = string.Empty;
    public static string LastSavedRelativePath = string.Empty;

    public static async Task<long> GetDurationMsAsync(string inputPath)
    {
        int retry = 0;
        while (!File.Exists(inputPath) && retry < 5)
        {
            await Task.Delay(100).ConfigureAwait(false);
            retry++;
        }

        var tcs = new TaskCompletionSource<long>();
    
        FFprobeKit.GetMediaInformationAsync(inputPath, new FFprobeCompleteCallback(tcs));

        var result = await tcs.Task.ConfigureAwait(false);
        Log.Info("NothingEncoder_debug", $"Total duration parsed: {result}ms");
        return result;
    }

    private static long _lastUpdateMs = 0;
    
    public static async Task<string> ConvertAndSaveAsync(
        Context context,
        string inputPath,
        string outputBaseName,
        FormatInfo.OutputFormat format,
        string? customOutputPath = null,
        IProgress<string>? progress = null)
    {
        //Log.Info("NothingEncoder_debug", $"Starting conversion: {inputPath} to format {format.Extension}");
        var cacheDir = context.CacheDir!.AbsolutePath;
        var outFileName = Path.ChangeExtension(outputBaseName, format.Extension);
        var tempOutputPath = Path.Combine(cacheDir, outFileName);

        //Log.Info("NothingEncoder_debug", $"Temporary output path: {tempOutputPath}");
        if (File.Exists(tempOutputPath)) File.Delete(tempOutputPath);

        progress?.Report("ENCODING...");

        var cmd = $"-i \"{inputPath}\" {format.FfmpegArgs} -stats_period 0.5 \"{tempOutputPath}\"";
        //Log.Info("NothingEncoder_debug", $"FFmpeg command: {cmd}");

        long totalDurationMs = await GetDurationMsAsync(inputPath).ConfigureAwait(false);
        //Log.Info("NothingEncoder_debug", $"Total duration of input file: {totalDurationMs}ms");
        var tcs = new TaskCompletionSource<bool>();

        //Log.Info("NothingEncoder", $"Executing FFmpeg with command: {cmd}");
        

        var statsCallback = new FFmpegStatisticsCallback(totalDurationMs, p =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastUpdateMs >= 500)
            {
                progress?.Report($"ENCODING... {p}%");
                _lastUpdateMs = now;
            }
        });

        FFmpegKit.ExecuteAsync(
            cmd,
            new FFmpegCompleteCallback(tcs),
            null,
            new FFmpegStatisticsCallback(totalDurationMs, p => progress?.Report($"ENCODING... {p}%")));

        await tcs.Task.ConfigureAwait(false);
        //Log.Info("NothingEncoder_debug", $"FFmpeg conversion completed");
        
        FFmpegKitConfig.ClearSessions();

        progress?.Report("SAVING...");

        var relativePath = !string.IsNullOrWhiteSpace(customOutputPath)
            ? customOutputPath.Trim().Trim('/')
            : FormatInfo.DefaultRelativePath(format.Category);

        var savedUri = await Task.Run(() =>
            SaveToMediaStore(context, tempOutputPath, outFileName, format, relativePath)).ConfigureAwait(false);
        LastSavedRelativePath = relativePath;

        return savedUri;
    }

    private static string SaveToMediaStore(
        Context context,
        string sourcePath,
        string fileName,
        FormatInfo.OutputFormat format,
        string relativePath)
    {
        var resolver = context.ContentResolver!;
        var values = new ContentValues();

        Log.Info("NothingEncoder_debug",
            $"Saving to MediaStore with filename: {fileName}, mime type: {format.MimeType}, relative path: {relativePath}");

        values.Put("_display_name", fileName);
        values.Put("title", Path.GetFileNameWithoutExtension(fileName));
        values.Put("mime_type", format.MimeType);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            values.Put("relative_path", relativePath);
            values.Put("is_pending", 1);
        }

        var collection = FormatInfo.MediaStoreCollection(format.Category);

        var itemUri = resolver.Insert(collection, values)
                      ?? throw new IOException("MediaStore insert returned null");

        using (var input = File.OpenRead(sourcePath))
        using (var output = resolver.OpenOutputStream(itemUri)!)
            input.CopyTo(output);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
        {
            values.Clear();
            values.Put("is_pending", 0);
            resolver.Update(itemUri, values, null, null);
        }

        LastSavedFile = itemUri.ToString() ?? string.Empty;
        return LastSavedFile;
    }

    public static string CopyUriToCache(Context context, Android.Net.Uri uri)
    {
        var ext = GetExtensionFromUri(context, uri) ?? "bin";
        var tempFile = Path.Combine(
            context.CacheDir!.AbsolutePath,
            $"input_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{ext}");

        using (var input = context.ContentResolver!.OpenInputStream(uri)!)
        using (var output = File.Create(tempFile))
        {
            input.CopyTo(output);
            output.Flush();
        }
    
        return tempFile;
    }
    public static string GetFileNameFromUri(Context context, Android.Net.Uri uri)
    {
        string? name = null;
        if (uri.Scheme == "content")
        {
            using var cursor = context.ContentResolver!.Query(uri, null, null, null, null);
            if (cursor != null && cursor.MoveToFirst())
            {
                var idx = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                if (idx >= 0) name = cursor.GetString(idx);
                cursor.Close();
            }
        }

        return name ?? Path.GetFileName(uri.Path) ?? "converted";
    }

    private static string? GetExtensionFromUri(Context context, Android.Net.Uri uri)
    {
        if (uri.Scheme == "content")
        {
            var mime = context.ContentResolver!.GetType(uri);
            if (mime != null)
                return mime switch
                {
                    "audio/mpeg" => "mp3",
                    "audio/ogg" or "audio/vorbis" => "ogg",
                    "audio/flac" => "flac",
                    "audio/aac" or "audio/mp4" => "m4a",
                    "audio/wav" or "audio/x-wav" => "wav",
                    "audio/opus" => "opus",
                    "audio/amr" => "amr",
                    "video/mp4" => "mp4",
                    "video/x-matroska" => "mkv",
                    "video/webm" => "webm",
                    "video/quicktime" => "mov",
                    "video/x-msvideo" => "avi",
                    "video/3gpp" => "3gp",
                    "image/gif" => "gif",
                    _ => "bin"
                };
        }

        return Path.GetExtension(uri.Path)?.TrimStart('.');
    }
}