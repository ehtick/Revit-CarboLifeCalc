using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CarboLifeUI.UI
{
    /// <summary>
    /// How the dialogs in this application take a typed value.
    ///
    /// Every editable field used to be wired to TextChanged and then began with
    /// <c>await Task.Delay(1000)</c>. That is not a debounce: each keystroke started its own
    /// timer, so typing "1500" fired four separate commits, each writing a partial value ("1",
    /// "15", "150") into the model and refreshing the interface from it - which wrote the boxes
    /// back and, in one case, jumped the caret to the end of the line while the user was still
    /// editing. Being <c>async void</c>, anything that threw after the await was unobservable
    /// and took the process down with it.
    ///
    /// The replacement is the ordinary convention: a value is read when the user has finished
    /// with the field, meaning Enter or focus leaving the box. Nothing happens while typing.
    /// </summary>
    public static class CarboUiCommit
    {
        /// <summary>
        /// Makes Enter commit the field under the caret, for every TextBox inside <paramref name="root"/>.
        ///
        /// Enter simply moves focus on, which raises LostFocus, which is where the commit
        /// handlers are wired. One call per dialog covers every box in it, so there is no
        /// per-field Enter plumbing to forget.
        ///
        /// Boxes with AcceptsReturn are left alone: in those Enter means a new line.
        /// </summary>
        public static void WireEnterCommits(FrameworkElement root)
        {
            if (root == null)
                return;

            //PreviewKeyDown, so this is seen before a default OK button can act on Enter.
            root.PreviewKeyDown -= OnPreviewKeyDown;
            root.PreviewKeyDown += OnPreviewKeyDown;
        }

        private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return)
                return;

            TextBox box = e.OriginalSource as TextBox;

            if (box == null || box.IsReadOnly || box.AcceptsReturn)
                return;

            //Moving focus is what raises LostFocus and therefore commits. Handled, so Enter in a
            //field does not also press the dialog's default button and close it.
            box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }

        /// <summary>
        /// Commits whatever field currently has focus.
        ///
        /// Clicking a button normally takes focus off the box first, so the commit has already
        /// happened by the time the Click handler runs. Call this from an OK handler when the
        /// button is Focusable="False", where that does not hold.
        /// </summary>
        public static void CommitFocused()
        {
            TextBox box = Keyboard.FocusedElement as TextBox;

            if (box == null || box.IsReadOnly)
                return;

            box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }

    /// <summary>
    /// A real debounce, for the places where waiting for the user to finish is right but
    /// committing on focus loss is not - a search box that filters a list as you type.
    ///
    /// Each poke restarts the clock, so N keystrokes produce exactly one call rather than N. The
    /// old pattern started an independent <c>Task.Delay</c> per keystroke and guarded it by
    /// comparing string LENGTH before and after, which passes for "abc" to "abd" and so fired
    /// anyway.
    ///
    /// A DispatcherTimer rather than async/await: the callback arrives on the UI thread, and
    /// there is no <c>async void</c> for an exception to escape from.
    /// </summary>
    public sealed class UiDebouncer
    {
        private readonly DispatcherTimer timer;
        private Action pending;

        public UiDebouncer(int milliseconds)
        {
            if (milliseconds < 1)
                milliseconds = 1;

            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            timer.Tick += OnTick;
        }

        /// <summary>Schedules the action, cancelling any call still waiting.</summary>
        public void Poke(Action action)
        {
            pending = action;

            //Stop then Start is what restarts the interval; Start alone on a running timer
            //does nothing.
            timer.Stop();
            timer.Start();
        }

        /// <summary>Drops a pending call without running it.</summary>
        public void Cancel()
        {
            timer.Stop();
            pending = null;
        }

        private void OnTick(object sender, EventArgs e)
        {
            timer.Stop();

            Action action = pending;
            pending = null;

            if (action == null)
                return;

            try
            {
                action();
            }
            catch (Exception ex)
            {
                //A filter that fails must not take the dialog down.
                MessageBox.Show(ex.Message);
            }
        }
    }
}
