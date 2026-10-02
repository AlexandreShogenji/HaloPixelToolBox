# 语音 Worker 离线测试

在仓库根目录运行：

```powershell
python -B tests/voice-agent/test_voice_agent_host.py
pwsh -File eng/Test.ps1 -Suite Voice -PythonPath C:/path/to/python.exe
```

需要 Python 与 numpy，直接载入 `src/HaloPixelToolBox/Assets/VoiceAgent/voice_agent_host.py`。测试用确定性的 PCM 片段和假进程检查唤醒、句尾停顿、缓存、取消与提示音状态，不采集麦克风、播放声音、加载 ASR 模型或控制设备。
