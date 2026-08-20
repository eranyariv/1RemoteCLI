using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OneRemoteCli.Daemon.Shell;
using OneRemoteCli.Protocol.Hub;

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

    private static readonly (CliType Type, string Text)[] Choices =
    [
        (CliType.Generic, "Generic — wrap the original command"),
        (CliType.Cmd, "Command Prompt — wrap with cmd controls"),
        (CliType.PowerShell, "PowerShell — wrap with PowerShell controls"),
        (CliType.ClaudeCode, "Claude Code — wrap the console CLI"),
        (CliType.CopilotCli, "GitHub Copilot CLI — create a native ACP chat"),
    ];

    public static CliType? Pick(IntPtr owner, ShortcutAnalysis analysis)
    {
        int buttonSize = Marshal.SizeOf<TaskDialogButton>();
        IntPtr buttons = Marshal.AllocHGlobal(buttonSize * Choices.Length);

        try
        {
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
            };

            int result = TaskDialogIndirect(ref config, out int pressed, out int selected, out _);
            if (result < 0)
            {
                throw new ExternalException("Windows could not show the shortcut type picker.", result);
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
            for (int i = 0; i < Choices.Length; i++)
            {
                Marshal.DestroyStructure<TaskDialogButton>(buttons + (i * buttonSize));
            }
            Marshal.FreeHGlobal(buttons);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TaskDialogButton
    {
        public int Id;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Text;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int TaskDialogIndirect(
        ref TaskDialogConfig config,
        out int button,
        out int radioButton,
        [MarshalAs(UnmanagedType.Bool)] out bool verificationFlagChecked);
}
