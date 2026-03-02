using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using ImagingEncoder = System.Drawing.Imaging.Encoder;

namespace OneNoteToNotion.Infrastructure;

/// <summary>
/// 图片处理常量
/// </summary>
public static class ImageProcessingConstants
{
    /// <summary>
    /// Notion API 免费版图片大小限制（字节）
    /// </summary>
    public const int NotionFreeImageSizeLimit = 5 * 1024 * 1024; // 5MB

    /// <summary>
    /// JPEG 压缩质量（0-100）
    /// </summary>
    public const int JpegQuality = 80;

    /// <summary>
    /// 每次缩图缩小的比例（0-1），缩小到原尺寸的 80%
    /// </summary>
    public const double ResizeScale = 0.8;

    /// <summary>
    /// 最大缩图迭代次数
    /// </summary>
    public const int MaxResizeIterations = 10;
}

/// <summary>
/// 图片处理结果
/// </summary>
public sealed class ImageProcessingResult
{
    /// <summary>
    /// 处理后的 Data URI
    /// </summary>
    public required string DataUri { get; init; }

    /// <summary>
    /// 处理是否成功
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// 错误信息（失败时）
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// 原始图片大小（字节）
    /// </summary>
    public int OriginalSize { get; init; }

    /// <summary>
    /// 处理后大小（字节）
    /// </summary>
    public int FinalSize { get; init; }

    /// <summary>
    /// 原始格式（png, jpg 等）
    /// </summary>
    public string OriginalFormat { get; init; } = string.Empty;

    /// <summary>
    /// 最终格式（通常是 jpeg）
    /// </summary>
    public string FinalFormat { get; init; } = string.Empty;

    /// <summary>
    /// 压缩率 (FinalSize / OriginalSize)
    /// </summary>
    public double CompressionRatio =>
        OriginalSize > 0 ? (double)FinalSize / OriginalSize : 0;
}

/// <summary>
/// 图片处理器 - 处理图片压缩、格式转换和缩图
/// </summary>
public sealed class ImageResizer
{
    /// <summary>
    /// 处理图片：转换为 JPEG 格式（80% 质量），超过限制时自动缩图
    /// </summary>
    /// <param name="base64Data">base64 编码的图片数据</param>
    /// <param name="format">原始图片格式（如 "png", "jpg"）</param>
    /// <returns>处理结果</returns>
    public ImageProcessingResult ProcessImage(string base64Data, string format)
    {
        var originalSize = Encoding.UTF8.GetByteCount(base64Data);

        try
        {
            // 解码 base64
            if (!TryDecodeFromBase64(base64Data, out var imageBytes, out var error))
            {
                return new ImageProcessingResult
                {
                    DataUri = string.Empty,
                    Success = false,
                    ErrorMessage = error,
                    OriginalSize = originalSize,
                    FinalSize = 0,
                    OriginalFormat = format,
                    FinalFormat = string.Empty
                };
            }

            // 加载图片
            using var image = LoadImageFromBytes(imageBytes);
            if (image is null)
            {
                return new ImageProcessingResult
                {
                    DataUri = string.Empty,
                    Success = false,
                    ErrorMessage = "无法加载图片数据",
                    OriginalSize = originalSize,
                    FinalSize = 0,
                    OriginalFormat = format,
                    FinalFormat = string.Empty
                };
            }

            // 转换为 JPEG 格式，质量 80%
            var jpegBytes = ConvertToJpeg(image, ImageProcessingConstants.JpegQuality);
            var base64Jpeg = Convert.ToBase64String(jpegBytes);
            var dataUri = $"data:image/jpeg;base64,{base64Jpeg}";
            var currentSize = Encoding.UTF8.GetByteCount(base64Jpeg);

            // 检查大小，超过限制则缩图
            if (currentSize > ImageProcessingConstants.NotionFreeImageSizeLimit)
            {
                var resizedResult = ResizeImageToFitLimit(image, ImageProcessingConstants.JpegQuality);
                if (resizedResult.Success)
                {
                    return new ImageProcessingResult
                    {
                        DataUri = resizedResult.DataUri,
                        Success = true,
                        OriginalSize = originalSize,
                        FinalSize = resizedResult.FinalSize,
                        OriginalFormat = format,
                        FinalFormat = "jpeg"
                    };
                }
                else
                {
                    // 缩图失败，图片超过 5MB 限制，返回失败
                    var overLimitMB = currentSize / 1024.0 / 1024;
                    return new ImageProcessingResult
                    {
                        DataUri = string.Empty,
                        Success = false,
                        ErrorMessage = $"图片压缩后仍超过 5MB 限制（{overLimitMB:F2}MB），无法同步",
                        OriginalSize = originalSize,
                        FinalSize = currentSize,
                        OriginalFormat = format,
                        FinalFormat = "jpeg"
                    };
                }
            }

            return new ImageProcessingResult
            {
                DataUri = dataUri,
                Success = true,
                OriginalSize = originalSize,
                FinalSize = currentSize,
                OriginalFormat = format,
                FinalFormat = "jpeg"
            };
        }
        catch (Exception ex)
        {
            return new ImageProcessingResult
            {
                DataUri = string.Empty,
                Success = false,
                ErrorMessage = $"图片处理异常: {ex.Message}",
                OriginalSize = originalSize,
                FinalSize = 0,
                OriginalFormat = format,
                FinalFormat = string.Empty
            };
        }
    }

    /// <summary>
    /// 尝试从 base64 字符串解码
    /// </summary>
    private static bool TryDecodeFromBase64(string base64Data, out byte[] imageBytes, out string? error)
    {
        imageBytes = Array.Empty<byte>();
        error = null;

        try
        {
            imageBytes = Convert.FromBase64String(base64Data);
            return true;
        }
        catch (FormatException)
        {
            error = "无效的 base64 格式";
            return false;
        }
    }

    /// <summary>
    /// 从字节数组加载图片
    /// </summary>
    private static Image? LoadImageFromBytes(byte[] imageBytes)
    {
        try
        {
            using var stream = new MemoryStream(imageBytes);
            return Image.FromStream(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 将图片转换为 JPEG 格式
    /// </summary>
    private static byte[] ConvertToJpeg(Image image, int quality)
    {
        using var stream = new MemoryStream();

        // 设置 JPEG 编码参数
        var encoder = GetEncoder(ImageFormat.Jpeg);
        var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(ImagingEncoder.Quality, (long)quality);

        image.Save(stream, encoder, encoderParams);
        return stream.ToArray();
    }

    /// <summary>
    /// 获取图片格式的编码器
    /// </summary>
    private static ImageCodecInfo GetEncoder(ImageFormat format)
    {
        var codecs = ImageCodecInfo.GetImageDecoders();
        foreach (var codec in codecs)
        {
            if (codec.FormatID == format.Guid)
            {
                return codec;
            }
        }
        throw new NotSupportedException($"不支持的图片格式: {format}");
    }

    /// <summary>
    /// 缩小图片以适应大小限制
    /// </summary>
    private ImageProcessingResult ResizeImageToFitLimit(Image originalImage, int quality)
    {
        int iterations = 0;
        var currentImage = originalImage;
        Image? resizedImage = null;

        try
        {
            while (iterations < ImageProcessingConstants.MaxResizeIterations)
            {
                // 计算新尺寸
                var newWidth = (int)(currentImage.Width * ImageProcessingConstants.ResizeScale);
                var newHeight = (int)(currentImage.Height * ImageProcessingConstants.ResizeScale);

                // 创建缩小的图片
                resizedImage = new Bitmap(newWidth, newHeight);
                using var graphics = Graphics.FromImage(resizedImage);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.DrawImage(currentImage, 0, 0, newWidth, newHeight);

                // 如果不是第一次迭代，释放之前的图片
                if (currentImage != originalImage)
                {
                    currentImage.Dispose();
                }
                currentImage = resizedImage;

                // 转换为 JPEG 并检查大小
                var jpegBytes = ConvertToJpeg(currentImage, quality);
                var base64Jpeg = Convert.ToBase64String(jpegBytes);
                var dataSize = Encoding.UTF8.GetByteCount(base64Jpeg);

                if (dataSize <= ImageProcessingConstants.NotionFreeImageSizeLimit)
                {
                    var dataUri = $"data:image/jpeg;base64,{base64Jpeg}";
                    return new ImageProcessingResult
                    {
                        DataUri = dataUri,
                        Success = true,
                        FinalSize = dataSize
                    };
                }

                iterations++;
            }

            // 达到最大迭代次数仍未满足要求
            return new ImageProcessingResult
            {
                DataUri = string.Empty,
                Success = false,
                ErrorMessage = $"图片经 {ImageProcessingConstants.MaxResizeIterations} 次压缩后仍超过限制"
            };
        }
        finally
        {
            // 清理
            if (currentImage != originalImage)
            {
                currentImage?.Dispose();
            }
        }
    }
}
