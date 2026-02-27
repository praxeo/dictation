using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;

namespace WhisperInk
{
    public partial class MainWindow : Window
    {
        // ── Win32 imports ──────────────────────────────────────────────
        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr hInstance, uint threadId);[DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);[DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);[DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        private const uint WM_CHAR = 0x0102;
        private const uint GW_CHILD = 5;[DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);[DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        // ── SendInput structures ───────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;
        private const int VK_CONTROL = 0x11;
        private const int VK_SPACE = 0x20;
        private const int VK_RETURN = 0x0D;

        private const byte KEYEVENTF_EXTENDEDKEY = 0x01;
        private const byte KEYEVENTF_KEYUP_BYTE = 0x02;

        private const int SYNTHETIC_MARKER_VALUE = 0x5AFE;
        private static readonly UIntPtr SYNTHETIC_MARKER = (UIntPtr)SYNTHETIC_MARKER_VALUE;
        private static readonly IntPtr SYNTHETIC_MARKER_PTR = new IntPtr(SYNTHETIC_MARKER_VALUE);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        // ── API configuration ──────────────────────────────────────────
        private const string AudioApiUrl = "https://api.mistral.ai/v1/audio/transcriptions";
        private const string AudioModel = "voxtral-mini-latest";
        private const string RealtimeModel = "voxtral-mini-transcribe-realtime-2602";
        private const string RealtimeWsUrl = "ws://localhost:8765/v1/realtime";
        private const string ChatApiUrl = "https://api.mistral.ai/v1/chat/completions";
        private const string ChatModel = "mistral-medium-latest";

        private static readonly string ConfigFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".WhisperInkRT");
        private static readonly string ConfigFile = Path.Combine(ConfigFolder, "config.json");

        // ── State ──────────────────────────────────────────────────────
        private IntPtr _hookId = IntPtr.Zero;
        private LowLevelKeyboardProc _hookCallback;
        private bool _isRecording;
        private bool _ctrlPressed;
        private bool _winPressed;
        private bool _altPressed;
        private bool _spacePressed;
        private bool _suppressingKeys;
        
        private string _mistralApiKey = "";
        private bool _isSoundEnabled = true;
        private string _systemPrompt = "You are a precise execution engine. The user will give you text and a voice instruction. Follow the instruction exactly. Return only the result — no commentary, no markdown, no explanation.";
        private int _selectedDeviceNumber;
        private int _targetStreamingDelayMs = 480;
        private string _proxyPath = "";

        // ── FIX: capture the target window before recording starts ────
        private IntPtr _targetWindow = IntPtr.Zero;

        private readonly HttpClient _httpClient = new();
        private WaveInEvent? _waveIn;
        private WaveFileWriter? _writer;
        private string _currentFileName = "";
        
        private DispatcherTimer _animationTimer;
        private readonly Random _rng = new();
        private readonly List<string> _history = new();

        private enum RecordingMode { Dictation, AnalyzeContext }
        private RecordingMode _currentMode = RecordingMode.Dictation;
        private enum SoundType { Start, Stop, Success, Error }

        // ── Realtime streaming state ───────────────────────────────────
        private ClientWebSocket? _realtimeWs;
        private CancellationTokenSource? _realtimeCts;
        private Task? _receiveTask;
        private string _accumulatedTranscript = "";
        private bool _leadingSpaceSent;
        private bool _isStopping; 
        private readonly SemaphoreSlim _wsSendLock = new(1, 1);
        private Process? _proxyProcess;

        private static readonly string LogFile = Path.Combine(ConfigFolder, "debug.log");

        private static void Log(string msg)
        {
            try { File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
        }

        public MainWindow()
        {
            InitializeComponent();
            _hookCallback = HookCallback;
            Loaded += MainWindow_Loaded;
            Closing += (_, _) =>
            {
                if (_hookId != IntPtr.Zero) UnhookWindowsHookEx(_hookId);
                try { _proxyProcess?.Kill(); } catch { }
            };
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Topmost = true;
            var screen = SystemParameters.WorkArea;
            Left = screen.Width - Width - 10;
            Top = screen.Height - Height - 10;

            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookCallback, GetModuleHandle(curModule.ModuleName!), 0);

            _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _animationTimer.Tick += (_, _) => UpdateHistogram();

            LoadConfig();

            // Auto-start the Mistral proxy
            if (!string.IsNullOrWhiteSpace(_proxyPath))
            {
                try
                {
                    _proxyProcess = new Process();
                    _proxyProcess.StartInfo = new ProcessStartInfo
                    {
                        FileName = "py",
                        Arguments = $"\"{_proxyPath}\" --api-key {_mistralApiKey}",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    _proxyProcess.Start();
                    System.Threading.Thread.Sleep(1500); // Give proxy time to start
                }
                catch (Exception ex) { Log($"Proxy start error: {ex.Message}"); }
            }

            try { File.WriteAllText(LogFile, $"=== WhisperInk RT started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n"); } catch { }

            lblStatus.Content = "Ready (RT)";
        }

        private void LoadConfig()
        {
            try
            {
                if (!Directory.Exists(ConfigFolder)) Directory.CreateDirectory(ConfigFolder);
                if (File.Exists(ConfigFile))
                {
                    var json = File.ReadAllText(ConfigFile);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("MistralApiKey", out var key)) _mistralApiKey = key.GetString() ?? "";
                    if (root.TryGetProperty("IsSoundEnabled", out var snd)) _isSoundEnabled = snd.GetBoolean();
                    if (root.TryGetProperty("SystemPrompt", out var sp)) _systemPrompt = sp.GetString() ?? _systemPrompt;
                    if (root.TryGetProperty("SelectedDevice", out var dev)) _selectedDeviceNumber = dev.GetInt32();
                    if (root.TryGetProperty("TargetStreamingDelayMs", out var delay)) _targetStreamingDelayMs = delay.GetInt32();
                    if (root.TryGetProperty("ProxyPath", out var pp)) _proxyPath = pp.GetString() ?? "";
                }
                else
                {
                    SaveConfig();
                    MessageBox.Show($"Config created at:\n{ConfigFile}\n\nPlease add your Mistral API key.", "WhisperInk RT");
                }
            }
            catch (Exception ex) { Log($"Config error: {ex.Message}"); }
        }

        private void SaveConfig()
        {
            try
            {
                if (!Directory.Exists(ConfigFolder)) Directory.CreateDirectory(ConfigFolder);
                var config = new
                {
                    MistralApiKey = _mistralApiKey,
                    IsSoundEnabled = _isSoundEnabled,
                    SystemPrompt = _systemPrompt,
                    SelectedDevice = _selectedDeviceNumber,
                    TargetStreamingDelayMs = _targetStreamingDelayMs,
                    ProxyPath = _proxyPath
                };
                File.WriteAllText(ConfigFile, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Log($"Config save error: {ex.Message}"); }
        }

        // ── Keyboard Hook ──────────────────────────────────────────────
        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var hookData = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vkCode = hookData.vkCode;
                bool isDown = (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN);
                bool isUp = (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP);
                bool isSynthetic = (hookData.dwExtraInfo == new IntPtr(SYNTHETIC_MARKER_VALUE));

                if (_suppressingKeys && !isSynthetic)
                {
                    if (vkCode == VK_LCONTROL || vkCode == VK_RCONTROL || vkCode == VK_SPACE)
                    {
                        if (vkCode == VK_SPACE) _spacePressed = isDown;
                        else _ctrlPressed = isDown;

                        if (isUp && _isRecording && _currentMode == RecordingMode.Dictation)
                        {
                            if (!_ctrlPressed || !_spacePressed)
                                Dispatcher.BeginInvoke(() => StopRealtimeStreaming());
                        }
                        if (!_ctrlPressed && !_spacePressed)
                            _suppressingKeys = false;
                        
                        return (IntPtr)1; 
                    }
                }

                if (vkCode == VK_LCONTROL || vkCode == VK_RCONTROL)
                {
                    if (!isSynthetic) _ctrlPressed = isDown;
                    if (isUp && _isRecording && _currentMode == RecordingMode.AnalyzeContext)
                    {
                        if (!_ctrlPressed || !_altPressed)
                            Dispatcher.BeginInvoke(() => StopBatchRecording());
                    }
                }
                else if (vkCode == VK_SPACE)
                {
                    if (!isSynthetic) _spacePressed = isDown;
                    if (isDown && !isSynthetic && _ctrlPressed && !_isRecording && !_suppressingKeys)
                    {
                        // ── FIX: capture target window BEFORE hotkey steals focus ──
                        _targetWindow = GetForegroundWindow();

                        _currentMode = RecordingMode.Dictation;
                        _suppressingKeys = true; 
                        Dispatcher.BeginInvoke(() => StartRealtimeStreaming());
                        return (IntPtr)1; 
                    }
                }
                else if (vkCode == VK_LWIN || vkCode == VK_RWIN)
                {
                    _winPressed = isDown;
                }
                else if (vkCode == VK_LMENU || vkCode == VK_RMENU)
                {
                    _altPressed = isDown;
                    if (isDown && _ctrlPressed && !_isRecording)
                    {
                        _currentMode = RecordingMode.AnalyzeContext;
                        Dispatcher.BeginInvoke(() => StartBatchRecording());
                    }
                    if (isUp && _isRecording && _currentMode == RecordingMode.AnalyzeContext)
                    {
                        if (!_ctrlPressed || !_altPressed)
                            Dispatcher.BeginInvoke(() => StopBatchRecording());
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // ════════════════════════════════════════════════════════════════
        // MISTRAL REALTIME STREAMING MODE
        // ════════════════════════════════════════════════════════════════

        private async void StartRealtimeStreaming()
        {
            if (_isRecording) return;
            if (string.IsNullOrEmpty(_mistralApiKey))
            {
                lblStatus.Content = "No API key!";
                return;
            }

            _isRecording = true;
            _accumulatedTranscript = "";
            _leadingSpaceSent = false;
            _suppressingKeys = true;
            ReleaseAllModifierKeys();

            MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(100, 255, 100)); 
            lblStatus.Content = "🎙 LIVE";
            lblStatus.Opacity = 1;
            HistogramPanel.Visibility = Visibility.Visible;
            _animationTimer.Start();

            _realtimeCts = new CancellationTokenSource();

            try
            {
                _realtimeWs = new ClientWebSocket();
                _realtimeWs.Options.SetRequestHeader("Authorization", $"Bearer {_mistralApiKey}");
                Log($"Connecting to Mistral Realtime {RealtimeWsUrl}...");
                await _realtimeWs.ConnectAsync(new Uri(RealtimeWsUrl), _realtimeCts.Token);

                var sessionUpdate = JsonSerializer.Serialize(new
                {
                    type = "session.update",
                    session = new {
                        model = RealtimeModel,
                        target_streaming_delay_ms = _targetStreamingDelayMs,
                        audio_format = new {
                            encoding = "pcm_s16le",
                            sample_rate = 16000
                        }
                    }
                });
                await SendTextMessageSafe(sessionUpdate, _realtimeCts.Token);

                _waveIn = new WaveInEvent();
                if (_selectedDeviceNumber < WaveIn.DeviceCount) _waveIn.DeviceNumber = _selectedDeviceNumber;
                else _selectedDeviceNumber = 0;
                
                _waveIn.WaveFormat = new WaveFormat(16000, 16, 1); 
                _waveIn.BufferMilliseconds = 100;
                _waveIn.DataAvailable += OnAudioDataAvailable;
                _waveIn.StartRecording();

                PlayUiSound(SoundType.Start);
                _receiveTask = Task.Run(() => ReceiveTranscriptionLoop(_realtimeWs, _realtimeCts.Token));
            }
            catch (Exception ex)
            {
                Log($"Realtime start error: {ex.Message}");
                ForceCleanupRealtime($"Error: {ex.Message}");
            }
        }

        private async void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
        {
            var cts = _realtimeCts;
            if (!_isRecording || _realtimeWs?.State != WebSocketState.Open || cts == null || cts.IsCancellationRequested)
                return;

            try
            {
                string base64Audio = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded);
                var msg = JsonSerializer.Serialize(new
                {
                    type = "input_audio_buffer.append",
                    audio = base64Audio
                });
                await SendTextMessageSafe(msg, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"Audio send error: {ex.Message}"); }
        }

        private async Task ReceiveTranscriptionLoop(ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var messageBuilder = new StringBuilder();

            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    messageBuilder.Clear();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            if (_isStopping || result.CloseStatus == WebSocketCloseStatus.NormalClosure) return;
                            var desc = result.CloseStatusDescription ?? result.CloseStatus?.ToString() ?? "unknown";
                            Dispatcher.Invoke(() => ForceCleanupRealtime($"WS closed: {desc}"));
                            return;
                        }
                        messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);

                    ProcessRealtimeEvent(messageBuilder.ToString());
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => ForceCleanupRealtime($"WS error: {ex.Message}"));
            }
        }

        private void ProcessRealtimeEvent(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl)) return;
                string eventType = typeEl.GetString() ?? "";

                switch (eventType)
                {
                    case "transcription.text.delta": 
                        if (!_isRecording) break; 
                        if (root.TryGetProperty("text", out var textEl))
                        {
                            string delta = textEl.GetString() ?? "";
                            if (!string.IsNullOrEmpty(delta))
                            {
                                Log($"delta: \"{delta}\"");
                                _accumulatedTranscript += delta;

                                Dispatcher.Invoke(() =>
                                {
                                    Log($"Typing to hwnd: {_targetWindow}");
                                    if (_targetWindow != IntPtr.Zero)
                                        SetForegroundWindow(_targetWindow);
                                    if (!_leadingSpaceSent)
                                    {
                                        TypeTextViaInput(" ");
                                        _leadingSpaceSent = true;
                                    }
                                    TypeTextViaInput(delta);
                                });
                            }
                        }
                        break;

                    case "transcription.done":
                        Log($"Transcription done: {_accumulatedTranscript}");
                        break;

                    case "error":
                        if (root.TryGetProperty("error", out var errEl))
                        {
                            string errorMsg = errEl.ToString();
                            if (errEl.ValueKind == JsonValueKind.Object && errEl.TryGetProperty("message", out var msgEl))
                            {
                                errorMsg = msgEl.GetString() ?? errorMsg;
                            }
                            Dispatcher.Invoke(() => ForceCleanupRealtime($"Error: {errorMsg}"));
                        }
                        break;
                }
            }
            catch (Exception ex) { Log($"Parse error: {ex.Message}"); }
        }

        private async void StopRealtimeStreaming()
        {
            if (!_isRecording || _isStopping) return;
            _isStopping = true;

            PlayUiSound(SoundType.Stop);

            try { _waveIn?.StopRecording(); } catch { }
            try { _waveIn?.Dispose(); } catch { }
            _waveIn = null;

            try
            {
                if (_realtimeWs?.State == WebSocketState.Open && _realtimeCts != null && !_realtimeCts.IsCancellationRequested)
                {
                    var commit = JsonSerializer.Serialize(new { type = "input_audio_buffer.commit" });
                    await SendTextMessageSafe(commit, _realtimeCts.Token);
                    await Task.Delay(500);
                }
            }
            catch { }

            _isRecording = false;

            if (_realtimeWs != null && _realtimeWs.State == WebSocketState.Open)
            {
                try { await _realtimeWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "Finished", CancellationToken.None); }
                catch { }
            }

            try { _realtimeCts?.Cancel(); } catch { }
            try { _realtimeWs?.Dispose(); } catch { }
            _realtimeWs = null;

            if (_receiveTask != null)
            {
                try { await Task.WhenAny(_receiveTask, Task.Delay(1000)); } catch { }
                _receiveTask = null;
            }

            if (!string.IsNullOrWhiteSpace(_accumulatedTranscript))
            {
                _history.Insert(0, _accumulatedTranscript.Trim());
                if (_history.Count > 50) _history.RemoveAt(50);
                PlayUiSound(SoundType.Success);
            }

            ReleaseAllModifierKeys();

            _isStopping = false;
            ResetUi();
            lblStatus.Content = "Ready (RT)";
        }

        private void ForceCleanupRealtime(string statusMessage)
        {
            Log($"ForceCleanupRealtime: {statusMessage}");

            if (!_isRecording && _realtimeWs == null && !_isStopping) return;
            _isRecording = false;
            _isStopping = false;

            try { _waveIn?.StopRecording(); _waveIn?.Dispose(); } catch { }
            _waveIn = null;

            try { _realtimeCts?.Cancel(); _realtimeWs?.Dispose(); } catch { }
            _realtimeWs = null;

            ReleaseAllModifierKeys();
            ResetUi();
            lblStatus.Content = statusMessage;
            lblStatus.Opacity = 1;

            PlayUiSound(SoundType.Error);
        }

        private void ReleaseAllModifierKeys()
        {
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP_BYTE, SYNTHETIC_MARKER);
            keybd_event(0xA0, 0, KEYEVENTF_KEYUP_BYTE, SYNTHETIC_MARKER);
            keybd_event(0xA1, 0, KEYEVENTF_KEYUP_BYTE, SYNTHETIC_MARKER);
            keybd_event((byte)VK_LMENU, 0, KEYEVENTF_KEYUP_BYTE, SYNTHETIC_MARKER);
            keybd_event((byte)VK_RMENU, 0, KEYEVENTF_KEYUP_BYTE, SYNTHETIC_MARKER);
            keybd_event((byte)VK_LWIN, 0, KEYEVENTF_KEYUP_BYTE, SYNTHETIC_MARKER);
            keybd_event((byte)VK_RWIN, 0, KEYEVENTF_KEYUP_BYTE, SYNTHETIC_MARKER);
        }

        private INPUT CreateInput(ushort wVk, ushort wScan, uint dwFlags, IntPtr dwExtraInfo)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = wVk, wScan = wScan, dwFlags = dwFlags, time = 0, dwExtraInfo = dwExtraInfo
                    }
                }
            };
        }

        private void TypeTextViaInput(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_targetWindow == IntPtr.Zero) return;

            // Post WM_CHAR directly to target window — no focus required
            foreach (char c in text)
            {
                PostMessage(_targetWindow, WM_CHAR, (IntPtr)c, IntPtr.Zero);
            }
        }

        private async Task SendTextMessageSafe(string message, CancellationToken ct)
        {
            if (_realtimeWs == null || _realtimeWs.State != WebSocketState.Open) return;

            await _wsSendLock.WaitAsync(ct);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                await _realtimeWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            catch { }
            finally { _wsSendLock.Release(); }
        }

        // ════════════════════════════════════════════════════════════════
        // BATCH RECORDING MODE 
        // ════════════════════════════════════════════════════════════════
        public void StartBatchRecording()
        {
            if (string.IsNullOrEmpty(_mistralApiKey)) { lblStatus.Content = "No API key!"; return; }

            _isRecording = true;
            if (_currentMode == RecordingMode.AnalyzeContext)
            {
                try { Clipboard.Clear(); } catch { }
                MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(100, 100, 255));
            }
            else MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 100, 100)); 

            lblStatus.Opacity = 0;
            HistogramPanel.Visibility = Visibility.Visible;
            _animationTimer.Start();

            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MyRecordings");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            _currentFileName = Path.Combine(folder, "temp_audio.wav");

            _waveIn = new WaveInEvent();
            if (_selectedDeviceNumber < WaveIn.DeviceCount) _waveIn.DeviceNumber = _selectedDeviceNumber;
            else _selectedDeviceNumber = 0;
            _waveIn.WaveFormat = new WaveFormat(16000, 1);
            _writer = new WaveFileWriter(_currentFileName, _waveIn.WaveFormat);
            _waveIn.DataAvailable += (_, a) => _writer!.Write(a.Buffer, 0, a.BytesRecorded);
            _waveIn.StartRecording();

            PlayUiSound(SoundType.Start);
        }

        private async void StopBatchRecording()
        {
            if (!_isRecording) return;
            _isRecording = false;

            PlayUiSound(SoundType.Stop);

            try { _waveIn?.StopRecording(); _writer?.Dispose(); _waveIn?.Dispose(); } catch { }
            _waveIn = null; _writer = null;

            lblStatus.Content = "Processing...";
            lblStatus.Opacity = 1;

            if (_currentMode == RecordingMode.AnalyzeContext)
            {
                string selectedText = GetSelectedText();
                string? transcribedVoice = await TranscribeAudioAsync(_currentFileName);

                if (!string.IsNullOrEmpty(transcribedVoice))
                {
                    string? aiResponse = await ProcessAiQueryAsync(selectedText, transcribedVoice);
                    if (!string.IsNullOrEmpty(aiResponse))
                    {
                        PasteTextToActiveWindow(aiResponse);
                        _history.Insert(0, aiResponse);
                        PlayUiSound(SoundType.Success);
                    }
                    else { PlayUiSound(SoundType.Error); lblStatus.Content = "AI error"; }
                }
                else { PlayUiSound(SoundType.Error); lblStatus.Content = "Transcribe error"; }
            }
            else
            {
                string? text = await TranscribeAudioAsync(_currentFileName);
                if (!string.IsNullOrEmpty(text))
                {
                    PasteTextToActiveWindow(text);
                    _history.Insert(0, text);
                    PlayUiSound(SoundType.Success);
                }
                else { PlayUiSound(SoundType.Error); lblStatus.Content = "Error"; }
            }

            ResetUi();
            lblStatus.Content = "Ready (RT)";
        }

        private async Task<string?> TranscribeAudioAsync(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, AudioApiUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _mistralApiKey);

                using var content = new MultipartFormDataContent();
                using var fileStream = File.OpenRead(filePath);
                var fileContent = new StreamContent(fileStream);
                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");

                content.Add(fileContent, "file", "audio.wav");
                content.Add(new StringContent(AudioModel), "model");
                content.Add(new StringContent("en"), "language");

                request.Content = content;
                var response = await _httpClient.SendAsync(request);
                string responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(responseString);
                if (doc.RootElement.TryGetProperty("text", out var textElement)) return textElement.GetString();
            }
            catch (Exception ex) { Log($"Network error: {ex.Message}"); }
            return null;
        }

        private async Task<string?> ProcessAiQueryAsync(string context, string voiceInstruction)
        {
            try
            {
                string userContent = string.IsNullOrEmpty(context)
                    ? voiceInstruction
                    : $"Context:\n{context}\n\nInstruction: {voiceInstruction}";

                var payload = new
                {
                    model = ChatModel,
                    messages = new[] {
                        new { role = "system", content = _systemPrompt },
                        new { role = "user", content = userContent }
                    }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, ChatApiUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _mistralApiKey);
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);
                string responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(responseString);
                return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            }
            catch (Exception ex) { Log($"AI error: {ex.Message}"); return null; }
        }

        private string GetSelectedText()
        {
            try
            {
                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(0x43, 0, 0, UIntPtr.Zero);
                keybd_event(0x43, 0, KEYEVENTF_KEYUP_BYTE, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP_BYTE, UIntPtr.Zero);

                Thread.Sleep(100);
                string text = "";
                var staThread = new Thread(() => { try { text = Clipboard.GetText(); } catch { } });
                staThread.SetApartmentState(ApartmentState.STA);
                staThread.Start();
                staThread.Join();
                return text;
            }
            catch { return ""; }
        }

        private void PasteTextToActiveWindow(string text)
        {
            text = " " + text;
            var staThread = new Thread(() => { try { Clipboard.SetText(text); } catch { } });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();
            SimulateCtrlV();
        }

        private void SimulateCtrlV()
        {
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(0x56, 0, 0, UIntPtr.Zero);
            keybd_event(0x56, 0, KEYEVENTF_KEYUP_BYTE, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP_BYTE, UIntPtr.Zero);
        }

        private void ResetUi()
        {
            _animationTimer.Stop();
            HistogramPanel.Visibility = Visibility.Collapsed;
            MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(64, 64, 64));

            foreach (var child in HistogramPanel.Children)
                if (child is Border bar) bar.Height = 2;
        }

        private void UpdateHistogram()
        {
            foreach (var child in HistogramPanel.Children)
            {
                if (child is Border bar)
                {
                    double target = _isRecording ? _rng.Next(4, 22) : 2;
                    bar.Height = bar.Height + (target - bar.Height) * 0.4;
                }
            }
        }

        private void PlayUiSound(SoundType type)
        {
            if (!_isSoundEnabled) return;
            Task.Run(() =>
            {
                try
                {
                    int sampleRate = 44100;
                    int duration = type switch { SoundType.Start => 80, SoundType.Stop => 80, SoundType.Success => 120, SoundType.Error => 200, _ => 0 };
                    double freq = type switch { SoundType.Start => 880, SoundType.Stop => 440, SoundType.Success => 1200, SoundType.Error => 300, _ => 0 };

                    int samples = sampleRate * duration / 1000;
                    using var ms = new MemoryStream();
                    using var writer = new BinaryWriter(ms);

                    int dataSize = samples * 2;
                    writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                    writer.Write(36 + dataSize);
                    writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                    writer.Write(Encoding.ASCII.GetBytes("fmt "));
                    writer.Write(16);
                    writer.Write((short)1);
                    writer.Write((short)1);
                    writer.Write(sampleRate);
                    writer.Write(sampleRate * 2);
                    writer.Write((short)2);
                    writer.Write((short)16);
                    writer.Write(Encoding.ASCII.GetBytes("data"));
                    writer.Write(dataSize);

                    for (int i = 0; i < samples; i++)
                    {
                        double t = (double)i / sampleRate;
                        double envelope = Math.Max(0, 1.0 - t / (duration / 1000.0));
                        double sample = Math.Sin(2 * Math.PI * freq * t) * envelope * 0.3;
                        writer.Write((short)(sample * short.MaxValue));
                    }

                    ms.Position = 0;
                    using var player = new System.Media.SoundPlayer(ms);
                    player.PlaySync();
                }
                catch { }
            });
        }

        private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var menu = new ContextMenu();
            var micMenu = new MenuItem { Header = "🎙 Microphone" };
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var cap = WaveIn.GetCapabilities(i);
                int deviceIndex = i;
                var item = new MenuItem { Header = cap.ProductName, IsChecked = i == _selectedDeviceNumber };
                item.Click += (_, _) => { _selectedDeviceNumber = deviceIndex; SaveConfig(); };
                micMenu.Items.Add(item);
            }
            menu.Items.Add(micMenu);

            var soundItem = new MenuItem { Header = _isSoundEnabled ? "🔊 Sound: ON" : "🔇 Sound: OFF" };
            soundItem.Click += (_, _) => { _isSoundEnabled = !_isSoundEnabled; SaveConfig(); };
            menu.Items.Add(soundItem);

            menu.Items.Add(new Separator());

            var delayMenu = new MenuItem { Header = "⏱ Streaming Delay" };
            foreach (int ms in new[] { 240, 480, 1000, 1500, 2400 })
            {
                int delayMs = ms;
                string label = ms switch { 240 => "240ms (fastest)", 480 => "480ms (recommended)", 1000 => "1000ms", 1500 => "1500ms", 2400 => "2400ms (most accurate)", _ => $"{ms}ms" };
                var delayItem = new MenuItem { Header = label, IsChecked = _targetStreamingDelayMs == ms };
                delayItem.Click += (_, _) => { _targetStreamingDelayMs = delayMs; SaveConfig(); };
                delayMenu.Items.Add(delayItem);
            }
            menu.Items.Add(delayMenu);

            menu.Items.Add(new Separator());

            var promptItem = new MenuItem { Header = "📝 System Prompt" };
            promptItem.Click += (_, _) =>
            {
                var pw = new PromptWindow(_systemPrompt);
                if (pw.ShowDialog() == true) { _systemPrompt = pw.PromptText; SaveConfig(); }
            };
            menu.Items.Add(promptItem);

            if (_history.Count > 0)
            {
                menu.Items.Add(new Separator());
                var historyMenu = new MenuItem { Header = "📋 History" };
                foreach (var item in _history.Take(10))
                {
                    string preview = item.Length > 60 ? item[..60] + "..." : item;
                    var histItem = new MenuItem { Header = preview };
                    string fullText = item;
                    histItem.Click += (_, _) =>
                    {
                        var t = new Thread(() => { try { Clipboard.SetText(fullText); } catch { } });
                        t.SetApartmentState(ApartmentState.STA);
                        t.Start();
                        t.Join();
                    };
                    historyMenu.Items.Add(histItem);
                }
                menu.Items.Add(historyMenu);
            }

            menu.Items.Add(new Separator());
            var configItem = new MenuItem { Header = $"📂 Config: {ConfigFile}" };
            configItem.Click += (_, _) => Process.Start("explorer.exe", $"/select,\"{ConfigFile}\"");
            menu.Items.Add(configItem);

            var exitItem = new MenuItem { Header = "❌ Exit" };
            exitItem.Click += (_, _) => Application.Current.Shutdown();
            menu.Items.Add(exitItem);

            menu.IsOpen = true;
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }
    }
}
