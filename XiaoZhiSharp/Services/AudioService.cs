using Concentus;
using Concentus.Enums;
using PortAudioSharp;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using XiaoZhiSharp.Utils;

namespace XiaoZhiSharp.Services
{
    public class AudioService : IDisposable
    {
        // Opus 相关组件
        private readonly IOpusDecoder opusDecoder;   // 解码器
        private readonly IOpusEncoder opusEncoder;   // 编码器

        // 音频输出相关组件
        private  PortAudioSharp.Stream? _waveOut;
        private readonly ConcurrentQueue<float[]> _waveOutStream = new ConcurrentQueue<float[]>();
        private readonly object _waveOutLock = new object(); // 新增锁对象

        // 音频输入相关组件
        private  PortAudioSharp.Stream? _waveIn;

        // 音频参数
        private const int OutputSampleRate = 24000;
        private const int InputSampleRate = 24000;
        private const int Channels = 1;
        private const int FrameDuration = 60;
        private const int InputFrameSize = InputSampleRate * FrameDuration / 1000; // 帧大小
        private const int OutputFrameSize = OutputSampleRate * FrameDuration / 1000; // 帧大小

        // Opus 数据包缓存池
        private readonly ConcurrentQueue<byte[]> _opusRecordPackets = new ConcurrentQueue<byte[]>();
        private readonly ConcurrentQueue<byte[]> _opusPlayPackets = new ConcurrentQueue<byte[]>();

        // 批量解码和播放参数
        private const int MaxBatchSize = 10; // 最大批量处理大小
        private const int MinBufferLevel = 5; // 最小缓冲区水平，低于此值时启动播放

        // 设备缓存
        private int _cachedInputDeviceIndex;
        private int _cachedOutputDeviceIndex;

        // 状态管理
        public bool IsRecording { get; private set; }
        public bool IsPlaying { get; private set; }
        private bool _isClosing;

        public AudioService()
        {
            try
            {
                // 初始化 Opus 解码器和编码器
                opusDecoder = OpusCodecFactory.CreateDecoder(OutputSampleRate, Channels);
                opusEncoder = OpusCodecFactory.CreateEncoder(InputSampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);

                // 初始化音频组件
                PortAudio.Initialize();

                // 初始化输出设备
                _cachedOutputDeviceIndex = InitializeAudioDevice(isInput: false, out var outputInfo);
                var outparam = CreateStreamParameters(_cachedOutputDeviceIndex, outputInfo, isInput: false);
                _waveOut = new PortAudioSharp.Stream(
                    inParams: null, outParams: outparam, sampleRate: OutputSampleRate, framesPerBuffer: OutputFrameSize,
                    streamFlags: StreamFlags.ClipOff, callback: PlayCallback, userData: IntPtr.Zero
                );

                // 初始化输入设备
                _cachedInputDeviceIndex = InitializeAudioDevice(isInput: true, out var inputInfo);
                var inparam = CreateStreamParameters(_cachedInputDeviceIndex, inputInfo, isInput: true);
                _waveIn = new PortAudioSharp.Stream(
                    inParams: inparam, outParams: null, sampleRate: InputSampleRate, framesPerBuffer: InputFrameSize,
                    streamFlags: StreamFlags.ClipOff, callback: InCallback, userData: IntPtr.Zero
                );

                // 启动音频播放
                StartPlaying();

                // 启动 Opus 数据解码线程
                StartOpusDecodeThread();

                LogConsole.InfoLine($"当前默认音频输入设备： {_cachedInputDeviceIndex} ({inputInfo.name})");
                LogConsole.InfoLine($"当前默认音频输出设备： {_cachedOutputDeviceIndex} ({outputInfo.name})");
            }
            catch (Exception ex)
            {
                LogConsole.ErrorLine($"初始化音频服务失败: {ex.Message}");
                Dispose();
                throw;
            }
        }

        private int InitializeAudioDevice(bool isInput, out DeviceInfo deviceInfo)
        {
            int deviceIndex = isInput ? PortAudio.DefaultInputDevice : PortAudio.DefaultOutputDevice;

            if (deviceIndex == PortAudio.NoDevice)
            {
                string deviceType = isInput ? "输入" : "输出";
                LogConsole.InfoLine($"未找到默认{deviceType}设备，尝试查找可用设备");
                LogConsole.InfoLine(PortAudio.VersionInfo.versionText);
                LogConsole.WriteLine($"设备数量: {PortAudio.DeviceCount}");

                // 查找可用设备
                for (int i = 0; i < PortAudio.DeviceCount; ++i)
                {
                    var info = PortAudio.GetDeviceInfo(i);
                    LogConsole.WriteLine($" 设备 {i}");
                    LogConsole.WriteLine($"   名称: {info.name}");
                    LogConsole.WriteLine($"   最大输入通道: {info.maxInputChannels}");
                    LogConsole.WriteLine($"   默认采样率: {info.defaultSampleRate}");

                    // 选择第一个可用设备
                    if ((isInput && info.maxInputChannels > 0) || (!isInput && info.maxOutputChannels > 0))
                    {
                        deviceIndex = i;
                        LogConsole.InfoLine($"已选择替代{deviceType}设备: {info.name} (索引: {i})");
                        break;
                    }
                }

                if (deviceIndex == PortAudio.NoDevice)
                {
                    throw new InvalidOperationException($"找不到可用的{deviceType}设备");
                }
            }

            deviceInfo = PortAudio.GetDeviceInfo(deviceIndex);
            return deviceIndex;
        }

        private StreamParameters CreateStreamParameters(int deviceIndex, DeviceInfo deviceInfo, bool isInput)
        {
            return new StreamParameters
            {
                device = deviceIndex,
                channelCount = Channels,
                sampleFormat = SampleFormat.Float32,
                suggestedLatency = isInput ? deviceInfo.defaultLowInputLatency : deviceInfo.defaultLowOutputLatency,
                hostApiSpecificStreamInfo = IntPtr.Zero
            };
        }

        private void StartOpusDecodeThread()
        {
            Thread threadOpus = new Thread(() =>
            {
                while (!_isClosing)
                {
                    try
                    {
                        // 批量处理优化
                        if (_opusPlayPackets.Count > 0)
                        {
                            // 确定批处理大小，不超过队列中的实际数据
                            int batchSize = Math.Min(MaxBatchSize, _opusPlayPackets.Count);
                            List<byte[]> opusDataBatch = new List<byte[]>(batchSize);

                            // 收集批量数据
                            for (int i = 0; i < batchSize; i++)
                            {
                                if (_opusPlayPackets.TryDequeue(out var opusData))
                                {
                                    opusDataBatch.Add(opusData);
                                }
                                else
                                {
                                    break;
                                }
                            }

                            // 批量解码
                            ProcessOpusDataBatch(opusDataBatch);
                        }
                        else
                        {
                            Thread.Sleep(5); // 当没有数据时减少CPU使用率
                        }
                    }
                    catch (Exception ex)
                    {
                        LogConsole.ErrorLine($"Opus解码线程错误: {ex.Message}");
                        Thread.Sleep(100); // 错误恢复延迟
                    }
                }
            });
            threadOpus.IsBackground = true;
            threadOpus.Start();
        }

        private void ProcessOpusDataBatch(List<byte[]> opusDataBatch)
        {
            foreach (var opusData in opusDataBatch)
            {
                if (opusData == null || opusData.Length == 0)
                    continue;

                try
                {
                    // 解码 Opus 数据
                    short[] pcmData = new short[OutputFrameSize * 10];
                    int decodedSamples = opusDecoder.Decode(opusData, pcmData, OutputFrameSize * 10, false);

                    if (decodedSamples > 0)
                    {
                        // 将解码后的 PCM 数据转换为 float 数组
                        float[] floatData = new float[decodedSamples];
                        for (int i = 0; i < decodedSamples; i++)
                        {
                            floatData[i] = pcmData[i] / (float)short.MaxValue;
                        }

                        // 将 PCM 数据添加到缓冲区
                        lock (_waveOutLock)
                        {
                            _waveOutStream.Enqueue(floatData);

                            // 如果缓冲区足够大且未播放，则启动播放
                            if (_waveOutStream.Count >= MinBufferLevel && !IsPlaying)
                            {
                                StartPlaying();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogConsole.ErrorLine($"处理Opus数据错误: {ex.Message}");
                }
            }
        }

        private StreamCallbackResult PlayCallback(
            IntPtr input, IntPtr output, uint frameCount, ref StreamCallbackTimeInfo timeInfo,
            StreamCallbackFlags statusFlags, IntPtr userData)
        {
            try
            {
                if (_isClosing)
                    return StreamCallbackResult.Complete;

                // 优化静音处理
                if (_waveOutStream.IsEmpty)
                {
                    // 填充静音
                    float[] silenceBuffer = new float[frameCount];
                    Marshal.Copy(silenceBuffer, 0, output, (int)frameCount);
                    return StreamCallbackResult.Continue;
                }

                // 正常处理音频数据
                float[] buffer = null;
                bool hasData = false;

                lock (_waveOutLock)
                {
                    hasData = _waveOutStream.TryDequeue(out buffer);
                }

                if (hasData && buffer != null)
                {
                    if (buffer.Length < frameCount)
                    {
                        float[] paddedBuffer = new float[frameCount];
                        Array.Copy(buffer, paddedBuffer, buffer.Length);
                        Marshal.Copy(paddedBuffer, 0, output, (int)frameCount);
                    }
                    else
                    {
                        Marshal.Copy(buffer, 0, output, (int)frameCount);
                    }
                }
                else
                {
                    // 没有数据时输出静音
                    float[] silenceBuffer = new float[frameCount];
                    Marshal.Copy(silenceBuffer, 0, output, (int)frameCount);
                }

                return StreamCallbackResult.Continue;
            }
            catch (Exception ex)
            {
                LogConsole.ErrorLine($"音频输出回调错误: {ex.Message}");
                return StreamCallbackResult.Complete;
            }
        }

        private StreamCallbackResult InCallback(
            IntPtr input, IntPtr output, uint frameCount, ref StreamCallbackTimeInfo timeInfo,
            StreamCallbackFlags statusFlags, IntPtr userData)
        {
            try
            {
                if (_isClosing || !IsRecording)
                {
                    return StreamCallbackResult.Complete;
                }

                // 创建一个数组来存储输入的音频数据
                float[] samples = new float[frameCount];
                // 将输入的音频数据从非托管内存复制到托管数组
                Marshal.Copy(input, samples, 0, (int)frameCount);

                // 将音频数据转换为字节数组
                byte[] buffer = FloatArrayToByteArray(samples);

                // 处理音频数据
                AddRecordSamples(buffer, buffer.Length);

                return StreamCallbackResult.Continue;
            }
            catch (Exception ex)
            {
                LogConsole.ErrorLine($"音频输入回调错误: {ex.ToString()}");
                return StreamCallbackResult.Complete;
            }
        }

        private void AddRecordSamples(byte[] buffer, int bytesRecorded)
        {
            int frameCount = bytesRecorded / (InputFrameSize * 2); // 每个样本 2 字节

            for (int i = 0; i < frameCount; i++)
            {
                byte[] frame = new byte[InputFrameSize * 2];
                Array.Copy(buffer, i * InputFrameSize * 2, frame, 0, InputFrameSize * 2);

                // 将字节数组转换为 short 数组 (Concentus 使用 short[] 而不是 byte[])
                short[] pcmShorts = BytesToShorts(frame);

                // 编码音频帧
                byte[] opusBytes = new byte[960];
                int encodedLength = opusEncoder.Encode(pcmShorts, InputFrameSize, opusBytes, opusBytes.Length);

                byte[] opusPacket = new byte[encodedLength];
                Array.Copy(opusBytes, 0, opusPacket, 0, encodedLength);
                _opusRecordPackets.Enqueue(opusPacket);
            }
        }

        public void AddOutStreamSamples(byte[] opusData)
        {
            if (opusData == null || opusData.Length == 0)
                return;

            try
            {
                // 解码 Opus 数据
                short[] pcmData = new short[OutputFrameSize * 10];
                int decodedSamples = opusDecoder.Decode(opusData, pcmData, OutputFrameSize * 10, false);

                if (decodedSamples > 0)
                {
                    // 将解码后的 PCM 数据转换为 float 数组
                    float[] floatData = new float[decodedSamples];
                    for (int i = 0; i < decodedSamples; i++)
                    {
                        floatData[i] = pcmData[i] / (float)short.MaxValue;
                    }

                    // 将 PCM 数据添加到缓冲区
                    lock (_waveOutLock)
                    {
                        _waveOutStream.Enqueue(floatData);
                    }

                    // 优化播放触发逻辑
                    if (_waveOutStream.Count >= MinBufferLevel && !IsPlaying)
                    {
                        StartPlaying();
                    }
                }
            }
            catch (Exception ex)
            {
                LogConsole.ErrorLine($"Error decoding Opus data: {ex.Message}");
            }
        }

        public void StartRecording()
        {
            if (!IsRecording)
            {
                try
                {
                    _waveIn?.Start();
                    IsRecording = true;
                    LogConsole.InfoLine("音频录制已启动");
                }
                catch (Exception ex)
                {
                    LogConsole.ErrorLine($"启动录制失败: {ex.Message}");
                    ReinitializeInputStream();
                }
            }
        }

        public void StopRecording()
        {
            if (IsRecording)
            {
                try
                {
                    _waveIn?.Stop();
                    IsRecording = false;
                    LogConsole.InfoLine("音频录制已停止");
                }
                catch (Exception ex)
                {
                    LogConsole.ErrorLine($"停止录制失败: {ex.Message}");
                }
            }
        }

        public void StartPlaying()
        {
            if (!IsPlaying)
            {
                try
                {
                    _waveOut?.Start();
                    IsPlaying = true;
                    LogConsole.InfoLine("音频播放已启动");
                }
                catch (Exception ex)
                {
                    LogConsole.ErrorLine($"启动播放失败: {ex.Message}");
                    ReinitializeOutputStream();
                }
            }
        }

        public void StopPlaying()
        {
            if (IsPlaying)
            {
                try
                {
                    _waveOut?.Stop();
                    IsPlaying = false;
                    LogConsole.InfoLine("音频播放已停止");
                }
                catch (Exception ex)
                {
                    LogConsole.ErrorLine($"停止播放失败: {ex.Message}");
                }
            }
        }

        private void ReinitializeInputStream()
        {
            if (_isClosing)
                return;

            try
            {
                // 停止并关闭当前流
                if (_waveIn != null)
                {
                    try
                    {
                        _waveIn.Stop();
                        _waveIn.Dispose();
                    }
                    catch (Exception ex)
                    {
                        LogConsole.WarningLine($"关闭输入流失败: {ex.Message}");
                    }
                }

                // 重新获取输入设备索引
                _cachedInputDeviceIndex = InitializeAudioDevice(isInput: true, out var inputInfo);
                var inparam = CreateStreamParameters(_cachedInputDeviceIndex, inputInfo, isInput: true);

                // 创建新的输入流
                _waveIn = new PortAudioSharp.Stream(
                    inParams: inparam, outParams: null, sampleRate: InputSampleRate, framesPerBuffer: InputFrameSize,
                    streamFlags: StreamFlags.ClipOff, callback: InCallback, userData: IntPtr.Zero
                );

                // 如果之前在录制，则重新启动
                if (IsRecording)
                {
                    _waveIn.Start();
                }

                LogConsole.InfoLine($"音频输入流已重新初始化，设备: {inputInfo.name}");
            }
            catch (Exception ex)
            {
                LogConsole.ErrorLine($"重新初始化输入流失败: {ex.Message}");
                IsRecording = false;
            }
        }

        private void ReinitializeOutputStream()
        {
            if (_isClosing)
                return;

            try
            {
                // 停止并关闭当前流
                if (_waveOut != null)
                {
                    try
                    {
                        _waveOut.Stop();
                        _waveOut.Dispose();
                    }
                    catch (Exception ex)
                    {
                        LogConsole.WarningLine($"关闭输出流失败: {ex.Message}");
                    }
                }

                // 重新获取输出设备索引
                _cachedOutputDeviceIndex = InitializeAudioDevice(isInput: false, out var outputInfo);
                var outparam = CreateStreamParameters(_cachedOutputDeviceIndex, outputInfo, isInput: false);

                // 创建新的输出流
                _waveOut = new PortAudioSharp.Stream(
                    inParams: null, outParams: outparam, sampleRate: OutputSampleRate, framesPerBuffer: OutputFrameSize,
                    streamFlags: StreamFlags.ClipOff, callback: PlayCallback, userData: IntPtr.Zero
                );

                // 如果之前在播放，则重新启动
                if (IsPlaying)
                {
                    _waveOut.Start();
                }

                LogConsole.InfoLine($"音频输出流已重新初始化，设备: {outputInfo.name}");
            }
            catch (Exception ex)
            {
                LogConsole.ErrorLine($"重新初始化输出流失败: {ex.Message}");
                IsPlaying = false;
            }
        }

        public void OpusPlayEnqueue(byte[] opusData)
        {
            if (opusData != null && opusData.Length > 0)
            {
                _opusPlayPackets.Enqueue(opusData);
            }
        }

        public bool OpusRecordEnqueue(out byte[]? opusData)
        {
            return _opusRecordPackets.TryDequeue(out opusData);
        }

        public void ClearAudioBuffers()
        {
            lock (_waveOutLock)
            {
                while (_waveOutStream.TryDequeue(out _)) { }

                byte[] opusData;
                while (_opusPlayPackets.TryDequeue(out opusData)) { }
                while (_opusRecordPackets.TryDequeue(out opusData)) { }
            }

            LogConsole.InfoLine("已清空所有音频缓冲区");
        }

        public bool HasPendingAudio()
        {
            return !_waveOutStream.IsEmpty || !_opusPlayPackets.IsEmpty;
        }

        public void WaitForAudioComplete(int timeoutMs = 5000)
        {
            var startTime = DateTime.UtcNow;
            while (HasPendingAudio() && (DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
            {
                Thread.Sleep(100);
            }
        }

        // 工具方法：将字节数组转换为短整型数组(Concentus要求)
        private static short[] BytesToShorts(byte[] byteArray)
        {
            int shortCount = byteArray.Length / 2;
            short[] shortArray = new short[shortCount];

            for (int i = 0; i < shortCount; i++)
            {
                shortArray[i] = (short)(byteArray[i * 2] | (byteArray[i * 2 + 1] << 8));
            }

            return shortArray;
        }

        public static byte[] FloatArrayToByteArray(float[] floatArray)
        {
            // 初始化一个与 float 数组长度两倍的 byte 数组，因为每个 short 占 2 个字节
            byte[] byteArray = new byte[floatArray.Length * 2];

            for (int i = 0; i < floatArray.Length; i++)
            {
                // 将 float 类型的值映射到 short 类型的范围
                short sample = (short)(floatArray[i] * short.MaxValue);

                // 将 short 类型的值拆分为两个字节
                byteArray[i * 2] = (byte)(sample & 0xFF);
                byteArray[i * 2 + 1] = (byte)(sample >> 8);
            }

            return byteArray;
        }

        public static float[] ByteArrayToFloatArray(byte[] byteArray)
        {
            int floatArrayLength = byteArray.Length / 2;
            float[] floatArray = new float[floatArrayLength];

            for (int i = 0; i < floatArrayLength; i++)
            {
                floatArray[i] = BitConverter.ToInt16(byteArray, i * 2) / 32768f;
            }

            return floatArray;
        }

        public void Dispose()
        {
            if (_isClosing)
                return;

            _isClosing = true;
            LogConsole.InfoLine("正在释放音频服务资源...");

            try
            {
                // 先停止所有音频活动
                StopRecording();
                StopPlaying();

                // 清空所有缓冲区
                ClearAudioBuffers();

                // 关闭和释放资源
                _waveIn?.Dispose();
                _waveOut?.Dispose();

                // 最后终止PortAudio
                PortAudio.Terminate();

                LogConsole.InfoLine("音频服务资源已成功释放");
            }
            catch (Exception ex)
            {
                LogConsole.ErrorLine($"释放音频资源时发生错误: {ex.Message}");
            }
            finally
            {
                _isClosing = false;
            }
        }
    }
}