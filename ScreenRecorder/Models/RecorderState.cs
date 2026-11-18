namespace ScreenRecorder.Models
{
    /// <summary>
    /// 录制控制状态机（开始/暂停/停止/保存）。
    /// 状态流转：Idle --Start--> Recording --Pause--> Paused --Resume--> Recording
    ///           --Stop--> Saving --> Idle（文件已落盘）
    /// </summary>
    public enum RecorderState
    {
        /// <summary>空闲，未录制。</summary>
        Idle,

        /// <summary>正在录制。</summary>
        Recording,

        /// <summary>已暂停（画面冻结，不写入帧）。</summary>
        Paused,

        /// <summary>正在收尾并落盘。</summary>
        Saving
    }

    /// <summary>
    /// 编码器偏好。
    /// </summary>
    public enum EncoderPreference
    {
        /// <summary>自动：优先 NVENC 硬编码（GPU），不可用则回退 libx264 软编码。</summary>
        Auto,

        /// <summary>强制 NVENC（NVIDIA 显卡硬编码，CPU 占用极低）。</summary>
        Nvenc,

        /// <summary>强制 libx264 软编码（兼容所有机器）。</summary>
        Software
    }
}
