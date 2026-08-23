using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OneRemoteCli.Daemon.Shell;
using OneRemoteCli.Protocol.Hub;
using static OneRemoteCli.Daemon.Tray.NativeMethods;

namespace OneRemoteCli.Daemon.Tray;

/// <summary>Confirms or overrides shortcut detection before anything is written.</summary>
[SupportedOSPlatform("windows")]
internal static class ShortcutTypePicker
{
    private const int IdOk = 1;
    private const int IdCancel = 2;
    private const uint OkButton = 0x0001;
    private const uint CancelButton = 0x0008;
    private const uint AllowCancellation = 0x0008;
    private const uint SizeToContent = 0x01000000;
    private const int FirstTypeId = 100;
    private const uint TaskDialogCreated = 0;
    private const int ClickTaskDialogButton = WM_USER + 102;

    private static readonly (CliType Type, string Text)[] Choices =
    [
        (CliType.Generic, "Generic — wrap the original command"),
        (CliType.Cmd, "Command Prompt — wrap with cmd controls"),
        (CliType.PowerShell, "PowerShell — wrap with PowerShell controls"),
        (CliType.ClaudeCode, "Claude Code — wrap the console CLI"),
        (CliType.CopilotCli, "GitHub Copilot CLI — create a native ACP chat"),
    ];

    private static readonly TaskDialogCallback SelfCheckCallback = CloseSelfCheck;

    internal static (int Config, int Button) NativeLayout =>
        (Marshal.SizeOf<TaskDialogConfig>(), Marshal.SizeOf<TaskDialogButton>());

    public static CliType? Pick(
        IntPtr owner,
        ShortcutAnalysis analysis,
        Action<string>? diagnostic = null) =>
        Show(owner, analysis, diagnostic, IntPtr.Zero);

    /// <summary>Creates and dismisses the real native dialog for the published-build self-check.</summary>
    internal static void CheckNativeDialog()
    {
        IntPtr callback = Marshal.GetFunctionPointerForDelegate(SelfCheckCallback);
        CliType? selected = Show(
            IntPtr.Zero,
            new ShortcutAnalysis(null, "Self check", CliType.Generic),
            diagnostic: null,
            callback);
        GC.KeepAlive(SelfCheckCallback);

        if (selected != CliType.Generic)
        {
            throw new InvalidOperationException("The shortcut type picker did not return its default choice.");
        }
    }

    private static CliType? Show(
        IntPtr owner,
        ShortcutAnalysis analysis,
        Action<string>? diagnostic,
        IntPtr callback)
    {
        int buttonSize = Marshal.SizeOf<TaskDialogButton>();
        IntPtr buttons = Marshal.AllocHGlobal(buttonSize * Choices.Length);
        int initializedButtons = 0;

        try
        {
            diagnostic?.Invoke(
                $"preparing {Choices.Length} choices; detected={CliTypes.Token(analysis.DetectedType)}; "
                + $"processArchitecture={RuntimeInformation.ProcessArchitecture}; pointerBytes={IntPtr.Size}; "
                + $"buttonBytes={buttonSize}.");

            for (int i = 0; i < Choices.Length; i++)
            {
                Marshal.StructureToPtr(
                    new TaskDialogButton
                    {
                        Id = FirstTypeId + (int)Choices[i].Type,
                        Text = Choices[i].Text,
                    },
                    buttons + (i * buttonSize),
                    false);
                initializedButtons++;
            }

            var config = new TaskDialogConfig
            {
                Size = (uint)Marshal.SizeOf<TaskDialogConfig>(),
                Owner = owner,
                Flags = AllowCancellation | SizeToContent,
                CommonButtons = OkButton | CancelButton,
                WindowTitle = SettingsPresenter.Title,
                MainInstruction = $"Detected: {CliTypes.Label(analysis.DetectedType)}",
                Content =
                    $"Confirm the type for “{analysis.DisplayName}”, or select an override. "
                    + "Nothing will be created until you press OK.",
                RadioButtonCount = (uint)Choices.Length,
                RadioButtons = buttons,
                DefaultRadioButton = FirstTypeId + (int)analysis.DetectedType,
                Callback = callback,
            };

            DLLVERSIONINFO comctl = default;
            comctl.cbSize = (uint)Marshal.SizeOf<DLLVERSIONINFO>();
            int versionResult = ComCtlGetVersion(ref comctl);
            string version = versionResult == 0
                ? $"{comctl.dwMajorVersion}.{comctl.dwMinorVersion}.{comctl.dwBuildNumber}"
                : $"unavailable (HRESULT 0x{versionResult:x8})";

            diagnostic?.Invoke(
                $"calling TaskDialogIndirect; owner=0x{owner.ToInt64():x}; ownerValid={IsWindow(owner)}; "
                + $"configBytes={config.Size}; flags=0x{config.Flags:x8}; commonButtons=0x{config.CommonButtons:x8}; "
                + $"defaultRadio={config.DefaultRadioButton}; comctl32={version}.");

            int result = TaskDialogIndirect(ref config, out int pressed, out int selected, out _);
            int lastError = Marshal.GetLastPInvokeError();

            diagnostic?.Invoke(
                $"TaskDialogIndirect returned HRESULT 0x{result:x8}; lastWin32Error={lastError}; "
                + $"pressedButton={pressed}; selectedRadio={selected}.");

            if (result < 0)
            {
                string guidance = diagnostic is null
                    ? string.Empty
                    : " Diagnostic details were written to the agent log; use Open logs in Settings "
                        + "and share today's file.";

                throw new ExternalException(
                    $"Windows could not show the shortcut type picker (HRESULT 0x{result:x8}).{guidance}",
                    result);
            }

            if (pressed is IdCancel or 0)
            {
                return null;
            }

            int value = selected - FirstTypeId;
            return pressed == IdOk && Enum.IsDefined(typeof(CliType), value)
                ? (CliType)value
                : null;
        }
        finally
        {
            for (int i = 0; i < initializedButtons; i++)
            {
                Marshal.DestroyStructure<TaskDialogButton>(buttons + (i * buttonSize));
            }

            Marshal.FreeHGlobal(buttons);
            diagnostic?.Invoke("released the native radio-button buffer.");
        }
    }

    private static int CloseSelfCheck(
        IntPtr window,
        uint notification,
        IntPtr wParam,
        IntPtr lParam,
        IntPtr callbackData)
    {
        if (notification == TaskDialogCreated)
        {
            PostMessage(window, ClickTaskDialogButton, (IntPtr)IdOk, IntPtr.Zero);
        }

        return 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct TaskDialogButton
    {
        public int Id;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Text;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct TaskDialogConfig
    {
        public uint Size;
        public IntPtr Owner;
        public IntPtr Instance;
        public uint Flags;
        public uint CommonButtons;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? WindowTitle;

        public IntPtr MainIcon;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? MainInstruction;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Content;

        public uint ButtonCount;
        public IntPtr Buttons;
        public int DefaultButton;
        public uint RadioButtonCount;
        public IntPtr RadioButtons;
        public int DefaultRadioButton;
        public IntPtr VerificationText;
        public IntPtr ExpandedInformation;
        public IntPtr ExpandedControlText;
        public IntPtr CollapsedControlText;
        public IntPtr FooterIcon;
        public IntPtr Footer;
        public IntPtr Callback;
        public IntPtr CallbackData;
        public uint Width;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int TaskDialogCallback(
        IntPtr window,
        uint notification,
        IntPtr wParam,
        IntPtr lParam,
        IntPtr callbackData);

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode, PreserveSig = true, SetLastError = true)]
    private static extern int TaskDialogIndirect(
        ref TaskDialogConfig config,
        out int button,
        out int radioButton,
        [MarshalAs(UnmanagedType.Bool)] out bool verificationFlagChecked);
}
