namespace SonnetMQ;

/// <summary>
/// SonnetMQ 本地存储打开模式。
/// </summary>
public enum SonnetMqOpenMode
{
    /// <summary>
    /// Path 指向目录，队列数据写入该目录下的 sonnetmq.log。
    /// </summary>
    Directory = 0,

    /// <summary>
    /// Path 指向单个队列文件。
    /// </summary>
    SingleFile = 1,
}
