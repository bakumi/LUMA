using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using Windows.Media.SpeechRecognition;

namespace LUMA.Services;

public class VoiceAssistantService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const int VkMenu = 0x12;    // Alt
    private const int VkLmenu = 0xA4;
    private const int VkRmenu = 0xA5;
    private const int VkLwin = 0x5B;
    private const int VkRwin = 0x5C;
    private const int VkSpace = 0x20;

    private SpeechRecognizer? _recognizer;
    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc;
    private bool _isListening;
    private bool _disposed;
    private string _currentRecognizedText = "";
    private IntPtr _targetWindowHandle = IntPtr.Zero;

    public event Action<bool>? StateChanged;
    public event Action<string>? HypothesisGenerated;
    public event Action<string>? ResultGenerated;

    public bool IsListening => _isListening;
    public string CurrentText => _currentRecognizedText;

    public VoiceAssistantService()
    {
        _proc = HookCallback;
        InstallHook();
        _ = InitializeSpeechRecognizerAsync();
    }

    private async Task InitializeSpeechRecognizerAsync()
    {
        try
        {
            if (SpeechRecognizer.SupportedTopicLanguages.Count == 0 && SpeechRecognizer.SupportedGrammarLanguages.Count == 0)
            {
                _recognizer = null;
                return;
            }

            var rec = new SpeechRecognizer();
            var dictationConstraint = new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation");
            rec.Constraints.Add(dictationConstraint);
            await rec.CompileConstraintsAsync();

            rec.HypothesisGenerated += (s, e) =>
            {
                var text = e.Hypothesis.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _currentRecognizedText = text;
                    HypothesisGenerated?.Invoke(text);
                }
            };

            rec.ContinuousRecognitionSession.ResultGenerated += (s, e) =>
            {
                var text = e.Result.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _currentRecognizedText = text;
                    ResultGenerated?.Invoke(text);
                }
            };

            _recognizer = rec;
        }
        catch
        {
            try { _recognizer?.Dispose(); } catch { }
            _recognizer = null;
        }
    }

    private void InstallHook()
    {
        try
        {
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule;
            if (curModule != null)
            {
                _hookId = SetWindowsHookEx(WhKeyboardLl, _proc!, GetModuleHandle(curModule.ModuleName), 0);
            }
        }
        catch { }
    }

    private bool _hotkeyComboActive;
    private DateTime _lastHotkeyToggleTime = DateTime.MinValue;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);

            bool isAlt = vkCode == VkMenu || vkCode == VkLmenu || vkCode == VkRmenu;
            bool isWin = vkCode == VkLwin || vkCode == VkRwin;

            bool winDown = (GetAsyncKeyState(VkLwin) & 0x8000) != 0 || (GetAsyncKeyState(VkRwin) & 0x8000) != 0;
            bool altDown = (GetAsyncKeyState(VkMenu) & 0x8000) != 0 || (GetAsyncKeyState(VkLmenu) & 0x8000) != 0 || (GetAsyncKeyState(VkRmenu) & 0x8000) != 0;
            bool comboPressed = (isAlt && winDown) || (isWin && altDown) || (winDown && altDown);

            if (wParam == (IntPtr)WmKeydown || wParam == (IntPtr)WmSyskeydown)
            {
                if (comboPressed)
                {
                    // Debounce: trigger once on initial press, ignore auto-repeat while keys are held down
                    if (!_hotkeyComboActive && (DateTime.UtcNow - _lastHotkeyToggleTime).TotalMilliseconds > 450)
                    {
                        _hotkeyComboActive = true;
                        _lastHotkeyToggleTime = DateTime.UtcNow;
                        Application.Current?.Dispatcher?.InvokeAsync(ToggleVoiceAssistant);
                    }
                    // CRITICAL: NEVER return 1 for Alt or Win in WH_KEYBOARD_LL! It desynchronizes Windows OS modifier state and causes an unhandled menu loop hang.
                }
                else if (_isListening && vkCode == 0x0D) // Enter
                {
                    Application.Current?.Dispatcher?.InvokeAsync(CommitAndStop);
                    return (IntPtr)1;
                }
                else if (_isListening && vkCode == 0x1B) // Escape
                {
                    Application.Current?.Dispatcher?.InvokeAsync(CancelAndStop);
                    return (IntPtr)1;
                }
            }
            else if (wParam == (IntPtr)0x0101 || wParam == (IntPtr)0x0105) // WM_KEYUP or WM_SYSKEYUP
            {
                if (isAlt || isWin || !winDown || !altDown)
                {
                    _hotkeyComboActive = false;
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void ToggleVoiceAssistant()
    {
        if (_isListening)
        {
            CommitAndStop();
        }
        else
        {
            StartListening();
        }
    }

    public void StartListening()
    {
        if (_isListening) return;
        _isListening = true;
        _currentRecognizedText = "";

        // Capture currently focused foreground window before we show any UI
        _targetWindowHandle = GetForegroundWindow();

        StateChanged?.Invoke(true);

        if (_recognizer != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _recognizer.ContinuousRecognitionSession.StartAsync();
                }
                catch { }
            });
        }
    }

    public void CommitAndStop()
    {
        if (!_isListening) return;
        _isListening = false;

        var textToInsert = _currentRecognizedText.Trim();

        if (_recognizer != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _recognizer.ContinuousRecognitionSession.StopAsync();
                }
                catch { }
            });
        }

        StateChanged?.Invoke(false);

        if (!string.IsNullOrEmpty(textToInsert))
        {
            InsertTextIntoActiveWindow(textToInsert);
        }
    }

    public void CancelAndStop()
    {
        if (!_isListening) return;
        _isListening = false;
        _currentRecognizedText = "";

        if (_recognizer != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _recognizer.ContinuousRecognitionSession.StopAsync();
                }
                catch { }
            });
        }

        StateChanged?.Invoke(false);
    }

    private void InsertTextIntoActiveWindow(string text)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(120);

            // Restore focus to previously active foreground window
            if (_targetWindowHandle != IntPtr.Zero)
            {
                SetForegroundWindow(_targetWindowHandle);
                await Task.Delay(80);
            }

            if (Application.Current?.Dispatcher != null)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        Clipboard.SetText(text);
                    }
                    catch { }
                });
            }

            await Task.Delay(80);

            // Simulate Ctrl + V
            keybd_event(0x11, 0, 0, UIntPtr.Zero); // Ctrl Down
            keybd_event(0x56, 0, 0, UIntPtr.Zero); // V Down
            keybd_event(0x56, 0, 2, UIntPtr.Zero); // V Up
            keybd_event(0x11, 0, 2, UIntPtr.Zero); // Ctrl Up
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        try
        {
            _recognizer?.Dispose();
        }
        catch { }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
