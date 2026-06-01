namespace HaloPixelToolBox.Core.Models.Scenes;

/// <summary>
/// 像素屏场景资源上传进度。
/// 进度按已发送的 .bin 资源字节数计算，后续如果改为更底层的流式上传，可继续扩展传输阶段字段。
/// </summary>
public sealed record PixelSceneUploadProgress(long SentBytes, long TotalBytes, string Stage)
{
    public double Ratio => TotalBytes <= 0 ? 0 : Math.Clamp(SentBytes / (double)TotalBytes, 0, 1);

    public double Percent => Ratio * 100;
}
