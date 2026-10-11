using System.Security.Cryptography;
using System.Text;
using Batterio.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Batterio.App;

/// <summary>
/// Resolves row images and caches transparent grayscale variants for offline devices.
/// </summary>
public static class BatteryImageHelper
{
    public static string ResolveSource(DeviceConfiguration configuration, bool offline)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Batterio");
        var configuredPath = TryResolveConfiguredPhoto(configuration, appDataDirectory);
        var source = configuredPath ?? GetFallbackImageSource(configuration.Id);
        if (!offline)
        {
            return source;
        }

        var sourcePath = ResolvePackagedPath(source);
        return sourcePath is null ? source : CreateGrayscaleVariant(sourcePath, appDataDirectory) ?? source;
    }

    private static string? TryResolveConfiguredPhoto(DeviceConfiguration configuration, string appDataDirectory)
    {
        try
        {
            var path = configuration.ResolvePhotoPath(appDataDirectory);
            return path is not null && File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string GetFallbackImageSource(string deviceId) => deviceId switch
    {
        "logitech-g-pro-x2-superstrike" => "superstrike.png",
        "logitech-g-pro-x2-lightspeed" => "lightspeed.webp",
        "steelseries-arctis-nova-pro-omni" => "arctis_nova_pro_omni_black.png",
        "logitech-mx-master-3s" => "mx_master_3s.webp",
        "logitech-mx-keys-mechanical" => "mx_mechanical.png",
        _ => "battery_placeholder.scale-100.png"
    };

    private static string? ResolvePackagedPath(string source)
    {
        if (Path.IsPathFullyQualified(source) && File.Exists(source))
        {
            return source;
        }

        var sourceName = Path.GetFileNameWithoutExtension(source);
        var extension = Path.GetExtension(source);
        var packagedName = $"{sourceName}.scale-100{extension}";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, source),
            Path.Combine(AppContext.BaseDirectory, packagedName),
            Path.Combine(AppContext.BaseDirectory, "Resources", "Images", source),
            Path.Combine(AppContext.BaseDirectory, "Resources", "Images", packagedName)
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? CreateGrayscaleVariant(string sourcePath, string appDataDirectory)
    {
        string? temporary = null;
        try
        {
            var sourceInfo = new FileInfo(sourcePath);
            var key = $"{Path.GetFullPath(sourcePath)}|{sourceInfo.Length}|{sourceInfo.LastWriteTimeUtc.Ticks}";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
            var cacheDirectory = Path.Combine(appDataDirectory, "ImageCache");
            Directory.CreateDirectory(cacheDirectory);
            var destination = Path.Combine(cacheDirectory, $"{hash}.gray.png");
            if (File.Exists(destination))
            {
                return destination;
            }

            temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
            CreateGrayscalePng(sourcePath, temporary);
            File.Move(temporary, destination, overwrite: true);
            temporary = null;
            return destination;
        }
        catch (Exception)
        {
            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception)
                {
                }
            }

            return null;
        }
    }

    private static void CreateGrayscalePng(string sourcePath, string destination)
    {
        var sourceFile = StorageFile.GetFileFromPathAsync(sourcePath)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        using var sourceStream = RandomAccessStreamReference.CreateFromFile(sourceFile)
            .OpenReadAsync()
            .AsTask()
            .GetAwaiter()
            .GetResult();
        var decoder = BitmapDecoder.CreateAsync(sourceStream)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        using var bitmap = decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        using (var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.ReadWrite))
        using (var reference = buffer.CreateReference())
        {
            unsafe
            {
                ((IMemoryBufferByteAccess)reference).GetBuffer(out var data, out _);
                var plane = buffer.GetPlaneDescription(0);
                for (var y = 0; y < plane.Height; y++)
                {
                    var row = data + plane.StartIndex + y * plane.Stride;
                    for (var x = 0; x < plane.Width; x++)
                    {
                        var pixel = row + x * 4;
                        var luminance = (byte)Math.Clamp(
                            (0.114 * pixel[0]) + (0.587 * pixel[1]) + (0.299 * pixel[2]),
                            0,
                            255);
                        pixel[0] = luminance;
                        pixel[1] = luminance;
                        pixel[2] = luminance;
                    }
                }
            }
        }

        using var outputStream = new InMemoryRandomAccessStream();
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        encoder.SetSoftwareBitmap(bitmap);
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
        outputStream.Seek(0);
        using var output = outputStream.AsStreamForRead();
        using var file = File.Create(destination);
        output.CopyTo(file);
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("5B0D3235-4DBA-4D44-865E-8F1D0A5A1C5A")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
