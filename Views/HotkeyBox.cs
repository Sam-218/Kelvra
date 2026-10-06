using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Kelvra;

/// <summary>
/// A box that records a shortcut: click it, press the keys. Backspace/Delete turns the shortcut off, Esc cancels,
/// Tab moves on as usual. While it records, the main window lifts the global hotkeys so the current ones can be pressed.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    private string _hotkey = "";

    public HotkeyBox()
    {
        // Implicit styles only match the exact type, so pick up the theme's TextBox look explicitly
        SetResourceReference(StyleProperty, typeof(TextBox));
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        Tag = "Off";
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("MonoFont");
    }

    /// <summary>The shortcut as stored in settings ("" = off).</summary>
    public string Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value ?? "";
            if (!IsKeyboardFocused) Text = _hotkey;
        }
    }

    public event Action<HotkeyBox>? HotkeyChanged;
    public event Action? RecordingStarted;
    public event Action? RecordingEnded;

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Text = "Press the new shortcut…";
        RecordingStarted?.Invoke();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Text = _hotkey;
        RecordingEnded?.Invoke();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key == Key.Tab && mods is ModifierKeys.None or ModifierKeys.Shift) return; // keyboard navigation

        e.Handled = true;
        if (mods == ModifierKeys.None && key == Key.Escape)
        {
            Finish();
            return;
        }
        if (mods == ModifierKeys.None && key is Key.Back or Key.Delete)
        {
            Commit("");
            return;
        }
        if (!Kelvra.Hotkey.IsUsableKey(key))
        {
            // Only modifiers so far: show what's held
            Text = mods == ModifierKeys.None ? "Press the new shortcut…" : Kelvra.Hotkey.ModifierText(mods) + "+…";
            return;
        }
        Commit(new Hotkey(mods, key).ToString());
    }

    private void Commit(string hotkey)
    {
        _hotkey = hotkey;
        HotkeyChanged?.Invoke(this);
        Finish();
    }

    /// <summary>Leaves the box (which ends recording and shows the stored value).</summary>
    private void Finish()
    {
        var scope = FocusManager.GetFocusScope(this);
        FocusManager.SetFocusedElement(scope, null);
        Keyboard.ClearFocus();
    }
}
