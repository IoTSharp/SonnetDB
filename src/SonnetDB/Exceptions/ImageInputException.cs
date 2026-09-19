namespace SonnetDB.Exceptions;

/// <summary>图片格式、尺寸或编码内容不符合图片处理合同。</summary>
internal sealed class ImageInputException : Exception
{
    internal ImageInputException(string message) : base(message) { }

    internal ImageInputException(string message, Exception innerException) : base(message, innerException) { }
}
