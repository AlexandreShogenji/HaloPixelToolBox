# 语音 Worker 离线测试

在仓库根目录运行：

```powershell
python -B tests/voice-agent/test_voice_agent_host.py
pwsh -File eng/Test.ps1 -Suite Voice -PythonPath C:/path/to/python.exe
```

需要 Python 与 numpy，直接载入 `src/HaloPixelToolBox/Assets/VoiceAgent/voice_agent_host.py`。测试用确定性的 PCM 片段和假进程检查唤醒、句尾停顿、缓存、取消与提示音状态，不采集麦克风、播放声音、加载 ASR 模型或控制设备。

任务语音回归还覆盖：连续问题无需重复唤醒、最终确认回到唤醒模式、提示音与动态播报的回声清理、在界面回答后丢弃旧语音答案、旧问题排队/合成/播放失效、JSON 控制数据校验，以及本地语音 LRU 缓存（最多 32 条、16 MiB）。

动态任务语音使用 Windows 本机中文语音包，优先已安装的晓晓或其他中文女声；不将任务或授权内容发到在线语音服务。固定「我在」「收到」音色保持原 WAV。没有中文语音包时仍显示字幕并播放提示音，界面说明语音合成不可用。测试中的合成和播放均为替身；真实麦克风、扬声器音量和远场识别仍需设备验收。
